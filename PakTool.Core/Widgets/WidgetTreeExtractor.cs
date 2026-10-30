using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;

namespace PakTool.Core;

/// <summary>解码好的一张控件贴图（PNG）。</summary>
public readonly record struct WidgetEncodedTexture(int Width, int Height, byte[] PngData);

/// <summary>
/// 把一个 UMG 控件蓝图（WidgetBlueprint）的控件树读成 DTO：
/// WidgetBlueprintGeneratedClass.WidgetTree → RootWidget → 各面板的 Slots[].Content。
/// 布局不在这里算 —— 文字度量要字体，交给 UI 侧。
/// </summary>
public sealed class WidgetTreeExtractor
{
    /// <summary>控件里再套控件（UserWidget）时往下展开的层数。</summary>
    public const int DefaultNestedDepth = 2;

    private const int MaxPropertyDepth = 4;
    private const int MaxArrayItems = 64;

    private readonly IPackage _package;
    private readonly Func<UTexture, WidgetEncodedTexture?> _encodeTexture;
    private readonly Action<string>? _log;
    private readonly List<WidgetImageDto> _images = [];
    private readonly Dictionary<string, string?> _imageIds = new(StringComparer.Ordinal);

    public WidgetTreeExtractor(
        IPackage package,
        Func<UTexture, WidgetEncodedTexture?> encodeTexture,
        Action<string>? log = null)
    {
        _package = package;
        _encodeTexture = encodeTexture;
        _log = log;
    }

    public static bool LooksLikeWidgetBlueprint(IPackage package)
    {
        try
        {
            return package.GetExports().Any(export => export is UWidgetBlueprintGeneratedClass);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public WidgetPreviewDto? Extract(string? displayName = null, int nestedDepth = DefaultNestedDepth)
    {
        var widgetClass = _package.GetExports().OfType<UWidgetBlueprintGeneratedClass>().FirstOrDefault();
        if (widgetClass is null)
            return null;

        var tree = Object(widgetClass, "WidgetTree")
                   ?? _package.GetExports().FirstOrDefault(export => export.ExportType == "WidgetTree");

        var root = Object(tree, "RootWidget");
        if (root is null)
            return new WidgetPreviewDto(
                displayName ?? widgetClass.Name,
                widgetClass.ExportType,
                null,
                [],
                [],
                [],
                "这个控件蓝图里没有控件树（可能是空蓝图或 cook 时被裁掉了）");

        var visited = new HashSet<string>(StringComparer.Ordinal) { widgetClass.GetPathName() };
        var rootNode = ReadWidget(root, null, nestedDepth, visited);

        var bindings = new List<WidgetBindingDto>();
        foreach (var binding in Structs(widgetClass, "Bindings"))
        {
            bindings.Add(new WidgetBindingDto(
                Text(binding, "ObjectName"),
                Text(binding, "PropertyName"),
                Text(binding, "FunctionName")));
        }

        var animations = new List<string>();
        foreach (var animation in Objects(widgetClass, "Animations"))
            animations.Add(animation.Name);

        var note = rootNode is null ? "控件树读取失败" : null;
        return new WidgetPreviewDto(
            displayName ?? widgetClass.Name,
            widgetClass.ExportType,
            rootNode,
            bindings,
            animations,
            _images.ToArray(),
            note);
    }

    private WidgetNodeDto? ReadWidget(UObject widget, UObject? slot, int nestedDepth, HashSet<string> visited)
    {
        var children = new List<WidgetNodeDto>();
        foreach (var childSlot in Objects(widget, "Slots"))
        {
            var content = Object(childSlot, "Content");
            if (content is null)
                continue;

            var child = ReadWidget(content, childSlot, nestedDepth, visited);
            if (child is not null)
                children.Add(child);
        }

        string? note = null;

        // 控件里套的另一个控件蓝图：把它的控件树也展开，展不开就留个说明。
        if (TryGetUserWidgetClass(widget, out var userWidgetClass) && userWidgetClass is not null)
        {
            var path = userWidgetClass.GetPathName();
            if (nestedDepth <= 0)
            {
                note = "嵌套控件未展开（层数限制）";
            }
            else if (!visited.Add(path))
            {
                note = "嵌套控件未展开（循环引用）";
            }
            else
            {
                try
                {
                    var nestedTree = Object(userWidgetClass, "WidgetTree");
                    var nestedRoot = Object(nestedTree, "RootWidget");
                    if (nestedRoot is not null && ReadWidget(nestedRoot, null, nestedDepth - 1, visited) is { } nestedNode)
                        children.Add(nestedNode);
                    else
                        note = "嵌套控件没有控件树";
                }
                catch (Exception ex)
                {
                    note = $"嵌套控件读取失败：{ex.Message}";
                }
                finally
                {
                    visited.Remove(path);
                }
            }
        }

        return new WidgetNodeDto(
            widget.Name,
            widget.ExportType,
            slot?.ExportType ?? string.Empty,
            ConvertProperties(widget),
            slot is null ? [] : ConvertProperties(slot),
            children,
            note);
    }

    private static bool TryGetUserWidgetClass(UObject widget, out UWidgetBlueprintGeneratedClass? widgetClass)
    {
        widgetClass = null;
        try
        {
            if (widget.Class?.Object?.Value is UWidgetBlueprintGeneratedClass resolved)
            {
                widgetClass = resolved;
                return true;
            }
        }
        catch (Exception)
        {
            // 类解析失败就当普通控件处理
        }

        return false;
    }

    private IReadOnlyList<WidgetPropertyDto> ConvertProperties(IPropertyHolder holder)
    {
        var result = new List<WidgetPropertyDto>(holder.Properties.Count);
        foreach (var tag in holder.Properties)
        {
            var name = tag.Name.Text;
            var typeName = tag.PropertyType.Text;
            WidgetValue value;
            try
            {
                value = ConvertValue(tag.Tag?.GenericValue, typeName, tag.TagData?.EnumName, 0);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"控件属性 {name} 读取失败：{ex.Message}");
                value = new WidgetRaw(typeName, ex.Message);
            }

            result.Add(new WidgetPropertyDto(name, typeName, value, tag.TagData?.EnumName));
        }

        return result;
    }

    private WidgetValue ConvertValue(object? raw, string typeName, string? enumName, int depth)
    {
        if (raw is null)
            return WidgetValue.Empty;

        switch (raw)
        {
            case FScriptStruct scriptStruct:
                return ConvertValue(scriptStruct.StructType, typeName, enumName, depth);

            case FStructFallback fallback:
                if (depth >= MaxPropertyDepth)
                    return new WidgetRaw(typeName, $"结构体（{fallback.Properties.Count} 个字段）");
                return new WidgetStruct(typeName, ConvertProperties(fallback));

            case FText text:
                return new WidgetText(text.Text ?? string.Empty);

            case FName name:
                return enumName is not null ? new WidgetEnum(TrimEnum(name.Text)) : new WidgetName(name.Text);

            case string str:
                return enumName is not null ? new WidgetEnum(TrimEnum(str)) : new WidgetName(str);

            case bool b:
                return new WidgetBoolean(b);

            case float f:
                return new WidgetNumber(f);

            case double d:
                return new WidgetNumber(d);

            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return new WidgetInteger(Convert.ToInt64(raw));

            case FLinearColor color:
                return new WidgetColor(color.R, color.G, color.B, color.A);

            case FVector2D vector2:
                return new WidgetVector(vector2.X, vector2.Y);

            case FVector4 vector4:
                return new WidgetVector4(vector4.X, vector4.Y, vector4.Z, vector4.W);

            case FIntPoint point:
                return new WidgetVector(point.X, point.Y);

            case FPackageIndex index:
                return index.IsNull ? WidgetValue.Empty : ConvertObjectRef(index, depth);

            case FSoftObjectPath softPath:
                return softPath.AssetPathName.IsNone
                    ? WidgetValue.Empty
                    : ConvertSoftObjectRef(softPath, depth);

            case UScriptArray array:
            {
                var items = new List<WidgetValue>(Math.Min(array.Properties.Count, MaxArrayItems));
                for (var i = 0; i < array.Properties.Count && i < MaxArrayItems; i++)
                {
                    var item = array.Properties[i];
                    items.Add(ConvertValue(item.GenericValue, array.InnerType, null, depth + 1));
                }

                return new WidgetList(array.InnerType, items);
            }

            default:
                return new WidgetRaw(typeName, Shorten(raw.ToString()));
        }
    }

    private WidgetValue ConvertObjectRef(FPackageIndex index, int depth)
    {
        UObject? resolved = null;
        try
        {
            if (index.ResolvedObject is not null && index.TryLoad(out var loaded))
                resolved = loaded;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"控件引用的对象加载失败：{ex.Message}");
        }

        if (resolved is null)
            return new WidgetObjectRef("Object", index.Name, string.Empty);

        var path = resolved.GetPathName();
        return new WidgetObjectRef(resolved.ExportType, resolved.Name, path, ResolveImageId(resolved, depth));
    }

    private WidgetValue ConvertSoftObjectRef(FSoftObjectPath softPath, int depth)
    {
        var path = softPath.AssetPathName.Text;
        UObject? resolved = null;
        try
        {
            if (softPath.TryLoad(out var loaded))
                resolved = loaded;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"控件引用的软引用加载失败：{ex.Message}");
        }

        return resolved is null
            ? new WidgetObjectRef("Object", softPath.AssetPathName.Text, path)
            : new WidgetObjectRef(resolved.ExportType, resolved.Name, path, ResolveImageId(resolved, depth));
    }

    /// <summary>贴图/材质 → 一张 PNG 的编号；解不出来就是 null（UI 侧画占位）。</summary>
    private string? ResolveImageId(UObject obj, int depth)
    {
        if (depth > 4)
            return null;

        var key = obj.GetPathName();
        if (_imageIds.TryGetValue(key, out var cached))
            return cached;

        // 先占位，避免材质互相引用时无限递归
        _imageIds[key] = null;

        string? id = null;
        var texture = FindTexture(obj, depth);
        if (texture is not null)
        {
            var encoded = _encodeTexture(texture);
            if (encoded is { PngData.Length: > 0 } && encoded.Value.Width > 0 && encoded.Value.Height > 0)
            {
                id = "img" + _images.Count;
                _images.Add(new WidgetImageDto(id, texture.Name, texture.GetPathName(), encoded.Value.Width, encoded.Value.Height, encoded.Value.PngData));
            }
        }

        _imageIds[key] = id;
        return id;
    }

    private UTexture? FindTexture(UObject obj, int depth)
    {
        switch (obj)
        {
            case UTexture texture:
                return texture;

            case UPaperSprite sprite:
                return sprite.BakedSourceTexture is { IsNull: false } atlas && atlas.TryLoad(out UTexture atlasTexture)
                    ? atlasTexture
                    : null;

            case UMaterialInstanceConstant instanceConstant:
                foreach (var parameter in instanceConstant.TextureParameterValues)
                {
                    try
                    {
                        if (parameter.ParameterValue is { IsNull: false } index &&
                            index.TryLoad(out UObject? value) &&
                            value is UTexture parameterTexture)
                            return parameterTexture;
                    }
                    catch (Exception)
                    {
                        // 参数贴图不存在就继续往下找
                    }
                }

                return instanceConstant.Parent is { } constantParent ? FindTexture(constantParent, depth + 1) : null;

            case UMaterialInstance instance:
                return instance.Parent is { } parent ? FindTexture(parent, depth + 1) : null;

            case UMaterial material:
                foreach (var referenced in material.ReferencedTextures)
                {
                    if (referenced is not null)
                        return referenced;
                }

                return null;

            default:
                return null;
        }
    }

    private static string TrimEnum(string value)
    {
        var separator = value.IndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? value[(separator + 2)..] : value;
    }

    private static string Shorten(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length <= 160 ? value : value[..160] + "…";
    }

    #region 宽容读取（所有属性都可能因为 unversioned 而缺失）

    private static object? Raw(IPropertyHolder? holder, string name)
    {
        if (holder is null)
            return null;

        foreach (var tag in holder.Properties)
        {
            if (!tag.Name.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = tag.Tag?.GenericValue;
            return value is FScriptStruct scriptStruct ? scriptStruct.StructType : value;
        }

        return null;
    }

    private static UObject? Object(IPropertyHolder? holder, string name)
    {
        return Raw(holder, name) switch
        {
            FPackageIndex index when !index.IsNull => index.TryLoad(out UObject loaded) ? loaded : null,
            FSoftObjectPath path when !path.AssetPathName.IsNone => path.TryLoad(out UObject loaded) ? loaded : null,
            _ => null,
        };
    }

    private static IReadOnlyList<UObject> Objects(IPropertyHolder? holder, string name)
    {
        var result = new List<UObject>();
        if (Raw(holder, name) is not UScriptArray array)
            return result;

        foreach (var item in array.Properties)
        {
            if (item.GenericValue is FPackageIndex index && !index.IsNull && index.TryLoad(out UObject loaded))
                result.Add(loaded);
        }

        return result;
    }

    private static IReadOnlyList<FStructFallback> Structs(IPropertyHolder? holder, string name)
    {
        var result = new List<FStructFallback>();
        if (Raw(holder, name) is not UScriptArray array)
            return result;

        foreach (var item in array.Properties)
        {
            if (item.GenericValue is FScriptStruct { StructType: FStructFallback fallback })
                result.Add(fallback);
        }

        return result;
    }

    private static string Text(IPropertyHolder? holder, string name)
    {
        return Raw(holder, name) switch
        {
            FText text => text.Text ?? string.Empty,
            FName fname => fname.Text,
            string str => str,
            _ => string.Empty,
        };
    }

    #endregion
}
