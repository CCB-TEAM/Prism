# Prism

**English** · [简体中文](README.zh-CN.md)

UE `.pak` asset management toolkit for **Windows desktop and Android**, built on [CUE4Parse](https://github.com/FabianFG/CUE4Parse). Extends [kardswalker/Prism](https://github.com/kardswalker/Prism).

Browse pak contents, preview textures / audio / 3D meshes / localization, export and share assets, build texture / audio / video replacement mod paks — and convert texture packs between the PC and mobile builds of a game.

## Features

**Browsing & preview**
- Mount UE4/UE5 `.pak` archives through CUE4Parse; AES key support for encrypted paks.
- `.usmap` **and** `.jmap` mapping files (including `.gz`), auto-detected by extension.
- File-manager style browsing, keyword search, and **direct path navigation** from the search box.
- Groups related `.uasset` / `.uexp` / `.ubulk` into one asset entry.
- Previews: textures (with thumbnails), audio (built-in player), 3D mesh wireframe, localization (`.locres`), blueprint pseudo-code.
- **Audio preview**: play / pause / scrub. BINKA sounds decode on desktop too (via `prism_binka.dll`).
- **Video preview**: embedded playback (ffmpeg decodes frames, `-re` paces them in real time), poster
  frame, duration / resolution, timeline; without ffmpeg you still get "open in system player".
- Export raw packages, PNG images, whole folders, or share to other apps.

**Texture replacement**
- Pick a texture asset, supply an image, and build a patch pak — no manual repacking.
- Desktop encodes via UAssetCLI + astcenc / texconv; Android encodes in-process via `libprism_codecs`.
- Uncompressed textures (`PF_B8G8R8A8` / `PF_R8G8B8A8` / `PF_A8R8G8B8`) and `PF_DXT1` need no
  external encoder — the pixels are packed / BC1-encoded directly.

**Audio & video replacement**
- Pick a streamed `SoundWave`, supply a clip (WAV / MP3 / OGG, plus FLAC / M4A when ffmpeg is around),
  and it is encoded to BINKA and written back — channels, sample rate and duration are updated too,
  with a 0–9 quality knob.
- Video (or any raw file in the pak) is replaced byte for byte: select the `.mp4` / `.bk2`, supply a new
  file. Selecting a `UFileMediaSource` asset resolves to the video it points at.
- Both live on the same rail: add to replace list -> choose the clip -> build a patch pak.
- Longer clips are split into multiple streaming chunks automatically (each ≤ 256 KB, mirroring how
  the game itself chunks them), so the old "about a minute" ceiling is gone.
- Audio is encoded by a built-in native encoder (`prism_encode_binka`: `prism_binka.dll` on
  desktop, `libprism_codecs.so` on Android — sources in
  [native/BinkaEnc](native/BinkaEnc/README.md)), so **audio replacement works on both
  platforms**. It only falls back to `tools/binka_encode.exe` (a third-party binary, not
  committed — see [tools/README.md](tools/README.md)) when the native library is missing.
- Video replacement follows the same rail and works on both platforms as well.

**Pak merging**
- Select multiple paks at once and drag to reorder; **the higher entry wins** on conflicting paths.
  Every entry is removable and reorderable — there is no pinned "main pak".
- Conflict inspection before building.
- Optional **baseline `.locres`**: give it the game's original localization file and same-named
  `.locres` files are merged entry by entry instead of one whole file winning — so two mods that each
  edited different strings can be combined. Without a baseline they fall back to whole-file override.

**Pak conversion (cross-platform texture porting)**
- Move textures from one platform's pak into another's, re-encoded to the target asset's format.
- Outputs only the changed assets by default — a 7 GB main pak plus one texture yields a **685 KB** mod pak.
- Optionally carry the mod's other files (blueprints, `.locres`, data tables) straight into the output
  pak — they are platform-agnostic, so no conversion and no separate merge step is needed.
- See [How Pak conversion works](#how-pak-conversion-works).

**Localization**
- Edit `.locres` entries in-app and write them back into a patch pak.
- Export localization to JSON, and round-trip it safely across all four locres format versions.

**Diagnostics**
- Structured log with severity, timestamps, full exception stacks, on-disk mirror, and an exportable report with environment header.
- Settings page lists every external dependency (UAssetCLI, astcenc, texconv, Oodle) with a green/red dot,
  so "texture replacement doesn't work" answers itself.

**Updates**
- Checks GitHub releases on start-up (once per 6 h, can be turned off). When there is a newer release the
  welcome page is replaced by that release's notes; on Android it is a skippable dialog instead.
- Desktop: **download and update in place** — the zip is fetched, verified (SHA-256 when the release
  provides a digest) and extracted, then a generated script waits for the app to exit, backs up the old
  exe, robocopies the new files in and restarts it. Android: downloads the APK and hands it to the
  system installer.
- "Later" is remembered per version; "Check for updates" in Settings re-triggers it at any time.

## Layout

Projects sit at the repository root — `PakTool.Core` and friends resolve `..\external\CUE4Parse`
and `..\third_party` as siblings, so a flat tree is what the project files expect.

```
Prism/                     Android WebView app (earlier, upstream-derived)
Prism.PC/                     Local web UI build
Prism.Desktop/                Shared Avalonia UI + logic (library, net10.0)
  Docs/                       In-app guide content + doc screenshot provider
  Views/HelpView.axaml        The guide page (rendered from Docs/PrismDocs.cs)
Prism.Desktop.Desktop/        Windows shell (single-file publish)
Prism.Desktop.Android/        Android shell (APK, arm64-v8a)
PakTool.Core/                 Pak session / preview / export / merge / mappings / locres
UAssetTexture.Core/           Texture parsing / mip encoding / replacement (UAssetAPI + ImageSharp)
UAssetTexture.PakConversion/  Pak conversion on top of it (needs PakTool.Core / CUE4Parse)
UAssetCLI/                    Texture replacement CLI (references UAssetTexture.Core)
test/Prism.FeatureTests/      Integration tests (console app)
UAssetAPI-master/             Vendored UAssetAPI with local patches
tools/                        astcenc + texconv (copied next to the app on build)
third_party/                  Android native libraries (not committed, see below)
```

Not committed — supply locally if you need them:

```
external/CUE4Parse/        Build dependency, must exist before anything compiles
third_party/lib/           The .so files for Android builds
native/                    Source of those .so files (PrismCodecs C++, RepakBind Rust)
reference/                 Local clones kept for reference (astcenc, FModel, …)
```

`UAssetCLI` references `UAssetTexture.Core` directly, so there is exactly **one** texture parser,
format table and mip writer for both the in-process and the CLI path. That only works because
`UAssetTexture.Core` is kept free of CUE4Parse — everything that needs pak sessions lives in
`UAssetTexture.PakConversion`. The AOT binary stays lean (no CUE4Parse in the bundle) and a parser fix can no
longer reach one path but not the other.

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
```

### The texture CLI (`UAssetCLI/`)

Texture replacement and pak conversion shell out to `UAssetCLI`, which is **not** a project
reference — the desktop build copies it into `UAssetCLI/` next to the executable. Build it before
the app, and build it in Release so it lands where the copy target looks:

```sh
dotnet build UAssetCLI/UAssetCLI.csproj -c Release
```

A Release build of the CLI is a framework-dependent executable that needs the .NET 10 runtime
installed. To get the self-contained native binary instead (requires the AOT toolchain), publish it:

```sh
dotnet publish UAssetCLI/UAssetCLI.csproj -c Release
```

Both the app's build and its publish copy the CLI; publish copies from the CLI's publish output, so
run the CLI publish too when you want a complete release directory. `smoke` reports a missing CLI.

### `external/CUE4Parse`

`PakTool.Core` references `..\external\CUE4Parse\CUE4Parse\CUE4Parse.csproj` and
`CUE4Parse-Conversion.csproj`, so that tree must exist before anything builds. **It is not in this
repository and `.gitmodules` cannot fetch it for you** — the tree this project was developed
against carries local changes and predates current upstream (`CUE4Parse-Natives`, per-package
versions such as SharpGLTF 1.0.6 rather than upstream's alpha), and `.gitmodules` declares no
pinned commit.

Supply one of:

- **A copy of the matching tree**, placed at `external/CUE4Parse/`, if you have the same development
  snapshot. This is the only option guaranteed to match.
- **Upstream `main`** cloned to `external/CUE4Parse/`, then `git checkout` the commit your game's
  version needs. Expect API drift: this code was written against an older CUE4Parse, so a current
  checkout may need fixes, and `CUE4Parse-Natives/ACL` is itself a submodule
  (`git submodule update --init --recursive` inside it).

`third_party/` and `tools/` resolve to this repository's root, so a plain layout works as-is.

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
| `MergeSourceFiles` | Changed textures **+ the source pak's other files** (blueprints, `.locres`, data tables) copied verbatim | ≈ size of the change |
| `FullRepack` | Every main-pak file plus the replaced textures | ≈ main pak |
| `MergeAll` | Replacement + source-only files + remaining main-pak files | ≈ main pak |

Games load mod paks as an overlay where the later-registered copy of a path wins, so `ReplacedOnly`
is what a mod pak should contain. Measured: **7 GB main pak + 1 texture → 685 KB output**, 3.2 s
wall clock and 1.6 MB peak temp space (a full repack would need ≈7 GB temp and 88 s).

`MergeSourceFiles` is the one to reach for when a mod does more than swap textures: blueprints,
localization and data tables are platform-agnostic, so they need no conversion — they are copied
into the output pak as-is (the mod's copy wins, matching overlay semantics). Everything the run
*did* convert as a texture is excluded, otherwise a source-platform pixel format (say PC's BC)
would overwrite the freshly re-encoded asset. Textures that could not be converted are still
carried verbatim and reported, rather than dropped silently. Measured on a 39-texture mod:
default mode 40 entries / 14.29 MB with no `.locres`; with the toggle 41 entries / 15.21 MB and
`Game.locres` included.

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
| Welcome | Landing page; becomes the release notes when GitHub has a newer version |
| Unpack & Mods | Browse/search pak contents, preview assets, export, texture replacement, patch pak |
| Pak Merge | Multi-select paks, long-press/drag to reorder override priority, build one pak |
| Pak Convert | Port same-path textures from another platform's pak, re-encoded for the main pak |
| Guide (使用说明) | The manual itself: nine chapters, real screenshots of the current version |
| Settings | Appearance, behaviour, updates, dependency status, log — **no duplicated inputs** |

- **Search box**: a keyword searches; a pak-internal path (e.g. `kards/Content/Assets/Textures`)
  navigates instead. The placeholder changes to indicate which mode the input will use.
- **Merge list**: **the higher entry wins** on conflicting paths, and every entry can be dragged or
  removed — there is no pinned main pak. Drag the `⠿` handle — **on touch, long-press for ~0.35 s first**.
- **Guide**: a real page, not an overlay. The top-bar 使用说明 button on each page jumps straight to
  that page's chapter. Layout follows the welcome page (accent rule + chapter number + title, then a
  divider, a table of contents on the left and the chapter body on the right). Content lives in
  `Docs/PrismDocs.cs` as data (headings / paragraphs / steps / bullets / figures / callouts) and is
  rendered by one set of templates, so adding a chapter is adding a record. Figures are capped in
  height and open full-size on click; the PNGs are generated by `--capture --docs` from the **current
  build** in both themes, so the guide can never show a button that no longer exists.
- **Settings**: grouped cards with a header row (icon + title + one-line subtitle) and hairline-separated
  rows: name and explanation on the left, switch or action right-aligned. Paths, mapping file, AES key
  and merge/convert outputs are **not** duplicated here — they belong to the page that uses them, and
  two editors for one value made it ambiguous which one "took". A 前往配置 action remains for
  discoverability.
- **Adaptive density**: one UI, two tiers. Below 620 px it is the compact single column built for
  phones. From **1200 px** it enters a wide tier that (a) enlarges the **text only** — through
  app-level font-size resources, so padding and control sizes stay put — and (b) widens the content
  columns (620 → 1000) and lays the settings/merge cards out in **two columns**, so pages get
  *shorter* rather than taller. Resizing switches tiers live; the phone layout is unchanged.
  Scaling the whole UI by transform was tried first and rejected: it enlarged padding and controls
  too, which read as a zoomed phone UI and put *less* on screen than before.
  Every font size is an **integer**: at 100 % display scaling a half-pixel size (10.5, 12.5 …) can
  only be spread over half a pixel, and anti-aliasing smears the strokes into grey edges — which is
  exactly why the windowed UI used to look blurry while a maximised one looked sharp.
- **Theme**: 跟随系统 / 浅色 / 深色 in Settings. The dark theme existed but was unreachable — the app
  followed the OS, so on a light Windows you always got a full screen of light surface, which is
  tiring on a 1920 display. Dark uses **white** body text (15.1:1 on cards); the light theme is the
  opposite trade — its body text is softened to `#33322e` (11.6:1) because pure black on white was
  glaring. The two themes are tuned in opposite directions on purpose.
- **Legibility**: secondary text sits at **5.4:1** against cards (4.9:1 against the page) and the
  page background is separated from the white cards. Text is never dimmed with `Opacity` — that
  multiplies contrast down (one common label style measured **2.0:1**) and is what made the UI look
  washed out. Hierarchy comes from size and weight instead.

## Verifying

```sh
# Integration tests (341 assertions): mapping format detection, search-box path resolution,
# pak conversion (both paths + all output modes), merge priority, locres <-> JSON round-trip,
# non-ASCII pak path handling, headless UI loading and navigation, audio replacement
# (BINKA round-trip through a real pak), media replacement, the replace-page flow driven
# through the real view-model, uncompressed texture formats (PF_B8G8R8A8 …), a real
# UAssetCLI replace run (needs its Release build; skipped without it), and the update
# machinery (release JSON, version comparison, note parsing, and a self-update script that
# is actually executed against a disposable directory)
dotnet run --project test/Prism.FeatureTests

# UI screenshots: every page × light/dark × compact/wide, rendered by Skia to PNG
# (useful for design review and before/after comparisons; writes to the given folder)
dotnet run --project test/Prism.FeatureTests -- --capture artifacts/ui

# Guide screenshots: renders the real pages in both themes into
# Prism.Desktop/Assets/help/{light,dark}/ — run this after changing the UI, then rebuild,
# otherwise the in-app guide shows the previous version
dotnet run --project test/Prism.FeatureTests -- --capture ignore --docs

# Smoke test against a real pak: open -> search -> texture preview -> audio replacement, plus dependency self-check
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
- **`NU1608: SkiaSharp 2.88.9 vs 3.119.4` is expected, and it is live, not dead code.** Avalonia 12's
  renderer requires SkiaSharp 3.119.4 while the vendored `CUE4Parse-Conversion` pins 2.88.9, so NuGet
  resolves 3.119.4 and the pinned reference is ignored. That code path *does* run: every texture
  preview and PNG export goes through `CTexture.Encode(...)` → `ToSkBitmap()` → `SKBitmap`. It works
  only because the handful of SkiaSharp members used there are unchanged in 3.x — it is a two-major-
  version ABI gamble, not a clean build. Do not silence the warning; treat it as the reason `--smoke`
  exists, and re-check texture preview after any Avalonia or CUE4Parse bump.

## Scope

`.pak` archives only; `.utoc` / `.ucas` containers are not implemented. Mapping files are not bundled —
import the matching one from the game.

## License

GPL-3.0, inherited from upstream [kardswalker/Prism](https://github.com/kardswalker/Prism).
See `LICENSE` and `NOTICE`.