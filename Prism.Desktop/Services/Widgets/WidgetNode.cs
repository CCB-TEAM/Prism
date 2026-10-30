using PakTool.Core;

namespace Prism.Desktop.Services.Widgets;

/// <summary>
/// 控件树里的一个节点。既能当数据（属性列表）也能放下布局结果。
/// </summary>
public sealed class WidgetNode
{
    public string Name { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string SlotClassName { get; init; } = string.Empty;
    public IReadOnlyList<WidgetPropertyDto> Properties { get; init; } = [];
    public IReadOnlyList<WidgetPropertyDto> SlotProperties { get; init; } = [];
    public string? Note { get; init; }

    public WidgetNode? Parent { get; private set; }
    public List<WidgetNode> Children { get; } = [];

    // 布局结果（设计坐标系）
    public float X;
    public float Y;
    public float Width;
    public float Height;
    public float DesiredWidth;
    public float DesiredHeight;
    public bool IsVisible = true;

    /// <summary>ScaleBox 会把子树缩放后贴进来：子节点用自己的尺寸布局，再映射回父空间。</summary>
    public float ContentScaleX = 1;
    public float ContentScaleY = 1;
    public float ContentOriginX;
    public float ContentOriginY;

    public bool HasContentTransform => Math.Abs(ContentScaleX - 1) > 0.0001f || Math.Abs(ContentScaleY - 1) > 0.0001f;

    public (float X, float Y) ContentToParent(float x, float y) =>
        (ContentOriginX + (x - ContentOriginX) * ContentScaleX, ContentOriginY + (y - ContentOriginY) * ContentScaleY);

    public (float X, float Y) ParentToContent(float x, float y) =>
        (ContentOriginX + (x - ContentOriginX) / ContentScaleX, ContentOriginY + (y - ContentOriginY) / ContentScaleY);

    public string DisplayName => Name == ClassName ? Name : $"{Name} [{ClassName}]";

    /// <summary>ESlateVisibility。</summary>
    public string Visibility => WidgetProps.Enum(Properties, "Visibility", "Visible");

    public bool IsCollapsed => Visibility == "Collapsed";
    public bool IsHitTestVisible => Visibility is "Visible" or "HitTestInvisible" or "SelfHitTestInvisible";

    /// <summary>在设计坐标系里的矩形，含祖先 ScaleBox 的缩放。</summary>
    public (float X, float Y, float Width, float Height) DesignRect()
    {
        float left = X, top = Y, right = X + Width, bottom = Y + Height;
        for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (!ancestor.HasContentTransform)
                continue;

            (left, top) = ancestor.ContentToParent(left, top);
            (right, bottom) = ancestor.ContentToParent(right, bottom);
        }

        return (left, top, right - left, bottom - top);
    }

    public IEnumerable<WidgetNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.SelfAndDescendants())
                yield return node;
    }

    public override string ToString() => DisplayName;

    public static WidgetNode? FromDto(WidgetNodeDto? dto)
    {
        if (dto is null)
            return null;

        var node = new WidgetNode
        {
            Name = dto.Name,
            ClassName = dto.ClassName,
            SlotClassName = dto.SlotClassName,
            Properties = dto.Properties,
            SlotProperties = dto.SlotProperties,
            Note = dto.Note,
        };

        foreach (var childDto in dto.Children)
        {
            var child = FromDto(childDto);
            if (child is null)
                continue;

            child.Parent = node;
            node.Children.Add(child);
        }

        return node;
    }
}
