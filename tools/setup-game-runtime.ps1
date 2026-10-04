# =====================================================================
# Install the repo's BepInEx runtime into a game folder and regenerate
# the IL2CPP interop assemblies for THAT game build.
#
# Why: interop assemblies are bound to the game's metadata tokens. A game
# rebuild (even a pure C# change) reorders tokens, so a stale interop set
# causes native AV (0xC0000005) at an arbitrary interop call site.
# Interop must therefore always be generated from the exact game binary
# that will run it.
#
# What it does:
#   1. stop the game
#   2. copy winhttp.dll / doorstop_config.ini / .doorstop_version / dotnet\
#      and BepInEx\{core,patchers,unity-libs,config} from runtime\ into the
#      game folder (interop + plugins are NOT copied)
#   3. delete the game's BepInEx\interop so it gets regenerated
#   4. launch the game once and wait for interop generation to finish
#   5. stop the game and report the interop assembly count
#
# Then refresh the repo runtime with (this keeps runtime\ authoritative):
#     .\tools\sync-runtime.ps1 -GameDir "<GameDir>"
#
# ASCII only (see run-selftest.ps1 for why).
# =====================================================================
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDir,

    [string]$RuntimeDir = "",

    [int]$TimeoutSec = 420,

    # Copy the environment but do not launch / regenerate.
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility

. "$PSScriptRoot\build-common.ps1"

if ([string]::IsNullOrWhiteSpace($RuntimeDir)) {
    $RuntimeDir = Join-Path (Get-RepoRoot) 'runtime'
}

function Write-Step([string]$msg) { Write-Host ("[setup] " + $msg) -ForegroundColor Cyan }

if (-not (Test-Path (Join-Path $RuntimeDir 'BepInEx\core\BepInEx.Core.dll'))) {
    throw ("runtime 里没有 BepInEx 环境: " + $RuntimeDir)
}
if (-not (Test-Path (Join-Path $GameDir 'MinecraftVSZombies2.exe'))) {
    throw ("游戏目录无效（找不到 MinecraftVSZombies2.exe）: " + $GameDir)
}

$GameDir = $GameDir.TrimEnd('\') + '\'
$RuntimeDir = $RuntimeDir.TrimEnd('\') + '\'

Write-Step ("game    = " + $GameDir)
Write-Step ("runtime = " + $RuntimeDir)

# --- 1. stop the game -------------------------------------------------
Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# --- 2. copy the runtime environment ---------------------------------
$files = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')
foreach ($f in $files) {
    $src = Join-Path $RuntimeDir $f
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination $GameDir -Force
    }
}

$dirs = @('dotnet', 'BepInEx\core', 'BepInEx\patchers', 'BepInEx\unity-libs', 'BepInEx\config')
foreach ($d in $dirs) {
    $src = Join-Path $RuntimeDir $d
    if (-not (Test-Path -LiteralPath $src)) { continue }
    $dst = Join-Path $GameDir $d
    New-Item -ItemType Directory -Path $dst -Force | Out-Null
    Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force
    Write-Step ("copied " + $d)
}

foreach ($d in @('BepInEx\plugins', 'BepInEx\cache')) {
    $p = Join-Path $GameDir $d
    if (-not (Test-Path -LiteralPath $p)) {
        New-Item -ItemType Directory -Path $p -Force | Out-Null
    }
}

# --- 3. force interop regeneration -----------------------------------
$interop = Join-Path $GameDir 'BepInEx\interop'
if (Test-Path -LiteralPath $interop) {
    Remove-Item -LiteralPath $interop -Recurse -Force
    Write-Step "removed stale BepInEx\interop"
}

$cfg = Join-Path $GameDir 'BepInEx\config\BepInEx.cfg'
if (-not (Test-Path -LiteralPath $cfg)) {
    throw ("缺少 BepInEx.cfg: " + $cfg)
}
$text = [System.IO.File]::ReadAllText($cfg)
if ($text -notmatch 'UpdateInteropAssemblies\s*=\s*true') {
    $text = [regex]::Replace($text, 'UpdateInteropAssemblies\s*=\s*\S+', 'UpdateInteropAssemblies = true')
    [System.IO.File]::WriteAllText($cfg, $text)
    Write-Step "set UpdateInteropAssemblies = true"
}

if ($NoLaunch) {
    Write-Step "NoLaunch: environment ready, skipping interop generation"
    exit 0
}

# --- 4. launch and wait for interop generation -----------------------
$log = Join-Path $GameDir 'BepInEx\LogOutput.log'
$err = Join-Path $GameDir 'BepInEx\ErrorLog.log'
foreach ($p in @($log, $err)) {
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue }
}

function Read-TextShared([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    try {
        $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            $sr = New-Object System.IO.StreamReader($fs)
            return $sr.ReadToEnd()
        } finally { $fs.Dispose() }
    } catch { return '' }
}

Write-Step "launching game (interop generation takes a while)..."
Start-Process -FilePath (Join-Path $GameDir 'MinecraftVSZombies2.exe') -WorkingDirectory $GameDir

$deadline = (Get-Date).AddSeconds($TimeoutSec)
$generated = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    $t = Read-TextShared $log
    if ($t -match 'Generating interop assemblies') {
        Write-Step "generating interop assemblies..."
        # wait for the Done marker that follows generation
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 3
            $t2 = Read-TextShared $log
            if ($t2 -match 'Done!') { $generated = $true; break }
            if (-not (Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue)) { break }
        }
        break
    }
    if (-not (Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue)) {
        Write-Step "game exited before interop generation was detected"
        break
    }
}

# --- 5. stop the game and report -------------------------------------
Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$count = 0
if (Test-Path -LiteralPath $interop) {
    $count = (Get-ChildItem -LiteralPath $interop -Filter *.dll -ErrorAction SilentlyContinue).Count
}
Write-Step ("interop assemblies: " + $count)

$report = @()
$report += "game=$GameDir"
$report += "generated=$generated"
$report += "interopDlls=$count"
$report += "timestamp=" + (Get-Date).ToString('s')
$report | Set-Content -LiteralPath (Join-Path $GameDir 'BepInEx\_setup_report.txt') -Encoding UTF8

if ($count -lt 50) {
    throw ("interop 生成似乎失败（只有 " + $count + " 个 dll）。检查 " + $log)
}
Write-Step "OK"
