using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PakTool.Core;
using Prism.Desktop.Models;
using Prism.Desktop.Services;

namespace Prism.Desktop.ViewModels;

/// <summary>
/// 模组工程（<c>.prismproj</c>）：保存、打开、最近工程、脏标记。
///
/// 为什么值得单独一个文件：它把"干了半天活、关掉应用就没了"这件事接住。
/// 保存下来的是**配方**（改哪个资产 + 用哪个素材 + 什么参数），不是快照 ——
/// 所以游戏更新之后打开旧工程仍然能出包，编码与解包在构建时重做。
///
/// 三条不变量，改动代码时别破坏：
///   · AES 密钥永远不写进工程文件（工程是拿来分享的）；
///   · 工作目录与解包出来的字节也不写（那是缓存，每次重新生成）；
///   · 缺素材、缺资产只标在对应行上，整个工程照样打得开、构得动。
/// </summary>
public partial class MainViewModel
{
    private readonly ObservableCollection<RecentProjectItem> _recentProjects = [];
    private readonly ObservableCollection<RecentProjectItem> _visibleRecentProjects = [];
    private ModProject? _project;
    private IStorageFile? _projectStorageFile;
    private string _projectLocalPath = string.Empty;
    private bool _suppressProjectDirty;

    /// <summary>工程文件的位置：桌面是本地路径，Android 是 SAF 的 content URI。</summary>
    [ObservableProperty]
    public partial string ProjectPath { get; set; } = string.Empty;

    /// <summary>工程名（保存时取文件名，可改）。</summary>
    [ObservableProperty]
    public partial string ProjectName { get; set; } = string.Empty;

    /// <summary>有未保存到工程文件的改动。</summary>
    [ObservableProperty]
    public partial bool IsProjectDirty { get; set; }

    /// <summary>保存时把素材复制进 <c>&lt;工程名&gt;.assets/</c>（Android 上强制开启）。</summary>
    [ObservableProperty]
    public partial bool CollectProjectAssets { get; set; } = true;

    partial void OnProjectPathChanged(string value) => NotifyProjectState();

    partial void OnProjectNameChanged(string value) => NotifyProjectState();

    partial void OnIsProjectDirtyChanged(bool value) => NotifyProjectState();

    partial void OnCollectProjectAssetsChanged(bool value)
    {
        _settings.CollectProjectAssets = value;
        SaveSettings();
    }

    /// <summary>最近打开的工程（最新的在前，设置里最多留 8 条）。</summary>
    public ObservableCollection<RecentProjectItem> RecentProjects => _recentProjects;

    /// <summary>
    /// 欢迎页实际显示的那几项。
    ///
    /// 手机上一屏放不下八张卡：这块在入口列表上面，摆太多就把"从哪开始"挤出屏幕。
    /// 所以按布局截断（手机 3 张、宽窗全部），内存里仍留全量。
    /// </summary>
    public ObservableCollection<RecentProjectItem> VisibleRecentProjects => _visibleRecentProjects;

    /// <summary>手机上最多显示几张最近工程卡。</summary>
    public int MaxRecentProjectCards => IsCompact ? 3 : _recentProjects.Count;

    private void RebuildVisibleRecentProjects()
    {
        _visibleRecentProjects.Clear();
        foreach (RecentProjectItem item in _recentProjects.Take(MaxRecentProjectCards))
        {
            _visibleRecentProjects.Add(item);
        }
    }

    public bool HasRecentProjects => _recentProjects.Count > 0;

    /// <summary>
    /// 最近工程卡片宽度：宽窗固定 220 一行几张都行；手机按可用宽度铺满，
    /// 免得 430 的屏上左边一列 220、右边空一半。
    /// </summary>
    public double RecentProjectCardWidth
    {
        get
        {
            if (!IsCompact)
            {
                return 220d;
            }

            double available = WindowWidth - SidebarWidth - 48d;
            return Math.Max(200d, Math.Floor(available));
        }
    }

    public bool HasProject => !string.IsNullOrWhiteSpace(ProjectPath);

    public string ProjectDisplayName => string.IsNullOrWhiteSpace(ProjectName) ? "未命名工程" : ProjectName;

    /// <summary>页头上的工程标识：有工程显示工程名，只有改动时显示「未保存的改动」。</summary>
    public string ProjectChipText
    {
        get
        {
            if (HasProject)
            {
                return ProjectDisplayName;
            }

            // 什么都没有时也留一个入口：工程这件事不该等"有改动"才出现，
            // 点开就能「打开工程」或「保存当前工程」。
            return PatchItems.Count > 0 ? "未保存的改动" : "工程";
        }
    }

    /// <summary>
    /// 页头的工程入口始终可见。
    ///
    /// 之前它只在"有改动或已打开工程"时出现，于是想先建个工程、或者想打开一个工程，
    /// 都得先随便改动点什么才行 —— 入口不该被状态挡住。
    /// 保留这个属性是为了视图那边只有一个开关可绑，将来真要按状态隐藏也只改这里。
    /// </summary>
    public bool ShowProjectChip => true;

    public string ProjectChipTooltip
    {
        get
        {
            if (!HasProject)
            {
                return PatchItems.Count > 0
                    ? $"当前 {PatchItems.Count} 项替换还没保存成工程文件。点这里「保存工程」，下次打开就能接着改。"
                    : "还没有打开工程。点这里可以打开一个工程，或者把当前状态存成新的工程文件。";
            }

            string tip = ProjectPath;
            if (_project?.LastBuild is { } build)
            {
                tip += $"{Environment.NewLine}上次构建：{build.Files} 个文件 · {EntryItem.FormatSize(build.Bytes)}";
            }

            return IsProjectDirty ? tip + $"{Environment.NewLine}（有未保存的改动）" : tip;
        }
    }

    /// <summary>关窗口时判断要不要拦一下。</summary>
    public bool HasUnsavedProjectChanges => IsProjectDirty && (PatchItems.Count > 0 || HasProject);

    private void NotifyProjectState()
    {
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(ProjectDisplayName));
        OnPropertyChanged(nameof(ProjectChipText));
        OnPropertyChanged(nameof(ShowProjectChip));
        OnPropertyChanged(nameof(ProjectChipTooltip));
        OnPropertyChanged(nameof(HasUnsavedProjectChanges));
    }

    /// <summary>
    /// 标记"有未保存的改动"。加/删替换项、换素材、改本地化译文、改打包选项都会走这里。
    /// 加载工程期间被 <see cref="_suppressProjectDirty"/> 关掉，否则打开工程立刻显示"未保存"。
    /// </summary>
    private void MarkProjectDirty()
    {
        OnPropertyChanged(nameof(ProjectChipText));
        OnPropertyChanged(nameof(ShowProjectChip));
        if (_suppressProjectDirty || IsProjectDirty)
        {
            return;
        }

        IsProjectDirty = true;
    }

    // ============ 保存 ============

    [RelayCommand]
    private async Task SaveProjectAsync()
    {
        if (!string.IsNullOrWhiteSpace(_projectLocalPath))
        {
            await RunBusyAsync(() => SaveProjectCoreAsync(_projectLocalPath, null));
            return;
        }

        if (_projectStorageFile is not null)
        {
            await RunBusyAsync(() => SaveProjectCoreAsync(null, _projectStorageFile));
            return;
        }

        await SaveProjectAsAsync();
    }

    [RelayCommand]
    private async Task SaveProjectAsAsync()
    {
        TopLevel? top = TopLevel;
        if (top is null)
        {
            StatusText = "窗口还没就绪，先等一下再保存。";
            return;
        }

        IStorageFile? file;
        try
        {
            file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存模组工程",
                // 扩展名已经写在建议文件名里；再给 DefaultExtension 会在手机上变成
                // 「未命名工程.prismproj.prismproj」（DocumentsUI 会照它再补一次）。
                SuggestedFileName = SuggestedProjectFileName,
                FileTypeChoices = [new FilePickerFileType("Prism 模组工程") { Patterns = ["*.prismproj"] }],
            });
        }
        catch (Exception ex)
        {
            StatusText = $"打不开保存对话框：{ex.Message}";
            return;
        }

        if (file is null)
        {
            return;
        }

        await RunBusyAsync(() => SaveProjectCoreAsync(file.TryGetLocalPath(), file));
    }

    /// <summary>供无头测试/命令行用：把当前改动存到这个路径。</summary>
    public Task SaveProjectToAsync(string path) => RunBusyAsync(() => SaveProjectCoreAsync(path, null));

    private async Task SaveProjectCoreAsync(string? localPath, IStorageFile? storage)
    {
        // Android：一律走 SAF 流，绝不用「本地路径」直接写。
        //
        // 坑：对 DocumentsUI 返回的 content:// URI，TryGetLocalPath() 会翻译成
        // /storage/emulated/0/... 这样的真实路径 —— 那是分区存储下别人的地界，
        // 直接 File.WriteAllText 会 SecurityException。而系统在返回 URI 前已经把空文件建好了，
        // 于是用户看到的就是「工程是 0 字节」。
        if (OperatingSystem.IsAndroid())
        {
            localPath = null;
        }

        // 没有替换项也允许保存：工程还记着 Pak / 映射 / 输出路径，
        // 先存个骨架、下次打开接着加，比"必须先改点什么"合理。
        string directory = ResolveProjectDirectory(localPath);
        string stem = ProjectFileStem(localPath, storage);
        ModProject project = await BuildProjectSnapshotAsync(directory, stem);

        bool fellBackToPrivateCopy = false;
        if (storage is not null)
        {
            // 先在自己目录里落一份，再去写用户选的位置。
            // SAF 的写入偶发失败（提供者拒绝、空间不足、URI 失效），
            // 有这一份至少工作不丢，而且它本身是个本地路径，能直接继续用。
            string backup = Path.Combine(DefaultProjectDirectory(), stem + ProjectStore.Extension);
            await Task.Run(() => ProjectStore.Save(backup, project));

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(ProjectStore.Serialize(project));
                await WriteToStorageFileAsync(storage, bytes);
            }
            catch (Exception ex)
            {
                AppLog.Warn("工程", $"写到你选的位置失败，暂存到应用私有目录：{ex.Message}");
                fellBackToPrivateCopy = true;
                localPath = backup;
                StatusText = $"保存到所选位置失败（{ex.Message}），已先存在应用私有目录，工程不会丢。";
            }
        }
        else if (!string.IsNullOrWhiteSpace(localPath))
        {
            string target = localPath;
            await Task.Run(() => ProjectStore.Save(target, project));
        }
        else
        {
            throw new InvalidOperationException("没有可写入的位置。");
        }

        _project = project;
        _projectLocalPath = localPath ?? string.Empty;
        _projectStorageFile = storage;
        ProjectPath = string.IsNullOrWhiteSpace(localPath) ? ProjectUriOf(storage) : localPath;
        ProjectName = project.Name;
        IsProjectDirty = false;
        RememberRecentProject();
        NotifyProjectState();

        string assetNote = Directory.Exists(Path.Combine(directory, stem + ".assets"))
            ? $"，素材在 {stem}.assets"
            : string.Empty;
        if (!fellBackToPrivateCopy)
        {
            StatusText = project.Tasks.Count == 0
                ? $"工程已保存：{ProjectDisplayName}（还没有替换项，Pak 与配置已记下）"
                : $"工程已保存：{ProjectDisplayName}（{project.Tasks.Count} 项{assetNote}）";
        }

        AddLog($"保存工程：{ProjectPath}（{project.Tasks.Count} 项）", "工程");
    }

    /// <summary>
    /// 往 SAF / 文件提供者的目标写整份内容。
    ///
    /// 这里踩过一个坑：Android 的目标流（ParcelFileDescriptor 包出来的 OutputStream）**不支持 SetLength**。
    /// 原来的写法是「SetLength(0) 截断 → 写」，结果截断生效、SetLength 抛异常，
    /// 于是给用户留了一个 0 字节的工程文件（屏幕上还只看到一句错误，容易以为是导出成功）。
    /// 现在：先写、再尽力截断、最后回读长度核对；长度不对就明确报错，绝不留空文件。
    /// </summary>
    private static async Task WriteToStorageFileAsync(IStorageFile storage, byte[] bytes)
    {
        await using (Stream stream = await storage.OpenWriteAsync())
        {
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
            try
            {
                // 打开时多数提供者已经截断过；没截断的（旧内容更长）在这里收尾。
                if (stream.CanSeek)
                {
                    stream.SetLength(bytes.Length);
                }
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
            {
                AppLog.Warn("工程", $"目标流不支持截断，按实际写入长度为准：{ex.Message}");
            }
        }

        try
        {
            await using Stream verify = await storage.OpenReadAsync();
            if (verify.CanSeek && verify.Length != bytes.Length)
            {
                throw new ModProjectException(
                    $"工程写入不完整：期望 {bytes.Length:N0} 字节，实际 {verify.Length:N0} 字节。确认目标目录还有空间，或换一个位置再存一次。");
            }
        }
        catch (ModProjectException)
        {
            throw;
        }
        catch
        {
            // 有的提供者写完不让立刻读回来，校验失败不该当成保存失败。
        }
    }

    /// <summary>把当前状态攒成工程对象；这一步顺手把素材收进 assets 目录。</summary>
    private async Task<ModProject> BuildProjectSnapshotAsync(string directory, string stem)
    {
        var project = new ModProject
        {
            Name = string.IsNullOrWhiteSpace(ProjectName) ? stem : ProjectName,
            Prism = VersionText,
            Oodle = UseOodleCompression,
            Paks = string.IsNullOrWhiteSpace(PakPath) ? [] : [ProjectStore.ToStored(directory, PakPath)],
            Usmap = ProjectStore.ToStored(directory, UsmapPath),
            LastBuild = _project?.LastBuild,
        };

        // 素材收集：默认开（工程才能搬走/发给别人）。Android 上强制开启 ——
        // 选择器给的副本在私有缓存里，不收集的话重启后就断。
        bool collect = CollectProjectAssets || OperatingSystem.IsAndroid();
        Dictionary<string, string> copied = new(StringComparer.OrdinalIgnoreCase);
        string assetsDirectory = Path.Combine(directory, stem + ".assets");
        string[] wanted = PatchItems
            .Select(p => p.ReplacementSourcePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (collect && wanted.Length > 0)
        {
            await Task.Run(() =>
            {
                foreach (string source in wanted)
                {
                    try
                    {
                        copied[source] = ProjectStore.CollectAsset(assetsDirectory, source);
                    }
                    catch (Exception ex)
                    {
                        AddLog($"素材没能收进工程：{source}（{ex.Message}）", "工程");
                    }
                }
            });
        }

        foreach (PatchItem patch in PatchItems)
        {
            var task = new ModProjectTask
            {
                Type = patch.Kind,
                Asset = patch.SourcePath,
                Format = patch.Format,
                SizeLabel = patch.SizeLabel,
                Detail = string.IsNullOrWhiteSpace(patch.Detail) ? null : patch.Detail,
            };

            string? sourcePath = patch.ReplacementSourcePath;
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                string storedPath = copied.TryGetValue(sourcePath, out string? collected) ? collected : sourcePath;
                task.Source = ProjectStore.ToStored(directory, storedPath);
            }

            if (patch.IsAudio)
            {
                task.Quality = patch.AudioQuality;
            }

            if (patch.IsLocres)
            {
                List<ModProjectLocresEdit> edits = patch.LocresEntries
                    .Where(entry => entry.IsEdited)
                    .Select(entry => new ModProjectLocresEdit
                    {
                        Ns = entry.Namespace ?? string.Empty,
                        Key = entry.Key,
                        Text = entry.Text,
                    })
                    .ToList();
                task.Edits = edits.Count > 0 ? edits : null;
            }

            project.Tasks.Add(task);
        }

        return project;
    }

    // ============ 打开 ============

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        TopLevel? top = TopLevel;
        if (top is null)
        {
            StatusText = "窗口还没就绪。";
            return;
        }

        if (!await ConfirmDiscardChangesAsync("打开另一个工程"))
        {
            return;
        }

        IReadOnlyList<IStorageFile> files;
        try
        {
            files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "打开模组工程",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Prism 模组工程") { Patterns = ["*.prismproj"] }],
            });
        }
        catch (Exception ex)
        {
            StatusText = $"打不开选择对话框：{ex.Message}";
            return;
        }

        if (files.Count == 0)
        {
            return;
        }

        IStorageFile file = files[0];
        // Android：URI 对应的「本地路径」只是翻译出来的样子，读不了真东西（分区存储），
        // 一律走 SAF 流；桌面端才有真正的本地路径。
        string? local = OperatingSystem.IsAndroid() ? null : file.TryGetLocalPath();
        await RunBusyAsync(() => LoadProjectCoreAsync(local, file));
    }

    /// <summary>供无头测试/命令行用：打开这个路径下的工程。</summary>
    public async Task OpenProjectFromAsync(string path)
    {
        if (!await ConfirmDiscardChangesAsync("打开别的工程"))
        {
            return;
        }

        await RunBusyAsync(() => LoadProjectCoreAsync(path, null));
    }

    [RelayCommand]
    private async Task OpenRecentProjectAsync(RecentProjectItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!await ConfirmDiscardChangesAsync("打开最近工程"))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            try
            {
                await LoadProjectCoreAsync(item.Path, null);
            }
            catch (ModProjectException ex)
            {
                StatusText = ex.Message;
                ForgetRecentProject(item.Path);
            }
        });
    }

    private async Task LoadProjectCoreAsync(string? localPath, IStorageFile? storage)
    {
        if (OperatingSystem.IsAndroid())
        {
            localPath = null;
        }

        ModProject project;
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            project = ProjectStore.Load(localPath);
        }
        else if (storage is not null)
        {
            await using Stream stream = await storage.OpenReadAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            project = ProjectStore.Deserialize(await reader.ReadToEndAsync());
        }
        else
        {
            throw new InvalidOperationException("读不到这个工程文件。");
        }

        await ApplyProjectAsync(project, localPath, storage);
    }

    private async Task ApplyProjectAsync(ModProject project, string? localPath, IStorageFile? storage)
    {
        _suppressProjectDirty = true;
        try
        {
            ClearPatchItems();

            _project = project;
            _projectLocalPath = localPath ?? string.Empty;
            _projectStorageFile = storage;
            ProjectPath = string.IsNullOrWhiteSpace(localPath) ? ProjectUriOf(storage) : localPath;
            ProjectName = string.IsNullOrWhiteSpace(project.Name) ? ProjectFileStem(localPath, storage) : project.Name;
            IsProjectDirty = false;

            string directory = ResolveProjectDirectory(localPath);
            string pakPath = project.Paks.Count > 0 ? ProjectStore.ToAbsolute(directory, project.Paks[0]) : string.Empty;
            // 工程里没记 Pak / 映射时保持当前配置不动 —— 打开一个空工程不该把
            // 用户刚配好的 Pak 路径抹掉。
            if (!string.IsNullOrWhiteSpace(pakPath))
            {
                PakPath = pakPath;
            }

            string projectUsmap = ProjectStore.ToAbsolute(directory, project.Usmap);
            if (!string.IsNullOrWhiteSpace(projectUsmap))
            {
                UsmapPath = projectUsmap;
            }

            UseOodleCompression = project.Oodle;

            string pakNote;
            if (string.IsNullOrWhiteSpace(pakPath))
            {
                pakNote = "工程里没记 Pak，保留当前配置。";
            }
            else if (!File.Exists(pakPath))
            {
                pakNote = $"工程用的 Pak 不在了（{pakPath}），先把 Pak 路径改对再还原任务。";
            }
            else
            {
                PakOpenResult opened = await OpenPakCoreAsync();
                pakNote = $"已打开 {Path.GetFileName(pakPath)}：{opened.FileCount:N0} 个文件。";
                CurrentTabIndex = 1;
                await NavigateToAsync(string.Empty);
            }

            int restored = 0;
            int problems = 0;
            foreach (ModProjectTask task in project.Tasks)
            {
                bool ok = await RestoreProjectTaskAsync(task, directory);
                if (ok)
                {
                    restored++;
                }
                else
                {
                    problems++;
                }
            }

            IsProjectDirty = false;
            RememberRecentProject();
            NotifyPatchItemsState();
            NotifyProjectState();

            StatusText = problems == 0
                ? $"已打开工程「{ProjectDisplayName}」：{restored} 项还原完成。{pakNote}"
                : $"已打开工程「{ProjectDisplayName}」：{restored} 项还原，{problems} 项需要注意（原因写在对应行上）。{pakNote}";
            AddLog($"打开工程：{ProjectPath}（{restored} 项还原，{problems} 项异常）", "工程");
        }
        finally
        {
            _suppressProjectDirty = false;
        }
    }

    /// <summary>把工程里的一条任务重新做出来：解包 → 重新应用素材/译文。返回 false 表示这一项有问题。</summary>
    private async Task<bool> RestoreProjectTaskAsync(ModProjectTask task, string directory)
    {
        PatchItem patch;
        try
        {
            patch = await CreatePatchItemAsync(task.Asset);
        }
        catch (Exception ex)
        {
            patch = CreatePlaceholderPatch(task, $"这个资产在 Pak 里找不到了（游戏可能更新过）：{ex.Message}");
            PatchItems.Add(patch);
            SelectedPatchItem = patch;
            return false;
        }

        if (task.Quality is int quality)
        {
            patch.AudioQuality = quality;
        }

        if (patch.IsLocres)
        {
            int applied = ApplyLocresEditsFromProject(patch, task);
            if (applied > 0)
            {
                await ApplyLocresEditsAsync(patch);
            }
            else
            {
                patch.Status = "失败";
                patch.Error = $"工程里的 {task.Edits?.Count ?? 0} 条译文在当前 .locres 里一条也没对上（游戏可能改过词条）。";
            }
        }
        else
        {
            string source = ProjectStore.ToAbsolute(directory, task.Source);
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                patch.Status = "失败";
                patch.Error = $"素材文件不在了：{(string.IsNullOrWhiteSpace(source) ? "工程里没记素材" : source)}。" +
                              $"点「{patch.PickActionText}」重新指定。";
            }
            else
            {
                try
                {
                    if (patch.IsAudio)
                    {
                        await ApplyAudioReplacementAsync(patch, source);
                    }
                    else if (patch.IsMedia)
                    {
                        await ApplyMediaReplacementAsync(patch, source);
                    }
                    else
                    {
                        await ApplyTextureReplacementAsync(patch, source);
                    }
                }
                catch (Exception ex)
                {
                    patch.Status = "失败";
                    patch.Error = $"重新应用素材失败：{ex.Message}";
                }
            }
        }

        PatchItems.Add(patch);
        SelectedPatchItem = patch;
        return patch.IsReplaced || patch.IsEdited;
    }

    /// <summary>资产在 Pak 里没了时的占位行：保留在列表里，让人看得见、能移除。</summary>
    private static PatchItem CreatePlaceholderPatch(ModProjectTask task, string error)
    {
        string workDirectory = Path.Combine(Path.GetTempPath(), "prism-desktop-patch", Guid.NewGuid().ToString("N"));
        return new PatchItem(
            kind: string.IsNullOrWhiteSpace(task.Type) ? "texture" : task.Type,
            sourcePath: task.Asset,
            name: Path.GetFileNameWithoutExtension(task.Asset),
            format: string.IsNullOrWhiteSpace(task.Format) ? "?" : task.Format,
            sizeLabel: task.SizeLabel ?? string.Empty,
            width: 0,
            height: 0,
            workDirectory: workDirectory,
            inputUassetPath: string.Empty)
        {
            Detail = task.Detail ?? string.Empty,
            Status = "失败",
            Error = error,
        };
    }

    /// <summary>把工程里的改动集套到刚从 Pak 读出来的词条上（不认识的条目跳过）。</summary>
    private static int ApplyLocresEditsFromProject(PatchItem patch, ModProjectTask task)
    {
        if (task.Edits is null || task.Edits.Count == 0)
        {
            return 0;
        }

        var byFullKey = new Dictionary<string, ModProjectLocresEdit>(StringComparer.Ordinal);
        var byKey = new Dictionary<string, ModProjectLocresEdit>(StringComparer.Ordinal);
        foreach (ModProjectLocresEdit edit in task.Edits)
        {
            byFullKey[$"{edit.Ns}\u001f{edit.Key}"] = edit;
            byKey[edit.Key] = edit;
        }

        int applied = 0;
        foreach (LocresEntryVM entry in patch.LocresEntries)
        {
            if (!byFullKey.TryGetValue($"{entry.Namespace}\u001f{entry.Key}", out ModProjectLocresEdit? edit) &&
                !byKey.TryGetValue(entry.Key, out edit))
            {
                continue;
            }

            if (string.Equals(entry.Text, edit.Text, StringComparison.Ordinal))
            {
                continue;
            }

            entry.ApplyTranslation(edit.Text);
            applied++;
        }

        if (applied > 0)
        {
            patch.ApplyLocresFilter();
        }

        return applied;
    }

    // ============ 关闭 ============

    [RelayCommand]
    private async Task CloseProjectAsync()
    {
        if (!HasProject && PatchItems.Count == 0)
        {
            return;
        }

        if (!await ConfirmDiscardChangesAsync("关闭工程"))
        {
            return;
        }

        ClearPatchItems();
        _project = null;
        _projectStorageFile = null;
        _projectLocalPath = string.Empty;
        ProjectPath = string.Empty;
        ProjectName = string.Empty;
        IsProjectDirty = false;
        NotifyPatchItemsState();
        NotifyProjectState();
        StatusText = "已关闭工程（替换列表已清空）。";
    }

    /// <summary>关窗口前问一次。返回 true = 可以关。</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!HasUnsavedProjectChanges)
        {
            return true;
        }

        string message = HasProject
            ? $"「{ProjectDisplayName}」有还没保存的改动。{Environment.NewLine}{Environment.NewLine}" +
              "现在关掉就丢了 —— 想保留就先点页头的工程按钮保存。仍要关闭吗？"
            : $"当前有 {PatchItems.Count} 项替换还没保存成工程文件。{Environment.NewLine}{Environment.NewLine}" +
              "现在关掉就丢了。仍要关闭吗？";
        return await ConfirmAsync("有未保存的改动", message);
    }

    private async Task<bool> ConfirmDiscardChangesAsync(string action)
    {
        if (!HasUnsavedProjectChanges)
        {
            return true;
        }

        string message = $"「{ProjectDisplayName}」有 {PatchItems.Count} 项改动还没保存到工程文件。" +
                         $"{Environment.NewLine}{Environment.NewLine}{action}会丢掉它们，确定吗？";
        return await ConfirmAsync("有未保存的改动", message);
    }

    /// <summary>平台确认对话框：Android 用原生 AlertDialog，桌面用内置小窗，无头环境直接放过。</summary>
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        if (OperatingSystem.IsAndroid())
        {
            return NativeConfirmAsync is null || await NativeConfirmAsync(title, message);
        }

        if (TopLevel is not Window window)
        {
            return true;
        }

        try
        {
            return await Views.ConfirmDialog.ShowAsync(window, message, title);
        }
        catch
        {
            return true;
        }
    }

    // ============ 构建记录 ============

    /// <summary>构建完把结果写进工程（不改变"未保存"状态：这是记录，不是内容）。</summary>
    private void RecordPatchBuild(string? outputPath, int files, long bytes)
    {
        if (_project is null)
        {
            return;
        }

        string stored = string.IsNullOrWhiteSpace(outputPath)
            ? _project.Output
            : ProjectStore.ToStored(ProjectDirectory(), outputPath);
        _project.Output = stored;
        _project.LastBuild = new ModProjectBuild
        {
            Utc = DateTime.UtcNow.ToString("O"),
            Output = stored,
            Files = files,
            Bytes = bytes,
        };

        if (string.IsNullOrWhiteSpace(_projectLocalPath))
        {
            return;
        }

        try
        {
            ProjectStore.Save(_projectLocalPath, _project);
            NotifyProjectState();
        }
        catch (Exception ex)
        {
            AppLog.Warn("工程", $"构建记录没能写回工程：{ex.Message}");
        }
    }

    // ============ 最近工程 ============

    /// <summary>启动时从设置里恢复最近工程列表（不读工程文件本身，首屏才不会卡）。</summary>
    private void ReloadRecentProjects()
    {
        _recentProjects.Clear();
        foreach (RecentProjectRecord record in _settings.RecentProjects)
        {
            if (string.IsNullOrWhiteSpace(record.Path))
            {
                continue;
            }

            _recentProjects.Add(new RecentProjectItem(
                record.Path,
                string.IsNullOrWhiteSpace(record.Name) ? Path.GetFileNameWithoutExtension(record.Path) : record.Name,
                DescribeRecent(record) + DescribeRecentMissing(record.Path)));
        }

        RebuildVisibleRecentProjects();
        OnPropertyChanged(nameof(HasRecentProjects));
    }

    /// <summary>卡片上直接说清楚"这个文件不在了"，点开才知道就太晚了（移动盘、被删、被改名都会遇到）。</summary>
    private static string DescribeRecentMissing(string path)
    {
        if (path.Contains("://", StringComparison.Ordinal))
        {
            // Android 的 SAF 路径没法直接判断存在性，交给打开时再报。
            return string.Empty;
        }

        try
        {
            return File.Exists(path) ? string.Empty : " · 文件不在了";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DescribeRecent(RecentProjectRecord record)
    {
        string when = DescribeAgo(record.Utc);
        return record.TaskCount > 0 ? $"{record.TaskCount} 项 · {when}" : when;
    }

    private static string DescribeAgo(string utc)
    {
        if (!DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime saved))
        {
            return string.Empty;
        }

        TimeSpan age = DateTime.UtcNow - saved.ToUniversalTime();
        if (age.TotalMinutes < 2)
        {
            return "刚刚";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes} 分钟前";
        }

        if (age.TotalDays < 1)
        {
            return $"{(int)age.TotalHours} 小时前";
        }

        if (age.TotalDays < 30)
        {
            return $"{(int)age.TotalDays} 天前";
        }

        return saved.ToLocalTime().ToString("yyyy-MM-dd");
    }

    private void RememberRecentProject()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath))
        {
            return;
        }

        _settings.RecentProjects.RemoveAll(r => string.Equals(r.Path, ProjectPath, StringComparison.OrdinalIgnoreCase));
        _settings.RecentProjects.Insert(0, new RecentProjectRecord
        {
            Path = ProjectPath,
            Name = ProjectDisplayName,
            TaskCount = PatchItems.Count,
            Utc = DateTime.UtcNow.ToString("O"),
        });
        while (_settings.RecentProjects.Count > 8)
        {
            _settings.RecentProjects.RemoveAt(_settings.RecentProjects.Count - 1);
        }

        SaveSettings();
        ReloadRecentProjects();
    }

    private void ForgetRecentProject(string path)
    {
        _settings.RecentProjects.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
        ReloadRecentProjects();
    }

    // ============ 小工具 ============

    private void ClearPatchItems()
    {
        foreach (PatchItem patch in PatchItems.ToList())
        {
            TryDeleteDirectory(patch.WorkDirectory);
        }

        PatchItems.Clear();
        SelectedPatchItem = null;
    }

    /// <summary>
    /// 保存对话框里的建议文件名。
    ///
    /// 扩展名要我们自己带上：手机的系统保存界面并不会替我们补（不写就真的没有后缀，
    /// 之后「打开工程」的 *.prismproj 过滤就看不到它了）。
    /// 同时**不要**再给 FilePickerSaveOptions.DefaultExtension —— 两个都给会变成
    /// 「xxx.prismproj.prismproj」。
    /// </summary>
    private string SuggestedProjectFileName => SafeStem(ProjectDisplayName) + ProjectStore.Extension;

    /// <summary>构建补丁 Pak 时的建议文件名：有工程就用工程名，比 patch_20250601_120000 有用得多。</summary>
    private string SuggestedPakFileName => SafeStem(ProjectDisplayName) + "_P.pak";

    /// <summary>建议的构建输出目录（上次构建到哪就还去哪）。</summary>
    private async Task<IStorageFolder?> TryGetProjectOutputFolderAsync(TopLevel top)
    {
        string? folder = null;
        if (!string.IsNullOrWhiteSpace(_project?.Output))
        {
            try
            {
                folder = Path.GetDirectoryName(ProjectStore.ToAbsolute(ProjectDirectory(), _project!.Output));
            }
            catch
            {
                folder = null;
            }
        }

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        try
        {
            return await top.StorageProvider.TryGetFolderFromPathAsync(folder);
        }
        catch
        {
            return null;
        }
    }

    private string ProjectDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_projectLocalPath))
        {
            string? directory = Path.GetDirectoryName(_projectLocalPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }

        return DefaultProjectDirectory();
    }

    private static string DefaultProjectDirectory()
    {
        string directory = Path.Combine(PrivateDataDir(), "projects");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>工程里相对路径的解析基准：有本地路径就在旁边，Android SAF 落到应用私有目录。</summary>
    private string ResolveProjectDirectory(string? localPath)
    {
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            string? directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }

        return DefaultProjectDirectory();
    }

    private static string ProjectFileStem(string? localPath, IStorageFile? storage)
    {
        string name = !string.IsNullOrWhiteSpace(localPath)
            ? Path.GetFileName(localPath)
            : storage?.Name ?? string.Empty;
        string stem = SafeStem(Path.GetFileNameWithoutExtension(name));
        return stem.Length > 0 ? stem : "mod";
    }

    private static string ProjectUriOf(IStorageFile? storage) =>
        storage?.Path is { } uri ? uri.ToString() : string.Empty;

    private static string SafeStem(string? value)
    {
        string stem = string.IsNullOrWhiteSpace(value) ? "mod" : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(invalid, '_');
        }

        return stem.Length > 0 ? stem : "mod";
    }
}
