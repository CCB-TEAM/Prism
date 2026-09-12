using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Prism.Desktop.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// 结构化运行日志：内存环形缓冲 + 可选落盘镜像。
///
/// 相比旧版（只有一行 <c>HH:mm:ss 文本</c>）的改进：
/// - 毫秒时间戳与单调递增序号，长任务耗时可直接对照；
/// - 分级（DEBUG/INFO/WARN/ERROR）+ 分类标签，便于过滤与统计；
/// - 异常记录完整 <c>ToString()</c>（类型、消息、堆栈、InnerException），
///   而不是只留 <c>ex.Message</c>；
/// - 记录操作耗时、条目数与关键参数，导出后足以定位问题；
/// - 导出为带环境头（版本/系统/架构/编码器状态）的完整报告。
/// </summary>
public static class AppLog
{
    /// <summary>内存中保留的最大行数。10000 行约 1~3 MB，手机上也安全。</summary>
    private const int MaxLines = 10_000;

    /// <summary>单条日志最大长度，防止一次超大异常把缓冲撑爆。</summary>
    private const int MaxLineLength = 8_000;

    private static readonly List<LogRecord> Records = [];
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static long _sequence;
    private static string? _mirrorPath;
    private static bool _mirrorFailed;

    /// <summary>日志级别阈值：低于该级别的记录被丢弃。</summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    /// <summary>当前缓冲中的记录数。</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Records.Count;
            }
        }
    }

    /// <summary>各级别计数，用于导出报告与诊断面板。</summary>
    public static LogLevelCounts LevelCounts
    {
        get
        {
            lock (Gate)
            {
                int debug = 0, info = 0, warn = 0, error = 0;
                foreach (LogRecord record in Records)
                {
                    switch (record.Level)
                    {
                        case LogLevel.Debug: debug++; break;
                        case LogLevel.Info: info++; break;
                        case LogLevel.Warn: warn++; break;
                        case LogLevel.Error: error++; break;
                    }
                }

                return new LogLevelCounts(debug, info, warn, error);
            }
        }
    }

    /// <summary>
    /// 开启落盘镜像：日志实时追加到文件。崩溃/被杀进程时仍能拿到最后几条，
    /// 这是纯内存缓冲做不到的。失败时静默降级为纯内存。
    /// </summary>
    public static void EnableFileMirror(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (Gate)
            {
                _mirrorPath = path;
                _mirrorFailed = false;
                File.WriteAllText(path, string.Empty, Encoding.UTF8);
            }
        }
        catch
        {
            lock (Gate)
            {
                _mirrorPath = null;
                _mirrorFailed = true;
            }
        }
    }

    public static void Debug(string message, string category = "app") => Write(LogLevel.Debug, category, message);

    public static void Info(string message, string category = "app") => Write(LogLevel.Info, category, message);

    public static void Warn(string message, string category = "app") => Write(LogLevel.Warn, category, message);

    public static void Error(string message, string category = "app") => Write(LogLevel.Error, category, message);

    /// <summary>
    /// 记录异常：包含类型、消息与完整堆栈。可选附加上下文说明与耗时。
    /// </summary>
    public static void Exception(
        Exception exception,
        string context,
        string category = "app",
        LogLevel level = LogLevel.Error,
        long? elapsedMs = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        StringBuilder builder = new();
        builder.Append(context);
        builder.Append(" → ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
        if (elapsedMs is { } ms)
        {
            builder.Append(" [").Append(ms).Append(" ms]");
        }

        // 完整 ToString() 含堆栈与内部异常；这是定位问题的关键信息。
        builder.AppendLine();
        builder.Append(Indent(exception.ToString() ?? string.Empty));
        Write(level, category, builder.ToString());
    }

    /// <summary>记录一次操作及其耗时。</summary>
    public static void Operation(string category, string action, bool success, long elapsedMs, string? detail = null)
    {
        string status = success ? "OK" : "FAIL";
        string line = $"{action} … {status} ({elapsedMs} ms)";
        if (!string.IsNullOrWhiteSpace(detail))
        {
            line += " | " + detail;
        }

        Write(success ? LogLevel.Info : LogLevel.Warn, category, line);
    }

    /// <summary>
    /// 旧接口兼容：按 INFO 记录一行纯文本。
    /// </summary>
    public static void Add(string line) => Info(line);

    /// <summary>清空缓冲（不影响已落盘的镜像文件）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Records.Clear();
        }
    }

    /// <summary>纯日志正文（不含环境头），用于设置页预览。</summary>
    public static string FullText
    {
        get
        {
            lock (Gate)
            {
                return string.Join(Environment.NewLine, Records.Select(Format));
            }
        }
    }

    /// <summary>最近 <paramref name="count"/> 行的正文。</summary>
    public static string Tail(int count)
    {
        lock (Gate)
        {
            int skip = Math.Max(0, Records.Count - count);
            return string.Join(Environment.NewLine, Records.Skip(skip).Select(Format));
        }
    }

    /// <summary>
    /// 构建完整导出报告：环境头 + 统计 + 全部日志。
    /// 头部信息让日志脱离本机后仍可解读（版本、系统、编码器可用性）。
    /// </summary>
    public static string BuildReport(string? appName = null, IDictionary<string, string?>? environment = null)
    {
        List<LogRecord> snapshot;
        lock (Gate)
        {
            snapshot = [.. Records];
        }

        StringBuilder builder = new();
        builder.AppendLine("================ Prism 诊断日志 ================");
        builder.AppendLine($"生成时间   : {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"应用       : {appName ?? ResolveEntryAssemblyName()}");
        builder.AppendLine($"应用版本   : {ResolveVersion()}");
        builder.AppendLine($"运行时长   : {FormatDuration(Clock.Elapsed)}");
        builder.AppendLine($"操作系统   : {RuntimeInformation.OSDescription}");
        builder.AppendLine($"系统架构   : {RuntimeInformation.OSArchitecture} / 进程 {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($".NET 版本  : {RuntimeInformation.FrameworkDescription}");
        if (OperatingSystem.IsAndroid())
        {
            builder.AppendLine("平台       : Android（SAF 文件选择，进程内纹理编码）");
        }
        else
        {
            builder.AppendLine("平台       : Windows 桌面（UAssetCLI 进程外纹理替换）");
        }

        if (environment is not null)
        {
            foreach ((string key, string? value) in environment)
            {
                builder.AppendLine($"{key.PadRight(11)}: {value ?? "<未探测>"}");
            }
        }

        LogLevelCounts counts = LevelCounts;
        builder.AppendLine($"日志行数   : {snapshot.Count}（DEBUG {counts.Debug} / INFO {counts.Info} / WARN {counts.Warn} / ERROR {counts.Error}）");
        builder.AppendLine("================================================");
        builder.AppendLine();

        foreach (LogRecord record in snapshot)
        {
            builder.AppendLine(Format(record));
        }

        if (counts.Error == 0 && counts.Warn == 0)
        {
            builder.AppendLine();
            builder.AppendLine("（本次运行没有 WARN / ERROR 记录）");
        }

        return builder.ToString();
    }

    /// <summary>异常堆栈缩进，避免多行内容破坏时间戳列的视觉对齐。</summary>
    private static string Indent(string text) =>
        string.Join(Environment.NewLine, text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => "    " + line.TrimEnd()));

    private static void Write(LogLevel level, string category, string message)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        LogRecord record;
        lock (Gate)
        {
            record = new LogRecord(
                Interlocked.Increment(ref _sequence),
                DateTime.Now,
                Clock.ElapsedMilliseconds,
                level,
                category,
                Truncate(message));

            Records.Add(record);
            if (Records.Count > MaxLines)
            {
                // 一次多删一些，避免每写一行都做数组搬移。
                Records.RemoveRange(0, Records.Count - MaxLines);
            }

            AppendToMirror(record);
        }
    }

    private static void AppendToMirror(LogRecord record)
    {
        if (_mirrorPath is null || _mirrorFailed)
        {
            return;
        }

        try
        {
            File.AppendAllText(_mirrorPath, Format(record) + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 磁盘满/权限变化时停用镜像，不影响内存日志与 UI。
            _mirrorFailed = true;
        }
    }

    private static string Truncate(string message) =>
        message.Length <= MaxLineLength
            ? message
            : message[..MaxLineLength] + $" …（已截断，原长 {message.Length} 字符）";

    private static string Format(LogRecord record) =>
        $"{record.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{LevelLabel(record.Level)}] [{record.Category}] (+{record.ElapsedMs}ms) {record.Message}";

    private static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        _ => "ERROR",
    };

    private static string FormatDuration(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分 {elapsed.Seconds} 秒"
            : elapsed.TotalMinutes >= 1
                ? $"{elapsed.Minutes} 分 {elapsed.Seconds} 秒"
                : $"{elapsed.TotalSeconds:F1} 秒";

    private static string ResolveEntryAssemblyName() =>
        Assembly.GetEntryAssembly()?.GetName().Name ?? "Prism.Desktop";

    private static string ResolveVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "未知";

    private sealed record LogRecord(
        long Sequence,
        DateTime Timestamp,
        long ElapsedMs,
        LogLevel Level,
        string Category,
        string Message);
}

public readonly record struct LogLevelCounts(int Debug, int Info, int Warn, int Error);
