# Turn the UiGallery self-test off again (a running game would keep loading it).
# ASCII only (see run-selftest.ps1).
param(
    [string]$GameDir = ""
)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility
. "$PSScriptRoot\build-common.ps1"

# 游戏目录从目录配置读（见 run-selftest.ps1 里的说明）。
$GameDir = Normalize-GameDir $GameDir (Get-DefaultGameDir -RepoRoot (Split-Path -Parent $PSScriptRoot))
if (-not (Test-GameDir $GameDir)) { throw ("游戏目录无效: " + $GameDir) }

Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$cfg = Join-Path $GameDir 'BepInEx\config\qoznos.mvz2.uigallery.cfg'
if (Test-Path -LiteralPath $cfg) {
    (Get-Content -LiteralPath $cfg -Encoding UTF8) -replace '^SelfTest\s*=\s*.*$', 'SelfTest = false' |
        Set-Content -LiteralPath $cfg -Encoding UTF8
    Get-Content -LiteralPath $cfg -Encoding UTF8 | Select-String 'SelfTest' | ForEach-Object { Write-Host $_.Line }
}

Get-ChildItem -LiteralPath (Join-Path $GameDir 'BepInEx') -Filter '_uig_*.png' -ErrorAction SilentlyContinue | Remove-Item -Force
Remove-Item -LiteralPath (Join-Path $GameDir 'BepInEx\_selftest.txt') -Force -ErrorAction SilentlyContinue
Write-Host 'reset done'
