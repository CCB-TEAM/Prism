using System.Runtime.InteropServices;

namespace UAssetAudio.Core;

/// <summary>
/// 进程内 BINKA 编码：调 <c>prism_encode_binka</c>（Windows 在 prism_binka.dll，
/// Android 在 libprism_codecs.so）。
///
/// 为什么不只用 <c>binka_encode.exe</c>：那是个 Windows 外部进程，手机上根本起不来。
/// 编码器源码（native/BinkaEnc）本身是平台无关的，编成 .so 之后手机也能替换音频。
/// </summary>
internal static class NativeBinkaEncoder
{
    private static readonly object LoadLock = new();
    private static PrismEncodeBinka? CachedExport;
    private static string? CachedLoadError;

    public static bool IsAvailable()
    {
        lock (LoadLock)
        {
            return TryLoad(out _, out _);
        }
    }

    public static byte[] Encode(PcmAudio audio, int quality)
    {
        if (!TryLoad(out var encode, out var error))
            throw new InvalidOperationException(error);

        var pcm = audio.Pcm16;
        var rawQuality = Math.Clamp(quality, BinkaEncoder.MinQuality, BinkaEncoder.MaxQuality);

        // 第一趟只问长度
        var status = encode(pcm, pcm.Length, audio.SampleRate, audio.Channels, rawQuality, 1, null!, 0, out var required);
        if (status != 0 || required <= 0)
            throw new InvalidOperationException($"BINKA 编码失败（状态 {status}）。");

        var buffer = new byte[required];
        status = encode(pcm, pcm.Length, audio.SampleRate, audio.Channels, rawQuality, 1, buffer, buffer.Length, out var written);
        if (status != 0 || written <= 0)
            throw new InvalidOperationException($"BINKA 编码失败（状态 {status}）。");

        if (written != buffer.Length)
            Array.Resize(ref buffer, written);

        return buffer;
    }

    private static bool TryLoad(out PrismEncodeBinka encode, out string error)
    {
        lock (LoadLock)
        {
            if (CachedExport is not null)
            {
                encode = CachedExport;
                error = string.Empty;
                return true;
            }

            if (CachedLoadError is not null)
            {
                encode = default!;
                error = CachedLoadError;
                return false;
            }

            var failures = new List<string>();
            foreach (var candidate in Candidates())
            {
                if (!TryOpen(candidate, out var handle, failures))
                    continue;

                if (NativeLibrary.TryGetExport(handle, "prism_encode_binka", out var export))
                {
                    CachedExport = Marshal.GetDelegateForFunctionPointer<PrismEncodeBinka>(export);
                    encode = CachedExport;
                    error = string.Empty;
                    return true;
                }

                failures.Add($"{candidate}: prism_encode_binka 未导出");
            }

            CachedLoadError = "没有可用的 BINKA 编码器：" +
                             (failures.Count == 0 ? "找不到 prism_codecs / prism_binka 原生库。" : string.Join(" | ", failures));
            encode = default!;
            error = CachedLoadError;
            return false;
        }
    }

    private static bool TryOpen(string name, out nint handle, List<string> failures)
    {
        if (NativeLibrary.TryLoad(name, out handle))
            return true;

        // Android 上 NativeLibrary 的探测有时不及 dlopen 直接给文件名可靠
        try
        {
            handle = dlopen(name, 2);
            if (handle != 0)
                return true;

            failures.Add($"{name}: dlopen 失败");
            return false;
        }
        catch (Exception ex)
        {
            handle = 0;
            failures.Add($"{name}: {ex.GetType().Name}");
            return false;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        foreach (var name in new[] { "prism_codecs", "prism_binka" })
        {
            yield return name;
            yield return "lib" + name;
            yield return name + ".so";
            yield return "lib" + name + ".so";
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PrismEncodeBinka(
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pcm,
        int pcmLength,
        int sampleRate,
        int channels,
        int quality,
        int generateSeekTable,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 7)] byte[] output,
        int outputCapacity,
        out int outputLength);

    [DllImport("libdl.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dlopen(string filename, int flags);
}
