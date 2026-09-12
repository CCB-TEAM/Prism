using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Prism.Desktop.ViewModels;
using Prism.Desktop.Views;

namespace Prism.FeatureTests;

/// <summary>
/// UI 层验证：用 headless 后端实例化真实窗口与全部视图。
///
/// 为什么需要它：Avalonia 的 XAML 编译只检查属性名与绑定路径；
/// 样式选择器、控件模板、资源引用、附加属性等问题只有真正
/// Measure/Arrange（模板展开）时才会暴露。这个 fixture 把这一步
/// 放进可自动化、可退出（不残留原生循环）的测试里。
/// </summary>
internal static class UiFixture
{
    private static bool _initialized;

    /// <summary>初始化 headless Avalonia（幂等）。</summary>
    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        AppBuilder.Configure<Prism.Desktop.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
            .WithInterFont()
            .SetupWithoutStarting();

        _initialized = true;
    }

    /// <summary>
    /// 构造主窗口 + 视图模型并强制模板展开。
    /// 返回窗口与视图模型，供后续断言使用。
    /// </summary>
    public static (MainWindow Window, MainViewModel ViewModel) CreateShell()
    {
        EnsureInitialized();

        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };

        window.Measure(new Size(1280, 900));
        window.Arrange(new Rect(0, 0, 1280, 900));

        return (window, vm);
    }

    /// <summary>把待处理的 UI 任务跑完（动画/绑定更新都靠它推进）。</summary>
    public static void RunJobs()
    {
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>在可视树里按类型找第一个后代控件（测试断言用）。</summary>
    public static T? FindDescendant<T>(Visual root) where T : Visual
    {
        foreach (Visual child in root.GetVisualChildren())
        {
            if (child is T match)
            {
                return match;
            }

            T? deeper = FindDescendant<T>(child);
            if (deeper is not null)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>收集可视树中的控件类型名（诊断用）。</summary>
    public static void CollectTypes(Visual root, List<string> seen)
    {
        foreach (Visual child in root.GetVisualChildren())
        {
            string name = child.GetType().Name;
            if (!seen.Contains(name))
            {
                seen.Add(name);
            }

            CollectTypes(child, seen);
        }
    }

    /// <summary>
    /// 与 FileBrowserView 中 .browser-list 淡入动画等价的样式。
    /// 单独构造列表来验证"动画终态不透明度为 1"，
    /// 因为分离的 UserControl 在 headless 下不会应用模板、拿不到内部视觉树。
    /// </summary>
    public static Avalonia.Styling.Style BrowserListFadeStyle() =>
        new(selector => selector.OfType<ListBox>().Class("browser-list"))
        {
            Animations =
            {
                new Animation
                {
                    Duration = TimeSpan.FromMilliseconds(180),
                    FillMode = FillMode.Forward,
                    Children =
                    {
                        new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
                        new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
                    },
                },
            },
        };
}
