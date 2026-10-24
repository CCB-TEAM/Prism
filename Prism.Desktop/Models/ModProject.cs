namespace Prism.Desktop.Models;

/// <summary>
/// 模组工程（<c>.prismproj</c>）的磁盘格式。
///
/// 设计上它是一份**配方**而不是快照：只记"改哪个资产、用什么素材、什么参数"，
/// 编码与解包都在构建时重做。所以游戏更新（贴图格式变了、包的布局变了）之后，
/// 旧工程仍然能出包 —— 这是它和"备份一个 pak"的本质区别。
///
/// 因此这里不出现：工作目录、解出来的 .uasset 字节、编码后的 BINKA 流。
/// 也**不出现 AES 密钥** —— 工程是要发给别人的，密钥只留在本机设置里。
/// </summary>
public sealed class ModProject
{
    /// <summary>格式版本。读到更高版本时给一句可读的提示，而不是崩。</summary>
    public int Schema { get; set; } = 1;

    /// <summary>工程类型：目前只有 <c>patch</c>（合并/转换以后再加）。</summary>
    public string Kind { get; set; } = "patch";

    public string Name { get; set; } = string.Empty;

    /// <summary>分发用的元信息：现在不显示，做清单时不用改格式。</summary>
    public string Author { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    /// <summary>写工程时的应用版本，用于排查"旧版本打开新工程"。</summary>
    public string Prism { get; set; } = string.Empty;

    public string Game { get; set; } = "GAME_UE5_6";

    /// <summary>
    /// 基础包（第 1 项）与叠加层。目前只写一项，但格式先按列表留好 ——
    /// 多 Pak 叠加浏览接进来时工程格式不用动。
    /// </summary>
    public List<string> Paks { get; set; } = [];

    public string Usmap { get; set; } = string.Empty;

    /// <summary>上次构建的输出路径（下次构建时作为建议位置）。</summary>
    public string Output { get; set; } = string.Empty;

    public bool Oodle { get; set; }

    public List<ModProjectTask> Tasks { get; set; } = [];

    /// <summary>上次构建的记录，只用于显示，读取时不信任它。</summary>
    public ModProjectBuild? LastBuild { get; set; }
}

/// <summary>工程里的一条替换任务。</summary>
public sealed class ModProjectTask
{
    /// <summary>texture / audio / media / locres。</summary>
    public string Type { get; set; } = "texture";

    /// <summary>Pak 内的资产路径（或可直接替换的裸文件路径）。</summary>
    public string Asset { get; set; } = string.Empty;

    /// <summary>素材文件（贴图/音频/媒体）。本地化不需要。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>音频编码质量 0..9。</summary>
    public int? Quality { get; set; }

    /// <summary>本地化改动集：只存改动过的条目，不存整份 .locres。</summary>
    public List<ModProjectLocresEdit>? Edits { get; set; }

    // ↓ 以下只是显示缓存：读工程时先用它把列表画出来，解包成功后会被真实值覆盖。
    public string? Format { get; set; }

    public string? SizeLabel { get; set; }

    public string? Detail { get; set; }
}

public sealed class ModProjectLocresEdit
{
    public string Ns { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}

public sealed class ModProjectBuild
{
    public string Utc { get; set; } = string.Empty;

    public string Output { get; set; } = string.Empty;

    public int Files { get; set; }

    public long Bytes { get; set; }
}

/// <summary>最近工程卡片（欢迎页）。</summary>
public sealed record RecentProjectItem(string Path, string Name, string MetaText)
{
    public bool IsMissing => !File.Exists(Path);
}
