using Avalonia;
using Avalonia.Controls;

namespace Prism.Desktop.Services;

/// <summary>
/// 把"界面密度"写成应用级字号资源，供 XAML 用 <c>{DynamicResource FsXX}</c> 取值。
///
/// <b>为什么只放大文字</b>：曾经试过对整个界面做 <c>LayoutTransform</c> 等比缩放，
/// 结果内边距、控件、行高一起变大，看起来像"把手机界面拉大"，而不是桌面版 ——
/// 用户的原话是"单纯放大怪怪的"。所以这里只改字号：文字变大变清晰，
/// 间距与控件尺寸保持原样，视觉上才像正经的桌面界面。
///
/// 紧凑档的值与原来的写法完全一致，因此手机与窄窗的排版一个像素都不变。
/// </summary>
public static class LayoutDensity
{
    /// <summary>(资源键, 紧凑值, 宽屏值)。宽屏值≈紧凑值 ×1.2，取整到常用字号。</summary>
    private static readonly (string Key, double Compact, double Wide)[] FontSizes =
    [
        ("Fs8", 8d, 10d),
        ("Fs10", 10d, 12d),
        ("Fs11", 11d, 13d),
        ("Fs12", 12d, 14d),
        ("Fs10_5", 10.5d, 12.5d),
        ("Fs11_5", 11.5d, 14d),
        ("Fs12_5", 12.5d, 15d),
        ("Fs13_5", 13.5d, 16d),
        ("Fs13", 13d, 16d),
        ("Fs14", 14d, 17d),
        ("Fs15", 15d, 18d),
        ("Fs16", 16d, 19d),
        ("Fs17", 17d, 20d),
        ("Fs18", 18d, 22d),
        ("Fs20", 20d, 24d),
        ("Fs22", 22d, 26d),
        ("Fs25", 25d, 30d),
        ("Fs40", 40d, 48d),
    ];

    /// <summary>
    /// 应用密度。<paramref name="wide"/> 为 true 时把所有字号资源改成宽屏值，
    /// 否则恢复紧凑值。写入 Application.Resources 会触发 DynamicResource 重新求值，
    /// 所以界面会立即更新。
    /// </summary>
    public static void Apply(bool wide)
    {
        IResourceDictionary? resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        foreach ((string key, double compact, double wideValue) in FontSizes)
        {
            resources[key] = wide ? wideValue : compact;
        }
    }
}
