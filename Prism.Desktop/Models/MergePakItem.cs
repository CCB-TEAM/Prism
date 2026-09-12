using CommunityToolkit.Mvvm.ComponentModel;

namespace Prism.Desktop.Models;

/// <summary>
/// 合并列表中的一项。列表顺序即覆盖优先级：越靠后优先级越高。
/// </summary>
public sealed partial class MergePakItem : ObservableObject
{
    public MergePakItem(string path, string displayName, bool isBase)
    {
        Path = path;
        DisplayName = displayName;
        IsBase = isBase;
    }

    /// <summary>磁盘路径（Android 上是 SAF 复制到私有目录后的真实路径）。</summary>
    public string Path { get; }

    /// <summary>用于展示的文件名。</summary>
    public string DisplayName { get; }

    /// <summary>
    /// 是否为主 Pak（基底）。主 Pak 永远排在最前，且不允许删除或拖动到其他位置。
    /// </summary>
    public bool IsBase { get; }

    /// <summary>序号标签：主 Pak 固定显示"主"，其余显示优先级（1 起）。</summary>
    [ObservableProperty]
    public partial string OrderLabel { get; set; } = string.Empty;

    /// <summary>是否正在被拖动（UI 用半透明反馈）。</summary>
    [ObservableProperty]
    public partial bool IsDragging { get; set; }

    /// <summary>是否为当前拖动落点（UI 用高亮反馈）。</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    /// <summary>该 Pak 的文件数（检查冲突后填充）。</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>主 Pak 不可移除。</summary>
    public bool CanRemove => !IsBase;

    /// <summary>主 Pak 不可拖动（它永远是基底）。</summary>
    public bool CanDrag => !IsBase;
}
