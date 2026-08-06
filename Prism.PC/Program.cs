using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PakTool.Core;
using UAssetAPI;
using UAssetTexture.Core;

var port = int.TryParse(Environment.GetEnvironmentVariable("PRISM_PC_PORT"), out var configuredPort) && configuredPort > 0
    ? configuredPort
    : FindFreePort();
var url = $"http://127.0.0.1:{port}";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(url);
builder.Services.AddSingleton<PrismPcState>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/state", async (PrismPcState state) => await state.GetStateAsync());
app.MapPost("/api/open", async (PrismPcState state, OpenPakRequest request) => await Guard(() => state.OpenAsync(request)));
app.MapGet("/api/list", async (PrismPcState state, string? folder) => await Guard(() => state.ListAsync(folder)));
app.MapGet("/api/search", async (PrismPcState state, string q) => await Guard(() => state.SearchAsync(q)));
app.MapGet("/api/preview", async (PrismPcState state, string path) => await Guard(() => state.PreviewAsync(path)));
app.MapPost("/api/export/raw", async (PrismPcState state, ExportRawRequest request) => await Guard(() => state.ExportRawAsync(request)));
app.MapPost("/api/export/preview", async (PrismPcState state, ExportPreviewRequest request) => await Guard(() => state.ExportPreviewAsync(request)));
app.MapPost("/api/merge/inspect", async (MergeRequest request) => await Guard(() => PrismPcState.InspectMergeAsync(request)));
app.MapPost("/api/merge/build", async (MergeRequest request) => await Guard(() => PrismPcState.BuildMergeAsync(request)));

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"Prism PC WebUI: {url}");
    if (!string.Equals(Environment.GetEnvironmentVariable("PRISM_PC_NO_BROWSER"), "1", StringComparison.Ordinal))
        TryOpenBrowser(url);
});

await app.RunAsync();

static async Task<IResult> Guard<T>(Func<Task<T>> action)
{
    try
    {
        return Results.Json(await action());
    }
    catch (Exception ex)
    {
        return Results.Json(new ApiError(ex.Message, ex.ToString()), statusCode: 500);
    }
}

static int FindFreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        return ((IPEndPoint) listener.LocalEndpoint).Port;
    }
    finally
    {
        listener.Stop();
    }
}

static void TryOpenBrowser(string address)
{
    try
    {
        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }
    catch
    {
        Console.WriteLine("Open the WebUI URL manually if the browser did not launch.");
    }
}

public sealed class PrismPcState : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PakArchiveSession? _session;
    private string _currentFolder = string.Empty;
    private string _status = "Ready.";

    public async Task<StateResponse> GetStateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return new StateResponse(_session is not null, _currentFolder, _status);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OpenPakResponse> OpenAsync(OpenPakRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PakPath))
            throw new InvalidOperationException("Pak path is required.");

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _session?.Dispose();
            _session = new PakArchiveSession();
            var result = await _session.OpenAsync(new PakOpenOptions(
                [request.PakPath],
                NullIfWhiteSpace(request.AesKey),
                NullIfWhiteSpace(request.UsmapPath))).ConfigureAwait(false);

            _currentFolder = string.Empty;
            _status = $"Opened {Path.GetFileName(request.PakPath)}: {result.FileCount:N0} files.";
            var entries = await _session.ListAsync(_currentFolder).ConfigureAwait(false);
            return new OpenPakResponse(result, ToView(entries), _currentFolder, _status);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ListResponse> ListAsync(string? folder)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = RequireSession();
            _currentFolder = NormalizeFolder(folder);
            var entries = await session.ListAsync(_currentFolder).ConfigureAwait(false);
            _status = $"{entries.Count:N0} item(s).";
            return new ListResponse(ToView(entries), _currentFolder, _status);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ListResponse> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await ListAsync(_currentFolder).ConfigureAwait(false);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = await RequireSession().SearchAsync(query, 1000).ConfigureAwait(false);
            _status = $"Search returned {entries.Count:N0} item(s).";
            return new ListResponse(ToView(entries), _currentFolder, _status);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PreviewResponse> PreviewAsync(string path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var preview = await RequireSession().ReadPreviewAsync(path).ConfigureAwait(false);
            _status = preview.Title;
            return ToPreview(preview, _status);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExportResult> ExportRawAsync(ExportRawRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path) || string.IsNullOrWhiteSpace(request.OutputDirectory))
            throw new InvalidOperationException("Path and output directory are required.");

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await RequireSession().ExportAsync(new ExportRequest([request.Path], request.OutputDirectory)).ConfigureAwait(false);
            _status = $"Exported {result.Succeeded:N0}, failed {result.Failed:N0}.";
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PreviewExportResponse> ExportPreviewAsync(ExportPreviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path) || string.IsNullOrWhiteSpace(request.OutputDirectory))
            throw new InvalidOperationException("Path and output directory are required.");

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var export = await RequireSession().ReadTypedPreviewExportAsync(request.Path).ConfigureAwait(false);
            var files = new List<string>();
            foreach (var file in export.Files)
            {
                var outputPath = Path.Combine(request.OutputDirectory, SanitizeFileName(file.FileName));
                await File.WriteAllBytesAsync(outputPath, file.Data).ConfigureAwait(false);
                files.Add(outputPath);
            }

            _status = $"Exported {files.Count:N0} preview file(s).";
            return new PreviewExportResponse(export.Kind, export.Title, files);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task<MergeInspectionResponse> InspectMergeAsync(MergeRequest request)
    {
        ValidateMergeRequest(request, requireOutput: false);
        using var baseSession = new PakArchiveSession();
        using var mergeSession = new PakArchiveSession();
        await baseSession.OpenAsync(new PakOpenOptions([request.BasePakPath], NullIfWhiteSpace(request.AesKey), NullIfWhiteSpace(request.UsmapPath))).ConfigureAwait(false);
        await mergeSession.OpenAsync(new PakOpenOptions([request.MergePakPath], NullIfWhiteSpace(request.AesKey), NullIfWhiteSpace(request.UsmapPath))).ConfigureAwait(false);

        var baseFiles = await baseSession.ListRawFilePathsAsync().ConfigureAwait(false);
        var mergeFiles = await mergeSession.ListRawFilePathsAsync().ConfigureAwait(false);
        var conflicts = mergeFiles.Where(baseFiles.Contains).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new MergeInspectionResponse(baseFiles.Count, mergeFiles.Count, conflicts.Length, conflicts.Take(500).ToArray());
    }

    public static async Task<MergeBuildResponse> BuildMergeAsync(MergeRequest request)
    {
        ValidateMergeRequest(request, requireOutput: true);

        var tempRoot = Path.Combine(Path.GetTempPath(), "PrismPcMerge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var baseSession = new PakArchiveSession();
            using var mergeSession = new PakArchiveSession();
            var aes = NullIfWhiteSpace(request.AesKey);
            var usmap = NullIfWhiteSpace(request.UsmapPath);
            await baseSession.OpenAsync(new PakOpenOptions([request.BasePakPath], aes, usmap)).ConfigureAwait(false);
            await mergeSession.OpenAsync(new PakOpenOptions([request.MergePakPath], aes, usmap)).ConfigureAwait(false);

            var baseFiles = await baseSession.CopyAllRawFilesAsync(Path.Combine(tempRoot, "base")).ConfigureAwait(false);
            var mergeFiles = await mergeSession.CopyAllRawFilesAsync(Path.Combine(tempRoot, "merge")).ConfigureAwait(false);

            var mapped = baseFiles.ToDictionary(
                x => x.PakPath,
                x => new ModifiedPakFile(x.DiskPath, x.PakPath),
                StringComparer.OrdinalIgnoreCase);

            var conflicts = 0;
            var replaced = 0;
            foreach (var file in mergeFiles)
            {
                var hasConflict = mapped.ContainsKey(file.PakPath);
                if (hasConflict)
                {
                    conflicts++;
                    if (!request.ReplaceConflicts)
                        continue;
                    replaced++;
                }

                mapped[file.PakPath] = new ModifiedPakFile(file.DiskPath, file.PakPath);
            }

            ModifiedPakPackService.Pack(new ModifiedPakRequest(
                mapped.Values.OrderBy(x => x.PakPath, StringComparer.OrdinalIgnoreCase).ToArray(),
                request.OutputPakPath!,
                UseCompression: request.UseOodleCompression,
                Compression: PakCompression.Oodle));

            return new MergeBuildResponse(request.OutputPakPath!, mapped.Count, conflicts, replaced);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _gate.Dispose();
    }

    private PakArchiveSession RequireSession() => _session ?? throw new InvalidOperationException("Open a Pak first.");

    private static IReadOnlyList<EntryView> ToView(IReadOnlyList<ArchiveEntryDto> entries) =>
        entries.Select(entry => new EntryView(
            entry.FullPath,
            entry.Name,
            entry.IsDirectory,
            entry.Size,
            entry.Extension,
            entry.IsEncrypted,
            entry.CompressionMethod,
            entry.IsAssetPackage,
            entry.RelatedPaths ?? [],
            GuessKind(entry),
            FormatSize(entry.Size))).ToArray();

    private static PreviewResponse ToPreview(AssetPreviewDto preview, string status)
    {
        var dataUrl = preview.Data is { Length: > 0 } data && !string.IsNullOrWhiteSpace(preview.MimeType)
            ? $"data:{preview.MimeType};base64,{Convert.ToBase64String(data)}"
            : preview.Kind.Equals("texture", StringComparison.OrdinalIgnoreCase) && preview.Data is { Length: > 0 } pngData
                ? $"data:image/png;base64,{Convert.ToBase64String(pngData)}"
                : null;

        return new PreviewResponse(
            preview.Kind,
            preview.Title,
            preview.Details,
            preview.Text,
            preview.MimeType,
            dataUrl,
            preview.CanPlay,
            preview.Model is not null,
            status);
    }

    private static string GuessKind(ArchiveEntryDto entry)
    {
        if (entry.IsDirectory)
            return "Folder";
        var ext = entry.Extension.TrimStart('.').ToLowerInvariant();
        if (ext is "locres")
            return "Locres";
        if (!entry.IsAssetPackage)
            return string.IsNullOrWhiteSpace(ext) ? "File" : ext.ToUpperInvariant();
        var path = entry.FullPath.Replace('\\', '/');
        if (path.Contains("/Texture", StringComparison.OrdinalIgnoreCase) || path.Contains("/T_", StringComparison.OrdinalIgnoreCase))
            return "Texture";
        if (path.Contains("/Material", StringComparison.OrdinalIgnoreCase) || path.Contains("/MI_", StringComparison.OrdinalIgnoreCase) || path.Contains("/M_", StringComparison.OrdinalIgnoreCase))
            return "Material";
        if (path.Contains("/Mesh", StringComparison.OrdinalIgnoreCase) || path.Contains("/SK_", StringComparison.OrdinalIgnoreCase) || path.Contains("/SM_", StringComparison.OrdinalIgnoreCase))
            return "Model";
        return "UAsset";
    }

    private static void ValidateMergeRequest(MergeRequest request, bool requireOutput)
    {
        if (string.IsNullOrWhiteSpace(request.BasePakPath) || string.IsNullOrWhiteSpace(request.MergePakPath))
            throw new InvalidOperationException("Both Pak paths are required.");
        if (requireOutput && string.IsNullOrWhiteSpace(request.OutputPakPath))
            throw new InvalidOperationException("Output Pak path is required.");
    }

    private static string NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder == "/")
            return string.Empty;
        var normalized = folder.Replace('\\', '/').TrimStart('/');
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string FormatSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double) size;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{size} B" : $"{value:0.##} {units[unit]}";
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');
        return fileName;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}

public sealed record ApiError(string Message, string Detail);
public sealed record StateResponse(bool IsOpen, string CurrentFolder, string Status);
public sealed record OpenPakRequest(string PakPath, string? UsmapPath, string? AesKey);
public sealed record OpenPakResponse(PakOpenResult Result, IReadOnlyList<EntryView> Entries, string CurrentFolder, string Status);
public sealed record ListResponse(IReadOnlyList<EntryView> Entries, string CurrentFolder, string Status);
public sealed record EntryView(
    string FullPath,
    string Name,
    bool IsDirectory,
    long Size,
    string Extension,
    bool IsEncrypted,
    string CompressionMethod,
    bool IsAssetPackage,
    IReadOnlyList<string> RelatedPaths,
    string Kind,
    string SizeText);
public sealed record PreviewResponse(
    string Kind,
    string Title,
    IReadOnlyList<AssetPreviewDetailDto> Details,
    string? Text,
    string? MimeType,
    string? DataUrl,
    bool CanPlay,
    bool HasModel,
    string Status);
public sealed record ExportRawRequest(string Path, string OutputDirectory);
public sealed record ExportPreviewRequest(string Path, string OutputDirectory);
public sealed record PreviewExportResponse(string Kind, string Title, IReadOnlyList<string> Files);
public sealed record MergeRequest(
    string BasePakPath,
    string MergePakPath,
    string? OutputPakPath,
    string? UsmapPath,
    string? AesKey,
    bool ReplaceConflicts,
    bool UseOodleCompression);
public sealed record MergeInspectionResponse(int BaseCount, int MergeCount, int ConflictCount, IReadOnlyList<string> Conflicts);
public sealed record MergeBuildResponse(string OutputPakPath, int FileCount, int ConflictCount, int ReplacedCount);
