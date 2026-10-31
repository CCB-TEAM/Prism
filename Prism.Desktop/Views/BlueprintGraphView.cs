using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PakTool.Core.Blueprints;
using Prism.Desktop.Services.Blueprints;

namespace Prism.Desktop.Views;

/// <summary>
/// 蓝图节点图的只读画布。
///
/// 画什么：节点（圆角框 + 按节点类型着色的标题栏）、端口（按数据类型着色的圆点，
/// exec 是菱形）、连线（贝塞尔曲线，颜色跟着源端口走），以及未连接输入端上的
/// 字面量。坐标直接来自反编译器 + MSAGL 分层布局，这里只做平移缩放和命中测试。
///
/// 为什么自己画而不是用现成的图控件：节点尺寸是反编译器按每个引脚 32px 算出来的，
/// 外部控件没法按这个几何精确摆放引脚，连线会接不到端口上。Avalonia 的
/// DrawingContext 足够画这套东西，Windows 与 Android 表现一致。
/// </summary>
public sealed class BlueprintGraphView : Control
{
    public static readonly StyledProperty<BlueprintGraphViewDto?> GraphProperty =
        AvaloniaProperty.Register<BlueprintGraphView, BlueprintGraphViewDto?>(nameof(Graph));

    public static readonly StyledProperty<int> SelectedNodeIndexProperty =
        AvaloniaProperty.Register<BlueprintGraphView, int>(
            nameof(SelectedNodeIndex), defaultValue: -1,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public BlueprintGraphViewDto? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public int SelectedNodeIndex
    {
        get => GetValue(SelectedNodeIndexProperty);
        set => SetValue(SelectedNodeIndexProperty, value);
    }

    private static readonly Typeface UiTypeface = new(FontFamily.Default);
    private static readonly Typeface MonoTypeface = new(FontFamily.Parse("Consolas, Cascadia Mono, monospace"));

    private const double HeaderHeight = 30;
    private const double PinSpacing = 32;
    private const double FooterHeight = 26;

    private double _fitScale = 1;
    private double _zoom = 1;
    private Point _pan;
    private bool _dragging;
    private bool _moved;
    private Point _lastPointer;
    private int _hoveredIndex = -1;
    private bool _zoomToFit;
    private bool _needsFit = true;

    static BlueprintGraphView()
    {
        AffectsRender<BlueprintGraphView>(GraphProperty, SelectedNodeIndexProperty);
    }

    public BlueprintGraphView()
    {
        ClipToBounds = true;
        Focusable = true;
        DoubleTapped += (_, _) => FitToGraph();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 520 : availableSize.Height;
        return new Size(Math.Max(240, width), Math.Max(200, height));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GraphProperty)
        {
            _hoveredIndex = -1;
            _needsFit = true;
            FitToGraph();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        // 只在第一次拿到真实尺寸时自动适应；之后改窗口大小 / 开关详情面板
        // 都保留用户的缩放与平移，不然一选节点画布就"跳回去"。
        if (_needsFit)
            FitToGraph();
        else
            InvalidateVisual();
    }

    /// <summary>把整张图缩放到画布中央（双击空白处也会调用）。</summary>
    /// <summary>整张图缩到画布内（按钮用）；默认打开时只保证"看得清"。</summary>
    public void FitToGraph(bool wholeGraph = false)
    {
        _zoomToFit = wholeGraph;
        _zoom = 1;
        _pan = default;
        var graph = Graph;
        if (graph is null || Bounds.Width <= 1 || Bounds.Height <= 1)
        {
            InvalidateVisual();
            return;
        }

        var availableWidth = Math.Max(64, Bounds.Width - 56);
        var availableHeight = Math.Max(64, Bounds.Height - FooterHeight - 48);
        var scale = Math.Min(availableWidth / Math.Max(graph.Width, 1), availableHeight / Math.Max(graph.Height, 1));
        // 默认按"看得清"来：整张图缩到 20% 时字全糊了，宁可让用户左右拖。
        // 顶部的「适应画布」按钮仍然可以缩到完全可见。
        _fitScale = Math.Clamp(scale, _zoomToFit ? 0.06 : 0.5, 2.2);
        _needsFit = false;
        InvalidateVisual();
    }

    /// <summary>以画布中心为锚点缩放（触摸设备没有滚轮，靠标题栏的 +/−）。</summary>
    public void ZoomBy(double factor)
    {
        if (Graph is null)
            return;

        var center = new Point(Bounds.Width / 2, (Bounds.Height - FooterHeight) / 2);
        var anchor = ToGraph(center, Scale);
        var newZoom = Math.Clamp(_zoom * factor, 0.2, 4);
        if (Math.Abs(newZoom - _zoom) < 0.0001)
            return;

        _zoom = newZoom;
        var scale = Scale;
        var origin = GraphOriginScreen(scale);
        var newOriginX = center.X - (anchor.X - Graph.MinX) * scale;
        var newOriginY = center.Y - (anchor.Y - Graph.MinY) * scale;
        _pan += new Vector(newOriginX - origin.X, newOriginY - origin.Y);
        InvalidateVisual();
    }

    private double Scale => _fitScale * _zoom;

    private Point GraphOriginScreen(double scale)
    {
        var graph = Graph!;
        var width = graph.Width * scale;
        var height = graph.Height * scale;
        var availableWidth = Math.Max(64, Bounds.Width);
        var availableHeight = Math.Max(64, Bounds.Height - FooterHeight);

        // 整张图放得下就居中；放不下就从左上角开始 —— 打开时先看到入口节点，
        // 而不是从图的中间开始（那样第一眼全是线，找不到从哪读起）。
        var x = width <= availableWidth - 40 ? (availableWidth - width) / 2 : 24;
        var y = height <= availableHeight - 40 ? (availableHeight - height) / 2 : 24;
        return new Point(x + _pan.X, y + _pan.Y);
    }

    private Point ToScreen(Point graphPoint, double scale) =>
        GraphOriginScreen(scale) + new Vector(
            (graphPoint.X - Graph!.MinX) * scale,
            (graphPoint.Y - Graph!.MinY) * scale);

    private Point ToGraph(Point screenPoint, double scale)
    {
        var origin = GraphOriginScreen(scale);
        return new Point(
            (screenPoint.X - origin.X) / scale + Graph!.MinX,
            (screenPoint.Y - origin.Y) / scale + Graph!.MinY);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(BlueprintGraphStyle.CanvasBackground, bounds);

        var graph = Graph;
        if (graph is null || graph.Nodes.Count == 0)
        {
            DrawEmptyState(context, graph?.Note);
            return;
        }

        var scale = Scale;
        DrawGrid(context, bounds, scale);

        using (context.PushClip(bounds))
        using (context.PushTransform(
                   Matrix.CreateTranslation(-graph.MinX, -graph.MinY) *
                   Matrix.CreateScale(scale, scale) *
                   Matrix.CreateTranslation(GraphOriginScreen(scale).X, GraphOriginScreen(scale).Y)))
        {
            foreach (var link in graph.Links)
            {
                if (!TryFindPin(graph, link.FromPinId, out var from) ||
                    !TryFindPin(graph, link.ToPinId, out var to))
                    continue;

                DrawLink(context, from, to, link.PinCategory);
            }

            for (var i = 0; i < graph.Nodes.Count; i++)
                DrawNode(context, graph.Nodes[i], i == SelectedNodeIndex, i == _hoveredIndex, scale);
        }

        DrawFooter(context, bounds, graph, scale);
    }

    private void DrawEmptyState(DrawingContext context, string? note)
    {
        var text = note is { Length: > 0 } ? $"暂时画不出这张图\n{note}" : "选择左侧的事件或函数";
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            UiTypeface, 13, BlueprintGraphStyle.MutedTextBrush)
        {
            MaxTextWidth = Math.Max(160, Bounds.Width - 64),
            TextAlignment = TextAlignment.Center
        };

        context.DrawText(formatted, new Point((Bounds.Width - formatted.MaxTextWidth) / 2,
            Math.Max(20, Bounds.Height / 2 - formatted.Height / 2)));
    }

    private void DrawGrid(DrawingContext context, Rect bounds, double scale)
    {
        var step = 32 * scale;
        if (step < 14)
            return;

        var origin = GraphOriginScreen(scale);
        var startX = origin.X % step;
        var startY = origin.Y % step;
        var pen = new Pen(BlueprintGraphStyle.GridDotBrush, 1);
        for (var x = startX; x < bounds.Width; x += step)
            context.DrawLine(pen, new Point(x, 0), new Point(x, bounds.Height - FooterHeight));
        for (var y = startY; y < bounds.Height - FooterHeight; y += step)
            context.DrawLine(pen, new Point(0, y), new Point(bounds.Width, y));
    }

    private static void DrawLink(DrawingContext context, BlueprintPinDto from, BlueprintPinDto to, string category)
    {
        var start = new Point(from.X, from.Y);
        var end = new Point(to.X, to.Y);
        var distance = Math.Abs(end.X - start.X);
        var bend = Math.Max(30, distance * 0.45);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false);
            ctx.CubicBezierTo(
                new Point(start.X + bend, start.Y),
                new Point(end.X - bend, end.Y),
                end);
        }

        context.DrawGeometry(null, BlueprintGraphStyle.LinkPen(category), geometry);
    }

    private void DrawNode(DrawingContext context, BlueprintNodeDto node, bool selected, bool hovered, double scale)
    {
        var rect = new Rect(node.X, node.Y, node.Width, node.Height);
        var headerVisible = !node.HeaderHidden;
        var radius = 7.0;

        context.DrawRectangle(BlueprintGraphStyle.NodeBodyBrush, null, rect, radius, radius);
        if (headerVisible)
        {
            var headerRect = new Rect(node.X, node.Y, node.Width, Math.Min(HeaderHeight, node.Height));
            context.DrawRectangle(BlueprintGraphStyle.NodeHeaderBrush(node.NodeType, node.Pure), null, headerRect, radius, radius);
        }
        else if (node.Outputs.Count > 0 || node.Inputs.Count > 0)
        {
            // 变量节点没有标题栏：整块用端口色染上，才能一眼看出是"读变量"。
            context.DrawRectangle(BlueprintGraphStyle.NodeHeaderBrush(node.NodeType, true), null, rect, radius, radius);
        }

        if (hovered || selected)
            context.DrawRectangle(null, selected ? BlueprintGraphStyle.SelectedPen : BlueprintGraphStyle.HoverPen, rect, radius, radius);
        else
            context.DrawRectangle(null, BlueprintGraphStyle.NodeBorderPen, rect, radius, radius);

        DrawNodeText(context, node, rect, headerVisible);
        foreach (var pin in node.Inputs)
            DrawPin(context, pin, isInput: true, scale);
        foreach (var pin in node.Outputs)
            DrawPin(context, pin, isInput: false, scale);
    }

    private static void DrawNodeText(DrawingContext context, BlueprintNodeDto node, Rect rect, bool headerVisible)
    {
        var titleSize = headerVisible ? 12.5 : 11.5;
        var title = new FormattedText(Trim(node.Name, 64), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            UiTypeface, titleSize, BlueprintGraphStyle.TitleBrush)
        {
            MaxTextWidth = Math.Max(8, rect.Width - 18),
            Trimming = TextTrimming.CharacterEllipsis
        };

        var titleY = headerVisible
            ? rect.Y + (Math.Min(HeaderHeight, rect.Height) - title.Height) / 2
            : rect.Y + 8;
        context.DrawText(title, new Point(rect.X + 10, titleY));

        if (!headerVisible)
            return;

        // 副标题（函数所属类）放不下就只留在右侧属性里，这里不挤标题。
        if (node.Subtitle is { Length: > 0 } && rect.Height > HeaderHeight + 12)
        {
            var subtitle = new FormattedText(Trim(node.Subtitle, 40), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                UiTypeface, 9.5, BlueprintGraphStyle.MutedTextBrush)
            {
                MaxTextWidth = Math.Max(8, rect.Width - 18),
                Trimming = TextTrimming.CharacterEllipsis
            };
            context.DrawText(subtitle, new Point(rect.X + 10, rect.Y + HeaderHeight + 4));
        }
    }

    private static void DrawPin(DrawingContext context, BlueprintPinDto pin, bool isInput, double scale)
    {
        var center = new Point(pin.X, pin.Y);
        var radius = 4.5;
        var brush = BlueprintGraphStyle.PinBrush(pin.PinCategory);

        if (pin.IsExec)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(center.X, center.Y - 6), true);
                ctx.LineTo(new Point(center.X + 6, center.Y));
                ctx.LineTo(new Point(center.X, center.Y + 6));
                ctx.LineTo(new Point(center.X - 6, center.Y));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(brush, null, geometry);
        }
        else
        {
            context.DrawEllipse(brush, null, center, radius, radius);
        }

        var label = pin.IsNameHidden ? string.Empty : pin.Name;
        var value = string.IsNullOrWhiteSpace(pin.Value) ? string.Empty : Trim(pin.Value, 22);
        var text = value.Length == 0
            ? label
            : label.Length == 0 ? value : $"{label}: {value}";
        if (text.Length == 0)
            return;

        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            UiTypeface, 10.5, value.Length > 0 ? BlueprintGraphStyle.ValueTextBrush : BlueprintGraphStyle.PinTextBrush)
        {
            MaxTextWidth = 230,
            Trimming = TextTrimming.CharacterEllipsis
        };

        var x = isInput ? center.X + 9 : center.X - 9 - formatted.Width;
        context.DrawText(formatted, new Point(x, center.Y - formatted.Height / 2));
    }

    private void DrawFooter(DrawingContext context, Rect bounds, BlueprintGraphViewDto graph, double scale)
    {
        var footer = new Rect(0, bounds.Height - FooterHeight, bounds.Width, FooterHeight);
        context.FillRectangle(BlueprintGraphStyle.FooterBrush, footer);

        var percent = (int)Math.Round(scale * 100);
        var narrow = bounds.Width < 520;
        var summary = $"{graph.Nodes.Count} 个节点 · {graph.Links.Count} 条连线 · {percent}%";
        if (!narrow && !string.IsNullOrWhiteSpace(graph.Note))
            summary += " · " + Trim(graph.Note, 80);

        var text = new FormattedText(summary, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            UiTypeface, 10.5, BlueprintGraphStyle.PinTextBrush)
        {
            MaxTextWidth = Math.Max(80, bounds.Width - 240),
            Trimming = TextTrimming.CharacterEllipsis
        };
        context.DrawText(text, new Point(12, bounds.Height - FooterHeight + (FooterHeight - text.Height) / 2));

        if (bounds.Width > 620)
        {
            var hint = new FormattedText("滚轮缩放 · 拖动平移 · 双击适应", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, UiTypeface, 10, BlueprintGraphStyle.MutedTextBrush);
            context.DrawText(hint, new Point(bounds.Width - hint.Width - 12,
                bounds.Height - FooterHeight + (FooterHeight - hint.Height) / 2));
        }
    }

    private static bool TryFindPin(BlueprintGraphViewDto graph, string id, out BlueprintPinDto pin)
    {
        foreach (var node in graph.Nodes)
        {
            foreach (var candidate in node.Inputs)
            {
                if (candidate.Id != id)
                    continue;
                pin = candidate;
                return true;
            }

            foreach (var candidate in node.Outputs)
            {
                if (candidate.Id != id)
                    continue;
                pin = candidate;
                return true;
            }
        }

        pin = default!;
        return false;
    }

    private int HitTest(Point graphPoint)
    {
        var graph = Graph;
        if (graph is null)
            return -1;

        for (var i = graph.Nodes.Count - 1; i >= 0; i--)
        {
            var node = graph.Nodes[i];
            if (graphPoint.X >= node.X && graphPoint.X <= node.X + node.Width &&
                graphPoint.Y >= node.Y && graphPoint.Y <= node.Y + node.Height)
                return i;
        }
        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var graphPoint = ToGraph(point.Position, Scale);
        _lastPointer = point.Position;
        _moved = false;

        if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            _dragging = true;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            var hit = HitTest(graphPoint);
            if (hit >= 0)
            {
                SelectedNodeIndex = hit;
                InvalidateVisual();
            }

            _dragging = true;
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        var delta = position - _lastPointer;

        if (_dragging)
        {
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 1)
                _moved = true;
            _pan += delta;
            _lastPointer = position;
            InvalidateVisual();
            return;
        }

        var hit = HitTest(ToGraph(position, Scale));
        if (hit != _hoveredIndex)
        {
            _hoveredIndex = hit;
            Cursor = hit >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
        Cursor = _hoveredIndex >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Graph is null)
            return;

        var pointer = e.GetPosition(this);
        var anchorGraph = ToGraph(pointer, Scale);
        var factor = Math.Pow(1.15, e.Delta.Y);
        var newZoom = Math.Clamp(_zoom * factor, 0.2, 4);
        if (Math.Abs(newZoom - _zoom) < 0.0001)
            return;

        _zoom = newZoom;
        var scale = Scale;
        var origin = GraphOriginScreen(scale);
        var newOriginX = pointer.X - (anchorGraph.X - Graph.MinX) * scale;
        var newOriginY = pointer.Y - (anchorGraph.Y - Graph.MinY) * scale;
        _pan += new Vector(newOriginX - origin.X, newOriginY - origin.Y);
        InvalidateVisual();
        e.Handled = true;
    }



    private static string Trim(string value, int max)
    {
        value = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
