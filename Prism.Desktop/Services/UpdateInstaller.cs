using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Prism.Desktop.Services;

/// <summary>自更新准备的结果。</summary>
internal sealed record UpdatePrepareResult(bool Succeeded, string Message)
{
    /// <summary>解压后的新版本目录（成功时非空）。</summary>
    public string PayloadDirectory { get; init; } = string.Empty;

    /// <summary>下载下来的安装包路径（成功时非空，失败时也可能已下载一部分）。</summary>
    public string PackagePath { get; init; } = string.Empty;

    /// <summary>等待应用退出后执行的脚本（成功时非空）。</summary>
    public string ApplierPath { get; init; } = string.Empty;
}

/// <summary>
/// 就地自更新：下载发行版压缩包 → 解压到临时目录 → 写一个"等本进程退出再覆盖"的批处理。
///
/// <b>为什么必须绕一道脚本</b>：Windows 上正在运行的 exe 不能被覆盖（文件被占用），
/// 而覆盖又必须发生在本进程退出之后。所以这里只负责把新文件准备好、把搬运脚本写好，
/// 由调用方关掉界面再启动脚本；脚本等 PID 消失后 robocopy 覆盖、重新拉起程序、自删。
///
/// 失败一律返回可显示的中文原因，不做"尽力而为"的部分覆盖 —— 半更新的应用比不更新更难查。
/// </summary>
internal static class UpdateInstaller
{
    /// <summary>桌面端（Windows）就地更新；其它平台交给系统安装器。</summary>
    public static bool SupportsInPlaceUpdate => OperatingSystem.IsWindows();

    /// <summary>下载 + 解压 + 写脚本。不启动脚本，也不关应用（那一步由调用方决定）。</summary>
    public static async Task<UpdatePrepareResult> PrepareAsync(
        UpdateInfo info,
        UpdateAsset asset,
        IProgress<double>? downloadProgress,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        if (!SupportsInPlaceUpdate)
        {
            return new UpdatePrepareResult(false, "当前平台不支持就地更新。");
        }

        string root = Path.Combine(Path.GetTempPath(), "prism-update", info.Version);
        string packagePath = Path.Combine(root, asset.Name);
        status?.Report("正在下载更新包…");
        try
        {
            await UpdateService.DownloadAsync(asset, packagePath, downloadProgress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            return new UpdatePrepareResult(false, $"下载失败：{ex.Message}") { PackagePath = packagePath };
        }

        status?.Report("正在解压更新包…");
        string extractRoot = Path.Combine(root, "payload");
        try
        {
            if (Directory.Exists(extractRoot))
            {
                Directory.Delete(extractRoot, recursive: true);
            }

            ZipFile.ExtractToDirectory(packagePath, extractRoot);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new UpdatePrepareResult(false, $"解压失败：{ex.Message}") { PackagePath = packagePath };
        }

        (string payloadDirectory, string? executableName) = LocatePayload(extractRoot);
        if (executableName is null)
        {
            return new UpdatePrepareResult(false, "更新包里没找到主程序，已取消（请到 GitHub 手动下载）。")
            {
                PackagePath = packagePath,
            };
        }

        string targetDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (!IsDirectoryWritable(targetDirectory))
        {
            return new UpdatePrepareResult(
                false,
                $"应用所在目录不可写，无法自动替换：{targetDirectory}\n请手动解压 {packagePath} 覆盖该目录。")
            {
                PayloadDirectory = payloadDirectory,
                PackagePath = packagePath,
            };
        }

        status?.Report("正在准备替换…");
        int processId = Environment.ProcessId;
        string applierPath = Path.Combine(root, "apply-update.cmd");
        try
        {
            string script = BuildApplierScript(
                payloadDirectory,
                targetDirectory,
                executableName,
                processId,
                restart: true);
            await File.WriteAllTextAsync(applierPath, script, Encoding.Default, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UpdatePrepareResult(false, $"写入更新脚本失败：{ex.Message}")
            {
                PayloadDirectory = payloadDirectory,
                PackagePath = packagePath,
            };
        }

        return new UpdatePrepareResult(true, $"已准备好 v{info.Version}，重启后生效。")
        {
            PayloadDirectory = payloadDirectory,
            PackagePath = packagePath,
            ApplierPath = applierPath,
        };
    }

    /// <summary>
    /// 生成"等 PID 退出 → 备份旧 exe → 覆盖 → 重开 → 自删"的批处理。
    /// 独立成公开方法，测试可以断言脚本里确实引用了正确的目录与 PID（不执行它）。
    /// </summary>
    public static string BuildApplierScript(
        string payloadDirectory,
        string targetDirectory,
        string executableName,
        int processId,
        bool restart)
    {
        var builder = new StringBuilder();
        builder.AppendLine("@echo off");
        builder.AppendLine("chcp 65001 >nul");
        builder.AppendLine("setlocal");
        builder.AppendLine($"set \"SRC={payloadDirectory}\"");
        builder.AppendLine($"set \"DST={targetDirectory}\"");
        builder.AppendLine($"set \"EXE={executableName}\"");
        builder.AppendLine($"set \"PID={processId}\"");
        builder.AppendLine();
        builder.AppendLine("rem 等本进程退出：正在运行的 exe 无法覆盖");
        builder.AppendLine(":wait");
        builder.AppendLine("tasklist /FI \"PID eq %PID%\" /NH 2>nul | find /I \"%PID%\" >nul");
        builder.AppendLine("if not errorlevel 1 (");
        builder.AppendLine("  ping -n 2 127.0.0.1 >nul");
        builder.AppendLine("  goto wait");
        builder.AppendLine(")");
        builder.AppendLine();
        builder.AppendLine("rem 旧主程序留一份 .bak，覆盖失败还能手动改回来");
        builder.AppendLine("if exist \"%DST%\\%EXE%\" copy /y \"%DST%\\%EXE%\" \"%DST%\\%EXE%.bak\" >nul 2>nul");
        builder.AppendLine("robocopy \"%SRC%\" \"%DST%\" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul");
        if (restart)
        {
            builder.AppendLine();
            builder.AppendLine("rem 重新拉起应用（工作目录必须是应用目录，否则相对路径的 tools/ 找不到）");
            builder.AppendLine("pushd \"%DST%\"");
            builder.AppendLine("start \"\" \"%DST%\\%EXE%\"");
            builder.AppendLine("popd");
        }

        builder.AppendLine();
        builder.AppendLine("rem 清掉解压出来的那份新文件（解压后的体积是安装包的两倍，留着白占几百 MB）");
        builder.AppendLine("rd /s /q \"%SRC%\" 2>nul");
        builder.AppendLine("del \"%~dp0*.zip\" 2>nul");
        builder.AppendLine("del \"%~f0\"");
        return builder.ToString();
    }

    /// <summary>启动脚本（脱离本进程，关掉应用后继续跑）。返回是否成功拉起。</summary>
    public static bool LaunchApplier(string applierPath, string workingDirectory)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{applierPath}\"\"",
                WorkingDirectory = workingDirectory,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            return Process.Start(startInfo) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在解压目录里找主程序：发行包把文件放在根目录，但 GitHub 自动生成的 zip
    /// 会套一层 <c>Prism-1.2.3/</c>，所以根目录找不到就往下一层找。
    /// </summary>
    private static (string Directory, string? ExecutableName) LocatePayload(string extractRoot)
    {
        string? name = FindExecutable(extractRoot);
        if (name is not null)
        {
            return (extractRoot, name);
        }

        foreach (string child in Directory.EnumerateDirectories(extractRoot))
        {
            string? nested = FindExecutable(child);
            if (nested is not null)
            {
                return (child, nested);
            }
        }

        return (extractRoot, null);
    }

    private static string? FindExecutable(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*.exe"))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith("Prism", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>真实写一次再删掉：目录不可写时（例如装在 Program Files）要提前说清楚。</summary>
    private static bool IsDirectoryWritable(string directory)
    {
        string probe = Path.Combine(directory, $".prism-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
