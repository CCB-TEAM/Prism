using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UAssetAudio.Core;

namespace Prism.Desktop.Services;

/// <summary>
/// 视频预览用的 ffmpeg 封装：探元数据、抽封面帧、按实时速度吐原始帧。
///
/// 为什么用外部 ffmpeg 而不是内置解码：Avalonia 没有视频控件，能内嵌播放的三方库
/// （libVLC）会给发布包再加一百多兆原生库；而 ffmpeg 本来就已经是音频替换的可选依赖，
/// 复用它只多几十行进程管道。
/// </summary>
public static partial class Ffmpeg
{
    public const string ToolName = "ffmpeg.exe";

    /// <summary>预览帧的最大宽度：再大只是白烧内存与带宽。</summary>
    public const int PreviewMaxWidth = 720;

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern { get; }

    [GeneratedRegex(@"Video:.*?,\s*(\d{2,5})x(\d{2,5})", RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern { get; }

    [GeneratedRegex(@"([\d.]+)\s*fps", RegexOptions.CultureInvariant)]
    private static partial Regex FpsPattern { get; }

    public static string? Find(string? hintDirectory = null) => ExternalToolLocator.Find(ToolName, hintDirectory);

    /// <summary>读容器信息（时长/分辨率/帧率）。ffmpeg 的元数据走 stderr，这里按文本解析。</summary>
    public static bool TryProbe(string path, string? hintDirectory, out VideoProbeInfo info)
    {
        info = default;
        var tool = Find(hintDirectory);
        if (tool is null)
            return false;

        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-i", Path.GetFullPath(path) })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start);
        if (process is null)
            return false;

        // 没有 -y 也没给输出文件，ffmpeg 打印完元数据就以非零码退出，这正是我们要的。
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // 杀不掉就当探测失败
            }

            return false;
        }

        var text = stderrTask.GetAwaiter().GetResult() + stdoutTask.GetAwaiter().GetResult();
        var durationMatch = DurationPattern.Match(text);
        var sizeMatch = SizePattern.Match(text);
        if (!durationMatch.Success || !sizeMatch.Success)
            return false;

        var duration = TimeSpan.FromSeconds(
            (int.Parse(durationMatch.Groups[1].Value, CultureInfo.InvariantCulture) * 3600)
            + (int.Parse(durationMatch.Groups[2].Value, CultureInfo.InvariantCulture) * 60)
            + double.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture));

        var width = int.Parse(sizeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        var height = int.Parse(sizeMatch.Groups[2].Value, CultureInfo.InvariantCulture);
        if (width <= 0 || height <= 0 || duration <= TimeSpan.Zero)
            return false;

        var fpsMatch = FpsPattern.Match(text);
        var fps = fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 25d;
        if (fps is <= 0 or > 240)
            fps = 25d;

        info = new VideoProbeInfo(duration, width, height, fps);
        return true;
    }

    /// <summary>抽一帧当封面（时间点越界时 ffmpeg 会失败，所以按播放位置给个中间值）。</summary>
    public static bool TryExtractPoster(string path, string outputPath, string? hintDirectory, TimeSpan? at = null)
    {
        var tool = Find(hintDirectory);
        if (tool is null)
            return false;

        var seconds = (at ?? TimeSpan.FromSeconds(1)).TotalSeconds;
        return Run(tool, [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", Path.GetFullPath(path),
            "-frames:v", "1",
            "-vf", $"scale='min({PreviewMaxWidth},iw)':-2",
            Path.GetFullPath(outputPath),
        ], 20000) && File.Exists(outputPath);
    }

    /// <summary>把 ffmpeg 当"视频转音频"用：抽一条 16 位 PCM WAV，给 MCI 播放器放声音。</summary>
    public static bool TryExtractAudio(string path, string outputPath, string? hintDirectory)
    {
        var tool = Find(hintDirectory);
        if (tool is null)
            return false;

        return Run(tool, [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-i", Path.GetFullPath(path),
            "-vn", "-f", "wav", "-acodec", "pcm_s16le",
            Path.GetFullPath(outputPath),
        ], 60000) && File.Exists(outputPath) && new FileInfo(outputPath).Length > 44;
    }

    /// <summary>算预览用的显示尺寸：缩到 <see cref="PreviewMaxWidth"/> 以内，高取偶数（yuv 友好）。</summary>
    public static (int Width, int Height) ScaleToPreview(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return (0, 0);

        var scale = width > PreviewMaxWidth ? PreviewMaxWidth / (double)width : 1d;
        var scaledWidth = Math.Max(2, (int)Math.Round(width * scale));
        var scaledHeight = Math.Max(2, (int)Math.Round(height * scale));
        scaledWidth &= ~1;
        scaledHeight &= ~1;
        return (scaledWidth, scaledHeight);
    }

    private static bool Run(string tool, string[] arguments, int timeoutMs)
    {
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start);
        if (process is null)
            return false;

        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // 超时就当失败
            }

            return false;
        }

        _ = stderr.GetAwaiter().GetResult();
        _ = stdout.GetAwaiter().GetResult();
        return process.ExitCode == 0;
    }
}

/// <summary>容器信息。</summary>
public readonly record struct VideoProbeInfo(TimeSpan Duration, int Width, int Height, double FrameRate)
{
    public double Seconds => Duration.TotalSeconds;

    public string DescribeSize => $"{Width}×{Height}";

    public string DescribeDuration => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss\.f");
}
