internal sealed record TextureFormatInfo(string Name, int BlockWidth, int BlockHeight, int BlockBytes)
{
    public bool IsAstc => Name.StartsWith("PF_ASTC_", StringComparison.Ordinal);

    public bool IsDxt1 => string.Equals(Name, "PF_DXT1", StringComparison.OrdinalIgnoreCase);

    public bool RequiresTexconv => string.Equals(Name, "PF_DXT5", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Name, "PF_BC7", StringComparison.OrdinalIgnoreCase);

    public string AstcBlockSize
    {
        get
        {
            if (!IsAstc)
            {
                throw new InvalidOperationException($"{Name} is not an ASTC format.");
            }

            return Name["PF_ASTC_".Length..].Replace('x', 'x');
        }
    }

    public int GetMipByteSize(int width, int height)
    {
        int blocksWide = Math.Max(1, (width + BlockWidth - 1) / BlockWidth);
        int blocksHigh = Math.Max(1, (height + BlockHeight - 1) / BlockHeight);
        return blocksWide * blocksHigh * BlockBytes;
    }
}

internal sealed record TextureMip(int Index, int Width, int Height, int ByteLength);

internal enum TextureMipStorage
{
    Ubulk,
    UexpInline
}

internal sealed record TextureMipPlacement(int Index, int Width, int Height, int ByteLength, TextureMipStorage Storage, int Offset);

internal sealed record TextureAssetInfo(
    string AssetPath,
    string UexpPath,
    string UbulkPath,
    byte[] ExportData,
    byte[] UexpFooter,
    byte[] UbulkData,
    TextureFormatInfo Format,
    int Width,
    int Height,
    IReadOnlyList<TextureMip> Mips,
    int ExternalMipCount,
    IReadOnlyList<TextureMip> InlineMips,
    IReadOnlyList<int> InlineMarkerOffsets,
    int InlineSentinelOffset,
    IReadOnlyList<TextureMipPlacement> MipPlacements);
