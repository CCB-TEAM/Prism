using System.Text.RegularExpressions;
using PakTool.Core;
using UAssetTexture.Core;
using Avalonia;
using Avalonia.Controls;
using UAssetAPI.UnrealTypes;

// 截图模式必须在任何 Avalonia 初始化之前分流：常规测试用 UseHeadlessDrawing = true
// （不绘制），截图要挂 Skia 真正光栅化，两者不能在同一进程共存。
if (args.Length > 0 && args[0] == "--capture")
{
    return Prism.FeatureTests.UiCapture.Run(
        args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "ui-capture"));
}

// 新功能集成测试：映射格式识别 / 搜索路径解析 / Pak 转换 / 合并优先级。
// 直接引用生产程序集，验证真实行为而非复制逻辑。

int failures = 0;
int passed = 0;

void Check(bool condition, string label)
{
    if (condition)
    {
        passed++;
        Console.WriteLine("  PASS  " + label);
    }
    else
    {
        failures++;
        Console.WriteLine("  FAIL  " + label);
    }
}

void Section(string name)
{
    Console.WriteLine();
    Console.WriteLine("===== " + name + " =====");
}

string root = Path.Combine(Path.GetTempPath(), "prism-feature-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine("工作目录: " + root);

try
{
    // ══════════════════════════════════════════════════════════════
    Section("F6 · 映射格式识别（usmap / jmap）");

    string usmapPath = Path.Combine(root, "Mapping.usmap");
    string jmapBadPath = Path.Combine(root, "Mapping.jmap");
    string jmapGzPath = Path.Combine(root, "Mapping.jmap.gz");
    string datPath = Path.Combine(root, "Mapping.dat");

    // 合法的最小 usmap：CUE4Parse 的 UsmapParser 只认魔数 0x30C4。
    // 布局（见 UsmapParser 构造函数）：
    //   ushort magic, byte version, [仅 version>=1 才有 bool bHasVersioning],
    //   byte compressionMethod, uint compSize, uint decompSize, <decompSize 字节负载>
    // 负载本身又是：int 名称数, int 枚举数, int 结构数。
    using (var ms = new MemoryStream())
    using (var w = new BinaryWriter(ms))
    {
        w.Write((ushort)0x30C4);   // FileMagic
        w.Write((byte)0);          // EUsmapVersion.Initial（<1，故无 bHasVersioning 字段）
        w.Write((byte)0);          // EUsmapCompressionMethod.None
        w.Write((uint)12);         // compSize == decompSize（无压缩时必须相等）
        w.Write((uint)12);         // decompSize
        w.Write(0);                // 名称表数量
        w.Write(0);                // 枚举数量
        w.Write(0);                // 结构数量
        w.Flush();
        File.WriteAllBytes(usmapPath, ms.ToArray());
    }

    File.WriteAllBytes(jmapBadPath, [0x01, 0x02, 0x03, 0x04]);
    File.WriteAllBytes(jmapGzPath, [0x01, 0x02, 0x03, 0x04]);
    File.WriteAllBytes(datPath, [0x01, 0x02, 0x03, 0x04]);

    Check(MappingsLoader.IsJmap(jmapBadPath), ".jmap 识别为 jmap");
    Check(MappingsLoader.IsJmap(jmapGzPath), ".jmap.gz 识别为 jmap");
    Check(!MappingsLoader.IsJmap(usmapPath), ".usmap 不是 jmap");
    Check(!MappingsLoader.IsJmap(datPath), ".dat 不是 jmap");
    Check(MappingsLoader.IsJmap(jmapBadPath.ToUpperInvariant()), "扩展名大小写不敏感");
    Check(MappingsLoader.DescribeFormat(jmapBadPath) == "jmap", "DescribeFormat(.jmap) = jmap");
    Check(MappingsLoader.DescribeFormat(usmapPath) == "usmap", "DescribeFormat(.usmap) = usmap");
    Check(MappingsLoader.Patterns.Length == 4, "文件选择器过滤 4 种扩展名");
    Check(MappingsLoader.Patterns.Contains("*.jmap"), "过滤包含 *.jmap");

    // 合法的最小 jmap：验证确实走进 JmapTypeMappingsProvider。
    string validJmapPath = Path.Combine(root, "Valid.jmap");
    File.WriteAllText(validJmapPath, """{"metadata":null,"image_base_address":"0x0","objects":{},"vtables":{}}""");
    var jmapProvider = MappingsLoader.Create(validJmapPath);
    Check(jmapProvider.GetType().Name == "JmapTypeMappingsProvider",
        $"jmap 使用 JmapTypeMappingsProvider（实际 {jmapProvider.GetType().Name}）");
    Check(jmapProvider.MappingsForGame is not null, "合法 jmap 解析出非空映射对象");

    var usmapProvider = MappingsLoader.Create(usmapPath);
    Check(usmapProvider.GetType().Name == "FileUsmapTypeMappingsProvider",
        $"usmap 使用 FileUsmapTypeMappingsProvider（实际 {usmapProvider.GetType().Name}）");

    // 非法 jmap 内容要给出可读错误，而不是裸的 JsonReaderException。
    string jmapError = string.Empty;
    try
    {
        _ = MappingsLoader.Create(jmapBadPath);
    }
    catch (InvalidOperationException ex)
    {
        jmapError = ex.Message;
    }
    catch (Exception ex)
    {
        jmapError = $"未包装的 {ex.GetType().Name}: {ex.Message}";
    }

    Check(jmapError.Contains("Mapping.jmap") && jmapError.Contains("解析失败"),
        "非法 jmap 抛出可读的 InvalidOperationException");

    bool missingThrew = false;
    try
    {
        _ = MappingsLoader.Create(Path.Combine(root, "nope.jmap"));
    }
    catch (FileNotFoundException ex)
    {
        missingThrew = ex.Message.Contains("找不到");
    }

    Check(missingThrew, "不存在的映射文件抛出可读的 FileNotFoundException");

    // ══════════════════════════════════════════════════════════════
    Section("F7/F1 · 构造测试用 Pak（真实目录结构）");

    // 真实纹理资产：仓库根目录的 t_cromwell（1251 B uasset + 3045 B uexp + 680 KB ubulk）。
    // 用真实资产而不是合成数据，是为了让"纹理布局解析 + mip 载荷搬运"走真正的解析代码。
    // 找不到就跳过相关断言，保证在干净 checkout 上其余用例仍可运行。
    string realAssetDir = LocateRepoRoot();
    string realUasset = Path.Combine(realAssetDir, "t_cromwell.uasset");
    string realUexp = Path.Combine(realAssetDir, "t_cromwell.uexp");
    string realUbulk = Path.Combine(realAssetDir, "t_cromwell.ubulk");
    bool hasRealTexture = File.Exists(realUasset) && File.Exists(realUexp) && File.Exists(realUbulk);
    Console.WriteLine($"        真实纹理资产可用: {hasRealTexture}");

    const string uiFolder = "Kards/Content/UI/";
    const string newFolder = "Kards/Content/New/";
    const string cfgFolder = "Kards/Content/Config/";

    // 主 Pak：真实纹理 + 一个占位纹理包 + 配置文件
    string mainPak = Path.Combine(root, "main.pak");
    List<ModifiedPakFile> mainEntries = [];
    if (hasRealTexture)
    {
        mainEntries.Add(new ModifiedPakFile(realUasset, uiFolder + "T_Cromwell_P.uasset"));
        mainEntries.Add(new ModifiedPakFile(realUexp, uiFolder + "T_Cromwell_P.uexp"));
        mainEntries.Add(new ModifiedPakFile(realUbulk, uiFolder + "T_Cromwell_P.ubulk"));
    }

    mainEntries.Add(FileOf(root, uiFolder + "T_Placeholder_P.uasset", "MAIN-PLACEHOLDER-UASSET"));
    mainEntries.Add(FileOf(root, uiFolder + "T_Placeholder_P.uexp", "MAIN-PLACEHOLDER-UEXP"));
    mainEntries.Add(FileOf(root, cfgFolder + "Game.ini", "MAIN-CONFIG"));
    mainEntries.Add(FileOf(root, cfgFolder + "MainOnly.txt", "ONLY-IN-MAIN"));
    ModifiedPakPackService.Pack(new ModifiedPakRequest(mainEntries, mainPak));

    // 源 Pak：同一真实纹理（应被重编码替换）+ 主 Pak 没有的纹理 + 独有普通文件
    string sourcePak = Path.Combine(root, "source.pak");
    List<ModifiedPakFile> sourceEntries = [];
    if (hasRealTexture)
    {
        sourceEntries.Add(new ModifiedPakFile(realUasset, uiFolder + "T_Cromwell_P.uasset"));
        sourceEntries.Add(new ModifiedPakFile(realUexp, uiFolder + "T_Cromwell_P.uexp"));
        sourceEntries.Add(new ModifiedPakFile(realUbulk, uiFolder + "T_Cromwell_P.ubulk"));
    }

    sourceEntries.Add(FileOf(root, newFolder + "T_Extra_P.uasset", "SOURCE-EXTRA-UASSET"));
    sourceEntries.Add(FileOf(root, newFolder + "T_Extra_P.uexp", "SOURCE-EXTRA-UEXP"));
    sourceEntries.Add(FileOf(root, cfgFolder + "Game.ini", "SOURCE-CONFIG"));
    sourceEntries.Add(FileOf(root, cfgFolder + "SourceOnly.txt", "ONLY-IN-SOURCE"));
    ModifiedPakPackService.Pack(new ModifiedPakRequest(sourceEntries, sourcePak));

    int mainCount = mainEntries.Count;
    Check(File.Exists(mainPak) && new FileInfo(mainPak).Length > 0, $"主 Pak 已生成（{mainEntries.Count} 个文件，{new FileInfo(mainPak).Length} 字节）");
    Check(File.Exists(sourcePak) && new FileInfo(sourcePak).Length > 0, $"源 Pak 已生成（{sourceEntries.Count} 个文件）");

    using (var session = new PakArchiveSession())
    {
        PakOpenResult open = await session.OpenAsync(new PakOpenOptions([mainPak]));
        Check(open.FileCount == mainCount, $"主 Pak 读回 {open.FileCount} 个文件（期望 {mainCount}）");

        IReadOnlySet<string> paths = await session.ListRawFilePathsAsync();
        // ListRawFilePathsAsync 返回的是提供程序内部路径（带前导 /），这与 CopyAllRawFilesAsync 一致。
        Check(paths.Any(p => p.TrimStart('/') == uiFolder + "T_Placeholder_P.uasset"),
            "路径以提供程序内部形态返回（可能带前导斜杠）");
    }

    // ══════════════════════════════════════════════════════════════
    Section("F5 · 搜索框路径解析（TryResolveBrowsePathAsync）");

    using (var session = new PakArchiveSession())
    {
        await session.OpenAsync(new PakOpenOptions([mainPak]));

        BrowsePathResolution? r = await session.TryResolveBrowsePathAsync("Kards/Content/UI");
        Check(r is { IsFolder: true }, "目录路径命中且标记为目录");
        Check(r?.Folder == uiFolder, $"目录路径规范化为带尾斜杠（{r?.Folder}）");

        r = await session.TryResolveBrowsePathAsync("\\Kards\\Content\\Config\\");
        Check(r is { IsFolder: true }, "反斜杠路径命中");
        Check(r?.Folder == cfgFolder, $"反斜杠已规范化（{r?.Folder}）");

        r = await session.TryResolveBrowsePathAsync(uiFolder + "T_Placeholder_P.uasset");
        Check(r is { IsFolder: false }, "文件路径命中且标记为文件");
        Check(r?.Folder == uiFolder, $"文件返回其所在目录（{r?.Folder}）");
        Check(r?.FileName == "T_Placeholder_P.uasset", $"文件返回文件名（{r?.FileName}）");

        r = await session.TryResolveBrowsePathAsync($"\"{uiFolder.TrimEnd('/')}\"");
        Check(r is { IsFolder: true }, "带引号的路径也能命中");

        r = await session.TryResolveBrowsePathAsync("/");
        Check(r is { IsFolder: true } && r.Folder.Length == 0, "单独的 / 解析为根目录");

        // 关键词不得被误判为路径，否则搜索功能会失效
        Check(await session.TryResolveBrowsePathAsync("T_Placeholder") is null,
            "关键词 \"T_Placeholder\" 不解析为路径（回退为搜索）");
        Check(await session.TryResolveBrowsePathAsync("Placeholder") is null, "关键词 \"Placeholder\" 不解析为路径");
        Check(await session.TryResolveBrowsePathAsync("Kards/Content/cromwell") is null,
            "Kards/Content/cromwell 回退为搜索（小写尾段视为关键词而非目录）");

        // 尾段是 PascalCase 而父目录存在 → 认为用户想去父目录（有意行为，避免跳错目录）
        BrowsePathResolution? parentFallback = await session.TryResolveBrowsePathAsync("Kards/NoSuchFolder");
        Check(parentFallback is { IsFolder: true } && parentFallback.Folder == "Kards/",
            $"Kards/NoSuchFolder 落到已存在的父目录 Kards/（实际 {parentFallback?.Folder}）");
        Check(await session.TryResolveBrowsePathAsync("   ") is null, "空白输入返回 null");
        Check(await session.TryResolveBrowsePathAsync(null) is null, "null 输入返回 null");

        r = await session.TryResolveBrowsePathAsync("kards/content/ui");
        Check(r is { IsFolder: true }, "路径大小写不敏感");

        IReadOnlyList<ArchiveEntryDto> hits = await session.SearchAsync("T_Placeholder", 50);
        Check(hits.Count > 0, $"关键词搜索仍可用（命中 {hits.Count} 项）");
    }

    // ══════════════════════════════════════════════════════════════
    Section("F1 · Pak 转换 —— 用真实纹理走完整链路");

    // 转换 = 解源像素 + 按主 Pak 格式重编码。桌面端需要注入编码器委托。
    // 测试里用一个受控的委托（内部走生产代码的替换服务），既能验证真实链路，
    // 又能把"是否走了重编码"变成可断言的开关。
    var conversion = new PakConversionService();
    int encodeCallCount = 0;
    conversion.EncodeIntoAssetAsync = async (templateAsset, pngPath, outputAsset, ct) =>
    {
        encodeCallCount++;
        try
        {
            await new TextureReplacementService().ReplaceAsync(
                templateAsset, pngPath, outputAsset,
                EngineVersion.VER_UE5_6, null,
                new TextureCodecOptions(AstcQuality: "fast"), ct);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    };

    Check(conversion.CanReEncodeCrossFormat, "注入编码器后具备跨格式重编码能力");

    // 不注入时不应崩溃，而是把跨格式项标为跳过并说明原因。
    Check(!new PakConversionService().CanReEncodeCrossFormat || OperatingSystem.IsAndroid(),
        "未注入编码器时（桌面）报告不具备重编码能力");


    if (!hasRealTexture)
    {
        Console.WriteLine("        !! 找不到 t_cromwell 真实资产，跳过真实转换断言");
    }
    else
    {
        // ── 默认模式 ReplacedOnly：只有 T_Cromwell_P 两边都有 → 替换 1 个，
        //    输出 Pak 里只装改动过的那 3 个文件（uasset/uexp/ubulk），
        //    而不是整个主 Pak —— 这是模组 Pak 的正确形态。
        string outReplace = Path.Combine(root, "out-replace.pak");
        PakConversionResult res = await conversion.ConvertAsync(new PakConversionOptions(
            sourcePak, mainPak, outReplace, Mode: PakConversionMode.ReplacedOnly, UseOodleCompression: false));

        Console.WriteLine($"        统计: 候选={res.Counts.TextureCandidates} 替换={res.Counts.Replaced} " +
                          $"缺失={res.Counts.MissingInTarget} 跳过={res.Counts.Skipped} 失败={res.Counts.Failed} " +
                          $"输出={res.Counts.TotalFiles}");
        foreach (PakConversionItemResult item in res.Items)
        {
            Console.WriteLine($"          {item.Status,-16} {item.PakPath}{(item.Message is null ? "" : "  // " + item.Message)}");
        }

        Check(res.Counts.Failed == 0, $"无失败项（失败 {res.Counts.Failed}）");
        Check(res.Counts.Replaced == 1, $"替换 1 个纹理（实际 {res.Counts.Replaced}）");
        Check(res.Counts.MissingInTarget == 1, $"主 Pak 缺失项 1 个（实际 {res.Counts.MissingInTarget}）");
        Check(res.Counts.TotalFiles == 3,
            $"ReplacedOnly 输出只含 3 个改动文件（实际 {res.Counts.TotalFiles}，主 Pak 有 {mainCount} 个）");
        Check(res.Items.Any(i => i.Status == PakConversionItemStatus.Replaced
                                 && i.Method == PakConversionMethod.RawCopy),
            "结果标记为 RawCopy 方式（无损搬运）");

        // 独立验证：FullRepack 模式才输出完整整包
        string outFull = Path.Combine(root, "out-full.pak");
        PakConversionResult resFull = await conversion.ConvertAsync(new PakConversionOptions(
            sourcePak, mainPak, outFull, Mode: PakConversionMode.FullRepack, UseOodleCompression: false));
        Check(resFull.Counts.TotalFiles == mainCount,
            $"FullRepack 输出完整整包 {mainCount} 个文件（实际 {resFull.Counts.TotalFiles}）");

        // ── 读回校验 ──
        using (var session = new PakArchiveSession())
        {
            await session.OpenAsync(new PakOpenOptions([outReplace]));

            byte[] outUasset = await session.ReadFileAsync(uiFolder + "T_Cromwell_P.uasset");
            byte[] outUexp = await session.ReadFileAsync(uiFolder + "T_Cromwell_P.uexp");
            byte[] outUbulk = await session.ReadFileAsync(uiFolder + "T_Cromwell_P.ubulk");
            byte[] origUbulk = await File.ReadAllBytesAsync(realUbulk);

            Check(outUasset.Length > 0, $"替换后的 uasset 非空（{outUasset.Length} 字节）");
            Check(outUexp.Length > 0, $"替换后的 uexp 非空（{outUexp.Length} 字节）");
            Check(outUbulk.Length == origUbulk.Length,
                $"替换后的 ubulk 尺寸不变（{outUbulk.Length} vs 原始 {origUbulk.Length}）");
            // 源与目标同为 PF_DXT1 → 快速路径，压缩载荷必须逐字节一致（无损搬运）。
            Check(outUbulk.SequenceEqual(origUbulk), "快速路径：格式一致时 mip 数据逐字节无损搬运");
            Check(File.ReadAllBytes(realUasset).AsSpan().SequenceEqual(outUasset),
                "快速路径：uasset 头与源一致（只替换了 uexp/ubulk 载荷）");
            Check(res.Items.Any(i => i.Status == PakConversionItemStatus.Replaced
                                     && i.Method == PakConversionMethod.RawCopy),
                "结果标记为 RawCopy 方式");
            Check(outUbulk.SequenceEqual(origUbulk), "替换后的 mip 载荷与源完全一致（无损）");

            // ReplacedOnly 模式下未改动的文件根本不在输出里 —— 所以不能去读它们，
            // 而是断言"不存在"。这正是模组 Pak 该有的形态。
            IReadOnlySet<string> outPaths = await session.ListRawFilePathsAsync();
            Check(!outPaths.Any(p => p.Contains("T_Extra_P")), "ReplacedOnly 不并入主 Pak 没有的纹理");
            Check(!outPaths.Any(p => p.Contains("SourceOnly")), "ReplacedOnly 不并入源 Pak 独有文件");
            Check(!outPaths.Any(p => p.Contains("T_Placeholder_P")),
                "ReplacedOnly 输出不含未改动的纹理（模组 Pak 只装改动项）");
            Check(!outPaths.Any(p => p.Contains("Game.ini")), "ReplacedOnly 输出不含未改动的普通文件");
        }

        // ── FullRepack：完整整包，未改动文件也应保持主 Pak 原值 ──
        using (var session = new PakArchiveSession())
        {
            await session.OpenAsync(new PakOpenOptions([outFull]));

            IReadOnlySet<string> fullPaths = await session.ListRawFilePathsAsync();
            Console.WriteLine($"        FullRepack 内容（{fullPaths.Count} 条）：");
            foreach (string p in fullPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"          {p}");
            }

            string placeholder = System.Text.Encoding.UTF8.GetString(
                await session.ReadFileAsync(uiFolder + "T_Placeholder_P.uasset"));
            Check(placeholder == "MAIN-PLACEHOLDER-UASSET", $"FullRepack 未改动的纹理保持主 Pak 原值（{placeholder}）");

            string config = System.Text.Encoding.UTF8.GetString(await session.ReadFileAsync(cfgFolder + "Game.ini"));
            Check(config == "MAIN-CONFIG", $"FullRepack 未改动的普通文件保持主 Pak 原值（{config}）");

            byte[] fullBulk = await session.ReadFileAsync(uiFolder + "T_Cromwell_P.ubulk");
            byte[] origBulk = await File.ReadAllBytesAsync(realUbulk);
            Check(fullBulk.SequenceEqual(origBulk), "FullRepack 里被替换的纹理同样是无损搬运");
        }

        // ── MergeAll：源 Pak 独有文件也应并入 ──
        string outMergeAll = Path.Combine(root, "out-mergeall.pak");
        PakConversionResult resMerge = await conversion.ConvertAsync(new PakConversionOptions(
            sourcePak, mainPak, outMergeAll, Mode: PakConversionMode.MergeAll, UseOodleCompression: false));

        Console.WriteLine($"        统计: 替换={resMerge.Counts.Replaced} 缺失={resMerge.Counts.MissingInTarget} " +
                          $"并入其他={resMerge.Counts.OtherFilesIncluded} 输出={resMerge.Counts.TotalFiles}");

        Check(resMerge.Counts.Replaced == 1, $"MergeAll 也替换 1 个纹理（实际 {resMerge.Counts.Replaced}）");
        Check(resMerge.Counts.OtherFilesIncluded == 3,
            $"MergeAll 并入 3 个主 Pak 没有的文件（T_Extra_P ×2 + SourceOnly.txt，实际 {resMerge.Counts.OtherFilesIncluded}）");

        using (var session = new PakArchiveSession())
        {
            await session.OpenAsync(new PakOpenOptions([outMergeAll]));
            IReadOnlySet<string> outPaths = await session.ListRawFilePathsAsync();

            Check(outPaths.Any(p => p.Contains("T_Extra_P")), "MergeAll 并入源 Pak 独有的纹理");
            Check(outPaths.Any(p => p.Contains("SourceOnly")), "MergeAll 并入源 Pak 独有的普通文件");

            string config = System.Text.Encoding.UTF8.GetString(await session.ReadFileAsync(cfgFolder + "Game.ini"));
            Check(config == "MAIN-CONFIG", $"MergeAll 也不覆盖主 Pak 已存在的普通文件（{config}）");

            byte[] button = await session.ReadFileAsync(uiFolder + "T_Cromwell_P.uasset");
            Check(button.Length > 0, "MergeAll 中纹理替换仍然生效");
        }

        // ── MergeSourceFiles：并入源 Pak 的文件，但不搬运主 Pak 的其他文件 ──
        // 这是"模组除了换贴图还改了蓝图/本地化"的用法：输出仍≈改动量，不必再单独合并。
        string outModFiles = Path.Combine(root, "out-modfiles.pak");
        PakConversionResult resMod = await conversion.ConvertAsync(new PakConversionOptions(
            sourcePak, mainPak, outModFiles, Mode: PakConversionMode.MergeSourceFiles, UseOodleCompression: false));

        Console.WriteLine($"        统计: 替换={resMod.Counts.Replaced} 缺失={resMod.Counts.MissingInTarget} " +
                          $"并入其他={resMod.Counts.OtherFilesIncluded} 输出={resMod.Counts.TotalFiles}");

        Check(resMod.Counts.Replaced == 1, $"MergeSourceFiles 也替换 1 个纹理（实际 {resMod.Counts.Replaced}）");
        // 源 Pak 里除"已成功转换的纹理"外全部并入：T_Extra_P ×2 + Game.ini + SourceOnly.txt。
        // 若已转换纹理没被排除，这里会变成 7（多出 T_Cromwell_P 的三件套）。
        Check(resMod.Counts.OtherFilesIncluded == 4,
            $"MergeSourceFiles 并入 4 个源 Pak 文件（T_Extra_P ×2 + Game.ini + SourceOnly.txt，实际 {resMod.Counts.OtherFilesIncluded}）");

        using (var session = new PakArchiveSession())
        {
            await session.OpenAsync(new PakOpenOptions([outModFiles]));
            IReadOnlySet<string> outPaths = await session.ListRawFilePathsAsync();

            Check(outPaths.Any(p => p.Contains("T_Extra_P")), "MergeSourceFiles 并入源 Pak 独有的纹理");
            Check(outPaths.Any(p => p.Contains("SourceOnly")), "MergeSourceFiles 并入源 Pak 独有的普通文件");

            // 模组 Pak 是叠加层，同路径以模组为准 —— 所以这里应是源 Pak 的版本。
            string config = System.Text.Encoding.UTF8.GetString(await session.ReadFileAsync(cfgFolder + "Game.ini"));
            Check(config == "SOURCE-CONFIG", $"MergeSourceFiles 用源 Pak 覆盖同路径普通文件（{config}）");

            // 不搬运主 Pak 的其他文件 —— 这正是它区别于 MergeAll（输出完整整包）的地方。
            Check(!outPaths.Any(p => p.Contains("T_Placeholder_P")), "MergeSourceFiles 不搬运主 Pak 未改动的纹理");
            Check(!outPaths.Any(p => p.Contains("MainOnly")), "MergeSourceFiles 不搬运主 Pak 独有的普通文件");
        }

        // ── 映射文件对两条路径的不同要求（如实记录） ──────────────────────
        // 快路径（格式/布局一致）只读原始字节，不反序列化 UObject → 不需要映射文件。
        // 重编码路径要解出源纹理像素 → 必须能反序列化 UTexture，未版本化资产就需要映射。
        encodeCallCount = 0;
        string outNoMap = Path.Combine(root, "out-no-map.pak");
        PakConversionResult resNoMap = await conversion.ConvertAsync(new PakConversionOptions(
            sourcePak, mainPak, outNoMap, MappingsPath: null,
            Mode: PakConversionMode.ReplacedOnly, UseOodleCompression: false));

        Console.WriteLine($"        无映射统计: 替换={resNoMap.Counts.Replaced} 跳过={resNoMap.Counts.Skipped} 失败={resNoMap.Counts.Failed}");
        Check(resNoMap.Counts.Replaced == 1 && resNoMap.Counts.Failed == 0,
            $"两侧格式一致时无需映射文件即可转换（快路径替换 {resNoMap.Counts.Replaced} 个）");
        Check(resNoMap.Items.Any(i => i.Method == PakConversionMethod.RawCopy),
            "确认走的是 RawCopy 快路径（不反序列化 UObject，故不需要映射）");

        // ── 重编码链路：图片 → 按目标格式编码 → 写回资产 → 打包 ──────────
        // 这是"解出像素后按主 Pak 格式重编码"的核心步骤。转换服务内部在 Android
        // 直接调它、在桌面通过注入的编码器委托调它，两条路都落到同一段生产代码。
        string reencodeDir = Path.Combine(root, "reencode");
        Directory.CreateDirectory(reencodeDir);
        string newPng = Path.Combine(reencodeDir, "new.png");
        using (var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(1024, 1024))
        {
            for (int y = 0; y < 1024; y += 64)
            for (int x = 0; x < 1024; x += 64)
            {
                var c = new SixLabors.ImageSharp.PixelFormats.Rgba32((byte)(x % 256), (byte)(y % 256), 200, 255);
                for (int dy = 0; dy < 64; dy++)
                for (int dx = 0; dx < 64; dx++)
                {
                    img[x + dx, y + dy] = c;
                }
            }

            using var fs = File.Create(newPng);
            img.Save(fs, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        }

        TextureAssetInfo mainInfo = TextureAssetParser.Load(
            outputOf(mainPak, uiFolder + "T_Cromwell_P.uasset"), EngineVersion.VER_UE5_6, null);

        string reencodedAsset = Path.Combine(reencodeDir, "T_Cromwell_P.uasset");
        var replacer = new TextureReplacementService();
        TextureReplacementResult reencoded = await replacer.ReplaceAsync(
            outputOf(mainPak, uiFolder + "T_Cromwell_P.uasset"),
            newPng,
            reencodedAsset,
            EngineVersion.VER_UE5_6, null,
            new TextureCodecOptions(), CancellationToken.None);

        TextureAssetInfo reencodedInfo = TextureAssetParser.Load(reencoded.AssetPath, EngineVersion.VER_UE5_6, null);
        Console.WriteLine($"        重编码: {mainInfo.Format.Name} → {reencodedInfo.Format.Name} " +
                          $"{reencodedInfo.Width}x{reencodedInfo.Height} mips={reencodedInfo.Mips.Count}");

        Check(reencodedInfo.Format.Name == mainInfo.Format.Name,
            $"输出格式沿用主 Pak 资产格式（{reencodedInfo.Format.Name}）");
        Check(reencodedInfo.Width == mainInfo.Width && reencodedInfo.Height == mainInfo.Height,
            "输出尺寸沿用主 Pak 资产尺寸");
        Check(reencodedInfo.Mips.Count == mainInfo.Mips.Count, "输出 mip 级数沿用主 Pak 资产");

        // 打包这批产物，确认端到端可用
        string outReencode = Path.Combine(root, "out-reencode.pak");
        var reencodeFiles = new List<ModifiedPakFile>();
        foreach (string ext in new[] { ".uasset", ".uexp", ".ubulk" })
        {
            string p = Path.ChangeExtension(reencoded.AssetPath, ext);
            if (File.Exists(p))
            {
                reencodeFiles.Add(new ModifiedPakFile(p, uiFolder + "T_Cromwell_P" + ext));
            }
        }

        ModifiedPakPackService.Pack(new ModifiedPakRequest(reencodeFiles, outReencode, UseCompression: false));
        using (var session = new PakArchiveSession())
        {
            PakOpenResult openRe = await session.OpenAsync(new PakOpenOptions([outReencode]));
            Check(openRe.FileCount == reencodeFiles.Count,
                $"重编码产物打包并读回成功（{openRe.FileCount} 个文件）");

            byte[] outBulk = await session.ReadFileAsync(uiFolder + "T_Cromwell_P.ubulk");
            byte[] origBulk = await File.ReadAllBytesAsync(realUbulk);
            Check(outBulk.Length == origBulk.Length, $"重编码后载荷长度与目标格式匹配（{outBulk.Length:N0}B）");
            Check(!outBulk.AsSpan().SequenceEqual(origBulk.AsSpan()), "重编码后载荷确实被改写");
        }

        Check(conversion.CanReEncodeCrossFormat, "已注入编码器的转换服务具备重编码能力");
        Console.WriteLine($"        编码器调用统计: {encodeCallCount} 次（本段未经过服务，服务侧调用在 Android 上为进程内）");
    }

    // ── 未选择映射文件也应能转换（RawCopy 只读原始字节，不反序列化 UObject）──
    if (hasRealTexture)
    {
        string outNoMapping = Path.Combine(root, "out-no-mapping.pak");
        PakConversionResult resNoMapping = await conversion.ConvertAsync(new PakConversionOptions(
            sourcePak, mainPak, outNoMapping, MappingsPath: null,
            Mode: PakConversionMode.ReplacedOnly, UseOodleCompression: false));

        Check(resNoMapping.Counts.Replaced == 1,
            $"未提供映射文件时转换仍成功（RawCopy 不反序列化 UObject，实际替换 {resNoMapping.Counts.Replaced}）");
    }

    // ══════════════════════════════════════════════════════════════
    Section("F7 · 合并覆盖优先级（越靠上优先级越高）");

    string pakA = Path.Combine(root, "prio-a.pak");
    string pakB = Path.Combine(root, "prio-b.pak");
    string pakC = Path.Combine(root, "prio-c.pak");

    ModifiedPakPackService.Pack(new ModifiedPakRequest(
    [
        FileOf(root, cfgFolder + "Shared.ini", "FROM-A"),
        FileOf(root, cfgFolder + "OnlyA.txt", "A-ONLY"),
    ], pakA));
    ModifiedPakPackService.Pack(new ModifiedPakRequest(
    [
        FileOf(root, cfgFolder + "Shared.ini", "FROM-B"),
        FileOf(root, cfgFolder + "OnlyB.txt", "B-ONLY"),
    ], pakB));
    ModifiedPakPackService.Pack(new ModifiedPakRequest(
        [FileOf(root, cfgFolder + "Shared.ini", "FROM-C")], pakC));

    // 复刻 BuildMergeCoreAsync 的语义：顺序遍历，先出现者胜出 —— 即"越靠上优先级越高"。
    // 用 CopyAllRawFilesAsync（与生产代码同一条路径）+ ModifiedPakPackService。
    async Task<Dictionary<string, string>> MergeInOrderAsync(params string[] paks)
    {
        string work = Path.Combine(root, "merge-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        Dictionary<string, ModifiedPakFile> merged = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < paks.Length; i++)
        {
            using var session = new PakArchiveSession();
            await session.OpenAsync(new PakOpenOptions([paks[i]]));
            IReadOnlyList<PakRawFileCopy> files = await session.CopyAllRawFilesAsync(Path.Combine(work, $"in{i}"));

            foreach (PakRawFileCopy file in files)
            {
                // 已被更靠上的 Pak 收录 —— 这一份被覆盖。
                if (merged.ContainsKey(file.PakPath))
                {
                    continue;
                }

                merged[file.PakPath] = new ModifiedPakFile(file.DiskPath, file.PakPath);
            }
        }

        string outPak = Path.Combine(work, "merged.pak");
        ModifiedPakPackService.Pack(new ModifiedPakRequest(
            merged.Values.OrderBy(v => v.PakPath, StringComparer.OrdinalIgnoreCase).ToArray(), outPak));

        Dictionary<string, string> content = new(StringComparer.OrdinalIgnoreCase);
        using var readSession = new PakArchiveSession();
        await readSession.OpenAsync(new PakOpenOptions([outPak]));
        IReadOnlySet<string> paths = await readSession.ListRawFilePathsAsync();
        foreach (string path in paths)
        {
            content[path] = System.Text.Encoding.UTF8.GetString(await readSession.ReadFileAsync(path));
        }

        return content;
    }

    // 路径形态：子目录下的文件不带前导斜杠（只有 pak 根目录文件才带）。
    string sharedKey = cfgFolder + "Shared.ini";
    string onlyAKey = cfgFolder + "OnlyA.txt";
    string onlyBKey = cfgFolder + "OnlyB.txt";

    foreach ((string[] order, string expected, string label) in new (string[], string, string)[]
             {
                 ([pakA, pakB, pakC], "FROM-A", "A,B,C"),
                 ([pakC, pakB, pakA], "FROM-C", "C,B,A"),
                 ([pakB, pakA, pakC], "FROM-B", "B,A,C"),
                 ([pakC, pakA, pakB], "FROM-C", "C,A,B"),
             })
    {
        Dictionary<string, string> merged = await MergeInOrderAsync(order);
        string actual = merged.TryGetValue(sharedKey, out string? v) ? v : "<缺失>";
        Check(actual == expected, $"顺序 {label} → {expected} 胜出（实际 {actual}）");
        Check(merged.ContainsKey(onlyAKey) && merged.ContainsKey(onlyBKey), $"顺序 {label} 非冲突文件全部保留");
    }

    // ══════════════════════════════════════════════════════════════
    Section("F3 · 从 Pak 内直接导出 locres JSON");

    var locres = new LocresPreviewDto("Optimized", 1, 2,
    [
        new LocresEntryDto(0, "/Game/Loc/UI", "Hello", "你好", NamespaceHash: 1, KeyHash: 2),
        new LocresEntryDto(1, "/Game/Loc/UI", "Bye", "再见", NamespaceHash: 1, KeyHash: 3),
    ]);
    byte[] locresBytes = LocresResourceCodec.Write(locres);
    string locresPak = Path.Combine(root, "locres.pak");
    ModifiedPakPackService.Pack(new ModifiedPakRequest(
        [new ModifiedPakFile(WriteTemp(root, cfgFolder + "Game.locres", locresBytes), cfgFolder + "Game.locres")],
        locresPak));

    using (var session = new PakArchiveSession())
    {
        await session.OpenAsync(new PakOpenOptions([locresPak]));
        byte[] fromPak = await session.ReadFileAsync(cfgFolder + "Game.locres");
        Check(fromPak.Length == locresBytes.Length, $"从 Pak 读出的 locres 长度一致（{fromPak.Length}）");

        byte[] json = LocresJsonExporter.ToJsonBytes(fromPak);
        LocresJsonDocument? doc = LocresJsonExporter.Parse(json);
        Check(doc is not null, "Pak 内 locres 可直接转 JSON");
        Check(doc?.Entries.Count == 2, $"JSON 含 2 条（实际 {doc?.Entries.Count}）");
        Check(doc?.Entries.Any(e => e.Text == "你好") == true, "中文内容正确保留");
    }

    // ══════════════════════════════════════════════════════════════
    Section("F8 · 本地化三路合并（需要基准文件）");

    // 基准 = 游戏原版。用 4 条词条方便观察。
    var baseLoc = new LocresPreviewDto("Optimized", 1, 4,
    [
        new LocresEntryDto(0, "/Game/Loc/UI", "K0", "原0", NamespaceHash: 1, KeyHash: 10),
        new LocresEntryDto(1, "/Game/Loc/UI", "K1", "原1", NamespaceHash: 1, KeyHash: 11),
        new LocresEntryDto(2, "/Game/Loc/UI", "K2", "原2", NamespaceHash: 1, KeyHash: 12),
        new LocresEntryDto(3, "/Game/Loc/UI", "K3", "原3", NamespaceHash: 1, KeyHash: 13),
    ]);
    byte[] baseLocBytes = LocresResourceCodec.Write(baseLoc);

    static byte[] EditLocres(LocresPreviewDto dto, string key, string text) =>
        LocresResourceCodec.Write(dto with
        {
            Entries = dto.Entries.Select(e => e.Key == key ? e with { Text = text } : e).ToArray(),
        });

    // 两份模组各改各的：A 改 K0，B 改 K1。
    byte[] modLocA = EditLocres(baseLoc, "K0", "A改0");
    byte[] modLocB = EditLocres(baseLoc, "K1", "B改1");

    // 对照：没有基准时只能整文件覆盖 —— 这正是"两份文本没法合起来"的原因。
    LocresPreviewDto overrideOnly = LocresResourceCodec.Read(modLocA);
    Check(overrideOnly.Entries.First(e => e.Key == "K1").Text == "原1",
        "整文件覆盖会丢掉另一份模组的改动（说明必须要有基准）");

    // 三路合并：两份改动都保留，没动过的保持原版。
    byte[] mergedLocBytes = LocresMerge.Merge(baseLocBytes, [modLocA, modLocB], out LocresMergeReport mr1);
    LocresPreviewDto mergedLoc = LocresResourceCodec.Read(mergedLocBytes);
    Check(mergedLoc.Entries.First(e => e.Key == "K0").Text == "A改0", "三路合并保留 A 的改动");
    Check(mergedLoc.Entries.First(e => e.Key == "K1").Text == "B改1", "三路合并保留 B 的改动");
    Check(mergedLoc.Entries.First(e => e.Key == "K2").Text == "原2", "未被任何模组改动的词条保持原版");
    Check(mergedLoc.EntryCount == 4, $"词条总数保持（{mergedLoc.EntryCount}）");
    Check(mr1.ChangedFromBaseline == 2, $"统计出 2 条实际改动（实际 {mr1.ChangedFromBaseline}）");
    Check(mr1.Conflicts == 0, $"无冲突（实际 {mr1.Conflicts}）");

    // 顺序调换：改动不重叠时，两份改动同样都在。
    byte[] mergedLocRev = LocresMerge.Merge(baseLocBytes, [modLocB, modLocA], out _);
    LocresPreviewDto revLoc = LocresResourceCodec.Read(mergedLocRev);
    Check(revLoc.Entries.First(e => e.Key == "K0").Text == "A改0"
          && revLoc.Entries.First(e => e.Key == "K1").Text == "B改1",
        "顺序调换后两份改动仍然都在");

    // 冲突：两份都改了同一条 → 列表靠上（优先级高）的胜出。
    byte[] conflictA = EditLocres(baseLoc, "K2", "A改2");
    byte[] conflictB = EditLocres(baseLoc, "K2", "B改2");
    LocresPreviewDto conflictLoc = LocresResourceCodec.Read(
        LocresMerge.Merge(baseLocBytes, [conflictA, conflictB], out LocresMergeReport mr2));
    Check(conflictLoc.Entries.First(e => e.Key == "K2").Text == "A改2",
        "冲突时按优先级取高者（列表靠上胜出）");
    Check(mr2.Conflicts == 1, $"统计出 1 条冲突（实际 {mr2.Conflicts}）");

    // 基准里有、模组文件里没有 → 计入 MissingFromMods（版本不一致的信号）。
    var shortLoc = new LocresPreviewDto("Optimized", 1, 2,
    [
        new LocresEntryDto(0, "/Game/Loc/UI", "K0", "原0", NamespaceHash: 1, KeyHash: 10),
        new LocresEntryDto(1, "/Game/Loc/UI", "K1", "原1", NamespaceHash: 1, KeyHash: 11),
    ]);
    LocresMerge.Merge(baseLocBytes, [LocresResourceCodec.Write(shortLoc)], out LocresMergeReport mr3);
    Check(mr3.MissingFromMods == 2, $"基准多出的词条计入 MissingFromMods（实际 {mr3.MissingFromMods}）");

    Check(LocresMerge.MatchesBaselineFileName(
            "kards/Content/Localization/Game/zh-Hans/Game.locres", @"D:\x\Game.locres"),
        "按文件名匹配基准（大小写不敏感）");
    Check(!LocresMerge.MatchesBaselineFileName(
            "kards/Content/Localization/Game/en/Game.locres", @"D:\x\Other.locres"),
        "文件名不同则不匹配");

    // ══════════════════════════════════════════════════════════════
    Section("F9 · 音频替换（BINKA 流式 SoundWave）与媒体文件替换");

    // WAV 读写：binka_encode 只吃 16 位整型 PCM，24 位/浮点素材必须先折到 16 位。
    var sinePcm = new byte[1600];
    for (var i = 0; i < sinePcm.Length / 2; i++)
    {
        var sample = (short)(Math.Sin(i * 0.05) * 20000);
        sinePcm[i * 2] = (byte)sample;
        sinePcm[i * 2 + 1] = (byte)(sample >> 8);
    }

    var sineAudio = new UAssetAudio.Core.PcmAudio(sinePcm, 8000, 2);
    var sineWav = UAssetAudio.Core.WavCodec.Write16(sineAudio);
    var sineBack = UAssetAudio.Core.WavCodec.Read(sineWav);
    Check(sineBack.Pcm16.SequenceEqual(sinePcm), "WAV 写→读往返逐字节一致");
    Check(sineBack.SampleRate == 8000 && sineBack.Channels == 2, "WAV 往返保住采样率与声道数");
    Check(Math.Abs(sineBack.Seconds - 0.05) < 0.001, $"WAV 时长按帧数换算（{sineBack.Seconds:0.###} 秒）");

    // 24 位 WAV：右移 8 位折到 16 位，且符号扩展正确（满幅/负满幅各一个采样）
    byte[] wav24 = BuildWav(24, 1, 8000,
    [
        0xFF, 0xFF, 0x7F, // +满幅
        0x00, 0x00, 0x80, // -满幅
        0x00, 0x00, 0x00, // 0
        0x00, 0x00, 0x40, // 0.5
    ]);
    var read24 = UAssetAudio.Core.WavCodec.Read(wav24);
    Check(read24.Pcm16.Length == 8, "24 位 WAV 被折成 16 位 PCM（每采样 2 字节）");
    Check(BitConverter.ToInt16(read24.Pcm16, 0) == short.MaxValue, "24 位满幅折到 16 位满幅");
    Check(BitConverter.ToInt16(read24.Pcm16, 2) == short.MinValue, "24 位负满幅正确符号扩展");
    Check(BitConverter.ToInt16(read24.Pcm16, 4) == 0, "24 位静音折成 0");

    // 视频/任意二进制：包里的 mp4 按字节替换，不做定长假设
    string mediaPak = Path.Combine(root, "media.pak");
    string originalVideoPath = Path.Combine(root, "Intro.mp4");
    string newVideoPath = Path.Combine(root, "Intro-new.mp4");
    const string mediaPakPath = "Kards/Content/Movies/Intro.mp4";
    byte[] originalVideoBytes = BuildPseudoRandomBytes(4096, 11);
    byte[] newVideoBytes = BuildPseudoRandomBytes(16384, 22); // 比原来大 4 倍
    File.WriteAllBytes(originalVideoPath, originalVideoBytes);
    File.WriteAllBytes(newVideoPath, newVideoBytes);
    ModifiedPakPackService.Pack(new ModifiedPakRequest(
        [new ModifiedPakFile(originalVideoPath, mediaPakPath)], mediaPak));

    using (var session = new PakArchiveSession())
    {
        await session.OpenAsync(new PakOpenOptions([mediaPak]));
        IReadOnlyDictionary<string, byte[]> related = await session.ReadRelatedRawFilesAsync(mediaPakPath);
        Check(related.Count == 1 && related.Values.First().SequenceEqual(originalVideoBytes),
            "原始视频字节可从 Pak 取出（媒体替换不需要 .uasset 兄弟文件）");
        AssetPreviewDto videoPreview = await session.ReadPreviewAsync(mediaPakPath);
        Check(videoPreview.Kind == "video", $"mp4 被识别成视频（{videoPreview.Kind}）");
    }

    string mediaPatchedPak = Path.Combine(root, "media-patched.pak");
    ModifiedPakPackService.Pack(new ModifiedPakRequest(
        [new ModifiedPakFile(newVideoPath, mediaPakPath)], mediaPatchedPak));
    using (var session = new PakArchiveSession())
    {
        await session.OpenAsync(new PakOpenOptions([mediaPatchedPak]));
        byte[] replaced = await session.ReadFileAsync(mediaPakPath);
        Check(replaced.SequenceEqual(newVideoBytes), "替换后 Pak 里的视频是新的字节");
        Check(replaced.Length != originalVideoBytes.Length, "长度变化也能正确写入（不做定长假设）");
    }

    // 音频替换：fixture + 映射文件 + binka_encode.exe 都是本地资源，缺哪个就跳过。
    string repoRootForAudio = LocateRepoRoot();
    string audioStem = Path.Combine(repoRootForAudio, "test", "AU_Arrow_morse_loop_01");
    string toolsDir = Path.Combine(repoRootForAudio, "tools");
    string? audioUsmap = FirstExistingFile(
        Path.Combine(repoRootForAudio, "Mapping.usmap"),
        Path.Combine(repoRootForAudio, "reference", "Mapping.usmap"),
        Path.Combine(repoRootForAudio, "test", "Mapping.usmap"));
    bool hasAudioFixture = File.Exists(audioStem + ".uasset") && File.Exists(audioStem + ".uexp") &&
                           File.Exists(audioStem + ".ubulk") && File.Exists(audioStem + ".wav");
    bool hasBinkaEncoder = UAssetAudio.Core.BinkaEncoder.IsAvailable(toolsDir);
    Console.WriteLine($"        音频 fixture={hasAudioFixture} 映射={audioUsmap is not null} binka_encode={hasBinkaEncoder}");

    if (!hasAudioFixture || audioUsmap is null || !hasBinkaEncoder)
    {
        Console.WriteLine("        跳过音频替换断言（缺少 fixture / 映射文件 / tools/binka_encode.exe）");
    }
    else
    {
        UAssetAudio.Core.SoundWaveInfo originalInfo = UAssetAudio.Core.SoundWavePatcher.Inspect(audioStem + ".uasset", audioUsmap);
        Check(originalInfo.IsBinka, $"原始资产是 BINKA（{originalInfo.Format}）");
        Check(originalInfo.IsStreaming && originalInfo.ChunkCount == 2,
            $"原始资产是两块的流式布局（{originalInfo.ChunkCount} 块）");
        Check(originalInfo.CanReplace, $"原始资产可替换（{originalInfo.Problem ?? "无阻碍"}）");
        Check(Math.Abs(originalInfo.Seconds - 2.53) < 0.15, $"时长从属性读出（{originalInfo.Seconds:0.###} 秒）");

        // UE 的块内 seek 表规则：首项 0，其余 = 上一块长度 + 4（实测原始资产就是这个规则）
        byte[] originalBulkBytes = File.ReadAllBytes(audioStem + ".ubulk");
        bool originalSeekOk = SeekTableFollowsUeRule(originalBulkBytes, out int originalBlocks, out uint framesPerChunk);
        Check(originalSeekOk, $"原始 ubulk 的 seek 表符合 UE 规则（{originalBlocks} 个数据块）");
        Check(Math.Abs(framesPerChunk - originalInfo.SampleRate * 10.24) < 1,
            $"块头记录每块 10.24 秒（{framesPerChunk} 帧 @ {originalInfo.SampleRate} Hz）");

        var fixturePcm = UAssetAudio.Core.WavCodec.Read(File.ReadAllBytes(audioStem + ".wav"));
        byte[] binkaBytes = UAssetAudio.Core.BinkaEncoder.Encode(fixturePcm, 4, toolsDir);
        var binkaInfo = UAssetAudio.Core.BinkaEncoder.ReadInfo(binkaBytes);
        Check(binkaBytes.AsSpan(0, 4).SequenceEqual("ABEU"u8), "编码结果是 ABEU 容器");
        Check(binkaInfo.SampleCount == fixturePcm.FrameCount,
            $"编码后采样数与原素材一致（{binkaInfo.SampleCount}）");
        Check(Math.Abs(binkaInfo.Seconds - fixturePcm.Seconds) < 0.01, "编码后时长与原素材一致");

        UAssetAudio.Core.SoundWaveReplacement replacement =
            UAssetAudio.Core.SoundWavePatcher.Replace(audioStem + ".uasset", binkaBytes, audioUsmap);
        Check(replacement.Uexp.Length == new FileInfo(audioStem + ".uexp").Length,
            "重建后 .uexp 长度不变（导出表偏移无需改动）");
        Check(SeekTableFollowsUeRule(replacement.Ubulk, out int newBlocks, out _),
            $"新 ubulk 的 seek 表同样符合规则（{newBlocks} 个数据块）");
        Check(replacement.Ubulk.AsSpan(0, 4).SequenceEqual("SEEK"u8), "新 ubulk 以 SEEK 前导开始");

        string patchedAudioDir = Path.Combine(root, "audio-patched");
        Directory.CreateDirectory(patchedAudioDir);
        string patchedAudioStem = Path.Combine(patchedAudioDir, "AU_Patched");
        File.WriteAllBytes(patchedAudioStem + ".uasset", replacement.Uasset);
        File.WriteAllBytes(patchedAudioStem + ".uexp", replacement.Uexp);
        File.WriteAllBytes(patchedAudioStem + ".ubulk", replacement.Ubulk);

        UAssetAudio.Core.SoundWaveInfo patchedInfo = UAssetAudio.Core.SoundWavePatcher.Inspect(patchedAudioStem + ".uasset", audioUsmap);
        Check(patchedInfo.CanReplace, "替换后的资产仍能按同一套布局解析");
        Check(Math.Abs(patchedInfo.Seconds - binkaInfo.Seconds) < 0.01,
            $"Duration 属性跟着新音频更新（{patchedInfo.Seconds:0.###} 秒）");
        Check(patchedInfo.Channels == fixturePcm.Channels && patchedInfo.SampleRate == fixturePcm.SampleRate,
            "声道数与采样率属性与素材一致");

        // 多块：长素材必须被切成多个流式块（每块 ≤ 256 KB），而不是报错拒绝
        {
            var longPcm = new byte[44100L * 2 * 2 * 60];
            for (var i = 0; i < longPcm.Length / 2; i++)
            {
                var sample = (short)(Math.Sin(i * 0.02) * 9000);
                longPcm[i * 2] = (byte)sample;
                longPcm[i * 2 + 1] = (byte)(sample >> 8);
            }

            byte[] longBinka = UAssetAudio.Core.BinkaEncoder.Encode(
                new UAssetAudio.Core.PcmAudio(longPcm, 44100, 2), 0, toolsDir); // 质量 0 = 体积最大，确保超一块
            Console.WriteLine($"        长素材编码：{longBinka.Length:N0} 字节");
            UAssetAudio.Core.SoundWaveReplacement longReplacement =
                UAssetAudio.Core.SoundWavePatcher.Replace(audioStem + ".uasset", longBinka, audioUsmap);

            string longDir = Path.Combine(root, "audio-long");
            Directory.CreateDirectory(longDir);
            string longStem = Path.Combine(longDir, "AU_Long");
            File.WriteAllBytes(longStem + ".uasset", longReplacement.Uasset);
            File.WriteAllBytes(longStem + ".uexp", longReplacement.Uexp);
            File.WriteAllBytes(longStem + ".ubulk", longReplacement.Ubulk);

            UAssetAudio.Core.SoundWaveInfo longInfo =
                UAssetAudio.Core.SoundWavePatcher.Inspect(longStem + ".uasset", audioUsmap);
            Check(longInfo.ChunkCount > 2, $"长素材被切成多个流式块（{longInfo.ChunkCount} 块）");
            Check(longInfo.MaxChunkBytes <= UAssetAudio.Core.SoundWavePatcher.MaxDataChunkBytes,
                $"每块都不超过上限（最大 {longInfo.MaxChunkBytes:N0} ≤ {UAssetAudio.Core.SoundWavePatcher.MaxDataChunkBytes:N0}）");
            Check(longInfo.AudioBytes == longReplacement.Ubulk.Length,
                $"块字节数之和等于 ubulk 长度（{longInfo.AudioBytes:N0}）");
            Check(longInfo.CanReplace, "多块结果仍能被同一条解析路径接受");
            Check(Math.Abs(longInfo.Seconds - longPcm.Length / 4d / 44100) < 0.1,
                $"多块结果时长正确（{longInfo.Seconds:0.##} 秒）");
        }
        // 打进 Pak 再用 CUE4Parse 读回：整条链路（导出表 / data resource / 内联块）都要对得上
        string audioPak = Path.Combine(root, "audio.pak");
        const string audioPakAsset = "Kards/Content/Audio/AU_Replaced.uasset";
        ModifiedPakPackService.Pack(new ModifiedPakRequest(
        [
            new ModifiedPakFile(patchedAudioStem + ".uasset", audioPakAsset),
            new ModifiedPakFile(patchedAudioStem + ".uexp", "Kards/Content/Audio/AU_Replaced.uexp"),
            new ModifiedPakFile(patchedAudioStem + ".ubulk", "Kards/Content/Audio/AU_Replaced.ubulk"),
        ], audioPak));

        using (var session = new PakArchiveSession())
        {
            await session.OpenAsync(new PakOpenOptions([audioPak], UsmapPath: audioUsmap));
            AssetPreviewDto audioPreview = await session.ReadPreviewAsync(audioPakAsset);
            Check(audioPreview.Kind == "audio",
                $"音频资产的预览 Kind 是 audio（{audioPreview.Kind}），替换页据此走音频分支");
            IReadOnlyDictionary<string, byte[]> audioParts = await session.ReadRelatedRawFilesAsync(audioPakAsset);
            Check(audioParts.Count == 3, $"一个 SoundWave 会带出三件套（{audioParts.Count} 个文件）");
            AudioPayloadDto payload = await session.ReadAudioPayloadAsync(audioPakAsset);
            Check(payload.Format.Equals("binka", StringComparison.OrdinalIgnoreCase),
                $"CUE4Parse 把新资产识别成 BINKA（{payload.Format}）");
            Check(payload.Data.Length > 32 && payload.Data.AsSpan(0, 4).SequenceEqual("ABEU"u8),
                "载荷以 ABEU 头开始（内联块被正确读回）");
            var payloadHeader = UAssetAudio.Core.BinkaEncoder.ReadInfo(payload.Data);
            Check(payloadHeader.SampleCount == binkaInfo.SampleCount,
                $"读回的采样数与编码结果一致（{payloadHeader.SampleCount}）");
            byte[] pakBulk = await session.ReadFileAsync("Kards/Content/Audio/AU_Replaced.ubulk");
            Check(pakBulk.SequenceEqual(replacement.Ubulk), "Pak 里的 ubulk 与重建结果逐字节一致");
        }
    }

    Section("F4/F1/F7 · UI 层验证（headless 真实窗口）");

    try
    {
        // 首次启动落在「欢迎」页，之后每次直接进「解包 & 模组」。
        //
        // 顺序很重要：App.OnFrameworkInitializationCompleted 在 Avalonia 初始化时就会
        // 自建一个 MainViewModel，而那一次构造会把 welcomeShown=true 写进设置文件。
        // 所以必须先完成初始化、再写"没进过主界面"的设置，否则测到的永远是"第二次启动"。
        Prism.FeatureTests.UiFixture.EnsureInitialized();

        string settingsFile = Path.Combine(AppContext.BaseDirectory, "Prism.Desktop.settings.json");
        File.WriteAllText(settingsFile, "{ \"welcomeShown\": false }");

        (Prism.Desktop.Views.MainWindow window, Prism.Desktop.ViewModels.MainViewModel vm) = Prism.FeatureTests.UiFixture.CreateShell();

        Check(window is not null, "MainWindow + 全部视图在运行时成功加载（模板已展开）");
        Check(vm.IsHomeVisible, "首次启动落在欢迎页");
        Check(!vm.IsConvertVisible, "初始不显示 Pak 转换页");

        // 再建一个视图模型：此时设置文件里已记下"进过主界面"，应直接落回干活的那页。
        var returning = new Prism.Desktop.ViewModels.MainViewModel();
        Check(returning.IsWorkspaceVisible, "再次启动直接进「解包 & 模组」（欢迎页只出现一次）");

        // 回归：文件列表的空状态原先绑在 private 的 IsPakOpen 上 —— 它不发变更通知，
        // 绑定只在加载时求值一次，于是打开 Pak 之后「还没有打开 Pak」仍然压在列表上。
        // 现在由会通知的 ShowPakEmptyState 驱动，这里验证它确实随状态翻转。
        Check(vm.ShowPakEmptyState, "未打开 Pak 时显示列表空状态");
        vm.PakPath = @"C:\fake\game.pak";
        Check(!vm.ShowPakEmptyState, "打开 Pak 后列表空状态消失（属性可通知）");
        vm.PakPath = string.Empty;

        foreach ((string property, Func<bool> visible, string label) in new (string, Func<bool>, string)[]
                 {
                     ("GoWorkspaceCommand", () => vm.IsWorkspaceVisible, "解包 & 模组"),
                     ("GoMergeCommand", () => vm.IsMergeVisible, "Pak 合并"),
                     ("GoConvertCommand", () => vm.IsConvertVisible, "Pak 转换"),
                     ("GoSettingsCommand", () => vm.IsSettingsVisible, "设置"),
                     ("GoHomeCommand", () => vm.IsHomeVisible, "主页"),
                 })
        {
            var prop = typeof(Prism.Desktop.ViewModels.MainViewModel).GetProperty(property);
            if (prop?.GetValue(vm) is CommunityToolkit.Mvvm.Input.IRelayCommand relay)
            {
                relay.Execute(null);
                Prism.FeatureTests.UiFixture.RunJobs();
                Check(visible(), $"导航到{label}页（{property}）");
            }
            else
            {
                Check(false, $"找不到命令 {property}");
            }
        }

        Check(vm.IsAnimationsEnabled, "过渡动画默认开启");

        // 转换页的「并入模组文件」开关：默认关闭 = 只转换贴图。
        // 打开后才把源 Pak 的蓝图/本地化等双端通用文件一并塞进输出 Pak。
        Check(!vm.ConvertIncludeModFiles, "「并入模组文件」开关默认关闭（默认只转换贴图）");

        // 「使用说明」浮层：各页共用一个，按 HelpTopic 切换内容，默认收起。
        // 用页内浮层而非独立 Window，是为了 Android 上也能用（TopLevel 不一定是 Window）。
        Check(!vm.IsHelpOpen, "「使用说明」浮层默认收起");
        if (typeof(Prism.Desktop.ViewModels.MainViewModel).GetProperty("OpenHelpCommand")?.GetValue(vm)
                is CommunityToolkit.Mvvm.Input.IRelayCommand openHelp
            && typeof(Prism.Desktop.ViewModels.MainViewModel).GetProperty("CloseHelpCommand")?.GetValue(vm)
                is CommunityToolkit.Mvvm.Input.IRelayCommand closeHelp)
        {
            openHelp.Execute("convert");
            Check(vm.IsHelpOpen && vm.IsHelpConvert, "OpenHelp(\"convert\") 展开转换说明");
            Check(vm.HelpTitle.Contains("转换"), $"标题随主题切换（{vm.HelpTitle}）");

            openHelp.Execute("merge");
            Check(vm.IsHelpMerge && !vm.IsHelpConvert, "切到合并主题时旧主题不再显示");

            closeHelp.Execute(null);
            Check(!vm.IsHelpOpen, "CloseHelpCommand 收起浮层");
        }
        else
        {
            Check(false, "找不到使用说明的开合命令");
        }

        // 宽屏档位：整体放大 + 内容列与搜索框放宽；窄窗必须回到原值（手机布局一行不改）。
        Check(!vm.IsWideLayout, "默认窗口宽度下不是宽屏档位");
        double narrowMaxWidth = vm.ConvertContentMaxWidth;
        double narrowSearchWidth = vm.SearchBoxWidth;

        vm.WindowWidth = 1920;
        Check(vm.IsWideLayout, "窗口 1920 宽时进入宽屏档位");
        Check(vm.ConvertContentMaxWidth > narrowMaxWidth,
            $"宽屏内容列放宽（{narrowMaxWidth} → {vm.ConvertContentMaxWidth}）");
        Check(vm.SearchBoxWidth > narrowSearchWidth,
            $"宽屏搜索框加宽（{narrowSearchWidth} → {vm.SearchBoxWidth}）");

        // 只放大文字：宽屏下把字号资源整体调大，内边距与控件尺寸不变。
        // （曾经用整块 LayoutTransform 等比缩放，结果控件与留白一起变大，看起来像把手机界面拉大。）
        double wideHintFont = FontResource("Fs11");
        double wideBodyFont = FontResource("Fs12");
        Check(wideHintFont > 11, $"宽屏字号资源已调大（Fs11 {11} → {wideHintFont}）");
        Check(wideBodyFont > 12, $"多档字号同步调大（Fs12 {12} → {wideBodyFont}）");

        vm.WindowWidth = 600;
        Check(vm.IsCompact && !vm.IsWideLayout, "窄窗回到紧凑档位");
        Check(FontResource("Fs11") == 11 && FontResource("Fs12") == 12,
            $"窄窗字号恢复原值（Fs11={FontResource("Fs11")}, Fs12={FontResource("Fs12")}）");
        Check(vm.ConvertContentMaxWidth == narrowMaxWidth, "窄窗内容列恢复原值");

        // 主题切换：浅色主题在大屏上是一整片亮色，用户应能不依赖系统设置直接换深色。
        Check(vm.ThemeMode == "system" && vm.IsThemeSystem, $"默认跟随系统（{vm.ThemeMode}）");
        vm.IsThemeDark = true;
        Check(vm.ThemeMode == "dark" && vm.IsThemeDark && !vm.IsThemeSystem,
            "选「深色」后单选状态与主题值同步");
        Check(Avalonia.Application.Current!.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Dark,
            "深色已应用到应用主题");
        vm.IsThemeLight = true;
        Check(Avalonia.Application.Current!.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Light,
            "浅色已应用到应用主题");
        vm.IsThemeSystem = true;
        Check(Avalonia.Application.Current!.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Default,
            "回到「跟随系统」");


        // 主页「搜索 & 导出」入口应直达浏览标签，而不是和工作区入口落在同一个标签。
        var goBrowse = typeof(Prism.Desktop.ViewModels.MainViewModel).GetProperty("GoBrowseCommand");
        if (goBrowse?.GetValue(vm) is CommunityToolkit.Mvvm.Input.IRelayCommand browseRelay)
        {
            vm.CurrentTabIndex = 0;
            browseRelay.Execute(null);
            Prism.FeatureTests.UiFixture.RunJobs();
            Check(vm.IsWorkspaceVisible && vm.IsBrowseTab,
                $"GoBrowseCommand 直达浏览标签（view={vm.CurrentView}, tab={vm.CurrentTabIndex}）");
        }
        else
        {
            Check(false, "找不到 GoBrowseCommand");
        }

        // 合并列表不再有固定的"主 Pak"：每一项都能拖动、能移除。
        var mergeItem = new Prism.Desktop.Models.MergePakItem("a.pak", "a.pak");
        Check(mergeItem.CanDrag && mergeItem.CanRemove, "合并列表每一项都可拖动、可移除（没有固定的主 Pak）");

        // 优先级：越靠上越高，且任意一项都能被移到首位。
        vm.MergePaks.Clear();
        vm.MergePaks.Add(new Prism.Desktop.Models.MergePakItem("x.pak", "x.pak"));
        vm.MergePaks.Add(new Prism.Desktop.Models.MergePakItem("y.pak", "y.pak"));
        vm.MoveMergePak(vm.MergePaks[1], 0);
        Check(vm.MergePaks[0].Path == "y.pak", "任意一项都能移到首位（第 1 项即最高优先级）");
        vm.MergePaks.Clear();

        // 合并的三路合并"判定"是纯函数，直接单测（不经过 VM 的异步命令）。
        // 完整链路由 F8 的算法测试 + 真实数据原型覆盖。
        Check(LocresMerge.ShouldMergeConflict(cfgFolder + "Game.locres", @"D:\x\Game.locres", baseLocBytes),
            "设了基准且路径同名 .locres → 该走三路合并");
        Check(!LocresMerge.ShouldMergeConflict(cfgFolder + "Game.locres", @"D:\x\Game.locres", null),
            "没设基准（字节为 null）→ 退回整文件覆盖");
        Check(!LocresMerge.ShouldMergeConflict(cfgFolder + "Shared.ini", @"D:\x\Game.locres", baseLocBytes),
            "非 .locres 路径 → 不走三路合并");
        Check(!LocresMerge.ShouldMergeConflict(cfgFolder + "Other.locres", @"D:\x\Game.locres", baseLocBytes),
            "基准文件名不同 → 不走三路合并");

        vm.SearchQuery = "T_Button";
        Check(vm.SearchPlaceholder.Contains("搜索"), $"关键词输入时提示搜索（{vm.SearchPlaceholder}）");
        vm.SearchQuery = "Kards/Content/UI";
        Check(vm.SearchPlaceholder.Contains("跳转"), $"路径输入时提示跳转（{vm.SearchPlaceholder}）");
        vm.SearchQuery = string.Empty;

        vm.ConvertSourcePath = string.Empty;
        Check(!vm.CanConvertPak, "未选源 Pak 时不可转换");

        // 列表淡入动画（FillMode=Forward）不应把条目的最终不透明度钉在中间值上。
        // 列表淡入动画：用真实窗口里的 ListBox 验证（分离控件在 headless 下
        // 不会应用模板，必须先放进 Window 才能拿到真实视觉树）。
        // 关键断言是终态不透明度为 1 —— FillMode=Forward 若写错会把元素钉在 0。
        var animWindow = new Window
        {
            Width = 640,
            Height = 480,
            Content = new ListBox
            {
                Classes = { "browser-list" },
                ItemsSource = new[] { "one", "two", "three" },
                Styles = { (Avalonia.Styling.IStyle)Prism.FeatureTests.UiFixture.BrowserListFadeStyle() },
            },
        };

        animWindow.Measure(new Size(640, 480));
        animWindow.Arrange(new Rect(0, 0, 640, 480));
        Prism.FeatureTests.UiFixture.RunJobs();

        var listBox = Prism.FeatureTests.UiFixture.FindDescendant<ListBox>(animWindow);
        if (listBox is null)
        {
            Check(false, "找不到测试用 ListBox");
        }
        else
        {
            Check(listBox.ItemCount == 3, $"ListBox 渲染出 3 项（实际 {listBox.ItemCount}）");
            Check(listBox.Classes.Contains("browser-list"), "浏览器列表使用了 browser-list 样式类");

            if (listBox.ContainerFromIndex(0) is Control itemControl)
            {
                Check(itemControl.IsVisible, "列表条目可见");
                Check(itemControl.Opacity > 0.99,
                    $"列表条目终态不透明度为 1（实际 {itemControl.Opacity:F3}）");
            }
            else
            {
                Check(false, "虚拟化未生成列表容器");
            }
        }
    }
    catch (Exception ex)
    {
        Check(false, $"UI 验证抛出异常：{ex.GetType().Name}: {ex.Message}");
        Console.Error.WriteLine(ex.ToString());
    }

    // ══════════════════════════════════════════════════════════════
    Section("F11 · 视频预览（ffmpeg 探元数据 / 抽封面 / 预览尺寸）");

    // 预览面板的视频播放靠 ffmpeg 出帧，这里验的是"能探出元数据、能抽封面、尺寸算对"。
    // 片源现场用 ffmpeg 的 testsrc2 生成，不依赖仓库里的样本文件。
    string? ffmpegExe = UAssetAudio.Core.ExternalToolLocator.Find(
        Prism.Desktop.Services.Ffmpeg.ToolName, Path.Combine(repoRootForAudio, "tools"));
    if (ffmpegExe is null)
    {
        Console.WriteLine($"        跳过（没找到 {Prism.Desktop.Services.Ffmpeg.ToolName}）");
    }
    else
    {
        string clip = Path.Combine(root, "preview-clip.mp4");
        RunFfmpeg(ffmpegExe, [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=15:duration=2",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", clip,
        ]);
        Check(File.Exists(clip) && new FileInfo(clip).Length > 1024, "用 testsrc2 生成 2 秒测试片");

        bool probed = Prism.Desktop.Services.Ffmpeg.TryProbe(clip, Path.Combine(repoRootForAudio, "tools"), out Prism.Desktop.Services.VideoProbeInfo probe);
        Check(probed, "ffmpeg 能探出容器信息");
        if (probed)
        {
            Check(probe.Width == 320 && probe.Height == 240, $"分辨率解析正确（{probe.DescribeSize}）");
            Check(Math.Abs(probe.Seconds - 2) < 0.5, $"时长解析正确（{probe.Seconds:0.##} 秒）");
            Check(probe.FrameRate is > 10 and < 20, $"帧率解析正确（{probe.FrameRate:0.##} fps）");
        }

        string poster = Path.Combine(root, "preview-poster.png");
        Check(
            Prism.Desktop.Services.Ffmpeg.TryExtractPoster(clip, poster, Path.Combine(repoRootForAudio, "tools"), TimeSpan.FromSeconds(0.5)) &&
            new FileInfo(poster).Length > 512,
            "抽得出封面帧（PNG 非空）");

        (int scaledWidth, int scaledHeight) = Prism.Desktop.Services.Ffmpeg.ScaleToPreview(1920, 1080);
        Check(scaledWidth == 720 && scaledHeight == 404, $"1080p 缩到预览尺寸（{scaledWidth}x{scaledHeight}）");
        (int smallWidth, int smallHeight) = Prism.Desktop.Services.Ffmpeg.ScaleToPreview(320, 240);
        Check(smallWidth == 320 && smallHeight == 240, "小于上限的片子不放大");

        // 音频时长读取：预览要在播放前就把进度条填上
        var probeWav = UAssetAudio.Core.WavCodec.Write16(new byte[48000], 24000, 1);
        Check(
            UAssetAudio.Core.WavCodec.TryReadSeconds(probeWav, out double probeSeconds) && Math.Abs(probeSeconds - 1) < 0.01,
            $"WAV 头算时长（{probeSeconds:0.###} 秒）");
    }

    // ══════════════════════════════════════════════════════════════
    Section("F10 · 替换页流程（音频 / 视频，含真实编码）");

    // 这里驱动真实的 MainViewModel：打开 pak → 进目录 → 选中资产 → 加入替换 → 选素材。
    // 文件选择器用 FilePickerOverride 注入，绕开无头环境里没有的 StorageProvider。
    Prism.FeatureTests.UiFixture.EnsureInitialized();
    try
    {
        if (File.Exists(mediaPak))
        {
            var mediaVm = new Prism.Desktop.ViewModels.MainViewModel { PakPath = mediaPak };
            RunVmCommand(mediaVm, mediaVm.OpenPakCommand);
            EnterFolder(mediaVm, "Kards");
            EnterFolder(mediaVm, "Content");
            EnterFolder(mediaVm, "Movies");

            Prism.Desktop.Models.EntryItem? videoEntry = mediaVm.Entries
                .FirstOrDefault(entry => !entry.IsDirectory && entry.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
            Check(videoEntry is not null, "视频文件出现在列表里");
            mediaVm.SelectedItem = videoEntry;
            RunVmCommand(mediaVm, mediaVm.AddSelectedToPatchCommand);

            Prism.Desktop.Models.PatchItem? mediaPatch = mediaVm.PatchItems.FirstOrDefault();
            Check(mediaPatch is not null && mediaPatch.IsMedia, "视频条目「加入替换」后被识别为媒体替换");
            Check(mediaPatch?.SourcePath.EndsWith("Intro.mp4", StringComparison.OrdinalIgnoreCase) == true,
                $"媒体替换项指向那个视频文件（{mediaPatch?.SourcePath}）");

            mediaVm.FilePickerOverride = (_, _) => Task.FromResult<string?>(newVideoPath);
            RunVmCommand(mediaVm, mediaVm.PickReplacementCommand);
            Check(mediaPatch!.IsReplaced, $"视频替换完成（{mediaPatch.Error ?? "无错误"}）");
            Check(mediaPatch.PatchedFiles.Count == 1, "媒体替换只产出一个文件");
            Check(File.ReadAllBytes(mediaPatch.PatchedFiles.Values.First()).SequenceEqual(newVideoBytes),
                "替换内容就是选中的新视频字节");
        }
        else
        {
            Console.WriteLine("        跳过视频流程断言（测试 pak 未生成）");
        }

        if (hasAudioFixture && audioUsmap is not null && hasBinkaEncoder)
        {
            // 用原始 fixture 现场打一个 pak，走"替换真资产"的真实路径
            string vmAudioPak = Path.Combine(root, "vm-audio.pak");
            ModifiedPakPackService.Pack(new ModifiedPakRequest(
            [
                new ModifiedPakFile(audioStem + ".uasset", "Kards/Content/Audio/AU_Arrow_morse_loop_01.uasset"),
                new ModifiedPakFile(audioStem + ".uexp", "Kards/Content/Audio/AU_Arrow_morse_loop_01.uexp"),
                new ModifiedPakFile(audioStem + ".ubulk", "Kards/Content/Audio/AU_Arrow_morse_loop_01.ubulk"),
            ], vmAudioPak));

            var audioVm = new Prism.Desktop.ViewModels.MainViewModel { PakPath = vmAudioPak, UsmapPath = audioUsmap };
            RunVmCommand(audioVm, audioVm.OpenPakCommand);
            EnterFolder(audioVm, "Kards");
            EnterFolder(audioVm, "Content");
            EnterFolder(audioVm, "Audio");

            Prism.Desktop.Models.EntryItem? audioEntry = audioVm.Entries
                .FirstOrDefault(entry => !entry.IsDirectory && entry.Name.StartsWith("AU_Arrow", StringComparison.OrdinalIgnoreCase));
            Check(audioEntry is not null, "音频资产出现在列表里");
            audioVm.SelectedItem = audioEntry;
            RunVmCommand(audioVm, audioVm.AddSelectedToPatchCommand);

            Prism.Desktop.Models.PatchItem? audioPatch = audioVm.PatchItems.FirstOrDefault();
            Check(audioPatch is not null && audioPatch.IsAudio, "SoundWave「加入替换」后被识别为音频替换");
            Check(audioPatch?.Detail.Contains("48,000") == true, $"音频替换项带声道/采样率（{audioPatch?.Detail}）");

            audioVm.PatchItems.FirstOrDefault()!.AudioQuality = 6;
            audioVm.FilePickerOverride = (_, _) => Task.FromResult<string?>(audioStem + ".wav");
            RunVmCommand(audioVm, audioVm.PickReplacementCommand);
            Check(audioPatch!.IsReplaced, $"音频替换完成（{audioPatch.Error ?? "无错误"}）");
            Check(audioPatch.PatchedFiles.Count == 3, $"三件套都被替换（{audioPatch.PatchedFiles.Count} 个文件）");
            Check(audioPatch.ReplacementDetail?.Contains("编码后") == true,
                $"替换信息带编码后体积（{audioPatch.ReplacementDetail}）");
        }
        else
        {
            Console.WriteLine("        跳过音频流程断言（缺少 fixture / 映射文件 / binka_encode.exe）");
        }
    }
    catch (Exception ex)
    {
        Check(false, $"替换页流程抛出异常：{ex.GetType().Name}: {ex.Message}");
    }

    Section("Pak 打包 · 非 ASCII 路径回归");

    // 这条曾经导致打包进程直接 abort（Rust unwrap panic）或静默损坏路径。
    // 详见 PakPathEncodingTests 的说明。
    Prism.FeatureTests.PakPathEncodingTests.Run(
        Path.Combine(root, "path-encoding"),
        (ok, label) => Check(ok, label));

    Section("Pak 打包 · Oodle 缺失时必须降级而不是闪退");

    // 真实事故：设置里开着「替换时使用 Oodle 压缩」，但发行包不含 oo2core_9_win64.dll。
    // repak_bind 找不到 Oodle 会 Rust panic → abort 整个进程（Windows 上 0xC0000409），
    // .NET 的 try/catch 与 UnhandledException 全都拦不住，用户看到的就是"点一下闪退"。
    // 唯一可行的做法是调用前探测，所以这里验证：请求 Oodle 不会崩，且如实报告降级。
    Console.WriteLine($"        OodleNative.IsAvailable = {OodleNative.IsAvailable}");
    string oodlePak = Path.Combine(root, "oodle-fallback.pak");
    ModifiedPakPackResult oodleResult = ModifiedPakPackService.Pack(new ModifiedPakRequest(
        [new ModifiedPakFile(WriteTemp(root, "oodle/payload.bin", new byte[1024]), "test/payload.bin")],
        oodlePak,
        UseCompression: true,
        Compression: UAssetAPI.PakCompression.Oodle));

    Check(File.Exists(oodlePak) && new FileInfo(oodlePak).Length > 0,
        "请求 Oodle 压缩时打包成功返回（进程没有 abort）");
    Check(oodleResult.OodleRequestedButUnavailable == !OodleNative.IsAvailable,
        $"降级标记与本机 Oodle 可用性一致（可用={OodleNative.IsAvailable}）");
    if (oodleResult.OodleRequestedButUnavailable)
    {
        Check(oodleResult.Note is not null && oodleResult.Note.Contains("oo2core"),
            "降级说明里点明了缺的是 oo2core_9_win64.dll");
    }

    // 不请求压缩时不应有任何降级标记
    ModifiedPakPackResult plainResult = ModifiedPakPackService.Pack(new ModifiedPakRequest(
        [new ModifiedPakFile(WriteTemp(root, "plain/payload.bin", new byte[1024]), "test/plain.bin")],
        Path.Combine(root, "plain.pak")));
    Check(!plainResult.OodleRequestedButUnavailable && plainResult.Note is null,
        "不请求 Oodle 时不产生降级提示");

    Section("纹理解析双副本一致性（UAssetTexture.Core ↔ UAssetCLI）");

    // 贴图替换/转换的纹理布局解析器有两份：
    //   UAssetTexture.Core —— 桌面端进程内、Android 进程内替换、双端转换都走它；
    //   UAssetCLI          —— AOT 单文件，刻意不引用 CUE4Parse，故自带一份副本。
    // 两份之间没有任何编译期约束，README 只写了"改一处要改两处"这句口头约定。
    //
    // 代价已经付过一次：2600b0d（尺寸不能写死在固定偏移）就是同一个 bug 在两边各修
    // 一遍才收工。所以这里用同一份真实资产分别走两条链路，把 format/尺寸/mip 数/
    // 每一级 mip 的 (尺寸, 字节数, 存储位置, 偏移) 全部对齐比对 —— 只改一边就会红。
    //
    // 需要 UAssetCLI 产物（Release AOT 发布优先）。没有就跳过，与真实资产缺失的处理一致。
    string? cliPath = FindUAssetCli(realAssetDir);
    Console.WriteLine($"        UAssetCLI 产物: {cliPath ?? "未找到"}");
    if (!hasRealTexture || cliPath is null)
    {
        Console.WriteLine("        !! 缺真实资产或 UAssetCLI 产物，跳过双副本一致性断言");
    }
    else
    {
        TextureAssetInfo coreTexture = TextureAssetParser.Load(realUasset, EngineVersion.VER_UE5_6, null);
        string cliOutput = RunCliInspectTexture(cliPath, realUasset, "VER_UE5_6");

        string cliFormat = FieldOf(cliOutput, "Format");
        Check(string.Equals(coreTexture.Format.Name, cliFormat, StringComparison.OrdinalIgnoreCase),
            $"像素格式一致（Core={coreTexture.Format.Name} / CLI={cliFormat}）");

        string cliSize = FieldOf(cliOutput, "Size");
        Check(cliSize == $"{coreTexture.Width}x{coreTexture.Height}",
            $"纹理尺寸一致（Core={coreTexture.Width}x{coreTexture.Height} / CLI={cliSize}）");

        int cliExternalMips = int.Parse(FieldOf(cliOutput, "External mips"));
        Check(coreTexture.ExternalMipCount == cliExternalMips,
            $"外部 mip（.ubulk）数量一致（Core={coreTexture.ExternalMipCount} / CLI={cliExternalMips}）");

        int cliInlineMips = int.Parse(FieldOf(cliOutput, "Inline mips"));
        Check(coreTexture.InlineMips.Count == cliInlineMips,
            $"内联 mip（.uexp）数量一致（Core={coreTexture.InlineMips.Count} / CLI={cliInlineMips}）");

        List<(int Index, int Width, int Height, int ByteLength, string Storage, int Offset)> cliMips =
            ParseMipLines(cliOutput);
        Check(coreTexture.MipPlacements.Count == cliMips.Count,
            $"mip 总数一致（Core={coreTexture.MipPlacements.Count} / CLI={cliMips.Count}）");

        // 逐 mip 比对：任何一级的尺寸/字节数/偏移错位，都是替换时会写坏贴图的那种偏差。
        foreach (TextureMipPlacement placement in coreTexture.MipPlacements)
        {
            var cliMip = cliMips.FirstOrDefault(m => m.Index == placement.Index);
            bool same = cliMip.Index == placement.Index
                && cliMip.Width == placement.Width
                && cliMip.Height == placement.Height
                && cliMip.ByteLength == placement.ByteLength
                && cliMip.Storage == placement.Storage.ToString()
                && cliMip.Offset == placement.Offset;
            Check(same,
                $"mip {placement.Index} 布局一致（{placement.Width}x{placement.Height}, " +
                $"{placement.ByteLength} 字节, {placement.Storage}, 偏移 {placement.Offset}）");
        }
    }

    Section("结果");
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch
    {
        // 临时目录清理失败可忽略。
    }
}

Console.WriteLine();
Console.WriteLine($"通过 {passed} 项，失败 {failures} 项 " + (failures == 0 ? "✅" : "❌"));
return failures == 0 ? 0 : 1;

// ── 辅助 ──

// 把 Pak 里的一个资产（连同同名 .uexp/.ubulk）取到磁盘，返回 .uasset 路径。
// 替换逻辑要求模板资产的三件套在同一目录里。
static string outputOf(string pakPath, string entryPath)
{
    using var session = new PakArchiveSession();
    session.OpenAsync(new PakOpenOptions([pakPath])).GetAwaiter().GetResult();

    string dir = Path.Combine(Path.GetTempPath(), "prism-test-extract-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    string fileName = Path.GetFileName(entryPath);
    string stem = Path.GetFileNameWithoutExtension(entryPath);
    string? parent = Path.GetDirectoryName(entryPath)?.Replace('\\', '/');

    foreach (string name in new[] { fileName, stem + ".uexp", stem + ".ubulk" })
    {
        string pakEntry = string.IsNullOrEmpty(parent) ? name : parent + "/" + name;
        try
        {
            byte[] data = session.ReadFileAsync(pakEntry).GetAwaiter().GetResult();
            File.WriteAllBytes(Path.Combine(dir, name), data);
        }
        catch
        {
            // 该资产没有这个兄弟文件（例如 mip 全部内联），跳过。
        }
    }

    return Path.Combine(dir, fileName);
}

// 从当前程序集位置向上找到仓库根（含 t_cromwell.uasset 的目录），避免写死绝对路径。
static string LocateRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "t_cromwell.uasset")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return AppContext.BaseDirectory;
}

static ModifiedPakFile FileOf(string root, string pakPath, string content) =>
    new(WriteTemp(root, pakPath, System.Text.Encoding.UTF8.GetBytes(content)), pakPath);

/// <summary>读应用级字号资源（宽屏档位由 LayoutDensity 写入）。找不到返回 -1。</summary>
static double FontResource(string key) =>
    Avalonia.Application.Current?.Resources[key] is double value ? value : -1d;

static string WriteTemp(string root, string pakPath, byte[] content)
{
    string dir = Path.Combine(root, "payload",
        Path.GetDirectoryName(pakPath)?.Replace('/', Path.DirectorySeparatorChar) ?? string.Empty);
    Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, Path.GetFileName(pakPath));
    File.WriteAllBytes(path, content);
    return path;
}

// ── 双副本一致性用到的辅助 ──

/// <summary>
/// 找可用的 UAssetCLI 产物，优先 Release AOT 发布（README 推荐的发布形态），
/// 其次桌面端输出目录里被复制过去的那份。找不到返回 null（该组断言跳过）。
/// </summary>
static string? FindUAssetCli(string repoRoot)
{
    string[] candidates =
    [
        Path.Combine(repoRoot, "UAssetCLI", "bin", "Release", "net10.0", "win-x64", "publish", "UAssetCLI.exe"),
        Path.Combine(repoRoot, "UAssetCLI", "bin", "Release", "net10.0", "publish", "UAssetCLI.exe"),
        Path.Combine(repoRoot, "Prism.Desktop.Desktop", "bin", "Debug", "net10.0", "UAssetCLI", "UAssetCLI.exe"),
        Path.Combine(repoRoot, "Prism.Desktop.Desktop", "bin", "Release", "net10.0", "UAssetCLI", "UAssetCLI.exe"),
        Path.Combine(repoRoot, "UAssetCLI", "bin", "Debug", "net10.0", "win-x64", "UAssetCLI.exe"),
        Path.Combine(repoRoot, "UAssetCLI", "bin", "Debug", "net10.0", "UAssetCLI.exe"),
    ];

    return candidates.FirstOrDefault(File.Exists);
}

/// <summary>跑 <c>UAssetCLI inspect-texture</c>，返回 stdout。非零退出码抛异常（测试应显式失败）。</summary>
static string RunCliInspectTexture(string cliPath, string assetPath, string engine)
{
    System.Diagnostics.ProcessStartInfo startInfo = new()
    {
        FileName = cliPath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add("inspect-texture");
    startInfo.ArgumentList.Add("--asset");
    startInfo.ArgumentList.Add(assetPath);
    startInfo.ArgumentList.Add("--engine");
    startInfo.ArgumentList.Add(engine);

    using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)
        ?? throw new InvalidOperationException("无法启动 UAssetCLI"); 
    string stdout = process.StandardOutput.ReadToEnd();
    string stderr = process.StandardError.ReadToEnd();

    if (!process.WaitForExit(120_000))
    {
        process.Kill(entireProcessTree: true);
        throw new TimeoutException("UAssetCLI inspect-texture 超时（120s）");
    }

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"UAssetCLI inspect-texture 退出码 {process.ExitCode}：{stderr}");
    }

    return stdout;
}

/// <summary>取 <c>inspect-texture</c> 输出的单行字段（形如 <c>Format: PF_DXT1</c>）。</summary>
static string FieldOf(string output, string name)
{
    foreach (string line in output.Split('\n'))
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith(name + ": ", StringComparison.Ordinal))
        {
            return trimmed[(name.Length + 2)..].Trim();
        }
    }

    throw new InvalidOperationException($"UAssetCLI 输出里找不到字段 “{name}”");
}

/// <summary>解析 <c>mip 3: 128x128, 8192 bytes, Ubulk, offset 688128</c> 这类行。</summary>
static List<(int Index, int Width, int Height, int ByteLength, string Storage, int Offset)> ParseMipLines(string output)
{
    List<(int, int, int, int, string, int)> mips = [];
    foreach (string line in output.Split('\n'))
    {
        string trimmed = line.Trim();
        if (!trimmed.StartsWith("mip ", StringComparison.Ordinal))
        {
            continue;
        }

        Match match = Regex.Match(trimmed, @"^mip (\d+): (\d+)x(\d+), (\d+) bytes, (\w+), offset (\d+)$");
        if (!match.Success)
        {
            throw new InvalidOperationException($"无法解析 UAssetCLI 的 mip 行：“{trimmed}”");
        }

        mips.Add((
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value),
            int.Parse(match.Groups[4].Value),
            match.Groups[5].Value,
            int.Parse(match.Groups[6].Value)));
    }

    return mips;
}

/// <summary>按顺序返回第一个存在的文件（跳过缺失的可选资源）。</summary>
static string? FirstExistingFile(params string[] candidates) => candidates.FirstOrDefault(File.Exists);

/// <summary>拼一个最小 WAV（44 字节头 + PCM 数据），用于喂各种位深给解码器。</summary>
static byte[] BuildWav(int bitsPerSample, int channels, int sampleRate, byte[] pcm)
{
    byte[] wav = new byte[44 + pcm.Length];
    "RIFF"u8.CopyTo(wav);
    BitConverter.GetBytes(36 + pcm.Length).CopyTo(wav, 4);
    "WAVE"u8.CopyTo(wav.AsSpan(8));
    "fmt "u8.CopyTo(wav.AsSpan(12));
    BitConverter.GetBytes(16).CopyTo(wav, 16);
    BitConverter.GetBytes((ushort)1).CopyTo(wav, 20); // PCM
    BitConverter.GetBytes((ushort)channels).CopyTo(wav, 22);
    BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
    BitConverter.GetBytes(sampleRate * channels * bitsPerSample / 8).CopyTo(wav, 28);
    BitConverter.GetBytes((ushort)(channels * bitsPerSample / 8)).CopyTo(wav, 32);
    BitConverter.GetBytes((ushort)bitsPerSample).CopyTo(wav, 34);
    "data"u8.CopyTo(wav.AsSpan(36));
    BitConverter.GetBytes(pcm.Length).CopyTo(wav, 40);
    pcm.CopyTo(wav, 44);
    return wav;
}

/// <summary>确定性伪随机字节（避免测试里用 Random 造成不可复现）。</summary>
static byte[] BuildPseudoRandomBytes(int length, int seed)
{
    byte[] bytes = new byte[length];
    uint state = ((uint)seed * 2654435761u) | 1u;
    for (var i = 0; i < length; i++)
    {
        state = (state * 1664525u) + 1013904223u;
        bytes[i] = (byte)(state >> 16);
    }

    return bytes;
}

/// <summary>
/// 校验 <c>.ubulk</c> 的流式块符合 UE 的写法：
/// <c>"SEEK" + 帧数 + 保留 + 表项数</c> 共 15 字节前导，表首项 0、其余等于上一块长度 + 4，
/// 表后是若干 <c>0x9999</c> 同步帧，且恰好铺满整个块。
/// </summary>
static bool SeekTableFollowsUeRule(byte[] chunk, out int blockCount, out uint framesPerChunk)
{
    blockCount = 0;
    framesPerChunk = 0;
    if (chunk.Length < 32 || !chunk.AsSpan(0, 4).SequenceEqual("SEEK"u8))
    {
        return false;
    }

    framesPerChunk = BitConverter.ToUInt32(chunk, 4);
    int entries = BitConverter.ToInt32(chunk, 11);
    if (entries <= 0)
    {
        return false;
    }

    const int tableStart = 15;
    if (tableStart + (entries * 2) > chunk.Length || BitConverter.ToUInt16(chunk, tableStart) != 0)
    {
        return false;
    }

    int position = tableStart + (entries * 2);
    List<int> sizes = [];
    while (position + 4 <= chunk.Length)
    {
        uint header = BitConverter.ToUInt32(chunk, position);
        if ((header & 0xFFFF) != 0x9999)
        {
            return false;
        }

        int size = (int)(header >> 16);
        int headerBytes = 4;
        if (size == 0xFFFF)
        {
            // 变长帧：真实长度与采样数写在后面的 u32 里
            if (position + 8 > chunk.Length)
            {
                return false;
            }

            size = BitConverter.ToUInt16(chunk, position + 4);
            headerBytes = 8;
        }

        position += headerBytes + size;
        sizes.Add(size);
        if (sizes.Count > entries)
        {
            return false;
        }
    }

    if (position != chunk.Length || sizes.Count != entries)
    {
        return false;
    }

    for (var i = 1; i < entries; i++)
    {
        if (BitConverter.ToUInt16(chunk, tableStart + (i * 2)) != (ushort)(sizes[i - 1] + 4))
        {
            return false;
        }
    }

    blockCount = sizes.Count;
    return true;
}

/// <summary>跑一个异步命令并把它推完（无头环境没有消息循环，需要手动 Pump）。</summary>
static void RunVmCommand(Prism.Desktop.ViewModels.MainViewModel vm, CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command)
{
    Task task = command.ExecuteAsync(null);
    DateTime deadline = DateTime.UtcNow.AddSeconds(120);
    while (!task.IsCompleted && DateTime.UtcNow < deadline)
    {
        Prism.FeatureTests.UiFixture.RunJobs();
        Thread.Sleep(5);
    }

    Prism.FeatureTests.UiFixture.RunJobs();
    task.GetAwaiter().GetResult();
}

/// <summary>在浏览器列表里双击进入某个目录。</summary>
static void EnterFolder(Prism.Desktop.ViewModels.MainViewModel vm, string name)
{
    Prism.Desktop.Models.EntryItem? entry = vm.Entries.FirstOrDefault(
        candidate => candidate.IsDirectory && candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (entry is null)
    {
        throw new InvalidOperationException($"列表里找不到目录 {name}（当前 {vm.CurrentFolder}）");
    }

    vm.SelectedItem = entry;
    RunVmCommand(vm, vm.ActivateCommand);
}
/// <summary>跑一次 ffmpeg（测试里用来生成片源），退出码非零直接失败。</summary>
static void RunFfmpeg(string ffmpeg, string[] arguments)
{
    System.Diagnostics.ProcessStartInfo start = new(ffmpeg)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
    };
    foreach (string argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)
        ?? throw new InvalidOperationException("无法启动 ffmpeg");
    string stderr = process.StandardError.ReadToEnd();
    process.StandardOutput.ReadToEnd();
    if (!process.WaitForExit(60_000))
    {
        process.Kill(entireProcessTree: true);
        throw new TimeoutException("ffmpeg 超时（60s）");
    }

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"ffmpeg 退出码 {process.ExitCode}：{stderr}");
    }
}
