using UAssetAPI;

namespace UAssetTexture.Core;

public sealed record ModifiedPakFile(string DiskPath, string PakPath);

public sealed record ModifiedPakRequest(
    IReadOnlyList<ModifiedPakFile> Files,
    string OutputPakPath,
    PakVersion Version = PakVersion.V11,
    string MountPoint = "../../../",
    bool UseCompression = false,
    PakCompression Compression = PakCompression.Zlib);

public static class ModifiedPakPackService
{
    public static void Pack(ModifiedPakRequest request)
    {
        if (request.Files.Count == 0)
            throw new InvalidOperationException("No modified files are available to pack.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPakPath))!);

        try
        {
            using var output = File.Create(request.OutputPakPath);
            using var builder = new PakBuilder();
            if (request.UseCompression)
                builder.Compression([request.Compression]);

            using var writer = builder.Writer(output, request.Version, NormalizeMountPoint(request.MountPoint));
            foreach (var file in request.Files)
            {
                var diskPath = Path.GetFullPath(file.DiskPath);
                if (!File.Exists(diskPath))
                    throw new FileNotFoundException("Modified file was not found.", diskPath);

                writer.WriteFile(NormalizePakPath(file.PakPath), File.ReadAllBytes(diskPath));
            }

            writer.WriteIndex();
        }
        catch (Exception ex) when (IsNativePakWriterLoadFailure(ex))
        {
            throw new InvalidOperationException(
                "Could not load UAssetAPI PakWriter native library repak_bind. " +
                "Android builds must include arm64-v8a/librepak_bind.so.",
                ex);
        }
    }

    private static string NormalizePakPath(string pakPath)
    {
        var normalized = pakPath.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("../", StringComparison.Ordinal))
            throw new InvalidOperationException($"Invalid Pak path: {pakPath}");

        return normalized;
    }

    private static string NormalizeMountPoint(string mountPoint)
    {
        var normalized = string.IsNullOrWhiteSpace(mountPoint) ? "../../../" : mountPoint.Replace('\\', '/');
        return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : normalized + "/";
    }

    private static bool IsNativePakWriterLoadFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
                return true;
        }

        return false;
    }
}
