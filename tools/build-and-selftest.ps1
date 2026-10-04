# Build a mod (default: UiGallery) and then run the UiGallery self-test.
#
# Why a script instead of a one-liner in tasks.json: nested powershell -Command
# quoting mangles the single quotes, and `$` gets swallowed by the task wrapper.
# ASCII only (a .ps1 with Chinese and no BOM is read as GBK by PowerShell 5.1).
param(
    [string]$Mod = 'uig',
    [string]$GameDir = "",
    [int]$TimeoutSec = 180,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue
. "$PSScriptRoot\build-common.ps1"

$repo = Get-RepoRoot
$GameDir = Normalize-GameDir $GameDir (Get-DefaultGameDir -RepoRoot $repo)

if (-not $SkipBuild) {
    # Capture the build output: when the compile fails, build.ps1 REPORTS it and
    # returns normally (it does not throw). Without this guard the self-test would
    # happily launch the game with the PREVIOUS DLLs, and every measurement after
    # that is worthless. `error CS` is the compiler's own ASCII marker.
    $buildOut = (& (Join-Path $repo 'build.ps1') -Mod $Mod *>&1 | Out-String)
    Write-Host $buildOut
    if ($buildOut -match 'error CS') {
        throw "build failed (compiler error) - aborting before the self-test, otherwise the game would run stale DLLs"
    }
}

& (Join-Path $PSScriptRoot 'run-selftest.ps1') -GameDir $GameDir -TimeoutSec $TimeoutSec
