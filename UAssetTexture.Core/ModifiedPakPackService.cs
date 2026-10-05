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

/// <summary>
/// 打包结果。<see cref="OodleRequestedButUnavailable"/> 为 true 时说明调用方要了 Oodle
/// 压缩但本机没有 Oodle 原生库，已自动退回不压缩（详见 <see cref="Note"/>）。
/// </summary>
public sealed record ModifiedPakPackResult(bool OodleRequestedButUnavailable, string? Note);

public static class ModifiedPakPackService
{
    public static ModifiedPakPackResult Pack(ModifiedPakRequest request)
    {
        if (request.Files.Count == 0)
            throw new InvalidOperationException("No modified files are available to pack.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPakPath))!);

        // 关键防护：请求 Oodle 但本机没有 Oodle 原生库时**绝不能**把 Oodle 传给 repak_bind。
        // 那个 Rust 库找不到 Oodle 会 panic → abort 整个进程（0xC0000409），.NET 拦不住。
        // 所以这里退回不压缩，并把情况如实报给调用方。
        bool oodleRequested = request.UseCompression && request.Compression == PakCompression.Oodle;
        bool oodleUnavailable = oodleRequested && !OodleNative.IsAvailable;

        try
        {
            using var output = File.Create(request.OutputPakPath);
            using var builder = new PakBuilder();
            if (request.UseCompression && !oodleUnavailable)
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

        return new ModifiedPakPackResult(oodleUnavailable, oodleUnavailable ? OodleNative.MissingHint : null);
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
