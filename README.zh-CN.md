# Prism

[English](README.md) · **简体中文**

面向 **Windows 桌面端与 Android** 的 UE `.pak` 资产管理工具，基于 [CUE4Parse](https://github.com/FabianFG/CUE4Parse)，扩展自 [kardswalker/Prism](https://github.com/kardswalker/Prism)。

浏览 pak 内容，预览贴图 / 音频 / 三维网格 / 本地化文本，导出与分享资产，制作贴图 / 音频 / 视频替换 mod，还能在同一游戏的 PC 端与手机端之间互转贴图包。

## 功能

**浏览与预览**
- 通过 CUE4Parse 挂载 UE4/UE5 `.pak`，支持加密 pak 的 AES 密钥。
- 同时支持 `.usmap` 与 `.jmap` 映射文件（含 `.gz`），按扩展名自动识别格式。
- 文件管理器式浏览、关键词搜索，以及**在搜索框里直接输入路径跳转**。
- 把相关的 `.uasset` / `.uexp` / `.ubulk` 归并为一个资产条目。
- 预览贴图（带缩略图）、音频（内置播放器）、三维网格线框、本地化（`.locres`）、蓝图伪代码。
- **音频预览**：播放 / 暂停 / 进度条拖动，BINKA 音频在桌面端也能直接听（`prism_binka.dll` 解码）。
- **视频预览**：内嵌播放（ffmpeg 解帧，`-re` 实时节奏）、封面帧、时长/分辨率、进度条，
  没有 ffmpeg 时给「用系统播放器打开」。
- 导出原始包体、PNG 图片、整个文件夹，或分享到其他应用。

**贴图替换**
- 选中贴图资产、给一张图片，直接生成补丁 pak，无需手工重打包。
- 桌面端经 UAssetCLI + astcenc / texconv 编码；Android 端由 `libprism_codecs` 在进程内编码。
- 未压缩贴图（`PF_B8G8R8A8` / `PF_R8G8B8A8` / `PF_A8R8G8B8`）与 `PF_DXT1` 不需要外部编码器：
  像素直接打包 / 内置 BC1 编码。

**音频替换 / 视频替换**
- 选中流式 `SoundWave`，喂一段素材（WAV / MP3 / OGG，装了 ffmpeg 还能吃 FLAC / M4A），
  编成 BINKA 后写回资产：声道、采样率、时长一并更新，音质 0–9 可调。
- 视频（以及包里任意原始文件）直接按字节替换：选中 `.mp4` / `.bk2` 再给个新文件即可；
  选中 `UFileMediaSource` 资产时会自动定位到它引用的那个视频文件。
- 两者走同一条路：加入替换 → 选择素材 → 构建补丁 pak。
- 长素材会自动切成多个流式块（每块 ≤ 256 KB，和游戏自己的切块方式一致），不再有"只能换 1 分钟以内"的限制。
- 音频编码走内置原生编码器（`prism_encode_binka`：桌面在 `prism_binka.dll`、Android 在
  `libprism_codecs.so`，源码见 [native/BinkaEnc](native/BinkaEnc/README.md)），**两端都能替换音频**；
  找不到原生库时才回退到 `tools/binka_encode.exe`（三方二进制，不入库，见 [tools/README.md](tools/README.md)）。
- 视频替换与音频替换走同一条流程，两端都可用。

**Pak 合并**
- 一次性多选多个 pak，长按拖动调整覆盖优先级（**越靠上优先级越高**）。
  每一项都能拖动、都能移除，没有钉死的"主 Pak"。
- 合并前查看冲突。
- 可选的**基准 `.locres`**：把游戏原版的本地化文件交给它，同名 `.locres` 就会逐条合并
  （而不是整份文件覆盖），于是两份各改了不同文本的模组能真正合到一起。
  不提供基准时退回整文件覆盖。

**Pak 转换（跨端贴图移植）**
- 把某个平台的 pak 里的贴图移植到另一个平台的 pak 中，并按目标资产格式重新编码。
- 默认只输出本次真正改动的资产 —— 7 GB 主 pak 加一张贴图，产出**仅 685 KB** 的 mod pak。
- 可选把模组的其他文件（蓝图、`.locres`、数据表）原样带进输出 pak —— 它们双端通用，
  不需要转换，也就不必再单独做一次合并。
- 原理见 [Pak 转换的工作原理](#pak-转换的工作原理)。

**本地化**
- 在应用内编辑 `.locres` 词条并写回补丁 pak。
- 导出本地化为 JSON，四种 locres 格式版本均可安全往返。

**诊断**
- 结构化日志：级别、时间戳、完整异常栈、落盘镜像，以及带环境头的可导出报告。
- 设置页把外部依赖（UAssetCLI、astcenc、texconv、Oodle）逐条列出并标绿/标红，
  "贴图替换没反应"这类问题一眼能看出缺哪一样。

**更新**
- 启动时查一次 GitHub 发行版（6 小时内只查一次，可关闭）。查到新版本时欢迎页换成
  该版本的更新说明；Android 则是可跳过的弹窗。
- 桌面端**下载后自动替换**：拉取 zip、按发行版给的 SHA-256 校验、解压，再生成一个
  脚本等应用退出 → 备份旧 exe → robocopy 覆盖 → 重新拉起。Android 下载 APK 后
  交给系统安装器。
- 「稍后」按版本号记住，不会反复打扰；想重新看就到「设置 › 更新」点「检查更新」。

## 目录结构

工程都在仓库顶层 —— `PakTool.Core` 等以 `..\external\CUE4Parse`、`..\third_party`
作为同级目录引用，扁平结构才是工程文件期望的形态。

```
Prism/                     Android WebView 应用（早期版本，源自上游）
Prism.PC/                     本地 Web UI 构建
Prism.Desktop/                共享 Avalonia UI 与逻辑（库，net10.0）
  Docs/                         使用说明的文案与截图取用
  Views/HelpView.axaml          说明页（由 Docs/PrismDocs.cs 的数据渲染）
Prism.Desktop.Desktop/        Windows 外壳（单文件发布）
Prism.Desktop.Android/        Android 外壳（APK，arm64-v8a）
PakTool.Core/                 pak 会话 / 预览 / 导出 / 合并 / 映射 / locres
UAssetTexture.Core/           贴图解析 / mip 编码 / 替换（只依赖 UAssetAPI + ImageSharp）
UAssetTexture.PakConversion/  在其之上的 pak 转换（需要 PakTool.Core / CUE4Parse）
UAssetCLI/                    贴图替换命令行工具（引用 UAssetTexture.Core）
test/Prism.FeatureTests/      集成测试（控制台程序）
UAssetAPI-master/             内置改造过的 UAssetAPI
tools/                        astcenc + texconv（构建时复制到应用旁）
third_party/                  Android 原生库（不提交，见下文）
```

以下不随仓库提交，需要时自行放置：

```
external/CUE4Parse/        构建依赖，编译前必须存在
third_party/lib/           Android 构建用的 .so
native/                    那些 .so 的源码（PrismCodecs C++、RepakBind Rust）
reference/                 本地留存的参考克隆（astcenc、FModel 等）
```

`UAssetCLI` 直接引用 `UAssetTexture.Core`，所以「进程内替换」和「CLI 子进程替换」用的是**同一份**
解析器、格式表与 mip 回写代码。前提是 `UAssetTexture.Core` 不依赖 CUE4Parse —— 需要 pak 会话的
逻辑都放在 `UAssetTexture.PakConversion`。这样 AOT 产物依然瘦（包里没有 CUE4Parse），也不会再出现
「同一个修复只落到一条链路上」的情况。

## 下载

预编译包见 [Releases](../../releases)。Windows 压缩包是自包含的（无需安装 .NET 运行时），但**必须把 `UAssetCLI/` 和 `tools/` 与可执行文件放在一起** —— 贴图替换与 pak 转换依赖它们。

## 构建

```sh
# Windows 桌面端（Debug）—— 同时拷贝 UAssetCLI 与 tools/
dotnet build Prism.Desktop.Desktop/Prism.Desktop.Desktop.csproj

# Windows 自包含单文件发布
dotnet publish Prism.Desktop.Desktop/Prism.Desktop.Desktop.csproj \
  -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# Android release APK（需要 JDK 与 Android SDK）
dotnet build Prism.Desktop.Android/Prism.Desktop.Android.csproj \
  -c Release -p:JavaSdkDirectory=<你的 JDK 路径>
```

### 贴图命令行工具（`UAssetCLI/`）

贴图替换与 pak 转换会调用外部程序 `UAssetCLI`，它**不是**项目引用 —— 桌面端构建会把它复制到
可执行文件旁边的 `UAssetCLI/` 目录。请先构建它，并且用 Release，产物才会落在复制目标查找的位置：

```sh
dotnet build UAssetCLI/UAssetCLI.csproj -c Release
```

Release 构建出的 CLI 是**依赖框架**的可执行文件，需要机器上装有 .NET 10 运行时。
想要自包含的原生单文件（需要 AOT 工具链），请发布它：

```sh
dotnet publish UAssetCLI/UAssetCLI.csproj -c Release
```

应用自身的 build 与 publish 都会复制 CLI；publish 是从 CLI 的 publish 输出取件的，
所以想要一个完整的发布目录，CLI 也要执行 publish。缺件时 `smoke` 会报告。

### `external/CUE4Parse`

`PakTool.Core` 引用的是 `..\external\CUE4Parse\CUE4Parse\CUE4Parse.csproj` 与
`CUE4Parse-Conversion.csproj`，因此**构建任何东西之前**这棵树必须存在。
**它不在本仓库中，且 `.gitmodules` 无法帮你拉取** —— 本项目开发所用的那棵树带有本地改动、
且早于当前上游（`CUE4Parse-Natives`、包版本如 SharpGLTF 1.0.6 而非上游的 alpha 版），
而 `.gitmodules` 里也没有声明任何固定提交。

请自行提供其中一种：

- **与之匹配的那棵树**，放到 `external/CUE4Parse/`。如果你有同一份开发快照，这是唯一能保证匹配的做法。
- **上游 `main`** 克隆到 `external/CUE4Parse/`，再用 `git checkout` 切到你那个游戏版本需要的提交。
  预期会有 API 漂移：本代码是针对较旧的 CUE4Parse 写的，当前版本可能需要改动；
  并且 `CUE4Parse-Natives/ACL` 本身也是一个子模块（需在其中再执行
  `git submodule update --init --recursive`）。

`third_party/` 与 `tools/` 都解析到本仓库根目录，因此扁平布局可直接使用。

### 原生依赖（不随仓库提交）

Android 构建需要以下文件位于 `third_party/lib/arm64-v8a/`：

```text
liboodle-data-shared.so    Oodle 解压（可选，请自行提供）
libprism_codecs.so         进程内贴图编码器（ASTC / BC）
librepak_bind.so           Rust pak 写入绑定
libc++_shared.so           C++ 运行时
```

Prism **不**分发 Oodle。若你在本地或私有构建中放入 Oodle 相关文件，需自行遵守虚幻引擎 EULA 及 RAD/Epic 的授权条款。详见 `third_party/README.md`。

桌面端的贴图替换与转换还需要 `tools/texconv.exe` 与 `tools/astcenc-*.exe`；源文件位于 `UAssetTextureWeb/tools/`，构建时会自动拷贝到输出目录。

## Pak 转换的工作原理

这是把贴图在同一游戏的 PC 端与手机端之间搬运的功能：两边的 pak 里存在
相同的资产路径，但存放的是各自平台的像素格式（PC 端多为 BC，手机端多为 ASTC）。

1. 打开**主 pak**（目标平台的 pak，即"母包"），读取其文件索引。
2. 解包**待替换 pak**（另一平台的 pak），按 **pak 内路径**逐条匹配。
3. 对每条匹配，取出源贴图的**像素**，重新编码为**主 pak 中该资产的格式**，写回该资产，再打包输出。

**为什么输出格式由主 pak 决定：** 已烘焙（cooked）资产在 `.uasset` 头部声明了像素格式、
尺寸与 mip 布局，而这些头部信息同时决定了每个 mip 落在 `.uexp` / `.ubulk` 中的位置。
替换只覆写这些字节区间，无法改写头部。因此输出必然采用主 pak 资产的格式，
而这正是目标平台所需的格式。

### 输出模式

| 模式 | 内容 | 体积 |
|---|---|---|
| `ReplacedOnly`（默认） | 仅本次被改动的资产 | ≈ 改动量 |
| `MergeSourceFiles` | 改动过的贴图 **+ 源 pak 的其他文件**（蓝图 / `.locres` / 数据表）原样复制 | ≈ 改动量 |
| `FullRepack` | 主 pak 全部文件 + 被替换的贴图 | ≈ 主 pak |
| `MergeAll` | 替换结果 + 源 pak 独有文件 + 主 pak 其余文件 | ≈ 主 pak |

游戏以叠加方式加载 mod pak，同一路径后注册的副本生效，所以 mod pak 应该只放
`ReplacedOnly`。实测：**7 GB 主 pak + 1 张贴图 → 685 KB 输出**，耗时 3.2 秒，
临时空间峰值 1.6 MB（整包重打则需约 7 GB 临时空间与 88 秒）。

`MergeSourceFiles` 是给"模组不止换贴图"用的：蓝图、本地化、数据表这些内容双端通用，
不需要转换，直接原样复制进输出 pak（同路径以模组为准，符合叠加加载的语义），
因此不必再单独做一次 Pak 合并。本次**成功转换过的贴图不在并入之列** —— 否则源平台的
像素格式（比如 PC 的 BC）会覆盖掉刚重编码好的资产。没能转换的贴图仍会原样带入并在
明细里报出原因，不会静默丢件。实测（39 张贴图的模组）：默认模式 40 条目 / 14.29 MB
且不含 `.locres`；开启开关后 41 条目 / 15.21 MB，`Game.locres` 已并入。

### 两条转换路径（自动选择）

| 条件 | 路径 | 特点 |
|---|---|---|
| 格式与 mip 布局相同 | 直接搬运压缩后的 mip 数据（`RawCopy`） | 无损、极快，**不需要映射文件** |
| 格式不同（跨端常见） | 解码像素 → 按主 pak 格式重新编码（`ReEncode`） | 需要**映射文件**；桌面端还需 astcenc/texconv |

### 映射文件

重编码路径必须反序列化源 `UTexture`，因此无版本信息的资产需要先在设置页选择一个
`.usmap` / `.jmap`。在其中一个平台导出的映射文件对另一个平台同样有效。
缺失时，受影响的贴图会以**跳过**并附原因的形式上报 —— 工具不会静默产出损坏资产。

### 加密 pak

在设置页填入 AES 密钥（十六进制，如 `0x...`）。不填时主 pak 会挂载出 0 个文件，
表现为"没有可转换的贴图"。

### 贴图候选识别

优先识别 `_P.uasset` 命名，但也接受"任意带同级 `.uexp` 的 `.uasset`"——
因此不遵循 `_P` 约定的游戏（例如 KARDS 的 `t_xxx.uasset`）也能正确转换。

### 性能

在 7 GB / 35,586 文件的主 pak 与一个 59 张贴图的 ASTC 源 pak 上实测：读取主 pak 索引约
0.3 秒，默认模式数秒内完成。重编码路径每张贴图约 0.3–1 秒用于像素解码，
上千张贴图需要数分钟。整包重打需要与主 pak 等大的临时空间。

## 界面与操作

| 页面 | 用途 |
|---|---|
| 欢迎 | 落地页；GitHub 上有新版本时整页换成那一版的更新说明 |
| 解包与 Mod | 浏览/搜索 pak 内容、预览资产、导出、贴图替换、生成补丁 pak |
| Pak 合并 | 多选 pak，长按拖动调整覆盖优先级，合成一个 pak |
| Pak 转换 | 把另一平台 pak 中同路径贴图移植过来，按主 pak 格式重编码 |
| 使用说明 | 说明书本身：九章、配当前版本的真实截图 |
| 设置 | 外观、行为、更新、依赖状态、日志 —— **不再重复业务页的输入项** |

- **搜索框**：输入关键词即搜索；输入 pak 内路径（如 `kards/Content/Assets/Textures`）则跳转。
  占位提示会随输入内容变化，提示当前会走哪种模式。
- **合并列表**：**越靠上优先级越高**，路径冲突时以上方那份为准；
  每一项都能拖动排序、都能移除，没有钉死的"主 Pak"。
  拖动 `⠿` 手柄 —— **触屏上需先长按约 0.35 秒**。
- **使用说明**：从浮层改成了**独立页面**。各页顶栏的「使用说明」按钮直接跳到该页对应的那一章；
  版式沿用欢迎页（主色细条 + 章号 + 大标题 → 分隔线 → 左目录右正文）。文案在
  `Docs/PrismDocs.cs` 里以数据形式维护（小标题 / 段落 / 步骤 / 要点 / 截图 / 提示），
  视图只有一套模板 —— 加一章就是加一条记录。截图限高、点开可看原尺寸；
  PNG 由 `--capture --docs` 从**当前构建**按两套主题生成，所以说明书里不会出现已经删掉的按钮。
- **设置**：分组卡片（图标 + 标题 + 一句副标题）内是细线分隔的行：左侧名称与解释，右侧开关或动作。
  Pak / 映射 / AES / 合并输出 / 转换路径**不再**在这里重复一份 —— 它们属于用它们的那个页面，
  同一份状态两处可改会让用户分不清"到底改了哪边"。保留一个「前往配置」入口维持可发现性。
- **界面密度自适应**：一套界面两档。窄于 620px 是给手机用的紧凑单列；
  宽度达到 **1200px** 进入宽屏档，此时 ① **只放大文字**（走应用级字号资源，
  内边距与控件尺寸不变），② 内容列从 620 放宽到 1000，并把设置/合并页的卡片排成**两列** ——
  于是页面变**矮**而不是变高。拖动窗口会实时切换档位；手机布局完全不变。
  （最初试过用变换把整个界面等比放大，已否决：内边距和控件一起变大，像把手机界面拉大，
  一屏反而看到更少。）
  所有字号都是**整数**：100% 缩放时半像素字号（10.5、12.5 这种）只能摊在半个像素上，
  抗锯齿会把笔画涂成灰边 —— 这正是"窗口化时字发虚、最大化后又好了"的原因。
- **主题**：设置页可选 跟随系统 / 浅色 / 深色。深色主题其实早就写好了，但一直跟着系统走 ——
  系统是浅色时永远只能看到一整片亮面，在 1920 屏幕上很费眼。深色主题正文用**纯白**（对卡片 15.1:1）；
  浅色主题则相反，正文柔化到 `#33322e`（11.6:1），因为纯黑配纯白太刺眼。两套主题是**朝相反方向**调的。
- **可读性**：次要文字对白卡片 **5.4:1**（对页面底色 4.9:1），页面底色与白卡片之间有分离度。
  文字**不再用 `Opacity` 压暗** —— 那是乘性降低对比度（一种常见标签样式实测只有 **2.0:1**），
  正是界面看起来"发灰发糊"的原因。层次改由字号与字重表达。

## 验证

```sh
# 集成测试（316 条断言）：映射格式识别、搜索框路径解析、pak 转换（两条路径 + 全部输出模式）、
# 合并优先级、locres <-> JSON 往返、非 ASCII pak 路径处理、无头 UI 加载与导航，
# 音频替换（BINKA 经真实 pak 往返）、媒体文件替换、用真实视图模型跑一遍替换页流程，
# 以及视频预览的 ffmpeg 探测/封面抽取/尺寸换算，
# 未压缩贴图格式（PF_B8G8R8A8 等）的识别与通道顺序，
# 真实跑一次 UAssetCLI 替换（需要先有它的 Release 产物，没有则跳过该组），
# 以及更新机制（发行版 JSON、版本比较、正文解析，和一段真的被执行过的自更新脚本）
dotnet run --project test/Prism.FeatureTests

# 界面截图：每个页面 × 浅色/深色 × 紧凑/宽屏，由 Skia 渲染成 PNG
# （用于审美评审与改版前后对比，输出到指定目录）
dotnet run --project test/Prism.FeatureTests -- --capture artifacts/ui

# 说明书截图：把真实页面按两套主题渲染进 Prism.Desktop/Assets/help/{light,dark}/
# 改过界面就要重跑一次再重新构建，否则应用里的说明书还是上一版的样子
dotnet run --project test/Prism.FeatureTests -- --capture ignore --docs

# 针对真实 pak 的冒烟测试：打开 -> 搜索 -> 贴图预览 -> 音频替换，并自检外部依赖
Prism.Desktop.Desktop/bin/Debug/net10.0/Prism.Desktop.Desktop.exe --smoke <pak> [映射文件]
```

`Prism.FeatureTests` 做成控制台程序而非测试框架：它需要真实文件系统、真实 pak 打包
与无头 UI 后端，用一个小的断言执行器更直接，失败时以非零码退出。其中的贴图用例依赖
`t_cromwell.*` 样本，缺失时会跳过相关断言。

### 构建注意事项

- **Android 构建**要求 `UAssetAPI-master/UAssetAPI/git_commit.txt` 存在；上游的
  `PreBuildEvent` 在 Android 目标上不触发，缺失时报
  `CS1566 无法读取资源 git_commit.txt`。创建该文件即可，构建后会自动删除：
  ```sh
  git rev-parse --short HEAD > UAssetAPI-master/UAssetAPI/git_commit.txt
  ```
- **不要并行构建 Windows 与 Android 的 Release 配置** —— 它们共用
  `PakTool.Core` / `UAssetTexture.Core` / `UAssetAPI` 的输出目录，会互相锁文件。
- **`--smoke` 不能初始化 Avalonia** —— 该进程已处于 WPF 消息循环中，在其中启动 Avalonia
  会导致进程无法退出。UI 校验请用无头测试工程。
- **`NU1608: SkiaSharp 2.88.9 vs 3.119.4` 是预期警告，而且它作用在活路径上，不是死代码。**
  Avalonia 12 的渲染层要求 SkiaSharp 3.119.4，而 vendored 的 `CUE4Parse-Conversion` 钉死 2.88.9，
  NuGet 于是解析成 3.119.4、忽略钉死的那份。这条路径**确实在跑**：每一次贴图预览与 PNG 导出
  都经过 `CTexture.Encode(...)` → `ToSkBitmap()` → `SKBitmap`。它能正常工作，只是因为这里用到
  的那几个 SkiaSharp 成员在 3.x 里签名没变 —— 这是跨两个大版本的 ABI 赌注，不是干净的构建。
  不要屏蔽这条警告：它正是 `--smoke` 存在的理由；升级 Avalonia 或 CUE4Parse 之后务必重新验证
  贴图预览。

## 范围

仅支持 `.pak` 归档；未实现 `.utoc` / `.ucas` 容器。不内置映射文件，请从游戏中导入匹配的那份。

## 许可

GPL-3.0，继承自上游 [kardswalker/Prism](https://github.com/kardswalker/Prism)。
见 `LICENSE` 与 `NOTICE`。