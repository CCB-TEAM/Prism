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
    private bool _loaded;

    public MainViewModel()
    {
        _settings = AppSettingsStore.Load();
        ShowThumbnails = _settings.ShowThumbnails;
        UseOodleCompression = _settings.UseOodleCompression;
        AskBeforeReplace = _settings.AskBeforeReplace;
        ExportDirectory = _settings.ExportDirectory;
        PakPath = _settings.PakPath;
        UsmapPath = _settings.UsmapPath;
        MergePakPath = _settings.MergePakPath;
        MergeOutputPath = _settings.MergeOutputPath;
        AesKey = _settings.AesKey;
        InitializeSettings();
        _loaded = true;
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

    public bool IsNotLandscape => !IsLandscape;

    partial void OnIsLandscapeChanged(bool value) => OnPropertyChanged(nameof(IsNotLandscape));

    partial void OnWindowWidthChanged(double value)
    {
        bool landscape = value >= 980;
        if (landscape != IsLandscape)
        {
            IsLandscape = landscape;
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

    public bool CanExportRaw => SelectedItem is { IsDirectory: false } && IsPakOpen;

    public bool CanExportPreview => SelectedItem is { IsDirectory: false } && HasPreview;

    public bool CanAddToPatch => SelectedItem is { IsDirectory: false } && IsPakOpen;

    partial void OnCurrentFolderChanged(string value) => UpCommand.NotifyCanExecuteChanged();

    partial void OnSelectedItemChanged(EntryItem? value)
    {
        ExportRawCommand.NotifyCanExecuteChanged();
        ExportPreviewCommand.NotifyCanExecuteChanged();
        AddSelectedToPatchCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasPreviewChanged(bool value) => ExportPreviewCommand.NotifyCanExecuteChanged();

    // ============ 预览 ============

    [ObservableProperty]
    public partial Bitmap? PreviewImage { get; set; }

    [ObservableProperty]
    public partial string PreviewTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PreviewText { get; set; } = string.Empty;

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
    public partial string AudioStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AudioButtonText { get; set; } = "播放";

    partial void OnIsAudioPlayingChanged(bool value) => AudioButtonText = value ? "停止" : "播放";

    [ObservableProperty]
    public partial string PreviewEmptyText { get; set; } = "此文件无预览";

    private string? _audioTempFile;

    [RelayCommand]
    private void ToggleAudioPlayback()
    {
        if (!HasAudioPreview || _audioTempFile is null)
        {
            return;
        }

        if (IsAudioPlaying)
        {
            Win32PlaySound.Stop();
            IsAudioPlaying = false;
            return;
        }

        Win32PlaySound.PlayFile(_audioTempFile);
        IsAudioPlaying = true;
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
    }

    // ============ 持久化 ============

    /// <summary>窗口高度（关闭时保存）。</summary>
    public double WindowHeight { get; set; } = 820;

    partial void OnUseOodleCompressionChanged(bool value) => SaveSettings();

    partial void OnAskBeforeReplaceChanged(bool value) => SaveSettings();

    partial void OnExportDirectoryChanged(string value) => SaveSettings();

    partial void OnPakPathChanged(string value) => SaveSettings();

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
        _settings.UseOodleCompression = UseOodleCompression;
        _settings.AskBeforeReplace = AskBeforeReplace;
        _settings.ExportDirectory = ExportDirectory;
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
            CurrentTabIndex = 1;
            await NavigateToAsync(string.Empty);
        });
    }

    // ============ 浏览导航 ============

    [RelayCommand(CanExecute = nameof(CanUp))]
    private async Task UpAsync()
    {
        await NavigateToAsync(ParentOf(CurrentFolder));
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

        if (item.IsDirectory)
        {
            await NavigateToAsync(item.FullPath);
        }
        else
        {
            await PreviewAsync(item);
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
            if (preview.Data is { Length: > 0 })
            {
                using MemoryStream ms = new(preview.Data);
                PreviewImage = Bitmap.DecodeToWidth(ms, 1280);
            }

            List<DetailItem> details = preview.Details.Select(d => new DetailItem(d.Label, d.Value)).ToList();
            PreviewEmptyText = "此文件无预览";

            // 模型：追加几何信息
            if (preview.Model is { } model)
            {
                details.Add(new DetailItem("网格类型", model.MeshType));
                details.Add(new DetailItem("顶点数", $"{model.VertexCount:N0}"));
                details.Add(new DetailItem("三角形", $"{model.TriangleCount:N0}"));
                details.Add(new DetailItem("分段数", $"{model.Sections.Count:N0}"));
            }

            // 音频：加载可播放的 WAV
            StopAudioPreview();
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
        Win32PlaySound.Stop();
        IsAudioPlaying = false;
        HasAudioPreview = false;
        if (_audioTempFile is not null)
        {
            try
            {
                File.Delete(_audioTempFile);
            }
            catch
            {
            }

            _audioTempFile = null;
        }
    }

    // ============ 导出 ============

    [RelayCommand(CanExecute = nameof(CanExportRaw))]
    private async Task ExportRawAsync()
    {
        EntryItem? item = SelectedItem;
        if (item is null || string.IsNullOrWhiteSpace(ExportDirectory))
        {
            StatusText = "请先选择导出目录。";
            return;
        }

        await RunBusyAsync(async () =>
        {
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
        if (item is null || string.IsNullOrWhiteSpace(ExportDirectory))
        {
            StatusText = "请先选择导出目录。";
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

            List<string> written = [];
            foreach (PreviewExportFileDto file in export.Files)
            {
                string outputPath = Path.Combine(ExportDirectory, SanitizeFileName(file.FileName));
                await File.WriteAllBytesAsync(outputPath, file.Data);
                written.Add(outputPath);
            }

            StatusText = $"已导出 {written.Count:N0} 个预览文件。";
        });
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
            string? uassetPakPath = rawFiles.Keys.FirstOrDefault(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase));
            if (uassetPakPath is null)
            {
                throw new InvalidOperationException("该资源不是 .uasset 资产。");
            }

            string inputUassetPath = Path.Combine(inputDir, baseName + ".uasset");
            Dictionary<string, string> originalFiles = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string pakPath, byte[] data) in rawFiles)
            {
                string localPath = Path.Combine(inputDir, Path.GetFileName(pakPath));
                await File.WriteAllBytesAsync(localPath, data);
                originalFiles[pakPath] = localPath;
            }

            // 3. 检查是否为纹理
            TextureInspectionResult inspect = await new TextureReplacementService().InspectAsync(
                inputUassetPath,
                EngineVersion.VER_UE5_6,
                NullIfWhiteSpace(UsmapPath));

            // 4. 建 patch 项
            var patch = new PatchItem(
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
            foreach ((string pakPath, string localPath) in originalFiles)
            {
                patch.OriginalFiles[pakPath] = localPath;
            }

            PatchItems.Add(patch);
            SelectedPatchItem = patch;
            StatusText = $"已加入替换：{baseName}（{inspect.Format}）";
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

        // 复用 PickFileAsync：Android 上会把 SAF 文件复制到私有目录
        string? imagePath = await PickFileAsync("选择替换图片", ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tga", "*.webp"]);
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
                // Android SAF：先打包到临时文件，再流式写入用户选择的位置
                string tempPakPath = Path.Combine(Path.GetTempPath(), $"patch_{Guid.NewGuid():N}.pak");
                await Task.Run(() => ModifiedPakPackService.Pack(new ModifiedPakRequest(
                    files.Values.OrderBy(f => f.PakPath, StringComparer.OrdinalIgnoreCase).ToArray(),
                    tempPakPath,
                    UseCompression: UseOodleCompression,
                    Compression: PakCompression.Oodle)));

                await using Stream src = File.OpenRead(tempPakPath);
                await using Stream dst = await output.OpenWriteAsync();
                await src.CopyToAsync(dst);
                try
                {
                    File.Delete(tempPakPath);
                }
                catch
                {
                }

                StatusText = $"补丁 Pak 已保存：{output.Name}（{files.Count} 个文件）";
            }
            else
            {
                await Task.Run(() => ModifiedPakPackService.Pack(new ModifiedPakRequest(
                    files.Values.OrderBy(f => f.PakPath, StringComparer.OrdinalIgnoreCase).ToArray(),
                    outputPath!,
                    UseCompression: UseOodleCompression,
                    Compression: PakCompression.Oodle)));

                StatusText = $"补丁 Pak 已构建：{Path.GetFileName(outputPath)}（{files.Count} 个文件）";
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
                bool confirmed = await Views.ConfirmDialog.ShowAsync(
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
                await using Stream src = File.OpenRead(result.OutputPakPath);
                await using Stream dst = await _mergeOutputTarget.OpenWriteAsync();
                await src.CopyToAsync(dst);
                try
                {
                    File.Delete(result.OutputPakPath);
                }
                catch
                {
                }

                MergeStatus = $"合并完成：{_mergeOutputTarget.Name}，{result.FileCount:N0} 个文件，冲突 {result.ConflictCount:N0}";
                StatusText = $"合并完成：{_mergeOutputTarget.Name}";
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

    // ============ 列表缩略图（默认关闭，设置中开启） ============

    [ObservableProperty]
    public partial bool ShowThumbnails { get; set; }

    private CancellationTokenSource? _thumbCts;
    private readonly SemaphoreSlim _thumbGate = new(4, 4); // 并发解码上限

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

    /// <summary>为当前列表中的图片类条目生成缩略图（后台解码，UI 线程建图）。</summary>
    private void StartThumbnails()
    {
        if (!ShowThumbnails || !IsPakOpen)
        {
            return;
        }

        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _thumbCts = new CancellationTokenSource();
        CancellationToken ct = _thumbCts.Token;
        List<EntryItem> candidates = Entries.Where(e => e.IsImageKind).ToList();
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
    }

    private IStorageFile? _mergeOutputTarget;

    // ============ 文件选择 ============

    [RelayCommand]
    private async Task BrowsePakAsync()
    {
        string? path = await PickFileAsync("选择 Pak 文件", ["*.pak"]);
        if (path is not null)
        {
            PakPath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseUsmapAsync()
    {
        string? path = await PickFileAsync("选择 .usmap 映射文件", ["*.usmap"]);
        if (path is not null)
        {
            UsmapPath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseMergePakAsync()
    {
        string? path = await PickFileAsync("选择合并 Pak 文件", ["*.pak"]);
        if (path is not null)
        {
            MergePakPath = path;
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
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } dir)
        {
            ExportDirectory = dir;
        }
    }

    private async Task<string?> PickFileAsync(string title, string[] patterns)
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
            FileTypeFilter = [new FilePickerFileType("文件") { Patterns = patterns }],
        });
        if (files.Count == 0)
        {
            return null;
        }

        IStorageFile file = files[0];
        if (OperatingSystem.IsAndroid())
        {
            // SAF 返回 content:// URI，无本地路径：复制到应用私有目录
            string destDir = Path.Combine(PrivateDataDir(), "picked");
            Directory.CreateDirectory(destDir);
            string destPath = Path.Combine(destDir, SanitizeFileName(file.Name));
            await using Stream src = await file.OpenReadAsync();
            await using FileStream dst = File.Create(destPath);
            await src.CopyToAsync(dst);
            return destPath;
        }

        return file.TryGetLocalPath();
    }

    // ============ 工具 ============

    private void ClearPreview()
    {
        StopAudioPreview();
        PreviewImage = null;
        PreviewTitle = string.Empty;
        PreviewText = string.Empty;
        PreviewDetails = [];
        SelectedPathText = string.Empty;
        HasPreview = false;
        PreviewEmptyText = "此文件无预览";
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusText = $"错误：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
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
