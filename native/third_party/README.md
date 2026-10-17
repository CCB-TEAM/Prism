# native/third_party — 构建内置编解码器所需的上游源码

这里的代码不是 Prism 写的，是从两个 GPL-3.0 源码树里挑出来的**构建必需文件**，
目的只有一个：让 `git clone` 之后能直接编出 `prism_binka.dll` 和 `libprism_codecs.so`，
不再依赖构建机上的 `reference/` 目录。

| 目录 | 来源 | 许可证 | 用来编什么 |
| --- | --- | --- | --- |
| `binkaenc-main/` | [595554963github/binkaenc](https://github.com/595554963github/binkaenc)（RAD/Epic 的 BINK 音频编码器 `binkace.c` + UE 封装） | GPL-3.0（见内附 LICENSE） | `prism_binka.dll` / `libprism_codecs.so` 的**编码**部分（`prism_encode_binka`） |
| `binka_decoder-main/` | 同一来源的公开 BINKA 解码器（`binka_dec.cpp` / `binka_transform.cpp` / `ueba_decoder.cpp` / `bcf1_decoder.cpp`） | GPL-3.0（见内附 LICENSE） | 两个库的**解码**部分（`prism_probe_binka_audio` / `prism_decode_binka_to_wav`）、`binkadec` 校验工具 |

本仓库整体也是 GPL-3.0（根目录 `LICENSE`），所以这些文件与仓库同许可。

## 只收了用得到的文件

`binkaenc-main/` 里没有 `binkacd.cpp` / `binkbits.h` / `console.h` / `crsfade.inl` ——
那是 RAD 的另一份解码器实现，需要我们没有的 `radfft` / `varbits`，本仓库不编译它。
想要完整上游树的话，直接从上面链接取。

`native/BinkaEnc/compat/` 里那 5 个头文件（`radmath.h` / `varbits.h` / `popmal.h` /
`radfft.h` / `undeci.inc`）正是为了让 `binkace.c` 能脱离 RAD 的预编译库编译而写的替身，
为什么要那样写见 [../BinkaEnc/README.md](../BinkaEnc/README.md)。

## 构建脚本怎么找源码

三个脚本都按 `native/third_party/` → 仓库根 → `reference/` 的顺序找（同名目录），
所以想用自己下载的完整上游树覆盖这里的副本，把它放到仓库根或 `reference/` 下即可，
删掉 `native/third_party/` 对应目录也不会影响构建。
