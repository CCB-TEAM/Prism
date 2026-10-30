namespace PakTool.Core;

/// <summary>
/// UMG 控件蓝图里一个属性值的通用表示。
/// 读控件树时把 CUE4Parse 的对象图压平成这几种值，UI 侧就不用再引用 CUE4Parse。
/// </summary>
public abstract record WidgetValue
{
    public static readonly WidgetValue Empty = new WidgetNull();
}

public sealed record WidgetNull : WidgetValue;

public sealed record WidgetNumber(double Value) : WidgetValue;

public sealed record WidgetInteger(long Value) : WidgetValue;

public sealed record WidgetBoolean(bool Value) : WidgetValue;

/// <summary>FText（已取到文本内容，本地化键不保留）。</summary>
public sealed record WidgetText(string Value) : WidgetValue;

/// <summary>FName / 字符串。</summary>
public sealed record WidgetName(string Value) : WidgetValue;

/// <summary>枚举值，已去掉「EType::」前缀。</summary>
public sealed record WidgetEnum(string Value) : WidgetValue;

public sealed record WidgetColor(float R, float G, float B, float A) : WidgetValue;

public sealed record WidgetVector(double X, double Y) : WidgetValue;

public sealed record WidgetVector4(double X, double Y, double Z, double W) : WidgetValue;

/// <summary>结构体：SlateBrush、Margin、Anchors 之类。字段保持资产里的原顺序。</summary>
public sealed record WidgetStruct(string TypeName, IReadOnlyList<WidgetPropertyDto> Fields) : WidgetValue;

/// <summary>
/// 对象引用（贴图、材质、字体…）。<see cref="ImageId"/> 非空表示已经解码好一张图，
/// 图片数据在 <see cref="WidgetPreviewDto.Images"/> 里。
/// </summary>
public sealed record WidgetObjectRef(string TypeName, string Name, string Path, string? ImageId = null) : WidgetValue;

public sealed record WidgetList(string ItemType, IReadOnlyList<WidgetValue> Items) : WidgetValue;

/// <summary>没能识别的值，只给出类型和一段可读预览。</summary>
public sealed record WidgetRaw(string TypeName, string Preview) : WidgetValue;

public sealed record WidgetPropertyDto(
    string Name,
    string TypeName,
    WidgetValue Value,
    string? EnumName = null);

/// <summary>控件树的一个节点。属性和槽位属性分开，方便 UI 分组显示。</summary>
public sealed record WidgetNodeDto(
    string Name,
    string ClassName,
    string SlotClassName,
    IReadOnlyList<WidgetPropertyDto> Properties,
    IReadOnlyList<WidgetPropertyDto> SlotProperties,
    IReadOnlyList<WidgetNodeDto> Children,
    string? Note = null);

public sealed record WidgetImageDto(
    string Id,
    string Name,
    string SourcePath,
    int Width,
    int Height,
    byte[] PngData);

public sealed record WidgetBindingDto(
    string ObjectName,
    string PropertyName,
    string FunctionName);

/// <summary>
/// 一个 UMG 控件蓝图的预览数据：控件树 + 解出来的贴图 + 绑定/动画。
/// 布局在 UI 侧算（要用字体度量），这里只负责把数据拿全。
/// </summary>
public sealed record WidgetPreviewDto(
    string Name,
    string ClassName,
    WidgetNodeDto? Root,
    IReadOnlyList<WidgetBindingDto> Bindings,
    IReadOnlyList<string> Animations,
    IReadOnlyList<WidgetImageDto> Images,
    string? Note = null);
