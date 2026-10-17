using System.Buffers.Binary;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetAudio.Core;

/// <summary>
/// 把新的 BINKA 音频塞回一个流式（streamed）SoundWave 资产。
///
/// UE 的流式音频布局（5.x，实测与 Flora 的实现一致）：
/// <list type="bullet">
///   <item>chunk 0 内联在 <c>.uexp</c> 里，内容是 28 字节的 ABEU 头；</item>
///   <item>chunk 1…N 放在 <c>.ubulk</c>（5.3+ 走 data resource），内容是一段 15 字节的
///         <c>SEEK</c> 前导 + seek 表 + BINKA 数据块。</item>
/// </list>
/// 替换只改这些定长字段与数据资源的长度，属性值（声道/采样率/时长）同步更新，
/// 因此不会牵动导出表以外的结构。
/// </summary>
public static class SoundWavePatcher
{
    /// <summary>UE 单个流式块的上限：再大就要分块，而分块的 seek 表语义尚未验证，这里先卡住。</summary>
    public const int MaxDataChunkBytes = 262144;

    private const int PrefixSize = 36; // flags(4) + cuePoints(4) + guid(16) + numChunks(4) + audioFormat(8)
    private const int SeekPrefixSize = 15; // "SEEK" + frames(4) + 3 字节保留 + seek 表项数(4)

    private const uint WaveCookedFlag = 1u << 0;
    private const uint ChunkIsCooked = 1u << 0;
    private const uint ChunkHasSeekOffset = 1u << 1;
    private const uint ChunkIsInlined = 1u << 2;

    /// <summary>读一个流式 SoundWave 的现状，用于 UI 判断能不能替换。</summary>
    public static SoundWaveInfo Inspect(string uassetPath, string? usmapPath = null)
    {
        var (asset, export) = Load(uassetPath, usmapPath);
        var layout = ParseLayout(export, asset);

        var channels = GetInt(export, "NumChannels");
        var sampleRate = GetInt(export, "SampleRate");
        var seconds = (double)GetFloat(export, "Duration");
        var audioBytes = layout.Chunks.Sum(chunk => Math.Max(chunk.DataSize, 0));

        // 属性缺失时退回到 ABEU 头里的值（头是音频自身的真值时更可信）
        if (layout.Header is { Length: >= BinkaEncoder.HeaderSize })
        {
            try
            {
                var header = BinkaEncoder.ReadInfo(layout.Header);
                channels = channels > 0 ? channels : header.Channels;
                sampleRate = sampleRate > 0 ? sampleRate : header.SampleRate;
                seconds = seconds > 0 ? seconds : header.Seconds;
            }
            catch (InvalidDataException)
            {
                // 头不合法就只用属性里的值
            }
        }

        return new SoundWaveInfo(
            Format: layout.AudioFormatText,
            IsStreaming: layout.Chunks.Count > 1,
            IsBinka: layout.IsBinka,
            IsCooked: (layout.Flags & WaveCookedFlag) != 0,
            ChunkCount: layout.Chunks.Count,
            CuePointCount: layout.CuePointCount,
            Channels: channels,
            SampleRate: sampleRate,
            Seconds: seconds,
            AudioBytes: audioBytes)
        {
            Problem = DescribeProblem(layout),
        };
    }

    /// <summary>
    /// 用新的 BINKA 字节重建三件套。<paramref name="binka"/> 可以是
    /// <see cref="BinkaEncoder.Encode"/> 的输出，也可以是别处拿到的 ABEU 文件。
    /// </summary>
    public static SoundWaveReplacement Replace(string uassetPath, byte[] binka, string? usmapPath = null)
    {
        var audio = BinkaEncoder.ReadInfo(binka);
        var (asset, export) = Load(uassetPath, usmapPath);

        var layout = ParseLayout(export, asset);
        var problem = DescribeProblem(layout);
        if (problem is not null)
            throw new InvalidOperationException(problem);

        var resources = asset.DataResources;
        if (resources is null || resources.Count == 0)
            throw new InvalidOperationException("这个 SoundWave 没有 data resource（不是 5.3+ 的流式布局）。");

        var header = BuildHeaderChunk(binka);
        var dataChunk = BuildDataChunk(binka, audio);
        if (dataChunk.Length > MaxDataChunkBytes)
        {
            throw new InvalidOperationException(
                $"编码后 {dataChunk.Length / 1024} KB，超过单块上限 {MaxDataChunkBytes / 1024} KB。" +
                "把音质数字调大（更省）或换短一点的素材。");
        }

        // 只保留头块 + 数据块：原资产若切成更多块，多出来的 data resource 一并移除
        var headerIndex = layout.Chunks[0].ResourceIndex;
        var dataIndex = layout.Chunks[1].ResourceIndex;
        var dropped = layout.Chunks.Skip(2)
            .Select(chunk => chunk.ResourceIndex)
            .Where(index => index != headerIndex && index != dataIndex)
            .Distinct()
            .OrderByDescending(index => index)
            .ToList();
        foreach (var index in dropped)
        {
            if (index >= 0 && index < resources.Count)
                resources.RemoveAt(index);
        }

        headerIndex = ShiftIndex(headerIndex, dropped);
        dataIndex = ShiftIndex(dataIndex, dropped);
        if (headerIndex < 0 || dataIndex < 0 || headerIndex >= resources.Count || dataIndex >= resources.Count)
            throw new InvalidOperationException("整理数据块后 data resource 索引越界，资产布局超出预期。");

        var headerResource = resources[headerIndex];
        headerResource.SerialSize = headerResource.RawSize = header.Length;
        resources[headerIndex] = headerResource;

        var dataResource = resources[dataIndex];
        dataResource.SerialSize = dataResource.RawSize = dataChunk.Length;
        resources[dataIndex] = dataResource;

        export.Extras = BuildExtras(layout, header, dataChunk, headerIndex, dataIndex);

        SetInt(export, "NumChannels", audio.Channels);
        SetInt(export, "SampleRate", audio.SampleRate);
        SetFloat(export, "Duration", (float)audio.Seconds);
        SetFloat(export, "TotalSamples", audio.SampleCount);

        var (uasset, uexp) = WriteAsset(asset);
        return new SoundWaveReplacement(uasset, uexp, dataChunk, audio);
    }

    /// <summary>把 <paramref name="binka"/> 编成 <c>.ubulk</c> 的内容（SEEK 前导 + seek 表 + 数据块）。</summary>
    public static byte[] BuildDataChunk(byte[] binka, BinkaAudioInfo audio)
    {
        var seekEntries = audio.SeekEntryCount;
        if (seekEntries <= 1)
            throw new InvalidDataException("BINKA 的 seek 表太小，无法生成流式块。");

        var blocksStart = BinkaEncoder.HeaderSize + seekEntries * 2;
        if (blocksStart >= binka.Length)
            throw new InvalidDataException("BINKA 数据的 seek 表越界（文件可能被截断）。");

        var blockBytes = binka.Length - blocksStart;
        var chunk = new byte[SeekPrefixSize + seekEntries * 2 + blockBytes];

        "SEEK"u8.CopyTo(chunk);
        // 一个流式块覆盖的帧数：UE 的编码器固定按 10.24 秒一块写
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)Math.Round(audio.SampleRate * 10.24));
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(11), (uint)seekEntries);

        // UE 的 seek 表比 ABEU 那张整体后移一格，首项补 0（实测原始资产就是这个规则）
        var table = chunk.AsSpan(SeekPrefixSize, seekEntries * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(table, 0);
        binka.AsSpan(BinkaEncoder.HeaderSize, (seekEntries - 1) * 2).CopyTo(table[2..]);

        binka.AsSpan(blocksStart, blockBytes).CopyTo(chunk.AsSpan(SeekPrefixSize + seekEntries * 2));
        return chunk;
    }

    private static byte[] BuildHeaderChunk(byte[] binka)
    {
        var header = binka.AsSpan(0, BinkaEncoder.HeaderSize).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24), 0); // 头里不带 seek 表
        return header;
    }

    private static byte[] BuildExtras(
        StreamedLayout layout,
        byte[] header,
        byte[] dataChunk,
        int headerResourceIndex,
        int dataResourceIndex)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(layout.Flags);
        writer.Write(layout.CuePointCount);
        writer.Write(Guid.NewGuid().ToByteArray()); // 新音频 = 新的压缩数据标识
        writer.Write(2);                            // 只留头块 + 数据块
        writer.Write(layout.AudioFormatNameIndex);
        writer.Write(layout.AudioFormatNameNumber);

        // chunk 0：内联 ABEU 头
        writer.Write((layout.Chunks[0].Flags | ChunkIsCooked | ChunkIsInlined) & ~ChunkHasSeekOffset);
        writer.Write(headerResourceIndex);
        writer.Write(header);
        writer.Write(header.Length);
        writer.Write(header.Length);

        // chunk 1：数据块，带 seek 偏移
        writer.Write((layout.Chunks[1].Flags | ChunkIsCooked | ChunkHasSeekOffset) & ~ChunkIsInlined);
        writer.Write(dataResourceIndex);
        writer.Write(dataChunk.Length);
        writer.Write(dataChunk.Length);
        writer.Write(0u);

        writer.Flush();
        return stream.ToArray();
    }

    private static (UAsset Asset, NormalExport Export) Load(string uassetPath, string? usmapPath)
    {
        var full = Path.GetFullPath(uassetPath);
        if (!File.Exists(full))
            throw new FileNotFoundException("找不到 .uasset。", full);

        var mappings = string.IsNullOrWhiteSpace(usmapPath) ? null : new Usmap(Path.GetFullPath(usmapPath));
        var asset = new UAsset(full, EngineVersion.VER_UE5_6, mappings);

        var export = asset.Exports
            .OfType<NormalExport>()
            .FirstOrDefault(candidate => HasProperty(candidate, "SampleRate") || HasProperty(candidate, "NumChannels"));
        if (export is null)
        {
            if (asset.HasUnversionedProperties && mappings is null)
            {
                throw new InvalidOperationException(
                    "音频资产用的是 unversioned 属性，需要先在设置页选择映射文件（.usmap）才能解析并替换。");
            }

            throw new InvalidOperationException("这个包里没找到可替换的 SoundWave 导出。");
        }

        return (asset, export);
    }

    private static StreamedLayout ParseLayout(NormalExport export, UAsset asset)
    {
        var extras = export.Extras;
        if (extras is not { Length: > PrefixSize })
            throw new InvalidOperationException("SoundWave 的尾部数据太短，不像流式音频。");

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(extras);
        var cuePoints = BinaryPrimitives.ReadInt32LittleEndian(extras.AsSpan(4));
        var chunkCount = BinaryPrimitives.ReadInt32LittleEndian(extras.AsSpan(24));
        var formatNameIndex = BinaryPrimitives.ReadInt32LittleEndian(extras.AsSpan(28));
        var formatNameNumber = BinaryPrimitives.ReadInt32LittleEndian(extras.AsSpan(32));

        if (chunkCount is < 1 or > 4096)
            throw new InvalidOperationException($"SoundWave 的数据块数量异常（{chunkCount}）。");

        var resources = asset.DataResources;
        var chunks = new List<ChunkRecord>(chunkCount);
        var position = PrefixSize;
        for (var i = 0; i < chunkCount; i++)
        {
            if (position + 8 > extras.Length)
                throw new InvalidOperationException("SoundWave 的数据块记录被截断。");

            var chunkFlags = BinaryPrimitives.ReadUInt32LittleEndian(extras.AsSpan(position));
            var resourceIndex = BinaryPrimitives.ReadInt32LittleEndian(extras.AsSpan(position + 4));
            position += 8;

            byte[]? inline = null;
            if ((chunkFlags & ChunkIsInlined) != 0)
            {
                if (resources is null || resourceIndex < 0 || resourceIndex >= resources.Count)
                    throw new InvalidOperationException("内联数据块指向了不存在的 data resource。");

                var size = checked((int)resources[resourceIndex].SerialSize);
                if (size < 0 || position + size > extras.Length)
                    throw new InvalidOperationException("内联数据块的长度越界。");

                inline = extras.AsSpan(position, size).ToArray();
                position += size;
            }

            if (position + 8 > extras.Length)
                throw new InvalidOperationException("SoundWave 的数据块尺寸字段被截断。");

            var dataSize = BinaryPrimitives.ReadInt32LittleEndian(extras.AsSpan(position));
            var audioSize = BinaryPrimitives.ReadInt32LittleEndian(extras.AsSpan(position + 4));
            position += 8;

            uint seekOffset = 0;
            if ((chunkFlags & ChunkHasSeekOffset) != 0)
            {
                if (position + 4 > extras.Length)
                    throw new InvalidOperationException("SoundWave 的 seek 偏移字段被截断。");

                seekOffset = BinaryPrimitives.ReadUInt32LittleEndian(extras.AsSpan(position));
                position += 4;
            }

            chunks.Add(new ChunkRecord(chunkFlags, resourceIndex, inline, dataSize, audioSize, seekOffset));
        }

        if (position != extras.Length)
            throw new InvalidOperationException("SoundWave 的尾部数据和已知的流式布局对不上。");

        return new StreamedLayout(
            flags,
            cuePoints,
            formatNameIndex,
            formatNameNumber,
            asset.GetNameReference(formatNameIndex).ToString(),
            chunks,
            chunks[0].Inline);
    }

    private static string? DescribeProblem(StreamedLayout layout)
    {
        if ((layout.Flags & WaveCookedFlag) == 0)
            return "这个音频不是 cook 过的资产，无法替换。";
        if (layout.CuePointCount > 0)
            return $"这个音频带 {layout.CuePointCount} 个 SoundWaveCuePoint，暂不支持替换。";
        if (layout.Chunks.Count < 2)
            return "这个音频是内嵌（非流式）存储，暂不支持替换。";
        if (layout.Chunks[0].Inline is null)
            return "这个音频的头部块不在 .uexp 里，布局超出预期。";
        if (layout.Chunks[1].Inline is not null)
            return "这个音频的数据块是内联的，布局超出预期。";
        if (!layout.IsBinka)
            return $"只支持 BINKA（Bink Audio）编码的流式音频，这个资产是 {layout.AudioFormatText}。";

        return null;
    }

    private static int ShiftIndex(int index, List<int> removed)
    {
        foreach (var candidate in removed)
        {
            if (candidate < index)
                index--;
        }

        return index;
    }

    private static (byte[] Uasset, byte[] Uexp) WriteAsset(UAsset asset)
    {
        var temp = Path.Combine(Path.GetTempPath(), "prism-soundwave", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var path = Path.Combine(temp, "patched.uasset");
            asset.Write(path);
            var uexpPath = Path.ChangeExtension(path, ".uexp");
            if (!File.Exists(path) || !File.Exists(uexpPath))
                throw new IOException("UAssetAPI 没有写出 .uasset/.uexp。");
            return (File.ReadAllBytes(path), File.ReadAllBytes(uexpPath));
        }
        finally
        {
            try
            {
                Directory.Delete(temp, true);
            }
            catch
            {
                // 临时目录残留不影响结果
            }
        }
    }

    private static bool HasProperty(NormalExport export, string name) =>
        export.Data.Any(property => string.Equals(property.Name.ToString(), name, StringComparison.Ordinal));

    private static int GetInt(NormalExport export, string name)
    {
        var property = export.Data.FirstOrDefault(candidate =>
            string.Equals(candidate.Name.ToString(), name, StringComparison.Ordinal));
        return property is UAssetAPI.PropertyTypes.Objects.IntPropertyData intProperty ? intProperty.Value : 0;
    }

    private static float GetFloat(NormalExport export, string name)
    {
        var property = export.Data.FirstOrDefault(candidate =>
            string.Equals(candidate.Name.ToString(), name, StringComparison.Ordinal));
        return property is UAssetAPI.PropertyTypes.Objects.FloatPropertyData floatProperty ? floatProperty.Value : 0f;
    }

    private static void SetInt(NormalExport export, string name, int value)
    {
        foreach (var property in export.Data)
        {
            if (property is UAssetAPI.PropertyTypes.Objects.IntPropertyData intProperty &&
                string.Equals(property.Name.ToString(), name, StringComparison.Ordinal))
            {
                intProperty.Value = value;
            }
        }
    }

    private static void SetFloat(NormalExport export, string name, float value)
    {
        foreach (var property in export.Data)
        {
            if (property is UAssetAPI.PropertyTypes.Objects.FloatPropertyData floatProperty &&
                string.Equals(property.Name.ToString(), name, StringComparison.Ordinal))
            {
                floatProperty.Value = value;
            }
        }
    }

    private sealed record ChunkRecord(
        uint Flags,
        int ResourceIndex,
        byte[]? Inline,
        int DataSize,
        int AudioDataSize,
        uint SeekOffsetInFrames);

    private sealed record StreamedLayout(
        uint Flags,
        int CuePointCount,
        int AudioFormatNameIndex,
        int AudioFormatNameNumber,
        string AudioFormatText,
        List<ChunkRecord> Chunks,
        byte[]? Header)
    {
        public bool IsBinka =>
            AudioFormatText.Equals("BINKA", StringComparison.OrdinalIgnoreCase) ||
            (Header is { Length: >= 4 } header && header.AsSpan(0, 4).SequenceEqual("ABEU"u8));
    }
}
