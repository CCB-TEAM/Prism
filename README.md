# Prism

**English** · [简体中文](README.zh-CN.md)

UE `.pak` asset management toolkit for **Windows desktop and Android**, built on [CUE4Parse](https://github.com/FabianFG/CUE4Parse). Extends [kardswalker/Prism](https://github.com/kardswalker/Prism).

Browse pak contents, preview textures / audio / 3D meshes / localization, export and share assets, build texture-replacement mod paks — and convert texture packs between the PC and mobile builds of a game.

## Features

**Browsing & preview**
- Mount UE4/UE5 `.pak` archives through CUE4Parse; AES key support for encrypted paks.
- `.usmap` **and** `.jmap` mapping files (including `.gz`), auto-detected by extension.
- File-manager style browsing, keyword search, and **direct path navigation** from the search box.
- Groups related `.uasset` / `.uexp` / `.ubulk` into one asset entry.
- Previews: textures (with thumbnails), audio (built-in player), 3D mesh wireframe, localization (`.locres`), blueprint pseudo-code.
- Export raw packages, PNG images, whole folders, or share to other apps.

**Texture replacement**
- Pick a texture asset, supply an image, and build a patch pak — no manual repacking.
- Desktop encodes via UAssetCLI + astcenc / texconv; Android encodes in-process via `libprism_codecs`.

**Pak merging**
- Select multiple paks at once and drag to set override priority (lower in the list wins).
- Conflict inspection before building.

**Pak conversion (cross-platform texture porting)**
- Move textures from one platform's pak into another's, re-encoded to the target asset's format.
- Outputs only the changed assets by default — a 7 GB main pak plus one texture yields a **685 KB** mod pak.
- See [How Pak conversion works](#how-pak-conversion-works).

**Localization**
- Edit `.locres` entries in-app and write them back into a patch pak.
- Export localization to JSON, and round-trip it safely across all four locres format versions.

**Diagnostics**
- Structured log with severity, timestamps, full exception stacks, on-disk mirror, and an exportable report with environment header.

## Layout

```
Prism/                     Android WebView app (earlier, upstream-derived)
Prism.PC/                  Local web UI build
Prism.Desktop/             Shared Avalonia UI + logic (library, net10.0)
Prism.Desktop.Desktop/     Windows shell (single-file publish)
Prism.Desktop.Android/     Android shell (APK, arm64-v8a)
PakTool.Core/              Pak session / preview / export / merge / mappings / locres
UAssetTexture.Core/        Texture replacement engine + pak conversion
UAssetCLI/                 Texture replacement CLI
test/Prism.FeatureTests/   Integration tests (console app)
third_party/               Android native libraries (not committed, see below)
UAssetAPI-master/          Vendored UAssetAPI with local patches
```

## Download

Prebuilt packages are attached to [Releases](../../releases). The Windows zip is self-contained (no .NET runtime needed) but **keep `UAssetCLI/` and `tools/` next to the executable** — texture replacement and conversion need them.

## Building

```sh
# Windows desktop (Debug) — also copies UAssetCLI and tools/
dotnet build Prism.Desktop.Desktop/Prism.Desktop.Desktop.csproj

# Windows self-contained single file
dotnet publish Prism.Desktop.Desktop/Prism.Desktop.Desktop.csproj \
  -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# Android release APK (requires JDK + Android SDK)
dotnet build Prism.Desktop.Android/Prism.Desktop.Android.csproj \
  -c Release -p:JavaSdkDirectory=<path-to-jdk>

# Texture replacement CLI: build Release so AOT output lands where the app looks for it
dotnet build UAssetCLI/UAssetCLI.csproj -c Release
```

`external/CUE4Parse` is a git submodule — run `git submodule update --init --recursive` first.

### Native dependencies (not committed)

Android builds need these under `third_party/lib/arm64-v8a/`:

```text
liboodle-data-shared.so    Oodle decompression (optional; supply your own)
libprism_codecs.so         In-process texture encoders (ASTC / BC)
librepak_bind.so           Rust pak writer binding
libc++_shared.so           C++ runtime
```

Prism does **not** distribute Oodle. If you place Oodle artifacts here for a local or private build, you are responsible for complying with the Unreal Engine EULA and any RAD/Epic licensing terms. See `third_party/README.md`.

Desktop texture replacement and conversion additionally need `tools/texconv.exe` and `tools/astcenc-*.exe`; the source copies live in `UAssetTextureWeb/tools/` and are copied into the build output automatically.

## How Pak conversion works

This is the feature that moves textures between the PC and mobile builds of a game, where the
same asset path exists in both paks but holds platform-specific pixel formats (BC on PC, ASTC on mobile).

1. Open the **main pak** (the target platform's pak — the "mother" pak) and read its file index.
2. Unpack the **source pak** (the other platform's pak) and match entries by **pak-internal path**.
3. For each match, extract the source texture's **pixels**, re-encode them into the **main pak
   asset's format**, write them into that asset, and pack the result.

**Why the output format comes from the main pak:** a cooked asset declares its pixel format,
dimensions, and mip layout in the `.uasset` header, and that header also determines where each mip
lives in `.uexp` / `.ubulk`. Replacement only overwrites those byte ranges — it cannot rewrite the
header. So the output necessarily uses the main pak asset's format, which is exactly what the target
platform needs.

### Output modes

| Mode | Contents | Size |
|---|---|---|
| `ReplacedOnly` (default) | Only the assets this run changed | ≈ size of the change |
| `FullRepack` | Every main-pak file plus the replaced textures | ≈ main pak |
| `MergeAll` | Replacement + source-only files + remaining main-pak files | ≈ main pak |

Games load mod paks as an overlay where the later-registered copy of a path wins, so `ReplacedOnly`
is what a mod pak should contain. Measured: **7 GB main pak + 1 texture → 685 KB output**, 3.2 s
wall clock and 1.6 MB peak temp space (a full repack would need ≈7 GB temp and 88 s).

### Two conversion paths (chosen automatically)

| Condition | Path | Characteristics |
|---|---|---|
| Same format and mip layout | Copy compressed mip payloads (`RawCopy`) | Lossless, fast, **no mapping file needed** |
| Different format (typical cross-platform) | Decode pixels → re-encode to main pak format (`ReEncode`) | Needs a **mapping file**; desktop also needs astcenc/texconv |

### Mapping files

The re-encode path must deserialize the source `UTexture`, so unversioned assets require a
`.usmap` / `.jmap` selected on the Settings page. A mapping recorded on one platform works for the
other. When it is missing, the affected textures are reported as **skipped** with a reason — the tool
never silently emits a broken asset.

### Encrypted paks

Enter the AES key (hex, e.g. `0x...`) on the Settings page. Without it the main pak mounts zero
files, which surfaces as "no convertible textures".

### Texture candidate detection

`_P.uasset` naming is recognized first, but any `.uasset` with a sibling `.uexp` is also accepted —
so games that do not follow the `_P` convention (for example KARDS `t_xxx.uasset`) convert correctly.

### Performance

Measured on a 7 GB / 35,586-file main pak with a 59-texture ASTC source pak: reading the main pak
index takes ~0.3 s, and the default mode finishes in seconds. The re-encode path costs roughly
0.3–1 s per texture for pixel decoding, so thousands of textures take minutes.
A full repack needs temp space equal to the main pak.

## Screens & controls

| Screen | Purpose |
|---|---|
| Unpack & Mods | Browse/search pak contents, preview assets, export, texture replacement, patch pak |
| Pak Merge | Multi-select paks, long-press/drag to reorder override priority, build one pak |
| Pak Convert | Port same-path textures from another platform's pak, re-encoded for the main pak |
| Settings | Paths, mapping file, AES key, cache, animation toggle, log and export |

- **Search box**: a keyword searches; a pak-internal path (e.g. `kards/Content/Assets/Textures`)
  navigates instead. The placeholder changes to indicate which mode the input will use.
- **Merge list**: the first entry is the main pak and is pinned; everything below overrides it,
  with the lowest entry winning. Drag the `⠿` handle — **on touch, long-press for ~0.35 s first**.

## Verifying

```sh
# Integration tests (114 assertions): mapping format detection, search-box path resolution,
# pak conversion (both paths), merge priority, locres <-> JSON round-trip,
# non-ASCII pak path handling, headless UI loading and navigation
dotnet run --project test/Prism.FeatureTests

# Smoke test against a real pak: open -> search -> texture preview, plus dependency self-check
Prism.Desktop.Desktop/bin/Debug/net10.0/Prism.Desktop.Desktop.exe --smoke <pak> [mapping]
```

`Prism.FeatureTests` is a console app rather than a test framework: it needs a real filesystem, real
pak packing, and a headless UI backend, so a small assertion runner keeps it direct and exits
non-zero on failure. Its texture cases use `t_cromwell.*` fixtures; if they are absent those
assertions are skipped.

### Build notes

- **Android builds** need `UAssetAPI-master/UAssetAPI/git_commit.txt` to exist; the upstream
  `PreBuildEvent` does not fire for the Android target and the build fails with
  `CS1566 unable to read resource git_commit.txt`. Create it and it will be removed afterwards:
  ```sh
  git rev-parse --short HEAD > UAssetAPI-master/UAssetAPI/git_commit.txt
  ```
- **Do not build the Windows and Android Release configurations in parallel** — they share the
  `PakTool.Core` / `UAssetTexture.Core` / `UAssetAPI` output directories and will lock each other's files.
- **`--smoke` must not initialize Avalonia** — the process is already inside a WPF message loop, and
  starting Avalonia there prevents it from exiting. Use the headless test project for UI checks.

## Scope

`.pak` archives only; `.utoc` / `.ucas` containers are not implemented. Mapping files are not bundled —
import the matching one from the game.

## License

GPL-3.0, inherited from upstream [kardswalker/Prism](https://github.com/kardswalker/Prism).
See `LICENSE` and `NOTICE`.
