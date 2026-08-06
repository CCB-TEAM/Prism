using System.Text.Json;
using Android.Content.PM;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetTexture.Core;

namespace Prism;

[Activity(
    Name = "com.ccbteam.prism.MainActivity",
    Label = "@string/app_name",
    MainLauncher = true,
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.ScreenSize |
        ConfigChanges.SmallestScreenSize |
        ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    private const int PickPakRequest = 1001;
    private const int PickUsmapRequest = 1002;
    private const int PickRawExportTreeRequest = 1003;
    private const int PickPngExportTreeRequest = 1004;
    private const int PickReplacementFileRequest = 1005;
    private const int CreateModifiedPakRequest = 1006;
    private const int CreateLogExportRequest = 1007;
    private const int PickTypedExportTreeRequest = 1008;
    private const int PickFolderExportTreeRequest = 1009;
    private const int PickMergePakRequest = 1010;
    private const int CreateMergedPakRequest = 1011;

    private const int MaxDiagnosticLines = 4000;
    private const int UiDiagnosticLines = 120;
    private const int MaxEntryThumbnails = 80;
    private const string GitHubLatestReleaseApi = "https://api.github.com/repos/kardswalker/Prism/releases/latest";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient UpdateHttpClient = CreateUpdateHttpClient();
    private static readonly object DiagnosticsLock = new();
    private static readonly List<string> Diagnostics = [];
    private static int _compressionWarmupStarted;

    private PakTool.Core.PakArchiveSession? _session;
    private string? _pakPath;
    private string? _pakDisplayName;
    private string? _mergePakPath;
    private string? _mergePakDisplayName;
    private string? _usmapPath;
    private string? _usmapDisplayName;
    private string _currentFolder = string.Empty;
    private string _status = "请选择一个 .pak 文件开始使用。";
    private string? _selectedSummary;
    private string? _previewDataUrl;
    private string? _previewTitle;
    private PakTool.Core.AssetPreviewDto? _selectedPreview;
    private string? _selectedPreviewResourceUrl;
    private readonly PreviewBlobStore _previewBlobStore = new();
    private readonly Dictionary<string, string> _entryThumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _entryThumbnailOrder = new();
    private System.Threading.CancellationTokenSource? _previewCancellation;
    private int _previewGeneration;
    private string _oodleStatus = "Oodle native not checked.";
    private bool _busy;
    private bool _webReady;
    private int _openGeneration;
    private System.Threading.CancellationTokenSource? _indexCancellation;
    private global::Android.Net.Uri? _exportTreeUri;
    private PakTool.Core.ArchiveEntryDto? _selectedEntry;
    private readonly List<PatchPakItem> _patchItems = [];
    private string? _selectedPatchItemId;
    private string? _pendingReplacementPatchItemId;
    private bool _pendingPatchPakUseOodleCompression;
    private bool _pendingMergeUseOodleCompression;
    private bool _pendingMergeReplaceConflicts = true;
    private string? _pendingMergeAesKey;
    private string? _pendingFolderExportKind;
    private string _activePage = "browse";
    private string? _lastError;
    private readonly SemaphoreSlim _updateCheckLock = new(1, 1);
    private readonly SemaphoreSlim _previewDecodeLock = new(1, 1);
    private int _automaticUpdateCheckStarted;
    private int _activityRequestInFlight;
    private IReadOnlyList<PakTool.Core.ArchiveEntryDto> _entries = [];
    private global::Android.Webkit.WebView? _webView;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        RequestWindowFeature(global::Android.Views.WindowFeatures.NoTitle);
        base.OnCreate(savedInstanceState);
        LogPerf("Prism OnCreate started.");
        ApplySystemBarsMode();
        CleanupImportCache();
        _oodleStatus = EnsureBundledOodleInitialized("startup");
        LogDecode("Oodle startup status: " + _oodleStatus);

        _webView = new global::Android.Webkit.WebView(this);
        ConfigureWebView(_webView);
        SetContentView(_webView);
        _webView.LoadDataWithBaseURL("https://paktool.local/", BuildHtml(), "text/html", "utf-8", null);
    }

    protected override void OnDestroy()
    {
        _indexCancellation?.Cancel();
        _indexCancellation?.Dispose();
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _session?.Dispose();
        _previewBlobStore.Clear();
        ClearPatchWorkspace(pushState: false);
        DeleteCachedImport(_pakPath);
        DeleteCachedImport(_mergePakPath);
        DeleteCachedImport(_usmapPath);
        _webView?.Destroy();
        base.OnDestroy();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
            ApplySystemBarsMode();
    }

    protected override void OnResume()
    {
        base.OnResume();
        ApplySystemBarsMode();
    }

    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplySystemBarsMode();
        PushState();
    }

    public override void OnBackPressed()
    {
        if (!string.IsNullOrEmpty(_currentFolder))
        {
            _ = NavigateUpAsync();
            return;
        }

        base.OnBackPressed();
    }

    protected override async void OnActivityResult(int requestCode, Result resultCode, global::Android.Content.Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        try
        {
            if (resultCode != Result.Ok || data?.Data is null)
                return;

            if (requestCode is PickRawExportTreeRequest or PickPngExportTreeRequest or PickTypedExportTreeRequest or PickFolderExportTreeRequest)
            {
                _exportTreeUri = data.Data;
                TryPersistUriPermission(data);
                if (requestCode == PickFolderExportTreeRequest)
                    await ExportCurrentFolderAsync(_pendingFolderExportKind ?? "raw");
                else if (requestCode == PickTypedExportTreeRequest)
                    await ExportSelectedTypedAsync();
                else if (requestCode == PickPngExportTreeRequest)
                    await ExportSelectedPngAsync();
                else
                    await ExportSelectedRawAsync();
            }
            else if (requestCode == PickReplacementFileRequest)
            {
                var patchItemId = _pendingReplacementPatchItemId;
                _pendingReplacementPatchItemId = null;
                if (string.IsNullOrWhiteSpace(patchItemId))
                    throw new InvalidOperationException("Select a Patch item before choosing a replacement file.");

                var item = FindPatchItem(patchItemId)
                    ?? throw new InvalidOperationException("Patch item was not found.");
                var isAudioReplacement = item.Kind.Equals("audio", StringComparison.OrdinalIgnoreCase);
                var displayName = GetDisplayName(data.Data, isAudioReplacement ? "replacement.audio" : "replacement.png");
                var extension = System.IO.Path.GetExtension(displayName);
                if (string.IsNullOrWhiteSpace(extension))
                    extension = isAudioReplacement ? ".audio" : ".png";

                var replacementPath = await CopyDocumentToCacheAsync(data.Data, extension, "Importing replacement file");
                if (isAudioReplacement)
                    await ReplacePatchItemAudioAsync(patchItemId, replacementPath, displayName);
                else
                    await ReplacePatchItemTextureAsync(patchItemId, replacementPath, displayName);
            }
            else if (requestCode == CreateModifiedPakRequest)
            {
                var useOodleCompression = _pendingPatchPakUseOodleCompression;
                _pendingPatchPakUseOodleCompression = false;
                await BuildPatchPakToUriAsync(data.Data, useOodleCompression);
            }
            else if (requestCode == CreateMergedPakRequest)
            {
                var useOodleCompression = _pendingMergeUseOodleCompression;
                var replaceConflicts = _pendingMergeReplaceConflicts;
                await BuildMergedPakToUriAsync(data.Data, replaceConflicts, useOodleCompression, _pendingMergeAesKey);
                _pendingMergeAesKey = null;
            }
            else if (requestCode == CreateLogExportRequest)
            {
                await ExportLogToUriAsync(data.Data);
            }
            else if (requestCode == PickUsmapRequest)
            {
                var oldUsmapPath = _usmapPath;
                var newUsmapDisplayName = GetDisplayName(data.Data, "Mapping.usmap");
                var newUsmapPath = await CopyDocumentToCacheAsync(data.Data, ".usmap", "Importing usmap");
                _usmapDisplayName = newUsmapDisplayName;
                _usmapPath = newUsmapPath;
                if (_session?.IsOpen == true)
                {
                    await _session.LoadUsmapAsync(_usmapPath);
                    SetStatus("已选择并加载 Usmap。 ");
                }
                else
                {
                    SetStatus("已选择 Usmap。 ");
                }

                DeleteCachedImport(oldUsmapPath);
            }
            else if (requestCode == PickMergePakRequest)
            {
                var oldMergePakPath = _mergePakPath;
                var newMergePakDisplayName = GetDisplayName(data.Data, "Merge pak");
                var newMergePakPath = await CopyDocumentToCacheAsync(data.Data, ".pak", "Importing merge pak");
                _mergePakDisplayName = newMergePakDisplayName;
                _mergePakPath = newMergePakPath;
                SetStatus("已选择要合并进来的 Pak。");
                DeleteCachedImport(oldMergePakPath);
            }
            else
            {
                var oldPakPath = _pakPath;
                var newPakDisplayName = GetDisplayName(data.Data, "Selected pak");
                var newPakPath = await CopyDocumentToCacheAsync(data.Data, ".pak", "Importing pak");
                CloseCurrentArchive();
                _pakDisplayName = newPakDisplayName;
                _pakPath = newPakPath;
                SetStatus("已选择 Pak。 ");
                DeleteCachedImport(oldPakPath);
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            SetStatus(ex.Message);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _activityRequestInFlight, 0);
        }
    }

    private void ConfigureWebView(global::Android.Webkit.WebView webView)
    {
        webView.SetBackgroundColor(global::Android.Graphics.Color.Rgb(247, 244, 235));
        webView.Settings.JavaScriptEnabled = true;
        webView.Settings.DomStorageEnabled = true;
        webView.Settings.UseWideViewPort = true;
        webView.Settings.LoadWithOverviewMode = true;
        webView.Settings.AllowFileAccess = false;
        webView.Settings.AllowContentAccess = false;
        webView.SetWebViewClient(new PakToolWebViewClient(this));
    }

    private void ApplySystemBarsMode()
    {
        var decorView = Window?.DecorView;
        if (decorView is null)
            return;

        decorView.SystemUiVisibility = (global::Android.Views.StatusBarVisibility)(
            global::Android.Views.SystemUiFlags.ImmersiveSticky |
            global::Android.Views.SystemUiFlags.HideNavigation |
            global::Android.Views.SystemUiFlags.Fullscreen |
            global::Android.Views.SystemUiFlags.LayoutHideNavigation |
            global::Android.Views.SystemUiFlags.LayoutFullscreen |
            global::Android.Views.SystemUiFlags.LayoutStable);
    }

    private bool HandleBridgeUri(global::Android.Net.Uri? uri)
    {
        if (uri?.Scheme is null || !uri.Scheme.Equals("paktool", StringComparison.OrdinalIgnoreCase))
            return false;

        var action = uri.Host ?? string.Empty;
        var payload = uri.GetQueryParameter("payload") ?? "{}";
        _ = HandleBridgeActionAsync(action, payload);
        return true;
    }

    private async Task HandleBridgeActionAsync(string action, string payloadJson)
    {
        try
        {
            if (_busy && IsBusyGuardedBridgeAction(action))
            {
                LogDecode($"Bridge action ignored while busy: {action}");
                return;
            }

            switch (action)
            {
                case "pickPak":
                    PickFile(PickPakRequest);
                    break;
                case "pickMergePak":
                    PickFile(PickMergePakRequest);
                    break;
                case "pickUsmap":
                    PickFile(PickUsmapRequest);
                    break;
                case "openPak":
                    await OpenPakAsync(GetPayloadString(payloadJson, "aesKey"));
                    break;
                case "search":
                    await SearchAsync(GetPayloadString(payloadJson, "query"));
                    break;
                case "up":
                    await NavigateUpAsync();
                    break;
                case "entry":
                    var index = GetPayloadInt(payloadJson, "index");
                    if (index >= 0 && index < _entries.Count)
                        await OpenEntryAsync(_entries[index]);
                    break;
                case "exportRaw":
                    await ExportSelectedRawAsync();
                    break;
                case "exportTyped":
                    await ExportSelectedTypedAsync();
                    break;
                case "exportPng":
                    await ExportSelectedTypedAsync();
                    break;
                case "exportFolder":
                    await ExportCurrentFolderAsync(GetPayloadString(payloadJson, "kind"));
                    break;
                case "addFolderToPatchPak":
                    await AddCurrentFolderToPatchPakAsync();
                    break;
                case "showPage":
                    ShowPage(GetPayloadString(payloadJson, "page"));
                    break;
                case "addSelectedToPatchPak":
                    await AddSelectedToPatchPakAsync();
                    break;
                case "selectPatchItem":
                    SelectPatchItem(GetPayloadString(payloadJson, "id"));
                    break;
                case "removePatchItem":
                    RemovePatchItem(GetPayloadString(payloadJson, "id"));
                    break;
                case "pickPatchReplacementImage":
                    PickPatchReplacementImage(GetPayloadString(payloadJson, "id"));
                    break;
                case "updatePatchLocresEntry":
                    await UpdatePatchLocresEntryAsync(
                        GetPayloadString(payloadJson, "id"),
                        GetPayloadInt(payloadJson, "index"),
                        GetPayloadString(payloadJson, "text"));
                    break;
                case "updateMaterialParameter":
                    await UpdatePatchMaterialParameterAsync(
                        GetPayloadString(payloadJson, "id"),
                        GetPayloadString(payloadJson, "kind"),
                        GetPayloadInt(payloadJson, "index"),
                        GetPayloadFloat(payloadJson, "value"),
                        GetPayloadFloat(payloadJson, "r"),
                        GetPayloadFloat(payloadJson, "g"),
                        GetPayloadFloat(payloadJson, "b"),
                        GetPayloadFloat(payloadJson, "a"),
                        GetPayloadInt(payloadJson, "rawIndex"));
                    break;
                case "buildPatchPak":
                    CreatePatchPakDocument(GetPayloadBool(payloadJson, "useOodleCompression"));
                    break;
                case "mergePak":
                    await StartMergePakAsync(
                        GetPayloadBool(payloadJson, "askBeforeReplace"),
                        GetPayloadBool(payloadJson, "useOodleCompression"),
                        GetPayloadString(payloadJson, "aesKey"));
                    break;
                case "exportLog":
                    CreateLogExportDocument();
                    break;
                case "checkUpdate":
                    await CheckForUpdatesAsync(userInitiated: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            SetStatus(ex.Message);
        }
    }

    private static bool IsBusyGuardedBridgeAction(string action)
    {
        return action is
            "pickPak" or
            "pickMergePak" or
            "pickUsmap" or
            "openPak" or
            "search" or
            "up" or
            "entry" or
            "exportRaw" or
            "exportTyped" or
            "exportPng" or
            "exportFolder" or
            "addFolderToPatchPak" or
            "addSelectedToPatchPak" or
            "pickPatchReplacementImage" or
            "updatePatchLocresEntry" or
            "updateMaterialParameter" or
            "buildPatchPak" or
            "mergePak" or
            "exportLog" or
            "checkUpdate";
    }

    private void PickFile(int requestCode)
    {
        var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionOpenDocument);
        intent.AddCategory(global::Android.Content.Intent.CategoryOpenable);
        intent.SetType("*/*");
        StartActivityRequest(intent, requestCode);
    }

    private bool StartActivityRequest(global::Android.Content.Intent intent, int requestCode)
    {
        if (System.Threading.Interlocked.Exchange(ref _activityRequestInFlight, 1) != 0)
        {
            LogDecode($"Activity request ignored while another request is in flight: {requestCode}");
            return false;
        }

        try
        {
            StartActivityForResult(intent, requestCode);
            return true;
        }
        catch
        {
            System.Threading.Interlocked.Exchange(ref _activityRequestInFlight, 0);
            throw;
        }
    }

    private void ShowPage(string? page)
    {
        _activePage = string.Equals(page, "patch", StringComparison.OrdinalIgnoreCase) ? "patch" : "browse";
        PushState();
    }

    private void PickPatchReplacementImage(string? id)
    {
        var item = FindPatchItem(id);
        if (item is null)
        {
            SetStatus("Select a Patch item first.");
            return;
        }

        _pendingReplacementPatchItemId = item.Id;
        _selectedPatchItemId = item.Id;
        _activePage = "patch";
        var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionOpenDocument);
        intent.AddCategory(global::Android.Content.Intent.CategoryOpenable);
        if (item.Kind.Equals("audio", StringComparison.OrdinalIgnoreCase))
        {
            intent.SetType("*/*");
            intent.PutExtra(global::Android.Content.Intent.ExtraMimeTypes, new[]
            {
                "audio/*",
                "application/octet-stream",
                "application/x-wem",
                "application/x-binka"
            });
        }
        else
        {
            intent.SetType("image/*");
        }
        StartActivityRequest(intent, PickReplacementFileRequest);
    }

    private void CreatePatchPakDocument(bool useOodleCompression)
    {
        if (_patchItems.Count == 0)
        {
            SetStatus("Add at least one resource to Patch Pak first.");
            return;
        }

        _pendingPatchPakUseOodleCompression = useOodleCompression;
        var baseName = _pakDisplayName;
        if (string.IsNullOrWhiteSpace(baseName) || !baseName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            baseName = "patched.pak";
        else
            baseName = System.IO.Path.GetFileNameWithoutExtension(baseName) + "_P.pak";

        var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionCreateDocument);
        intent.AddCategory(global::Android.Content.Intent.CategoryOpenable);
        intent.SetType("application/octet-stream");
        intent.PutExtra(global::Android.Content.Intent.ExtraTitle, baseName);
        StartActivityRequest(intent, CreateModifiedPakRequest);
    }

    private async Task StartMergePakAsync(bool askBeforeReplace, bool useOodleCompression, string? aesKey)
    {
        if (string.IsNullOrWhiteSpace(_pakPath))
        {
            SetStatus("请先选择第一个 Pak。");
            return;
        }

        if (string.IsNullOrWhiteSpace(_mergePakPath))
        {
            SetStatus("请先选择要合并进来的第二个 Pak。");
            return;
        }

        if (string.Equals(Path.GetFullPath(_pakPath), Path.GetFullPath(_mergePakPath), StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("第二个 Pak 不能和第一个 Pak 相同。");
            return;
        }

        if (!askBeforeReplace)
        {
            CreateMergedPakDocument(replaceConflicts: true, useOodleCompression, aesKey);
            return;
        }

        try
        {
            SetBusy(true, "正在扫描 Pak 冲突...");
            await LetWebViewRenderAsync();
            var summary = await Task.Run(async () => await InspectPakMergeAsync(aesKey));
            SetBusy(false);

            RunOnUiThread(() =>
            {
                var builder = new global::Android.App.AlertDialog.Builder(this);
                builder.SetTitle("确认合并 Pak");
                builder.SetMessage(
                    $"基础 Pak：{_pakDisplayName ?? "Base Pak"}\n" +
                    $"合并 Pak：{_mergePakDisplayName ?? "Merge Pak"}\n\n" +
                    $"基础文件：{summary.BaseCount:N0}\n" +
                    $"第二个 Pak 文件：{summary.MergeCount:N0}\n" +
                    $"同路径冲突：{summary.ConflictCount:N0}\n\n" +
                    "选择“替换”会保留第二个 Pak 的同路径文件；选择“不替换”会保留第一个 Pak 的同路径文件。");
                builder.SetPositiveButton("替换", (_, _) => CreateMergedPakDocument(true, useOodleCompression, aesKey));
                builder.SetNegativeButton("不替换", (_, _) => CreateMergedPakDocument(false, useOodleCompression, aesKey));
                builder.SetNeutralButton("取消", (_, _) => SetStatus("已取消 Pak 合并。"));
                builder.Show();
            });
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            SetStatus("扫描 Pak 冲突失败：" + ex.Message);
        }
        finally
        {
            if (_busy)
                SetBusy(false);
        }
    }

    private void CreateMergedPakDocument(bool replaceConflicts, bool useOodleCompression, string? aesKey)
    {
        if (string.IsNullOrWhiteSpace(_pakPath) || string.IsNullOrWhiteSpace(_mergePakPath))
        {
            SetStatus("请先选择两个 Pak。");
            return;
        }

        _pendingMergeReplaceConflicts = replaceConflicts;
        _pendingMergeUseOodleCompression = useOodleCompression;
        _pendingMergeAesKey = aesKey;

        var baseName = _pakDisplayName;
        if (string.IsNullOrWhiteSpace(baseName) || !baseName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            baseName = "merged.pak";
        else
            baseName = System.IO.Path.GetFileNameWithoutExtension(baseName) + "_merged.pak";

        var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionCreateDocument);
        intent.AddCategory(global::Android.Content.Intent.CategoryOpenable);
        intent.SetType("application/octet-stream");
        intent.PutExtra(global::Android.Content.Intent.ExtraTitle, baseName);
        StartActivityRequest(intent, CreateMergedPakRequest);
    }

    private void CreateLogExportDocument()
    {
        var fileName = $"prism-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionCreateDocument);
        intent.AddCategory(global::Android.Content.Intent.CategoryOpenable);
        intent.SetType("text/plain");
        intent.PutExtra(global::Android.Content.Intent.ExtraTitle, fileName);
        StartActivityRequest(intent, CreateLogExportRequest);
    }

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Prism-Android/1.2");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (!await _updateCheckLock.WaitAsync(0))
        {
            if (userInitiated)
                SetStatus("正在检查更新，请稍候。");
            return;
        }

        try
        {
            if (userInitiated)
                SetStatus("正在从 GitHub 检查更新...");

            using var response = await UpdateHttpClient.GetAsync(GitHubLatestReleaseApi);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            var tag = GetJsonString(root, "tag_name") ?? GetJsonString(root, "name") ?? "unknown";
            var releaseName = GetJsonString(root, "name") ?? tag;
            var releasePage = GetJsonString(root, "html_url") ?? "https://github.com/kardswalker/Prism/releases/latest";
            var notes = GetJsonString(root, "body");
            var apkUrl = FindReleaseApkUrl(root);
            var currentText = GetInstalledVersionName();
            var currentVersion = ParseReleaseVersion(currentText);
            var latestVersion = ParseReleaseVersion(tag);

            if (latestVersion is null)
                throw new InvalidOperationException($"GitHub release tag '{tag}' is not a recognizable version.");

            if (currentVersion is not null && latestVersion <= currentVersion)
            {
                if (userInitiated)
                {
                    SetStatus($"Prism {currentText} 已是最新版本。");
                    RunOnUiThread(() =>
                    {
                        var dialog = new global::Android.App.AlertDialog.Builder(this);
                        dialog.SetTitle("没有可用更新");
                        dialog.SetMessage($"当前使用的 Prism {currentText} 已是最新版本。");
                        dialog.SetPositiveButton("确定", (_, _) => { });
                        dialog.Show();
                    });
                }
                return;
            }

            SetStatus($"发现新版本 Prism {tag}。");
            var message = $"当前版本：{currentText}\n最新版本：{tag}";
            if (!string.IsNullOrWhiteSpace(notes))
            {
                var trimmedNotes = notes.Trim();
                if (trimmedNotes.Length > 1200)
                    trimmedNotes = trimmedNotes[..1200] + "...";
                message += "\n\n" + trimmedNotes;
            }

            RunOnUiThread(() =>
            {
                var builder = new global::Android.App.AlertDialog.Builder(this);
                builder.SetTitle($"发现新版本：{releaseName}");
                builder.SetMessage(message);
                builder.SetNegativeButton("稍后", (_, _) => { });

                if (!string.IsNullOrWhiteSpace(apkUrl))
                {
                    builder.SetPositiveButton("下载 APK", (_, _) => OpenExternalUrl(apkUrl));
                    builder.SetNeutralButton("发布页面", (_, _) => OpenExternalUrl(releasePage));
                }
                else
                {
                    builder.SetPositiveButton("打开发布页面", (_, _) => OpenExternalUrl(releasePage));
                }

                builder.Show();
            });
        }
        catch (Exception ex)
        {
            LogDecode($"Update check failed: {ex.GetType().Name}: {ex.Message}");
            if (userInitiated)
            {
                _lastError = ex.ToString();
                SetStatus("检查更新失败：" + ex.Message);
                RunOnUiThread(() =>
                {
                    var dialog = new global::Android.App.AlertDialog.Builder(this);
                    dialog.SetTitle("检查更新失败");
                    dialog.SetMessage(ex.Message);
                    dialog.SetPositiveButton("确定", (_, _) => { });
                    dialog.Show();
                });
            }
        }
        finally
        {
            _updateCheckLock.Release();
        }
    }

    private string GetInstalledVersionName()
    {
        try
        {
            return PackageManager?.GetPackageInfo(PackageName!, 0)?.VersionName ?? "1.2";
        }
        catch
        {
            return "1.2";
        }
    }

    private static Version? ParseReleaseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().TrimStart('v', 'V');
        var suffix = normalized.IndexOfAny(['-', '+']);
        if (suffix >= 0)
            normalized = normalized[..suffix];
        return Version.TryParse(normalized, out var version) ? version : null;
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? FindReleaseApkUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        return assets.EnumerateArray()
            .Select(asset => new
            {
                Name = GetJsonString(asset, "name"),
                Url = GetJsonString(asset, "browser_download_url")
            })
            .Where(asset => asset.Name?.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) == true &&
                            !string.IsNullOrWhiteSpace(asset.Url))
            .OrderByDescending(asset => asset.Name!.Contains("signed", StringComparison.OrdinalIgnoreCase))
            .Select(asset => asset.Url)
            .FirstOrDefault();
    }

    private void OpenExternalUrl(string url)
    {
        try
        {
            var intent = new global::Android.Content.Intent(
                global::Android.Content.Intent.ActionView,
                global::Android.Net.Uri.Parse(url));
            StartActivity(intent);
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            SetStatus("Could not open update link: " + ex.Message);
        }
    }

    private void PickExportTree(int requestCode)
    {
        var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionOpenDocumentTree);
        intent.AddFlags(global::Android.Content.ActivityFlags.GrantReadUriPermission);
        intent.AddFlags(global::Android.Content.ActivityFlags.GrantWriteUriPermission);
        intent.AddFlags(global::Android.Content.ActivityFlags.GrantPersistableUriPermission);
        StartActivityRequest(intent, requestCode);
    }

    private async Task OpenPakAsync(string? aesKey)
    {
        if (string.IsNullOrWhiteSpace(_pakPath))
        {
            SetStatus("请先选择 Pak。 ");
            return;
        }

        try
        {
            _indexCancellation?.Cancel();
            _indexCancellation?.Dispose();
            _indexCancellation = new System.Threading.CancellationTokenSource();
            var generation = ++_openGeneration;
            ClearPatchWorkspace(pushState: false);

            SetBusy(true, "正在打开 Pak...");
            await LetWebViewRenderAsync();

            var session = Session;
            var options = new PakTool.Core.PakOpenOptions(
                [_pakPath],
                aesKey,
                _usmapPath,
                DecodeLogger: LogDecode);
            var openClock = System.Diagnostics.Stopwatch.StartNew();
            var result = await Task.Run(async () => await session.OpenAsync(options));
            openClock.Stop();
            LogOpenTimings(result, openClock.Elapsed);

            SetStatus($"已挂载 {result.MountedArchiveCount} 个归档，共 {result.FileCount} 个文件，用时 {FormatDuration(openClock.Elapsed)}。 ");
            var listClock = System.Diagnostics.Stopwatch.StartNew();
            await NavigateToAsync(string.Empty, "Building file list...");
            listClock.Stop();
            LogPerf($"Root list: {FormatDuration(listClock.Elapsed)}, {_entries.Count} item(s)");
            StartDirectoryIndexBuild(session, generation, result.FileCount);
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            SetStatus(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SearchAsync(string? query)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                await NavigateToAsync(_currentFolder);
                return;
            }

            SetBusy(true, "正在搜索...");
            await LetWebViewRenderAsync();

            var session = Session;
            var trimmedQuery = query.Trim();
            var results = await Task.Run(async () => await session.SearchAsync(trimmedQuery));
            _entries = results;
            ClearSelection(pushState: false);
            SetStatus($"找到 {results.Count} 个结果。 ");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            SetStatus(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task NavigateToAsync(string folder, string? busyStatus = null)
    {
        var ownsBusy = !_busy && busyStatus is not null;
        if (busyStatus is not null)
        {
            SetBusy(true, busyStatus);
            await LetWebViewRenderAsync();
        }

        try
        {
            _currentFolder = NormalizeFolder(folder);
            _entries = await Task.Run(async () => await Session.ListAsync(_currentFolder));
            ClearSelection(pushState: false);
            SetStatus($"共 {_entries.Count} 个项目。 ");
        }
        finally
        {
            if (ownsBusy)
                SetBusy(false);
        }
    }

    private async Task NavigateUpAsync()
    {
        if (string.IsNullOrEmpty(_currentFolder))
            return;

        var trimmed = _currentFolder.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        await NavigateToAsync(slash < 0 ? string.Empty : trimmed[..(slash + 1)], "Loading folder...");
    }

    private async Task OpenEntryAsync(PakTool.Core.ArchiveEntryDto entry)
    {
        if (entry.IsDirectory)
        {
            await NavigateToAsync(entry.FullPath, "Loading folder...");
            return;
        }

        await SelectEntryAsync(entry);
    }

    private async Task SelectEntryAsync(PakTool.Core.ArchiveEntryDto entry)
    {
        var generation = System.Threading.Interlocked.Increment(ref _previewGeneration);
        var previousCancellation = _previewCancellation;
        previousCancellation?.Cancel();
        var previewCancellation = new System.Threading.CancellationTokenSource();
        _previewCancellation = previewCancellation;
        var cancellationToken = previewCancellation.Token;

        _selectedEntry = entry;
        LogDecode($"Entry selected: path={entry.FullPath}, ext={entry.Extension}, asset={entry.IsAssetPackage}, related={entry.RelatedPaths?.Count ?? 1}");
        var relatedCount = entry.RelatedPaths?.Count ?? 1;
        var packageSuffix = entry.IsAssetPackage && relatedCount > 1 ? $" / {relatedCount} raw files" : string.Empty;
        _selectedSummary = $"{entry.FullPath} ({entry.Size:n0} bytes{packageSuffix})";
        _previewDataUrl = null;
        _previewTitle = null;
        _selectedPreview = null;
        _selectedPreviewResourceUrl = null;
        _previewBlobStore.Clear();
        SetStatus("已选择文件。 ");

        try
        {
            SetBusy(true, "正在加载预览...");
            await LetWebViewRenderAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var session = Session;
            LogDecode($"Preview start: {entry.FullPath}");
            _oodleStatus = EnsureBundledOodleInitialized("preview");
            LogDecode($"Preview Oodle status: {_oodleStatus}; initialized={CUE4Parse.Compression.OodleHelper.Instance is not null}");
            await _previewDecodeLock.WaitAsync(cancellationToken);
            PakTool.Core.AssetPreviewDto preview;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                preview = await Task.Run(async () => await session.ReadPreviewAsync(entry.FullPath, cancellationToken), cancellationToken);
            }
            finally
            {
                _previewDecodeLock.Release();
            }

            if (generation != _previewGeneration || cancellationToken.IsCancellationRequested)
                return;

            if (preview is null)
            {
                LogDecode($"Preview returned null: {entry.FullPath}");
                _previewTitle = "No preview available.";
                SetStatus("该资源没有可用预览。 ");
                return;
            }

            _selectedPreview = preview;
            LogDecode($"Preview success: kind={preview.Kind}, title={preview.Title}, data={preview.Data?.Length ?? 0} bytes");
            if (preview.Kind.Equals("texture", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(preview.MimeType, "image/png", StringComparison.OrdinalIgnoreCase) &&
                preview.Data is { Length: > 0 } imageData)
            {
                _previewDataUrl = "data:image/png;base64," + Convert.ToBase64String(imageData);
                RememberEntryThumbnail(entry.FullPath, imageData);
            }

            _previewTitle = preview.Title;
            SetStatus(_previewTitle);
        }
        catch (OperationCanceledException) when (generation != _previewGeneration || cancellationToken.IsCancellationRequested)
        {
            LogDecode($"Preview canceled: {entry.FullPath}");
        }
        catch (Exception ex)
        {
            if (generation != _previewGeneration)
                return;

            _lastError = ex.ToString();
            LogDecode($"Preview failed: {entry.FullPath}, {ex.GetType().Name}: {ex.Message}");
            _previewTitle = "Preview failed.";
            _selectedPreview = new PakTool.Core.AssetPreviewDto(
                "error",
                "Preview failed",
                [new PakTool.Core.AssetPreviewDetailDto("Error", ex.Message)],
                Text: ex.ToString(),
                CanExportRaw: true);
            SetStatus("预览失败：" + ex.Message);
        }
        finally
        {
            if (generation == _previewGeneration)
                SetBusy(false);

            if (ReferenceEquals(_previewCancellation, previewCancellation))
            {
                _previewCancellation = null;
            }

            previewCancellation.Dispose();
        }
    }

    private async Task ExportSelectedRawAsync()
    {
        if (_selectedEntry is not { IsDirectory: false } entry)
        {
            SetStatus("请先选择文件。 ");
            return;
        }

        if (_exportTreeUri is null)
        {
            PickExportTree(PickRawExportTreeRequest);
            return;
        }

        try
        {
            SetBusy(true, "正在导出原始文件...");
            await LetWebViewRenderAsync();

            var session = Session;
            var files = await Task.Run(async () => await session.ReadRelatedRawFilesAsync(entry.FullPath));

            foreach (var (path, data) in files)
            {
                var outputUri = CreateDocument(_exportTreeUri, System.IO.Path.GetFileName(path), "application/octet-stream");
                await using var output = ContentResolver!.OpenOutputStream(outputUri, "wt")
                    ?? throw new InvalidOperationException("Could not open output document.");
                await output.WriteAsync(data);
            }

            SetStatus($"已导出 {files.Count} 个原始文件。 ");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            SetStatus("导出失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ExportSelectedPngAsync()
    {
        if (_selectedEntry is not { IsDirectory: false } entry)
        {
            SetStatus("请先选择贴图资源。 ");
            return;
        }

        if (_exportTreeUri is null)
        {
            PickExportTree(PickPngExportTreeRequest);
            return;
        }

        try
        {
            SetBusy(true, "正在解码贴图...");
            await LetWebViewRenderAsync();

            var session = Session;
            LogDecode($"PNG export decode start: {entry.FullPath}");
            _oodleStatus = EnsureBundledOodleInitialized("png export");
            LogDecode($"PNG export Oodle status: {_oodleStatus}; initialized={CUE4Parse.Compression.OodleHelper.Instance is not null}");
            var preview = await Task.Run(async () => await session.TryReadTexturePreviewAsync(entry.FullPath, int.MaxValue));
            if (preview is null)
            {
                LogDecode($"PNG export decode returned null: {entry.FullPath}");
                SetStatus("未找到可预览的贴图。 ");
                return;
            }

            SetBusy(true, "正在编码 PNG...");
            await LetWebViewRenderAsync();

            var png = preview.PngData;
            LogDecode($"PNG export writing: {preview.TextureName}, {preview.Width}x{preview.Height}, png={png.Length} bytes");
            var fileName = System.IO.Path.GetFileNameWithoutExtension(entry.Name) + ".png";
            var outputUri = CreateDocument(_exportTreeUri, fileName, "image/png");
            await using var output = ContentResolver!.OpenOutputStream(outputUri, "wt")
                ?? throw new InvalidOperationException("Could not open output document.");
            await output.WriteAsync(png);

            SetStatus($"已导出 {fileName}（{preview.Width}x{preview.Height}）。 ");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            LogDecode($"PNG export failed: {entry.FullPath}, {ex.GetType().Name}: {ex.Message}");
            SetStatus("PNG 导出失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ExportSelectedTypedAsync()
    {
        if (_selectedEntry is not { IsDirectory: false } entry)
        {
            SetStatus("请先选择可预览的资源。 ");
            return;
        }

        if (_exportTreeUri is null)
        {
            PickExportTree(PickTypedExportTreeRequest);
            return;
        }

        try
        {
            SetBusy(true, "正在准备导出...");
            await LetWebViewRenderAsync();

            var session = Session;
            LogDecode($"Typed export start: {entry.FullPath}, selectedKind={_selectedPreview?.Kind ?? "<none>"}");
            var export = await Task.Run(async () => await session.ReadTypedPreviewExportAsync(entry.FullPath));
            if (export.Files.Count == 0)
            {
                SetStatus("没有生成可导出的文件。 ");
                return;
            }

            SetBusy(true, "正在写入导出文件...");
            await LetWebViewRenderAsync();

            foreach (var file in export.Files)
            {
                var outputUri = CreateDocument(_exportTreeUri, file.FileName, file.MimeType);
                await using var output = ContentResolver!.OpenOutputStream(outputUri, "wt")
                    ?? throw new InvalidOperationException("Could not open output document.");
                await output.WriteAsync(file.Data);
            }

            var totalBytes = export.Files.Sum(file => (long) file.Data.Length);
            SetStatus($"已导出 {export.Files.Count} 个 {export.Kind} 文件，共 {FormatSize(totalBytes)}。 ");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            LogDecode($"Typed export failed: {entry.FullPath}, {ex.GetType().Name}: {ex.Message}");
            SetStatus("资源导出失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ExportCurrentFolderAsync(string? requestedKind)
    {
        var kind = NormalizeFolderExportKind(requestedKind);
        if (_session?.IsOpen != true)
        {
            SetStatus("请先打开 Pak。 ");
            return;
        }

        if (_exportTreeUri is null)
        {
            _pendingFolderExportKind = kind;
            PickExportTree(PickFolderExportTreeRequest);
            return;
        }

        try
        {
            SetBusy(true, $"正在扫描文件夹中的{GetFolderExportKindLabel(kind)}...");
            await LetWebViewRenderAsync();

            var session = Session;
            var rootFolder = NormalizeFolder(_currentFolder);
            var entries = await Task.Run(async () => await session.ListAsync(rootFolder, recursive: true));
            var files = entries
                .Where(entry => !entry.IsDirectory)
                .DistinctBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (files.Length == 0)
            {
                SetStatus("当前文件夹中没有可导出的文件。 ");
                return;
            }

            var rootUri = CreateFolderExportRoot(_exportTreeUri, rootFolder, kind);
            var createdDirectories = new Dictionary<string, global::Android.Net.Uri>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = rootUri
            };
            var createdFileNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var exportedFiles = 0;
            var exportedAssets = 0;
            var skipped = 0;
            var failed = 0;
            long totalBytes = 0;
            var rawPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < files.Length; i++)
            {
                var entry = files[i];
                if (i == 0 || i % 10 == 0)
                {
                    SetBusy(true, $"正在导出{GetFolderExportKindLabel(kind)} {i + 1}/{files.Length}...");
                    await LetWebViewRenderAsync();
                }

                try
                {
                    if (kind == "raw")
                    {
                        var rawFiles = await Task.Run(async () => await session.ReadRelatedRawFilesAsync(entry.FullPath));
                        var wroteAny = false;
                        foreach (var (path, data) in rawFiles)
                        {
                            if (!rawPaths.Add(path))
                                continue;

                            var relativePath = GetFolderRelativePath(rootFolder, path);
                            var relativeDirectory = GetParentFolder(relativePath);
                            var parentUri = EnsureOutputDirectory(rootUri, createdDirectories, relativeDirectory);
                            var fileName = GetFileNameFromPakPath(relativePath);
                            var outputUri = CreateUniqueDocument(parentUri, createdFileNames, relativeDirectory, fileName, "application/octet-stream");
                            await WriteDocumentAsync(outputUri, data);
                            exportedFiles++;
                            totalBytes += data.Length;
                            wroteAny = true;
                        }

                        if (wroteAny)
                            exportedAssets++;
                        else
                            skipped++;
                    }
                    else
                    {
                        var export = await Task.Run(async () => await session.ReadTypedPreviewExportAsync(entry.FullPath));
                        if (!FolderExportKindMatches(kind, export.Kind))
                        {
                            skipped++;
                            continue;
                        }

                        var relativePath = GetFolderRelativePath(rootFolder, entry.FullPath);
                        var relativeDirectory = GetParentFolder(relativePath);
                        var parentUri = EnsureOutputDirectory(rootUri, createdDirectories, relativeDirectory);
                        foreach (var file in export.Files)
                        {
                            var outputUri = CreateUniqueDocument(parentUri, createdFileNames, relativeDirectory, file.FileName, file.MimeType);
                            await WriteDocumentAsync(outputUri, file.Data);
                            exportedFiles++;
                            totalBytes += file.Data.Length;
                        }

                        exportedAssets++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    LogDecode($"Folder export skipped: kind={kind}, path={entry.FullPath}, {ex.GetType().Name}: {ex.Message}");
                }
            }

            SetStatus($"文件夹导出完成：{exportedAssets} 个资源、{exportedFiles} 个文件，共 {FormatSize(totalBytes)}；跳过 {skipped} 个，失败 {failed} 个。 ");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            LogDecode($"Folder export failed: kind={kind}, folder={_currentFolder}, {ex.GetType().Name}: {ex.Message}");
            SetStatus("文件夹导出失败：" + ex.Message);
        }
        finally
        {
            _pendingFolderExportKind = null;
            SetBusy(false);
        }
    }

    private async Task AddCurrentFolderToPatchPakAsync()
    {
        if (_session?.IsOpen != true)
        {
            SetStatus("请先打开 Pak。 ");
            return;
        }

        var rootFolder = NormalizeFolder(_currentFolder);
        var sourcePath = string.IsNullOrEmpty(rootFolder) ? "/" : rootFolder;
        try
        {
            SetBusy(true, "正在扫描文件夹中的可替换资源...");
            await LetWebViewRenderAsync();

            var entries = await Task.Run(async () => await Session.ListAsync(rootFolder, recursive: true));
            var files = entries
                .Where(entry => !entry.IsDirectory)
                .DistinctBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0)
            {
                SetStatus("当前文件夹中没有可添加的文件。");
                return;
            }

            var added = 0;
            var duplicates = 0;
            var unsupported = 0;
            var failed = 0;

            for (var i = 0; i < files.Length; i++)
            {
                if (i == 0 || i % 10 == 0)
                {
                    SetBusy(true, $"正在识别可替换资源 {i + 1}/{files.Length}...");
                    await LetWebViewRenderAsync();
                }

                var entry = files[i];
                try
                {
                    PatchAddResult result;
                    if (entry.Extension.TrimStart('.').Equals("locres", StringComparison.OrdinalIgnoreCase))
                    {
                        result = await AddLocresPatchItemAsync(entry);
                    }
                    else if (entry.IsAssetPackage)
                    {
                        var preview = await Task.Run(async () => await Session.TryReadTexturePreviewAsync(entry.FullPath));
                        result = preview is null
                            ? PatchAddResult.Unsupported
                            : await AddTexturePatchItemAsync(
                                entry,
                                preview.PngData.Length <= 2 * 1024 * 1024 ? EncodePreviewDataUrl(preview) : null,
                                loadPreviewIfMissing: false,
                                formatHint: preview.PixelFormat);
                    }
                    else
                    {
                        result = PatchAddResult.Unsupported;
                    }

                    switch (result)
                    {
                        case PatchAddResult.Added: added++; break;
                        case PatchAddResult.AlreadyExists: duplicates++; break;
                        default: unsupported++; break;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    LogDecode($"Folder Patch scan skipped: path={entry.FullPath}, {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (added > 0)
            {
                _activePage = "patch";
                _selectedPatchItemId ??= _patchItems.FirstOrDefault()?.Id;
            }

            SetStatus($"文件夹扫描完成：已添加 {added:N0} 个可替换资源，重复 {duplicates:N0} 个，跳过不支持的资源 {unsupported:N0} 个，失败 {failed:N0} 个。");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            LogDecode($"Add folder to Patch Pak failed: folder={sourcePath}, {ex.GetType().Name}: {ex.Message}");
            SetStatus("添加文件夹失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task AddSelectedToPatchPakAsync()
    {
        if (_selectedEntry is not { IsDirectory: false, IsAssetPackage: true } entry)
        {
            if (_selectedEntry is { IsDirectory: false } locresEntry &&
                _selectedPreview?.Kind.Equals("locres", StringComparison.OrdinalIgnoreCase) == true)
            {
                await AddSelectedLocresToPatchPakAsync(locresEntry);
                return;
            }

            SetStatus("请选择可替换的贴图或本地化资源。");
            return;
        }

        if (_selectedPreview?.Kind.Equals("locres", StringComparison.OrdinalIgnoreCase) == true)
        {
            await AddSelectedLocresToPatchPakAsync(entry);
            return;
        }

        if (_selectedPreview?.Kind.Equals("material", StringComparison.OrdinalIgnoreCase) == true)
        {
            await AddSelectedMaterialInstanceToPatchPakAsync(entry);
            return;
        }

        if (!IsSelectedTexturePreview())
        {
            SetStatus("请选择可替换的贴图或本地化资源。");
            return;
        }

        try
        {
            SetBusy(true, "正在将贴图加入替换 Pak...");
            await LetWebViewRenderAsync();
            var result = await AddTexturePatchItemAsync(entry, _previewDataUrl, formatHint: GetSelectedTextureFormatHint());
            _activePage = "patch";
            SetStatus(result == PatchAddResult.AlreadyExists
                ? "该资源已在替换 Pak 列表中。"
                : $"已将 {entry.Name} 加入替换 Pak。");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            SetStatus("加入替换 Pak 失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<PatchAddResult> AddTexturePatchItemAsync(
        PakTool.Core.ArchiveEntryDto entry,
        string? originalPreviewDataUrl,
        bool loadPreviewIfMissing = true,
        string? formatHint = null)
    {
        var existing = _patchItems.FirstOrDefault(item => item.SourcePath.Equals(entry.FullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _selectedPatchItemId = existing.Id;
            return PatchAddResult.AlreadyExists;
        }

        var workDirectory = CreatePatchItemWorkDirectory();
        try
        {
            var inputDirectory = System.IO.Path.Combine(workDirectory, "input");
            System.IO.Directory.CreateDirectory(inputDirectory);
            var rawFiles = await Task.Run(async () => await Session.ReadRelatedRawFilesAsync(entry.FullPath));
            string? localAssetPath = null;
            var originalFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pakPath, data) in rawFiles)
            {
                var localPath = System.IO.Path.Combine(inputDirectory, System.IO.Path.GetFileName(pakPath));
                await File.WriteAllBytesAsync(localPath, data);
                originalFiles[pakPath] = localPath;
                if (pakPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                    pakPath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                    localAssetPath = localPath;
            }

            if (localAssetPath is null)
                throw new InvalidOperationException("资源包中缺少 .uasset 文件。");

            if (loadPreviewIfMissing && (string.IsNullOrWhiteSpace(originalPreviewDataUrl) || string.IsNullOrWhiteSpace(formatHint)))
            {
                var preview = await Task.Run(async () => await Session.TryReadTexturePreviewAsync(entry.FullPath));
                formatHint ??= preview?.PixelFormat;
                if (string.IsNullOrWhiteSpace(originalPreviewDataUrl) && preview is not null && preview.PngData.Length <= 2 * 1024 * 1024)
                    originalPreviewDataUrl = EncodePreviewDataUrl(preview);
            }

            var service = new TextureReplacementService();
            var inspection = await Task.Run(async () => await service.InspectAsync(
                localAssetPath,
                EngineVersion.VER_UE5_6,
                _usmapPath,
                CancellationToken.None,
                formatHint));

            var relatedPaths = entry.RelatedPaths is { Count: > 0 } ? entry.RelatedPaths.ToArray() : [entry.FullPath];
            var item = new PatchPakItem(
                Guid.NewGuid().ToString("N"), "texture", entry.FullPath, entry.Name, relatedPaths,
                workDirectory, originalFiles, inspection.Format, inspection.Width, inspection.Height,
                originalPreviewDataUrl);
            _patchItems.Add(item);
            _selectedPatchItemId = item.Id;
            return PatchAddResult.Added;
        }
        catch
        {
            TryDeleteDirectory(workDirectory);
            throw;
        }
    }

    private async Task AddSelectedAudioToPatchPakAsync(PakTool.Core.ArchiveEntryDto entry)
    {
        var existing = _patchItems.FirstOrDefault(item => item.SourcePath.Equals(entry.FullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _selectedPatchItemId = existing.Id;
            _activePage = "patch";
            SetStatus("Resource is already in the Patch Pak list.");
            return;
        }

        try
        {
            SetBusy(true, "Adding audio to Patch Pak...");
            await LetWebViewRenderAsync();

            var payload = await Task.Run(async () => await Session.ReadAudioPayloadAsync(entry.FullPath));
            var workDirectory = CreatePatchItemWorkDirectory();
            var inputDirectory = System.IO.Path.Combine(workDirectory, "input");
            System.IO.Directory.CreateDirectory(inputDirectory);

            var rawFiles = await Task.Run(async () => await Session.ReadRelatedRawFilesAsync(entry.FullPath));
            var originalFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pakPath, data) in rawFiles)
            {
                var localPath = System.IO.Path.Combine(inputDirectory, System.IO.Path.GetFileName(pakPath));
                await File.WriteAllBytesAsync(localPath, data);
                originalFiles[pakPath] = localPath;
            }

            var relatedPaths = entry.RelatedPaths is { Count: > 0 } ? entry.RelatedPaths.ToArray() : [entry.FullPath];
            var item = new PatchPakItem(
                Guid.NewGuid().ToString("N"),
                "audio",
                entry.FullPath,
                entry.Name,
                relatedPaths,
                workDirectory,
                originalFiles,
                string.IsNullOrWhiteSpace(payload.Format) ? "audio" : payload.Format.ToUpperInvariant(),
                payload.Data.Length,
                0,
                null);

            _patchItems.Add(item);
            _selectedPatchItemId = item.Id;
            _activePage = "patch";
            SetStatus($"Added audio {entry.Name} to Patch Pak ({item.Format}, {FormatSize(payload.Data.Length)}).");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            SetStatus("Add audio to Patch Pak failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task AddSelectedLocresToPatchPakAsync(PakTool.Core.ArchiveEntryDto entry)
    {
        try
        {
            SetBusy(true, "正在将本地化资源加入替换 Pak...");
            await LetWebViewRenderAsync();
            var result = await AddLocresPatchItemAsync(entry);
            _activePage = "patch";
            SetStatus(result == PatchAddResult.AlreadyExists
                ? "该资源已在替换 Pak 列表中。"
                : $"已将本地化资源 {entry.Name} 加入替换 Pak。");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            SetStatus("加入本地化资源失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<PatchAddResult> AddLocresPatchItemAsync(PakTool.Core.ArchiveEntryDto entry)
    {
        var existing = _patchItems.FirstOrDefault(item => item.SourcePath.Equals(entry.FullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _selectedPatchItemId = existing.Id;
            return PatchAddResult.AlreadyExists;
        }

        var workDirectory = CreatePatchItemWorkDirectory();
        try
        {
            var locres = await Task.Run(async () => await Session.ReadLocresPreviewAsync(entry.FullPath));
            var inputDirectory = System.IO.Path.Combine(workDirectory, "input");
            System.IO.Directory.CreateDirectory(inputDirectory);
            var rawFiles = await Task.Run(async () => await Session.ReadRelatedRawFilesAsync(entry.FullPath));
            var originalFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pakPath, data) in rawFiles)
            {
                var localPath = System.IO.Path.Combine(inputDirectory, System.IO.Path.GetFileName(pakPath));
                await File.WriteAllBytesAsync(localPath, data);
                originalFiles[pakPath] = localPath;
            }

            var item = new PatchPakItem(
                Guid.NewGuid().ToString("N"), "locres", entry.FullPath, entry.Name,
                entry.RelatedPaths is { Count: > 0 } ? entry.RelatedPaths.ToArray() : [entry.FullPath],
                workDirectory, originalFiles, locres.Version, locres.EntryCount, locres.NamespaceCount, null)
            {
                LocresEntries = locres.Entries.ToList()
            };
            _patchItems.Add(item);
            _selectedPatchItemId = item.Id;
            return PatchAddResult.Added;
        }
        catch
        {
            TryDeleteDirectory(workDirectory);
            throw;
        }
    }

    private async Task AddSelectedMaterialInstanceToPatchPakAsync(PakTool.Core.ArchiveEntryDto entry)
    {
        try
        {
            SetBusy(true, "Adding material instance to Patch Pak...");
            await LetWebViewRenderAsync();
            var result = await AddMaterialInstancePatchItemAsync(entry);
            _activePage = "patch";
            SetStatus(result == PatchAddResult.AlreadyExists
                ? "Material instance is already in the Patch Pak list."
                : $"Added material instance {entry.Name} to Patch Pak.");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            SetStatus("Add material instance failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<PatchAddResult> AddMaterialInstancePatchItemAsync(PakTool.Core.ArchiveEntryDto entry)
    {
        var existing = _patchItems.FirstOrDefault(item => item.SourcePath.Equals(entry.FullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _selectedPatchItemId = existing.Id;
            return PatchAddResult.AlreadyExists;
        }

        var workDirectory = CreatePatchItemWorkDirectory();
        try
        {
            var inputDirectory = System.IO.Path.Combine(workDirectory, "input");
            System.IO.Directory.CreateDirectory(inputDirectory);
            var rawFiles = await Task.Run(async () => await Session.ReadRelatedRawFilesAsync(entry.FullPath));
            string? localAssetPath = null;
            var originalFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pakPath, data) in rawFiles)
            {
                var localPath = System.IO.Path.Combine(inputDirectory, System.IO.Path.GetFileName(pakPath));
                await File.WriteAllBytesAsync(localPath, data);
                originalFiles[pakPath] = localPath;
                if (pakPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                    pakPath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                    localAssetPath = localPath;
            }

            if (localAssetPath is null)
                throw new InvalidOperationException("Resource package is missing its .uasset file.");

            var service = new MaterialInstanceParameterService();
            var parameters = await Task.Run(async () => await service.InspectAsync(
                localAssetPath,
                EngineVersion.VER_UE5_6,
                _usmapPath,
                CancellationToken.None));

            if (parameters.Scalars.Count == 0 && parameters.Vectors.Count == 0 && parameters.Textures.Count == 0)
                throw new InvalidOperationException("Material instance has no editable parameter overrides.");

            var relatedPaths = entry.RelatedPaths is { Count: > 0 } ? entry.RelatedPaths.ToArray() : [entry.FullPath];
            var item = new PatchPakItem(
                Guid.NewGuid().ToString("N"),
                "material",
                entry.FullPath,
                entry.Name,
                relatedPaths,
                workDirectory,
                originalFiles,
                parameters.ExportClass,
                parameters.Scalars.Count + parameters.Vectors.Count + parameters.Textures.Count,
                0,
                null)
            {
                MaterialParameters = parameters
            };
            _patchItems.Add(item);
            _selectedPatchItemId = item.Id;
            return PatchAddResult.Added;
        }
        catch
        {
            TryDeleteDirectory(workDirectory);
            throw;
        }
    }

    private async Task ReplacePatchItemTextureAsync(string itemId, string imagePath, string imageDisplayName)
    {
        var item = FindPatchItem(itemId);
        if (item is null)
        {
            SetStatus("请先选择替换项目。 ");
            return;
        }

        try
        {
            _selectedPatchItemId = item.Id;
            _activePage = "patch";
            item.Status = "Replacing";
            item.Error = null;
            item.PatchedFiles.Clear();
            item.ReplacementImagePath = null;
            item.ReplacementDisplayName = null;
            item.ReplacementPreviewDataUrl = null;
            SetBusy(true, "正在编码替换贴图...");
            await LetWebViewRenderAsync();

            var localAssetPath = item.OriginalAssetPath
                ?? throw new InvalidOperationException("Patch item is missing its original .uasset file.");
            var outputDirectory = System.IO.Path.Combine(item.WorkDirectory, "output");
            System.IO.Directory.CreateDirectory(outputDirectory);
            var outputAssetPath = System.IO.Path.Combine(
                outputDirectory,
                System.IO.Path.GetFileNameWithoutExtension(localAssetPath) + ".patched.uasset");

            var service = new TextureReplacementService();
            LogDecode($"Patch replace start: item={item.SourcePath}, format={item.Format}, size={item.Width}x{item.Height}, image={imageDisplayName}");
            var codecLibrary = ResolvePrismCodecsLibrary();
            LogDecode($"Patch replace codec selected: library={codecLibrary}, astcQuality=fast");
            var replacement = await Task.Run(async () => await service.ReplaceAsync(
                localAssetPath,
                imagePath,
                outputAssetPath,
                EngineVersion.VER_UE5_6,
                _usmapPath,
                new TextureCodecOptions(AstcQuality: "fast", NativeLibraryName: codecLibrary, Log: message => LogDecode("Texture codec: " + message)),
                CancellationToken.None,
                formatHint: item.Format));
            LogDecode($"Patch replace wrote: asset={replacement.AssetPath}, uexp={replacement.UexpPath}, ubulk={replacement.UbulkPath ?? "<none>"}");

            item.PatchedFiles = BuildModifiedPakFiles(item.RelatedPaths, item.SourcePath, replacement)
                .ToDictionary(file => file.PakPath, file => file.DiskPath, StringComparer.OrdinalIgnoreCase);
            if (item.PatchedFiles.Count == 0)
                throw new InvalidOperationException("Texture was patched, but Prism could not map the patched files back to Pak paths.");

            item.ReplacementImagePath = imagePath;
            item.ReplacementDisplayName = imageDisplayName;
            item.ReplacementPreviewDataUrl = TryEncodeFileAsDataUrl(imagePath);
            item.Status = "Replaced";
            item.Error = null;
            SetStatus($"已替换 {item.Name}，可以构建替换 Pak。 ");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            item.Status = "Failed";
            item.Error = ex.Message;
            LogDecode("Texture replacement failed: " + ex);
            SetStatus("贴图替换失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReplacePatchItemAudioAsync(string itemId, string audioPath, string audioDisplayName)
    {
        var item = FindPatchItem(itemId);
        if (item is null)
        {
            SetStatus("Select a Patch item first.");
            return;
        }

        try
        {
            _selectedPatchItemId = item.Id;
            _activePage = "patch";
            item.Status = "Replacing";
            item.Error = null;
            item.PatchedFiles.Clear();
            item.ReplacementImagePath = null;
            item.ReplacementDisplayName = null;
            item.ReplacementPreviewDataUrl = null;
            SetBusy(true, "Replacing audio payload...");
            await LetWebViewRenderAsync();

            var payload = await Task.Run(async () => await Session.ReadAudioPayloadAsync(item.SourcePath));
            var sourceFormat = NormalizeAudioFormat(payload.Format);
            var replacementFormat = NormalizeAudioFormat(System.IO.Path.GetExtension(audioDisplayName));
            if (!AudioFormatsAreCompatible(sourceFormat, replacementFormat))
            {
                throw new InvalidOperationException(
                    $"Replacement audio must use the same encoded format as the source ({sourceFormat.ToUpperInvariant()}). " +
                    "Prism does not transcode audio for replacement yet.");
            }

            var replacementBytes = await File.ReadAllBytesAsync(audioPath);
            var sourceIsDirectAudio = item.RelatedPaths.Count == 1 &&
                                      !item.RelatedPaths[0].EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                                      !item.RelatedPaths[0].EndsWith(".umap", StringComparison.OrdinalIgnoreCase);
            if (!sourceIsDirectAudio && replacementBytes.Length > payload.Data.Length)
            {
                throw new InvalidOperationException(
                    $"Replacement audio is larger than the original payload ({FormatSize(replacementBytes.Length)} > {FormatSize(payload.Data.Length)}). " +
                    "Use an equal-or-smaller same-format file for uasset audio replacement.");
            }

            var outputDirectory = System.IO.Path.Combine(item.WorkDirectory, "output");
            System.IO.Directory.CreateDirectory(outputDirectory);
            var patchedFiles = item.OriginalFiles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            if (sourceIsDirectAudio)
            {
                var sourcePakPath = item.RelatedPaths[0];
                var outputPath = System.IO.Path.Combine(outputDirectory, System.IO.Path.GetFileName(sourcePakPath));
                await File.WriteAllBytesAsync(outputPath, replacementBytes);
                patchedFiles[sourcePakPath] = outputPath;
            }
            else
            {
                var match = FindAudioPayloadLocation(item.OriginalFiles, payload.Data)
                    ?? throw new InvalidOperationException("Could not locate the original audio payload bytes in the package files.");

                var originalBytes = await File.ReadAllBytesAsync(match.LocalPath);
                Buffer.BlockCopy(replacementBytes, 0, originalBytes, match.Offset, replacementBytes.Length);
                if (replacementBytes.Length < payload.Data.Length)
                    Array.Clear(originalBytes, match.Offset + replacementBytes.Length, payload.Data.Length - replacementBytes.Length);

                var outputPath = System.IO.Path.Combine(
                    outputDirectory,
                    System.IO.Path.GetFileNameWithoutExtension(match.LocalPath) + ".patched" + System.IO.Path.GetExtension(match.LocalPath));
                await File.WriteAllBytesAsync(outputPath, originalBytes);
                patchedFiles[match.PakPath] = outputPath;
            }

            item.PatchedFiles = patchedFiles;
            item.ReplacementImagePath = audioPath;
            item.ReplacementDisplayName = audioDisplayName;
            item.ReplacementPreviewDataUrl = TryEncodeAudioFileAsDataUrl(audioPath);
            item.Status = "Replaced";
            item.Error = null;

            var paddingNote = !sourceIsDirectAudio && replacementBytes.Length < payload.Data.Length
                ? $" Padded {FormatSize(payload.Data.Length - replacementBytes.Length)}."
                : string.Empty;
            SetStatus($"Replaced audio {item.Name} ({sourceFormat.ToUpperInvariant()}, {FormatSize(replacementBytes.Length)}).{paddingNote} Build Patch Pak when ready.");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            item.Status = "Failed";
            item.Error = ex.Message;
            LogDecode("Audio replacement failed: " + ex);
            SetStatus("Audio replacement failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task UpdatePatchLocresEntryAsync(string? itemId, int index, string? text)
    {
        var item = FindPatchItem(itemId);
        if (item is null)
        {
            SetStatus("请先选择本地化替换项目。 ");
            return;
        }

        if (!item.Kind.Equals("locres", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("选择的项目不是本地化资源。 ");
            return;
        }

        if (index < 0 || index >= item.LocresEntries.Count)
        {
            SetStatus("未找到该本地化条目。 ");
            return;
        }

        try
        {
            _selectedPatchItemId = item.Id;
            _activePage = "patch";
            item.Status = "Editing";
            item.Error = null;
            SetBusy(true, "正在更新本地化资源...");
            await LetWebViewRenderAsync();

            item.LocresEntries[index] = item.LocresEntries[index] with { Text = text ?? string.Empty };
            var originalPath = item.OriginalFiles
                .OrderBy(pair => PackagePartOrder(pair.Key))
                .Select(pair => pair.Value)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("Patch item is missing its original locres file.");

            var outputDirectory = System.IO.Path.Combine(item.WorkDirectory, "output");
            System.IO.Directory.CreateDirectory(outputDirectory);
            var outputPath = System.IO.Path.Combine(
                outputDirectory,
                System.IO.Path.GetFileNameWithoutExtension(originalPath) + ".patched.locres");

            var originalBytes = await File.ReadAllBytesAsync(originalPath);
            var patchedBytes = PakTool.Core.LocresResourceCodec.ApplyTranslations(originalBytes, item.LocresEntries);
            await File.WriteAllBytesAsync(outputPath, patchedBytes);

            item.PatchedFiles = item.OriginalFiles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            item.PatchedFiles[item.SourcePath] = outputPath;
            item.Status = "Replaced";
            item.Error = null;
            item.ReplacementDisplayName = $"{item.LocresEntries.Count:N0} text entries edited";
            SetStatus($"已更新第 {index + 1:N0} 条本地化文本，可以构建替换 Pak。 ");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            item.Status = "Failed";
            item.Error = ex.Message;
            SetStatus("本地化更新失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task UpdatePatchMaterialParameterAsync(
        string? itemId,
        string? kind,
        int index,
        float? value,
        float? r,
        float? g,
        float? b,
        float? a,
        int rawIndex)
    {
        var item = FindPatchItem(itemId);
        if (item is null)
        {
            SetStatus("Select a material instance Patch item first.");
            return;
        }

        if (!item.Kind.Equals("material", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Selected Patch item is not a material instance.");
            return;
        }

        try
        {
            _selectedPatchItemId = item.Id;
            _activePage = "patch";
            item.Status = "Editing";
            item.Error = null;
            SetBusy(true, "Updating material parameter...");
            await LetWebViewRenderAsync();

            var localAssetPath = item.OriginalAssetPath
                ?? throw new InvalidOperationException("Patch item is missing its original .uasset file.");
            var outputDirectory = System.IO.Path.Combine(item.WorkDirectory, "output");
            System.IO.Directory.CreateDirectory(outputDirectory);
            var outputAssetPath = System.IO.Path.Combine(
                outputDirectory,
                System.IO.Path.GetFileNameWithoutExtension(localAssetPath) + ".patched.uasset");

            var service = new MaterialInstanceParameterService();
            var updates = BuildMaterialParameterUpdates(item.MaterialParameters, kind, index, value, r, g, b, a, rawIndex);
            var replacement = await Task.Run(async () => await service.ApplyAsync(
                localAssetPath,
                outputAssetPath,
                EngineVersion.VER_UE5_6,
                _usmapPath,
                updates,
                CancellationToken.None));

            item.MaterialParameters = replacement.Parameters;
            item.PatchedFiles = BuildModifiedPakFiles(item.RelatedPaths, item.SourcePath, replacement)
                .ToDictionary(file => file.PakPath, file => file.DiskPath, StringComparer.OrdinalIgnoreCase);
            if (item.PatchedFiles.Count == 0)
                throw new InvalidOperationException("Material instance was patched, but Prism could not map the patched files back to Pak paths.");

            item.Status = "Replaced";
            item.Error = null;
            item.ReplacementDisplayName = $"{updates.Count:N0} material parameter(s) edited";
            SetStatus($"Updated material parameter {index + 1:N0}; build Patch Pak when ready.");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            item.Status = "Failed";
            item.Error = ex.Message;
            LogDecode("Material parameter update failed: " + ex);
            SetStatus("Material parameter update failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static IReadOnlyList<MaterialInstanceParameterUpdate> BuildMaterialParameterUpdates(
        MaterialInstanceParameterSet? current,
        string? kind,
        int index,
        float? value,
        float? r,
        float? g,
        float? b,
        float? a,
        int rawIndex)
    {
        if (current is null)
            throw new InvalidOperationException("Material parameter state is missing.");
        if (string.IsNullOrWhiteSpace(kind))
            throw new InvalidOperationException("Material parameter kind is missing.");

        var updates = new List<MaterialInstanceParameterUpdate>();
        foreach (var scalar in current.Scalars)
            updates.Add(new MaterialInstanceParameterUpdate("scalar", scalar.Index, Value: scalar.Value));
        foreach (var vector in current.Vectors)
            updates.Add(new MaterialInstanceParameterUpdate("vector", vector.Index, R: vector.R, G: vector.G, B: vector.B, A: vector.A));
        foreach (var texture in current.Textures)
            updates.Add(new MaterialInstanceParameterUpdate("texture", texture.Index, RawIndex: texture.RawIndex));

        var normalizedKind = kind.Trim().ToLowerInvariant();
        for (var i = 0; i < updates.Count; i++)
        {
            var update = updates[i];
            if (!update.Kind.Equals(normalizedKind, StringComparison.OrdinalIgnoreCase) || update.Index != index)
                continue;

            updates[i] = normalizedKind switch
            {
                "scalar" => update with { Value = value ?? update.Value },
                "vector" => update with
                {
                    R = r ?? update.R,
                    G = g ?? update.G,
                    B = b ?? update.B,
                    A = a ?? update.A
                },
                "texture" => update with { RawIndex = rawIndex },
                _ => throw new InvalidOperationException($"Unsupported material parameter kind: {kind}")
            };
            return updates;
        }

        throw new InvalidOperationException("Material parameter was not found.");
    }

    private async Task BuildPatchPakToUriAsync(global::Android.Net.Uri uri, bool useOodleCompression)
    {
        if (_patchItems.Count == 0)
        {
            SetStatus("请先向替换 Pak 添加至少一个资源。 ");
            return;
        }

        string? tempPakPath = null;
        try
        {
            SetBusy(true, "正在构建替换 Pak...");
            await LetWebViewRenderAsync();

            var mapped = new Dictionary<string, ModifiedPakFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _patchItems)
            {
                foreach (var pair in item.OriginalFiles)
                {
                    if (File.Exists(pair.Value))
                        mapped[pair.Key] = new ModifiedPakFile(pair.Value, pair.Key);
                }
            }

            foreach (var item in _patchItems)
            {
                foreach (var pair in item.PatchedFiles)
                {
                    if (File.Exists(pair.Value))
                        mapped[pair.Key] = new ModifiedPakFile(pair.Value, pair.Key);
                }
            }

            var files = mapped.Values.OrderBy(file => file.PakPath, StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0)
                throw new InvalidOperationException("No files are available to pack.");

            tempPakPath = System.IO.Path.Combine(CacheDir!.AbsolutePath, $"prism-patched-{Guid.NewGuid():N}.pak");
            await Task.Run(() => ModifiedPakPackService.Pack(new ModifiedPakRequest(
                files,
                tempPakPath,
                UseCompression: useOodleCompression,
                Compression: PakCompression.Oodle)));

            var tempPakSize = new FileInfo(tempPakPath).Length;
            if (tempPakSize <= 0)
                throw new InvalidOperationException("Pak writer produced an empty output file.");

            SetBusy(true, "正在写入替换 Pak...");
            await LetWebViewRenderAsync();

            await using var input = File.OpenRead(tempPakPath);
            await using var output = ContentResolver!.OpenOutputStream(uri, "wt")
                ?? throw new InvalidOperationException("Could not open output document.");
            await input.CopyToAsync(output);
            await output.FlushAsync();

            var compressionLabel = useOodleCompression ? "，使用 Oodle 压缩" : "，未使用压缩";
            SetStatus($"已将 {_patchItems.Count} 个项目中的 {files.Length} 个文件打包{compressionLabel}：{FormatSize(tempPakSize)}。 ");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            LogDecode("Patch Pak build failed: " + ex);
            SetStatus("替换 Pak 构建失败：" + ex.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPakPath))
                TryDeleteFile(tempPakPath);
            SetBusy(false);
        }
    }

    private async Task<MergeInspection> InspectPakMergeAsync(string? aesKey)
    {
        if (string.IsNullOrWhiteSpace(_pakPath) || string.IsNullOrWhiteSpace(_mergePakPath))
            throw new InvalidOperationException("Both Pak files must be selected before merging.");

        _oodleStatus = EnsureBundledOodleInitialized("merge inspect");
        using var baseSession = new PakTool.Core.PakArchiveSession();
        using var mergeSession = new PakTool.Core.PakArchiveSession();
        await OpenStandalonePakSessionAsync(baseSession, _pakPath, aesKey);
        await OpenStandalonePakSessionAsync(mergeSession, _mergePakPath, aesKey);

        var basePaths = await baseSession.ListRawFilePathsAsync();
        var mergePaths = await mergeSession.ListRawFilePathsAsync();
        var conflicts = mergePaths.Count(path => basePaths.Contains(path));
        return new MergeInspection(basePaths.Count, mergePaths.Count, conflicts);
    }

    private async Task BuildMergedPakToUriAsync(global::Android.Net.Uri uri, bool replaceConflicts, bool useOodleCompression, string? aesKey)
    {
        if (string.IsNullOrWhiteSpace(_pakPath) || string.IsNullOrWhiteSpace(_mergePakPath))
        {
            SetStatus("请先选择两个 Pak。");
            return;
        }

        string? tempRoot = null;
        string? tempPakPath = null;
        try
        {
            _oodleStatus = EnsureBundledOodleInitialized("merge");
            tempRoot = System.IO.Path.Combine(CacheDir!.AbsolutePath, "prism-merge-" + Guid.NewGuid().ToString("N"));
            var baseDirectory = System.IO.Path.Combine(tempRoot, "base");
            var mergeDirectory = System.IO.Path.Combine(tempRoot, "merge");
            System.IO.Directory.CreateDirectory(tempRoot);

            using var baseSession = new PakTool.Core.PakArchiveSession();
            using var mergeSession = new PakTool.Core.PakArchiveSession();

            SetBusy(true, "正在打开第一个 Pak...");
            await LetWebViewRenderAsync();
            await Task.Run(async () => await OpenStandalonePakSessionAsync(baseSession, _pakPath, aesKey));

            SetBusy(true, "正在打开第二个 Pak...");
            await LetWebViewRenderAsync();
            await Task.Run(async () => await OpenStandalonePakSessionAsync(mergeSession, _mergePakPath, aesKey));

            var baseProgress = new Progress<PakTool.Core.PakRawFileCopyProgress>(progress =>
            {
                if (!string.IsNullOrWhiteSpace(progress.CurrentPath))
                    SetStatus($"正在复制第一个 Pak {progress.Completed + 1:N0}/{progress.Total:N0}: {ShortenPakPath(progress.CurrentPath)}");
            });
            var mergeProgress = new Progress<PakTool.Core.PakRawFileCopyProgress>(progress =>
            {
                if (!string.IsNullOrWhiteSpace(progress.CurrentPath))
                    SetStatus($"正在复制第二个 Pak {progress.Completed + 1:N0}/{progress.Total:N0}: {ShortenPakPath(progress.CurrentPath)}");
            });

            SetBusy(true, "正在复制第一个 Pak 文件...");
            await LetWebViewRenderAsync();
            var baseFiles = await Task.Run(async () => await baseSession.CopyAllRawFilesAsync(baseDirectory, baseProgress));

            SetBusy(true, "正在复制第二个 Pak 文件...");
            await LetWebViewRenderAsync();
            var mergeFiles = await Task.Run(async () => await mergeSession.CopyAllRawFilesAsync(mergeDirectory, mergeProgress));

            var mapped = baseFiles.ToDictionary(
                file => file.PakPath,
                file => new ModifiedPakFile(file.DiskPath, file.PakPath),
                StringComparer.OrdinalIgnoreCase);
            var conflicts = 0;
            var replaced = 0;
            var skipped = 0;
            var added = 0;
            foreach (var file in mergeFiles)
            {
                if (mapped.ContainsKey(file.PakPath))
                {
                    conflicts++;
                    if (!replaceConflicts)
                    {
                        skipped++;
                        continue;
                    }

                    replaced++;
                }
                else
                {
                    added++;
                }

                mapped[file.PakPath] = new ModifiedPakFile(file.DiskPath, file.PakPath);
            }

            var files = mapped.Values.OrderBy(file => file.PakPath, StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0)
                throw new InvalidOperationException("No files are available to pack.");

            SetBusy(true, $"正在打包合并 Pak（{files.Length:N0} 个文件）...");
            await LetWebViewRenderAsync();
            tempPakPath = System.IO.Path.Combine(CacheDir!.AbsolutePath, $"prism-merged-{Guid.NewGuid():N}.pak");
            await Task.Run(() => ModifiedPakPackService.Pack(new ModifiedPakRequest(
                files,
                tempPakPath,
                UseCompression: useOodleCompression,
                Compression: PakCompression.Oodle)));

            var tempPakSize = new FileInfo(tempPakPath).Length;
            if (tempPakSize <= 0)
                throw new InvalidOperationException("Pak writer produced an empty output file.");

            SetBusy(true, "正在写入合并 Pak...");
            await LetWebViewRenderAsync();
            await using var input = File.OpenRead(tempPakPath);
            await using var output = ContentResolver!.OpenOutputStream(uri, "wt")
                ?? throw new InvalidOperationException("Could not open output document.");
            await input.CopyToAsync(output);
            await output.FlushAsync();

            var compressionLabel = useOodleCompression ? "，使用 Oodle 压缩" : "，未使用压缩";
            SetStatus($"合并 Pak 完成：共 {files.Length:N0} 个文件，新增 {added:N0}，冲突 {conflicts:N0}，替换 {replaced:N0}，保留原文件 {skipped:N0}{compressionLabel}，大小 {FormatSize(tempPakSize)}。");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            LogDecode("Pak merge failed: " + ex);
            SetStatus("Pak 合并失败：" + ex.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPakPath))
                TryDeleteFile(tempPakPath);
            if (!string.IsNullOrWhiteSpace(tempRoot))
                TryDeleteDirectory(tempRoot);
            SetBusy(false);
        }
    }

    private async Task OpenStandalonePakSessionAsync(PakTool.Core.PakArchiveSession session, string pakPath, string? aesKey)
    {
        var options = new PakTool.Core.PakOpenOptions(
            [pakPath],
            aesKey,
            _usmapPath,
            DecodeLogger: LogDecode);
        await session.OpenAsync(options);
    }

    private async Task ExportLogToUriAsync(global::Android.Net.Uri uri)
    {
        try
        {
            SetBusy(true, "正在导出日志...");
            await LetWebViewRenderAsync();

            var logText = BuildExportLogText();
            await using var output = ContentResolver!.OpenOutputStream(uri, "wt")
                ?? throw new InvalidOperationException("Could not open log output document.");
            await using var writer = new StreamWriter(output, System.Text.Encoding.UTF8);
            await writer.WriteAsync(logText);
            await writer.FlushAsync();

            LogPerf("Diagnostic log exported.");
            SetStatus("日志已导出。 ");
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            SetStatus("日志导出失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private string BuildExportLogText()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Prism diagnostic log");
        builder.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
        builder.AppendLine("Package: " + (PackageName ?? "unknown"));
        builder.AppendLine("Version: " + GetAppVersionText());
        var device = global::Android.OS.Build.Manufacturer + " " + global::Android.OS.Build.Model;
        var androidVersion = global::Android.OS.Build.VERSION.Release + " (API " + (int)global::Android.OS.Build.VERSION.SdkInt + ")";
        builder.AppendLine("Device: " + device);
        builder.AppendLine("Android: " + androidVersion);
        builder.AppendLine("ABI: " + string.Join(", ", global::Android.OS.Build.SupportedAbis ?? []));
        builder.AppendLine("Pak: " + (_pakDisplayName ?? "<none>"));
        builder.AppendLine("Usmap: " + (_usmapDisplayName ?? "<none>"));
        builder.AppendLine("Oodle: " + _oodleStatus);
        builder.AppendLine("Status: " + _status);
        builder.AppendLine();
        builder.AppendLine("Diagnostics:");
        foreach (var line in SnapshotAllDiagnostics())
            builder.AppendLine(line);

        if (!string.IsNullOrWhiteSpace(_lastError))
        {
            builder.AppendLine();
            builder.AppendLine("Last error:");
            builder.AppendLine(_lastError);
        }

        return builder.ToString();
    }

    private string GetAppVersionText()
    {
        try
        {
            var packageInfo = PackageManager?.GetPackageInfo(PackageName!, 0);
            if (packageInfo is null)
                return "unknown";

            var versionName = string.IsNullOrWhiteSpace(packageInfo.VersionName) ? "unknown" : packageInfo.VersionName;
            return $"{versionName} ({packageInfo.LongVersionCode})";
        }
        catch
        {
            return "unknown";
        }
    }

    private static IEnumerable<ModifiedPakFile> BuildModifiedPakFiles(
        IReadOnlyList<string> relatedPathsInput,
        string sourcePath,
        TextureReplacementResult replacement)
    {
        var mapped = new Dictionary<string, ModifiedPakFile>(StringComparer.OrdinalIgnoreCase);
        var relatedPaths = (relatedPathsInput.Count > 0 ? relatedPathsInput : [sourcePath])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var pakPath in relatedPaths)
        {
            string? diskPath = null;
            if (pakPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                pakPath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            {
                diskPath = replacement.AssetPath;
            }
            else if (pakPath.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
            {
                diskPath = replacement.UexpPath;
            }
            else if (pakPath.EndsWith(".ubulk", StringComparison.OrdinalIgnoreCase))
            {
                diskPath = replacement.UbulkPath;
            }

            if (!string.IsNullOrWhiteSpace(diskPath) && File.Exists(diskPath))
                mapped[pakPath] = new ModifiedPakFile(diskPath, pakPath);
        }

        var assetPakPath = relatedPaths.FirstOrDefault(path =>
            path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(assetPakPath))
            assetPakPath = sourcePath;

        AddMappedFile(mapped, replacement.AssetPath, assetPakPath);
        AddMappedFile(mapped, replacement.UexpPath, System.IO.Path.ChangeExtension(assetPakPath, ".uexp"));
        if (!string.IsNullOrWhiteSpace(replacement.UbulkPath))
            AddMappedFile(mapped, replacement.UbulkPath, System.IO.Path.ChangeExtension(assetPakPath, ".ubulk"));

        foreach (var file in mapped.Values.OrderBy(file => PackagePartOrder(file.PakPath)))
            yield return file;
    }

    private static IEnumerable<ModifiedPakFile> BuildModifiedPakFiles(
        IReadOnlyList<string> relatedPathsInput,
        string sourcePath,
        MaterialInstanceParameterPatchResult replacement)
    {
        var mapped = new Dictionary<string, ModifiedPakFile>(StringComparer.OrdinalIgnoreCase);
        var relatedPaths = (relatedPathsInput.Count > 0 ? relatedPathsInput : [sourcePath])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var pakPath in relatedPaths)
        {
            string? diskPath = null;
            if (pakPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                pakPath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            {
                diskPath = replacement.AssetPath;
            }
            else if (pakPath.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
            {
                diskPath = replacement.UexpPath;
            }

            if (!string.IsNullOrWhiteSpace(diskPath) && File.Exists(diskPath))
                mapped[pakPath] = new ModifiedPakFile(diskPath, pakPath);
        }

        var assetPakPath = relatedPaths.FirstOrDefault(path =>
            path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(assetPakPath))
            assetPakPath = sourcePath;

        AddMappedFile(mapped, replacement.AssetPath, assetPakPath);
        AddMappedFile(mapped, replacement.UexpPath, System.IO.Path.ChangeExtension(assetPakPath, ".uexp"));

        foreach (var file in mapped.Values.OrderBy(file => PackagePartOrder(file.PakPath)))
            yield return file;
    }

    private static void AddMappedFile(Dictionary<string, ModifiedPakFile> mapped, string? diskPath, string? pakPath)
    {
        if (string.IsNullOrWhiteSpace(diskPath) ||
            string.IsNullOrWhiteSpace(pakPath) ||
            !File.Exists(diskPath))
        {
            return;
        }

        mapped[pakPath] = new ModifiedPakFile(diskPath, pakPath);
    }

    private static AudioPayloadLocation? FindAudioPayloadLocation(
        IReadOnlyDictionary<string, string> originalFiles,
        byte[] payload)
    {
        if (payload.Length == 0)
            return null;

        foreach (var pair in originalFiles.OrderBy(file => PackagePartOrder(file.Key)))
        {
            if (!File.Exists(pair.Value))
                continue;

            var bytes = File.ReadAllBytes(pair.Value);
            var offset = IndexOfSequence(bytes, payload);
            if (offset >= 0)
                return new AudioPayloadLocation(pair.Key, pair.Value, offset);
        }

        return null;
    }

    private static int IndexOfSequence(byte[] source, byte[] pattern)
    {
        if (pattern.Length == 0)
            return 0;
        if (pattern.Length > source.Length)
            return -1;

        var first = pattern[0];
        var max = source.Length - pattern.Length;
        for (var i = 0; i <= max; i++)
        {
            if (source[i] != first)
                continue;

            if (source.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                return i;
        }

        return -1;
    }

    private static string NormalizeAudioFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim().TrimStart('.').ToLowerInvariant();
        return normalized switch
        {
            "oga" => "ogg",
            "wave" => "wav",
            "bink" => "binka",
            _ => normalized
        };
    }

    private static bool AudioFormatsAreCompatible(string sourceFormat, string replacementFormat)
    {
        if (string.IsNullOrWhiteSpace(sourceFormat) || string.IsNullOrWhiteSpace(replacementFormat))
            return false;

        return sourceFormat.Equals(replacementFormat, StringComparison.OrdinalIgnoreCase);
    }

    private static int PackagePartOrder(string path)
    {
        var extension = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "uasset" or "umap" => 0,
            "uexp" => 1,
            "ubulk" => 2,
            _ => 3
        };
    }

    private string CreateReplacementWorkDirectory()
    {
        var directory = System.IO.Path.Combine(CacheDir!.AbsolutePath, "prism-replacements", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    private string CreatePatchItemWorkDirectory()
    {
        var directory = System.IO.Path.Combine(CacheDir!.AbsolutePath, "prism-patch-items", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    private string ResolvePrismCodecsLibrary()
    {
        var nativeDir = ApplicationInfo?.NativeLibraryDir;
        LogPerf($"Prism codecs resolve check. nativeDir={nativeDir ?? "<null>"}");
        if (!string.IsNullOrWhiteSpace(nativeDir))
        {
            LogNativeCodecFiles(nativeDir);
            var path = Path.Combine(nativeDir, "libprism_codecs.so");
            if (File.Exists(path))
            {
                return path;
            }

            LogPerf("Bundled Prism codecs native file was not found at " + path, global::Android.Util.LogPriority.Warn);
        }

        return "prism_codecs";
    }

    private static void LogNativeCodecFiles(string nativeDir)
    {
        try
        {
            if (!Directory.Exists(nativeDir))
            {
                LogPerf("Native library directory does not exist: " + nativeDir, global::Android.Util.LogPriority.Warn);
                return;
            }

            var files = Directory.EnumerateFiles(nativeDir)
                .Select(Path.GetFileName)
                .Where(name =>
                    name?.Contains("prism", StringComparison.OrdinalIgnoreCase) == true ||
                    name?.Contains("c++", StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            LogPerf(files.Length == 0
                ? "Native library directory has no Prism codec files."
                : "Native Prism codec files: " + string.Join(", ", files));
        }
        catch (Exception ex)
        {
            LogPerf("Native codec directory scan failed: " + ex.Message, global::Android.Util.LogPriority.Warn);
        }
    }

    private PatchPakItem? FindPatchItem(string? id)
    {
        return string.IsNullOrWhiteSpace(id)
            ? null
            : _patchItems.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private PatchPakItem? SelectedPatchItem()
    {
        return FindPatchItem(_selectedPatchItemId) ?? _patchItems.FirstOrDefault();
    }

    private void SelectPatchItem(string? id)
    {
        var item = FindPatchItem(id);
        if (item is null)
        {
            SetStatus("未找到替换项目。 ");
            return;
        }

        _selectedPatchItemId = item.Id;
        _activePage = "patch";
        PushState();
    }

    private void RemovePatchItem(string? id)
    {
        var item = FindPatchItem(id);
        if (item is null)
        {
            SetStatus("未找到替换项目。 ");
            return;
        }

        _patchItems.Remove(item);
        TryDeleteDirectory(item.WorkDirectory);
        if (string.Equals(_selectedPatchItemId, item.Id, StringComparison.OrdinalIgnoreCase))
            _selectedPatchItemId = _patchItems.FirstOrDefault()?.Id;

        _activePage = "patch";
        SetStatus($"已从替换 Pak 移除 {item.Name}。 ");
    }

    private static string? TryEncodeFileAsDataUrl(string imagePath)
    {
        try
        {
            var mimeType = System.IO.Path.GetExtension(imagePath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ => "image/png"
            };

            return $"data:{mimeType};base64," + Convert.ToBase64String(File.ReadAllBytes(imagePath));
        }
        catch
        {
            return null;
        }
    }

    private static string? TryEncodeAudioFileAsDataUrl(string audioPath)
    {
        try
        {
            var info = new FileInfo(audioPath);
            if (!info.Exists || info.Length > 16L * 1024 * 1024)
                return null;

            var mimeType = System.IO.Path.GetExtension(audioPath).ToLowerInvariant() switch
            {
                ".ogg" or ".oga" or ".opus" => "audio/ogg",
                ".wav" => "audio/wav",
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".aac" => "audio/aac",
                ".flac" => "audio/flac",
                _ => "application/octet-stream"
            };

            if (!mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                return null;

            return $"data:{mimeType};base64," + Convert.ToBase64String(File.ReadAllBytes(audioPath));
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> CopyDocumentToCacheAsync(global::Android.Net.Uri uri, string extension, string statusPrefix)
    {
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var outputPath = System.IO.Path.Combine(GetImportCacheDirectory(), fileName);

        SetBusy(true, statusPrefix + "...");
        await LetWebViewRenderAsync();

        try
        {
            var totalBytes = GetDocumentSize(uri);
            var copiedBytes = 0L;
            var buffer = new byte[1024 * 1024];
            var progressClock = System.Diagnostics.Stopwatch.StartNew();

            await using var input = ContentResolver!.OpenInputStream(uri) ?? throw new InvalidOperationException("Could not open selected document.");
            await using var output = File.Create(outputPath);

            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length));
                if (read <= 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, read));
                copiedBytes += read;

                if (progressClock.ElapsedMilliseconds < 250)
                    continue;

                progressClock.Restart();
                SetBusy(true, totalBytes > 0
                    ? $"{statusPrefix} {Math.Min(99, copiedBytes * 100 / totalBytes)}%"
                    : $"{statusPrefix} {FormatBytes(copiedBytes)}");
            }

            return outputPath;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private string GetImportCacheDirectory()
    {
        var directory = System.IO.Path.Combine(CacheDir!.AbsolutePath, "prism-imports");
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    private void CloseCurrentArchive()
    {
        _indexCancellation?.Cancel();
        _indexCancellation?.Dispose();
        _indexCancellation = null;
        _session?.Dispose();
        _session = null;
        _currentFolder = string.Empty;
        _entries = [];
        ClearEntryThumbnails();
        ClearPatchWorkspace(pushState: false);
        ClearSelection(pushState: false);
    }

    private void CleanupImportCache(params string?[] keepPaths)
    {
        try
        {
            var keep = keepPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => System.IO.Path.GetFullPath(path!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            DeleteCachedImportsInDirectory(CacheDir!.AbsolutePath, keep, includeLegacyRootFiles: true);

            var importDirectory = GetImportCacheDirectory();
            DeleteCachedImportsInDirectory(importDirectory, keep, includeLegacyRootFiles: false);
        }
        catch
        {
            // Cache cleanup is best-effort; failing to delete should not block the app.
        }
    }

    private static void DeleteCachedImportsInDirectory(string directory, ISet<string> keep, bool includeLegacyRootFiles)
    {
        if (!System.IO.Directory.Exists(directory))
            return;

        var files = includeLegacyRootFiles
            ? System.IO.Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase))
            : System.IO.Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            var fullPath = System.IO.Path.GetFullPath(file);
            if (keep.Contains(fullPath))
                continue;

            TryDeleteFile(fullPath);
        }
    }

    private static void DeleteCachedImport(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            TryDeleteFile(path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The provider may still be releasing a handle; the next startup cleanup will try again.
        }
    }

    private long GetDocumentSize(global::Android.Net.Uri uri)
    {
        try
        {
            using var cursor = ContentResolver!.Query(uri, null, null, null, null);
            if (cursor is null || !cursor.MoveToFirst())
                return -1;

            var index = cursor.GetColumnIndex(global::Android.Provider.IOpenableColumns.Size);
            return index >= 0 && !cursor.IsNull(index) ? cursor.GetLong(index) : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double) bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private void ClearSelection(bool pushState = true)
    {
        _selectedEntry = null;
        _selectedSummary = null;
        _previewDataUrl = null;
        _previewTitle = null;
        _selectedPreview = null;
        _selectedPreviewResourceUrl = null;
        _previewBlobStore.Clear();
        if (pushState)
            PushState();
    }

    private void ClearPatchWorkspace(bool pushState = true)
    {
        foreach (var item in _patchItems)
            TryDeleteDirectory(item.WorkDirectory);

        _patchItems.Clear();
        _selectedPatchItemId = null;
        _pendingReplacementPatchItemId = null;
        if (pushState)
            PushState();
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (System.IO.Directory.Exists(path))
                System.IO.Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Cache cleanup is best-effort; stale patch files are harmless.
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (status is not null)
            _status = status;
        PushState();
    }

    private void SetStatus(string text)
    {
        _status = text;
        PushState();
    }

    private static async Task LetWebViewRenderAsync()
    {
        await Task.Yield();
        await Task.Delay(120);
    }

    private static void StartCompressionWarmup()
    {
        if (System.Threading.Interlocked.Exchange(ref _compressionWarmupStarted, 1) != 0)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                var source = new byte[] { 80, 114, 105, 115, 109, 32, 90, 108, 105, 98, 32, 87, 97, 114, 109, 117, 112 };
                using var compressedStream = new MemoryStream();
                using (var zlib = new System.IO.Compression.ZLibStream(
                           compressedStream,
                           System.IO.Compression.CompressionLevel.Fastest,
                           leaveOpen: true))
                {
                    zlib.Write(source, 0, source.Length);
                }

                var compressed = compressedStream.ToArray();
                var output = new byte[source.Length];
                CUE4Parse.Compression.Compression.Decompress(
                    compressed,
                    0,
                    compressed.Length,
                    output,
                    0,
                    output.Length,
                    CUE4Parse.Compression.CompressionMethod.Zlib);

                LogPerf("Compression warmup completed.");
            }
            catch (Exception ex)
            {
                LogPerf("Compression warmup failed: " + ex.Message, global::Android.Util.LogPriority.Warn);
            }
        });
    }

    private string EnsureBundledOodleInitialized(string reason)
    {
        if (CUE4Parse.Compression.OodleHelper.Instance is not null)
        {
            var already = $"Oodle native already initialized ({reason}).";
            LogPerf(already);
            return already;
        }

        try
        {
            var nativeDir = ApplicationInfo?.NativeLibraryDir;
            LogPerf($"Oodle initialize check ({reason}). nativeDir={nativeDir ?? "<null>"}");
            if (!string.IsNullOrWhiteSpace(nativeDir))
            {
                LogOodleFiles(nativeDir);
                var oodlePath = Path.Combine(nativeDir, "liboodle-data-shared.so");
                if (File.Exists(oodlePath))
                {
                    if (TryInitializeOodleFromPath(oodlePath))
                    {
                        var loaded = "Oodle native initialized from bundled native library.";
                        LogDecode(loaded);
                        return loaded;
                    }

                    LogPerf("Bundled Oodle native file was found but could not be loaded: " + oodlePath, global::Android.Util.LogPriority.Warn);
                }
                else
                {
                    LogPerf("Bundled Oodle native file was not found at " + oodlePath, global::Android.Util.LogPriority.Warn);
                }
            }

            if (TryInitializeOodleByName("oodle-data-shared") ||
                TryInitializeOodleByName("liboodle-data-shared.so"))
            {
                var loaded = "Oodle native initialized by library name.";
                LogDecode(loaded);
                return loaded;
            }

            var unavailable = "Bundled Oodle native library is not available; Oodle-compressed assets cannot be decoded.";
            LogPerf(unavailable, global::Android.Util.LogPriority.Warn);
            return unavailable;
        }
        catch (Exception ex)
        {
            var failed = "Oodle native initialization failed: " + ex.Message;
            LogPerf(failed, global::Android.Util.LogPriority.Warn);
            LogDecode(failed + Environment.NewLine + ex);
            return failed;
        }
    }

    private static void LogOodleFiles(string nativeDir)
    {
        try
        {
            if (!Directory.Exists(nativeDir))
            {
                LogPerf("Native library directory does not exist: " + nativeDir, global::Android.Util.LogPriority.Warn);
                return;
            }

            var files = Directory.EnumerateFiles(nativeDir)
                .Select(Path.GetFileName)
                .Where(name => name?.Contains("oodle", StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            LogPerf(files.Length == 0
                ? "Native library directory has no Oodle files."
                : "Native Oodle files: " + string.Join(", ", files));
        }
        catch (Exception ex)
        {
            LogPerf("Native library directory scan failed: " + ex.Message, global::Android.Util.LogPriority.Warn);
        }
    }

    private static bool TryInitializeOodleFromPath(string oodlePath)
    {
        try
        {
            CUE4Parse.Compression.OodleHelper.Initialize(new OodleDotNet.Oodle(oodlePath));
            LogPerf("Oodle native initialized from " + oodlePath);
            return true;
        }
        catch (Exception ex)
        {
            LogPerf("Oodle native path load failed: " + ex.Message, global::Android.Util.LogPriority.Warn);
            LogDecode("Oodle native path load failed: " + ex);
            return false;
        }
    }

    private static bool TryInitializeOodleByName(string libraryName)
    {
        nint handle = 0;
        try
        {
            if (!System.Runtime.InteropServices.NativeLibrary.TryLoad(
                    libraryName,
                    typeof(MainActivity).Assembly,
                    System.Runtime.InteropServices.DllImportSearchPath.AssemblyDirectory,
                    out handle) &&
                !System.Runtime.InteropServices.NativeLibrary.TryLoad(libraryName, out handle))
            {
                return false;
            }

            CUE4Parse.Compression.OodleHelper.Initialize(new OodleDotNet.Oodle(handle));
            LogPerf("Oodle native initialized by library name " + libraryName);
            return true;
        }
        catch (Exception ex)
        {
            if (handle != 0)
                System.Runtime.InteropServices.NativeLibrary.Free(handle);

            LogPerf($"Oodle native name load failed for {libraryName}: {ex.Message}", global::Android.Util.LogPriority.Warn);
            LogDecode($"Oodle native name load failed for {libraryName}: {ex}");
            return false;
        }
    }

    private void StartDirectoryIndexBuild(PakTool.Core.PakArchiveSession session, int generation, int fileCount)
    {
        var token = _indexCancellation?.Token ?? System.Threading.CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                SetStatusFromAnyThread($"Indexing {fileCount} file(s) in background...");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var result = await session.BuildDirectoryIndexAsync(token);
                clock.Stop();

                if (token.IsCancellationRequested || generation != _openGeneration)
                    return;

                LogPerf($"Directory index: {FormatDuration(clock.Elapsed)}, {result.FolderCount} folder(s), {result.EntryCount} visible item(s)");
                SetStatusFromAnyThread($"Indexed {result.FolderCount} folder(s) in {FormatDuration(clock.Elapsed)}.");
            }
            catch (OperationCanceledException)
            {
                // A newer pak was opened before this background index finished.
            }
            catch (Exception ex)
            {
                LogPerf("Directory index failed: " + ex.Message, global::Android.Util.LogPriority.Warn);
                if (generation == _openGeneration)
                    SetStatusFromAnyThread("Background index failed: " + ex.Message);
            }
        }, token);
    }

    private void SetStatusFromAnyThread(string text)
    {
        RunOnUiThread(() => SetStatus(text));
    }

    private static void LogOpenTimings(PakTool.Core.PakOpenResult result, TimeSpan wallClock)
    {
        var timings = result.Timings.Count == 0
            ? string.Empty
            : " [" + string.Join(", ", result.Timings.Select(timing => $"{timing.Name}={timing.Milliseconds}ms")) + "]";
        LogPerf($"Open pak wall={FormatDuration(wallClock)}, mounted={result.MountedArchiveCount}, files={result.FileCount}, requiredKeys={result.RequiredKeyCount}{timings}");
    }

    private static void LogPerf(string message, global::Android.Util.LogPriority priority = global::Android.Util.LogPriority.Info)
    {
        const string tag = "PrismPerf";
        AddDiagnostic("PERF", message);
        Console.WriteLine($"{tag}: {message}");
        Java.Lang.JavaSystem.Err.Println($"{tag}: {message}");
        switch (priority)
        {
            case global::Android.Util.LogPriority.Warn:
                global::Android.Util.Log.Warn(tag, message);
                break;
            case global::Android.Util.LogPriority.Error:
                global::Android.Util.Log.Error(tag, message);
                break;
            default:
                global::Android.Util.Log.Info(tag, message);
                break;
        }
    }

    private static void LogDecode(string message)
    {
        AddDiagnostic("DECODE", message);
        Console.WriteLine("PrismDecode: " + message);
        Java.Lang.JavaSystem.Err.Println("PrismDecode: " + message);
        global::Android.Util.Log.Info("PrismDecode", message);
    }

    private static void AddDiagnostic(string channel, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {channel}: {message}";
        lock (DiagnosticsLock)
        {
            Diagnostics.Add(line);
            if (Diagnostics.Count > MaxDiagnosticLines)
                Diagnostics.RemoveRange(0, Diagnostics.Count - MaxDiagnosticLines);
        }
    }

    private static string[] SnapshotDiagnostics()
    {
        lock (DiagnosticsLock)
        {
            return Diagnostics
                .Skip(Math.Max(0, Diagnostics.Count - UiDiagnosticLines))
                .ToArray();
        }
    }

    private static string[] SnapshotAllDiagnostics()
    {
        lock (DiagnosticsLock)
        {
            return Diagnostics.ToArray();
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds >= 1
            ? $"{duration.TotalSeconds:0.0}s"
            : $"{duration.TotalMilliseconds:0}ms";
    }

    private PakTool.Core.PakArchiveSession Session => _session ??= new PakTool.Core.PakArchiveSession();

    private void TryPersistUriPermission(global::Android.Content.Intent data)
    {
        try
        {
            var flags = data.Flags & (global::Android.Content.ActivityFlags.GrantReadUriPermission |
                                      global::Android.Content.ActivityFlags.GrantWriteUriPermission);
            ContentResolver!.TakePersistableUriPermission(data.Data!, flags);
        }
        catch
        {
            // Some file providers do not grant persistable permissions; exporting can still work for this session.
        }
    }

    private global::Android.Net.Uri CreateDocument(global::Android.Net.Uri treeUri, string fileName, string mimeType)
    {
        var treeDocumentId = global::Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri);
        var parentUri = global::Android.Provider.DocumentsContract.BuildDocumentUriUsingTree(treeUri, treeDocumentId)
            ?? throw new InvalidOperationException("Could not resolve the export directory.");
        var documentUri = global::Android.Provider.DocumentsContract.CreateDocument(
            ContentResolver!,
            parentUri,
            mimeType,
            fileName);

        return documentUri ?? throw new InvalidOperationException("Could not create output document.");
    }

    private string GetDisplayName(global::Android.Net.Uri uri, string fallback)
    {
        try
        {
            using var cursor = ContentResolver!.Query(uri, null, null, null, null);
            if (cursor is null || !cursor.MoveToFirst())
                return fallback;

            var index = cursor.GetColumnIndex(global::Android.Provider.IOpenableColumns.DisplayName);
            return index >= 0 ? cursor.GetString(index) ?? fallback : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private void PushState()
    {
        if (!_webReady || _webView is null)
            return;

        var stateJson = JsonSerializer.Serialize(CreateState(), JsonOptions);
        RunOnUiThread(() => _webView?.EvaluateJavascript($"window.PakToolUI.applyState({stateJson});", null));
    }

    private object CreateState()
    {
        var selectedPatchItem = SelectedPatchItem();
        return new
        {
            status = _status,
            busy = _busy,
            activePage = _activePage,
            currentPath = string.IsNullOrEmpty(_currentFolder) ? "/" : "/" + _currentFolder,
            pakName = _pakDisplayName ?? "未选择 Pak",
            mergePakName = _mergePakDisplayName ?? "No merge pak",
            usmapName = _usmapDisplayName ?? "未选择 Usmap",
            selectedSummary = _selectedSummary,
            selectedEntry = _selectedEntry is null ? null : new
            {
                name = _selectedEntry.Name,
                path = _selectedEntry.FullPath,
                extension = _selectedEntry.Extension,
                isDirectory = _selectedEntry.IsDirectory,
                isAssetPackage = _selectedEntry.IsAssetPackage,
                sizeBytes = _selectedEntry.Size
            },
            canExportRaw = _selectedEntry is { IsDirectory: false },
            canExportPng = IsSelectedTexturePreview(),
            canExportTyped = CanExportSelectedTypedPreview(),
            canExportFolder = _session?.IsOpen == true,
            canAddFolderToPatchPak = _session?.IsOpen == true,
            canMergePak = !string.IsNullOrWhiteSpace(_pakPath) && !string.IsNullOrWhiteSpace(_mergePakPath),
            exportLabel = GetTypedExportLabel(),
            canAddToPatchPak = CanAddSelectedToPatchPak(),
            preview = CreatePreviewState(),
            previewDataUrl = (string?)null,
            previewTitle = _previewTitle,
            selectedPatchItemId = selectedPatchItem?.Id,
            canBuildPatchPak = _patchItems.Count > 0,
            patchItemCount = _patchItems.Count,
            replacedPatchItemCount = _patchItems.Count(item => item.Status == "Replaced"),
            patchItems = _patchItems.Select(item => new
            {
                id = item.Id,
                kind = item.Kind,
                sourcePath = item.SourcePath,
                name = item.Name,
                status = item.Status,
                error = item.Error,
                format = item.Format,
                width = item.Width,
                height = item.Height,
                sizeLabel = item.Kind == "audio"
                    ? FormatSize(item.Width)
                    : item.Kind == "locres"
                        ? $"{item.Width:N0} 条 / {item.Height:N0} 个命名空间"
                        : item.Kind == "raw-folder"
                            ? $"{item.Width:N0} files"
                        : $"{item.Width}x{item.Height}",
                relatedCount = item.RelatedPaths.Count,
                replacementName = item.ReplacementDisplayName,
                originalPreviewDataUrl = item.OriginalPreviewDataUrl,
                replacementPreviewDataUrl = item.ReplacementPreviewDataUrl,
                locresEntries = item.LocresEntries.Select(entry => new
                {
                    index = entry.Index,
                    ns = entry.Namespace,
                    key = entry.Key,
                    text = entry.Text
                }).ToArray(),
                materialParameters = item.MaterialParameters is null ? null : new
                {
                    scalars = item.MaterialParameters.Scalars.Select(parameter => new
                    {
                        index = parameter.Index,
                        name = parameter.Name,
                        value = parameter.Value
                    }).ToArray(),
                    vectors = item.MaterialParameters.Vectors.Select(parameter => new
                    {
                        index = parameter.Index,
                        name = parameter.Name,
                        r = parameter.R,
                        g = parameter.G,
                        b = parameter.B,
                        a = parameter.A
                    }).ToArray(),
                    textures = item.MaterialParameters.Textures.Select(parameter => new
                    {
                        index = parameter.Index,
                        name = parameter.Name,
                        rawIndex = parameter.RawIndex,
                        textureName = parameter.TextureName,
                        texturePath = parameter.TexturePath
                    }).ToArray(),
                    textureOptions = item.MaterialParameters.TextureOptions.Select(option => new
                    {
                        rawIndex = option.RawIndex,
                        name = option.Name,
                        path = option.Path
                    }).ToArray()
                }
            }).ToArray(),
            oodleStatus = _oodleStatus,
            diagnostics = SnapshotDiagnostics(),
            entries = _entries.Select((entry, index) => new
            {
                index,
                name = entry.Name,
                path = entry.FullPath,
                size = FormatSize(entry.Size),
                sizeBytes = entry.Size,
                extension = entry.Extension,
                isDirectory = entry.IsDirectory,
                isAssetPackage = entry.IsAssetPackage,
                relatedCount = entry.RelatedPaths?.Count ?? 1,
                kind = GuessEntryKind(entry),
                thumbnailUrl = _entryThumbnails.TryGetValue(entry.FullPath, out var thumbnailUrl) ? thumbnailUrl : null
            }).ToArray()
        };
    }

    private string GuessEntryKind(PakTool.Core.ArchiveEntryDto entry)
    {
        if (entry.IsDirectory)
            return "Folder";

        var extension = entry.Extension.TrimStart('.').ToLowerInvariant();
        if (extension is "png" or "jpg" or "jpeg" or "webp")
            return "Image";
        if (extension is "ogg" or "oga" or "wav" or "mp3" or "m4a" or "aac" or "flac" or "opus" or "wem" or "binka" or "rada" or "at9")
            return "Audio";
        if (extension is "mp4" or "m4v" or "webm" or "mov" or "ogv")
            return "Video";

        if (!entry.IsAssetPackage)
            return string.IsNullOrWhiteSpace(extension) ? "File" : extension.ToUpperInvariant();

        if (_selectedEntry?.FullPath.Equals(entry.FullPath, StringComparison.OrdinalIgnoreCase) == true &&
            _selectedPreview is not null)
        {
            return _selectedPreview.Kind switch
            {
                "texture" => "Texture",
                "audio" => "Audio",
                "video" => "Video",
                "model" => "Model",
                "material" => "Material",
                "blueprint" => "Blueprint",
                "locres" => "Locres",
                _ => "UE"
            };
        }

        if (extension == "locres")
            return "Locres";

        var path = entry.FullPath.Replace('\\', '/');
        if (path.Contains("/Audio/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Sound", StringComparison.OrdinalIgnoreCase))
            return "Audio";
        if (path.Contains("/Movies/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Media/", StringComparison.OrdinalIgnoreCase))
            return "Video";
        if (path.Contains("/Blueprint", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/BP_", StringComparison.OrdinalIgnoreCase))
            return "Blueprint";
        if (path.Contains("/Material", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/MI_", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/M_", StringComparison.OrdinalIgnoreCase))
            return "Material";
        if (path.Contains("/Mesh", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Model", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/SK_", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/SM_", StringComparison.OrdinalIgnoreCase))
            return "Model";
        if (path.Contains("/Texture", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/T_", StringComparison.OrdinalIgnoreCase))
            return "Texture";

        return "UE";
    }

    private bool IsSelectedTexturePreview()
    {
        return _selectedPreview?.Kind.Equals("texture", StringComparison.OrdinalIgnoreCase) == true &&
               !string.IsNullOrWhiteSpace(_previewDataUrl);
    }

    private bool CanAddSelectedToPatchPak()
    {
        if (_selectedEntry is not { IsDirectory: false } || _selectedPreview is null)
            return false;

        if (_selectedPreview.Kind.Equals("texture", StringComparison.OrdinalIgnoreCase))
            return _selectedEntry.IsAssetPackage && !string.IsNullOrWhiteSpace(_previewDataUrl);

        return _selectedPreview.Kind.Equals("locres", StringComparison.OrdinalIgnoreCase) ||
               (_selectedEntry.IsAssetPackage && _selectedPreview.Kind.Equals("material", StringComparison.OrdinalIgnoreCase));
    }

    private bool CanExportSelectedTypedPreview()
    {
        if (_selectedEntry is not { IsDirectory: false } || _selectedPreview is null)
            return false;

        return _selectedPreview.Kind.Equals("texture", StringComparison.OrdinalIgnoreCase) ||
               _selectedPreview.Kind.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
               _selectedPreview.Kind.Equals("model", StringComparison.OrdinalIgnoreCase) ||
               _selectedPreview.Kind.Equals("blueprint", StringComparison.OrdinalIgnoreCase);
    }

    private string GetTypedExportLabel()
    {
        return _selectedPreview?.Kind.ToLowerInvariant() switch
        {
            "texture" => "导出 PNG",
            "audio" => "导出 WAV",
            "model" => "导出 GLB+FBX",
            "blueprint" => "导出 CPP",
            _ => "导出"
        };
    }

    private object? CreatePreviewState()
    {
        if (_selectedPreview is null)
            return null;

        string? resourceUrl = null;
        object? model = null;
        object? locres = _selectedPreview.Locres;
        string? text = _selectedPreview.Text;
        if (_selectedPreview.Data is { Length: > 0 } data &&
            !_selectedPreview.Kind.Equals("model", StringComparison.OrdinalIgnoreCase))
        {
            _selectedPreviewResourceUrl ??= _previewBlobStore.Put(data, _selectedPreview.MimeType ?? "application/octet-stream");
            resourceUrl = _selectedPreviewResourceUrl;
        }
        else if (_selectedPreview.Model is not null)
        {
            _selectedPreviewResourceUrl ??= _previewBlobStore.Put(
                JsonSerializer.SerializeToUtf8Bytes(_selectedPreview.Model, JsonOptions),
                "application/json");
            resourceUrl = _selectedPreviewResourceUrl;
        }
        else if (!string.IsNullOrEmpty(text) && System.Text.Encoding.UTF8.GetByteCount(text) > 64 * 1024)
        {
            _selectedPreviewResourceUrl ??= _previewBlobStore.Put(
                System.Text.Encoding.UTF8.GetBytes(text),
                "text/plain; charset=utf-8");
            resourceUrl = _selectedPreviewResourceUrl;
            text = null;
        }
        else
        {
            model = _selectedPreview.Model;
        }

        return new
        {
            kind = _selectedPreview.Kind,
            title = _selectedPreview.Title,
            details = _selectedPreview.Details.Select(detail => new
            {
                label = detail.Label,
                value = detail.Value
            }).ToArray(),
            mimeType = _selectedPreview.MimeType,
            resourceUrl,
            text,
            model,
            locres,
            canPlay = _selectedPreview.CanPlay,
            canExportRaw = _selectedPreview.CanExportRaw
        };
    }

    private void RememberEntryThumbnail(string pakPath, byte[] pngData)
    {
        var thumbnail = CreateThumbnailDataUrl(pngData, 96);
        if (string.IsNullOrWhiteSpace(thumbnail))
            return;

        if (!_entryThumbnails.ContainsKey(pakPath))
            _entryThumbnailOrder.Enqueue(pakPath);

        _entryThumbnails[pakPath] = thumbnail;
        while (_entryThumbnailOrder.Count > MaxEntryThumbnails)
        {
            var oldest = _entryThumbnailOrder.Dequeue();
            _entryThumbnails.Remove(oldest);
        }
    }

    private static string? CreateThumbnailDataUrl(byte[] pngData, int maxSide)
    {
        try
        {
            using var source = global::Android.Graphics.BitmapFactory.DecodeByteArray(pngData, 0, pngData.Length);
            if (source is null || source.Width <= 0 || source.Height <= 0)
                return null;

            var scale = Math.Min(1.0, (double)maxSide / Math.Max(source.Width, source.Height));
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            var thumbnail = global::Android.Graphics.Bitmap.CreateScaledBitmap(source, width, height, filter: true);
            using var stream = new MemoryStream();
#pragma warning disable CA1416
            thumbnail.Compress(global::Android.Graphics.Bitmap.CompressFormat.Png!, 90, stream);
#pragma warning restore CA1416
            if (!ReferenceEquals(thumbnail, source))
                thumbnail.Dispose();
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private void ClearEntryThumbnails()
    {
        _entryThumbnails.Clear();
        _entryThumbnailOrder.Clear();
    }

    private static byte[] EncodePreviewPng(PakTool.Core.TexturePreviewDto preview)
    {
        return preview.PngData;
    }

    private static string EncodePreviewDataUrl(PakTool.Core.TexturePreviewDto preview)
    {
        return "data:image/png;base64," + Convert.ToBase64String(preview.PngData);
    }

    private static string NormalizeFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder == "/")
            return string.Empty;

        var normalized = folder.Replace('\\', '/').TrimStart('/');
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    private static string NormalizeFolderExportKind(string? kind)
    {
        return (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "model" or "models" => "model",
            "texture" or "textures" or "png" => "texture",
            "audio" or "wav" => "audio",
            "blueprint" or "cpp" => "blueprint",
            _ => "raw"
        };
    }

    private static string GetFolderExportKindLabel(string kind)
    {
        return kind switch
        {
            "model" => "模型",
            "texture" => "贴图",
            "audio" => "音频",
            "blueprint" => "蓝图",
            _ => "原始文件"
        };
    }

    private static bool FolderExportKindMatches(string requestedKind, string exportedKind)
    {
        return requestedKind.Equals(exportedKind, StringComparison.OrdinalIgnoreCase);
    }

    private global::Android.Net.Uri CreateFolderExportRoot(global::Android.Net.Uri treeUri, string rootFolder, string kind)
    {
        var treeDocumentId = global::Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri);
        var parentUri = global::Android.Provider.DocumentsContract.BuildDocumentUriUsingTree(treeUri, treeDocumentId)
            ?? throw new InvalidOperationException("Could not resolve output directory.");
        var folderName = string.IsNullOrEmpty(rootFolder)
            ? "pak_root"
            : rootFolder.TrimEnd('/').Split('/').LastOrDefault() ?? "pak_folder";
        folderName = SanitizeDocumentName($"{folderName}_{kind}_{DateTime.Now:yyyyMMdd_HHmmss}");
        return CreateDocumentInParent(parentUri, folderName, "vnd.android.document/directory");
    }

    private global::Android.Net.Uri EnsureOutputDirectory(
        global::Android.Net.Uri rootUri,
        IDictionary<string, global::Android.Net.Uri> createdDirectories,
        string relativeDirectory)
    {
        relativeDirectory = NormalizeFolder(relativeDirectory);
        if (string.IsNullOrEmpty(relativeDirectory))
            return rootUri;

        if (createdDirectories.TryGetValue(relativeDirectory, out var cached))
            return cached;

        var parentKey = GetParentFolder(relativeDirectory.TrimEnd('/'));
        var parentUri = EnsureOutputDirectory(rootUri, createdDirectories, parentKey);
        var folderName = relativeDirectory.TrimEnd('/').Split('/').Last();
        var folderUri = CreateDocumentInParent(parentUri, SanitizeDocumentName(folderName), "vnd.android.document/directory");
        createdDirectories[relativeDirectory] = folderUri;
        return folderUri;
    }

    private global::Android.Net.Uri CreateUniqueDocument(
        global::Android.Net.Uri parentUri,
        IDictionary<string, HashSet<string>> createdFileNames,
        string relativeDirectory,
        string fileName,
        string mimeType)
    {
        relativeDirectory = NormalizeFolder(relativeDirectory);
        if (!createdFileNames.TryGetValue(relativeDirectory, out var names))
            createdFileNames[relativeDirectory] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        fileName = SanitizeDocumentName(fileName);
        var candidate = fileName;
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var extension = System.IO.Path.GetExtension(fileName);
        var suffix = 2;
        while (!names.Add(candidate))
            candidate = $"{stem}_{suffix++}{extension}";

        return CreateDocumentInParent(parentUri, candidate, mimeType);
    }

    private global::Android.Net.Uri CreateDocumentInParent(global::Android.Net.Uri parentUri, string fileName, string mimeType)
    {
        var documentUri = global::Android.Provider.DocumentsContract.CreateDocument(
            ContentResolver!,
            parentUri,
            mimeType,
            fileName);

        return documentUri ?? throw new InvalidOperationException($"Could not create output document: {fileName}");
    }

    private async Task WriteDocumentAsync(global::Android.Net.Uri outputUri, byte[] data)
    {
        await using var output = ContentResolver!.OpenOutputStream(outputUri, "wt")
            ?? throw new InvalidOperationException("Could not open output document.");
        await output.WriteAsync(data);
    }

    private static string GetFolderRelativePath(string rootFolder, string path)
    {
        rootFolder = NormalizeFolder(rootFolder);
        path = path.Replace('\\', '/').TrimStart('/');
        return !string.IsNullOrEmpty(rootFolder) && path.StartsWith(rootFolder, StringComparison.OrdinalIgnoreCase)
            ? path[rootFolder.Length..]
            : GetFileNameFromPakPath(path);
    }

    private static string GetParentFolder(string path)
    {
        path = path.Replace('\\', '/').Trim('/');
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..(slash + 1)];
    }

    private static string GetFileNameFromPakPath(string path)
    {
        path = path.Replace('\\', '/').TrimEnd('/');
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string SanitizeDocumentName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "export";

        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value.Replace('/', '_').Replace('\\', '_').Trim();
    }

    private static string FormatSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)size;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private static string ShortenPakPath(string path, int maxLength = 72)
    {
        path = path.Replace('\\', '/');
        if (path.Length <= maxLength)
            return path;

        var fileName = GetFileNameFromPakPath(path);
        if (fileName.Length + 4 >= maxLength)
            return "..." + fileName[^Math.Max(1, maxLength - 3)..];

        return "..." + path[^Math.Max(1, maxLength - 3)..];
    }

    private static string? GetPayloadString(string payloadJson, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(name, out var property) ? property.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static int GetPayloadInt(string payloadJson, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static float? GetPayloadFloat(string payloadJson, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty(name, out var property))
                return null;

            return property.ValueKind switch
            {
                JsonValueKind.Number when property.TryGetSingle(out var value) => value,
                JsonValueKind.String when float.TryParse(
                    property.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value) => value,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private string? GetSelectedTextureFormatHint()
    {
        return _selectedPreview?.Details.FirstOrDefault(detail =>
            detail.Label.Equals("Format", StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static bool GetPayloadBool(string payloadJson, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string? LoadEmbeddedMainWebViewHtml()
    {
        var assembly = typeof(MainActivity).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(".mainwebview.html", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(resourceName))
            return null;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string InjectPrismWebViewBridge(string html)
    {
        const string marker = "</body>";
        var script = """
<script>
(function () {
  let latestState = {};
  let entryIndexByPath = new Map();
  let modelFrame = 0;
  let modelAbortController = null;
  let lastPreviewSignature = "";

  function send(action, payload) {
    const encoded = encodeURIComponent(JSON.stringify(payload || {}));
    location.href = `paktool://${action}?payload=${encoded}&t=${Date.now()}`;
  }

  function kindOf(entry) {
    if (entry.isDirectory) return "DIR";
    if (entry.isAssetPackage) return "UE";
    const ext = (entry.extension || "").replace(".", "").toUpperCase();
    return ext || "FILE";
  }

  function patchStatus(status) {
    const s = (status || "").toLowerCase();
    if (s === "replaced") return "replaced";
    if (s === "failed") return "failed";
    return "pending";
  }

  function escHtml(value) {
    return String(value || "").replace(/[&<>]/g, m => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[m]));
  }

  function resetPreviewRenderState() {
    if (modelFrame) {
      cancelAnimationFrame(modelFrame);
      modelFrame = 0;
    }
    if (modelAbortController) {
      modelAbortController.abort();
      modelAbortController = null;
    }
  }

  function previewSignature(preview) {
    if (!preview) return `empty:${latestState.busy ? 1 : 0}:${latestState.status || ""}`;
    return [
      preview.kind || "",
      preview.title || "",
      preview.resourceUrl || "",
      preview.mimeType || "",
      preview.canPlay ? "1" : "0",
      preview.text ? preview.text.length : 0,
      preview.model ? `${preview.model.vertexCount || 0}:${preview.model.triangleCount || 0}` : "remote",
      preview.locres ? `${preview.locres.version || ""}:${preview.locres.entryCount || 0}` : "",
      latestState.busy ? "busy" : "ready"
    ].join("|");
  }

  function typeLabel(kind) {
    switch ((kind || "").toLowerCase()) {
      case "texture": return "贴图";
      case "audio": return "音频";
      case "video": return "视频";
      case "model": return "模型";
      case "blueprint": return "蓝图";
      case "locres": return "本地化";
      case "error": return "错误";
      default: return "资源";
    }
  }

  function typeTone(kind) {
    switch ((kind || "").toLowerCase()) {
      case "texture": return ["#1f6feb", "rgba(31,111,235,.12)"];
      case "audio": return ["#16803c", "rgba(22,128,60,.12)"];
      case "video": return ["#b45309", "rgba(180,83,9,.14)"];
      case "model": return ["#6f42c1", "rgba(111,66,193,.12)"];
      case "blueprint": return ["#0969da", "rgba(9,105,218,.12)"];
      case "locres": return ["#0f766e", "rgba(15,118,110,.12)"];
      case "error": return ["#cf222e", "rgba(207,34,46,.12)"];
      default: return ["#57606a", "rgba(87,96,106,.12)"];
    }
  }

  function entryKindMeta(entry) {
    const kind = String(entry?.kind || (entry?.isDirectory ? "folder" : entry?.extension || "file")).toLowerCase();
    const ext = String(entry?.extension || "").replace(".", "").toLowerCase();
    if (entry?.isDirectory || kind === "folder") return { icon: "DIR", label: "文件夹", color: "#5b4636", bg: "#f2dfc3" };
    if (kind.includes("texture") || kind === "image" || ["png", "jpg", "jpeg", "bmp", "tga", "dds"].includes(ext)) return { icon: "IMG", label: "贴图", color: "#1f6feb", bg: "rgba(31,111,235,.13)" };
    if (kind === "audio" || ["wav", "ogg", "wem", "binka", "opus", "at9"].includes(ext)) return { icon: "AUD", label: "音频", color: "#16803c", bg: "rgba(22,128,60,.13)" };
    if (kind === "video" || ["mp4", "webm", "m4v", "mov", "bk2", "bik"].includes(ext)) return { icon: "VID", label: "视频", color: "#b45309", bg: "rgba(180,83,9,.15)" };
    if (kind === "model") return { icon: "3D", label: "模型", color: "#6f42c1", bg: "rgba(111,66,193,.14)" };
    if (kind === "blueprint") return { icon: "BP", label: "蓝图", color: "#0969da", bg: "rgba(9,105,218,.14)" };
    if (kind === "locres" || ext === "locres") return { icon: "LOC", label: "本地化", color: "#0f766e", bg: "rgba(15,118,110,.14)" };
    if (entry?.isAssetPackage) return { icon: "UE", label: "UAsset", color: "#5f3dc4", bg: "rgba(95,61,196,.12)" };
    return { icon: (ext || "FILE").toUpperCase().slice(0, 4), label: ext ? ext.toUpperCase() : "File", color: "#57606a", bg: "rgba(87,96,106,.12)" };
  }

  function enhanceEntryBadges() {
    const list = document.getElementById("list");
    if (!list) return;

    const rows = Array.from(list.querySelectorAll(".row"));
    const entries = latestState.entries || [];
    for (let i = 0; i < rows.length && i < entries.length; i++) {
      const meta = entryKindMeta(entries[i]);
      const prefix = rows[i].querySelector(".prefix,.kind");
      if (prefix) {
        prefix.textContent = meta.icon;
        prefix.style.background = meta.bg;
        prefix.style.color = meta.color;
        prefix.style.borderRadius = "5px";
        prefix.style.padding = "2px 5px";
        prefix.style.minWidth = "30px";
        prefix.style.textAlign = "center";
        prefix.style.fontWeight = "650";
      }

      if (entries[i]?.thumbnailUrl && !rows[i].querySelector("[data-prism-thumb]")) {
        const thumb = document.createElement("img");
        thumb.dataset.prismThumb = "true";
        thumb.src = entries[i].thumbnailUrl;
        thumb.alt = "";
        thumb.style.cssText = "width:42px;height:42px;object-fit:contain;border:1px solid rgba(0,0,0,.12);border-radius:6px;background:rgba(255,255,255,.65);";
        rows[i].style.gridTemplateColumns = "42px 48px minmax(0,1fr) auto";
        if (prefix?.parentElement) {
          prefix.parentElement.insertBefore(thumb, prefix.nextSibling);
        } else {
          rows[i].insertBefore(thumb, rows[i].firstChild);
        }
      }

      let badge = rows[i].querySelector(".type-tag,.badge");
      if (!badge && !entries[i].isDirectory) {
        badge = document.createElement("span");
        badge.className = "type-tag";
        rows[i].appendChild(badge);
      }
      if (badge) {
        badge.textContent = meta.label;
        badge.style.color = meta.color;
        badge.style.background = meta.bg;
        badge.style.borderRadius = "999px";
        badge.style.padding = "2px 7px";
        badge.style.fontWeight = "650";
        badge.style.whiteSpace = "nowrap";
      }
    }
  }

  function createPreviewShell(container, preview) {
    const [accent, bg] = typeTone(preview.kind);
    const root = document.createElement("div");
    root.style.cssText = "width:100%;min-height:100%;display:grid;grid-template-rows:auto minmax(180px,1fr) auto;gap:10px;";

    const header = document.createElement("div");
    header.style.cssText = "display:grid;grid-template-columns:minmax(0,1fr) auto;gap:8px;align-items:start;";
    const titleBox = document.createElement("div");
    titleBox.style.minWidth = "0";
    const title = document.createElement("div");
    title.style.cssText = "font-size:13px;font-weight:650;color:var(--text);overflow-wrap:anywhere;";
    title.textContent = preview.title || "预览";
    const subtitle = document.createElement("div");
    subtitle.style.cssText = "font-size:11px;color:var(--text2);margin-top:2px;overflow-wrap:anywhere;";
    subtitle.textContent = preview.mimeType || typeLabel(preview.kind);
    titleBox.append(title, subtitle);

    const badge = document.createElement("div");
    badge.style.cssText = `border:1px solid ${accent};background:${bg};color:${accent};border-radius:8px;padding:4px 7px;font-size:11px;font-weight:650;`;
    badge.textContent = typeLabel(preview.kind);
    header.append(titleBox, badge);

    const frame = document.createElement("div");
    frame.style.cssText = "min-height:180px;display:grid;place-items:stretch;overflow:hidden;border-radius:8px;border:1px solid var(--line);background:var(--panel2,rgba(0,0,0,.035));";
    const footer = document.createElement("div");
    footer.style.cssText = "min-height:0;padding-bottom:2px;";

    root.append(header, frame, footer);
    container.appendChild(root);
    return { root, frame, footer, accent };
  }

  function renderPreviewDetails(container, preview) {
    const details = document.createElement("div");
    details.style.cssText = "width:100%;display:grid;grid-template-columns:repeat(auto-fit,minmax(128px,1fr));gap:6px;font-size:11px;color:var(--text2);";
    for (const row of preview.details || []) {
      const line = document.createElement("div");
      line.style.cssText = "min-width:0;overflow-wrap:anywhere;border:1px solid var(--line);border-radius:8px;padding:6px;background:rgba(255,255,255,.35);";
      line.innerHTML = `<strong style="display:block;color:var(--text);font-size:10px;">${escHtml(row.label)}</strong><span>${escHtml(row.value)}</span>`;
      details.appendChild(line);
    }
    container.appendChild(details);
  }

  function renderUnifiedPreview() {
    const preview = latestState.preview;
    const container = document.getElementById("preview");
    if (!container) return;

    const signature = previewSignature(preview);
    if (signature === lastPreviewSignature) return;
    lastPreviewSignature = signature;
    resetPreviewRenderState();

    container.replaceChildren();
    container.style.display = "block";
    container.style.padding = "10px";
    container.style.overflowX = "hidden";
    container.style.overflowY = "auto";
    container.style.webkitOverflowScrolling = "touch";

    if (!preview) {
      const empty = document.createElement("div");
      empty.style.cssText = "height:100%;display:grid;place-items:center;text-align:center;color:var(--text2);font-size:13px;padding:16px;";
      empty.textContent = latestState.busy ? (latestState.status || "正在加载预览...") : (latestState.previewTitle || "请选择要预览的资源。");
      container.appendChild(empty);
      return;
    }

    const shell = createPreviewShell(container, preview);

    if (preview.kind === "texture" && preview.resourceUrl) {
      const img = document.createElement("img");
      img.style.cssText = "width:100%;height:100%;object-fit:contain;background:repeating-conic-gradient(rgba(0,0,0,.06) 0 25%,transparent 0 50%) 0 0/18px 18px;";
      img.src = preview.resourceUrl;
      img.alt = preview.title || "贴图预览";
      shell.frame.appendChild(img);
    } else if (preview.kind === "audio" && preview.resourceUrl && preview.canPlay) {
      shell.frame.style.placeItems = "center";
      const panel = document.createElement("div");
      panel.style.cssText = "width:min(100%,420px);display:grid;gap:14px;padding:18px;";
      const meter = document.createElement("div");
      meter.style.cssText = `height:70px;border-radius:8px;background:linear-gradient(90deg,${shell.accent} 0 4px,transparent 4px 13px);background-size:13px 100%;opacity:.65;`;
      const audio = document.createElement("audio");
      audio.controls = true;
      audio.preload = "metadata";
      audio.src = preview.resourceUrl;
      audio.style.width = "100%";
      panel.append(meter, audio);
      shell.frame.appendChild(panel);
    } else if (preview.kind === "video" && preview.resourceUrl && preview.canPlay) {
      const video = document.createElement("video");
      video.controls = true;
      video.preload = "metadata";
      video.src = preview.resourceUrl;
      video.style.cssText = "width:100%;height:100%;object-fit:contain;background:#080808;";
      shell.frame.appendChild(video);
    } else if ((preview.kind === "audio" || preview.kind === "video") && !preview.canPlay) {
      renderUnsupportedMedia(shell.frame, preview, shell.accent);
    } else if (preview.kind === "model" && (preview.model || preview.resourceUrl)) {
      renderModelPreview(shell.frame, preview);
    } else if (preview.kind === "blueprint") {
      renderPayloadText(shell.frame, preview, "cpp");
    } else if (preview.kind === "locres" && preview.locres) {
      renderLocresPreview(shell.frame, preview.locres);
    } else if (preview.kind === "error") {
      renderPayloadText(shell.frame, preview, "error");
    } else {
      renderPayloadText(shell.frame, preview, "plain");
    }

    if ((preview.kind === "audio" || preview.kind === "video") && !preview.canPlay) {
      const note = document.createElement("div");
      note.style.cssText = "font-size:11px;color:var(--text2);overflow-wrap:anywhere;margin-bottom:8px;";
      note.textContent = "此格式可以导出原始文件，但内置播放器无法直接播放。";
      shell.footer.appendChild(note);
    }

    renderPreviewDetails(shell.footer, preview);
  }

  function renderLocresPreview(frame, locres) {
    frame.style.overflow = "auto";
    frame.style.display = "block";
    const entries = locres.entries || [];
    const search = document.createElement("input");
    search.type = "search";
    search.placeholder = "查找命名空间、键或文本";
    search.autocomplete = "off";
    search.style.cssText = "position:sticky;top:0;z-index:2;width:calc(100% - 16px);box-sizing:border-box;margin:8px 8px 0;padding:9px 10px;border:1px solid var(--line);border-radius:8px;background:var(--panel);color:var(--text);font-size:12px;";
    const count = document.createElement("div");
    count.style.cssText = "padding:6px 10px 0;font-size:11px;color:var(--text2);";
    const table = document.createElement("div");
    table.style.cssText = "display:grid;gap:6px;padding:8px;min-width:320px;";
    const renderRows = () => {
      const query = search.value.trim().toLocaleLowerCase();
      const matches = query
        ? entries.filter(entry => `${entry.namespace || entry.ns || ""}\n${entry.key || ""}\n${entry.text || ""}`.toLocaleLowerCase().includes(query))
        : entries;
      count.textContent = `${matches.length} 个匹配项 / 共 ${entries.length} 条`;
      table.replaceChildren();
      for (const entry of matches.slice(0, 200)) {
        const row = document.createElement("div");
        row.style.cssText = "display:grid;grid-template-columns:minmax(92px,.32fr) minmax(92px,.32fr) minmax(140px,1fr);gap:6px;align-items:start;border:1px solid var(--line);border-radius:8px;padding:7px;background:rgba(255,255,255,.38);font-size:11px;";
        const ns = document.createElement("div");
        ns.style.cssText = "color:var(--text2);overflow-wrap:anywhere;";
        ns.textContent = entry.namespace || entry.ns || "";
        const key = document.createElement("div");
        key.style.cssText = "font-weight:650;color:var(--text);overflow-wrap:anywhere;";
        key.textContent = entry.key || "";
        const text = document.createElement("div");
        text.style.cssText = "color:var(--text);white-space:pre-wrap;overflow-wrap:anywhere;";
        text.textContent = entry.text || "";
        row.append(ns, key, text);
        table.appendChild(row);
      }
      if (matches.length > 200) {
        const more = document.createElement("div");
        more.style.cssText = "font-size:11px;color:var(--text2);padding:4px 2px;";
        more.textContent = `仅显示前 200 个匹配项（共 ${matches.length} 个）。加入替换 Pak 后可编辑。`;
        table.appendChild(more);
      }
    };
    search.oninput = renderRows;
    frame.append(search, count, table);
    renderRows();
  }

  function renderUnsupportedMedia(frame, preview, accent) {
    frame.style.placeItems = "center";
    frame.style.overflow = "auto";
    const panel = document.createElement("div");
    panel.style.cssText = "width:min(100%,460px);box-sizing:border-box;display:grid;gap:10px;padding:18px;text-align:left;";
    const icon = document.createElement("div");
    icon.style.cssText = `width:44px;height:44px;border-radius:8px;display:grid;place-items:center;background:${accent}22;color:${accent};font-size:20px;font-weight:800;`;
    icon.textContent = preview.kind === "audio" ? "A" : "V";
    const title = document.createElement("div");
    title.style.cssText = "font-size:13px;font-weight:650;color:var(--text);";
    title.textContent = preview.kind === "audio" ? "内置浏览器无法播放此音频编码" : "内置浏览器无法播放此视频编码";
    const body = document.createElement("div");
    body.style.cssText = "font-size:12px;line-height:1.45;color:var(--text2);overflow-wrap:anywhere;";
    body.textContent = preview.text || "该资源可以导出原始文件，但内置 WebView 无法解码。";
    panel.append(icon, title, body);
    frame.appendChild(panel);
  }

  function renderPayloadText(frame, preview, mode) {
    const pre = document.createElement("pre");
    pre.style.cssText = "width:100%;height:100%;box-sizing:border-box;margin:0;padding:12px;overflow:auto;white-space:pre-wrap;font-size:11px;line-height:1.45;color:var(--text);font-family:var(--mono,monospace);";
    if (mode === "error") {
      pre.style.color = "#cf222e";
      pre.style.background = "rgba(207,34,46,.06)";
    }
    pre.textContent = preview.text || (preview.canPlay === false ? "该资源可以导出，但不能直接播放。" : "没有可用的预览内容。");
    frame.appendChild(pre);

    if (!preview.text && preview.resourceUrl) {
      fetch(preview.resourceUrl)
        .then(response => response.ok ? response.text() : Promise.reject(new Error(`HTTP ${response.status}`)))
        .then(text => { pre.textContent = text || "没有可用的预览内容。"; })
        .catch(error => { pre.textContent = `加载预览文本失败：${error.message}`; });
    }
  }

  async function renderModelPreview(frame, preview) {
    let model = preview.model || null;
    if (!model && preview.resourceUrl) {
      const loading = document.createElement("div");
      loading.style.cssText = "display:grid;place-items:center;color:var(--text2);font-size:12px;";
      loading.textContent = "正在加载模型几何体...";
      frame.appendChild(loading);
      modelAbortController = new AbortController();
      try {
        const response = await fetch(preview.resourceUrl, { signal: modelAbortController.signal });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        model = await response.json();
      } catch (error) {
        if (error.name === "AbortError") return;
        loading.textContent = `加载模型几何体失败：${error.message}`;
        return;
      }
      if (!frame.isConnected) return;
      frame.replaceChildren();
    }

    const canvas = document.createElement("canvas");
    canvas.style.cssText = "width:100%;height:100%;min-height:180px;display:block;touch-action:none;";
    frame.appendChild(canvas);

    const gl = canvas.getContext("webgl", { antialias: true });
    if (!gl) {
      frame.textContent = "当前 WebView 不支持 WebGL。";
      return;
    }

    const positions = new Float32Array(model.positions || []);
    const uvSets = Array.isArray(model.uvSets) && model.uvSets.length
      ? model.uvSets.map(uv => new Float32Array(uv || []))
      : [new Float32Array(model.uvs || [])];
    const indices = new Uint32Array(model.indices || []);
    if (!positions.length || !indices.length) {
      frame.textContent = "未生成可显示的模型几何体。";
      return;
    }
    const uintIndices = !!gl.getExtension("OES_element_index_uint");
    if (!uintIndices) {
      for (let i = 0; i < indices.length; i++) {
        if (indices[i] > 65535) {
          frame.textContent = "该模型需要 32 位索引，但当前 WebView 不支持。";
          return;
        }
      }
    }

    const vs = gl.createShader(gl.VERTEX_SHADER);
    gl.shaderSource(vs, "attribute vec3 aPosition;attribute vec2 aUv;attribute float aLayer;uniform mat4 uMvp;uniform vec3 uCenter;uniform float uScale;varying vec2 vUv;varying float vLayer;void main(){vec3 p=(aPosition-uCenter)*uScale;vUv=aUv;vLayer=aLayer;gl_Position=uMvp*vec4(p,1.0);}");
    gl.compileShader(vs);
    const fs = gl.createShader(gl.FRAGMENT_SHADER);
    gl.shaderSource(fs, "precision mediump float;varying vec2 vUv;varying float vLayer;uniform sampler2D uTexture0;uniform sampler2D uTexture1;uniform sampler2D uTexture2;uniform sampler2D uTexture3;uniform sampler2D uTexture4;uniform sampler2D uTexture5;uniform sampler2D uTexture6;uniform sampler2D uTexture7;uniform bool uReady0;uniform bool uReady1;uniform bool uReady2;uniform bool uReady3;uniform bool uReady4;uniform bool uReady5;uniform bool uReady6;uniform bool uReady7;vec4 sampleLayer(int layer,vec2 uv){if(layer==1&&uReady1)return texture2D(uTexture1,uv);if(layer==2&&uReady2)return texture2D(uTexture2,uv);if(layer==3&&uReady3)return texture2D(uTexture3,uv);if(layer==4&&uReady4)return texture2D(uTexture4,uv);if(layer==5&&uReady5)return texture2D(uTexture5,uv);if(layer==6&&uReady6)return texture2D(uTexture6,uv);if(layer==7&&uReady7)return texture2D(uTexture7,uv);if(uReady0)return texture2D(uTexture0,uv);return vec4(0.95,0.62,0.28,1.0);}void main(){int layer=int(floor(vLayer+0.5));gl_FragColor=sampleLayer(layer,vUv);}");
    gl.compileShader(fs);
    const program = gl.createProgram();
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);
    gl.useProgram(program);

    const vb = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, vb);
    gl.bufferData(gl.ARRAY_BUFFER, positions, gl.STATIC_DRAW);
    const ib = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ib);
    const indexData = uintIndices ? indices : new Uint16Array(indices);
    const indexType = uintIndices ? gl.UNSIGNED_INT : gl.UNSIGNED_SHORT;
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, indexData, gl.STATIC_DRAW);

    const aPosition = gl.getAttribLocation(program, "aPosition");
    gl.enableVertexAttribArray(aPosition);
    gl.vertexAttribPointer(aPosition, 3, gl.FLOAT, false, 0, 0);
    const aUv = gl.getAttribLocation(program, "aUv");
    const uvBuffers = [];
    if (aUv >= 0) {
      const expectedUvLength = (positions.length / 3) * 2;
      for (const sourceUv of uvSets) {
        const uvData = sourceUv.length === expectedUvLength ? sourceUv : (uvSets[0]?.length === expectedUvLength ? uvSets[0] : new Float32Array(expectedUvLength));
        const uvb = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, uvb);
        gl.bufferData(gl.ARRAY_BUFFER, uvData, gl.STATIC_DRAW);
        uvBuffers.push(uvb);
      }
      if (!uvBuffers.length) {
        const uvb = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, uvb);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(expectedUvLength), gl.STATIC_DRAW);
        uvBuffers.push(uvb);
      }
    }

    const aLayer = gl.getAttribLocation(program, "aLayer");
    const layerBuffer = gl.createBuffer();
    if (aLayer >= 0) {
      const vertexCount = positions.length / 3;
      const sourceLayers = new Float32Array(model.textureLayers || []);
      const layerData = sourceLayers.length === vertexCount ? sourceLayers : new Float32Array(vertexCount);
      gl.bindBuffer(gl.ARRAY_BUFFER, layerBuffer);
      gl.bufferData(gl.ARRAY_BUFFER, layerData, gl.STATIC_DRAW);
      gl.enableVertexAttribArray(aLayer);
      gl.vertexAttribPointer(aLayer, 1, gl.FLOAT, false, 0, 0);
    }

    const textureUniforms = Array.from({ length: 8 }, (_, i) => gl.getUniformLocation(program, `uTexture${i}`));
    const readyUniforms = Array.from({ length: 8 }, (_, i) => gl.getUniformLocation(program, `uReady${i}`));
    const materialTextures = new Map();
    const createFallbackTexture = color => {
      const texture = gl.createTexture();
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, color);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      return texture;
    };
    const fallbackTexture = createFallbackTexture(new Uint8Array([242, 158, 71, 255]));
    for (const material of (model.materials || [])) {
      if (!material) continue;
      const layers = Array.isArray(material.diffuseTextures) && material.diffuseTextures.length
        ? material.diffuseTextures
        : (material.diffuseTextureData ? [{
            layer: material.diffuseUvSet || 0,
            name: material.diffuseTextureName || material.name || "Diffuse",
            mimeType: material.diffuseTextureMime || "image/png",
            data: material.diffuseTextureData
          }] : []);
      const textureRecord = {
        textures: Array.from({ length: 8 }, () => fallbackTexture),
        ready: Array.from({ length: 8 }, () => false),
        uvSet: 0
      };
      materialTextures.set(material.materialIndex, textureRecord);
      for (const layerInfo of layers) {
        if (!layerInfo?.data) continue;
        const layer = Math.max(0, Math.min(7, Math.round(layerInfo.layer || 0)));
        const texture = createFallbackTexture(new Uint8Array([242, 158, 71, 255]));
        textureRecord.textures[layer] = texture;
        if (layer !== 0 && !textureRecord.ready[0]) textureRecord.textures[0] = texture;
        const image = new Image();
        image.onload = function () {
          gl.activeTexture(gl.TEXTURE0);
          gl.bindTexture(gl.TEXTURE_2D, texture);
          gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
          gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, image);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
          textureRecord.ready[layer] = true;
          if (layer !== 0 && !textureRecord.ready[0]) {
            textureRecord.textures[0] = texture;
            textureRecord.ready[0] = true;
          }
        };
        image.src = `data:${layerInfo.mimeType || "image/png"};base64,${layerInfo.data}`;
      }
    }
    for (let i = 0; i < textureUniforms.length; i++) {
      if (textureUniforms[i]) gl.uniform1i(textureUniforms[i], i);
    }

    const bounds = model.bounds || { minX: -1, minY: -1, minZ: -1, maxX: 1, maxY: 1, maxZ: 1 };
    const center = [
      (bounds.minX + bounds.maxX) / 2,
      (bounds.minY + bounds.maxY) / 2,
      (bounds.minZ + bounds.maxZ) / 2
    ];
    const extent = Math.max(bounds.maxX - bounds.minX, bounds.maxY - bounds.minY, bounds.maxZ - bounds.minZ, 1);
    const uMvp = gl.getUniformLocation(program, "uMvp");
    const uCenter = gl.getUniformLocation(program, "uCenter");
    const uScale = gl.getUniformLocation(program, "uScale");
    gl.uniform3fv(uCenter, new Float32Array(center));
    gl.uniform1f(uScale, 2 / extent);
    gl.enable(gl.DEPTH_TEST);

    let rotX = -0.55, rotY = 0.7, dragging = false, lastX = 0, lastY = 0;
    canvas.onpointerdown = e => { dragging = true; lastX = e.clientX; lastY = e.clientY; canvas.setPointerCapture(e.pointerId); };
    canvas.onpointermove = e => {
      if (!dragging) return;
      rotY += (e.clientX - lastX) * 0.01;
      rotX += (e.clientY - lastY) * 0.01;
      lastX = e.clientX; lastY = e.clientY;
    };
    canvas.onpointerup = canvas.onpointercancel = () => { dragging = false; };

    function mvp() {
      const cx = Math.cos(rotX), sx = Math.sin(rotX), cy = Math.cos(rotY), sy = Math.sin(rotY);
      return new Float32Array([
        cy, 0, -sy, 0,
        sy * sx, cx, cy * sx, 0,
        sy * cx, -sx, cy * cx, 0,
        0, 0, 0, 1
      ]);
    }

    function draw() {
      const rect = canvas.getBoundingClientRect();
      const w = Math.max(1, Math.floor(rect.width * devicePixelRatio));
      const h = Math.max(1, Math.floor(rect.height * devicePixelRatio));
      if (canvas.width !== w || canvas.height !== h) {
        canvas.width = w;
        canvas.height = h;
      }
      gl.viewport(0, 0, canvas.width, canvas.height);
      gl.clearColor(0.08, 0.08, 0.08, 1);
      gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
      gl.uniformMatrix4fv(uMvp, false, mvp());
      const sections = Array.isArray(model.sections) && model.sections.length
        ? model.sections
        : [{ materialIndex: 0, firstIndex: 0, indexCount: indexData.length }];
      for (const section of sections) {
        const firstIndex = Math.max(0, section.firstIndex || 0);
        const indexCount = Math.max(0, Math.min(section.indexCount || 0, indexData.length - firstIndex));
        if (!indexCount) continue;
        const material = materialTextures.get(section.materialIndex) || null;
        const uvBuffer = uvBuffers[Math.max(0, Math.min(uvBuffers.length - 1, material?.uvSet || 0))] || uvBuffers[0];
        if (aUv >= 0 && uvBuffer) {
          gl.bindBuffer(gl.ARRAY_BUFFER, uvBuffer);
          gl.enableVertexAttribArray(aUv);
          gl.vertexAttribPointer(aUv, 2, gl.FLOAT, false, 0, 0);
        }
        if (aLayer >= 0 && layerBuffer) {
          gl.bindBuffer(gl.ARRAY_BUFFER, layerBuffer);
          gl.enableVertexAttribArray(aLayer);
          gl.vertexAttribPointer(aLayer, 1, gl.FLOAT, false, 0, 0);
        }
        for (let slot = 0; slot < 8; slot++) {
          gl.activeTexture(gl.TEXTURE0 + slot);
          gl.bindTexture(gl.TEXTURE_2D, material?.textures?.[slot] || fallbackTexture);
          if (readyUniforms[slot]) gl.uniform1i(readyUniforms[slot], material?.ready?.[slot] ? 1 : 0);
        }
        gl.drawElements(gl.TRIANGLES, indexCount, indexType, firstIndex * (uintIndices ? 4 : 2));
      }
      modelFrame = requestAnimationFrame(draw);
    }
    draw();
  }

  function ensureOodleToggle() {
    const build = document.getElementById("buildPatchBtn");
    if (!build || document.getElementById("oodleCompression")) return;

    const label = document.createElement("label");
    label.className = "btn btn-sec btn-sm";
    label.style.gap = "6px";
    label.style.cursor = "pointer";

    const input = document.createElement("input");
    input.id = "oodleCompression";
    input.type = "checkbox";
    input.checked = localStorage.getItem("prism.oodleCompression") === "true";
    input.onchange = function () {
      localStorage.setItem("prism.oodleCompression", input.checked ? "true" : "false");
    };

    const text = document.createElement("span");
    text.textContent = "Oodle";
    label.append(input, text);
    build.parentElement.insertBefore(label, build);
  }

  function ensureExportLogButton() {
    if (document.getElementById("exportLogButton")) return;

    const anchor = document.getElementById("toolbarToggle") || document.querySelector(".toolbar-top .spacer");
    if (!anchor || !anchor.parentElement) return;

    const button = document.createElement("button");
    button.id = "exportLogButton";
    button.className = anchor.classList.contains("toolbar-toggle") ? "toolbar-toggle" : "btn btn-sm";
    button.type = "button";
    button.textContent = "导出日志";
    button.onclick = function () {
      send("exportLog");
    };

    anchor.parentElement.insertBefore(button, anchor);
  }

  function ensureUpdateButton() {
    if (document.getElementById("checkUpdateButton")) return;

    const anchor = document.getElementById("exportLogButton") || document.getElementById("toolbarToggle");
    if (!anchor || !anchor.parentElement) return;

    const button = document.createElement("button");
    button.id = "checkUpdateButton";
    button.className = anchor.className;
    button.type = "button";
    button.textContent = "检查更新";
    button.onclick = function () { send("checkUpdate"); };
    anchor.parentElement.insertBefore(button, anchor);
  }

  function ensureMergeControls() {
    if (document.getElementById("mergePakPanel")) {
      updateMergeControls();
      return;
    }

    const anchor = document.querySelector(".toolbar,.toolbar-top,.hero,.topbar") || document.body;
    const panel = document.createElement("div");
    panel.id = "mergePakPanel";
    panel.style.cssText = "display:flex;align-items:center;gap:6px;flex-wrap:wrap;margin:6px 0;padding:6px;border:1px solid rgba(0,0,0,.12);border-radius:8px;background:rgba(255,255,255,.45);font-size:12px;";
    panel.style.gridColumn = "1 / -1";

    const label = document.createElement("span");
    label.id = "mergePakName";
    label.style.cssText = "max-width:220px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;";
    label.textContent = latestState.mergePakName || "No merge pak";

    const pick = document.createElement("button");
    pick.id = "pickMergePakButton";
    pick.type = "button";
    pick.className = "btn btn-sec btn-sm";
    pick.textContent = "Merge Pak";
    pick.onclick = function () { send("pickMergePak"); };

    const askLabel = document.createElement("label");
    askLabel.style.cssText = "display:flex;align-items:center;gap:4px;cursor:pointer;";
    const ask = document.createElement("input");
    ask.id = "mergeAskBeforeReplace";
    ask.type = "checkbox";
    ask.checked = localStorage.getItem("prism.mergeAsk") === "true";
    ask.onchange = function () { localStorage.setItem("prism.mergeAsk", ask.checked ? "true" : "false"); };
    const askText = document.createElement("span");
    askText.textContent = "Ask";
    askLabel.append(ask, askText);

    const run = document.createElement("button");
    run.id = "runMergePakButton";
    run.type = "button";
    run.className = "btn btn-acc btn-sm";
    run.textContent = "Build Merged";
    run.onclick = function () {
      const oodle = document.getElementById("oodleCompression");
      const aes = document.getElementById("aesKey");
      send("mergePak", {
        askBeforeReplace: !!ask.checked,
        useOodleCompression: !!oodle?.checked,
        aesKey: aes?.value || ""
      });
    };

    panel.append(pick, label, askLabel, run);
    anchor.appendChild(panel);
    updateMergeControls();
  }

  function updateMergeControls() {
    const label = document.getElementById("mergePakName");
    if (label) label.textContent = latestState.mergePakName || "No merge pak";
    const pick = document.getElementById("pickMergePakButton");
    if (pick) pick.disabled = !!latestState.busy;
    const run = document.getElementById("runMergePakButton");
    if (run) run.disabled = !!latestState.busy || !latestState.canMergePak;
    const ask = document.getElementById("mergeAskBeforeReplace");
    if (ask) ask.disabled = !!latestState.busy;
  }

  function createPatchPreviewCard(label, dataUrl, isAudio, emptyText) {
    const card = document.createElement("div");
    card.style.cssText = "min-height:140px;display:flex;flex-direction:column;gap:6px;padding:10px;border-radius:8px;background:rgba(0,0,0,.04);overflow:hidden;";
    const strong = document.createElement("strong");
    strong.style.cssText = "font-size:10px;opacity:.55;text-transform:uppercase;letter-spacing:.04em;";
    strong.textContent = label;
    const frame = document.createElement("div");
    frame.style.cssText = "flex:1;min-height:0;display:grid;place-items:center;overflow:hidden;";
    if (dataUrl) {
      if (isAudio && dataUrl.indexOf("data:audio/") === 0) {
        const aud = document.createElement("audio");
        aud.controls = true; aud.src = dataUrl; aud.style.width = "100%";
        frame.appendChild(aud);
      } else {
        const img = document.createElement("img");
        img.src = dataUrl; img.alt = label; img.style.cssText = "max-width:100%;max-height:100%;object-fit:contain;";
        frame.appendChild(img);
      }
    } else {
      const span = document.createElement("span");
      span.style.cssText = "color:rgba(0,0,0,.35);font-size:10px;";
      span.textContent = emptyText || "无预览";
      frame.appendChild(span);
    }
    card.appendChild(strong); card.appendChild(frame);
    return card;
  }

  function augmentPatchDetail() {
    const detail = document.getElementById("patchDetail");
    if (!detail) return;

    const items = latestState.patchItems || [];
    const selectedId = latestState.selectedPatchItemId;
    const item = items.find(x => x.id === selectedId) || items[0];
    if (!item) return;
    var existing = detail.querySelector("[data-prism-patch-actions]");
    // 已有完整渲染且 item 没变，跳过；否则重建（预览数据可能异步到达）
    if (existing && existing.getAttribute("data-prism-patch-id") === item.id && (item.originalPreview || item.replacementPreview || item.kind === "locres" || item.kind === "raw-folder" || item.kind === "material")) return;
    detail.replaceChildren();

    if (item.kind === "locres") {
      const editor = document.createElement("div");
      editor.dataset.prismPatchActions = "true"; editor.setAttribute("data-prism-patch-id", item.id);
      editor.style.cssText = "display:grid;gap:8px;margin-top:8px;";
      const summary = document.createElement("div");
      summary.style.cssText = "font-size:12px;font-weight:650;overflow-wrap:anywhere;";
      summary.textContent = `${item.sourcePath || item.name || "Locres"} (${item.format || "Locres"}, ${item.sizeLabel || ""})`;
      const entries = item.locresEntries || [];
      const search = document.createElement("input");
      search.type = "search";
      search.placeholder = "查找命名空间、键或文本";
      search.autocomplete = "off";
      search.style.cssText = "width:100%;box-sizing:border-box;border:1px solid rgba(0,0,0,.16);border-radius:8px;padding:9px;font-size:12px;";
      const count = document.createElement("div");
      count.style.cssText = "font-size:11px;opacity:.7;";
      const rows = document.createElement("div");
      rows.style.cssText = "display:grid;gap:8px;";
      const renderRows = () => {
        const query = search.value.trim().toLocaleLowerCase();
        const matches = query
          ? entries.filter(entry => `${entry.ns || ""}\n${entry.key || ""}\n${entry.text || ""}`.toLocaleLowerCase().includes(query))
          : entries;
        count.textContent = `${matches.length} 个匹配项 / 共 ${entries.length} 条`;
        rows.replaceChildren();
        for (const entry of matches.slice(0, 300)) {
          const row = document.createElement("div");
          row.style.cssText = "display:grid;grid-template-columns:minmax(0,.9fr) minmax(0,1.2fr);gap:8px;border:1px solid rgba(0,0,0,.12);border-radius:8px;padding:8px;";
          const name = document.createElement("div");
          name.style.cssText = "font-size:11px;overflow-wrap:anywhere;opacity:.75;";
          name.textContent = `${entry.ns || ""}::${entry.key || ""}`;
          const input = document.createElement("textarea");
          input.value = entry.text || "";
          input.rows = 3;
          input.style.cssText = "width:100%;box-sizing:border-box;resize:vertical;border-radius:8px;padding:7px;";
          input.onchange = function () {
            send("updatePatchLocresEntry", { id: item.id, index: entry.index, text: input.value });
          };
          row.append(name, input);
          rows.appendChild(row);
        }
      };
      search.oninput = renderRows;
      editor.append(summary, search, count, rows);
      renderRows();
      const remove = document.createElement("button");
      remove.className = "btn btn-sec btn-sm";
      remove.textContent = "移除";
      remove.disabled = !!latestState.busy;
      remove.onclick = function () {
        send("removePatchItem", { id: item.id });
      };
      editor.appendChild(remove);
      detail.appendChild(editor);
      return;
    }

    if (item.kind === "raw-folder") {
      const panel = document.createElement("div");
      panel.dataset.prismPatchActions = "true"; panel.setAttribute("data-prism-patch-id", item.id);
      panel.style.cssText = "display:grid;gap:8px;margin-top:8px;";
      const summary = document.createElement("div");
      summary.style.cssText = "font-size:12px;font-weight:650;overflow-wrap:anywhere;";
      summary.textContent = `${item.sourcePath || item.name || "/"}（文件夹，${item.sizeLabel || ""}，${item.relatedCount || 0} 个原始文件）`;
      const note = document.createElement("div");
      note.style.cssText = "font-size:11px;opacity:.72;line-height:1.5;";
      note.textContent = "该文件夹及其子文件夹中的所有文件将被复制到替换 Pak。";
      const remove = document.createElement("button");
      remove.className = "btn btn-sec btn-sm";
      remove.textContent = "移除";
      remove.disabled = !!latestState.busy;
      remove.onclick = function () {
        send("removePatchItem", { id: item.id });
      };
      panel.append(summary, note, remove);
      detail.appendChild(panel);
      return;
    }

    if (item.kind === "material") {
      const panel = document.createElement("div");
      panel.dataset.prismPatchActions = "true"; panel.setAttribute("data-prism-patch-id", item.id);
      panel.style.cssText = "display:grid;gap:10px;margin-top:8px;";
      const summary = document.createElement("div");
      summary.style.cssText = "font-size:12px;font-weight:650;overflow-wrap:anywhere;";
      summary.textContent = `${item.path || item.name || "Material"} (${item.format || "MaterialInstance"}, ${item.sizeLabel || ""})`;
      panel.appendChild(summary);

      const parameters = item.materialParameters || {};
      appendMaterialGroup(panel, "Scalar", (parameters.scalars || []).map(parameter => {
        const row = materialParameterRow(parameter.name);
        const input = materialNumberInput(parameter.value ?? 0);
        input.onchange = function () {
          send("updateMaterialParameter", { id: item.id, kind: "scalar", index: parameter.index, value: Number(input.value) });
        };
        row.appendChild(input);
        return row;
      }));
      appendMaterialGroup(panel, "Vector", (parameters.vectors || []).map(parameter => {
        const row = materialParameterRow(parameter.name);
        const grid = document.createElement("div");
        grid.style.cssText = "display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:6px;";
        const fields = ["r", "g", "b", "a"].map(channel => {
          const input = materialNumberInput(parameter[channel] ?? (channel === "a" ? 1 : 0));
          input.title = channel.toUpperCase();
          input.onchange = function () {
            send("updateMaterialParameter", {
              id: item.id,
              kind: "vector",
              index: parameter.index,
              r: Number(fields[0].value),
              g: Number(fields[1].value),
              b: Number(fields[2].value),
              a: Number(fields[3].value)
            });
          };
          return input;
        });
        grid.append(...fields);
        row.appendChild(grid);
        return row;
      }));
      const textureOptions = parameters.textureOptions || [];
      appendMaterialGroup(panel, "Texture", (parameters.textures || []).map(parameter => {
        const row = materialParameterRow(parameter.name);
        const select = document.createElement("select");
        select.style.cssText = materialInputStyle();
        const options = textureOptions.length ? textureOptions : [{ rawIndex: parameter.rawIndex, name: parameter.textureName, path: parameter.texturePath }];
        for (const option of options) {
          const opt = document.createElement("option");
          opt.value = String(option.rawIndex);
          opt.textContent = option.path || option.name || String(option.rawIndex);
          opt.selected = option.rawIndex === parameter.rawIndex;
          select.appendChild(opt);
        }
        select.onchange = function () {
          send("updateMaterialParameter", { id: item.id, kind: "texture", index: parameter.index, rawIndex: Number(select.value) });
        };
        row.appendChild(select);
        return row;
      }));

      const remove = document.createElement("button");
      remove.className = "btn btn-sec btn-sm";
      remove.textContent = "绉婚櫎";
      remove.disabled = !!latestState.busy;
      remove.onclick = function () { send("removePatchItem", { id: item.id }); };
      panel.appendChild(remove);
      detail.appendChild(panel);
      return;
    }

    // 普通贴图/音频类型：渲染预览卡片
    if (item.originalPreview || item.replacementPreview) {
      const previews = document.createElement("div");
      previews.style.cssText = "display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-bottom:10px;";
      previews.appendChild(createPatchPreviewCard("原始", item.originalPreview, item.kind === "audio"));
      previews.appendChild(createPatchPreviewCard("替换", item.replacementPreview, item.kind === "audio", item.replacementName || "未选择替换"));
      detail.appendChild(previews);
    }

    const actions = document.createElement("div");
    actions.dataset.prismPatchActions = "true"; actions.setAttribute("data-prism-patch-id", item.id);
    actions.style.display = "flex";
    actions.style.flexWrap = "wrap";
    actions.style.gap = "6px";

    const choose = document.createElement("button");
    choose.className = "btn btn-acc btn-sm";
    choose.textContent = "选择替换图片";
    choose.disabled = !!latestState.busy;
    choose.onclick = function () {
      send("pickPatchReplacementImage", { id: item.id });
    };

    const remove = document.createElement("button");
    remove.className = "btn btn-sec btn-sm";
    remove.textContent = "移除";
    remove.disabled = !!latestState.busy;
    remove.onclick = function () {
      send("removePatchItem", { id: item.id });
    };

    actions.append(choose, remove);
    detail.appendChild(actions);
  }

  function appendMaterialGroup(panel, title, rows) {
    if (!rows.length) return;
    const group = document.createElement("div");
    group.style.cssText = "display:grid;gap:7px;";
    const heading = document.createElement("strong");
    heading.style.cssText = "font-size:12px;";
    heading.textContent = title;
    group.appendChild(heading);
    for (const row of rows) group.appendChild(row);
    panel.appendChild(group);
  }

  function materialParameterRow(labelText) {
    const row = document.createElement("label");
    row.style.cssText = "display:grid;grid-template-columns:minmax(110px,.75fr) minmax(150px,1fr);gap:8px;align-items:center;border:1px solid rgba(0,0,0,.12);border-radius:8px;padding:8px;";
    const label = document.createElement("span");
    label.style.cssText = "font-size:11px;overflow-wrap:anywhere;opacity:.75;";
    label.textContent = labelText || "Parameter";
    row.appendChild(label);
    return row;
  }

  function materialNumberInput(value) {
    const input = document.createElement("input");
    input.type = "number";
    input.step = "0.01";
    input.value = Number(value || 0).toString();
    input.style.cssText = materialInputStyle();
    return input;
  }

  function materialInputStyle() {
    return "width:100%;box-sizing:border-box;border:1px solid rgba(0,0,0,.16);border-radius:8px;padding:7px;font-size:12px;";
  }

  function refreshInjectedControls() {
    ensureOodleToggle();
    ensureExportLogButton();
    ensureUpdateButton();
    ensureMergeControls();
    enhanceEntryBadges();
    augmentPatchDetail();
    refreshPatchDetailButtons();
    renderUnifiedPreview();
  }

  function refreshPatchDetailButtons() {
    const detail = document.getElementById("patchDetail");
    if (!detail) return;
    const buttons = detail.querySelectorAll("button");
    const busy = !!latestState.busy;
    buttons.forEach(function (btn) {
      btn.disabled = busy;
    });
  }

  function scheduleInjectedControls() {
    refreshInjectedControls();
    window.requestAnimationFrame(refreshInjectedControls);
    window.setTimeout(refreshInjectedControls, 280);
  }

  function wrapUiRenderers() {
    const originalRender = window.render;
    if (typeof originalRender === "function" && !originalRender.__prismWrapped) {
      const wrappedRender = function () {
        const result = originalRender.apply(this, arguments);
        renderUnifiedPreview();
        scheduleInjectedControls();
        return result;
      };
      wrappedRender.__prismWrapped = true;
      window.render = wrappedRender;
    }

    const originalSwitchPage = window.switchPage;
    if (typeof originalSwitchPage === "function" && !originalSwitchPage.__prismWrapped) {
      const wrappedSwitchPage = function () {
        const result = originalSwitchPage.apply(this, arguments);
        scheduleInjectedControls();
        return result;
      };
      wrappedSwitchPage.__prismWrapped = true;
      window.switchPage = wrappedSwitchPage;
    }
  }

  function adaptState(next) {
    latestState = next || {};
    entryIndexByPath = new Map();

    const entries = (latestState.entries || []).map(entry => {
      entryIndexByPath.set(entry.path, entry.index);
      return {
        path: entry.path,
        name: entry.name,
        isDirectory: !!entry.isDirectory,
        kind: entry.kind || kindOf(entry),
        size: entry.sizeBytes ?? null,
        thumbnailUrl: entry.thumbnailUrl || null
      };
    });

    const selected = latestState.selectedEntry
      ? {
          path: latestState.selectedEntry.path,
          name: latestState.selectedEntry.name,
          isDirectory: !!latestState.selectedEntry.isDirectory,
          kind: latestState.preview?.kind || latestState.selectedEntry.kind || kindOf(latestState.selectedEntry),
          size: latestState.selectedEntry.sizeBytes ?? null,
          preview: latestState.previewDataUrl || null
        }
      : null;

    const patchItems = (latestState.patchItems || []).map(item => ({
      id: item.id,
      kind: item.kind,
      name: item.name,
      path: item.sourcePath,
      format: item.format,
      sizeLabel: item.sizeLabel,
      status: patchStatus(item.status),
      originalPreview: item.originalPreviewDataUrl || null,
      replacementPreview: item.replacementPreviewDataUrl || null,
      error: item.error || null,
      locresEntries: item.locresEntries || [],
      materialParameters: item.materialParameters || null
    }));

    const selectedPatchIndex = Math.max(0, patchItems.findIndex(item => item.id === latestState.selectedPatchItemId));
    return {
      pakPath: latestState.pakName || "No pak selected",
      usmapPath: latestState.usmapName || "No usmap",
      currentPath: latestState.currentPath || "/",
      entries,
      selectedEntry: selected,
      patchItems,
      selectedPatchIndex: patchItems.length ? selectedPatchIndex : -1,
      busy: !!latestState.busy,
      diagnostics: [
        `Status: ${latestState.status || "Ready"}`,
        `Oodle: ${latestState.oodleStatus || "unknown"}`,
        ...(latestState.diagnostics || [])
      ]
    };
  }

  window.PakToolUI = {
    applyState(next) {
      latestState = next || {};
      if (typeof window.updateState === "function") {
        window.updateState(adaptState(latestState));
        enhanceEntryBadges();
      }
      const status = document.getElementById("status");
      if (status) status.textContent = latestState.status || "就绪";
      const wsStatus = document.getElementById("wsStatus");
      if (wsStatus) wsStatus.textContent = latestState.status || "就绪";
      const typedExport = document.getElementById("exportPng");
      if (typedExport) {
        typedExport.textContent = latestState.exportLabel || "导出";
        typedExport.disabled = !latestState.canExportTyped || !!latestState.busy;
        typedExport.onclick = function () { send("exportTyped"); };
      }
      const rawExport = document.getElementById("exportRaw");
      if (rawExport) rawExport.disabled = !latestState.canExportRaw || !!latestState.busy;
      const addPatchBtn = document.getElementById("addPatchPak");
      if (addPatchBtn) addPatchBtn.disabled = !latestState.canAddToPatchPak || !!latestState.busy;
      const folderExport = document.getElementById("exportFolder");
      if (folderExport) {
        folderExport.disabled = !latestState.canExportFolder || !!latestState.busy;
        folderExport.onclick = function () {
          send("exportFolder", { kind: document.getElementById("folderExportKind")?.value || "raw" });
        };
      }
      const folderExportBtn = document.getElementById("exportFolderBtn");
      if (folderExportBtn) folderExportBtn.disabled = !latestState.canExportFolder || !!latestState.busy;
      const addFolderPatch = document.getElementById("addFolderPatch");
      if (addFolderPatch) {
        addFolderPatch.disabled = !latestState.canAddFolderToPatchPak || !!latestState.busy;
        addFolderPatch.onclick = function () { send("addFolderToPatchPak"); };
      }
      const addFolderPatchBtn = document.getElementById("addFolderPatchBtn");
      if (addFolderPatchBtn) addFolderPatchBtn.disabled = !latestState.canAddFolderToPatchPak || !!latestState.busy;
      const buildBtn = document.getElementById("buildPatchBtn");
      if (buildBtn) buildBtn.disabled = !latestState.canBuildPatchPak || !!latestState.busy;
      const mergeButton = document.getElementById("mergeBtn");
      if (mergeButton) mergeButton.disabled = !latestState.canMergePak || !!latestState.busy;
      const oodleCheck = document.getElementById("oodleCompression");
      if (oodleCheck && latestState.useOodleCompression !== undefined) {
        oodleCheck.checked = !!latestState.useOodleCompression;
      }
      window.__prismState = {
        busy: !!latestState.busy,
        canExportRaw: !!latestState.canExportRaw,
        canExportTyped: !!latestState.canExportTyped,
        canExportFolder: !!latestState.canExportFolder,
        canAddToPatchPak: !!latestState.canAddToPatchPak,
        canAddFolderToPatchPak: !!latestState.canAddFolderToPatchPak,
        canBuildPatchPak: !!latestState.canBuildPatchPak,
        canMergePak: !!latestState.canMergePak,
        exportLabel: latestState.exportLabel || '导出',
        mergePakName: latestState.mergePakName || '',
        useOodleCompression: !!latestState.useOodleCompression
      };
      wrapUiRenderers();
      renderUnifiedPreview();
      scheduleInjectedControls();
    }
  };

  window.native = function (action, arg) {
    try { backendDetected = true; } catch (_) {}

    switch (action) {
      case "pickPak":
      case "pickUsmap":
      case "up":
      case "exportRaw":
      case "exportTyped":
      case "exportPng":
      case "exportFolder":
      case "addFolderToPatchPak":
      case "addSelectedToPatchPak":
      case "exportLog":
      case "checkUpdate":
        send(action === "exportPng" ? "exportTyped" : action, action === "exportFolder" ? { kind: document.getElementById("folderExportKind")?.value || "raw" } : {});
        return;
      case "openPak":
        send("openPak", { aesKey: (document.getElementById("aesKey")?.value || "") });
        return;
      case "search":
        send("search", { query: arg || document.getElementById("search")?.value || "" });
        return;
      case "select":
      case "enter": {
        const index = entryIndexByPath.get(arg);
        if (index !== undefined) send("entry", { index });
        return;
      }
      case "selectPatch": {
        const index = Number.parseInt(arg, 10);
        const item = (latestState.patchItems || [])[index];
        if (item) send("selectPatchItem", { id: item.id });
        return;
      }
      case "buildPatch":
        send("buildPatchPak", {
          useOodleCompression: !!document.getElementById("oodleCompression")?.checked
        });
        return;
      case "entry":
      case "selectPatchItem":
      case "removePatchItem":
      case "pickPatchReplacementImage":
      case "updatePatchLocresEntry":
      case "updateMaterialParameter":
      case "mergePak":
      case "pickMergePak":
      case "buildPatchPak":
        send(action, arg || {});
        return;
      default:
        send(action, typeof arg === "undefined" ? {} : { value: arg });
        return;
    }
  };

  wrapUiRenderers();
  scheduleInjectedControls();
})();
</script>
""";

        var index = html.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? html + script : html.Insert(index, script);
    }

    private static string BuildHtml()
    {
        var embeddedHtml = LoadEmbeddedMainWebViewHtml();
        if (!string.IsNullOrWhiteSpace(embeddedHtml))
            return InjectPrismWebViewBridge(embeddedHtml);

        return """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0, user-scalable=no, viewport-fit=cover" />
  <title>Prism</title>
  <style>
    :root {
      --paper: #fff6e8;
      --panel: #fffaf1;
      --ink: #2c2117;
      --muted: #7d684f;
      --line: #efd7b2;
      --accent: #ff9f45;
      --accent-2: #ffd586;
      --accent-3: #ffe0a3;
      --accent-gradient: linear-gradient(112deg, #FFB772, #FFE0A3, #FFD586);
      --danger: #a43d22;
      --shadow: 0 18px 48px rgba(111, 74, 28, .16);
      --body-bg:
        radial-gradient(circle at top left, rgba(255, 183, 114, .52), transparent 30rem),
        radial-gradient(circle at bottom right, rgba(255, 213, 134, .42), transparent 28rem),
        linear-gradient(145deg, #fff6e8 0%, #fff1d8 100%);
      --panel-glass: rgba(255, 250, 241, .88);
      --panel-solid: rgba(255, 250, 241, .92);
      --soft-panel: rgba(255, 255, 255, .56);
      --hero-border: rgba(151, 98, 35, .12);
      --status-color: #4f2a05;
      --status-bg: linear-gradient(112deg, rgba(255, 183, 114, .92), rgba(255, 224, 163, .96), rgba(255, 213, 134, .92));
      --progress-bg: rgba(255, 159, 69, .16);
      --button-bg: #2c2117;
      --button-fg: #fffaf1;
      --button-secondary-bg: #f4dfbd;
      --button-accent-fg: #4a2705;
      --button-disabled-fg: #a8967c;
      --button-disabled-bg: #ead8bd;
      --input-bg: rgba(255, 255, 255, .72);
      --focus-border: rgba(255, 159, 69, .7);
      --focus-shadow: rgba(255, 183, 114, .22);
      --browser-shadow: 0 12px 34px rgba(111, 74, 28, .1);
      --row-selected: #fff0d1;
      --kind-color: #5a3007;
      --kind-bg: #ffe9bf;
      --kind-file-color: #7a6245;
      --kind-file-bg: #f4dfbd;
      --badge-bg: #f8e7c8;
      --details-bg: linear-gradient(180deg, rgba(255, 247, 232, .64), rgba(255, 250, 241, .96));
      --preview-border: #e2bd83;
      --preview-tile: rgba(111, 74, 28, .045);
      --preview-bg: #fff7e8;
      --spinner-track: rgba(255, 159, 69, .18);
      --toggle-bg: rgba(255, 255, 255, .48);
      font-family: "Noto Sans SC", "HarmonyOS Sans", "MiSans", "Avenir Next", sans-serif;
    }

    body.theme-mint {
      --paper: #f7f4eb;
      --panel: #fffdf7;
      --ink: #20231f;
      --muted: #6f7469;
      --line: #e3dece;
      --accent: #0f766e;
      --accent-2: #d8f36a;
      --accent-3: #9bd8c6;
      --accent-gradient: linear-gradient(112deg, #0f766e, #9bd8c6, #d8f36a);
      --danger: #8b2f1d;
      --shadow: 0 18px 48px rgba(39, 61, 44, .14);
      --body-bg:
        radial-gradient(circle at top left, rgba(216, 243, 106, .55), transparent 30rem),
        radial-gradient(circle at bottom right, rgba(15, 118, 110, .16), transparent 28rem),
        linear-gradient(145deg, #f7f4eb 0%, #edf4e8 100%);
      --panel-glass: rgba(255, 253, 247, .88);
      --panel-solid: rgba(255, 253, 247, .92);
      --soft-panel: rgba(255, 255, 255, .58);
      --hero-border: rgba(31, 63, 52, .11);
      --status-color: #073f39;
      --status-bg: linear-gradient(112deg, rgba(216, 243, 106, .92), rgba(155, 216, 198, .9), rgba(15, 118, 110, .18));
      --progress-bg: rgba(15, 118, 110, .13);
      --button-bg: #20231f;
      --button-fg: #fffdf7;
      --button-secondary-bg: #ebe5d4;
      --button-accent-fg: #17332e;
      --button-disabled-fg: #989b8d;
      --button-disabled-bg: #e5dfcd;
      --input-bg: rgba(255, 255, 255, .72);
      --focus-border: rgba(15, 118, 110, .62);
      --focus-shadow: rgba(216, 243, 106, .28);
      --browser-shadow: 0 12px 34px rgba(39, 61, 44, .09);
      --row-selected: #eef4df;
      --kind-color: #173f39;
      --kind-bg: #e7f2df;
      --kind-file-color: #73776d;
      --kind-file-bg: #ebe5d4;
      --badge-bg: #eef0de;
      --details-bg: linear-gradient(180deg, rgba(247, 244, 235, .72), rgba(255, 253, 247, .96));
      --preview-border: #cfc8b5;
      --preview-tile: rgba(39, 61, 44, .045);
      --preview-bg: #fbf8ed;
      --spinner-track: rgba(15, 118, 110, .16);
      --toggle-bg: rgba(255, 255, 255, .5);
    }

    * { box-sizing: border-box; min-width: 0; }

    html {
      width: 100%;
      min-height: 100%;
      overflow-x: hidden;
      font-size: clamp(13px, 2.8vw, 16px);
    }

    body {
      margin: 0;
      width: 100%;
      min-height: 100%;
      overflow-x: hidden;
      overflow-y: auto;
      color: var(--ink);
      background: var(--body-bg);
    }

    button, input {
      max-width: 100%;
      font: inherit;
    }

    .shell {
      width: min(100%, 1280px);
      min-height: 100vh;
      min-height: 100dvh;
      margin: 0 auto;
      padding: calc(clamp(10px, 3.6vw, 16px) + env(safe-area-inset-top)) clamp(10px, 3.6vw, 16px) calc(clamp(10px, 3.6vw, 18px) + env(safe-area-inset-bottom));
      display: flex;
      flex-direction: column;
      gap: clamp(8px, 2.4vw, 14px);
      overflow: visible;
    }

    .hero {
      display: grid;
      flex: 0 0 auto;
      gap: clamp(8px, 2.4vw, 12px);
      padding: clamp(12px, 3.6vw, 18px);
      border: 1px solid var(--hero-border);
      border-radius: clamp(20px, 6vw, 28px);
      background: var(--panel-glass);
      box-shadow: var(--shadow);
      backdrop-filter: blur(18px);
      overflow: hidden;
    }

    .topline {
      display: grid;
      grid-template-columns: minmax(108px, auto) minmax(0, 1fr);
      align-items: center;
      gap: 12px;
    }

    .brand {
      display: grid;
      gap: 2px;
    }

    .brand h1 {
      margin: 0;
      font-size: clamp(21px, 5.6vw, 25px);
      letter-spacing: -.04em;
      line-height: 1;
    }

    .brand p, .file-meta, .muted {
      margin: 0;
      color: var(--muted);
      font-size: 12px;
    }

    .top-actions {
      min-width: 0;
      width: 100%;
      display: grid;
      grid-template-columns: auto minmax(0, 1fr);
      align-items: center;
      justify-content: flex-end;
      gap: 8px;
    }

    .theme-toggle {
      flex: 0 0 auto;
      min-height: 34px;
      padding: 0 11px;
      border: 1px solid var(--line);
      border-radius: 999px;
      color: var(--ink);
      background: var(--toggle-bg);
      font-size: 12px;
      font-weight: 850;
      white-space: nowrap;
    }

    .status {
      min-width: 0;
      max-width: 100%;
      padding: 8px 10px;
      border-radius: 999px;
      color: var(--status-color);
      background: var(--status-bg);
      font-size: 12px;
      font-weight: 750;
      text-align: right;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    body.busy .status::before {
      content: "";
      display: inline-block;
      width: 7px;
      height: 7px;
      margin-right: 7px;
      border-radius: 999px;
      background: var(--accent);
      animation: pulse .9s infinite alternate;
    }

    @keyframes pulse { from { opacity: .35; transform: scale(.75); } to { opacity: 1; transform: scale(1); } }

    .progress {
      position: relative;
      height: 0;
      overflow: hidden;
      border-radius: 999px;
      background: var(--progress-bg);
      opacity: 0;
      transition: height .18s ease, opacity .18s ease;
    }

    body.busy .progress {
      height: 7px;
      opacity: 1;
    }

    .progress::after {
      content: "";
      position: absolute;
      inset: 0 auto 0 0;
      width: 42%;
      border-radius: inherit;
      background: var(--accent-gradient);
      animation: progress-slide 1.05s ease-in-out infinite;
    }

    @keyframes progress-slide {
      0% { transform: translateX(-110%); }
      55% { transform: translateX(70%); }
      100% { transform: translateX(245%); }
    }

    .chosen {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 158px), 1fr));
      gap: 8px;
    }

    .chip {
      min-width: 0;
      padding: 10px 12px;
      border: 1px solid var(--line);
      border-radius: 18px;
      background: var(--soft-panel);
    }

    .chip strong {
      display: block;
      margin-bottom: 2px;
      font-size: 11px;
      color: var(--muted);
      text-transform: uppercase;
      letter-spacing: .09em;
    }

    .chip span {
      display: block;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: 13px;
      font-weight: 750;
    }

    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 86px), 1fr));
      gap: 8px;
    }

    .button {
      min-height: clamp(39px, 8vw, 43px);
      padding: 7px 10px;
      border: 0;
      border-radius: 16px;
      background: var(--button-bg);
      color: var(--button-fg);
      font-weight: 800;
      letter-spacing: -.01em;
      line-height: 1.15;
      overflow-wrap: anywhere;
      white-space: normal;
    }

    .button.secondary {
      color: var(--ink);
      background: var(--button-secondary-bg);
    }

    .button.accent {
      color: var(--button-accent-fg);
      background: var(--accent-gradient);
    }

    .button:disabled {
      color: var(--button-disabled-fg);
      background: var(--button-disabled-bg);
    }

    .searchbar {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(76px, 96px);
      gap: 8px;
    }

    input {
      min-width: 0;
      width: 100%;
      height: clamp(40px, 8vw, 44px);
      padding: 0 13px;
      border: 1px solid var(--line);
      border-radius: 16px;
      outline: none;
      color: var(--ink);
      background: var(--input-bg);
    }

    input:focus { border-color: var(--focus-border); box-shadow: 0 0 0 4px var(--focus-shadow); }

    .browser {
      flex: 1 1 auto;
      min-height: clamp(430px, 62dvh, 760px);
      display: grid;
      grid-template-rows: auto minmax(180px, 1fr) auto;
      overflow: hidden;
      border: 1px solid var(--hero-border);
      border-radius: 28px;
      background: var(--panel-solid);
      box-shadow: var(--browser-shadow);
    }

    .pathbar {
      display: grid;
      grid-template-columns: minmax(54px, 62px) minmax(0, 1fr) minmax(128px, 168px) minmax(126px, auto) minmax(150px, auto);
      align-items: center;
      gap: 10px;
      padding: 12px;
      border-bottom: 1px solid var(--line);
    }

    .path {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: 13px;
      font-weight: 800;
    }

    .folder-export-kind {
      width: 100%;
      min-width: 0;
      border: 1px solid var(--button-border);
      border-radius: 8px;
      padding: 10px 9px;
      background: var(--button-bg);
      color: var(--text);
      font-size: 12px;
      font-weight: 750;
    }

    .list {
      overflow: auto;
      padding: 8px;
    }

    .row {
      width: 100%;
      display: grid;
      grid-template-columns: 44px minmax(0, 1fr) auto;
      gap: 10px;
      align-items: center;
      padding: 12px 10px;
      border: 0;
      border-radius: 18px;
      color: inherit;
      background: transparent;
      text-align: left;
    }

    .row:active, .row.selected { background: var(--row-selected); }

    .kind {
      width: 40px;
      height: 40px;
      display: grid;
      place-items: center;
      border-radius: 15px;
      color: var(--kind-color);
      background: var(--kind-bg);
      font-size: 10px;
      font-weight: 900;
      letter-spacing: .04em;
    }

    .kind.asset { background: var(--accent-gradient); }
    .kind.file { background: var(--kind-file-bg); color: var(--kind-file-color); }

    .file-title {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: 14px;
      font-weight: 850;
      letter-spacing: -.015em;
    }

    .badge {
      padding: 6px 8px;
      border-radius: 999px;
      color: var(--muted);
      background: var(--badge-bg);
      font-size: 11px;
      font-weight: 800;
      white-space: nowrap;
    }

    .details {
      display: grid;
      grid-template-rows: auto minmax(126px, auto) auto auto auto;
      gap: 12px;
      padding: 12px;
      border-top: 1px solid var(--line);
      background: var(--details-bg);
      overflow: auto;
    }

    .selected {
      overflow: visible;
      overflow-wrap: anywhere;
      white-space: normal;
      font-size: 12px;
      color: var(--muted);
    }

    .replacement-status:empty {
      display: none;
    }

    .preview {
      position: relative;
      min-height: clamp(128px, 24dvh, 220px);
      max-height: min(36dvh, 320px);
      display: grid;
      place-items: center;
      overflow: hidden;
      border: 1px dashed var(--preview-border);
      border-radius: 20px;
      background:
        linear-gradient(45deg, var(--preview-tile) 25%, transparent 25%),
        linear-gradient(-45deg, var(--preview-tile) 25%, transparent 25%),
        var(--preview-bg);
      background-size: 18px 18px;
    }

    .preview-stage {
      position: absolute;
      inset: 0;
      display: grid;
      place-items: center;
      overflow: hidden;
      touch-action: none;
    }

    .preview-stage.dragging {
      cursor: grabbing;
    }

    .preview-image {
      max-width: 100%;
      max-height: calc(100% - 10px);
      object-fit: contain;
      image-rendering: auto;
      pointer-events: none;
      user-select: none;
      transform-origin: center;
      will-change: transform;
    }

    .preview-tools {
      position: absolute;
      top: 8px;
      right: 8px;
      z-index: 2;
      display: flex;
      gap: 6px;
      padding: 5px;
      border: 1px solid var(--line);
      border-radius: 999px;
      background: var(--panel-glass);
      box-shadow: 0 10px 24px rgba(80, 55, 24, .12);
      backdrop-filter: blur(12px);
    }

    .preview-tool {
      width: 31px;
      height: 31px;
      border: 0;
      border-radius: 999px;
      color: var(--ink);
      background: var(--button-secondary-bg);
      font-weight: 900;
      line-height: 1;
    }

    .preview-empty {
      padding: 18px;
      text-align: center;
      color: var(--muted);
      font-size: 13px;
    }

    .preview-loading {
      display: grid;
      place-items: center;
      gap: 10px;
      color: var(--muted);
      font-size: 13px;
      text-align: center;
    }

    .spinner {
      width: 34px;
      height: 34px;
      border: 4px solid var(--spinner-track);
      border-top-color: var(--accent);
      border-radius: 999px;
      animation: spin .78s linear infinite;
    }

    @keyframes spin { to { transform: rotate(360deg); } }

    .actions {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 128px), 1fr));
      gap: 8px;
    }

    .tabs {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
      gap: 8px;
    }

    .tab {
      min-height: 38px;
      border: 1px solid var(--line);
      border-radius: 16px;
      color: var(--muted);
      background: var(--soft-panel);
      font-weight: 900;
    }

    .tab.active {
      color: var(--button-accent-fg);
      background: var(--accent-gradient);
      border-color: transparent;
    }

    .page[hidden] {
      display: none !important;
    }

    .patch-page {
      flex: 1 1 auto;
      min-height: clamp(430px, 62dvh, 760px);
      display: grid;
      grid-template-rows: auto minmax(0, 1fr);
      overflow: hidden;
      border: 1px solid var(--hero-border);
      border-radius: 28px;
      background: var(--panel-solid);
      box-shadow: var(--browser-shadow);
    }

    .patch-header {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(0, auto) minmax(138px, auto);
      align-items: center;
      gap: 10px;
      padding: 12px;
      border-bottom: 1px solid var(--line);
    }

    .patch-title {
      display: grid;
      gap: 3px;
    }

    .patch-title strong {
      font-size: 14px;
      font-weight: 900;
    }

    .patch-title span {
      color: var(--muted);
      font-size: 12px;
      overflow-wrap: anywhere;
    }

    .compression-toggle {
      min-height: 38px;
      display: inline-grid;
      grid-template-columns: auto minmax(0, auto);
      align-items: center;
      gap: 8px;
      padding: 8px 10px;
      border: 1px solid var(--line);
      border-radius: 16px;
      color: var(--muted);
      background: var(--soft-panel);
      font-size: 12px;
      font-weight: 900;
      white-space: nowrap;
    }

    .compression-toggle input {
      width: 18px;
      height: 18px;
      min-height: 0;
      accent-color: var(--accent);
    }

    .patch-workspace {
      min-height: 0;
      display: grid;
      grid-template-columns: minmax(220px, .82fr) minmax(0, 1.18fr);
      overflow: hidden;
    }

    .patch-list {
      min-height: 0;
      overflow: auto;
      padding: 8px;
      border-right: 1px solid var(--line);
    }

    .patch-item {
      width: 100%;
      display: grid;
      gap: 5px;
      padding: 11px;
      border: 0;
      border-radius: 16px;
      color: inherit;
      background: transparent;
      text-align: left;
    }

    .patch-item.active {
      background: var(--row-selected);
    }

    .patch-item strong {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: 13px;
    }

    .patch-item span {
      color: var(--muted);
      font-size: 11px;
      overflow-wrap: anywhere;
    }

    .patch-status {
      justify-self: start;
      padding: 5px 8px;
      border-radius: 999px;
      color: var(--muted);
      background: var(--badge-bg);
      font-size: 11px;
      font-weight: 900;
    }

    .patch-status.replaced {
      color: var(--button-accent-fg);
      background: var(--accent-gradient);
    }

    .patch-status.failed {
      color: var(--danger);
      background: rgba(164, 61, 34, .1);
    }

    .patch-detail {
      min-height: 0;
      display: grid;
      grid-template-rows: auto minmax(0, 1fr) auto;
      gap: 12px;
      padding: 12px;
      overflow: auto;
      background: var(--details-bg);
    }

    .patch-previews {
      min-height: 0;
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 10px;
    }

    .patch-preview-card {
      min-height: 180px;
      display: grid;
      grid-template-rows: auto minmax(0, 1fr);
      gap: 8px;
      padding: 10px;
      border: 1px dashed var(--preview-border);
      border-radius: 18px;
      background: var(--preview-bg);
    }

    .patch-preview-card strong {
      font-size: 12px;
      color: var(--muted);
      text-transform: uppercase;
    }

    .patch-preview-frame {
      min-height: 0;
      display: grid;
      place-items: center;
      overflow: hidden;
    }

    .patch-preview-frame img {
      max-width: 100%;
      max-height: 100%;
      object-fit: contain;
    }

    .patch-empty, .patch-error {
      padding: 18px;
      color: var(--muted);
      text-align: center;
      font-size: 13px;
    }

    .patch-error {
      color: var(--danger);
      background: rgba(164, 61, 34, .08);
      border-radius: 14px;
      text-align: left;
      overflow-wrap: anywhere;
    }

    .diagnostics {
      overflow: hidden;
      border: 1px solid var(--line);
      border-radius: 18px;
      background: rgba(255, 255, 255, .42);
    }

    .diagnostics summary {
      padding: 10px 12px;
      color: var(--muted);
      font-size: 12px;
      font-weight: 900;
      letter-spacing: .02em;
      text-transform: uppercase;
    }

    .diagnostics-log {
      max-height: 160px;
      overflow: auto;
      padding: 0 12px 12px;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      color: var(--muted);
      font-family: "JetBrains Mono", "Cascadia Mono", monospace;
      font-size: 11px;
      line-height: 1.45;
    }

    .empty {
      padding: 42px 18px;
      color: var(--muted);
      text-align: center;
      font-size: 13px;
    }

    @media (max-width: 520px) {
      .badge { display: none; }
    }

    @media (max-width: 440px) {
      .topline {
        grid-template-columns: minmax(0, 1fr);
        align-items: start;
      }

      .top-actions {
        grid-template-columns: auto minmax(0, 1fr);
      }

      .searchbar {
        grid-template-columns: minmax(0, 1fr);
      }

      .patch-header {
        grid-template-columns: minmax(0, 1fr);
      }

      .compression-toggle,
      #buildPatchPak {
        width: 100%;
      }

      .compression-toggle {
        justify-content: start;
      }

      .patch-workspace {
        grid-template-columns: minmax(0, 1fr);
        grid-template-rows: minmax(140px, .72fr) minmax(260px, 1fr);
      }

      .patch-list {
        border-right: 0;
        border-bottom: 1px solid var(--line);
      }

      .patch-previews {
        grid-template-columns: minmax(0, 1fr);
      }
    }

    @media (max-width: 380px) {
      .brand p { display: none; }
      .top-actions { gap: 6px; }
      .theme-toggle { padding: 0 9px; }
      .chip { padding: 9px 10px; }
      .button { padding: 7px 8px; }
      .row { grid-template-columns: 38px minmax(0, 1fr); }
      .kind { width: 36px; height: 36px; border-radius: 13px; }
    }

    @media (orientation: landscape) and (min-width: 640px) {
      html { font-size: clamp(12px, 1.6vw, 15px); }

      .shell {
        display: grid;
        grid-template-rows: auto minmax(360px, 1fr);
        gap: 10px;
        padding: calc(8px + env(safe-area-inset-top)) calc(12px + env(safe-area-inset-right)) calc(8px + env(safe-area-inset-bottom)) calc(12px + env(safe-area-inset-left));
      }

      .hero {
        grid-template-columns: minmax(150px, .78fr) minmax(210px, 1.05fr) minmax(240px, 1.3fr);
        align-items: center;
        gap: 8px 10px;
        padding: 10px;
        border-radius: 22px;
      }

      .topline {
        grid-column: 1;
        grid-row: 1;
      }

      .brand h1 { font-size: clamp(18px, 2.4vw, 22px); }
      .brand p { display: none; }

      .status { padding: 7px 9px; }
      .theme-toggle { min-height: 32px; padding: 0 10px; }

      .progress {
        grid-column: 1 / -1;
        grid-row: 2;
      }

      .chosen {
        grid-column: 2;
        grid-row: 1;
      }

      #aesKey {
        grid-column: 3;
        grid-row: 1;
      }

      .grid {
        grid-column: 1;
        grid-row: 3;
      }

      .searchbar {
        grid-column: 2 / 4;
        grid-row: 3;
      }

      .button {
        min-height: 36px;
        border-radius: 14px;
      }

      input {
        min-height: 36px;
        height: 36px;
        border-radius: 14px;
      }

      .chip {
        padding: 7px 9px;
        border-radius: 15px;
      }

      .chip strong { font-size: 10px; }
      .chip span { font-size: 12px; }

      .browser {
        grid-template-columns: minmax(250px, .95fr) minmax(300px, 1.05fr);
        grid-template-rows: auto minmax(0, 1fr);
        border-radius: 22px;
      }

      .patch-page {
        border-radius: 22px;
      }

      .patch-workspace {
        grid-template-columns: minmax(250px, .82fr) minmax(300px, 1.18fr);
      }

      .pathbar {
        grid-column: 1;
        grid-row: 1;
        grid-template-columns: minmax(54px, 62px) minmax(0, 1fr);
        padding: 10px;
      }

      .folder-export-kind,
      #exportFolder {
        grid-column: span 1;
      }

      .list {
        grid-column: 1;
        grid-row: 2;
      }

      .details {
        grid-column: 2;
        grid-row: 1 / 3;
        grid-template-rows: auto minmax(0, 1fr) auto auto auto;
        border-top: 0;
        border-left: 1px solid var(--line);
        padding: 10px;
        overflow: auto;
      }

      .preview {
        min-height: 0;
        max-height: none;
        height: 100%;
      }

      .preview-image {
        max-height: 100%;
      }

      .row {
        grid-template-columns: 42px minmax(0, 1fr) auto;
        padding: 10px;
      }

      .kind {
        width: 38px;
        height: 38px;
      }
    }

    @media (orientation: landscape) and (max-height: 460px) {
      .shell {
        min-height: auto;
        display: flex;
      }

      .hero {
        grid-template-columns: minmax(145px, .8fr) minmax(180px, 1fr) minmax(220px, 1.25fr);
      }

      .browser {
        min-height: 360px;
      }

      .patch-page {
        min-height: 360px;
      }

      .diagnostics-log {
        max-height: 96px;
      }
    }
  </style>
</head>
<body>
  <main class="shell">
    <section class="hero">
      <div class="topline">
        <div class="brand">
          <h1>Prism</h1>
          <p>UE pak browser for Android</p>
        </div>
        <div class="top-actions">
          <button id="themeToggle" class="theme-toggle" type="button" onclick="toggleTheme()">Warm</button>
          <div id="status" class="status">Ready</div>
        </div>
      </div>
      <div class="progress" aria-hidden="true"></div>

      <div class="tabs" role="tablist" aria-label="Prism workspace">
        <button id="browseTab" class="tab active" type="button" onclick="native('showPage', { page: 'browse' })">Browse</button>
        <button id="patchTab" class="tab" type="button" onclick="native('showPage', { page: 'patch' })">Patch</button>
      </div>

      <div class="chosen">
        <div class="chip"><strong>Pak</strong><span id="pakName">No pak selected</span></div>
        <div class="chip"><strong>Merge</strong><span id="mergePakNameInline">No merge pak</span></div>
        <div class="chip"><strong>Usmap</strong><span id="usmapName">No usmap</span></div>
      </div>

      <input id="aesKey" autocomplete="off" spellcheck="false" placeholder="AES key, optional" />

      <div class="grid">
        <button class="button secondary" onclick="native('pickPak')">Pak</button>
        <button class="button secondary" onclick="native('pickMergePak')">Merge Pak</button>
        <button class="button secondary" onclick="native('pickUsmap')">Usmap</button>
        <button class="button accent" onclick="openPak()">Open</button>
        <label class="compression-toggle"><input id="mergeAskInline" type="checkbox" /> Ask</label>
        <button id="mergePakButtonInline" class="button accent" onclick="mergePakInline()" disabled>Merge</button>
      </div>

      <div class="searchbar">
        <input id="search" autocomplete="off" spellcheck="false" placeholder="Search assets or files" />
        <button class="button secondary" onclick="search()">Search</button>
      </div>
    </section>

    <section id="browsePage" class="browser page">
      <div class="pathbar">
        <button class="button secondary" onclick="native('up')">Up</button>
        <div id="path" class="path">/</div>
        <select id="folderExportKind" class="folder-export-kind" aria-label="Folder export type">
          <option value="raw">Raw</option>
          <option value="model">Models GLB+FBX</option>
          <option value="texture">Textures PNG</option>
          <option value="audio">Audio WAV</option>
          <option value="blueprint">Blueprint CPP</option>
        </select>
        <button id="exportFolder" class="button secondary" onclick="exportCurrentFolder()" disabled>Export Folder</button>
        <button id="addFolderPatch" class="button accent" onclick="native('addFolderToPatchPak')" disabled>Add Folder to Patch</button>
      </div>

      <div id="list" class="list"></div>

      <div class="details">
        <div id="selected" class="selected">No file selected</div>
        <div id="preview" class="preview"><div class="preview-empty">Select a texture asset to preview it.</div></div>
        <div class="actions">
          <button id="exportRaw" class="button secondary" onclick="native('exportRaw')" disabled>Export Raw</button>
          <button id="exportPng" class="button" onclick="native('exportTyped')" disabled>Export</button>
          <button id="addPatchPak" class="button accent" onclick="native('addSelectedToPatchPak')" disabled>Add to Patch Pak</button>
        </div>
        <details class="diagnostics">
          <summary>Diagnostics</summary>
          <div id="diagnostics" class="diagnostics-log">No diagnostics yet.</div>
        </details>
      </div>
    </section>

    <section id="patchPage" class="patch-page page" hidden>
      <div class="patch-header">
        <div class="patch-title">
          <strong>Patch Pak</strong>
          <span id="patchStats">No resources added.</span>
        </div>
        <label class="compression-toggle">
          <input id="oodleCompression" type="checkbox" onchange="setOodleCompression(this.checked)" />
          <span>Oodle compression</span>
        </label>
        <button id="buildPatchPak" class="button accent" onclick="buildPatchPak()" disabled>Build Patch Pak</button>
      </div>

      <div class="patch-workspace">
        <div id="patchList" class="patch-list"></div>
        <div id="patchDetail" class="patch-detail"></div>
      </div>
    </section>
  </main>

  <script>
    let state = {
      status: "Ready",
      busy: false,
      activePage: "browse",
      currentPath: "/",
      pakName: "No pak selected",
      mergePakName: "No merge pak",
      usmapName: "No usmap",
      selectedSummary: null,
      canExportRaw: false,
      canExportPng: false,
      canExportTyped: false,
      canExportFolder: false,
      canAddFolderToPatchPak: false,
      canMergePak: false,
      exportLabel: "Export",
      canAddToPatchPak: false,
      canBuildPatchPak: false,
      patchItemCount: 0,
      replacedPatchItemCount: 0,
      selectedPatchItemId: null,
      patchItems: [],
      useOodleCompression: localStorage.getItem("prism.oodleCompression") === "true",
      previewDataUrl: null,
      previewTitle: null,
      oodleStatus: "Oodle native not checked.",
      diagnostics: [],
      entries: []
    };

    const $ = id => document.getElementById(id);
    const themeLabels = { warm: "Warm", mint: "Mint" };
    let theme = localStorage.getItem("prism.theme") || "warm";
    let previewScale = 1;
    let previewPanX = 0;
    let previewPanY = 0;
    let previewPointerId = null;
    let previewLastX = 0;
    let previewLastY = 0;
    let lastPreviewDataUrl = null;

    function applyTheme() {
      if (!themeLabels[theme]) theme = "warm";
      document.body.classList.toggle("theme-warm", theme === "warm");
      document.body.classList.toggle("theme-mint", theme === "mint");
      const toggle = $("themeToggle");
      if (toggle) toggle.textContent = themeLabels[theme];
    }

    function toggleTheme() {
      theme = theme === "warm" ? "mint" : "warm";
      localStorage.setItem("prism.theme", theme);
      applyTheme();
    }

    function native(action, payload = {}) {
      const encoded = encodeURIComponent(JSON.stringify(payload));
      location.href = `paktool://${action}?payload=${encoded}&t=${Date.now()}`;
    }

    function openPak() {
      native("openPak", { aesKey: $("aesKey").value });
    }

    function search() {
      native("search", { query: $("search").value });
    }

    function exportCurrentFolder() {
      native("exportFolder", { kind: $("folderExportKind").value || "raw" });
    }

    function setOodleCompression(value) {
      state.useOodleCompression = !!value;
      localStorage.setItem("prism.oodleCompression", state.useOodleCompression ? "true" : "false");
    }

    function buildPatchPak() {
      native("buildPatchPak", { useOodleCompression: !!state.useOodleCompression });
    }

    function mergePakInline() {
      native("mergePak", {
        askBeforeReplace: !!$("mergeAskInline")?.checked,
        useOodleCompression: !!state.useOodleCompression,
        aesKey: $("aesKey").value
      });
    }

    $("search").addEventListener("keydown", event => {
      if (event.key === "Enter") search();
    });

    window.PakToolUI = {
      applyState(next) {
        state = { ...state, ...next };
        render();
      }
    };

    function render() {
      document.body.classList.toggle("busy", !!state.busy);
      $("status").textContent = state.status || "Ready";
      $("path").textContent = state.currentPath || "/";
      $("pakName").textContent = state.pakName || "No pak selected";
      const mergeName = $("mergePakNameInline");
      if (mergeName) mergeName.textContent = state.mergePakName || "No merge pak";
      $("usmapName").textContent = state.usmapName || "No usmap";
      $("selected").textContent = state.selectedSummary || "No file selected";
      $("exportRaw").disabled = !state.canExportRaw;
      $("exportPng").disabled = !state.canExportTyped;
      $("exportPng").textContent = state.exportLabel || "Export";
      $("exportFolder").disabled = !state.canExportFolder || !!state.busy;
      $("addFolderPatch").disabled = !state.canAddFolderToPatchPak || !!state.busy;
      const mergeButton = $("mergePakButtonInline");
      if (mergeButton) mergeButton.disabled = !state.canMergePak || !!state.busy;
      $("addPatchPak").disabled = !state.canAddToPatchPak || !!state.busy;
      $("buildPatchPak").disabled = !state.canBuildPatchPak || !!state.busy;
      const oodleCompression = $("oodleCompression");
      if (oodleCompression) {
        oodleCompression.checked = !!state.useOodleCompression;
        oodleCompression.disabled = !!state.busy;
      }
      const onPatch = state.activePage === "patch";
      $("browsePage").hidden = onPatch;
      $("patchPage").hidden = !onPatch;
      $("browseTab").classList.toggle("active", !onPatch);
      $("patchTab").classList.toggle("active", onPatch);
      $("patchStats").textContent = `${state.patchItemCount || 0} resource(s), ${state.replacedPatchItemCount || 0} replaced. Source: ${state.pakName || "No pak"}`;
      renderList();
      renderPreview();
      renderPatchList();
      renderPatchDetail();
      renderDiagnostics();
    }

    function renderDiagnostics() {
      const diagnostics = $("diagnostics");
      if (!diagnostics) return;
      const lines = state.diagnostics || [];
      diagnostics.textContent = lines.length
        ? [`Oodle: ${state.oodleStatus || "unknown"}`, ...lines].join("\n")
        : `Oodle: ${state.oodleStatus || "unknown"}`;
      diagnostics.scrollTop = diagnostics.scrollHeight;
    }

    function renderList() {
      const list = $("list");
      list.replaceChildren();

      if (!state.entries || state.entries.length === 0) {
        const empty = document.createElement("div");
        empty.className = "empty";
        empty.textContent = "No items here yet.";
        list.appendChild(empty);
        return;
      }

      for (const entry of state.entries) {
        const row = document.createElement("button");
        row.className = "row";
        row.onclick = () => native("entry", { index: entry.index });

        const metaInfo = entryKindMeta(entry);
        const kind = document.createElement("div");
        kind.className = "kind";
        kind.textContent = metaInfo.icon;
        kind.style.background = metaInfo.bg;
        kind.style.color = metaInfo.color;

        let thumbnail = null;
        if (entry.thumbnailUrl) {
          thumbnail = document.createElement("img");
          thumbnail.src = entry.thumbnailUrl;
          thumbnail.alt = "";
          thumbnail.style.cssText = "width:42px;height:42px;object-fit:contain;border:1px solid var(--line);border-radius:6px;background:rgba(255,255,255,.65);";
          row.style.gridTemplateColumns = "42px 48px minmax(0,1fr) auto";
        }

        const middle = document.createElement("div");
        const title = document.createElement("div");
        title.className = "file-title";
        title.textContent = entry.name;
        const meta = document.createElement("div");
        meta.className = "file-meta";
        meta.textContent = entry.isDirectory
          ? entry.path
          : `${entry.size}${entry.relatedCount > 1 ? " / " + entry.relatedCount + " parts" : ""}`;
        middle.append(title, meta);

        const badge = document.createElement("div");
        badge.className = "badge";
        badge.textContent = metaInfo.label;

        if (thumbnail) row.append(kind, thumbnail, middle, badge);
        else row.append(kind, middle, badge);
        list.appendChild(row);
      }
    }

    function entryKindMeta(entry) {
      const kind = String(entry.kind || (entry.isDirectory ? "Folder" : entry.extension || "File")).toLowerCase();
      if (entry.isDirectory || kind === "folder") return { icon: "DIR", label: "Folder", color: "#5b4636", bg: "#f2dfc3" };
      if (kind.includes("texture") || kind === "image") return { icon: "IMG", label: "Texture", color: "#1f6feb", bg: "rgba(31,111,235,.13)" };
      if (kind === "audio") return { icon: "AUD", label: "Audio", color: "#16803c", bg: "rgba(22,128,60,.13)" };
      if (kind === "video") return { icon: "VID", label: "Video", color: "#b45309", bg: "rgba(180,83,9,.15)" };
      if (kind === "model") return { icon: "3D", label: "Model", color: "#6f42c1", bg: "rgba(111,66,193,.14)" };
      if (kind === "material") return { icon: "MAT", label: "Material", color: "#7c3aed", bg: "rgba(124,58,237,.13)" };
      if (kind === "blueprint") return { icon: "BP", label: "Blueprint", color: "#0969da", bg: "rgba(9,105,218,.14)" };
      if (kind === "locres") return { icon: "LOC", label: "Locres", color: "#0f766e", bg: "rgba(15,118,110,.14)" };
      if (entry.isAssetPackage) return { icon: "UE", label: "UAsset", color: "#5f3dc4", bg: "rgba(95,61,196,.12)" };
      return { icon: "FILE", label: entry.extension || "File", color: "var(--kind-file-color)", bg: "var(--kind-file-bg)" };
    }

    function selectedPatchItem() {
      const items = state.patchItems || [];
      return items.find(item => item.id === state.selectedPatchItemId) || items[0] || null;
    }

    function patchStatusClass(status) {
      const normalized = (status || "Original").toLowerCase();
      if (normalized === "replaced") return "patch-status replaced";
      if (normalized === "failed") return "patch-status failed";
      return "patch-status";
    }

    function renderPatchList() {
      const list = $("patchList");
      if (!list) return;
      list.replaceChildren();

      const items = state.patchItems || [];
      if (items.length === 0) {
        const empty = document.createElement("div");
        empty.className = "patch-empty";
        empty.textContent = "Add resources or folders from Browse to create a Patch Pak.";
        list.appendChild(empty);
        return;
      }

      for (const item of items) {
        const row = document.createElement("button");
        row.className = "patch-item";
        row.classList.toggle("active", item.id === state.selectedPatchItemId);
        row.onclick = () => native("selectPatchItem", { id: item.id });

        const title = document.createElement("strong");
        title.textContent = item.name || "Texture";
        const path = document.createElement("span");
        path.textContent = item.sourcePath || "";
        const meta = document.createElement("span");
        meta.textContent = `${patchKindLabel(item.kind)} / ${item.format || "Unknown"} ${item.sizeLabel || `${item.width || 0}x${item.height || 0}`} / ${item.relatedCount || 1} file(s)`;
        const status = document.createElement("div");
        status.className = patchStatusClass(item.status);
        status.textContent = item.status || "Original";

        row.append(title, path, meta, status);
        list.appendChild(row);
      }
    }

    function renderPatchDetail() {
      const detail = $("patchDetail");
      if (!detail) return;
      detail.replaceChildren();

      const item = selectedPatchItem();
      if (!item) {
        const empty = document.createElement("div");
        empty.className = "patch-empty";
        empty.textContent = "No Patch Pak resource selected.";
        detail.appendChild(empty);
        return;
      }

      const summary = document.createElement("div");
      summary.className = "selected";
      summary.textContent = `${item.sourcePath} (${patchKindLabel(item.kind)}, ${item.format || "Unknown"}, ${item.sizeLabel || `${item.width || 0}x${item.height || 0}`})`;

      detail.appendChild(summary);

      if (item.kind === "locres") {
        renderLocresPatchEditor(detail, item);
        appendPatchRemoveAction(detail, item);
        return;
      }

      if (item.kind === "material") {
        renderMaterialPatchEditor(detail, item);
        appendPatchRemoveAction(detail, item);
        return;
      }

      if (item.kind === "raw-folder") {
        const note = document.createElement("div");
        note.className = "patch-empty";
        note.textContent = `This item contains ${item.relatedCount || 0} raw file(s) from the selected folder and all subfolders.`;
        detail.appendChild(note);
        appendPatchRemoveAction(detail, item);
        return;
      }

      const previews = document.createElement("div");
      previews.className = "patch-previews";
      previews.append(
        createPatchPreview("Original", item.originalPreviewDataUrl, "Original texture preview"),
        createPatchPreview("Replacement", item.replacementPreviewDataUrl, item.replacementName || "No replacement selected")
      );

      const actions = document.createElement("div");
      actions.className = "actions";
      const choose = document.createElement("button");
      choose.className = "button accent";
      choose.textContent = item.kind === "audio" ? "Choose Audio" : "Choose Replacement";
      choose.disabled = !!state.busy;
      choose.onclick = () => native("pickPatchReplacementImage", { id: item.id });
      const remove = document.createElement("button");
      remove.className = "button secondary";
      remove.textContent = "Remove";
      remove.disabled = !!state.busy;
      remove.onclick = () => native("removePatchItem", { id: item.id });
      actions.append(choose, remove);

      detail.append(previews);
      if (item.error) {
        const error = document.createElement("div");
        error.className = "patch-error";
        error.textContent = item.error;
        detail.appendChild(error);
      }
      detail.appendChild(actions);
    }

    function patchKindLabel(kind) {
      if (kind === "audio") return "Audio";
      if (kind === "locres") return "Locres";
      if (kind === "material") return "Material";
      if (kind === "raw-folder") return "Folder";
      return "Texture";
    }

    function appendPatchRemoveAction(detail, item) {
      if (item.error) {
        const error = document.createElement("div");
        error.className = "patch-error";
        error.textContent = item.error;
        detail.appendChild(error);
      }
      const actions = document.createElement("div");
      actions.className = "actions";
      const remove = document.createElement("button");
      remove.className = "button secondary";
      remove.textContent = "Remove";
      remove.disabled = !!state.busy;
      remove.onclick = () => native("removePatchItem", { id: item.id });
      actions.appendChild(remove);
      detail.appendChild(actions);
    }

    function renderLocresPatchEditor(detail, item) {
      const entries = item.locresEntries || [];
      const editor = document.createElement("div");
      editor.style.cssText = "display:grid;gap:8px;min-height:0;overflow:auto;";
      const search = document.createElement("input");
      search.type = "search";
      search.placeholder = "Find namespace, key, or text";
      search.autocomplete = "off";
      search.style.cssText = "position:sticky;top:0;z-index:2;width:100%;box-sizing:border-box;border:1px solid var(--line);border-radius:8px;padding:9px;background:var(--panel);color:var(--text);font-size:12px;";
      const count = document.createElement("div");
      count.style.cssText = "font-size:11px;color:var(--text2);";
      const rows = document.createElement("div");
      rows.style.cssText = "display:grid;gap:8px;";
      const renderRows = () => {
        const query = search.value.trim().toLocaleLowerCase();
        const matches = query
          ? entries.filter(entry => `${entry.ns || ""}\n${entry.key || ""}\n${entry.text || ""}`.toLocaleLowerCase().includes(query))
          : entries;
        count.textContent = `${matches.length} match(es) / ${entries.length} entries`;
        rows.replaceChildren();
        for (const entry of matches.slice(0, 300)) {
          const row = document.createElement("div");
          row.style.cssText = "display:grid;grid-template-columns:minmax(0,.9fr) minmax(0,1.2fr);gap:8px;border:1px solid var(--line);border-radius:8px;padding:8px;background:rgba(255,255,255,.42);";
          const name = document.createElement("div");
          name.style.cssText = "font-size:11px;color:var(--text2);overflow-wrap:anywhere;";
          name.textContent = `${entry.ns || ""}::${entry.key || ""}`;
          const input = document.createElement("textarea");
          input.value = entry.text || "";
          input.rows = 3;
          input.style.cssText = "width:100%;box-sizing:border-box;resize:vertical;border:1px solid var(--line);border-radius:8px;padding:7px;background:var(--panel);color:var(--text);font-size:12px;";
          input.onchange = () => native("updatePatchLocresEntry", { id: item.id, index: entry.index, text: input.value });
          row.append(name, input);
          rows.appendChild(row);
        }
        if (matches.length > 300) {
          const more = document.createElement("div");
          more.className = "patch-empty";
          more.textContent = `Showing first 300 of ${matches.length} matches.`;
          rows.appendChild(more);
        }
      };
      search.oninput = renderRows;
      editor.append(search, count, rows);
      renderRows();
      detail.appendChild(editor);
    }

    function renderMaterialPatchEditor(detail, item) {
      const params = item.materialParameters || {};
      const scalars = params.scalars || [];
      const vectors = params.vectors || [];
      const textures = params.textures || [];
      const textureOptions = params.textureOptions || [];
      const editor = document.createElement("div");
      editor.style.cssText = "display:grid;gap:10px;min-height:0;overflow:auto;";

      const addGroup = (title, rows) => {
        if (!rows.length) return;
        const group = document.createElement("div");
        group.style.cssText = "display:grid;gap:7px;";
        const heading = document.createElement("strong");
        heading.style.cssText = "font-size:12px;color:var(--text);";
        heading.textContent = title;
        group.appendChild(heading);
        for (const row of rows) group.appendChild(row);
        editor.appendChild(group);
      };

      addGroup("Scalar", scalars.map(parameter => {
        const row = materialRow(parameter.name);
        const input = document.createElement("input");
        input.type = "number";
        input.step = "0.01";
        input.value = Number(parameter.value || 0).toString();
        input.style.cssText = materialInputStyle();
        input.onchange = () => native("updateMaterialParameter", {
          id: item.id,
          kind: "scalar",
          index: parameter.index,
          value: Number(input.value)
        });
        row.appendChild(input);
        return row;
      }));

      addGroup("Vector", vectors.map(parameter => {
        const row = materialRow(parameter.name);
        const grid = document.createElement("div");
        grid.style.cssText = "display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:6px;";
        const fields = ["r", "g", "b", "a"].map(channel => {
          const input = document.createElement("input");
          input.type = "number";
          input.step = "0.01";
          input.value = Number(parameter[channel] ?? (channel === "a" ? 1 : 0)).toString();
          input.title = channel.toUpperCase();
          input.style.cssText = materialInputStyle();
          input.onchange = () => native("updateMaterialParameter", {
            id: item.id,
            kind: "vector",
            index: parameter.index,
            r: Number(fields[0].value),
            g: Number(fields[1].value),
            b: Number(fields[2].value),
            a: Number(fields[3].value)
          });
          return input;
        });
        grid.append(...fields);
        row.appendChild(grid);
        return row;
      }));

      addGroup("Texture", textures.map(parameter => {
        const row = materialRow(parameter.name);
        const select = document.createElement("select");
        select.style.cssText = materialInputStyle();
        const allOptions = textureOptions.length ? textureOptions : [{ rawIndex: parameter.rawIndex, name: parameter.textureName, path: parameter.texturePath }];
        for (const option of allOptions) {
          const itemOption = document.createElement("option");
          itemOption.value = String(option.rawIndex);
          itemOption.textContent = option.path || option.name || String(option.rawIndex);
          itemOption.selected = option.rawIndex === parameter.rawIndex;
          select.appendChild(itemOption);
        }
        select.onchange = () => native("updateMaterialParameter", {
          id: item.id,
          kind: "texture",
          index: parameter.index,
          rawIndex: Number(select.value)
        });
        row.appendChild(select);
        return row;
      }));

      if (!editor.childElementCount) {
        const empty = document.createElement("div");
        empty.className = "patch-empty";
        empty.textContent = "No editable material instance parameters were found.";
        editor.appendChild(empty);
      }

      detail.appendChild(editor);
    }

    function materialRow(labelText) {
      const row = document.createElement("label");
      row.style.cssText = "display:grid;grid-template-columns:minmax(120px,.75fr) minmax(160px,1fr);gap:8px;align-items:center;border:1px solid var(--line);border-radius:8px;padding:8px;background:rgba(255,255,255,.42);";
      const label = document.createElement("span");
      label.style.cssText = "font-size:11px;color:var(--text2);overflow-wrap:anywhere;";
      label.textContent = labelText || "Parameter";
      row.appendChild(label);
      return row;
    }

    function materialInputStyle() {
      return "width:100%;box-sizing:border-box;border:1px solid var(--line);border-radius:8px;padding:7px;background:var(--panel);color:var(--text);font-size:12px;";
    }

    function createPatchPreview(label, dataUrl, emptyText) {
      const card = document.createElement("div");
      card.className = "patch-preview-card";
      const title = document.createElement("strong");
      title.textContent = label;
      const frame = document.createElement("div");
      frame.className = "patch-preview-frame";
      if (dataUrl) {
        if (dataUrl.startsWith("data:audio/")) {
          const audio = document.createElement("audio");
          audio.controls = true;
          audio.src = dataUrl;
          audio.style.width = "100%";
          frame.appendChild(audio);
        } else {
          const img = document.createElement("img");
          img.src = dataUrl;
          img.alt = label;
          frame.appendChild(img);
        }
      } else {
        const empty = document.createElement("div");
        empty.className = "patch-empty";
        empty.textContent = emptyText;
        frame.appendChild(empty);
      }

      card.append(title, frame);
      return card;
    }

    function renderPreview() {
      const preview = $("preview");
      preview.replaceChildren();

      if (state.preview && state.preview.kind === "locres" && state.preview.locres) {
        renderLocresPreviewFallback(preview, state.preview.locres);
        return;
      }

      if (state.previewDataUrl !== lastPreviewDataUrl) {
        lastPreviewDataUrl = state.previewDataUrl;
        resetPreviewTransform(false);
      }

      if (state.busy && /preview|Decoding|Encoding/i.test(state.status || "")) {
        const loading = document.createElement("div");
        loading.className = "preview-loading";
        const spinner = document.createElement("div");
        spinner.className = "spinner";
        const label = document.createElement("div");
        label.textContent = state.status || "Loading...";
        loading.append(spinner, label);
        preview.appendChild(loading);
        return;
      }

      if (state.previewDataUrl) {
        const stage = document.createElement("div");
        stage.className = "preview-stage";

        const img = document.createElement("img");
        img.className = "preview-image";
        img.id = "previewImage";
        img.src = state.previewDataUrl;
        img.alt = state.previewTitle || "Texture preview";

        const tools = document.createElement("div");
        tools.className = "preview-tools";
        tools.innerHTML = `
          <button class="preview-tool" type="button" aria-label="Zoom out" onclick="zoomPreview(-0.2)">-</button>
          <button class="preview-tool" type="button" aria-label="Reset preview" onclick="resetPreviewTransform()">1:1</button>
          <button class="preview-tool" type="button" aria-label="Zoom in" onclick="zoomPreview(0.2)">+</button>
        `;

        stage.appendChild(img);
        preview.append(stage, tools);
        wirePreviewStage(stage);
        applyPreviewTransform();
        return;
      }

      const empty = document.createElement("div");
      empty.className = "preview-empty";
      empty.textContent = state.previewTitle || "Select a texture asset to preview it.";
      preview.appendChild(empty);
    }

    function renderLocresPreviewFallback(preview, locres) {
      preview.style.overflow = "auto";
      const entries = locres.entries || [];
      const search = document.createElement("input");
      search.type = "search";
      search.placeholder = "Find namespace, key, or text";
      search.autocomplete = "off";
      search.style.cssText = "position:sticky;top:0;z-index:2;width:calc(100% - 16px);box-sizing:border-box;margin:8px 8px 0;padding:9px;border:1px solid var(--line);border-radius:8px;background:var(--panel);color:var(--text);font-size:12px;";
      const count = document.createElement("div");
      count.style.cssText = "padding:6px 10px 0;font-size:11px;color:var(--text2);";
      const rows = document.createElement("div");
      rows.style.cssText = "display:grid;gap:6px;padding:8px;";
      const renderRows = () => {
        const query = search.value.trim().toLocaleLowerCase();
        const matches = query
          ? entries.filter(entry => `${entry.namespace || entry.ns || ""}\n${entry.key || ""}\n${entry.text || ""}`.toLocaleLowerCase().includes(query))
          : entries;
        count.textContent = `${matches.length} match(es) / ${entries.length} entries`;
        rows.replaceChildren();
        for (const entry of matches.slice(0, 200)) {
          const row = document.createElement("div");
          row.style.cssText = "display:grid;grid-template-columns:minmax(90px,.32fr) minmax(90px,.32fr) minmax(140px,1fr);gap:6px;border:1px solid var(--line);border-radius:8px;padding:7px;background:rgba(255,255,255,.42);font-size:11px;";
          const ns = document.createElement("div");
          ns.style.color = "var(--text2)";
          ns.textContent = entry.namespace || entry.ns || "";
          const key = document.createElement("div");
          key.style.fontWeight = "650";
          key.textContent = entry.key || "";
          const text = document.createElement("div");
          text.style.whiteSpace = "pre-wrap";
          text.textContent = entry.text || "";
          row.append(ns, key, text);
          rows.appendChild(row);
        }
      };
      search.oninput = renderRows;
      preview.append(search, count, rows);
      renderRows();
    }

    function clamp(value, min, max) {
      return Math.max(min, Math.min(max, value));
    }

    function applyPreviewTransform() {
      const img = $("previewImage");
      if (!img) return;
      img.style.transform = `translate(${previewPanX}px, ${previewPanY}px) scale(${previewScale})`;
    }

    function resetPreviewTransform(renderNow = true) {
      previewScale = 1;
      previewPanX = 0;
      previewPanY = 0;
      if (renderNow) applyPreviewTransform();
    }

    function zoomPreview(delta) {
      previewScale = clamp(previewScale + delta, 0.25, 8);
      if (previewScale <= 1) {
        previewPanX = 0;
        previewPanY = 0;
      }
      applyPreviewTransform();
    }

    function wirePreviewStage(stage) {
      stage.onpointerdown = event => {
        previewPointerId = event.pointerId;
        previewLastX = event.clientX;
        previewLastY = event.clientY;
        stage.setPointerCapture(event.pointerId);
        stage.classList.add("dragging");
      };

      stage.onpointermove = event => {
        if (previewPointerId !== event.pointerId) return;
        previewPanX += event.clientX - previewLastX;
        previewPanY += event.clientY - previewLastY;
        previewLastX = event.clientX;
        previewLastY = event.clientY;
        applyPreviewTransform();
      };

      const stopDrag = event => {
        if (previewPointerId !== event.pointerId) return;
        previewPointerId = null;
        stage.classList.remove("dragging");
      };

      stage.onpointerup = stopDrag;
      stage.onpointercancel = stopDrag;
      stage.ondblclick = () => resetPreviewTransform();
      stage.onwheel = event => {
        event.preventDefault();
        zoomPreview(event.deltaY < 0 ? 0.2 : -0.2);
      };
    }

    applyTheme();
    render();
  </script>
</body>
</html>
""";
    }

    private global::Android.Webkit.WebResourceResponse? TryOpenPreviewBlob(
        global::Android.Net.Uri? uri,
        IDictionary<string, string>? requestHeaders)
    {
        if (uri is null ||
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "paktool.local", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = uri.Path ?? string.Empty;
        if (!path.StartsWith("/preview/", StringComparison.OrdinalIgnoreCase))
            return null;

        var id = path["/preview/".Length..];
        if (!_previewBlobStore.TryGet(id, out var blob))
            return null;

        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept-Ranges"] = "bytes",
            ["Cache-Control"] = "no-store"
        };

        if (requestHeaders is not null &&
            requestHeaders.TryGetValue("Range", out var rangeHeader) &&
            TryParseByteRange(rangeHeader, blob.Data.Length, out var start, out var length))
        {
            responseHeaders["Content-Length"] = length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            responseHeaders["Content-Range"] = $"bytes {start}-{start + length - 1}/{blob.Data.Length}";
            return new global::Android.Webkit.WebResourceResponse(
                blob.MimeType,
                null,
                206,
                "Partial Content",
                responseHeaders,
                new MemoryStream(blob.Data, start, length, writable: false));
        }

        responseHeaders["Content-Length"] = blob.Data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new global::Android.Webkit.WebResourceResponse(
            blob.MimeType,
            null,
            200,
            "OK",
            responseHeaders,
            new MemoryStream(blob.Data, writable: false));
    }

    private static bool TryParseByteRange(string rangeHeader, int totalLength, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (totalLength <= 0 || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;

        var range = rangeHeader["bytes=".Length..];
        var dash = range.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
            return false;

        var startText = range[..dash].Trim();
        var endText = range[(dash + 1)..].Trim();
        if (!int.TryParse(startText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out start) ||
            start < 0 ||
            start >= totalLength)
        {
            return false;
        }

        var end = totalLength - 1;
        if (!string.IsNullOrEmpty(endText) &&
            (!int.TryParse(endText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out end) || end < start))
        {
            return false;
        }

        end = Math.Min(end, totalLength - 1);
        length = end - start + 1;
        return length > 0;
    }

    private sealed class PakToolWebViewClient(MainActivity activity) : global::Android.Webkit.WebViewClient
    {
        public override global::Android.Webkit.WebResourceResponse? ShouldInterceptRequest(
            global::Android.Webkit.WebView? view,
            global::Android.Webkit.IWebResourceRequest? request)
        {
            return activity.TryOpenPreviewBlob(request?.Url, request?.RequestHeaders) ?? base.ShouldInterceptRequest(view, request);
        }

        public override bool ShouldOverrideUrlLoading(global::Android.Webkit.WebView? view, global::Android.Webkit.IWebResourceRequest? request)
        {
            return activity.HandleBridgeUri(request?.Url);
        }

        public override bool ShouldOverrideUrlLoading(global::Android.Webkit.WebView? view, string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            try
            {
                return activity.HandleBridgeUri(global::Android.Net.Uri.Parse(url));
            }
            catch
            {
                return false;
            }
        }

        public override void OnPageFinished(global::Android.Webkit.WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            activity._webReady = true;
            StartCompressionWarmup();
            activity.PushState();
            if (Interlocked.Exchange(ref activity._automaticUpdateCheckStarted, 1) == 0)
                _ = activity.CheckForUpdatesAsync(userInitiated: false);
        }
    }

    private sealed class PreviewBlobStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, PreviewBlob> _blobs = new(StringComparer.Ordinal);

        public string Put(byte[] data, string mimeType)
        {
            var id = Guid.NewGuid().ToString("N");
            var copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);
            lock (_lock)
            {
                _blobs[id] = new PreviewBlob(copy, mimeType);
            }

            return "https://paktool.local/preview/" + id;
        }

        public bool TryGet(string id, out PreviewBlob blob)
        {
            lock (_lock)
            {
                return _blobs.TryGetValue(id, out blob!);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _blobs.Clear();
            }
        }
    }

    private sealed record PreviewBlob(byte[] Data, string MimeType);

    private readonly record struct AudioPayloadLocation(string PakPath, string LocalPath, int Offset);

    private readonly record struct MergeInspection(int BaseCount, int MergeCount, int ConflictCount);

    private enum PatchAddResult
    {
        Added,
        AlreadyExists,
        Unsupported
    }

    private sealed class PatchPakItem(
        string id,
        string kind,
        string sourcePath,
        string name,
        IReadOnlyList<string> relatedPaths,
        string workDirectory,
        Dictionary<string, string> originalFiles,
        string format,
        int width,
        int height,
        string? originalPreviewDataUrl)
    {
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public string SourcePath { get; } = sourcePath;
        public string Name { get; } = name;
        public IReadOnlyList<string> RelatedPaths { get; } = relatedPaths;
        public string WorkDirectory { get; } = workDirectory;
        public Dictionary<string, string> OriginalFiles { get; } = originalFiles;
        public Dictionary<string, string> PatchedFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string Format { get; } = format;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public string? OriginalPreviewDataUrl { get; } = originalPreviewDataUrl;
        public string? ReplacementPreviewDataUrl { get; set; }
        public string? ReplacementImagePath { get; set; }
        public string? ReplacementDisplayName { get; set; }
        public List<PakTool.Core.LocresEntryDto> LocresEntries { get; set; } = [];
        public MaterialInstanceParameterSet? MaterialParameters { get; set; }
        public string Status { get; set; } = "Original";
        public string? Error { get; set; }

        public string? OriginalAssetPath => OriginalFiles
            .Where(pair => pair.Key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                           pair.Key.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => PackagePartOrder(pair.Key))
            .Select(pair => pair.Value)
            .FirstOrDefault();
    }
}
