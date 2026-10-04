# 检查一个 .mrep / .mrep.json 回放文件：打印头部、关键帧大小、事件种类直方图与事件列表。
#
# 用法：
#   tools\inspect-replay.ps1 -Name mvz2_halloween_1_20261004_150332
#   tools\inspect-replay.ps1 -Name mvz2_halloween_1_20261004_150332 -Events 80 -Out _r.json.txt
#
# 说明：优先读 .mrep.json（纯文本，gzip 的 .mrep 需要解压），两者内容一致。

param(
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Dir,
    [int]$Events = 40,
    [int]$Fingerprints = 8,
    [string]$Out
)

$ErrorActionPreference = 'Stop'

function Get-GameDir {
    $root = Split-Path -Parent $PSScriptRoot
    $props = Join-Path $root 'Directory.Build.local.props'
    if (Test-Path $props) {
        $m = [regex]::Match((Get-Content $props -Raw), '<GameDir>(.*?)</GameDir>')
        if ($m.Success) { return $m.Groups[1].Value.Trim() }
    }
    return $null
}

if (-not $Dir) {
    $g = Get-GameDir
    if (-not $g) { throw '未找到 GameDir（Directory.Build.local.props），请用 -Dir 指定回放目录。' }
}

# 回放现在放在存档目录里（按用户分），旧版在 <游戏根>\BepInEx\replays。
. "$PSScriptRoot\build-common.ps1"
$path = Find-ReplayFile -GameDir $g -Name $Name
if (-not $path -and $Dir) {
    foreach ($c in @($Name, $Name + '.mrep.json', $Name + '.mrep')) {
        $p = Join-Path $Dir $c
        if (Test-Path -LiteralPath $p) { $path = $p; break }
    }
}
if (-not $path) { throw "找不到回放文件：$Name" }

$lines = [System.IO.File]::ReadAllLines($path)   # 回放文件通常没被占用；被占用时调用方应改用副本

$buf = New-Object System.Collections.Generic.List[string]
$buf.Add("file  : $path")
$buf.Add("lines : $($lines.Length)")
$buf.Add("")

# ---- 头部 ----
$head = $lines[0]
$buf.Add('--- header ---')
try {
    $h = $head | ConvertFrom-Json
    foreach ($p in $h.PSObject.Properties) { $buf.Add(('  {0,-16} {1}' -f $p.Name, $p.Value)) }
}
catch { $buf.Add("  (无法解析) $head") }
$buf.Add('')

# ---- 关键帧 ----
$i = 1
$kinds = @{}
$evts = New-Object System.Collections.Generic.List[string]
$fps = New-Object System.Collections.Generic.List[string]
$badLines = 0
$keyframeTick = $null
$keyframeLen = $null

for (; $i -lt $lines.Length; $i++) {
    $ln = $lines[$i]
    if ([string]::IsNullOrWhiteSpace($ln)) { continue }
    try { $o = $ln | ConvertFrom-Json } catch { $badLines++; continue }
    $k = [string]$o.k
    if ($kinds.ContainsKey($k)) { $kinds[$k]++ } else { $kinds[$k] = 1 }
    if ($k -eq 'keyframe') {
        $keyframeTick = $o.tick
        $keyframeLen = ([string]$o.state).Length
    }
    elseif ($k -eq 'fp') {
        if ($fps.Count -lt $Fingerprints) { $fps.Add($ln) }
    }
    else {
        if ($evts.Count -lt $Events) { $evts.Add($ln) }
    }
}

$buf.Add('--- summary ---')
$buf.Add(('  keyframeTick {0}   stateChars {1}' -f $keyframeTick, $keyframeLen))
$buf.Add(('  badLines     {0}' -f $badLines))
foreach ($k in ($kinds.Keys | Sort-Object)) { $buf.Add(('  kind {0,-10} {1}' -f $k, $kinds[$k])) }
$buf.Add('')

$buf.Add("--- events (前 $Events 条，keyframe/fp 除外) ---")
foreach ($e in $evts) { $buf.Add('  ' + $e) }
$buf.Add('')
$buf.Add("--- fingerprints (前 $Fingerprints 条) ---")
foreach ($f in $fps) { $buf.Add('  ' + $f) }

$text = $buf -join "`r`n"
if ($Out) {
    [System.IO.File]::WriteAllText($Out, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "-> $Out"
}
else { Write-Host $text }
