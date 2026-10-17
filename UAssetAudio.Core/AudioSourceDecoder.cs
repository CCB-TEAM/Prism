using System.Diagnostics;

namespace UAssetAudio.Core;

/// <summary>
/// 把玩家的音频素材解成 16 位 PCM：WAV 自带解析，MP3 / OGG 用纯托管解码器，
/// 其它格式（flac / m4a / wma…）在机器上有 <c>ffmpeg.exe</c> 时交给它。
/// </summary>
public static class AudioSourceDecoder
{
    /// <summary>能直接解码的扩展名（不含依赖 ffmpeg 的）。</summary>
    public static readonly string[] DirectExtensions = [".wav", ".wave", ".mp3", ".ogg", ".oga"];

    /// <summary>依赖 ffmpeg 的扩展名。</summary>
    public static readonly string[] FfmpegExtensions = [".flac", ".m4a", ".aac", ".wma", ".opus", ".aiff", ".aif", ".wem"];

    public static readonly string[] AllExtensions = [.. DirectExtensions, .. FfmpegExtensions];

    /// <summary>UI 里提示用的说明文字。</summary>
    public const string SupportedHint = "支持 WAV / MP3 / OGG；装了 ffmpeg 还能吃 FLAC / M4A 等";

    public static PcmAudio Decode(string path, string? toolDirectory = null)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("找不到音频文件。", full);

        var extension = Path.GetExtension(full).ToLowerInvariant();
        return extension switch
        {
            ".wav" or ".wave" => WavCodec.Read(File.ReadAllBytes(full)),
            ".mp3" => DecodeMp3(full),
            ".ogg" or ".oga" => DecodeOgg(full),
            _ => DecodeWithFfmpeg(full, toolDirectory),
        };
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

    private static PcmAudio DecodeWithFfmpeg(string path, string? toolDirectory)
    {
        var ffmpeg = ExternalToolLocator.Find("ffmpeg.exe", toolDirectory);
        if (ffmpeg is null)
        {
            throw new NotSupportedException(
                $"{Path.GetExtension(path)} 需要 ffmpeg 才能解码。把 ffmpeg.exe 放进 tools 目录，或先把素材转成 WAV / MP3 / OGG。");
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
