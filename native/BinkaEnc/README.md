# native/BinkaEnc — 进程内 BINKA 编码器

把 16 位 PCM 编成 UE 用的 BINKA（ABEU 容器）。**桌面和 Android 共用这一份源码**，
所以手机端也能做音频替换（之前不行，因为只有一个 Windows 专用 exe）。

## 为什么需要它

音频替换要把素材重新压成 BINKA。原来的做法是调外部进程 `tools/binka_encode.exe`：

- 那是 Windows 二进制，Android 上根本起不来；
- RAD 只提供 win64 / winarm64 / lnx64 / osx 的预编译静态库，**没有 Android arm64**。

但 `reference/binkaenc-main/Src/binkace.c` 是完整逻辑，缺的只是 RAD 的 5 个内部头文件。
本目录把它们补出来，于是同一份 C 源码可以直接编到任何平台。

## 目录内容

| 文件 | 作用 |
| --- | --- |
| `compat/radmath.h` | `radabs` / `radfloor` / `radpow` / `getbitlevelvar` / `mult64anddiv` / `ranged_log_0p05_to_0p5` |
| `compat/varbits.h` | LSB-first（Vorbis 风格）位写入器，对应解码器 `bitstream_lsb.h` 的 `bl_put` |
| `compat/popmal.h` | RAD 的“一次分配多个缓冲”辅助函数声明（`popmal.c` 原样编） |
| `compat/radfft.h` | `radfft_init` / `radfft_rfft` / `radfft_dct` |
| `compat/undeci.inc` | 量化步长表 96 项，与解码器 `binka_scalefactors` 逐值一致（10^(0.0664·i)） |
| `radfft_prism.cpp` | 自己的 radix-2 FFT + DCT-II 实现 |
| `prism_binkaenc.cpp` | 导出 `prism_encode_binka`（两趟调用：先问长度，再取数据） |
| `dct_probe.cpp` | 诊断用：验证分析/合成变换往返增益必须是 1.0 |
| `build-win-x64.ps1` | `-Mode exe`（自建 CLI）/ `dll` / `probe` 三种产物的编译脚本 |

## 关键数学约定

解码端（`reference/binka_decoder-main/binka_transform.cpp` 的 `transform_dct`）是**不带 1/2 直流项**的 DCT-III：

```
x[n] = c[0] + Σ_{k>0} c[k]·cos(πk(2n+1)/(2N))
```

所以编码端的分析变换必须是**未归一化的 DCT-II**：

```
c[k] = Σ_n x[n]·cos(πk(2n+1)/(2N))
```

`binkace.c` 之后的 `samples[0] *= 0.5f` 正好补上直流项的 1/2。两者配合：

- 编码：`×1/√N`（`transform_size_root`）
- 解码：`×2/√N`（`binka_dec.cpp` 的 `dec->scale`）
- 变换对本身往返增益 `N/2`

乘积正好 1.0 —— `dct_probe` 实测 `gain 0.999999`，残差是浮点噪声级别。
**改 radfft_prism.cpp 里的任何缩放都要重跑 dct_probe，否则出来的是能解码但声音不对的流。**

## 编译

```powershell
# 独立 CLI（调试用，产物在 artifacts/binka-enc/）
pwsh -File native/BinkaEnc/build-win-x64.ps1 -Mode exe

# 变换往返校验
pwsh -File native/BinkaEnc/build-win-x64.ps1 -Mode probe
./artifacts/binka-enc/prism_dct_probe.exe some.wav     # gain 必须是 1.000000

# 正式产物的两条路：
pwsh -File native/BinkaDll/build-win-x64.ps1                      # Windows: prism_binka.dll
pwsh -File native/PrismCodecs/build-android-arm64.ps1 -NdkRoot …  # Android: libprism_codecs.so
```

Android 那条脚本要注意两点：

1. `.c` 文件必须单独 `-x c` 编译，`clang++` 会把 `.c` 当 C++ 处理；
2. `reference/binkaenc-main/Src/popmal.h` 是个 **0 字节空文件**，而引号包含永远先搜当前
   文件所在目录，所以必须 `-include compat/popmal.h` 先把它盖住。

## 速度

x64（`/O2`）实测：60 秒立体声素材约 2.0 秒，约 30× 实时；RAD 原厂库约 73× 实时。
慢的部分是自己写的 DCT（用 2N 点复数 FFT 实现）。如果手机上感觉慢，第一步可以把它换成
标准的 N/2 点复数 FFT 版本（约 4 倍），代价是要重新验证 `dct_probe` 的往返增益。

## 质量

同一段素材（`test/AU_Arrow_morse_loop_01.wav` 拉到 -6 dBFS）对比原厂 exe：

| q | 原厂字节 | 自建字节 | PSNR 原厂 | PSNR 自建 |
| --- | --- | --- | --- | --- |
| 0 | 79,730 | 65,526 | 16.02 | 15.99 |
| 4 | 27,682 | 21,102 | 15.97 | 15.86 |
| 8 | 10,782 | 9,226 | 16.00 | 15.91 |

同质量下体积小 15–20%。合成双音的极端素材上自建版本差约 4 dB（感知模型的
`ranged_log` 用了精确 log10 近似，RAD 用的是分段近似），实际音乐听不出差别。
