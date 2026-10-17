# tools/

本目录放**运行时要用到的外部工具**。构建时 `*.exe` 会被复制到输出目录的 `tools/` 下，
程序从 `AppContext.BaseDirectory/tools` 找它们。

| 文件 | 用途 | 是否入库 |
| --- | --- | --- |
| `astcenc-*.exe` | ASTC 纹理编码（移动端格式） | 是 |
| `texconv.exe` | DirectXTex，BC 系列纹理编码 | 是 |
| `binka_encode.exe` | Bink Audio 编码，音频替换用 | **否**（见下） |

## binka_encode.exe

音频替换要把素材编成 UE 用的 BINKA（ABEU 容器），这一步依赖 RAD 的 Bink Audio 编码器，
Prism 不自带实现。

- 来源：[binkaenc](https://github.com/595554963github/binkaenc)（GPL-3.0）
- 用法：`binka_encode.exe <input.wav> [-f 0|1] [-q 0-9]`，只接受 **16 位整型 PCM WAV**
  （Prism 会把素材先解成 16 位 PCM 再喂给它）
- 放置：把这个 exe 放进本目录（`tools/binka_encode.exe`），重启 Prism 即可启用音频替换

之所以不入库：它是三方二进制（且链接了 RAD 的 Bink Audio 编码库）。本地的发布包会带上它，
源码仓库里则保持干净。找不到它时，替换页会明确提示缺的是哪个文件、该放到哪里，
其余功能不受影响。

## ffmpeg.exe（可选）

音频素材若是 FLAC / M4A 等格式，Prism 会去找 `ffmpeg.exe`（同样放在本目录或 PATH 里）
先解成 WAV。没有 ffmpeg 时只支持 WAV / MP3 / OGG —— 这三种是内置纯托管解码器。
