using System.IO;
using UAssetAPI;

namespace UE_Pak_Manager;

public sealed class PakManagerService : IDisposable
{
    private FileStream? _openStream;
    private PakReader? _reader;

    public string? PakPath { get; private set; }

    public string MountPoint { get; private set; } = string.Empty;

    public PakVersion Version { get; private set; }

    public IReadOnlyList<PakFileItem> Open(string pakPath)
    {
        DisposeReader();

        _openStream = File.Open(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new PakBuilder().Reader(_openStream);
        PakPath = Path.GetFullPath(pakPath);
        MountPoint = _reader.GetMountPoint();
        Version = _reader.GetVersion();

        return _reader.Files()
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new PakFileItem { Path = path })
            .ToArray();
    }

    public void Extract(IEnumerable<PakFileItem> files, string outputDirectory, IProgress<string>? progress = null)
    {
        if (_reader is null || _openStream is null)
        {
            throw new InvalidOperationException("No pak file is open.");
        }

        string root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);

        foreach (PakFileItem file in files)
        {
            progress?.Report($"Extracting {file.Path}");
            byte[]? data = _reader.Get(_openStream, file.Path);
            if (data is null)
            {
                throw new InvalidOperationException($"Failed to extract '{file.Path}'.");
            }

            string outputPath = GetSafeOutputPath(root, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, data);
        }
    }

    public static int PackDirectory(
        string sourceDirectory,
        string outputPakPath,
        string mountPoint,
        string pakPathPrefix,
        PakVersion version,
        PakCompression? compression,
        IProgress<string>? progress = null)
    {
        string sourceRoot = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException(sourceRoot);
        }

        string outputPath = Path.GetFullPath(outputPakPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        string[] files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using FileStream output = File.Create(outputPath);
        PakBuilder builder = new();
        if (compression is not null)
        {
            builder.Compression([compression.Value]);
        }

        using PakWriter writer = builder.Writer(output, version, NormalizeMountPoint(mountPoint));
        foreach (string file in files)
        {
            string entryPath = BuildPakEntryPath(sourceRoot, file, pakPathPrefix);
            progress?.Report($"Packing {entryPath}");
            writer.WriteFile(entryPath, File.ReadAllBytes(file));
        }

        writer.WriteIndex();
        return files.Length;
    }

    public void Dispose()
    {
        DisposeReader();
    }

    private void DisposeReader()
    {
        _reader?.Dispose();
        _openStream?.Dispose();
        _reader = null;
        _openStream = null;
        PakPath = null;
        MountPoint = string.Empty;
    }

    private static string BuildPakEntryPath(string sourceRoot, string filePath, string pakPathPrefix)
    {
        string relativePath = Path.GetRelativePath(sourceRoot, filePath).Replace('\\', '/');
        string prefix = pakPathPrefix.Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(prefix) ? relativePath : $"{prefix}/{relativePath}";
    }

    private static string NormalizeMountPoint(string mountPoint)
    {
        string normalized = string.IsNullOrWhiteSpace(mountPoint) ? "../../../" : mountPoint.Replace('\\', '/');
        return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : normalized + "/";
    }

    private static string GetSafeOutputPath(string root, string pakPath)
    {
        string relative = pakPath.Replace('\\', '/').TrimStart('/');
        while (relative.StartsWith("../", StringComparison.Ordinal))
        {
            relative = relative[3..];
        }

        string candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Pak path escapes the output directory: {pakPath}");
        }

        return candidate;
    }
}
