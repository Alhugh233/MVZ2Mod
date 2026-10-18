# Map an absolute/native code offset inside GameAssembly.dll to the managed method
# that contains it, using Il2CppDumper's dump.cs (needs DumpMethodOffset=true).
#
#   .\_zz_rva.ps1 -Dump <dump.cs> -Rva 0x01818e5b
#
# ASCII only (PowerShell 5.1 reads .ps1 as ANSI).
param(
    [Parameter(Mandatory = $true)][string]$Dump,
    [Parameter(Mandatory = $true)][string]$Rva,
    [int]$Around = 4
)

$ErrorActionPreference = 'Stop'

$target = [Convert]::ToInt64($Rva, 16)
Write-Host ("dump   : " + $Dump)
Write-Host ("target : 0x" + $target.ToString('X'))

$rvas = New-Object System.Collections.Generic.List[long]
$sigs = New-Object System.Collections.Generic.List[string]
$classes = New-Object System.Collections.Generic.List[string]

$pendingRva = [long]-1
$curClass = '?'
$pendingClass = $false

foreach ($line in [IO.File]::ReadLines($Dump)) {
    if ($line.Length -eq 0) { continue }

    if ($line -match '^\s*//\s*RVA:\s*0x([0-9A-Fa-f]+)') {
        $pendingRva = [Convert]::ToInt64($matches[1], 16)
        continue
    }

    if ($line -match '^\s*//\s*TypeDefIndex:\s*(\d+)') {
        $pendingClass = $true
        continue
    }

    if ($line -match '^\s*//') { continue }

    if ($pendingClass) {
        $curClass = $line.Trim()
        $pendingClass = $false
        continue
    }

    if ($pendingRva -ge 0) {
        $rvas.Add($pendingRva)
        $sigs.Add($line.Trim())
        $classes.Add($curClass)
        $pendingRva = [long]-1
    }
}

$n = $rvas.Count
Write-Host ("methods with RVA: " + $n)

# find greatest index with Rva <= target.
# NOTE: dump.cs groups methods per-type, so the RVA column is NOT globally sorted --
# must sort for real (and permute the parallel arrays together).
$idx = New-Object int[] $n
for ($i = 0; $i -lt $n; $i++) { $idx[$i] = $i }
$rvaArr = $rvas.ToArray()
[Array]::Sort($rvaArr, $idx)

Write-Host ("min RVA = 0x" + $rvaArr[0].ToString('X') + "   max RVA = 0x" + $rvaArr[$n - 1].ToString('X'))

$lo = 0; $hi = $n - 1; $best = -1
while ($lo -le $hi) {
    $mid = [int](($lo + $hi) / 2)
    if ($rvaArr[$mid] -le $target) { $best = $mid; $lo = $mid + 1 } else { $hi = $mid - 1 }
}

if ($best -lt 0) { Write-Host 'no method below target'; exit 1 }

Write-Host ''
Write-Host '===== containing method (nearest RVA <= target) ====='
for ($k = $best; $k -ge [Math]::Max(0, $best - 1); $k--) {
    $orig = $idx[$k]
    $delta = $target - $rvaArr[$k]
    Write-Host ("RVA 0x{0}  (+0x{1:X} into method)  {2}" -f $rvaArr[$k].ToString('X'), $delta, $classes[$orig])
    Write-Host ("   " + $sigs[$orig])
}

Write-Host ''
Write-Host '===== next methods ====='
for ($k = $best + 1; $k -lt [Math]::Min($n, $best + 1 + $Around); $k++) {
    $orig = $idx[$k]
    Write-Host ("RVA 0x{0}  {1}" -f $rvaArr[$k].ToString('X'), $classes[$orig])
    Write-Host ("   " + $sigs[$orig])
}
