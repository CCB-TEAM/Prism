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
