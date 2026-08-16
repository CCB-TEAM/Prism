using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PakTool.Core;
using Prism.Desktop.Models;
using Prism.Desktop.Services;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
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
        ExportDirectory = _settings.ExportDirectory;
        _exportDirectoryBookmark = _settings.ExportDirectoryBookmark;
        PakPath = _settings.PakPath;
        UsmapPath = _settings.UsmapPath;
        MergePakPath = _settings.MergePakPath;
        MergeOutputPath = _settings.MergeOutputPath;
        AesKey = _settings.AesKey;
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
    }

    [ObservableProperty]
    public partial bool IsPreviewExpanded { get; set; } = true;

    // ============ 视图导航（主页 / 工作区 / 合并页 / 设置） ============

    [ObservableProperty]
    public partial string CurrentView { get; set; } = "Home";

    public bool IsHomeVisible => CurrentView == "Home";

    public bool IsWorkspaceVisible => CurrentView == "Workspace";

    public bool IsMergeVisible => CurrentView == "Merge";

    public bool IsSettingsVisible => CurrentView == "Settings";

    partial void OnCurrentViewChanged(string value)
    {
        OnPropertyChanged(nameof(IsHomeVisible));
        OnPropertyChanged(nameof(IsWorkspaceVisible));
        OnPropertyChanged(nameof(IsMergeVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
    }

    [RelayCommand]
    private void GoHome() => CurrentView = "Home";

    [RelayCommand]
    private void GoWorkspace() => CurrentView = "Workspace";

    [RelayCommand]
    private void GoMerge() => CurrentView = "Merge";

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

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<EntryItem> Entries { get; set; } = [];

    [ObservableProperty]
    public partial EntryItem? SelectedItem { get; set; }

    private bool IsPakOpen => PakPath.Length > 0;

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

    /// <summary>内置播放器进度条：Android 支持拖动定位；Windows 的 Win32 播放器不提供 seek。</summary>
    public bool CanSeekAudio => HasAudioPreview && AudioDuration > 0 && OperatingSystem.IsAndroid();

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

    [RelayCommand]
    private async Task ToggleAudioPlayback()
    {
        if (!HasAudioPreview || _audioTempFile is null)
        {
            return;
        }

        if (IsAudioPlaying)
        {
            // 暂停，保留进度；再次点击继续播放。
            if (OperatingSystem.IsAndroid())
            {
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
            }
            else
            {
                Win32PlaySound.Stop();
            }

            IsAudioPlaying = false;
            IsAudioPaused = false;
            return;
        }

        if (OperatingSystem.IsAndroid() && IsAudioPaused)
        {
            if (NativeAudioSetPausedAsync is not null)
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
        }

        IsAudioPlaying = true;
        IsAudioPaused = false;
        if (OperatingSystem.IsAndroid())
        {
            if (NativeAudioPlayAsync is null)
            {
                IsAudioPlaying = false;
                StatusText = "音频播放服务未就绪，请重新打开应用后重试。";
                return;
            }

            try
            {
                // 内置播放器支持拖动进度后从指定位置开始播放。
                await NativeAudioPlayAsync(_audioTempFile, AudioPosition);
            }
            catch (Exception ex)
            {
                IsAudioPlaying = false;
                StatusText = "音频播放失败：" + ex.Message;
            }
        }
        else
        {
            AudioPosition = 0;
            Win32PlaySound.PlayFile(_audioTempFile);
        }
    }

    [RelayCommand]
    private async Task SeekAudioAsync()
    {
        if (!CanSeekAudio || NativeAudioSeekAsync is null)
        {
            return;
        }

        await NativeAudioSeekAsync(Math.Clamp(AudioPosition, 0, AudioDuration));
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
        ExportRawCommand.NotifyCanExecuteChanged();
        ShareRawCommand.NotifyCanExecuteChanged();
        AddSelectedToPatchCommand.NotifyCanExecuteChanged();
        ExportFolderRawCommand.NotifyCanExecuteChanged();
        ExportFolderImagesCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    partial void OnUsmapPathChanged(string value) => SaveSettings();

    partial void OnMergePakPathChanged(string value)
    {
        MergePakCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    partial void OnMergeOutputPathChanged(string value)
    {
        MergePakCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    partial void OnAesKeyChanged(string value) => SaveSettings();

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
        _settings.ExportDirectory = ExportDirectory;
        _settings.ExportDirectoryBookmark = OperatingSystem.IsAndroid() ? _exportDirectoryBookmark : string.Empty;
        _settings.PakPath = PakPath;
        _settings.UsmapPath = UsmapPath;
        _settings.MergePakPath = MergePakPath;
        // Android 上输出目标是 SAF 会话句柄，不持久化（重启后需重新选择）
        _settings.MergeOutputPath = OperatingSystem.IsAndroid() ? string.Empty : MergeOutputPath;
        _settings.AesKey = AesKey;
        AppSettingsStore.Save(_settings);
    }

    public bool CanPickReplacement => SelectedPatchItem is not null && !IsBusy;

    public bool CanRemovePatchItem => SelectedPatchItem is not null;

    // ============ 合并页 ============

    [ObservableProperty]
    public partial string MergePakPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MergeOutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AskBeforeReplace { get; set; } = true;

    [ObservableProperty]
    public partial string MergeStatus { get; set; } = "就绪";

    public bool CanMergePak => IsPakOpen && MergePakPath.Length > 0 && MergeOutputPath.Length > 0;

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
            await NavigateToAsync(ParentOf(CurrentFolder));
        }
        finally
        {
            ResumeThumbnailsAfterUserAction();
        }
    }

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

        await RunBusyAsync(async () =>
        {
            IReadOnlyList<ArchiveEntryDto> results;
            await _gate.WaitAsync();
            try
            {
                results = await _session.SearchAsync(SearchQuery.Trim(), 500);
            }
            finally
            {
                _gate.Release();
            }

            Entries = new ObservableCollection<EntryItem>(results.Select(EntryItem.Create));
            CurrentPathText = $"搜索：{SearchQuery.Trim()}";
            StatusText = $"搜索命中 {results.Count:N0} 项。";
            AddLog($"搜索“{SearchQuery.Trim()}”命中 {results.Count:N0} 项");
            ClearPreview();
            StartThumbnails();
        });
    }

    private async Task NavigateToAsync(string folder)
    {
        IReadOnlyList<ArchiveEntryDto> entries = await ListFolderAsync(folder);
        Entries = new ObservableCollection<EntryItem>(entries.Select(EntryItem.Create));
        CurrentFolder = folder;
        CurrentPathText = "/" + folder;
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
            if (preview.Data is { Length: > 0 })
            {
                using MemoryStream ms = new(preview.Data);
                PreviewImage = Bitmap.DecodeToWidth(ms, 1280);
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
            if (string.Equals(preview.Kind, "audio", StringComparison.OrdinalIgnoreCase) || preview.CanPlay)
            {
                try
                {
                    AudioPayloadDto audio = await ReadAudioPayloadAsync(item.FullPath);
                    if (audio.Data is { Length: > 0 })
                    {
                        _audioTempFile = Path.Combine(Path.GetTempPath(), "prism-desktop-audio", $"{Guid.NewGuid():N}.wav");
                        Directory.CreateDirectory(Path.GetDirectoryName(_audioTempFile)!);
                        await File.WriteAllBytesAsync(_audioTempFile, audio.Data);
                        AudioStatus = $"{audio.Format}  ·  点击播放";
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

            // 视频：暂不支持播放
            if (string.Equals(preview.Kind, "video", StringComparison.OrdinalIgnoreCase))
            {
                PreviewEmptyText = "视频暂不支持播放，可用「导出原始」获取文件";
            }

            PreviewDetails = new ObservableCollection<DetailItem>(details);
            SelectedPathText = item.FullPath;
            HasPreview = true;
            StatusText = preview.Title;
        });
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
            Win32PlaySound.Stop();
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

            if (string.IsNullOrWhiteSpace(ExportDirectory))
            {
                StatusText = "请先选择导出目录。";
                return;
            }

            ExportResult result;
            await _gate.WaitAsync();
            try
            {
                result = await _session.ExportAsync(new ExportRequest([item.FullPath], ExportDirectory));
            }
            finally
            {
                _gate.Release();
            }

            StatusText = result.Failed == 0
                ? $"已导出 {result.Succeeded:N0} 个文件到 {ExportDirectory}"
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

            if (string.IsNullOrWhiteSpace(ExportDirectory))
            {
                StatusText = "请先选择导出目录。";
                return;
            }

            List<string> writtenPaths = [];
            foreach (PreviewExportFileDto file in export.Files)
            {
                string outputPath = Path.Combine(ExportDirectory, SanitizeFileName(file.FileName));
                await File.WriteAllBytesAsync(outputPath, file.Data);
                writtenPaths.Add(outputPath);
            }

            StatusText = $"已导出 {writtenPaths.Count:N0} 个预览文件。";
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
            if (OperatingSystem.IsAndroid() && !await EnsureAndroidExportFolderAsync())
            {
                StatusText = "请先在设置中选择导出目录。";
                return;
            }

            if (!OperatingSystem.IsAndroid() && string.IsNullOrWhiteSpace(ExportDirectory))
            {
                StatusText = "请先选择导出目录。";
                return;
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
                                string outputPath = Path.Combine(ExportDirectory, SanitizeRelativePath(relativePath));
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
                                string outputDirectory = Path.Combine(ExportDirectory, SanitizeRelativePath(relativeDirectory));
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

            StatusText = $"文件夹导出完成：{exportedAssets} 个资源 / {exportedFiles} 个文件，共 {FormatSize(totalBytes)}；跳过 {skipped}，失败 {failed}。";
            AddLog($"文件夹导出完成（{kind}）：成功 {exportedAssets}，文件 {exportedFiles}，跳过 {skipped}，失败 {failed}");
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
            // 1. 从 Pak 解出原始关联文件，并生成原始预览
            IReadOnlyDictionary<string, byte[]> rawFiles;
            AssetPreviewDto? originalPreview = null;
            await _gate.WaitAsync();
            try
            {
                rawFiles = await _session.ReadRelatedRawFilesAsync(item.FullPath);
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

            if (rawFiles.Count == 0)
            {
                throw new InvalidOperationException("该资源没有可替换的关联文件。");
            }

            // 2. 写入工作目录
            string workDir = Path.Combine(Path.GetTempPath(), "prism-desktop-patch", Guid.NewGuid().ToString("N"));
            string inputDir = Path.Combine(workDir, "input");
            Directory.CreateDirectory(inputDir);

            string baseName = Path.GetFileNameWithoutExtension(item.FullPath);
            Dictionary<string, string> originalFiles = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string pakPath, byte[] data) in rawFiles)
            {
                string localPath = Path.Combine(inputDir, Path.GetFileName(pakPath));
                await File.WriteAllBytesAsync(localPath, data);
                originalFiles[pakPath] = localPath;
            }

            // 3. 本地化（locres）或纹理
            PatchItem patch;
            bool isLocres = originalPreview is { Kind: "locres" } || originalPreview?.Locres is not null;
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
                    format: locres.Version,
                    sizeLabel: $"{locres.EntryCount:N0} 条",
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

    [RelayCommand(CanExecute = nameof(CanPickReplacement))]
    private async Task PickReplacementAsync()
    {
        PatchItem? patch = SelectedPatchItem;
        if (patch is null || TopLevel is null)
        {
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
                    patch.Status = "失败";
                    patch.Error = result.CombinedOutput;
                    StatusText = $"替换失败：{result.CombinedOutput.Split('\n').FirstOrDefault()}";
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
                await Task.Run(() => ModifiedPakPackService.Pack(new ModifiedPakRequest(
                    files.Values.OrderBy(f => f.PakPath, StringComparer.OrdinalIgnoreCase).ToArray(),
                    tempPakPath,
                    UseCompression: UseOodleCompression,
                    Compression: PakCompression.Oodle)));

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
                await Task.Run(() => ModifiedPakPackService.Pack(new ModifiedPakRequest(
                    files.Values.OrderBy(f => f.PakPath, StringComparer.OrdinalIgnoreCase).ToArray(),
                    outputPath!,
                    UseCompression: UseOodleCompression,
                    Compression: PakCompression.Oodle)));

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
        if (AskBeforeReplace)
        {
            MergeInspectionResponse inspection = await InspectMergeCoreAsync();
            MergeStatus = $"主 Pak {inspection.BaseCount:N0} 项，合并 Pak {inspection.MergeCount:N0} 项，冲突 {inspection.ConflictCount:N0} 项";
            if (inspection.ConflictCount > 0)
            {
                bool confirmed = OperatingSystem.IsAndroid()
                    ? (NativeConfirmAsync is not null
                        ? await NativeConfirmAsync("确认", $"发现 {inspection.ConflictCount} 个冲突。用合并 Pak 的文件替换？")
                        : true)
                    : await Views.ConfirmDialog.ShowAsync(
                        TopLevel as Window ?? throw new InvalidOperationException("窗口未就绪"),
                        $"发现 {inspection.ConflictCount} 个冲突。用合并 Pak 的文件替换？");
                if (!confirmed)
                {
                    MergeStatus = "已取消";
                    return;
                }
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

                MergeStatus = $"合并完成：{_mergeOutputTarget.Name}，{result.FileCount:N0} 个文件，冲突 {result.ConflictCount:N0}";
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
                MergeStatus = $"合并完成：{result.FileCount:N0} 个文件，冲突 {result.ConflictCount:N0}，替换 {result.ReplacedCount:N0}";
                StatusText = $"合并完成：{Path.GetFileName(result.OutputPakPath)}";
            }
        });
    }

    private async Task<MergeInspectionResponse> InspectMergeCoreAsync()
    {
        using var baseSession = new PakArchiveSession();
        using var mergeSession = new PakArchiveSession();
        string? aes = NullIfWhiteSpace(AesKey);
        string? usmap = NullIfWhiteSpace(UsmapPath);
        await baseSession.OpenAsync(new PakOpenOptions([PakPath], aes, usmap));
        await mergeSession.OpenAsync(new PakOpenOptions([MergePakPath], aes, usmap));

        IReadOnlySet<string> baseFiles = await baseSession.ListRawFilePathsAsync();
        IReadOnlySet<string> mergeFiles = await mergeSession.ListRawFilePathsAsync();
        string[] conflicts = mergeFiles.Where(baseFiles.Contains).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new MergeInspectionResponse(baseFiles.Count, mergeFiles.Count, conflicts.Length, conflicts.Take(500).ToArray());
    }

    private async Task<MergeBuildResponse> BuildMergeCoreAsync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "PrismDesktopMerge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var baseSession = new PakArchiveSession();
            using var mergeSession = new PakArchiveSession();
            string? aes = NullIfWhiteSpace(AesKey);
            string? usmap = NullIfWhiteSpace(UsmapPath);
            await baseSession.OpenAsync(new PakOpenOptions([PakPath], aes, usmap));
            await mergeSession.OpenAsync(new PakOpenOptions([MergePakPath], aes, usmap));

            IReadOnlyList<PakRawFileCopy> baseFiles = await baseSession.CopyAllRawFilesAsync(Path.Combine(tempRoot, "base"));
            IReadOnlyList<PakRawFileCopy> mergeFiles = await mergeSession.CopyAllRawFilesAsync(Path.Combine(tempRoot, "merge"));

            Dictionary<string, ModifiedPakFile> mapped = baseFiles.ToDictionary(
                x => x.PakPath,
                x => new ModifiedPakFile(x.DiskPath, x.PakPath),
                StringComparer.OrdinalIgnoreCase);

            int conflicts = 0;
            int replaced = 0;
            foreach (PakRawFileCopy file in mergeFiles)
            {
                bool hasConflict = mapped.ContainsKey(file.PakPath);
                if (hasConflict)
                {
                    conflicts++;
                    if (!AskBeforeReplace)
                    {
                        continue;
                    }

                    replaced++;
                }

                mapped[file.PakPath] = new ModifiedPakFile(file.DiskPath, file.PakPath);
            }

            string packTarget = OperatingSystem.IsAndroid()
                ? Path.Combine(Path.GetTempPath(), $"merged_{Guid.NewGuid():N}.pak")
                : MergeOutputPath;

            await Task.Run(() => ModifiedPakPackService.Pack(new ModifiedPakRequest(
                mapped.Values.OrderBy(x => x.PakPath, StringComparer.OrdinalIgnoreCase).ToArray(),
                packTarget,
                UseCompression: UseOodleCompression,
                Compression: PakCompression.Oodle)));

            return new MergeBuildResponse(packTarget, mapped.Count, conflicts, replaced);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    // ============ 设置（应用内视图，兼容手机） ============

    [ObservableProperty]
    public partial string VersionText { get; set; }

    [ObservableProperty]
    public partial string CliStatus { get; set; }

    [ObservableProperty]
    public partial string AstcencStatus { get; set; }

    [ObservableProperty]
    public partial string TexconvStatus { get; set; }

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
        CliStatus = runner is not null ? "已找到 UAssetCLI" : "未找到 UAssetCLI（替换功能不可用）";
        AstcencStatus = runner?.HasAstcenc == true ? "已找到 astcenc" : "未找到 astcenc";
        TexconvStatus = runner?.HasTexconv == true ? "已找到 texconv" : "未找到 texconv";
        TempDirectory = Path.GetTempPath();
        RefreshLog();
        Services.AppLog.Add($"应用启动 v{VersionText}");
    }

    /// <summary>刷新日志预览文本（设置页展示）。</summary>
    private void RefreshLog() => LogText = Services.AppLog.FullText;

    /// <summary>记录一条日志并刷新预览。</summary>
    private void AddLog(string line)
    {
        Services.AppLog.Add(line);
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

            RefreshLog();
            byte[] content = System.Text.Encoding.UTF8.GetBytes(LogText);
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

                    AddLog($"日志已导出：{file.Name}");
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
                AddLog($"日志已导出：{path}");
            }

            StatusText = $"日志已导出（{Services.AppLog.Count:N0} 行）。";
        }
        finally
        {
            ResumeThumbnailsAfterUserAction();
        }
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

    [RelayCommand]
    private async Task BrowseUsmapAsync()
    {
        string? path = await PickFileAsync("选择 .usmap 映射文件", ["*.usmap"], UsmapPath);
        if (path is not null)
        {
            UsmapPath = path;
            StatusText = $"已选择 Usmap：{Path.GetFileName(path)}";
        }
    }

    [RelayCommand]
    private async Task BrowseMergePakAsync()
    {
        string? path = await PickFileAsync("选择合并 Pak 文件", ["*.pak"], MergePakPath);
        if (path is not null)
        {
            MergePakPath = path;
            StatusText = $"已选择合并 Pak：{Path.GetFileName(path)}";
        }
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
        foreach (string? path in new[] { PakPath, UsmapPath, MergePakPath })
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
        PreviewImage = null;
        PreviewTitle = string.Empty;
        PreviewText = string.Empty;
        PreviewDetails = [];
        SelectedPathText = string.Empty;
        HasPreview = false;
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
            AddLog($"错误：{ex.Message}");
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

/// <summary>Merge 检查结果。</summary>
public sealed record MergeInspectionResponse(int BaseCount, int MergeCount, int ConflictCount, IReadOnlyList<string> Conflicts);

/// <summary>Merge 构建结果。</summary>
public sealed record MergeBuildResponse(string OutputPakPath, int FileCount, int ConflictCount, int ReplacedCount);
