using System.Diagnostics;
using System.Globalization;

namespace Prism.Desktop.Services;

/// <summary>
/// 用 ffmpeg 的裸帧管道做视频预览播放。
///
/// 做法：<c>ffmpeg -re -i in.mp4 -f rawvideo -pix_fmt bgra -an -</c>，
/// 每次从 stdout 读一帧 <c>W×H×4</c> 字节，交给上层贴到 WriteableBitmap 上。
/// <c>-re</c> 让 ffmpeg 按源帧率吐帧，于是"读到一帧就显示一帧"天然就是实时速度，
/// 不需要自己做时钟同步。
///
/// 只做画面：音轨要另外抽出来交给播放器（见 <see cref="Ffmpeg.TryExtractAudio"/>），
/// 视频预览默认静音。
/// </summary>
public sealed class FfmpegVideoPlayer : IDisposable
{
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _pump;

    /// <summary>一帧到位（BGRA，行优先，无 padding）。<b>背景线程</b>回调。</summary>
    public event Action<byte[]>? Frame;

    /// <summary>播完了或流结束（背景线程回调）。</summary>
    public event Action? Finished;

    public int FrameWidth { get; private set; }

    public int FrameHeight { get; private set; }

    public bool IsRunning => _pump is { IsCompleted: false };

    /// <summary>从 <paramref name="startSeconds"/> 开始播放。<paramref name="width"/>/<paramref name="height"/> 是解码目标尺寸。</summary>
    public bool Start(string path, int width, int height, double startSeconds, string? toolDirectory, out string error)
    {
        error = string.Empty;
        Stop();

        var tool = Ffmpeg.Find(toolDirectory);
        if (tool is null)
        {
            error = $"没找到 {Ffmpeg.ToolName}，视频预览不可用。";
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            error = "视频尺寸未知，无法播放。";
            return false;
        }

        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = null,
        };

        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-re" })
        {
            start.ArgumentList.Add(argument);
        }

        if (startSeconds > 0.05)
        {
            start.ArgumentList.Add("-ss");
            start.ArgumentList.Add(startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        foreach (var argument in new[]
                 {
                     "-i", Path.GetFullPath(path),
                     "-vf", $"scale={width.ToString(CultureInfo.InvariantCulture)}:{height.ToString(CultureInfo.InvariantCulture)}",
                     "-pix_fmt", "bgra",
                     "-an", "-sn", "-dn",
                     "-f", "rawvideo",
                     "pipe:1",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            _process = Process.Start(start);
        }
        catch (Exception ex)
        {
            error = $"ffmpeg 启动失败：{ex.Message}";
            return false;
        }

        if (_process is null)
        {
            error = "ffmpeg 启动失败。";
            return false;
        }

        FrameWidth = width;
        FrameHeight = height;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var frameBytes = width * height * 4;
        var stream = _process.StandardOutput.BaseStream;
        _ = _process.StandardError.ReadToEndAsync(token); // 别让 stderr 堵住管道

        _pump = Task.Run(async () =>
        {
            var buffer = new byte[frameBytes];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var filled = 0;
                    while (filled < frameBytes)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(filled, frameBytes - filled), token).ConfigureAwait(false);
                        if (read <= 0)
                            break;
                        filled += read;
                    }

                    if (filled < frameBytes)
                        break;

                    Frame?.Invoke(buffer);
                    buffer = new byte[frameBytes];
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
            catch
            {
                // 解码中断（文件损坏、进程被杀）都按"播完"处理，上层据此复位
            }
            finally
            {
                Finished?.Invoke();
            }
        }, token);

        return true;
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 已经取消过
        }

        try
        {
            if (_process is { HasExited: false })
                _process.Kill(true);
        }
        catch
        {
            // 进程可能刚好自己退出
        }

        try
        {
            _process?.StandardOutput.BaseStream.Dispose();
            _process?.Dispose();
        }
        catch
        {
            // 释放失败不影响下次播放
        }

        _process = null;
        _cts?.Dispose();
        _cts = null;
        _pump = null;
    }

    public void Dispose() => Stop();
}
