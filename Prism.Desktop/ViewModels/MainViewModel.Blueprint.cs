using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PakTool.Core.Blueprints;
using Prism.Desktop.Services.Blueprints;

namespace Prism.Desktop.ViewModels;

/// <summary>函数列表里的一行（事件或函数）。</summary>
public sealed class BlueprintFunctionOption
{
    public BlueprintFunctionOption(BlueprintFunctionDto function)
    {
        Key = function.Key;
        DisplayName = string.IsNullOrWhiteSpace(function.DisplayName) ? function.FunctionName : function.DisplayName;
        Kind = function.Kind;
        KindLabel = function.Kind == "event" ? "事件" : "函数";
        Icon = function.Kind == "event" ? "⚡" : "ƒ";
    }

    public string Key { get; }

    public string DisplayName { get; }

    public string Kind { get; }

    public string KindLabel { get; }

    public string Icon { get; }

    public string Tooltip => $"{KindLabel} · {DisplayName}";
}

/// <summary>右侧节点详情里的一行。</summary>
public sealed class BlueprintNodeRow
{
    public BlueprintNodeRow(string label, string value, bool isHeader = false, int indent = 0, string? pinCategory = null)
    {
        Label = label;
        Value = value;
        IsHeader = isHeader;
        Indent = new Thickness(indent * 12, 0, 0, 0);
        PinBrush = pinCategory is null ? null : BlueprintGraphStyle.PinBrush(pinCategory);
    }

    public string Label { get; }

    public string Value { get; }

    public bool IsHeader { get; }

    public Thickness Indent { get; }

    public IBrush? PinBrush { get; }

    public bool HasPin => PinBrush is not null;

    public bool HasValue => !string.IsNullOrEmpty(Value);
}

/// <summary>
/// 蓝图节点图：预览栏里的入口 + 全屏只读画布。
///
/// 数据和画图是分开的：反编译在 PakTool.Core 里做（<see cref="PakArchiveSession.ReadBlueprintGraphAsync"/>），
/// 这里只保存当前资产、函数列表、选中项和节点详情。只读 —— 没有任何写回字节码的入口。
/// </summary>
public partial class MainViewModel
{
    private string? _blueprintSourcePath;
    private int _blueprintLoadVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlueprintSummary))]
    public partial BlueprintGraphDto? BlueprintSummary { get; set; }

    [ObservableProperty]
    public partial bool IsBlueprintGraphOpen { get; set; }

    [ObservableProperty]
    public partial bool IsBlueprintGraphBusy { get; set; }

    [ObservableProperty]
    public partial string BlueprintGraphTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BlueprintGraphSubtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BlueprintGraphStatus { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlueprintGraph))]
    public partial BlueprintGraphViewDto? BlueprintGraph { get; set; }

    [ObservableProperty]
    public partial BlueprintFunctionOption? SelectedBlueprintFunction { get; set; }

    [ObservableProperty]
    public partial int SelectedBlueprintNodeIndex { get; set; } = -1;

    [ObservableProperty]
    public partial bool ShowBlueprintNodePanel { get; set; }

    public bool HasBlueprintSummary => BlueprintSummary is not null;

    public bool HasBlueprintGraph => BlueprintGraph is not null;

    public bool HasSelectedBlueprintNode =>
        BlueprintGraph is { } graph && SelectedBlueprintNodeIndex >= 0 && SelectedBlueprintNodeIndex < graph.Nodes.Count;

    public bool HasBlueprintFunctions => BlueprintFunctions.Count > 0;

    public string BlueprintFunctionSummary => BlueprintSummary is null
        ? string.Empty
        : $"{BlueprintSummary.Functions.Count(f => f.Kind == "event")} 个事件 · " +
          $"{BlueprintSummary.Functions.Count(f => f.Kind != "event")} 个函数";

    public string BlueprintAssetKind => BlueprintSummary?.ClassName ?? string.Empty;

    public ObservableCollection<BlueprintFunctionOption> BlueprintFunctions { get; } = [];

    public ObservableCollection<BlueprintNodeRow> BlueprintNodeRows { get; } = [];

    partial void OnBlueprintSummaryChanged(BlueprintGraphDto? value)
    {
        BlueprintFunctions.Clear();
        BlueprintNodeRows.Clear();
        BlueprintGraph = null;
        SelectedBlueprintFunction = null;
        SelectedBlueprintNodeIndex = -1;
        IsBlueprintGraphOpen = false;

        if (value is not null)
        {
            foreach (var function in value.Functions)
                BlueprintFunctions.Add(new BlueprintFunctionOption(function));
        }

        OnPropertyChanged(nameof(BlueprintFunctionSummary));
        OnPropertyChanged(nameof(BlueprintAssetKind));
        OnPropertyChanged(nameof(HasBlueprintFunctions));
    }

    [RelayCommand]
    private void OpenBlueprintGraph()
    {
        if (BlueprintSummary is null || BlueprintFunctions.Count == 0 || string.IsNullOrWhiteSpace(_blueprintSourcePath))
        {
            StatusText = BlueprintSummary is null ? "选中的资产不是可以从字节码重建的蓝图。" : "这个蓝图没有可显示的函数。";
            return;
        }

        BlueprintGraphTitle = BlueprintSummary.ClassName;
        BlueprintGraphSubtitle = $"{BlueprintSummary.Name} · {BlueprintFunctionSummary}";
        BlueprintGraphStatus = "从 cook 后的 Kismet 字节码重建；只读。";
        ShowBlueprintNodePanel = false;
        IsBlueprintGraphOpen = true;

        // 事件通常是最想看的（按钮点击、Construct…），默认先开第一个事件。
        var preferred = BlueprintFunctions.FirstOrDefault(f => f.Kind == "event") ?? BlueprintFunctions[0];
        if (ReferenceEquals(SelectedBlueprintFunction, preferred))
            _ = LoadBlueprintGraphAsync(preferred);
        else
            SelectedBlueprintFunction = preferred;
    }

    [RelayCommand]
    private void CloseBlueprintGraph()
    {
        IsBlueprintGraphOpen = false;
        SelectedBlueprintFunction = null;
        BlueprintGraph = null;
        BlueprintNodeRows.Clear();
        SelectedBlueprintNodeIndex = -1;
    }

    [RelayCommand]
    private void ToggleBlueprintNodePanel() => ShowBlueprintNodePanel = !ShowBlueprintNodePanel;

    partial void OnSelectedBlueprintFunctionChanged(BlueprintFunctionOption? value)
    {
        if (value is null)
            return;

        _ = LoadBlueprintGraphAsync(value);
    }

    partial void OnSelectedBlueprintNodeIndexChanged(int value)
    {
        BuildBlueprintNodeRows();
        OnPropertyChanged(nameof(HasSelectedBlueprintNode));

        // 宽窗里点了节点就顺手把详情打开；窄窗不抢画布（要自己点「详情」）。
        if (value >= 0 && IsWideLayout)
            ShowBlueprintNodePanel = true;
    }

    partial void OnBlueprintGraphChanged(BlueprintGraphViewDto? value)
    {
        SelectedBlueprintNodeIndex = -1;
        BlueprintNodeRows.Clear();
        if (value is null)
            return;

        BlueprintGraphStatus = string.IsNullOrWhiteSpace(value.Note)
            ? "从 cook 后的 Kismet 字节码重建；只读。"
            : value.Note;
        OnPropertyChanged(nameof(HasBlueprintGraph));
        OnPropertyChanged(nameof(HasSelectedBlueprintNode));
    }

    private async Task LoadBlueprintGraphAsync(BlueprintFunctionOption function)
    {
        var path = _blueprintSourcePath;
        if (path is null)
            return;

        var version = ++_blueprintLoadVersion;
        IsBlueprintGraphBusy = true;
        BlueprintGraphStatus = $"正在反编译 {function.DisplayName}…";
        try
        {
            var graph = await _session.ReadBlueprintGraphAsync(path, function.Key);
            if (version != _blueprintLoadVersion)
                return;

            BlueprintGraph = graph;
        }
        catch (Exception ex)
        {
            if (version != _blueprintLoadVersion)
                return;

            BlueprintGraph = null;
            BlueprintGraphStatus = $"反编译失败：{ex.Message}";
            AddLog($"蓝图反编译失败（{function.Key}）：{ex}");
        }
        finally
        {
            if (version == _blueprintLoadVersion)
                IsBlueprintGraphBusy = false;
        }
    }

    private void BuildBlueprintNodeRows()
    {
        BlueprintNodeRows.Clear();
        var graph = BlueprintGraph;
        if (graph is null || SelectedBlueprintNodeIndex < 0 || SelectedBlueprintNodeIndex >= graph.Nodes.Count)
            return;

        var node = graph.Nodes[SelectedBlueprintNodeIndex];
        BlueprintNodeRows.Add(new BlueprintNodeRow("节点", node.Name, isHeader: true));
        BlueprintNodeRows.Add(new BlueprintNodeRow("类型", node.NodeType));
        if (!string.IsNullOrWhiteSpace(node.Subtitle))
            BlueprintNodeRows.Add(new BlueprintNodeRow("所属", node.Subtitle));
        if (node.StatementIndex >= 0)
            BlueprintNodeRows.Add(new BlueprintNodeRow("字节码", $"语句 #{node.StatementIndex}"));
        if (node.Pure)
            BlueprintNodeRows.Add(new BlueprintNodeRow("标记", "纯函数（无执行引脚）"));
        if (node.IsSignatureInferred)
            BlueprintNodeRows.Add(new BlueprintNodeRow("标记", "函数签名由调用处推断"));

        AppendPins("输入", node.Inputs);
        AppendPins("输出", node.Outputs);
        return;

        void AppendPins(string title, IReadOnlyList<BlueprintPinDto> pins)
        {
            if (pins.Count == 0)
                return;

            BlueprintNodeRows.Add(new BlueprintNodeRow(title, string.Empty, isHeader: true));
            foreach (var pin in pins)
            {
                var type = pin.PinCategory;
                if (!string.IsNullOrEmpty(pin.PinSubCategory) && pin.PinSubCategory != "None")
                    type += $" · {pin.PinSubCategory}";
                if (pin.ContainerType is not ("None" or ""))
                    type += $"（{pin.ContainerType}）";

                var value = string.IsNullOrWhiteSpace(pin.Value) ? type : $"{type} = {pin.Value}";
                BlueprintNodeRows.Add(new BlueprintNodeRow(pin.Name, value, indent: 1, pinCategory: pin.PinCategory));
            }
        }
    }

    /// <summary>预览栏的入口按钮：只有预览真的带了蓝图概要时才显示。</summary>
    public string BlueprintEntryText => BlueprintSummary is null
        ? "打开节点图"
        : $"打开节点图（{BlueprintSummary.Functions.Count} 个函数）";
}
