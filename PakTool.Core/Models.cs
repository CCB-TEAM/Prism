namespace PakTool.Core;

public sealed record PakOpenOptions(
    IReadOnlyList<string> PakPaths,
    string? AesKeyHex = null,
    string? UsmapPath = null,
    string Game = "GAME_UE5_6",
    bool CaseInsensitivePaths = true,
    Action<string>? DecodeLogger = null);

public sealed record PakOpenResult(
    int MountedArchiveCount,
    int FileCount,
    int RequiredKeyCount,
    IReadOnlyList<string> MountedArchives,
    IReadOnlyList<OperationTimingDto> Timings);

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
    bool CanExportRaw = true);

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

public sealed record PakRawFileCopyProgress(
    int Completed,
    int Total,
    string CurrentPath);
