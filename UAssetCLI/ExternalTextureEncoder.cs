using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal sealed record ExternalEncoderOptions(string? AstcencPath, string? TexconvPath, string AstcQuality);

internal static class ExternalTextureEncoder
{
    public static async Task<byte[][]> EncodeImageAsync(
        string imagePath,
        TextureFormatInfo format,
        int width,
        int height,
        IReadOnlyList<TextureMip> mips,
        ExternalEncoderOptions options)
    {
        using Image<Rgba32> source = await TextureReplacementSource.LoadSourceImageAsync(imagePath, width, height);

        byte[][] result = new byte[mips.Count][];
        string tempDirectory = Path.Combine(Path.GetTempPath(), "UAssetCLI-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            for (int i = 0; i < mips.Count; i++)
            {
                TextureMip mip = mips[i];
                string mipPngPath = Path.Combine(tempDirectory, $"mip{i}.png");
                await TextureReplacementSource.SaveMipImageAsync(source, mip, mipPngPath);

                result[i] = format.IsAstc
                    ? await EncodeAstcMipAsync(mipPngPath, tempDirectory, i, format, options)
                    : await EncodeTexconvMipAsync(mipPngPath, tempDirectory, i, format, options);

                int expected = format.GetMipByteSize(mip.Width, mip.Height);
                if (result[i].Length != expected)
                {
                    throw new InvalidOperationException(
                        $"Encoder produced {result[i].Length} bytes for mip {i}, but {expected} bytes are required.");
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort cleanup only; encoding output has already been validated.
            }
        }

        return result;
    }

    private static async Task<byte[]> EncodeAstcMipAsync(
        string mipPngPath,
        string tempDirectory,
        int mipIndex,
        TextureFormatInfo format,
        ExternalEncoderOptions options)
    {
        string encoder = ResolveTool(options.AstcencPath, "astcenc-avx2", "astcenc-sse4.1", "astcenc");
        string astcPath = Path.Combine(tempDirectory, $"mip{mipIndex}.astc");

        await RunAsync(encoder, ["-cl", mipPngPath, astcPath, format.AstcBlockSize, NormalizeAstcQuality(options.AstcQuality)]);

        byte[] file = await File.ReadAllBytesAsync(astcPath);
        if (file.Length < 16 || file[0] != 0x13 || file[1] != 0xAB || file[2] != 0xA1 || file[3] != 0x5C)
        {
            throw new InvalidOperationException("astcenc output was not a valid .astc file.");
        }

        return file[16..];
    }

    private static async Task<byte[]> EncodeTexconvMipAsync(
        string mipPngPath,
        string tempDirectory,
        int mipIndex,
        TextureFormatInfo format,
        ExternalEncoderOptions options)
    {
        string encoder = ResolveTool(options.TexconvPath, "texconv");
        string outputDirectory = Path.Combine(tempDirectory, $"texconv{mipIndex}");
        Directory.CreateDirectory(outputDirectory);
        string ddsPath = Path.Combine(outputDirectory, $"mip{mipIndex}.dds");

        await RunAsync(encoder, ["-nologo", "-y", "-m", "1", "-f", GetTexconvFormat(format), "-o", outputDirectory, mipPngPath]);

        if (!File.Exists(ddsPath))
        {
            throw new InvalidOperationException("texconv did not produce the expected DDS output.");
        }

        return ExtractDdsPayload(await File.ReadAllBytesAsync(ddsPath));
    }

    private static string GetTexconvFormat(TextureFormatInfo format)
    {
        return format.Name.ToUpperInvariant() switch
        {
            "PF_DXT5" => "BC3_UNORM",
            "PF_BC7" => "BC7_UNORM",
            _ => throw new InvalidOperationException($"Format {format.Name} is not supported by texconv integration.")
        };
    }

    private static byte[] ExtractDdsPayload(byte[] dds)
    {
        if (dds.Length < 128 || dds[0] != (byte)'D' || dds[1] != (byte)'D' || dds[2] != (byte)'S' || dds[3] != (byte)' ')
        {
            throw new InvalidOperationException("texconv output was not a valid DDS file.");
        }

        int dataOffset = 128;
        bool hasDx10Header = dds[84] == (byte)'D' && dds[85] == (byte)'X' && dds[86] == (byte)'1' && dds[87] == (byte)'0';
        if (hasDx10Header)
        {
            dataOffset += 20;
        }

        if (dds.Length <= dataOffset)
        {
            throw new InvalidOperationException("DDS file did not contain texture payload data.");
        }

        return dds[dataOffset..];
    }

    private static string ResolveTool(string? configuredPath, params string[] candidates)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string fullPath = Path.GetFullPath(configuredPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Configured encoder was not found.", fullPath);
            }

            return fullPath;
        }

        foreach (string candidate in candidates)
        {
            if (IsToolAvailable(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not find encoder tool. Install one of: {string.Join(", ", candidates)}, or pass the explicit path with the matching CLI option.");
    }

    private static bool IsToolAvailable(string fileName)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim('"'), fileName);
            if (File.Exists(candidate) || File.Exists(candidate + ".exe"))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeAstcQuality(string quality)
    {
        string normalized = quality.StartsWith("-", StringComparison.Ordinal) ? quality : "-" + quality;
        string[] allowed = ["-fastest", "-fast", "-medium", "-thorough", "-exhaustive"];
        if (!allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ASTC quality must be one of: fastest, fast, medium, thorough, exhaustive.");
        }

        return normalized.ToLowerInvariant();
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start encoder '{executable}'.");

        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Encoder '{executable}' failed with exit code {process.ExitCode}.{Environment.NewLine}{stdout}{stderr}");
        }
    }
}
