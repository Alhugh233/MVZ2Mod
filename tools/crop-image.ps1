# Crop a region out of a PNG and scale it up, so a screenshot can be inspected
# at the pixel level (the repository rule: "node exists + size is right" does NOT
# mean "it is visible"; visual verdicts need pixels).
#
# ASCII only (a .ps1 with Chinese and no BOM is read as GBK by PowerShell 5.1).
#
#   powershell -File crop-image.ps1 -Path <png> -X 530 -Y 490 -W 380 -H 230 -Scale 3 -Out <png>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [Parameter(Mandatory = $true)][int]$W,
    [Parameter(Mandatory = $true)][int]$H,
    [int]$Scale = 3,
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $Path)) { throw "png not found: $Path" }
if ([string]::IsNullOrWhiteSpace($Out)) { $Out = [System.IO.Path]::ChangeExtension($Path, '.crop.png') }

$src = New-Object System.Drawing.Bitmap $Path
try {
    $rect = New-Object System.Drawing.Rectangle $X, $Y, $W, $H
    $crop = $src.Clone($rect, $src.PixelFormat)
    try {
        $tw = $W * $Scale
        $th = $H * $Scale
        $dst = New-Object System.Drawing.Bitmap $tw, $th
        try {
            $g = [System.Drawing.Graphics]::FromImage($dst)
            try {
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
                $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
                $g.DrawImage($crop, 0, 0, $tw, $th)
            }
            finally { $g.Dispose() }
            $dst.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $dst.Dispose() }
    }
    finally { $crop.Dispose() }
}
finally { $src.Dispose() }

Write-Host ("saved {0}  ({1}x{2} -> {3}x{4}, scale {5})" -f $Out, $W, $H, ($W * $Scale), ($H * $Scale), $Scale)
