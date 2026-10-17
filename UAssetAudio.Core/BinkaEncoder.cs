using System.Buffers.Binary;
using System.Diagnostics;

namespace UAssetAudio.Core;

/// <summary>
/// 调 <c>binka_encode.exe</c> 把 16 位 PCM 编成 UE 用的 BINKA（ABEU 容器）。
///
/// 这个编码器是外部工具（来源见 tools/README.md），不随仓库提交：
/// 找不到时 <see cref="IsAvailable"/> 为 false，调用方据此给出可读的提示。
/// </summary>
public static class BinkaEncoder
{
    public const string ToolName = "binka_encode.exe";

    /// <summary>ABEU 文件头长度。</summary>
    public const int HeaderSize = 28;

    /// <summary>质量区间：0 最好，9 最省（编码器自己的约定）。</summary>
    public const int MinQuality = 0;
    public const int MaxQuality = 9;
    public const int DefaultQuality = 4;

    public static string? FindTool(string? toolDirectory = null) => ExternalToolLocator.Find(ToolName, toolDirectory);

    public static bool IsAvailable(string? toolDirectory = null) => FindTool(toolDirectory) is not null;

    /// <summary>把 PCM 编成 BINKA 字节（ABEU 头 + seek 表 + 数据块）。</summary>
    public static byte[] Encode(PcmAudio audio, int quality = DefaultQuality, string? toolDirectory = null)
    {
        if (audio.Pcm16.Length == 0)
            throw new InvalidDataException("音频是空的。");
        if (audio.Channels <= 0 || audio.SampleRate <= 0)
            throw new InvalidDataException("音频的声道/采样率非法。");

        var tool = FindTool(toolDirectory)
                   ?? throw new FileNotFoundException(
                       $"没找到 {ToolName}。把它放到 tools 目录（和 Prism 可执行文件同级）即可启用音频替换。");

        var temp = Path.Combine(Path.GetTempPath(), "prism-binka", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var wavPath = Path.Combine(temp, "input.wav");
        try
        {
            File.WriteAllBytes(wavPath, WavCodec.Write16(audio));

            var start = new ProcessStartInfo(tool)
            {
                WorkingDirectory = temp,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(wavPath);
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add("0"); // ABEU（UE 的容器）
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add(Math.Clamp(quality, MinQuality, MaxQuality).ToString());

            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{ToolName} 启动失败。");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // 杀不掉就算了，下面按失败处理
                }

                throw new InvalidOperationException($"{ToolName} 超时（5 分钟）。");
            }

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException($"{ToolName} 失败（退出码 {process.ExitCode}）：{FirstLine(detail)}");
            }

            var encoded = Path.ChangeExtension(wavPath, ".binka");
            if (!File.Exists(encoded))
                throw new FileNotFoundException($"{ToolName} 没有产出 .binka 文件。", encoded);

            var data = File.ReadAllBytes(encoded);
            ReadInfo(data); // 顺手校验一下头，坏文件尽早报错
            return data;
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

    /// <summary>解析 ABEU 头。</summary>
    public static BinkaAudioInfo ReadInfo(ReadOnlySpan<byte> binka)
    {
        if (binka.Length < HeaderSize + 4)
            throw new InvalidDataException("BINKA 数据太短，不像 ABEU 文件。");
        if (!binka[..4].SequenceEqual("ABEU"u8))
            throw new InvalidDataException("BINKA 数据没有 ABEU 头。");

        var version = binka[4];
        if (version != 1)
            throw new InvalidDataException($"不支持的 ABEU 版本 {version}。");

        var channels = binka[5];
        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(binka[8..]);
        var sampleCount = BinaryPrimitives.ReadInt32LittleEndian(binka[12..]);
        var outputSize = BinaryPrimitives.ReadInt32LittleEndian(binka[20..]);
        var seekEntries = BinaryPrimitives.ReadUInt16LittleEndian(binka[24..]);
        var blocksPerSeek = BinaryPrimitives.ReadUInt16LittleEndian(binka[26..]);

        if (channels <= 0 || sampleRate <= 0 || sampleCount <= 0)
            throw new InvalidDataException("BINKA 头里的声道/采样率/采样数非法。");

        return new BinkaAudioInfo(channels, sampleRate, sampleCount, seekEntries, outputSize, Math.Max(1, (int)blocksPerSeek));
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "（没有输出）";
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? text.Trim() : lines[0];
    }
}
