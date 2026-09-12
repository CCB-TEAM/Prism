using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PakTool.Core;

/// <summary>
/// locres（本地化资源）与 JSON 之间的转换。
///
/// 导出的 JSON 是"往返安全"的：包含足够信息（版本、命名空间哈希、键哈希、
/// 源哈希）以便重新写回 locres，同时保留 <c>index</c> 作为稳定的回填锚点。
///
/// 也兼容只带 <c>namespace</c> / <c>key</c> / <c>text</c> 的"瘦"翻译文件
/// （手工翻译或第三方工具产出的常见形式）——导入时按 index 优先、
/// namespace+key 兜底匹配。
/// </summary>
public static class LocresJsonExporter
{
    public const string FormatId = "prism.locres.json";

    /// <summary>导出所用的 JSON 选项：缩进、不转义非 ASCII（中文/日文可读）、忽略 null。</summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// 把 locres 二进制导出为 JSON 字节。默认带 UTF-8 BOM：Windows 记事本与
    /// 常见表格工具靠 BOM 判断编码，否则中文会显示为乱码。
    /// </summary>
    public static byte[] ToJsonBytes(byte[] locresData, bool includeMetadata = true, bool withBom = true)
    {
        LocresPreviewDto locres = LocresResourceCodec.Read(locresData);
        return ToJsonBytes(locres, includeMetadata, withBom);
    }

    public static byte[] ToJsonBytes(LocresPreviewDto locres, bool includeMetadata = true, bool withBom = true)
    {
        string json = ToJson(locres, includeMetadata);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: withBom);
        byte[] preamble = encoding.GetPreamble();
        byte[] body = encoding.GetBytes(json);
        if (preamble.Length == 0)
        {
            return body;
        }

        byte[] output = new byte[preamble.Length + body.Length];
        preamble.CopyTo(output, 0);
        body.CopyTo(output, preamble.Length);
        return output;
    }

    public static string ToJson(LocresPreviewDto locres, bool includeMetadata = true)
    {
        LocresJsonDocument document = new(
            includeMetadata ? FormatId : null,
            includeMetadata ? locres.Version : null,
            includeMetadata ? locres.NamespaceCount : null,
            includeMetadata ? locres.EntryCount : null,
            locres.Entries
                .Select(entry => new LocresJsonEntry(
                    entry.Index,
                    entry.Namespace,
                    entry.Key,
                    entry.Text,
                    // 哈希仅在该版本实际携带时写出，避免 Legacy 文件里一堆无意义的 0。
                    entry.NamespaceHash == 0 ? null : entry.NamespaceHash,
                    entry.KeyHash == 0 ? null : entry.KeyHash,
                    entry.SourceHash == 0 ? null : entry.SourceHash))
                .ToArray());

        return JsonSerializer.Serialize(document, WriteOptions);
    }

    /// <summary>
    /// 解析 JSON 翻译文件。返回 null 表示内容不是有效的 locres JSON（而非"没有条目"）。
    /// </summary>
    public static LocresJsonDocument? Parse(byte[] jsonBytes)
    {
        // 兼容带 BOM 与不带 BOM 的文件。
        string text = Encoding.UTF8.GetString(jsonBytes);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        try
        {
            return JsonSerializer.Deserialize<LocresJsonDocument>(text, ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把 JSON 翻译回填到原始 locres：以 index 为主键，缺失或越界时用
    /// namespace + key 匹配。返回回填后的 locres 二进制。
    /// </summary>
    /// <param name="originalLocres">原始 locres 二进制（决定版本与布局）。</param>
    /// <param name="jsonBytes">JSON 翻译文件字节。</param>
    /// <param name="applied">成功回填的条目数。</param>
    /// <param name="skipped">JSON 中未能匹配到原始条目的数量。</param>
    public static byte[] ApplyJson(byte[] originalLocres, byte[] jsonBytes, out int applied, out int skipped)
    {
        LocresJsonDocument document = Parse(jsonBytes)
            ?? throw new InvalidOperationException("JSON 文件格式无法解析为 locres 翻译。");

        if (document.Entries.Count == 0)
            throw new InvalidOperationException("JSON 文件中没有任何 entries 条目。");

        LocresPreviewDto original = LocresResourceCodec.Read(originalLocres);
        Dictionary<int, LocresEntryDto> byIndex = original.Entries.ToDictionary(entry => entry.Index);
        Dictionary<string, LocresEntryDto> byName = new(StringComparer.Ordinal);
        foreach (LocresEntryDto entry in original.Entries)
        {
            byName[$"{entry.Namespace}\u0000{entry.Key}"] = entry;
        }

        Dictionary<int, LocresEntryDto> replacements = [];
        skipped = 0;
        foreach (LocresJsonEntry jsonEntry in document.Entries)
        {
            LocresEntryDto? target = null;
            if (jsonEntry.Index >= 0 && byIndex.TryGetValue(jsonEntry.Index, out LocresEntryDto? indexed))
            {
                // index 命中时仍校验 namespace/key，避免错位的 JSON 静默写错行。
                bool nameMatches =
                    string.IsNullOrEmpty(jsonEntry.Namespace)
                    || (indexed.Namespace == jsonEntry.Namespace && indexed.Key == jsonEntry.Key);
                if (nameMatches)
                {
                    target = indexed;
                }
            }

            target ??= byName.GetValueOrDefault($"{jsonEntry.Namespace}\u0000{jsonEntry.Key}");
            if (target is null)
            {
                skipped++;
                continue;
            }

            replacements[target.Index] = target with { Text = jsonEntry.Text ?? string.Empty };
        }

        applied = replacements.Count;
        return LocresResourceCodec.ApplyTranslations(originalLocres, replacements.Values.ToArray());
    }
}

/// <summary>导出的 locres JSON 文档根。</summary>
public sealed record LocresJsonDocument(
    string? Format,
    string? Version,
    int? NamespaceCount,
    int? EntryCount,
    IReadOnlyList<LocresJsonEntry> Entries);

/// <summary>导出的 locres JSON 单条条目。</summary>
public sealed record LocresJsonEntry(
    int Index,
    string Namespace,
    string Key,
    string Text,
    uint? NamespaceHash = null,
    uint? KeyHash = null,
    uint? SourceHash = null);
