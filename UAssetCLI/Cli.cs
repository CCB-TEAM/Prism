using SixLabors.ImageSharp;
using UAssetAPI.UnrealTypes;

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        try
        {
            string command = args[0].ToLowerInvariant();
            Dictionary<string, string?> options = ParseOptions(args.Skip(1).ToArray());

            return command switch
            {
                "inspect-texture" => InspectTexture(options),
                "extract-mips" => ExtractMips(options),
                "replace-texture" => await ReplaceTextureAsync(options),
                _ => Fail($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int InspectTexture(Dictionary<string, string?> options)
    {
        string assetPath = Require(options, "asset");
        TextureAssetInfo info = TextureAssetParser.Load(assetPath, ParseEngineVersion(options), GetUsmapPath(options));

        Console.WriteLine($"Asset: {info.AssetPath}");
        Console.WriteLine($"Format: {info.Format.Name}");
        Console.WriteLine($"Size: {info.Width}x{info.Height}");
        Console.WriteLine($"Export bytes: {info.ExportData.Length}");
        Console.WriteLine($"UEXP footer bytes: {info.UexpFooter.Length}");
        Console.WriteLine($"UBULK bytes: {info.UbulkData.Length}");
        Console.WriteLine($"Has UBULK file: {File.Exists(info.UbulkPath)}");
        Console.WriteLine($"External mips: {info.ExternalMipCount}");
        Console.WriteLine($"Inline mips: {info.InlineMips.Count}");
        Console.WriteLine("Mip layout:");

        foreach (TextureMipPlacement mip in info.MipPlacements)
        {
            Console.WriteLine(
                $"  mip {mip.Index}: {mip.Width}x{mip.Height}, {mip.ByteLength} bytes, {mip.Storage}, offset {mip.Offset}");
        }

        if (info.InlineSentinelOffset >= 0)
        {
            Console.WriteLine($"Inline sentinel offset: {info.InlineSentinelOffset}");
        }

        return 0;
    }

    private static async Task<int> ReplaceTextureAsync(Dictionary<string, string?> options)
    {
        string assetPath = Require(options, "asset");
        TextureAssetInfo info = TextureAssetParser.Load(assetPath, ParseEngineVersion(options), GetUsmapPath(options));
        if (options.TryGetValue("expected-format", out string? expectedFormat)
            && !string.IsNullOrWhiteSpace(expectedFormat)
            && !string.Equals(info.Format.Name, expectedFormat, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Asset format is {info.Format.Name}, but {expectedFormat} was selected. Select the matching format or provide a matching asset.");
        }

        string outputAssetPath = options.TryGetValue("output", out string? output) && !string.IsNullOrWhiteSpace(output)
            ? Path.GetFullPath(output)
            : Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(assetPath))!,
                Path.GetFileNameWithoutExtension(assetPath) + ".patched.uasset");

        byte[][] mipPayloads;
        string source = Require(options, "source");
        string? sourceKind = options.TryGetValue("source-kind", out string? kind) ? kind : null;

        if (Directory.Exists(source) || string.Equals(sourceKind, "raw-dir", StringComparison.OrdinalIgnoreCase))
        {
            mipPayloads = TextureReplacementSource.LoadRawMipDirectory(source, info.Mips, info.Format);
        }
        else if (info.Format.IsDxt1)
        {
            mipPayloads = await TextureReplacementSource.LoadAndEncodeDxt1ImageAsync(source, info.Width, info.Height, info.Mips);
        }
        else if (info.Format.IsAstc || info.Format.RequiresTexconv)
        {
            ExternalEncoderOptions encoderOptions = new(
                GetOptionalPath(options, "astcenc"),
                GetOptionalPath(options, "texconv"),
                options.TryGetValue("astc-quality", out string? quality) && !string.IsNullOrWhiteSpace(quality) ? quality : "medium");
            mipPayloads = await ExternalTextureEncoder.EncodeImageAsync(source, info.Format, info.Width, info.Height, info.Mips, encoderOptions);
        }
        else
        {
            throw new InvalidOperationException(
                $"Format {info.Format.Name} currently requires pre-compressed mip input. " +
                "Pass --source-kind raw-dir and provide mip0.bin, mip1.bin, ...");
        }

        TextureReplacer.WriteReplacement(info, mipPayloads, outputAssetPath);

        Console.WriteLine($"Wrote: {outputAssetPath}");
        Console.WriteLine($"Wrote: {Path.ChangeExtension(outputAssetPath, ".uexp")}");
        string outputUbulkPath = Path.ChangeExtension(outputAssetPath, ".ubulk");
        if (File.Exists(outputUbulkPath))
        {
            Console.WriteLine($"Wrote: {outputUbulkPath}");
        }
        return 0;
    }

    private static int ExtractMips(Dictionary<string, string?> options)
    {
        string assetPath = Require(options, "asset");
        string outputDirectory = Require(options, "output-dir");
        TextureAssetInfo info = TextureAssetParser.Load(assetPath, ParseEngineVersion(options), GetUsmapPath(options));
        TextureReplacer.ExtractMipPayloads(info, outputDirectory);

        Console.WriteLine($"Wrote mip payloads to: {outputDirectory}");
        return 0;
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            string current = args[i];
            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected positional argument '{current}'.");
            }

            string key = current[2..];
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            options[key] = value ?? "true";
        }

        return options;
    }

    private static EngineVersion ParseEngineVersion(Dictionary<string, string?> options)
    {
        if (!options.TryGetValue("engine", out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return EngineVersion.VER_UE5_6;
        }

        return Enum.Parse<EngineVersion>(value, ignoreCase: true);
    }

    private static string? GetUsmapPath(Dictionary<string, string?> options)
    {
        return GetOptionalPath(options, "usmap");
    }

    private static string? GetOptionalPath(Dictionary<string, string?> options, string key)
    {
        return options.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value)
            : null;
    }

    private static string Require(Dictionary<string, string?> options, string key)
    {
        if (options.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
        {
            return Path.GetFullPath(value);
        }

        throw new ArgumentException($"Missing required option --{key}.");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("UAssetCLI texture tools");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  inspect-texture --asset <file.uasset> [--usmap <file.usmap>] [--engine VER_UE5_6]");
        Console.WriteLine("  extract-mips --asset <file.uasset> --output-dir <folder> [--usmap <file.usmap>] [--engine VER_UE5_6]");
        Console.WriteLine("  replace-texture --asset <file.uasset> --source <image-or-mip-dir> [--source-kind raw-dir]");
        Console.WriteLine("                 [--output <target.uasset>] [--usmap <file.usmap>] [--engine VER_UE5_6]");
        Console.WriteLine("                 [--expected-format PF_BC7]");
        Console.WriteLine("                 [--astcenc <astcenc.exe>] [--astc-quality medium] [--texconv <texconv.exe>]");
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine("  PF_DXT1 image input is encoded internally.");
        Console.WriteLine("  Image input is resized to the asset texture size when dimensions differ.");
        Console.WriteLine("  PF_ASTC_* image input uses astcenc; PF_DXT5 and PF_BC7 image input use texconv.");
        Console.WriteLine("  Raw mip directories must contain mip0.bin, mip1.bin, ... with already compressed payload bytes.");
    }
}
