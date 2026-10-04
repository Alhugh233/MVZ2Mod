# Ensure a .ps1 (or any UTF-8 text file) has a UTF-8 BOM.
# Why: PowerShell 5.1 reads a BOM-less file as GBK, which mangles Chinese
# literals and silently breaks string comparisons / regex patterns.
#
# Usage: powershell -File tools\add-bom.ps1 <path> [<path>...]
# ASCII only.
param([Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)][string[]]$Paths)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility

$enc = New-Object System.Text.UTF8Encoding($true)
foreach ($p in $Paths) {
    if (-not (Test-Path -LiteralPath $p)) { throw ("not found: " + $p) }
    $bytes = [System.IO.File]::ReadAllBytes($p)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($p, $text, $enc)
    $state = if ($hasBom) { "already" } else { "added  " }
    Write-Host ($state + " BOM  " + $p)
}
