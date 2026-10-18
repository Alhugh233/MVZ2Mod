# =====================================================================
# Loadout self-test runner (ASCII only on purpose).
#
# Why ASCII: a .ps1 containing Chinese and saved *without* a BOM is read as
# GBK by PowerShell 5.1, so Chinese literals get mangled and string
# comparisons silently fail. This repo has been bitten by that already, so
# this script keeps everything ASCII and writes its findings to a UTF-8 file
# the caller can read with a UTF-8-aware reader.
#
# Usage:
#   powershell -File tools\run-loadout-selftest.ps1 [-GameDir <path>] [-TimeoutSec 300] [-NoLaunch] [-KeepSelfTest]
#
# Output:
#   <GameDir>\BepInEx\_loadout_selftest.txt   full report (self-test lines + key criteria)
#
# Note: the config switch is restored to empty at the end (unless -KeepSelfTest),
#       otherwise every later launch would keep opening the panel by itself.
# =====================================================================
param(
    [string]$GameDir = "",
    [int]$TimeoutSec = 300,
    [switch]$NoLaunch,
    [switch]$KeepSelfTest
)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility
. "$PSScriptRoot\build-common.ps1"

# 游戏目录从目录配置读（见 run-selftest.ps1 里的说明）。
$GameDir = Normalize-GameDir $GameDir (Get-DefaultGameDir -RepoRoot (Split-Path -Parent $PSScriptRoot))
if (-not (Test-GameDir $GameDir)) { throw ("游戏目录无效: " + $GameDir) }

$cfg = Join-Path $GameDir 'BepInEx\config\qoznos.mvz2.loadout.cfg'
$log = Join-Path $GameDir 'BepInEx\LogOutput.log'
$err = Join-Path $GameDir 'BepInEx\ErrorLog.log'
$pngDir = Join-Path $GameDir 'BepInEx'
$out = Join-Path $pngDir '_loadout_selftest.txt'

# 0) make sure no previous run is still holding the log file open
Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# 1) make sure the config exists and self-test is on
#    (BepInEx merges a partial cfg with the defaults, so a minimal file is fine)
$dir = Split-Path -Parent $cfg
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
if (-not (Test-Path -LiteralPath $cfg)) {
    "[General]`r`nSelfTest = all`r`n" | Set-Content -LiteralPath $cfg -Encoding UTF8
    Write-Host "created config: $cfg"
}
else {
    $text = Get-Content -LiteralPath $cfg -Raw -Encoding UTF8
    if ($text -match '(?m)^SelfTest\s*=') {
        $text = $text -replace '(?m)^SelfTest\s*=.*$', 'SelfTest = all'
    }
    else {
        $text = $text.TrimEnd() + "`r`nSelfTest = all`r`n"
    }
    [System.IO.File]::WriteAllText($cfg, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "patched config: $cfg"
}

# 2) clean old logs / screenshots (only when we are about to launch)
if (-not $NoLaunch) {
    foreach ($f in @($log, $err, $out)) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force } }
    Get-ChildItem -LiteralPath $pngDir -Filter '_ld_shot*.png' -ErrorAction SilentlyContinue | Remove-Item -Force
}

# 3) launch
if (-not $NoLaunch) {
    Start-Process -FilePath (Join-Path $GameDir 'MinecraftVSZombies2.exe') -WorkingDirectory $GameDir
    Write-Host 'launched, waiting for [SelfTest] DONE ...'
}

# 4) wait
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$done = $NoLaunch
while (-not $done -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    if (Test-Path -LiteralPath $log) {
        $t = Get-Content -LiteralPath $log -Raw -Encoding UTF8
        if ($t.Contains('[SelfTest] DONE')) { $done = $true; break }
    }
    if ($null -eq (Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue)) { break }
}

# 5) collect
$lines = @()
if (Test-Path -LiteralPath $log) {
    $all = Get-Content -LiteralPath $log -Encoding UTF8
    $lines += '# ---- SelfTest / dump lines ----'
    $lines += ($all | Where-Object { $_ -match '\[SelfTest\]|\[Loadout|判据|放得下|挤（会溢出）|屏幕矩形|面板宽|页集合|补齐字段|图标入口|蓝图' })
}
if (Test-Path -LiteralPath $err) {
    $e = Get-Content -LiteralPath $err -Encoding UTF8
    $lines += '# ---- ErrorLog ----'
    if ($e.Count -eq 0) { $lines += '(empty)' } else { $lines += $e }
}
$lines += '# ---- shots ----'
$shots = Get-ChildItem -LiteralPath $pngDir -Filter '_ld_shot*.png' -ErrorAction SilentlyContinue
if ($shots) { $lines += ($shots | ForEach-Object { $_.Name + '  ' + $_.Length }) } else { $lines += '(none)' }
$lines += '# ---- verdict ----'
$lines += ('completed=' + $done)
$lines += ('log=' + (Test-Path -LiteralPath $log))

[System.IO.File]::WriteAllLines($out, $lines, (New-Object System.Text.UTF8Encoding($false)))
$shotCount = 0
if ($shots) { $shotCount = $shots.Count }
$logExists = Test-Path -LiteralPath $log
Write-Host ("report -> " + $out)
Write-Host ("completed=" + $done + "  log=" + $logExists + "  shots=" + $shotCount)

# 6) switch self-test back off (the game already read the cfg at startup)
if (-not $KeepSelfTest) {
    try {
        $text = Get-Content -LiteralPath $cfg -Raw -Encoding UTF8
        $text = $text -replace '(?m)^SelfTest\s*=.*$', 'SelfTest = '
        [System.IO.File]::WriteAllText($cfg, $text, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host 'self-test restored to empty (use -KeepSelfTest to keep it on)'
    }
    catch { Write-Host ('could not restore cfg: ' + $_.Exception.Message) }
}
