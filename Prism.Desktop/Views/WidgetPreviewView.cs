using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PakTool.Core;
using Prism.Desktop.Services.Widgets;

namespace Prism.Desktop.Views;

/// <summary>
/// UMG 控件蓝图预览：把控件树按 Slate 的规则排一遍，再用 Avalonia 直接画出来。
/// 点一下可以选中控件（右侧属性表跟着变），滚轮缩放，右键拖动平移。
/// 不依赖 SkiaSharp，Windows 与 Android 都能用。
/// </summary>
public sealed class WidgetPreviewView : Control
{
    public static readonly StyledProperty<WidgetPreviewDto?> PreviewProperty =
        AvaloniaProperty.Register<WidgetPreviewView, WidgetPreviewDto?>(nameof(Preview));

    public static readonly StyledProperty<WidgetNode?> RootProperty =
        AvaloniaProperty.Register<WidgetPreviewView, WidgetNode?>(nameof(Root));

    public static readonly StyledProperty<WidgetNode?> SelectedNodeProperty =
        AvaloniaProperty.Register<WidgetPreviewView, WidgetNode?>(
            nameof(SelectedNode), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ShowOutlinesProperty =
        AvaloniaProperty.Register<WidgetPreviewView, bool>(nameof(ShowOutlines), defaultValue: false);

    public WidgetPreviewDto? Preview
    {
        get => GetValue(PreviewProperty);
        set => SetValue(PreviewProperty, value);
    }

    public WidgetNode? Root
    {
        get => GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public WidgetNode? SelectedNode
    {
        get => GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    public bool ShowOutlines
    {
        get => GetValue(ShowOutlinesProperty);
        set => SetValue(ShowOutlinesProperty, value);
    }

    /// <summary>画布里的点击/悬停选中，交给外面同步列表选择。</summary>
    public event Action<WidgetNode?>? NodeSelected;

    private static readonly Typeface UiTypeface = new(FontFamily.Default);

    private static readonly IBrush CanvasBackground = new SolidColorBrush(Color.FromRgb(24, 24, 28));
    private static readonly IBrush DesignBackground = new SolidColorBrush(Color.FromRgb(15, 15, 18));
    private static readonly IBrush FooterBackground = new SolidColorBrush(Color.FromRgb(18, 18, 22));
    private static readonly IPen DesignBorderPen = new Pen(new SolidColorBrush(Color.FromRgb(90, 90, 100)), 1);
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.FromRgb(100, 180, 255)), 1.5);
    private static readonly IPen SelectionPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 165, 0)), 2);
    private static readonly IBrush MutedText = new SolidColorBrush(Color.FromRgb(150, 150, 160));
    private static readonly IBrush PlaceholderStroke = new SolidColorBrush(Color.FromArgb(140, 200, 90, 120));

    private const float DefaultDesignWidth = 1920;
    private const float DefaultDesignHeight = 1080;
    private const double FooterHeight = 22;

    private readonly Dictionary<string, Bitmap> _bitmaps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap> _tintedBitmaps = new(StringComparer.Ordinal);

    private float _designWidth = DefaultDesignWidth;
    private float _designHeight = DefaultDesignHeight;
    private double _fitScale = 1;
    private double _zoom = 1;
    private Point _pan;
    private bool _dragging;
    private Point _lastPointer;
    private WidgetNode? _hovered;

    static WidgetPreviewView()
    {
        AffectsRender<WidgetPreviewView>(PreviewProperty, RootProperty, SelectedNodeProperty, ShowOutlinesProperty);
        AffectsMeasure<WidgetPreviewView>(RootProperty);
    }

    public WidgetPreviewView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 360 : availableSize.Height;
        return new Size(Math.Max(240, width), Math.Max(220, Math.Min(520, height)));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RootProperty || change.Property == PreviewProperty)
        {
            Relayout();
            ResetView();
        }
    }

    /// <summary>重新算一遍布局。设计尺寸跟着根控件走：CanvasPanel 按 1920×1080，其它按自身大小。</summary>
    public void Relayout()
    {
        var root = Root;
        if (root is null)
            return;

        var layout = new WidgetLayout(MeasureText);
        var kind = layout.GetKind(root);

        // 铺满屏幕的控件（CanvasPanel / Overlay，HUD 常见）按 1920×1080 设计稿算；
        // 其它按自身期望尺寸算 —— PE-BOX 计数器那种小组件 248×80 才是它真实的样子。
        if (kind is WidgetKind.CanvasPanel or WidgetKind.Overlay)
        {
            _designWidth = DefaultDesignWidth;
            _designHeight = DefaultDesignHeight;
            layout.Layout(root, _designWidth, _designHeight);
        }
        else
        {
            layout.Layout(root, DefaultDesignWidth, DefaultDesignHeight);
            _designWidth = Math.Max(1, root.DesiredWidth);
            _designHeight = Math.Max(1, root.DesiredHeight);
            layout.Layout(root, _designWidth, _designHeight);
        }
    }

    private void ResetView()
    {
        _zoom = 1;
        _pan = default;
        UpdateFitScale();
        InvalidateVisual();
    }

    private void UpdateFitScale()
    {
        var availableWidth = Math.Max(32, Bounds.Width - 32);
        var availableHeight = Math.Max(32, Bounds.Height - FooterHeight - 24);
        _fitScale = Math.Min(availableWidth / _designWidth, availableHeight / _designHeight);
        _fitScale = Math.Clamp(_fitScale, 0.02, 4);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateFitScale();
    }

    /// <summary>屏幕坐标 → 设计坐标。</summary>
    private Point ToDesign(Point point)
    {
        var scale = Math.Max(0.0001, _fitScale * _zoom);
        var originX = (Bounds.Width - _designWidth * _fitScale) / 2 + _pan.X;
        var originY = (Bounds.Height - FooterHeight - _designHeight * _fitScale) / 2 + _pan.Y;
        return new Point((point.X - originX) / scale, (point.Y - originY) / scale);
    }

    #region 文字度量

    public static (float Width, float Height) MeasureText(WidgetNode node, string text, float fontSizePx, float wrapWidth)
    {
        if (string.IsNullOrEmpty(text))
            return (0, fontSizePx * 1.2f);

        var formatted = CreateText(text, fontSizePx, null, wrapWidth > 0 ? wrapWidth : double.PositiveInfinity);
        return ((float) formatted.Width, (float) Math.Max(formatted.Height, fontSizePx * 1.2));
    }

    private static FormattedText CreateText(string text, double fontSizePx, IBrush? foreground, double maxWidth)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            UiTypeface,
            Math.Max(1, fontSizePx),
            foreground ?? Brushes.White);

        if (!double.IsInfinity(maxWidth) && maxWidth > 0)
        {
            formatted.MaxTextWidth = maxWidth;
            formatted.TextAlignment = TextAlignment.Left;
        }

        return formatted;
    }

    #endregion

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        context.FillRectangle(CanvasBackground, new Rect(Bounds.Size));

        var root = Root;
        if (root is null)
        {
            DrawFooter(context, "这个控件蓝图里没有控件树");
            return;
        }

        UpdateFitScale();
        var scale = _fitScale * _zoom;
        var originX = (Bounds.Width - _designWidth * _fitScale) / 2 + _pan.X;
        var originY = (Bounds.Height - FooterHeight - _designHeight * _fitScale) / 2 + _pan.Y;
        var designRect = new Rect(originX, originY, _designWidth * scale, _designHeight * scale);

        using (context.PushClip(new Rect(0, 0, Bounds.Width, Math.Max(0, Bounds.Height - FooterHeight))))
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(originX, originY)))
        {
            context.FillRectangle(DesignBackground, new Rect(0, 0, _designWidth, _designHeight));
            DrawWidget(context, root, 1f, scale);

            if (SelectedNode is { IsVisible: true } selected)
            {
                var (x, y, width, height) = selected.DesignRect();
                var pen = new Pen(SelectionPen.Brush, Math.Max(1, 2 / scale));
                context.DrawRectangle(null, pen, new Rect(x, y, width, height));
            }

            if (_hovered is { IsVisible: true } hovered && !ReferenceEquals(hovered, SelectedNode))
            {
                var (x, y, width, height) = hovered.DesignRect();
                context.DrawRectangle(null, new Pen(HoverPen.Brush, Math.Max(1, 1.5 / scale)), new Rect(x, y, width, height));
            }
        }

        context.DrawRectangle(null, DesignBorderPen, designRect);
        DrawFooter(context, BuildFooterText(root));
    }

    private string BuildFooterText(WidgetNode root)
    {
        var text = $"设计尺寸 {_designWidth:0}×{_designHeight:0}   缩放 {_fitScale * _zoom:0.00}×";
        var node = _hovered ?? SelectedNode;
        if (node is not null)
        {
            var (_, _, width, height) = node.DesignRect();
            text += $"   {node.DisplayName}  ({node.X:0},{node.Y:0}) {width:0}×{height:0}";
        }

        return text;
    }

    private void DrawFooter(DrawingContext context, string text)
    {
        var rect = new Rect(0, Math.Max(0, Bounds.Height - FooterHeight), Bounds.Width, FooterHeight);
        context.FillRectangle(FooterBackground, rect);
        var formatted = CreateText(text, 11, MutedText, Bounds.Width - 12);
        context.DrawText(formatted, new Point(6, Math.Max(0, Bounds.Height - FooterHeight + 4)));
    }

    #region 逐节点绘制

    private void DrawWidget(DrawingContext context, WidgetNode node, double parentOpacity, double scale)
    {
        if (!node.IsVisible)
            return;

        var opacity = parentOpacity * (WidgetProps.Float(node.Properties, "RenderOpacity", 1) is var rawOpacity && rawOpacity > 0.001f ? rawOpacity : 1f);
        var hidden = node.Visibility == "Hidden";

        using var _ = context.PushOpacity(Math.Clamp(opacity, 0, 1));
        var rect = new Rect(node.X, node.Y, Math.Max(0, node.Width), Math.Max(0, node.Height));

        var clip = node.Visibility is "ClipToBounds" or "ClipToBoundsAlways" ||
                   WidgetProps.Enum(node.Properties, "Clipping", "Inherit") is "ClipToBounds" or "ClipToBoundsAlways";

        if (clip && rect.Width > 0 && rect.Height > 0)
        {
            using var clipScope = context.PushClip(rect);
            if (!hidden)
                DrawSelf(context, node, rect, opacity);

            DrawChildren(context, node, opacity, scale);
        }
        else
        {
            if (!hidden)
                DrawSelf(context, node, rect, opacity);

            DrawChildren(context, node, opacity, scale);
        }

        if (hidden)
            return;

        if (ShowOutlines && scale > 0.05 && rect.Width > 0 && rect.Height > 0)
        {
            var outlinePen = new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), Math.Max(0.5, 1 / scale));
            context.DrawRectangle(null, outlinePen, rect);
        }
    }

    private void DrawChildren(DrawingContext context, WidgetNode node, double opacity, double scale)
    {
        var children = node.Children.Where(child => child.IsVisible).ToList();
        if (node.ClassName is "CanvasPanel" or "ConstraintCanvas")
            children = children.OrderBy(child => WidgetProps.Int(child.SlotProperties, "ZOrder")).ToList();

        foreach (var child in children)
        {
            if (node.HasContentTransform)
            {
                using var transform = context.PushTransform(Matrix.CreateScale(node.ContentScaleX, node.ContentScaleY) *
                                                            Matrix.CreateTranslation(node.ContentOriginX * (1 - node.ContentScaleX),
                                                                node.ContentOriginY * (1 - node.ContentScaleY)));
                DrawWidget(context, child, opacity, scale);
            }
            else
            {
                DrawWidget(context, child, opacity, scale);
            }
        }
    }

    private void DrawSelf(DrawingContext context, WidgetNode node, Rect rect, double opacity)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var kind = GetKind(node);
        var properties = node.Properties;
        switch (kind)
        {
            case WidgetKind.Image:
            {
                var tint = WidgetProps.LinearColor(properties, "ColorAndOpacity", WidgetRgba.White);
                DrawBrush(context, properties, "Brush", rect, tint, opacity, fillWhenEmpty: false);
                break;
            }
            case WidgetKind.Border:
            {
                var tint = WidgetProps.LinearColor(properties, "BrushColor", WidgetRgba.White);
                DrawBrush(context, properties, "Background", rect, tint, opacity, fillWhenEmpty: true);
                break;
            }
            case WidgetKind.Button:
            {
                var style = WidgetProps.Struct(properties, "WidgetStyle");
                var normal = WidgetProps.Struct(style, "Normal");
                var tint = WidgetProps.LinearColor(properties, "BackgroundColor", WidgetRgba.White);
                if (normal is not null)
                    DrawBrushFields(context, normal, rect, tint, opacity, fillWhenEmpty: true);
                else
                    FillTinted(context, rect, WidgetRgba.White.WithAlpha(0.15f));

                break;
            }
            case WidgetKind.TextBlock:
            case WidgetKind.RichTextBlock:
                DrawTextWidget(context, node, rect, opacity);
                break;

            case WidgetKind.ProgressBar:
                DrawProgressBar(context, node, rect, opacity);
                break;

            case WidgetKind.CheckBox:
                DrawCheckBox(context, node, rect, opacity);
                break;

            case WidgetKind.Slider:
                DrawSlider(context, node, rect, opacity);
                break;

            case WidgetKind.EditableText:
            case WidgetKind.ComboBox:
                DrawBoxedText(context, node, rect, opacity);
                break;

            case WidgetKind.Throbber:
                DrawThrobber(context, rect, opacity);
                break;
        }
    }

    private void DrawTextWidget(DrawingContext context, WidgetNode node, Rect rect, double opacity)
    {
        var text = WidgetLayout.TextOf(node);
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (node.ClassName == "RichTextBlock")
            text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", string.Empty);

        var color = WidgetProps.SlateColor(node.Properties, "ColorAndOpacity", WidgetRgba.White).WithAlpha((float) opacity);
        var margin = WidgetProps.Margin(node.Properties, "Margin", WidgetMargin.Zero);
        var inner = new Rect(rect.X + margin.Left, rect.Y + margin.Top,
            Math.Max(0, rect.Width - margin.Horizontal), Math.Max(0, rect.Height - margin.Vertical));

        var fontSize = WidgetLayout.FontSizePx(node);
        var autoWrap = WidgetProps.Bool(node.Properties, "AutoWrapText");
        var formatted = CreateText(text, fontSize, ToBrush(color), autoWrap ? inner.Width : double.PositiveInfinity);

        var x = inner.X;
        var justification = WidgetProps.Enum(node.Properties, "Justification", "ETextJustify::Left");
        if (justification.EndsWith("Center", StringComparison.Ordinal))
            x = inner.X + Math.Max(0, (inner.Width - formatted.Width) / 2);
        else if (justification.EndsWith("Right", StringComparison.Ordinal))
            x = inner.X + Math.Max(0, inner.Width - formatted.Width);

        context.DrawText(formatted, new Point(x, inner.Y));
    }

    private void DrawProgressBar(DrawingContext context, WidgetNode node, Rect rect, double opacity)
    {
        var style = WidgetProps.Struct(node.Properties, "WidgetStyle");
        var background = WidgetProps.Struct(style, "BackgroundImage");
        if (background is not null)
            DrawBrushFields(context, background, rect, new WidgetRgba(0.2f, 0.2f, 0.2f, 1f), opacity, fillWhenEmpty: true);
        else
            FillTinted(context, rect, new WidgetRgba(0.2f, 0.2f, 0.2f, 0.6f));

        var percent = Math.Clamp(WidgetProps.Float(node.Properties, "Percent"), 0, 1);
        var fillColor = WidgetProps.LinearColor(node.Properties, "FillColorAndOpacity", WidgetRgba.White);
        var fillRect = new Rect(rect.X, rect.Y, rect.Width * percent, rect.Height);
        var fill = WidgetProps.Struct(style, "FillImage");
        if (fill is not null)
            DrawBrushFields(context, fill, fillRect, fillColor, opacity, fillWhenEmpty: true);
        else
            FillTinted(context, fillRect, fillColor.WithAlpha((float) opacity));
    }

    private void DrawCheckBox(DrawingContext context, WidgetNode node, Rect rect, double opacity)
    {
        var size = Math.Min(20, Math.Min(rect.Width, rect.Height));
        var box = new Rect(rect.X, rect.Y + (rect.Height - size) / 2, size, size);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte) (120 * opacity), 255, 255, 255)), new Pen(Brushes.Gray, 1), box);

        if (WidgetProps.Enum(node.Properties, "CheckedState", "Unchecked") == "Checked")
        {
            var check = CreateText("✓", size * 0.9, Brushes.White, double.PositiveInfinity);
            context.DrawText(check, new Point(box.X + 1, box.Y - 1));
        }
    }

    private void DrawSlider(DrawingContext context, WidgetNode node, Rect rect, double opacity)
    {
        var trackHeight = 4.0;
        var track = new Rect(rect.X, rect.Y + (rect.Height - trackHeight) / 2, rect.Width, trackHeight);
        FillTinted(context, track, new WidgetRgba(0.35f, 0.35f, 0.35f, (float) opacity));

        var value = Math.Clamp(WidgetProps.Float(node.Properties, "Value"), 0, 1);
        var radius = 7.0;
        var center = new Point(rect.X + rect.Width * value, rect.Y + rect.Height / 2);
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte) (255 * opacity), 220, 220, 220)), null, center, radius, radius);
    }

    private void DrawBoxedText(DrawingContext context, WidgetNode node, Rect rect, double opacity)
    {
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte) (40 * opacity), 255, 255, 255)),
            new Pen(new SolidColorBrush(Color.FromArgb((byte) (120 * opacity), 160, 160, 160)), 1), rect);

        var text = WidgetProps.Text(node.Properties, "Text");
        if (string.IsNullOrEmpty(text))
            text = WidgetProps.Text(node.Properties, "HintText");
        if (string.IsNullOrEmpty(text))
            text = WidgetProps.Text(node.Properties, "SelectedOption");
        if (string.IsNullOrEmpty(text))
            return;

        var formatted = CreateText(text, 13, ToBrush(WidgetRgba.White.WithAlpha((float) opacity)), rect.Width - 8);
        context.DrawText(formatted, new Point(rect.X + 4, rect.Y + Math.Max(0, (rect.Height - formatted.Height) / 2)));
    }

    private void DrawThrobber(DrawingContext context, Rect rect, double opacity)
    {
        var radius = Math.Max(1, Math.Min(rect.Width, rect.Height) / 8);
        var brush = new SolidColorBrush(Color.FromArgb((byte) (200 * opacity), 220, 220, 220));
        var centerY = rect.Y + rect.Height / 2;
        for (var i = 0; i < 3; i++)
        {
            var centerX = rect.X + rect.Width / 2 + (i - 1) * radius * 2.6;
            context.DrawEllipse(brush, null, new Point(centerX, centerY), radius, radius);
        }
    }

    #endregion

    #region 画刷

    /// <summary>画一个 Slate 画刷：纯色、贴图、九宫格都走这里。字段名找不到就按默认值。</summary>
    private void DrawBrush(
        DrawingContext context,
        IReadOnlyList<WidgetPropertyDto> holder,
        string propertyName,
        Rect rect,
        WidgetRgba widgetTint,
        double opacity,
        bool fillWhenEmpty)
    {
        if (WidgetProps.Struct(holder, propertyName) is not { } brush)
        {
            if (fillWhenEmpty)
                FillTinted(context, rect, widgetTint.WithAlpha((float) (widgetTint.A * opacity)));

            return;
        }

        DrawBrushFields(context, brush, rect, widgetTint, opacity, fillWhenEmpty);
    }

    private void DrawBrushFields(
        DrawingContext context,
        IReadOnlyList<WidgetPropertyDto>? brush,
        Rect rect,
        WidgetRgba widgetTint,
        double opacity,
        bool fillWhenEmpty)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var drawAs = WidgetProps.Enum(brush, "DrawAs", "Image");
        if (drawAs == "NoDrawType")
            return;

        var tint = WidgetProps.SlateColor(brush, "TintColor", WidgetRgba.White);
        var color = Multiply(tint, widgetTint).WithAlpha((float) Math.Clamp(tint.A * widgetTint.A * opacity, 0, 1));

        if (drawAs == "RoundedBox")
        {
            DrawRoundedBox(context, brush, rect, color);
            return;
        }

        var resource = WidgetProps.Object(brush, "ResourceObject");
        var bitmap = resource?.ImageId is null ? null : GetBitmap(resource.ImageId, color);
        if (bitmap is null)
        {
            if (color.A > 0.001f && fillWhenEmpty)
                FillTinted(context, rect, color);

            // 只有「确实引用了贴图但解不出来」才画斜线占位；纯色画刷不画
            if (resource is not null)
                DrawPlaceholder(context, rect, resource.Name);

            return;
        }

        var margin = WidgetProps.Margin(brush, "Margin", WidgetMargin.Zero);
        var isBox = drawAs is "Box" or "Border";
        if (isBox && (margin.Left > 0 || margin.Top > 0 || margin.Right > 0 || margin.Bottom > 0))
            DrawNinePatch(context, bitmap, rect, margin);
        else
            context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height), rect);
    }

    private static void DrawRoundedBox(DrawingContext context, IReadOnlyList<WidgetPropertyDto>? brush, Rect rect, WidgetRgba color)
    {
        var outline = WidgetProps.Struct(brush, "OutlineSettings");
        var radius = 0f;
        if (WidgetProps.Enum(outline, "RoundingType", "FixedRadius") == "HalfHeightRadius")
        {
            radius = (float) Math.Min(rect.Width, rect.Height) / 2;
        }
        else if (WidgetProps.GetRaw(outline, "CornerRadii") is { } corners)
        {
            radius = corners switch
            {
                WidgetVector4 vector => (float) Math.Max(Math.Max(vector.X, vector.Y), Math.Max(vector.Z, vector.W)),
                WidgetStruct structValue => structValue.Fields.Select(field => field.Value).OfType<WidgetNumber>().Select(n => (float) n.Value).DefaultIfEmpty(0).Max(),
                _ => 0,
            };
        }

        var fill = ToBrush(color);
        var width = WidgetProps.Float(outline, "Width");
        var outlineColor = WidgetProps.LinearColor(outline, "Color", WidgetRgba.White);
        var pen = width > 0 ? new Pen(ToBrush(outlineColor), width) : null;

        if (radius > 0)
            context.DrawRectangle(fill, pen, new RoundedRect(rect, radius));
        else
            context.DrawRectangle(fill, pen, rect);
    }

    private static void DrawNinePatch(DrawingContext context, Bitmap bitmap, Rect rect, WidgetMargin margin)
    {
        var imageWidth = bitmap.PixelSize.Width;
        var imageHeight = bitmap.PixelSize.Height;
        var left = Math.Min(margin.Left * imageWidth, imageWidth / 2f);
        var top = Math.Min(margin.Top * imageHeight, imageHeight / 2f);
        var right = Math.Min(margin.Right * imageWidth, imageWidth / 2f);
        var bottom = Math.Min(margin.Bottom * imageHeight, imageHeight / 2f);

        var sourceXs = new[] { 0f, left, imageWidth - right, imageWidth };
        var sourceYs = new[] { 0f, top, imageHeight - bottom, imageHeight };
        var targetXs = new[] { (float) rect.X, (float) rect.X + left, (float) rect.Right - right, (float) rect.Right };
        var targetYs = new[] { (float) rect.Y, (float) rect.Y + top, (float) rect.Bottom - bottom, (float) rect.Bottom };

        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var source = new Rect(sourceXs[column], sourceYs[row],
                    Math.Max(0, sourceXs[column + 1] - sourceXs[column]),
                    Math.Max(0, sourceYs[row + 1] - sourceYs[row]));
                var target = new Rect(targetXs[column], targetYs[row],
                    Math.Max(0, targetXs[column + 1] - targetXs[column]),
                    Math.Max(0, targetYs[row + 1] - targetYs[row]));

                if (source.Width > 0 && source.Height > 0 && target.Width > 0 && target.Height > 0)
                    context.DrawImage(bitmap, source, target);
            }
        }
    }

    private static void DrawPlaceholder(DrawingContext context, Rect rect, string? resourceName)
    {
        var pen = new Pen(PlaceholderStroke, 1, new DashStyle([4, 3], 0));
        context.DrawRectangle(null, pen, rect);
        if (rect.Width < 40 || rect.Height < 16 || string.IsNullOrEmpty(resourceName))
            return;

        var text = CreateText(resourceName, 10, PlaceholderStroke, rect.Width - 6);
        context.DrawText(text, new Point(rect.X + 3, rect.Y + 2));
    }

    private static void FillTinted(DrawingContext context, Rect rect, WidgetRgba color)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || color.A <= 0.001f)
            return;

        context.FillRectangle(ToBrush(color), rect);
    }

    #endregion

    #region 贴图缓存（含着色）

    private Bitmap? GetBitmap(string imageId, WidgetRgba tint)
    {
        var tinted = Math.Abs(tint.R - 1) > 0.01f || Math.Abs(tint.G - 1) > 0.01f || Math.Abs(tint.B - 1) > 0.01f;
        var key = tinted
            ? $"{imageId}|{tint.R:0.000},{tint.G:0.000},{tint.B:0.000}"
            : imageId;

        if (tinted && _tintedBitmaps.TryGetValue(key, out var cachedTinted))
            return cachedTinted;
        if (!tinted && _bitmaps.TryGetValue(imageId, out var cached))
            return cached;

        var source = LoadBitmap(imageId);
        if (source is null)
            return null;

        if (!tinted)
            return source;

        var tintedBitmap = Tint(source, tint);
        if (tintedBitmap is not null)
            _tintedBitmaps[key] = tintedBitmap;

        return tintedBitmap ?? source;
    }

    private Bitmap? LoadBitmap(string imageId)
    {
        if (_bitmaps.TryGetValue(imageId, out var cached))
            return cached;

        var image = Preview?.Images.FirstOrDefault(item => item.Id == imageId);
        if (image is null)
            return null;

        try
        {
            using var stream = new MemoryStream(image.PngData);
            var bitmap = new Bitmap(stream);
            _bitmaps[imageId] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Bitmap? Tint(Bitmap source, WidgetRgba tint)
    {
        try
        {
            var width = source.PixelSize.Width;
            var height = source.PixelSize.Height;
            var stride = width * 4;
            var buffer = new byte[stride * height];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                source.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), buffer.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            // Avalonia 的位图是预乘 BGRA
            for (var i = 0; i < buffer.Length; i += 4)
            {
                buffer[i] = (byte) Math.Clamp(buffer[i] * tint.B, 0, 255);
                buffer[i + 1] = (byte) Math.Clamp(buffer[i + 1] * tint.G, 0, 255);
                buffer[i + 2] = (byte) Math.Clamp(buffer[i + 2] * tint.R, 0, 255);
            }

            var writeable = new WriteableBitmap(source.PixelSize, source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var locked = writeable.Lock())
            {
                System.Runtime.InteropServices.Marshal.Copy(buffer, 0, locked.Address, buffer.Length);
            }

            return writeable;
        }
        catch (Exception)
        {
            return null;
        }
    }

    #endregion

    #region 输入

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        _lastPointer = point.Position;
        _dragging = true;
        e.Pointer.Capture(this);

        if (point.Properties.IsLeftButtonPressed)
        {
            var hit = HitTest(ToDesign(point.Position));
            SelectedNode = hit;
            NodeSelected?.Invoke(hit);
            e.Handled = true;
        }

        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);

        if (_dragging)
        {
            var delta = position - _lastPointer;
            if (Math.Abs(delta.X) > 0.5 || Math.Abs(delta.Y) > 0.5)
            {
                _pan += delta;
                _lastPointer = position;
                InvalidateVisual();
            }

            return;
        }

        var hovered = HitTest(ToDesign(position));
        if (!ReferenceEquals(hovered, _hovered))
        {
            _hovered = hovered;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var factor = e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
        var oldZoom = _zoom;
        _zoom = Math.Clamp(_zoom * factor, 0.05, 12);
        if (Math.Abs(_zoom - oldZoom) < 0.0001)
            return;

        // 以光标为中心缩放
        var position = e.GetPosition(this);
        var ratio = _zoom / oldZoom;
        _pan = new Point(
            position.X - (position.X - _pan.X) * ratio - (Bounds.Width / 2) * (1 - ratio),
            position.Y - (position.Y - _pan.Y) * ratio - ((Bounds.Height - FooterHeight) / 2) * (1 - ratio));

        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>设计坐标里最里面那个可见控件。</summary>
    public WidgetNode? HitTest(Point designPoint)
    {
        var root = Root;
        return root is null ? null : HitTest(root, designPoint);
    }

    private WidgetNode? HitTest(WidgetNode node, Point designPoint)
    {
        if (!node.IsVisible)
            return null;

        // 后画的在上面，先试后面的子控件
        for (var i = node.Children.Count - 1; i >= 0; i--)
        {
            var child = node.Children[i];
            var local = node.HasContentTransform
                ? node.ParentToContent((float) designPoint.X, (float) designPoint.Y)
                : (X: (float) designPoint.X, Y: (float) designPoint.Y);

            if (HitTest(child, new Point(local.X, local.Y)) is { } hit)
                return hit;
        }

        var rect = new Rect(node.X, node.Y, Math.Max(0, node.Width), Math.Max(0, node.Height));
        return rect.Contains(designPoint) ? node : null;
    }

    #endregion

    private WidgetKind GetKind(WidgetNode node) => _kindLookup.TryGetValue(node.ClassName, out var kind) ? kind : ComputeKind(node);

    private readonly Dictionary<string, WidgetKind> _kindLookup = new(StringComparer.Ordinal);

    private WidgetKind ComputeKind(WidgetNode node)
    {
        var layout = new WidgetLayout(MeasureText);
        var kind = layout.GetKind(node);
        _kindLookup[node.ClassName] = kind;
        return kind;
    }

    private static WidgetRgba Multiply(WidgetRgba left, WidgetRgba right) =>
        new(left.R * right.R, left.G * right.G, left.B * right.B, left.A * right.A);

    private static IBrush ToBrush(WidgetRgba color) =>
        new SolidColorBrush(Color.FromArgb(
            (byte) Math.Clamp(color.A * 255, 0, 255),
            (byte) Math.Clamp(color.R * 255, 0, 255),
            (byte) Math.Clamp(color.G * 255, 0, 255),
            (byte) Math.Clamp(color.B * 255, 0, 255)));
}
