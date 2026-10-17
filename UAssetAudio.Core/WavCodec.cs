using System.Buffers.Binary;

namespace UAssetAudio.Core;

/// <summary>
/// 自带的最小 WAV 读写：<c>binka_encode.exe</c> 只吃 16 位整型 PCM，而素材常是 24 位或浮点，
/// 所以这里把各种位深统一折到 16 位，避免再引入一个第三方依赖。
/// </summary>
public static class WavCodec
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>解析 WAV，返回 16 位小端交错 PCM。</summary>
    public static PcmAudio Read(byte[] data)
    {
        if (data.Length < 12 ||
            !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !data.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("不是有效的 WAV 文件（缺少 RIFF/WAVE 头）。");
        }

        ushort format = 0;
        ushort channels = 0;
        int sampleRate = 0;
        ushort bitsPerSample = 0;
        int dataOffset = -1;
        int dataLength = 0;

        var position = 12;
        while (position + 8 <= data.Length)
        {
            var chunkId = data.AsSpan(position, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position + 4, 4));
            var body = position + 8;
            if (chunkSize < 0)
                throw new InvalidDataException("WAV 片段长度非法。");

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunkSize < 16 || body + 16 > data.Length)
                    throw new InvalidDataException("WAV 的 fmt 片段不完整。");

                format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 14));

                if (format == FormatExtensible && chunkSize >= 40 && body + 26 <= data.Length)
                {
                    // WAVE_FORMAT_EXTENSIBLE：真实编码在前 2 字节的 SubFormat GUID 里。
                    format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 24));
                }
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataOffset = body;
                // 有些文件把 data 长度写成 0 或超出文件，按实际剩余长度兜底。
                dataLength = chunkSize > 0 && body + chunkSize <= data.Length ? chunkSize : data.Length - body;
                break;
            }

            position = body + chunkSize + (chunkSize & 1);
        }

        if (dataOffset < 0)
            throw new InvalidDataException("WAV 里没有 data 片段。");
        if (channels == 0 || sampleRate <= 0)
            throw new InvalidDataException("WAV 的 fmt 片段缺少声道或采样率。");
        if (format != FormatPcm)
        {
            if (format != FormatFloat)
                throw new InvalidDataException($"这个 WAV 是压缩编码（format={format}），请先解成 PCM。");
            if (bitsPerSample is not (32 or 64))
                throw new InvalidDataException($"浮点 WAV 只支持 32/64 位，当前 {bitsPerSample} 位。");
        }

        var pcm = ToPcm16(data.AsSpan(dataOffset, dataLength), bitsPerSample, format == FormatFloat);
        return new PcmAudio(pcm, sampleRate, channels);
    }

    private static byte[] ToPcm16(ReadOnlySpan<byte> source, int bitsPerSample, bool isFloat)
    {
        var bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample <= 0)
            throw new InvalidDataException("WAV 的位深非法。");

        var frames = source.Length / bytesPerSample;
        var result = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var sample = source.Slice(i * bytesPerSample, bytesPerSample);
            short value;
            if (isFloat)
            {
                var f = bitsPerSample == 64 ? BinaryPrimitives.ReadDoubleLittleEndian(sample) : BinaryPrimitives.ReadSingleLittleEndian(sample);
                value = (short)Math.Clamp(Math.Round(f * short.MaxValue), short.MinValue, short.MaxValue);
            }
            else
            {
                value = bitsPerSample switch
                {
                    8 => (short)((sample[0] - 128) << 8),
                    16 => BinaryPrimitives.ReadInt16LittleEndian(sample),
                    24 => Read24(sample),
                    32 => (short)(BinaryPrimitives.ReadInt32LittleEndian(sample) >> 16),
                    _ => throw new InvalidDataException($"不支持 {bitsPerSample} 位整型 WAV。"),
                };
            }

            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 2), value);
        }

        return result;
    }

    private static short Read24(ReadOnlySpan<byte> sample)
    {
        var value = sample[0] | (sample[1] << 8) | (sample[2] << 16);
        if ((value & 0x800000) != 0)
            value |= unchecked((int)0xFF000000); // 符号扩展
        return (short)(value >> 8);
    }

    /// <summary>把 16 位 PCM 包成标准 44 字节头的 WAV。</summary>
    public static byte[] Write16(PcmAudio audio)
    {
        var result = new byte[44 + audio.Pcm16.Length];
        var span = result.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + audio.Pcm16.Length);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)audio.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], audio.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], audio.SampleRate * audio.Channels * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)(audio.Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], audio.Pcm16.Length);
        audio.Pcm16.CopyTo(span[44..]);
        return result;
    }

    public static byte[] Write16(byte[] pcm16, int sampleRate, int channels) =>
        Write16(new PcmAudio(pcm16, sampleRate, channels));

    /// <summary>
    /// 只读文件头算时长，不复制 PCM —— 预览面板要在播放前就把进度条填上，
    /// 为此整段解一遍太浪费。
    /// </summary>
    public static bool TryReadSeconds(byte[] data, out double seconds)
    {
        seconds = 0;
        if (data.Length < 12 ||
            !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !data.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }

        var sampleRate = 0;
        var channels = 0;
        var bitsPerSample = 0;
        var dataLength = -1;

        var position = 12;
        while (position + 8 <= data.Length)
        {
            var chunkId = data.AsSpan(position, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position + 4, 4));
            var body = position + 8;
            if (chunkSize < 0)
                return false;

            if (chunkId.SequenceEqual("fmt "u8) && chunkSize >= 16 && body + 16 <= data.Length)
            {
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 14));
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataLength = chunkSize > 0 && body + chunkSize <= data.Length ? chunkSize : data.Length - body;
                break;
            }

            position = body + chunkSize + (chunkSize & 1);
        }

        if (sampleRate <= 0 || channels <= 0 || bitsPerSample <= 0 || dataLength <= 0)
            return false;

        var bytesPerFrame = channels * (bitsPerSample / 8);
        if (bytesPerFrame <= 0)
            return false;

        seconds = dataLength / (double)bytesPerFrame / sampleRate;
        return seconds > 0;
    }
}
