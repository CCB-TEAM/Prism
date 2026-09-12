using PakTool.Core;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace UAssetTexture.Core;

/// <summary>转换模式：决定输出的 Pak 里装什么。</summary>
public enum PakConversionMode
{
    /// <summary>
    /// <b>仅输出被替换的资产</b>（默认）。
    ///
    /// 游戏会把模组 Pak 叠加在原 Pak 之上，同路径以后加载的为准，所以模组只需要
    /// 包含改动过的那几个 <c>.uasset/.uexp/.ubulk</c>，主 Pak 的静态资源不必重复。
    /// 输出的 Pak 体积 ≈ 改动量，而不是整个主 Pak。
    ///
    /// 例：7 GB 主 Pak + 1 张纹理 → 输出约 700 KB（而不是 6.8 GB）。
    /// </summary>
    ReplacedOnly = 0,

    /// <summary>
    /// 替换纹理 + 把主 Pak 的其他文件原样并入，输出一个完整可独立使用的 Pak。
    /// 体积≈主 Pak，仅在需要"单文件替换整包"时使用。
    /// </summary>
    FullRepack = 1,

    /// <summary>
    /// 替换纹理 + 并入源 Pak 中主 Pak 没有的文件；主 Pak 已有的其他文件也原样带入。
    /// </summary>
    MergeAll = 2,
}

/// <summary>单个纹理的转换结果分类。</summary>
public enum PakConversionItemStatus
{
    /// <summary>已在主 Pak 找到同路径纹理并完成替换。</summary>
    Replaced = 0,

    /// <summary>源 Pak 有该纹理，但主 Pak 没有同路径条目。</summary>
    MissingInTarget = 1,

    /// <summary>不是纹理资产，或解不出像素（可能缺少映射文件）。</summary>
    Skipped = 2,

    /// <summary>读取 / 编码 / 写回过程中失败。</summary>
    Failed = 3,
}

public sealed record PakConversionOptions(
    string SourcePakPath,
    string TargetPakPath,
    string OutputPakPath,
    string? AesKeyHex = null,
    string? MappingsPath = null,
    /// <summary>转换模式：决定输出的 Pak 里装什么。默认只装改动过的资产。</summary>
    PakConversionMode Mode = PakConversionMode.ReplacedOnly,
    bool UseOodleCompression = true,
    EngineVersion Engine = EngineVersion.VER_UE5_6,
    /// <summary>ASTC 编码质量（Android 进程内编码时生效；桌面由 astcenc 参数决定）。</summary>
    string AstcQuality = "fast",
    /// <summary>
    /// 解包工作目录。默认为系统临时目录。
    ///
    /// 转换会把<b>主 Pak 的全部文件</b>解到磁盘，大 Pak（数 GB）会占用同等空间，
    /// 因此这里允许指定到大容量磁盘，避免撑爆系统盘。
    /// </summary>
    string? TempDirectory = null);

public sealed record PakConversionItemResult(
    string PakPath,
    PakConversionItemStatus Status,
    string? Message = null,
    /// <summary>实际使用的转换方式（仅 Replaced 时有意义）。</summary>
    PakConversionMethod Method = PakConversionMethod.None);

/// <summary>纹理实际使用的转换方式。</summary>
public enum PakConversionMethod
{
    None = 0,

    /// <summary>两侧格式与布局一致，直接搬运已压缩的 mip 载荷（无损）。</summary>
    RawCopy = 1,

    /// <summary>解出源像素后按主 Pak 资产的格式重新编码（跨平台场景走这条）。</summary>
    ReEncode = 2,
}

public sealed record PakConversionProgress(
    int Completed,
    int Total,
    string CurrentItem,
    PakConversionItemStatus? LastStatus = null);

public sealed record PakConversionResult(
    string OutputPakPath,
    PakConversionCounts Counts,
    IReadOnlyList<PakConversionItemResult> Items);

public sealed record PakConversionCounts(
    int TotalFiles,
    int TextureCandidates,
    int Replaced,
    int MissingInTarget,
    int Skipped,
    int Failed,
    int OtherFilesIncluded);

/// <summary>
/// 双端 Pak 转换：把「待转换 Pak」里的纹理像素，替换进「主 Pak」的同路径资产，
/// 重新编码成<b>主 Pak 那侧的纹理格式</b>，最后合成一个新的 Pak。
///
/// 典型用途：游戏有 PC 端和手机端两套资产。主 Pak 用 PC 端（BC 系格式），
/// 待转换 Pak 用手机端（同路径、ASTC 系格式）。把手机的像素搬进 PC 资产里，
/// 就得到一个 PC 端能用的替换包——不用手动一张张导出图片再加替换任务。
///
/// 流程（与手工操作一一对应）：
/// 1. 解包主 Pak 的全部原始文件到临时目录（与 Pak 合并共用打包链路）；
/// 2. 遍历待转换 Pak，挑出纹理资产，按 Pak 内路径在主 Pak 中找<b>同路径</b>项；
/// 3. 命中则把源资产解成像素，按主 Pak 资产的格式重新编码，写进主 Pak 那份
///    <c>.uasset/.uexp/.ubulk</c> 模板；
/// 4. 用 <see cref="ModifiedPakPackService"/> 打包输出。
///
/// <b>为什么输出格式由主 Pak 决定</b>：cooked 资产的像素格式、尺寸、mip 布局都写在
/// .uasset 头部，mip 区域的位置与长度也由头部决定。替换只覆盖这些区域的字节，
/// 改不了头部 —— 所以输出必然沿用主 Pak 资产的格式，这也正是我们要的结果
/// （PC 端游戏读得懂）。
///
/// 平台差异：把像素编码成<b>目标格式</b>这一步两端不同 ——
/// Android 用进程内 <c>libprism_codecs</c>；Windows 用 UAssetCLI + astcenc/texconv，
/// 由调用方通过 <see cref="EncodeIntoAssetAsync"/> 注入。
/// </summary>
public sealed class PakConversionService
{
    /// <summary>
    /// 把图片写进资产模板（平台相关）。
    /// 参数：(主 Pak 资产(.uasset) 路径, 源像素 PNG 路径, 目标输出 .uasset 路径, 取消令牌)。
    /// 返回 null 表示成功，否则返回错误信息。
    ///
    /// 不注入时退化为"仅同格式字节搬运"（无损快路径），跨格式项会被标记为不可用。
    /// </summary>
    public Func<string, string, string, CancellationToken, Task<string?>>? EncodeIntoAssetAsync { get; set; }

    /// <summary>是否具备跨格式重编码能力。</summary>
    public bool CanReEncodeCrossFormat => OperatingSystem.IsAndroid() || EncodeIntoAssetAsync is not null;

    public async Task<PakConversionResult> ConvertAsync(
        PakConversionOptions options,
        IProgress<PakConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        string tempRoot = Path.Combine(
            string.IsNullOrWhiteSpace(options.TempDirectory) ? Path.GetTempPath() : options.TempDirectory,
            "PrismPakConvert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string? aes = string.IsNullOrWhiteSpace(options.AesKeyHex) ? null : options.AesKeyHex;
            string? mappings = string.IsNullOrWhiteSpace(options.MappingsPath) ? null : options.MappingsPath;

            // 默认模式（ReplacedOnly）只输出改动过的资产，因此不需要把主 Pak 全量解出来；
            // 但每个纹理仍需主 Pak 里那份同名资产作"模板"，那就按需单独取。
            bool carryAllTargetFiles = options.Mode is PakConversionMode.FullRepack or PakConversionMode.MergeAll;

            // 两个会话都要活到循环结束：源提供像素、目标提供模板（体积极小，按需解出）。
            using var targetSession = new PakArchiveSession();
            using var sourceSession = new PakArchiveSession();

            Dictionary<string, ModifiedPakFile> output = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> targetPaths;
            HashSet<string> targetTexturePackages;

            await targetSession.OpenAsync(new PakOpenOptions([options.TargetPakPath], aes, mappings))
                .ConfigureAwait(false);

            if (carryAllTargetFiles)
            {
                progress?.Report(new PakConversionProgress(0, 1, "正在解包主 Pak…"));
                IReadOnlyList<PakRawFileCopy> targetFiles = await targetSession
                    .CopyAllRawFilesAsync(Path.Combine(tempRoot, "target"))
                    .ConfigureAwait(false);

                foreach (PakRawFileCopy file in targetFiles)
                {
                    output[file.PakPath] = new ModifiedPakFile(file.DiskPath, file.PakPath);
                }

                targetPaths = output.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                // 只读索引，不读内容 —— 加密 Pak 也只需索引，几乎瞬时（7 GB 约 0.3 秒）。
                progress?.Report(new PakConversionProgress(0, 1, "正在读取主 Pak 索引…"));
                IReadOnlySet<string> listed = await targetSession
                    .ListRawFilePathsAsync()
                    .ConfigureAwait(false);

                // 列表里的路径可能带前导 '/'，统一成打包时用的形态。
                targetPaths = listed
                    .Where(p => !p.EndsWith('/'))
                    .Select(NormalizePakPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            // 主 Pak 里"可能是纹理"的资产：用于判断源纹理是否有对应项。
            targetTexturePackages = targetPaths
                .Where(p => IsTextureCandidate(p, targetPaths))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // ── 2. 解包源 Pak ────────────────────────────────────────────────
            progress?.Report(new PakConversionProgress(0, 1, "正在解包待转换 Pak…"));
            List<PakConversionItemResult> items = [];
            int replaced = 0;
            int missing = 0;
            int skipped = 0;
            int failed = 0;
            int otherIncluded = 0;

            {
                await sourceSession.OpenAsync(new PakOpenOptions([options.SourcePakPath], aes, mappings))
                    .ConfigureAwait(false);

                IReadOnlyList<PakRawFileCopy> sourceFiles = await sourceSession
                    .CopyAllRawFilesAsync(Path.Combine(tempRoot, "source"))
                    .ConfigureAwait(false);

                HashSet<string> sourcePaths = sourceFiles
                    .Select(f => f.PakPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // 纹理候选：_P.uasset 约定，或任意带同名 .uexp 的 .uasset。
                // 避免对 .uexp/.ubulk 重复工作。
                List<PakRawFileCopy> textureCandidates = sourceFiles
                    .Where(f => IsTextureCandidate(f.PakPath, sourcePaths))
                    .ToList();

                int total = textureCandidates.Count;
                int index = 0;

                foreach (PakRawFileCopy candidate in textureCandidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    index++;
                    progress?.Report(new PakConversionProgress(index, total, candidate.PakPath));

                    // 主 Pak 没有同路径纹理包 → 按模式决定是并入还是跳过。
                    if (!targetTexturePackages.Contains(candidate.PakPath))
                    {
                        if (options.Mode == PakConversionMode.MergeAll)
                        {
                            // 只在主 Pak 完全没有该文件时才并入，避免覆盖主 Pak 的 uexp/ubulk。
                            foreach (PakRawFileCopy related in RelatedFilesOf(candidate.PakPath, sourceFiles))
                            {
                                if (!output.ContainsKey(related.PakPath))
                                {
                                    output[related.PakPath] = new ModifiedPakFile(related.DiskPath, related.PakPath);
                                    otherIncluded++;
                                }
                            }
                        }

                        missing++;
                        items.Add(new PakConversionItemResult(
                            candidate.PakPath,
                            PakConversionItemStatus.MissingInTarget,
                            "主 Pak 中没有同路径纹理"));
                        progress?.Report(new PakConversionProgress(index, total, candidate.PakPath, PakConversionItemStatus.MissingInTarget));
                        continue;
                    }

                    try
                    {
                        TextureConversionOutcome conversion = await ConvertTextureAsync(
                            sourceSession, targetSession, candidate, output, options, tempRoot, cancellationToken)
                            .ConfigureAwait(false);

                        PakConversionItemStatus status = conversion.Status;
                        switch (status)
                        {
                            case PakConversionItemStatus.Replaced:
                                replaced++;
                                items.Add(new PakConversionItemResult(
                                    candidate.PakPath,
                                    status,
                                    conversion.Method == PakConversionMethod.RawCopy
                                        ? "格式一致，已无损搬运原始 mip 数据"
                                        : $"已解出像素并按主 Pak 格式（{conversion.TargetFormat}）重新编码",
                                    conversion.Method));
                                break;
                            default:
                                skipped++;
                                items.Add(new PakConversionItemResult(
                                    candidate.PakPath,
                                    status,
                                    conversion.Message ?? "无法解出纹理像素，已跳过"));
                                break;
                        }

                        progress?.Report(new PakConversionProgress(index, total, candidate.PakPath, status));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        items.Add(new PakConversionItemResult(candidate.PakPath, PakConversionItemStatus.Failed, ex.Message));
                        progress?.Report(new PakConversionProgress(index, total, candidate.PakPath, PakConversionItemStatus.Failed));
                    }
                }

                // MergeAll：源 Pak 独有的普通文件也并入（主 Pak 已有的普通文件不覆盖）。
                if (options.Mode == PakConversionMode.MergeAll)
                {
                    foreach (PakRawFileCopy file in sourceFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (IsTexturePackagePath(file.PakPath) && targetTexturePackages.Contains(file.PakPath))
                            continue; // 已由纹理替换逻辑处理

                        if (!output.ContainsKey(file.PakPath))
                        {
                            output[file.PakPath] = new ModifiedPakFile(file.DiskPath, file.PakPath);
                            otherIncluded++;
                        }
                    }
                }
            }

            // ── 3. 打包输出 ─────────────────────────────────────────────────
            // ReplacedOnly 模式下 output 里只有本次改动过的资产 —— 这正是模组该有的内容。
            if (output.Count == 0)
            {
                throw new InvalidOperationException(
                    "没有任何文件需要写入输出 Pak：待转换 Pak 里的纹理都没能在主 Pak 中找到同路径项，" +
                    "或全部解像素失败。请检查两侧 Pak 是否为同一游戏版本，以及映射文件是否匹配。");
            }

            progress?.Report(new PakConversionProgress(1, 1,
                options.Mode == PakConversionMode.ReplacedOnly
                    ? $"正在打包输出 Pak（仅包含 {output.Count:N0} 个改动文件）…"
                    : "正在打包输出 Pak…"));

            string packTarget = options.OutputPakPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(packTarget))!);

            ModifiedPakFile[] filesToPack = output.Values
                .OrderBy(f => f.PakPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await Task.Run(() => ModifiedPakPackService.Pack(new ModifiedPakRequest(
                filesToPack,
                packTarget,
                UseCompression: options.UseOodleCompression,
                Compression: PakCompression.Oodle)), cancellationToken).ConfigureAwait(false);

            return new PakConversionResult(
                packTarget,
                new PakConversionCounts(
                    output.Count,
                    replaced + missing + skipped + failed,
                    replaced,
                    missing,
                    skipped,
                    failed,
                    otherIncluded),
                items);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    /// <summary>
    /// 转换单个纹理。两条路径：
    ///
    /// <b>快速路径（优先）</b>：源与目标的纹理格式/mip 布局一致时，直接搬运已压缩的
    /// mip 载荷。无损、无需映射文件、速度快，是"两端同平台同格式"这类最常见情形的解法。
    ///
    /// <b>重编码路径</b>：格式不同（例如把别的平台的 BC 贴图转成本平台的 ASTC）时，
    /// 必须解码成像素再按目标格式编码。此路径需要映射文件才能反序列化未版本化资产。
    /// </summary>
    private async Task<TextureConversionOutcome> ConvertTextureAsync(
        PakArchiveSession sourceSession,
        PakArchiveSession targetSession,
        PakRawFileCopy sourceAsset,
        Dictionary<string, ModifiedPakFile> output,
        PakConversionOptions options,
        string tempRoot,
        CancellationToken cancellationToken)
    {
        // 主 Pak 那份模板 .uasset —— 它的格式就是输出格式。
        //
        // ReplacedOnly 模式下主 Pak 没有全量解包（省下与主 Pak 等量的磁盘与时间），
        // 所以这里按需只把这一份资产的三件套取出来。
        string outputDir = Path.Combine(tempRoot, "converted", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);
        string outputAssetPath = Path.Combine(outputDir, Path.GetFileName(sourceAsset.PakPath));
        string? mappings = string.IsNullOrWhiteSpace(options.MappingsPath) ? null : options.MappingsPath;
        var service = new TextureReplacementService();

        ModifiedPakFile? template;
        if (output.TryGetValue(sourceAsset.PakPath, out ModifiedPakFile? alreadyUnpacked))
        {
            template = alreadyUnpacked;
        }
        else
        {
            // 目标 Pak 的 uasset/uexp/ubulk 必须同目录，替换逻辑才能找到兄弟文件。
            string templateDir = Path.Combine(tempRoot, "template", Guid.NewGuid().ToString("N"));
            IReadOnlyDictionary<string, string> extracted = await targetSession
                .ExtractAssetsAsync([sourceAsset.PakPath], templateDir, cancellationToken)
                .ConfigureAwait(false);

            if (!extracted.TryGetValue(sourceAsset.PakPath, out string? templateDisk))
            {
                return new TextureConversionOutcome(PakConversionItemStatus.MissingInTarget);
            }

            template = new ModifiedPakFile(templateDisk, sourceAsset.PakPath);
        }

        // ── 快路径：两侧格式与 mip 布局一致时，直接搬运压缩载荷 ──────────────
        //
        // 无损、不需要映射文件、不需要外部编码器。这种情况出现在"源 Pak 与主 Pak
        // 同平台同格式"（例如把 PC 端自己的贴图包合进来）。能用就走它。
        try
        {
            TextureReplacementResult? copied = service.TryReplaceWithRawMips(
                sourceAsset.DiskPath,
                template.DiskPath,
                outputAssetPath,
                options.Engine,
                mappings);

            if (copied is not null)
            {
                MapOutputFiles(sourceAsset.PakPath, copied, output);
                return new TextureConversionOutcome(PakConversionItemStatus.Replaced, PakConversionMethod.RawCopy);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 布局探测失败不是致命错误：下面还有"解像素 → 重编码"这条路。
            TryDeleteFile(outputAssetPath);
        }

        // ── 主路径：解出源像素，按主 Pak 资产的格式重新编码写回 ──────────────
        //
        // 这是"手机端资产 → PC 端资产"的核心步骤：源是 ASTC，主 Pak 是 BC，
        // 格式与载荷长度都对不上，只能走像素级的重编码。
        TexturePreviewDto? pixels;
        try
        {
            pixels = await sourceSession
                .TryReadFullTextureAsync(sourceAsset.PakPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TextureConversionOutcome(
                PakConversionItemStatus.Skipped,
                Message: DescribeDecodeFailure(ex.Message, mappings is not null));
        }

        if (pixels is null || pixels.PngData.Length == 0)
        {
            return new TextureConversionOutcome(
                PakConversionItemStatus.Skipped,
                Message: DescribeDecodeFailure(null, mappings is not null));
        }

        // 源像素落盘成 PNG，供平台编码器读取。
        string pngPath = Path.ChangeExtension(outputAssetPath, ".png");
        await File.WriteAllBytesAsync(pngPath, pixels.PngData, cancellationToken).ConfigureAwait(false);

        // 输出格式由主 Pak 资产的头部决定；读出来用于日志与结果说明。
        string? targetFormat = null;
        try
        {
            targetFormat = TextureAssetParser.Load(template.DiskPath, options.Engine, mappings).Format.Name;
        }
        catch
        {
            // 读不到格式名不影响转换本身，只是说明文字少一项。
        }

        if (!CanReEncodeCrossFormat)
        {
            return new TextureConversionOutcome(
                PakConversionItemStatus.Skipped,
                Message: "两侧纹理格式不同，需要重新编码，但当前环境没有可用的编码器" +
                         "（桌面端需要 UAssetCLI + astcenc/texconv）。");
        }

        string? error;
        if (OperatingSystem.IsAndroid())
        {
            // Android：进程内编码（libprism_codecs 处理 ASTC/BC，BC1 走内置编码器）。
            try
            {
                TextureReplacementResult result = await service.ReplaceAsync(
                    template.DiskPath,
                    pngPath,
                    outputAssetPath,
                    options.Engine,
                    mappings,
                    new TextureCodecOptions(AstcQuality: options.AstcQuality),
                    cancellationToken).ConfigureAwait(false);

                MapOutputFiles(sourceAsset.PakPath, result, output);
                return new TextureConversionOutcome(
                    PakConversionItemStatus.Replaced, PakConversionMethod.ReEncode, TargetFormat: targetFormat);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
        else
        {
            // Windows：交给 UAssetCLI（DXT1 内部编码，ASTC 用 astcenc，BC7/DXT5 用 texconv）。
            error = await EncodeIntoAssetAsync!(
                template.DiskPath,
                pngPath,
                outputAssetPath,
                cancellationToken).ConfigureAwait(false);
        }

        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        MapOutputFiles(sourceAsset.PakPath, outputAssetPath, output);
        return new TextureConversionOutcome(
            PakConversionItemStatus.Replaced, PakConversionMethod.ReEncode, TargetFormat: targetFormat);
    }

    /// <summary>单个纹理的转换结果：状态 + 使用的方式 + 说明 + 目标格式。</summary>
    private sealed record TextureConversionOutcome(
        PakConversionItemStatus Status,
        PakConversionMethod Method = PakConversionMethod.None,
        string? Message = null,
        string? TargetFormat = null);

    /// <summary>
    /// 把"解不出源纹理像素"的底层错误翻译成可操作的说明。
    ///
    /// 这一步是跨格式转换最容易卡住的地方，而底层报错（CUE4Parse / UAssetAPI 的
    /// 原始消息）通常看不出该怎么办，所以这里按最常见的几种原因给出下一步动作。
    /// </summary>
    private static string DescribeDecodeFailure(string? rawMessage, bool hasMappings)
    {
        const string Prefix = "无法解出源纹理像素";

        if (!string.IsNullOrWhiteSpace(rawMessage))
        {
            // 未版本化属性必须先有映射：这是最常见的原因。
            if (rawMessage.Contains("mapping file is missing", StringComparison.OrdinalIgnoreCase) ||
                rawMessage.Contains("unversioned", StringComparison.OrdinalIgnoreCase))
            {
                return hasMappings
                    ? $"{Prefix}：所选映射文件与该 Pak 不匹配（资产提示缺少映射）。请确认映射与游戏版本一致。"
                    : $"{Prefix}：该 Pak 的资产使用未版本化属性，需要映射文件。请在设置页选择对应的 .usmap/.jmap 后重试。";
            }

            if (rawMessage.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                rawMessage.Contains("was not found", StringComparison.OrdinalIgnoreCase))
            {
                return $"{Prefix}：源 Pak 中找不到该资产的配套文件（.uexp/.ubulk 可能缺失）。";
            }

            return $"{Prefix}：{rawMessage}";
        }

        // 没有异常但也没解出像素：多半不是纹理资产。
        return hasMappings
            ? $"{Prefix}：该资产不是纹理（或纹理数据为空），已跳过。"
            : $"{Prefix}：该资产不是纹理，或需要映射文件才能解析。请在设置页选择映射文件后重试。";
    }

    /// <summary>把进程内替换产出的三个文件映射回 Pak 内路径。</summary>
    private static void MapOutputFiles(
        string sourcePakPath,
        TextureReplacementResult result,
        Dictionary<string, ModifiedPakFile> output) =>
        MapOutputFiles(sourcePakPath, result.AssetPath, output, result.UbulkPath);

    /// <summary>
    /// 按 <paramref name="outputAssetPath"/> 推断同目录的 .uexp/.ubulk 并映射回 Pak 内路径。
    /// </summary>
    private static void MapOutputFiles(
        string sourcePakPath,
        string outputAssetPath,
        Dictionary<string, ModifiedPakFile> output,
        string? knownUbulkPath = null)
    {
        MapFile(outputAssetPath, ".uasset");

        string uexpPath = Path.ChangeExtension(outputAssetPath, ".uexp");
        MapFile(uexpPath, ".uexp");

        string ubulkPath = knownUbulkPath ?? Path.ChangeExtension(outputAssetPath, ".ubulk");
        MapFile(ubulkPath, ".ubulk");

        void MapFile(string diskPath, string extension)
        {
            if (!File.Exists(diskPath))
                return;

            string pakPath = PakPathFor(sourcePakPath, extension);
            output[pakPath] = new ModifiedPakFile(diskPath, pakPath);
        }
    }

    /// <summary>
    /// 由纹理包路径 + 目标扩展名推出 Pak 内路径。
    /// 例如 <c>Game/X/T_Foo_P.uasset</c> + <c>.ubulk</c> → <c>Game/X/T_Foo_P.ubulk</c>。
    /// </summary>
    private static string PakPathFor(string packagePath, string extension)
    {
        string current = Path.GetExtension(packagePath);
        return current.Length == 0
            ? packagePath + extension
            : packagePath[..^current.Length] + extension;
    }

    /// <summary>同一纹理包的 .uasset/.uexp/.ubulk 兄弟文件。</summary>
    private static IEnumerable<PakRawFileCopy> RelatedFilesOf(string packagePath, IReadOnlyList<PakRawFileCopy> all)
    {
        string stem = Path.GetFileNameWithoutExtension(packagePath);
        string? directory = Path.GetDirectoryName(packagePath)?.Replace('\\', '/');
        string prefix = string.IsNullOrEmpty(directory) ? stem : $"{directory}/{stem}";

        foreach (PakRawFileCopy file in all)
        {
            if (file.PakPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && file.PakPath.Length > prefix.Length
                && file.PakPath[prefix.Length] == '.')
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// 统一 Pak 内路径形态：反斜杠转正斜杠、去掉前导斜杠。
    /// <c>ListRawFilePathsAsync</c> 返回的路径可能带前导 '/'，
    /// 而打包服务写入的是不带前导斜杠的形态，比较前必须归一化。
    /// </summary>
    private static string NormalizePakPath(string pakPath) =>
        pakPath.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// 是否符合纹理资产的 <c>_P</c> 命名约定。
    /// 注意这只是<b>候选筛选</b>之一，不是最终判定 —— 最终由实际解码决定。
    /// </summary>
    private static bool IsTexturePackagePath(string pakPath)
    {
        if (!pakPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            return false;

        string stem = Path.GetFileNameWithoutExtension(pakPath);
        return stem.EndsWith("_P", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 纹理候选筛选：<c>_P.uasset</c> 约定，或任意 <c>.uasset</c> 且存在同名 <c>.uexp</c>。
    ///
    /// 加第二条判据是为了不依赖命名约定：很多游戏不用 <c>_P</c> 后缀，
    /// 但所有 cooked 资产都有 <c>.uexp</c>。这样转换能覆盖更多游戏，
    /// 代价是会把少量非纹理资产也纳入候选（解码失败会被记为"跳过"，不影响结果）。
    /// </summary>
    private static bool IsTextureCandidate(string pakPath, IReadOnlySet<string> allPaths)
    {
        if (IsTexturePackagePath(pakPath))
            return true;

        if (!pakPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            return false;

        return allPaths.Contains(PakPathFor(pakPath, ".uexp"));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响转换结果。
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 清理失败不影响后续流程（会被覆盖写入）。
        }
    }
}
