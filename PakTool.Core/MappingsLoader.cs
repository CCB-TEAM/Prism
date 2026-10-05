using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Jmap;
using CUE4Parse.MappingsProvider.Usmap;

namespace PakTool.Core;

/// <summary>
/// 映射文件（mappings）加载器：按扩展名在 .usmap 与 .jmap 之间自动选择解析器。
///
/// - <c>.usmap</c>：标准 usmap 二进制格式（FModel / UE4SS 导出）。
/// - <c>.jmap</c>：JSON 映射格式，部分社区的 game dump 使用。
/// - <c>.jmap.gz</c> / <c>.usmap.gz</c>：gzip 压缩的 jmap（CUE4Parse 会自动解压）。
///
/// 解析器基类相同（<see cref="JmapTypeMappingsProvider"/> 派生自
/// <see cref="FileUsmapTypeMappingsProvider"/>），所以调用方拿到的是同一套接口。
/// </summary>
public static class MappingsLoader
{
    /// <summary>映射文件可用的扩展名（供文件选择器过滤）。</summary>
    public static readonly string[] Patterns = ["*.usmap", "*.jmap", "*.usmap.gz", "*.jmap.gz"];

    /// <summary>
    /// 判断扩展名是否需要 jmap 解析器。CUE4Parse 的 <c>JmapParser</c> 以
    /// <c>.gz</c> 结尾为准做解压，所以 <c>.jmap.gz</c> 也走 jmap 分支。
    /// </summary>
    public static bool IsJmap(string path)
    {
        string fileName = Path.GetFileName(path);
        return fileName.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 按扩展名创建映射提供程序。无法识别的扩展名按 usmap 处理
    /// （usmap 解析器会校验魔数，失败信息更明确）。
    ///
    /// 解析失败会包装成带文件路径与格式的可读异常：底层解析器
    /// （CUE4Parse 的 <c>UsmapParser</c> / <c>JmapParser</c>）抛出的是
    /// <c>FormatException</c> 或 <c>JsonReaderException</c>，直接冒泡到 UI
    /// 只会显示"JSON 起始字符无效"这类看不出所以然的信息。
    /// </summary>
    public static AbstractTypeMappingsProvider Create(string path, StringComparer? comparer = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("映射文件路径不能为空。", nameof(path));

        string format = DescribeFormat(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"找不到{format} 映射文件：{path}", path);
        }

        try
        {
            return IsJmap(path)
                ? new JmapTypeMappingsProvider(path, comparer)
                : new FileUsmapTypeMappingsProvider(path, comparer);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"{format} 映射文件解析失败：{Path.GetFileName(path)}。" +
                $"请确认文件扩展名与实际格式一致（.usmap 是二进制、.jmap 是 JSON），且与游戏版本匹配。" +
                $"原始错误：{ex.Message}",
                ex);
        }
    }

    /// <summary>映射格式的展示名，用于日志与状态栏。</summary>
    public static string DescribeFormat(string path) => IsJmap(path) ? "jmap" : "usmap";
}
