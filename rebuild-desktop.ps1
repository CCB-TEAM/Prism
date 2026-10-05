# 重新构建带修复的桌面端 Release 版
#
# 背景：贴图替换由 UAssetCLI 子进程完成，修复在 UAssetCLI/TextureAssetParser.cs。
# 应用启动时只做一次依赖探测，且构建过程会先清空再填回 UAssetCLI/ 与 tools/，
# 因此必须在应用关闭时重建，然后再启动应用。

$ErrorActionPreference = 'Stop'
Set-Location 'E:\项目\prism\prism'

$running = Get-Process 'Prism.Desktop.Desktop' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "检测到 Prism 正在运行（PID $($running.Id -join ', ')）。" -ForegroundColor Yellow
    Write-Host "文件被占用会导致构建失败，请先关闭它，然后重新运行本脚本。"
    Write-Host "（若确认可以强制关闭，改用：Stop-Process -Id $($running.Id -join ',') -Force）"
    exit 1
}

Write-Host "== 1/3 构建修复版 UAssetCLI（Release AOT）==" -ForegroundColor Cyan
dotnet publish 'UAssetCLI/UAssetCLI.csproj' -c Release -v:q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw "UAssetCLI 构建失败" }

Write-Host "== 2/3 构建桌面端 Release（并把新 CLI 与 tools 复制进去）==" -ForegroundColor Cyan
dotnet build 'Prism.Desktop.Desktop/Prism.Desktop.Desktop.csproj' -c Release -v:q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw "桌面端构建失败" }

Write-Host "== 3/3 校验产物 ==" -ForegroundColor Cyan
$out = 'Prism.Desktop.Desktop\bin\Release\net10.0'
$cli = "$out\UAssetCLI\UAssetCLI.exe"
$tools = @(Get-ChildItem "$out\tools" -Filter '*.exe' -ErrorAction SilentlyContinue)
$ok = $true
if (-not (Test-Path "$out\Prism.Desktop.Desktop.exe")) { Write-Host "  ✗ 缺少主程序" -ForegroundColor Red; $ok = $false } else { Write-Host "  ✓ 主程序" }
if (-not (Test-Path $cli)) { Write-Host "  ✗ 缺少 UAssetCLI" -ForegroundColor Red; $ok = $false }
else { Write-Host "  ✓ UAssetCLI（$([math]::Round((Get-Item $cli).Length/1MB,1)) MB, $((Get-Item $cli).LastWriteTime)）" }
if ($tools.Count -lt 4) { Write-Host "  ✗ 编码器工具只有 $($tools.Count) 个" -ForegroundColor Red; $ok = $false }
else { Write-Host "  ✓ 编码器工具 $($tools.Count) 个" }

if (-not $ok) { exit 1 }

Write-Host "`n完成。启动：" -ForegroundColor Green
Write-Host "  & '$((Resolve-Path "$out\Prism.Desktop.Desktop.exe").Path)'"
