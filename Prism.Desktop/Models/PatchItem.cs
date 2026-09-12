using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Prism.Desktop.Models;

/// <summary>替换（Patch）任务项，对应 Web 版 patchItems 中的一项。</summary>
public sealed partial class PatchItem : ObservableObject
{
    public PatchItem(
        string kind,
        string sourcePath,
        string name,
        string format,
        string sizeLabel,
        int width,
        int height,
        string workDirectory,
        string inputUassetPath)
    {
        Kind = kind;
        SourcePath = sourcePath;
        Name = name;
        Format = format;
        SizeLabel = sizeLabel;
        Width = width;
        Height = height;
        WorkDirectory = workDirectory;
        InputUassetPath = inputUassetPath;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string Kind { get; }

    public string SourcePath { get; }

    public string Name { get; }

    public string Format { get; }

    public string SizeLabel { get; }

    public int Width { get; }

    public int Height { get; }

    public string WorkDirectory { get; }

    public string InputUassetPath { get; }

    /// <summary>原始文件映射：Pak 内路径 → 本地临时文件。</summary>
    public Dictionary<string, string> OriginalFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>替换后文件映射：Pak 内路径 → 本地临时文件。</summary>
    public Dictionary<string, string> PatchedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ============ 本地化（locres）编辑 ============

    /// <summary>一次最多渲染的条目数（防手机等低配设备渲染爆炸）。</summary>
    public const int MaxLocresRender = 300;

    /// <summary>Android 分页每页条数。</summary>
    public const int LocresPageSize = 100;

    /// <summary>Android 分页渲染；桌面保留虚拟化全量。</summary>
    public bool IsPaged => OperatingSystem.IsAndroid();

    public ObservableCollection<LocresEntryVM> LocresEntries { get; } = [];

    [ObservableProperty]
    public partial string LocresFilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ObservableCollection<LocresEntryVM> FilteredLocresEntries { get; set; } = [];

    /// <summary>列表计数提示（分页/虚拟化说明）。</summary>
    public string LocresFilterSummary { get; private set; } = string.Empty;

    public int LocresPage { get; private set; }

    public int LocresPageCount { get; private set; }

    public string LocresPageText => $"第 {LocresPage + 1} / {LocresPageCount} 页";

    partial void OnLocresFilterTextChanged(string value)
    {
        LocresPage = 0;
        ApplyLocresFilter();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void PrevLocresPage()
    {
        if (LocresPage > 0)
        {
            LocresPage--;
            ApplyLocresFilter();
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void NextLocresPage()
    {
        if (LocresPage < LocresPageCount - 1)
        {
            LocresPage++;
            ApplyLocresFilter();
        }
    }

    public void ApplyLocresFilter()
    {
        IReadOnlyList<LocresEntryVM> source = LocresEntries;
        if (!string.IsNullOrWhiteSpace(LocresFilterText))
        {
            string q = LocresFilterText.Trim().ToLowerInvariant();
            source = LocresEntries.Where(e =>
                (e.Namespace is not null && e.Namespace.ToLowerInvariant().Contains(q, StringComparison.Ordinal)) ||
                e.Key.ToLowerInvariant().Contains(q, StringComparison.Ordinal) ||
                e.Text.ToLowerInvariant().Contains(q, StringComparison.Ordinal)).ToList();
        }

        if (OperatingSystem.IsAndroid())
        {
            // 手机：分页渲染，每页 LocresPageSize 条
            LocresPageCount = Math.Max(1, (int)Math.Ceiling(source.Count / (double)LocresPageSize));
            if (LocresPage >= LocresPageCount)
            {
                LocresPage = LocresPageCount - 1;
            }

            FilteredLocresEntries = new ObservableCollection<LocresEntryVM>(
                source.Skip(LocresPage * LocresPageSize).Take(LocresPageSize));
            LocresFilterSummary = $"第 {LocresPage + 1} / {LocresPageCount} 页 · 共 {source.Count} 条";
        }
        else
        {
            // 桌面：ListBox 虚拟化，全量渲染不卡
            FilteredLocresEntries = new ObservableCollection<LocresEntryVM>(source);
            LocresFilterSummary = $"共 {source.Count} 条（虚拟化渲染）";
        }

        OnPropertyChanged(nameof(LocresFilterSummary));
        OnPropertyChanged(nameof(LocresPageText));
    }

    public bool IsLocres => Kind == "locres";

    public bool IsTexture => Kind == "texture";

    [ObservableProperty]
    public partial string Status { get; set; } = "待替换";

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial Bitmap? OriginalPreview { get; set; }

    [ObservableProperty]
    public partial Bitmap? ReplacementPreview { get; set; }

    [ObservableProperty]
    public partial string? ReplacementName { get; set; }

    public bool IsReplaced => Status == "已替换";

    public bool IsFailed => Status == "失败";

    public bool IsPending => !IsReplaced && !IsFailed;

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsReplaced));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsPending));
    }
}
