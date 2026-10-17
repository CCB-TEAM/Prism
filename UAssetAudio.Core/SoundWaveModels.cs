namespace UAssetAudio.Core;

/// <summary>一段已解码的 PCM：16 位小端、交错声道，可直接写进 WAV 或交给 BINKA 编码器。</summary>
public sealed record PcmAudio(byte[] Pcm16, int SampleRate, int Channels)
{
    public int FrameCount => Channels <= 0 ? 0 : Pcm16.Length / (2 * Channels);

    public double Seconds => SampleRate <= 0 ? 0 : FrameCount / (double)SampleRate;
}

/// <summary>BINKA（ABEU 容器）文件头，对应 UE 的 <c>BinkAudioFileHeader</c>。</summary>
public sealed record BinkaAudioInfo(
    int Channels,
    int SampleRate,
    int SampleCount,
    int SeekEntryCount,
    int OutputFileSize)
{
    public double Seconds => SampleRate <= 0 ? 0 : SampleCount / (double)SampleRate;
}

/// <summary>一个可替换的 SoundWave 资产的现状。用于 UI 展示与"能不能替换"的判断。</summary>
public sealed record SoundWaveInfo(
    string Format,
    bool IsStreaming,
    bool IsBinka,
    bool IsCooked,
    int ChunkCount,
    int CuePointCount,
    int Channels,
    int SampleRate,
    double Seconds,
    int AudioBytes,
    int MaxChunkBytes = 0)
{
    /// <summary>可替换时为 null，否则是给人看的原因。</summary>
    public string? Problem { get; init; }

    public bool CanReplace => Problem is null;

    public string DescribeSeconds => Seconds <= 0 ? "未知" : $"{Seconds:0.##} 秒";

    public string DescribeChannels => Channels <= 0 ? "未知" : $"{Channels} 声道 / {SampleRate:N0} Hz";
}

/// <summary>替换结果：三个包内文件的新内容。</summary>
public sealed record SoundWaveReplacement(
    byte[] Uasset,
    byte[] Uexp,
    byte[] Ubulk,
    BinkaAudioInfo Audio)
{
    public double Seconds => Audio.Seconds;

    public int Channels => Audio.Channels;

    public int SampleRate => Audio.SampleRate;
}
