using System.Diagnostics;

namespace UAssetAudio.Core;

/// <summary>素材容器的实际类型。按文件头判断，扩展名只当参考。</summary>
public enum AudioContainer
{
    Unknown,
    Wav,
    Mp3,
    Ogg,
    Flac,
    Mp4,
    Aac,
    Wma,
    Aiff,
    Other,
}

/// <summary>
/// 把玩家的音频素材解成 16 位 PCM：WAV 自带解析，MP3 / OGG 用纯托管解码器。
///
/// 其它格式分两条路：Android 交给系统 <c>MediaCodec</c>（<see cref="PlatformDecoder"/>，
/// 由 Prism.Desktop.Android 注入），桌面上有 <c>ffmpeg.exe</c> 时交给它。
///
/// 格式判断**以文件头为准**：手机 SAF 选出来的文件常常没有扩展名（缓存文件是一串哈希），
/// 只按扩展名会把它当成"未知格式"，进而误报「需要 ffmpeg」。
/// </summary>
public static class AudioSourceDecoder
{
    /// <summary>能直接解码的扩展名（不含依赖 ffmpeg 的）。</summary>
    public static readonly string[] DirectExtensions = [".wav", ".wave", ".mp3", ".ogg", ".oga"];

    /// <summary>依赖 ffmpeg 或平台解码器的扩展名。</summary>
    public static readonly string[] FfmpegExtensions = [".flac", ".m4a", ".aac", ".wma", ".opus", ".aiff", ".aif", ".wem"];

    public static readonly string[] AllExtensions = [.. DirectExtensions, .. FfmpegExtensions];

    /// <summary>UI 里提示用的说明文字。</summary>
    public const string SupportedHint = "支持 WAV / MP3 / OGG；有 ffmpeg（手机上用系统解码器）还能吃 FLAC / M4A 等";

    /// <summary>
    /// 平台原生解码器（Android 注入 MediaCodec 实现）。解不了时返回 null 或抛异常，
    /// 调用方会继续往后面的兜底走。
    /// </summary>
    public static Func<string, PcmAudio?>? PlatformDecoder { get; set; }

    public static PcmAudio Decode(string path, string? toolDirectory = null)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("找不到音频文件。", full);

        return DetectContainer(full) switch
        {
            AudioContainer.Wav => WavCodec.Read(File.ReadAllBytes(full)),
            AudioContainer.Mp3 => DecodeMp3(full),
            AudioContainer.Ogg => DecodeOgg(full),
            var other => DecodeWithBackup(full, toolDirectory, other),
        };
    }

    /// <summary>按文件头判断容器类型；认不出来再退回扩展名。</summary>
    public static AudioContainer DetectContainer(string path)
    {
        AudioContainer byMagic = ReadMagic(path);
        if (byMagic != AudioContainer.Unknown)
            return byMagic;

        return ContainerFromExtension(Path.GetExtension(path));
    }

    /// <summary>给提示文字用的名字。</summary>
    public static string DescribeContainer(AudioContainer container) => container switch    {
        AudioContainer.Wav => "WAV 音频",
        AudioContainer.Mp3 => "MP3 音频",
        AudioContainer.Ogg => "OGG 音频",
        AudioContainer.Flac => "FLAC 音频",
        AudioContainer.Mp4 => "M4A / MP4 容器",
        AudioContainer.Aac => "AAC 音频",
        AudioContainer.Wma => "WMA 音频",
        AudioContainer.Aiff => "AIFF 音频",
        AudioContainer.Other => "这个文件",
        _ => "这个文件",
    };

    /// <summary>按文件头猜一个扩展名（猜不出返回空字符串），用于给没有扩展名的素材补名字。</summary>
    public static string SuggestExtension(string path) => DetectContainer(path) switch
    {
        AudioContainer.Wav => ".wav",
        AudioContainer.Mp3 => ".mp3",
        AudioContainer.Ogg => ".ogg",
        AudioContainer.Flac => ".flac",
        AudioContainer.Mp4 => ".m4a",
        AudioContainer.Aac => ".aac",
        AudioContainer.Aiff => ".aiff",
        AudioContainer.Wma => ".wma",
        _ => string.Empty,
    };

    private static AudioContainer ContainerFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".wav" or ".wave" => AudioContainer.Wav,
        ".mp3" => AudioContainer.Mp3,
        ".ogg" or ".oga" => AudioContainer.Ogg,
        ".flac" => AudioContainer.Flac,
        ".m4a" or ".mp4" or ".m4b" => AudioContainer.Mp4,
        ".aac" => AudioContainer.Aac,
        ".wma" => AudioContainer.Wma,
        ".aiff" or ".aif" or ".aifc" => AudioContainer.Aiff,
        _ => AudioContainer.Unknown,
    };

    private static AudioContainer ReadMagic(string path)
    {
        Span<byte> head = stackalloc byte[16];
        int read;
        try
        {
            using var stream = File.OpenRead(path);
            read = stream.Read(head);
        }
        catch
        {
            return AudioContainer.Unknown;
        }

        if (read < 4)
            return AudioContainer.Unknown;

        // RIFF 家族
        if (head[..4].SequenceEqual("RIFF"u8))
        {
            if (read >= 12 && head[8..12].SequenceEqual("WAVE"u8))
                return AudioContainer.Wav;
            if (read >= 12 && head[8..12].SequenceEqual("AVI "u8))
                return AudioContainer.Other;
            return AudioContainer.Unknown;
        }

        if (head[..4].SequenceEqual("OggS"u8))
            return AudioContainer.Ogg;
        if (head[..4].SequenceEqual("fLaC"u8))
            return AudioContainer.Flac;
        if (read >= 8 && head[4..8].SequenceEqual("ftyp"u8))
            return AudioContainer.Mp4;
        if (head[..4].SequenceEqual("FORM"u8) && read >= 12 &&
            (head[8..12].SequenceEqual("AIFF"u8) || head[8..12].SequenceEqual("AIFC"u8)))
            return AudioContainer.Aiff;
        if (head[..3].SequenceEqual("ID3"u8))
            return AudioContainer.Mp3;

        // MPEG 音频 / ADTS AAC 都从 0xFF 同步字开始，靠 layer 位区分
        if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)
        {
            var layer = (head[1] >> 1) & 0x03;
            return layer == 0 ? AudioContainer.Aac : AudioContainer.Mp3;
        }

        return AudioContainer.Unknown;
    }

    private static PcmAudio DecodeWithBackup(string path, string? toolDirectory, AudioContainer container)
    {
        // 手机上没有 ffmpeg，先问系统自带的解码器（m4a / flac / aac 等）
        var platform = PlatformDecoder;
        if (platform is not null && container is not (AudioContainer.Other or AudioContainer.Unknown))
        {
            try
            {
                PcmAudio? decoded = platform(path);
                if (decoded is { Pcm16.Length: > 0 })
                    return decoded;
            }
            catch (Exception)
            {
                // 平台解码失败就继续往下走，让最终的提示说明清楚
            }
        }

        return DecodeWithFfmpeg(path, toolDirectory, container);
    }

    private static PcmAudio DecodeWithFfmpeg(string path, string? toolDirectory, AudioContainer container)
    {
        var ffmpeg = ExternalToolLocator.Find("ffmpeg.exe", toolDirectory);
        if (ffmpeg is null)
        {
            var what = DescribeContainer(container);
            if (OperatingSystem.IsAndroid())
            {
                throw new NotSupportedException(
                    $"{what} 手机端解不了：内置解码器只能解 WAV / MP3 / OGG。请先在转换工具里转成这三种格式再选。");
            }

            throw new NotSupportedException(
                $"{what} 需要 ffmpeg 才能解码。把 ffmpeg.exe 放进 tools 目录，或先把素材转成 WAV / MP3 / OGG。");
        }

        var temp = Path.Combine(Path.GetTempPath(), "prism-audio-src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var wavPath = Path.Combine(temp, "decoded.wav");
        try
        {
            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", Path.GetFullPath(path), "-vn", "-f", "wav", "-acodec", "pcm_s16le", wavPath })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg 启动失败。");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(TimeSpan.FromMinutes(2));
            if (process.ExitCode != 0 || !File.Exists(wavPath))
            {
                throw new InvalidDataException(
                    $"ffmpeg 没能把这个文件解成 PCM：{FirstLine(error)}");
            }

            return WavCodec.Read(File.ReadAllBytes(wavPath));
        }
        finally
        {
            try
            {
                Directory.Delete(temp, true);
            }
            catch
            {
                // 临时目录清不掉只是脏盘，不影响结果
            }
        }
    }

    private static PcmAudio DecodeMp3(string path)
    {
        using var file = new NLayer.MpegFile(path);
        var channels = file.Channels;
        var sampleRate = file.SampleRate;
        if (channels <= 0 || sampleRate <= 0)
            throw new InvalidDataException("MP3 头解析失败（文件可能损坏或不是 MP3）。");

        using var pcm = new MemoryStream();
        var buffer = new float[channels * 4096];
        int read;
        while ((read = file.ReadSamples(buffer, 0, buffer.Length)) > 0)
            FloatToPcm16(buffer.AsSpan(0, read), pcm);

        if (pcm.Length == 0)
            throw new InvalidDataException("MP3 里没有解出任何采样。");

        return new PcmAudio(pcm.ToArray(), sampleRate, channels);
    }

    private static PcmAudio DecodeOgg(string path)
    {
        using var reader = new NVorbis.VorbisReader(path);
        var channels = reader.Channels;
        var sampleRate = reader.SampleRate;
        if (channels <= 0 || sampleRate <= 0)
            throw new InvalidDataException("OGG 头解析失败（文件可能不是 Vorbis 编码）。");

        using var pcm = new MemoryStream();
        var buffer = new float[channels * 4096];
        int read;
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
            FloatToPcm16(buffer.AsSpan(0, read), pcm);

        if (pcm.Length == 0)
            throw new InvalidDataException("OGG 里没有解出任何采样。");

        return new PcmAudio(pcm.ToArray(), sampleRate, channels);
    }

    private static void FloatToPcm16(ReadOnlySpan<float> samples, Stream output)
    {
        Span<byte> chunk = stackalloc byte[8192];
        var written = 0;
        foreach (var sample in samples)
        {
            var scaled = (short)Math.Clamp(Math.Round(sample * short.MaxValue), short.MinValue, short.MaxValue);
            chunk[written++] = (byte)scaled;
            chunk[written++] = (byte)(scaled >> 8);
            if (written == chunk.Length)
            {
                output.Write(chunk);
                written = 0;
            }
        }

        if (written > 0)
            output.Write(chunk[..written]);
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "（ffmpeg 没有输出）";
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return line.Length == 0 ? text.Trim() : line[^1];
    }
}
