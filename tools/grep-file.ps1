# Grep a (possibly locked) text file with context, writing the result to -Out.
# Why: the game keeps BepInEx\LogOutput.log open for writing; a plain read can
# fail, and this workspace's terminal sometimes loses output entirely. Also
# passing `$`-containing one-liners through the outer shell mangles them -
# a script file avoids that whole class of problem.
#
# Usage:
#   powershell -File tools\grep-file.ps1 -Path <log> -Out <file> -Pattern "Exception|NullReference" -Before 2 -After 12
# ASCII only.
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string]$Pattern,
    [int]$Before = 2,
    [int]$After = 12,
    [int]$MaxHits = 20
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

$lines = $text -split "`r?`n"
# NOTE: PowerShell variable names are case-insensitive, so the local buffer must
# NOT be called $out/$result-like names that collide with the -Out parameter.
$buf = New-Object System.Collections.Generic.List[string]
$buf.Add("source : " + $Path)
$buf.Add("pattern: " + $Pattern)
$hits = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -notmatch $Pattern) { continue }
    $hits++
    if ($hits -gt $MaxHits) {
        $buf.Add("... (more than " + $MaxHits + " hits, truncated)")
        break
    }
    $buf.Add("========== hit " + $hits + " (line " + ($i + 1) + ") ==========")
    $from = [Math]::Max(0, $i - $Before)
    $to = [Math]::Min($lines.Count - 1, $i + $After)
    for ($j = $from; $j -le $to; $j++) {
        $buf.Add(("{0,6}: {1}" -f ($j + 1), $lines[$j]))
    }
}

$buf.Add("")
$buf.Add("hits   : " + $hits)
[IO.File]::WriteAllLines($Out, $buf, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("hits=" + $hits + " -> " + $Out)
