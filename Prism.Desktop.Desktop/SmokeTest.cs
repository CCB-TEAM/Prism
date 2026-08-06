using PakTool.Core;

namespace Prism.Desktop;

/// <summary>
/// 命令行冒烟测试：不启动 UI，直接跑通「打开 Pak → 搜索 → 纹理预览」核心链路，
/// 用于验证依赖链（尤其 SkiaSharp 版本兼容性）在真实运行环境下是否正常。
/// 用法: Prism.Desktop --smoke [pakPath] [usmapPath]
/// </summary>
public static class SmokeTest
{
    public static async Task<int> RunAsync(string[] args)
    {
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
