using System.Runtime.InteropServices;

namespace PakTool.Core;

internal sealed record NativeBinkaInfo(
    int SampleRate,
    int Channels,
    int SampleCount,
    int WavLength);

internal static class NativeBinkaDecoder
{
    private static readonly object LoadLock = new();
    private static NativeBinkaApi? CachedApi;
    private static string? CachedLoadError;

    public static bool TryDecodeToWav(
        byte[] input,
        long maxOutputBytes,
        out byte[] wavData,
        out NativeBinkaInfo info,
        out string error)
    {
        wavData = [];
        info = new NativeBinkaInfo(0, 0, 0, 0);
        error = string.Empty;

        if (input.Length == 0)
        {
            error = "BINKA payload is empty.";
            return false;
        }

        if (!TryLoadApi(out var api, out error))
            return false;

        var probeStatus = api.Probe(
            input,
            input.Length,
            out var sampleRate,
            out var channels,
            out var sampleCount,
            out var wavLength);

        if (probeStatus != 0)
        {
            error = $"Native BINKA probe failed with status {probeStatus}.";
            return false;
        }

        info = new NativeBinkaInfo(sampleRate, channels, sampleCount, wavLength);
        if (wavLength <= 44)
        {
            error = "Native BINKA probe returned an invalid WAV size.";
            return false;
        }

        if (wavLength > maxOutputBytes)
        {
            error = $"Decoded WAV would be {FormatBytes(wavLength)}, which exceeds the inline preview limit.";
            return false;
        }

        wavData = new byte[wavLength];
        var decodeStatus = api.Decode(
            input,
            input.Length,
            wavData,
            wavData.Length,
            out var writtenLength);

        if (decodeStatus != 0)
        {
            wavData = [];
            error = $"Native BINKA decode failed with status {decodeStatus}.";
            return false;
        }

        if (writtenLength <= 44 || writtenLength > wavData.Length)
        {
            wavData = [];
            error = "Native BINKA decode returned an invalid byte count.";
            return false;
        }

        if (writtenLength != wavData.Length)
            Array.Resize(ref wavData, writtenLength);

        return true;
    }

    private static bool TryLoadApi(out NativeBinkaApi api, out string error)
    {
        lock (LoadLock)
        {
            if (CachedApi is not null)
            {
                api = CachedApi;
                error = string.Empty;
                return true;
            }

            if (CachedLoadError is not null)
            {
                api = default!;
                error = CachedLoadError;
                return false;
            }

            var failures = new List<string>();
            nint handle = 0;
            foreach (var candidate in GetLibraryCandidates("prism_codecs"))
            {
                if (TryLoadNativeLibrary(candidate, out handle, failures) ||
                    TryDlopen(candidate, out handle, failures))
                {
                    break;
                }
            }

            if (handle == 0)
            {
                CachedLoadError = "Could not load Prism native codecs library. " + string.Join(" | ", failures);
                api = default!;
                error = CachedLoadError;
                return false;
            }

            if (!TryGetNativeExport(handle, "prism_probe_binka_audio", out var probeExport, failures) ||
                !TryGetNativeExport(handle, "prism_decode_binka_to_wav", out var decodeExport, failures))
            {
                CachedLoadError = "Prism native codecs library does not expose BINKA decoder functions. " + string.Join(" | ", failures);
                api = default!;
                error = CachedLoadError;
                return false;
            }

            CachedApi = new NativeBinkaApi(
                Marshal.GetDelegateForFunctionPointer<PrismProbeBinkaAudio>(probeExport),
                Marshal.GetDelegateForFunctionPointer<PrismDecodeBinkaToWav>(decodeExport));
            api = CachedApi;
            error = string.Empty;
            return true;
        }
    }

    private static IEnumerable<string> GetLibraryCandidates(string libraryName)
    {
        yield return libraryName;
        yield return "lib" + libraryName;
        yield return libraryName + ".so";
        yield return "lib" + libraryName + ".so";
    }

    private static bool TryLoadNativeLibrary(string libraryName, out nint handle, List<string> failures)
    {
        if (NativeLibrary.TryLoad(libraryName, out handle))
            return true;

        try
        {
            handle = NativeLibrary.Load(libraryName);
            return true;
        }
        catch (Exception ex)
        {
            handle = 0;
            failures.Add($"{libraryName}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryDlopen(string libraryName, out nint handle, List<string> failures)
    {
        try
        {
            handle = dlopen(libraryName, 2);
            if (handle != 0)
                return true;

            failures.Add($"{libraryName}: dlopen: {GetDlError()}");
            return false;
        }
        catch (Exception ex)
        {
            handle = 0;
            failures.Add($"{libraryName}: dlopen unavailable: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetNativeExport(nint handle, string name, out nint export, List<string> failures)
    {
        if (NativeLibrary.TryGetExport(handle, name, out export))
            return true;

        try
        {
            export = dlsym(handle, name);
            if (export != 0)
                return true;

            failures.Add($"{name}: dlsym: {GetDlError()}");
            return false;
        }
        catch (Exception ex)
        {
            export = 0;
            failures.Add($"{name}: dlsym unavailable: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string GetDlError()
    {
        var error = dlerror();
        return error == 0 ? "<no dlerror>" : Marshal.PtrToStringAnsi(error) ?? "<unreadable dlerror>";
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var unit = 0;
        var scaled = (double)value;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} {units[unit]}" : $"{scaled:0.##} {units[unit]}";
    }

    [DllImport("libdl.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dlopen(string filename, int flags);

    [DllImport("libdl.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dlsym(nint handle, string symbol);

    [DllImport("libdl.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dlerror();

    private sealed record NativeBinkaApi(
        PrismProbeBinkaAudio Probe,
        PrismDecodeBinkaToWav Decode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PrismProbeBinkaAudio(
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] input,
        int inputLength,
        out int sampleRate,
        out int channels,
        out int sampleCount,
        out int wavLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PrismDecodeBinkaToWav(
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] input,
        int inputLength,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] byte[] output,
        int outputLength,
        out int writtenLength);
}
