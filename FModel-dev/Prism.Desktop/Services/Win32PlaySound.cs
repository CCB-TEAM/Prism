using System.Runtime.InteropServices;

namespace Prism.Desktop.Services;

/// <summary>
/// 通过 Win32 PlaySound 播放/停止 WAV 音频（仅 Windows；其他平台自动降级为无操作，避免 DllNotFound）。
/// </summary>
public static class Win32PlaySound
{
    private const uint SndFilename = 0x00020000; // SND_FILENAME
    private const uint SndAsync = 0x0001;        // SND_ASYNC
    private const uint SndPurge = 0x0040;        // SND_PURGE

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    public static void PlayFile(string wavPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        PlaySound(wavPath, IntPtr.Zero, SndFilename | SndAsync);
    }

    public static void Stop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        PlaySound(null, IntPtr.Zero, SndPurge);
    }
}
