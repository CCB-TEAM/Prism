namespace Prism.Desktop.Docs;

/// <summary>文档里的一块内容。<see cref="Views.HelpView"/> 按具体类型挑模板渲染。</summary>
public abstract record DocBlock;

/// <summary>小标题：把一章切成几节（"配置 / 浏览 / 替换"）。</summary>
public sealed record DocHeading(string Text) : DocBlock;

/// <summary>一段正文。</summary>
public sealed record DocParagraph(string Text) : DocBlock;

/// <summary>编号步骤：有先后顺序的操作。</summary>
public sealed record DocSteps(IReadOnlyList<string> Items) : DocBlock
{
    private IReadOnlyList<DocNumberedStep>? _numbered;

    /// <summary>带序号的展开形式（视图直接绑它，编号由数据给出，模板里不用数行数）。</summary>
    public IReadOnlyList<DocNumberedStep> Numbered =>
        _numbered ??= Items.Select((text, index) => new DocNumberedStep(index + 1, text)).ToList();
}

public sealed record DocNumberedStep(int Number, string Text)
{
    public string NumberText => Number.ToString("D2");
}

/// <summary>要点列表：平级的注意项。</summary>
public sealed record DocBullets(IReadOnlyList<string> Items) : DocBlock;

/// <summary>截图 + 图注。<see cref="ImageKey"/> 对应 Assets/help/{light|dark}/{key}.png。</summary>
public sealed record DocFigure(string ImageKey, string Caption) : DocBlock;

/// <summary>提示块：一句容易被忽略、但踩到会浪费时间的话。</summary>
public sealed record DocCallout(string Title, string Text) : DocBlock;

/// <summary>一章文档。</summary>
public sealed record DocChapter(
    string Key,
    string Label,
    string Title,
    string Summary,
    IReadOnlyList<DocBlock> Blocks);

/// <summary>
/// 使用说明的全部内容。
///
/// 为什么把文案放进 C# 而不是 XAML：
///   · 原来的浮层是几十个 <c>StackPanel</c> + <c>TextBlock</c>，改一句话要在一大段标签里
///     找到对应那行；现在一章就是一条记录，文案可以整段重写；
///   · 内容可以断言（测试检查「每章都有内容、每个图都真有文件」），不会出现点了没反应的章节；
///   · 版式只有一份（HelpView 的模板），十章共用一个排版，改版式不用改十章。
/// </summary>
public static class PrismDocs
{
    public static IReadOnlyList<DocChapter> Chapters { get; } =
    [
        new DocChapter(
            Key: "start",
            Label: "快速开始",
            Title: "从打开一个 Pak 到装进游戏",
            Summary: "三个工作区各管一段路：先看清包里有什么，改完打成补丁包，最后把包放进游戏的 Paks 目录。全程在本机完成。",
            Blocks:
            [
                new DocSteps(
                [
                    "「解包 & 模组 › 配置」里选 Pak 文件与映射文件，点「打开 Pak」。第一次建议先拿一个小的测试包试手。",
                    "「浏览」里找到要改的资产，在预览栏确认是它，再点「加入替换」。",
                    "切到「替换」，逐项给出新文件：贴图选一张图片，音频选 wav，本地化直接改文本。放好后点「构建 Pak」。",
                    "把生成的 .pak 放进游戏的 Paks 目录（例如 <游戏目录>/Kards/Content/Paks/），重启游戏。",
                ]),
                new DocCallout(
                    "补丁是叠加的，不必覆盖原包",
                    "游戏会同时加载多个 Pak，同路径的资产以后加载的那个为准。所以输出一个小补丁包放进 Paks 就够了 —— 原来的大包可以一直不动。"),
                new DocHeading("界面分区"),
                new DocFigure("welcome", "欢迎页：左侧常驻导航，正文把四个工作区的分工列清楚"),
                new DocBullets(
                [
                    "左侧是常驻导航：欢迎、解包 & 模组、Pak 合并、Pak 转换、使用说明、设置。窗口窄于 980 px 时导航自动收起，改用页头的返回箭头。",
                    "页头右侧的「使用说明」按钮总是跳到当前页面对应的那一章，不用自己找。",
                    "底部状态栏常驻：当前状态、进度与失败原因都在那里，出问题先看它。",
                ]),
                new DocHeading("开始之前"),
                new DocBullets(
                [
                    "映射文件（.usmap / .jmap）：读取蓝图属性、跨平台转换要用。在「配置」里选一次即可，两个平台导出的映射通用。",
                    "AES 密钥：只有加密 Pak 需要，明文保存在本机设置文件里。",
                    "编码器：桌面端的贴图替换依赖随包的 UAssetCLI 与 tools/ 里的 astcenc、texconv；Android 端在应用内完成编码。",
                ]),
            ]),

        new DocChapter(
            Key: "workspace",
            Label: "解包 & 模组",
            Title: "浏览、预览、导出，以及把改动攒成补丁",
            Summary: "这是用得最多的一个工作区：左边是包内文件，右边是选中资产的预览；所有改动都先进入「替换」列表，最后一次性打包。",
            Blocks:
            [
                new DocHeading("配置"),
                new DocFigure("workspace-config", "配置：Pak、映射文件、AES 与导出目录都只在这里填一次"),
                new DocBullets(
                [
                    "Pak 文件：可以从任意位置选，应用会自己处理读写。",
                    "映射文件：可选。缺它仍能浏览与导出，但蓝图属性解析和跨平台转换会受限。",
                    "导出：「原始」导出资产本身，「替换」导出预览图（贴图直接给 PNG），手机上还能直接调系统分享。",
                    "「更多」菜单里的「导出当前文件夹」会把当前目录递归导出，并保留包内的目录结构。没有设过导出目录时会当场弹一次选择器，选完就记住。",
                ]),
                new DocHeading("浏览与预览"),
                new DocFigure("workspace-browse", "浏览：搜索框既可搜关键词，也可直接粘 Pak 内路径跳转"),
                new DocBullets(
                [
                    "搜索框输入 kards/Content/Assets/Textures 这类路径会直接跳转到该目录，输入关键词则是全文搜索。",
                    "贴图、音频、视频、模型、本地化都有内置预览：贴图看原图，音频能播放，视频能拖时间轴，模型是线框图。",
                    "列表左侧的缩略图可以在「设置 › 行为」里关掉，低配设备会明显更顺。",
                ]),
                new DocHeading("替换列表"),
                new DocFigure("workspace-replace", "替换：一个资产一条任务，状态（待替换 / 已替换 / 已编辑 / 失败）写在行上"),
                new DocBullets(
                [
                    "同一路径重复加入会合并成一条，避免打出一个自相矛盾的包。",
                    "失败的那一条不会中断其它项，原因写在行上；构建前可以移除它再打一次。",
                    "「Oodle 压缩」开关就在这个页面上：它只影响本次打包，所以放在操作旁边而不是设置里。",
                ]),
            ]),

        new DocChapter(
            Key: "project",
            Label: "模组工程",
            Title: "把「这次改了什么」存成文件，改天接着改",
            Summary: "工程文件（.prismproj）记的是配方：改哪些资产、用哪些素材、什么参数。构建时重新解包、重新编码，所以游戏更新之后旧工程照样能出包。",
            Blocks:
            [
                new DocHeading("什么时候用"),
                new DocBullets(
                [
                    "一次改十几项、要分几次做完的时候。",
                    "游戏更新、原来的包换了版本，需要把同一批替换再做一遍的时候。",
                    "想把模组分享出去、或者换一台机器接着做的时候。",
                ]),
                new DocHeading("怎么存"),
                new DocSteps(
                [
                    "在「解包 & 模组」里把替换列表攒好，贴图、音频、视频、本地化可以混在一起。",
                    "点页头右上角的工程按钮（没打开工程时显示「工程」，有改动时显示「未保存的改动」），选「保存当前工程」或「另存为…」。",
                    "选个位置保存。素材会同时复制到旁边的 <工程名>.assets/ 目录，整个目录可以搬走或发给别人。",
                    "之后点「保存当前工程」或按 Ctrl+S 就是原地更新；Ctrl+O 打开工程。",
                ]),
                new DocCallout(
                    "不用先改点什么才能开工",
                    "这个按钮一直在。想先建个空工程也可以：一份替换项都没有时照样能保存，工程会记下 Pak、映射与输出路径，下次打开直接接着加。"),
                new DocHeading("打开工程时会发生什么"),
                new DocBullets(
                [
                    "重新打开工程里记的 Pak，再逐项把替换重做一遍 —— 所以素材文件得还在。",
                    "缺素材、或者资产在 Pak 里没了（游戏更新过），只在那一行上标「失败」并写明原因，其它项照常还原。",
                    "本地化只存改动过的词条，打开时套在当前 .locres 的原文上；游戏改过的词条不会被工程里的旧译文盖掉。",
                    "桌面端关窗口时有未保存的改动会问一次；欢迎页的「最近工程」可以直接接着打开。",
                ]),
                new DocCallout(
                    "工程里没有 AES 密钥",
                    "密钥只存在本机设置里。把工程发给别人时，对方要自己填一次密钥 —— 这是故意的：密钥不该跟着工程文件到处跑。"),
            ]),

        new DocChapter(
            Key: "texture",
            Label: "贴图替换",
            Title: "换一张贴图，格式交给应用判断",
            Summary: "你只要给一张图片，剩下的按原资产的格式与尺寸重新编码：DXT1 / BC7 走 texconv，ASTC 走 astcenc，未压缩格式由应用自己打包。",
            Blocks:
            [
                new DocHeading("能换哪些"),
                new DocBullets(
                [
                    "PF_DXT1 / PF_BC1：桌面端用内置编码器，不需要外部工具。",
                    "PF_BC7、PF_ASTC_*：分别需要 tools/texconv.exe、tools/astcenc-avx2.exe（发行包已随附）。",
                    "未压缩格式（PF_B8G8R8A8、PF_R8G8B8A8、PF_A8R8G8B8）：按像素直接打包，非 2 的幂尺寸也能换。",
                ]),
                new DocHeading("选图建议"),
                new DocBullets(
                [
                    "尺寸不必和原图一致：会按原贴图的尺寸拉伸（Lanczos3）。想不变形就先把图片裁成同样的宽高比。",
                    "所有 mip 都会一起重算，所以输出里不会出现「小 mip 还是旧图」的闪烁。",
                    "透明通道会被保留（BC1 除外 —— 它没有 alpha）。",
                ]),
                new DocCallout(
                    "报错「Could not detect a supported pixel format」？",
                    "这是 1.2.2 及更早版本的问题：桌面端把替换交给 UAssetCLI，而它自带一份旧的格式表，遇到未压缩格式和非 2 次幂尺寸就认不出。1.2.3 起 CLI 直接引用同一份解析实现，两侧不会再判得不一样。"),
            ]),

        new DocChapter(
            Key: "audio",
            Label: "音频与视频",
            Title: "声音换素材，影片换字节",
            Summary: "音频要重新编码成游戏自己的 BINKA 流，所以给 wav；影片是整段字节替换，给 mp4 即可。两者都在「替换」列表里处理。",
            Blocks:
            [
                new DocHeading("音频"),
                new DocBullets(
                [
                    "支持 wav 直接输入；mp3 / m4a / flac 等由系统解码器或 ffmpeg 先转成 PCM，再编码成 BINKA。",
                    "编码质量 0（最好、最占空间）到 9（最省）。",
                    "编码后超过 256 KB 会自动切成多个流式块，和游戏自己切块的方式一致，长度不受限。",
                    "时长与原音频不同也可以：资产里的时长字段会一起改写。",
                    "资产自带 cue point（例如音乐里的 Tempo 标记）时会原样保留，只是时间线还是原来那一段的。",
                ]),
                new DocHeading("视频与影片"),
                new DocBullets(
                [
                    "影片文件（.mp4）是整段字节替换，不改资产结构，所以换更大更小的文件都行。",
                    "预览栏可以播放、暂停、拖时间轴；不需要装额外的播放器。",
                    "没有 ffmpeg 的平台上，视频预览会退回成只显示封面帧。",
                ]),
            ]),

        new DocChapter(
            Key: "locres",
            Label: "本地化",
            Title: "改词条，而不是改二进制",
            Summary: "把 .locres 加入替换列表后会变成翻译编辑器：左侧词条、右侧原文，改完回车写回，再构建补丁 Pak。",
            Blocks:
            [
                new DocHeading("直接改"),
                new DocBullets(
                [
                    "在替换页选中 .locres，按关键词搜索词条；列表带分页，几千条词条也不会卡。",
                    "改完按回车或点其它地方即写回，行状态从「待替换」变成「已编辑」。",
                    "「导出 JSON」可以把全部词条导出，交给别人翻译后再导回。",
                ]),
                new DocHeading("多份模组的本地化怎么合"),
                new DocBullets(
                [
                    "两份文本模组可能各改了不同的词条，直接覆盖会丢掉一份。",
                    "在「Pak 合并」里指定一份原版 .locres 作为基准，同名的 .locres 就会逐条三路合并：谁改过就取谁的，冲突时按优先级。",
                    "原版 .locres 从游戏原版 Pak 里导出一次即可，长期复用。",
                ]),
            ]),

        new DocChapter(
            Key: "merge",
            Label: "Pak 合并",
            Title: "多个包合成一个，顺序就是优先级",
            Summary: "把要合并的 Pak 依次加入列表，越靠上优先级越高；同名路径以靠上的那份为准。",
            Blocks:
            [
                new DocFigure("merge", "合并：列表可以拖动排序，优先级就是从上到下的顺序"),
                new DocSteps(
                [
                    "点「添加 Pak」多选文件，或者把文件拖进窗口。",
                    "拖动行调整顺序：第 1 项最高。",
                    "可选：指定基准 .locres，让同名本地化做逐条合并而不是整份覆盖。",
                    "选输出路径，点「开始合并」。",
                ]),
                new DocCallout(
                    "冲突提示",
                    "存在同名路径时可以在「设置 › 行为」里打开「合并前询问冲突」，先看一眼有多少冲突再决定要不要调整顺序。"),
            ]),

        new DocChapter(
            Key: "convert",
            Label: "Pak 转换",
            Title: "把手机包的贴图搬到 PC 包，或反过来",
            Summary: "同一个资产在两个平台上可能是不同的纹理格式（ASTC 对 BC7）。转换会解出源像素、按目标包的格式重编码，再打成新 Pak。",
            Blocks:
            [
                new DocFigure("convert", "转换：主 Pak 决定输出格式，待转换 Pak 提供新贴图"),
                new DocSteps(
                [
                    "主 Pak：本平台要装进游戏的那个包（它决定输出的纹理格式）。",
                    "待转换 Pak：另一个平台的包（它提供新贴图）。",
                    "输出：选保存位置，点「开始转换」，完成后把输出包放进 Paks 目录。",
                ]),
                new DocHeading("输出里装什么"),
                new DocBullets(
                [
                    "默认只装转换过的贴图，体积≈改动量。",
                    "「并入模组的其他文件」：再带上源包里的蓝图、本地化、数据表等双端通用内容，开了它就不必再单独做一次 Pak 合并。",
                    "「输出完整整包」：连主 Pak 的其他文件一起打包，体积≈主 Pak，只在需要「用一个包替换整包」时用。",
                ]),
                new DocCallout(
                    "转换需要映射文件",
                    "两侧贴图格式不同时必须解出源像素，这一步需要 .usmap / .jmap。缺映射的贴图会被跳过并在明细里写明原因，不会静默丢文件。"),
            ]),

        new DocChapter(
            Key: "settings",
            Label: "设置与更新",
            Title: "全局偏好、更新与诊断",
            Summary: "设置页只放「全局」的东西：主题、行为、更新、依赖状态与日志。Pak 路径这类输入跟着各自的业务页走，不在这里重复一份。",
            Blocks:
            [
                new DocFigure("settings", "设置：分组卡片 + 行，右侧是开关与动作"),
                new DocBullets(
                [
                    "外观：主题（跟随系统 / 浅色 / 深色）与界面过渡动画。",
                    "行为：纹理缩略图、合并前询问冲突。",
                    "依赖与诊断：UAssetCLI、astcenc、texconv、Oodle 是否就位，一眼能看出缺哪一样。",
                    "日志：最近 400 行；出问题时点「导出日志」把完整报告发出来。",
                ]),
                new DocHeading("检查更新"),
                new DocFigure("update", "查到新版本时，欢迎页会换成这一版的更新说明"),
                new DocBullets(
                [
                    "启动时（默认开启，6 小时内只查一次）去 GitHub 查最新发行版。",
                    "查到新版本：桌面端把欢迎页换成更新说明；手机端弹一次可跳过的提示。",
                    "桌面端点「下载并自动更新」会下载、校验、解压，然后重启应用完成文件替换；手机端下载 APK 后拉起系统安装器。",
                    "点过「稍后」的版本不再打扰；想重新看，到「设置 › 更新」点「检查更新」。",
                ]),
            ]),

        new DocChapter(
            Key: "faq",
            Label: "常见问题",
            Title: "出问题时先看这里",
            Summary: "下面这些是实际被问到最多的几件事。日志里通常已经有答案，设置页的「导出日志」能直接给出报告。",
            Blocks:
            [
                new DocHeading("点了导出没反应？"),
                new DocParagraph("导出需要一个目标目录。没设置过的话，应用会在你第一次点导出时当场问一次（或者到「配置 › 导出」里先选好），选完会记住；状态栏同时会显示导出了几个文件、输出到哪个目录。"),
                new DocHeading("提示未找到 UAssetCLI"),
                new DocParagraph("桌面端的贴图替换依赖主程序旁边的 UAssetCLI/ 目录。如果你是从压缩包里单独拷了 exe 出来，它会找不到；整个目录解压到同一处即可。设置页「依赖与诊断」里能看到具体状态。"),
                new DocHeading("勾了 Oodle 之后程序直接消失"),
                new DocParagraph("1.2.1 及更早版本在缺 oo2core 原生库时，repak 会 panic 并 abort 进程，.NET 拦不住。现在会先探测，缺库时自动降级为不压缩并在状态栏说明。"),
                new DocHeading("手机上和电脑上操作一样吗"),
                new DocParagraph("界面是同一份，手机上窄屏会变成单列。文件通过系统选择器授权访问，导出走目录授权，不需要「所有文件」权限。"),
                new DocHeading("手机上选的 Pak 存在哪？"),
                new DocParagraph("系统选择器给的是 content:// 授权，没有本地路径，所以应用会先复制一份到自己私有目录再读。你选的 Pak 与映射放在 paks/，素材副本放在 picked/：启动时只清理 picked/ 里没人用的素材，paks/ 不会被自动删除，换一个才会删掉旧的。想腾空间就到系统设置里清除应用数据（导出的文件在外部目录，不受影响）。"),
                new DocHeading("更新检查失败"),
                new DocParagraph("查更新走的是 GitHub 的公开接口，公司网络或代理下可能连不上。这不影响任何本地功能；也可以直接到 GitHub 的 Releases 页面手动下载。"),
                new DocHeading("点开文件报「Read size is bigger than remaining archive length」"),
                new DocParagraph("这是 UE5.5 之前的引擎 cook 出来的包：摘要里不写版本号，布局也比现在的包少一段（没有 SavedHash）。1.3.7 及更早的版本一律按新格式读，于是整包资产都打不开，但列表看起来又完全正常。现在打开 Pak 时会先抽几个资产头看一眼，认出来是这批老包就自动按 UE5.4 的格式读，状态栏会写明「已按 UE5.4 的格式读」。"),
            ]),
    ];
}
