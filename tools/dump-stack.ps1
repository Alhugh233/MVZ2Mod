# Analyze a Windows minidump of an IL2CPP Unity game: scan the faulting thread's
# stack for return addresses that point into GameAssembly.dll, then map each one
# to the managed method that contains it (using Il2CppDumper's dump.cs, which
# carries `// RVA: 0x…` when DumpMethodOffset = true).
#
# Why: an IL2CPP access violation is invisible to BepInEx (ErrorLog.log stays 0
# bytes) and the WER event only gives one module offset. The stack scan recovers
# the *call chain* without any debugger, which is usually enough to name the
# culprit.
#
#   .\tools\dump-stack.ps1 -Dump <file.dmp>                   # scan failure thread
#   .\tools\dump-stack.ps1 -Dump <file.dmp> -Depth 64 -All
#
# ASCII only (PowerShell 5.1 reads .ps1 as ANSI).
param(
    [Parameter(Mandatory = $true)][string]$Dump,
    [string]$DumpCs = 'E:\Game\PVZ\MVZ2\_dump_official\dump.cs',
    [string]$GameAssembly = 'E:\Game\PVZ\MVZ2\MVZ2 0.7.0\GameAssembly.dll',
    [string]$IndexCache = '',
    [int]$Depth = 48,
    [switch]$All
)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility

if ([string]::IsNullOrWhiteSpace($IndexCache)) {
    $IndexCache = Join-Path $PSScriptRoot 'rva-index.tsv'
}

# ---------------------------------------------------------------------
# [1/3] RVA -> method index (cached; dump.cs is 32 MB, parsing it is slow)
# ---------------------------------------------------------------------
function Get-RvaIndex([string]$dumpCs, [string]$cache) {
    if ((Test-Path -LiteralPath $cache)) {
        $fi = Get-Item -LiteralPath $cache
        if ($fi.LastWriteTime -ge (Get-Item -LiteralPath $dumpCs).LastWriteTime) {
            Write-Host ("index cache: " + $cache)
            $idx = New-Object System.Collections.Generic.List[object]
            foreach ($line in [IO.File]::ReadLines($cache)) {
                $p = $line.Split("`t")
                if ($p.Length -ge 3) {
                    $idx.Add(@{ R = [Convert]::ToInt64($p[0], 16); C = $p[1]; S = $p[2] })
                }
            }
            return $idx
        }
    }

    Write-Host ("parsing " + $dumpCs + " (first run only, ~30s) ...")
    $idx = New-Object System.Collections.Generic.List[object]
    $pendingRva = [long]-1
    $curClass = ""
    $pendingClass = $false
    foreach ($raw in [IO.File]::ReadLines($dumpCs)) {
        $line = $raw.TrimStart()
        if ($line.Length -eq 0) { continue }
        if ($line[0] -eq '/') {
            if ($line.StartsWith('// RVA: 0x')) {
                # "// RVA: 0x1818DF0 Offset: 0x... VA: 0x... Slot: 15"
                $hex = $line.Substring(10)          # skip "// RVA: 0x"
                $end = $hex.IndexOf(' ')
                if ($end -lt 0) { $end = $hex.Length }
                $pendingRva = [Convert]::ToInt64($hex.Substring(0, $end), 16)
            }
            elseif ($line.StartsWith('// TypeDefIndex:')) { $pendingClass = $true }
            continue
        }        if ($line.Contains('// TypeDefIndex:')) {
            # Class declaration: the index sits at the END of the line, e.g.
            #   public class Conveyor : BlueprintSet // TypeDefIndex: 5592
            $s = $line.Substring(0, $line.IndexOf('// TypeDefIndex'))
            $ci = $s.IndexOf(' class ')
            if ($ci -ge 0) { $s = $s.Substring($ci + 7) }
            $s = $s.Trim()
            $sp = $s.IndexOfAny(@(' ', ':', '<'))
            if ($sp -gt 0) { $s = $s.Substring(0, $sp) }
            if ($s.Length -gt 0) { $curClass = $s }
            $pendingRva = [long]-1
            continue
        }
        if ($pendingRva -ge 0) {
            $sig = $line.Trim()
            if ($sig.Length -gt 120) { $sig = $sig.Substring(0, 120) }
            $idx.Add(@{ R = $pendingRva; C = $curClass; S = $sig })
            $pendingRva = [long]-1
        }
    }

    # Sorted by RVA, then written out in that order so the cache reload is O(1).
    $arr = $idx.ToArray()
    [Array]::Sort($arr, [Comparison[object]]{ param($a, $b) $a.R.CompareTo($b.R) })
    $sb = New-Object System.Text.StringBuilder
    foreach ($e in $arr) { [void]$sb.Append(('{0:X}' -f $e.R)).Append("`t").Append($e.C).Append("`t").Append($e.S).Append("`n") }
    [IO.File]::WriteAllText($cache, $sb.ToString(), (New-Object Text.UTF8Encoding($false)))
    Write-Host ("index built: " + $arr.Count + " methods -> " + $cache)
    # Return in the same shape/sort order as the cache path.
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($e in $arr) { $out.Add($e) }
    return $out
}

# ---------------------------------------------------------------------
# [2/3] minidump parsing
# ---------------------------------------------------------------------
function Read-MiniDump([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    if ([BitConverter]::ToUInt32($b, 0) -ne 0x504D444D) { throw "not a minidump: $path" }
    $nStreams = [BitConverter]::ToUInt32($b, 8)
    $dirRva = [BitConverter]::ToUInt32($b, 12)

    $streams = @{}
    for ($i = 0; $i -lt $nStreams; $i++) {
        $o = $dirRva + $i * 12
        # NOTE: keep the key as [int]. A [uint32] key is NOT found by
        # ContainsKey([int]3) -- Int32.Equals(UInt32) is false.
        $type = [int][BitConverter]::ToUInt32($b, $o)
        $size = [BitConverter]::ToUInt32($b, $o + 4)
        $rva = [BitConverter]::ToUInt32($b, $o + 8)
        $streams[$type] = @{ Size = $size; Rva = $rva }
    }

    # modules
    $modules = New-Object System.Collections.Generic.List[object]
    if ($streams.ContainsKey(4)) {
        $r = $streams[4].Rva
        $n = [BitConverter]::ToUInt32($b, $r)
        for ($i = 0; $i -lt $n; $i++) {
            $o = $r + 4 + $i * 108
            $base = [BitConverter]::ToUInt64($b, $o)
            $imgSz = [BitConverter]::ToUInt32($b, $o + 8)
            $nameRva = [BitConverter]::ToUInt32($b, $o + 20)
            $len = [BitConverter]::ToUInt32($b, $nameRva)
            $name = [Text.Encoding]::Unicode.GetString($b, $nameRva + 4, $len)
            $modules.Add(@{ Base = [long]$base; Size = [int]$imgSz; Name = (Split-Path $name -Leaf) })
        }
    }

    # threads
    $threads = New-Object System.Collections.Generic.List[object]
    if ($streams.ContainsKey(3)) {
        $r = $streams[3].Rva
        $n = [BitConverter]::ToUInt32($b, $r)
        for ($i = 0; $i -lt $n; $i++) {
            $o = $r + 4 + $i * 48
            $tid = [BitConverter]::ToUInt32($b, $o)
            $stkStart = [BitConverter]::ToUInt64($b, $o + 16)
            $stkSize = [BitConverter]::ToUInt32($b, $o + 24)
            $stkRva = [BitConverter]::ToUInt32($b, $o + 28)
            $ctxSize = [BitConverter]::ToUInt32($b, $o + 32)
            $ctxRva = [BitConverter]::ToUInt32($b, $o + 36)
            $eip = 0; $esp = 0; $ebp = 0
            if ($ctxSize -ge 200 -and $ctxRva -gt 0) {
                # x86 CONTEXT
                $ebp = [BitConverter]::ToUInt32($b, $ctxRva + 180)
                $eip = [BitConverter]::ToUInt32($b, $ctxRva + 184)
                $esp = [BitConverter]::ToUInt32($b, $ctxRva + 196)
            }
            $threads.Add(@{
                    Tid = $tid; StkStart = [long]$stkStart; StkSize = $stkSize; StkRva = $stkRva
                    Eip = $eip; Esp = $esp; Ebp = $ebp
                })
        }
    }

    # memory ranges (type 5) -- used to read stack bytes by address
    $memRanges = New-Object System.Collections.Generic.List[object]
    if ($streams.ContainsKey(5)) {
        $r = $streams[5].Rva
        $n = [BitConverter]::ToUInt32($b, $r)
        for ($i = 0; $i -lt $n; $i++) {
            $o = $r + 4 + $i * 16
            $memRanges.Add(@{
                    Start = [long][BitConverter]::ToUInt64($b, $o)
                    Size  = [int][BitConverter]::ToUInt32($b, $o + 8)
                    Rva   = [int][BitConverter]::ToUInt32($b, $o + 12)
                })
        }
    }

    return @{ Bytes = $b; Modules = $modules; Threads = $threads; Streams = $streams; Mem = $memRanges }
}

# ---------------------------------------------------------------------
# [3/3] stack scan
# ---------------------------------------------------------------------
$index = Get-RvaIndex $DumpCs $IndexCache
$rvas = New-Object long[] $index.Count
for ($i = 0; $i -lt $index.Count; $i++) { $rvas[$i] = $index[$i].R }

function Find-Method([long]$rva) {
    $lo = 0; $hi = $rvas.Length - 1; $best = -1
    while ($lo -le $hi) {
        $mid = [int](($lo + $hi) / 2)
        if ($rvas[$mid] -le $rva) { $best = $mid; $lo = $mid + 1 } else { $hi = $mid - 1 }
    }
    return $best
}

function Format-Method([int]$i, [long]$rva) {
    $e = $index[$i]
    return ('+0x' + ('{0:X}' -f ($rva - $rvas[$i])) + '  ' + $e.C + '.' + $e.S)
}

# A stack slot counts as a return address only if it maps to a method in dump.cs
# with a small offset. IL2CPP puts the generated code in the `il2cpp` section (that
# is where dump.cs RVAs live), NOT in .text, so .text cannot be used as the filter.
function Map-RetAddr([long]$v) {
    $rva = $v - $gaBase
    if ($rva -lt $script:minRva) { return $null }
    $i = Find-Method $rva
    if ($i -lt 0) { return $null }
    if (($rva - $rvas[$i]) -gt 0x4000) { return $null }
    return (Format-Method $i $rva)
}

$d = Read-MiniDump $Dump
$bytes = $d.Bytes
$ga = $d.Modules | Where-Object { $_.Name -ieq 'GameAssembly.dll' } | Select-Object -First 1

Write-Host ("dump     : " + $Dump)
Write-Host ("threads  : " + $d.Threads.Count + "   modules: " + $d.Modules.Count)
if (-not $ga) { throw "GameAssembly.dll not found in dump" }
Write-Host ("GameAssembly base=0x{0:X} size=0x{1:X}" -f $ga.Base, $ga.Size)

# Cache the range as plain [long]s: comparing a [uint32] against a hashtable
# member inside a scriptblock trips PowerShell's binder.
$gaBase = [long]$ga.Base
$gaEnd = [long]($ga.Base + $ga.Size)

# ---- restrict the scan to the executable section ----
# GameAssembly.dll also holds data (vtables, string literals, metadata). Any random
# pointer into the module range would otherwise look like a "return address" and the
# output drowns in noise. Read the PE section table and keep only .text.
$peSections = @()
if (Test-Path -LiteralPath $GameAssembly) {
    $pe = [IO.File]::ReadAllBytes($GameAssembly)
    $peOff = [BitConverter]::ToInt32($pe, 0x3C)
    $nSec = [BitConverter]::ToUInt16($pe, $peOff + 6)
    $optSize = [BitConverter]::ToUInt16($pe, $peOff + 20)
    $secOff = $peOff + 24 + $optSize
    for ($i = 0; $i -lt $nSec; $i++) {
        $o = $secOff + $i * 40
        $name = [Text.Encoding]::ASCII.GetString($pe, $o, 8).TrimEnd([char]0)
        $vsz = [BitConverter]::ToUInt32($pe, $o + 8)
        $va = [BitConverter]::ToUInt32($pe, $o + 12)
        $peSections += @{ Name = $name; Va = [long]$va; Size = [long]$vsz }
    }
    Write-Host ("sections: " + (($peSections | ForEach-Object { $_.Name + '@0x' + ('{0:X}' -f $_.Va) + '+0x' + ('{0:X}' -f $_.Size) }) -join '  '))
}
$text = $peSections | Where-Object { $_.Name -eq '.text' } | Select-Object -First 1
if ($text) {
    $textLo = $gaBase + $text.Va
    $textHi = $gaBase + $text.Va + $text.Size
}
else {
    $textLo = $gaBase
    $textHi = $gaEnd
}
Write-Host ("code range: 0x{0:X8} .. 0x{1:X8}" -f $textLo, $textHi)

# dump.cs RVA span (the il2cpp generated-code section).
$minRva = [long]$rvas[0]
$maxRva = [long]$rvas[$rvas.Length - 1]
Write-Host ("dump RVA span: 0x{0:X} .. 0x{1:X}" -f $minRva, $maxRva)

function Test-InGa([long]$v) {
    return ($v -ge $script:gaBase -and $v -lt $script:gaEnd)
}
function Test-InCode([long]$v) {
    return ($v -ge $script:textLo -and $v -lt $script:textHi)
}

# ---- faulting thread, from the exception stream (more reliable than scanning Eip)
$faultTid = 0
$faultCtx = $null
if ($d.Streams.ContainsKey(6)) {
    $er = $d.Streams[6].Rva
    $faultTid = [BitConverter]::ToUInt32($bytes, $er)          # ThreadId
    $excAddr = [BitConverter]::ToUInt64($bytes, $er + 8 + 16)  # ExceptionAddress
    $ctxSize = [BitConverter]::ToUInt32($bytes, $er + 160)
    $ctxRva = [BitConverter]::ToUInt32($bytes, $er + 164)
    Write-Host ('exception: tid=' + $faultTid + ' addr=0x' + ('{0:X8}' -f $excAddr) +
        ' ctxRva=0x' + ('{0:X}' -f $ctxRva) + ' ctxSize=' + $ctxSize + '  in-GA=' + (Test-InGa ([long]$excAddr)))
    if ($ctxSize -ge 200 -and $ctxRva -gt 0) {
        $faultCtx = @{
            Tid    = $faultTid
            Ebp    = [BitConverter]::ToUInt32($bytes, $ctxRva + 180)
            Eip    = [BitConverter]::ToUInt32($bytes, $ctxRva + 184)
            Esp    = [BitConverter]::ToUInt32($bytes, $ctxRva + 196)
        }
    }
}

# stack range for a thread id (the context itself has no stack base/size)
function Get-ThreadStack([int]$tid) {
    foreach ($th in $d.Threads) { if ($th.Tid -eq $tid) { return $th } }
    return $null
}

$cands = New-Object System.Collections.Generic.List[object]
if ($faultCtx -ne $null) {
    $ts = Get-ThreadStack $faultTid
    if ($ts -ne $null) {
        [void]$cands.Add(@{ Tid = $faultTid; Eip = $faultCtx.Eip; Esp = $faultCtx.Esp; Ebp = $faultCtx.Ebp; StkRva = $ts.StkRva; StkSize = $ts.StkSize })
    }
    else {
        [void]$cands.Add($faultCtx)
    }
}
foreach ($th in $d.Threads) {
    if (Test-InGa ([long]$th.Eip) -and $th.Tid -ne $faultTid) { [void]$cands.Add($th) }
}
Write-Host ('threads with context: ' + (@($d.Threads | Where-Object { $_.Eip -ne 0 }).Count) +
    ' / ' + $d.Threads.Count + '   candidates: ' + $cands.Count)

$order = $cands
if ($All) {
    $order = New-Object System.Collections.Generic.List[object]
    foreach ($th in $cands) { [void]$order.Add($th) }
    foreach ($th in $d.Threads) { if (-not (Test-InGa ([long]$th.Eip))) { [void]$order.Add($th) } }
}

$shown = 0
foreach ($th in $order) {
    $shown++
    if ($shown -gt 3) { break }

    $hit = 'no'
    if (Test-InGa ([long]$th.Eip)) { $hit = 'yes' }
    Write-Host ''
    Write-Host ('===== thread ' + $th.Tid + '  eip=0x' + ('{0:X8}' -f $th.Eip) +
        ' esp=0x' + ('{0:X8}' -f $th.Esp) + '  eip-in-GameAssembly=' + $hit)

    $scanned = 0
    $memRva = -1
    $memBase = 0
    foreach ($mr in $d.Mem) {
        if ([long]$th.Esp -ge $mr.Start -and [long]$th.Esp -lt ($mr.Start + $mr.Size)) {
            $memBase = [long]$mr.Start
            $memRva = $mr.Rva + ([long]$th.Esp - $memBase)
            $avail = $mr.Size - ([long]$th.Esp - $memBase)
            break
        }
    }
    if ($memRva -lt 0) {
        Write-Host ('   (esp 0x' + ('{0:X8}' -f $th.Esp) + ' is not covered by any dump memory range)')
        continue
    }
    Write-Host ('   stack memory: base=0x' + ('{0:X8}' -f $memBase) + ' avail=' + $avail)

    $off = 0
    while ($off -lt $avail -and $scanned -lt $Depth) {
        $v = [long][BitConverter]::ToUInt32($bytes, $memRva + $off)
        $txt = Map-RetAddr $v
        if ($txt -ne $null) {
            $scanned++
            $mark = ''
            if ($off -eq 0) { $mark = '  <-- ESP' }
            Write-Host ('  [' + $scanned + '] +0x' + ('{0:X4}' -f $off) + '  0x' +
                ('{0:X8}' -f $v) + '  ' + $txt + $mark)
        }
        $off += 4
    }
    if ($scanned -eq 0) { Write-Host '   (no GameAssembly return address on this stack)' }
}
