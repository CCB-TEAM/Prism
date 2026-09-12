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
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[smoke] FAIL: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }
}
