using System.Globalization;
using Avalonia.Data.Converters;

namespace Prism.Desktop.Views;

/// <summary>
/// 把文档块的 <c>ImageKey</c>（"welcome"、"merge"…）换成当前主题下的那张截图。
/// 转换器本身无状态，解码与缓存都在 <see cref="Docs.DocImageProvider"/> 里。
/// </summary>
public sealed class DocImageConverter : IValueConverter
{
    public static readonly DocImageConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Docs.DocImageProvider.Get(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
