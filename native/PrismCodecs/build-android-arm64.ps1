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
$outDir = Join-Path $root "native\PrismCodecs\build-android-arm64-direct"
$targetDir = Join-Path $root "third_party\lib\arm64-v8a"
$clang = Join-Path $NdkRoot "toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android24-clang++.cmd"
$libcxx = Join-Path $NdkRoot "toolchains\llvm\prebuilt\windows-x86_64\sysroot\usr\lib\aarch64-linux-android\libc++_shared.so"

if (-not (Test-Path $clang)) {
    throw "Android NDK clang++ was not found: $clang"
}

if (-not (Test-Path $libcxx)) {
    throw "Android NDK libc++ shared runtime was not found: $libcxx"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
$objDir = Join-Path $outDir "obj"
New-Item -ItemType Directory -Force -Path $objDir | Out-Null

# 第三方源码放在 reference/ 下（曾经放在仓库根目录，两个位置都兼容一下）
function Resolve-Source([string]$relative) {
    foreach ($base in @($root, (Join-Path $root "reference"))) {
        $candidate = Join-Path $base $relative
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw "缺少源码：$relative"
}

$astc = Resolve-Source "astc-encoder-main\Source"
$bc7 = Resolve-Source "bc7enc_rdo-master"
$binka = Resolve-Source "binka_decoder-main"
$encRoot = Resolve-Source "binkaenc-main"
$encSrc = Join-Path $encRoot "Src"
$encInclude = Join-Path $encRoot "Include"
$encCompat = Join-Path $root "native\BinkaEnc\compat"
$encPrism = Join-Path $root "native\BinkaEnc"
$wrapper = Join-Path $root "native\PrismCodecs\prism_codecs.cpp"
$output = Join-Path $targetDir "libprism_codecs.so"

foreach ($required in $encSrc, $encInclude, $encCompat) {
    if (-not (Test-Path $required)) { throw "缺少 BINKA 编码器源码：$required" }
}

$includeArgs = @(
    "-I", $astc,
    "-I", $bc7,
    "-I", $binka,
    # compat 必须在 encSrc 前面：reference 里的 popmal.h 是个空文件（0 字节），
    # 而真正的声明在 native/BinkaEnc/compat/popmal.h。
    "-I", $encCompat,
    "-I", $encSrc,
    "-I", $encInclude
)

# RAD 的 binkace.c / popmal.c 是 C 源码；clang++ 会把 .c 当 C++ 编，必须单独用 -x c 过一遍。
$cSources = @(
    (Join-Path $encSrc "binkace.c"),
    (Join-Path $encSrc "popmal.c")
)
$cObjects = @()
foreach ($source in $cSources) {
    $obj = Join-Path $objDir ([IO.Path]::GetFileNameWithoutExtension($source) + ".o")
    # -include 强制先吃 compat/popmal.h：Src 目录下那个同名文件是 0 字节的空壳，
    # 而 "popmal.h" 这种引号包含永远优先搜当前文件所在目录，只有先包含才盖得住。
    & $clang -c -x c -O3 -fPIC -fvisibility=hidden -std=c99 `
        -DASTCENC_NEON=1 @includeArgs -include (Join-Path $encCompat "popmal.h") $source -o $obj
    if ($LASTEXITCODE -ne 0) { throw "编译 $source 失败（退出码 $LASTEXITCODE）" }
    $cObjects += $obj
    Write-Host "Wrote $obj"
}

$sources = @(
    $wrapper,
    (Join-Path $astc "astcenc_averages_and_directions.cpp"),
    (Join-Path $astc "astcenc_block_sizes.cpp"),
    (Join-Path $astc "astcenc_color_quantize.cpp"),
    (Join-Path $astc "astcenc_color_unquantize.cpp"),
    (Join-Path $astc "astcenc_compress_symbolic.cpp"),
    (Join-Path $astc "astcenc_compute_variance.cpp"),
    (Join-Path $astc "astcenc_decompress_symbolic.cpp"),
    (Join-Path $astc "astcenc_diagnostic_trace.cpp"),
    (Join-Path $astc "astcenc_entry.cpp"),
    (Join-Path $astc "astcenc_find_best_partitioning.cpp"),
    (Join-Path $astc "astcenc_ideal_endpoints_and_weights.cpp"),
    (Join-Path $astc "astcenc_image.cpp"),
    (Join-Path $astc "astcenc_integer_sequence.cpp"),
    (Join-Path $astc "astcenc_mathlib.cpp"),
    (Join-Path $astc "astcenc_mathlib_softfloat.cpp"),
    (Join-Path $astc "astcenc_partition_tables.cpp"),
    (Join-Path $astc "astcenc_percentile_tables.cpp"),
    (Join-Path $astc "astcenc_pick_best_endpoint_format.cpp"),
    (Join-Path $astc "astcenc_quantization.cpp"),
    (Join-Path $astc "astcenc_symbolic_physical.cpp"),
    (Join-Path $astc "astcenc_weight_align.cpp"),
    (Join-Path $astc "astcenc_weight_quant_xfer_tables.cpp"),
    (Join-Path $bc7 "bc7enc.cpp"),
    (Join-Path $bc7 "bc7decomp.cpp"),
    (Join-Path $bc7 "bc7decomp_ref.cpp"),
    (Join-Path $bc7 "ert.cpp"),
    (Join-Path $bc7 "lodepng.cpp"),
    (Join-Path $bc7 "rgbcx.cpp"),
    (Join-Path $bc7 "rdo_bc_encoder.cpp"),
    (Join-Path $bc7 "utils.cpp"),
    (Join-Path $binka "bcf1_decoder.cpp"),
    (Join-Path $binka "binka_dec.cpp"),
    (Join-Path $binka "binka_transform.cpp"),
    (Join-Path $binka "ueba_decoder.cpp"),
    (Join-Path $encSrc "binka_ue_encode.cpp"),
    (Join-Path $encPrism "radfft_prism.cpp"),
    (Join-Path $encPrism "prism_binkaenc.cpp")
)

& $clang `
    -shared `
    -O3 `
    -fPIC `
    -fvisibility=hidden `
    -fno-strict-aliasing `
    -std=c++17 `
    -DANDROID `
    -DSUPPORT_BC7E=0 `
    -DASTCENC_NEON=1 `
    -DASTCENC_SVE=0 `
    -DASTCENC_SSE=0 `
    -DASTCENC_AVX=0 `
    -DASTCENC_POPCNT=0 `
    -DASTCENC_F16C=0 `
    @includeArgs `
    $sources `
    $cObjects `
    -o $output

if ($LASTEXITCODE -ne 0) { throw "链接 $output 失败（退出码 $LASTEXITCODE）" }

Copy-Item -LiteralPath $libcxx -Destination (Join-Path $targetDir "libc++_shared.so") -Force
Write-Host "Wrote $output"
