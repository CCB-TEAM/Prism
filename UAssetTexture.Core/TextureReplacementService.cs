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
}
