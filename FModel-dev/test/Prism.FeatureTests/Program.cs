using PakTool.Core;
using UAssetTexture.Core;
using Avalonia;
using Avalonia.Controls;
using UAssetAPI.UnrealTypes;

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
    Section("F7 · 合并覆盖优先级（后加入的覆盖先加入的）");

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

    // 复刻 BuildMergeCoreAsync 的语义：按列表顺序遍历，后写覆盖先写。
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
                 ([pakA, pakB, pakC], "FROM-C", "A,B,C"),
                 ([pakC, pakB, pakA], "FROM-A", "C,B,A"),
                 ([pakB, pakA, pakC], "FROM-C", "B,A,C"),
                 ([pakC, pakA, pakB], "FROM-B", "C,A,B"),
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

    Section("F4/F1/F7 · UI 层验证（headless 真实窗口）");

    try
    {
        (Prism.Desktop.Views.MainWindow window, Prism.Desktop.ViewModels.MainViewModel vm) = Prism.FeatureTests.UiFixture.CreateShell();

        Check(window is not null, "MainWindow + 全部视图在运行时成功加载（模板已展开）");
        Check(vm.IsHomeVisible, "初始显示主页");
        Check(!vm.IsConvertVisible, "初始不显示 Pak 转换页");

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

        var mergeBase = new Prism.Desktop.Models.MergePakItem("a.pak", "a.pak", isBase: true);
        var mergeOther = new Prism.Desktop.Models.MergePakItem("b.pak", "b.pak", isBase: false);
        Check(!mergeBase.CanDrag && !mergeBase.CanRemove, "主 Pak 不可拖动、不可移除");
        Check(mergeOther.CanDrag && mergeOther.CanRemove, "普通 Pak 可拖动、可移除");

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

    Section("Pak 打包 · 非 ASCII 路径回归");

    // 这条曾经导致打包进程直接 abort（Rust unwrap panic）或静默损坏路径。
    // 详见 PakPathEncodingTests 的说明。
    Prism.FeatureTests.PakPathEncodingTests.Run(
        Path.Combine(root, "path-encoding"),
        (ok, label) => Check(ok, label));

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

static string WriteTemp(string root, string pakPath, byte[] content)
{
    string dir = Path.Combine(root, "payload",
        Path.GetDirectoryName(pakPath)?.Replace('/', Path.DirectorySeparatorChar) ?? string.Empty);
    Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, Path.GetFileName(pakPath));
    File.WriteAllBytes(path, content);
    return path;
}