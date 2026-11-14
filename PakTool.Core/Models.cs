namespace PakTool.Core;

public sealed record PakOpenOptions(
    IReadOnlyList<string> PakPaths,
    string? AesKeyHex = null,
    // 映射文件路径：支持 .usmap 与 .jmap（含 .gz，按扩展名自动识别解析器）。
    string? UsmapPath = null,
    string Game = "GAME_UE5_6",
    bool CaseInsensitivePaths = true,
    Action<string>? DecodeLogger = null);

public sealed record PakOpenResult(
    int MountedArchiveCount,
    int FileCount,
    int RequiredKeyCount,
    IReadOnlyList<string> MountedArchives,
    IReadOnlyList<OperationTimingDto> Timings,
    // 非空表示这个包是 UE5.5 之前 cook 的老排布，已自动换成那个年代的版本号来读。
    string? CookedSummaryNote = null);

public sealed record OperationTimingDto(
    string Name,
    long Milliseconds);

public sealed record DirectoryIndexResult(
    int FolderCount,
    int EntryCount);

public sealed record ArchiveEntryDto(
    string FullPath,
    string Name,
    bool IsDirectory,
    long Size,
    string Extension,
    bool IsEncrypted,
    string CompressionMethod,
    bool IsAssetPackage = false,
    IReadOnlyList<string>? RelatedPaths = null);

public sealed record AssetPropertyDto(
    string Name,
    string Type,
    string? ValuePreview);

public sealed record AssetExportDto(
    string Name,
    string Type,
    int PropertyCount,
    IReadOnlyList<AssetPropertyDto> Properties);

public sealed record AssetInfoDto(
    string Path,
    int NameCount,
    int ExportCount,
    IReadOnlyList<AssetExportDto> Exports);

public sealed record TexturePreviewDto(
    string SourcePath,
    string TextureName,
    int Width,
    int Height,
    byte[] PngData,
    string? PixelFormat = null);

/// <summary>
/// 资产类型探测结果：只回答"这是什么"，<b>不解像素、不建预览</b>。
///
/// Pak 转换需要按类型分流（纹理走像素转换、音频/字体/蓝图原样并入），
/// 没有这个探测就只能一律按"纹理候选"处理，报告里会出现大量
/// "无法解出源纹理像素"，让人误以为文件被丢弃了。
/// </summary>
public sealed record AssetProbeDto(
    /// <summary>主导出的类名，例如 <c>Texture2D</c>、<c>SoundWave</c>、<c>FontFace</c>。</summary>
    string? ExportType,
    bool IsTexture,
    /// <summary>纹理的像素格式（仅 <see cref="IsTexture"/> 为 true 时有值），例如 <c>PF_ASTC_6x6</c>。</summary>
    string? PixelFormat = null,
    /// <summary>资产使用未版本化属性但缺映射文件 —— 类型无法判定。</summary>
    bool NeedsMappings = false,
    /// <summary>探测失败时的原始原因（找不到文件 / 包解析失败）。</summary>
    string? Message = null)
{
    /// <summary>确认为非纹理（拿到了类名且不是 UTexture）。</summary>
    public bool IsKnownNonTexture => !IsTexture && ExportType is not null;
}

public sealed record AssetPreviewDto(
    string Kind,
    string Title,
    IReadOnlyList<AssetPreviewDetailDto> Details,
    string? MimeType = null,
    byte[]? Data = null,
    string? Text = null,
    ModelPreviewDto? Model = null,
    LocresPreviewDto? Locres = null,
    bool CanPlay = false,
    bool CanExportRaw = true,
    WidgetPreviewDto? Widget = null,
    Blueprints.BlueprintGraphDto? Blueprint = null);

public sealed record AssetPreviewDetailDto(
    string Label,
    string Value);

public sealed record ModelPreviewDto(
    string Name,
    string MeshType,
    int VertexCount,
    int TriangleCount,
    float[] Positions,
    float[] Normals,
    float[] Uvs,
    uint[] Indices,
    ModelBoundsDto Bounds,
    IReadOnlyList<ModelSectionDto> Sections,
    IReadOnlyList<float[]>? UvSets = null,
    float[]? TextureLayers = null,
    IReadOnlyList<ModelMaterialDto>? Materials = null);

public sealed record ModelMaterialDto(
    int MaterialIndex,
    string Name,
    int DiffuseUvSet = 0,
    string? DiffuseTextureName = null,
    string? DiffuseTextureMime = null,
    byte[]? DiffuseTextureData = null,
    IReadOnlyList<ModelTextureDto>? DiffuseTextures = null,
    IReadOnlyList<ModelTextureDto>? NormalTextures = null,
    IReadOnlyList<ModelTextureDto>? PbrTextures = null);

public sealed record ModelTextureDto(
    int Layer,
    string Name,
    string MimeType,
    byte[] Data);

public sealed record ModelBoundsDto(
    float MinX,
    float MinY,
    float MinZ,
    float MaxX,
    float MaxY,
    float MaxZ);

public sealed record ModelSectionDto(
    string Name,
    int MaterialIndex,
    int FirstIndex,
    int IndexCount);

public sealed record LocresPreviewDto(
    string Version,
    int NamespaceCount,
    int EntryCount,
    IReadOnlyList<LocresEntryDto> Entries);

public sealed record LocresEntryDto(
    int Index,
    string Namespace,
    string Key,
    string Text,
    uint NamespaceHash = 0,
    uint KeyHash = 0,
    uint SourceHash = 0);

public sealed record MaterialParameterPreviewDto(
    string Kind,
    string Name,
    string Value);

public sealed record ExportRequest(
    IReadOnlyList<string> EntryPaths,
    string OutputDirectory,
    bool IncludePackagePayloads = true);

public sealed record ExportProgress(
    int Completed,
    int Total,
    string CurrentPath);

public sealed record ExportResult(
    int Succeeded,
    int Failed,
    IReadOnlyList<string> Errors);

public sealed record PreviewExportFileDto(
    string FileName,
    string MimeType,
    byte[] Data);

public sealed record PreviewExportDto(
    string Kind,
    string Title,
    IReadOnlyList<PreviewExportFileDto> Files);

public sealed record AudioPayloadDto(
    string Title,
    string Format,
    string? MimeType,
    byte[] Data);

public sealed record PakRawFileCopy(
    string PakPath,
    string DiskPath,
    long Size);

/// <summary>
/// 「搜索框输入路径」的解析结果：命中目录或文件时返回，未命中返回 null。
/// </summary>
public sealed record BrowsePathResolution(
    string Folder,
    string? FileName,
    bool IsFolder);

public sealed record PakRawFileCopyProgress(
    int Completed,
    int Total,
    string CurrentPath);
