# tools/

本目录放**运行时要用到的外部工具**。构建时 `*.exe` 会被复制到输出目录的 `tools/` 下，
程序从 `AppContext.BaseDirectory/tools` 找它们。

| 文件 | 用途 | 是否入库 |
| --- | --- | --- |
| `astcenc-*.exe` | ASTC 纹理编码（移动端格式） | 是 |
| `texconv.exe` | DirectXTex，BC 系列纹理编码 | 是 |
| `binka_encode.exe` | Bink Audio 编码（**可选**，内置编码器缺失时的后备） | **否**（见下） |
| `ffmpeg.exe` | 视频预览出帧 + 解 FLAC / M4A 等音频 | **否**（见下） |

## BINKA 音频编码（内置）

音频替换要把素材编成 UE 用的 BINKA（ABEU 容器）。这一步现在由 **Prism 自带的原生编码器**
完成：`prism_encode_binka`，桌面在 `prism_binka.dll`、Android 在 `libprism_codecs.so`，
源码在 `native/BinkaEnc`（RAD/Epic 的 `binkace.c` + 我们自己补的 `radmath/varbits/popmal/
radfft/undeci` 五个内部头文件）。

因此：

- 桌面上不再强制需要 `binka_encode.exe`；
- Android 上也能替换音频（之前不行，就是因为只有一个 Windows 编码器 exe）；
- `tools/binka_encode.exe` 只在原生库缺失时作为后备使用；两者都没有时，替换页会提示缺什么。

### binka_encode.exe（可选后备）

- 来源：[binkaenc](https://github.com/595554963github/binkaenc)（GPL-3.0）
- 用法：`binka_encode.exe <input.wav> [-f 0|1] [-q 0-9]`，只接受 **16 位整型 PCM WAV**
- 放置：需要后备时放进本目录（`tools/binka_encode.exe`），重启 Prism 生效

之所以不入库：它是三方二进制（且链接了 RAD 的 Bink Audio 编码库）。本地发布包会带上它，
源码仓库里则保持干净。

## ffmpeg.exe

两处会用到它：

1. **视频预览**：ffmpeg 把视频解开成帧通过管道吐给 Prism（`-re` 实时节奏），
   预览面板里就能直接播放；封面帧、时长、分辨率也都靠它探。
2. **音频解码**：FLAC / M4A / AAC / WMA 这些格式 Prism 不解，交给 ffmpeg 转成 WAV。

没有 ffmpeg 时：视频只剩「用系统播放器打开」入口，音频只支持 WAV / MP3 / OGG
（这三种是内置纯托管解码器，不依赖外部程序）。

- 来源：任一 Windows 版 ffmpeg 静态构建（[gyan.dev](https://www.gyan.dev/ffmpeg/builds/) 等）
- 放置：`tools/ffmpeg.exe`，或放进 `PATH`

## prism_binka.dll（BINKA 编解码库）

桌面上原本只有 Android 的 `libprism_codecs.so`，没有对应的 Windows DLL，
于是预览 BINKA 音频时只能显示 "BINKA decoder unavailable; export raw"。
`prism_binka.dll` 补上这一块：它由 `native/BinkaDll/build-win-x64.ps1`
从 `native/PrismCodecs/prism_codecs.cpp` + `native/BinkaEnc` 编出来，导出：

| 导出 | 用途 |
| --- | --- |
| `prism_probe_binka_audio` | 读 ABEU 头，算解码后大小 |
| `prism_decode_binka_to_wav` | BINKA → WAV（预览/播放） |
| `prism_encode_binka` | PCM → BINKA（音频替换，两个平台同一份源码） |

发布包会把它放在 exe 同级目录。
