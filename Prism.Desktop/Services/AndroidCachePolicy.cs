namespace Prism.Desktop.Services;

/// <summary>
/// Android 私有目录里那份"你选的文件"的缓存策略。
///
/// 为什么要有这个东西：SAF 给的是 <c>content://</c>，没有本地路径，所以每个文件都得
/// 复制一份到应用私有目录才能真正去读。问题是这些副本既占空间、又是"唯一的一份" ——
/// 删错了就是"我明明选过这个 Pak，怎么没了"。
///
/// 所以分成两类，规则不一样：
///
///   · <c>paks/</c> —— **你选的包与映射**。大、且重新选一次很烦（要找路径、可能要密码）。
///     每个槽位（Pak / 映射 / 待转换 Pak / 基准本地化）只留最新一份，换一个才删旧的；
///     启动时的自动清理**不碰**这里。
///   · <c>picked/</c> —— **素材副本**（换图 / 换音频 / 换视频 / 合并列表里的包）。
///     这些要么已经收进工程了 <c>&lt;工程名&gt;.assets/</c>，要么下次还会重新选，
///     所以启动时按"当前设置还引用着谁"清一遍。
///
/// 之前两类混在 <c>picked/</c> 里，于是"启动清理"会顺手删掉工程要用的 Pak —— 那是个真 bug。
/// </summary>
public static class AndroidCachePolicy
{
    /// <summary>你选的包与映射。启动清理不会碰。</summary>
    public const string DocumentsFolder = "paks";

    /// <summary>素材副本。启动清理会收拾。</summary>
    public const string AssetsFolder = "picked";

    /// <summary>当前设置里还引用着的路径（素材副本里这些要留下）。</summary>
    public static HashSet<string> BuildKeepSet(IEnumerable<string?> trackedPaths)
    {
        HashSet<string> keep = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in trackedPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                keep.Add(Path.GetFullPath(path));
            }
            catch
            {
                // 非法路径无法作为保留项
            }
        }

        return keep;
    }

    /// <summary>
    /// 删掉 <paramref name="folder"/> 里不在保留集合中的文件，返回删除数量。
    ///
    /// 与平台无关：Android 启动清理与测试都走这一份，
    /// 这样"哪份会被删"是可断言的，而不是只在手机上才看得出来。
    /// </summary>
    public static int Cleanup(string folder, ISet<string> keep)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        int removed = 0;
        foreach (string file in Directory.EnumerateFiles(folder))
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(file);
            }
            catch
            {
                continue;
            }

            if (keep.Contains(fullPath))
            {
                continue;
            }

            try
            {
                File.Delete(fullPath);
                removed++;
            }
            catch
            {
                // 删不掉就留着，下次再说
            }
        }

        return removed;
    }

    /// <summary>这个文件是不是我们自己的缓存副本（换选新文件时可以安全删掉旧的）。</summary>
    public static bool IsCacheCopy(string privateDataDirectory, string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        foreach (string folder in new[] { DocumentsFolder, AssetsFolder })
        {
            string root;
            try
            {
                root = Path.GetFullPath(Path.Combine(privateDataDirectory, folder)) + Path.DirectorySeparatorChar;
            }
            catch
            {
                continue;
            }

            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>统计一个目录里的文件数与总字节（目录不存在就是 0）。</summary>
    public static (int Files, long Bytes) Measure(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return (0, 0);
        }

        int files = 0;
        long bytes = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch
                {
                    // 统计不了的不影响总数
                }
            }
        }
        catch
        {
            // 目录读不了就报 0
        }

        return (files, bytes);
    }

    /// <summary>把几处加起来（目录不存在的不计）。</summary>
    public static (int Files, long Bytes) MeasureAll(IEnumerable<string> folders)
    {
        int files = 0;
        long bytes = 0;
        foreach (string folder in folders)
        {
            (int partFiles, long partBytes) = Measure(folder);
            files += partFiles;
            bytes += partBytes;
        }

        return (files, bytes);
    }
}
