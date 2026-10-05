using UAssetAPI.UnrealTypes;

namespace UAssetTexture.Core;

public sealed class TextureReplacementService
{
    public Task<TextureInspectionResult> InspectAsync(
        string assetPath,
        EngineVersion engineVersion,
        string? usmapPath,
        CancellationToken cancellationToken = default,
        string? formatHint = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = TextureAssetParser.Load(assetPath, engineVersion, usmapPath, formatHint);
        return Task.FromResult(ToInspection(info));
    }

    public async Task<TextureReplacementResult> ReplaceAsync(
        string assetPath,
        string sourceImagePath,
        string outputAssetPath,
        EngineVersion engineVersion,
        string? usmapPath,
        TextureCodecOptions? codecOptions = null,
        CancellationToken cancellationToken = default,
        string? formatHint = null)
    {
        codecOptions ??= new TextureCodecOptions();
        var info = TextureAssetParser.Load(assetPath, engineVersion, usmapPath, formatHint);
        var mipPayloads = await TextureReplacementSource.LoadAndEncodeImageAsync(
            sourceImagePath,
            info.Format,
            info.Width,
            info.Height,
            info.Mips,
            codecOptions,
            cancellationToken).ConfigureAwait(false);

        TextureReplacer.WriteReplacement(info, mipPayloads, outputAssetPath);
        var ubulkPath = Path.ChangeExtension(outputAssetPath, ".ubulk");
        return new TextureReplacementResult(
            Path.GetFullPath(outputAssetPath),
            Path.ChangeExtension(Path.GetFullPath(outputAssetPath), ".uexp"),
            File.Exists(ubulkPath) ? ubulkPath : null,
            ToInspection(info));
    }

    /// <summary>
    /// 快速路径：源纹理与目标纹理的格式/mip 布局一致时，直接把源纹理的
    /// <b>已压缩 mip 载荷</b>搬进目标资产模板。
    ///
    /// 相比"解码成 PNG 再重编码"的好处：
    /// - <b>无损</b>：压缩数据原样搬运，不会经过两次有损转换；
    /// - <b>无需映射文件</b>：只读原始字节，不做 UObject 反序列化；
    /// - <b>快很多</b>：跳过解码与 ASTC/BC 编码。
    ///
    /// 返回 null 表示布局不兼容，调用方应回退到解码/重编码路径。
    /// </summary>
    public TextureReplacementResult? TryReplaceWithRawMips(
        string sourceAssetPath,
        string targetAssetPath,
        string outputAssetPath,
        EngineVersion engineVersion,
        string? usmapPath,
        string? sourceFormatHint = null,
        string? targetFormatHint = null)
    {
        var source = TextureAssetParser.Load(sourceAssetPath, engineVersion, usmapPath, sourceFormatHint);
        var target = TextureAssetParser.Load(targetAssetPath, engineVersion, usmapPath, targetFormatHint);

        if (!TextureReplacer.IsLayoutCompatible(source, target))
        {
            return null;
        }

        IReadOnlyList<(int Index, byte[] Payload)> payloads = TextureReplacer.ExtractMipPayloads(source);

        // WriteReplacement 要求载荷按 info.Mips 的顺序（而非 mip Index 顺序）排列。
        var byIndex = payloads.ToDictionary(p => p.Index, p => p.Payload);
        var ordered = new byte[target.Mips.Count][];
        for (int i = 0; i < target.Mips.Count; i++)
        {
            if (!byIndex.TryGetValue(target.Mips[i].Index, out byte[]? payload))
            {
                return null; // 缺少某个 mip，交给慢路径处理
            }

            ordered[i] = payload;
        }

        TextureReplacer.WriteReplacement(target, ordered, outputAssetPath);
        var ubulkPath = Path.ChangeExtension(outputAssetPath, ".ubulk");
        return new TextureReplacementResult(
            Path.GetFullPath(outputAssetPath),
            Path.ChangeExtension(Path.GetFullPath(outputAssetPath), ".uexp"),
            File.Exists(ubulkPath) ? ubulkPath : null,
            ToInspection(target));
    }

    private static TextureInspectionResult ToInspection(TextureAssetInfo info)
    {
        return new TextureInspectionResult(
            info.AssetPath,
            info.Format.Name,
            info.Width,
            info.Height,
            info.Mips.Count,
            info.ExternalMipCount,
            info.InlineMips.Count,
            File.Exists(info.UbulkPath),
            info.MipPlacements);
    }

    /// <summary>
    /// 与 <see cref="ReplaceAsync"/> 相同，但替换图来自内存字节而非磁盘文件。
    ///
    /// 供 Pak 转换使用：源纹理的像素是刚解包出来的 PNG 字节，
    /// 走字节入口可以省掉一次落盘 + 读回。
    /// </summary>
    public async Task<TextureReplacementResult> ReplaceFromImageBytesAsync(
        string assetPath,
        byte[] imageData,
        string outputAssetPath,
        EngineVersion engineVersion,
        string? usmapPath,
        TextureCodecOptions? codecOptions = null,
        CancellationToken cancellationToken = default,
        string? formatHint = null)
    {
        codecOptions ??= new TextureCodecOptions();
        var info = TextureAssetParser.Load(assetPath, engineVersion, usmapPath, formatHint);
        var mipPayloads = await TextureReplacementSource.LoadAndEncodeImageAsync(
            imageData,
            info.Format,
            info.Width,
            info.Height,
            info.Mips,
            codecOptions,
            cancellationToken).ConfigureAwait(false);

        TextureReplacer.WriteReplacement(info, mipPayloads, outputAssetPath);
        var ubulkPath = Path.ChangeExtension(outputAssetPath, ".ubulk");
        return new TextureReplacementResult(
            Path.GetFullPath(outputAssetPath),
            Path.ChangeExtension(Path.GetFullPath(outputAssetPath), ".uexp"),
            File.Exists(ubulkPath) ? ubulkPath : null,
            ToInspection(info));
    }
}
