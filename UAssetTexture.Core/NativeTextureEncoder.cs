using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace UAssetTexture.Core;

internal static class NativeTextureEncoder
{
    private const int NativeFormatAstc = 1;
    private const int NativeFormatBc3 = 2;
    private const int NativeFormatBc7 = 3;
    private static readonly object EncoderLock = new();
    private static readonly Dictionary<string, PrismEncodeTexture> Encoders = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoadedDependencies = new(StringComparer.Ordinal);

    public static byte[] Encode(Image<Rgba32> image, TextureFormatInfo format, TextureCodecOptions options)
    {
        var output = new byte[format.GetMipByteSize(image.Width, image.Height)];
        var rgba = new byte[checked(image.Width * image.Height * 4)];
        image.CopyPixelDataTo(rgba);

        var formatId = GetNativeFormatId(format);
        var quality = NormalizeAstcQuality(options.AstcQuality);
        var encoder = LoadEncoder(options.NativeLibraryName, options.Log);
        var status = encoder(
            rgba,
            image.Width,
            image.Height,
            formatId,
            format.BlockWidth,
            format.BlockHeight,
            quality,
            output,
            output.Length);

        if (status != 0)
            throw new InvalidOperationException($"Native texture encoder failed for {format.Name} with status {status}.");

        return output;
    }

    private static int GetNativeFormatId(TextureFormatInfo format)
    {
        if (format.IsAstc)
            return NativeFormatAstc;
        if (format.IsDxt5)
            return NativeFormatBc3;
        if (format.IsBc7)
            return NativeFormatBc7;

        throw new InvalidOperationException($"{format.Name} does not use the native texture encoder.");
    }

    private static int NormalizeAstcQuality(string quality)
    {
        return quality.Trim().ToLowerInvariant() switch
        {
            "fastest" => 0,
            "fast" => 1,
            "" or "medium" => 2,
            "thorough" => 3,
            "exhaustive" => 4,
            _ => throw new InvalidOperationException("ASTC quality must be one of: fastest, fast, medium, thorough, exhaustive.")
        };
    }

    private static PrismEncodeTexture LoadEncoder(string libraryName, Action<string>? log)
    {
        var key = string.IsNullOrWhiteSpace(libraryName) ? "prism_codecs" : libraryName;
        lock (EncoderLock)
        {
            if (Encoders.TryGetValue(key, out var cached))
            {
                log?.Invoke("Native texture encoder cache hit: " + key);
                return cached;
            }

            var attempted = new List<string>();
            var failures = new List<string>();
            PreloadKnownDependencies(key, failures);
            nint handle = 0;
            foreach (var candidate in GetLibraryCandidates(key))
            {
                attempted.Add(candidate);
                if (TryLoadNativeLibrary(candidate, out handle, failures))
                {
                    log?.Invoke("Native texture encoder loaded by NativeLibrary: " + candidate);
                    break;
                }

                if (TryDlopen(candidate, out handle, failures))
                {
                    log?.Invoke("Native texture encoder loaded by dlopen: " + candidate);
                    break;
                }
            }

            if (handle == 0)
            {
                throw new DllNotFoundException(
                    "Could not load Prism native texture encoder. Tried: " + string.Join(", ", attempted) +
                    (failures.Count == 0 ? string.Empty : ". Loader errors: " + string.Join(" | ", failures)));
            }

            if (!TryGetNativeExport(handle, "prism_encode_texture", out var export, failures))
                throw new EntryPointNotFoundException("Native texture encoder did not export prism_encode_texture.");

            log?.Invoke("Native texture encoder export resolved: prism_encode_texture");
            var encoder = Marshal.GetDelegateForFunctionPointer<PrismEncodeTexture>(export);
            Encoders[key] = encoder;
            return encoder;
        }
    }

    private static void PreloadKnownDependencies(string libraryName, List<string> failures)
    {
        if (!Path.IsPathFullyQualified(libraryName))
            return;

        var directory = Path.GetDirectoryName(libraryName);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        TryPreloadDependency(Path.Combine(directory, "libc++_shared.so"), failures);
    }

    private static void TryPreloadDependency(string path, List<string> failures)
    {
        if (!File.Exists(path) || LoadedDependencies.Contains(path))
            return;

        if (TryLoadNativeLibrary(path, out _, failures) ||
            TryDlopen(path, out _, failures))
            LoadedDependencies.Add(path);
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

    [DllImport("libdl.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dlopen(string filename, int flags);

    [DllImport("libdl.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dlsym(nint handle, string symbol);

    [DllImport("libdl.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dlerror();

    private static IEnumerable<string> GetLibraryCandidates(string libraryName)
    {
        yield return libraryName;

        if (Path.IsPathFullyQualified(libraryName))
            yield break;

        if (!libraryName.StartsWith("lib", StringComparison.Ordinal))
            yield return "lib" + libraryName;

        if (!libraryName.EndsWith(".so", StringComparison.Ordinal))
        {
            yield return libraryName + ".so";
            if (!libraryName.StartsWith("lib", StringComparison.Ordinal))
                yield return "lib" + libraryName + ".so";
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PrismEncodeTexture(
        byte[] rgba,
        int width,
        int height,
        int format,
        int blockWidth,
        int blockHeight,
        int quality,
        byte[] output,
        int outputLength);
}
