# 编译 Windows 版 BINKA 编解码库（prism_binka.dll）
#
# 内容：
#   - 解码：reference/binka_decoder-main（prism_probe_binka_audio / prism_decode_binka_to_wav）
#   - 编码：reference/binkaenc-main 的 binkace.c + native/BinkaEnc 的补丁头
#           （prism_encode_binka）
#
# 编码器以前只有 binka_encode.exe（外部进程，Windows 专用）。这里把同一份源码
# 直接编进 DLL，桌面上不再依赖 exe，Android 上也用同一份源码编进 libprism_codecs.so。
#
# 用法：pwsh -File native/BinkaDll/build-win-x64.ps1

param(
    [string]$VsDevCmd = 'E:\vs\Community\Common7\Tools\VsDevCmd.bat'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')

# 源码解析顺序：native/third_party → 仓库根 → reference/（都按同名目录找）
# 想用自己下载的完整上游树，把它放到仓库根或 reference/ 下即可覆盖 third_party 里的副本。
function Resolve-Source([string]$name) {
    foreach ($base in @((Join-Path $repo 'native\third_party'), $repo, (Join-Path $repo 'reference'))) {
        $candidate = Join-Path $base $name
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw "缺少源码目录：$name（放到 native\third_party 或 reference 下都行）"
}

$binkaRoot = Resolve-Source 'binka_decoder-main'
$encRoot = Resolve-Source 'binkaenc-main'
$encCompat = Join-Path $repo 'native\BinkaEnc\compat'
$encPrism = Join-Path $repo 'native\BinkaEnc'
$output = Join-Path $repo 'third_party\lib\win-x64'

Write-Host "解码器源码: $binkaRoot"
Write-Host "编码器源码: $encRoot"

foreach ($required in $binkaRoot, $encRoot, $encCompat) {
    if (-not (Test-Path $required)) { throw "缺少源码 $required" }
}

New-Item -ItemType Directory -Force $output | Out-Null

# 仓库路径含中文，cl 经 cmd 传递时会被代码页破坏；先把源码摆到纯 ASCII 的临时目录再编。
$stage = Join-Path $env:TEMP ('prism-binka-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $stage | Out-Null

# 解码器源码（含 binka_data.h / binka_transform.h）
Copy-Item (Join-Path $binkaRoot '*.h') $stage -Force
Copy-Item (Join-Path $binkaRoot '*.cpp') $stage -Force
Copy-Item (Join-Path $repo 'native\PrismCodecs\prism_codecs.cpp') $stage -Force

# 编码器源码 + RAD 基础头
$encFiles = @(
    @{ From = "$encRoot\Src\rrCore.h";              To = 'rrCore.h' },
    @{ From = "$encRoot\Src\egttypes.h";            To = 'egttypes.h' },
    @{ From = "$encRoot\Src\cpu.h";                 To = 'cpu.h' },
    @{ From = "$encRoot\Src\binkace.c";             To = 'binkace.c' },
    @{ From = "$encRoot\Src\binkace.h";             To = 'binkace.h' },
    @{ From = "$encRoot\Src\popmal.c";              To = 'popmal.c' },
    @{ From = "$encRoot\Src\binka_ue_encode.cpp";   To = 'binka_ue_encode.cpp' },
    @{ From = "$encRoot\Include\binka_ue_encode.h"; To = 'binka_ue_encode.h' },
    @{ From = "$encRoot\Include\binka_ue_file_header.h"; To = 'binka_ue_file_header.h' },
    @{ From = "$encPrism\radfft_prism.cpp";         To = 'radfft_prism.cpp' },
    @{ From = "$encPrism\prism_binkaenc.cpp";       To = 'prism_binkaenc.cpp' }
)
foreach ($item in $encFiles) {
    if (-not (Test-Path $item.From)) { throw "缺少源码 $($item.From)" }
    Copy-Item $item.From (Join-Path $stage $item.To) -Force
}

# RAD 内部头文件的替代实现（最后覆盖，Src 里的 popmal.h 是空文件）
Copy-Item (Join-Path $encCompat '*.h') $stage -Force
Copy-Item (Join-Path $encCompat 'undeci.inc') $stage -Force

$sources = @(
    'prism_codecs.cpp',
    'bcf1_decoder.cpp',
    'binka_dec.cpp',
    'binka_transform.cpp',
    'ueba_decoder.cpp',
    'binkace.c',
    'popmal.c',
    'binka_ue_encode.cpp',
    'radfft_prism.cpp',
    'prism_binkaenc.cpp'
)
foreach ($source in $sources) {
    if (-not (Test-Path (Join-Path $stage $source))) { throw "缺少源码 $source" }
}

$dll = Join-Path $output 'prism_binka.dll'
$clLine = 'cl /nologo /LD /O2 /EHsc /utf-8 /DPRISM_BINKA_ONLY /DNDEBUG /I. ' +
    ($sources -join ' ') + ' /link /OUT:prism_binka.dll'
$cmd = "`"$VsDevCmd`" -arch=x64 -host_arch=x64 >nul && $clLine"
Write-Host $clLine

Push-Location $stage
try {
    & cmd.exe /c $cmd
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($code -ne 0) {
    throw "cl 编译失败（退出码 $code；临时目录 $stage 保留供排查）"
}

$built = Join-Path $stage 'prism_binka.dll'
if (-not (Test-Path $built)) {
    throw "没有产出 prism_binka.dll（临时目录 $stage）"
}

Copy-Item $built $dll -Force
Remove-Item -Recurse -Force $stage
$info = Get-Item $dll
Write-Host ("生成 {0}（{1:N0} 字节）" -f $info.FullName, $info.Length)
