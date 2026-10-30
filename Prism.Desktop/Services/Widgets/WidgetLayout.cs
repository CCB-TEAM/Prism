namespace Prism.Desktop.Services.Widgets;

public enum WidgetKind
{
    Unknown,
    UserWidget,
    CanvasPanel,
    HorizontalBox,
    VerticalBox,
    ScrollBox,
    Overlay,
    Border,
    Button,
    SizeBox,
    ScaleBox,
    UniformGridPanel,
    GridPanel,
    WrapBox,
    WidgetSwitcher,
    ContentWidget,
    Image,
    TextBlock,
    RichTextBlock,
    ProgressBar,
    Spacer,
    CheckBox,
    Slider,
    EditableText,
    ComboBox,
    Throbber,
}

/// <summary>量一段文字：给定字号（像素）和换行宽度（0 = 不换行），返回占多大。</summary>
public delegate (float Width, float Height) TextMeasurer(WidgetNode node, string text, float fontSizePx, float wrapWidth);

/// <summary>
/// 按 Slate 的规则粗略算一遍控件布局（设计坐标系）。
/// 没写在属性里的槽位参数一律按 UMG 默认值处理。
/// 移植自 CCB-TEAM/Flora 的 FModel.BlueprintGraph.Widgets.WidgetLayout（GPL-3.0）。
/// </summary>
public sealed class WidgetLayout
{
    // UMG 的字号是 96 DPI 下的磅值
    public const float PointsToPixels = 96f / 72f;

    private static readonly Dictionary<string, WidgetKind> KnownKinds = new(StringComparer.Ordinal)
    {
        ["UserWidget"] = WidgetKind.UserWidget,
        ["CanvasPanel"] = WidgetKind.CanvasPanel, ["ConstraintCanvas"] = WidgetKind.CanvasPanel,
        ["HorizontalBox"] = WidgetKind.HorizontalBox,
        ["VerticalBox"] = WidgetKind.VerticalBox,
        ["ScrollBox"] = WidgetKind.ScrollBox, ["ListView"] = WidgetKind.ScrollBox, ["TileView"] = WidgetKind.ScrollBox, ["TreeView"] = WidgetKind.ScrollBox,
        ["Overlay"] = WidgetKind.Overlay, ["StackBox"] = WidgetKind.VerticalBox,
        ["Border"] = WidgetKind.Border, ["BackgroundBlur"] = WidgetKind.Border,
        ["Button"] = WidgetKind.Button,
        ["SizeBox"] = WidgetKind.SizeBox,
        ["ScaleBox"] = WidgetKind.ScaleBox,
        ["UniformGridPanel"] = WidgetKind.UniformGridPanel,
        ["GridPanel"] = WidgetKind.GridPanel,
        ["WrapBox"] = WidgetKind.WrapBox,
        ["WidgetSwitcher"] = WidgetKind.WidgetSwitcher,
        ["ContentWidget"] = WidgetKind.ContentWidget, ["NamedSlot"] = WidgetKind.ContentWidget, ["SafeZone"] = WidgetKind.ContentWidget,
        ["RetainerBox"] = WidgetKind.ContentWidget, ["InvalidationBox"] = WidgetKind.ContentWidget, ["MenuAnchor"] = WidgetKind.ContentWidget,
        ["Image"] = WidgetKind.Image,
        ["TextBlock"] = WidgetKind.TextBlock, ["TextLayoutWidget"] = WidgetKind.TextBlock,
        ["RichTextBlock"] = WidgetKind.RichTextBlock,
        ["ProgressBar"] = WidgetKind.ProgressBar,
        ["Spacer"] = WidgetKind.Spacer,
        ["CheckBox"] = WidgetKind.CheckBox,
        ["Slider"] = WidgetKind.Slider, ["SpinBox"] = WidgetKind.Slider,
        ["EditableText"] = WidgetKind.EditableText, ["EditableTextBox"] = WidgetKind.EditableText,
        ["MultiLineEditableText"] = WidgetKind.EditableText, ["MultiLineEditableTextBox"] = WidgetKind.EditableText,
        ["ComboBoxString"] = WidgetKind.ComboBox, ["ComboBoxKey"] = WidgetKind.ComboBox,
        ["Throbber"] = WidgetKind.Throbber, ["CircularThrobber"] = WidgetKind.Throbber,
    };

    private readonly TextMeasurer _measureText;
    private readonly Func<string, string?>? _superClassOf;
    private readonly Dictionary<string, WidgetKind> _kindCache = new(StringComparer.Ordinal);

    /// <param name="measureText">画字的那一层提供的度量</param>
    /// <param name="superClassOf">原生类名 → 父类名（来自 mappings），用来认出游戏自定义控件</param>
    public WidgetLayout(TextMeasurer measureText, Func<string, string?>? superClassOf = null)
    {
        _measureText = measureText;
        _superClassOf = superClassOf;
    }

    public WidgetKind GetKind(WidgetNode node)
    {
        if (_kindCache.TryGetValue(node.ClassName, out var cached))
            return cached;

        var kind = WidgetKind.Unknown;
        var className = node.ClassName;
        for (var depth = 0; className is not null && depth < 16; depth++)
        {
            if (KnownKinds.TryGetValue(className, out kind))
                break;

            kind = WidgetKind.Unknown;
            className = _superClassOf?.Invoke(className);
        }

        if (kind == WidgetKind.Unknown && node.Children.Count > 0)
            kind = WidgetKind.ContentWidget;

        _kindCache[node.ClassName] = kind;
        return kind;
    }

    public void Layout(WidgetNode root, float width, float height)
    {
        _kindCache.Clear();
        Measure(root);
        Arrange(root, 0, 0, width, height);
    }

    #region 槽位默认值

    private static string DefaultHAlign(string slotClass) => slotClass switch
    {
        "OverlaySlot" or "UniformGridSlot" or "WrapBoxSlot" or "GridSlot" => "HAlign_Left",
        "ButtonSlot" or "ScaleBoxSlot" => "HAlign_Center",
        _ => "HAlign_Fill",
    };

    private static string DefaultVAlign(string slotClass) => slotClass switch
    {
        "OverlaySlot" or "UniformGridSlot" or "WrapBoxSlot" or "GridSlot" => "VAlign_Top",
        "ButtonSlot" or "ScaleBoxSlot" => "VAlign_Center",
        _ => "VAlign_Fill",
    };

    private static WidgetMargin DefaultPadding(string slotClass) => slotClass switch
    {
        "BorderSlot" or "ButtonSlot" => new WidgetMargin(4, 2, 4, 2),
        _ => WidgetMargin.Zero,
    };

    /// <summary>子控件的槽位类型；资产里没存就看父面板会造哪种槽。</summary>
    private string SlotClass(WidgetNode child)
    {
        if (!string.IsNullOrEmpty(child.SlotClassName))
            return child.SlotClassName;

        return child.Parent is null ? string.Empty : GetKind(child.Parent) switch
        {
            WidgetKind.Overlay => "OverlaySlot",
            WidgetKind.Border => "BorderSlot",
            WidgetKind.Button => "ButtonSlot",
            WidgetKind.ScaleBox => "ScaleBoxSlot",
            WidgetKind.UniformGridPanel => "UniformGridSlot",
            WidgetKind.GridPanel => "GridSlot",
            WidgetKind.WrapBox => "WrapBoxSlot",
            _ => string.Empty,
        };
    }

    public WidgetMargin SlotPadding(WidgetNode child) =>
        WidgetProps.Margin(child.SlotProperties, "Padding", DefaultPadding(SlotClass(child)));

    public string SlotHAlign(WidgetNode child) =>
        WidgetProps.Enum(child.SlotProperties, "HorizontalAlignment", DefaultHAlign(SlotClass(child)));

    public string SlotVAlign(WidgetNode child) =>
        WidgetProps.Enum(child.SlotProperties, "VerticalAlignment", DefaultVAlign(SlotClass(child)));

    /// <summary>盒子的填充规则：(是否 Fill, 权重)。UMG 默认是 Automatic。</summary>
    private static (bool Fill, float Value) SlotSize(WidgetNode child)
    {
        var size = WidgetProps.Struct(child.SlotProperties, "Size");
        return size is null
            ? (false, 1)
            : (WidgetProps.Enum(size, "SizeRule", "Automatic") == "Fill", WidgetProps.Float(size, "Value", 1));
    }

    #endregion

    #region 量尺寸

    private void Measure(WidgetNode node)
    {
        foreach (var child in node.Children)
            Measure(child);

        node.IsVisible = !node.IsCollapsed;
        if (!node.IsVisible)
        {
            node.DesiredWidth = node.DesiredHeight = 0;
            return;
        }

        var (width, height) = ComputeDesired(node);
        node.DesiredWidth = Math.Max(0, width);
        node.DesiredHeight = Math.Max(0, height);
    }

    private static IEnumerable<WidgetNode> Visible(WidgetNode node) => node.Children.Where(child => !child.IsCollapsed);

    private static (float, float) ChildWithPadding(WidgetNode child, WidgetLayout layout)
    {
        var padding = layout.SlotPadding(child);
        return (child.DesiredWidth + padding.Horizontal, child.DesiredHeight + padding.Vertical);
    }

    private (float, float) MaxOfChildren(WidgetNode node)
    {
        float width = 0, height = 0;
        foreach (var child in Visible(node))
        {
            var (childWidth, childHeight) = ChildWithPadding(child, this);
            width = Math.Max(width, childWidth);
            height = Math.Max(height, childHeight);
        }

        return (width, height);
    }

    private (float, float) ComputeDesired(WidgetNode node)
    {
        var export = node.Properties;
        switch (GetKind(node))
        {
            case WidgetKind.CanvasPanel:
            {
                float width = 0, height = 0;
                foreach (var child in Visible(node))
                {
                    var (anchorMin, anchorMax, offsets, _, autoSize) = CanvasSlot(child);
                    if (anchorMin.X == anchorMax.X)
                        width = Math.Max(width, offsets.Left + (autoSize ? child.DesiredWidth : offsets.Right));
                    if (anchorMin.Y == anchorMax.Y)
                        height = Math.Max(height, offsets.Top + (autoSize ? child.DesiredHeight : offsets.Bottom));
                }

                return (width, height);
            }
            case WidgetKind.HorizontalBox:
            case WidgetKind.WrapBox:
            {
                float width = 0, height = 0;
                foreach (var child in Visible(node))
                {
                    var (childWidth, childHeight) = ChildWithPadding(child, this);
                    width += childWidth;
                    height = Math.Max(height, childHeight);
                }

                return (width, height);
            }
            case WidgetKind.VerticalBox:
            case WidgetKind.ScrollBox:
            {
                var horizontal = GetKind(node) == WidgetKind.ScrollBox &&
                                 WidgetProps.Enum(export, "Orientation", "Orient_Vertical") == "Orient_Horizontal";
                float width = 0, height = 0;
                foreach (var child in Visible(node))
                {
                    var (childWidth, childHeight) = ChildWithPadding(child, this);
                    if (horizontal)
                    {
                        width += childWidth;
                        height = Math.Max(height, childHeight);
                    }
                    else
                    {
                        height += childHeight;
                        width = Math.Max(width, childWidth);
                    }
                }

                return (width, height);
            }
            case WidgetKind.UniformGridPanel:
            case WidgetKind.GridPanel:
            {
                var (rows, columns, cellWidth, cellHeight) = GridMetrics(node);
                var slotPadding = WidgetProps.Margin(export, "SlotPadding", WidgetMargin.Zero);
                return (columns * (cellWidth + slotPadding.Horizontal), rows * (cellHeight + slotPadding.Vertical));
            }
            case WidgetKind.SizeBox:
            {
                var (width, height) = MaxOfChildren(node);
                if (WidgetProps.Bool(export, "bOverride_MinDesiredWidth")) width = Math.Max(width, WidgetProps.Float(export, "MinDesiredWidth"));
                if (WidgetProps.Bool(export, "bOverride_MinDesiredHeight")) height = Math.Max(height, WidgetProps.Float(export, "MinDesiredHeight"));
                if (WidgetProps.Bool(export, "bOverride_MaxDesiredWidth")) width = Math.Min(width, WidgetProps.Float(export, "MaxDesiredWidth"));
                if (WidgetProps.Bool(export, "bOverride_MaxDesiredHeight")) height = Math.Min(height, WidgetProps.Float(export, "MaxDesiredHeight"));
                if (WidgetProps.Bool(export, "bOverride_WidthOverride")) width = WidgetProps.Float(export, "WidthOverride");
                if (WidgetProps.Bool(export, "bOverride_HeightOverride")) height = WidgetProps.Float(export, "HeightOverride");
                return (width, height);
            }
            case WidgetKind.WidgetSwitcher:
            {
                var active = ActiveSwitcherChild(node);
                return active is null ? (0, 0) : ChildWithPadding(active, this);
            }
            case WidgetKind.Border:
            {
                var (width, height) = MaxOfChildren(node);
                if (node.Children.Count == 0)
                {
                    var padding = WidgetProps.Margin(export, "Padding", new WidgetMargin(4, 2, 4, 2));
                    return (padding.Horizontal, padding.Vertical);
                }

                return (width, height);
            }
            case WidgetKind.Button:
            {
                var (width, height) = MaxOfChildren(node);
                return node.Children.Count == 0 ? (8, 4) : (width, height);
            }
            case WidgetKind.Image:
            {
                var brush = WidgetProps.Struct(export, "Brush");
                return WidgetProps.Vector2(brush, "ImageSize", 32, 32);
            }
            case WidgetKind.TextBlock:
            case WidgetKind.RichTextBlock:
            {
                var text = TextOf(node);
                if (string.IsNullOrEmpty(text))
                    return (0, FontSizePx(node) * 1.2f);

                var margin = WidgetProps.Margin(export, "Margin", WidgetMargin.Zero);
                var (width, height) = _measureText(node, text, FontSizePx(node), 0);
                return (width + margin.Horizontal, height + margin.Vertical);
            }
            case WidgetKind.Spacer:
                return WidgetProps.Vector2(export, "Size", 1, 1);
            case WidgetKind.ProgressBar:
                return (100, 16);
            case WidgetKind.CheckBox:
                return node.Children.Count > 0 ? MaxOfChildren(node) : (24, 24);
            case WidgetKind.Slider:
                return (100, 18);
            case WidgetKind.EditableText:
            case WidgetKind.ComboBox:
                return (120, 30);
            case WidgetKind.Throbber:
                return (32, 32);
            default:
                // UserWidget / ScaleBox / Overlay / ContentWidget / 未知
                return node.Children.Count > 0 ? MaxOfChildren(node) : (100, 30);
        }
    }

    public static string TextOf(WidgetNode node) => WidgetProps.Text(node.Properties, "Text");

    public static float FontSizePx(WidgetNode node)
    {
        var font = WidgetProps.Struct(node.Properties, "Font");
        return WidgetProps.Float(font, "Size", 24) * PointsToPixels;
    }

    public static WidgetNode? ActiveSwitcherChild(WidgetNode switcher)
    {
        var index = WidgetProps.Int(switcher.Properties, "ActiveWidgetIndex");
        return index >= 0 && index < switcher.Children.Count ? switcher.Children[index] : switcher.Children.FirstOrDefault();
    }

    private (int Rows, int Columns, float CellWidth, float CellHeight) GridMetrics(WidgetNode node)
    {
        int rows = 1, columns = 1;
        float cellWidth = 0, cellHeight = 0;
        foreach (var child in Visible(node))
        {
            rows = Math.Max(rows, WidgetProps.Int(child.SlotProperties, "Row") + 1);
            columns = Math.Max(columns, WidgetProps.Int(child.SlotProperties, "Column") + 1);
            var (childWidth, childHeight) = ChildWithPadding(child, this);
            cellWidth = Math.Max(cellWidth, childWidth);
            cellHeight = Math.Max(cellHeight, childHeight);
        }

        return (rows, columns, cellWidth, cellHeight);
    }

    public static ((float X, float Y) AnchorMin, (float X, float Y) AnchorMax, WidgetMargin Offsets, (float X, float Y) Alignment, bool AutoSize) CanvasSlot(WidgetNode child)
    {
        var layoutData = WidgetProps.Struct(child.SlotProperties, "LayoutData");
        var anchors = WidgetProps.Struct(layoutData, "Anchors");
        return (
            WidgetProps.Vector2(anchors, "Minimum"),
            WidgetProps.Vector2(anchors, "Maximum"),
            WidgetProps.Margin(layoutData, "Offsets", new WidgetMargin(0, 0, 100, 30)),
            WidgetProps.Vector2(layoutData, "Alignment"),
            WidgetProps.Bool(child.SlotProperties, "bAutoSize"));
    }

    #endregion

    #region 摆位置

    private void Arrange(WidgetNode node, float x, float y, float width, float height)
    {
        node.X = x;
        node.Y = y;
        node.Width = Math.Max(0, width);
        node.Height = Math.Max(0, height);
        node.ContentScaleX = node.ContentScaleY = 1;
        if (!node.IsVisible)
            return;

        var export = node.Properties;
        switch (GetKind(node))
        {
            case WidgetKind.CanvasPanel:
                foreach (var child in Visible(node))
                {
                    var (anchorMin, anchorMax, offsets, alignment, autoSize) = CanvasSlot(child);
                    ArrangeCanvasAxis(anchorMin.X, anchorMax.X, offsets.Left, offsets.Right, alignment.X, autoSize, child.DesiredWidth, width, out var childX, out var childWidth);
                    ArrangeCanvasAxis(anchorMin.Y, anchorMax.Y, offsets.Top, offsets.Bottom, alignment.Y, autoSize, child.DesiredHeight, height, out var childY, out var childHeight);
                    Arrange(child, x + childX, y + childY, childWidth, childHeight);
                }

                break;

            case WidgetKind.HorizontalBox:
                ArrangeBox(node, x, y, width, height, horizontal: true);
                break;

            case WidgetKind.VerticalBox:
                ArrangeBox(node, x, y, width, height, horizontal: false);
                break;

            case WidgetKind.ScrollBox:
            {
                // 内容按自身尺寸堆起来，可以超出（被裁掉的）滚动框
                var horizontal = WidgetProps.Enum(export, "Orientation", "Orient_Vertical") == "Orient_Horizontal";
                var offset = 0f;
                foreach (var child in Visible(node))
                {
                    var padding = SlotPadding(child);
                    if (horizontal)
                    {
                        var cellWidth = child.DesiredWidth + padding.Horizontal;
                        ArrangeAligned(child, x + offset, y, cellWidth, height);
                        offset += cellWidth;
                    }
                    else
                    {
                        var cellHeight = child.DesiredHeight + padding.Vertical;
                        ArrangeAligned(child, x, y + offset, width, cellHeight);
                        offset += cellHeight;
                    }
                }

                break;
            }
            case WidgetKind.WrapBox:
            {
                float cursorX = 0, cursorY = 0, rowHeight = 0;
                var wrapWidth = WidgetProps.Bool(export, "bExplicitWrapSize") ? WidgetProps.Float(export, "WrapSize", width) : width;
                foreach (var child in Visible(node))
                {
                    var (childWidth, childHeight) = ChildWithPadding(child, this);
                    if (cursorX > 0 && cursorX + childWidth > wrapWidth)
                    {
                        cursorX = 0;
                        cursorY += rowHeight;
                        rowHeight = 0;
                    }

                    ArrangeAligned(child, x + cursorX, y + cursorY, childWidth, childHeight);
                    cursorX += childWidth;
                    rowHeight = Math.Max(rowHeight, childHeight);
                }

                break;
            }
            case WidgetKind.UniformGridPanel:
            case WidgetKind.GridPanel:
            {
                var (rows, columns, _, _) = GridMetrics(node);
                var slotPadding = WidgetProps.Margin(export, "SlotPadding", WidgetMargin.Zero);
                var cellWidth = width / columns;
                var cellHeight = height / rows;
                foreach (var child in Visible(node))
                {
                    var row = WidgetProps.Int(child.SlotProperties, "Row");
                    var column = WidgetProps.Int(child.SlotProperties, "Column");
                    ArrangeAligned(child,
                        x + column * cellWidth + slotPadding.Left, y + row * cellHeight + slotPadding.Top,
                        cellWidth - slotPadding.Horizontal, cellHeight - slotPadding.Vertical);
                }

                break;
            }
            case WidgetKind.WidgetSwitcher:
            {
                var active = ActiveSwitcherChild(node);
                foreach (var child in node.Children)
                {
                    if (ReferenceEquals(child, active))
                        ArrangeAligned(child, x, y, width, height);
                    else
                        Hide(child);
                }

                break;
            }
            case WidgetKind.ScaleBox:
                if (Visible(node).FirstOrDefault() is { } scaled)
                    ArrangeScaled(node, scaled, x, y, width, height);
                break;

            default:
                // Overlay / Border / Button / SizeBox / UserWidget 等单内容控件
                foreach (var child in Visible(node))
                    ArrangeAligned(child, x, y, width, height);
                break;
        }

        foreach (var child in node.Children.Where(child => child.IsCollapsed))
            Hide(child);
    }

    private static void Hide(WidgetNode node)
    {
        foreach (var descendant in node.SelfAndDescendants())
            descendant.IsVisible = false;
    }

    private static void ArrangeCanvasAxis(float anchorMin, float anchorMax, float offsetStart, float offsetEnd, float alignment,
        bool autoSize, float desired, float parentSize, out float position, out float size)
    {
        if (anchorMin == anchorMax)
        {
            // 两个锚点重合：Offsets 是位置（起点）和尺寸（终点）
            size = autoSize ? desired : offsetEnd;
            position = anchorMin * parentSize + offsetStart - size * alignment;
        }
        else
        {
            // 拉伸：Offsets 是相对锚点的边距
            var start = anchorMin * parentSize + offsetStart;
            var end = anchorMax * parentSize - offsetEnd;
            size = end - start;
            position = start - size * alignment;
        }
    }

    private void ArrangeBox(WidgetNode node, float x, float y, float width, float height, bool horizontal)
    {
        var children = Visible(node).ToList();
        float fixedSize = 0, fillWeight = 0;
        foreach (var child in children)
        {
            var padding = SlotPadding(child);
            var (fill, value) = SlotSize(child);
            fixedSize += horizontal ? padding.Horizontal : padding.Vertical;
            if (fill)
                fillWeight += Math.Max(0, value);
            else
                fixedSize += horizontal ? child.DesiredWidth : child.DesiredHeight;
        }

        var remaining = Math.Max(0, (horizontal ? width : height) - fixedSize);
        var offset = 0f;
        foreach (var child in children)
        {
            var padding = SlotPadding(child);
            var (fill, value) = SlotSize(child);
            var content = fill
                ? fillWeight > 0 ? remaining * Math.Max(0, value) / fillWeight : 0
                : horizontal ? child.DesiredWidth : child.DesiredHeight;
            var cell = content + (horizontal ? padding.Horizontal : padding.Vertical);

            if (horizontal)
                ArrangeAligned(child, x + offset, y, cell, height);
            else
                ArrangeAligned(child, x, y + offset, width, cell);

            offset += cell;
        }
    }

    /// <summary>把子控件按槽位内边距和对齐方式放进一个格子。</summary>
    private void ArrangeAligned(WidgetNode child, float cellX, float cellY, float cellWidth, float cellHeight)
    {
        var padding = SlotPadding(child);
        var availX = cellX + padding.Left;
        var availY = cellY + padding.Top;
        var availWidth = Math.Max(0, cellWidth - padding.Horizontal);
        var availHeight = Math.Max(0, cellHeight - padding.Vertical);

        var (childX, childWidth) = Align(SlotHAlign(child), availX, availWidth, child.DesiredWidth);
        var (childY, childHeight) = Align(SlotVAlign(child), availY, availHeight, child.DesiredHeight);
        Arrange(child, childX, childY, childWidth, childHeight);
    }

    private static (float Position, float Size) Align(string alignment, float start, float available, float desired)
    {
        if (alignment.EndsWith("_Fill", StringComparison.Ordinal))
            return (start, available);

        var size = Math.Min(desired, available);
        return alignment switch
        {
            _ when alignment.EndsWith("_Center", StringComparison.Ordinal) => (start + (available - size) / 2, size),
            _ when alignment.EndsWith("_Right", StringComparison.Ordinal) || alignment.EndsWith("_Bottom", StringComparison.Ordinal) => (start + available - size, size),
            _ => (start, size),
        };
    }

    private void ArrangeScaled(WidgetNode scaleBox, WidgetNode child, float x, float y, float width, float height)
    {
        var stretch = WidgetProps.Enum(scaleBox.Properties, "Stretch", "ScaleToFit");
        var desiredWidth = Math.Max(1, child.DesiredWidth);
        var desiredHeight = Math.Max(1, child.DesiredHeight);
        var scale = stretch switch
        {
            "None" => 1f,
            "ScaleToFill" => Math.Max(width / desiredWidth, height / desiredHeight),
            "ScaleToFitX" => width / desiredWidth,
            "ScaleToFitY" => height / desiredHeight,
            "UserSpecified" => WidgetProps.Float(scaleBox.Properties, "UserSpecifiedScale", 1),
            _ => Math.Min(width / desiredWidth, height / desiredHeight),
        };

        var stretchDirection = WidgetProps.Enum(scaleBox.Properties, "StretchDirection", "Both");
        if (stretchDirection == "DownOnly")
            scale = Math.Min(scale, 1);
        else if (stretchDirection == "UpOnly")
            scale = Math.Max(scale, 1);

        var (scaleX, scaleY) = stretch == "Fill" ? (width / desiredWidth, height / desiredHeight) : (scale, scale);
        if (scaleX <= 0 || scaleY <= 0 || float.IsNaN(scaleX) || float.IsNaN(scaleY))
            (scaleX, scaleY) = (1, 1);

        // 和 SScaleBox 一样：先在「未缩放」的区域里按槽位摆好，再把整个子树按原点缩放
        scaleBox.ContentScaleX = scaleX;
        scaleBox.ContentScaleY = scaleY;
        scaleBox.ContentOriginX = x;
        scaleBox.ContentOriginY = y;
        ArrangeAligned(child, x, y, width / scaleX, height / scaleY);
    }

    #endregion
}
