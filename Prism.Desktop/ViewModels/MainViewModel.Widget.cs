using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using PakTool.Core;
using Prism.Desktop.Services.Widgets;

namespace Prism.Desktop.ViewModels;

/// <summary>控件树列表里的一行。</summary>
public sealed partial class WidgetTreeItem : ObservableObject
{
    public WidgetTreeItem(WidgetNode node, int depth)
    {
        Node = node;
        Depth = depth;
        Indent = new Thickness(depth * 12, 0, 0, 0);
        DisplayName = node.DisplayName;
        Note = node.Note;
    }

    public WidgetNode Node { get; }

    public int Depth { get; }

    public Thickness Indent { get; }

    public string DisplayName { get; }

    public string? Note { get; }

    public bool HasNote => !string.IsNullOrEmpty(Note);

    public string Tooltip => Note is null ? Node.ClassName : $"{Node.ClassName}（{Note}）";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>右侧属性表的一行（只读）。</summary>
public sealed class WidgetPropertyRow
{
    public WidgetPropertyRow(string label, string value, int depth = 0, bool isHeader = false, string? tooltip = null)
    {
        Label = label;
        Value = value;
        Depth = depth;
        IsHeader = isHeader;
        Tooltip = tooltip;
        Indent = new Thickness(depth * 12, 0, 0, 0);
    }

    public string Label { get; }

    public string Value { get; }

    public int Depth { get; }

    public bool IsHeader { get; }

    public string? Tooltip { get; }

    public Thickness Indent { get; }

    public bool IsValue => !IsHeader;
}

/// <summary>
/// 预览栏里的「控件蓝图」部分：画布 + 控件树 + 属性表。
/// </summary>
public partial class MainViewModel
{
    private bool _syncingWidgetSelection;

    [ObservableProperty]
    public partial WidgetPreviewDto? WidgetPreview { get; set; }

    [ObservableProperty]
    public partial WidgetNode? WidgetRoot { get; set; }

    [ObservableProperty]
    public partial WidgetNode? SelectedWidgetNode { get; set; }

    [ObservableProperty]
    public partial WidgetTreeItem? SelectedWidgetItem { get; set; }

    [ObservableProperty]
    public partial bool ShowWidgetOutlines { get; set; } = false;

    public bool HasWidgetPreview => WidgetPreview is not null;

    public ObservableCollection<WidgetTreeItem> WidgetTreeItems { get; } = [];

    public ObservableCollection<WidgetPropertyRow> WidgetPropertyRows { get; } = [];

    public string WidgetSummary { get; private set; } = string.Empty;

    partial void OnWidgetPreviewChanged(WidgetPreviewDto? value)
    {
        WidgetTreeItems.Clear();
        WidgetPropertyRows.Clear();
        SelectedWidgetItem = null;
        SelectedWidgetNode = null;
        WidgetRoot = null;
        WidgetSummary = string.Empty;

        if (value?.Root is not null)
        {
            WidgetRoot = WidgetNode.FromDto(value.Root);
            foreach (var node in WidgetRoot.SelfAndDescendants())
            {
                var depth = 0;
                for (var parent = node.Parent; parent is not null; parent = parent.Parent)
                    depth++;

                WidgetTreeItems.Add(new WidgetTreeItem(node, depth));
            }

            WidgetSummary = $"{WidgetTreeItems.Count} 个控件 · {value.Images.Count} 张贴图" +
                            (value.Bindings.Count > 0 ? $" · {value.Bindings.Count} 个绑定" : string.Empty);

            SelectedWidgetItem = WidgetTreeItems.FirstOrDefault();
        }

        OnPropertyChanged(nameof(HasWidgetPreview));
        OnPropertyChanged(nameof(WidgetSummary));
    }

    partial void OnSelectedWidgetNodeChanged(WidgetNode? value)
    {
        // 列表选中与画布选中互相同步，但属性表无论从哪边来都要重建。
        if (!_syncingWidgetSelection)
        {
            _syncingWidgetSelection = true;
            try
            {
                SelectedWidgetItem = WidgetTreeItems.FirstOrDefault(item => ReferenceEquals(item.Node, value));
            }
            finally
            {
                _syncingWidgetSelection = false;
            }
        }

        BuildWidgetPropertyRows(value);
    }

    partial void OnSelectedWidgetItemChanged(WidgetTreeItem? value)
    {
        if (_syncingWidgetSelection)
            return;

        _syncingWidgetSelection = true;
        try
        {
            SelectedWidgetNode = value?.Node;
        }
        finally
        {
            _syncingWidgetSelection = false;
        }
    }

    private void BuildWidgetPropertyRows(WidgetNode? node)
    {
        WidgetPropertyRows.Clear();
        if (node is null)
            return;

        foreach (var row in DescribeHolder($"{node.DisplayName}", node.Properties, includeHeader: true))
            WidgetPropertyRows.Add(row);

        if (node.SlotProperties.Count > 0)
        {
            foreach (var row in DescribeHolder($"槽位 [{node.SlotClassName}]", node.SlotProperties, includeHeader: true))
                WidgetPropertyRows.Add(row);
        }
    }

    private static IEnumerable<WidgetPropertyRow> DescribeHolder(string header, IReadOnlyList<WidgetPropertyDto> properties, bool includeHeader)
    {
        if (includeHeader)
            yield return new WidgetPropertyRow(header, string.Empty, 0, isHeader: true);

        foreach (var property in properties)
        {
            if (property.Name is "Slot" or "Slots")
                continue;

            foreach (var row in DescribeValue(property.Name, property.Value, property.TypeName, 1, 0))
                yield return row;
        }
    }

    private static IEnumerable<WidgetPropertyRow> DescribeValue(string label, WidgetValue value, string typeName, int depth, int level)
    {
        switch (value)
        {
            case WidgetStruct structValue when level < 2:
                yield return new WidgetPropertyRow(label, structValue.TypeName, depth, isHeader: true);
                foreach (var field in structValue.Fields)
                    foreach (var row in DescribeValue(field.Name, field.Value, field.TypeName, depth + 1, level + 1))
                        yield return row;
                break;

            case WidgetStruct structValue:
                yield return new WidgetPropertyRow(label, $"{structValue.TypeName}（{structValue.Fields.Count} 个字段）", depth);
                break;

            default:
                yield return new WidgetPropertyRow(label, FormatWidgetValue(value, typeName), depth, tooltip: typeName);
                break;
        }
    }

    private static string FormatWidgetValue(WidgetValue value, string typeName)
    {
        return value switch
        {
            WidgetText text => text.Value.Replace("\r", string.Empty).Replace("\n", "\\n"),
            WidgetName name => name.Value,
            WidgetEnum enumValue => enumValue.Value,
            WidgetBoolean boolean => boolean.Value ? "true" : "false",
            WidgetNumber number => number.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
            WidgetInteger integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PakTool.Core.WidgetColor color => $"rgba({color.R:0.##}, {color.G:0.##}, {color.B:0.##}, {color.A:0.##})",
            WidgetVector vector => $"({vector.X:0.##}, {vector.Y:0.##})",
            WidgetVector4 vector => $"({vector.X:0.##}, {vector.Y:0.##}, {vector.Z:0.##}, {vector.W:0.##})",
            WidgetObjectRef reference => reference.ImageId is null
                ? $"{reference.TypeName} '{reference.Name}'"
                : $"{reference.TypeName} '{reference.Name}'（已解码）",
            WidgetList list => $"{list.ItemType}[{list.Items.Count}]",
            WidgetRaw raw => raw.Preview,
            WidgetNull => "—",
            _ => typeName,
        };
    }
}
