# =====================================================================
# Run Replay's built-in GUI self-test (the "Addons page -> Replay page" path).
#
# It does not need any manual clicking: the mod switches the main scene to the
# Addons page itself, installs its entry button, clicks it, dumps the layout and
# takes screenshots. This is the regression check for the page after the
# "sub-page infrastructure" was moved into DSHCore.
#
#   powershell -File run-replay-gui-test.ps1 [-GameDir <dir>] [-TimeoutSec 300] [-Off]
#
# Notes:
#   * ASCII only (a .ps1 with Chinese and no BOM is read as GBK by PowerShell 5.1).
#   * The [SelfTest] section is turned OFF here: it is a different state machine
#     and this one quits the game when it is done.
#   * Report: <游戏目录>\BepInEx\_replay_gui.txt
#     Shots : <游戏目录>\BepInEx\_rp_shot0_page.png / _rp_shot1_confirm.png / _rp_shot2_closed.png
# =====================================================================
param(
    [string]$GameDir = '',
    [int]$TimeoutSec = 300,
    [switch]$Off,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue
. "$PSScriptRoot\build-common.ps1"

$repo = Get-RepoRoot
$GameDir = Normalize-GameDir $GameDir (Get-DefaultGameDir -RepoRoot $repo)
if (-not (Test-GameDir $GameDir)) { throw ("game dir invalid: " + $GameDir) }

$cfg = Join-Path $GameDir 'BepInEx\config\qoznos.mvz2.replay.cfg'
$report = Join-Path $GameDir 'BepInEx\_replay_gui.txt'
$exe = Join-Path $GameDir 'MinecraftVSZombies2.exe'

function Set-Section([bool]$on) {
    $lines = @()
    if (Test-Path -LiteralPath $cfg) {
        $lines = @([IO.File]::ReadAllText($cfg) -split "`r?`n")
    }
    $kept = New-Object System.Collections.Generic.List[string]
    $inTarget = $false
    foreach ($l in $lines) {
        if ($l -match '^\s*\[') {
            $inTarget = ($l -match '^\s*\[(GuiTest|SelfTest)\]\s*$')
        }
        if (-not $inTarget) { $kept.Add($l) }
    }
    if ($on) {
        $kept.Add('')
        $kept.Add('[GuiTest]')
        $kept.Add('Enabled = true')
        $kept.Add('HoldFrames = 90')
        $kept.Add('AutoQuit = true')
    }
    $text = ($kept -join "`r`n")
    [IO.File]::WriteAllText($cfg, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("config updated: {0} (GuiTest={1}, SelfTest off)" -f $cfg, $on)
}

Set-Section (-not $Off)
if ($Off) { Write-Host 'GUI self-test disabled.'; return }
if ($NoLaunch) { return }

if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report -Force }

Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host 'launching game ...'
Start-Process -FilePath $exe -WorkingDirectory $GameDir | Out-Null

$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
    $proc = Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue
    if (-not $proc) { break }
    if (Test-Path -LiteralPath $report) {
        # The report is written incrementally; "自动退出游戏" means it is done.
        $tail = [IO.File]::ReadAllText($report)
        if ($tail -match '结论') { break }
    }
    Start-Sleep -Seconds 3
}

$proc = Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host 'timeout / still running -> killing'
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}

if (Test-Path -LiteralPath $report) {
    Write-Host ('--- report: ' + $report + ' ---')
    Get-Content -LiteralPath $report -Encoding UTF8 | Select-Object -Last 120
} else {
    Write-Host 'no report produced'
}
