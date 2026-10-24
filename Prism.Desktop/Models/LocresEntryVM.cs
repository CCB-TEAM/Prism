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
        OriginalText = dto.Text;
    }

    public int Index { get; }

    public string Namespace { get; }

    public string Key { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>
    /// 从 Pak 里读出来的原文。
    ///
    /// 保存工程时只写"改过的条目"（<see cref="IsEdited"/>），所以必须留着原文做对比；
    /// 否则工程里就得塞整份 .locres，既臃肿又会在游戏更新后过期。
    /// </summary>
    public string OriginalText { get; private set; }

    /// <summary>这一条被改过（和原文不同）。</summary>
    public bool IsEdited => !string.Equals(Text, OriginalText, StringComparison.Ordinal);

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(IsEdited));

    public string DisplayKey => string.IsNullOrEmpty(Namespace) ? Key : $"{Namespace}::{Key}";

    /// <summary>应用工程里的译文（加载工程时用）：保留原文，只换译文。</summary>
    public void ApplyTranslation(string text) => Text = text;

    public LocresEntryDto ToDto() => new(Index, Namespace, Key, Text);
}
