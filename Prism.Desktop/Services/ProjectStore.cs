using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Prism.Desktop.Models;

namespace Prism.Desktop.Services;

/// <summary>工程文件读写失败的统一异常，<see cref="Exception.Message"/> 直接给用户看。</summary>
public sealed class ModProjectException : Exception
{
    public ModProjectException(string message) : base(message)
    {
    }
}

/// <summary>
/// <c>.prismproj</c> 的读写：序列化、相对路径、素材收集。
///
/// 三条规则：
///   · 路径优先存相对路径（跟工程一起搬走仍然能用），跨盘才退回绝对路径；
///   · 素材默认复制到 <c>&lt;工程名&gt;.assets/</c>，工程才能发给别人；
///   · 写入走临时文件再改名，中途失败不会留下半份工程。
///
/// 这里不碰 AES 密钥，也不碰工作目录 —— 两者都不该进工程文件。
/// </summary>
public static class ProjectStore
{
    public const int CurrentSchema = 1;

    public const string Extension = ".prismproj";

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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(ModProject project) => JsonSerializer.Serialize(project, WriteOptions);

    public static ModProject Deserialize(string json)
    {
        ModProject? project;
        try
        {
            project = JsonSerializer.Deserialize<ModProject>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new ModProjectException($"这不是一份能读的工程文件：{ex.Message}");
        }

        if (project is null)
        {
            throw new ModProjectException("工程文件是空的。");
        }

        if (project.Schema > CurrentSchema)
        {
            throw new ModProjectException(
                $"这个工程由更新版本的 Prism 保存（格式 {project.Schema}，本版本最高支持 {CurrentSchema}），请先升级。");
        }

        if (!string.Equals(project.Kind, "patch", StringComparison.OrdinalIgnoreCase))
        {
            throw new ModProjectException($"这个工程是「{project.Kind}」工作流的，当前版本还只能打开替换工程。");
        }

        return project;
    }

    public static ModProject Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ModProjectException($"工程文件不在了：{path}");
        }

        return Deserialize(File.ReadAllText(path));
    }

    /// <summary>原子写入：先写 <c>.tmp</c> 再改名，避免半份工程盖掉好的那份。</summary>
    public static void Save(string path, ModProject project)
    {
        project.Schema = CurrentSchema;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(project));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>把绝对路径转成工程里的写法：能相对就相对，跨盘才写绝对路径。</summary>
    public static string ToStored(string projectDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return path.Replace('\\', '/');
        }

        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return full.Replace('\\', '/');
        }

        string directory;
        try
        {
            directory = Path.GetFullPath(projectDirectory);
        }
        catch
        {
            return full.Replace('\\', '/');
        }

        string? rootA = Path.GetPathRoot(directory);
        string? rootB = Path.GetPathRoot(full);
        if (rootA is null || rootB is null || !string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase))
        {
            return full.Replace('\\', '/');
        }

        return Path.GetRelativePath(directory, full).Replace('\\', '/');
    }

    /// <summary>工程里的写法 → 绝对路径（相对路径按工程所在目录解析）。</summary>
    public static string ToAbsolute(string projectDirectory, string stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return string.Empty;
        }

        string normalized = stored.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        try
        {
            if (Path.IsPathRooted(normalized))
            {
                return Path.GetFullPath(normalized);
            }

            return string.IsNullOrWhiteSpace(projectDirectory)
                ? normalized
                : Path.GetFullPath(Path.Combine(projectDirectory, normalized));
        }
        catch
        {
            return normalized;
        }
    }

    /// <summary>素材目录：<c>&lt;工程名&gt;.assets</c>，与工程文件同目录。</summary>
    public static string AssetsDirectoryFor(string projectPath)
    {
        string full = Path.GetFullPath(projectPath);
        string? directory = Path.GetDirectoryName(full);
        string stem = Path.GetFileNameWithoutExtension(full);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "project";
        }

        return Path.Combine(directory ?? ".", stem + ".assets");
    }

    /// <summary>
    /// 把一个素材复制进 <paramref name="assetsDirectory"/>，返回复制后的绝对路径。
    /// 同名文件内容相同则直接复用；内容不同则加 <c>_2</c> 后缀，绝不覆盖。
    /// </summary>
    public static string CollectAsset(string assetsDirectory, string sourcePath)
    {
        Directory.CreateDirectory(assetsDirectory);
        string name = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "asset";
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        string dest = Path.Combine(assetsDirectory, name);
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
        {
            return dest;
        }

        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        int suffix = 2;
        while (File.Exists(dest) && !SameContent(dest, sourcePath))
        {
            dest = Path.Combine(assetsDirectory, $"{stem}_{suffix}{extension}");
            suffix++;
        }

        File.Copy(sourcePath, dest, overwrite: true);
        return dest;
    }

    /// <summary>两个文件内容是否一致（先比长度，再比 SHA-256）。</summary>
    public static bool SameContent(string left, string right)
    {
        try
        {
            var a = new FileInfo(left);
            var b = new FileInfo(right);
            if (!a.Exists || !b.Exists || a.Length != b.Length)
            {
                return false;
            }

            return Hash(left).SequenceEqual(Hash(right));
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }
}
