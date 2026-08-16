namespace Prism.Desktop.Services;

/// <summary>轻量运行日志收集器（内存环形缓冲，供诊断面板与导出日志使用）。</summary>
public static class AppLog
{
    private const int MaxLines = 2000;
    private static readonly List<string> Lines = [];
    private static readonly object Gate = new();

    public static void Add(string line)
    {
        lock (Gate)
        {
            Lines.Add($"{DateTime.Now:HH:mm:ss} {line}");
            if (Lines.Count > MaxLines)
            {
                Lines.RemoveRange(0, Lines.Count - MaxLines);
            }
        }
    }

    public static string FullText
    {
        get
        {
            lock (Gate)
            {
                return string.Join(Environment.NewLine, Lines);
            }
        }
    }

    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Lines.Count;
            }
        }
    }
}
