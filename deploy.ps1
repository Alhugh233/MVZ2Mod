# MVZ2LabMod 一键部署脚本
# 功能：部署 XML 资源 → 构建插件 → 停游戏 → 拷贝插件 → 启动游戏
param(
    [string]$GameDir = "E:\Game\PVZ\MVZ2\MVZ2 0.7.0 test-1",
    [switch]$SkipLaunch
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dataDir = Join-Path $GameDir "MinecraftVSZombies2_Data"
$assetTarget = Join-Path $dataDir "StreamingAssets\Mods\mvz2_lab"

Write-Host "== [1/5] 部署 Mod 资源（XML metas）→ $assetTarget" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $assetTarget | Out-Null
Copy-Item (Join-Path $root "ModAssets\mvz2_lab\*") $assetTarget -Recurse -Force

Write-Host "== [2/5] 构建插件（构建后也会自动部署资源与拷贝 DLL）" -ForegroundColor Cyan
dotnet build (Join-Path $root "MVZ2LabMod\MVZ2LabMod.csproj") -c Debug
if ($LASTEXITCODE -ne 0) { Write-Error "构建失败，终止。"; exit 1 }

Write-Host "== [3/5] 停止游戏进程（释放 DLL 锁）" -ForegroundColor Cyan
Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Write-Host "== [4/5] 拷贝插件 DLL" -ForegroundColor Cyan
Copy-Item (Join-Path $root "MVZ2LabMod\bin\Debug\MVZ2LabMod.dll") (Join-Path $GameDir "BepInEx\plugins\MVZ2LabMod.dll") -Force

Write-Host "== [5/5] 验证部署产物" -ForegroundColor Cyan
Get-ChildItem $assetTarget -Recurse -File | ForEach-Object { Write-Host ("  " + $_.FullName.Substring($assetTarget.Length)) }

if (-not $SkipLaunch) {
    Write-Host "== 启动游戏 ==" -ForegroundColor Cyan
    Start-Process -FilePath (Join-Path $GameDir "MinecraftVSZombies2.exe") -WorkingDirectory $GameDir
    Write-Host "完成。日志检查点：" -ForegroundColor Green
    Write-Host "  BepInEx  : $GameDir\BepInEx\LogOutput.log   （找 [ModFileProvider]/[MetaValidation]）"
    Write-Host "  游戏日志 : %LOCALAPPDATA%Low\Cuerzor\MinecraftVSZombies2Test\mvz2_log.log"
} else {
    Write-Host "完成（未启动游戏）。"
}
