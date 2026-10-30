using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.VisualTree;
using PakTool.Core;
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
/// 用法：
///   dotnet run --project test/Prism.FeatureTests -- --capture &lt;输出目录&gt;
///   dotnet run --project test/Prism.FeatureTests -- --capture &lt;忽略&gt; --docs
///
/// <c>--docs</c> 是另一个用途：把「使用说明」里要用的截图按 light / dark 两套写进
/// Prism.Desktop/Assets/help/ 下。文档里的图因此永远来自当前版本的真实界面，
/// 不会出现"图上有个已经不存在的按钮"。
/// </summary>
internal static class UiCapture
{
    /// <summary>渲染所有页面（评审用），或把文档截图写进资源目录。</summary>
    public static int Run(string outputDirectory, bool docsOnly = false)
    {
        AppBuilder.Configure<Prism.Desktop.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

return docsOnly ? CaptureDocs() : CapturePages(outputDirectory);
    }

    // ══════════════════════════════════════════════════════════════════
    //  文档截图（--docs）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 文档用的页面截图。视口取 1240×780：跨过 1200 的宽屏阈值，截到的是桌面版排版；
    /// 于是截到的是桌面版排版（字号放大一档），而宽度又不至于塞不进文档正文列。
    /// </summary>
    private static int CaptureDocs()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine("!! 找不到仓库根（找不到 Prism.Desktop 目录），文档截图未生成");
            return 1;
        }

        string assetsRoot = Path.Combine(repoRoot, "Prism.Desktop", "Assets", "help");

        (string Key, string Command, int Tab, Action<MainViewModel>? Seed)[] shots =
        [
            ("welcome", "GoHome", 0, null),
            ("workspace-config", "GoWorkspace", 0, null),
            ("workspace-browse", "GoWorkspace", 1, vm => SeedPreviewDemo(vm, video: false)),
            ("workspace-replace", "GoWorkspace", 2, vm => SeedPatchDemo(vm, selectMedia: false)),
            ("merge", "GoMerge", 0, SeedMergeDemo),
            ("convert", "GoConvert", 0, null),
            ("settings", "GoSettings", 0, null),
            ("update", "GoHome", 0, SeedUpdateDemo),
        ];

        int written = 0;
        foreach (string theme in new[] { "light", "dark" })
        {
            string themeDir = Path.Combine(assetsRoot, theme);
            Directory.CreateDirectory(themeDir);

            (MainViewModel vm, MainWindow window) = CreateShell(1240, 780, theme, collapsed: false);
            foreach ((string key, string command, int tab, Action<MainViewModel>? seed) in shots)
            {
                seed?.Invoke(vm);
                Navigate(vm, command, tab);
                Pump();

                string path = Path.Combine(themeDir, key + ".png");
                if (Capture(window, path))
                {
                    written++;
                    long kb = new FileInfo(path).Length / 1024;
                    Console.WriteLine($"  {theme}/{key}.png  ({kb:N0} KB)");
                }
            }

            window.Close();
        }

        Console.WriteLine($"文档截图：{written} 张写入 {assetsRoot}");
        RestoreSettings();
        return written == 0 ? 1 : 0;
    }

    /// <summary>更新面板的示例内容：拿真实发行说明的形态（标题 / 列表 / 加粗）来截。</summary>
    private static void SeedUpdateDemo(MainViewModel vm)
    {
        vm.ShowUpdatePreview(
            version: "1.3.0",
            title: "界面细化与自动更新",
            notes: """
                ## 界面
                - 设置页重做成分组卡片，**删掉了和业务页重复的输入项**
                - 使用说明变成独立页面，按章节配截图
                - 窗口化时文字发虚的问题修好了

                ## 更新
                - 启动时检查 GitHub 发行版，欢迎页直接显示更新内容
                - 下载完成后自动替换文件并重启
                - 手机端弹出可跳过的提示，可在设置里重新触发
                """,
            assetName: "Prism-win-x64-1.3.0.zip",
            assetSize: 124_724_173);
    }

    /// <summary>合并页示例：两条输入，展示优先级列表的排版。</summary>
    private static void SeedMergeDemo(MainViewModel vm)
    {
        if (vm.MergePaks.Count > 0)
        {
            return;
        }

        vm.MergePaks.Add(new Prism.Desktop.Models.MergePakItem(
            @"E:\pak\kards-Android_ASTC.pak", "kards-Android_ASTC.pak")
        {
            OrderLabel = "1",
            Detail = "18,432 个文件",
        });
        vm.MergePaks.Add(new Prism.Desktop.Models.MergePakItem(
            @"E:\pak\texture-pack.pak", "texture-pack.pak")
        {
            OrderLabel = "2",
            Detail = "212 个文件",
        });
        Pump();
    }

    // ══════════════════════════════════════════════════════════════════
    //  页面评审截图（--capture）
    // ══════════════════════════════════════════════════════════════════

    private static int CapturePages(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        (string Label, double Width, double Height, bool Collapsed)[] viewports =
        [
            ("compact", 430, 900, false),
            ("wide", 1440, 900, false),
            ("wide-collapsed", 1440, 900, true),
        ];

        (string Label, string Navigate, int Tab)[] pages =
        [
            ("home", "GoHome", 0),
            ("home-update", "GoHome", 0),
            ("workspace-config", "GoWorkspace", 0),
            ("workspace-browse", "GoWorkspace", 1),
            ("workspace-replace-audio", "GoWorkspace", 2),
            ("workspace-replace-media", "GoWorkspace", 2),
            ("workspace-replace-locres", "GoWorkspace", 2),
            ("preview-audio", "GoWorkspace", 1),
            ("preview-video", "GoWorkspace", 1),
            ("merge", "GoMerge", 0),
            ("convert", "GoConvert", 0),
            ("settings", "GoSettings", 0),
            ("docs-start", "GoDocs", 0),
            ("docs-workspace", "GoDocsWorkspace", 0),
            ("docs-settings", "GoDocsSettings", 0),
        ];

        // 控件预览要真实资产才有意义：仓库里不能带受版权保护的 .uasset，
        // 所以这一页只在给了环境变量（PRISM_WIDGET_PAK + 可选 PRISM_WIDGET_USMAP）时才截。
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PRISM_WIDGET_PAK")))
        {
            pages =
            [
                .. pages[..9],
                ("preview-widget", "GoWorkspace", 1),
                // 同一页再截一张：把列表/预览之间的分隔条往左拖 120px，证明区域能拖
                ("preview-widget-dragged", "GoWorkspace", 1),
                .. pages[9..],
            ];
        }

        int written = 0;
        foreach ((string viewportLabel, double width, double height, bool collapsed) in viewports)
        {
            // 折叠态只需要比一页，两套主题各一张就够说明问题，不必全量渲染。
            (string Label, string Navigate, int Tab)[] forViewport = collapsed
                ? [("workspace-browse", "GoWorkspace", 1)]
                : pages;

            foreach (string theme in new[] { "light", "dark" })
            {
                (MainViewModel vm, MainWindow window) = CreateShell(width, height, theme, collapsed);

                foreach ((string pageLabel, string command, int tab) in forViewport)
                {
                    // 更新面板要在欢迎页上截：先造一份"发现新版本"的状态
                    if (pageLabel == "home-update")
                    {
                        SeedUpdateDemo(vm);
                    }

                    Navigate(vm, command, tab);

                    // 替换页需要真的有任务才看得出来长什么样：塞两条示例（音频 / 视频）。
                    if (pageLabel.StartsWith("workspace-replace", StringComparison.Ordinal))
                    {
                        if (pageLabel.EndsWith("locres", StringComparison.Ordinal))
                        {
                            SeedLocresDemo(vm);
                        }
                        else
                        {
                            SeedPatchDemo(vm, selectMedia: pageLabel.EndsWith("media", StringComparison.Ordinal));
                        }
                    }

                    // 预览栏的内置播放器 / 视频画面：同样造一份示例状态
                    if (pageLabel.StartsWith("preview-", StringComparison.Ordinal))
                    {
                        if (pageLabel.StartsWith("preview-widget", StringComparison.Ordinal))
                        {
                            SeedWidgetDemo(vm);

                            // 拖两张：画布往下拉高 160px，列表↔预览往左拉 140px（预览变宽）
                            if (pageLabel.EndsWith("dragged", StringComparison.Ordinal))
                            {
                                UiFixture.DragHandle(window, "widget-row-handle", 0, 160);
                                if (vm.IsLandscape)
                                {
                                    UiFixture.DragHandle(window, "browse-handle", -140, 0);
                                }
                            }
                        }
                        else
                        {
                            SeedPreviewDemo(vm, video: pageLabel.EndsWith("video", StringComparison.Ordinal));
                        }
                    }

                    string name = $"{theme}-{viewportLabel}-{pageLabel}.png";
                    if (Capture(window, Path.Combine(outputDirectory, name)))
                    {
                        written++;
                        Console.WriteLine($"  {name}");
                    }

                    // 更新状态是同一次 shell 里造的，后面截设置页时得清掉；
                    // 否则设置页会带着"发现新版本"的结果面板，看着像默认状态。
                    if (pageLabel == "home-update")
                    {
                        vm.HideUpdatePreview();
                    }
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
    /// 让那条断言莫名其妙地失败。窗口尺寸与"已忽略的更新版本"同理。
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

    /// <summary>从输出目录往上找仓库根（含 Prism.Desktop 目录的那一层）。</summary>
    private static string? FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Prism.Desktop", "Assets")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// 给替换页塞两条示例任务：一条是编码过的音频（已替换），一条是待替换的视频。
    /// 截图评审要看的是控件排版与信息层级，不是真实解码结果，所以这里只填展示字段。
    /// </summary>
    private static void SeedPatchDemo(MainViewModel vm, bool selectMedia)
    {
        if (vm.PatchItems.Count == 0)
        {
            var audio = new Prism.Desktop.Models.PatchItem(
                kind: "audio",
                sourcePath: "Kards/Content/Audio/BGM/AU_Arrow_morse_loop_01.uasset",
                name: "AU_Arrow_morse_loop_01",
                format: "BINKA",
                sizeLabel: "2.53 秒",
                width: 0,
                height: 0,
                workDirectory: Path.GetTempPath(),
                inputUassetPath: string.Empty)
            {
                Detail = "2 声道 / 48,000 Hz · 2.53 秒 · 1 块（最大 11.9 KB） · 原始 11.9 KB",
                AudioQuality = 4,
                Status = "已替换",
                ReplacementName = "arrow_loop.wav",
                ReplacementDetail = "2 声道 / 48,000 Hz / 2.53 秒 · 编码后 12 KB",
            };

            var media = new Prism.Desktop.Models.PatchItem(
                kind: "media",
                sourcePath: "Kards/Content/Movies/Intro_Main.mp4",
                name: "Intro_Main.mp4",
                format: "MP4",
                sizeLabel: "48.2 MB",
                width: 0,
                height: 0,
                workDirectory: Path.GetTempPath(),
                inputUassetPath: string.Empty)
            {
                Detail = "48.2 MB（直接替换字节，不改资产）",
            };

            vm.PatchItems = new System.Collections.ObjectModel.ObservableCollection<Prism.Desktop.Models.PatchItem>
            {
                audio,
                media,
            };
        }

        vm.SelectedPatchItem = selectMedia
            ? vm.PatchItems.FirstOrDefault(item => item.IsMedia)
            : vm.PatchItems.FirstOrDefault(item => item.IsAudio);
        Pump();
    }

    /// <summary>
    /// 给替换页塞一份本地化任务：裸 .locres 应该显示翻译编辑器（搜索 + 词条列表 + 导出 JSON），
    /// 而不是"直接替换字节"的通用卡片。
    /// </summary>
    private static void SeedLocresDemo(MainViewModel vm)
    {
        var patch = new Prism.Desktop.Models.PatchItem(
            kind: "locres",
            sourcePath: "kards/Content/Localization/Game/zh-Hans/Game.locres",
            name: "Game",
            format: "LOCRES",
            sizeLabel: "4 条词条",
            width: 0,
            height: 0,
            workDirectory: Path.GetTempPath(),
            inputUassetPath: string.Empty);

        (string Key, string Text)[] demo =
        [
            ("PlayButton", "开始"),
            ("SettingsButton", "设置"),
            ("VictoryMessage", "胜利"),
            ("DefeatMessage", "失败"),
        ];
        for (var i = 0; i < demo.Length; i++)
        {
            patch.LocresEntries.Add(new Prism.Desktop.Models.LocresEntryVM(
                new PakTool.Core.LocresEntryDto(i, "/Game/Loc/UI", demo[i].Key, demo[i].Text)));
        }

        patch.ApplyLocresFilter();
        vm.PatchItems = new System.Collections.ObjectModel.ObservableCollection<Prism.Desktop.Models.PatchItem> { patch };
        vm.SelectedPatchItem = patch;
        Pump();
    }

    /// <summary>
    /// 给预览栏塞一份"已选中媒体文件"的示例状态：音频播放器 / 视频画面 + 进度条。
    /// 只用公开可见属性拼状态，不碰播放器本身（截图不需要真的出声）。
    /// </summary>
    private static void SeedPreviewDemo(MainViewModel vm, bool video)
    {
        // 同一视口里页面是连着截的，先把上一种预览状态清掉，免得两张卡叠在一起
        vm.HasAudioPreview = false;
        vm.HasVideoPreview = false;
        vm.VideoPoster = null;

        vm.HasPreview = true;
        vm.SelectedPathText = video
            ? "Kards/Content/Movies/Preview_Sample.mp4"
            : "Kards/Content/Audio/AU_Arrow_morse_loop_01.uasset";
        vm.PreviewTitle = video ? "Preview_Sample.mp4" : "AU_Arrow_morse_loop_01";
        vm.PreviewDetails = new System.Collections.ObjectModel.ObservableCollection<Prism.Desktop.Models.DetailItem>(
        [
            new("Export", video ? "FileMediaSource" : "SoundWave"),
            new("Format", video ? "MP4" : "BINKA"),
            new("Size", video ? "597.55 KB" : "474.75 KB"),
        ]);

        if (video)
        {
            vm.HasVideoPreview = true;
            vm.CanPlayVideo = true;
            vm.VideoDuration = 6;
            vm.VideoPosition = 2.4;
            vm.VideoStatus = "640×360 · 0:06.0 · 静音预览";
            vm.VideoPoster = LoadDemoPoster();
        }
        else
        {
            vm.HasAudioPreview = true;
            vm.AudioStatus = "wav  ·  474.75 KB";
            vm.AudioDuration = 2.53;
            vm.AudioPosition = 1.1;
            vm.AudioButtonText = "暂停";
            vm.IsAudioPlaying = true;
        }

        Pump();
    }

    /// <summary>
    /// 控件蓝图预览：用环境变量指向一个真实 .pak，把里面的 WBP 渲染到预览栏里。
    /// 只在本地评审时用（受版权保护的资产不能进仓库），没给变量就不会有这一页。
    /// </summary>
    private static void SeedWidgetDemo(MainViewModel vm)
    {
        vm.HasAudioPreview = false;
        vm.HasVideoPreview = false;
        vm.VideoPoster = null;

        string pak = Environment.GetEnvironmentVariable("PRISM_WIDGET_PAK")!;
        string? usmap = Environment.GetEnvironmentVariable("PRISM_WIDGET_USMAP");
        string asset = Environment.GetEnvironmentVariable("PRISM_WIDGET_ASSET")
                       ?? "kards/Content/LobCorp/UI/WBP_PEBoxCounter.uasset";

        try
        {
            // 这里已经初始化 Avalonia，不能用 await（续体会排到没人泵的消息队列上），
            // 交给线程池跑完再阻塞等待。
            WidgetPreviewDto? widget = Task.Run(async () =>
            {
                using var session = new PakArchiveSession();
                await session.OpenAsync(new PakOpenOptions([pak], null, usmap)).ConfigureAwait(false);
                return await session.ReadWidgetPreviewAsync(asset).ConfigureAwait(false);
            }).GetAwaiter().GetResult();

            if (widget?.Root is not null)
            {
                vm.HasPreview = true;
                vm.SelectedPathText = asset;
                vm.PreviewTitle = widget.Name;
                vm.PreviewDetails = new System.Collections.ObjectModel.ObservableCollection<Prism.Desktop.Models.DetailItem>(
                [
                    new("控件", CountWidgets(widget.Root).ToString()),
                    new("贴图", widget.Images.Count.ToString()),
                    new("绑定", widget.Bindings.Count.ToString()),
                ]);
                vm.WidgetPreview = widget;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  !! 控件预览示例加载失败：{ex.Message}");
        }

        Pump();
    }

    /// <summary>示例封面帧：仓库里的 artifacts/e2e/poster.png（没有就不放图，卡片会显示占位）。</summary>
    private static Avalonia.Media.Imaging.Bitmap? LoadDemoPoster()    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "e2e", "poster.png");
            if (File.Exists(candidate))
            {
                try
                {
                    using FileStream stream = File.OpenRead(candidate);
                    return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 640);
                }
                catch
                {
                    return null;
                }
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static (MainViewModel Vm, MainWindow Window) CreateShell(double width, double height, string theme, bool collapsed)    {
        var vm = new MainViewModel
        {
            // 关掉过渡动画：截到淡入中间态会得到半透明页面，评审时看不出真实观感。
            // 该属性的 setter 会写设置文件，但那是测试输出目录里的副本，不影响应用。
            IsAnimationsEnabled = false,

            // 关掉启动检查更新：截图不能依赖网络，更不能把 GitHub 的限流提示截进去。
            AutoCheckUpdates = false,
            WindowWidth = width,
            WindowHeight = height,
            IsSidebarCollapsed = collapsed,
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
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);

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
            case "GoDocs": vm.OpenDocsCommand.Execute("start"); break;
            case "GoDocsWorkspace": vm.OpenDocsCommand.Execute("workspace"); break;
            case "GoDocsSettings": vm.OpenDocsCommand.Execute("settings"); break;
        }

        Pump();
    }

    private static bool Capture(MainWindow window, string path)    {
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

    private static int CountWidgets(WidgetNodeDto node)
    {
        var count = 1;
        foreach (var child in node.Children)
        {
            count += CountWidgets(child);
        }

        return count;
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
