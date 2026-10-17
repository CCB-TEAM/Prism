namespace UAssetAudio.Core;

/// <summary>
/// 找外部工具（<c>binka_encode.exe</c> / <c>ffmpeg.exe</c>）。
/// 这些工具不随仓库走：先看调用方给的目录，再看程序旁边的 tools，最后看 PATH。
/// </summary>
public static class ExternalToolLocator
{
    public static string? Find(string fileName, string? hintDirectory = null)
    {
        foreach (var candidate in Candidates(fileName, hintDirectory))
        {
            try
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // 路径非法就当没这个人选
            }
        }

        return FindOnPath(fileName);
    }

    private static IEnumerable<string> Candidates(string fileName, string? hintDirectory)
    {
        if (!string.IsNullOrWhiteSpace(hintDirectory))
        {
            yield return Path.Combine(hintDirectory, fileName);
            yield return Path.Combine(hintDirectory, "tools", fileName);
        }

        foreach (var root in Roots())
        {
            yield return Path.Combine(root, fileName);
            yield return Path.Combine(root, "tools", fileName);
        }
    }

    private static IEnumerable<string> Roots()
    {
        yield return AppContext.BaseDirectory;

        var current = Directory.GetCurrentDirectory();
        yield return current;

        // 开发时：bin/<配置>/<框架>/ → 一路向上找仓库根的 tools
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && !string.IsNullOrEmpty(probe); i++)
        {
            probe = Path.GetDirectoryName(probe.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(probe))
                yield return probe;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // PATH 里的非法项直接跳过
            }
        }

        return null;
    }
}
