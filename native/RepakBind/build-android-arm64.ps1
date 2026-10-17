param(
    [string]$NdkRoot = ""
)

if (-not $NdkRoot) {
    $ndkBase = "$env:LOCALAPPDATA\Android\Sdk\ndk"
    $candidates = Get-ChildItem -LiteralPath $ndkBase -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if (-not $candidates) {
        throw "No NDK found under $ndkBase. Install NDK via Android Studio SDK Manager, or pass -NdkRoot explicitly."
    }
    $NdkRoot = $candidates.FullName
    Write-Host "Auto-detected NDK: $NdkRoot"
}

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$repakRoot = Join-Path $root ".codex-tmp\repak-src"
$output = Join-Path $root "third_party\lib\arm64-v8a"

if (-not (Test-Path $repakRoot)) {
    git clone https://github.com/trumank/repak $repakRoot
}

$oodleLoader = Join-Path $repakRoot "oodle_loader\src\lib.rs"
$oodleSource = Get-Content $oodleLoader -Raw
if ($oodleSource -notmatch 'target_os = "android"') {
    $oodleSource = $oodleSource -replace '(\#\[cfg\(windows\)\]\s*static OODLE_PLATFORM: OodlePlatform = OodlePlatform \{[^}]+\};)', @'
$1

#[cfg(target_os = "android")]
static OODLE_PLATFORM: OodlePlatform = OodlePlatform {
    path: ".",
    name: "liboodle-data-shared.so",
    hash: "",
};
'@
    $oodleSource = $oodleSource -replace 'fn load_oodle\(\) -> Result<Oodle> \{\s*let path = fetch_oodle\(\)\?;\s*unsafe \{\s*let library = libloading::Library::new\(path\)\?;\s*Oodle::new\(library\)\s*\}\s*\}', @'
fn load_oodle() -> Result<Oodle> {
    #[cfg(target_os = "android")]
    {
        unsafe {
            let library = libloading::Library::new("liboodle-data-shared.so")?;
            return Oodle::new(library);
        }
    }

    #[cfg(not(target_os = "android"))]
    {
        let path = fetch_oodle()?;
        unsafe {
            let library = libloading::Library::new(path)?;
            Oodle::new(library)
        }
    }
}
'@
    Set-Content -LiteralPath $oodleLoader -Value $oodleSource -Encoding UTF8
}

$bin = Join-Path $NdkRoot "toolchains\llvm\prebuilt\windows-x86_64\bin"
$env:CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER = Join-Path $bin "aarch64-linux-android24-clang.cmd"
$env:CC_aarch64_linux_android = Join-Path $bin "aarch64-linux-android24-clang.cmd"
$env:AR_aarch64_linux_android = Join-Path $bin "llvm-ar.exe"

rustup target add aarch64-linux-android
cargo build --manifest-path (Join-Path $PSScriptRoot "Cargo.toml") --target aarch64-linux-android --release

New-Item -ItemType Directory -Force $output | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "target\aarch64-linux-android\release\librepak_bind.so") -Destination (Join-Path $output "librepak_bind.so") -Force
