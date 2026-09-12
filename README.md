# Prism 工作区

UE `.pak` 资产管理工具生态：**Android + Windows 桌面**双端，基于 [CUE4Parse](https://github.com/FabianFG/CUE4Parse)，在 [kardswalker/Prism](https://github.com/kardswalker/Prism) 基础上扩展。

## 目录

```
FModel-dev/                      Prism 主工程（独立解决方案）
  ├── Prism/                     Android WebView 版（上游原样）
  ├── Prism.PC/                  本地 Web UI 版
  ├── Prism.Desktop/             Avalonia 共享 UI/逻辑（库，net10.0）
  ├── Prism.Desktop.Desktop/     Windows 壳（单文件发布）
  ├── Prism.Desktop.Android/     Android 壳（APK，arm64-v8a）
  ├── PakTool.Core/              Pak 会话/预览/导出/合并/映射/locres
  ├── UAssetTexture.Core/        纹理替换引擎 + Pak 转换
  ├── UAssetCLI/                 纹理替换 CLI
  └── third_party/               Android native 库（prism_codecs/repak_bind）
UAssetTextureWeb/                纹理替换 Web 应用（tools/ 编码器目录）
UAssetCLI/                       纹理替换 CLI（独立副本，供桌面版运行）
UAssetAPI-master/                UAssetAPI（vendored，含本地修改）
UE-Pak-Manager/                  WPF Pak 管理器
rel/                             发行版输出（win/、android/）
```

## 快速开始

```sh
# Windows 桌面（Debug）
dotnet build FModel-dev/Prism.Desktop.Desktop/Prism.Desktop.Desktop.csproj

# Windows 单文件发布
dotnet publish FModel-dev/Prism.Desktop.Desktop/Prism.Desktop.Desktop.csproj \
  -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# Android Release APK（需 JDK + Android SDK）
dotnet build FModel-dev/Prism.Desktop.Android/Prism.Desktop.Android.csproj \
  -c Release -p:JavaSdkDirectory=<JDK路径>
```

说明：`FModel-dev/external/CUE4Parse` 是 git submodule；桌面**纹理替换**（`解包 & 模组` 页的替换功能）
与 **Pak 转换** 的跨格式重编码都需要 `UAssetCLI`（先 `dotnet build UAssetCLI -c Release`）与
`UAssetTextureWeb/tools/` 下的 astcenc/texconv。**Pak 合并**不需要它们。

## Pak 转换的工作原理

把「待转换 Pak」（例如手机端资产）与「主 Pak」（例如 PC 端资产）按 **Pak 内路径**配对，
解出源纹理的像素，按 **主 Pak 资产的格式**重新编码后写回，最后打包成模组 Pak。

**输出内容（默认只装改动项）**：

| 模式 | 输出内容 | 体积 |
|---|---|---|
| `ReplacedOnly`（默认） | 只装本次改动过的 `.uasset/.uexp/.ubulk` | ≈ 改动量 |
| `FullRepack` | 主 Pak 全部文件 + 替换后的纹理 | ≈ 主 Pak |
| `MergeAll` | 替换 + 源 Pak 独有文件 + 主 Pak 其余文件 | ≈ 主 Pak |

游戏会把模组 Pak 叠加在原 Pak 之上，同路径以后加载的为准，所以默认模式才是模组该有的形态。
实测：**7 GB 主 Pak + 1 张纹理 → 输出 685 KB**，耗时 3.2 秒、临时目录峰值 1.6 MB
（全量重打包模式则需要 ≈7 GB 临时空间和 88 秒）。

**两条转换路径**（服务自动选择）：

| 条件 | 走法 | 特点 |
|---|---|---|
| 两侧格式与 mip 布局一致 | 直接搬运已压缩的 mip 载荷（RawCopy） | 无损、快、**不需要映射文件** |
| 两侧不同（跨平台常见） | 解出像素 → 按主 Pak 格式重编码（ReEncode） | 需要**映射文件**；桌面另需 astcenc/texconv |

**为什么输出格式由主 Pak 决定**：cooked 资产的像素格式、尺寸、mip 布局都写在 `.uasset` 头部，
mip 区域的位置与长度也由头部决定。替换只覆盖这些区域的字节，改不了头部 —— 输出必然沿用主 Pak
资产的格式，这也正是想要的结果（本平台游戏读得懂）。

**映射文件要求**：重编码路径要反序列化源 `UTexture`，因此未版本化资产需要 `.usmap`/`.jmap`。
请在设置页选择映射文件；缺失时该项会记为「跳过」并说明原因，不会产出坏资产。

**加密 Pak**：在设置页填 AES 密钥（形如 `0x...`）。缺密钥时主 Pak 会挂载出 0 个文件，
表现为"没有可转换的纹理"——转换页会提示。

**纹理候选的识别**：优先认 `_P.uasset` 命名约定，同时接受「任意 `.uasset` 且有同名 `.uexp`」，
因此不依赖命名约定的游戏（例如 KARDS 的 `t_xxx.uasset`）也能正常转换。

**性能参考**（实测：7 GB / 35,586 文件的主 Pak + 59 张 ASTC 纹理的源 Pak）：
读主 Pak 索引约 0.3 秒；默认模式整轮约数秒。若改用全量重打包模式，
需预留与主 Pak 等量的临时磁盘空间。

## 功能页面

| 页面 | 说明 |
|---|---|
| 解包 & 模组 | 浏览/搜索 Pak 内容；纹理、音频、3D 线框、本地化预览；导出与分享；纹理替换与补丁 Pak |
| Pak 合并 | 多选 Pak，长按/拖动调整覆盖顺序（越靠下优先级越高，首项为主 Pak），合成一个 Pak |
| Pak 转换 | 把「待转换 Pak」的同路径纹理像素，按主 Pak 资产的格式重新编码后写回，合成新 Pak（跨平台搬贴图） |
| 设置 | 路径、映射文件（usmap/jmap）、AES、缓存、动画开关、日志与导出 |

## 验证

```sh
# 集成测试（100 项断言）：映射格式识别、搜索框路径解析、Pak 转换（快路径与重编码链路）、
# 合并覆盖优先级、locres ↔ JSON 往返、headless UI 加载与页面导航
dotnet run --project FModel-dev/test/Prism.FeatureTests

# 冒烟测试：真实 Pak 的「打开 → 搜索 → 纹理预览」核心链路
# 默认读 E:\pak\test.pak，也可显式传入
FModel-dev/Prism.Desktop.Desktop/bin/Debug/net10.0/Prism.Desktop.Desktop.exe \
  --smoke <pak路径> [映射文件路径]
```

`Prism.FeatureTests` 是控制台程序（非 xunit）：需要真实文件系统、真实 Pak 打包与
headless UI，自建迷你断言器比引入测试框架更直接，失败时返回非零退出码。
其中的纹理用例依赖仓库根的 `t_cromwell.*` 样本；找不到会自动跳过相关断言。

### 已知构建注意

- **Android 构建**：`UAssetAPI.csproj` 的 `PreBuildEvent` 在 Android 目标下不生效，会报
  `CS1566 读取资源 git_commit.txt 失败`。手动补一个即可（构建后会被自动删除）：
  ```sh
  git rev-parse --short HEAD > FModel-dev/UAssetAPI-master/UAssetAPI/git_commit.txt
  ```
- **不要并行构建** Windows 与 Android 的 Release 配置：两者共享
  `PakTool.Core` / `UAssetTexture.Core` / `UAssetAPI` 的输出目录，会互相锁文件。
- **`--smoke` 不应初始化 Avalonia**：该进程已进入 WPF 消息循环，再初始化 Avalonia
  会让进程无法退出（脚本拿不到结果）。UI 验证请用上面的 headless 测试工程。

## 许可

GPL-3.0（继承上游 kardswalker/Prism）。详见 `LICENSE` 与 `NOTICE`。
