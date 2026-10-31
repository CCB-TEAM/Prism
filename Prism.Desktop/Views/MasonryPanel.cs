using Avalonia;
using Avalonia.Controls;

namespace Prism.Desktop.Views;

/// <summary>
/// 按"放进当前最矮的一列"来排版的瀑布流面板。
///
/// 为什么不用 WrapPanel：设置页每一组的高度差很大（运行状态 500+、更新 200），
/// WrapPanel 按行排 —— 一行里最矮的那块下面就会空出一大片，视觉上像"页面破了个洞"。
/// 这里按列高贪心放置，两列自然平衡；窄窗时自动退化成单列。
///
/// 只依赖 Children 与 DesiredSize，不做虚拟化：设置页只有十来个分组，
/// 全部measure 一遍比维护回收逻辑便宜得多。
/// </summary>
public sealed class MasonryPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(MinItemWidth), defaultValue: 460);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(ColumnSpacing), defaultValue: 12);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(RowSpacing), defaultValue: 20);

    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<MasonryPanel, int>(nameof(MaxColumns), defaultValue: 2);

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    static MasonryPanel()
    {
        AffectsMeasure<MasonryPanel>(
            MinItemWidthProperty, ColumnSpacingProperty, RowSpacingProperty, MaxColumnsProperty);
    }

    private int ColumnsFor(double width)
    {
        var min = Math.Max(80, MinItemWidth);
        var spacing = Math.Max(0, ColumnSpacing);
        if (width <= 0)
            return 1;

        var columns = (int)Math.Floor((width + spacing) / (min + spacing));
        return Math.Clamp(columns, 1, Math.Max(1, MaxColumns));
    }

    private double ItemWidthFor(double width, int columns)
    {
        var spacing = Math.Max(0, ColumnSpacing);
        return Math.Max(1, (width - spacing * (columns - 1)) / columns);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        var columns = ColumnsFor(width);
        var itemWidth = ItemWidthFor(width, columns);
        var heights = new double[columns];

        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;

            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            var column = ShortestColumn(heights);
            heights[column] += child.DesiredSize.Height + RowSpacing;
        }

        var totalHeight = heights.Length == 0 ? 0 : heights.Max();
        totalHeight = Math.Max(0, totalHeight - RowSpacing);
        return new Size(width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        var columns = ColumnsFor(width);
        var itemWidth = ItemWidthFor(width, columns);
        var spacing = Math.Max(0, ColumnSpacing);
        var heights = new double[columns];

        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;

            var column = ShortestColumn(heights);
            var x = column * (itemWidth + spacing);
            var y = heights[column];
            child.Arrange(new Rect(x, y, itemWidth, child.DesiredSize.Height));
            heights[column] = y + child.DesiredSize.Height + RowSpacing;
        }

        var height = heights.Length == 0 ? 0 : Math.Max(0, heights.Max() - RowSpacing);
        return new Size(width, height);
    }

    private static int ShortestColumn(double[] heights)
    {
        var index = 0;
        for (var i = 1; i < heights.Length; i++)
        {
            if (heights[i] < heights[index] - 0.01)
                index = i;
        }
        return index;
    }
}
