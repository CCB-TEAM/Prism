using System;
using System.IO;
using PakTool.Core;
using UAssetAudio.Core;

namespace Prism.Desktop;

/// <summary>
/// 命令行冒烟测试：不启动 UI，直接跑通「打开 Pak → 搜索 → 纹理预览 → 音频替换」核心链路，
/// 用于验证依赖链（SkiaSharp 版本兼容性、外部工具是否随构建就位）在真实运行环境下是否正常。
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

            // 音频替换自检：能找到 BINKA 流式 SoundWave 就实打实地编一遍、写回一遍。
            // 缺 binka_encode.exe 或映射文件时只提示，不算失败（仓库本就不带这个二进制）。
            int audioResult = await VerifyAudioReplacementAsync(session, usmapPath);

            Console.WriteLine(audioResult == 0 ? "[smoke] PASS" : "[smoke] PASS（音频替换未验证）");
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
    /// 音频替换全链路自检：解三件套 → 解析 → 合成 1 秒素材 → 编 BINKA → 写回 → 再解析。
    /// 返回 0 表示跑通或被合理跳过；非 0 只用于打印，不影响整体退出码。
    /// </summary>
    private static async Task<int> VerifyAudioReplacementAsync(PakArchiveSession session, string? usmapPath)
    {
        string toolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
        if (!BinkaEncoder.IsAvailable(toolsDir))
        {
            Console.WriteLine($"[smoke] 音频替换: 跳过（内置编码器和 {BinkaEncoder.ToolName} 都不可用）");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(usmapPath) || !File.Exists(usmapPath))
        {
            Console.WriteLine("[smoke] 音频替换: 跳过（没有映射文件，unversioned 资产解析不了）");
            return 1;
        }

        string? audioAsset = await FindBinkaSoundWaveAsync(session);
        if (audioAsset is null)
        {
            Console.WriteLine("[smoke] 音频替换: 跳过（这个 pak 里没找到可替换的 BINKA 流式音频）");
            return 1;
        }

        string work = Path.Combine(Path.GetTempPath(), "prism-smoke-audio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            IReadOnlyDictionary<string, byte[]> parts = await session.ReadRelatedRawFilesAsync(audioAsset);
            string stem = Path.Combine(work, Path.GetFileNameWithoutExtension(audioAsset));
            foreach ((string pakPath, byte[] data) in parts)
            {
                File.WriteAllBytes(Path.Combine(work, Path.GetFileName(pakPath)), data);
            }

            SoundWaveInfo before = SoundWavePatcher.Inspect(stem + ".uasset", usmapPath);
            Console.WriteLine($"[smoke] 音频 \"{audioAsset}\": {before.Format} {before.DescribeChannels} {before.DescribeSeconds}，{before.ChunkCount} 块");

            // 1 秒 440Hz 正弦，走真实的 WAV 解码路径（与玩家选文件一致）
            var pcm = new byte[48000 * 2];
            for (var i = 0; i < 48000; i++)
            {
                var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 48000.0) * 12000);
                pcm[i * 2] = (byte)sample;
                pcm[i * 2 + 1] = (byte)(sample >> 8);
            }

            string sourceWav = Path.Combine(work, "tone.wav");
            File.WriteAllBytes(sourceWav, WavCodec.Write16(pcm, 48000, 1));

            PcmAudio decoded = AudioSourceDecoder.Decode(sourceWav, Path.Combine(AppContext.BaseDirectory, "tools"));
            byte[] binka = BinkaEncoder.Encode(decoded, 4, Path.Combine(AppContext.BaseDirectory, "tools"));
            var encoded = BinkaEncoder.ReadInfo(binka);
            Console.WriteLine($"[smoke] 编码完成: {binka.Length:N0} 字节，{encoded.Channels} 声道 {encoded.SampleRate} Hz {encoded.Seconds:0.##} 秒");

            ulong originalUexpLength = (ulong)new FileInfo(stem + ".uexp").Length;
            SoundWaveReplacement replacement = SoundWavePatcher.Replace(stem + ".uasset", binka, usmapPath);
            File.WriteAllBytes(stem + ".uasset", replacement.Uasset);
            File.WriteAllBytes(stem + ".uexp", replacement.Uexp);
            File.WriteAllBytes(stem + ".ubulk", replacement.Ubulk);

            SoundWaveInfo after = SoundWavePatcher.Inspect(stem + ".uasset", usmapPath);
            bool uexpStable = (ulong)replacement.Uexp.Length == originalUexpLength;
            bool durationOk = Math.Abs(after.Seconds - encoded.Seconds) < 0.05;
            Console.WriteLine($"[smoke] 写回完成: uexp {originalUexpLength} → {replacement.Uexp.Length}（稳定={uexpStable}），" +
                              $"时长 {after.Seconds:0.##} 秒（匹配={durationOk}），ubulk {replacement.Ubulk.Length:N0} 字节");

            if (!uexpStable || !durationOk)
            {
                Console.Error.WriteLine("[smoke] FAIL: 音频写回结果不符合预期");
                return 1;
            }

            Console.WriteLine("[smoke] 音频替换: OK");
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch
            {
                // 临时目录残留不影响结论
            }
        }
    }

    /// <summary>在 pak 里找第一个 BINKA 流式 SoundWave（最多扫若干候选，避免为了冒烟测试遍历几万个文件）。</summary>
    private static async Task<string?> FindBinkaSoundWaveAsync(PakArchiveSession session)
    {
        IReadOnlySet<string> paths = await session.ListRawFilePathsAsync();
        IEnumerable<string> candidates = paths
            .Where(path => path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .Where(path => path.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
                           path.Contains("Sound", StringComparison.OrdinalIgnoreCase))
            .Take(400);

        foreach (string candidate in candidates)
        {
            try
            {
                AssetPreviewDto preview = await session.ReadPreviewAsync(candidate);
                if (preview.Kind == "audio")
                {
                    return candidate;
                }
            }
            catch
            {
                // 单个资产解析失败（缺映射、格式特殊）就跳过
            }
        }

        return null;
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

        // 音频编码器：优先用随包的原生库（prism_binka.dll / libprism_codecs.so），
        // 没有时才退回外部 binka_encode.exe
        string binka = Path.Combine(baseDir, "tools", BinkaEncoder.ToolName);
        bool nativeEncoder = BinkaEncoder.IsAvailable(Path.Combine(baseDir, "tools"));
        if (nativeEncoder)
        {
            Console.WriteLine("[smoke] 依赖 binka 编码: OK（内置）");
        }
        else
        {
            Console.WriteLine(File.Exists(binka)
                ? $"[smoke] 依赖 binka_encode: OK ({new FileInfo(binka).Length / 1024:N0} KB)"
                : $"[smoke] 依赖 binka_encode: 缺失 → {binka}（音频替换不可用）");
        }

        if (missing > 0)
        {
            Console.Error.WriteLine(
                "[smoke] 纹理替换与 Pak 转换将不可用。请先执行：dotnet build UAssetCLI -c Release");
        }

        return missing == 0 ? 0 : 1;
    }
}
