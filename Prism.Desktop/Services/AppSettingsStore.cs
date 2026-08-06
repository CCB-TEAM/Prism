using System.Text.Json;

namespace Prism.Desktop.Services;

/// <summary>应用设置（持久化到 exe 目录下的 JSON 文件，便携可迁移）。</summary>
internal sealed class AppSettings
{
    public bool ShowThumbnails { get; set; }

    public bool UseOodleCompression { get; set; }

    public bool AskBeforeReplace { get; set; } = true;

    public string ExportDirectory { get; set; } = string.Empty;

    public string PakPath { get; set; } = string.Empty;

    public string UsmapPath { get; set; } = string.Empty;

    public string MergePakPath { get; set; } = string.Empty;

    public string MergeOutputPath { get; set; } = string.Empty;

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

    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "Prism.Desktop.settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ?? new AppSettings();
            }
        }
        catch
        {
            // 配置损坏时回退默认
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // 写入失败不阻塞使用
        }
    }
}
