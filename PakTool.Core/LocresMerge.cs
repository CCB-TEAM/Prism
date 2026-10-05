namespace PakTool.Core;

/// <summary>本地化三路合并的结果统计。</summary>
public sealed record LocresMergeReport(
    /// <summary>输出词条总数。</summary>
    int TotalEntries,
    /// <summary>与基准相比文本确实被改动过的词条数（即本次合并实际生效的改动）。</summary>
    int ChangedFromBaseline,
    /// <summary>被多于一份模组改动过的词条数（冲突，按优先级取高者）。</summary>
    int Conflicts,
    /// <summary>基准里没有、由模组新增的词条数。</summary>
    int AddedByMods,
    /// <summary>基准里有、但模组文件里找不到的词条数（版本不一致的信号）。</summary>
    int MissingFromMods);

/// <summary>
/// 本地化（.locres）三路合并。
///
/// <b>为什么需要"基准"</b>：两份文本模组对<b>每一条</b>词条都有值（改过的与没改的
/// 混在一起）。只拿这两份文件对比，无法判断某个词条究竟是谁改的 —— 例如两边不同时，
/// 可能是 A 改的，也可能是 B 改的。只有拿到<b>原版</b>才能算出各自的"改动集"，
/// 进而把两份改动叠加成一份。
///
/// 没有基准时上层会退回"整文件覆盖"（按优先级取一份），本类不做那种事。
/// </summary>
public static class LocresMerge
{
    /// <summary>
    /// 把多份模组的本地化合并成一份。
    /// </summary>
    /// <param name="baselineLocres">原版（游戏自带）的 .locres 字节。</param>
    /// <param name="modsHighestFirst">
    /// 各模组的 .locres 字节，<b>按优先级从高到低</b>排列。第 0 份决定输出的
    /// 版本与词条集合（它是最优先的那份文件，形状应尽量保持）。
    /// </param>
    /// <param name="report">合并统计。</param>
    public static byte[] Merge(
        byte[] baselineLocres,
        IReadOnlyList<byte[]> modsHighestFirst,
        out LocresMergeReport report)
    {
        ArgumentNullException.ThrowIfNull(baselineLocres);
        ArgumentNullException.ThrowIfNull(modsHighestFirst);
        if (modsHighestFirst.Count == 0)
        {
            throw new ArgumentException("至少需要一份模组本地化文件。", nameof(modsHighestFirst));
        }

        LocresPreviewDto baseline = LocresResourceCodec.Read(baselineLocres);

        // 用 (命名空间, 键) 作身份：与 locres 版本无关，跨版本也能对上。
        var baselineText = new Dictionary<(string Ns, string Key), string>();
        foreach (LocresEntryDto entry in baseline.Entries)
        {
            baselineText[(entry.Namespace, entry.Key)] = entry.Text;
        }

        // 逐份算出"改动集"：与基准不同才算改动。
        var deltas = new List<Dictionary<(string Ns, string Key), string>>(modsHighestFirst.Count);
        var seenInMods = new HashSet<(string Ns, string Key)>();
        foreach (byte[] modBytes in modsHighestFirst)
        {
            LocresPreviewDto mod = LocresResourceCodec.Read(modBytes);
            var delta = new Dictionary<(string Ns, string Key), string>();
            foreach (LocresEntryDto entry in mod.Entries)
            {
                (string Ns, string Key) key = (entry.Namespace, entry.Key);
                seenInMods.Add(key);
                if (!baselineText.TryGetValue(key, out string? original))
                {
                    continue;   // 基准里没有 → 属于"模组新增"，不算改动
                }

                if (!string.Equals(entry.Text, original, StringComparison.Ordinal))
                {
                    delta[key] = entry.Text;
                }
            }

            deltas.Add(delta);
        }

        // 冲突 = 被多于一份模组改动的词条。
        var hits = new Dictionary<(string Ns, string Key), int>();
        foreach (Dictionary<(string Ns, string Key), string> delta in deltas)
        {
            foreach ((string Ns, string Key) key in delta.Keys)
            {
                hits[key] = hits.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }

        int conflicts = hits.Count(pair => pair.Value > 1);

        // 从最低优先级往高优先级叠加，于是高优先级最后落笔、冲突时胜出。
        var finalText = new Dictionary<(string Ns, string Key), string>(baselineText);
        for (int i = deltas.Count - 1; i >= 0; i--)
        {
            foreach (((string Ns, string Key) key, string text) in deltas[i])
            {
                finalText[key] = text;
            }
        }

        // 输出沿用最高优先级那份的版本与词条集合。
        LocresPreviewDto head = LocresResourceCodec.Read(modsHighestFirst[0]);
        var entries = new LocresEntryDto[head.Entries.Count];
        int changed = 0;
        int added = 0;
        for (int i = 0; i < head.Entries.Count; i++)
        {
            LocresEntryDto entry = head.Entries[i];
            (string Ns, string Key) key = (entry.Namespace, entry.Key);

            if (!baselineText.ContainsKey(key))
            {
                added++;
            }

            string text = finalText.TryGetValue(key, out string? merged) ? merged : entry.Text;
            if (baselineText.TryGetValue(key, out string? original)
                && !string.Equals(text, original, StringComparison.Ordinal))
            {
                changed++;
            }

            entries[i] = entry with { Text = text };
        }

        int missing = baselineText.Keys.Count(key => !seenInMods.Contains(key));

        report = new LocresMergeReport(entries.Length, changed, conflicts, added, missing);
        return LocresResourceCodec.Write(head with { Entries = entries });
    }

    /// <summary>
    /// 判断某个在多份 Pak 中重复出现的路径是否应当走三路合并。
    ///
    /// 三个条件：提供了基准字节、路径是 <c>.locres</c>、文件名与基准一致
    /// （同一份本地化才谈得上逐条合并）。任一不满足都返回 false，
    /// 上层保持"整文件覆盖"语义。
    /// </summary>
    public static bool ShouldMergeConflict(string pakPath, string baselineFilePath, byte[]? baselineLocres)
    {
        return baselineLocres is { Length: > 0 }
            && pakPath.EndsWith(".locres", StringComparison.OrdinalIgnoreCase)
            && MatchesBaselineFileName(pakPath, baselineFilePath);
    }

    /// <summary>按文件名判断某个 Pak 内路径是否对应基准文件（大小写不敏感）。</summary>
    public static bool MatchesBaselineFileName(string pakPath, string baselineFilePath)
    {
        string pakName = Path.GetFileName(pakPath.Replace('\\', '/'));
        string baseName = Path.GetFileName(baselineFilePath);
        return pakName.Length > 0
            && baseName.Length > 0
            && string.Equals(pakName, baseName, StringComparison.OrdinalIgnoreCase);
    }
}
