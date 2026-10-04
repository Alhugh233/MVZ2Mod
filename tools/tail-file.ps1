# Dump the tail of a (possibly locked) text file to another file.
# Why: the game keeps BepInEx\LogOutput.log open; a plain read can fail, and
# this workspace's terminal sometimes loses command output entirely - writing
# to a file and reading that file is the reliable path.
#
# Usage: powershell -File tools\tail-file.ps1 -Path <file> -Out <file> [-Lines 80] [-Match <regex>]
# ASCII only.
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Lines = 80,
    [string]$Match = ''
)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility

if (-not (Test-Path -LiteralPath $Path)) {
    [IO.File]::WriteAllText($Out, "(not found: " + $Path + ")")
    Write-Host "not found: $Path"
    exit 0
}

$text = ''
try {
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $sr = New-Object System.IO.StreamReader($fs)
        $text = $sr.ReadToEnd()
    } finally { $fs.Dispose() }
} catch {
    [IO.File]::WriteAllText($Out, "(read failed: " + $_.Exception.Message + ")")
    Write-Host ("read failed: " + $_.Exception.Message)
    exit 0
}

$all = $text -split "`r?`n"
if (-not [string]::IsNullOrWhiteSpace($Match)) {
    $all = @($all | Where-Object { $_ -match $Match })
}

$tail = @($all | Select-Object -Last $Lines)
[IO.File]::WriteAllText($Out, ($tail -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("wrote " + $tail.Count + " lines to " + $Out)
