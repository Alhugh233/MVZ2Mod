# 按条件抽取回放里的事件行（带行号），用于精确定位「某一类动作附近发生了什么」。
#
#   tools\replay-events.ps1 -Name <回放名> -Kind tool
#   tools\replay-events.ps1 -Name <回放名> -Kind entity -From 300 -To 400
#   tools\replay-events.ps1 -Name <回放名> -Text pickaxe
#
# 默认输出到控制台；给 -Out 就写文件（避免长输出刷屏）。

param(
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Dir,
    [string]$Kind,
    [string]$Text,
    [int]$From = -1,
    [int]$To = -1,
    [int]$Context = 0,
    [int]$Max = 400,
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
    if (-not $g) { throw 'GameDir 未找到，请用 -Dir 指定回放目录。' }
}

# 回放现在放在存档目录里（按用户分），旧版在 <游戏根>\BepInEx\replays。
. "$PSScriptRoot\build-common.ps1"
$path = Find-ReplayFile -GameDir $g -Name $Name
if (-not $path -and $Dir) {
    foreach ($c in @($Name + '.mrep.json', $Name + '.mrep', $Name)) {
        $p = Join-Path $Dir $c
        if (Test-Path -LiteralPath $p) { $path = $p; break }
    }
}
if (-not $path) { throw ("找不到 " + $Name) }

$lines = [System.IO.File]::ReadAllLines($path)
$buf = New-Object System.Collections.Generic.List[string]
$buf.Add("file : $path")

$hits = 0
for ($i = 1; $i -lt $lines.Length; $i++) {
    $l = $lines[$i]
    if ([string]::IsNullOrWhiteSpace($l)) { continue }

    $ok = $true
    if ($Kind) { $ok = $ok -and ($l -like ('*"k":"' + $Kind + '"*')) }
    if ($Text) { $ok = $ok -and ($l -like ('*' + $Text + '*')) }
    if ($ok -and $From -ge 0) {
        $m = [regex]::Match($l, '"t":(-?\d+)')
        if (-not $m.Success) { $ok = $false }
        elseif ([int]$m.Groups[1].Value -lt $From) { $ok = $false }
    }
    if ($ok -and $To -ge 0) {
        $m = [regex]::Match($l, '"t":(-?\d+)')
        if ($m.Success -and [int]$m.Groups[1].Value -gt $To) { $ok = $false }
    }
    if (-not $ok) { continue }

    if ($Context -gt 0) {
        for ($j = [Math]::Max(1, $i - $Context); $j -le [Math]::Min($lines.Length - 1, $i + $Context); $j++) {
            # PS 5.1 不支持把 if 当表达式用，分开写。
            $mark = '  '
            if ($j -eq $i) { $mark = '>>' }
            $buf.Add(('L{0,-5} {1} {2}' -f $j, $mark, $lines[$j]))
        }
        $buf.Add('')
    }
    else {
        $buf.Add(('L{0,-5} {1}' -f $i, $l))
    }
    if (++$hits -ge $Max) { $buf.Add("... 已达上限 $Max"); break }
}

$buf.Add("hits : $hits")
$text = $buf -join "`r`n"
if ($Out) {
    [System.IO.File]::WriteAllText($Out, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "-> $Out  (hits=$hits)"
}
else { Write-Host $text }
