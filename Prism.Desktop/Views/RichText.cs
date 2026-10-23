using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Prism.Desktop.Services;

namespace Prism.Desktop.Views;

/// <summary>
/// 把 <see cref="UpdateNoteRun"/> 列表塞进 <see cref="TextBlock.Inlines"/> 的附加属性。
///
/// 为什么需要它：发行说明里的加粗与行内代码是"行内"格式，Avalonia 的 Inlines 不是
/// 可绑定属性 —— 要么写代码后置逐块构造，要么给 TextBlock 挂一个附加属性。后者能让
/// 视图继续用纯数据模板（<c>local:RichText.Runs="{Binding Runs}"</c>），
/// 也顺便把"哪些字符算强调"留在解析器里，视图不必知道 markdown。
/// </summary>
public static class RichText
{
    public static readonly AttachedProperty<IReadOnlyList<UpdateNoteRun>?> RunsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<UpdateNoteRun>?>(
            "Runs",
            typeof(RichText));

    static RichText()
    {
        RunsProperty.Changed.AddClassHandler<TextBlock>((block, args) =>
            Apply(block, args.NewValue as IReadOnlyList<UpdateNoteRun>));
    }

    public static IReadOnlyList<UpdateNoteRun>? GetRuns(TextBlock block) => block.GetValue(RunsProperty);

    public static void SetRuns(TextBlock block, IReadOnlyList<UpdateNoteRun>? value) =>
        block.SetValue(RunsProperty, value);

    private static void Apply(TextBlock block, IReadOnlyList<UpdateNoteRun>? runs)
    {
        block.Inlines?.Clear();
        if (runs is null || runs.Count == 0 || block.Inlines is not { } inlines)
        {
            return;
        }

        foreach (UpdateNoteRun run in runs)
        {
            if (run.Text.Length == 0)
            {
                continue;
            }

            var inline = new Run(run.Text);
            if (run.Bold)
            {
                inline.FontWeight = FontWeight.SemiBold;
            }

            if (run.Code)
            {
                inline.FontFamily = new FontFamily("Consolas, Courier New");
            }

            if (run.Link && TryFindBrush("PrimaryBrush") is { } linkBrush)
            {
                inline.Foreground = linkBrush;
            }

            inlines.Add(inline);
        }
    }

    private static IBrush? TryFindBrush(string key) =>
        Application.Current is { } app && app.TryFindResource(key, out object? value) && value is IBrush brush
            ? brush
            : null;
}
