using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

internal static class TextureReplacementSource
{
    public static byte[][] LoadRawMipDirectory(string directoryPath, IReadOnlyList<TextureMip> mips, TextureFormatInfo format)
    {
        string fullPath = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(fullPath);
        }

        byte[][] result = new byte[mips.Count][];
        for (int i = 0; i < mips.Count; i++)
        {
            string path = Path.Combine(fullPath, $"mip{i}.bin");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Missing required mip file '{Path.GetFileName(path)}'.", path);
            }

            byte[] bytes = File.ReadAllBytes(path);
            int expected = format.GetMipByteSize(mips[i].Width, mips[i].Height);
            if (bytes.Length != expected)
            {
                throw new InvalidOperationException(
                    $"Mip {i} has {bytes.Length} bytes but {expected} bytes are required for {mips[i].Width}x{mips[i].Height}.");
            }

            result[i] = bytes;
        }

        return result;
    }

    public static async Task<byte[][]> LoadAndEncodeDxt1ImageAsync(
        string imagePath,
        int width,
        int height,
        IReadOnlyList<TextureMip> mips)
    {
        using Image<Rgba32> source = await LoadSourceImageAsync(imagePath, width, height);

        byte[][] result = new byte[mips.Count][];
        for (int i = 0; i < mips.Count; i++)
        {
            TextureMip mip = mips[i];
            using Image<Rgba32> mipImage = CreateMipImage(source, mip);
            result[i] = Bc1Encoder.Encode(mipImage);
        }

        return result;
    }

    public static async Task<Image<Rgba32>> LoadSourceImageAsync(string imagePath, int width, int height)
    {
        Image<Rgba32> source = await Image.LoadAsync<Rgba32>(Path.GetFullPath(imagePath));
        if (source.Width != width || source.Height != height)
        {
            source.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Lanczos3,
            }));
        }

        return source;
    }

    public static async Task SaveMipImageAsync(Image<Rgba32> source, TextureMip mip, string outputPath)
    {
        using Image<Rgba32> mipImage = CreateMipImage(source, mip);
        await mipImage.SaveAsPngAsync(outputPath);
    }

    private static Image<Rgba32> CreateMipImage(Image<Rgba32> source, TextureMip mip)
    {
        return source.Width == mip.Width && source.Height == mip.Height
            ? source.Clone()
            : source.Clone(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(mip.Width, mip.Height),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Lanczos3,
            }));
    }
}
