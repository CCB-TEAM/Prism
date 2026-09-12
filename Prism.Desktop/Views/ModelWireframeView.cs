using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PakTool.Core;

namespace Prism.Desktop.Views;

/// <summary>
/// 轻量级 3D 模型线框预览：用 Skia 绘制三角形边线，拖动旋转、滚轮缩放。
/// 不依赖 WebView / OpenGL，Windows 与 Android 通用；手机性能不足时自动减少三角形数量。
/// </summary>
public sealed class ModelWireframeView : Control
{
    public static readonly StyledProperty<ModelPreviewDto?> ModelProperty =
        AvaloniaProperty.Register<ModelWireframeView, ModelPreviewDto?>(nameof(Model));

    public ModelPreviewDto? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    private static readonly IBrush WireBrush = new SolidColorBrush(Color.FromRgb(13, 148, 136));
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(248, 247, 243));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.FromArgb(90, 200, 200, 200));
    private static readonly IPen WirePen = new Pen(WireBrush, 1);
    private static readonly IPen GridPen = new Pen(GridBrush, 1);

    private double _rotationY = -0.65;
    private double _rotationX = 0.22;
    private double _zoom = 1.0;
    private Point _lastPointer;
    private bool _dragging;

    static ModelWireframeView()
    {
        AffectsRender<ModelWireframeView>(ModelProperty);
    }

    public ModelWireframeView()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
        {
            return;
        }

        Point position = e.GetPosition(this);
        _rotationY += (position.X - _lastPointer.X) * 0.012;
        _rotationX += (position.Y - _lastPointer.Y) * 0.012;
        _lastPointer = position;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _zoom = Math.Clamp(_zoom * (1 + e.Delta.Y * 0.12), 0.3, 6.0);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        _rotationY = -0.65;
        _rotationX = 0.22;
        _zoom = 1.0;
        InvalidateVisual();
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Size size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        context.FillRectangle(BackgroundBrush, new Rect(size));
        DrawGrid(context, size);

        ModelPreviewDto? model = Model;
        if (model is null || model.Positions.Length == 0 || model.Indices.Length == 0)
        {
            return;
        }

        int maxTriangles = OperatingSystem.IsAndroid() ? 6_000 : 16_000;
        int indexCount = Math.Min(model.Indices.Length, maxTriangles * 3);
        uint[] sampledIndices = model.Indices.AsSpan(0, indexCount).ToArray();
        if (sampledIndices.Length < 3)
        {
            return;
        }

        Point[] projected = ProjectVertices(model, sampledIndices, size);
        if (projected.Length == 0)
        {
            return;
        }

        StreamGeometry geometry = new();
        StreamGeometryContext geometryContext = geometry.Open();
        for (int i = 0; i + 2 < sampledIndices.Length; i += 3)
        {
            uint a = sampledIndices[i];
            uint b = sampledIndices[i + 1];
            uint c = sampledIndices[i + 2];
            if (a >= projected.Length || b >= projected.Length || c >= projected.Length)
            {
                continue;
            }

            Point pa = projected[a];
            Point pb = projected[b];
            Point pc = projected[c];
            if (IsValidPoint(pa) && IsValidPoint(pb) && IsValidPoint(pc))
            {
                geometryContext.BeginFigure(pa, false);
                geometryContext.LineTo(pb);
                geometryContext.LineTo(pc);
                geometryContext.EndFigure(true);
            }
        }

        context.DrawGeometry(null, WirePen, geometry);
    }

    private Point[] ProjectVertices(ModelPreviewDto model, uint[] sampledIndices, Size size)
    {
        uint maxIndex = 0;
        foreach (uint index in sampledIndices)
        {
            if (index > maxIndex)
            {
                maxIndex = index;
            }
        }

        int vertexCount = (int)Math.Min((long)maxIndex + 1, model.Positions.Length / 3L);
        var projected = new Point[vertexCount];
        if (vertexCount == 0)
        {
            return projected;
        }

        ModelBoundsDto bounds = model.Bounds;
        double centerX = (bounds.MinX + bounds.MaxX) * 0.5;
        double centerY = (bounds.MinY + bounds.MaxY) * 0.5;
        double centerZ = (bounds.MinZ + bounds.MaxZ) * 0.5;
        double spanX = Math.Max(0.0001, bounds.MaxX - bounds.MinX);
        double spanY = Math.Max(0.0001, bounds.MaxY - bounds.MinY);
        double spanZ = Math.Max(0.0001, bounds.MaxZ - bounds.MinZ);
        double maxSpan = Math.Max(spanX, Math.Max(spanY, spanZ));
        double fit = Math.Min(size.Width, size.Height) * 0.42;
        double halfWidth = size.Width * 0.5;
        double halfHeight = size.Height * 0.5;

        double cosY = Math.Cos(_rotationY);
        double sinY = Math.Sin(_rotationY);
        double cosX = Math.Cos(_rotationX);
        double sinX = Math.Sin(_rotationX);

        for (int i = 0; i < vertexCount; i++)
        {
            int offset = i * 3;
            double x = (model.Positions[offset] - centerX) / maxSpan;
            double y = (model.Positions[offset + 1] - centerY) / maxSpan;
            double z = (model.Positions[offset + 2] - centerZ) / maxSpan;

            // 绕 Y 轴旋转，再绕 X 轴旋转。
            double x1 = x * cosY + z * sinY;
            double z1 = -x * sinY + z * cosY;
            double y2 = y * cosX - z1 * sinX;
            double z2 = y * sinX + z1 * cosX;

            double perspective = 2.4 / (2.4 + z2);
            projected[i] = new Point(
                halfWidth + x1 * fit * _zoom * perspective,
                halfHeight - y2 * fit * _zoom * perspective);
        }

        return projected;
    }

    private static bool IsValidPoint(Point point) =>
        !double.IsNaN(point.X) && !double.IsInfinity(point.X) &&
        !double.IsNaN(point.Y) && !double.IsInfinity(point.Y);

    private void DrawGrid(DrawingContext context, Size size)
    {
        const double step = 24;
        for (double x = step; x < size.Width; x += step)
        {
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, size.Height));
        }

        for (double y = step; y < size.Height; y += step)
        {
            context.DrawLine(GridPen, new Point(0, y), new Point(size.Width, y));
        }
    }
}
