# Region-restricted diffs for the UiGallery dropdown probe.
#
# Why regions: the title screen animates (background art + icons), so a full-screen
# diff is dominated by animation and cannot tell us whether a widget actually drew.
# We therefore also measure a CONTROL region (top-left, where the animation lives)
# so the animation's contribution is known and can be subtracted mentally.
#
# Image coords: origin top-left, y downwards.  Screen coords: y upwards.
# Measured popup rect (screen): x496 y277 254x114  ->  image y = 755-1-391 .. 755-1-277
#
# ASCII only.
param(
    [string]$Dir = 'E:\Game\PVZ\MVZ2\MVZ2 0.7.0 test-10\BepInEx'
)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility

$base = Join-Path $Dir '_uig_dd0_closed.png'
$popup = Join-Path $Dir '_uig_dd1_tmpdd.png'
$control = Join-Path $Dir '_uig_dd2_closed_after_tmpdd.png'
$diff = Join-Path $PSScriptRoot 'img-diff.ps1'

$regions = @(
    @{ Name = 'popup-rect  '; Rect = @(496, 363, 254, 114) },
    @{ Name = 'panel-rect  '; Rect = @(254, 9, 508, 737) },
    @{ Name = 'control-tl  '; Rect = @(0, 0, 240, 200) }
)

# Expected result for the TMP dropdown: the "open" diff is the SAME ORDER OF MAGNITUDE
# as the "closed" control diff -> the popup really draws nothing.
foreach ($p in @(@('tmpdd(open)', $popup), @('control(closed)', $control))) {
    Write-Host ('===== dd0 vs ' + $p[0] + ' =====')
    foreach ($r in $regions) {
        $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $diff -A $base -B $p[1] `
            -RegionX $r.Rect[0] -RegionY $r.Rect[1] -RegionW $r.Rect[2] -RegionH $r.Rect[3] 2>&1
        $line = ($out | Where-Object { $_ -like 'DIFF_PIXELS*' -or $_ -like 'SCANNED*' }) -join '   '
        Write-Host ('  ' + $r.Name + '  ' + $line)
    }
}
