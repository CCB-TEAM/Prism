using System.Runtime.InteropServices;
using System.Text;

namespace Prism.Desktop.Services;

/// <summary>
/// Windows 上的音频播放器：直接走 MCI（winmm）的 waveaudio 设备。
///
/// 为什么不用原来的 <c>PlaySound</c>：它只能"放"和"停"，拿不到播放位置，
/// 所以桌面上那条进度条一直是空的、也不能拖动。MCI 支持
/// play / pause / resume / seek / status position|length，
/// 一套接口就能把播放器该有的交互补齐，而且不需要再引入播放库。
/// </summary>
public sealed class WindowsAudioPlayer : IDisposable
{
    private const string Alias = "prismpreview";

    private bool _open;

    /// <summary>只在 Windows 上可用；其它平台由调用方降级。</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    public bool IsOpen => _open;

    /// <summary>打开一个 WAV（MCI 的 waveaudio 设备只吃 PCM WAV，正好是我们解出来的格式）。</summary>
    public bool Open(string wavPath)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
            return false;

        Close();

        // MCI 的命令串用双引号包路径；路径里的引号会破坏解析，直接拒绝。
        if (wavPath.Contains('"'))
            return false;

        var open = $"open \"{wavPath}\" type waveaudio alias {Alias}";
        _open = Send(open, out _) == 0;
        if (!_open)
            return false;

        // 设了 time format 才能用毫秒做单位查询/定位
        Send($"set {Alias} time format milliseconds", out _);
        return true;
    }

    public void Play()
    {
        if (_open)
            Send($"play {Alias}", out _);
    }

    public void Pause()
    {
        if (_open)
            Send($"pause {Alias}", out _);
    }

    public void Resume()
    {
        if (_open)
            Send($"resume {Alias}", out _);
    }

    /// <summary>回到开头并停止。</summary>
    public void StopAndRewind()
    {
        if (!_open)
            return;

        Send($"stop {Alias}", out _);
        Send($"seek {Alias} to start", out _);
    }

    public void Seek(long milliseconds)
    {
        if (_open)
            Send($"seek {Alias} to {Math.Max(0, milliseconds)}", out _);
    }

    public void Close()
    {
        if (!_open)
            return;

        Send($"stop {Alias}", out _);
        Send($"close {Alias}", out _);
        _open = false;
    }

    /// <summary>当前播放位置（毫秒）；拿不到时返回 -1。</summary>
    public long PositionMs => QueryNumber($"status {Alias} position");

    /// <summary>总长度（毫秒）；拿不到时返回 -1。</summary>
    public long LengthMs => QueryNumber($"status {Alias} length");

    /// <summary>MCI 报告的播放状态：playing / paused / stopped。</summary>
    public string Mode
    {
        get
        {
            if (!_open)
                return "closed";

            return Send($"status {Alias} mode", out var value) == 0 ? value.ToLowerInvariant() : "closed";
        }
    }

    public void Dispose() => Close();

    private long QueryNumber(string command)
    {
        if (!_open)
            return -1;

        return Send(command, out var value) == 0 && long.TryParse(value, out var parsed) ? parsed : -1;
    }

    private static int Send(string command, out string result)
    {
        var buffer = new StringBuilder(128);
        var code = mciSendStringW(command, buffer, buffer.Capacity, IntPtr.Zero);
        result = buffer.ToString();
        return code;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendStringW(string command, StringBuilder? returnString, int returnLength, IntPtr hwndCallback);
}
