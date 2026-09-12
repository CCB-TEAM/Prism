# UAssetTextureWeb 部署

## 先发布 UAssetCLI

```powershell
dotnet publish ..\UAssetCLI\UAssetCLI.csproj -c Release -r win-x64 --self-contained false
```

发布后可使用 `UAssetCLI.dll` 或 `UAssetCLI.exe` 作为后端调用目标。

## 首次启动配置

网站启动时会检查：

```text
UAssetTextureWeb/App_Data/server-settings.json
```

如果配置不完整，并且当前进程运行在可交互命令行中，程序会引导输入：

- 监听地址或端口
- UAssetCLI 路径
- BC7 编码器路径，即 `texconv.exe`
- ASTC 编码器路径，即 `astcenc.exe`
- 最大并发编码任务数

填写完成后会保存到 `server-settings.json`，后续启动会自动读取。

配置文件示例：

```json
{
  "url": "http://0.0.0.0:5299",
  "uAssetCliPath": "D:\\site\\UAssetCLI\\UAssetCLI.dll",
  "bc7EncoderPath": "C:\\Tools\\texconv.exe",
  "astcEncoderPath": "C:\\Tools\\astcenc-sse2.exe",
  "maxParallelJobs": 2
}
```

## 启动参数覆盖

配置文件之外，也可以用启动参数或环境变量覆盖：

```powershell
dotnet run --project UAssetTextureWeb -- `
  --Server:Url "http://0.0.0.0:5299" `
  --Encoders:BC7 "C:\Tools\texconv.exe" `
  --Encoders:ASTC "C:\Tools\astcenc-sse2.exe" `
  --UAssetCli:Path "D:\site\UAssetCLI\UAssetCLI.dll" `
  --Jobs:MaxParallel 2
```

环境变量：

```powershell
$env:Server__Url = "http://0.0.0.0:5299"
$env:Encoders__BC7 = "C:\Tools\texconv.exe"
$env:Encoders__ASTC = "C:\Tools\astcenc-sse2.exe"
$env:UAssetCli__Path = "D:\site\UAssetCLI\UAssetCLI.dll"
$env:Jobs__MaxParallel = "2"
dotnet UAssetTextureWeb.dll
```

兼容的简短环境变量：

```powershell
$env:BC7_ENCODER_PATH = "C:\Tools\texconv.exe"
$env:ASTC_ENCODER_PATH = "C:\Tools\astcenc-sse2.exe"
```

## 非交互部署

如果服务以 systemd、Windows 服务、IIS 或容器方式运行，通常没有交互式控制台。此时配置不完整会直接启动失败。请提前放好 `App_Data/server-settings.json`，或使用环境变量/启动参数。

## 并发模型

- HTTP 上传、文件保存、读取结果和等待 CLI 进程均使用异步 API。
- 每个请求使用独立的 `App_Data/jobs/<guid>` 工作目录，互不覆盖。
- `UAssetCLI` 使用已构建的 dll/exe，不使用 `dotnet run`，避免服务器并发时产生 build/obj 文件锁。
- 同时运行的编码任务由 `maxParallelJobs` / `Jobs:MaxParallel` 限制，默认是 CPU 核心数的一半，至少为 1。

## 纹理替换说明

- `.uasset` 和 `.uexp` 必填。
- `.ubulk` 可选；Android ASTC 等 inline-only cooked 纹理经常没有 `.ubulk`。
- 上传图片尺寸不必和原纹理完全一致；后端会自动缩放到 UAsset 记录的原始纹理尺寸，再生成 mip 并压缩。
- 选择的压缩格式必须和原始资源真实格式一致，否则 CLI 会拒绝处理。

## Pak 打包说明

网页中的 Pak 打包功能使用 UAssetAPI 的 `PakBuilder/PakWriter`，直接打包服务器本地路径。用户需要填写服务器上真实存在的源目录和输出 Pak 路径；浏览器本地路径不会被服务器访问。
