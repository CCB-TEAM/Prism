using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

internal static class TextureAssetParser
{
    public static TextureAssetInfo Load(string assetPath, EngineVersion engineVersion, string? usmapPath)
    {
        string fullAssetPath = Path.GetFullPath(assetPath);
        string uexpPath = Path.ChangeExtension(fullAssetPath, ".uexp");
        string ubulkPath = Path.ChangeExtension(fullAssetPath, ".ubulk");

        if (!File.Exists(uexpPath))
        {
            throw new FileNotFoundException("Matching .uexp was not found.", uexpPath);
        }

        Usmap? mappings = string.IsNullOrWhiteSpace(usmapPath) ? null : new Usmap(usmapPath);
        UAsset asset = new(fullAssetPath, engineVersion, mappings, CustomSerializationFlags.SkipParsingExports);

        RawExport rawExport = asset.Exports.OfType<RawExport>().FirstOrDefault()
            ?? throw new InvalidOperationException("No raw export was found. This tool currently expects cooked texture exports.");

        byte[] exportData = rawExport.Data;
        byte[] uexpBytes = File.ReadAllBytes(uexpPath);
        if (uexpBytes.Length < exportData.Length)
        {
            throw new InvalidOperationException("UEXP is shorter than the export data length.");
        }

        byte[] footer = uexpBytes[exportData.Length..];
        byte[] ubulkBytes = File.Exists(ubulkPath) ? File.ReadAllBytes(ubulkPath) : [];
        TextureFormatInfo format = TextureFormats.DetectFormat(exportData, asset.GetNameMapIndexList().Select(name => name.Value));

        int width = BitConverter.ToInt32(exportData, 4);
        int height = BitConverter.ToInt32(exportData, 8);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Failed to read a valid texture size from the export data.");
        }

        List<TextureMip> fullMipChain = BuildMipChain(width, height, format).ToList();
        int externalMipCount = ResolveExternalMipCount(fullMipChain, ubulkBytes.Length);
        List<TextureMip> externalMips = fullMipChain.Take(externalMipCount).ToList();
        (List<TextureMip> inlineMips, List<int> markers) = ResolveInlineMipLayout(exportData, fullMipChain.Skip(externalMipCount).ToList());
        List<TextureMip> mips = externalMips.Concat(inlineMips).ToList();

        List<TextureMipPlacement> placements = new();
        int ubulkOffset = 0;
        for (int i = 0; i < externalMipCount; i++)
        {
            TextureMip mip = mips[i];
            placements.Add(new TextureMipPlacement(mip.Index, mip.Width, mip.Height, mip.ByteLength, TextureMipStorage.Ubulk, ubulkOffset));
            ubulkOffset += mip.ByteLength;
        }

        if (inlineMips.Count > 0)
        {
            int firstInlineStart = markers[0] - inlineMips[0].ByteLength;
            placements.Add(new TextureMipPlacement(
                inlineMips[0].Index,
                inlineMips[0].Width,
                inlineMips[0].Height,
                inlineMips[0].ByteLength,
                TextureMipStorage.UexpInline,
                firstInlineStart));

            for (int i = 1; i < inlineMips.Count; i++)
            {
                int offset = markers[i - 1] + 16;
                TextureMip mip = inlineMips[i];
                placements.Add(new TextureMipPlacement(mip.Index, mip.Width, mip.Height, mip.ByteLength, TextureMipStorage.UexpInline, offset));
            }
        }

        int sentinelOffset = markers.Count > 0 ? markers[^1] : -1;

        return new TextureAssetInfo(
            fullAssetPath,
            uexpPath,
            ubulkPath,
            exportData,
            footer,
            ubulkBytes,
            format,
            width,
            height,
            mips,
            externalMipCount,
            inlineMips,
            markers,
            sentinelOffset,
            placements);
    }

    private static IEnumerable<TextureMip> BuildMipChain(int width, int height, TextureFormatInfo format)
    {
        int mipIndex = 0;
        int currentWidth = width;
        int currentHeight = height;
        while (true)
        {
            yield return new TextureMip(mipIndex, currentWidth, currentHeight, format.GetMipByteSize(currentWidth, currentHeight));
            if (currentWidth == 1 && currentHeight == 1)
            {
                break;
            }

            currentWidth = Math.Max(1, currentWidth / 2);
            currentHeight = Math.Max(1, currentHeight / 2);
            mipIndex++;
        }
    }

    private static int ResolveExternalMipCount(IReadOnlyList<TextureMip> mips, int ubulkLength)
    {
        if (ubulkLength == 0)
        {
            return 0;
        }

        int total = 0;
        for (int i = 0; i < mips.Count; i++)
        {
            total += mips[i].ByteLength;
            if (total == ubulkLength)
            {
                return i + 1;
            }
        }

        throw new InvalidOperationException($"Could not match UBULK length {ubulkLength} to a prefix of the mip chain.");
    }

    private static (List<TextureMip> Mips, List<int> Markers) ResolveInlineMipLayout(byte[] exportData, IReadOnlyList<TextureMip> candidateInlineMips)
    {
        if (candidateInlineMips.Count == 0)
        {
            return ([], []);
        }

        List<int> markers = new();
        List<TextureMip> inlineMips = new();
        foreach (TextureMip mip in candidateInlineMips)
        {
            List<int> hits = FindDimensionMarkers(exportData, mip.Width, mip.Height);
            if (hits.Count == 0)
            {
                break;
            }

            inlineMips.Add(mip);
            markers.Add(hits[^1]);
        }

        if (inlineMips.Count == 0)
        {
            return ([], []);
        }

        for (int i = 1; i < markers.Count; i++)
        {
            if (markers[i] <= markers[i - 1])
            {
                throw new InvalidOperationException("Inline mip markers were not strictly increasing.");
            }
        }

        if (inlineMips.Count > 1)
        {
            for (int i = 1; i < inlineMips.Count; i++)
            {
                int actual = markers[i] - markers[i - 1] - 16;
                int expected = inlineMips[i].ByteLength;
                if (actual != expected)
                {
                    throw new InvalidOperationException(
                        $"Inline mip layout mismatch near mip {inlineMips[i].Index}: expected {expected} bytes, found {actual}.");
                }
            }
        }

        int firstStart = markers[0] - inlineMips[0].ByteLength;
        if (firstStart < 0)
        {
            throw new InvalidOperationException("The first inline mip would start before the export data begins.");
        }

        return (inlineMips, markers);
    }

    private static List<int> FindDimensionMarkers(byte[] exportData, int width, int height)
    {
        byte[] widthBytes = BitConverter.GetBytes(width);
        byte[] heightBytes = BitConverter.GetBytes(height);
        byte[] depthBytes = BitConverter.GetBytes(1);
        List<int> hits = [];

        for (int i = 0; i <= exportData.Length - 16; i++)
        {
            if (Matches(exportData, i, widthBytes)
                && Matches(exportData, i + 4, heightBytes)
                && Matches(exportData, i + 8, depthBytes))
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    private static bool Matches(byte[] source, int offset, byte[] pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (source[offset + i] != pattern[i])
            {
                return false;
            }
        }

        return true;
    }
}
