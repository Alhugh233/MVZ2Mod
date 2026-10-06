# =====================================================================
# UiGallery self-test runner (ASCII only on purpose).
#
# Why ASCII: a .ps1 containing Chinese and saved *without* a BOM is read as
# GBK by PowerShell 5.1, so Chinese literals get mangled and string
# comparisons silently fail. This repo has been bitten by that already, so
# the script keeps everything ASCII and writes its findings to a UTF-8 file
# that the caller can read with a UTF-8-aware reader.
#
# Usage:
#   powershell -File tools\run-selftest.ps1 [-GameDir <path>] [-TimeoutSec 300]
#
# Output:
#   <GameDir>\BepInEx\_selftest.txt   full report (self-test log lines + pixel diffs)
# =====================================================================
param(
    [string]$GameDir = 'E:\Game\PVZ\MVZ2\MVZ2 0.7.0 test-10',
    [int]$TimeoutSec = 300,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility

$repo = Split-Path -Parent $PSScriptRoot
$cfg = Join-Path $GameDir 'BepInEx\config\qoznos.mvz2.uigallery.cfg'
$log = Join-Path $GameDir 'BepInEx\LogOutput.log'
$err = Join-Path $GameDir 'BepInEx\ErrorLog.log'
$pngDir = Join-Path $GameDir 'BepInEx'
$out = Join-Path $pngDir '_selftest.txt'

if (-not (Test-Path -LiteralPath $cfg)) { throw "config not found: $cfg" }

# 1) enable self-test
(Get-Content -LiteralPath $cfg -Encoding UTF8) -replace '^SelfTest\s*=\s*.*$', 'SelfTest = true' |
    Set-Content -LiteralPath $cfg -Encoding UTF8

# 2) clean old logs / screenshots  (only when we are about to launch: a running
#    game keeps LogOutput.log locked, and -NoLaunch wants to keep it anyway)
if (-not $NoLaunch) {
    foreach ($f in @($log, $err, $out)) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force } }
    Get-ChildItem -LiteralPath $pngDir -Filter '_uig_*.png' -ErrorAction SilentlyContinue | Remove-Item -Force
}

# 3) launch
if (-not $NoLaunch) {
    Start-Process -FilePath (Join-Path $GameDir 'MinecraftVSZombies2.exe') -WorkingDirectory $GameDir
    Write-Host 'launched, waiting for SELFTEST_DONE ...'
}

# 4) wait
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$done = $NoLaunch
while (-not $done -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    if (Test-Path -LiteralPath $log) {
        $text = Get-Content -LiteralPath $log -Raw -Encoding UTF8
        if ($text.Contains('SELFTEST_DONE')) { $done = $true; break }
    }
    if ($null -eq (Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue)) { break }
}
Write-Host ("SELFTEST_DONE = " + $done)

# 5) report
$lines = @()
$lines += ('selftest_done = ' + $done)
$lines += ('errorlog_bytes = ' + (Get-Item -LiteralPath $err -ErrorAction SilentlyContinue).Length)
$lines += ''

$logLines = @()
if (Test-Path -LiteralPath $log) { $logLines = Get-Content -LiteralPath $log -Encoding UTF8 }

# 注意两点（第一版都写错过）：
#  · 用 .Contains() 而不是 -like —— 在 -like 里 "[SelfTest]" 会被当成**字符类**；
#  · 多行日志在文件里是**分开的行**，所以要"跟着标记行把缩进的明细也收进来"，
#    但必须排除以 '[' 开头的行，否则整份启动日志都会被收进来。
$include = $false
foreach ($l in $logLines) {
    $isMarker = $l.Contains('[SelfTest]')
    if ($isMarker) { $include = $true }
    elseif ($l.StartsWith('[')) { $include = $false }

    if ($include) {
        $lines += ($l -replace '^\[Info\s*:MVZ2 UI Gallery\]\s*', '')
    }
}
$lines += ''
$lines += ('selftest_lines = ' + $lines.Count)

$lines += ''
$lines += '--- pixel diffs (region restricted; image coords, y downwards) ---'
# 每个"开着"的图都配一张同间隔的"关着"对照：标题界面自己在动，
# 只有对照才能把动画噪音从差异里排除掉。
# 说明：dd1 是**已知不可用**的下拉框 —— 这里的期望值是"差异 ≈ 对照"，即它确实没画。
$pairs = @(
    'dd1_tmpdd',
    'dd2_closed_after_tmpdd'
)
# image coords of interest:
#   popup rect (screen x496 y277 254x114) -> image y = 755-1-391 .. 755-1-277
#   panel rect (screen x254 y9  508x737)
#   left control area (title screen animation lives there)
$regions = @(
    @('popup-rect ', 496, 363, 254, 114),
    @('panel-rect ', 254, 9, 508, 737),
    @('ctrl-left  ', 0, 0, 240, 200)
)

$dd0 = Join-Path $pngDir '_uig_dd0_closed.png'
foreach ($r in $regions) {
    $lines += ('region ' + $r[0] + ' x' + $r[1] + ' y' + $r[2] + ' ' + $r[3] + 'x' + $r[4])
    foreach ($name in $pairs) {
        $b = Join-Path $pngDir ('_uig_' + $name + '.png')
        if (-not ((Test-Path -LiteralPath $dd0) -and (Test-Path -LiteralPath $b))) {
            $lines += ('  ' + $name + ' : (missing screenshot)')
            continue
        }
        $diffOut = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'img-diff.ps1') `
            -A $dd0 -B $b -RegionX $r[1] -RegionY $r[2] -RegionW $r[3] -RegionH $r[4] 2>&1
        $want = ($diffOut | Where-Object { $_ -like 'DIFF_PIXELS*' -or $_ -like 'SCANNED*' }) -join '  '
        $lines += ('  ' + $name + ' : ' + $want)
    }
}

$lines += ''
$lines += '--- screenshots ---'
foreach ($f in (Get-ChildItem -LiteralPath $pngDir -Filter '_uig_*.png' -ErrorAction SilentlyContinue)) {
    $lines += ('  ' + $f.Name + '  ' + $f.Length)
}

Set-Content -LiteralPath $out -Value $lines -Encoding UTF8
Write-Host ('report -> ' + $out)

# Also echo the extracted lines to the console (short enough to read directly).
$echo = $lines
if ($echo.Count -gt 160) { $echo = $echo[($echo.Count - 160)..($echo.Count - 1)] }
Write-Host '---------- extracted ----------'
foreach ($l in $echo) { Write-Host $l }
