namespace UAssetTexture.Core;

public sealed record MaterialInstanceParameterSet(
    string AssetPath,
    string ExportName,
    string ExportClass,
    IReadOnlyList<MaterialScalarParameter> Scalars,
    IReadOnlyList<MaterialVectorParameter> Vectors,
    IReadOnlyList<MaterialTextureParameter> Textures,
    IReadOnlyList<MaterialTextureOption> TextureOptions);

public sealed record MaterialScalarParameter(
    int Index,
    string Name,
    float Value);

public sealed record MaterialVectorParameter(
    int Index,
    string Name,
    float R,
    float G,
    float B,
    float A);

public sealed record MaterialTextureParameter(
    int Index,
    string Name,
    int RawIndex,
    string TextureName,
    string TexturePath);

public sealed record MaterialTextureOption(
    int RawIndex,
    string Name,
    string Path);

public sealed record MaterialInstanceParameterUpdate(
    string Kind,
    int Index,
    float? Value = null,
    float? R = null,
    float? G = null,
    float? B = null,
    float? A = null,
    int? RawIndex = null);

public sealed record MaterialInstanceParameterPatchResult(
    string AssetPath,
    string? UexpPath,
    MaterialInstanceParameterSet Parameters);
