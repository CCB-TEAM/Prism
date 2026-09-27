using CommunityToolkit.Mvvm.ComponentModel;
using PakTool.Core;

namespace Prism.Desktop.Models;

/// <summary>可编辑的本地化条目（包装 LocresEntryDto，支持 UI 双向绑定）。</summary>
public sealed partial class LocresEntryVM : ObservableObject
{
    public LocresEntryVM(LocresEntryDto dto)
    {
        Index = dto.Index;
        Namespace = dto.Namespace;
        Key = dto.Key;
        Text = dto.Text;
    }

    public int Index { get; }

    public string Namespace { get; }

    public string Key { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    public string DisplayKey => string.IsNullOrEmpty(Namespace) ? Key : $"{Namespace}::{Key}";

    public LocresEntryDto ToDto() => new(Index, Namespace, Key, Text);
}
