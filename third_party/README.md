# Third-party Native Dependencies

This directory is intentionally kept out of source control except for this note.

Optional Oodle support expects locally supplied libraries at:

```text
third_party/lib/arm64-v8a/liboodle-data-shared.so   # Android (arm64-v8a)
third_party/lib/win-x64/oo2core_9_win64.dll         # Windows x64
```

The Windows file is picked up automatically by `Prism.Desktop.Desktop.csproj`
(`CopyOodleNative` / `CopyOodleNativeForPublish`), so it lands next to
`Prism.Desktop.Desktop.exe` in both `dotnet build` and `dotnet publish` output.
It must sit beside the executable because the Rust pak writer (`repak_bind`)
loads it by name at runtime.

**Why this matters:** `repak_bind` panics (and therefore aborts the whole
process with `0xC0000409`) when Oodle compression is requested but the library
is missing. That abort cannot be caught from .NET. The app therefore probes for
the library before requesting Oodle and silently falls back to uncompressed
packing, but shipping the library avoids the fallback entirely.

If no library is present, everything still works — Pak merging, conversion and
patch building simply produce uncompressed output, and the log says so.

The Oodle source, static libraries, and generated shared libraries are not committed here. Build or provide them locally only if you have the appropriate rights to use and distribute them.

Prism does not claim ownership of Oodle, Unreal Engine code, or native artifacts derived from Epic Games or RAD Game Tools. If you place Oodle files in this directory for a local or private build, you are responsible for complying with the Unreal Engine EULA and any applicable RAD/Epic licensing terms.
