# 回放事件统计：按 kind × ia 分组，并检查「按下/松开」是否配对。
#
#   tools\replay-stats.ps1 -Name mvz2_halloween_1_20261004_161933
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Dir
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

$names = @{ 0='Hover'; 1='Enter'; 2='Stay'; 3='Exit'; 4='Down'; 5='Hold'; 6='Up'; 7='Click';
            8='BeginDrag'; 9='Drag'; 10='EndDrag'; 11='Drop'; 12='Streak'; 13='Release'; 100='Key'; -1='(none)' }

$lines = [System.IO.File]::ReadAllLines($path)
$table = @{}
$posMissing = @{}
$total = 0
$fpCount = 0

for ($i = 1; $i -lt $lines.Length; $i++) {
    $l = $lines[$i]
    if ([string]::IsNullOrWhiteSpace($l)) { continue }
    $o = $l | ConvertFrom-Json
    $k = [string]$o.k
    if ($k -eq 'fp') { $fpCount++; continue }
    if ($k -eq 'keyframe') { continue }
    $total++
    $ia = -1
    if ($o.PSObject.Properties.Name -contains 'ia') { $ia = [int]$o.ia }
    $key = $k + ' / ' + $ia + ' (' + $names[$ia] + ')'
    if ($table.ContainsKey($key)) { $table[$key]++ } else { $table[$key] = 1 }
    if ($o.PSObject.Properties.Name -contains 'sx') {
        if ([double]$o.sx -eq 0 -and [double]$o.sy -eq 0 -and [double]$o.wx -eq 0) {
            if ($posMissing.ContainsKey($k)) { $posMissing[$k]++ } else { $posMissing[$k] = 1 }
        }
    }
}

Write-Host ("file   : " + $path)
Write-Host ("events : " + $total + "   fp: " + $fpCount)
Write-Host ""
Write-Host "--- kind / ia ---"
foreach ($k in ($table.Keys | Sort-Object)) { Write-Host ("  {0,-22} {1}" -f $k, $table[$k]) }
Write-Host ""
Write-Host "--- 位置全 0 的事件数（按 kind）---"
foreach ($k in ($posMissing.Keys | Sort-Object)) { Write-Host ("  {0,-12} {1}" -f $k, $posMissing[$k]) }
