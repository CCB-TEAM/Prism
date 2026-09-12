using PakTool.Core;
using UAssetTexture.Core;

namespace Prism.FeatureTests;

/// <summary>
/// Pak 打包的非 ASCII 路径回归测试。
///
/// 背景：repak_bind 是 Rust 库，用 <c>std::str::from_utf8().unwrap()</c> 解析路径。
/// P/Invoke 的 string 默认按<b>系统 ANSI 代码页</b>封送（中文 Windows 是 GBK/936），
/// 于是含重音字符的 Pak 路径会：
///   - 映射成单字节的（Ä U+00C4）恰好是合法 UTF-8 续字节 → <b>静默损坏</b>成 '?'
///   - 不能映射的（É U+00C9）产生非法 UTF-8 → Rust unwrap panic →
///     该库 catch_unwind 拦不住 → <b>abort 整个 .NET 进程</b>
///
/// 修复：<c>UAssetAPI/Pak/Interop.cs</c> 里给 string 参数加
/// <c>[MarshalAs(UnmanagedType.LPUTF8Str)]</c>。
///
/// 这组用例用真实游戏里出现过的路径形态，确保修复不被回退。
/// </summary>
internal static class PakPathEncodingTests
{
    /// <summary>真实游戏（KARDS）里出现过的、会让打包出问题的路径形态。</summary>
    private static readonly (string Label, string PakPath)[] Cases =
    [
        ("纯 ASCII", "kards/Content/Assets/Textures/Images/Britain/t_foo.uasset"),
        ("重音 É (U+00C9)", "kards/Content/Assets/Textures/HistoricalPhotos/48e_RÉGIMENT_D_INFANTERIE.uasset"),
        ("重音 Ä (U+00C4)", "kards/Content/Assets/Textures/HistoricalPhotos/56__JÄGER_REGIMENT.uasset"),
        ("弯引号 ’ (U+2019)", "kards/Content/Assets/Textures/HistoricalPhotos/73e_RÉGIMENT_D’INFANTERIE.uasset"),
        ("重音 ä (U+00E4)", "kards/Content/Assets/Textures/Portraits/T_Portrait_Standard_Blitzmädel.uasset"),
        ("重音 Ö (U+00D6)", "kards/Content/Assets/Textures/HistoricalPhotos/BF_110_C_ZERSTÖRER.uasset"),
        ("重音 Ü (U+00DC)", "kards/Content/Assets/Textures/HistoricalPhotos/STUG_III_G_SCHÜRZEN.uasset"),
        ("含空格", "kards/Content/Assets/Textures/Images/Britain/my file.uasset"),
        ("中文目录名", "kards/Content/Assets/Textures/测试/贴图.uasset"),
    ];

    /// <summary>打包每个路径的资产，再读回比对——名字必须逐字一致。</summary>
    public static void Run(string workRoot, Action<bool, string> check)
    {
        Directory.CreateDirectory(workRoot);

        foreach ((string label, string pakPath) in Cases)
        {
            string disk = Path.Combine(workRoot, "payload.bin");
            File.WriteAllBytes(disk, [0x01, 0x02, 0x03]);

            string outPak = Path.Combine(workRoot, "enc-test.pak");
            try
            {
                ModifiedPakPackService.Pack(new ModifiedPakRequest(
                    [new ModifiedPakFile(disk, pakPath)],
                    outPak,
                    UseCompression: false));
            }
            catch (Exception ex)
            {
                check(false, $"打包含{label}路径的 Pak 未抛异常（{ex.GetType().Name}: {ex.Message}）");
                continue;
            }

            // 读回：路径必须逐字一致，不能出现 '?' 之类的替换字符
            try
            {
                using var session = new PakArchiveSession();
                PakOpenResult open = session.OpenAsync(new PakOpenOptions([outPak])).GetAwaiter().GetResult();
                IReadOnlySet<string> paths = session.ListRawFilePathsAsync().GetAwaiter().GetResult();
                string? readBack = paths.FirstOrDefault(p => !p.EndsWith('/'));

                bool exact = readBack is not null
                             && string.Equals(readBack.TrimStart('/'), pakPath, StringComparison.Ordinal);
                check(exact,
                    exact
                        ? $"路径含{label}时逐字往返（{pakPath}）"
                        : $"路径含{label}时逐字往返（期望 {pakPath}，实际 {readBack}）");
            }
            catch (Exception ex)
            {
                check(false, $"读回含{label}路径的 Pak（{ex.GetType().Name}: {ex.Message}）");
            }
        }

        // 组合场景：多个非 ASCII 路径同在一个 Pak 里（真实 Pak 就是这样）
        {
            var files = new List<ModifiedPakFile>();
            int i = 0;
            foreach ((string label, string pakPath) in Cases)
            {
                string disk = Path.Combine(workRoot, $"multi{i}.bin");
                File.WriteAllBytes(disk, [(byte)i]);
                files.Add(new ModifiedPakFile(disk, pakPath));
                i++;
            }

            string outPak = Path.Combine(workRoot, "enc-multi.pak");
            try
            {
                ModifiedPakPackService.Pack(new ModifiedPakRequest(files, outPak, UseCompression: false));

                using var session = new PakArchiveSession();
                session.OpenAsync(new PakOpenOptions([outPak])).GetAwaiter().GetResult();
                IReadOnlySet<string> paths = session.ListRawFilePathsAsync().GetAwaiter().GetResult();

                var set = paths
                    .Where(p => !p.EndsWith('/'))
                    .Select(p => p.TrimStart('/'))
                    .ToHashSet(StringComparer.Ordinal);

                int missing = Cases.Count(c => !set.Contains(c.PakPath));
                check(missing == 0,
                    missing == 0
                        ? $"混合非 ASCII 路径的 Pak 全部逐字往返（{Cases.Length} 条）"
                        : $"混合非 ASCII 路径的 Pak 全部逐字往返（缺失 {missing} 条）");
            }
            catch (Exception ex)
            {
                check(false, $"混合非 ASCII 路径打包（{ex.GetType().Name}: {ex.Message}）");
            }
        }
    }
}
