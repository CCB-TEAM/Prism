using System.Text;

internal static class TextureFormats
{
    private static readonly TextureFormatInfo[] KnownFormats =
    [
        new("PF_DXT1", 4, 4, 8),
        new("PF_DXT5", 4, 4, 16),
        new("PF_BC7", 4, 4, 16),
        new("PF_ASTC_4x4", 4, 4, 16),
        new("PF_ASTC_5x4", 5, 4, 16),
        new("PF_ASTC_5x5", 5, 5, 16),
        new("PF_ASTC_6x5", 6, 5, 16),
        new("PF_ASTC_6x6", 6, 6, 16),
        new("PF_ASTC_8x5", 8, 5, 16),
        new("PF_ASTC_8x6", 8, 6, 16),
        new("PF_ASTC_8x8", 8, 8, 16),
        new("PF_ASTC_10x5", 10, 5, 16),
        new("PF_ASTC_10x6", 10, 6, 16),
        new("PF_ASTC_10x8", 10, 8, 16),
        new("PF_ASTC_10x10", 10, 10, 16),
        new("PF_ASTC_12x10", 12, 10, 16),
        new("PF_ASTC_12x12", 12, 12, 16),
    ];

    public static TextureFormatInfo DetectFormat(byte[] exportData, IEnumerable<string> nameMap)
    {
        string ascii = Encoding.ASCII.GetString(exportData);
        foreach (TextureFormatInfo format in KnownFormats)
        {
            if (ascii.Contains(format.Name, StringComparison.Ordinal))
            {
                return format;
            }
        }

        foreach (string name in nameMap)
        {
            TextureFormatInfo? match = KnownFormats.FirstOrDefault(format =>
                string.Equals(format.Name, name, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        throw new InvalidOperationException("Could not detect a supported pixel format from the asset.");
    }
}
