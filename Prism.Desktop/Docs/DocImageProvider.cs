using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Prism.Desktop.Docs;

/// <summary>
/// 文档截图的取用处：<c>Assets/help/{light|dark}/{key}.png</c>。
///
/// 为什么准备两套：文档页面本身跟主题走，浅色截图压在深色底上会像一块白板
/// （原来的使用说明浮层就是这个毛病 —— 深色主题里嵌一张读者屏幕一样白的图）。
/// 这些 PNG 由 <c>test/Prism.FeatureTests --capture --docs</c> 从真实页面上截，
/// 所以文档里的界面永远和当前版本一致，不会出现"图上有个已经不存在的按钮"。
///
/// 一张图只解码一次（Bitmap 不便宜），按 主题+键 缓存。
/// </summary>
public static class DocImageProvider
{
    private static readonly Dictionary<string, IImage?> Cache = new(StringComparer.Ordinal);

    /// <summary>文档里用到的全部图片键（测试据此断言资源齐全）。</summary>
    public static IReadOnlyList<string> KnownKeys { get; } =
    [
        "welcome",
        "workspace-config",
        "workspace-browse",
        "workspace-replace",
        "merge",
        "convert",
        "settings",
        "update",
    ];

    /// <summary>取一张文档截图；资源不存在时返回 null（视图会只显示图注，不崩）。</summary>
    public static IImage? Get(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        string theme = CurrentThemeName();
        string cacheKey = theme + "/" + key;
        if (Cache.TryGetValue(cacheKey, out IImage? cached))
        {
            return cached;
        }

        IImage? image = Load(theme, key);
        Cache[cacheKey] = image;
        return image;
    }

    /// <summary>主题变了之后清缓存：下次取图会重新解码另一套。</summary>
    public static void ClearCache() => Cache.Clear();

    private static IImage? Load(string theme, string key)
    {
        var uri = new Uri($"avares://Prism.Desktop/Assets/help/{theme}/{key}.png");
        try
        {
            if (!AssetLoader.Exists(uri))
            {
                return null;
            }

            using Stream stream = AssetLoader.Open(uri);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static string CurrentThemeName()
    {
        ThemeVariant? variant = Application.Current?.ActualThemeVariant;
        return variant == ThemeVariant.Dark ? "dark" : "light";
    }
}
