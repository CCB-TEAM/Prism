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

        // 纹理尺寸不能写死在固定偏移上。
        //
        // 原先固定读 exportData[4] / exportData[8]：对 t_cromwell 这类资产恰好正确，
        // 但 KARDS 的资产在导出数据前还有一段内联元数据，尺寸三元组落在 offset 80，
        // 于是读到的 15x0 被当成真实尺寸，mip 链整条错位，最终在
        // TextureReplacer.WriteReplacement 里以 Buffer.BlockCopy 越界告终。
        //
        // 改为扫描所有 (宽度, 高度, 深度=1) 三元组作为候选，按面积从大到小逐个尝试，
        // 取第一个能解析出完整 mip 布局的候选。
        List<(int Width, int Height)> candidates = FindTextureSizeCandidates(exportData);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("Failed to find a valid texture size in the export data.");
        }

        Exception? lastError = null;
        foreach ((int candidateWidth, int candidateHeight) in candidates)
        {
            try
            {
                List<TextureMip> fullMipChain = BuildMipChain(candidateWidth, candidateHeight, format).ToList();
                int externalMipCount = ResolveExternalMipCount(fullMipChain, ubulkBytes.Length);
                List<TextureMip> externalMips = fullMipChain.Take(externalMipCount).ToList();
                (List<TextureMip> inlineMips, List<int> markers) = ResolveInlineMipLayout(exportData, fullMipChain.Skip(externalMipCount).ToList());
                if (inlineMips.Count == 0 && externalMipCount == 0)
                {
                    continue;
                }

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
                    candidateWidth,
                    candidateHeight,
                    mips,
                    externalMipCount,
                    inlineMips,
                    markers,
                    sentinelOffset,
                    placements);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            $"Failed to resolve the texture mip layout. format={format.Name}, export={exportData.Length} bytes, " +
            $"uexp={uexpBytes.Length} bytes, ubulk={ubulkBytes.Length} bytes, " +
            $"candidates=[{string.Join(", ", candidates.Select(c => $"{c.Width}x{c.Height}"))}], " +
            $"last={lastError?.Message ?? "<none>"}",
            lastError);
    }

    /// <summary>
    /// 扫描导出数据，收集所有看似纹理尺寸的 (宽度, 高度, 深度=1) 三元组，
    /// 按面积从大到小返回。过滤掉非 2 的幂与超范围的值，避免把纹理数据里的
    /// 巧合字节当成尺寸。
    /// </summary>
    private static List<(int Width, int Height)> FindTextureSizeCandidates(byte[] exportData)
    {
        List<(int Width, int Height)> result = new();
        HashSet<(int Width, int Height)> seen = new();

        for (int offset = 0; offset <= exportData.Length - 12; offset++)
        {
            int width = BitConverter.ToInt32(exportData, offset);
            int height = BitConverter.ToInt32(exportData, offset + 4);
            int depth = BitConverter.ToInt32(exportData, offset + 8);

            if (depth != 1 || !IsSaneTextureSize(width) || !IsSaneTextureSize(height))
            {
                continue;
            }

            if (seen.Add((width, height)))
            {
                result.Add((width, height));
            }
        }

        return result
            .OrderByDescending(candidate => (long)candidate.Width * candidate.Height)
            .ThenByDescending(candidate => Math.Max(candidate.Width, candidate.Height))
            .ToList();
    }

    private static bool IsSaneTextureSize(int value)
    {
        return value > 0
            && value <= 32768
            && (value & (value - 1)) == 0;
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
