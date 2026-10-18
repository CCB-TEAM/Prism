using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace UAssetTexture.Core;

/// <summary>
/// 未压缩 8 位颜色（PF_B8G8R8A8 / PF_R8G8B8A8 / PF_A8R8G8B8）的 mip 编码：
/// 把 RGBA 像素按目标资产的字节序打包成原始载荷。
///
/// 这类贴图（KARDS 的 UI 插图就是）不走 BC/ASTC 压缩，替换时既不需要
/// prism_codecs 原生库，也不需要 astcenc / texconv —— 所以进程外的
/// UAssetCLI 和进程内的 TextureReplacementService 共用这一份实现。
/// </summary>
public static class TexturePixelEncoder
{
    public static byte[] EncodeUncompressed(Image<Rgba32> image, TextureFormatInfo format)
    {
        if (!format.IsUncompressed8BitColor)
            throw new InvalidOperationException($"{format.Name} is not an uncompressed 8-bit color format.");

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
            else
            {
                output[i] = rgba[i + 3];
                output[i + 1] = rgba[i];
                output[i + 2] = rgba[i + 1];
                output[i + 3] = rgba[i + 2];
            }
        }

        return output;
    }
}
