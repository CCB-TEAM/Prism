using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal static class Bc1Encoder
{
    public static byte[] Encode(Image<Rgba32> image)
    {
        int blocksWide = Math.Max(1, (image.Width + 3) / 4);
        int blocksHigh = Math.Max(1, (image.Height + 3) / 4);
        byte[] output = new byte[blocksWide * blocksHigh * 8];
        Span<Rgba32> block = stackalloc Rgba32[16];
        int offset = 0;

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                ReadBlock(image, bx * 4, by * 4, block);
                EncodeBlock(block, output.AsSpan(offset, 8));
                offset += 8;
            }
        }

        return output;
    }

    private static void ReadBlock(Image<Rgba32> image, int startX, int startY, Span<Rgba32> pixels)
    {
        int idx = 0;
        for (int y = 0; y < 4; y++)
        {
            int py = Math.Min(startY + y, image.Height - 1);
            for (int x = 0; x < 4; x++)
            {
                int px = Math.Min(startX + x, image.Width - 1);
                pixels[idx++] = image[px, py];
            }
        }
    }

    private static void EncodeBlock(ReadOnlySpan<Rgba32> pixels, Span<byte> output)
    {
        bool useTransparency = false;
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].A < 128)
            {
                useTransparency = true;
                break;
            }
        }
        (Rgba32 minColor, Rgba32 maxColor) = FindEndpoints(pixels);
        ushort c0 = ToRgb565(maxColor);
        ushort c1 = ToRgb565(minColor);

        if (useTransparency)
        {
            if (c0 > c1)
            {
                (c0, c1) = (c1, c0);
            }
        }
        else if (c0 < c1)
        {
            (c0, c1) = (c1, c0);
        }

        Span<Rgba32> palette = stackalloc Rgba32[4];
        BuildPalette(c0, c1, useTransparency, palette);

        uint indices = 0;
        for (int i = 0; i < 16; i++)
        {
            uint index = (uint)SelectIndex(pixels[i], palette, useTransparency);
            indices |= index << (i * 2);
        }

        BitConverter.TryWriteBytes(output[..2], c0);
        BitConverter.TryWriteBytes(output.Slice(2, 2), c1);
        BitConverter.TryWriteBytes(output.Slice(4, 4), indices);
    }

    private static (Rgba32 Min, Rgba32 Max) FindEndpoints(ReadOnlySpan<Rgba32> pixels)
    {
        Rgba32 min = pixels[0];
        Rgba32 max = pixels[0];
        int minLuma = Luma(min);
        int maxLuma = minLuma;

        for (int i = 1; i < pixels.Length; i++)
        {
            int luma = Luma(pixels[i]);
            if (luma < minLuma)
            {
                minLuma = luma;
                min = pixels[i];
            }

            if (luma > maxLuma)
            {
                maxLuma = luma;
                max = pixels[i];
            }
        }

        return (min, max);
    }

    private static int SelectIndex(Rgba32 pixel, ReadOnlySpan<Rgba32> palette, bool useTransparency)
    {
        if (useTransparency && pixel.A < 128)
        {
            return 3;
        }

        int bestIndex = 0;
        int bestDistance = int.MaxValue;
        int maxPalette = useTransparency ? 3 : 4;
        for (int i = 0; i < maxPalette; i++)
        {
            int distance = ColorDistance(pixel, palette[i]);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static void BuildPalette(ushort c0, ushort c1, bool useTransparency, Span<Rgba32> palette)
    {
        palette[0] = FromRgb565(c0);
        palette[1] = FromRgb565(c1);

        if (useTransparency)
        {
            palette[2] = Interpolate(palette[0], palette[1], 1, 1);
            palette[3] = new Rgba32(0, 0, 0, 0);
        }
        else
        {
            palette[2] = Interpolate(palette[0], palette[1], 2, 1);
            palette[3] = Interpolate(palette[0], palette[1], 1, 2);
        }
    }

    private static Rgba32 Interpolate(Rgba32 a, Rgba32 b, int aWeight, int bWeight)
    {
        int total = aWeight + bWeight;
        return new Rgba32(
            (byte)((a.R * aWeight + b.R * bWeight) / total),
            (byte)((a.G * aWeight + b.G * bWeight) / total),
            (byte)((a.B * aWeight + b.B * bWeight) / total),
            (byte)((a.A * aWeight + b.A * bWeight) / total));
    }

    private static ushort ToRgb565(Rgba32 color)
    {
        int r = color.R >> 3;
        int g = color.G >> 2;
        int b = color.B >> 3;
        return (ushort)((r << 11) | (g << 5) | b);
    }

    private static Rgba32 FromRgb565(ushort value)
    {
        byte r = (byte)(((value >> 11) & 0x1F) * 255 / 31);
        byte g = (byte)(((value >> 5) & 0x3F) * 255 / 63);
        byte b = (byte)((value & 0x1F) * 255 / 31);
        return new Rgba32(r, g, b, 255);
    }

    private static int ColorDistance(Rgba32 a, Rgba32 b)
    {
        int dr = a.R - b.R;
        int dg = a.G - b.G;
        int db = a.B - b.B;
        int da = a.A - b.A;
        return dr * dr + dg * dg + db * db + da * da;
    }

    private static int Luma(Rgba32 color)
    {
        return color.R * 299 + color.G * 587 + color.B * 114;
    }
}
