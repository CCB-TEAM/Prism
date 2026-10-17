# 构建 Windows 版 BINKA 音频编码器（binka_encode.exe 的自建替代品）
#
# 目的：验证我们能不能不依赖 RAD 的预编译静态库、也不依赖 Windows 专有代码，
# 自己把 RAD 的编码器源码（binkace.c）编出来。如果这一步通了，同一套源码就能
# 用 NDK 交叉编译成 arm64 的 .so，手机端的音频替换也就成立了。
#
# RAD 的 binkace.c 需要 radmath.h / varbits.h / popmal.h / radfft.h / undeci.inc
# 这几个内部头文件；仓库里没有，compat/ 下是按调用点重写的等价实现。
#
# 用法：pwsh -File native/BinkaEnc/build-win-x64.ps1 [-Mode exe|dll]

param(
    [string]$VsDevCmd = 'E:\vs\Community\Common7\Tools\VsDevCmd.bat',
    [ValidateSet('exe', 'dll', 'probe')]
    [string]$Mode = 'exe',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')

# 源码解析顺序：native/third_party → 仓库根 → reference/（都按同名目录找）
function Resolve-Source([string]$name) {
    foreach ($base in @((Join-Path $repo 'native\third_party'), $repo, (Join-Path $repo 'reference'))) {
        $candidate = Join-Path $base $name
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw "缺少源码目录：$name（放到 native\third_party 或 reference 下都行）"
}

$encRoot = Resolve-Source 'binkaenc-main'
$decRoot = Resolve-Source 'binka_decoder-main'

if (-not (Test-Path $encRoot)) { throw "缺少编码器源码 $encRoot" }
if (-not (Test-Path $decRoot)) { throw "缺少解码器源码 $decRoot" }

if (-not $OutDir) {
    $OutDir = Join-Path $repo 'artifacts\binka-enc'
}
New-Item -ItemType Directory -Force $OutDir | Out-Null

# 仓库路径含中文，cl 经 cmd 传递时会按代码页破坏路径；统一搬到纯 ASCII 临时目录再编。
$stage = Join-Path $env:TEMP ('prism-binka-enc-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $stage | Out-Null

$copies = @(
    @{ From = "$encRoot\Src\rrCore.h";           To = 'rrCore.h' },
    @{ From = "$encRoot\Src\egttypes.h";          To = 'egttypes.h' },
    @{ From = "$encRoot\Src\cpu.h";               To = 'cpu.h' },
    @{ From = "$encRoot\Src\binkace.c";           To = 'binkace.c' },
    @{ From = "$encRoot\Src\binkace.h";           To = 'binkace.h' },
    @{ From = "$encRoot\Src\popmal.c";            To = 'popmal.c' },
    @{ From = "$encRoot\Src\binka_ue_encode.cpp"; To = 'binka_ue_encode.cpp' },
    @{ From = "$encRoot\Src\binka_encode_main.cpp"; To = 'binka_encode_main.cpp' },
    @{ From = "$encRoot\Include\binka_ue_encode.h";       To = 'binka_ue_encode.h' },
    @{ From = "$encRoot\Include\binka_ue_file_header.h";  To = 'binka_ue_file_header.h' },
    @{ From = "$decRoot\binka_transform.cpp";     To = 'binka_transform.cpp' },
    @{ From = "$decRoot\binka_transform.h";       To = 'binka_transform.h' },
    @{ From = "$decRoot\binka_data.h";            To = 'binka_data.h' },
    @{ From = "$PSScriptRoot\compat\radmath.h";   To = 'radmath.h' },
    @{ From = "$PSScriptRoot\compat\varbits.h";   To = 'varbits.h' },
    @{ From = "$PSScriptRoot\compat\popmal.h";    To = 'popmal.h' },
    @{ From = "$PSScriptRoot\compat\radfft.h";    To = 'radfft.h' },
    @{ From = "$PSScriptRoot\compat\undeci.inc";  To = 'undeci.inc' },
    @{ From = "$PSScriptRoot\radfft_prism.cpp";   To = 'radfft_prism.cpp' },
    @{ From = "$PSScriptRoot\dct_probe.cpp";      To = 'dct_probe.cpp' }
)

foreach ($item in $copies) {
    if (-not (Test-Path $item.From)) { throw "缺少源码 $($item.From)" }
    Copy-Item $item.From (Join-Path $stage $item.To) -Force
}

$sources = @('binkace.c', 'popmal.c', 'binka_ue_encode.cpp', 'radfft_prism.cpp', 'binka_transform.cpp')
if ($Mode -eq 'exe') {
    $sources += 'binka_encode_main.cpp'
}
if ($Mode -eq 'probe') {
    $sources = @('radfft_prism.cpp', 'binka_transform.cpp', 'dct_probe.cpp')
}

if ($Mode -eq 'exe') {
    $clLine = 'cl /nologo /O2 /EHsc /utf-8 /DNDEBUG /I. ' + ($sources -join ' ') +
              ' /Fe:prism_binka_encode.exe'
}
elseif ($Mode -eq 'probe') {
    $clLine = 'cl /nologo /O2 /EHsc /utf-8 /DNDEBUG /I. ' + ($sources -join ' ') +
              ' /Fe:prism_dct_probe.exe'
}
else {
    $clLine = 'cl /nologo /LD /O2 /EHsc /utf-8 /DNDEBUG /I. ' + ($sources -join ' ') +
              ' /Feprism_binkaenc.dll /link /OUT:prism_binkaenc.dll'
}
Write-Host $clLine

$cmd = "`"$VsDevCmd`" -arch=x64 -host_arch=x64 >nul && $clLine"
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

$artifact = switch ($Mode) {
    'exe'   { 'prism_binka_encode.exe' }
    'probe' { 'prism_dct_probe.exe' }
    default { 'prism_binkaenc.dll' }
}
$built = Join-Path $stage $artifact
if (-not (Test-Path $built)) { throw "没有产出 $artifact（临时目录 $stage）" }

$target = Join-Path $OutDir $artifact
Copy-Item $built $target -Force
Remove-Item -Recurse -Force $stage

$info = Get-Item $target
Write-Host ("生成 {0}（{1:N0} 字节）" -f $info.FullName, $info.Length)
