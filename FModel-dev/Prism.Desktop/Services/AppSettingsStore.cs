using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prism.Desktop.Services;

/// <summary>应用设置（Windows 持久化到 exe 目录；Android 持久化到应用私有目录，保证可写）。</summary>
internal sealed class AppSettings
{
    public bool ShowThumbnails { get; set; } = true;

    /// <summary>是否已应用“缩略图默认开启”的一次性迁移。</summary>
    public bool ThumbnailDefaultApplied { get; set; }

    public bool UseOodleCompression { get; set; }

    public bool AskBeforeReplace { get; set; } = true;

    /// <summary>界面过渡动画开关（低配设备或偏好静态界面的用户可关闭）。</summary>
    public bool IsAnimationsEnabled { get; set; } = true;

    public string ExportDirectory { get; set; } = string.Empty;

    /// <summary>Android SAF 导出目录书签（重启后恢复目录权限，Windows 不使用）。</summary>
    public string ExportDirectoryBookmark { get; set; } = string.Empty;

    public string PakPath { get; set; } = string.Empty;

    public string UsmapPath { get; set; } = string.Empty;

    /// <summary>
    /// 合并输入列表（顺序即覆盖优先级，主 Pak 在首位）。
    /// 兼容旧版本的单项 <c>MergePakPath</c>。
    /// </summary>
    public List<string> MergePakPaths { get; set; } = [];

    /// <summary>旧版本的单合并 Pak 字段，仅用于迁移。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MergePakPath { get; set; }

    public string MergeOutputPath { get; set; } = string.Empty;

    /// <summary>Pak 转换：待转换 Pak 路径。</summary>
    public string ConvertSourcePath { get; set; } = string.Empty;

    /// <summary>Pak 转换：输出路径（Android 上不持久化，见 SaveSettings）。</summary>
    public string ConvertOutputPath { get; set; } = string.Empty;

    /// <summary>Pak 转换：是否并入源 Pak 中主 Pak 没有的文件。</summary>
    public bool ConvertMergeAll { get; set; }

    public string AesKey { get; set; } = string.Empty;

    public double WindowWidth { get; set; } = 1280;

    public double WindowHeight { get; set; } = 820;
}

internal static class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string SettingsPath
    {
        get
        {
            // Android 上 AppContext.BaseDirectory 通常只读（APK 安装目录），
            // 必须放到应用私有目录，路径持久化才能跨重启生效。
            if (OperatingSystem.IsAndroid())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Prism",
                    "Prism.Desktop.settings.json");
            }

            return Path.Combine(AppContext.BaseDirectory, "Prism.Desktop.settings.json");
        }
    }

    public static AppSettings Load()
    {
        string path = SettingsPath;
        try
        {
            // Android 旧版本曾把配置写到 APK 安装目录（可能只读，写入会失败）。
            // 迁移一次旧配置，避免用户重新选择 Pak/Usmap 等路径。
            if (OperatingSystem.IsAndroid() && !File.Exists(path))
            {
                string legacyPath = Path.Combine(AppContext.BaseDirectory, "Prism.Desktop.settings.json");
                if (File.Exists(legacyPath))
                {
                    AppSettings? legacy = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(legacyPath), Options);
                    if (legacy is not null)
                    {
                        Save(legacy);
                        return legacy;
                    }
                }
            }

            if (File.Exists(path))
            {
                AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
                return Migrate(loaded);
            }
        }
        catch
        {
            // 配置损坏时回退默认
        }

        return new AppSettings();
    }

    /// <summary>
    /// 旧配置迁移：v1 只有单个 <c>mergePakPath</c>，本版本改为列表。
    /// 迁移后清空旧字段，避免下次写入又冒出来。
    /// </summary>
    private static AppSettings Migrate(AppSettings settings)
    {
        if (settings.MergePakPaths.Count == 0 && !string.IsNullOrWhiteSpace(settings.MergePakPath))
        {
            settings.MergePakPaths = [settings.MergePakPath];
        }

        settings.MergePakPath = null;
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string path = SettingsPath;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // 写入失败不阻塞使用
        }
    }
}
