using System.Runtime.InteropServices;
using UAssetTexture.Core;

namespace Prism.Desktop.Services;

/// <summary>
/// 设置页「运行状态」里的一行。
/// </summary>
public sealed record DependencyCheck(
    string Name,
    string Description,
    bool Available,
    string Status)
{
    public bool IsMissing => !Available;
}

/// <summary>
/// 依赖探测：桌面端查外部 exe/dll，安卓端查随 APK 打包的原生库。
///
/// 之前安卓版也走桌面端那套 <c>UAssetCliRunner</c> 探测（找 UAssetCLI.exe、
/// tools/astcenc.exe…），手机上当然一个都没有，于是设置页整片显示"缺失" ——
/// 但纹理由 libprism_codecs.so 在进程内编码，Oodle 在 liboodle-data-shared.so 里，
/// 功能其实是好的。所以这里按平台分开探测，安卓只看它真的需要的 .so。
/// </summary>
public static class DependencyReport
{
    public static bool IsMobile => OperatingSystem.IsAndroid();

    public static string Headline => IsMobile
        ? "手机版把编码与压缩编进了随 APK 的 .so，不依赖外部 exe。"
        : "替换与打包用到的外部组件；缺失时对应格式会自动降级或不提供。";

    public static IReadOnlyList<DependencyCheck> Probe()
    {
        try
        {
            return IsMobile ? ProbeAndroid() : ProbeDesktop();
        }
        catch (Exception ex)
        {
            AppLog.Exception(ex, "依赖探测");
            return [];
        }
    }

    private static List<DependencyCheck> ProbeAndroid()
    {
        var codecs = ProbeNativeExports("prism_codecs", "prism_encode_texture", "prism_encode_binka", "prism_decode_binka");
        var oodle = OodleNative.IsAvailable;
        var cpp = ProbeNative("c++_shared");
        var repak = ProbeNative("repak_bind");

        return
        [
            new DependencyCheck(
                "纹理编码（prism_codecs）",
                "ASTC / BC 贴图在应用内重新编码，换图不依赖外部工具。",
                codecs.Available,
                codecs.Detail),

            new DependencyCheck(
                "音频编码（prism_codecs）",
                "BINKA 音频的编解码；没有它时只能用未压缩的 WAV 替换。",
                codecs.Available,
                codecs.Detail),

            new DependencyCheck(
                "Oodle 压缩（liboodle-data-shared）",
                "打包时可选 Oodle；缺库会自动按不压缩打包。",
                oodle,
                oodle ? "已就绪" : "缺失 · 打包自动降级为不压缩"),

            new DependencyCheck(
                "Pak 写入（repak_bind）",
                "合并/转换要写出 pak 文件。",
                repak.Available,
                repak.Detail),

            new DependencyCheck(
                "C++ 运行时（libc++_shared）",
                "上面几个 .so 的运行库。",
                cpp.Available,
                cpp.Detail),
        ];
    }

    private static List<DependencyCheck> ProbeDesktop()
    {
        UAssetCliRunner? runner = null;
        try
        {
            runner = UAssetCliRunner.TryCreate();
        }
        catch (Exception ex)
        {
            AppLog.Exception(ex, "依赖探测");
        }

        var oodle = OodleNative.IsAvailable;
        var binka = File.Exists(Path.Combine(AppContext.BaseDirectory, "prism_binka.dll"));
        var ffmpeg = Ffmpeg.Find() is not null;

        return
        [
            new DependencyCheck(
                "UAssetCLI",
                "解包/回写 uasset；贴图与音频替换靠它。",
                runner is not null,
                runner is not null ? "已就绪" : "缺失 · 替换功能不可用"),

            new DependencyCheck(
                "astcenc",
                "ASTC 贴图编码（手机包常用格式）。",
                runner?.HasAstcenc == true,
                runner?.HasAstcenc == true ? "已就绪" : "缺失 · ASTC 贴图不可用"),

            new DependencyCheck(
                "texconv",
                "DXT / BC 贴图编码（PC 包常用格式）。",
                runner?.HasTexconv == true,
                runner?.HasTexconv == true ? "已就绪" : "缺失 · DXT/BC 贴图不可用"),

            new DependencyCheck(
                "Oodle 压缩库",
                "打包时可选 Oodle；缺库会自动按不压缩打包。",
                oodle,
                oodle ? "已就绪" : "缺失 · 打包自动降级为不压缩"),

            new DependencyCheck(
                "BINKA 音频（prism_binka.dll）",
                "BINKA 音频编解码。",
                binka,
                binka ? "已就绪" : "缺失 · BINKA 音频不可用"),

            new DependencyCheck(
                "ffmpeg（可选）",
                "视频预览取帧；没有它时只给系统播放器入口。",
                ffmpeg,
                ffmpeg ? "已就绪" : "缺失 · 视频只给系统播放器入口"),
        ];
    }

    private sealed record NativeCheck(bool Available, string Detail);

    /// <summary>
    /// 安卓的 .so 可能以 soname（prism_codecs）、带 lib 前缀（libprism_codecs.so）
    /// 两种名字被 dlopen 命中，两个都试一次。加载成功只加了一次引用计数，探测完释放。
    /// </summary>
    private static bool TryLoadAndroidLibrary(string name, out nint handle)
    {
        foreach (var candidate in new[] { $"lib{name}.so", name, $"{name}.so" })
        {
            if (NativeLibrary.TryLoad(candidate, out handle))
                return true;
        }

        handle = 0;
        return false;
    }

    private static NativeCheck ProbeNative(string name)
    {
        try
        {
            if (TryLoadAndroidLibrary(name, out var handle))
            {
                NativeLibrary.Free(handle);
                return new NativeCheck(true, "已就绪");
            }
        }
        catch
        {
            // 落到下面的"缺失"。
        }

        return new NativeCheck(false, $"缺失 · lib{name}.so");
    }

    private static NativeCheck ProbeNativeExports(string name, params string[] exports)
    {
        nint handle = 0;
        try
        {
            if (!TryLoadAndroidLibrary(name, out handle))
                return new NativeCheck(false, $"缺失 · lib{name}.so");

            var missing = exports.Where(export => !NativeLibrary.TryGetExport(handle, export, out _)).ToArray();
            return missing.Length == 0
                ? new NativeCheck(true, "已就绪")
                : new NativeCheck(false, $"已加载，但缺少导出：{string.Join(", ", missing)}");
        }
        catch (Exception ex)
        {
            return new NativeCheck(false, $"缺失 · {ex.GetType().Name}");
        }
        finally
        {
            if (handle != 0)
                NativeLibrary.Free(handle);
        }
    }
}
