using System.Text.RegularExpressions;
using PakTool.Core;
using Prism.Desktop.Services.Widgets;
using UAssetTexture.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using UAssetAPI.UnrealTypes;

// Avalonia.Controls 里也有 Image、Avalonia 里也有 Size，这里只用到 ImageSharp 的泛型 Image<Rgba32>，
// 起个别名避免歧义（SaveAsPng 这类扩展方法则显式走静态调用）。
using SharpImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

// 截图模式必须在任何 Avalonia 初始化之前分流：常规测试用 UseHeadlessDrawing = true
// （不绘制），截图要挂 Skia 真正光栅化，两者不能在同一进程共存。
if (args.Length > 0 && args[0] == "--capture")
{
    bool docsOnly = args.Contains("--docs", StringComparer.OrdinalIgnoreCase);
    return Prism.FeatureTests.UiCapture.Run(
        args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "ui-capture"),
        docsOnly);
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

    // 依赖探测绝不能抛异常：安卓上它会从 app 私有目录往上找仓库根，走到 /data/user/0
    // 时枚举被拒（UnauthorizedAccessException），而这在 MainViewModel 构造里跑 ——
    // 1.3.3 / 1.3.4 的安卓版"点开就闪退"就是它。这里守住"不抛"这条底线。
    try
    {
        Prism.Desktop.Services.UAssetCliRunner.TryCreate();
        Check(true, "依赖探测不会抛异常（安卓启动闪退的根因）");
    }
    catch (Exception ex)
    {
        Check(false, $"依赖探测不应抛异常：{ex.GetType().Name}: {ex.Message}");
    }

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
    string encoderKind = UAssetAudio.Core.BinkaEncoder.DescribeEncoder(toolsDir);
    Console.WriteLine($"        音频 fixture={hasAudioFixture} 映射={audioUsmap is not null} 编码器={encoderKind}");

    if (!hasAudioFixture || audioUsmap is null || !hasBinkaEncoder)
    {
        Console.WriteLine("        跳过音频替换断言（缺少 fixture / 映射文件 / 编码器）");
    }
    else
    {
        // 手机端没有 binka_encode.exe，音频替换必须靠进程内的原生编码器
        Check(encoderKind == "内置编码器", $"音频编码走进程内原生库（{encoderKind}）");

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

        // 手机 SAF 选出来的素材常常没有扩展名（缓存文件名就是一串哈希）。格式判断必须看文件头，
        // 否则会把 WAV 当成"未知格式"、再误报「需要 ffmpeg」—— 这是手机端音频替换踩过的坑。
        {
            string weirdDir = Path.Combine(root, "audio-extensionless");
            Directory.CreateDirectory(weirdDir);
            string weirdPath = Path.Combine(weirdDir, "0f3c9e5a7b1d4c8e9a6b2f5d3c1e8a7b");
            File.Copy(audioStem + ".wav", weirdPath, true);

            Check(UAssetAudio.Core.AudioSourceDecoder.DetectContainer(weirdPath) == UAssetAudio.Core.AudioContainer.Wav,
                "没有扩展名的 WAV 也能按文件头认出容器");
            Check(UAssetAudio.Core.AudioSourceDecoder.SuggestExtension(weirdPath) == ".wav",
                "能按文件头给它补出 .wav 扩展名");

            var fromWeirdName = UAssetAudio.Core.AudioSourceDecoder.Decode(weirdPath, toolsDir);
            Check(fromWeirdName.SampleRate == fixturePcm.SampleRate && fromWeirdName.FrameCount == fixturePcm.FrameCount,
                "没有扩展名的素材仍能正常解码（手机端那条路）");
        }
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

        // UE 5.4+ 的 SoundWave 可能在 flags 后面带一段 cue point（每个带一个 FString 标签）。
        // 游戏里的 AU_Mus_* / Main_menu_Play 就是这样；以前这段会被当成块记录，
        // 报「SoundWave 的数据块数量异常（842080314）」这种完全看不懂的错误。
        {
            byte[] cueLabel = System.Text.Encoding.ASCII.GetBytes("Tempo: 120.0\0");
            var cueRegion = new byte[7 + 4 + cueLabel.Length];
            new byte[] { 0x80, 0x0b, 0x1c, 0x01, 0, 0, 0 }.CopyTo(cueRegion, 0); // 这部分内部结构未知，原样搬运
            BitConverter.GetBytes(cueLabel.Length).CopyTo(cueRegion, 7);
            cueLabel.CopyTo(cueRegion, 11);

            var cueUsmap = new UAssetAPI.Unversioned.Usmap(audioUsmap);
            var cueAsset = new UAssetAPI.UAsset(audioStem + ".uasset", EngineVersion.VER_UE5_6, cueUsmap);
            UAssetAPI.ExportTypes.NormalExport cueExport = cueAsset.Exports
                .OfType<UAssetAPI.ExportTypes.NormalExport>()
                .First(candidate => candidate.Extras is { Length: > 0 });
            byte[] plainExtras = cueExport.Extras;
            var withCue = new List<byte>();
            withCue.AddRange(plainExtras.AsSpan(0, 4).ToArray());   // flags
            withCue.AddRange(BitConverter.GetBytes(1));            // cue point 数量
            withCue.AddRange(cueRegion);
            withCue.AddRange(plainExtras.AsSpan(8).ToArray());     // guid + 块记录原样
            cueExport.Extras = withCue.ToArray();

            string cueDir = Path.Combine(root, "audio-cue");
            Directory.CreateDirectory(cueDir);
            string cueStem = Path.Combine(cueDir, "AU_Cue");
            cueAsset.Write(cueStem + ".uasset");
            File.Copy(audioStem + ".ubulk", cueStem + ".ubulk", overwrite: true);

            UAssetAudio.Core.SoundWaveInfo cueInfo =
                UAssetAudio.Core.SoundWavePatcher.Inspect(cueStem + ".uasset", audioUsmap);
            Check(cueInfo.CuePointCount == 1, $"能认出带 cue point 的资产（{cueInfo.CuePointCount} 个）");
            Check(cueInfo.CanReplace, $"带 cue point 的资产也可以替换（{cueInfo.Problem ?? "无阻碍"}）");

            UAssetAudio.Core.SoundWaveReplacement cueReplacement =
                UAssetAudio.Core.SoundWavePatcher.Replace(cueStem + ".uasset", binkaBytes, audioUsmap);
            string cuePatchedStem = Path.Combine(cueDir, "AU_CuePatched");
            File.WriteAllBytes(cuePatchedStem + ".uasset", cueReplacement.Uasset);
            File.WriteAllBytes(cuePatchedStem + ".uexp", cueReplacement.Uexp);
            File.WriteAllBytes(cuePatchedStem + ".ubulk", cueReplacement.Ubulk);

            UAssetAudio.Core.SoundWaveInfo cueAfter =
                UAssetAudio.Core.SoundWavePatcher.Inspect(cuePatchedStem + ".uasset", audioUsmap);
            Check(cueAfter.CuePointCount == 1, "替换后 cue point 数量不变");
            Check(cueAfter.CanReplace && cueAfter.ChunkCount == cueInfo.ChunkCount,
                $"替换后布局仍能解析，块数不变（{cueAfter.ChunkCount} 块）");

            var patchedCueAsset = new UAssetAPI.UAsset(cuePatchedStem + ".uasset", EngineVersion.VER_UE5_6, cueUsmap);
            byte[] patchedCueExtras = patchedCueAsset.Exports
                .OfType<UAssetAPI.ExportTypes.NormalExport>()
                .First(candidate => candidate.Extras is { Length: > 0 })
                .Extras;
            Check(patchedCueExtras.AsSpan(8, cueRegion.Length).SequenceEqual(cueRegion),
                "cue point 区块逐字节原样保留");
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

        // 「使用说明」从浮层改成了独立页面：章节由 Docs/PrismDocs 提供，
        // 页头的「使用说明」按钮带着当前页的主题跳进去（见 OpenDocs 命令）。
        Check(!vm.IsDocsVisible, "「使用说明」页面默认不显示");
        Check(vm.DocChapters.Count >= 8, $"使用说明有 {vm.DocChapters.Count} 个章节");
        Check(vm.DocChapters.All(chapter => chapter.Blocks.Count > 0), "每个章节都有内容（不会点开是空的）");
        Check(vm.DocChapters.Select(chapter => chapter.Key).Distinct().Count() == vm.DocChapters.Count,
            "章节 Key 唯一（页头跳转按 Key 定位）");
        Check(vm.SelectedChapter is not null, "启动时就选中了第一章（绑定在进页面前就求值）");

        vm.OpenDocsCommand.Execute("convert");
        Check(vm.IsDocsVisible && vm.SelectedChapter?.Key == "convert", "OpenDocs(\"convert\") 跳进转换章节");
        Check(vm.DocBlocks.Count > 0, "当前章节的正文块可枚举");
        int convertChapterIndex = vm.DocChapters.ToList().FindIndex(chapter => chapter.Key == "convert");
        Check(vm.DocChapterNumber == (convertChapterIndex + 1).ToString("D2"),
            $"章号按目录序号给出（{vm.DocChapterNumber}，位于第 {convertChapterIndex + 1} 章）");

        vm.GoNextDocCommand.Execute(null);
        Check(vm.SelectedChapter?.Key == "settings", "「下一章」走到更新之后的设置章节");
        vm.GoPreviousDocCommand.Execute(null);
        Check(vm.SelectedChapter?.Key == "convert", "「上一章」退回转换章节");

        // 文档里的每张截图都必须真有资源（缺图时页面只剩图注，读者会以为功能没了）。
        // 直接查源码树：文档截图是构建期打进程序的 avares 资源，磁盘上有就一定编进去了。
        string? assetRepoRoot = null;
        DirectoryInfo? assetProbe = new(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && assetProbe is not null; i++, assetProbe = assetProbe.Parent)
        {
            if (Directory.Exists(Path.Combine(assetProbe.FullName, "Prism.Desktop", "Assets")))
            {
                assetRepoRoot = assetProbe.FullName;
                break;
            }
        }

        Check(assetRepoRoot is not null, "能找到仓库根以校验文档截图");
        var figureKeys = Prism.Desktop.Docs.PrismDocs.Chapters
            .SelectMany(chapter => chapter.Blocks)
            .OfType<Prism.Desktop.Docs.DocFigure>()
            .Select(figure => figure.ImageKey)
            .Distinct()
            .ToList();
        var missingFigures = assetRepoRoot is null
            ? []
            : figureKeys
                .Where(key => !new[] { "light", "dark" }.All(theme => File.Exists(
                    Path.Combine(assetRepoRoot, "Prism.Desktop", "Assets", "help", theme, key + ".png"))))
                .ToList();
        Check(figureKeys.Count >= 6, $"文档引用了 {figureKeys.Count} 张截图");
        Check(missingFigures.Count == 0,
            missingFigures.Count == 0
                ? "文档截图 light / dark 两套都齐全"
                : $"缺少文档截图：{string.Join(", ", missingFigures)}");

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

        // 返回上一级时：选中"刚离开的那个目录"，并请求把它滚到可见处；
        // 进入目录时：请求回到列表顶部（否则会停在上一个目录的滚动位置）。
        if (File.Exists(mediaPak))
        {
            var scrollVm = new Prism.Desktop.ViewModels.MainViewModel { PakPath = mediaPak };
            var scrollRequests = new List<Prism.Desktop.Models.EntryItem?>();
            scrollVm.ScrollToEntryRequested += item => scrollRequests.Add(item);

            RunVmCommand(scrollVm, scrollVm.OpenPakCommand);
            EnterFolder(scrollVm, "Kards");
            EnterFolder(scrollVm, "Content");
            EnterFolder(scrollVm, "Movies");
            Prism.FeatureTests.UiFixture.RunJobs();
            scrollRequests.Clear();

            RunVmCommand(scrollVm, scrollVm.UpCommand);
            Prism.FeatureTests.UiFixture.RunJobs();
            Check(scrollVm.SelectedItem?.Name == "Movies", $"回上一级会选中刚离开的目录（{scrollVm.SelectedItem?.Name}）");
            Check(scrollRequests.Count == 1 && scrollRequests[0]?.Name == "Movies",
                $"并请求把该项滚到可见处（{scrollRequests.Count} 次请求）");

            scrollRequests.Clear();
            EnterFolder(scrollVm, "Movies");
            Prism.FeatureTests.UiFixture.RunJobs();
            Check(scrollRequests.Count == 1 && scrollRequests[0] is null,
                $"进入目录时请求回到列表顶部（{scrollRequests.Count} 次请求）");
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

        // 本地化：.locres 是裸文件（不是 .uasset 包）。「加入替换」必须走翻译编辑器那条路，
        // 不能被"非 uasset 就当原始文件替换"提前吃掉 —— 那样界面上就只剩"直接替换字节"。
        if (File.Exists(locresPak))
        {
            var locresVm = new Prism.Desktop.ViewModels.MainViewModel { PakPath = locresPak };
            RunVmCommand(locresVm, locresVm.OpenPakCommand);
            EnterFolder(locresVm, "Kards");
            EnterFolder(locresVm, "Content");
            EnterFolder(locresVm, "Config");

            Prism.Desktop.Models.EntryItem? locresEntry = locresVm.Entries
                .FirstOrDefault(entry => !entry.IsDirectory && entry.Name.EndsWith(".locres", StringComparison.OrdinalIgnoreCase));
            Check(locresEntry is not null, "locres 文件出现在列表里");

            locresVm.SelectedItem = locresEntry;
            RunVmCommand(locresVm, locresVm.AddSelectedToPatchCommand);

            Prism.Desktop.Models.PatchItem? locresPatch = locresVm.PatchItems.FirstOrDefault();
            Check(locresPatch?.IsLocres == true, $"裸 .locres「加入替换」后被识别为本地化项（kind={locresPatch?.Kind}）");
            Check(locresPatch?.LocresEntries.Count == 2, $"词条已载入翻译编辑器（{locresPatch?.LocresEntries.Count} 条）");
            Check(locresPatch?.Format == "LOCRES", $"本地化项的标签是 LOCRES（{locresPatch?.Format}）");

            // 编辑一条再写回：走的是 LocresResourceCodec，不经过"原始文件替换"
            locresPatch!.LocresEntries[0].Text = "改过的译文";
            RunVmCommand(locresVm, locresVm.UpdateLocresEntryCommand, locresPatch);
            Check(locresPatch.Status == "已编辑", $"编辑后状态变成已编辑（{locresPatch.Status}）");
            Check(locresPatch.PatchedFiles.Count == 1, $"本地化写回只产出一个文件（{locresPatch.PatchedFiles.Count}）");
        }
        else
        {
            Console.WriteLine("        跳过本地化流程断言（locres.pak 未生成）");
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

    Section("纹理 · 未压缩格式（PF_B8G8R8A8 这类）");

    // 真实事故（用户截图）：KARDS 的 T_standard_ammo 是 PF_B8G8R8A8、2090x2311 的单级内联贴图。
    // 桌面端走 UAssetCLI 子进程替换，而 CLI 自带的那份格式表里没有未压缩格式，
    // 于是在 DetectFormat 里抛 "Could not detect a supported pixel format from the asset."。
    // 修法是删掉 CLI 的副本、直接引用 UAssetTexture.Core，并补上未压缩格式的编码。
    byte[] bgraExport = System.Text.Encoding.ASCII.GetBytes("...PF_B8G8R8A8\u0000....");
    TextureFormatInfo bgraFormat = TextureFormats.DetectFormat(bgraExport, []);
    Check(bgraFormat.Name == "PF_B8G8R8A8" && bgraFormat.IsUncompressed8BitColor,
        $"从资产字节里认出未压缩格式（{bgraFormat.Name}）");

    // 2090x2311 是那个资产的真实尺寸；未压缩格式按 1x1 像素块算，不是 4x4。
    Check(bgraFormat.GetMipByteSize(2090, 2311) == 19_319_960,
        "PF_B8G8R8A8 的 mip 字节数按 1x1 像素块算（2090x2311 → 19,319,960）");

    // 通道顺序必须真的换位，否则替换出来的贴图会红蓝互换。
    using (SharpImage probeImage = new(2, 1))
    {
        probeImage[0, 0] = new SixLabors.ImageSharp.PixelFormats.Rgba32(10, 20, 30, 40);
        probeImage[1, 0] = new SixLabors.ImageSharp.PixelFormats.Rgba32(50, 60, 70, 80);

        byte[] bgraBytes = TexturePixelEncoder.EncodeUncompressed(probeImage, bgraFormat);
        Check(bgraBytes.SequenceEqual(new byte[] { 30, 20, 10, 40, 70, 60, 50, 80 }),
            "PF_B8G8R8A8 编码把 RGBA 换成 BGRA 字节序");

        byte[] rgbaBytes = TexturePixelEncoder.EncodeUncompressed(
            probeImage, new TextureFormatInfo("PF_R8G8B8A8", 1, 1, 4));
        Check(rgbaBytes.SequenceEqual(new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 }),
            "PF_R8G8B8A8 编码保持 RGBA 原序");
    }

    Section("纹理解析单一实现（UAssetCLI 直接引用 UAssetTexture.Core）");

    // 历史包袱：CLI 是 AOT 单文件，原先为了不拖进 CUE4Parse，自带一份 TextureAssetParser /
    // TextureFormats / TextureModels 副本。两份实现之间没有任何编译期约束：2600b0d
    // （尺寸不能写死在固定偏移）就是同一个 bug 在两边各修一遍；这次 PF_B8G8R8A8
    // 只在 Core 里补过、CLI 里漏掉，直接把"这张贴图能替换"打断了。
    //
    // 现在 UAssetTexture.Core 只依赖 UAssetAPI + ImageSharp（pak 相关逻辑拆到
    // UAssetTexture.Pak），CLI 直接引用它 —— 下面既比对"CLI 与 Core 看到的布局一致"，
    // 也真的让 CLI 替换一遍，确认它不只是会打印信息。
    string? cliPath = FindUAssetCli(realAssetDir);
    Console.WriteLine($"        UAssetCLI 产物: {cliPath ?? "未找到"}");
    if (!hasRealTexture || cliPath is null)
    {
        Console.WriteLine("        !! 缺真实资产或 UAssetCLI 产物，跳过 CLI 链路断言");
    }
    else
    {
        TextureAssetInfo coreTexture = TextureAssetParser.Load(realUasset, EngineVersion.VER_UE5_6, null);
        string cliInspect = RunCliInspectTexture(cliPath, realUasset, "VER_UE5_6");

        string cliFormat = FieldOf(cliInspect, "Format");
        Check(string.Equals(coreTexture.Format.Name, cliFormat, StringComparison.OrdinalIgnoreCase),
            $"像素格式一致（Core={coreTexture.Format.Name} / CLI={cliFormat}）");

        string cliSize = FieldOf(cliInspect, "Size");
        Check(cliSize == $"{coreTexture.Width}x{coreTexture.Height}",
            $"纹理尺寸一致（Core={coreTexture.Width}x{coreTexture.Height} / CLI={cliSize}）");

        int cliExternalMips = int.Parse(FieldOf(cliInspect, "External mips"));
        Check(coreTexture.ExternalMipCount == cliExternalMips,
            $"外部 mip（.ubulk）数量一致（Core={coreTexture.ExternalMipCount} / CLI={cliExternalMips}）");

        int cliInlineMips = int.Parse(FieldOf(cliInspect, "Inline mips"));
        Check(coreTexture.InlineMips.Count == cliInlineMips,
            $"内联 mip（.uexp）数量一致（Core={coreTexture.InlineMips.Count} / CLI={cliInlineMips}）");

        List<(int Index, int Width, int Height, int ByteLength, string Storage, int Offset)> cliMips =
            ParseMipLines(cliInspect);
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

        // CLI 真的替换一遍：t_cromwell 是 PF_DXT1，走 CLI 内置的 BC1 编码器，
        // 不需要 astcenc / texconv —— 正好覆盖"没有外部编码器也能替换"的路径。
        string cliWork = Path.Combine(root, "cli-replace");
        Directory.CreateDirectory(cliWork);
        string cliAsset = Path.Combine(cliWork, "t_cromwell.uasset");
        File.Copy(realUasset, cliAsset, true);
        File.Copy(realUexp, Path.ChangeExtension(cliAsset, ".uexp"), true);
        File.Copy(realUbulk, Path.ChangeExtension(cliAsset, ".ubulk"), true);

        string cliSourceImage = Path.Combine(cliWork, "source.png");
        using (SharpImage sourceImage = new(64, 64))
        {
            sourceImage.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    Span<SixLabors.ImageSharp.PixelFormats.Rgba32> row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        row[x] = new SixLabors.ImageSharp.PixelFormats.Rgba32((byte)(x * 4), (byte)(y * 4), 128, 255);
                    }
                }
            });
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(sourceImage, cliSourceImage);        }

        string cliPatchedAsset = Path.Combine(cliWork, "t_cromwell.patched.uasset");
        (int ExitCode, string StandardOutput, string StandardError) replaceRun = RunUAssetCli(
            cliPath,
            [
                "replace-texture",
                "--asset", cliAsset,
                "--source", cliSourceImage,
                "--output", cliPatchedAsset,
                "--engine", "VER_UE5_6",
                "--expected-format", coreTexture.Format.Name,
            ],
            300_000);

        Check(replaceRun.ExitCode == 0,
            $"UAssetCLI 替换退出码 0（实际 {replaceRun.ExitCode}{(replaceRun.ExitCode == 0 ? string.Empty : "：" + replaceRun.StandardError.Trim())}）");

        string cliPatchedUexp = Path.ChangeExtension(cliPatchedAsset, ".uexp");
        string cliPatchedUbulk = Path.ChangeExtension(cliPatchedAsset, ".ubulk");
        Check(File.Exists(cliPatchedAsset) && File.Exists(cliPatchedUexp) && File.Exists(cliPatchedUbulk),
            "UAssetCLI 替换写出 uasset/uexp/ubulk 三件套");

        if (File.Exists(cliPatchedUexp))
        {
            Check(new FileInfo(cliPatchedUexp).Length == new FileInfo(realUexp).Length,
                "UAssetCLI 替换后 uexp 长度不变（内联 mip 没有挪位）");

            TextureAssetInfo cliPatched = TextureAssetParser.Load(cliPatchedAsset, EngineVersion.VER_UE5_6, null);
            Check(cliPatched.MipPlacements.Count == coreTexture.MipPlacements.Count,
                "UAssetCLI 替换结果仍能被 Core 解析出同样的 mip 数");

            // 编译后至少有一级 mip 的字节变了，否则说明编码结果压根没写进去。
            byte[] patchedMip0 = File.ReadAllBytes(cliPatchedUbulk).AsSpan(0, coreTexture.Mips[0].ByteLength).ToArray();
            byte[] originalMip0 = File.ReadAllBytes(realUbulk).AsSpan(0, coreTexture.Mips[0].ByteLength).ToArray();
            Check(!patchedMip0.SequenceEqual(originalMip0), "UAssetCLI 真的重编码了 mip0（载荷与原始不同）");
        }
    }

    // ══════════════════════════════════════════════════════════════
    Section("F13 · 导出当前文件夹（视图模型 → 真实 pak）");

    string folderExportOut = Path.Combine(root, "folder-export-root");
    Directory.CreateDirectory(folderExportOut);
    var folderVm = new Prism.Desktop.ViewModels.MainViewModel
    {
        PakPath = mainPak,
        ExportDirectory = folderExportOut,
    };

    RunVmCommand(folderVm, folderVm.OpenPakCommand);
    Check(!folderVm.ShowPakEmptyState, "视图模型打开了 Pak（导出命令的前提）");
    Check(folderVm.ExportFolderRawCommand.CanExecute(null), "打开 Pak 后「导出当前文件夹」可执行");

    // 根目录（CurrentFolder 为空）：应当递归导出全部文件，并保留包内相对目录
    RunVmCommand(folderVm, folderVm.ExportFolderRawCommand);
    string[] rootExport = Directory.GetFiles(folderExportOut, "*", SearchOption.AllDirectories);
    Check(rootExport.Length >= 4, $"根目录递归导出写出 {rootExport.Length} 个文件");
    Check(rootExport.Any(p => p.Replace('\\', '/').EndsWith("Kards/Content/Config/Game.ini", StringComparison.OrdinalIgnoreCase)),
        "导出保留了 Pak 内的相对目录结构");
    string exportedIni = rootExport.FirstOrDefault(p =>
        p.Replace('\\', '/').EndsWith("Kards/Content/Config/Game.ini", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    Check(exportedIni.Length > 0 && File.ReadAllText(exportedIni) == "MAIN-CONFIG",
        "导出的文件内容与 Pak 内一致");
    Check(folderVm.StatusText.Contains("文件夹导出完成", StringComparison.Ordinal),
        $"状态栏给出结果摘要（{folderVm.StatusText}）");

    // 子目录：只导出该目录下的文件，且不再多套一层目录
    string folderExportSub = Path.Combine(root, "folder-export-sub");
    Directory.CreateDirectory(folderExportSub);
    folderVm.ExportDirectory = folderExportSub;
    folderVm.CurrentFolder = "Kards/Content/Config";
    RunVmCommand(folderVm, folderVm.ExportFolderRawCommand);
    string[] subExport = Directory.GetFiles(folderExportSub, "*", SearchOption.AllDirectories);
    Check(subExport.Length == 2, $"子目录导出只写出该目录的 2 个文件（实际 {subExport.Length}）");
    Check(subExport.All(p => Path.GetFileName(p) is "Game.ini" or "MainOnly.txt"),
        "子目录导出不会把别的目录一起带上");
    Check(subExport.All(p => Path.GetDirectoryName(p) == folderExportSub),
        "子目录导出的文件直接落在目标目录里（不多套一层 Kards/Content/Config）");

    // 没设导出目录时必须是「有反馈的失败」，而不是静默什么都不做
    string? promptedDir = null;
    var unsetVm = new Prism.Desktop.ViewModels.MainViewModel
    {
        PakPath = mainPak,
    };
    unsetVm.ExportDirectory = string.Empty;
    unsetVm.ExportFolderPromptOverride = () =>
    {
        promptedDir = Path.Combine(root, "folder-export-prompted");
        Directory.CreateDirectory(promptedDir);
        return Task.FromResult<string?>(promptedDir);
    };
    RunVmCommand(unsetVm, unsetVm.OpenPakCommand);
    RunVmCommand(unsetVm, unsetVm.ExportFolderRawCommand);
    Check(promptedDir is not null, "未设置导出目录时会即时询问一个目录（而不是只写一行状态）");
    Check(Directory.Exists(promptedDir ?? string.Empty) &&
          Directory.GetFiles(promptedDir!, "*", SearchOption.AllDirectories).Length > 0,
        "询问到的目录里确实写出了文件");
    Check(unsetVm.ExportDirectory == promptedDir, "选定后记进导出目录设置（下次不用再问）");

    // ══════════════════════════════════════════════════════════════
    Section("F12 · 检查更新（发行版解析 / 版本比较 / 自更新脚本）");

    // ── 发行版 JSON 解析：字段缺失或 API 改版时不能把界面搞崩 ──
    const string releaseJson = """
        {
          "tag_name": "v1.3.0",
          "name": "界面细化与自动更新",
          "body": "## 界面\n- 设置页重做\n- **修好**窗口化发虚\n",
          "html_url": "https://github.com/CCB-TEAM/Prism/releases/tag/v1.3.0",
          "published_at": "2026-02-11T08:30:00Z",
          "assets": [
            { "name": "Prism-win-x64-1.3.0.zip", "size": 124724173,
              "browser_download_url": "https://example.invalid/a.zip",
              "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" },
            { "name": "Prism-android-1.3.0.apk", "size": 94284432,
              "browser_download_url": "https://example.invalid/a.apk" }
          ]
        }
        """;

    Prism.Desktop.Services.UpdateInfo? parsed = Prism.Desktop.Services.UpdateService.ParseRelease(releaseJson);
    Check(parsed is not null, "发行版 JSON 能解析");
    Check(parsed?.Version == "1.3.0", $"tag 归一化 v1.3.0 → {parsed?.Version}");
    Check(parsed?.Assets.Count == 2, "资产列表解析出 2 个文件");
    Check(parsed?.Assets[0].Sha256 is { Length: 64 }, "digest 的 sha256 被取出（下载后可校验）");
    Check(parsed?.Assets[1].Sha256 is null, "没有 digest 的资产不硬编校验值");
    Check(parsed?.PublishedText == DateTimeOffset.Parse("2026-02-11T08:30:00Z").ToLocalTime().ToString("yyyy-MM-dd"),
        $"发布日期按本地时区格式化（{parsed?.PublishedText}）");
    Check(parsed?.NoteBlocks.Count >= 3, $"正文解析出 {parsed?.NoteBlocks.Count} 块");

    Check(Prism.Desktop.Services.UpdateService.ParseRelease("{}") is null, "缺 tag_name 的 JSON 返回 null（不抛）");
    Check(Prism.Desktop.Services.UpdateService.NormalizeVersion("V1.2.3") == "1.2.3", "大写 V 前缀也能归一化");
    Check(Prism.Desktop.Services.UpdateService.NormalizeVersion("1.2.3") == "1.2.3", "没有前缀时原样返回");

    // ── 版本比较：等于 / 小于都不算更新，解析不了按"有更新"处理 ──
    var v123 = new Version(1, 2, 3);
    Check(Prism.Desktop.Services.UpdateService.IsNewer("1.3.0", v123), "1.3.0 > 1.2.3 → 有更新");
    Check(!Prism.Desktop.Services.UpdateService.IsNewer("1.2.3", v123), "同版本不算更新");
    Check(!Prism.Desktop.Services.UpdateService.IsNewer("1.2.2", v123), "旧版本不算更新");
    Check(Prism.Desktop.Services.UpdateService.IsNewer("1.2.10", v123), "字符串比较会错的 1.2.10 > 1.2.3（按数值比）");
    Check(Prism.Desktop.Services.UpdateService.IsNewer("abc", v123), "版本号看不懂时按有更新处理（不静默）");
    Check(Prism.Desktop.Services.UpdateService.IsNewer("1.0.0", null), "当前版本读不出来时按有更新处理");

    // ── 资产选择：Windows 挑 win 的 zip ──
    Prism.Desktop.Services.UpdateAsset? chosenAsset =
        parsed is null ? null : Prism.Desktop.Services.UpdateService.SelectAsset(parsed);
    Check(chosenAsset?.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) == true,
        $"桌面端挑到 Windows 包（{chosenAsset?.Name}）");

    // ── 发行说明解析：标题 / 列表 / 加粗 / 图片 / 链接 ──
    IReadOnlyList<Prism.Desktop.Services.UpdateNote> notes = Prism.Desktop.Services.UpdateNotes.Parse(
        """
        # 大标题
        普通一行
        - 列表项 **加粗**
        - `code` 与 [链接](https://example.invalid)
        ![截图](https://example.invalid/x.png)
        ---
        """);
    Check(notes.Count == 4, $"解析出 4 块内容（标题/段落/2 条列表，图片与分隔线被丢掉）：{notes.Count}");
    Check(notes[0].IsHeading, "第一块是标题");
    Check(notes[1].Kind == Prism.Desktop.Services.UpdateNoteKind.Paragraph, "第二块是段落");
    Check(notes[2].IsBullet, "第三块是列表项");
    Check(notes[2].Runs.Any(run => run.Bold && run.Text == "加粗"), "列表里的 **加粗** 单独成一个加粗片段");
    Check(notes[3].Runs.Any(run => run.Code && run.Text == "code"), "行内代码被识别");
    Check(notes[3].Runs.Any(run => run.Link && run.Text == "链接"), "链接只保留文字（不当作 UI 元素）");
    Check(!notes.Any(note => note.PlainText.Contains("example.invalid", StringComparison.Ordinal)),
        "图片与链接的 URL 不会漏进正文");
    Check(Prism.Desktop.Services.UpdateNotes.Parse("", 10).Count == 0, "空正文解析出 0 块");
    Check(Prism.Desktop.Services.UpdateNotes.Parse("- a\n- b\n- c", 2).Count == 2, "maxBlocks 生效（欢迎页只展示前几块）");

    // ── 自更新脚本：真的跑一遍"等进程退出 → 覆盖 → 不重启"的路径 ──
    string updateWork = Path.Combine(root, "self-update");
    string payloadDir = Path.Combine(updateWork, "payload");
    string targetDir = Path.Combine(updateWork, "target");
    Directory.CreateDirectory(Path.Combine(payloadDir, "tools"));
    Directory.CreateDirectory(targetDir);
    File.WriteAllText(Path.Combine(payloadDir, "Prism.Desktop.Desktop.exe"), "NEW-BUILD");
    File.WriteAllText(Path.Combine(payloadDir, "tools", "astcenc-avx2.exe"), "NEW-TOOL");
    File.WriteAllText(Path.Combine(targetDir, "Prism.Desktop.Desktop.exe"), "OLD-BUILD");
    File.WriteAllText(Path.Combine(targetDir, "Prism.Desktop.settings.json"), "{ \"theme\": \"dark\" }");

    string applierScript = Prism.Desktop.Services.UpdateInstaller.BuildApplierScript(
        payloadDir,
        targetDir,
        "Prism.Desktop.Desktop.exe",
        // 一个几乎不可能存在的 PID：脚本的等待循环会立刻放行
        processId: 999_997,
        restart: false);

    Check(applierScript.Contains("robocopy", StringComparison.Ordinal), "脚本用 robocopy 覆盖");
    Check(applierScript.Contains("tasklist", StringComparison.Ordinal), "脚本会等本进程退出");
    Check(applierScript.Contains(payloadDir, StringComparison.OrdinalIgnoreCase), "脚本引用了解压目录");
    Check(applierScript.Contains(targetDir, StringComparison.OrdinalIgnoreCase), "脚本引用了应用目录");
    Check(applierScript.Contains("999997", StringComparison.Ordinal), "脚本等待的是真实的 PID");
    Check(!applierScript.Contains("start \"\"", StringComparison.Ordinal), "restart=false 时不自动再拉起应用");

    string applierPath = Path.Combine(updateWork, "apply-update.cmd");
    File.WriteAllText(applierPath, applierScript);
    var applierRun = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "cmd.exe",
        Arguments = $"/c \"\"{applierPath}\"\"",
        WorkingDirectory = updateWork,
        UseShellExecute = false,
        CreateNoWindow = true,
    });
    applierRun?.WaitForExit(60_000);

    Check(File.ReadAllText(Path.Combine(targetDir, "Prism.Desktop.Desktop.exe")) == "NEW-BUILD",
        "更新脚本真的把主程序换成了新版本");
    Check(File.Exists(Path.Combine(targetDir, "tools", "astcenc-avx2.exe")), "新版本新增的文件也被复制过去");
    Check(File.Exists(Path.Combine(targetDir, "Prism.Desktop.Desktop.exe.bak")), "旧主程序留了一份 .bak");
    Check(File.Exists(Path.Combine(targetDir, "Prism.Desktop.settings.json")), "用户设置文件不会被删除");
    Check(!File.Exists(applierPath), "脚本跑完自删");

    // ── 文档页：截图资源与更新面板的状态机 ──
    var previewVm = new Prism.Desktop.ViewModels.MainViewModel();
    Check(!previewVm.ShowUpdatePanel, "没有新版本时不显示更新面板");
    previewVm.ShowUpdatePreview(
        "9.9.9",
        "测试版本",
        "## 标题\n- 一条\n",
        assetName: "Prism-win-x64-9.9.9.zip",
        assetSize: 1024);
    Check(previewVm.HasUpdate && previewVm.ShowUpdatePanel, "注入发行版后欢迎页切成更新内容");
    Check(previewVm.UpdateIntroText.Contains(previewVm.VersionText, StringComparison.Ordinal),
        "更新面板说明了从哪个版本升级");
    Check(previewVm.CanInstallUpdate, "有安装包时「下载并自动更新」可用");
    Check(previewVm.HasInstallTarget, "下载所需状态齐全（发行版信息 + 安装包都在，点了不会再报「没有可下载的安装包」）");
    // ── 自动查到更新时，启动那一屏就该是欢迎页（默认落点是「解包 & 模组」） ──
    var autoInfo = new Prism.Desktop.Services.UpdateInfo(
        "v9.9.8", "9.9.8", "测试发行版", "## 更新\n- 一条\n", DateTimeOffset.Now,
        "https://example.invalid", [new Prism.Desktop.Services.UpdateAsset("Prism-win-x64-9.9.8.zip", 1024, "https://example.invalid/a.zip", null)]);

    var landingVm = new Prism.Desktop.ViewModels.MainViewModel();
    landingVm.GoWorkspaceCommand.Execute(null);
    Check(landingVm.IsWorkspaceVisible, "默认落点是「解包 & 模组」");
    landingVm.ApplyUpdate(autoInfo, manual: false);
    Check(landingVm.IsHomeVisible, "自动查到新版本时切到欢迎页（否则用户根本看不到更新说明）");
    Check(landingVm.ShowUpdatePanel, "欢迎页此时显示的就是更新内容");

    var manualVm = new Prism.Desktop.ViewModels.MainViewModel();
    manualVm.GoWorkspaceCommand.Execute(null);
    manualVm.ApplyUpdate(autoInfo, manual: true);
    Check(manualVm.IsWorkspaceVisible, "手动检查时留在当前页（结果就显示在设置页里，不该把用户拽走）");

    var busyVm = new Prism.Desktop.ViewModels.MainViewModel();
    busyVm.GoWorkspaceCommand.Execute(null);
    busyVm.IsBusy = true;
    busyVm.ApplyUpdate(autoInfo, manual: false);
    Check(busyVm.IsWorkspaceVisible, "正在忙的时候不打断（不跳页）");

    var pakVm = new Prism.Desktop.ViewModels.MainViewModel { PakPath = mainPak };
    pakVm.GoWorkspaceCommand.Execute(null);
    // 真的打开（而不是只设了路径）：恢复的路径不算"在干活"，打开后的会话才算
    RunVmCommand(pakVm, pakVm.OpenPakCommand);
    pakVm.ApplyUpdate(autoInfo, manual: false);
    Check(pakVm.IsWorkspaceVisible, $"已经打开了 Pak 时不跳走（当前：{(pakVm.IsHomeVisible ? "欢迎" : "工作区")}）");

    var dismissedVm = new Prism.Desktop.ViewModels.MainViewModel { DismissedUpdateVersion = "9.9.8" };
    dismissedVm.GoWorkspaceCommand.Execute(null);
    dismissedVm.ApplyUpdate(autoInfo, manual: false);
    Check(dismissedVm.IsWorkspaceVisible && !dismissedVm.ShowUpdatePanel, "点过「稍后」的版本不会再把人带到欢迎页");

    previewVm.DismissUpdateCommand.Execute(null);
    Check(!previewVm.ShowUpdatePanel, "点「稍后」后回到正常欢迎页");
    Check(previewVm.DismissedUpdateVersion == "9.9.9", "「稍后」记住的是这个版本号（更高版本还会再提示）");
    previewVm.HideUpdatePreview();
    Check(!previewVm.HasUpdate && !previewVm.ShowUpdatePanel, "清掉预览状态后欢迎页恢复");

    // ── 设置页不再重复业务页的输入项（这是这次改版的核心：一份状态只有一处入口）──
    string? settingsRepoRoot = null;
    DirectoryInfo? settingsProbe = new(AppContext.BaseDirectory);
    for (var i = 0; i < 8 && settingsProbe is not null; i++, settingsProbe = settingsProbe.Parent)
    {
        if (Directory.Exists(Path.Combine(settingsProbe.FullName, "Prism.Desktop", "Views")))
        {
            settingsRepoRoot = settingsProbe.FullName;
            break;
        }
    }

    string? settingsXamlPath = settingsRepoRoot is null
        ? null
        : Path.Combine(settingsRepoRoot, "Prism.Desktop", "Views", "SettingsView.axaml");
    if (settingsXamlPath is not null && File.Exists(settingsXamlPath))
    {
        string settingsXaml = File.ReadAllText(settingsXamlPath);
        Check(!settingsXaml.Contains("<TextBox", StringComparison.Ordinal),
            "设置页里没有输入框（Pak / 映射 / 输出路径都只在业务页里改）");
        Check(!settingsXaml.Contains("Command=\"{Binding Browse", StringComparison.Ordinal),
            "设置页里没有「浏览…」按钮（不再重复业务页的选择器）");
        Check(settingsXaml.Contains("SettingsCardWidth", StringComparison.Ordinal),
            "设置页卡片宽度由视图模型算（宽屏两列 / 窄窗单列）");
    }
    else
    {
        Check(false, "找不到 SettingsView.axaml（无法校验设置页不再重复输入项）");
    }

    // ── 模组工程：改天还能接着改 ──
    Section("F14 · 模组工程（.prismproj）");

    // 0) 入口不该被"有没有改动"挡住：没有任何替换项时，照样能打开/保存工程
    var emptyProjectVm = new Prism.Desktop.ViewModels.MainViewModel();
    Check(emptyProjectVm.ShowProjectChip && emptyProjectVm.ProjectChipText == "工程",
        $"页头的工程入口始终可见（{emptyProjectVm.ProjectChipText}）");

    string emptyProjectDir = Path.Combine(root, "empty-project");
    Directory.CreateDirectory(emptyProjectDir);
    string emptyProjectPath = Path.Combine(emptyProjectDir, "骨架.prismproj");
    Pump(emptyProjectVm.SaveProjectToAsync(emptyProjectPath));
    Check(File.Exists(emptyProjectPath), "没有替换项也能先把工程存下来");
    Prism.Desktop.Models.ModProject emptyReloaded =
        Prism.Desktop.Services.ProjectStore.Load(emptyProjectPath);
    Check(emptyReloaded.Tasks.Count == 0, "空工程读回来是空的，不是坏文件");

    var openEmptyVm = new Prism.Desktop.ViewModels.MainViewModel();
    Pump(openEmptyVm.OpenProjectFromAsync(emptyProjectPath));
    Check(openEmptyVm.HasProject && openEmptyVm.PatchItems.Count == 0,
        $"打开空工程：列表为空但工程已打开（{openEmptyVm.ProjectChipText}）");

    // 1) 纯读写层：格式往返、相对路径、素材收集
    string projectHome = Path.Combine(root, "project-home");
    Directory.CreateDirectory(projectHome);
    var roundTrip = new Prism.Desktop.Models.ModProject
    {
        Name = "往返测试",
        Paks = ["kards.pak"],
        Usmap = "../Mapping.usmap",
        Oodle = true,
        Tasks =
        [
            new Prism.Desktop.Models.ModProjectTask
            {
                Type = "texture",
                Asset = "Kards/Content/T_Card.uasset",
                Source = "assets/a.png",
                Format = "PF_BC7",
                SizeLabel = "512×512",
            },
            new Prism.Desktop.Models.ModProjectTask
            {
                Type = "locres",
                Asset = "Kards/Content/Localization/Game.locres",
                Edits = [new Prism.Desktop.Models.ModProjectLocresEdit { Ns = "Card", Key = "Moonlight_Name", Text = "月光小夜曲" }],
            },
        ],
    };
    string roundTripPath = Path.Combine(projectHome, "roundtrip.prismproj");
    Prism.Desktop.Services.ProjectStore.Save(roundTripPath, roundTrip);
    Prism.Desktop.Models.ModProject reloaded =
        Prism.Desktop.Services.ProjectStore.Load(roundTripPath);
    Check(reloaded.Name == "往返测试" && reloaded.Tasks.Count == 2,
        $"工程存了再读回来内容一致（{reloaded.Name} / {reloaded.Tasks.Count} 项）");
    Check(reloaded.Oodle && reloaded.Tasks[0].Format == "PF_BC7",
        "打包选项与显示缓存都跟着存（读工程时列表能立刻画出来）");
    Check(reloaded.Tasks[1].Edits?[0].Text == "月光小夜曲",
        "本地化只存改动集（key → 译文）");
    Check(!File.ReadAllText(roundTripPath).Contains("aesKey", StringComparison.OrdinalIgnoreCase),
        "工程文件里没有 aesKey 字段（工程是要发给别人的）");

    string storedRelative = Prism.Desktop.Services.ProjectStore.ToStored(projectHome, Path.Combine(projectHome, "assets", "a.png"));
    Check(storedRelative == "assets/a.png", $"同盘路径存成相对路径（{storedRelative}）");
    Check(Prism.Desktop.Services.ProjectStore.ToAbsolute(projectHome, storedRelative) == Path.Combine(projectHome, "assets", "a.png"),
        "相对路径能还原回绝对路径（工程整个目录搬走仍然能用）");
    string outsideRelative = Prism.Desktop.Services.ProjectStore.ToStored(projectHome, Path.Combine(root, "elsewhere", "b.png"));
    Check(outsideRelative.Contains("..", StringComparison.Ordinal) && !Path.IsPathRooted(outsideRelative),
        $"工程外的素材用 ../ 相对引用（{outsideRelative}）");

    string collectDir = Path.Combine(projectHome, "collect.assets");
    string sourceA = Path.Combine(projectHome, "a.png");
    File.WriteAllBytes(sourceA, [1, 2, 3, 4]);
    string firstCollect = Prism.Desktop.Services.ProjectStore.CollectAsset(collectDir, sourceA);
    string secondCollect = Prism.Desktop.Services.ProjectStore.CollectAsset(collectDir, sourceA);
    Check(firstCollect == secondCollect, "同样内容重复收集会复用同一个素材文件");
    string otherDir = Path.Combine(projectHome, "other");
    Directory.CreateDirectory(otherDir);
    string sourceA2 = Path.Combine(otherDir, "a.png");
    File.WriteAllBytes(sourceA2, [9, 9, 9]);
    string thirdCollect = Prism.Desktop.Services.ProjectStore.CollectAsset(collectDir, sourceA2);
    Check(Path.GetFileName(thirdCollect) == "a_2.png",
        $"同名但内容不同的素材另起一个名字，绝不覆盖（{Path.GetFileName(thirdCollect)}）");

    // 2) 真的存一个工程：打开 pak → 加替换 → 选素材 → 保存
    if (File.Exists(mediaPak))
    {
        var saveVm = new Prism.Desktop.ViewModels.MainViewModel { PakPath = mediaPak };
        RunVmCommand(saveVm, saveVm.OpenPakCommand);
        EnterFolder(saveVm, "Kards");
        EnterFolder(saveVm, "Content");
        EnterFolder(saveVm, "Movies");
        saveVm.SelectedItem = saveVm.Entries.First(entry => !entry.IsDirectory && entry.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        RunVmCommand(saveVm, saveVm.AddSelectedToPatchCommand);
        saveVm.FilePickerOverride = (_, _) => Task.FromResult<string?>(newVideoPath);
        RunVmCommand(saveVm, saveVm.PickReplacementCommand);
        Check(saveVm.IsProjectDirty, "有改动就带未保存标记");
        Check(saveVm.ShowProjectChip && saveVm.ProjectChipText.Contains("未保存", StringComparison.Ordinal),
            $"页头出现工程标识（{saveVm.ProjectChipText}）");

        // 密钥只在本机设置里：先塞一个假的，再断言它没被写进工程
        saveVm.AesKey = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";

        string savedProjectDir = Path.Combine(root, "saved-project");
        Directory.CreateDirectory(savedProjectDir);
        string savedProjectPath = Path.Combine(savedProjectDir, "intro.prismproj");
        Pump(saveVm.SaveProjectToAsync(savedProjectPath));
        Check(File.Exists(savedProjectPath), "工程文件写出来了");
        Check(!saveVm.IsProjectDirty && saveVm.ProjectPath == savedProjectPath,
            "保存后不再显示未保存");

        string savedJson = File.ReadAllText(savedProjectPath);
        Check(!savedJson.Contains("00112233445566778899AABBCCDDEEFF", StringComparison.OrdinalIgnoreCase),
            "AES 密钥没有被写进工程文件");
        Check(!savedJson.Contains("prism-desktop-patch", StringComparison.Ordinal),
            "工作目录之类的缓存路径没有进工程（那是每次重建的）");
        Check(Directory.Exists(Path.Combine(savedProjectDir, "intro.assets")),
            "素材被收进 <工程名>.assets 目录");
        Check(savedJson.Contains("intro.assets/Intro-new.mp4", StringComparison.Ordinal),
            "工程里素材记的是收集副本的相对路径");

        // 3) 打开工程 = 重开 Pak + 重建替换列表 + 重新应用素材
        var loadVm = new Prism.Desktop.ViewModels.MainViewModel();
        Pump(loadVm.OpenProjectFromAsync(savedProjectPath));
        Prism.Desktop.Models.PatchItem? restored = loadVm.PatchItems.FirstOrDefault();
        Check(loadVm.PatchItems.Count == 1, $"打开工程还原出替换列表（{loadVm.PatchItems.Count} 项）");
        Check(restored?.IsReplaced == true, $"素材重新应用成功（{restored?.Error ?? "无错误"}）");
        Check(restored is not null && restored.PatchedFiles.Count == 1 &&
              File.ReadAllBytes(restored.PatchedFiles.Values.First()).SequenceEqual(File.ReadAllBytes(newVideoPath)),
            "还原出来的就是那份素材的字节");
        Check(!loadVm.IsProjectDirty, "刚打开的工程不该显示未保存");
        Check(loadVm.RecentProjects.Any(item => item.Path == savedProjectPath),
            "打开过的工程进「最近工程」");

        // 4) 缺素材：只要标在那一行上，工程照样打得开
        foreach (string assetFile in Directory.GetFiles(Path.Combine(savedProjectDir, "intro.assets")))
        {
            File.Delete(assetFile);
        }

        var missingVm = new Prism.Desktop.ViewModels.MainViewModel();
        Pump(missingVm.OpenProjectFromAsync(savedProjectPath));
        Prism.Desktop.Models.PatchItem? missing = missingVm.PatchItems.FirstOrDefault();
        Check(missingVm.PatchItems.Count == 1, "素材没了也不丢任务（留一行让人看见）");
        Check(missing?.Status == "失败" && (missing.Error?.Contains("素材", StringComparison.Ordinal) ?? false),
            $"缺素材标在行上（{missing?.Error}）");

        // 5) 资产在 Pak 里没了：同样只标在行上
        string brokenProjectPath = Path.Combine(savedProjectDir, "broken.prismproj");
        File.WriteAllText(brokenProjectPath, savedJson.Replace("Movies/Intro.mp4", "Movies/NotThere.mp4"));
        var brokenVm = new Prism.Desktop.ViewModels.MainViewModel();
        Pump(brokenVm.OpenProjectFromAsync(brokenProjectPath));
        Prism.Desktop.Models.PatchItem? broken = brokenVm.PatchItems.FirstOrDefault();
        Check(broken?.Status == "失败" && (broken.Error?.Contains("找不到", StringComparison.Ordinal) ?? false),
            $"资产不在了标在行上（{broken?.Error}）");
    }
    else
    {
        Console.WriteLine("        跳过工程流程断言（测试 pak 未生成）");
    }

    // 6) 本地化：工程只存改动集，读回来套在当前 .locres 上
    if (File.Exists(locresPak))
    {
        var locresSaveVm = new Prism.Desktop.ViewModels.MainViewModel { PakPath = locresPak };
        RunVmCommand(locresSaveVm, locresSaveVm.OpenPakCommand);
        EnterFolder(locresSaveVm, "Kards");
        EnterFolder(locresSaveVm, "Content");
        EnterFolder(locresSaveVm, "Config");
        locresSaveVm.SelectedItem = locresSaveVm.Entries.First(entry => !entry.IsDirectory && entry.Name.EndsWith(".locres", StringComparison.OrdinalIgnoreCase));
        RunVmCommand(locresSaveVm, locresSaveVm.AddSelectedToPatchCommand);

        Prism.Desktop.Models.PatchItem locresTask = locresSaveVm.PatchItems.First();
        locresTask.LocresEntries[0].Text = "工程里的译文";
        RunVmCommand(locresSaveVm, locresSaveVm.UpdateLocresEntryCommand, locresTask);

        string locresProjectDir = Path.Combine(root, "locres-project");
        Directory.CreateDirectory(locresProjectDir);
        string locresProjectPath = Path.Combine(locresProjectDir, "locres.prismproj");
        Pump(locresSaveVm.SaveProjectToAsync(locresProjectPath));
        string locresJson = File.ReadAllText(locresProjectPath);
        Check(locresJson.Contains("工程里的译文", StringComparison.Ordinal), "译文进了工程的改动集");
        Check(!locresJson.Contains("你好", StringComparison.Ordinal),
            "没改的条目不会写进工程（否则等于把整份 .locres 塞进来）");

        var locresLoadVm = new Prism.Desktop.ViewModels.MainViewModel();
        Pump(locresLoadVm.OpenProjectFromAsync(locresProjectPath));
        Prism.Desktop.Models.PatchItem? restoredLocres = locresLoadVm.PatchItems.FirstOrDefault();
        Check(restoredLocres?.IsLocres == true && restoredLocres.Status == "已编辑",
            $"打开工程后本地化仍是已编辑（{restoredLocres?.Status}）");
        Check(restoredLocres?.LocresEntries.Any(entry => entry.Text == "工程里的译文") == true,
            "改动集套回了当前 .locres 的词条");
    }
    else
    {
        Console.WriteLine("        跳过工程里的本地化断言（locres.pak 未生成）");
    }

    // 7) 安卓私有目录：paks/（你选的包）不自动清理，picked/（素材副本）按引用清理
    string cacheRoot = Path.Combine(root, "android-cache");
    string docsFolder = Path.Combine(cacheRoot, Prism.Desktop.Services.AndroidCachePolicy.DocumentsFolder);
    string assetsFolder = Path.Combine(cacheRoot, Prism.Desktop.Services.AndroidCachePolicy.AssetsFolder);
    Directory.CreateDirectory(docsFolder);
    Directory.CreateDirectory(assetsFolder);
    string pakCopy = Path.Combine(docsFolder, "kards.pak");
    string staleAsset = Path.Combine(assetsFolder, "stale.png");
    string liveAsset = Path.Combine(assetsFolder, "live.png");
    File.WriteAllBytes(pakCopy, [1, 2, 3]);
    File.WriteAllBytes(staleAsset, [4]);
    File.WriteAllBytes(liveAsset, [5]);

    HashSet<string> cacheKeep = Prism.Desktop.Services.AndroidCachePolicy.BuildKeepSet([liveAsset, null, string.Empty]);
    int staleRemoved = Prism.Desktop.Services.AndroidCachePolicy.Cleanup(assetsFolder, cacheKeep);
    Check(staleRemoved == 1 && !File.Exists(staleAsset) && File.Exists(liveAsset),
        $"素材副本按引用清理，还在用的留着（删了 {staleRemoved} 个）");
    Check(File.Exists(pakCopy), "你选的 Pak 在 paks/ 里，启动清理不会碰它");
    Check(Prism.Desktop.Services.AndroidCachePolicy.IsCacheCopy(cacheRoot, pakCopy) &&
          Prism.Desktop.Services.AndroidCachePolicy.IsCacheCopy(cacheRoot, liveAsset) &&
          !Prism.Desktop.Services.AndroidCachePolicy.IsCacheCopy(cacheRoot, Path.Combine(root, "mine.pak")),
        "只有私有目录里的副本算缓存，用户自己选的原文件不会被删");

    (int cacheFiles, long cacheBytes) = Prism.Desktop.Services.AndroidCachePolicy.Measure(assetsFolder);
    Check(cacheFiles == 1 && cacheBytes == 1,
        $"缓存占用能统计出来（{cacheFiles} 个 / {cacheBytes} 字节）");

    // ══════════════════════════════════════════════════════════════
    Section("F15 · cooked 老包的摘要排布（legacy -8 / -9）");

    // 摘要头：tag + legacyFileVersion + [UE3] + UE4 + [UE5] + licensee
    static byte[] summaryHead(int legacy, int ue3, int ue4, int ue5, int licensee, bool withUe5Field)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(PackageSummaryLayout.PackageFileTag);
        bw.Write(legacy);
        if (legacy != -4)
            bw.Write(ue3);
        bw.Write(ue4);
        if (withUe5Field)
            bw.Write(ue5);
        bw.Write(licensee);
        bw.Write(new byte[16]); // 后面跟着的真实内容（SavedHash / CustomVersions …）随便填
        bw.Flush();
        return ms.ToArray();
    }

    PackageSummaryLayout legacyUnversioned = PackageSummaryLayout.Parse(
        summaryHead(-8, 0, 0, 0, 0, withUe5Field: true));
    Check(legacyUnversioned.Flavor == PackageSummaryFlavor.UnversionedBeforeSavedHash &&
          legacyUnversioned.NeedsPreSavedHashProfile,
        $"legacy -8 + 版本号全 0 → 认成 UE5.5 之前的排布（{legacyUnversioned.Flavor}）");

    PackageSummaryLayout modernUnversioned = PackageSummaryLayout.Parse(
        summaryHead(-9, 0, 0, 0, 0, withUe5Field: true));
    Check(modernUnversioned.Flavor == PackageSummaryFlavor.UnversionedWithSavedHash &&
          !modernUnversioned.NeedsPreSavedHashProfile,
        $"legacy -9 + 版本号全 0 → 认成带 SavedHash 的新排布（{modernUnversioned.Flavor}）");

    PackageSummaryLayout versioned = PackageSummaryLayout.Parse(
        summaryHead(-8, 864, 522, 1012, 0, withUe5Field: true));
    Check(versioned.Flavor == PackageSummaryFlavor.Versioned,
        $"写了真实版本号的包不受影响（{versioned.Flavor}）");
    Check(versioned.IsUnversioned == false, "有版本号的包不会被当成 unversioned");

    PackageSummaryLayout ue4Unversioned = PackageSummaryLayout.Parse(
        summaryHead(-7, 0, 0, 0, 0, withUe5Field: false));
    Check(ue4Unversioned.Flavor == PackageSummaryFlavor.UnversionedBeforeSavedHash &&
          !ue4Unversioned.NeedsPreSavedHashProfile,
        "UE4 年代（legacy -7）的 unversioned 包不乱套 UE5 版本号");

    Check(PackageSummaryLayout.Parse([]).Flavor == PackageSummaryFlavor.Unknown &&
          PackageSummaryLayout.Parse([1, 2, 3]).Flavor == PackageSummaryFlavor.Unknown,
        "短包 / 脏数据判不出来时保持 Unknown，不会瞎猜");
    Check(PackageSummaryLayout.Parse(summaryHead(-8, 0, 0, 0, 0, withUe5Field: true)[..8])
              .Flavor == PackageSummaryFlavor.Unknown,
        "只给 8 字节时宁可判不出来也不越界");

    Check(PackageSummaryLayout.DecideProfile([legacyUnversioned]) ==
          PackageSummaryProfileDecision.UsePreSavedHashProfile,
        "抽到的全是老排布 → 换 UE5.4 的版本号再读");
    Check(PackageSummaryLayout.DecideProfile([modernUnversioned]) ==
          PackageSummaryProfileDecision.KeepRequestedProfile,
        "抽到新排布 → 保持调用方要求的版本号");
    Check(PackageSummaryLayout.DecideProfile([legacyUnversioned, modernUnversioned]) ==
          PackageSummaryProfileDecision.KeepRequestedProfile,
        "混装（两代包都有）→ 保持请求的版本号，别把本来能读的新包读坏");
    Check(PackageSummaryLayout.DecideProfile([]) == PackageSummaryProfileDecision.KeepRequestedProfile &&
          PackageSummaryLayout.DecideProfile([versioned]) == PackageSummaryProfileDecision.KeepRequestedProfile,
        "没有样本 / 样本都带版本号 → 不动版本号");

    // ══════════════════════════════════════════════════════════════
    Section("F16 · 贴图替换用的引擎版本号（UAssetAPI 一侧）");

    // UAssetAPI 也是靠"传进去的引擎版本号"去套 cooked 包摘要的排布，老包要降到 5.4。
    string legacyAsset = Path.Combine(root, "legacy.uasset");
    File.WriteAllBytes(legacyAsset, summaryHead(-8, 0, 0, 0, 0, withUe5Field: true));
    string modernAsset = Path.Combine(root, "modern.uasset");
    File.WriteAllBytes(modernAsset, summaryHead(-9, 0, 0, 0, 0, withUe5Field: true));
    string versionedAsset = Path.Combine(root, "versioned.uasset");
    File.WriteAllBytes(versionedAsset, summaryHead(-8, 864, 522, 1012, 0, withUe5Field: true));
    string truncatedAsset = Path.Combine(root, "short.uasset");
    File.WriteAllBytes(truncatedAsset, [0xC1, 0x83, 0x2A, 0x9E, 0xF8, 0xFF, 0xFF, 0xFF]);

    Check(CookedAssetVersion.IsUnversionedBeforeSavedHash(legacyAsset),
        "认出 UE5.5 之前 cook 的资产头（legacy -8 + 版本号全 0）");
    Check(CookedAssetVersion.Resolve(legacyAsset, EngineVersion.VER_UE5_6) ==
          CookedAssetVersion.PreSavedHashEngineVersion,
        "老资产把引擎版本号降到 5.4 再解析");
    Check(CookedAssetVersion.Resolve(modernAsset, EngineVersion.VER_UE5_6) == EngineVersion.VER_UE5_6,
        "新资产（legacy -9）保持调用方要求的版本号");
    Check(CookedAssetVersion.Resolve(versionedAsset, EngineVersion.VER_UE5_6) == EngineVersion.VER_UE5_6,
        "有版本号的资产不用降级（文件里写的版本说了算）");
    Check(CookedAssetVersion.Resolve(legacyAsset, EngineVersion.VER_UE5_4) == EngineVersion.VER_UE5_4,
        "调用方本来就按老版本读时原样返回");
    Check(!CookedAssetVersion.IsUnversionedBeforeSavedHash(truncatedAsset) &&
          CookedAssetVersion.Resolve(truncatedAsset, EngineVersion.VER_UE5_6) == EngineVersion.VER_UE5_6,
        "文件头不够长时不动版本号，交给 UAssetAPI 自己报错");

    Section("F17 · UMG 控件预览（读属性 / 算布局 / 控件树）");

    // 1) 宽容读取：unversioned 序列化只写和默认值不同的字段，属性缺失必须落到默认值。
    var fontProp = StructProp("Font", Num("Size", 10));
    var brushProp = StructProp("Brush",
        new WidgetPropertyDto("ResourceObject", "ObjectProperty",
            new WidgetObjectRef("Texture2D", "T_Icon", "pkg.T_Icon", "img0")));
    var probeNode = Play(Dto("Probe", "TextBlock", "", [fontProp, brushProp], []));

    Check(Math.Abs(WidgetLayout.FontSizePx(probeNode) - 10f * WidgetLayout.PointsToPixels) < 0.01f,
        "Font.Size 磅值换算成像素（96/72）");
    Check(Math.Abs(WidgetProps.Float(probeNode.Properties, "RenderOpacity", 1) - 1) < 0.001,
        "没写 RenderOpacity 时按 1");
    Check(WidgetProps.Enum(probeNode.Properties, "Visibility", "Visible") == "Visible",
        "没写 Visibility 时按 Visible");
    Check(WidgetProps.Object(WidgetProps.Struct(probeNode.Properties, "Brush"), "ResourceObject")?.ImageId == "img0",
        "画刷里的贴图引用带出了已解码图片编号");
    Check(WidgetProps.Margin(null, "Padding", new WidgetMargin(4, 2, 4, 2)) == new WidgetMargin(4, 2, 4, 2),
        "空属性的 Margin 回落到调用方给的默认值");

    // 2) SizeBox 覆写尺寸：两个 bOverride 开关都写死时以覆写值为准。
    var sizeBox = Play(Dto("Root", "SizeBox", "", [
        BoolProp("bOverride_WidthOverride", true), Num("WidthOverride", 248),
        BoolProp("bOverride_HeightOverride", true), Num("HeightOverride", 80),
    ], [], Dto("Body", "Border", "SizeBoxSlot")));
    var sizeLayout = new WidgetLayout(Measure);
    sizeLayout.Layout(sizeBox, 1920, 1080);
    Check(Math.Abs(sizeBox.DesiredWidth - 248) < 0.01f && Math.Abs(sizeBox.DesiredHeight - 80) < 0.01f,
        $"SizeBox 覆写后是 248×80（实际 {sizeBox.DesiredWidth:0.#}×{sizeBox.DesiredHeight:0.#}）");
    Check(Math.Abs(sizeBox.Width - 1920) < 0.01f && Math.Abs(sizeBox.Height - 1080) < 0.01f,
        "根控件铺满整块设计区域（大小由调用方给的画布决定）");

    // 3) 横向盒：两个子控件宽度相加，槽位内边距算进格子里。
    var boxRow = Play(Dto("Row", "HorizontalBox", "", [], [],
        Dto("Left", "SizeBox", "HorizontalBoxSlot", [
            BoolProp("bOverride_WidthOverride", true), Num("WidthOverride", 40),
            BoolProp("bOverride_HeightOverride", true), Num("HeightOverride", 38),
        ], [StructProp("Padding", Num("Left", 4), Num("Top", 2), Num("Right", 4), Num("Bottom", 2))]),
        Dto("Right", "SizeBox", "HorizontalBoxSlot", [
            BoolProp("bOverride_WidthOverride", true), Num("WidthOverride", 100),
            BoolProp("bOverride_HeightOverride", true), Num("HeightOverride", 20),
        ], [])));
    var rowLayout = new WidgetLayout(Measure);
    rowLayout.Layout(boxRow, 400, 200);
    Check(Math.Abs(boxRow.Children[0].X - 4) < 0.01f && Math.Abs(boxRow.Children[1].X - 48) < 0.01f,
        $"横向盒里第二个子控件的起点把前一个的宽度和内边距算进去（x={boxRow.Children[1].X:0.#}）");
    Check(Math.Abs(boxRow.DesiredWidth - (40 + 8 + 100)) < 0.01f,
        $"横向盒的期望宽度是两者之和（实际 {boxRow.DesiredWidth:0.#}）");

    // 4) CanvasPanel：锚点重合时 Offsets 是位置 + 尺寸；拉伸锚点时是四周边距。
    var canvas = Play(Dto("Canvas", "CanvasPanel", "", [], [],
        Dto("Fixed", "Border", "CanvasPanelSlot", [], [
            StructProp("LayoutData",
                StructProp("Anchors", VecProp("Minimum", 0, 0), VecProp("Maximum", 0, 0)),
                StructProp("Offsets", Num("Left", 10), Num("Top", 20), Num("Right", 100), Num("Bottom", 30)),
                VecProp("Alignment", 0, 0)),
        ]),
        Dto("Stretched", "Border", "CanvasPanelSlot", [], [
            StructProp("LayoutData",
                StructProp("Anchors", VecProp("Minimum", 0, 0), VecProp("Maximum", 1, 1)),
                StructProp("Offsets", Num("Left", 0), Num("Top", 10), Num("Right", 0), Num("Bottom", 10)),
                VecProp("Alignment", 0, 0)),
        ])));
    var canvasLayout = new WidgetLayout(Measure);
    canvasLayout.Layout(canvas, 400, 200);
    Check(Math.Abs(canvas.Children[0].X - 10) < 0.01f && Math.Abs(canvas.Children[0].Y - 20) < 0.01f &&
          Math.Abs(canvas.Children[0].Width - 100) < 0.01f && Math.Abs(canvas.Children[0].Height - 30) < 0.01f,
        "CanvasPanel 固定锚点：Offsets 当位置与尺寸");
    Check(Math.Abs(canvas.Children[1].Width - 400) < 0.01f && Math.Abs(canvas.Children[1].Height - 180) < 0.01f,
        $"CanvasPanel 拉伸锚点：Offsets 当四周边距（实际 {canvas.Children[1].Width:0.#}×{canvas.Children[1].Height:0.#}）");

    // 5) ScaleBox：子树按未缩放尺寸布局，再整体缩放。
    var scaleBox = Play(Dto("Scale", "ScaleBox", "", [], [],
        Dto("Content", "SizeBox", "ScaleBoxSlot", [
            BoolProp("bOverride_WidthOverride", true), Num("WidthOverride", 100),
            BoolProp("bOverride_HeightOverride", true), Num("HeightOverride", 50),
        ], [])));
    var scaleLayout = new WidgetLayout(Measure);
    scaleLayout.Layout(scaleBox, 200, 200);
    Check(Math.Abs(scaleBox.Children[0].Width - 100) < 0.01f && Math.Abs(scaleBox.Children[0].Height - 50) < 0.01f,
        "ScaleBox 里的内容按自身尺寸布局");
    Check(Math.Abs(scaleBox.ContentScaleX - 2) < 0.01f && Math.Abs(scaleBox.ContentScaleY - 2) < 0.01f,
        $"ScaleBox ScaleToFit 取宽高缩放的较小值（实际 {scaleBox.ContentScaleX:0.##}）");

    // 6) Collapsed 的控件不参与布局。
    var boxWithCollapsed = Play(Dto("Row2", "HorizontalBox", "", [], [],
        Dto("Collapsed", "SizeBox", "HorizontalBoxSlot", [EnumProp("Visibility", "Collapsed")], []),
        Dto("Visible", "SizeBox", "HorizontalBoxSlot", [
            BoolProp("bOverride_WidthOverride", true), Num("WidthOverride", 60),
            BoolProp("bOverride_HeightOverride", true), Num("HeightOverride", 10),
        ], [])));
    var collapsedLayout = new WidgetLayout(Measure);
    collapsedLayout.Layout(boxWithCollapsed, 400, 100);
    Check(!boxWithCollapsed.Children[0].IsVisible && Math.Abs(boxWithCollapsed.Children[0].DesiredWidth) < 0.01f,
        "Collapsed 的控件不参与布局");
    Check(Math.Abs(boxWithCollapsed.DesiredWidth - 60) < 0.01f,
        $"Collapsed 的兄弟节点宽度不含它（实际 {boxWithCollapsed.DesiredWidth:0.#}）");

    // 7) 文字：TextBlock 的期望宽度来自注入了度量函数的那一层。
    var textDto = Dto("Label", "TextBlock", "", [TextProp("Text", "PE-BOX"), fontProp], []);
    var textNode = Play(textDto);
    var textLayout = new WidgetLayout(Measure);
    textLayout.Layout(textNode, 400, 100);
    Check(Math.Abs(textNode.DesiredWidth - "PE-BOX".Length * WidgetLayout.FontSizePx(textNode) * 0.5f) < 0.01f,
        $"TextBlock 期望宽度走注入的文字度量（实际 {textNode.DesiredWidth:0.#}）");

    // 8) 控件树 → 预览视图模型：展平成带缩进的列表，选中同步属性表。
    var previewDto = new WidgetPreviewDto(
        "WBP_Demo",
        "WidgetBlueprintGeneratedClass",
        Dto("Root", "SizeBox", "", [], [], Dto("Row", "HorizontalBox", "SizeBoxSlot", [], [], textDto)),
        [new WidgetBindingDto("Label", "Text", "GetText")],
        [],
        [],
        null);
    Prism.FeatureTests.UiFixture.EnsureInitialized();
    var widgetVm = new Prism.Desktop.ViewModels.MainViewModel { WidgetPreview = previewDto };
    Check(widgetVm.HasWidgetPreview && widgetVm.WidgetTreeItems.Count == 3,
        $"控件树展平成 3 行（实际 {widgetVm.WidgetTreeItems.Count}）");
    Check(widgetVm.WidgetTreeItems[2].Depth == 2, "缩进按层级递进");
    Check(widgetVm.WidgetSummary.Contains("3 个控件") && widgetVm.WidgetSummary.Contains("1 个绑定"),
        $"摘要里有控件数与绑定数（实际「{widgetVm.WidgetSummary}」）");
    widgetVm.SelectedWidgetItem = widgetVm.WidgetTreeItems[2];
    Check(ReferenceEquals(widgetVm.SelectedWidgetNode, widgetVm.WidgetTreeItems[2].Node),
        "列表选中 → 画布选中的节点一致");
    Check(widgetVm.WidgetPropertyRows.Any(row => row.Label == "Text" && row.Value == "PE-BOX"),
        "属性表里能看到 Text 的真实值");
    Check(widgetVm.WidgetPropertyRows.Any(row => row.Label == "Font" && row.IsHeader),
        "结构体属性展开成分组标题");
    widgetVm.WidgetPreview = null;
    Check(!widgetVm.HasWidgetPreview && widgetVm.WidgetTreeItems.Count == 0,
        "清空预览后控件树也清空");

    Section("F18 · 区域拖拽（列表↔预览 / 画布↔详情 / 控件树↔属性）");

    Prism.FeatureTests.UiFixture.EnsureInitialized();
    {
        (Prism.Desktop.Views.MainWindow splitWindow, Prism.Desktop.ViewModels.MainViewModel splitVm) = Prism.FeatureTests.UiFixture.CreateShell();
        splitWindow.Show();
        Prism.FeatureTests.UiFixture.RunJobs();
        splitVm.CurrentTabIndex = 1;
        splitVm.GoWorkspaceCommand.Execute(null);
        Prism.FeatureTests.UiFixture.RunJobs();

        // 横屏/竖屏两套布局各有一个预览栏（另一套 IsVisible=false 也还在可视树里），取真在用的那个
        Prism.Desktop.Views.PreviewPaneView? pane = splitWindow.GetVisualDescendants()
            .OfType<Prism.Desktop.Views.PreviewPaneView>()
            .OrderByDescending(candidate => candidate.Bounds.Width)
            .FirstOrDefault();
        Check(pane is not null, "浏览页里能找到预览栏");

        double paneWidthBefore = pane?.Bounds.Width ?? 0;
        bool dragged = Prism.FeatureTests.UiFixture.DragHandle(splitWindow, "browse-handle", -140, 0);
        Check(dragged, "列表↔预览之间有可拖的条");
        Check((pane?.Bounds.Width ?? 0) > paneWidthBefore + 60,
            $"往左拖之后预览栏变宽（{paneWidthBefore:0} → {pane?.Bounds.Width ?? 0:0}）");

        // 塞一份控件预览，让「画布 ↔ 详情」那张卡片出现
        splitVm.WidgetPreview = previewDto;
        Prism.FeatureTests.UiFixture.RunJobs();

        Prism.Desktop.Views.WidgetPreviewView? widgetCanvas = splitWindow.GetVisualDescendants()
            .OfType<Prism.Desktop.Views.WidgetPreviewView>()
            .OrderByDescending(candidate => candidate.Bounds.Width * candidate.Bounds.Height)
            .FirstOrDefault();
        Check(widgetCanvas is not null, "控件预览画布出现在可视树里");
        double canvasHeightBefore = widgetCanvas?.Bounds.Height ?? 0;

        Check(Prism.FeatureTests.UiFixture.DragHandle(splitWindow, "widget-row-handle", 0, 160),
            "画布与详情之间有可拖的条");
        Check((widgetCanvas?.Bounds.Height ?? 0) > canvasHeightBefore + 60,
            $"往下拖之后画布变高（{canvasHeightBefore:0} → {widgetCanvas?.Bounds.Height ?? 0:0}）");

        splitWindow.Close();
    }

    Section("F19 · 蓝图节点图（概要 → 函数列表 → 节点详情 → 铺在浏览页上）");

    // 1) 概要到函数列表：事件排在函数前面，入口文案带回函数数。
    var bpSummary = new PakTool.Core.Blueprints.BlueprintGraphDto(
        "WBP_Demo_C",
        "WBP_Demo_C",
        "/Script/UMG.UserWidget",
        [
            new PakTool.Core.Blueprints.BlueprintFunctionDto("Construct", "Construct", "Construct", "event", 4, null),
            new PakTool.Core.Blueprints.BlueprintFunctionDto("GetValue", "GetValue", "GetValue", "function", 2, null),
        ],
        "1 个事件 / 1 个函数");

    var bpVm = new Prism.Desktop.ViewModels.MainViewModel { BlueprintSummary = bpSummary };
    Check(bpVm.HasBlueprintSummary && bpVm.BlueprintFunctions.Count == 2,
        $"蓝图概要带出 2 个函数（实际 {bpVm.BlueprintFunctions.Count}）");
    Check(bpVm.BlueprintFunctions[0].Kind == "event" && bpVm.BlueprintFunctions[0].Icon == "⚡",
        "事件排在前面并带事件标记");
    Check(bpVm.BlueprintEntryText.Contains("2 个函数"),
        $"预览栏入口写明函数数（实际「{bpVm.BlueprintEntryText}」）");

    // 2) 一张最小的图：事件 → 调函数；用来验证节点/连线/端口详情。
    var eventNode = new PakTool.Core.Blueprints.BlueprintNodeDto(
        0, "Construct", "K2Node_Event", null, 0, 0, 220, 98, false, false, false, false, 0,
        [],
        [new PakTool.Core.Blueprints.BlueprintPinDto("n0o0", "then", false, true, "exec", null, "None", 220, 47, "", false, true)]);
    var callNode = new PakTool.Core.Blueprints.BlueprintNodeDto(
        1, "PrintString", "K2Node_CallFunction", "KismetSystemLibrary", 320, 0, 260, 130, false, false, false, false, 12,
        [
            new PakTool.Core.Blueprints.BlueprintPinDto("n1i0", "execute", true, true, "exec", null, "None", 320, 47, "", false, true),
            new PakTool.Core.Blueprints.BlueprintPinDto("n1i1", "InString", true, false, "String", null, "None", 320, 79, "Hello", false, true),
        ],
        [new PakTool.Core.Blueprints.BlueprintPinDto("n1o0", "then", false, true, "exec", null, "None", 580, 47, "", false, true)]);
    var bpGraph = new PakTool.Core.Blueprints.BlueprintGraphViewDto(
        "Construct", "Construct", "event",
        [eventNode, callNode],
        [new PakTool.Core.Blueprints.BlueprintLinkDto("n0o0", "n1i0", "exec")],
        0, 0, 580, 130,
        "1 个节点的函数签名由调用处推断",
        false);

    bpVm.BlueprintGraph = bpGraph;
    Check(bpVm.HasBlueprintGraph && !bpVm.HasSelectedBlueprintNode, "图加载后还没有选中节点");

    bpVm.SelectedBlueprintNodeIndex = 1;
    Check(bpVm.HasSelectedBlueprintNode, "点节点后详情面板进入选中态");
    Check(bpVm.BlueprintNodeRows.Any(row => row.Label == "类型" && row.Value == "K2Node_CallFunction"),
        "详情里写明节点类型");
    Check(bpVm.BlueprintNodeRows.Any(row => row.Label == "字节码" && row.Value.Contains("12")),
        "详情里带语句位置");
    Check(bpVm.BlueprintNodeRows.Any(row => row.Label == "InString" && row.Value.Contains("Hello")),
        "详情里能看到端口默认值");
    Check(bpVm.BlueprintNodeRows.Any(row => row.PinBrush is not null), "笔刷按端口类型着色");

    bpVm.SelectedBlueprintNodeIndex = -1;
    Check(bpVm.BlueprintNodeRows.Count == 0, "取消选中后详情清空");

    // 3) 真把页面铺到浏览页上：只读画布要出现在可视树里。
    Prism.FeatureTests.UiFixture.EnsureInitialized();
    {
        (Prism.Desktop.Views.MainWindow bpWindow, Prism.Desktop.ViewModels.MainViewModel bpShellVm) =
            Prism.FeatureTests.UiFixture.CreateShell();
        bpWindow.Show();
        Prism.FeatureTests.UiFixture.RunJobs();
        bpShellVm.CurrentTabIndex = 1;
        bpShellVm.GoWorkspaceCommand.Execute(null);
        Prism.FeatureTests.UiFixture.RunJobs();

        bpShellVm.BlueprintSummary = bpSummary;
        bpShellVm.BlueprintGraph = bpGraph;
        bpShellVm.IsBlueprintGraphOpen = true;
        Prism.FeatureTests.UiFixture.RunJobs();

        var page = bpWindow.GetVisualDescendants().OfType<Prism.Desktop.Views.BlueprintGraphPageView>().FirstOrDefault();
        Check(page is not null && page.IsVisible, "打开节点图后页面铺在浏览页上");

        var bpCanvas = bpWindow.GetVisualDescendants().OfType<Prism.Desktop.Views.BlueprintGraphView>().FirstOrDefault();
        Check(bpCanvas is not null && bpCanvas.Bounds.Width > 200 && bpCanvas.Bounds.Height > 200,
            $"节点画布有实际尺寸（{bpCanvas?.Bounds.Width:0}×{bpCanvas?.Bounds.Height:0}）");

        bpShellVm.CloseBlueprintGraphCommand.Execute(null);
        Prism.FeatureTests.UiFixture.RunJobs();
        Check(!bpShellVm.IsBlueprintGraphOpen && bpShellVm.BlueprintGraph is null,
            "返回到列表后图与页面都收起来");

        bpWindow.Close();
    }

    Section("F20 · 设置：关于信息与按平台探测的依赖");

    var aboutVm = new Prism.Desktop.ViewModels.MainViewModel();
    Check(aboutVm.Creators.Count == 2 &&
          aboutVm.Creators.Any(person => person.Name.Contains("CuO") && person.Url.Contains("3546971308559129")),
        "关于里有氧化铜依旧CuO 的 B 站主页");
    Check(aboutVm.Creators.Any(person => person.Name.Contains("KardsWalker") && person.Url.Contains("1253227194")),
        "关于里有 KardsWalker卡兹走路人的 B 站主页");
    Check(aboutVm.Credits.Any(credit => credit.Name == "Flora" && credit.Url?.Contains("Flora") == true),
        "致谢里点名 Flora 并给出仓库");
    Check(aboutVm.Credits.Any(credit => credit.Name.Contains("Blueprint Graph Viewer")),
        "致谢里保留上游反编译器的 MIT 署名");

    aboutVm.RefreshDependenciesCommand.Execute(null);
    Check(aboutVm.Dependencies.Count >= 5,
        $"依赖清单按平台列出组件（{aboutVm.Dependencies.Count} 项）");
    Check(aboutVm.DependencySummary is "全部就绪" || aboutVm.DependencySummary.EndsWith("项缺失"),
        $"依赖结论是一句话（实际「{aboutVm.DependencySummary}」）");

    if (OperatingSystem.IsAndroid())
    {
        Check(aboutVm.Dependencies.Any(item => item.Name.Contains("prism_codecs")),
            "安卓列出随包的 prism_codecs");
        Check(!aboutVm.Dependencies.Any(item => item.Name == "UAssetCLI"),
            "安卓不再列桌面端的 UAssetCLI（之前整片显示缺失的原因）");
        Check(aboutVm.DependencyHeadline.Contains(".so"),
            "安卓的说明写清楚依赖的是 .so");
    }
    else
    {
        Check(aboutVm.Dependencies.Any(item => item.Name == "UAssetCLI"),
            "桌面列出 UAssetCLI");
        Check(aboutVm.Dependencies.Any(item => item.Name.Contains("astcenc")),
            "桌面列出 astcenc");
        Check(!aboutVm.Dependencies.Any(item => item.Name.Contains("prism_codecs")),
            "桌面不列安卓的 .so");
        Check(aboutVm.DependencyHeadline.Contains("外部组件"),
            "桌面的说明写清楚依赖的是外部组件");
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

// 控件预览测试用的最小 DTO 构造器：只写需要的属性，其余留给"缺属性用默认值"。
static WidgetPropertyDto Num(string name, double value) => new(name, "FloatProperty", new WidgetNumber(value));

static WidgetPropertyDto BoolProp(string name, bool value) => new(name, "BoolProperty", new WidgetBoolean(value));

static WidgetPropertyDto EnumProp(string name, string value) => new(name, "EnumProperty", new WidgetEnum(value), "ETest");

static WidgetPropertyDto TextProp(string name, string value) => new(name, "TextProperty", new WidgetText(value));

static WidgetPropertyDto VecProp(string name, double x, double y) => new(name, "StructProperty", new WidgetVector(x, y));

static WidgetPropertyDto StructProp(string name, params WidgetPropertyDto[] fields) =>
    new(name, "StructProperty", new WidgetStruct(name, fields));

static WidgetNodeDto Node(
    string name,
    string className,
    string slotClassName = "",
    WidgetPropertyDto[]? properties = null,
    WidgetPropertyDto[]? slotProperties = null,
    params WidgetNodeDto[] children) =>
    new(name, className, slotClassName, properties ?? [], slotProperties ?? [], children);

// 控件树构造的别名：读起来像「一个节点」，返回的是还没算布局的 DTO。
static WidgetNodeDto Dto(
    string name,
    string className,
    string slotClassName = "",
    WidgetPropertyDto[]? properties = null,
    WidgetPropertyDto[]? slotProperties = null,
    params WidgetNodeDto[] children) =>
    Node(name, className, slotClassName, properties, slotProperties, children);

static WidgetNode Play(WidgetNodeDto dto) =>
    WidgetNode.FromDto(dto) ?? throw new InvalidOperationException("控件树为空");

// 测试里的文字度量：宽 = 字符数 × 字号 × 0.5，高 = 字号 × 1.2，和真实渲染无关但足够验证传递链路。
static (float Width, float Height) Measure(WidgetNode node, string text, float fontSizePx, float wrapWidth) =>
    (text.Length * fontSizePx * 0.5f, fontSizePx * 1.2f);

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

/// <summary>跑一次 UAssetCLI，返回退出码与两路输出（超时会杀掉整棵进程树）。</summary>
static (int ExitCode, string StandardOutput, string StandardError) RunUAssetCli(
    string cliPath,
    IReadOnlyList<string> arguments,
    int timeoutMs)
{
    System.Diagnostics.ProcessStartInfo startInfo = new()
    {
        FileName = cliPath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)
        ?? throw new InvalidOperationException("无法启动 UAssetCLI");
    string stdout = process.StandardOutput.ReadToEnd();
    string stderr = process.StandardError.ReadToEnd();

    if (!process.WaitForExit(timeoutMs))
    {
        process.Kill(entireProcessTree: true);
        throw new TimeoutException($"UAssetCLI {arguments.FirstOrDefault()} 超时（{timeoutMs} ms）");
    }

    return (process.ExitCode, stdout, stderr);
}

/// <summary>跑 <c>UAssetCLI inspect-texture</c>，返回 stdout。非零退出码抛异常（测试应显式失败）。</summary>
static string RunCliInspectTexture(string cliPath, string assetPath, string engine)
{
    (int exitCode, string stdout, string stderr) = RunUAssetCli(
        cliPath,
        ["inspect-texture", "--asset", assetPath, "--engine", engine],
        120_000);

    if (exitCode != 0)
    {
        throw new InvalidOperationException($"UAssetCLI inspect-texture 退出码 {exitCode}：{stderr}");
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
static void RunVmCommand(Prism.Desktop.ViewModels.MainViewModel vm, CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command, object? parameter = null)
{
    Task task = command.ExecuteAsync(parameter);
    Pump(task);
}

/// <summary>把任意异步操作推完（工程保存/打开不是命令，但同样要 Pump）。</summary>
static void Pump(Task task)
{
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
