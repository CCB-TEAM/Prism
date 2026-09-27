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
        return EncodeMips(source, format, mips, options, cancellationToken);
    }

    /// <summary>
    /// 从内存中的图片字节（PNG/JPG/...）编码各 mip。
    /// 供 Pak 转换使用：像素来自解包结果，不需要落盘再读回。
    /// </summary>
    public static async Task<byte[][]> LoadAndEncodeImageAsync(
        byte[] imageData,
        TextureFormatInfo format,
        int width,
        int height,
        IReadOnlyList<TextureMip> mips,
        TextureCodecOptions options,
        CancellationToken cancellationToken)
    {
        using var source = await LoadSourceImageAsync(imageData, width, height, cancellationToken).ConfigureAwait(false);
        return EncodeMips(source, format, mips, options, cancellationToken);
    }

    private static byte[][] EncodeMips(
        Image<Rgba32> source,
        TextureFormatInfo format,
        IReadOnlyList<TextureMip> mips,
        TextureCodecOptions options,
        CancellationToken cancellationToken)
    {
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

    private static readonly int MaxAllowedDimension = TextureAssetParser.MaxTextureDimension;
    private static readonly long MaxAllowedPixels = TextureAssetParser.MaxTexturePixels;

    private static void ValidateTextureSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"Invalid texture size {width}x{height}: dimensions must be positive.");
        if (width > MaxAllowedDimension || height > MaxAllowedDimension)
            throw new InvalidOperationException($"Texture size {width}x{height} exceeds the maximum allowed dimension of {MaxAllowedDimension}.");
        if ((long)width * height > MaxAllowedPixels)
            throw new InvalidOperationException($"Texture size {width}x{height} has {width * (long)height} pixels, exceeding the maximum of {MaxAllowedPixels} pixels.");
    }

    private static async Task<Image<Rgba32>> LoadSourceImageAsync(string imagePath, int width, int height, CancellationToken cancellationToken)
    {
        ValidateTextureSize(width, height);

        var source = await Image.LoadAsync<Rgba32>(Path.GetFullPath(imagePath), cancellationToken).ConfigureAwait(false);
        ResizeToTarget(source, width, height);
        return source;
    }

    private static async Task<Image<Rgba32>> LoadSourceImageAsync(byte[] imageData, int width, int height, CancellationToken cancellationToken)
    {
        ValidateTextureSize(width, height);

        using var stream = new MemoryStream(imageData, writable: false);
        var source = await Image.LoadAsync<Rgba32>(stream, cancellationToken).ConfigureAwait(false);
        ResizeToTarget(source, width, height);
        return source;
    }

    private static void ResizeToTarget(Image<Rgba32> source, int width, int height)
    {
        if (source.Width == width && source.Height == height)
            return;

        source.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3,
        }));
    }

    private static Image<Rgba32> CreateMipImage(Image<Rgba32> source, TextureMip mip)
    {
        ValidateTextureSize(mip.Width, mip.Height);

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
