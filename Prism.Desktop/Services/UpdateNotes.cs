using System.Text;
using System.Text.RegularExpressions;

namespace Prism.Desktop.Services;

/// <summary>发行说明里一段文字中带样式的最小单位。</summary>
public sealed record UpdateNoteRun(string Text, bool Bold = false, bool Code = false, bool Link = false);

/// <summary>发行说明里的一块内容。</summary>
public enum UpdateNoteKind
{
    Heading,
    Bullet,
    Paragraph,
}

public sealed record UpdateNote(UpdateNoteKind Kind, IReadOnlyList<UpdateNoteRun> Runs)
{
    /// <summary>纯文本形式（日志与原生对话框用）。</summary>
    public string PlainText => string.Concat(Runs.Select(run => run.Text));

    /// <summary>视图用的纯文本（不绑 Inlines 时的兜底）。</summary>
    public string Text => PlainText;

    public bool IsHeading => Kind == UpdateNoteKind.Heading;

    public bool IsBullet => Kind == UpdateNoteKind.Bullet;
}

/// <summary>
/// 把 GitHub 发行版正文（markdown）解析成可渲染的小块。
///
/// 为什么不用完整的 markdown 库：这里只需要"标题 / 列表 / 段落 / 加粗 / 行内代码 / 链接"
/// 这几样，而正文是我们自己写的更新日志。引入一个 markdown → Avalonia 控件的渲染器
/// 体积和风险都远超收益。解析器保持纯函数，测试可以拿真实发行版正文直接断言。
/// </summary>
internal static class UpdateNotes
{
    /// <summary>最多保留的块数：发行说明是欢迎页正文，不是一整本书。</summary>
    public const int MaxBlocks = 24;

    /// <summary>单行最多字符数：超长段落截断，避免一屏全是字。</summary>
    public const int MaxLineLength = 300;

    private static readonly Regex ImagePattern = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex InlinePattern = new(
        @"\*\*(?<bold>[^*]+)\*\*|`(?<code>[^`]+)`|\[(?<link>[^\]]+)\]\([^)]*\)|~~(?<strike>[^~]+)~~",
        RegexOptions.Compiled);

    public static IReadOnlyList<UpdateNote> Parse(string? markdown, int maxBlocks = MaxBlocks)
    {
        var notes = new List<UpdateNote>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return notes;
        }

        foreach (string rawLine in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (notes.Count >= maxBlocks)
            {
                break;
            }

            string line = rawLine.Trim();

            // 分隔线、空行、纯图片行、HTML 注释：不产生内容
            if (line.Length == 0 ||
                line is "---" or "***" or "___" ||
                line.StartsWith("<!--", StringComparison.Ordinal))
            {
                continue;
            }

            line = ImagePattern.Replace(line, string.Empty).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            UpdateNoteKind kind = UpdateNoteKind.Paragraph;
            if (line.StartsWith('#'))
            {
                line = line.TrimStart('#').Trim();
                kind = UpdateNoteKind.Heading;
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) ||
                     line.StartsWith("* ", StringComparison.Ordinal) ||
                     line.StartsWith("+ ", StringComparison.Ordinal))
            {
                line = line[2..].Trim();
                kind = UpdateNoteKind.Bullet;
            }
            else if (line.StartsWith('>'))
            {
                line = line.TrimStart('>').Trim();
            }

            // 复选框列表 - [x] / - [ ]
            if (line.StartsWith("[x]", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[ ]", StringComparison.Ordinal))
            {
                line = line[3..].Trim();
            }

            line = HtmlTagPattern.Replace(line, string.Empty);
            if (line.Length > MaxLineLength)
            {
                line = line[..MaxLineLength] + "…";
            }

            IReadOnlyList<UpdateNoteRun> runs = ParseRuns(line);
            if (runs.Count > 0)
            {
                notes.Add(new UpdateNote(kind, runs));
            }
        }

        return notes;
    }

    /// <summary>把一行里的 **加粗** / `代码` / [链接](url) / ~~删除线~~ 拆成带样式的片段。</summary>
    public static IReadOnlyList<UpdateNoteRun> ParseRuns(string line)
    {
        var runs = new List<UpdateNoteRun>();
        if (line.Length == 0)
        {
            return runs;
        }

        int cursor = 0;
        foreach (Match match in InlinePattern.Matches(line))
        {
            if (match.Index > cursor)
            {
                runs.Add(new UpdateNoteRun(line[cursor..match.Index]));
            }

            if (match.Groups["bold"].Success)
            {
                runs.Add(new UpdateNoteRun(match.Groups["bold"].Value, Bold: true));
            }
            else if (match.Groups["code"].Success)
            {
                runs.Add(new UpdateNoteRun(match.Groups["code"].Value, Code: true));
            }
            else if (match.Groups["link"].Success)
            {
                runs.Add(new UpdateNoteRun(match.Groups["link"].Value, Link: true));
            }
            else if (match.Groups["strike"].Success)
            {
                // 删除线在发行说明里几乎只出现在"废弃"语境，保留文字、丢掉样式即可
                runs.Add(new UpdateNoteRun(match.Groups["strike"].Value));
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < line.Length)
        {
            runs.Add(new UpdateNoteRun(line[cursor..]));
        }

        return runs;
    }

    /// <summary>纯文本渲染（原生对话框 / 日志用）：标题空一行、列表加圆点。</summary>
    public static string ToPlainText(IReadOnlyList<UpdateNote> notes)
    {
        var builder = new StringBuilder();
        foreach (UpdateNote note in notes)
        {
            switch (note.Kind)
            {
                case UpdateNoteKind.Heading:
                    if (builder.Length > 0)
                    {
                        builder.AppendLine();
                    }

                    builder.AppendLine(note.PlainText);
                    break;
                case UpdateNoteKind.Bullet:
                    builder.Append("· ").AppendLine(note.PlainText);
                    break;
                default:
                    builder.AppendLine(note.PlainText);
                    break;
            }
        }

        return builder.ToString().TrimEnd();
    }
}
