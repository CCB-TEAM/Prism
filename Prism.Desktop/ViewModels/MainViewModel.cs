using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PakTool.Core;
using Prism.Desktop.Models;
using Prism.Desktop.Services;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAudio.Core;
using UAssetTexture.Core;

namespace Prism.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private PakArchiveSession _session = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UAssetCliRunner? _cliRunner;
    private int _cliRunnerTried;
    private readonly AppSettings _settings;
    private string _exportDirectoryBookmark;
    private IStorageFolder? _exportFolder;
    private bool _loaded;

    public MainViewModel()
    {
        _settings = AppSettingsStore.Load();
        if (!_settings.ThumbnailDefaultApplied)
        {
            // 一次性迁移：本版本起缩略图默认开启（手机性能足够）。
            _settings.ShowThumbnails = true;
            _settings.ThumbnailDefaultApplied = true;
        }

        ShowThumbnails = _settings.ShowThumbnails;
        UseOodleCompression = _settings.UseOodleCompression;
        AskBeforeReplace = _settings.AskBeforeReplace;
        IsAnimationsEnabled = _settings.IsAnimationsEnabled;
        IsSidebarCollapsed = _settings.SidebarCollapsed;

        // 首次启动落在欢迎页，之后直接进「解包 & 模组」——那才是干活的地方。
        // 侧栏还留着「欢迎」入口，随时能回到这里。
        CurrentView = _settings.WelcomeShown ? "Workspace" : "Home";
        _settings.WelcomeShown = true;
        ExportDirectory = _settings.ExportDirectory;
        _exportDirectoryBookmark = _settings.ExportDirectoryBookmark;
        PakPath = _settings.PakPath;
        UsmapPath = _settings.UsmapPath;
        MergeOutputPath = _settings.MergeOutputPath;
        RestoreMergePaks(_settings.MergePakPaths);
        MergeBaseLocresPath = _settings.MergeBaseLocresPath;
        ConvertSourcePath = _settings.ConvertSourcePath;
        ConvertMergeAll = _settings.ConvertMergeAll;
        ConvertIncludeModFiles = _settings.ConvertIncludeModFiles;
        ConvertOutputPath = _settings.ConvertOutputPath;
        ThemeMode = _settings.Theme;
        AesKey = _settings.AesKey;
        AutoCheckUpdates = _settings.AutoCheckUpdates;
        DismissedUpdateVersion = _settings.DismissedUpdateVersion;

        // 字号资源统一由 LayoutDensity 落一遍：App.axaml 里的紧凑档只是"窗口还没跨过
        // 阈值"时的兜底，两边值必须一致；这里主动对齐一次，避免将来改漏一处。
        Services.LayoutDensity.Apply(IsWideLayout);

        // 文档页启动就要有内容（绑定在进入页面前就在求值），默认停在「快速开始」。
        SelectedChapter = DocChapters.FirstOrDefault();

        InitializeSettings();
        _loaded = true;
        SaveSettings();
        CleanupAndroidCaches();
    }

    /// <summary>窗口引用，用于文件选择对话框；由 MainWindow 在 Opened 时注入。</summary>
    public TopLevel? TopLevel { get; set; }

    // ============ 响应式布局 ============

    /// <summary>窗口内容宽度，由 MainWindow / WorkspaceView 的 SizeChanged 驱动。</summary>
    [ObservableProperty]
    public partial double WindowWidth { get; set; } = 1280;

    /// <summary>横屏（宽窗口）为左列右详情；竖屏（窄窗口）为列表 + 底部抽屉。</summary>
    [ObservableProperty]
    public partial bool IsLandscape { get; set; } = true;

    /// <summary>紧凑布局：手机宽度下自动改为上下排布，避免按钮被挤出屏幕。</summary>
    [ObservableProperty]
    public partial bool IsCompact { get; set; }

    /// <summary>
    /// 宽屏（桌面）布局：窗口够宽时把字号放大一档，并把内容列放宽用满空间。
    ///
    /// 阈值取 1200。手机与窄窗保持紧凑档，排版与原设计完全一致。
    /// </summary>
    [ObservableProperty]
    public partial bool IsWideLayout { get; set; }

    // 内容列宽上限：窄窗保持原有数值（手机布局一行不改），宽屏放开以利用两侧空间。
    public double ConvertContentMaxWidth => IsWideLayout ? 1000 : 620;
    public double MergeContentMaxWidth => IsWideLayout ? 1360 : 560;
    public double SettingsContentMaxWidth => IsWideLayout ? 1360 : 620;
    public double WorkspaceConfigMaxWidth => IsWideLayout ? 1000 : 560;
    public double HomeGridMaxWidth => IsWideLayout ? 1200 : 760;

    /// <summary>
    /// 卡片分几列。宽屏两列 —— 这是"一览众山小"的关键：用宽度换高度，
    /// 页面不再比窗口还高，一屏能看到全部内容，而不是靠滚动。
    /// </summary>
    public int SettingsColumns => IsWideLayout ? 2 : 1;

    /// <summary>
    /// 设置页卡片的槽宽（像素）。WrapPanel 用它决定一行放几张：
    /// 宽屏 = 内容宽度的一半，窄窗/手机 = 整行。
    ///
    /// 为什么自己算而不是让 WrapPanel 自己排：WrapPanel 在水平方向以无限宽度量孩子，
    /// 卡片会缩成"内容想要的那点宽度"，两列排不出来。给一个确定的槽宽最省事，
    /// 也让两列的宽度永远一致。减 2 是留给行内取整误差的余量 —— 差 1px 就会掉成单列。
    /// </summary>
    public double SettingsCardWidth
    {
        get
        {
            // 页面内容区 = 窗口宽 - 侧栏 - 页面左右边距(20+20)
            double content = Math.Max(300d, WindowWidth - SidebarWidth - 40d);
            double usable = Math.Min(content, 1060d);
            return IsWideLayout ? Math.Floor((usable - 2d) / 2d) : usable;
        }
    }

    /// <summary>合并页卡片列数：宽屏把「待合并的 Pak」与「输出 Pak」并排。</summary>
    public int MergeColumns => IsWideLayout ? 2 : 1;

    /// <summary>
    /// 卡片间距。两列时四周留缝，单列时只在下方留缝（原来靠 StackPanel 的 Spacing）。
    /// </summary>
    public Thickness CardGapMargin => IsWideLayout
        ? new Thickness(0, 0, 14, 14)
        : new Thickness(0, 0, 0, 20);

    /// <summary>是否运行在 Android 上（共享 UI 仅在 Android 显示分享入口、走 SAF 等）。</summary>
    public bool IsAndroid => OperatingSystem.IsAndroid();

    public bool IsNotLandscape => !IsLandscape;
    public bool IsNotCompact => !IsCompact;

    /// <summary>顶栏状态文本最大宽度：窄屏压短，避免把标题/返回键挤出屏幕。</summary>
    public double TopStatusMaxWidth => IsCompact ? 150 : 480;

    partial void OnIsLandscapeChanged(bool value) => OnPropertyChanged(nameof(IsNotLandscape));

    partial void OnIsCompactChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotCompact));
        OnPropertyChanged(nameof(TopStatusMaxWidth));
    }

    /// <summary>
    /// 宽屏档位变化：把字号资源切成宽屏值（只放大文字，间距与控件尺寸不变），
    /// 并通知内容列宽等派生属性。
    /// </summary>
    partial void OnIsWideLayoutChanged(bool value)
    {
        LayoutDensity.Apply(value);
        OnPropertyChanged(nameof(ConvertContentMaxWidth));
        OnPropertyChanged(nameof(MergeContentMaxWidth));
        OnPropertyChanged(nameof(SettingsContentMaxWidth));
        OnPropertyChanged(nameof(WorkspaceConfigMaxWidth));
        OnPropertyChanged(nameof(HomeGridMaxWidth));
        OnPropertyChanged(nameof(SearchBoxWidth));
        OnPropertyChanged(nameof(SettingsColumns));
        OnPropertyChanged(nameof(MergeColumns));
        OnPropertyChanged(nameof(CardGapMargin));
        OnPropertyChanged(nameof(SettingsCardWidth));
    }

    partial void OnWindowWidthChanged(double value)
    {
        bool landscape = value >= 980;
        if (landscape != IsLandscape)
        {
            IsLandscape = landscape;
        }

        bool compact = value < 620;
        if (compact != IsCompact)
        {
            IsCompact = compact;
        }

        // 宽屏档位单独判断：只有桌面把窗口拉得足够宽时才放大整体 UI。
        bool wide = value >= 1200;
        if (wide != IsWideLayout)
        {
            IsWideLayout = wide;
        }

        // 侧栏的开关与 IsLandscape 用同一个阈值（980），但它必须跟着窗口宽度
        // 重新通知一次：侧栏显示/隐藏会改变内容区可用宽度，只通知 IsLandscape
        // 不会刷新绑定在 IsSidebarVisible 上的可见性。
        OnPropertyChanged(nameof(CanCollapseSidebar));
        OnPropertyChanged(nameof(IsSidebarVisible));
        OnPropertyChanged(nameof(ShowBackButton));
        OnPropertyChanged(nameof(ShowSidebarToggle));
        OnPropertyChanged(nameof(ShowWelcomeBranding));
        OnPropertyChanged(nameof(ShowWideCards));
        OnPropertyChanged(nameof(ShowNarrowCards));
        OnPropertyChanged(nameof(SettingsCardWidth));

        // 窄窗没有侧栏：宽度归零，内容边距随之消失
        if (!_sidebarAnimating)
        {
            SidebarWidth = value >= 980 && !IsSidebarCollapsed ? SidebarExpandedWidth : 0d;
        }
    }

    [ObservableProperty]
    public partial bool IsPreviewExpanded { get; set; } = true;

    // ============ 视图导航（主页 / 工作区 / 合并页 / 设置） ============

    [ObservableProperty]
    public partial string CurrentView { get; set; } = "Home";

    public bool IsHomeVisible => CurrentView == "Home";

    public bool IsWorkspaceVisible => CurrentView == "Workspace";

    public bool IsMergeVisible => CurrentView == "Merge";

    public bool IsConvertVisible => CurrentView == "Convert";

    public bool IsSettingsVisible => CurrentView == "Settings";

    public bool IsDocsVisible => CurrentView == "Docs";

    /// <summary>
    /// 是否显示左侧导航栏。
    ///
    /// 这套界面原先按"手机 App"的信息架构写：主页是一屏大卡片，进二级页靠返回箭头。
    /// 窗口拉宽到 1440 之后，它仍然只有一个居中的窄条内容 + 一个返回箭头 —— 于是像
    /// "把手机界面拉伸到桌面上"。从 980 px 起改为常驻侧栏，返回箭头随之消失；
    /// 窄窗与手机仍走"主页 → 二级页 → 返回"的老路径，行为不变。
    /// </summary>
    public bool CanCollapseSidebar => WindowWidth >= 980;

    /// <summary>窄窗/手机：页头左侧显示返回箭头（没有侧栏可折叠）。</summary>
    public bool ShowBackButton => !CanCollapseSidebar;

    /// <summary>桌面但侧栏被折叠：页头左侧显示展开按钮。</summary>
    public bool ShowSidebarToggle => CanCollapseSidebar && !IsSidebarVisible;


    /// <summary>
    /// 欢迎页有两种形态，按宽度切换，而不是同一份内容做两种排版：
    ///   桌面（≥980px）—— 纯文字说明，导航由左侧常驻侧栏提供；
    ///   窄窗/手机   —— 入口卡片，因为那里没有侧栏可点。
    /// </summary>
    public bool ShowWelcomeBranding => !CanCollapseSidebar;

    public bool ShowWideCards => !CanCollapseSidebar && !IsCompact;

    public bool ShowNarrowCards => !CanCollapseSidebar && IsCompact;


    public bool IsSidebarVisible => CanCollapseSidebar && !IsSidebarCollapsed;

    /// <summary>
    /// 用户手动折叠侧栏（持久化）。折叠后内容区多出 216 px，窄屏笔记本上一个窗口
    /// 能多放一列；宽窗下侧栏本来就占着位置，愿意常驻展开还是收起由用户决定。
    /// </summary>
    [ObservableProperty]
    public partial bool IsSidebarCollapsed { get; set; }

    /// <summary>
    /// 侧栏当前宽度（像素）。折叠时是 0 而不是把元素隐藏 —— 元素留在可视树里，
    /// 宽度才能逐帧变化，从而带动内容区一起平移。
    /// </summary>
    [ObservableProperty]
    public partial double SidebarWidth { get; set; } = 216;

    partial void OnSidebarWidthChanged(double value)
    {
        OnPropertyChanged(nameof(ContentMargin));
        OnPropertyChanged(nameof(SettingsCardWidth));
    }

    private const double SidebarExpandedWidth = 216d;

    /// <summary>
    /// 内容区的左外边距，与侧栏宽度同源。
    ///
    /// 为什么不用 Grid 的 Auto 列：实测把侧栏 Width 从 216 改到 0 之后，Auto 列没有
    /// 重新测量 —— 内容按 0 排、侧栏却仍画在 216 的位置，页头与标签页直接压在侧栏上。
    /// 改成"内容自己带左边距 + 侧栏绝对靠左"，两者都由 SidebarWidth 驱动，逐帧一致。
    /// </summary>
    public Thickness ContentMargin => new(SidebarWidth, 0d, 0d, 0d);

    private const int SidebarAnimationMs = 200;

    /// <summary>动画进行中：此时不要按 IsSidebarCollapsed 直接吸附宽度。</summary>
    private bool _sidebarAnimating;

    private CancellationTokenSource? _sidebarAnimationCts;

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSidebarVisible));
        OnPropertyChanged(nameof(ShowSidebarToggle));

        // 非动画路径（读设置、截图）直接到位；动画路径由 ToggleSidebarAsync 自己逐帧写宽度。
        if (!_sidebarAnimating)
        {
            SidebarWidth = value ? 0d : SidebarExpandedWidth;
        }

        SaveSettings();
    }

    /// <summary>
    /// 折叠 / 展开侧栏。
    ///
    /// 为什么不用 XAML 的过渡动画：宽度是<b>布局属性</b>。用 DoubleTransition 做 216→0 时，
    /// 渲染宽度走了动画而布局列宽不跟着走，结果侧栏画到一半、内容按全宽排，页头和标签页
    /// 直接压在侧栏上（实测过一次，是那个"整页错位"的 bug）。
    /// 这里改成在视图模型里逐帧写 SidebarWidth：每帧都是一次真实的属性变更，布局每帧
    /// 都会重算，渲染与布局始终一致。
    /// </summary>
    [RelayCommand]
    private async Task ToggleSidebarAsync()
    {
        bool target = !IsSidebarCollapsed;

        _sidebarAnimationCts?.Cancel();
        _sidebarAnimationCts?.Dispose();
        _sidebarAnimationCts = null;

        double to = target ? 0d : SidebarExpandedWidth;
        double from = SidebarWidth;

        if (!IsAnimationsEnabled || !CanCollapseSidebar)
        {
            _sidebarAnimating = false;
            SidebarWidth = to;
            IsSidebarCollapsed = target;
            return;
        }

        var cts = new CancellationTokenSource();
        _sidebarAnimationCts = cts;
        _sidebarAnimating = true;

        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                double t = Math.Clamp(watch.ElapsedMilliseconds / (double)SidebarAnimationMs, 0d, 1d);
                double eased = 1d - Math.Pow(1d - t, 3d); // CubicEaseOut
                SidebarWidth = from + ((to - from) * eased);

                if (t >= 1d)
                {
                    break;
                }

                await Task.Delay(16, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 被下一次切换打断：由新的调用接管，这里不再改状态。
            return;
        }

        SidebarWidth = to;
        _sidebarAnimating = false;
        IsSidebarCollapsed = target;
        _sidebarAnimationCts = null;
        cts.Dispose();
    }

    /// <summary>页头标题：由当前视图决定，各页面共用同一处渲染。</summary>
    public string PageTitle => CurrentView switch
    {
        "Workspace" => "解包 & 模组",
        "Merge" => "Pak 合并",
        "Convert" => "Pak 转换",
        "Settings" => "设置",
        "Docs" => "使用说明",
        _ => "欢迎",
    };

    /// <summary>当前页对应的使用说明章节；没有对应章节的页面（欢迎 / 文档页）返回空串。</summary>
    public string PageHelpTopic => CurrentView switch
    {
        "Workspace" => "workspace",
        "Merge" => "merge",
        "Convert" => "convert",
        "Settings" => "settings",
        _ => string.Empty,
    };

    public bool HasPageHelp => PageHelpTopic.Length > 0;

    partial void OnCurrentViewChanged(string value)
    {
        OnPropertyChanged(nameof(IsHomeVisible));
        OnPropertyChanged(nameof(IsWorkspaceVisible));
        OnPropertyChanged(nameof(IsMergeVisible));
        OnPropertyChanged(nameof(IsConvertVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(IsDocsVisible));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageHelpTopic));
        OnPropertyChanged(nameof(HasPageHelp));
        OnPropertyChanged(nameof(CurrentStatusText));
    }

    [RelayCommand]
    private void GoHome() => CurrentView = "Home";

    [RelayCommand]
    private void GoWorkspace() => CurrentView = "Workspace";

    /// <summary>设置页「前往配置」：直接落到工作区的配置标签（路径都在那里）。</summary>
    [RelayCommand]
    private void GoWorkspaceConfig()
    {
        CurrentTabIndex = 0;
        CurrentView = "Workspace";
    }

    /// <summary>
    /// 主页「搜索 &amp; 导出」入口：直接进入工作区的浏览标签。
    /// 与「解包 &amp; 模组」区分开——那个停在配置/浏览的默认标签，这个直达搜索。
    /// </summary>
    [RelayCommand]
    private void GoBrowse()
    {
        CurrentTabIndex = 1;
        CurrentView = "Workspace";
    }

    [RelayCommand]
    private void GoMerge() => CurrentView = "Merge";

    [RelayCommand]
    private void GoConvert() => CurrentView = "Convert";

    [RelayCommand]
    private void GoSettings() => CurrentView = "Settings";

    // ============ 工作区标签（配置 / 浏览 / 替换） ============

    [ObservableProperty]
    public partial int CurrentTabIndex { get; set; }

    public bool IsConfigTab => CurrentTabIndex == 0;

    public bool IsBrowseTab => CurrentTabIndex == 1;

    public bool IsPatchTab => CurrentTabIndex == 2;

    partial void OnCurrentTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsConfigTab));
        OnPropertyChanged(nameof(IsBrowseTab));
        OnPropertyChanged(nameof(IsPatchTab));

        // 离开浏览页就停止后台缩略图；回到浏览页时只补缺失的缩略图。
        if (value == 1)
        {
            StartThumbnails();
        }
        else
        {
            _resumeThumbnailsAfterUserAction = false;
            _thumbCts?.Cancel();
        }
    }

    [RelayCommand]
    private void SwitchConfigTab() => CurrentTabIndex = 0;

    [RelayCommand]
    private void SwitchBrowseTab() => CurrentTabIndex = 1;

    [RelayCommand]
    private void SwitchPatchTab() => CurrentTabIndex = 2;

    // ============ 打开配置 ============

    [ObservableProperty]
    public partial string PakPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UsmapPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AesKey { get; set; } = string.Empty;

    // ============ 浏览状态 ============

    [ObservableProperty]
    public partial string CurrentFolder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentPathText { get; set; } = "/";

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    /// <summary>
    /// 搜索框提示语：输入看起来像 Pak 内路径时切换文案，
    /// 让"可以直接输入路径跳转"这一能力可被发现。
    ///
    /// 文案要短：搜索框本身只有 230 px（宽屏 360 px），原先那句
    /// "搜索关键词，或直接输入 Pak 内路径（如 Game/Content/UI）" 会被截成
    /// "…（如 Game/Conte"，恰好把示例剪断，比不写还难看。示例挪到 ToolTip。
    /// </summary>
    public string SearchPlaceholder => LooksLikePackagePath(SearchQuery)
        ? "按回车跳转到该路径"
        : "搜索关键词，或输入 Pak 内路径";

    partial void OnSearchQueryChanged(string value) => OnPropertyChanged(nameof(SearchPlaceholder));

    /// <summary>
    /// 判断输入是否"像" Pak 内路径：含路径分隔符，或带已知资源扩展名。
    /// 仅用于提示文案，不影响实际解析（解析以存在性为准）。
    /// </summary>
    private static bool LooksLikePackagePath(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        string trimmed = query.Trim();
        if (trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            return true;
        }

        string extension = Path.GetExtension(trimmed);
        return extension.Length > 1 && KnownPackageExtensions.Contains(extension);
    }

    private static readonly HashSet<string> KnownPackageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".uasset", ".uexp", ".ubulk", ".umap", ".locres", ".pak", ".ini",
        ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".wav", ".ogg", ".bnk", ".binka",
        ".json", ".csv", ".ttf", ".otf", ".fbx", ".psk", ".usmap", ".jmap",
    };

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    /// <summary>
    /// 状态栏显示什么。转换页有自己的进度文案（<see cref="ConvertStatus"/>），
    /// 其余页面共用 <see cref="StatusText"/> —— 外壳只有一条状态栏，不能各页各绑一个。
    /// </summary>
    public string CurrentStatusText => CurrentView == "Convert" ? ConvertStatus : StatusText;

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(CurrentStatusText));

    partial void OnConvertStatusChanged(string value) => OnPropertyChanged(nameof(CurrentStatusText));

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<EntryItem> Entries { get; set; } = [];

    [ObservableProperty]
    public partial EntryItem? SelectedItem { get; set; }

    private bool IsPakOpen => PakPath.Length > 0;

    /// <summary>
    /// 文件列表是否显示"还没有打开 Pak"的空状态。
    ///
    /// 单独做成会通知的公开属性，而不是在 XAML 里直接绑 IsPakOpen —— 后者是私有计算属性、
    /// 不发变更通知，绑定只会在加载时求值一次，于是打开 Pak 之后空状态仍压在列表上。
    /// </summary>
    public bool ShowPakEmptyState => !IsPakOpen;

    public bool CanUp => !string.IsNullOrEmpty(CurrentFolder);

    public bool CanExportRaw => SelectedItem is { IsDirectory: false } && IsPakOpen && !IsBusy;

    public bool CanExportPreview => SelectedItem is { IsDirectory: false } && HasPreview && !IsBusy;

    public bool CanShareRaw => CanExportRaw && IsAndroid;

    public bool CanSharePreview => CanExportPreview && IsAndroid;

    public bool CanAddToPatch => SelectedItem is { IsDirectory: false } && IsPakOpen && !IsBusy;

    partial void OnCurrentFolderChanged(string value) => UpCommand.NotifyCanExecuteChanged();

    partial void OnSelectedItemChanged(EntryItem? value)
    {
        ExportRawCommand.NotifyCanExecuteChanged();
        ExportPreviewCommand.NotifyCanExecuteChanged();
        ShareRawCommand.NotifyCanExecuteChanged();
        SharePreviewCommand.NotifyCanExecuteChanged();
        AddSelectedToPatchCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasPreviewChanged(bool value)
    {
        ExportPreviewCommand.NotifyCanExecuteChanged();
        SharePreviewCommand.NotifyCanExecuteChanged();
    }

    // ============ 预览 ============

    [ObservableProperty]
    public partial Bitmap? PreviewImage { get; set; }

    public bool HasImagePreview => PreviewImage is not null;

    partial void OnPreviewImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasImagePreview));

    [ObservableProperty]
    public partial string PreviewTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PreviewText { get; set; } = string.Empty;

    public bool HasTextPreview => !string.IsNullOrWhiteSpace(PreviewText);

    partial void OnPreviewTextChanged(string value) => OnPropertyChanged(nameof(HasTextPreview));

    [ObservableProperty]
    public partial ObservableCollection<DetailItem> PreviewDetails { get; set; } = [];

    [ObservableProperty]
    public partial bool HasPreview { get; set; }

    [ObservableProperty]
    public partial string SelectedPathText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExportDirectory { get; set; } = string.Empty;

    // ============ 音频 / 视频 / 模型预览 ============

    [ObservableProperty]
    public partial bool HasAudioPreview { get; set; }

    [ObservableProperty]
    public partial bool IsAudioPlaying { get; set; }

    [ObservableProperty]
    public partial bool IsAudioPaused { get; set; }

    [ObservableProperty]
    public partial string AudioStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AudioButtonText { get; set; } = "播放";

    [ObservableProperty]
    public partial double AudioDuration { get; set; }

    [ObservableProperty]
    public partial double AudioPosition { get; set; }

    [ObservableProperty]
    public partial bool IsAudioSeeking { get; set; }

    public string AudioTimeText => $"{FormatAudioTime(AudioPosition)} / {FormatAudioTime(AudioDuration)}";

    /// <summary>
    /// 内置播放器进度条：Android 走 MediaPlayer，桌面走 MCI，两边都能拖。
    /// 只要有音频就允许拖 —— 时长还没算出来时禁用，滑块看起来就是"坏掉"，不如让它可点、
    /// seek 时再按已知时长夹一下。
    /// </summary>
    public bool CanSeekAudio => HasAudioPreview;

    partial void OnIsAudioPlayingChanged(bool value)
    {
        UpdateAudioButtonText();
        OnPropertyChanged(nameof(CanSeekAudio));
    }

    partial void OnIsAudioPausedChanged(bool value) => UpdateAudioButtonText();

    private void UpdateAudioButtonText()
    {
        AudioButtonText = IsAudioPlaying ? "暂停" : IsAudioPaused ? "继续" : "播放";
    }

    partial void OnAudioDurationChanged(double value)
    {
        OnPropertyChanged(nameof(CanSeekAudio));
        OnPropertyChanged(nameof(AudioTimeText));
    }

    partial void OnAudioPositionChanged(double value) => OnPropertyChanged(nameof(AudioTimeText));

    partial void OnHasAudioPreviewChanged(bool value) => OnPropertyChanged(nameof(CanSeekAudio));

    [ObservableProperty]
    public partial ModelPreviewDto? ModelPreview { get; set; }

    public bool HasModelPreview => ModelPreview is not null;

    partial void OnModelPreviewChanged(ModelPreviewDto? value) => OnPropertyChanged(nameof(HasModelPreview));

    [ObservableProperty]
    public partial string PreviewEmptyText { get; set; } = "此文件无预览";

    private string? _audioTempFile;

    /// <summary>桌面播放器（MCI）。Android 由壳里的 MediaPlayer 负责，这里不参与。</summary>
    private readonly WindowsAudioPlayer _audioPlayer = new();

    private Avalonia.Threading.DispatcherTimer? _audioProgressTimer;

    [RelayCommand]
    private async Task ToggleAudioPlayback()
    {
        if (!HasAudioPreview || _audioTempFile is null)
        {
            return;
        }

        if (OperatingSystem.IsAndroid())
        {
            await ToggleAndroidAudioPlaybackAsync();
            return;
        }

        // ── 桌面：MCI ──
        if (IsAudioPlaying)
        {
            _audioPlayer.Pause();
            IsAudioPlaying = false;
            IsAudioPaused = true;
            return;
        }

        if (IsAudioPaused && _audioPlayer.IsOpen)
        {
            _audioPlayer.Resume();
            IsAudioPlaying = true;
            IsAudioPaused = false;
            StartAudioProgressTimer();
            return;
        }

        if (!WindowsAudioPlayer.IsSupported)
        {
            StatusText = "当前系统不支持内置音频播放，可用「导出原始」拿文件。";
            return;
        }

        if (!_audioPlayer.IsOpen && !_audioPlayer.Open(_audioTempFile))
        {
            StatusText = "音频播放失败：系统播放设备打不开这个文件。";
            return;
        }

        var length = _audioPlayer.LengthMs;
        if (length > 0)
        {
            AudioDuration = length / 1000d;
        }

        _audioPlayer.Seek((long)(Math.Clamp(AudioPosition, 0, AudioDuration) * 1000));
        _audioPlayer.Play();
        IsAudioPlaying = true;
        IsAudioPaused = false;
        StartAudioProgressTimer();
    }

    /// <summary>Android：暂停/继续/播放都由壳里的 MediaPlayer 完成，进度靠回调推进。</summary>
    private async Task ToggleAndroidAudioPlaybackAsync()
    {
        if (IsAudioPlaying)
        {
            // 暂停，保留进度；再次点击继续播放。
            if (NativeAudioSetPausedAsync is not null)
            {
                try
                {
                    await NativeAudioSetPausedAsync(true);
                    IsAudioPlaying = false;
                    IsAudioPaused = true;
                    return;
                }
                catch (Exception ex)
                {
                    StatusText = "音频暂停失败：" + ex.Message;
                    IsAudioPlaying = false;
                    IsAudioPaused = false;
                    return;
                }
            }

            if (NativeAudioStopAsync is not null)
            {
                await NativeAudioStopAsync();
            }

            IsAudioPlaying = false;
            IsAudioPaused = false;
            return;
        }

        if (IsAudioPaused && NativeAudioSetPausedAsync is not null)
        {
            IsAudioPlaying = true;
            IsAudioPaused = false;
            try
            {
                await NativeAudioSetPausedAsync(false);
            }
            catch (Exception ex)
            {
                IsAudioPlaying = false;
                StatusText = "音频继续播放失败：" + ex.Message;
            }

            return;
        }

        IsAudioPlaying = true;
        IsAudioPaused = false;
        if (NativeAudioPlayAsync is null)
        {
            IsAudioPlaying = false;
            StatusText = "音频播放服务未就绪，请重新打开应用后重试。";
            return;
        }

        try
        {
            // 内置播放器支持拖动进度后从指定位置开始播放。
            await NativeAudioPlayAsync(_audioTempFile!, AudioPosition);
        }
        catch (Exception ex)
        {
            IsAudioPlaying = false;
            StatusText = "音频播放失败：" + ex.Message;
        }
    }

    private void StartAudioProgressTimer()
    {
        if (OperatingSystem.IsAndroid())
        {
            return; // Android 的进度由 MediaPlayer 回调推进
        }

        _audioProgressTimer ??= new Avalonia.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(200),
            Avalonia.Threading.DispatcherPriority.Normal,
            (_, _) => OnAudioProgressTick());
        _audioProgressTimer.Start();
    }

    private void OnAudioProgressTick()
    {
        if (!_audioPlayer.IsOpen)
        {
            _audioProgressTimer?.Stop();
            return;
        }

        if (IsAudioSeeking)
        {
            return;
        }

        var length = _audioPlayer.LengthMs;
        if (length > 0)
        {
            AudioDuration = length / 1000d;
        }

        var position = _audioPlayer.PositionMs;
        if (position >= 0)
        {
            AudioPosition = Math.Clamp(position / 1000d, 0, AudioDuration > 0 ? AudioDuration : position / 1000d);
        }

        // 放完了：MCI 自己会变成 stopped，这里复位成"未播放"，下次点击从头开始。
        if (IsAudioPlaying && _audioPlayer.Mode == "stopped")
        {
            _audioPlayer.StopAndRewind();
            IsAudioPlaying = false;
            IsAudioPaused = false;
            AudioPosition = 0;
            _audioProgressTimer?.Stop();
        }
    }

    [RelayCommand]
    private async Task SeekAudioAsync()
    {
        if (!CanSeekAudio)
        {
            return;
        }

        var seconds = Math.Clamp(AudioPosition, 0, AudioDuration);
        if (OperatingSystem.IsAndroid())
        {
            if (NativeAudioSeekAsync is not null)
            {
                await NativeAudioSeekAsync(seconds);
            }

            return;
        }

        if (!_audioPlayer.IsOpen)
        {
            return;
        }

        _audioPlayer.Seek((long)(seconds * 1000));
        if (IsAudioPlaying)
        {
            _audioPlayer.Play();
        }
    }

    /// <summary>供 Android MediaPlayer 定时回传播放进度。</summary>
    public void UpdateAudioPlaybackState(double positionSeconds, double durationSeconds)
    {
        if (IsAudioSeeking)
        {
            return;
        }

        if (durationSeconds > 0)
        {
            AudioDuration = durationSeconds;
        }

        AudioPosition = Math.Clamp(positionSeconds, 0, AudioDuration > 0 ? AudioDuration : positionSeconds);
    }

    /// <summary>供 Android MediaPlayer 播放完成回调。</summary>
    public void NotifyNativeAudioPlaybackCompleted()
    {
        if (IsAudioPlaying)
        {
            IsAudioPlaying = false;
        }

        IsAudioPaused = false;
        AudioPosition = 0;
    }

    private static string FormatAudioTime(double totalSeconds)
    {
        if (double.IsNaN(totalSeconds) || totalSeconds < 0)
        {
            totalSeconds = 0;
        }

        TimeSpan time = TimeSpan.FromSeconds(totalSeconds);
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"mm\:ss");
    }

    /// <summary>把预览载荷当图片解；不是图片（或解码器报错）就返回 false，不要抛。</summary>
    private static bool TryDecodePreviewImage(byte[] data, out Bitmap? image)
    {
        image = null;
        try
        {
            using MemoryStream stream = new(data);
            image = Bitmap.DecodeToWidth(stream, 1280);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ============ 视频预览 ============

    /// <summary>内嵌视频预览的落地文件上限：超过就只提示用「导出原始」。</summary>
    private const long MaxVideoPreviewBytes = 256L * 1024 * 1024;

    [ObservableProperty]
    public partial bool HasVideoPreview { get; set; }

    [ObservableProperty]
    public partial Bitmap? VideoPoster { get; set; }

    [ObservableProperty]
    public partial Bitmap? VideoFrame { get; set; }

    [ObservableProperty]
    public partial bool IsVideoPlaying { get; set; }

    [ObservableProperty]
    public partial string VideoStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double VideoDuration { get; set; }

    [ObservableProperty]
    public partial double VideoPosition { get; set; }

    [ObservableProperty]
    public partial bool IsVideoSeeking { get; set; }

    [ObservableProperty]
    public partial bool CanPlayVideo { get; set; }

    public string VideoTimeText => $"{FormatAudioTime(VideoPosition)} / {FormatAudioTime(VideoDuration)}";

    public string VideoButtonText => IsVideoPlaying ? "暂停" : "播放";

    public bool CanSeekVideo => HasVideoPreview && VideoDuration > 0;

    /// <summary>画布内容：播放中贴解码帧，暂停/未播贴封面帧。</summary>
    public Bitmap? VideoSurface => VideoFrame ?? VideoPoster;

    public bool HasVideoSurface => VideoSurface is not null;

    private string? _videoTempFile;
    private VideoProbeInfo? _videoProbe;
    private readonly FfmpegVideoPlayer _videoPlayer = new();
    private WriteableBitmap[]? _videoBuffers;
    private int _videoBufferIndex;
    private readonly System.Diagnostics.Stopwatch _videoClock = new();
    private double _videoStartSeconds;
    private Avalonia.Threading.DispatcherTimer? _videoProgressTimer;

    partial void OnVideoFrameChanged(Bitmap? value) => NotifyVideoSurface();

    partial void OnVideoPosterChanged(Bitmap? value) => NotifyVideoSurface();

    partial void OnIsVideoPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(VideoButtonText));
        OnPropertyChanged(nameof(VideoSurface));
    }

    partial void OnVideoDurationChanged(double value)
    {
        OnPropertyChanged(nameof(CanSeekVideo));
        OnPropertyChanged(nameof(VideoTimeText));
    }

    partial void OnVideoPositionChanged(double value) => OnPropertyChanged(nameof(VideoTimeText));

    partial void OnHasVideoPreviewChanged(bool value) => OnPropertyChanged(nameof(CanSeekVideo));

    private void NotifyVideoSurface()
    {
        OnPropertyChanged(nameof(VideoSurface));
        OnPropertyChanged(nameof(HasVideoSurface));
    }

    /// <summary>选中一个视频文件后：落地 → 探元数据 → 抽封面。全部在后台，不卡 UI。</summary>
    private async Task PrepareVideoPreviewAsync(EntryItem item)
    {
        StopVideoPreview();
        HasVideoPreview = true;
        VideoStatus = "准备中…";

        if (Ffmpeg.Find(ToolsDirectory) is null)
        {
            VideoStatus = $"视频预览需要 {Ffmpeg.ToolName}（放进 tools 目录即可）；现在可以「导出原始」后用系统播放器看。";
            return;
        }

        if (item.SizeBytes > MaxVideoPreviewBytes)
        {
            VideoStatus = $"文件 {item.SizeBytes / (1024 * 1024)} MB 超过内嵌预览上限（{MaxVideoPreviewBytes / (1024 * 1024)} MB），请用「导出原始」。";
            return;
        }

        string? path = await EnsureVideoFileAsync(item.FullPath);
        if (path is null)
        {
            VideoStatus = "视频解到临时目录失败。";
            return;
        }

        var (probe, poster) = await Task.Run(() =>
        {
            if (!Ffmpeg.TryProbe(path, ToolsDirectory, out VideoProbeInfo info))
                return ((VideoProbeInfo?)null, (Bitmap?)null);

            Bitmap? bitmap = null;
            string posterPath = Path.ChangeExtension(path, ".poster.png");
            // 封面取靠前但不在第一帧的位置：很多片子第一帧是黑的
            var at = TimeSpan.FromSeconds(Math.Clamp(info.Seconds * 0.15, 0.2, 3));
            if (Ffmpeg.TryExtractPoster(path, posterPath, ToolsDirectory, at))
            {
                try
                {
                    bitmap = LoadImage(posterPath, Ffmpeg.PreviewMaxWidth);
                }
                catch
                {
                    bitmap = null;
                }
            }

            return (info, bitmap);
        });

        if (probe is null)
        {
            VideoStatus = "无法解析这个视频（ffmpeg 认不出容器或编码）。";
            return;
        }

        _videoProbe = probe.Value;
        VideoDuration = probe.Value.Seconds;
        VideoPoster = poster;
        CanPlayVideo = true;
        VideoStatus = $"{probe.Value.DescribeSize} · {probe.Value.DescribeDuration} · 静音预览";
    }

    /// <summary>把 Pak 里的视频解到临时目录（同一路径只解一次）。</summary>
    private async Task<string?> EnsureVideoFileAsync(string pakPath)
    {
        if (_videoTempFile is not null && File.Exists(_videoTempFile))
            return _videoTempFile;

        byte[] data;
        await _gate.WaitAsync();
        try
        {
            data = await _session.ReadFileAsync(pakPath);
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }

        string directory = Path.Combine(Path.GetTempPath(), "prism-desktop-video", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Path.GetFileName(pakPath));
        await File.WriteAllBytesAsync(path, data);
        _videoTempFile = path;
        return path;
    }

    [RelayCommand]
    private void ToggleVideoPlayback()
    {
        if (!HasVideoPreview)
        {
            return;
        }

        if (IsVideoPlaying)
        {
            _videoPlayer.Stop();
            IsVideoPlaying = false;
            _videoProgressTimer?.Stop();
            VideoStatus = "已暂停";
            return;
        }

        if (!CanPlayVideo)
        {
            return; // VideoStatus 已经说明了为什么
        }

        string? path = _videoTempFile;
        if (path is null || _videoProbe is null)
        {
            VideoStatus = "视频还没准备好。";
            return;
        }

        StartVideoPlayback(path);
    }

    private void StartVideoPlayback(string path)
    {
        VideoProbeInfo probe = _videoProbe!.Value;
        (int width, int height) = Ffmpeg.ScaleToPreview(probe.Width, probe.Height);
        _videoStartSeconds = VideoPosition;
        _videoClock.Restart();

        _videoPlayer.Frame -= OnVideoFrame;
        _videoPlayer.Finished -= OnVideoFinished;
        _videoPlayer.Frame += OnVideoFrame;
        _videoPlayer.Finished += OnVideoFinished;

        // 换位置就重建画布：尺寸可能和上一部片子不同
        if (_videoBuffers is null ||
            _videoBuffers[0].PixelSize.Width != width ||
            _videoBuffers[0].PixelSize.Height != height)
        {
            _videoBuffers = null;
        }

        if (!_videoPlayer.Start(path, width, height, VideoPosition, ToolsDirectory, out string error))
        {
            VideoStatus = error;
            IsVideoPlaying = false;
            return;
        }

        IsVideoPlaying = true;
        VideoStatus = $"播放中 · {probe.DescribeSize}（静音）";
        StartVideoProgressTimer();
    }

    private void OnVideoFrame(byte[] frame)
    {
        int width = _videoPlayer.FrameWidth;
        int height = _videoPlayer.FrameHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _videoBuffers ??= CreateVideoBuffers(width, height);
            WriteableBitmap target = _videoBuffers[_videoBufferIndex];
            _videoBufferIndex ^= 1;
            CopyFrame(frame, target, width, height);
            VideoFrame = target;
        });
    }

    private void OnVideoFinished()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!IsVideoPlaying)
            {
                return;
            }

            IsVideoPlaying = false;
            _videoProgressTimer?.Stop();
            VideoPosition = 0;
            VideoFrame = null;
            VideoStatus = "播放结束";
        });
    }

    private static WriteableBitmap[] CreateVideoBuffers(int width, int height) =>
    [
        new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque),
        new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque),
    ];

    private static void CopyFrame(byte[] source, WriteableBitmap target, int width, int height)
    {
        using Avalonia.Platform.ILockedFramebuffer buffer = target.Lock();
        int rowBytes = width * 4;
        if (buffer.RowBytes == rowBytes)
        {
            System.Runtime.InteropServices.Marshal.Copy(source, 0, buffer.Address, Math.Min(source.Length, rowBytes * height));
            return;
        }

        for (int y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(source, y * rowBytes, buffer.Address + (y * buffer.RowBytes), rowBytes);
        }
    }

    private void StartVideoProgressTimer()
    {
        _videoProgressTimer ??= new Avalonia.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(200),
            Avalonia.Threading.DispatcherPriority.Normal,
            (_, _) => OnVideoProgressTick());
        _videoProgressTimer.Start();
    }

    private void OnVideoProgressTick()
    {
        if (!IsVideoPlaying || _videoProbe is null)
        {
            _videoProgressTimer?.Stop();
            return;
        }

        if (IsVideoSeeking)
        {
            return;
        }

        // 用挂钟而不是"帧数 / fps"：ffmpeg 起进程有几帧的启动延迟，按帧算会一直落后
        double position = _videoStartSeconds + _videoClock.Elapsed.TotalSeconds;
        if (VideoDuration > 0)
        {
            position = Math.Min(position, VideoDuration);
        }

        VideoPosition = position;
    }

    [RelayCommand]
    private void SeekVideo()
    {
        if (!CanSeekVideo)
        {
            return;
        }

        bool wasPlaying = IsVideoPlaying;
        _videoPlayer.Stop();
        IsVideoPlaying = false;
        _videoProgressTimer?.Stop();
        VideoPosition = Math.Clamp(VideoPosition, 0, VideoDuration);

        if (wasPlaying && _videoTempFile is not null && _videoProbe is not null)
        {
            StartVideoPlayback(_videoTempFile);
            return;
        }

        // 暂停状态下拖动也要有反馈：把那一帧抠出来替换画面，
        // 否则"拖了但画面不动"看起来就像进度条坏了。
        if (_videoTempFile is not null && _videoProbe is not null)
        {
            _ = ShowVideoFrameAtAsync(_videoTempFile, VideoPosition);
        }
    }

    /// <summary>抽指定时间点的一帧当画面（暂停拖动时用）。</summary>
    private async Task ShowVideoFrameAtAsync(string path, double seconds)
    {
        Bitmap? bitmap = await Task.Run(() =>
        {
            string framePath = Path.ChangeExtension(path, ".seek.png");
            try
            {
                return Ffmpeg.TryExtractPoster(path, framePath, ToolsDirectory, TimeSpan.FromSeconds(seconds))
                    ? LoadImage(framePath, Ffmpeg.PreviewMaxWidth)
                    : null;
            }
            catch
            {
                return null;
            }
        });

        if (bitmap is not null && !IsVideoPlaying)
        {
            VideoFrame = bitmap;
        }
    }

    /// <summary>用系统播放器打开当前视频（没有 ffmpeg 时这就是唯一的"看片"方式）。</summary>
    [RelayCommand]
    private async Task OpenVideoExternallyAsync()
    {
        if (!HasVideoPreview || SelectedPathText.Length == 0)
        {
            return;
        }

        string? path = await EnsureVideoFileAsync(SelectedPathText);
        if (path is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "打开系统播放器失败：" + ex.Message;
        }
    }

    private void StopVideoPreview()
    {
        _videoPlayer.Stop();
        _videoProgressTimer?.Stop();
        IsVideoPlaying = false;
        HasVideoPreview = false;
        CanPlayVideo = false;
        VideoFrame = null;
        VideoPoster = null;
        VideoPosition = 0;
        VideoDuration = 0;
        VideoStatus = string.Empty;
        _videoProbe = null;
        _videoBuffers = null;
        _videoClock.Reset();

        string? temp = _videoTempFile;
        _videoTempFile = null;
        if (temp is not null)
        {
            TryDeleteFile(temp);
            TryDeleteDirectory(Path.GetDirectoryName(temp));
        }
    }

    // ============ 替换（Patch） ============

    [ObservableProperty]
    public partial ObservableCollection<PatchItem> PatchItems { get; set; } = [];

    [ObservableProperty]
    public partial PatchItem? SelectedPatchItem { get; set; }

    [ObservableProperty]
    public partial bool UseOodleCompression { get; set; }

    private ObservableCollection<PatchItem>? _patchItemsSubscribed;

    public bool CanBuildPatchPak => PatchItems.Count > 0 && !IsBusy;

    public bool HasPatchItems => PatchItems.Count > 0;

    public bool HasNoPatchItems => !HasPatchItems;

    partial void OnPatchItemsChanged(ObservableCollection<PatchItem> value)
    {
        if (_patchItemsSubscribed is not null)
        {
            _patchItemsSubscribed.CollectionChanged -= OnPatchItemsCollectionChanged;
        }

        value.CollectionChanged += OnPatchItemsCollectionChanged;
        _patchItemsSubscribed = value;
        NotifyPatchItemsState();
    }

    private void OnPatchItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => NotifyPatchItemsState();

    private void NotifyPatchItemsState()
    {
        OnPropertyChanged(nameof(CanBuildPatchPak));
        OnPropertyChanged(nameof(HasPatchItems));
        OnPropertyChanged(nameof(HasNoPatchItems));
        BuildPatchPakCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPatchItemChanged(PatchItem? value)
    {
        PickReplacementCommand.NotifyCanExecuteChanged();
        RemovePatchItemCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        BuildPatchPakCommand.NotifyCanExecuteChanged();
        PickReplacementCommand.NotifyCanExecuteChanged();
        ExportRawCommand.NotifyCanExecuteChanged();
        ExportPreviewCommand.NotifyCanExecuteChanged();
        ShareRawCommand.NotifyCanExecuteChanged();
        SharePreviewCommand.NotifyCanExecuteChanged();
        AddSelectedToPatchCommand.NotifyCanExecuteChanged();
        ExportFolderRawCommand.NotifyCanExecuteChanged();
        ExportFolderImagesCommand.NotifyCanExecuteChanged();
    }

    // ============ 持久化 ============

    /// <summary>窗口高度（关闭时保存）。</summary>
    public double WindowHeight { get; set; } = 820;

    partial void OnUseOodleCompressionChanged(bool value) => SaveSettings();

    partial void OnAskBeforeReplaceChanged(bool value) => SaveSettings();

    partial void OnExportDirectoryChanged(string value) => SaveSettings();

    partial void OnPakPathChanged(string value)
    {
        OnPropertyChanged(nameof(ShowPakEmptyState));
        ExportRawCommand.NotifyCanExecuteChanged();
        ShareRawCommand.NotifyCanExecuteChanged();
        AddSelectedToPatchCommand.NotifyCanExecuteChanged();
        ExportFolderRawCommand.NotifyCanExecuteChanged();
        ExportFolderImagesCommand.NotifyCanExecuteChanged();
        ConvertPakCommand.NotifyCanExecuteChanged();
        // 转换页把主 Pak 当作基底显示，需要跟着刷新。
        OnPropertyChanged(nameof(IsPakPathEmpty));
        SaveSettings();
    }

    partial void OnUsmapPathChanged(string value)
    {
        OnPropertyChanged(nameof(MappingSummary));
        OnPropertyChanged(nameof(NeedsMappingsForConversion));
        SaveSettings();
    }

    partial void OnMergeOutputPathChanged(string value)
    {
        MergePakCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    partial void OnSelectedMergePakChanged(MergePakItem? value) => SaveSettings();

    /// <summary>
    /// 合并输入列表的持久化形式：每行一个路径，保留顺序即保留优先级。
    /// （Android 上的 SAF 缓存路径跨重启仍有效，故可安全落盘。）
    /// </summary>
    private void PersistMergePakPaths()
    {
        _settings.MergePakPaths = MergePaks.Select(p => p.Path).ToList();
        _settings.MergeBaseLocresPath = MergeBaseLocresPath;
    }

    partial void OnAesKeyChanged(string value)
    {
        OnPropertyChanged(nameof(AesKeySummary));
        SaveSettings();
    }

    public void SaveWindowState()
    {
        _settings.WindowWidth = WindowWidth;
        _settings.WindowHeight = WindowHeight;
        SaveSettings();
    }

    /// <summary>
    /// Android 返回键：按页面层级返回。返回 false 表示已在主页，允许退出应用。
    /// 浏览页在子目录时先返回上级目录。
    /// </summary>
    public bool HandleBack()
    {
        if (CurrentView != "Home")
        {
            if (CurrentView == "Workspace" && IsBrowseTab && !string.IsNullOrEmpty(CurrentFolder))
            {
                _ = UpCommand.ExecuteAsync(null);
                return true;
            }

            CurrentView = "Home";
            return true;
        }

        return false;
    }

    /// <summary>
    /// 打包时若请求了 Oodle 压缩但本机缺 Oodle 原生库，说明已自动降级为不压缩。
    ///
    /// 这条日志很重要：repak_bind 找不到 Oodle 会 panic 并 abort 整个进程
    /// （Windows 上 0xC0000409，进程直接消失，.NET 拦不住），所以现在是提前探测 + 降级。
    /// </summary>
    private void LogOodleFallback(ModifiedPakPackResult result)
    {
        if (result.OodleRequestedButUnavailable)
        {
            AddLog($"警告：{result.Note}");
            AppLog.Warn("打包", result.Note ?? "Oodle 不可用，已降级为不压缩");
        }
    }

    private void SaveSettings()
    {
        if (!_loaded)
        {
            return;
        }

        _settings.ShowThumbnails = ShowThumbnails;
        _settings.ThumbnailDefaultApplied = true;
        _settings.UseOodleCompression = UseOodleCompression;
        _settings.AskBeforeReplace = AskBeforeReplace;
        _settings.IsAnimationsEnabled = IsAnimationsEnabled;
        _settings.SidebarCollapsed = IsSidebarCollapsed;
        _settings.Theme = ThemeMode;
        _settings.ExportDirectory = ExportDirectory;
        _settings.ExportDirectoryBookmark = OperatingSystem.IsAndroid() ? _exportDirectoryBookmark : string.Empty;
        _settings.PakPath = PakPath;
        _settings.UsmapPath = UsmapPath;
        PersistMergePakPaths();
        // Android 上输出目标是 SAF 会话句柄，不持久化（重启后需重新选择）
        _settings.MergeOutputPath = OperatingSystem.IsAndroid() ? string.Empty : MergeOutputPath;
        _settings.ConvertSourcePath = ConvertSourcePath;
        _settings.ConvertMergeAll = ConvertMergeAll;
        _settings.ConvertIncludeModFiles = ConvertIncludeModFiles;
        // 同 MergeOutputPath：Android 上是 SAF 会话句柄，重启后需重新选择。
        _settings.ConvertOutputPath = OperatingSystem.IsAndroid() ? string.Empty : ConvertOutputPath;
        _settings.AesKey = AesKey;
        _settings.AutoCheckUpdates = AutoCheckUpdates;
        _settings.DismissedUpdateVersion = DismissedUpdateVersion;
        AppSettingsStore.Save(_settings);
    }

    public bool CanPickReplacement => SelectedPatchItem is not null && !IsBusy;

    public bool CanRemovePatchItem => SelectedPatchItem is not null;

    /// <summary>BINKA 编码质量选项（0 最好、9 最省），给替换页的下拉用。</summary>
    public IReadOnlyList<int> AudioQualities { get; } = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

    /// <summary>
    /// 文件选择器的注入口：桌面与 Android 走系统选择器（需要真实 TopLevel），
    /// 无头测试注入一个"直接返回路径"的实现，就能把选择之后的链路一起测掉。
    /// 参数为（对话框标题, 通配符），返回 null 表示用户取消。
    /// </summary>
    public Func<string, string[], Task<string?>>? FilePickerOverride { get; set; }

    // ============ 合并页 ============

    /// <summary>
    /// 待合并的 Pak 列表。<b>顺序即覆盖优先级</b>：越靠后优先级越高，
    /// 路径冲突时后面的会覆盖前面的。
    /// </summary>
    public ObservableCollection<MergePakItem> MergePaks { get; } = [];

    [ObservableProperty]
    public partial MergePakItem? SelectedMergePak { get; set; }

    [ObservableProperty]
    public partial string MergeOutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AskBeforeReplace { get; set; } = true;

    [ObservableProperty]
    public partial string MergeStatus { get; set; } = "就绪";

    /// <summary>合并列表是否为空（驱动空状态提示）。</summary>
    public bool HasNoMergePaks => MergePaks.Count == 0;

    public bool HasMergePaks => MergePaks.Count > 0;

    /// <summary>合并页脚摘要：输入个数与覆盖顺序说明。</summary>
    public string MergeInputSummary => MergePaks.Count == 0
        ? string.Empty
        : $"{MergePaks.Count} 个输入 · 覆盖顺序：{string.Join(" → ", MergePaks.Select(p => p.OrderLabel))}" +
          "（数字越小越优先）";

    /// <summary>列表变化时刷新与合并相关的派生属性。</summary>
    private void NotifyMergeListChanged()
    {
        OnPropertyChanged(nameof(HasNoMergePaks));
        OnPropertyChanged(nameof(HasMergePaks));
        OnPropertyChanged(nameof(MergeInputSummary));
        MergePakCommand.NotifyCanExecuteChanged();
    }

    public bool CanMergePak => MergePaks.Count >= 1 && HasMergeOutput;

    /// <summary>
    /// 合并：基准本地化文件（游戏原版的 .locres，可选）。
    ///
    /// 设了它，就把合并输入里同名的 .locres 做三路合并：逐条与基准对比算出每份模组的
    /// 改动集，再按优先级叠加。不设则同名 .locres 退回整文件覆盖。
    /// </summary>
    [ObservableProperty]
    public partial string MergeBaseLocresPath { get; set; } = string.Empty;

    partial void OnMergeBaseLocresPathChanged(string value)
    {
        SaveSettings();
        OnPropertyChanged(nameof(HasMergeBaseLocres));
        OnPropertyChanged(nameof(MergeBaseLocresSummary));
    }

    public bool HasMergeBaseLocres => MergeBaseLocresPath.Length > 0;

    public string MergeBaseLocresSummary => HasMergeBaseLocres
        ? Path.GetFileName(MergeBaseLocresPath)
        : "未选择：同名 .locres 按整文件覆盖";

    [RelayCommand]
    private async Task BrowseMergeBaseLocresAsync()
    {
        string? path = await PickFileAsync(
            "选择基准本地化文件（游戏原版的 .locres）",
            ["*.locres"],
            MergeBaseLocresPath);

        if (path is not null)
        {
            MergeBaseLocresPath = path;
            StatusText = $"已选择基准本地化：{Path.GetFileName(path)}";
            AddLog($"合并基准本地化：{Path.GetFileName(path)}", "合并");
        }
    }

    [RelayCommand]
    private void ClearMergeBaseLocres()
    {
        MergeBaseLocresPath = string.Empty;
        MergeStatus = "已清除基准本地化，同名 .locres 将按整文件覆盖";
    }

    /// <summary>输出目标是否就绪（Android 上是 SAF 句柄，Windows 上是路径）。</summary>
    private bool HasMergeOutput => OperatingSystem.IsAndroid()
        ? _mergeOutputTarget is not null
        : MergeOutputPath.Length > 0;

    // ============ Pak 转换（主 Pak + 待转换 Pak） ============

    /// <summary>待转换的 Pak（提供新贴图）。</summary>
    [ObservableProperty]
    public partial string ConvertSourcePath { get; set; } = string.Empty;

    /// <summary>转换输出路径（Android 上仅用于显示，实际写 SAF 句柄）。</summary>
    [ObservableProperty]
    public partial string ConvertOutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConvertStatus { get; set; } = "就绪";

    /// <summary>已转换纹理的明细（失败项会列出原因）。</summary>
    public ObservableCollection<PakConversionRow> ConvertRows { get; } = [];

    /// <summary>true = 也把源 Pak 中主 Pak 没有的文件一并并入。</summary>
    [ObservableProperty]
    public partial bool ConvertMergeAll { get; set; }

    /// <summary>
    /// true = 把源 Pak 的其他文件（蓝图 / 本地化 / 数据表等双端通用内容）原样并入输出 Pak，
    /// 输出体积仍≈改动量。开启后不必再单独做一次 Pak 合并。
    /// </summary>
    [ObservableProperty]
    public partial bool ConvertIncludeModFiles { get; set; }

    // ============ 使用说明（独立页面） ============

    /// <summary>
    /// 文档章节（内容见 <see cref="Docs.PrismDocs"/>）。
    ///
    /// 原来是挂在 MainView 顶层的一个浮层：600px 宽的模态里塞图 + 长文，正文被
    /// 「知道了」按钮裁掉半截，浅色截图压在深色底上像一块白板。现在它是一个正经页面 ——
    /// 版式与欢迎页同一套（引题带 + 分隔线 + 编号小节），左边是章节列表，右边是正文与截图。
    /// </summary>
    public IReadOnlyList<Docs.DocChapter> DocChapters => Docs.PrismDocs.Chapters;

    [ObservableProperty]
    public partial Docs.DocChapter? SelectedChapter { get; set; }

    partial void OnSelectedChapterChanged(Docs.DocChapter? value)
    {
        OnPropertyChanged(nameof(DocBlocks));
        OnPropertyChanged(nameof(HasDocContent));
        OnPropertyChanged(nameof(DocChapterNumber));
        OnPropertyChanged(nameof(CanGoPreviousDoc));
        OnPropertyChanged(nameof(CanGoNextDoc));
        OnPropertyChanged(nameof(PreviousDocTitle));
        OnPropertyChanged(nameof(NextDocTitle));
    }

    public IReadOnlyList<Docs.DocBlock> DocBlocks => SelectedChapter?.Blocks ?? [];

    public bool HasDocContent => SelectedChapter is not null;

    /// <summary>章号（01 / 02 …）：欢迎页与配置页都用两位编号做视觉锚点。</summary>
    public string DocChapterNumber =>
        SelectedChapterIndex < 0 ? string.Empty : (SelectedChapterIndex + 1).ToString("D2");

    private int SelectedChapterIndex =>
        SelectedChapter is null ? -1 : DocChapters.ToList().IndexOf(SelectedChapter);

    public bool CanGoPreviousDoc => SelectedChapterIndex > 0;

    public bool CanGoNextDoc => SelectedChapterIndex >= 0 && SelectedChapterIndex < DocChapters.Count - 1;

    public string PreviousDocTitle => CanGoPreviousDoc ? DocChapters[SelectedChapterIndex - 1].Title : string.Empty;

    public string NextDocTitle => CanGoNextDoc ? DocChapters[SelectedChapterIndex + 1].Title : string.Empty;

    /// <summary>页头的「使用说明」按钮：带着当前页的主题跳进文档页。</summary>
    [RelayCommand]
    private void OpenDocs(string? topic)
    {
        Docs.DocChapter? chapter = DocChapters.FirstOrDefault(item =>
            string.Equals(item.Key, topic, StringComparison.Ordinal));

        SelectedChapter = chapter ?? SelectedChapter ?? DocChapters.FirstOrDefault();
        CurrentView = "Docs";
    }

    [RelayCommand]
    private void GoPreviousDoc()
    {
        if (CanGoPreviousDoc)
        {
            SelectedChapter = DocChapters[SelectedChapterIndex - 1];
        }
    }

    [RelayCommand]
    private void GoNextDoc()
    {
        if (CanGoNextDoc)
        {
            SelectedChapter = DocChapters[SelectedChapterIndex + 1];
        }
    }

    /// <summary>
    /// 文档里那张被点开的截图（null = 没开）。
    ///
    /// 正文里的图是缩略的（一整页截图按原尺寸铺满正文列，会把下面的文字挤出屏幕），
    /// 点一下开全屏看原尺寸 —— 想看清某个按钮叫什么时不用去开原图。
    /// </summary>
    [ObservableProperty]
    public partial IImage? DocImagePreview { get; set; }

    public bool IsDocImageOpen => DocImagePreview is not null;

    partial void OnDocImagePreviewChanged(IImage? value) => OnPropertyChanged(nameof(IsDocImageOpen));

    [RelayCommand]
    private void OpenDocImage(string? key) => DocImagePreview = Docs.DocImageProvider.Get(key);

    [RelayCommand]
    private void CloseDocImage() => DocImagePreview = null;

    /// <summary>
    /// 主题："system" / "light" / "dark"。
    ///
    /// 之所以给显式开关：浅色主题在 1920 屏幕上是一整片亮色，长时间看眼睛累，
    /// 而"想用深色"不该被迫去改系统设置。
    /// </summary>
    [ObservableProperty]
    public partial string ThemeMode { get; set; } = "system";

    public bool IsThemeSystem
    {
        get => ThemeMode == "system";
        set { if (value) { ThemeMode = "system"; } }
    }

    public bool IsThemeLight
    {
        get => ThemeMode == "light";
        set { if (value) { ThemeMode = "light"; } }
    }

    public bool IsThemeDark
    {
        get => ThemeMode == "dark";
        set { if (value) { ThemeMode = "dark"; } }
    }

    partial void OnThemeModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
        ApplyTheme(value);
        SaveSettings();

        // 文档截图按主题分两套（浅色图压在深色底上像一块白板）。换主题后要丢掉缓存，
        // 并且重建当前章节 —— 图片是通过转换器取的，不重建模板就还是旧主题那张。
        Docs.DocImageProvider.ClearCache();
        if (IsDocsVisible && SelectedChapter is { } chapter)
        {
            SelectedChapter = null;
            SelectedChapter = chapter;
        }
    }

    /// <summary>把主题选择落到 Application.RequestedThemeVariant（Default 即跟随系统）。</summary>
    private static void ApplyTheme(string mode)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = mode switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>设置页的主题分段控件（跟随系统 / 浅色 / 深色）。</summary>
    [RelayCommand]
    private void SetTheme(string? mode) => ThemeMode = string.IsNullOrWhiteSpace(mode) ? "system" : mode;

    // ============ 检查更新（GitHub 发行版） ============

    /// <summary>
    /// 上一次查到的发行版信息（null 表示还没查到 / 没有）。
    /// 注意：这里是"服务端的真相"，界面上还要再和 <see cref="DismissedUpdateVersion"/> 比一次。
    /// </summary>
    private UpdateInfo? _update;

    private UpdateAsset? _updateAsset;
    private CancellationTokenSource? _updateCts;

    /// <summary>启动时自动检查更新。关掉后仍可在设置页手动检查。</summary>
    [ObservableProperty]
    public partial bool AutoCheckUpdates { get; set; } = true;

    partial void OnAutoCheckUpdatesChanged(bool value) => SaveSettings();

    /// <summary>用户点过「稍后」的版本号。同一版本不再弹，出现更高版本时自然失效。</summary>
    [ObservableProperty]
    public partial string DismissedUpdateVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCheckingUpdate { get; set; }

    /// <summary>设置页里那行说明（检查中 / 已是最新 / 发现新版本 / 失败原因）。</summary>
    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = "尚未检查更新。";

    /// <summary>检查过至少一次（决定设置页要不要显示结果面板，别在没查时先占一块地方）。</summary>
    [ObservableProperty]
    public partial bool HasUpdateResult { get; set; }

    /// <summary>是否查到了比当前版本更高的发行版。</summary>
    [ObservableProperty]
    public partial bool HasUpdate { get; set; }

    [ObservableProperty]
    public partial string UpdateVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdateTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdatePublishedText { get; set; } = string.Empty;

    /// <summary>安装包描述，例如 <c>Prism-win-x64-1.3.0.zip · 124.7 MB</c>。</summary>
    [ObservableProperty]
    public partial string UpdatePackageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUpdateDownloading { get; set; }

    /// <summary>下载进度 0~1（只在下载期间有意义）。</summary>
    [ObservableProperty]
    public partial double UpdateProgress { get; set; }

    /// <summary>下载 / 解压 / 重启过程中的一句话状态。</summary>
    [ObservableProperty]
    public partial string UpdateActionStatus { get; set; } = string.Empty;

    partial void OnHasUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowUpdatePanel));
        OnPropertyChanged(nameof(UpdateBadgeText));
        OnPropertyChanged(nameof(UpdateIntroText));
        OnPropertyChanged(nameof(CanInstallUpdate));
        OnPropertyChanged(nameof(InstallButtonText));
    }

    partial void OnUpdateVersionChanged(string value)
    {
        OnPropertyChanged(nameof(ShowUpdatePanel));
        OnPropertyChanged(nameof(UpdateBadgeText));
        OnPropertyChanged(nameof(UpdateIntroText));
        OnPropertyChanged(nameof(InstallButtonText));
    }

    partial void OnIsUpdateDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallUpdate));
        OnPropertyChanged(nameof(InstallButtonText));
    }

    /// <summary>当前发行版的更新内容（欢迎页正文 / 原生对话框文案）。</summary>
    public ObservableCollection<UpdateNote> UpdateNoteBlocks { get; } = [];

    public bool HasUpdateNotes => UpdateNoteBlocks.Count > 0;

    /// <summary>这个版本已经被「稍后」忽略掉了。</summary>
    public bool IsUpdateDismissed =>
        UpdateVersion.Length > 0 && string.Equals(DismissedUpdateVersion, UpdateVersion, StringComparison.Ordinal);

    /// <summary>欢迎页整体换成更新说明（桌面）/ 显示更新卡片（手机）。</summary>
    public bool ShowUpdatePanel => HasUpdate && !IsUpdateDismissed;

    public string UpdateBadgeText => HasUpdate ? $"v{UpdateVersion}" : string.Empty;

    /// <summary>更新面板的说明行：说清从哪个版本升级，以及这一页为什么会变。</summary>
    public string UpdateIntroText =>
        $"从 v{VersionText} 升级。下面是这一版在 GitHub 上发布的更新内容，" +
        "装好之后这一页会恢复成正常的欢迎页。";

    public bool CanInstallUpdate => HasUpdate && !IsUpdateDownloading && _updateAsset is not null;

    /// <summary>
    /// 下载所需的两个前提都齐了：发行版信息 + 本平台的安装包。
    /// 按钮的 CanExecute 只看 <see cref="CanInstallUpdate"/>（它是"界面状态"），
    /// 这里额外要求 <c>_update</c> 也在 —— 下载代码真的会读它，缺一个就会在点下去之后
    /// 才报「没有可下载的安装包」。曾经漏掉 <c>_update</c> 的赋值，就是这个症状。
    /// </summary>
    public bool HasInstallTarget => _update is not null && _updateAsset is not null;

    public string InstallButtonText => IsUpdateDownloading
        ? "正在更新…"
        : OperatingSystem.IsAndroid() ? "下载 APK" : "下载并自动更新";

    /// <summary>原生「发现新版本」对话框（Android 注入；桌面不注入，走欢迎页）。</summary>
    public Func<string, string, string, string, Task<bool>>? NativeUpdateDialogAsync { get; set; }

    /// <summary>拉起系统安装器（Android 注入；桌面走就地替换）。参数为下载好的 apk 路径。</summary>
    public Func<string, Task>? NativeInstallApkAsync { get; set; }

    /// <summary>启动流程调用：按设置与节流策略在后台查一次更新（失败静默，只写日志）。</summary>
    public void BeginStartupUpdateCheck()
    {
        if (!AutoCheckUpdates)
        {
            return;
        }

        if (DateTimeOffset.TryParse(_settings.LastUpdateCheckUtc, out DateTimeOffset last) &&
            DateTimeOffset.UtcNow - last < TimeSpan.FromHours(6))
        {
            // 6 小时内查过就不再打扰 GitHub（手动检查不受限制）
            return;
        }

        // 整段都回到 UI 线程上跑：检查过程会改一堆被绑定的属性（版本号、说明块、
        // 面板显隐、页面跳转）。以前这里是 Task.Run + ConfigureAwait(false)，
        // 那些属性是从线程池线程上改的 —— 界面会不会刷新全靠运气。
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                // 延时 1.5 秒让首屏先画出来，别和启动争抢
                await Task.Delay(TimeSpan.FromSeconds(1.5));
                await CheckForUpdatesAsync(manual: false);
            }
            catch
            {
                // 自动检查失败永远不该影响使用
            }
        });
    }

    /// <summary>
    /// 自动查到更新时能否把用户直接带到欢迎页：前提是"刚启动、还没干活"。
    /// 已经打开了 Pak、有替换任务、或正在忙的时候跳走会把用户从做的事上拽开。
    /// </summary>
    private bool CanLandOnWelcomeForUpdate =>
        !IsBusy &&
        !_session.IsOpen &&
        PatchItems.Count == 0 &&
        CurrentView is "Workspace" or "Home";

    [RelayCommand]
    private Task CheckUpdateAsync() => CheckForUpdatesAsync(manual: true);

    /// <summary>
    /// 查询发行版。<paramref name="manual"/> 为 true（设置页点按钮）时，
    /// 即使当前版本已被「稍后」忽略也会重新展示；Android 上会弹原生对话框。
    /// </summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (IsCheckingUpdate)
        {
            return;
        }

        _updateCts?.Cancel();
        _updateCts = new CancellationTokenSource();
        CancellationToken token = _updateCts.Token;

        IsCheckingUpdate = true;
        UpdateStatus = manual ? "正在检查…" : "正在检查更新…";
        try
        {
            _update = null;
            UpdateCheckResult result = await UpdateService
                .CheckAsync(ParseCurrentVersion(), token)
                .ConfigureAwait(true);

            UpdateStatus = result.Message;
            if (result.Update is { } info)
            {
                ApplyUpdate(info, manual);
            }
            else
            {
                ClearUpdate();
            }

            if (result.Succeeded)
            {
                _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow.ToString("O");
                SaveSettings();
            }

            if (!result.Succeeded)
            {
                AppLog.Warn("更新", result.Message);
            }
            else if (result.HasUpdate)
            {
                AppLog.Info("更新", $"发现新版本 v{result.Update!.Version}（{result.Update.AssetSummary}）");
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatus = "已取消检查。";
        }
        finally
        {
            IsCheckingUpdate = false;
            HasUpdateResult = true;
            RefreshLog();
        }
    }

    /// <summary>
    /// 不联网地把一段发行说明当成「发现新版本」展示出来（截图与离线测试用）。
    ///
    /// 更新面板的全部绑定都能在没有网络的情况下被验证：解析、渲染、按钮文案、下载前的
    /// 状态。传了 <paramref name="assetName"/> 就会显示「下载并自动更新」按钮。
    /// </summary>
    public void ShowUpdatePreview(string version, string title, string notes, string? assetName = null, long assetSize = 0)
    {
        var info = new UpdateInfo(
            Tag: $"v{version}",
            Version: version,
            Title: title,
            Notes: notes,
            PublishedAt: DateTimeOffset.Now,
            HtmlUrl: UpdateService.ReleasesPageUrl,
            Assets: []);

        ApplyUpdate(info, manual: true);
        if (assetName is { Length: > 0 })
        {
            _updateAsset = new UpdateAsset(assetName, assetSize, UpdateService.ReleasesPageUrl, null);
            UpdatePackageText = $"{assetName} · {_updateAsset.SizeText}";
        }

        UpdateStatus = $"发现新版本 v{version}。";
        HasUpdateResult = true;
        OnPropertyChanged(nameof(CanInstallUpdate));
    }

    /// <summary>
    /// 清掉 <see cref="ShowUpdatePreview"/> 造出来的状态（截图流程在截完更新面板后调用）。
    /// 只是把界面恢复成"还没查过"，不写设置、不影响真实的检查结果。
    /// </summary>
    public void HideUpdatePreview()
    {
        ClearUpdate();
        UpdateStatus = "尚未检查更新。";
        UpdateActionStatus = string.Empty;
        UpdateProgress = 0;
        HasUpdateResult = false;
    }

    /// <summary>
    /// 把查到的发行版落到界面状态上，并在 Android 上弹一次可跳过的对话框。
    /// <paramref name="manual"/> 为 false 表示这是启动时的自动检查。
    /// </summary>
    internal void ApplyUpdate(UpdateInfo info, bool manual)
    {
        // 记住这份发行版信息 —— 下载、校验、"在 GitHub 上查看" 都要用它。
        // 曾经漏了这一行：界面（版本号 / 说明 / 安装包名）全靠下面这些字段拼出来，
        // 看起来一切正常，但点「下载并自动更新」时守卫读到 _update 是空的，
        // 直接弹「没有可下载的安装包」—— 查到新版本却永远下不了。
        _update = info;

        UpdateVersion = info.Version;
        UpdateTitle = info.Title;
        UpdatePublishedText = info.PublishedText;
        UpdateNoteBlocks.Clear();
        foreach (UpdateNote note in info.NoteBlocks)
        {
            UpdateNoteBlocks.Add(note);
        }

        OnPropertyChanged(nameof(HasUpdateNotes));

        _updateAsset = UpdateService.SelectAsset(info);
        UpdatePackageText = _updateAsset is null
            ? "发行版没有可下载的安装包，请到 GitHub 下载。"
            : $"{_updateAsset.Name} · {_updateAsset.SizeText}";

        // 用户已经为这个版本点过「稍后」，自动检查就不要再翻出来；
        // 手动检查必须展示，否则"点了没反应"。
        if (!manual && string.Equals(DismissedUpdateVersion, info.Version, StringComparison.Ordinal))
        {
            HasUpdate = false;
            return;
        }

        HasUpdate = true;

        // 启动时自动查到新版本：直接把用户带到欢迎页 —— 那一页整页就是这一版的
        // 更新说明。默认落点是「解包 & 模组」，不跳的话用户根本看不到这页。
        // 手机端交给原生对话框，不切页面。
        if (!manual && !OperatingSystem.IsAndroid() && CanLandOnWelcomeForUpdate)
        {
            CurrentView = "Home";
        }

        if (OperatingSystem.IsAndroid())
        {
            _ = PromptAndroidUpdateAsync(info);
        }
    }

    /// <summary>Android：原生对话框（可跳过），确认后走下载 + 安装器。</summary>
    private async Task PromptAndroidUpdateAsync(UpdateInfo info)
    {
        if (NativeUpdateDialogAsync is null)
        {
            return;
        }

        string body = InfoMessageForDialog(info);
        bool install;
        try
        {
            install = await NativeUpdateDialogAsync(
                $"发现新版本 v{info.Version}",
                body,
                "立即更新",
                "稍后");
        }
        catch
        {
            return;
        }

        if (install)
        {
            await InstallUpdateAsync().ConfigureAwait(true);
        }
        else
        {
            DismissUpdate();
        }
    }

    private string InfoMessageForDialog(UpdateInfo info)
    {
        string notes = UpdateNotes.ToPlainText(UpdateNoteBlocks.Take(12).ToList());
        return notes.Length == 0
            ? $"{info.Title}\n\n发布于 {info.PublishedText}。是否现在下载？"
            : $"{notes}\n\n发布于 {info.PublishedText}。是否现在下载？";
    }

    private void ClearUpdate()
    {
        HasUpdate = false;
        UpdateVersion = string.Empty;
        UpdateTitle = string.Empty;
        UpdatePublishedText = string.Empty;
        UpdatePackageText = string.Empty;
        UpdateNoteBlocks.Clear();
        OnPropertyChanged(nameof(HasUpdateNotes));
        _update = null;
        _updateAsset = null;
    }

    /// <summary>「稍后」：记住这个版本号，回到正常欢迎页。</summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        if (UpdateVersion.Length > 0)
        {
            DismissedUpdateVersion = UpdateVersion;
            SaveSettings();
        }

        OnPropertyChanged(nameof(IsUpdateDismissed));
        OnPropertyChanged(nameof(ShowUpdatePanel));
        UpdateActionStatus = string.Empty;
    }

    /// <summary>在系统浏览器里打开发行版页面（拿不到安装包时的兜底）。</summary>
    [RelayCommand]
    private async Task OpenReleasePageAsync()
    {
        string url = _update?.HtmlUrl is { Length: > 0 } html ? html : UpdateService.ReleasesPageUrl;
        try
        {
            if (TopLevel?.Launcher is { } launcher && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                await launcher.LaunchUriAsync(uri);
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("更新", $"打开浏览器失败：{ex.Message}");
        }

        UpdateActionStatus = $"请手动访问：{url}";
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_update is not { } info || _updateAsset is not { } asset)
        {
            UpdateActionStatus = "没有可下载的安装包，请用「在 GitHub 上查看」。";
            return;
        }

        if (IsUpdateDownloading)
        {
            return;
        }

        IsUpdateDownloading = true;
        UpdateProgress = 0;
        UpdateActionStatus = OperatingSystem.IsAndroid() ? "正在下载 APK…" : "正在下载更新包…";
        var progress = new Progress<double>(value => UpdateProgress = value);
        try
        {
            if (OperatingSystem.IsAndroid())
            {
                await InstallOnAndroidAsync(info, asset, progress).ConfigureAwait(true);
                return;
            }

            var status = new Progress<string>(text => UpdateActionStatus = text);
            UpdatePrepareResult prepared = await UpdateInstaller
                .PrepareAsync(info, asset, progress, status, CancellationToken.None)
                .ConfigureAwait(true);

            UpdateActionStatus = prepared.Message;
            if (!prepared.Succeeded)
            {
                AppLog.Error("更新", prepared.Message);
                RefreshLog();
                if (prepared.PackagePath.Length > 0 && File.Exists(prepared.PackagePath))
                {
                    // 不能自动替换（目录只读之类），至少把下载好的包交给用户
                    _ = OpenContainingFolderAsync(prepared.PackagePath);
                }

                return;
            }

            AppLog.Info("更新", $"已准备 v{info.Version}，正在重启完成替换");
            RefreshLog();
            UpdateActionStatus = "正在重启完成更新…";
            await Task.Delay(1200).ConfigureAwait(true);
            if (!UpdateInstaller.LaunchApplier(prepared.ApplierPath, Path.GetDirectoryName(prepared.ApplierPath) ?? Path.GetTempPath()))
            {
                UpdateActionStatus = $"无法启动更新脚本，请手动解压 {prepared.PackagePath}。";
                return;
            }

            ShutdownApplication();
        }
        catch (Exception ex)
        {
            UpdateActionStatus = $"更新失败：{ex.Message}";
            AppLog.Error("更新", ex.Message);
            RefreshLog();
        }
        finally
        {
            IsUpdateDownloading = false;
        }
    }

    private async Task InstallOnAndroidAsync(UpdateInfo info, UpdateAsset asset, IProgress<double> progress)
    {
        string path = Path.Combine(Path.GetTempPath(), asset.Name);
        try
        {
            await UpdateService.DownloadAsync(asset, path, progress, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            UpdateActionStatus = $"下载失败：{ex.Message}";
            AppLog.Error("更新", UpdateActionStatus);
            RefreshLog();
            return;
        }

        UpdateActionStatus = "下载完成，正在打开安装器…";
        if (NativeInstallApkAsync is null)
        {
            UpdateActionStatus = $"APK 已下载到 {path}，请手动安装。";
            return;
        }

        try
        {
            await NativeInstallApkAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            UpdateActionStatus = $"无法打开安装器：{ex.Message}（APK 在 {path}）";
        }
    }

    private async Task OpenContainingFolderAsync(string filePath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("更新", $"打开目录失败：{ex.Message}");
        }
    }

    /// <summary>关掉桌面窗口（自更新脚本在等本进程退出）。</summary>
    private static void ShutdownApplication()
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>当前程序集版本；解析失败时返回 null（此时把任何发行版都视为更新）。</summary>
    private static Version? ParseCurrentVersion() =>
        Version.TryParse(
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? string.Empty,
            out Version? version)
            ? version
            : null;

    /// <summary>设置页「关于」里的版本行：<c>v1.3.0 · Windows x64</c>。</summary>
    public string VersionLine =>
        $"v{VersionText} · {(OperatingSystem.IsAndroid() ? "Android" : "Windows")} " +
        $"{(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.X64 ? "x64" : System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant())}";

    /// <summary>
    /// 浏览页搜索框宽度：宽屏给足，避免占位提示被截断（原来写死 170 太窄）。
    /// </summary>
    public double SearchBoxWidth => IsWideLayout ? 360 : 230;

    public bool CanConvertPak => IsPakOpen
        && ConvertSourcePath.Length > 0
        && HasConvertOutput
        && !IsBusy;

    private bool HasConvertOutput => OperatingSystem.IsAndroid()
        ? _convertOutputTarget is not null
        : ConvertOutputPath.Length > 0;

    private IStorageFile? _convertOutputTarget;

    /// <summary>转换页是否尚未选择主 Pak（用于红色提示）。</summary>
    public bool IsPakPathEmpty => string.IsNullOrWhiteSpace(PakPath);

    public bool HasConvertRows => ConvertRows.Count > 0;

    /// <summary>AES 密钥摘要（不回显密钥本身，只说明是否已填写）。</summary>
    public string AesKeySummary => string.IsNullOrWhiteSpace(AesKey) ? "AES：未填写" : "AES：已填写";

    /// <summary>映射文件摘要（含 usmap / jmap 格式标注）。</summary>
    public string MappingSummary => string.IsNullOrWhiteSpace(UsmapPath)
        ? "映射文件：未选择"
        : $"映射：{PakTool.Core.MappingsLoader.DescribeFormat(UsmapPath)}";

    /// <summary>是否需要提醒用户补映射文件：跨格式重编码要解出源纹理像素，
    /// 未版本化资产必须有映射。未选映射时在转换页给出显式警告。</summary>
    public bool NeedsMappingsForConversion => string.IsNullOrWhiteSpace(UsmapPath);

    /// <summary>
    /// 转换前置条件：只报编码器可用性。
    /// 工作方式的解释放在「使用说明」浮层里，页面上不重复。
    /// </summary>
    public string ConvertServiceStatus
    {
        get
        {
            if (OperatingSystem.IsAndroid())
            {
                return "编码器：应用内（libprism_codecs）";
            }

            Services.UAssetCliRunner? runner = Services.UAssetCliRunner.CreateCached();
            if (runner is null)
            {
                return "⚠ 未找到 UAssetCLI，桌面端无法重新编码纹理（详见 README 构建章节）";
            }

            return $"编码器：UAssetCLI 可用 · astcenc {(runner.HasAstcenc ? "可用" : "缺失")}"
                 + $" · texconv {(runner.HasTexconv ? "可用" : "缺失")}";
        }
    }

    [RelayCommand]
    private void ClearConvertRows()
    {
        ConvertRows.Clear();
        OnPropertyChanged(nameof(HasConvertRows));
        ConvertStatus = "就绪";
    }

    partial void OnConvertSourcePathChanged(string value)
    {
        ConvertPakCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    partial void OnConvertOutputPathChanged(string value)
    {
        ConvertPakCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    partial void OnConvertMergeAllChanged(bool value) => SaveSettings();

    partial void OnConvertIncludeModFilesChanged(bool value) => SaveSettings();

    // ============ 打开 ============

    [RelayCommand]
    private async Task OpenPakAsync()
    {
        if (string.IsNullOrWhiteSpace(PakPath))
        {
            StatusText = "请先选择 Pak 文件。";
            return;
        }

        await RunBusyAsync(async () =>
        {
            PakOpenResult result;
            await _gate.WaitAsync();
            try
            {
                _session.Dispose();
                _session = new PakArchiveSession();
                result = await _session.OpenAsync(new PakOpenOptions(
                    [PakPath],
                    NullIfWhiteSpace(AesKey),
                    NullIfWhiteSpace(UsmapPath)));
            }
            finally
            {
                _gate.Release();
            }

            StatusText = $"已打开 {Path.GetFileName(PakPath)}：{result.FileCount:N0} 个文件。";
            AddLog($"打开 Pak：{Path.GetFileName(PakPath)}（{result.FileCount:N0} 个文件）");
            CurrentTabIndex = 1;
            await NavigateToAsync(string.Empty);
        });
    }

    // ============ 浏览导航 ============

    [RelayCommand(CanExecute = nameof(CanUp))]
    private async Task UpAsync()
    {
        PrioritizeUserAction();
        try
        {
            // 通过搜索框跳转进来时，"返回上一级"回到跳转前所在的目录，
            // 而不是机械地取路径的父目录 —— 那通常不是用户想回去的地方。
            string target = _returnFolder ?? ParentOf(CurrentFolder);
            _returnFolder = null;
            await NavigateToAsync(target);
        }
        finally
        {
            ResumeThumbnailsAfterUserAction();
        }
    }

    /// <summary>
    /// 由搜索/路径跳转设置的"返回目标"。非空时 <see cref="UpAsync"/> 优先回到它。
    /// </summary>
    private string? _returnFolder;

    /// <summary>双击目录项进入，双击文件项预览。</summary>
    [RelayCommand]
    private async Task ActivateAsync()
    {
        EntryItem? item = SelectedItem;
        if (item is null || !IsPakOpen)
        {
            return;
        }

        PrioritizeUserAction();
        try
        {
            if (item.IsDirectory)
            {
                // 用户主动进入子目录：这是一次新的浏览，清掉上一次搜索遗留的返回目标，
                // 否则之后按"返回"会跳回更早的位置而不是上一级。
                await NavigateToAsync(item.FullPath);
            }
            else
            {
                await PreviewAsync(item);
            }
        }
        finally
        {
            ResumeThumbnailsAfterUserAction();
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (!IsPakOpen)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            await NavigateToAsync(CurrentFolder);
            return;
        }

        string query = SearchQuery.Trim();

        await RunBusyAsync(async () =>
        {
            // 先尝试把输入当作 Pak 内路径。命中则直接跳转（并清空搜索框），
            // 否则回退为普通关键词搜索 —— 这样两种用法共用同一个输入框。
            BrowsePathResolution? resolved = null;
            await _gate.WaitAsync();
            try
            {
                resolved = await _session.TryResolveBrowsePathAsync(query);
            }
            catch (Exception ex)
            {
                // 路径解析失败不应阻塞搜索，记录后继续。
                Services.AppLog.Exception(ex, $"路径解析失败：{query}", "浏览", LogLevel.Debug);
            }
            finally
            {
                _gate.Release();
            }

            if (resolved is not null)
            {
                await NavigateFromSearchAsync(resolved);
                return;
            }

            IReadOnlyList<ArchiveEntryDto> results;
            await _gate.WaitAsync();
            try
            {
                results = await _session.SearchAsync(query, 500);
            }
            finally
            {
                _gate.Release();
            }

            Entries = new ObservableCollection<EntryItem>(results.Select(EntryItem.Create));
            CurrentPathText = $"搜索：{query}";
            StatusText = $"搜索命中 {results.Count:N0} 项。";
            AddLog($"搜索“{query}”命中 {results.Count:N0} 项");
            ClearPreview();
            StartThumbnails();
        });
    }

    /// <summary>
    /// 依据路径解析结果跳转：目录直接进入；文件进入其所在目录并选中该文件。
    /// 跳转后清空搜索框，并记住"上一级"应回到输入路径之前的目录。
    /// </summary>
    private async Task NavigateFromSearchAsync(BrowsePathResolution resolved)
    {
        string from = CurrentFolder;
        string target = resolved.Folder;

        await NavigateToAsync(target, returnTo: from);

        string? selectedName = resolved.FileName;
        if (selectedName is not null)
        {
            EntryItem? match = Entries.FirstOrDefault(e =>
                !e.IsDirectory && string.Equals(e.Name, selectedName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                SelectedItem = match;
            }
        }

        // 清空搜索框：这样"搜索 → 跳转 → 再输入"不会残留上一次的词。
        SearchQuery = string.Empty;

        StatusText = selectedName is null
            ? $"已跳转到 /{target}"
            : $"已定位到 {selectedName}";
        AddLog($"路径跳转：{CurrentPathText}（输入 {target.TrimEnd('/')}）");
    }

    /// <summary>
    /// 导航到指定目录。
    /// </summary>
    /// <param name="folder">目标目录（Pak 内路径，空串表示根）。</param>
    /// <param name="returnTo">
    /// 「返回上一级」的目标。仅在<b>紧接的下一次</b>返回时生效，用后即清。
    /// 搜索/路径跳转会传入跳转前所在目录，让返回键回到用户原本的位置；
    /// 其余导航不传，返回键按常规取其父目录。
    /// </param>
    private async Task NavigateToAsync(string folder, string? returnTo = null)
    {
        // 记住"正要离开哪个目录"：回到上一级时要把这一项选中并滚到可见处，
        // 否则列表会停在原来的滚动位置，用户得自己滑半天找回刚出来的那个文件夹。
        string leaving = CurrentFolder;

        IReadOnlyList<ArchiveEntryDto> entries = await ListFolderAsync(folder);
        Entries = new ObservableCollection<EntryItem>(entries.Select(EntryItem.Create));
        CurrentFolder = folder;

        EntryItem? cameFrom = null;
        if (!string.IsNullOrEmpty(leaving) && PathsEqual(ParentOf(leaving), folder))
        {
            cameFrom = Entries.FirstOrDefault(entry => entry.IsDirectory && PathsEqual(entry.FullPath, leaving));
        }

        // 选中/滚动都得等列表把新条目装好：直接设 SelectedItem，ListBox 重建容器后
        // 会把它重置成 null（双向绑定再写回来），选中项和滚动位置一起丢掉。
        EntryItem? target = cameFrom;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (target is not null)
                {
                    SelectedItem = target;
                }

                RequestScrollToEntry(target);
            },
            Avalonia.Threading.DispatcherPriority.Loaded);
        CurrentPathText = "/" + folder;
        _returnFolder = returnTo;
        ClearPreview();
        StartThumbnails();
    }

    /// <summary>供列表按目录加载。</summary>
    public async Task<IReadOnlyList<ArchiveEntryDto>> ListFolderAsync(string folder)
    {
        await _gate.WaitAsync();
        try
        {
            return await _session.ListAsync(folder);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 列表需要滚到某一项（null = 滚回顶部）。由 <c>FileBrowserView</c> 订阅：
    /// 视图模型不碰控件，滚动交给视图做。
    /// </summary>
    public event Action<EntryItem?>? ScrollToEntryRequested;

    private void RequestScrollToEntry(EntryItem? item) => ScrollToEntryRequested?.Invoke(item);

    /// <summary>Pak 内路径比较：忽略大小写与首尾斜杠。</summary>
    private static bool PathsEqual(string? left, string? right)
    {
        static string Normalize(string? value) =>
            string.IsNullOrEmpty(value) ? string.Empty : value.Replace('\\', '/').Trim('/');

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string ParentOf(string folder)
    {
        string trimmed = folder.TrimEnd('/');
        int idx = trimmed.LastIndexOf('/');
        return idx <= 0 ? string.Empty : trimmed[..(idx + 1)];
    }

    // ============ 预览 ============

    private async Task PreviewAsync(EntryItem item)
    {
        await RunBusyAsync(async () =>
        {
            AssetPreviewDto preview;
            await _gate.WaitAsync();
            try
            {
                preview = await _session.ReadPreviewAsync(item.FullPath);
            }
            finally
            {
                _gate.Release();
            }

            PreviewTitle = preview.Title;
            PreviewText = preview.Text ?? string.Empty;
            PreviewImage = null;
            ModelPreview = null;
            // 音视频的 Data 是原始载荷（mp4/wav 字节），拿去当图片解会炸在 Skia 里，
            // 之前的"选中视频报 NRE"就是这么来的。只对真图片解，解不出来就算了。
            bool isMediaPayload = preview.Kind is "video" or "audio";
            if (!isMediaPayload && preview.Data is { Length: > 0 } imageData && TryDecodePreviewImage(imageData, out Bitmap? previewImage))
            {
                PreviewImage = previewImage;
            }

            List<DetailItem> details = preview.Details.Select(d => new DetailItem(d.Label, d.Value)).ToList();
            PreviewEmptyText = "此文件无预览";

            // 模型：保存几何数据供 3D 线框预览，并追加几何信息
            if (preview.Model is { } model)
            {
                ModelPreview = model;
                details.Add(new DetailItem("网格类型", model.MeshType));
                details.Add(new DetailItem("顶点数", $"{model.VertexCount:N0}"));
                details.Add(new DetailItem("三角形", $"{model.TriangleCount:N0}"));
                details.Add(new DetailItem("分段数", $"{model.Sections.Count:N0}"));
            }

            // 音频：加载可播放的 WAV
            StopAudioPreview();
            AudioDuration = 0;
            AudioPosition = 0;
            if (string.Equals(preview.Kind, "audio", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // 预览里的 Data 已经是解好的 WAV（BINKA 等交给解码库解过）；
                    // ReadAudioPayloadAsync 返回的是原始载荷（BINKA 裸流），拿去当 WAV 放没有声音。
                    byte[]? playable = preview.Data is { Length: > 0 } decoded ? decoded : null;
                    string format = "wav";
                    if (playable is null)
                    {
                        AudioPayloadDto audio = await ReadAudioPayloadAsync(item.FullPath);
                        playable = audio.Data;
                        format = audio.Format;
                    }

                    if (playable is { Length: > 0 })
                    {
                        _audioTempFile = Path.Combine(Path.GetTempPath(), "prism-desktop-audio", $"{Guid.NewGuid():N}.wav");
                        Directory.CreateDirectory(Path.GetDirectoryName(_audioTempFile)!);
                        await File.WriteAllBytesAsync(_audioTempFile, playable);
                        // 先把进度条填上：只读 WAV 头算时长，不用整段解一遍
                        if (WavCodec.TryReadSeconds(playable, out var audioSeconds))
                        {
                            AudioDuration = audioSeconds;
                        }

                        AudioStatus = $"{format}  ·  {FormatSize(playable.Length)}";
                        HasAudioPreview = true;
                    }
                }
                catch (Exception ex)
                {
                    HasAudioPreview = false;
                    AudioStatus = $"音频加载失败：{ex.Message}";
                }
            }
            else
            {
                HasAudioPreview = false;
            }

            // 视频：解到临时目录后用 ffmpeg 出封面帧并内嵌播放
            if (string.Equals(preview.Kind, "video", StringComparison.OrdinalIgnoreCase))
            {
                await PrepareVideoPreviewAsync(item);
            }

            PreviewDetails = new ObservableCollection<DetailItem>(details);
            SelectedPathText = item.FullPath;
            _selectedLocresPath = preview.Locres is not null ? item.FullPath : null;
            HasLocresPreview = _selectedLocresPath is not null;
            HasPreview = true;
            StatusText = preview.Title;
        });
    }

    private string? _selectedLocresPath;

    /// <summary>当前预览是否为 locres（决定是否显示「导出 JSON」入口）。</summary>
    [ObservableProperty]
    public partial bool HasLocresPreview { get; set; }

    /// <summary>
    /// 把当前预览的 locres 导出为 JSON。
    /// 桌面直接写文件；Android 走 SAF 保存流并唤起系统分享。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasLocresPreview))]
    private async Task ExportLocresJsonAsync()
    {
        string? path = _selectedLocresPath;
        if (path is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            byte[] locresData;
            await _gate.WaitAsync();
            try
            {
                locresData = await _session.ReadFileAsync(path);
            }
            finally
            {
                _gate.Release();
            }

            await WriteLocresJsonAsync(locresData, Path.GetFileNameWithoutExtension(path));
        });
    }

    /// <summary>
    /// 把替换任务中的 locres 导出为 JSON（读原始文件，含尚未写回的编辑内容）。
    /// </summary>
    [RelayCommand]
    private async Task ExportPatchLocresJsonAsync(PatchItem? patch)
    {
        if (patch is null || !patch.IsLocres)
        {
            return;
        }

        string? originalPath = patch.OriginalFiles
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Value)
            .FirstOrDefault();
        if (originalPath is null || !File.Exists(originalPath))
        {
            StatusText = "找不到本地化原始文件，无法导出。";
            return;
        }

        await RunBusyAsync(async () =>
        {
            byte[] locresData = await File.ReadAllBytesAsync(originalPath);

            // 优先导出当前编辑结果：条目已被改写但尚未写回磁盘时，直接序列化编辑态。
            if (patch.LocresEntries.Count > 0)
            {
                locresData = PakTool.Core.LocresResourceCodec.ApplyTranslations(
                    locresData,
                    patch.LocresEntries.Select(e => e.ToDto()).ToList());
            }

            await WriteLocresJsonAsync(locresData, Path.GetFileNameWithoutExtension(originalPath));
        });
    }

    /// <summary>
    /// 公共落盘逻辑：桌面写导出目录，Android 走 SAF 保存 + 系统分享。
    /// </summary>
    private async Task WriteLocresJsonAsync(byte[] locresData, string baseName)
    {
        byte[] json = PakTool.Core.LocresJsonExporter.ToJsonBytes(locresData);
        string suggested = SanitizeFileName(baseName) + ".json";

        if (OperatingSystem.IsAndroid())
        {
            TopLevel? top = TopLevel;
            if (top is null)
            {
                return;
            }

            IStorageFile? target = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出本地化 JSON",
                SuggestedFileName = suggested,
                FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });
            if (target is null)
            {
                return;
            }

            // 先落缓存再流入 SAF，随后分享（与补丁 Pak 的保存流程一致）。
            string tempPath = Path.Combine(Path.GetTempPath(), $"prism-locres-{Guid.NewGuid():N}.json");
            await File.WriteAllBytesAsync(tempPath, json);
            try
            {
                await using (Stream dst = await target.OpenWriteAsync())
                {
                    await dst.WriteAsync(json);
                }

                AddLog($"本地化 JSON 已导出：{target.Name}（{json.Length:N0} 字节）");
                StatusText = $"本地化 JSON 已导出：{target.Name}";
                try
                {
                    await InvokeNativeShareAsync([tempPath], $"Prism 本地化 JSON：{target.Name}");
                }
                catch
                {
                    // 分享失败不影响已保存的文件。
                }
            }
            finally
            {
                TryDeleteFile(tempPath);
            }

            return;
        }

        string? targetDirectory = OperatingSystem.IsAndroid()
            ? (string.IsNullOrWhiteSpace(ExportDirectory) ? null : ExportDirectory)
            : await ResolveExportDirectoryAsync();
        if (targetDirectory is null)
        {
            StatusText = "已取消导出：还没有选择导出目录。";
            return;
        }

        string outputPath = Path.Combine(targetDirectory, suggested);
        await File.WriteAllBytesAsync(outputPath, json);
        AddLog($"本地化 JSON 已导出：{outputPath}（{json.Length:N0} 字节）");
        StatusText = $"本地化 JSON 已导出：{outputPath}";
    }

    private async Task<AudioPayloadDto> ReadAudioPayloadAsync(string path)
    {
        await _gate.WaitAsync();
        try
        {
            return await _session.ReadAudioPayloadAsync(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StopAudioPreview()
    {
        string? oldAudioFile = _audioTempFile;
        _audioTempFile = null;
        IsAudioSeeking = false;
        IsAudioPaused = false;
        AudioDuration = 0;
        AudioPosition = 0;

        if (OperatingSystem.IsAndroid())
        {
            IsAudioPlaying = false;
            if (NativeAudioStopAsync is not null)
            {
                if (oldAudioFile is not null)
                {
                    string fileToDelete = oldAudioFile;
                    _ = NativeAudioStopAsync().ContinueWith(
                        _ => TryDeleteFile(fileToDelete),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                }
                else
                {
                    _ = NativeAudioStopAsync();
                }
            }
            else if (oldAudioFile is not null)
            {
                TryDeleteFile(oldAudioFile);
            }
        }
        else
        {
            _audioProgressTimer?.Stop();
            _audioPlayer.Close();
            IsAudioPlaying = false;
            if (oldAudioFile is not null)
            {
                TryDeleteFile(oldAudioFile);
            }
        }

        HasAudioPreview = false;
    }

    // ============ 导出 ============

    [RelayCommand(CanExecute = nameof(CanExportRaw))]
    private async Task ExportRawAsync()
    {
        EntryItem? item = SelectedItem;
        if (item is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            if (OperatingSystem.IsAndroid())
            {
                IReadOnlyDictionary<string, byte[]> rawFiles;
                await _gate.WaitAsync();
                try
                {
                    rawFiles = await _session.ReadRelatedRawFilesAsync(item.FullPath);
                }
                finally
                {
                    _gate.Release();
                }

                if (rawFiles.Count == 0)
                {
                    StatusText = "该资源没有可导出的关联文件。";
                    return;
                }

                if (!await EnsureAndroidExportFolderAsync())
                {
                    StatusText = "请先在设置中选择导出目录。";
                    return;
                }

                var files = rawFiles.Select(pair => new PreviewExportFileDto(
                    Path.GetFileName(pair.Key),
                    "application/octet-stream",
                    pair.Value)).ToArray();
                int written = await WriteFilesToAndroidExportFolderAsync(files);
                StatusText = $"已导出 {written:N0} 个原始文件到 {ExportDirectory}";
                return;
            }

            string? targetDirectory = await ResolveExportDirectoryAsync();
            if (targetDirectory is null)
            {
                StatusText = "已取消导出：还没有选择导出目录。";
                return;
            }

            ExportResult result;
            await _gate.WaitAsync();
            try
            {
                result = await _session.ExportAsync(new ExportRequest([item.FullPath], targetDirectory));
            }
            finally
            {
                _gate.Release();
            }

            StatusText = result.Failed == 0
                ? $"已导出 {result.Succeeded:N0} 个文件到 {targetDirectory}"
                : $"导出：成功 {result.Succeeded:N0}，失败 {result.Failed:N0}。{string.Join("; ", result.Errors.Take(3))}";
        });
    }

    [RelayCommand(CanExecute = nameof(CanExportPreview))]
    private async Task ExportPreviewAsync()
    {
        EntryItem? item = SelectedItem;
        if (item is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            PreviewExportDto export;
            await _gate.WaitAsync();
            try
            {
                export = await _session.ReadTypedPreviewExportAsync(item.FullPath);
            }
            finally
            {
                _gate.Release();
            }

            if (export.Files.Count == 0)
            {
                StatusText = "没有生成可导出的预览文件。";
                return;
            }

            if (OperatingSystem.IsAndroid())
            {
                if (!await EnsureAndroidExportFolderAsync())
                {
                    StatusText = "请先在设置中选择导出目录。";
                    return;
                }

                int written = await WriteFilesToAndroidExportFolderAsync(export.Files);
                StatusText = $"已导出 {written:N0} 个预览文件到 {ExportDirectory}";
                return;
            }

            string? targetDirectory = await ResolveExportDirectoryAsync();
            if (targetDirectory is null)
            {
                StatusText = "已取消导出：还没有选择导出目录。";
                return;
            }

            List<string> writtenPaths = [];
            foreach (PreviewExportFileDto file in export.Files)
            {
                string outputPath = Path.Combine(targetDirectory, SanitizeFileName(file.FileName));
                await File.WriteAllBytesAsync(outputPath, file.Data);
                writtenPaths.Add(outputPath);
            }

            StatusText = $"已导出 {writtenPaths.Count:N0} 个预览文件到 {targetDirectory}。";
        });
    }

    // ============ Android 系统分享（单文件导出可直接发送到其他应用） ============

    [RelayCommand(CanExecute = nameof(CanShareRaw))]
    private async Task ShareRawAsync()
    {
        EntryItem? item = SelectedItem;
        if (item is null || !OperatingSystem.IsAndroid())
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            IReadOnlyDictionary<string, byte[]> rawFiles;
            await _gate.WaitAsync();
            try
            {
                rawFiles = await _session.ReadRelatedRawFilesAsync(item.FullPath);
            }
            finally
            {
                _gate.Release();
            }

            if (rawFiles.Count == 0)
            {
                StatusText = "该资源没有可分享的关联文件。";
                return;
            }

            string stageDir = CreateShareStagingDirectory();
            try
            {
                List<string> paths = [];
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach ((string pakPath, byte[] data) in rawFiles)
                {
                    string fileName = MakeUniqueFileName(SanitizeFileName(Path.GetFileName(pakPath)), usedNames);
                    string localPath = Path.Combine(stageDir, fileName);
                    await File.WriteAllBytesAsync(localPath, data);
                    paths.Add(localPath);
                }

                await InvokeNativeShareAsync(paths, $"Prism 分享：{item.Name}");
            }
            finally
            {
                TryDeleteDirectory(stageDir);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanSharePreview))]
    private async Task SharePreviewAsync()
    {
        EntryItem? item = SelectedItem;
        if (item is null || !OperatingSystem.IsAndroid())
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            PreviewExportDto export;
            await _gate.WaitAsync();
            try
            {
                export = await _session.ReadTypedPreviewExportAsync(item.FullPath);
            }
            finally
            {
                _gate.Release();
            }

            if (export.Files.Count == 0)
            {
                StatusText = "没有生成可分享的预览文件。";
                return;
            }

            string stageDir = CreateShareStagingDirectory();
            try
            {
                List<string> paths = [];
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (PreviewExportFileDto file in export.Files)
                {
                    string fileName = MakeUniqueFileName(SanitizeFileName(file.FileName), usedNames);
                    string localPath = Path.Combine(stageDir, fileName);
                    await File.WriteAllBytesAsync(localPath, file.Data);
                    paths.Add(localPath);
                }

                await InvokeNativeShareAsync(paths, $"Prism 分享：{item.Name}");
            }
            finally
            {
                TryDeleteDirectory(stageDir);
            }
        });
    }

    // ============ 文件夹批量导出 ============

    public bool CanExportFolder => IsPakOpen && !IsBusy;

    // ============ 导出目录（用之前没设就当场问） ============

    /// <summary>
    /// 无头测试注入的"询问导出目录"实现；桌面端不注入，走系统文件夹选择器。
    /// 返回 null 表示用户取消。
    /// </summary>
    public Func<Task<string?>>? ExportFolderPromptOverride { get; set; }

    /// <summary>
    /// 取桌面端的导出目录：已经设置过就直接用，没设置就当场问一次，并记进设置。
    ///
    /// 为什么要有这一步：以前没设导出目录时，「导出当前文件夹」只往状态栏写一行
    /// "请先选择导出目录"，菜单随即关闭 —— 用户看到的就是"点了没反应"。
    /// 既然单个文件导出都在用系统选择器，批量导出不该要求先去设置页配一遍。
    /// 返回 null = 用户取消，调用方必须回一句明确的话，而不是静默返回。
    /// </summary>
    private async Task<string?> ResolveExportDirectoryAsync()
    {
        if (!string.IsNullOrWhiteSpace(ExportDirectory))
        {
            return ExportDirectory;
        }

        if (ExportFolderPromptOverride is { } injected)
        {
            string? picked = await injected();
            if (!string.IsNullOrWhiteSpace(picked))
            {
                ExportDirectory = picked;
            }

            return string.IsNullOrWhiteSpace(picked) ? null : picked;
        }

        if (TopLevel?.StorageProvider is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFolder> folders = await TopLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "选择导出目录",
                AllowMultiple = false,
            });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } directory ||
            string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        ExportDirectory = directory;
        return directory;
    }

    [RelayCommand(CanExecute = nameof(CanExportFolder))]
    private Task ExportFolderRawAsync() => ExportFolderAsync("raw");

    [RelayCommand(CanExecute = nameof(CanExportFolder))]
    private Task ExportFolderImagesAsync() => ExportFolderAsync("images");

    private async Task ExportFolderAsync(string kind)
    {
        if (!IsPakOpen)
        {
            StatusText = "请先打开 Pak。";
            return;
        }

        string rootFolder = string.IsNullOrWhiteSpace(CurrentFolder)
            ? string.Empty
            : CurrentFolder.Trim('/') + "/";

        await RunBusyAsync(async () =>
        {
            string targetDirectory;
            if (OperatingSystem.IsAndroid())
            {
                if (!await EnsureAndroidExportFolderAsync())
                {
                    StatusText = "请先在设置中选择导出目录。";
                    return;
                }

                targetDirectory = ExportDirectory;
            }
            else
            {
                string? resolved = await ResolveExportDirectoryAsync();
                if (resolved is null)
                {
                    StatusText = "已取消导出：还没有选择导出目录。";
                    return;
                }

                targetDirectory = resolved;
            }

            IReadOnlyList<ArchiveEntryDto> entries;
            await _gate.WaitAsync();
            try
            {
                entries = await _session.ListAsync(rootFolder, recursive: true);
            }
            finally
            {
                _gate.Release();
            }

            ArchiveEntryDto[] files = entries
                .Where(entry => !entry.IsDirectory)
                .DistinctBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0)
            {
                StatusText = "当前文件夹中没有可导出的文件。";
                return;
            }

            var rawPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var androidFolders = OperatingSystem.IsAndroid()
                ? new Dictionary<string, IStorageFolder>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = _exportFolder! }
                : null;
            int exportedAssets = 0;
            int exportedFiles = 0;
            int skipped = 0;
            int failed = 0;
            long totalBytes = 0;

            for (int i = 0; i < files.Length; i++)
            {
                ArchiveEntryDto entry = files[i];
                if (i == 0 || i % 10 == 0)
                {
                    StatusText = kind == "raw"
                        ? $"正在导出原始文件 {i + 1}/{files.Length}..."
                        : $"正在导出图片 {i + 1}/{files.Length}...";
                    await Task.Yield();
                }

                try
                {
                    if (kind == "raw")
                    {
                        IReadOnlyDictionary<string, byte[]> rawFiles;
                        await _gate.WaitAsync();
                        try
                        {
                            rawFiles = await _session.ReadRelatedRawFilesAsync(entry.FullPath);
                        }
                        finally
                        {
                            _gate.Release();
                        }

                        bool wroteAny = false;
                        foreach ((string pakPath, byte[] data) in rawFiles)
                        {
                            if (!rawPaths.Add(pakPath))
                            {
                                continue;
                            }

                            string relativePath = GetFolderRelativePath(rootFolder, pakPath);
                            string relativeDirectory = GetParentFolder(relativePath);
                            string fileName = GetFileNameFromPakPath(relativePath);
                            if (OperatingSystem.IsAndroid())
                            {
                                IStorageFolder parent = await GetOrCreateAndroidFolderAsync(_exportFolder!, relativeDirectory, androidFolders!);
                                await WriteAndroidExportFileAsync(parent, fileName, data);
                            }
                            else
                            {
                                string outputPath = Path.Combine(targetDirectory, SanitizeRelativePath(relativePath));
                                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                                await File.WriteAllBytesAsync(outputPath, data);
                            }

                            exportedFiles++;
                            totalBytes += data.Length;
                            wroteAny = true;
                        }

                        if (wroteAny)
                        {
                            exportedAssets++;
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    else
                    {
                        // 只尝试可能的纹理资产；其他类型跳过，不打断批量导出。
                        string guessedKind = EntryItem.GuessKind(entry);
                        bool isLikelyTexture = guessedKind == "Texture";
                        bool isRawImage = !entry.IsAssetPackage && guessedKind is "PNG" or "JPG" or "JPEG" or "BMP" or "TGA" or "WEBP";
                        if (!isLikelyTexture && !isRawImage)
                        {
                            skipped++;
                            continue;
                        }

                        PreviewExportDto export;
                        await _gate.WaitAsync();
                        try
                        {
                            export = await _session.ReadTypedPreviewExportAsync(entry.FullPath);
                        }
                        catch
                        {
                            skipped++;
                            continue;
                        }
                        finally
                        {
                            _gate.Release();
                        }

                        if (!string.Equals(export.Kind, "texture", StringComparison.OrdinalIgnoreCase) || export.Files.Count == 0)
                        {
                            skipped++;
                            continue;
                        }

                        string relativePath = GetFolderRelativePath(rootFolder, entry.FullPath);
                        string relativeDirectory = GetParentFolder(relativePath);
                        foreach (PreviewExportFileDto file in export.Files)
                        {
                            if (OperatingSystem.IsAndroid())
                            {
                                IStorageFolder parent = await GetOrCreateAndroidFolderAsync(_exportFolder!, relativeDirectory, androidFolders!);
                                await WriteAndroidExportFileAsync(parent, SanitizeFileName(file.FileName), file.Data);
                            }
                            else
                            {
                                string outputDirectory = Path.Combine(targetDirectory, SanitizeRelativePath(relativeDirectory));
                                Directory.CreateDirectory(outputDirectory);
                                string outputPath = Path.Combine(outputDirectory, SanitizeFileName(file.FileName));
                                await File.WriteAllBytesAsync(outputPath, file.Data);
                            }

                            exportedFiles++;
                            totalBytes += file.Data.Length;
                        }

                        exportedAssets++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    AddLog($"文件夹导出跳过：{entry.FullPath}（{ex.Message}）");
                }
            }

            StatusText = $"文件夹导出完成：{exportedAssets} 个资源 / {exportedFiles} 个文件，共 {FormatSize(totalBytes)}；跳过 {skipped}，失败 {failed}。输出到 {targetDirectory}";
            AddLog($"文件夹导出完成（{kind}）：成功 {exportedAssets}，文件 {exportedFiles}，跳过 {skipped}，失败 {failed} → {targetDirectory}");
        });
    }

    private static string GetFolderRelativePath(string rootFolder, string pakPath)
    {
        string normalized = pakPath.Replace('\\', '/').TrimStart('/');
        string root = rootFolder.Replace('\\', '/').TrimStart('/');
        return string.IsNullOrEmpty(root) || !normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized[root.Length..].TrimStart('/');
    }

    private static string GetParentFolder(string relativePath)
    {
        int index = relativePath.LastIndexOf('/');
        return index <= 0 ? string.Empty : relativePath[..index];
    }

    private static string GetFileNameFromPakPath(string relativePath)
    {
        int index = relativePath.LastIndexOf('/');
        return index < 0 ? relativePath : relativePath[(index + 1)..];
    }

    private static string SanitizeRelativePath(string relativePath)
    {
        string[] parts = relativePath.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeFileName)
            .ToArray();
        return Path.Combine(parts);
    }

    private static async Task<IStorageFolder> GetOrCreateAndroidFolderAsync(
        IStorageFolder root,
        string relativeDirectory,
        IDictionary<string, IStorageFolder> cache)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory))
        {
            return root;
        }

        if (cache.TryGetValue(relativeDirectory, out IStorageFolder? cached))
        {
            return cached;
        }

        IStorageFolder current = root;
        foreach (string part in relativeDirectory.Replace('\\', '/')
                     .Split('/', StringSplitOptions.RemoveEmptyEntries)
                     .Select(SanitizeFileName))
        {
            try
            {
                current = await current.CreateFolderAsync(part) ?? current;
            }
            catch
            {
                current = await current.GetFolderAsync(part) ?? current;
            }
        }

        cache[relativeDirectory] = current;
        return current;
    }

    private static async Task WriteAndroidExportFileAsync(IStorageFolder folder, string fileName, byte[] data)
    {
        IStorageFile output = await CreateExportFileWithUniqueNameAsync(folder, SanitizeFileName(fileName));
        await using Stream stream = await output.OpenWriteAsync();
        await stream.WriteAsync(data);
    }

    private async Task InvokeNativeShareAsync(IReadOnlyList<string> filePaths, string title)
    {
        if (NativeShareFilesAsync is null)
        {
            StatusText = "分享服务未就绪，请重新打开应用后重试。";
            return;
        }

        await NativeShareFilesAsync(filePaths, title);
        StatusText = $"已打开系统分享：{Path.GetFileName(filePaths.FirstOrDefault() ?? title)}";
    }

    private static string MakeUniqueFileName(string fileName, ISet<string> usedNames)
    {
        if (usedNames.Add(fileName))
        {
            return fileName;
        }

        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int i = 2; i < 10_000; i++)
        {
            string candidate = $"{baseName} ({i}){extension}";
            if (usedNames.Add(candidate))
            {
                return candidate;
            }
        }

        return $"{Guid.NewGuid():N}{extension}";
    }

    private static string CreateShareStagingDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PrismShare", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private async Task<bool> EnsureAndroidExportFolderAsync()
    {
        if (_exportFolder is not null)
        {
            return true;
        }

        if (TopLevel?.StorageProvider is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_exportDirectoryBookmark))
        {
            try
            {
                _exportFolder = await TopLevel.StorageProvider.OpenFolderBookmarkAsync(_exportDirectoryBookmark);
                if (_exportFolder is not null)
                {
                    string? localPath = _exportFolder.TryGetLocalPath();
                    ExportDirectory = string.IsNullOrWhiteSpace(localPath)
                        ? _exportFolder.Name
                        : localPath;
                    return true;
                }
            }
            catch
            {
                // 书签失效时按未选择处理，用户可以重新选择。
            }

            _exportDirectoryBookmark = string.Empty;
            SaveSettings();
        }

        return false;
    }

    private async Task<int> WriteFilesToAndroidExportFolderAsync(IReadOnlyList<PreviewExportFileDto> files)
    {
        if (_exportFolder is null)
        {
            throw new InvalidOperationException("导出目录未就绪。");
        }

        int written = 0;
        foreach (PreviewExportFileDto file in files)
        {
            IStorageFile output = await CreateExportFileWithUniqueNameAsync(_exportFolder, SanitizeFileName(file.FileName));
            await using Stream stream = await output.OpenWriteAsync();
            await stream.WriteAsync(file.Data);
            written++;
        }

        return written;
    }

    private static async Task<IStorageFile> CreateExportFileWithUniqueNameAsync(IStorageFolder folder, string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int i = 0; i < 100; i++)
        {
            string candidate = i == 0 ? fileName : $"{baseName} ({i}){extension}";
            try
            {
                IStorageFile? file = await folder.CreateFileAsync(candidate);
                if (file is not null)
                {
                    return file;
                }
            }
            catch
            {
                // 名称已存在时尝试加序号；真正失败会继续尝试或最终抛出。
            }
        }

        throw new InvalidOperationException($"无法在导出目录中创建文件：{fileName}");
    }

    // ============ 替换（Patch） ============

    [RelayCommand(CanExecute = nameof(CanAddToPatch))]
    private async Task AddSelectedToPatchAsync()
    {
        EntryItem? item = SelectedItem;
        if (item is null || item.IsDirectory || !IsPakOpen)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            // 1. 先用预览判断类型：本地化 / 音频 / 媒体源 / 纹理
            AssetPreviewDto? originalPreview = null;
            await _gate.WaitAsync();
            try
            {
                try
                {
                    originalPreview = await _session.ReadPreviewAsync(item.FullPath);
                }
                catch
                {
                    // 预览失败不影响加入替换
                }
            }
            finally
            {
                _gate.Release();
            }

            bool isLocres = originalPreview is { Kind: "locres" } || originalPreview?.Locres is not null;
            bool isPackage = item.FullPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                             item.FullPath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

            // 媒体源资产（UFileMediaSource 等）：要替换的是它引用的那个视频文件
            string targetPath = item.FullPath;
            if (originalPreview is { Kind: "video" })
            {
                await _gate.WaitAsync();
                try
                {
                    targetPath = await _session.ResolveMediaSourceFileAsync(item.FullPath) ?? string.Empty;
                }
                catch
                {
                    targetPath = string.Empty;
                }
                finally
                {
                    _gate.Release();
                }

                if (string.IsNullOrEmpty(targetPath))
                {
                    throw new InvalidOperationException(
                        "这个媒体源资产没能解析出它引用的文件。可以到浏览页直接选中那段视频，再「加入替换」。");
                }
            }

            string workDir = Path.Combine(Path.GetTempPath(), "prism-desktop-patch", Guid.NewGuid().ToString("N"));
            string inputDir = Path.Combine(workDir, "input");
            Directory.CreateDirectory(inputDir);

            PatchItem patch;
            Dictionary<string, string> originalFiles = new(StringComparer.OrdinalIgnoreCase);

            // 2. 原始文件（视频 / 音频 / 任意二进制）：只换字节，不解析资产
            //    注意：.locres 大多是"裸文件"（不是 .uasset 包），必须在按包/非包分流之前
            //    把本地化排除掉，否则会掉进"直接替换字节"分支，翻译编辑器就再也出不来了。
            bool isMediaTarget = !isLocres &&
                                 (!isPackage || !string.Equals(targetPath, item.FullPath, StringComparison.Ordinal));
            if (isMediaTarget)
            {
                IReadOnlyDictionary<string, byte[]> mediaFiles;
                await _gate.WaitAsync();
                try
                {
                    mediaFiles = await _session.ReadRelatedRawFilesAsync(targetPath);
                }
                finally
                {
                    _gate.Release();
                }

                if (mediaFiles.Count == 0)
                {
                    throw new InvalidOperationException("这个文件不在 Pak 里，无法加入替换。");
                }

                foreach ((string pakPath, byte[] data) in mediaFiles)
                {
                    string localPath = Path.Combine(inputDir, Path.GetFileName(pakPath));
                    await File.WriteAllBytesAsync(localPath, data);
                    originalFiles[pakPath] = localPath;
                }

                string extension = Path.GetExtension(targetPath);
                long totalBytes = mediaFiles.Values.Sum(chunk => (long)chunk.Length);
                patch = new PatchItem(
                    kind: "media",
                    sourcePath: targetPath,
                    name: Path.GetFileName(targetPath),
                    format: string.IsNullOrEmpty(extension) ? "文件" : extension.TrimStart('.').ToUpperInvariant(),
                    sizeLabel: FormatSize(totalBytes),
                    width: 0,
                    height: 0,
                    workDirectory: workDir,
                    inputUassetPath: string.Empty)
                {
                    Detail = $"{FormatSize(totalBytes)}（直接替换字节，不改资产）",
                };
                StatusText = $"已加入替换：{patch.Name}（{patch.Format}）";
            }
            else
            {
                // 3. 资产：把三件套解到工作目录，再按类型处理
                IReadOnlyDictionary<string, byte[]> rawFiles;
                await _gate.WaitAsync();
                try
                {
                    rawFiles = await _session.ReadRelatedRawFilesAsync(item.FullPath);
                }
                finally
                {
                    _gate.Release();
                }

                if (rawFiles.Count == 0)
                {
                    throw new InvalidOperationException("该资源没有可替换的关联文件。");
                }

                string baseName = Path.GetFileNameWithoutExtension(item.FullPath);
                foreach ((string pakPath, byte[] data) in rawFiles)
                {
                    string localPath = Path.Combine(inputDir, Path.GetFileName(pakPath));
                    await File.WriteAllBytesAsync(localPath, data);
                    originalFiles[pakPath] = localPath;
                }

                if (isLocres)
                {
                    LocresPreviewDto locres;
                    await _gate.WaitAsync();
                    try
                    {
                        locres = await _session.ReadLocresPreviewAsync(item.FullPath);
                    }
                    finally
                    {
                        _gate.Release();
                    }

                    patch = new PatchItem(
                        kind: "locres",
                        sourcePath: item.FullPath,
                        name: baseName,
                        format: "LOCRES",
                        sizeLabel: $"{locres.EntryCount:N0} 条词条",
                        width: 0,
                        height: 0,
                        workDirectory: workDir,
                        inputUassetPath: string.Empty);
                    foreach (LocresEntryDto entry in locres.Entries)
                    {
                        patch.LocresEntries.Add(new LocresEntryVM(entry));
                    }

                    patch.ApplyLocresFilter();
                    StatusText = $"已加入本地化：{baseName}（{locres.EntryCount:N0} 条）";
                }
                else
                {
                    // 纹理：需要 .uasset 关联文件
                    string? uassetPakPath = rawFiles.Keys.FirstOrDefault(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase));
                    if (uassetPakPath is null)
                    {
                        throw new InvalidOperationException("该资源不是 .uasset 资产。");
                    }

                    string inputUassetPath = Path.Combine(inputDir, baseName + ".uasset");

                    if (originalPreview is { Kind: "audio" })
                    {
                        patch = CreateAudioPatch(item.FullPath, baseName, inputUassetPath, workDir);
                    }
                    else
                    {
                        // 检查纹理格式
                        TextureInspectionResult inspect = await new TextureReplacementService().InspectAsync(
                            inputUassetPath,
                            EngineVersion.VER_UE5_6,
                            NullIfWhiteSpace(UsmapPath));

                        patch = new PatchItem(
                            kind: "texture",
                            sourcePath: item.FullPath,
                            name: baseName,
                            format: inspect.Format,
                            sizeLabel: $"{inspect.Width}×{inspect.Height}",
                            width: inspect.Width,
                            height: inspect.Height,
                            workDirectory: workDir,
                            inputUassetPath: inputUassetPath)
                        {
                            OriginalPreview = originalPreview is { Data: { Length: > 0 } previewData }
                                ? DecodeImage(previewData, 640)
                                : null,
                        };
                        StatusText = $"已加入替换：{baseName}（{inspect.Format}）";
                    }
                }
            }

            foreach ((string pakPath, string localPath) in originalFiles)
            {
                patch.OriginalFiles[pakPath] = localPath;
            }

            PatchItems.Add(patch);
            SelectedPatchItem = patch;
            CurrentTabIndex = 2;
            NotifyPatchItemsState();
        });
    }

    /// <summary>音频替换项：先确认这是个可替换的流式 SoundWave，再登记声道/时长等展示信息。</summary>
    private PatchItem CreateAudioPatch(string sourcePath, string baseName, string inputUassetPath, string workDir)
    {
        if (!BinkaEncoder.IsAvailable(ToolsDirectory))
        {
            throw new InvalidOperationException(
                OperatingSystem.IsAndroid()
                    ? "内置音频编码器没能加载，音频替换暂时不可用（请更新到最新版本）。"
                    : $"没找到 {BinkaEncoder.ToolName}，音频替换暂时不可用。把它放进 {ToolsDirectory} 再重启即可（来源见 tools/README.md）。");
        }

        SoundWaveInfo info = SoundWavePatcher.Inspect(inputUassetPath, NullIfWhiteSpace(UsmapPath));
        if (!info.CanReplace)
        {
            throw new InvalidOperationException($"{baseName} 无法替换：{info.Problem}");
        }

        var patch = new PatchItem(
            kind: "audio",
            sourcePath: sourcePath,
            name: baseName,
            format: info.Format,
            sizeLabel: info.DescribeSeconds,
            width: 0,
            height: 0,
            workDirectory: workDir,
            inputUassetPath: inputUassetPath)
        {
            Detail = $"{info.DescribeChannels} · {info.DescribeSeconds} · {Math.Max(1, info.DataChunkCount)} 块（最大 {FormatSize(info.MaxChunkBytes)}） · 原始 {FormatSize(info.AudioBytes)}" +
                     (info.CuePointCount > 0 ? $" · {info.CuePointCount} 个 cue point（原样保留）" : string.Empty),
        };

        // 预览已经把原始音频解码成可播放的 WAV，这里不需要再读一次
        StatusText = $"已加入替换：{baseName}（{info.Format} · {info.DescribeSeconds}）";
        return patch;
    }

    [RelayCommand(CanExecute = nameof(CanPickReplacement))]
    private async Task PickReplacementAsync()
    {
        PatchItem? patch = SelectedPatchItem;
        // TopLevel 只影响能不能弹系统选择器，交给 PickFileAsync 判断；这里别提前返回，
        // 否则无头环境下（注入了 FilePickerOverride）整条替换流程都跑不起来。
        if (patch is null || patch.IsLocres)
        {
            return;
        }

        if (patch.IsMedia)
        {
            await PickMediaReplacementAsync(patch);
            return;
        }

        if (patch.IsAudio)
        {
            await PickAudioReplacementAsync(patch);
            return;
        }

        // 复用 PickFileAsync：Android 上会把 SAF 文件复制到私有目录。
        // 支持 JPG/JPEG 等常见图片格式；Android 选择器同时给 MIME 过滤。
        string? imagePath = await PickFileAsync(
            "选择替换图片",
            ["*.png", "*.jpg", "*.jpeg", "*.jpe", "*.jfif", "*.bmp", "*.tga", "*.webp", "*.gif", "*.tif", "*.tiff"],
            replaceCachePath: null,
            mimeTypes: ["image/png", "image/jpeg", "image/bmp", "image/webp", "image/gif", "image/tiff", "image/x-tga"]);
        if (string.IsNullOrEmpty(imagePath))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            string outputDir = Path.Combine(patch.WorkDirectory, "output");
            Directory.CreateDirectory(outputDir);
            string outputUassetPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(patch.InputUassetPath) + ".patched.uasset");

            if (OperatingSystem.IsAndroid())
            {
                // Android：进程内替换（libprism_codecs.so 已随 APK 打包）
                try
                {
                    await new TextureReplacementService().ReplaceAsync(
                        patch.InputUassetPath,
                        imagePath,
                        outputUassetPath,
                        EngineVersion.VER_UE5_6,
                        NullIfWhiteSpace(UsmapPath),
                        new TextureCodecOptions(AstcQuality: "fast"));
                }
                catch (Exception ex)
                {
                    patch.Status = "失败";
                    patch.Error = ex.Message;
                    StatusText = $"替换失败：{ex.Message}";
                    return;
                }
            }
            else
            {
                UAssetCliRunner runner = GetCliRunner();
                UAssetCliRunner.CliResult result = await runner.ReplaceTextureAsync(
                    patch.InputUassetPath,
                    imagePath,
                    outputUassetPath,
                    patch.Format,
                    "VER_UE5_6",
                    "fast");

                if (result.ExitCode != 0)
                {
                    AppLog.Error($"UAssetCLI 替换失败（exit={result.ExitCode}）：{patch.Name}{Environment.NewLine}{result.CombinedOutput}");
                    string failure = result.DescribeFailure();
                    patch.Status = "失败";
                    patch.Error = failure;
                    StatusText = $"替换失败：{failure}";
                    return;
                }
            }

            // 映射回 Pak 路径
            patch.PatchedFiles.Clear();
            string outputUexpPath = Path.ChangeExtension(outputUassetPath, ".uexp");
            string outputUbulkPath = Path.ChangeExtension(outputUassetPath, ".ubulk");
            foreach (string pakPath in patch.OriginalFiles.Keys)
            {
                string ext = Path.GetExtension(pakPath);
                string? candidate = ext.ToLowerInvariant() switch
                {
                    ".uasset" => outputUassetPath,
                    ".uexp" => outputUexpPath,
                    ".ubulk" => outputUbulkPath,
                    _ => null,
                };
                if (candidate is not null && File.Exists(candidate))
                {
                    patch.PatchedFiles[pakPath] = candidate;
                }
            }

            patch.ReplacementName = Path.GetFileName(imagePath);
            patch.ReplacementPreview = LoadImage(imagePath, 512);
            patch.Status = "已替换";
            StatusText = $"已替换 {patch.Name}（{patch.Format}）。";
        });
    }

    /// <summary>视频 / 原始文件：挑一个同名扩展名的文件，直接按字节替换。</summary>
    private async Task PickMediaReplacementAsync(PatchItem patch)
    {
        string extension = Path.GetExtension(patch.SourcePath);
        string pattern = string.IsNullOrEmpty(extension) ? "*.*" : "*" + extension;
        string? picked = await PickFileAsync(
            string.IsNullOrEmpty(extension) ? "选择替换文件" : $"选择替换的 {extension} 文件",
            [pattern, "*.*"],
            replaceCachePath: null,
            // 媒体替换什么都能换（mp4 / bk2 / usm / 自定义格式），Android 上按 MIME
            // 过滤反而会把文件藏起来，直接放开。
            mimeTypes: ["*/*"]);
        if (string.IsNullOrEmpty(picked))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            string outputDir = Path.Combine(patch.WorkDirectory, "output");
            Directory.CreateDirectory(outputDir);
            string outputPath = Path.Combine(outputDir, Path.GetFileName(patch.SourcePath));
            File.Copy(picked, outputPath, overwrite: true);
            long size = new FileInfo(outputPath).Length;

            patch.PatchedFiles.Clear();
            foreach (string pakPath in patch.OriginalFiles.Keys)
            {
                patch.PatchedFiles[pakPath] = outputPath;
            }

            patch.ReplacementName = Path.GetFileName(picked);
            patch.ReplacementDetail = FormatSize(size);
            patch.Status = "已替换";
            StatusText = $"已替换 {patch.Name}（{FormatSize(size)}）。";
            await Task.CompletedTask;
        });
    }

    /// <summary>音频：解码素材 → 编成 BINKA → 重建 SoundWave 三件套。</summary>
    private async Task PickAudioReplacementAsync(PatchItem patch)
    {
        string? picked = await PickFileAsync(
            "选择替换音频",
            ["*.wav", "*.mp3", "*.ogg", "*.oga", "*.flac", "*.m4a", "*.aac", "*.wma", "*.opus"],
            replaceCachePath: null,
            mimeTypes: ["audio/wav", "audio/x-wav", "audio/mpeg", "audio/ogg", "audio/flac", "audio/mp4", "audio/aac"]);
        if (string.IsNullOrEmpty(picked))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            try
            {
                string outputDir = Path.Combine(patch.WorkDirectory, "output");
                Directory.CreateDirectory(outputDir);
                string baseName = Path.GetFileNameWithoutExtension(patch.InputUassetPath);

                SoundWaveReplacement replacement = await Task.Run(() =>
                {
                    PcmAudio source = AudioSourceDecoder.Decode(picked, ToolsDirectory);
                    byte[] binka = BinkaEncoder.Encode(source, patch.AudioQuality, ToolsDirectory);
                    return SoundWavePatcher.Replace(patch.InputUassetPath, binka, NullIfWhiteSpace(UsmapPath));
                });

                string outputUassetPath = Path.Combine(outputDir, baseName + ".patched.uasset");
                string outputUexpPath = Path.ChangeExtension(outputUassetPath, ".uexp");
                string outputUbulkPath = Path.ChangeExtension(outputUassetPath, ".ubulk");
                await File.WriteAllBytesAsync(outputUassetPath, replacement.Uasset);
                await File.WriteAllBytesAsync(outputUexpPath, replacement.Uexp);
                await File.WriteAllBytesAsync(outputUbulkPath, replacement.Ubulk);

                patch.PatchedFiles.Clear();
                foreach (string pakPath in patch.OriginalFiles.Keys)
                {
                    string? candidate = Path.GetExtension(pakPath).ToLowerInvariant() switch
                    {
                        ".uasset" => outputUassetPath,
                        ".uexp" => outputUexpPath,
                        ".ubulk" => outputUbulkPath,
                        _ => null,
                    };
                    if (candidate is not null && File.Exists(candidate))
                    {
                        patch.PatchedFiles[pakPath] = candidate;
                    }
                }

                patch.ReplacementName = Path.GetFileName(picked);
                patch.ReplacementDetail =
                    $"{replacement.Channels} 声道 / {replacement.SampleRate:N0} Hz / {replacement.Seconds:0.##} 秒 · " +
                    $"编码后 {FormatSize(replacement.Ubulk.Length)}";
                patch.Status = "已替换";
                StatusText = $"已替换 {patch.Name}：{replacement.Seconds:0.##} 秒，{FormatSize(replacement.Ubulk.Length)}。";
            }
            catch (Exception ex)
            {
                patch.Status = "失败";
                patch.Error = ex.Message;
                StatusText = $"音频替换失败：{ex.Message}";
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanRemovePatchItem))]
    private void RemovePatchItem()
    {
        PatchItem? patch = SelectedPatchItem;
        if (patch is null)
        {
            return;
        }

        PatchItems.Remove(patch);
        SelectedPatchItem = null;
        TryDeleteDirectory(patch.WorkDirectory);
        StatusText = $"已移除替换项：{patch.Name}";
        NotifyPatchItemsState();
    }

    /// <summary>本地化条目修改后写回（失焦/回车触发），写出的 patched.locres 供构建补丁 Pak。</summary>
    [RelayCommand]
    private async Task UpdateLocresEntryAsync(PatchItem? patch)
    {
        if (patch is null || !patch.IsLocres || patch.OriginalFiles.Count == 0)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            string originalPath = patch.OriginalFiles
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Value)
                .First();
            string outputDir = Path.Combine(patch.WorkDirectory, "output");
            Directory.CreateDirectory(outputDir);
            string outputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(originalPath) + ".patched.locres");

            byte[] originalBytes = await File.ReadAllBytesAsync(originalPath);
            byte[] patchedBytes = LocresResourceCodec.ApplyTranslations(
                originalBytes,
                patch.LocresEntries.Select(e => e.ToDto()).ToList());
            await File.WriteAllBytesAsync(outputPath, patchedBytes);

            patch.PatchedFiles.Clear();
            foreach ((string pakPath, string localPath) in patch.OriginalFiles)
            {
                patch.PatchedFiles[pakPath] = localPath;
            }

            patch.PatchedFiles[patch.SourcePath] = outputPath;
            patch.Status = "已编辑";
            StatusText = $"本地化已更新：{patch.Name}（{patch.LocresEntries.Count:N0} 条），可构建补丁 Pak。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanBuildPatchPak))]
    private async Task BuildPatchPakAsync()
    {
        TopLevel? top = TopLevel;
        if (top is null || PatchItems.Count == 0)
        {
            return;
        }

        IStorageFile? output = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存补丁 Pak",
            SuggestedFileName = $"patch_{DateTime.Now:yyyyMMdd_HHmmss}.pak",
            FileTypeChoices = [new FilePickerFileType("Pak 文件") { Patterns = ["*.pak"] }],
        });
        if (output is null)
        {
            return;
        }

        string? outputPath = output.TryGetLocalPath();
        if (string.IsNullOrEmpty(outputPath) && !OperatingSystem.IsAndroid())
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            // 合并所有原始文件，再让替换文件覆盖
            Dictionary<string, ModifiedPakFile> files = new(StringComparer.OrdinalIgnoreCase);
            foreach (PatchItem patch in PatchItems)
            {
                foreach ((string pakPath, string localPath) in patch.OriginalFiles)
                {
                    files[pakPath] = new ModifiedPakFile(localPath, pakPath);
                }
            }

            foreach (PatchItem patch in PatchItems)
            {
                foreach ((string pakPath, string localPath) in patch.PatchedFiles)
                {
                    files[pakPath] = new ModifiedPakFile(localPath, pakPath);
                }
            }

            if (OperatingSystem.IsAndroid())
            {
                // Android SAF：先打包到临时文件，再流式写入用户选择的位置，随后直接打开系统分享。
                string tempPakPath = Path.Combine(Path.GetTempPath(), $"patch_{Guid.NewGuid():N}.pak");
                ModifiedPakPackResult androidPack = await Task.Run(() => ModifiedPakPackService.Pack(new ModifiedPakRequest(
                    files.Values.OrderBy(f => f.PakPath, StringComparer.OrdinalIgnoreCase).ToArray(),
                    tempPakPath,
                    UseCompression: UseOodleCompression,
                    Compression: PakCompression.Oodle)));
                LogOodleFallback(androidPack);

                await using (Stream src = File.OpenRead(tempPakPath))
                await using (Stream dst = await output.OpenWriteAsync())
                {
                    await src.CopyToAsync(dst);
                }

                StatusText = $"补丁 Pak 已保存：{output.Name}（{files.Count} 个文件）";
                try
                {
                    await InvokeNativeShareAsync([tempPakPath], $"Prism 补丁 Pak：{output.Name}");
                }
                catch
                {
                    // 分享失败不影响已保存的文件。
                }
                finally
                {
                    TryDeleteFile(tempPakPath);
                }
            }
            else
            {
                ModifiedPakPackResult desktopPack = await Task.Run(() => ModifiedPakPackService.Pack(new ModifiedPakRequest(
                    files.Values.OrderBy(f => f.PakPath, StringComparer.OrdinalIgnoreCase).ToArray(),
                    outputPath!,
                    UseCompression: UseOodleCompression,
                    Compression: PakCompression.Oodle)));
                LogOodleFallback(desktopPack);

                StatusText = $"补丁 Pak 已构建：{Path.GetFileName(outputPath)}（{files.Count} 个文件）";
            AddLog($"构建补丁 Pak：{Path.GetFileName(outputPath)}（{files.Count} 个文件）");
            }
        });
    }

    private UAssetCliRunner GetCliRunner()
    {
        if (_cliRunnerTried == 0)
        {
            _cliRunnerTried = 1;
            _cliRunner = UAssetCliRunner.TryCreate();
        }

        return _cliRunner ?? throw new InvalidOperationException(
            "未找到 UAssetCLI。请先构建 UAssetCLI 项目（dotnet build UAssetCLI），或确认输出目录包含 UAssetCLI/UAssetCLI.exe。");
    }

    // ============ 合并 ============

    [RelayCommand(CanExecute = nameof(CanMergePak))]
    private async Task MergePakAsync()
    {
        if (MergePaks.Count == 0)
        {
            return;
        }

        // 先把每个 Pak 的文件数与相互冲突统计出来，让用户看到"覆盖了什么"。
        MergeInspectionResponse inspection = await InspectMergeCoreAsync();
        MergeStatus = inspection.Summary;

        if (AskBeforeReplace && inspection.ConflictCount > 0)
        {
            string question = $"共发现 {inspection.ConflictCount:N0} 个路径冲突。" +
                              "冲突路径将以列表中靠上的 Pak 为准（第 1 项优先级最高）。" +
                              (HasMergeBaseLocres
                                  ? "同名 .locres 例外：会与所选基准逐条合并，各模组的改动都会保留。"
                                  : string.Empty) +
                              "是否继续？";
            bool confirmed = OperatingSystem.IsAndroid()
                ? (NativeConfirmAsync is not null
                    ? await NativeConfirmAsync("确认合并", question)
                    : true)
                : await Views.ConfirmDialog.ShowAsync(
                    TopLevel as Window ?? throw new InvalidOperationException("窗口未就绪"),
                    question);
            if (!confirmed)
            {
                MergeStatus = "已取消";
                return;
            }
        }

        await RunBusyAsync(async () =>
        {
            MergeBuildResponse result = await BuildMergeCoreAsync();
            if (OperatingSystem.IsAndroid() && _mergeOutputTarget is not null)
            {
                await using (Stream src = File.OpenRead(result.OutputPakPath))
                await using (Stream dst = await _mergeOutputTarget.OpenWriteAsync())
                {
                    await src.CopyToAsync(dst);
                }

                MergeStatus = $"合并完成：{_mergeOutputTarget.Name}，{result.FileCount:N0} 个文件，" +
                              $"冲突 {result.ConflictCount:N0}{DescribeLocresMerge(result)}";
                StatusText = $"合并完成：{_mergeOutputTarget.Name}";
                try
                {
                    await InvokeNativeShareAsync([result.OutputPakPath], $"Prism 合并 Pak：{_mergeOutputTarget.Name}");
                }
                catch
                {
                    // 分享失败不影响已保存的文件。
                }
                finally
                {
                    TryDeleteFile(result.OutputPakPath);
                }
            }
            else
            {
                MergeStatus = $"合并完成：{result.FileCount:N0} 个文件，冲突 {result.ConflictCount:N0}，" +
                              $"替换 {result.ReplacedCount:N0}{DescribeLocresMerge(result)}";
                StatusText = $"合并完成：{Path.GetFileName(result.OutputPakPath)}";
            }

            AddLog($"合并 Pak：{MergePaks.Count} 个输入 → {Path.GetFileName(result.OutputPakPath)}，" +
                   $"{result.FileCount:N0} 个文件，冲突 {result.ConflictCount:N0}，覆盖 {result.ReplacedCount:N0}" +
                   DescribeLocresMerge(result));
        });
    }

    /// <summary>
    /// 合并结果里关于"本地化三路合并"的一句话。没做合并（未设基准，或没有同名 .locres）时为空。
    /// </summary>
    private static string DescribeLocresMerge(MergeBuildResponse result)
    {
        if (result.MergedLocresFiles == 0)
        {
            return string.Empty;
        }

        string text = $"，本地化逐条合并 {result.MergedLocresFiles:N0} 个文件" +
                      $"（生效改动 {result.MergedLocresEntries:N0} 条";
        if (result.LocresConflicts > 0)
        {
            text += $"，冲突 {result.LocresConflicts:N0}";
        }

        text += "）";
        if (result.LocresFailed > 0)
        {
            text += $"，{result.LocresFailed:N0} 个退回整文件覆盖";
        }

        return text;
    }

    /// <summary>
    /// 统计参与合并的 Pak 的文件数与路径冲突数。
    /// 冲突定义：某个路径在多个 Pak 中都存在（即会被优先级更高的覆盖）。
    /// </summary>
    private async Task<MergeInspectionResponse> InspectMergeCoreAsync()
    {
        if (MergePaks.Count == 0)
        {
            return MergeInspectionResponse.Empty;
        }

        string? aes = NullIfWhiteSpace(AesKey);
        string? mappings = NullIfWhiteSpace(UsmapPath);

        // path → 出现过该路径的输入序号集合
        Dictionary<string, List<int>> owners = new(StringComparer.OrdinalIgnoreCase);
        List<int> perPakCounts = [];

        for (int index = 0; index < MergePaks.Count; index++)
        {
            MergePakItem item = MergePaks[index];
            using var session = new PakArchiveSession();
            await session.OpenAsync(new PakOpenOptions([item.Path], aes, mappings));

            IReadOnlySet<string> paths = await session.ListRawFilePathsAsync();
            perPakCounts.Add(paths.Count);
            item.Detail = $"{paths.Count:N0} 个文件";

            foreach (string path in paths)
            {
                if (!owners.TryGetValue(path, out List<int>? list))
                {
                    owners[path] = list = [];
                }

                list.Add(index);
            }
        }

        string[] conflicts = owners
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => pair.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int topCount = perPakCounts.Count > 0 ? perPakCounts[0] : 0;
        return new MergeInspectionResponse(
            topCount,
            perPakCounts.Sum(),
            perPakCounts,
            conflicts.Length,
            conflicts.Take(500).ToArray());
    }

    /// <summary>
    /// 按列表顺序合并：<b>越靠上优先级越高</b>，路径冲突时以上方那份为准。
    ///
    /// 实现方式是"只收第一份"—— 顺序遍历各 Pak，某个路径第一次出现时就把它定下来，
    /// 之后再遇到同路径一律丢弃；列表第 1 项因此天然成为最高优先级。
    ///
    /// <b>本地化例外</b>：若设置了<b>基准 .locres</b>，合并输入里同名的 .locres 不再整份
    /// 覆盖，而是与基准做三路合并 —— 逐条算出各模组的改动集再叠加。这样两份各改各的
    /// 文本模组能真正合到一起（否则整份覆盖会丢掉其中一份的全部改动）。
    /// 其它资产仍是覆盖语义（二进制资产无法逐条合并）。
    /// </summary>
    private async Task<MergeBuildResponse> BuildMergeCoreAsync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "PrismDesktopMerge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string? aes = NullIfWhiteSpace(AesKey);
            string? mappings = NullIfWhiteSpace(UsmapPath);

            // 基准本地化：只在确实存在时才启用；缺失则退回覆盖语义，不静默失败。
            byte[]? baselineLocres = null;
            if (HasMergeBaseLocres)
            {
                if (File.Exists(MergeBaseLocresPath))
                {
                    baselineLocres = await File.ReadAllBytesAsync(MergeBaseLocresPath);
                }
                else
                {
                    AddLog($"基准本地化文件不存在，本次按整文件覆盖处理：{MergeBaseLocresPath}", "合并");
                }
            }

            Dictionary<string, ModifiedPakFile> merged = new(StringComparer.OrdinalIgnoreCase);
            int conflicts = 0;
            int replaced = 0;
            int locresMergedFiles = 0;
            int locresChangedEntries = 0;
            int locresConflicts = 0;
            int locresFailed = 0;

            // 顺序遍历：先出现者胜出，即"越靠上优先级越高"。
            for (int index = 0; index < MergePaks.Count; index++)
            {
                MergePakItem item = MergePaks[index];
                string slot = Path.Combine(tempRoot, $"in{index:D3}");

                using var session = new PakArchiveSession();
                await session.OpenAsync(new PakOpenOptions([item.Path], aes, mappings));
                IReadOnlyList<PakRawFileCopy> files = await session.CopyAllRawFilesAsync(slot);

                foreach (PakRawFileCopy file in files)
                {
                    if (!merged.TryGetValue(file.PakPath, out ModifiedPakFile? existing))
                    {
                        merged[file.PakPath] = new ModifiedPakFile(file.DiskPath, file.PakPath);
                        continue;
                    }

                    // 该路径已被更靠上的 Pak 收录。
                    conflicts++;
                    replaced++;

                    // 同名 .locres + 有基准 → 三路合并，而不是丢掉这一份。
                    if (LocresMerge.ShouldMergeConflict(file.PakPath, MergeBaseLocresPath, baselineLocres))
                    {
                        try
                        {
                            byte[] higher = await File.ReadAllBytesAsync(existing.DiskPath);
                            byte[] lower = await File.ReadAllBytesAsync(file.DiskPath);
                            // ShouldMergeConflict 已保证 baselineLocres 非空（它要求 Length > 0），
                            // 但那是运行期判断，编译器看不穿，故此处显式断言。
                            byte[] combined = LocresMerge.Merge(
                                baselineLocres!, [higher, lower], out LocresMergeReport report);

                            string mergedLocresPath = Path.Combine(tempRoot, $"locres{Guid.NewGuid():N}.locres");
                            await File.WriteAllBytesAsync(mergedLocresPath, combined);
                            merged[file.PakPath] = new ModifiedPakFile(mergedLocresPath, file.PakPath);

                            locresMergedFiles++;
                            locresChangedEntries += report.ChangedFromBaseline;
                            locresConflicts += report.Conflicts;
                            continue;
                        }
                        catch (Exception ex)
                        {
                            // 某一份本地化合并失败不该让整次合并崩掉：退回覆盖语义并记录下来。
                            locresFailed++;
                            AddLog($"本地化合并失败，该项退回整文件覆盖：{file.PakPath}（{ex.Message}）", "合并");
                        }
                    }

                    // 覆盖语义：靠上的那份已收录，这一份丢弃。
                }
            }

            string packTarget = OperatingSystem.IsAndroid()
                ? Path.Combine(Path.GetTempPath(), $"merged_{Guid.NewGuid():N}.pak")
                : MergeOutputPath;

            ModifiedPakPackResult mergePack = await Task.Run(() => ModifiedPakPackService.Pack(new ModifiedPakRequest(
                merged.Values.OrderBy(x => x.PakPath, StringComparer.OrdinalIgnoreCase).ToArray(),
                packTarget,
                UseCompression: UseOodleCompression,
                Compression: PakCompression.Oodle)));
            LogOodleFallback(mergePack);

            return new MergeBuildResponse(
                packTarget, merged.Count, conflicts, replaced,
                locresMergedFiles, locresChangedEntries, locresConflicts, locresFailed);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    // ============ Pak 转换 ============

    /// <summary>
    /// 双端 Pak 转换：把待转换 Pak 里的纹理，按主 Pak 的纹理格式重新编码后替换进去，
    /// 再合成一个新的 Pak。
    ///
    /// 平台差异：
    /// - Android 走进程内 <c>libprism_codecs</c> 编码；
    /// - Windows 走 UAssetCLI + astcenc/texconv（由注入的委托调用）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConvertPak))]
    private async Task ConvertPakAsync()
    {
        if (!IsPakOpen || string.IsNullOrWhiteSpace(ConvertSourcePath))
        {
            return;
        }

        string targetPak = PakPath;
        string sourcePak = ConvertSourcePath;
        bool mergeAll = ConvertMergeAll;
        bool includeModFiles = ConvertIncludeModFiles;
        string? aes = NullIfWhiteSpace(AesKey);
        string? mappings = NullIfWhiteSpace(UsmapPath);
        bool useOodle = UseOodleCompression;

        // Android 先打包到临时文件，再流入 SAF 句柄并分享（与补丁 Pak 一致）。
        bool android = OperatingSystem.IsAndroid();
        string outputPath = android
            ? Path.Combine(Path.GetTempPath(), $"converted_{Guid.NewGuid():N}.pak")
            : ConvertOutputPath;

        await RunBusyAsync(async () =>
        {
            ConvertRows.Clear();
            OnPropertyChanged(nameof(HasConvertRows));
            ConvertStatus = "准备中…";

            // 转换 = 解出源纹理像素，按主 Pak 资产的格式重新编码写回。
            // 同格式时服务内部会走无损字节搬运的快路径。
            var service = new UAssetTexture.Core.PakConversionService();

            if (!android)
            {
                // 桌面：把"编码成目标格式并写回资产"交给 UAssetCLI。
                // 目标格式由主 Pak 资产自身决定（模板 uasset 里已声明），
                // 所以这里不传 --expected-format，让 CLI 从资产里读。
                Services.UAssetCliRunner? runner = Services.UAssetCliRunner.CreateCached();
                if (runner is null)
                {
                    ConvertStatus = "未找到 UAssetCLI，无法重新编码纹理。请先构建 UAssetCLI（见 README）。";
                    StatusText = ConvertStatus;
                    return;
                }

                service.EncodeIntoAssetAsync = async (templateAsset, pngPath, outputAsset, ct) =>
                {
                    UAssetCliRunner.CliResult cliResult = await runner.ReplaceTextureAsync(
                        templateAsset,
                        pngPath,
                        outputAsset,
                        format: string.Empty,
                        engine: "VER_UE5_6",
                        astcQuality: "fast",
                        ct).ConfigureAwait(false);

                    return cliResult.ExitCode == 0 ? null : cliResult.CombinedOutput;
                };
            }

            var progress = new Progress<PakConversionProgress>(p =>
            {
                ConvertStatus = p.Total > 1
                    ? $"[{p.Completed}/{p.Total}] {Path.GetFileName(p.CurrentItem)}"
                    : p.CurrentItem;
            });

            UAssetTexture.Core.PakConversionResult result = await service.ConvertAsync(
                new UAssetTexture.Core.PakConversionOptions(
                    sourcePak,
                    targetPak,
                    outputPath,
                    aes,
                    mappings,
                    // 输出内容三档：
                    //   默认           —— 只装转换过的纹理（体积≈改动量）
                    //   并入模组文件    —— 再带上源 Pak 的蓝图/本地化等双端通用文件（体积仍≈改动量）
                    //   输出完整整包    —— 连主 Pak 的其他文件一起打包（体积≈主 Pak）
                    mergeAll
                        ? UAssetTexture.Core.PakConversionMode.MergeAll
                        : includeModFiles
                            ? UAssetTexture.Core.PakConversionMode.MergeSourceFiles
                            : UAssetTexture.Core.PakConversionMode.ReplacedOnly,
                    useOodle,
                    EngineVersion.VER_UE5_6),
                progress).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(result.OodleNote))
        {
            AddLog($"警告：{result.OodleNote}");
            AppLog.Warn("转换", result.OodleNote);
        }

        UAssetTexture.Core.PakConversionCounts counts = result.Counts;
            foreach (UAssetTexture.Core.PakConversionItemResult item in result.Items)
            {
                ConvertRows.Add(new PakConversionRow(item.PakPath, DescribeConversionStatus(item.Status), item.Message));
            }

            OnPropertyChanged(nameof(HasConvertRows));

            // 并入的模组文件只在开启开关时才有意义，所以仅在非零时显示。
            string modFilesNote = counts.OtherFilesIncluded > 0
                ? $"，并入模组文件 {counts.OtherFilesIncluded:N0}"
                : string.Empty;

            ConvertStatus =
                $"完成：替换 {counts.Replaced:N0}，主 Pak 缺失 {counts.MissingInTarget:N0}，" +
                $"跳过 {counts.Skipped:N0}，失败 {counts.Failed:N0}{modFilesNote}";

            AddLog(
                $"Pak 转换：{Path.GetFileName(sourcePak)} → {Path.GetFileName(targetPak)}，" +
                $"候选纹理 {counts.TextureCandidates:N0}，替换 {counts.Replaced:N0}，" +
                $"缺失 {counts.MissingInTarget:N0}，跳过 {counts.Skipped:N0}，失败 {counts.Failed:N0}，" +
                $"并入模组文件 {counts.OtherFilesIncluded:N0}，输出文件 {counts.TotalFiles:N0}",
                "转换");

            if (!android)
            {
                StatusText = $"Pak 转换完成：{Path.GetFileName(outputPath)}（替换 {counts.Replaced:N0} 张纹理）";
                return;
            }

            if (_convertOutputTarget is null)
            {
                StatusText = "转换完成，但输出位置未就绪。";
                return;
            }

            await using (Stream src = File.OpenRead(outputPath))
            await using (Stream dst = await _convertOutputTarget.OpenWriteAsync())
            {
                await src.CopyToAsync(dst);
            }

            StatusText = $"Pak 转换完成：{_convertOutputTarget.Name}（替换 {counts.Replaced:N0} 张纹理）";
            try
            {
                await InvokeNativeShareAsync([outputPath], $"Prism 转换 Pak：{_convertOutputTarget.Name}");
            }
            catch
            {
                // 分享失败不影响已保存的文件。
            }
            finally
            {
                TryDeleteFile(outputPath);
            }
        });
    }

    private static string DescribeConversionStatus(UAssetTexture.Core.PakConversionItemStatus status) => status switch
    {
        UAssetTexture.Core.PakConversionItemStatus.Replaced => "已替换",
        UAssetTexture.Core.PakConversionItemStatus.MissingInTarget => "主 Pak 无此项",
        UAssetTexture.Core.PakConversionItemStatus.Skipped => "跳过",
        _ => "失败",
    };

    [RelayCommand]
    private async Task BrowseConvertSourceAsync()
    {
        string? path = await PickFileAsync("选择待转换的 Pak", ["*.pak"], ConvertSourcePath);
        if (path is not null)
        {
            ConvertSourcePath = path;
            StatusText = $"待转换 Pak：{Path.GetFileName(path)}";
        }
    }

    [RelayCommand]
    private async Task BrowseConvertOutputAsync()
    {
        TopLevel? top = TopLevel;
        if (top is null)
        {
            return;
        }

        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "选择转换输出 Pak 路径",
            SuggestedFileName = $"converted_{DateTime.Now:yyyyMMdd_HHmmss}.pak",
            FileTypeChoices = [new FilePickerFileType("Pak 文件") { Patterns = ["*.pak"] }],
        });
        if (file is null)
        {
            return;
        }

        _convertOutputTarget = file;
        ConvertOutputPath = OperatingSystem.IsAndroid()
            ? file.Name
            : file.TryGetLocalPath() ?? file.Name;
    }

    // ---------- 合并列表维护 ----------
    [RelayCommand]
    private void RemoveMergePak(MergePakItem? item)
    {
        if (item is null)
        {
            return;
        }

        MergePaks.Remove(item);
        if (ReferenceEquals(SelectedMergePak, item))
        {
            SelectedMergePak = null;
        }

        RenumberMergePaks();
        NotifyMergeListChanged();
        SaveSettings();
        MergeStatus = MergePaks.Count == 0 ? "就绪" : $"已移除 {item.DisplayName}，当前 {MergePaks.Count} 个输入";
    }

    [RelayCommand]
    private void ClearMergePaks()
    {
        // 没有固定的主 Pak，清空就是全部移除。
        MergePaks.Clear();

        RenumberMergePaks();
        NotifyMergeListChanged();
        SaveSettings();
        MergeStatus = "就绪";
    }

    /// <summary>
    /// 把 <paramref name="source"/> 移动到 <paramref name="targetIndex"/>（拖动排序）。
    /// 现在每一项都可以移动，第 0 位就是最高优先级。
    /// </summary>
    public void MoveMergePak(MergePakItem source, int targetIndex)
    {
        int from = MergePaks.IndexOf(source);
        if (from < 0)
        {
            return;
        }

        int to = Math.Clamp(targetIndex, 0, MergePaks.Count - 1);
        if (to == from)
        {
            return;
        }

        MergePaks.Move(from, to);
        RenumberMergePaks();
        NotifyMergeListChanged();
        SaveSettings();
        MergeStatus = $"已调整覆盖顺序（{MergePaks.Count} 个输入，越靠上优先级越高）";
    }

    /// <summary>按当前顺序刷新序号标签（1 起，1 表示优先级最高）。</summary>
    private void RenumberMergePaks()
    {
        for (int i = 0; i < MergePaks.Count; i++)
        {
            MergePaks[i].OrderLabel = (i + 1).ToString();
        }
    }

    /// <summary>
    /// 恢复上次的合并列表。仅保留仍然存在的文件：Android 的 SAF 缓存可能已被
    /// 系统清理，Windows 上用户也可能移走了文件。
    /// </summary>
    private void RestoreMergePaks(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                if (!File.Exists(path))
                {
                    Services.AppLog.Warn($"合并列表跳过已不存在的文件：{path}", "合并");
                    continue;
                }
            }
            catch
            {
                continue;
            }

            MergePaks.Add(new MergePakItem(path, Path.GetFileName(path)));
        }

        RenumberMergePaks();
    }

    /// <summary>把（可能重复的）路径加入合并列表；列表顺序即覆盖优先级。</summary>
    private void AddMergePaks(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            // 同一份 Pak 只保留一次。
            if (MergePaks.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            MergePaks.Add(new MergePakItem(path, Path.GetFileName(path)));
            added++;
        }

        if (added > 0)
        {
            RenumberMergePaks();
            NotifyMergeListChanged();
            SaveSettings();
        }

        MergeStatus = MergePaks.Count == 0
            ? "就绪"
            : $"已加入 {added} 个 Pak，共 {MergePaks.Count} 个输入；越靠上优先级越高";
    }

    // ============ 设置（应用内视图，兼容手机） ============

    [ObservableProperty]
    public partial string VersionText { get; set; }

    partial void OnVersionTextChanged(string value) => OnPropertyChanged(nameof(VersionLine));

    [ObservableProperty]
    public partial string CliStatus { get; set; }

    [ObservableProperty]
    public partial string AstcencStatus { get; set; }

    [ObservableProperty]
    public partial string TexconvStatus { get; set; }

    // ── 工具状态：设置页把「依赖探测」画成一行一条带状态点的清单，
    //    而不是三行同色的句子（"已找到 xxx" / "未找到 xxx" 扫不出来）。
    [ObservableProperty]
    public partial bool HasCli { get; set; }

    [ObservableProperty]
    public partial bool HasAstcenc { get; set; }

    [ObservableProperty]
    public partial bool HasTexconv { get; set; }

    public bool HasOodle => OodleNative.IsAvailable;

    public string CliValue => HasCli ? "已就绪" : "缺失 · 贴图替换不可用";
    public string AstcencValue => HasAstcenc ? "已就绪" : "缺失 · ASTC 贴图不可用";
    public string TexconvValue => HasTexconv ? "已就绪" : "缺失 · DXT/BC 贴图不可用";
    public string OodleValue => HasOodle ? "已就绪" : "缺失 · 自动降级为不压缩";

    /// <summary>
    /// Oodle 原生库状态。显示出来是因为：请求 Oodle 压缩而本机没有它时，
    /// 打包会**自动降级为不压缩**（repak_bind 缺 Oodle 会 panic 并 abort 进程，拦不住）。
    /// </summary>
    public string OodleStatus => OodleNative.IsAvailable
        ? "已找到 Oodle（可开启 Oodle 压缩）"
        : "未找到 Oodle（oo2core_9_win64.dll）—— 勾选 Oodle 压缩时将自动改用不压缩";

    [ObservableProperty]
    public partial string TempDirectory { get; set; }

    [ObservableProperty]
    public partial string LogText { get; set; } = "暂无日志";

    // ============ 列表缩略图（本版本起默认开启，设置中可关闭） ============

    [ObservableProperty]
    public partial bool ShowThumbnails { get; set; }

    private CancellationTokenSource? _thumbCts;
    private readonly SemaphoreSlim _thumbGate = new(4, 4); // 并发解码上限
    private bool _resumeThumbnailsAfterUserAction;

    partial void OnShowThumbnailsChanged(bool value)
    {
        if (value)
        {
            StartThumbnails();
        }
        else
        {
            ClearThumbnails();
        }

        SaveSettings();
    }

    /// <summary>
    /// 用户开始操作时先打断后台缩略图生成，把 Pak 读取优先级让给用户；
    /// 操作结束后如果没有新的列表渲染，再继续补缩略图。
    /// </summary>
    private void PrioritizeUserAction()
    {
        if (_thumbCts is null || _thumbCts.IsCancellationRequested)
        {
            return;
        }

        _resumeThumbnailsAfterUserAction = true;
        _thumbCts.Cancel();
    }

    private void ResumeThumbnailsAfterUserAction()
    {
        if (!_resumeThumbnailsAfterUserAction)
        {
            return;
        }

        _resumeThumbnailsAfterUserAction = false;
        if (ShowThumbnails && IsBrowseTab && Entries.Any(item => item.IsImageKind && item.Thumbnail is null))
        {
            StartThumbnails();
        }
    }

    /// <summary>为当前列表中尚未生成缩略图的图片类条目生成缩略图（后台解码，UI 线程建图）。</summary>
    private void StartThumbnails()
    {
        if (!ShowThumbnails || !IsPakOpen || !IsBrowseTab)
        {
            return;
        }

        // 显式开始新一轮缩略图后，用户操作结束就不需要再次恢复，避免重复解码。
        _resumeThumbnailsAfterUserAction = false;
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _thumbCts = new CancellationTokenSource();
        CancellationToken ct = _thumbCts.Token;
        int maxThumbnails = OperatingSystem.IsAndroid() ? 240 : 600;
        List<EntryItem> candidates = Entries
            .Where(e => e.IsImageKind && e.Thumbnail is null)
            .Take(maxThumbnails)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (EntryItem item in candidates)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                await _thumbGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    AssetPreviewDto? preview = null;
                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        preview = await _session.ReadPreviewAsync(item.FullPath, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 非纹理或解码失败：跳过
                    }
                    finally
                    {
                        _gate.Release();
                    }

                    if (preview?.Data is { Length: > 0 } data && !ct.IsCancellationRequested)
                    {
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (!ct.IsCancellationRequested)
                            {
                                item.Thumbnail = DecodeImage(data, 64);
                            }
                        });
                    }
                }
                finally
                {
                    _thumbGate.Release();
                }
            }
        });
    }

    private void ClearThumbnails()
    {
        _resumeThumbnailsAfterUserAction = false;
        _thumbCts?.Cancel();
        foreach (EntryItem item in Entries)
        {
            item.Thumbnail = null;
        }
    }

    private void InitializeSettings()
    {
        VersionText = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "未知";
        Services.UAssetCliRunner? runner = Services.UAssetCliRunner.TryCreate();
        HasCli = runner is not null;
        HasAstcenc = runner?.HasAstcenc == true;
        HasTexconv = runner?.HasTexconv == true;
        CliStatus = HasCli ? "已找到 UAssetCLI" : "未找到 UAssetCLI（替换功能不可用）";
        AstcencStatus = HasAstcenc ? "已找到 astcenc" : "未找到 astcenc";
        TexconvStatus = HasTexconv ? "已找到 texconv" : "未找到 texconv";
        OnPropertyChanged(nameof(CliValue));
        OnPropertyChanged(nameof(AstcencValue));
        OnPropertyChanged(nameof(TexconvValue));
        OnPropertyChanged(nameof(OodleValue));
        TempDirectory = Path.GetTempPath();
        _cliRunnerStatusForLog = CliStatus;
        _astcencStatusForLog = AstcencStatus;
        _texconvStatusForLog = TexconvStatus;

        // 落盘镜像：进程崩溃/被系统杀掉时仍能保留最后几条日志。
        try
        {
            Services.AppLog.EnableFileMirror(Path.Combine(PrivateDataDir(), "logs", "prism-last-session.log"));
        }
        catch
        {
            // 镜像失败不影响内存日志。
        }

        Services.AppLog.Info(
            $"应用启动 v{VersionText} | {System.Runtime.InteropServices.RuntimeInformation.OSDescription} | " +
            $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} | 临时目录 {TempDirectory}",
            "启动");
        Services.AppLog.Info($"依赖探测：{CliStatus} / {AstcencStatus} / {TexconvStatus}", "启动");
        if (!string.IsNullOrWhiteSpace(PakPath))
        {
            Services.AppLog.Debug($"上次 Pak 路径：{PakPath}", "启动");
        }

        if (!string.IsNullOrWhiteSpace(UsmapPath))
        {
            Services.AppLog.Debug(
                $"上次映射路径（{PakTool.Core.MappingsLoader.DescribeFormat(UsmapPath)}）：{UsmapPath}",
                "启动");
        }

        RefreshLog();
    }

    private string _cliRunnerStatusForLog = string.Empty;
    private string _astcencStatusForLog = string.Empty;
    private string _texconvStatusForLog = string.Empty;

    /// <summary>刷新日志预览文本（设置页展示，只取尾部若干行避免长日志卡 UI）。</summary>
    private void RefreshLog() => LogText = Services.AppLog.Count == 0
        ? "暂无日志"
        : Services.AppLog.Tail(LogPreviewLineLimit);

    /// <summary>设置页日志预览的最大行数。</summary>
    private const int LogPreviewLineLimit = 400;

    /// <summary>记录一条 INFO 日志并刷新预览。</summary>
    private void AddLog(string line, string category = "app")
    {
        Services.AppLog.Info(line, category);
        RefreshLog();
    }

    /// <summary>记录一条 WARN 日志并刷新预览。</summary>
    private void AddWarnLog(string line, string category = "app")
    {
        Services.AppLog.Warn(line, category);
        RefreshLog();
    }

    /// <summary>记录异常（含完整堆栈）并刷新预览。</summary>
    private void AddErrorLog(Exception exception, string context, string category = "app", long? elapsedMs = null)
    {
        Services.AppLog.Exception(exception, context, category, elapsedMs: elapsedMs);
        RefreshLog();
    }

    /// <summary>导出日志：桌面写文件，Android 走 SAF 保存流。</summary>
    [RelayCommand]
    private async Task ExportLogAsync()
    {
        TopLevel? top = TopLevel;
        if (top is null)
        {
            return;
        }

        PrioritizeUserAction();
        try
        {
            IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出日志",
                SuggestedFileName = $"prism-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                FileTypeChoices = [new FilePickerFileType("文本") { Patterns = ["*.txt"] }],
            });
            if (file is null)
            {
                return;
            }

            byte[] content = System.Text.Encoding.UTF8.GetBytes(BuildLogReport());
            if (OperatingSystem.IsAndroid())
            {
                // 先写应用缓存，再流式写入用户选择的位置；保存完成后直接唤起系统分享。
                string tempLogPath = Path.Combine(Path.GetTempPath(), $"prism-log-{Guid.NewGuid():N}.txt");
                await File.WriteAllBytesAsync(tempLogPath, content);
                try
                {
                    await using (Stream dst = await file.OpenWriteAsync())
                    {
                        await dst.WriteAsync(content);
                    }

                    AddLog($"日志已导出：{file.Name}（{Services.AppLog.Count:N0} 行）");
                    try
                    {
                        await InvokeNativeShareAsync([tempLogPath], $"Prism 日志：{file.Name}");
                    }
                    catch
                    {
                        // 分享失败不影响已导出的日志。
                    }
                }
                finally
                {
                    TryDeleteFile(tempLogPath);
                }
            }
            else if (file.TryGetLocalPath() is { } path)
            {
                await File.WriteAllBytesAsync(path, content);
                AddLog($"日志已导出：{path}（{Services.AppLog.Count:N0} 行）");
            }

            StatusText = $"日志已导出（{Services.AppLog.Count:N0} 行）。";
        }
        finally
        {
            ResumeThumbnailsAfterUserAction();
        }
    }

    /// <summary>
    /// 构建导出报告：环境头 + 当前会话配置 + 全部日志。
    /// 附带配置状态是为了让日志脱离本机后仍可解读（编码器是否可用、映射格式等）。
    /// </summary>
    private string BuildLogReport()
    {
        Dictionary<string, string?> environment = new()
        {
            ["UAssetCLI"] = CliStatus,
            ["astcenc"] = AstcencStatus,
            ["texconv"] = TexconvStatus,
            ["Pak"] = DescribeCurrentPath(PakPath),
            ["映射文件"] = DescribeCurrentPath(UsmapPath),
            ["导出目录"] = string.IsNullOrWhiteSpace(ExportDirectory) ? "<未设置>" : ExportDirectory,
            ["缩略图"] = ShowThumbnails ? "开启" : "关闭",
            ["Oodle 压缩"] = UseOodleCompression ? "开启" : "关闭",
        };

        return Services.AppLog.BuildReport($"Prism {VersionText}", environment);
    }

    /// <summary>路径摘要：带映射格式标注，空值给出可读占位。</summary>
    private static string DescribeCurrentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "<未选择>";
        }

        return PakTool.Core.MappingsLoader.Patterns.Any(p =>
            path.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase))
            ? $"{path}（{PakTool.Core.MappingsLoader.DescribeFormat(path)}）"
            : path;
    }

    private IStorageFile? _mergeOutputTarget;

    /// <summary>平台确认对话框委托：Android 壳注入原生 AlertDialog；桌面直接用 Window 弹窗。</summary>
    public Func<string, string, Task<bool>>? NativeConfirmAsync { get; set; }

    /// <summary>Android 系统分享委托：由 Android 壳复制文件并通过 FileProvider 打开分享面板。</summary>
    public Func<IReadOnlyList<string>, string, Task>? NativeShareFilesAsync { get; set; }

    /// <summary>Android 音频播放委托：由 Android 壳用 MediaPlayer 播放本地 WAV，第二个参数为起始秒数。</summary>
    public Func<string, double, Task>? NativeAudioPlayAsync { get; set; }

    /// <summary>Android 音频停止委托。</summary>
    public Func<Task>? NativeAudioStopAsync { get; set; }

    /// <summary>Android 音频进度定位委托（秒）。</summary>
    public Func<double, Task>? NativeAudioSeekAsync { get; set; }

    /// <summary>Android 音频暂停/继续委托（true=暂停，false=继续）。</summary>
    public Func<bool, Task>? NativeAudioSetPausedAsync { get; set; }

    // ============ 文件选择 ============

    [RelayCommand]
    private async Task BrowsePakAsync()
    {
        string? path = await PickFileAsync("选择 Pak 文件", ["*.pak"], PakPath);
        if (path is not null)
        {
            PakPath = path;
            StatusText = $"已选择 Pak：{Path.GetFileName(path)}";
        }
    }

    /// <summary>界面过渡动画开关（设置页）。</summary>
    [ObservableProperty]
    public partial bool IsAnimationsEnabled { get; set; } = true;

    partial void OnIsAnimationsEnabledChanged(bool value) => SaveSettings();

    [RelayCommand]
    private async Task BrowseUsmapAsync()
    {
        // 同时接受 .usmap 与 .jmap（含 gz）；CUE4Parse 按扩展名自动选择解析器。
        string? path = await PickFileAsync(
            "选择映射文件（.usmap / .jmap）",
            PakTool.Core.MappingsLoader.Patterns,
            UsmapPath,
            ["application/octet-stream", "application/gzip", "application/json"]);
        if (path is not null)
        {
            UsmapPath = path;
            string format = PakTool.Core.MappingsLoader.DescribeFormat(path);
            StatusText = $"已选择 {format} 映射：{Path.GetFileName(path)}";
            AddLog($"选择映射文件（{format}）：{Path.GetFileName(path)}");
        }
    }

    /// <summary>
    /// 多选加入合并列表；后加入的排在下方，因此优先级低于已有的项。
    /// Android 上逐个复制到私有缓存（SAF 无法一次拿多个本地路径）。
    /// </summary>
    [RelayCommand]
    private async Task BrowseMergePakAsync()
    {
        TopLevel? top = TopLevel;
        if (top is null)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要合并的 Pak（可多选）",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Pak 文件") { Patterns = ["*.pak"] }],
        });
        if (files.Count == 0)
        {
            return;
        }

        List<string> resolved = [];
        if (OperatingSystem.IsAndroid())
        {
            // SAF：逐个复制到私有目录，文件名保留可读前缀 + 唯一后缀。
            string destDir = Path.Combine(PrivateDataDir(), "picked");
            Directory.CreateDirectory(destDir);
            foreach (IStorageFile file in files)
            {
                string stem = SanitizeFileName(Path.GetFileNameWithoutExtension(file.Name));
                if (string.IsNullOrWhiteSpace(stem))
                {
                    stem = "merge";
                }

                string destPath = Path.Combine(destDir, $"{stem}.{Guid.NewGuid():N}.pak");
                await using Stream src = await file.OpenReadAsync();
                await using (FileStream dst = File.Create(destPath))
                {
                    await src.CopyToAsync(dst);
                }

                resolved.Add(destPath);
            }
        }
        else
        {
            foreach (IStorageFile file in files)
            {
                if (file.TryGetLocalPath() is { } path)
                {
                    resolved.Add(path);
                }
            }
        }

        AddMergePaks(resolved);
        StatusText = $"合并列表：{MergePaks.Count} 个 Pak（越靠上优先级越高）";
        AddLog($"合并列表更新：{MergePaks.Count} 个输入", "合并");
    }

    [RelayCommand]
    private async Task BrowseMergeOutputAsync()
    {
        TopLevel? top = TopLevel;
        if (top is null)
        {
            return;
        }

        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "选择输出 Pak 路径",
            SuggestedFileName = "merged.pak",
            FileTypeChoices = [new FilePickerFileType("Pak 文件") { Patterns = ["*.pak"] }],
        });
        if (file is null)
        {
            return;
        }

        _mergeOutputTarget = file;
        MergeOutputPath = OperatingSystem.IsAndroid()
            ? file.Name
            : file.TryGetLocalPath() ?? file.Name;
    }

    [RelayCommand]
    private async Task BrowseExportDirAsync()
    {
        TopLevel? top = TopLevel;
        if (top is null)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择导出目录",
            AllowMultiple = false,
        });
        if (folders.Count == 0)
        {
            return;
        }

        IStorageFolder folder = folders[0];
        if (OperatingSystem.IsAndroid())
        {
            // Android 优先保存 SAF 书签（持久化授权），不依赖“所有文件”权限。
            _exportFolder = folder;
            try
            {
                _exportDirectoryBookmark = folder.CanBookmark
                    ? await folder.SaveBookmarkAsync() ?? string.Empty
                    : string.Empty;
            }
            catch
            {
                _exportDirectoryBookmark = string.Empty;
            }

            ExportDirectory = folder.TryGetLocalPath() is { } localPath && !string.IsNullOrWhiteSpace(localPath)
                ? localPath
                : folder.Name;
            SaveSettings();
            StatusText = string.IsNullOrWhiteSpace(_exportDirectoryBookmark)
                ? $"导出目录已选择（仅本次运行有效）：{ExportDirectory}"
                : $"导出目录已保存：{ExportDirectory}";
            return;
        }

        if (folder.TryGetLocalPath() is { } dir)
        {
            _exportFolder = null;
            _exportDirectoryBookmark = string.Empty;
            ExportDirectory = dir;
        }
    }

    /// <summary>Android 启动后恢复上次选择的 SAF 导出目录（书签授权跨重启有效）。</summary>
    public async Task RestoreAndroidExportFolderAsync()
    {
        if (!OperatingSystem.IsAndroid() || TopLevel?.StorageProvider is null)
        {
            return;
        }

        await EnsureAndroidExportFolderAsync();
    }

    private async Task<string?> PickFileAsync(
        string title,
        string[] patterns,
        string? replaceCachePath = null,
        IReadOnlyList<string>? mimeTypes = null)
    {
        // 无头测试没有 StorageProvider，注入这个委托就能把"选文件"之后的全流程跑通。
        if (FilePickerOverride is not null)
        {
            return await FilePickerOverride(title, patterns);
        }

        TopLevel? top = TopLevel;
        if (top is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("文件") { Patterns = patterns, MimeTypes = mimeTypes }],
        });
        if (files.Count == 0)
        {
            return null;
        }

        IStorageFile file = files[0];
        if (OperatingSystem.IsAndroid())
        {
            // SAF 返回 content:// URI，无本地路径：复制到应用私有目录。
            // 文件名加短唯一后缀，避免不同来源同名文件互相覆盖；
            // 更换选择后旧缓存立即删除，下次启动还会清理未引用文件。
            string destDir = Path.Combine(PrivateDataDir(), "picked");
            Directory.CreateDirectory(destDir);
            string stem = SanitizeFileName(Path.GetFileNameWithoutExtension(file.Name));
            string extension = Path.GetExtension(file.Name);
            if (string.IsNullOrWhiteSpace(stem))
            {
                stem = "picked";
            }

            string destPath = Path.Combine(destDir, $"{stem}.{Guid.NewGuid():N}{extension}");
            await using Stream src = await file.OpenReadAsync();
            await using (FileStream dst = File.Create(destPath))
            {
                await src.CopyToAsync(dst);
            }

            // 下载缓存之类的文件名常常没有扩展名，缓存副本会变成 <名字>.<guid>。
            // 补一个按文件头猜出来的扩展名，后面识别格式、看日志都方便。
            if (extension.Length == 0)
            {
                string guessed = UAssetAudio.Core.AudioSourceDecoder.SuggestExtension(destPath);
                if (guessed.Length > 0)
                {
                    string renamed = destPath + guessed;
                    File.Move(destPath, renamed, true);
                    destPath = renamed;
                }
            }

            DeleteReplacedAndroidPickedFile(replaceCachePath, destPath);
            return destPath;
        }

        return file.TryGetLocalPath();
    }

    private static void DeleteReplacedAndroidPickedFile(string? oldPath, string newPath)
    {
        if (!OperatingSystem.IsAndroid() || string.IsNullOrWhiteSpace(oldPath))
        {
            return;
        }

        string pickedRoot = Path.GetFullPath(Path.Combine(PrivateDataDir(), "picked"));
        string oldFullPath;
        try
        {
            oldFullPath = Path.GetFullPath(oldPath);
        }
        catch
        {
            return;
        }

        if (!oldFullPath.StartsWith(pickedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(oldFullPath, Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TryDeleteFile(oldFullPath);
    }

    // ============ 工具 ============

    /// <summary>
    /// Android 缓存管理：保留当前设置引用的 picked 文件，删除已更换/不再引用的缓存，
    /// 并清理上次会话遗留的分享与替换临时目录。Windows 不复制 SAF 文件，无需清理。
    /// </summary>
    private void CleanupAndroidCaches()
    {
        if (!OperatingSystem.IsAndroid())
        {
            return;
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 合并列表可能有多项，逐项保留（否则下次启动前就被清掉了）。
        IEnumerable<string?> tracked = new[] { PakPath, UsmapPath, ConvertSourcePath }
            .Concat(MergePaks.Select(p => (string?)p.Path));
        foreach (string? path in tracked)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                keep.Add(Path.GetFullPath(path));
            }
            catch
            {
                // 非法路径无法作为缓存保留项。
            }
        }

        string pickedRoot = Path.Combine(PrivateDataDir(), "picked");
        try
        {
            if (Directory.Exists(pickedRoot))
            {
                foreach (string file in Directory.EnumerateFiles(pickedRoot))
                {
                    string fullPath;
                    try
                    {
                        fullPath = Path.GetFullPath(file);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!keep.Contains(fullPath))
                    {
                        TryDeleteFile(fullPath);
                    }
                }
            }
        }
        catch
        {
            // 缓存清理失败不应阻塞启动。
        }

        // 清理上一次运行留下的临时产物（Android 的临时目录位于应用私有 cache 内）。
        try
        {
            string tempRoot = Path.GetTempPath();
            foreach (string name in Directory.Exists(tempRoot)
                         ? Directory.EnumerateDirectories(tempRoot)
                         : Array.Empty<string>())
            {
                string directoryName = Path.GetFileName(name);
                if (directoryName.Equals("PrismShare", StringComparison.OrdinalIgnoreCase) ||
                    directoryName.Equals("prism-desktop-patch", StringComparison.OrdinalIgnoreCase) ||
                    directoryName.Equals("prism-desktop-audio", StringComparison.OrdinalIgnoreCase) ||
                    directoryName.StartsWith("PrismDesktopMerge-", StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteDirectory(name);
                }
            }
        }
        catch
        {
            // 临时目录枚举失败时同样忽略。
        }
    }

    private void ClearPreview()
    {
        StopAudioPreview();
        StopVideoPreview();
        PreviewImage = null;
        PreviewTitle = string.Empty;
        PreviewText = string.Empty;
        PreviewDetails = [];
        SelectedPathText = string.Empty;
        HasPreview = false;
        _selectedLocresPath = null;
        HasLocresPreview = false;
        ModelPreview = null;
        PreviewEmptyText = "此文件无预览";
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        IsBusy = true;
        PrioritizeUserAction();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusText = $"错误：{ex.Message}";
            // 日志里带堆栈：这类 NRE 光看消息根本定位不到
            AddLog($"错误：{ex}");
        }
        finally
        {
            IsBusy = false;
            ResumeThumbnailsAfterUserAction();
        }
    }

    private static Bitmap? LoadImage(string path, int maxWidth)
    {
        using FileStream fs = File.OpenRead(path);
        return Bitmap.DecodeToWidth(fs, maxWidth);
    }

    private static Bitmap? DecodeImage(byte[] data, int maxWidth)
    {
        using MemoryStream ms = new(data);
        return Bitmap.DecodeToWidth(ms, maxWidth);
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string FormatSize(long bytes) => EntryItem.FormatSize(bytes);

    /// <summary>
    /// 外部工具目录：<c>binka_encode.exe</c>（音频编码）、<c>ffmpeg.exe</c>（可选，解更多音频格式）
    /// 都从这里找。构建时 <c>tools/*.exe</c> 会被复制到输出目录的同名子目录。
    /// </summary>
    public static string ToolsDirectory => Path.Combine(AppContext.BaseDirectory, "tools");

    /// <summary>应用私有数据目录（Android = FilesDir/Prism，桌面 = %LOCALAPPDATA%/Prism）。</summary>
    private static string PrivateDataDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prism");

    private static string SanitizeFileName(string fileName)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }

        return fileName;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}

/// <summary>
/// 合并前的检查结果：每个输入 Pak 的文件数，以及路径冲突统计。
/// </summary>
public sealed record MergeInspectionResponse(
    int TopCount,
    int MergeCount,
    IReadOnlyList<int> PerPakCounts,
    int ConflictCount,
    IReadOnlyList<string> Conflicts)
{
    public static MergeInspectionResponse Empty { get; } = new(0, 0, [], 0, []);

    /// <summary>一行摘要，用于合并页状态栏。</summary>
    public string Summary => PerPakCounts.Count == 0
        ? "尚未加入待合并的 Pak"
        : $"{PerPakCounts.Count} 个输入（首项 {TopCount:N0} 项，合计 {MergeCount:N0} 项），路径冲突 {ConflictCount:N0} 个";
}

/// <summary>Merge 构建结果。</summary>
public sealed record MergeBuildResponse(
    string OutputPakPath,
    int FileCount,
    int ConflictCount,
    int ReplacedCount,
    /// <summary>做了三路合并的 .locres 文件数（设置了基准且确实合并了才有值）。</summary>
    int MergedLocresFiles = 0,
    /// <summary>这些本地化里相对基准真正生效的改动词条数。</summary>
    int MergedLocresEntries = 0,
    /// <summary>被多份模组改动、按优先级裁决的词条数。</summary>
    int LocresConflicts = 0,
    /// <summary>因异常退回整文件覆盖的本地化个数。</summary>
    int LocresFailed = 0);

/// <summary>Pak 转换的界面状态（单条纹理）。</summary>
public sealed partial class PakConversionRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public PakConversionRow(string pakPath, string status, string? message)
    {
        PakPath = pakPath;
        StatusText = status;
        Message = message ?? string.Empty;
    }

    public string PakPath { get; }

    public string StatusText { get; }

    public string Message { get; }

    public bool HasMessage => Message.Length > 0;

    public bool IsFailure => StatusText == "失败";
}
