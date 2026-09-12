# Prism

[English](README.md) · **简体中文**

面向 **Windows 桌面端与 Android** 的 UE `.pak` 资产管理工具，基于 [CUE4Parse](https://github.com/FabianFG/CUE4Parse)，扩展自 [kardswalker/Prism](https://github.com/kardswalker/Prism)。

浏览 pak 内容，预览贴图 / 音频 / 三维网格 / 本地化文本，导出与分享资产，制作贴图替换 mod，还能在同一游戏的 PC 端与手机端之间互转贴图包。

## 功能

**浏览与预览**
- 通过 CUE4Parse 挂载 UE4/UE5 `.pak`，支持加密 pak 的 AES 密钥。
- 同时支持 `.usmap` 与 `.jmap` 映射文件（含 `.gz`），按扩展名自动识别格式。
- 文件管理器式浏览、关键词搜索，以及**在搜索框里直接输入路径跳转**。
- 把相关的 `.uasset` / `.uexp` / `.ubulk` 归并为一个资产条目。
- 预览贴图（带缩略图）、音频（内置播放器）、三维网格线框、本地化（`.locres`）、蓝图伪代码。
- 导出原始包体、PNG 图片、整个文件夹，或分享到其他应用。

**贴图替换**
- 选中贴图资产、给一张图片，直接生成补丁 pak，无需手工重打包。
- 桌面端经 UAssetCLI + astcenc / texconv 编码；Android 端由 `libprism_codecs` 在进程内编码。

**Pak 合并**
- 一次性多选多个 pak，长按拖动调整覆盖优先级（列表越靠下优先级越高）。
- 合并前查看冲突。

**Pak 转换（跨端贴图移植）**
- 把某个平台的 pak 里的贴图移植到另一个平台的 pak 中，并按目标资产格式重新编码。
- 默认只输出本次真正改动的资产 —— 7 GB 主 pak 加一张贴图，产出**仅 685 KB** 的 mod pak。
- 原理见 [Pak 转换的工作原理](#pak-转换的工作原理)。

**本地化**
- 在应用内编辑 `.locres` 词条并写回补丁 pak。
- 导出本地化为 JSON，四种 locres 格式版本均可安全往返。

**诊断**
- 结构化日志：级别、时间戳、完整异常栈、落盘镜像，以及带环境头的可导出报告。

## 目录结构

```
Prism/                     Android WebView 应用（早期版本，源自上游）
Prism.PC/                  本地 Web UI 构建
Prism.Desktop/             共享 Avalonia UI 与逻辑（库，net10.0）
Prism.Desktop.Desktop/     Windows 外壳（单文件发布）
Prism.Desktop.Android/     Android 外壳（APK，arm64-v8a）
PakTool.Core/              pak 会话 / 预览 / 导出 / 合并 / 映射 / locres
UAssetTexture.Core/        贴图替换引擎与 pak 转换
UAssetCLI/                 贴图替换命令行工具
test/Prism.FeatureTests/   集成测试（控制台程序）
third_party/               Android 原生库（不提交，见下文）
UAssetAPI-master/          内置改造过的 UAssetAPI
```

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
| `FullRepack` | 主 pak 全部文件 + 被替换的贴图 | ≈ 主 pak |
| `MergeAll` | 替换结果 + 源 pak 独有文件 + 主 pak 其余文件 | ≈ 主 pak |

游戏以叠加方式加载 mod pak，同一路径后注册的副本生效，所以 mod pak 应该只放
`ReplacedOnly`。实测：**7 GB 主 pak + 1 张贴图 → 685 KB 输出**，耗时 3.2 秒，
临时空间峰值 1.6 MB（整包重打则需约 7 GB 临时空间与 88 秒）。

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
| 解包与 Mod | 浏览/搜索 pak 内容、预览资产、导出、贴图替换、生成补丁 pak |
| Pak 合并 | 多选 pak，长按拖动调整覆盖优先级，合成一个 pak |
| Pak 转换 | 把另一平台 pak 中同路径贴图移植过来，按主 pak 格式重编码 |
| 设置 | 路径、映射文件、AES 密钥、缓存、动画开关、日志与导出 |

- **搜索框**：输入关键词即搜索；输入 pak 内路径（如 `kards/Content/Assets/Textures`）则跳转。
  占位提示会随输入内容变化，提示当前会走哪种模式。
- **合并列表**：首项是主 pak 且固定不可拖，其后的条目覆盖它，越靠下优先级越高。
  拖动 `⠿` 手柄 —— **触屏上需先长按约 0.35 秒**。

## 验证

```sh
# 集成测试（114 条断言）：映射格式识别、搜索框路径解析、pak 转换（两条路径）、
# 合并优先级、locres <-> JSON 往返、非 ASCII pak 路径处理、无头 UI 加载与导航
dotnet run --project test/Prism.FeatureTests

# 针对真实 pak 的冒烟测试：打开 -> 搜索 -> 贴图预览，并自检外部依赖
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

## 范围

仅支持 `.pak` 归档；未实现 `.utoc` / `.ucas` 容器。不内置映射文件，请从游戏中导入匹配的那份。

## 许可

GPL-3.0，继承自上游 [kardswalker/Prism](https://github.com/kardswalker/Prism)。
见 `LICENSE` 与 `NOTICE`。
