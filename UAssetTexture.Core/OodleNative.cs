using System.Runtime.InteropServices;

namespace UAssetTexture.Core;

/// <summary>
/// Oodle 原生库（Windows 上是 <c>oo2core_9_win64.dll</c>）的可用性探测。
///
/// <b>为什么必须在调用前探测</b>：repak_bind 是 Rust 库，找不到 Oodle 时它会 panic，
/// 而 Rust 的 panic 会 abort 整个进程 —— Windows 上表现为 <c>0xC0000409</c>，
/// 进程直接消失，.NET 侧 <c>try/catch</c>、<c>AppDomain.UnhandledException</c> 全都拦不住。
/// 用户看到的就是"点一下闪退"。
///
/// 所以唯一可行的做法是：请求 Oodle 压缩之前先探测，探测不到就退回不压缩。
/// </summary>
public static class OodleNative
{
    private static readonly Lazy<bool> LazyAvailable = new(Probe, isThreadSafe: true);

    /// <summary>Oodle 原生库是否可加载。首次调用会真正尝试加载一次并立即释放。</summary>
    public static bool IsAvailable => LazyAvailable.Value;

    /// <summary>缺失时给用户看的说明（用于日志与界面提示）。</summary>
    public const string MissingHint =
        "未找到 Oodle 原生库（oo2core_9_win64.dll），本次按不压缩打包。" +
        "发行包不包含该库（再分发受 RAD/Epic 授权约束）；" +
        "需要 Oodle 压缩时把它放到主程序旁边即可。";

    private static bool Probe()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["oo2core_9_win64.dll", "oo2core_9_win64", "oo2core_8_win64.dll"]
            : ["liboo2corelinux64.so.9", "liboo2corelinux64.so"];

        foreach (string name in candidates)
        {
            if (NativeLibrary.TryLoad(name, out nint handle))
            {
                // 只是探测：加载成功就立刻释放，避免长期占用句柄。
                NativeLibrary.Free(handle);
                return true;
            }
        }

        return false;
    }
}
