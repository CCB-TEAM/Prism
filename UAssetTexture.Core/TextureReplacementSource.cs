using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace UAssetTexture.Core;

internal static class TextureReplacementSource
{
    public static async Task<byte[][]> LoadAndEncodeImageAsync(
        string imagePath,
        TextureFormatInfo format,
        int width,
        int height,
        IReadOnlyList<TextureMip> mips,
        TextureCodecOptions options,
        CancellationToken cancellationToken)
    {
        using var source = await LoadSourceImageAsync(imagePath, width, height, cancellationToken).ConfigureAwait(false);

        var result = new byte[mips.Count][];
        for (var i = 0; i < mips.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mip = mips[i];
            using var mipImage = CreateMipImage(source, mip);
            options.Log?.Invoke($"Encoding mip {i}: {mip.Width}x{mip.Height}, format={format.Name}");
            result[i] = format.IsUncompressed8BitColor
                ? EncodeUncompressedColor(mipImage, format)
                : format.IsDxt1
                    ? Bc1Encoder.Encode(mipImage)
                    : NativeTextureEncoder.Encode(mipImage, format, options);

            var expected = format.GetMipByteSize(mip.Width, mip.Height);
            if (result[i].Length != expected)
                throw new InvalidOperationException($"Encoder produced {result[i].Length} bytes for mip {i}, but {expected} bytes are required.");

            options.Log?.Invoke($"Encoded mip {i}: {result[i].Length} bytes");
        }

        return result;
    }

    private static async Task<Image<Rgba32>> LoadSourceImageAsync(string imagePath, int width, int height, CancellationToken cancellationToken)
    {
        var source = await Image.LoadAsync<Rgba32>(Path.GetFullPath(imagePath), cancellationToken).ConfigureAwait(false);
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

    private static byte[] EncodeUncompressedColor(Image<Rgba32> image, TextureFormatInfo format)
    {
        var rgba = new byte[checked(image.Width * image.Height * 4)];
        image.CopyPixelDataTo(rgba);

        if (format.IsRgba8)
            return rgba;

        var output = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (format.IsBgra8)
            {
                output[i] = rgba[i + 2];
                output[i + 1] = rgba[i + 1];
                output[i + 2] = rgba[i];
                output[i + 3] = rgba[i + 3];
            }
            else if (format.IsArgb8)
            {
                output[i] = rgba[i + 3];
                output[i + 1] = rgba[i];
                output[i + 2] = rgba[i + 1];
                output[i + 3] = rgba[i + 2];
            }
            else
            {
                throw new InvalidOperationException($"{format.Name} is not a supported uncompressed color format.");
            }
        }

        return output;
    }
}
