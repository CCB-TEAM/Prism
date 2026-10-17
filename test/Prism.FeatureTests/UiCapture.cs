using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Prism.Desktop.ViewModels;
using Prism.Desktop.Views;

namespace Prism.FeatureTests;

/// <summary>
/// UI 截图：把每个页面真正光栅化成 PNG，用于审美评审与改版前后对比。
///
/// 为什么单独一条路：常规测试用 <c>UseHeadlessDrawing = true</c>（不绘制，只做
/// 布局与逻辑断言，快且无 Skia 依赖）。截图必须关掉它并挂上 Skia 才会真正出像素。
/// 两种模式不能在同一进程共存，所以用命令行开关区分，并且必须在任何 Avalonia
/// 初始化之前决定 —— 因此入口在 Program.cs 最前面就分流了。
///
/// 用法：dotnet run --project test/Prism.FeatureTests -- --capture &lt;输出目录&gt;
/// </summary>
internal static class UiCapture
{
    /// <summary>渲染所有页面，返回进程退出码。</summary>
    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        AppBuilder.Configure<Prism.Desktop.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        (string Label, double Width, double Height)[] viewports =
        [
            ("compact", 430, 900),
            ("wide", 1440, 900),
        ];

        (string Label, string Navigate, int Tab)[] pages =
        [
            ("home", "GoHome", 0),
            ("workspace-config", "GoWorkspace", 0),
            ("workspace-browse", "GoWorkspace", 1),
            ("merge", "GoMerge", 0),
            ("convert", "GoConvert", 0),
            ("settings", "GoSettings", 0),
        ];

        int written = 0;
        foreach ((string viewportLabel, double width, double height) in viewports)
        {
            foreach (string theme in new[] { "light", "dark" })
            {
                (MainViewModel vm, MainWindow window) = CreateShell(width, height, theme);

                foreach ((string pageLabel, string command, int tab) in pages)
                {
                    Navigate(vm, command, tab);
                    string name = $"{theme}-{viewportLabel}-{pageLabel}.png";
                    if (Capture(window, Path.Combine(outputDirectory, name)))
                    {
                        written++;
                        Console.WriteLine($"  {name}");
                    }
                }

                // 浮层单独截一张（它覆盖在页面之上，且是各页共用的同一个实现）。
                Navigate(vm, "GoConvert", 0);
                vm.OpenHelpCommand.Execute("convert");
                if (Capture(window, Path.Combine(outputDirectory, $"{theme}-{viewportLabel}-help.png")))
                {
                    written++;
                    Console.WriteLine($"  {theme}-{viewportLabel}-help.png");
                }

                window.Close();
            }
        }

        Console.WriteLine($"共写出 {written} 张截图到 {outputDirectory}");
        RestoreSettings();
        return 0;
    }

    /// <summary>
    /// 把截图过程改过的持久化设置还原掉。
    ///
    /// 截图要关掉过渡动画（否则截到淡入中间态），而 <c>IsAnimationsEnabled</c> 是写进
    /// 设置文件的真设置 —— 不还原的话，紧接着跑常规测试就会读到"动画默认关闭"，
    /// 让那条断言莫名其妙地失败。窗口尺寸同理。
    /// </summary>
    private static void RestoreSettings()
    {
        string settingsPath = Path.Combine(AppContext.BaseDirectory, "Prism.Desktop.settings.json");
        try
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
                Console.WriteLine("已清除截图过程写入的设置文件");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  !! 设置文件清理失败（不影响截图）：{ex.Message}");
        }
    }

    private static (MainViewModel Vm, MainWindow Window) CreateShell(double width, double height, string theme)
    {
        var vm = new MainViewModel
        {
            // 关掉过渡动画：截到淡入中间态会得到半透明页面，评审时看不出真实观感。
            // 该属性的 setter 会写设置文件，但那是测试输出目录里的副本，不影响应用。
            IsAnimationsEnabled = false,
            WindowWidth = width,
            WindowHeight = height,
        };

        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        var window = new MainWindow
        {
            DataContext = vm,
            Width = width,
            Height = height,
        };

        // 关掉次像素（ClearType）抗锯齿：真实屏幕上眼睛会把 RGB 条纹合成为清晰文字，
        // 但只要把画面存成 PNG 放大看，字形边缘就会出现彩色重影，评审时会被误当成
        // "文字画了两遍"。灰度抗锯齿更接近截图该有的样子。
        RenderOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);

        window.Show();
        Pump();
        return (vm, window);
    }

    private static void Navigate(MainViewModel vm, string command, int tab)
    {
        vm.CurrentTabIndex = tab;

        switch (command)
        {
            case "GoHome": vm.GoHomeCommand.Execute(null); break;
            case "GoWorkspace": vm.GoWorkspaceCommand.Execute(null); break;
            case "GoMerge": vm.GoMergeCommand.Execute(null); break;
            case "GoConvert": vm.GoConvertCommand.Execute(null); break;
            case "GoSettings": vm.GoSettingsCommand.Execute(null); break;
        }

        Pump();
    }

    private static bool Capture(MainWindow window, string path)
    {
        // 抓两遍取第二帧：第一帧可能是在竖直滚动条刚出现、内容宽度因此变动的那一次
        // 布局之前合成的，会留下上一版位置的残影（设置页尤其明显）。第二帧是稳定态。
        Pump();
        _ = window.CaptureRenderedFrame();

        Pump();
        var frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            Console.Error.WriteLine($"  !! 渲染失败：{path}");
            return false;
        }

        frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return true;
    }

    /// <summary>把排队的布局/绑定/合成任务跑完（headless 下没有消息循环自动驱动）。</summary>
    private static void Pump()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }
}
