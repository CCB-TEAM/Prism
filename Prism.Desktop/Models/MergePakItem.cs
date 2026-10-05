using CommunityToolkit.Mvvm.ComponentModel;

namespace Prism.Desktop.Models;

/// <summary>
/// 合并列表中的一项。
///
/// 列表顺序即覆盖优先级：<b>越靠上优先级越高</b>，同一个路径在多份 Pak 里都存在时，
/// 以最上方那份为准。列表里的每一项都可以拖动排序、都可以移除 —— 没有固定的"主 Pak"。
/// </summary>
public sealed partial class MergePakItem : ObservableObject
{
    public MergePakItem(string path, string displayName)
    {
        Path = path;
        DisplayName = displayName;
    }

    /// <summary>磁盘路径（Android 上是 SAF 复制到私有目录后的真实路径）。</summary>
    public string Path { get; }

    /// <summary>用于展示的文件名。</summary>
    public string DisplayName { get; }

    /// <summary>序号标签：从 1 开始，1 表示优先级最高。</summary>
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

    /// <summary>
    /// 任何一项都可以移除。
    /// 保留这个属性（恒为 true）是为了让界面上的删除按钮绑定保持简单。
    /// </summary>
    public bool CanRemove => true;

    /// <summary>任何一项都可以拖动排序。</summary>
    public bool CanDrag => true;
}
