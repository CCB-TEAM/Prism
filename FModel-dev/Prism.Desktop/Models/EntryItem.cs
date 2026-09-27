using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PakTool.Core;

namespace Prism.Desktop.Models;

/// <summary>扁平文件列表项（对应 Web 版的 Name/Type/Size 三列，可选缩略图）。</summary>
public sealed partial class EntryItem : ObservableObject
{
    public EntryItem(string fullPath, string name, bool isDirectory, string kind, string sizeText)
    {
        FullPath = fullPath;
        Name = name;
        IsDirectory = isDirectory;
        Kind = kind;
        SizeText = sizeText;
    }

    public string FullPath { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    public string Kind { get; }

    public string SizeText { get; }

    /// <summary>类型图标前缀（DIR/IMG/AUD/VID/3D/MAT/BP/LOC/UE/FILE）。</summary>
    public string KindIcon => IsDirectory ? "DIR" : Kind switch
    {
        "Texture" => "IMG",
        "Audio" => "AUD",
        "Video" => "VID",
        "Model" => "3D",
        "Material" => "MAT",
        "Blueprint" => "BP",
        "Locres" => "LOC",
        "UAsset" => "UE",
        _ => (Kind.Length > 4 ? Kind[..4] : Kind).ToUpperInvariant(),
    };

    /// <summary>次要行：目录显示路径，文件显示大小。</summary>
    public string MetaText => IsDirectory ? FullPath : SizeText;

    /// <summary>懒加载的缩略图（设置中开启后生成）。</summary>
    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    public bool HasThumbnail => Thumbnail is not null;

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));

    /// <summary>是否为可生成缩略图的图片类资源。</summary>
    public bool IsImageKind => !IsDirectory && (Kind == "Texture" || Kind is "PNG" or "JPG" or "JPEG" or "BMP" or "TGA" or "WEBP" or "DDS");

    public static EntryItem Create(ArchiveEntryDto entry) => new(
        entry.FullPath,
        entry.Name,
        entry.IsDirectory,
        GuessKind(entry),
        entry.IsDirectory ? string.Empty : FormatSize(entry.Size));

    public static string GuessKind(ArchiveEntryDto entry)
    {
        if (entry.IsDirectory)
        {
            return "Folder";
        }

        string ext = entry.Extension.TrimStart('.').ToLowerInvariant();
        if (ext == "locres")
        {
            return "Locres";
        }

        if (ext is "wav" or "ogg" or "wem" or "binka" or "opus" or "at9")
        {
            return "Audio";
        }

        if (ext is "mp4" or "webm" or "m4v" or "mov" or "bk2" or "bik")
        {
            return "Video";
        }

        if (!entry.IsAssetPackage)
        {
            return string.IsNullOrWhiteSpace(ext) ? "File" : ext.ToUpperInvariant();
        }

        string path = entry.FullPath.Replace('\\', '/');
        if (path.Contains("/Blueprint", StringComparison.OrdinalIgnoreCase) || path.Contains("/BP_", StringComparison.OrdinalIgnoreCase))
        {
            return "Blueprint";
        }

        if (path.Contains("/Texture", StringComparison.OrdinalIgnoreCase) || path.Contains("/T_", StringComparison.OrdinalIgnoreCase))
        {
            return "Texture";
        }

        if (path.Contains("/Material", StringComparison.OrdinalIgnoreCase) || path.Contains("/MI_", StringComparison.OrdinalIgnoreCase) || path.Contains("/M_", StringComparison.OrdinalIgnoreCase))
        {
            return "Material";
        }

        if (path.Contains("/Mesh", StringComparison.OrdinalIgnoreCase) || path.Contains("/SK_", StringComparison.OrdinalIgnoreCase) || path.Contains("/SM_", StringComparison.OrdinalIgnoreCase))
        {
            return "Model";
        }

        return "UAsset";
    }

    public static string FormatSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = size;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size} B" : $"{value:0.##} {units[unit]}";
    }
}

/// <summary>预览详情行（Label: Value）。</summary>
public sealed record DetailItem(string Label, string Value);
