using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.Sound.Node;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.MediaAssets;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject.BlueprintDecompiler;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Textures;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PakTool.Core;

public sealed class PakArchiveSession : IDisposable
{
    private const long MaxInlineMediaPreviewBytes = 96L * 1024 * 1024;
    private const int MaxModelPreviewIndices = 300_000;

    private DefaultFileProvider? _provider;
    private Action<string>? _decodeLogger;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, IReadOnlyList<ArchiveEntryDto>> _listCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ArchiveEntryDto> _entryCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, IReadOnlyList<ArchiveEntryDto>>? _directoryIndex;

    public bool IsOpen => _provider is not null;

    public async Task<PakOpenResult> OpenAsync(PakOpenOptions options, CancellationToken cancellationToken = default)
    {
        if (options.PakPaths.Count == 0)
            throw new ArgumentException("At least one .pak path is required.", nameof(options));

        var timings = new List<OperationTimingDto>();
        var totalClock = System.Diagnostics.Stopwatch.StartNew();
        var stepClock = System.Diagnostics.Stopwatch.StartNew();

        DisposeProvider();
        ClearCaches();
        _decodeLogger = options.DecodeLogger;
        AddTiming(timings, "ResetSession", stepClock);

        stepClock.Restart();
        var firstPak = new FileInfo(options.PakPaths[0]);
        if (firstPak.Directory is null)
            throw new DirectoryNotFoundException("Could not resolve the pak directory.");

        var version = new VersionContainer(ParseGame(options.Game));
        var comparer = options.CaseInsensitivePaths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var provider = new DefaultFileProvider(firstPak.Directory, SearchOption.TopDirectoryOnly, version, comparer)
        {
            SkipReferencedTextures = false,
            ReadShaderMaps = false,
            ReadNaniteData = true,
            ReadScriptData = true
        };
        AddTiming(timings, "CreateProvider", stepClock);

        stepClock.Restart();
        foreach (var pakPath in options.PakPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            provider.RegisterVfs(pakPath);
        }
        AddTiming(timings, "RegisterVfs", stepClock);

        stepClock.Restart();
        if (!string.IsNullOrWhiteSpace(options.UsmapPath))
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(options.UsmapPath);
        AddTiming(timings, "LoadUsmap", stepClock);

        stepClock.Restart();
        if (!string.IsNullOrWhiteSpace(options.AesKeyHex))
            await provider.SubmitKeyAsync(new FGuid(), new FAesKey(NormalizeAesKey(options.AesKeyHex))).ConfigureAwait(false);
        AddTiming(timings, "SubmitAes", stepClock);

        stepClock.Restart();
        var mountedByScan = await provider.MountAsync().ConfigureAwait(false);
        AddTiming(timings, "Mount", stepClock);

        stepClock.Restart();
        provider.PostMount();
        AddTiming(timings, "PostMount", stepClock);

        _provider = provider;
        stepClock.Restart();
        var mountedArchives = provider.MountedVfs.Select(vfs => vfs.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        AddTiming(timings, "MountedArchives", stepClock);
        AddTiming(timings, "OpenTotal", totalClock);

        return new PakOpenResult(
            mountedArchives.Length == 0 ? mountedByScan : mountedArchives.Length,
            provider.Files.Count,
            provider.RequiredKeys.Count,
            mountedArchives,
            timings);
    }

    public Task LoadUsmapAsync(string usmapPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ArchiveEntryDto>> ListAsync(string? folder = null, bool recursive = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedFolder = NormalizeFolder(folder);
        if (!recursive)
        {
            lock (_cacheLock)
            {
                if (_directoryIndex?.TryGetValue(normalizedFolder, out var indexedEntries) == true)
                    return Task.FromResult(indexedEntries);

                if (_listCache.TryGetValue(normalizedFolder, out var cachedEntries))
                    return Task.FromResult(cachedEntries);
            }
        }

        var entries = recursive
            ? ListRecursive(normalizedFolder)
            : ListImmediate(normalizedFolder);

        var sortedEntries = entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!recursive)
        {
            lock (_cacheLock)
            {
                _listCache[normalizedFolder] = sortedEntries;
            }
        }

        return Task.FromResult<IReadOnlyList<ArchiveEntryDto>>(sortedEntries);
    }

    public Task<DirectoryIndexResult> BuildDirectoryIndexAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var folderMap = new Dictionary<string, Dictionary<string, ArchiveEntryDto>>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = new(StringComparer.OrdinalIgnoreCase)
        };
        var entryCount = 0;

        foreach (var file in Provider.Files.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddDirectoryChain(folderMap, file.Path);

            if (ShouldHidePackagePayload(file))
                continue;

            var folder = GetParentFolder(file.Path);
            if (!folderMap.TryGetValue(folder, out var entries))
                folderMap[folder] = entries = new Dictionary<string, ArchiveEntryDto>(StringComparer.OrdinalIgnoreCase);

            var entry = ToAssetAwareDto(file);
            if (entries.TryAdd(entry.FullPath, entry))
                entryCount++;
        }

        var finalized = folderMap.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ArchiveEntryDto>) pair.Value.Values
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);

        lock (_cacheLock)
        {
            _directoryIndex = finalized;
            _listCache.Clear();
        }

        return Task.FromResult(new DirectoryIndexResult(finalized.Count, entryCount));
    }

    public Task<IReadOnlyList<ArchiveEntryDto>> SearchAsync(string query, int limit = 250, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<ArchiveEntryDto>>([]);

        var results = Provider.Files.Values
            .Where(file => file.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, limit))
            .Select(ToAssetAwareDto)
            .DistinctBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ArchiveEntryDto>>(results);
    }

    public Task<AssetInfoDto> ReadAssetInfoAsync(string assetPath, int maxExports = 12, int maxPropertiesPerExport = 24, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = Provider;
        var fixedPath = provider.FixPath(assetPath);

        if (!provider.TryLoadPackage(fixedPath, out var package))
            throw new InvalidOperationException($"Could not load package: {assetPath}");

        var exports = package.GetExports()
            .Take(Math.Max(1, maxExports))
            .Select(export => new AssetExportDto(
                export.Name,
                export.ExportType,
                export.Properties.Count,
                export.Properties
                    .Take(Math.Max(1, maxPropertiesPerExport))
                    .Select(prop => new AssetPropertyDto(
                        prop.Name.Text,
                        prop.PropertyType.Text,
                        PreviewValue(prop.Tag?.GenericValue)))
                    .ToArray()))
            .ToArray();

        return Task.FromResult(new AssetInfoDto(fixedPath, package.NameMap.Length, package.ExportMapLength, exports));
    }

    public async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await ReadGameFileAsync(path).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> ReadRelatedRawFilesAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = Provider;
        var fixedPath = provider.FixPath(path);

        if (!provider.TryGetGameFile(fixedPath, out var file))
            throw new FileNotFoundException("The archive entry was not found.", fixedPath);

        var output = new Dictionary<string, byte[]>(provider.PathComparer);
        foreach (var related in GetRelatedFiles(provider, file))
        {
            cancellationToken.ThrowIfCancellationRequested();
            output[related.Path] = await related.ReadAsync().ConfigureAwait(false);
        }

        return output;
    }

    public async Task<IReadOnlyList<PakRawFileCopy>> CopyAllRawFilesAsync(
        string outputDirectory,
        IProgress<PakRawFileCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputDirectory);

        var provider = Provider;
        var files = provider.Files.Values
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var copied = new List<PakRawFileCopy>(files.Length);

        for (var i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];
            progress?.Report(new PakRawFileCopyProgress(i, files.Length, file.Path));

            var outputPath = BuildOutputPath(outputDirectory, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var data = await file.ReadAsync().ConfigureAwait(false);
            await File.WriteAllBytesAsync(outputPath, data, cancellationToken).ConfigureAwait(false);
            copied.Add(new PakRawFileCopy(file.Path, outputPath, file.Size));
        }

        progress?.Report(new PakRawFileCopyProgress(files.Length, files.Length, string.Empty));
        return copied;
    }

    public Task<IReadOnlySet<string>> ListRawFilePathsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlySet<string> paths = Provider.Files.Values
            .Select(file => file.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(paths);
    }

    public Task<AssetPreviewDto> ReadPreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = Provider;
            var fixedPath = provider.FixPath(path);
            LogDecode($"Unified preview requested: asset={path}, fixed={fixedPath}");

            if (!TryResolveGameFile(provider, fixedPath, out var gameFile))
                throw new FileNotFoundException("The archive entry was not found.", fixedPath);

            if (!gameFile.IsUePackage)
                return await ReadDirectFilePreviewAsync(gameFile, cancellationToken).ConfigureAwait(false);

            IPackage package;
            try
            {
                package = provider.LoadPackage(gameFile);
            }
            catch (Exception ex) when (IsMissingMappingsError(ex))
            {
                throw new InvalidOperationException("This asset uses unversioned properties. Import the matching .usmap mapping file, then preview it again.", ex);
            }

            var deferredBlueprints = new List<UObject>();
            var exportIndex = 0;
            foreach (var export in package.ExportsLazy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                exportIndex++;

                UObject value;
                try
                {
                    value = export.Value;
                }
                catch (Exception ex) when (IsMissingMappingsError(ex))
                {
                    throw new InvalidOperationException("This asset uses unversioned properties. Import the matching .usmap mapping file, then preview it again.", ex);
                }
                catch (Exception ex)
                {
                    LogDecode($"Unified preview export #{exportIndex} skipped: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (TryCreateTexturePreview(value, fixedPath, out var texturePreview))
                    return texturePreview;

                if (TryCreateAudioPreview(value, out var audioPreview))
                    return audioPreview;

                if (TryCreateVideoPreview(provider, value, out var videoPreview))
                    return videoPreview;

                if (TryCreateModelPreview(value, out var modelPreview))
                    return modelPreview;

                if (TryCreateMaterialInstancePreview(value, fixedPath, out var materialPreview))
                    return materialPreview;

                if (IsBlueprintLike(value))
                    deferredBlueprints.Add(value);
            }

            if (deferredBlueprints.Count > 0)
                return CreateBlueprintPreview(package, deferredBlueprints);

            var info = await ReadAssetInfoAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            return CreateInfoPreview(info);
        }, cancellationToken);
    }

    public Task<PreviewExportDto> ReadTypedPreviewExportAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = await ReadPreviewAsync(path, cancellationToken).ConfigureAwait(false);
            var baseName = GetExportBaseName(path, preview.Title);

            return preview.Kind.ToLowerInvariant() switch
            {
                "texture" when preview.Data is { Length: > 0 } textureData => new PreviewExportDto(
                    "texture",
                    preview.Title,
                    [new PreviewExportFileDto(baseName + ".png", "image/png", textureData)]),

                "audio" when preview.Data is { Length: > 0 } audioData &&
                             string.Equals(preview.MimeType, "audio/wav", StringComparison.OrdinalIgnoreCase) => new PreviewExportDto(
                    "audio",
                    preview.Title,
                    [new PreviewExportFileDto(baseName + ".wav", "audio/wav", audioData)]),

                "audio" => throw new InvalidOperationException(
                    "This audio payload could not be converted to WAV. Use Raw Export for the original encoded data."),

                "model" when preview.Model is not null => CreateModelExport(preview.Model, baseName, preview.Title),

                "blueprint" when !string.IsNullOrWhiteSpace(preview.Text) => new PreviewExportDto(
                    "blueprint",
                    preview.Title,
                    [new PreviewExportFileDto(baseName + ".cpp", "text/x-c++src", Encoding.UTF8.GetBytes(preview.Text))]),

                "blueprint" => throw new InvalidOperationException("This blueprint did not produce C++ pseudocode."),

                _ => throw new InvalidOperationException("This resource type does not have a typed export format yet.")
            };
        }, cancellationToken);
    }

    public Task<AudioPayloadDto> ReadAudioPayloadAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = Provider;
            var fixedPath = provider.FixPath(path);
            if (!TryResolveGameFile(provider, fixedPath, out var gameFile))
                throw new FileNotFoundException("The archive entry was not found.", fixedPath);

            if (!gameFile.IsUePackage)
            {
                var extension = NormalizeExtension(gameFile.Extension);
                if (!IsDirectAudioExtension(extension))
                    throw new InvalidOperationException("The selected file is not a supported audio payload.");

                return new AudioPayloadDto(
                    gameFile.Name,
                    extension,
                    GetMimeType(extension),
                    await gameFile.ReadAsync().ConfigureAwait(false));
            }

            IPackage package;
            try
            {
                package = provider.LoadPackage(gameFile);
            }
            catch (Exception ex) when (IsMissingMappingsError(ex))
            {
                throw new InvalidOperationException("This audio asset uses unversioned properties. Import the matching .usmap mapping file, then try again.", ex);
            }

            foreach (var export in package.ExportsLazy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UObject value;
                try
                {
                    value = export.Value;
                }
                catch (Exception ex) when (IsMissingMappingsError(ex))
                {
                    throw new InvalidOperationException("This audio asset uses unversioned properties. Import the matching .usmap mapping file, then try again.", ex);
                }
                catch
                {
                    continue;
                }

                if (TryReadAudioPayload(value, out var payload))
                    return payload;
            }

            throw new InvalidOperationException("No replaceable audio payload was found in this asset.");
        }, cancellationToken);
    }

    public async Task<LocresPreviewDto> ReadLocresPreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        var data = await ReadGameFileAsync(path).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return LocresResourceCodec.Read(data);
    }

    private async Task<AssetPreviewDto> ReadDirectFilePreviewAsync(GameFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = NormalizeExtension(file.Extension);
        var mimeType = GetMimeType(extension);
        var isAudio = IsDirectAudioExtension(extension);
        var isVideo = IsDirectVideoExtension(extension);

        if (extension == "locres")
        {
            var locresData = await file.ReadAsync().ConfigureAwait(false);
            var locres = LocresResourceCodec.Read(locresData);
            return new AssetPreviewDto(
                "locres",
                file.Name,
                [
                    new("Version", locres.Version),
                    new("Namespaces", locres.NamespaceCount.ToString("N0")),
                    new("Entries", locres.EntryCount.ToString("N0")),
                    new("Size", FormatBytes(locresData.Length))
                ],
                "application/octet-stream",
                Locres: locres,
                CanPlay: true);
        }

        if (!isAudio && !isVideo)
        {
            return new AssetPreviewDto(
                "info",
                file.Name,
                [
                    new("Path", file.Path),
                    new("Type", string.IsNullOrEmpty(extension) ? "Raw file" : extension.ToUpperInvariant()),
                    new("Size", FormatBytes(file.Size))
                ],
                Text: "No inline preview is available for this file type.");
        }

        if (file.Size > MaxInlineMediaPreviewBytes)
        {
            return new AssetPreviewDto(
                isVideo ? "video" : "audio",
                file.Name,
                [
                    new("Path", file.Path),
                    new("Format", extension.ToUpperInvariant()),
                    new("Size", FormatBytes(file.Size)),
                    new("Playback", "Too large for inline preview")
                ],
                mimeType,
                Text: "This media file is large enough to risk freezing the embedded preview. Export raw to play it externally.",
                CanPlay: false);
        }

        var data = await file.ReadAsync().ConfigureAwait(false);
        return new AssetPreviewDto(
            isVideo ? "video" : "audio",
            file.Name,
            [
                new("Path", file.Path),
                new("Format", extension.ToUpperInvariant()),
                new("Size", FormatBytes(data.Length)),
                new("Playback", CanPlayMime(mimeType) ? "Browser native" : "Export raw to play externally")
            ],
            mimeType,
            data,
            CanPlay: CanPlayMime(mimeType));
    }

    private bool TryCreateTexturePreview(UObject export, string fixedPath, out AssetPreviewDto preview)
    {
        preview = default!;
        if (export is not UTexture texture)
            return false;

        if (!TryEncodeTexturePreview(texture, 1024, Provider.Versions.Platform, out var encoded, out var error))
        {
            if (error is not null)
                throw new InvalidOperationException($"Texture found but could not be decoded for {texture.Name}: {error.Message}", error);

            return false;
        }

        preview = new AssetPreviewDto(
            "texture",
            $"{encoded.Name} ({encoded.Width}x{encoded.Height})",
            [
                new("Export", texture.ExportType),
                new("Format", texture.Format.ToString()),
                new("Size", $"{encoded.Width}x{encoded.Height}"),
                new("Path", fixedPath)
            ],
            "image/png",
            encoded.PngData,
            CanPlay: true);
        return true;
    }

    private static bool TryCreateMaterialInstancePreview(UObject export, string fixedPath, out AssetPreviewDto preview)
    {
        preview = default!;
        if (export is not UMaterialInstanceConstant material)
            return false;

        var details = new List<AssetPreviewDetailDto>
        {
            new("Export", export.ExportType),
            new("Path", fixedPath),
            new("Scalar Parameters", material.ScalarParameterValues.Length.ToString("N0")),
            new("Vector Parameters", material.VectorParameterValues.Length.ToString("N0")),
            new("Texture Parameters", material.TextureParameterValues.Length.ToString("N0"))
        };

        foreach (var parameter in material.ScalarParameterValues.Take(16))
            details.Add(new("Scalar", $"{parameter.Name} = {parameter.ParameterValue.ToString(CultureInfo.InvariantCulture)}"));
        foreach (var parameter in material.VectorParameterValues.Take(12))
            details.Add(new("Vector", $"{parameter.Name} = {parameter.ParameterValue}"));
        foreach (var parameter in material.TextureParameterValues.Take(12))
            details.Add(new("Texture", $"{parameter.Name} = {parameter.ParameterValue}"));

        preview = new AssetPreviewDto(
            "material",
            export.Name,
            details,
            Text: "Material instance parameters can be added to Patch Pak and edited on the Patch page.");
        return true;
    }

    private static bool TryCreateAudioPreview(UObject export, out AssetPreviewDto preview)
    {
        preview = default!;
        UObject? audioExport = export;
        string title = export.Name;

        if (export is UAkMediaAsset akMediaAsset)
        {
            title = string.IsNullOrWhiteSpace(akMediaAsset.MediaName) ? akMediaAsset.Name : akMediaAsset.MediaName;
            UObject loadedMediaData = default!;
#pragma warning disable CS8600
            if (akMediaAsset.CurrentMediaAssetData?.TryLoad(out loadedMediaData) == true &&
                loadedMediaData is UAkMediaAssetData mediaAssetData)
            {
                audioExport = mediaAssetData;
            }
#pragma warning restore CS8600
        }

        if (audioExport is not (USoundWave or USoundNodeWave or UAkMediaAssetData))
            return false;

        audioExport.Decode(true, out var audioFormat, out var data);
        audioFormat = NormalizeExtension(audioFormat);
        var mimeType = GetMimeType(audioFormat);
        var canPlay = data is { Length: > 0 } && CanPlayMime(mimeType);
        var sourceAudioFormat = audioFormat;
        string? binkaDecodeError = null;
        string? binkaDecodeSummary = null;
        var playbackText = canPlay
            ? "Browser native"
            : audioFormat.Equals("binka", StringComparison.OrdinalIgnoreCase)
                ? "BINKA / Bink Audio is not supported by Android WebView"
                : "Export raw to play externally";
        if (data is { LongLength: > MaxInlineMediaPreviewBytes })
        {
            preview = new AssetPreviewDto(
                "audio",
                title,
                [
                    new("Export", export.ExportType),
                    new("Decoded Format", string.IsNullOrWhiteSpace(audioFormat) ? "Unknown" : audioFormat.ToUpperInvariant()),
                    new("Size", FormatBytes(data.LongLength)),
                    new("Playback", "Too large for inline preview")
                ],
                mimeType,
                Text: "This decoded audio is large enough to risk freezing the embedded preview. Export raw to play it externally.",
                CanPlay: false);
            return true;
        }

        if (audioFormat.Equals("binka", StringComparison.OrdinalIgnoreCase) &&
            data is { Length: > 0 } binkaData)
        {
            if (NativeBinkaDecoder.TryDecodeToWav(
                    binkaData,
                    MaxInlineMediaPreviewBytes,
                    out var wavData,
                    out var binkaInfo,
                    out binkaDecodeError))
            {
                audioFormat = "wav";
                mimeType = GetMimeType(audioFormat);
                data = wavData;
                canPlay = CanPlayMime(mimeType);
                playbackText = "Decoded to WAV";
                binkaDecodeSummary = $"{binkaInfo.Channels} ch / {binkaInfo.SampleRate:N0} Hz / {FormatBytes(wavData.Length)}";
            }
            else
            {
                playbackText = "BINKA decoder unavailable; export raw";
            }
        }

        var details = new List<AssetPreviewDetailDto>
        {
            new("Export", export.ExportType),
            new("Decoded Format", string.IsNullOrWhiteSpace(audioFormat) ? "Unknown" : audioFormat.ToUpperInvariant()),
            new("Size", data is null ? "No audio payload found" : FormatBytes(data.Length)),
            new("Playback", playbackText)
        };
        if (!sourceAudioFormat.Equals(audioFormat, StringComparison.OrdinalIgnoreCase))
            details.Insert(2, new("Source Format", sourceAudioFormat.ToUpperInvariant()));
        if (!string.IsNullOrWhiteSpace(binkaDecodeSummary))
            details.Add(new("BINKA Decode", binkaDecodeSummary));
        else if (!string.IsNullOrWhiteSpace(binkaDecodeError))
            details.Add(new("BINKA Decode", binkaDecodeError));

        preview = new AssetPreviewDto(
            "audio",
            title,
            details,
            mimeType,
            data,
            Text: data is null || data.Length == 0
                ? "No playable audio payload was found in this asset."
                : canPlay
                    ? null
                    : audioFormat.Equals("binka", StringComparison.OrdinalIgnoreCase)
                        ? "Decoded format is BINKA / Bink Audio. Prism could not decode it to WAV with the native decoder, so it can only expose this payload for raw export."
                        : "This decoded audio format is not supported by the embedded browser player. Export raw to play it externally.",
            CanPlay: canPlay);
        return true;
    }

    private static bool TryReadAudioPayload(UObject export, out AudioPayloadDto payload)
    {
        payload = default!;
        UObject? audioExport = export;
        var title = export.Name;

        if (export is UAkMediaAsset akMediaAsset)
        {
            title = string.IsNullOrWhiteSpace(akMediaAsset.MediaName) ? akMediaAsset.Name : akMediaAsset.MediaName;
            UObject loadedMediaData = default!;
#pragma warning disable CS8600
            if (akMediaAsset.CurrentMediaAssetData?.TryLoad(out loadedMediaData) == true &&
                loadedMediaData is UAkMediaAssetData mediaAssetData)
            {
                audioExport = mediaAssetData;
            }
#pragma warning restore CS8600
        }

        if (audioExport is not (USoundWave or USoundNodeWave or UAkMediaAssetData))
            return false;

        audioExport.Decode(false, out var audioFormat, out var data);
        audioFormat = NormalizeExtension(audioFormat);
        if (data is not { Length: > 0 })
            throw new InvalidOperationException($"Audio asset {title} did not expose a replaceable payload.");

        payload = new AudioPayloadDto(title, audioFormat, GetMimeType(audioFormat), data);
        return true;
    }

    private static bool TryCreateVideoPreview(DefaultFileProvider provider, UObject export, out AssetPreviewDto preview)
    {
        preview = default!;
        if (export is not (UFileMediaSource or UStreamMediaSource or UBaseMediaSource))
            return false;

        var references = new List<string>();
        foreach (var propertyName in new[] { "FilePath", "StreamUrl", "Url", "MediaUrl", "ProxyOverride", "PrecacheFilePath" })
        {
            if (export.TryGetValue(out string value, propertyName) && !string.IsNullOrWhiteSpace(value))
                references.Add(value);
        }

        foreach (var reference in references)
        {
            if (TryResolveMediaFile(provider, reference, out var file) && IsDirectVideoExtension(NormalizeExtension(file.Extension)))
            {
                if (file.Size > MaxInlineMediaPreviewBytes)
                {
                    preview = new AssetPreviewDto(
                        "video",
                        export.Name,
                        [
                            new("Export", export.ExportType),
                            new("Referenced File", file.Path),
                            new("Format", NormalizeExtension(file.Extension).ToUpperInvariant()),
                            new("Size", FormatBytes(file.Size)),
                            new("Playback", "Too large for inline preview")
                        ],
                        GetMimeType(NormalizeExtension(file.Extension)),
                        Text: "The referenced video is large enough to risk freezing the embedded preview. Export raw to play it externally.",
                        CanPlay: false);
                    return true;
                }

                var data = file.Read();
                var mimeType = GetMimeType(NormalizeExtension(file.Extension));
                preview = new AssetPreviewDto(
                    "video",
                    export.Name,
                    [
                        new("Export", export.ExportType),
                        new("Referenced File", file.Path),
                        new("Format", NormalizeExtension(file.Extension).ToUpperInvariant()),
                        new("Size", FormatBytes(data.Length))
                    ],
                    mimeType,
                    data,
                    CanPlay: CanPlayMime(mimeType));
                return true;
            }
        }

        preview = new AssetPreviewDto(
            "video",
            export.Name,
            [
                new("Export", export.ExportType),
                new("Player", export is UBaseMediaSource source ? source.PlayerName.Text : "Unknown"),
                new("Reference", references.Count == 0 ? "No media path property found" : string.Join(", ", references))
            ],
            Text: references.Count == 0
                ? "This media source did not expose an inline video path."
                : "The referenced video could not be resolved inside the mounted pak.");
        return true;
    }

    private bool TryCreateModelPreview(UObject export, out AssetPreviewDto preview)
    {
        preview = default!;
        try
        {
            switch (export)
            {
                case UStaticMesh staticMesh when staticMesh.TryConvert(out var convertedStatic, ENaniteMeshFormat.AllLayersNaniteFirst):
                    using (convertedStatic)
                    {
                        if (convertedStatic.LODs.Count == 0)
                            return false;

                        preview = CreateModelPreview(export.Name, "StaticMesh", convertedStatic.LODs[0], convertedStatic.BoundingBox, Provider.Versions.Platform);
                        return true;
                    }

                case USkeletalMesh skeletalMesh when skeletalMesh.TryConvert(out var convertedSkeletal):
                    using (convertedSkeletal)
                    {
                        if (convertedSkeletal.LODs.Count == 0)
                            return false;

                        preview = CreateModelPreview(export.Name, "SkeletalMesh", convertedSkeletal.LODs[0], convertedSkeletal.BoundingBox, Provider.Versions.Platform);
                        return true;
                    }
            }
        }
        catch (Exception ex)
        {
            preview = new AssetPreviewDto(
                "model",
                export.Name,
                [
                    new("Export", export.ExportType),
                    new("Error", ex.Message)
                ],
                Text: "Model data was found, but it could not be converted for preview.");
            return true;
        }

        return false;
    }

    private static AssetPreviewDto CreateModelPreview(
        string name,
        string meshType,
        CBaseMeshLod lod,
        FBox bounds,
        ETexturePlatform platform,
        int maxIndices = MaxModelPreviewIndices,
        int textureMaxMipSize = 512)
    {
        var vertices = lod switch
        {
            CStaticMeshLod staticLod => staticLod.Verts ?? [],
            CSkelMeshLod skeletalLod => skeletalLod.Verts ?? [],
            _ => []
        };

        var sourceIndices = lod.Indices?.Value ?? [];
        var sourceIndexCount = sourceIndices.Length - (sourceIndices.Length % 3);
        var previewIndexCount = Math.Min(sourceIndexCount, maxIndices);
        previewIndexCount -= previewIndexCount % 3;
        var truncated = previewIndexCount < sourceIndexCount;
        var indexMap = new Dictionary<uint, uint>();
        var usedVertices = new List<int>();
        var remappedIndices = new List<uint>(previewIndexCount);

        for (var i = 0; i < previewIndexCount; i += 3)
        {
            var a = sourceIndices[i];
            var b = sourceIndices[i + 1];
            var c = sourceIndices[i + 2];
            if (a >= vertices.Length || b >= vertices.Length || c >= vertices.Length)
                continue;

            remappedIndices.Add(MapIndex(a));
            remappedIndices.Add(MapIndex(b));
            remappedIndices.Add(MapIndex(c));
        }

        var positions = new float[usedVertices.Count * 3];
        var normals = new float[usedVertices.Count * 3];
        var uvSetCount = Math.Max(1, lod.NumTexCoords);
        var extraUvSets = lod.ExtraUV?.Value ?? [];
        uvSetCount = Math.Max(uvSetCount, extraUvSets.Length + 1);
        var uvSets = new float[uvSetCount][];
        for (var set = 0; set < uvSets.Length; set++)
            uvSets[set] = new float[usedVertices.Count * 2];
        var textureLayers = new float[usedVertices.Count];

        for (var i = 0; i < usedVertices.Count; i++)
        {
            var sourceVertexIndex = usedVertices[i];
            var v = vertices[sourceVertexIndex];
            var pi = i * 3;
            positions[pi] = ToSingle(v.Position.X);
            positions[pi + 1] = ToSingle(v.Position.Y);
            positions[pi + 2] = ToSingle(v.Position.Z);
            normals[pi] = ToSingle(v.Normal.X);
            normals[pi + 1] = ToSingle(v.Normal.Y);
            normals[pi + 2] = ToSingle(v.Normal.Z);

            var ui = i * 2;
            uvSets[0][ui] = ToSingle(v.UV.U);
            uvSets[0][ui + 1] = ToSingle(v.UV.V);
            for (var set = 1; set < uvSets.Length; set++)
            {
                var sourceUvSet = set - 1;
                if (sourceUvSet < extraUvSets.Length && sourceVertexIndex < extraUvSets[sourceUvSet].Length)
                {
                    var uv = extraUvSets[sourceUvSet][sourceVertexIndex];
                    uvSets[set][ui] = ToSingle(uv.U);
                    uvSets[set][ui + 1] = ToSingle(uv.V);
                }
                else
                {
                    uvSets[set][ui] = uvSets[0][ui];
                    uvSets[set][ui + 1] = uvSets[0][ui + 1];
                }
            }

            textureLayers[i] = extraUvSets.Length > 0 && sourceVertexIndex < extraUvSets[0].Length
                ? MathF.Max(0, MathF.Round(ToSingle(extraUvSets[0][sourceVertexIndex].U - 1)))
                : 0;
        }

        var indices = remappedIndices.ToArray();
        var sections = truncated
            ? [new ModelSectionDto("Preview subset", 0, 0, indices.Length)]
            : (lod.Sections?.Value ?? [])
                .Select(section => new ModelSectionDto(
                    string.IsNullOrWhiteSpace(section.MaterialName) ? $"Material {section.MaterialIndex}" : section.MaterialName!,
                    section.MaterialIndex,
                    section.FirstIndex,
                    section.NumFaces * 3))
                .ToArray();

        var materials = CreateModelMaterials(lod, platform, textureMaxMipSize, Math.Min(CMaterialParams2.Diffuse.Length, uvSetCount));
        var model = new ModelPreviewDto(
            name,
            meshType,
            usedVertices.Count,
            indices.Length / 3,
            positions,
            normals,
            uvSets[0],
            indices,
            new ModelBoundsDto(
                ToSingle(bounds.Min.X),
                ToSingle(bounds.Min.Y),
                ToSingle(bounds.Min.Z),
                ToSingle(bounds.Max.X),
                ToSingle(bounds.Max.Y),
                ToSingle(bounds.Max.Z)),
            sections,
            uvSets,
            textureLayers,
            materials);

        return new AssetPreviewDto(
            "model",
            $"{name} ({meshType})",
            [
                new("Type", meshType),
                new("Vertices", usedVertices.Count.ToString("N0")),
                new("Triangles", (indices.Length / 3).ToString("N0")),
                new("Sections", sections.Length.ToString("N0")),
                new("Materials", materials.Count.ToString("N0")),
                new("UV Sets", uvSets.Length.ToString("N0")),
                new("Preview", truncated ? $"First {indices.Length / 3:N0} of {sourceIndexCount / 3:N0} triangles" : "Full first LOD")
            ],
            Model: model,
            CanPlay: true);

        uint MapIndex(uint sourceIndex)
        {
            if (indexMap.TryGetValue(sourceIndex, out var mapped))
                return mapped;

            mapped = (uint)usedVertices.Count;
            indexMap[sourceIndex] = mapped;
            usedVertices.Add((int)sourceIndex);
            return mapped;
        }
    }

    private static IReadOnlyList<ModelMaterialDto> CreateModelMaterials(
        CBaseMeshLod lod,
        ETexturePlatform platform,
        int maxMipSize,
        int textureLayerCount)
    {
        var sections = lod.Sections?.Value ?? [];
        if (sections.Length == 0)
            return [];

        var materials = new List<ModelMaterialDto>();
        var seen = new HashSet<int>();
        foreach (var section in sections)
        {
            if (!seen.Add(section.MaterialIndex))
                continue;

            var materialName = string.IsNullOrWhiteSpace(section.MaterialName)
                ? $"Material {section.MaterialIndex}"
                : section.MaterialName!;

            try
            {
                var material = section.Material?.Load<UMaterialInterface>();
                if (material is null)
                {
                    materials.Add(new ModelMaterialDto(section.MaterialIndex, materialName));
                    continue;
                }

                var parameters = new CMaterialParams2();
                material.GetParams(parameters, EMaterialFormat.FirstLayer);
                var layerCount = Math.Max(1, textureLayerCount);
                var diffuseTextures = CreateLayeredMaterialTextures(
                    parameters,
                    CMaterialParams2.Diffuse,
                    CMaterialParams2.FallbackDiffuse,
                    layerCount,
                    maxMipSize,
                    platform,
                    useFirstTextureFallback: true);
                var normalTextures = CreateLayeredMaterialTextures(
                    parameters,
                    CMaterialParams2.Normals,
                    CMaterialParams2.FallbackNormals,
                    layerCount,
                    maxMipSize,
                    platform,
                    useFirstTextureFallback: false);
                var pbrTextures = CreateLayeredMaterialTextures(
                    parameters,
                    CMaterialParams2.SpecularMasks,
                    CMaterialParams2.FallbackSpecularMasks,
                    layerCount,
                    maxMipSize,
                    platform,
                    useFirstTextureFallback: false);
                if (diffuseTextures.Count > 0 || normalTextures.Count > 0 || pbrTextures.Count > 0)
                {
                    var primary = diffuseTextures
                        .OrderBy(texture => texture.Layer)
                        .FirstOrDefault();
                    materials.Add(new ModelMaterialDto(
                        section.MaterialIndex,
                        materialName,
                        primary?.Layer ?? 0,
                        primary?.Name,
                        primary?.MimeType,
                        primary?.Data,
                        diffuseTextures,
                        normalTextures,
                        pbrTextures));
                    continue;
                }
            }
            catch
            {
                // Material references are optional for preview; failed material loads should not hide geometry.
            }

            materials.Add(new ModelMaterialDto(section.MaterialIndex, materialName));
        }

        return materials;
    }

    private static IReadOnlyList<ModelTextureDto> CreateLayeredMaterialTextures(
        CMaterialParams2 parameters,
        IReadOnlyList<string[]> triggers,
        string fallbackParameterName,
        int textureLayerCount,
        int maxMipSize,
        ETexturePlatform platform,
        bool useFirstTextureFallback)
    {
        var textures = new List<ModelTextureDto>();
        textureLayerCount = Math.Clamp(textureLayerCount, 1, triggers.Count);

        if (HasTopTexture(parameters, triggers))
        {
            ModelTextureDto? previous = null;
            for (var layer = 0; layer < textureLayerCount; layer++)
            {
                if (parameters.TryGetTexture2d(out var texture, triggers[layer]) &&
                    texture is not null &&
                    TryEncodeTexturePreview(texture, maxMipSize, platform, out var encoded, out _))
                {
                    previous = new ModelTextureDto(layer, encoded.Name, "image/png", encoded.PngData);
                    textures.Add(previous);
                }
                else if (previous is not null)
                {
                    textures.Add(previous with { Layer = layer });
                }
            }
        }

        if (textures.Count > 0)
            return textures;

        if (parameters.TryGetTexture2d(out var fallbackTexture, fallbackParameterName) &&
            fallbackTexture is not null &&
            TryEncodeTexturePreview(fallbackTexture, maxMipSize, platform, out var fallbackEncoded, out _))
        {
            for (var layer = 0; layer < textureLayerCount; layer++)
                textures.Add(new ModelTextureDto(layer, fallbackEncoded.Name, "image/png", fallbackEncoded.PngData));
            return textures;
        }

        if (useFirstTextureFallback &&
            parameters.TryGetFirstTexture2d(out var firstTexture) &&
            firstTexture is UTexture fallback &&
            TryEncodeTexturePreview(fallback, maxMipSize, platform, out var firstEncoded, out _))
        {
            for (var layer = 0; layer < textureLayerCount; layer++)
                textures.Add(new ModelTextureDto(layer, firstEncoded.Name, "image/png", firstEncoded.PngData));
        }

        return textures;
    }

    private static bool HasTopTexture(CMaterialParams2 parameters, IReadOnlyList<string[]> triggers)
    {
        return triggers.Count > 0 && parameters.TryGetTexture2d(out _, triggers[0]);
    }

    private static bool TryGetDiffuseTexture(CMaterialParams2 parameters, int uvSetCount, out UTexture texture, out int uvSet)
    {
        for (var set = 0; set < CMaterialParams2.Diffuse.Length; set++)
        {
            var names = CMaterialParams2.Diffuse[set];
            if (parameters.TryGetTexture2d(out var diffuseTexture, names) && diffuseTexture is not null)
            {
                texture = diffuseTexture;
                uvSet = Math.Clamp(set, 0, Math.Max(0, uvSetCount - 1));
                return true;
            }
        }

        if (parameters.TryGetTexture2d(out var fallbackTexture, CMaterialParams2.FallbackDiffuse) && fallbackTexture is not null)
        {
            texture = fallbackTexture;
            uvSet = 0;
            return true;
        }

        if (parameters.TryGetFirstTexture2d(out var firstTexture) && firstTexture is UTexture fallback)
        {
            texture = fallback;
            uvSet = 0;
            return true;
        }

        texture = null!;
        uvSet = 0;
        return false;
    }

    private static PreviewExportDto CreateModelExport(ModelPreviewDto model, string baseName, string title)
    {
        var files = new List<PreviewExportFileDto>
        {
            new(baseName + ".glb", "model/gltf-binary", BuildBinaryGlb(model)),
            new(baseName + ".fbx", "application/octet-stream", BuildAsciiFbx(model))
        };

        if (model.Materials is not null)
        {
            var textureFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var material in model.Materials)
            {
                AddModelTextureFiles(files, textureFileNames, baseName, material, "basecolor", GetDiffuseTextures(material));
                AddModelTextureFiles(files, textureFileNames, baseName, material, "normal", material.NormalTextures ?? []);
                AddModelTextureFiles(files, textureFileNames, baseName, material, "pbr", material.PbrTextures ?? []);
            }
        }

        return new PreviewExportDto("model", title, files);
    }

    private static IReadOnlyList<ModelTextureDto> GetDiffuseTextures(ModelMaterialDto material)
    {
        if (material.DiffuseTextures is { Count: > 0 })
            return material.DiffuseTextures;

        if (material.DiffuseTextureData is not { Length: > 0 })
            return [];

        return [new ModelTextureDto(
            material.DiffuseUvSet,
            string.IsNullOrWhiteSpace(material.DiffuseTextureName) ? $"material_{material.MaterialIndex}" : material.DiffuseTextureName!,
            material.DiffuseTextureMime ?? "image/png",
            material.DiffuseTextureData)];
    }

    private static void AddModelTextureFiles(
        List<PreviewExportFileDto> files,
        ISet<string> textureFileNames,
        string baseName,
        ModelMaterialDto material,
        string role,
        IReadOnlyList<ModelTextureDto> textures)
    {
        foreach (var texture in textures)
        {
            if (texture.Data.Length == 0)
                continue;

            var fileName = $"{baseName}_mat{material.MaterialIndex}_{role}_layer{texture.Layer}_{SanitizeFileName(texture.Name)}.png";
            if (!textureFileNames.Add(fileName))
                continue;

            files.Add(new PreviewExportFileDto(
                fileName,
                texture.MimeType,
                texture.Data));
        }
    }

    private static byte[] BuildBinaryGlb(ModelPreviewDto model)
    {
        var bin = new MemoryStream();
        var bufferViews = new List<GltfBufferView>();
        var accessors = new List<GltfAccessor>();

        var positionAccessor = AddFloatAccessor(
            model.Positions,
            componentType: 5126,
            type: "VEC3",
            count: model.VertexCount,
            byteStride: 12,
            target: 34962,
            min: [model.Bounds.MinX, model.Bounds.MinY, model.Bounds.MinZ],
            max: [model.Bounds.MaxX, model.Bounds.MaxY, model.Bounds.MaxZ]);

        var glbNormals = model.Normals.Length == model.VertexCount * 3
            ? InvertVector3Array(model.Normals)
            : [];
        var normalAccessor = -1;
        if (glbNormals.Length == model.VertexCount * 3)
        {
            normalAccessor = AddFloatAccessor(
                glbNormals,
                componentType: 5126,
                type: "VEC3",
                count: model.VertexCount,
                byteStride: 12,
                target: 34962);
        }

        var sourceUvSets = (model.UvSets is { Count: > 0 } sets ? sets : [model.Uvs])
            .Where(uvSet => uvSet.Length == model.VertexCount * 2)
            .Take(8)
            .ToArray();
        if (sourceUvSets.Length == 0 && model.Uvs.Length == model.VertexCount * 2)
            sourceUvSets = [model.Uvs];

        var uvAccessors = new List<int>();
        foreach (var uvSet in sourceUvSets)
        {
            uvAccessors.Add(AddFloatAccessor(
                uvSet,
                componentType: 5126,
                type: "VEC2",
                count: model.VertexCount,
                byteStride: 8,
                target: 34962));
        }

        var materialInfos = BuildGltfMaterialInfos(model.Materials, model.Sections);
        var materialByIndex = materialInfos
            .Select((material, index) => new { material.MaterialIndex, GltfIndex = index })
            .GroupBy(item => item.MaterialIndex)
            .ToDictionary(group => group.Key, group => group.First().GltfIndex);

        var imageInfos = new List<GltfImageInfo>();
        var baseColorTextureByMaterialIndex = new Dictionary<int, int>();
        var normalTextureByMaterialIndex = new Dictionary<int, int>();
        var pbrTextureByMaterialIndex = new Dictionary<int, int>();
        for (var materialIndex = 0; materialIndex < materialInfos.Count; materialIndex++)
        {
            var material = materialInfos[materialIndex].Material;
            if (material is null)
                continue;

            if (SelectPrimaryTexture(GetDiffuseTextures(material)) is { Data.Length: > 0 } baseColorTexture)
                baseColorTextureByMaterialIndex[material.MaterialIndex] = AddImage(baseColorTexture);

            if (SelectPrimaryTexture(material.NormalTextures) is { Data.Length: > 0 } normalTexture)
                normalTextureByMaterialIndex[material.MaterialIndex] = AddImage(normalTexture);

            if (SelectPrimaryTexture(material.PbrTextures) is { Data.Length: > 0 } pbrTexture)
                pbrTextureByMaterialIndex[material.MaterialIndex] = AddImage(pbrTexture);
        }

        var primitives = new List<GltfPrimitiveInfo>();
        var sections = model.Sections.Count > 0
            ? model.Sections
            : [new ModelSectionDto("Material 0", 0, 0, model.Indices.Length)];
        foreach (var section in sections)
        {
            var firstIndex = Math.Max(0, section.FirstIndex);
            var indexCount = Math.Max(0, section.IndexCount);
            if (firstIndex >= model.Indices.Length || indexCount == 0)
                continue;

            indexCount = Math.Min(indexCount, model.Indices.Length - firstIndex);
            indexCount -= indexCount % 3;
            if (indexCount <= 0)
                continue;

            var sectionIndices = new uint[indexCount];
            Array.Copy(model.Indices, firstIndex, sectionIndices, 0, indexCount);
            ReverseTriangleWinding(sectionIndices);
            var indexAccessor = AddUIntAccessor(
                sectionIndices,
                componentType: 5125,
                type: "SCALAR",
                count: sectionIndices.Length,
                byteStride: null,
                target: 34963,
                min: [sectionIndices.Length == 0 ? 0 : sectionIndices.Min()],
                max: [sectionIndices.Length == 0 ? 0 : sectionIndices.Max()]);

            var gltfMaterialIndex = materialByIndex.TryGetValue(section.MaterialIndex, out var mappedMaterial)
                ? mappedMaterial
                : 0;
            primitives.Add(new GltfPrimitiveInfo(indexAccessor, gltfMaterialIndex));
        }

        if (primitives.Count == 0)
        {
            var fallbackIndices = model.Indices.ToArray();
            ReverseTriangleWinding(fallbackIndices);
            var indexAccessor = AddUIntAccessor(
                fallbackIndices,
                componentType: 5125,
                type: "SCALAR",
                count: fallbackIndices.Length,
                byteStride: null,
                target: 34963,
                min: [fallbackIndices.Length == 0 ? 0 : fallbackIndices.Min()],
                max: [fallbackIndices.Length == 0 ? 0 : fallbackIndices.Max()]);
            primitives.Add(new GltfPrimitiveInfo(indexAccessor, 0));
        }

        var jsonBytes = BuildGlbJson(
            model,
            checked((int) bin.Length),
            bufferViews,
            accessors,
            imageInfos,
            materialInfos,
            baseColorTextureByMaterialIndex,
            normalTextureByMaterialIndex,
            pbrTextureByMaterialIndex,
            primitives,
            positionAccessor,
            normalAccessor,
            uvAccessors);

        return ComposeGlb(jsonBytes, bin.ToArray());

        int AddFloatAccessor(
            IReadOnlyList<float> values,
            int componentType,
            string type,
            int count,
            int? byteStride,
            int? target,
            IReadOnlyList<float>? min = null,
            IReadOnlyList<float>? max = null)
        {
            Align(bin, 4);
            var offset = checked((int) bin.Position);
            foreach (var value in values)
                WriteSingle(bin, value);

            var byteLength = checked((int) bin.Position - offset);
            var bufferView = bufferViews.Count;
            bufferViews.Add(new GltfBufferView(offset, byteLength, byteStride, target));
            var accessor = accessors.Count;
            accessors.Add(new GltfAccessor(bufferView, componentType, count, type, min?.ToArray(), max?.ToArray()));
            return accessor;
        }

        int AddUIntAccessor(
            IReadOnlyList<uint> values,
            int componentType,
            string type,
            int count,
            int? byteStride,
            int? target,
            IReadOnlyList<uint>? min = null,
            IReadOnlyList<uint>? max = null)
        {
            Align(bin, 4);
            var offset = checked((int) bin.Position);
            foreach (var value in values)
                WriteUInt32(bin, value);

            var byteLength = checked((int) bin.Position - offset);
            var bufferView = bufferViews.Count;
            bufferViews.Add(new GltfBufferView(offset, byteLength, byteStride, target));
            var accessor = accessors.Count;
            accessors.Add(new GltfAccessor(
                bufferView,
                componentType,
                count,
                type,
                min?.Select(value => (float) value).ToArray(),
                max?.Select(value => (float) value).ToArray()));
            return accessor;
        }

        int AddImage(ModelTextureDto texture)
        {
            Align(bin, 4);
            var offset = checked((int) bin.Position);
            bin.Write(texture.Data, 0, texture.Data.Length);
            var viewIndex = bufferViews.Count;
            bufferViews.Add(new GltfBufferView(offset, texture.Data.Length, null, null));
            var imageIndex = imageInfos.Count;
            imageInfos.Add(new GltfImageInfo(
                viewIndex,
                string.IsNullOrWhiteSpace(texture.MimeType) ? "image/png" : texture.MimeType,
                texture.Name));
            return imageIndex;
        }
    }

    private static ModelTextureDto? SelectPrimaryTexture(IReadOnlyList<ModelTextureDto>? textures)
    {
        return textures?
            .Where(texture => texture.Data.Length > 0)
            .OrderBy(texture => texture.Layer)
            .FirstOrDefault();
    }

    private static float[] InvertVector3Array(IReadOnlyList<float> values)
    {
        var output = new float[values.Count];
        for (var i = 0; i < values.Count; i++)
            output[i] = -values[i];
        return output;
    }

    private static void ReverseTriangleWinding(uint[] indices)
    {
        for (var i = 0; i + 2 < indices.Length; i += 3)
            (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
    }

    private static IReadOnlyList<GltfMaterialInfo> BuildGltfMaterialInfos(
        IReadOnlyList<ModelMaterialDto>? materials,
        IReadOnlyList<ModelSectionDto> sections)
    {
        var infos = new List<GltfMaterialInfo>();
        var seen = new HashSet<int>();

        if (materials is not null)
        {
            foreach (var material in materials)
            {
                if (!seen.Add(material.MaterialIndex))
                    continue;

                infos.Add(new GltfMaterialInfo(
                    material.MaterialIndex,
                    string.IsNullOrWhiteSpace(material.Name) ? $"Material {material.MaterialIndex}" : material.Name,
                    material));
            }
        }

        foreach (var section in sections)
        {
            if (!seen.Add(section.MaterialIndex))
                continue;

            infos.Add(new GltfMaterialInfo(
                section.MaterialIndex,
                string.IsNullOrWhiteSpace(section.Name) ? $"Material {section.MaterialIndex}" : section.Name,
                null));
        }

        if (infos.Count == 0)
            infos.Add(new GltfMaterialInfo(0, "Material 0", null));

        return infos;
    }

    private static byte[] BuildGlbJson(
        ModelPreviewDto model,
        int binaryByteLength,
        IReadOnlyList<GltfBufferView> bufferViews,
        IReadOnlyList<GltfAccessor> accessors,
        IReadOnlyList<GltfImageInfo> images,
        IReadOnlyList<GltfMaterialInfo> materials,
        IReadOnlyDictionary<int, int> baseColorTextureByMaterialIndex,
        IReadOnlyDictionary<int, int> normalTextureByMaterialIndex,
        IReadOnlyDictionary<int, int> pbrTextureByMaterialIndex,
        IReadOnlyList<GltfPrimitiveInfo> primitives,
        int positionAccessor,
        int normalAccessor,
        IReadOnlyList<int> uvAccessors)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("asset");
            writer.WriteString("version", "2.0");
            writer.WriteString("generator", "Prism");
            writer.WriteEndObject();

            writer.WriteNumber("scene", 0);
            writer.WriteStartArray("scenes");
            writer.WriteStartObject();
            writer.WriteStartArray("nodes");
            writer.WriteNumberValue(0);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartArray("nodes");
            writer.WriteStartObject();
            writer.WriteString("name", model.Name);
            writer.WriteNumber("mesh", 0);
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartArray("meshes");
            writer.WriteStartObject();
            writer.WriteString("name", model.Name);
            writer.WriteStartArray("primitives");
            foreach (var primitive in primitives)
            {
                writer.WriteStartObject();
                writer.WriteNumber("mode", 4);
                writer.WriteStartObject("attributes");
                writer.WriteNumber("POSITION", positionAccessor);
                if (normalAccessor >= 0)
                    writer.WriteNumber("NORMAL", normalAccessor);
                for (var uvSet = 0; uvSet < uvAccessors.Count; uvSet++)
                    writer.WriteNumber($"TEXCOORD_{uvSet}", uvAccessors[uvSet]);
                writer.WriteEndObject();
                writer.WriteNumber("indices", primitive.IndexAccessor);
                writer.WriteNumber("material", primitive.MaterialIndex);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartArray("materials");
            foreach (var material in materials)
            {
                writer.WriteStartObject();
                writer.WriteString("name", material.Name);
                writer.WriteStartObject("pbrMetallicRoughness");
                writer.WriteNumber("metallicFactor", pbrTextureByMaterialIndex.ContainsKey(material.MaterialIndex) ? 1 : 0);
                writer.WriteNumber("roughnessFactor", 1);
                if (baseColorTextureByMaterialIndex.TryGetValue(material.MaterialIndex, out var baseColorTextureIndex))
                {
                    writer.WriteStartObject("baseColorTexture");
                    writer.WriteNumber("index", baseColorTextureIndex);
                    writer.WriteEndObject();
                }
                if (pbrTextureByMaterialIndex.TryGetValue(material.MaterialIndex, out var pbrTextureIndex))
                {
                    writer.WriteStartObject("metallicRoughnessTexture");
                    writer.WriteNumber("index", pbrTextureIndex);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                if (normalTextureByMaterialIndex.TryGetValue(material.MaterialIndex, out var normalTextureIndex))
                {
                    writer.WriteStartObject("normalTexture");
                    writer.WriteNumber("index", normalTextureIndex);
                    writer.WriteEndObject();
                }
                if (pbrTextureByMaterialIndex.TryGetValue(material.MaterialIndex, out var occlusionTextureIndex))
                {
                    writer.WriteStartObject("occlusionTexture");
                    writer.WriteNumber("index", occlusionTextureIndex);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            if (images.Count > 0)
            {
                writer.WriteStartArray("samplers");
                writer.WriteStartObject();
                writer.WriteNumber("magFilter", 9729);
                writer.WriteNumber("minFilter", 9987);
                writer.WriteNumber("wrapS", 10497);
                writer.WriteNumber("wrapT", 10497);
                writer.WriteEndObject();
                writer.WriteEndArray();

                writer.WriteStartArray("textures");
                for (var i = 0; i < images.Count; i++)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("sampler", 0);
                    writer.WriteNumber("source", i);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteStartArray("images");
                foreach (var image in images)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", image.Name);
                    writer.WriteString("mimeType", image.MimeType);
                    writer.WriteNumber("bufferView", image.BufferView);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            writer.WriteStartArray("accessors");
            foreach (var accessor in accessors)
            {
                writer.WriteStartObject();
                writer.WriteNumber("bufferView", accessor.BufferView);
                writer.WriteNumber("componentType", accessor.ComponentType);
                writer.WriteNumber("count", accessor.Count);
                writer.WriteString("type", accessor.Type);
                WriteFloatArrayProperty(writer, "min", accessor.Min);
                WriteFloatArrayProperty(writer, "max", accessor.Max);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("bufferViews");
            foreach (var view in bufferViews)
            {
                writer.WriteStartObject();
                writer.WriteNumber("buffer", 0);
                writer.WriteNumber("byteOffset", view.ByteOffset);
                writer.WriteNumber("byteLength", view.ByteLength);
                if (view.ByteStride is not null)
                    writer.WriteNumber("byteStride", view.ByteStride.Value);
                if (view.Target is not null)
                    writer.WriteNumber("target", view.Target.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("buffers");
            writer.WriteStartObject();
            writer.WriteNumber("byteLength", binaryByteLength);
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static byte[] ComposeGlb(byte[] jsonBytes, byte[] binaryBytes)
    {
        var paddedJsonLength = AlignLength(jsonBytes.Length, 4);
        var paddedBinaryLength = AlignLength(binaryBytes.Length, 4);
        var totalLength = checked(12 + 8 + paddedJsonLength + 8 + paddedBinaryLength);
        var output = new byte[totalLength];
        var offset = 0;

        WriteUInt32(output, ref offset, 0x46546C67);
        WriteUInt32(output, ref offset, 2);
        WriteUInt32(output, ref offset, (uint) totalLength);
        WriteUInt32(output, ref offset, (uint) paddedJsonLength);
        WriteUInt32(output, ref offset, 0x4E4F534A);
        Array.Copy(jsonBytes, 0, output, offset, jsonBytes.Length);
        Array.Fill<byte>(output, 0x20, offset + jsonBytes.Length, paddedJsonLength - jsonBytes.Length);
        offset += paddedJsonLength;

        WriteUInt32(output, ref offset, (uint) paddedBinaryLength);
        WriteUInt32(output, ref offset, 0x004E4942);
        Array.Copy(binaryBytes, 0, output, offset, binaryBytes.Length);
        return output;
    }

    private static void WriteFloatArrayProperty(Utf8JsonWriter writer, string name, IReadOnlyList<float>? values)
    {
        if (values is null || values.Count == 0)
            return;

        writer.WriteStartArray(name);
        foreach (var value in values)
            writer.WriteNumberValue(value);
        writer.WriteEndArray();
    }

    private static void Align(Stream stream, int alignment)
    {
        var padding = AlignLength((int) stream.Position, alignment) - (int) stream.Position;
        for (var i = 0; i < padding; i++)
            stream.WriteByte(0);
    }

    private static int AlignLength(int value, int alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static void WriteSingle(Stream stream, float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(byte[] target, ref int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), value);
        offset += 4;
    }

    private static byte[] BuildAsciiFbx(ModelPreviewDto model)
    {
        var expanded = ExpandModelForFbx(model);
        var builder = new StringBuilder(Math.Max(4096, expanded.Positions.Length * 16));
        builder.AppendLine("; FBX 7.4.0 project file");
        builder.AppendLine("; Exported by Prism typed preview export");
        builder.AppendLine("FBXHeaderExtension:  {");
        builder.AppendLine("  FBXHeaderVersion: 1003");
        builder.AppendLine("  FBXVersion: 7400");
        builder.AppendLine("}");
        builder.AppendLine("Definitions:  {");
        builder.AppendLine("  ObjectType: \"Geometry\" { Count: 1 }");
        builder.AppendLine("  ObjectType: \"Model\" { Count: 1 }");
        builder.Append("  ObjectType: \"Material\" { Count: ").Append(expanded.Materials.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" }");
        builder.AppendLine("}");
        builder.AppendLine("Objects:  {");
        builder.AppendLine("  Geometry: 1, \"Geometry::PrismMesh\", \"Mesh\" {");
        AppendFbxFloatArray(builder, "Vertices", expanded.Positions);
        AppendFbxPolygonIndices(builder, expanded.Indices);
        if (expanded.Normals.Length == expanded.Positions.Length)
        {
            builder.AppendLine("    LayerElementNormal: 0 {");
            builder.AppendLine("      Version: 101");
            builder.AppendLine("      Name: \"\"");
            builder.AppendLine("      MappingInformationType: \"ByVertice\"");
            builder.AppendLine("      ReferenceInformationType: \"Direct\"");
            AppendFbxFloatArray(builder, "Normals", expanded.Normals, 6);
            builder.AppendLine("    }");
        }
        for (var uvSetIndex = 0; uvSetIndex < expanded.UvSets.Count; uvSetIndex++)
        {
            var uvSet = expanded.UvSets[uvSetIndex];
            if (uvSet.Length != expanded.VertexCount * 2)
                continue;

            builder.Append("    LayerElementUV: ").Append(uvSetIndex.ToString(CultureInfo.InvariantCulture)).AppendLine(" {");
            builder.AppendLine("      Version: 101");
            builder.Append("      Name: \"UVChannel_").Append(uvSetIndex.ToString(CultureInfo.InvariantCulture)).AppendLine("\"");
            builder.AppendLine("      MappingInformationType: \"ByVertice\"");
            builder.AppendLine("      ReferenceInformationType: \"Direct\"");
            AppendFbxFloatArray(builder, "UV", uvSet, 6);
            builder.AppendLine("    }");
        }
        if (expanded.FaceMaterials.Count > 0)
        {
            builder.AppendLine("    LayerElementMaterial: 0 {");
            builder.AppendLine("      Version: 101");
            builder.AppendLine("      Name: \"\"");
            builder.AppendLine("      MappingInformationType: \"ByPolygon\"");
            builder.AppendLine("      ReferenceInformationType: \"IndexToDirect\"");
            AppendFbxIntArray(builder, "Materials", expanded.FaceMaterials, 6);
            builder.AppendLine("    }");
        }
        builder.AppendLine("    Layer: 0 {");
        builder.AppendLine("      Version: 100");
        if (expanded.Normals.Length == expanded.Positions.Length)
        {
            builder.AppendLine("      LayerElement:  {");
            builder.AppendLine("        Type: \"LayerElementNormal\"");
            builder.AppendLine("        TypedIndex: 0");
            builder.AppendLine("      }");
        }
        for (var uvSetIndex = 0; uvSetIndex < expanded.UvSets.Count; uvSetIndex++)
        {
            if (expanded.UvSets[uvSetIndex].Length != expanded.VertexCount * 2)
                continue;

            builder.AppendLine("      LayerElement:  {");
            builder.AppendLine("        Type: \"LayerElementUV\"");
            builder.Append("        TypedIndex: ").Append(uvSetIndex.ToString(CultureInfo.InvariantCulture)).AppendLine();
            builder.AppendLine("      }");
        }
        if (expanded.FaceMaterials.Count > 0)
        {
            builder.AppendLine("      LayerElement:  {");
            builder.AppendLine("        Type: \"LayerElementMaterial\"");
            builder.AppendLine("        TypedIndex: 0");
            builder.AppendLine("      }");
        }
        builder.AppendLine("    }");
        builder.AppendLine("  }");
        builder.AppendLine("  Model: 2, \"Model::" + EscapeFbxString(model.Name) + "\", \"Mesh\" {");
        builder.AppendLine("    Version: 232");
        builder.AppendLine("    Properties70:  {");
        builder.AppendLine("      P: \"Lcl Translation\", \"Lcl Translation\", \"\", \"A\",0,0,0");
        builder.AppendLine("      P: \"Lcl Rotation\", \"Lcl Rotation\", \"\", \"A\",0,0,0");
        builder.AppendLine("      P: \"Lcl Scaling\", \"Lcl Scaling\", \"\", \"A\",1,1,1");
        builder.AppendLine("    }");
        builder.AppendLine("  }");
        for (var i = 0; i < expanded.Materials.Count; i++)
        {
            builder.Append("  Material: ").Append((100 + i).ToString(CultureInfo.InvariantCulture)).Append(", \"Material::")
                .Append(EscapeFbxString(expanded.Materials[i])).AppendLine("\", \"\" {");
            builder.AppendLine("    Version: 102");
            builder.AppendLine("    ShadingModel: \"lambert\"");
            builder.AppendLine("    Properties70:  {");
            builder.AppendLine("      P: \"DiffuseColor\", \"Color\", \"\", \"A\",1,1,1");
            builder.AppendLine("    }");
            builder.AppendLine("  }");
        }
        builder.AppendLine("}");
        builder.AppendLine("Connections:  {");
        builder.AppendLine("  C: \"OO\",1,2");
        for (var i = 0; i < expanded.Materials.Count; i++)
            builder.Append("  C: \"OO\",").Append((100 + i).ToString(CultureInfo.InvariantCulture)).AppendLine(",2");
        builder.AppendLine("}");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static ExpandedFbxModel ExpandModelForFbx(ModelPreviewDto model)
    {
        var sourceUvSets = (model.UvSets is { Count: > 0 } sets ? sets : [model.Uvs])
            .Where(uvSet => uvSet.Length == model.VertexCount * 2)
            .ToArray();
        if (sourceUvSets.Length == 0 && model.Uvs.Length == model.VertexCount * 2)
            sourceUvSets = [model.Uvs];

        var positions = new float[model.Indices.Length * 3];
        var normals = model.Normals.Length == model.VertexCount * 3 ? new float[model.Indices.Length * 3] : [];
        var uvSets = sourceUvSets
            .Select(_ => new float[model.Indices.Length * 2])
            .ToArray();
        var indices = new uint[model.Indices.Length];

        for (var i = 0; i < model.Indices.Length; i++)
        {
            var sourceIndex = (int)model.Indices[i];
            indices[i] = (uint)i;
            CopyTriple(model.Positions, sourceIndex, positions, i);
            if (normals.Length > 0)
                CopyTriple(model.Normals, sourceIndex, normals, i);

            for (var set = 0; set < uvSets.Length; set++)
                CopyPair(sourceUvSets[set], sourceIndex, uvSets[set], i);
        }

        var materialNames = new List<string>();
        var materialIndexById = new Dictionary<int, int>();
        if (model.Materials is { Count: > 0 })
        {
            foreach (var material in model.Materials)
            {
                if (materialIndexById.ContainsKey(material.MaterialIndex))
                    continue;

                materialIndexById[material.MaterialIndex] = materialNames.Count;
                materialNames.Add(string.IsNullOrWhiteSpace(material.Name) ? $"Material {material.MaterialIndex}" : material.Name);
            }
        }

        foreach (var section in model.Sections)
        {
            if (materialIndexById.ContainsKey(section.MaterialIndex))
                continue;

            materialIndexById[section.MaterialIndex] = materialNames.Count;
            materialNames.Add(string.IsNullOrWhiteSpace(section.Name) ? $"Material {section.MaterialIndex}" : section.Name);
        }

        if (materialNames.Count == 0)
        {
            materialIndexById[0] = 0;
            materialNames.Add("Material 0");
        }

        var faceMaterials = new int[model.Indices.Length / 3];
        var sections = model.Sections.Count > 0
            ? model.Sections
            : [new ModelSectionDto("Material 0", 0, 0, model.Indices.Length)];
        foreach (var section in sections)
        {
            if (!materialIndexById.TryGetValue(section.MaterialIndex, out var fbxMaterialIndex))
                fbxMaterialIndex = 0;

            var firstFace = Math.Max(0, section.FirstIndex / 3);
            var faceCount = Math.Max(0, section.IndexCount / 3);
            var lastFace = Math.Min(faceMaterials.Length, firstFace + faceCount);
            for (var face = firstFace; face < lastFace; face++)
                faceMaterials[face] = fbxMaterialIndex;
        }

        return new ExpandedFbxModel(
            model.Indices.Length,
            positions,
            normals,
            uvSets,
            indices,
            faceMaterials,
            materialNames);

        static void CopyTriple(IReadOnlyList<float> source, int sourceIndex, float[] target, int targetIndex)
        {
            var si = sourceIndex * 3;
            var ti = targetIndex * 3;
            target[ti] = source[si];
            target[ti + 1] = source[si + 1];
            target[ti + 2] = source[si + 2];
        }

        static void CopyPair(IReadOnlyList<float> source, int sourceIndex, float[] target, int targetIndex)
        {
            var si = sourceIndex * 2;
            var ti = targetIndex * 2;
            target[ti] = source[si];
            target[ti + 1] = source[si + 1];
        }
    }

    private sealed record ExpandedFbxModel(
        int VertexCount,
        float[] Positions,
        float[] Normals,
        IReadOnlyList<float[]> UvSets,
        uint[] Indices,
        IReadOnlyList<int> FaceMaterials,
        IReadOnlyList<string> Materials);

    private sealed record GltfBufferView(
        int ByteOffset,
        int ByteLength,
        int? ByteStride,
        int? Target);

    private sealed record GltfAccessor(
        int BufferView,
        int ComponentType,
        int Count,
        string Type,
        float[]? Min,
        float[]? Max);

    private sealed record GltfImageInfo(
        int BufferView,
        string MimeType,
        string Name);

    private sealed record GltfMaterialInfo(
        int MaterialIndex,
        string Name,
        ModelMaterialDto? Material);

    private sealed record GltfPrimitiveInfo(
        int IndexAccessor,
        int MaterialIndex);

    private static void AppendFbxFloatArray(StringBuilder builder, string name, IReadOnlyList<float> values, int indent = 4)
    {
        var pad = new string(' ', indent);
        builder.Append(pad).Append(name).Append(": *").Append(values.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" {");
        builder.Append(pad).Append("  a: ");
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(values[i].ToString("R", CultureInfo.InvariantCulture));
        }
        builder.AppendLine();
        builder.Append(pad).AppendLine("}");
    }

    private static void AppendFbxIntArray(StringBuilder builder, string name, IReadOnlyList<int> values, int indent = 4)
    {
        var pad = new string(' ', indent);
        builder.Append(pad).Append(name).Append(": *").Append(values.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" {");
        builder.Append(pad).Append("  a: ");
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(values[i].ToString(CultureInfo.InvariantCulture));
        }
        builder.AppendLine();
        builder.Append(pad).AppendLine("}");
    }

    private static void AppendFbxPolygonIndices(StringBuilder builder, IReadOnlyList<uint> indices)
    {
        builder.Append("    PolygonVertexIndex: *").Append(indices.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" {");
        builder.Append("      a: ");
        for (var i = 0; i < indices.Count; i += 3)
        {
            if (i > 0)
                builder.Append(',');

            var a = (int) indices[i];
            var b = i + 1 < indices.Count ? (int) indices[i + 1] : a;
            var c = i + 2 < indices.Count ? (int) indices[i + 2] : b;
            builder.Append(a.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(b.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append((-c - 1).ToString(CultureInfo.InvariantCulture));
        }
        builder.AppendLine();
        builder.AppendLine("    }");
    }

    private static string GetExportBaseName(string path, string? fallback)
    {
        var normalized = path.Replace('\\', '/');
        var name = normalized.SubstringAfterLast('/');
        name = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(name))
            name = string.IsNullOrWhiteSpace(fallback) ? "preview_export" : fallback;

        var paren = name.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0)
            name = name[..paren];

        return SanitizeFileName(name);
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "preview_export";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value.Replace('/', '_').Replace('\\', '_').Trim();
    }

    private static string EscapeFbxString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static bool IsBlueprintLike(UObject export)
    {
        return export is UClass or UBlueprintCore or UFunction or UUserDefinedStruct or UUserDefinedEnum;
    }

    private static AssetPreviewDto CreateBlueprintPreview(IPackage package, IReadOnlyList<UObject> exports)
    {
        var snippets = new List<string>();
        var details = new List<AssetPreviewDetailDto>
        {
            new("Package", package.Name),
            new("Exports", package.ExportMapLength.ToString("N0")),
            new("Name Count", package.NameMap.Length.ToString("N0"))
        };

        foreach (var export in exports)
        {
            switch (export)
            {
                case UBlueprintCore blueprint:
                    UObject loadedClass = default!;
#pragma warning disable CS8600
                    if (blueprint.GeneratedClass?.TryLoad(out loadedClass) == true &&
                        loadedClass is UClass generatedClass)
                    {
                        snippets.Add(DecompileClass(package, generatedClass));
                    }
                    else
                        snippets.Add($"// Blueprint {blueprint.Name} has no loaded generated class.");
#pragma warning restore CS8600
                    break;

                case UClass blueprintClass:
                    snippets.Add(DecompileClass(package, blueprintClass));
                    break;

                case UFunction function:
                    snippets.Add(DecompileFunction(function));
                    break;

                default:
                    snippets.Add($"// {export.ExportType} {export.Name}\n// No C++ pseudocode generator is available for this blueprint export.");
                    break;
            }
        }

        var text = snippets.Count == 0
            ? "// No blueprint pseudocode was produced."
            : CleanupPseudoCode(string.Join(Environment.NewLine + Environment.NewLine, snippets));

        details.Add(new("Pseudo Code", snippets.Count == 0 ? "No functions" : $"{snippets.Count:N0} block(s)"));
        return new AssetPreviewDto(
            "blueprint",
            exports.FirstOrDefault()?.Name ?? package.Name,
            details,
            Text: text);
    }

    private static string DecompileClass(IPackage package, UClass blueprintClass)
    {
        try
        {
            return blueprintClass.DecompileBlueprintToPseudo(package.Mappings ?? new TypeMappings());
        }
        catch (Exception ex)
        {
            var fallback = new List<string> { $"// Failed to decompile class {blueprintClass.Name}: {ex.Message}" };
            if (blueprintClass.FuncMap is { Count: > 0 })
            {
                foreach (var pair in blueprintClass.FuncMap)
                {
                    try
                    {
                        if (pair.Value.TryLoad(out var export) && export is UFunction function)
                            fallback.Add(DecompileFunction(function));
                    }
                    catch (Exception funcEx)
                    {
                        fallback.Add($"// Failed to decompile function {pair.Key.Text}: {funcEx.Message}");
                    }
                }
            }

            return string.Join(Environment.NewLine + Environment.NewLine, fallback);
        }
    }

    private static string DecompileFunction(UFunction function)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"// ({function.FunctionFlags.ToStringBitfield()})");
        builder.AppendLine($"void {function.Name}()");
        builder.AppendLine("{");

        if (function.ScriptBytecode is not { Length: > 0 })
        {
            builder.AppendLine("    // No Script Bytecode");
            builder.AppendLine("}");
            return builder.ToString();
        }

        BlueprintDecompilerUtils.Function = function;
        foreach (var expression in function.ScriptBytecode)
        {
            if (expression is EX_Nothing or EX_NothingInt32 or EX_EndFunctionParms or EX_EndStructConst or EX_EndArray or EX_EndArrayConst or EX_EndSet or EX_EndMap or EX_EndMapConst or EX_EndSetConst or EX_EndOfScript)
                continue;

            try
            {
                var line = BlueprintDecompilerUtils.GetLineExpression(expression);
                if (!string.IsNullOrWhiteSpace(line))
                    builder.AppendLine($"    {line};");
            }
            catch (Exception ex)
            {
                builder.AppendLine($"    // Failed to decompile {expression.GetType().Name} at {expression.StatementIndex}: {ex.Message}");
            }
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string CleanupPseudoCode(string text)
    {
        text = Regex.Replace(text, "__verse_0x[a-fA-F0-9]{8}_", "");
        text = Regex.Replace(text, @"CallFunc_([A-Za-z0-9_]+)_ReturnValue", "$1");
        text = Regex.Replace(text, @"K2Node_DynamicCast_([A-Za-z0-9_]+)", "$1");
        text = Regex.Replace(text, @"K2Node_([A-Za-z0-9_]+)", "$1");
        return text;
    }

    private static AssetPreviewDto CreateInfoPreview(AssetInfoDto info)
    {
        var text = string.Join(Environment.NewLine, info.Exports.Select(export =>
        {
            var props = string.Join(Environment.NewLine, export.Properties.Select(prop =>
                $"  - {prop.Name}: {prop.Type}{(string.IsNullOrWhiteSpace(prop.ValuePreview) ? string.Empty : $" = {prop.ValuePreview}")}"));
            return $"{export.Type} {export.Name} ({export.PropertyCount} properties){(props.Length == 0 ? string.Empty : Environment.NewLine + props)}";
        }));

        return new AssetPreviewDto(
            "info",
            info.Path,
            [
                new("Names", info.NameCount.ToString("N0")),
                new("Exports", info.ExportCount.ToString("N0"))
            ],
            Text: string.IsNullOrWhiteSpace(text) ? "No previewable export found." : text);
    }

    public Task<TexturePreviewDto?> TryReadTexturePreviewAsync(string assetPath, int maxMipSize = 1024, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = Provider;
            var fixedPath = provider.FixPath(assetPath);
            LogDecode($"Preview requested: asset={assetPath}, fixed={fixedPath}, maxMipSize={maxMipSize}, platform={provider.Versions.Platform}");

            try
            {
                if (!TryResolveGameFile(provider, fixedPath, out var gameFile))
                {
                    LogDecode($"GameFile not found for preview path: {fixedPath}");
                    LogNearbyFiles(provider, fixedPath);
                    return null;
                }

                LogDecode($"GameFile resolved: path={gameFile.Path}, name={gameFile.Name}, size={gameFile.Size}, type={gameFile.GetType().Name}");

                IPackage package;
                try
                {
                    package = provider.LoadPackage(gameFile);
                }
                catch (Exception ex)
                {
                    LogDecode($"Package load failed: file={gameFile.Path}, error={ex.GetType().Name}: {ex.Message}");
                    throw;
                }

                LogDecode($"Package loaded: {fixedPath}, exports={package.ExportMapLength}, names={package.NameMap.Length}");

                var sawTexture = false;
                string? failedTexture = null;
                Exception? decodeError = null;

                var exportIndex = 0;
                foreach (var export in package.ExportsLazy)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    exportIndex++;

                    CUE4Parse.UE4.Assets.Exports.UObject? value;
                    UTexture? texture;
                    try
                    {
                        value = export.Value;
                        texture = value as UTexture;
                    }
                    catch (Exception ex) when (IsMissingMappingsError(ex))
                    {
                        LogDecode($"Export #{exportIndex} failed due to missing mappings: {ex.Message}");
                        throw new InvalidOperationException("This asset uses unversioned properties. Import the matching .usmap mapping file, then open or preview it again.", ex);
                    }
                    catch (Exception ex)
                    {
                        LogDecode($"Export #{exportIndex} skipped: {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    if (texture is null)
                    {
                        LogDecode($"Export #{exportIndex} is not texture: type={value.ExportType}, name={value.Name}");
                        continue;
                    }

                    sawTexture = true;
                    LogDecode($"Texture export found: type={texture.ExportType}, name={texture.Name}, format={texture.Format}");
                    if (!TryEncodeTexturePreview(texture, maxMipSize, provider.Versions.Platform, out var preview, out var error))
                    {
                        failedTexture = $"{texture.ExportType} {texture.Name} ({texture.Format})";
                        decodeError = error;
                        LogDecode($"Texture decode failed: {failedTexture}, error={error?.GetType().Name}: {error?.Message ?? "decoder returned no bitmap"}");
                        continue;
                    }

                    LogDecode($"Texture decode succeeded: {preview.Name}, {preview.Width}x{preview.Height}, png={preview.PngData.Length} bytes");
                    return new TexturePreviewDto(
                        fixedPath,
                        preview.Name,
                        preview.Width,
                        preview.Height,
                        preview.PngData,
                        texture.Format.ToString());
                }

                if (sawTexture)
                {
                    var reason = decodeError is null ? "The decoder returned no bitmap." : decodeError.Message;
                    LogDecode($"Preview failed after seeing texture: texture={failedTexture}, reason={reason}");
                    throw new InvalidOperationException($"Texture found but could not be decoded{(failedTexture is null ? string.Empty : $" for {failedTexture}")}: {reason}", decodeError);
                }

                LogDecode($"No UTexture export found: {fixedPath}");
                return null;
            }
            catch (Exception ex) when (IsMissingMappingsError(ex))
            {
                LogDecode($"Preview failed due to missing mappings: {ex.Message}");
                throw new InvalidOperationException("This asset uses unversioned properties. Import the matching .usmap mapping file, then open or preview it again.", ex);
            }
        }, cancellationToken);
    }

    public async Task<ExportResult> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(request.OutputDirectory);

        var files = ResolveExportFiles(request.EntryPaths, request.IncludePackagePayloads);
        var errors = new List<string>();
        var completed = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ExportProgress(completed, files.Count, file.Path));

            try
            {
                var outputPath = BuildOutputPath(request.OutputDirectory, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                var data = await file.ReadAsync().ConfigureAwait(false);
                await File.WriteAllBytesAsync(outputPath, data, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add($"{file.Path}: {ex.Message}");
            }

            completed++;
        }

        progress?.Report(new ExportProgress(completed, files.Count, string.Empty));
        return new ExportResult(files.Count - errors.Count, errors.Count, errors);
    }

    public void Dispose()
    {
        DisposeProvider();
    }

    private DefaultFileProvider Provider => _provider ?? throw new InvalidOperationException("No pak session is open.");

    private IReadOnlyList<GameFile> ResolveExportFiles(IReadOnlyList<string> entryPaths, bool includePackagePayloads)
    {
        var provider = Provider;
        var results = new Dictionary<string, GameFile>(provider.PathComparer);

        foreach (var path in entryPaths)
        {
            var normalized = provider.FixPath(path).TrimStart('/');
            var isFolder = normalized.EndsWith('/');

            foreach (var file in provider.Files.Values)
            {
                if (isFolder)
                {
                    if (!file.Path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                else if (!file.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results[file.Path] = file;

                if (includePackagePayloads && file.IsUePackage)
                    AddPackagePayloads(provider, file, results);
            }
        }

        return results.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddPackagePayloads(DefaultFileProvider provider, GameFile file, IDictionary<string, GameFile> results)
    {
        foreach (var extension in GameFile.UePackagePayloadExtensions)
        {
            var payloadPath = $"{file.PathWithoutExtension}.{extension}";
            if (provider.TryGetGameFile(payloadPath, out var payload))
                results[payload.Path] = payload;
        }
    }

    private IReadOnlyList<ArchiveEntryDto> ListImmediate(string folder)
    {
        var directories = new Dictionary<string, ArchiveEntryDto>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ArchiveEntryDto>();

        foreach (var file in Provider.Files.Values)
        {
            if (!file.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = file.Path[folder.Length..];
            if (rest.Length == 0)
                continue;

            var slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                var dirName = rest[..slash];
                var fullPath = folder + dirName + "/";
                directories.TryAdd(fullPath, new ArchiveEntryDto(fullPath, dirName, true, 0, string.Empty, false, string.Empty));
            }
            else
            {
                if (!ShouldHidePackagePayload(file))
                    files.Add(ToAssetAwareDto(file));
            }
        }

        return directories.Values.Concat(files).ToArray();
    }

    private IReadOnlyList<ArchiveEntryDto> ListRecursive(string folder)
    {
        return Provider.Files.Values
            .Where(file => file.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .Where(file => !ShouldHidePackagePayload(file))
            .Select(ToAssetAwareDto)
            .ToArray();
    }

    private static ArchiveEntryDto ToDto(GameFile file)
    {
        return new ArchiveEntryDto(
            file.Path,
            file.Name,
            false,
            file.Size,
            file.Extension,
            file.IsEncrypted,
            file.CompressionMethod.ToString(),
            file.IsUePackage,
            [file.Path]);
    }

    private ArchiveEntryDto ToAssetAwareDto(GameFile file)
    {
        if (file.IsUePackagePayload && TryGetOwningPackage(Provider, file, out var owner))
            return ToAssetAwareDto(owner);

        lock (_cacheLock)
        {
            if (_entryCache.TryGetValue(file.Path, out var cachedEntry))
                return cachedEntry;
        }

        ArchiveEntryDto entry;
        if (!file.IsUePackage)
        {
            entry = ToDto(file);
        }
        else
        {
            var related = GetRelatedFiles(Provider, file).ToArray();
            entry = new ArchiveEntryDto(
                file.Path,
                file.Name,
                false,
                related.Sum(relatedFile => relatedFile.Size),
                file.Extension,
                related.Any(relatedFile => relatedFile.IsEncrypted),
                string.Join(", ", related.Select(relatedFile => relatedFile.CompressionMethod.ToString()).Distinct(StringComparer.OrdinalIgnoreCase)),
                true,
                related.Select(relatedFile => relatedFile.Path).ToArray());
        }

        lock (_cacheLock)
        {
            _entryCache[file.Path] = entry;
        }

        return entry;
    }

    private bool ShouldHidePackagePayload(GameFile file)
    {
        if (!file.IsUePackagePayload)
            return false;

        return TryGetOwningPackage(Provider, file, out _);
    }

    private static bool TryGetOwningPackage(DefaultFileProvider provider, GameFile file, out GameFile owner)
    {
        if (provider.TryGetGameFile($"{file.PathWithoutExtension}.uasset", out owner!) ||
            provider.TryGetGameFile($"{file.PathWithoutExtension}.umap", out owner!))
        {
            return true;
        }

        owner = null!;
        return false;
    }

    private static IReadOnlyList<GameFile> GetRelatedFiles(DefaultFileProvider provider, GameFile file)
    {
        if (!file.IsUePackage)
            return [file];

        var results = new Dictionary<string, GameFile>(provider.PathComparer)
        {
            [file.Path] = file
        };
        AddPackagePayloads(provider, file, results);

        return results.Values.OrderBy(related => PackagePartOrder(related.Extension)).ToArray();
    }

    private static int PackagePartOrder(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            "uasset" or "umap" => 0,
            "uexp" => 1,
            "ubulk" => 2,
            "uptnl" => 3,
            _ => 4
        };
    }

    private static bool IsDirectAudioExtension(string extension)
    {
        return extension is "ogg" or "oga" or "wav" or "mp3" or "m4a" or "aac" or "flac" or "opus" or "wem" or "binka" or "rada" or "at9";
    }

    private static bool IsDirectVideoExtension(string extension)
    {
        return extension is "mp4" or "m4v" or "webm" or "mov" or "ogv";
    }

    private static string NormalizeExtension(string? extension)
    {
        return string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.Trim().TrimStart('.').ToLowerInvariant();
    }

    private static string? GetMimeType(string extension)
    {
        return extension switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            "ogg" or "oga" => "audio/ogg",
            "wav" => "audio/wav",
            "mp3" => "audio/mpeg",
            "m4a" => "audio/mp4",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "opus" => "audio/ogg",
            "wem" => "audio/x-wem",
            "binka" => "audio/x-binka",
            "rada" => "audio/x-rada",
            "at9" => "audio/x-at9",
            "mp4" or "m4v" => "video/mp4",
            "webm" => "video/webm",
            "mov" => "video/quicktime",
            "ogv" => "video/ogg",
            _ => string.IsNullOrWhiteSpace(extension) ? null : "application/octet-stream"
        };
    }

    private static bool CanPlayMime(string? mimeType)
    {
        return mimeType is "image/png" or "image/jpeg" or "image/webp" or
            "audio/ogg" or "audio/wav" or "audio/mpeg" or "audio/mp4" or "audio/aac" or "audio/flac" or
            "video/mp4" or "video/webm" or "video/ogg";
    }

    private static bool TryResolveMediaFile(DefaultFileProvider provider, string reference, out GameFile file)
    {
        file = null!;
        if (string.IsNullOrWhiteSpace(reference))
            return false;

        var normalized = reference
            .Replace('\\', '/')
            .Trim()
            .Trim('"')
            .TrimStart('/');

        if (normalized.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[7..].TrimStart('/');

        var contentMarker = "/Content/";
        var contentIndex = normalized.IndexOf(contentMarker, StringComparison.OrdinalIgnoreCase);
        if (contentIndex >= 0)
            normalized = normalized[(contentIndex + contentMarker.Length)..];

        var candidates = new List<string> { normalized };
        if (normalized.StartsWith("./", StringComparison.Ordinal))
            candidates.Add(normalized[2..]);
        if (normalized.Contains("/Movies/", StringComparison.OrdinalIgnoreCase))
            candidates.Add(normalized.SubstringAfter("/Movies/"));

        foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (provider.TryGetGameFile(candidate, out file!))
                return true;

            var withMovies = "Content/Movies/" + candidate.SubstringAfterLast('/');
            if (provider.TryGetGameFile(withMovies, out file!))
                return true;
        }

        var name = normalized.SubstringAfterLast('/');
        if (string.IsNullOrWhiteSpace(name))
            return false;

        file = provider.Files.Values.FirstOrDefault(candidate =>
            candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            candidate.Path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase))!;
        return file is not null;
    }

    private static float ToSingle(double value) => (float) value;
    private static float ToSingle(float value) => value;

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:N0} {units[unit]}" : $"{size:N2} {units[unit]}";
    }

    private static void AddDirectoryChain(
        IDictionary<string, Dictionary<string, ArchiveEntryDto>> folderMap,
        string path)
    {
        var folder = string.Empty;
        var start = 0;

        while (true)
        {
            var slash = path.IndexOf('/', start);
            if (slash < 0)
                return;

            var dirName = path[start..slash];
            var fullPath = path[..(slash + 1)];

            if (!folderMap.TryGetValue(folder, out var entries))
                folderMap[folder] = entries = new Dictionary<string, ArchiveEntryDto>(StringComparer.OrdinalIgnoreCase);

            entries.TryAdd(fullPath, new ArchiveEntryDto(fullPath, dirName, true, 0, string.Empty, false, string.Empty));

            if (!folderMap.ContainsKey(fullPath))
                folderMap[fullPath] = new Dictionary<string, ArchiveEntryDto>(StringComparer.OrdinalIgnoreCase);

            folder = fullPath;
            start = slash + 1;
        }
    }

    private static string GetParentFolder(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..(slash + 1)];
    }

    private static void AddTiming(ICollection<OperationTimingDto> timings, string name, System.Diagnostics.Stopwatch stopwatch)
    {
        stopwatch.Stop();
        timings.Add(new OperationTimingDto(name, stopwatch.ElapsedMilliseconds));
    }

    private void ClearCaches()
    {
        lock (_cacheLock)
        {
            _listCache.Clear();
            _entryCache.Clear();
            _directoryIndex = null;
        }
    }

    private async Task<byte[]> ReadGameFileAsync(string path)
    {
        var provider = Provider;
        var fixedPath = provider.FixPath(path);

        if (!provider.TryGetGameFile(fixedPath, out var file))
            throw new FileNotFoundException("The archive entry was not found.", fixedPath);

        return await file.ReadAsync().ConfigureAwait(false);
    }

    private static string NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder is "/")
            return string.Empty;

        var normalized = folder.Replace('\\', '/').TrimStart('/');
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    private static string NormalizeAesKey(string key)
    {
        var trimmed = key.Trim();
        return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? trimmed : "0x" + trimmed;
    }

    private static EGame ParseGame(string game)
    {
        return Enum.TryParse<EGame>(game, true, out var parsed) ? parsed : EGame.GAME_UE5_6;
    }

    private static string BuildOutputPath(string outputDirectory, string archivePath)
    {
        var parts = archivePath.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizePathPart);

        return Path.Combine([outputDirectory, .. parts]);
    }

    private static string SanitizePathPart(string part)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            part = part.Replace(invalid, '_');
        return part;
    }

    private static string? PreviewValue(object? value)
    {
        if (value is null)
            return null;

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return text.Length <= 160 ? text : text[..160] + "...";
    }

    private static bool TryEncodeTexturePreview(
        UTexture texture,
        int maxMipSize,
        ETexturePlatform platform,
        out EncodedTexturePreview preview,
        out Exception? error)
    {
        preview = default;
        error = null;

        try
        {
            CTexture? bitmap;
            var textureName = texture.Name;

            if (texture is UTexture2DArray textureArray)
            {
                bitmap = textureArray.DecodeTextureArray(platform)?.FirstOrDefault();
                textureName += "_0";
            }
            else
            {
                bitmap = texture.Decode(maxMipSize, platform);
                if (bitmap is not null && texture is UTextureCube)
                    bitmap = bitmap.ToPanorama();
            }

            if (bitmap is null)
                return false;

            var pngData = bitmap.Encode(ETextureFormat.Png, false, out _);
            preview = new EncodedTexturePreview(textureName, bitmap.Width, bitmap.Height, pngData);
            return pngData.Length > 0;
        }
        catch (Exception ex) when (IsMissingMappingsError(ex))
        {
            throw new InvalidOperationException("This texture requires the matching .usmap mapping file.", ex);
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    private readonly record struct EncodedTexturePreview(string Name, int Width, int Height, byte[] PngData);

    private static bool IsMissingMappingsError(Exception ex)
    {
        return ex.Message.Contains("mapping file is missing", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("unversioned properties", StringComparison.OrdinalIgnoreCase) ||
               (ex.InnerException is not null && IsMissingMappingsError(ex.InnerException));
    }

    private static bool TryResolveGameFile(DefaultFileProvider provider, string path, out GameFile file)
    {
        if (provider.TryGetGameFile(path, out file!))
            return true;

        var dot = path.LastIndexOf('.');
        var noExtension = dot < 0 ? path : path[..dot];
        if (!string.Equals(noExtension, path, StringComparison.Ordinal) &&
            provider.TryGetGameFile(noExtension, out file!))
        {
            return true;
        }

        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (!string.Equals(normalized, path, StringComparison.Ordinal) &&
            provider.TryGetGameFile(normalized, out file!))
        {
            return true;
        }

        file = null!;
        return false;
    }

    private void LogNearbyFiles(DefaultFileProvider provider, string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var nearby = provider.Files.Values
            .Where(file => file.Path.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .Select(file => file.Path)
            .ToArray();

        LogDecode(nearby.Length == 0
            ? $"No nearby files found for name '{name}'."
            : $"Nearby files for '{name}': {string.Join(" | ", nearby)}");
    }

    private static byte[] ConvertToRgba8888(CTexture texture)
    {
        var pixelCount = checked(texture.Width * texture.Height);
        var output = new byte[checked(pixelCount * 4)];
        var input = texture.Data;

        switch (texture.PixelFormat)
        {
            case EPixelFormat.PF_R8G8B8A8:
                Buffer.BlockCopy(input, 0, output, 0, Math.Min(input.Length, output.Length));
                return output;

            case EPixelFormat.PF_B8G8R8A8:
            {
                for (var i = 0; i < pixelCount; i++)
                {
                    var src = i * 4;
                    output[src] = input[src + 2];
                    output[src + 1] = input[src + 1];
                    output[src + 2] = input[src];
                    output[src + 3] = input[src + 3];
                }
                return output;
            }

            case EPixelFormat.PF_A8R8G8B8:
            {
                for (var i = 0; i < pixelCount; i++)
                {
                    var src = i * 4;
                    output[src] = input[src + 1];
                    output[src + 1] = input[src + 2];
                    output[src + 2] = input[src + 3];
                    output[src + 3] = input[src];
                }
                return output;
            }

            case EPixelFormat.PF_G8:
            case EPixelFormat.PF_R8:
            {
                for (var i = 0; i < pixelCount; i++)
                {
                    var gray = input[i];
                    var dst = i * 4;
                    output[dst] = gray;
                    output[dst + 1] = gray;
                    output[dst + 2] = gray;
                    output[dst + 3] = byte.MaxValue;
                }
                return output;
            }

            default:
                throw new NotSupportedException($"Texture preview does not support decoded pixel format {texture.PixelFormat} yet.");
        }
    }

    private void DisposeProvider()
    {
        _provider?.Dispose();
        _provider = null;
        ClearCaches();
    }

    private void LogDecode(string message)
    {
        _decodeLogger?.Invoke(message);
    }
}
