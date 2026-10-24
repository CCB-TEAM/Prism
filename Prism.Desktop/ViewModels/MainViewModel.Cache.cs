using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Prism.Desktop.Models;
using Prism.Desktop.Services;

namespace Prism.Desktop.ViewModels;

/// <summary>
/// 应用私有目录的占用情况（设置页「缓存与存储」）。
///
/// 为什么要有这一块：手机上选中的文件会被复制到应用私有目录（SAF 只给 content://），
/// 其中 Pak 副本本身就可能几个 GB。看不见就会觉得"这软件怎么越装越大"，
/// 看得见、能一键清，才谈得上是个可控的策略。
/// </summary>
public partial class MainViewModel
{
    private (int Files, long Bytes) _pakCache;
    private (int Files, long Bytes) _assetCache;
    private (int Files, long Bytes) _projectAssets;

    /// <summary>有东西可显示时才出现这块卡片（桌面端正常使用时不该多一行）。</summary>
    public bool HasCacheData => _pakCache.Files + _assetCache.Files + _projectAssets.Files > 0;

    public string PakCacheText => DescribeCache(_pakCache, "没有");

    public string AssetCacheText => DescribeCache(_assetCache, "没有");

    public string ProjectAssetsText => DescribeCache(_projectAssets, "没有");

    public string CacheTotalText =>
        EntryItem.FormatSize(_pakCache.Bytes + _assetCache.Bytes + _projectAssets.Bytes);

    public string CacheNoteText => IsAndroid
        ? "手机上的系统选择器只给 content:// 授权，所以选中的文件会复制一份到这里。原始文件仍在原处，没有被动过。"
        : "这些是应用自己生成的文件。原始 Pak 与素材都没有被改动。";

    private static string DescribeCache((int Files, long Bytes) value, string empty)
        => value.Files == 0 ? empty : $"{value.Files} 个 · {EntryItem.FormatSize(value.Bytes)}";

    /// <summary>重算三处占用并通知界面。进设置页时调一次就够了。</summary>
    public void RefreshCacheUsage()
    {
        _pakCache = AndroidCachePolicy.Measure(Path.Combine(PrivateDataDir(), AndroidCachePolicy.DocumentsFolder));
        _assetCache = AndroidCachePolicy.Measure(Path.Combine(PrivateDataDir(), AndroidCachePolicy.AssetsFolder));
        _projectAssets = AndroidCachePolicy.Measure(Path.Combine(PrivateDataDir(), "projects"));

        OnPropertyChanged(nameof(HasCacheData));
        OnPropertyChanged(nameof(PakCacheText));
        OnPropertyChanged(nameof(AssetCacheText));
        OnPropertyChanged(nameof(ProjectAssetsText));
        OnPropertyChanged(nameof(CacheTotalText));
        OnPropertyChanged(nameof(CacheNoteText));
    }

    /// <summary>
    /// 清理素材副本。只动 <c>picked/</c>，而且保留当前设置还引用着的那些 ——
    /// Pak 副本（<c>paks/</c>）不在这里清：那是在用的东西，删了就得重新选包。
    /// </summary>
    [RelayCommand]
    private void ClearAssetCache()
    {
        IEnumerable<string?> tracked = new[]
            {
                PakPath,
                UsmapPath,
                ConvertSourcePath,
                MergeBaseLocresPath,
            }
            .Concat(MergePaks.Select(p => (string?)p.Path))
            // 正在替换列表里用的素材也要留着：删了它，工程保存时就没素材可收集了。
            .Concat(PatchItems.Select(p => p.ReplacementSourcePath));
        HashSet<string> keep = AndroidCachePolicy.BuildKeepSet(tracked);

        string assetsRoot = Path.Combine(PrivateDataDir(), AndroidCachePolicy.AssetsFolder);
        (int filesBefore, long bytesBefore) = AndroidCachePolicy.Measure(assetsRoot);
        int removed = AndroidCachePolicy.Cleanup(assetsRoot, keep);
        RefreshCacheUsage();

        long freed = Math.Max(0, bytesBefore - AndroidCachePolicy.Measure(assetsRoot).Bytes);
        StatusText = filesBefore == 0
            ? "没有需要清理的素材副本。"
            : removed == 0
                ? "素材副本都在使用中，没有可清理的。"
                : $"已清理 {removed} 个素材副本，释放 {EntryItem.FormatSize(freed)}。";
        AddLog($"清理素材副本：{removed} 个（释放 {EntryItem.FormatSize(freed)}）", "缓存");
    }
}
