using PakTool.Core;

namespace Prism.Desktop.Services.Widgets;

public readonly record struct WidgetMargin(float Left, float Top, float Right, float Bottom)
{
    public float Horizontal => Left + Right;
    public float Vertical => Top + Bottom;
    public static readonly WidgetMargin Zero = new(0, 0, 0, 0);
}

public readonly record struct WidgetRgba(float R, float G, float B, float A)
{
    public static readonly WidgetRgba White = new(1, 1, 1, 1);
    public WidgetRgba WithAlpha(float alpha) => this with { A = A * alpha };
}

/// <summary>
/// 从控件属性里取值。unversioned 序列化只写和默认值不一样的字段，
/// 所以每一个读取器都要能接受「这个属性根本没有」。
/// </summary>
public static class WidgetProps
{
    public static WidgetValue? GetRaw(IReadOnlyList<WidgetPropertyDto>? holder, string name)
    {
        if (holder is null)
            return null;

        foreach (var property in holder)
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            return property.Value;
        }

        return null;
    }

    public static bool Has(IReadOnlyList<WidgetPropertyDto>? holder, string name) => GetRaw(holder, name) is not null and not WidgetNull;

    public static float Float(IReadOnlyList<WidgetPropertyDto>? holder, string name, float defaultValue = 0)
    {
        return GetRaw(holder, name) switch
        {
            WidgetNumber number => (float) number.Value,
            WidgetInteger integer => integer.Value,
            _ => defaultValue,
        };
    }

    public static int Int(IReadOnlyList<WidgetPropertyDto>? holder, string name, int defaultValue = 0)
    {
        return GetRaw(holder, name) switch
        {
            WidgetInteger integer => (int) integer.Value,
            WidgetNumber number => (int) number.Value,
            _ => defaultValue,
        };
    }

    public static bool Bool(IReadOnlyList<WidgetPropertyDto>? holder, string name, bool defaultValue = false)
    {
        return GetRaw(holder, name) is WidgetBoolean value ? value.Value : defaultValue;
    }

    /// <summary>枚举值名，已经去掉「EType::」前缀。</summary>
    public static string Enum(IReadOnlyList<WidgetPropertyDto>? holder, string name, string defaultValue)
    {
        var text = GetRaw(holder, name) switch
        {
            WidgetEnum value => value.Value,
            WidgetName name2 => name2.Value,
            _ => null,
        };

        if (string.IsNullOrEmpty(text))
            return defaultValue;

        var separator = text.IndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? text[(separator + 2)..] : text;
    }

    public static string Text(IReadOnlyList<WidgetPropertyDto>? holder, string name, string defaultValue = "")
    {
        return GetRaw(holder, name) switch
        {
            WidgetText value => value.Value,
            WidgetName value => value.Value,
            WidgetEnum value => value.Value,
            _ => defaultValue,
        };
    }

    public static IReadOnlyList<WidgetPropertyDto>? Struct(IReadOnlyList<WidgetPropertyDto>? holder, string name) =>
        GetRaw(holder, name) switch
        {
            WidgetStruct value => value.Fields,
            _ => null,
        };

    public static (float X, float Y) Vector2(IReadOnlyList<WidgetPropertyDto>? holder, string name, float defaultX = 0, float defaultY = 0)
    {
        return GetRaw(holder, name) switch
        {
            WidgetVector value => ((float) value.X, (float) value.Y),
            WidgetStruct value => (Float(value.Fields, "X", defaultX), Float(value.Fields, "Y", defaultY)),
            _ => (defaultX, defaultY),
        };
    }

    public static WidgetMargin Margin(IReadOnlyList<WidgetPropertyDto>? holder, string name, WidgetMargin defaultValue)
    {
        return GetRaw(holder, name) is WidgetStruct value
            ? new WidgetMargin(Float(value.Fields, "Left"), Float(value.Fields, "Top"), Float(value.Fields, "Right"), Float(value.Fields, "Bottom"))
            : defaultValue;
    }

    public static WidgetRgba LinearColor(IReadOnlyList<WidgetPropertyDto>? holder, string name, WidgetRgba defaultValue)
    {
        return GetRaw(holder, name) switch
        {
            PakTool.Core.WidgetColor value => new WidgetRgba(value.R, value.G, value.B, value.A),
            WidgetStruct value => new WidgetRgba(
                Float(value.Fields, "R", 1),
                Float(value.Fields, "G", 1),
                Float(value.Fields, "B", 1),
                Float(value.Fields, "A", 1)),
            _ => defaultValue,
        };
    }

    /// <summary>FSlateColor：离线只有 SpecifiedColor 是确定的（主题色解析不出来）。</summary>
    public static WidgetRgba SlateColor(IReadOnlyList<WidgetPropertyDto>? holder, string name, WidgetRgba defaultValue)
    {
        return GetRaw(holder, name) is WidgetStruct value
            ? LinearColor(value.Fields, "SpecifiedColor", defaultValue)
            : defaultValue;
    }

    /// <summary>对象引用（贴图 / 材质 / 字体）。</summary>
    public static WidgetObjectRef? Object(IReadOnlyList<WidgetPropertyDto>? holder, string name) =>
        GetRaw(holder, name) as WidgetObjectRef;

    public static IReadOnlyList<WidgetValue>? List(IReadOnlyList<WidgetPropertyDto>? holder, string name) =>
        GetRaw(holder, name) is WidgetList list ? list.Items : null;
}
