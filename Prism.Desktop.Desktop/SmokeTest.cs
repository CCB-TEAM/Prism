using System;
using System.IO;
using PakTool.Core;

namespace Prism.Desktop;

/// <summary>
/// 命令行冒烟测试：不启动 UI，直接跑通「打开 Pak → 搜索 → 纹理预览」核心链路，
/// 用于验证依赖链（尤其 SkiaSharp 版本兼容性）在真实运行环境下是否正常。
/// 用法: Prism.Desktop --smoke [pakPath] [usmapPath]
///
/// 注意：这里刻意<b>不</b>初始化 Avalonia。此进程已进入 WPF 消息循环，
/// 再初始化 Avalonia 的 headless/真实后端会导致进程无法正常退出（脚本拿不到结果）。
/// 需要验证 XAML 加载用测试工程里的 headless fixture（见 README「验证」小节）。
/// </summary>
public static class SmokeTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        // 输出含中文；Windows 控制台默认代码页不是 UTF-8，不设置会变乱码。
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // 某些宿主（重定向输出）不支持设置编码。
        }

        string pakPath = args.Length > 1 ? args[1] : @"E:\pak\test.pak";
        string? usmapPath = args.Length > 2 ? args[2] : @"E:\pak\Mapping.usmap";

        // 依赖检查：纹理替换与 Pak 转换需要 UAssetCLI 与编码器工具。
        // 这两样靠构建目标复制（不在项目引用里），漏掉了应用仍能启动、
        // 但相关功能全废 —— 所以冒烟测试必须把它们报出来，而不是只测读写 Pak。
        int depsResult = VerifyExternalTools();

        try
        {
            using var session = new PakArchiveSession();
            PakOpenResult open = await session.OpenAsync(new PakOpenOptions([pakPath], null, usmapPath));
            Console.WriteLine($"[smoke] Open OK: {open.FileCount:N0} files, {open.MountedArchiveCount} archives");
            if (open.FileCount == 0)
            {
                Console.Error.WriteLine("[smoke] FAIL: no files mounted");
                return 1;
            }

            IReadOnlyList<ArchiveEntryDto> textures = await session.SearchAsync("T_", 20);
            Console.WriteLine($"[smoke] Search \"T_\": {textures.Count} hits");
            ArchiveEntryDto? target = textures.FirstOrDefault(e => !e.IsDirectory);
            if (target is null)
            {
                Console.Error.WriteLine("[smoke] FAIL: no texture entry found");
                return 1;
            }

            AssetPreviewDto preview = await session.ReadPreviewAsync(target.FullPath);
            Console.WriteLine($"[smoke] Preview \"{target.FullPath}\": kind={preview.Kind}, title={preview.Title}, data={preview.Data?.Length ?? 0} bytes");
            if (preview.Data is not { Length: > 0 })
            {
                Console.Error.WriteLine("[smoke] FAIL: preview produced no image data");
                return 1;
            }

            Console.WriteLine("[smoke] PASS");
            return depsResult;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[smoke] FAIL: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    /// <summary>
    /// 检查纹理替换 / Pak 转换所需的外部工具是否随构建一起就位。
    /// 返回 0 表示齐全，非 0 表示缺失（冒烟测试据此报失败）。
    /// </summary>
    private static int VerifyExternalTools()
    {
        string baseDir = AppContext.BaseDirectory;
        (string Label, string Path)[] required =
        [
            ("UAssetCLI", Path.Combine(baseDir, "UAssetCLI", "UAssetCLI.exe")),
            ("texconv", Path.Combine(baseDir, "tools", "texconv.exe")),
            ("astcenc", Path.Combine(baseDir, "tools", "astcenc-avx2.exe")),
        ];

        int missing = 0;
        foreach ((string label, string path) in required)
        {
            if (File.Exists(path))
            {
                long kb = new FileInfo(path).Length / 1024;
                Console.WriteLine($"[smoke] 依赖 {label}: OK ({kb:N0} KB)");
            }
            else
            {
                missing++;
                Console.Error.WriteLine($"[smoke] 依赖 {label}: 缺失 → {path}");
            }
        }

        if (missing > 0)
        {
            Console.Error.WriteLine(
                "[smoke] 纹理替换与 Pak 转换将不可用。请先执行：dotnet build UAssetCLI -c Release");
        }

        return missing == 0 ? 0 : 1;
    }
}
