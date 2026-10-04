# =====================================================================
# 转储 Unity prefab 的 GameObject 层级 + 每个节点上的组件种类。
#
# 为什么不解析 .meta 的脚本 GUID：本 Unity 工程里没有 .meta 文件，无法把
# MonoBehaviour 的 guid 映射回 C# 类型名。于是改为**按序列化字段指纹**识别
# Unity 内置 UI 组件（Toggle / Button / Dropdown / Image / ScrollRect /
# GridLayoutGroup / LayoutElement / ContentSizeFitter / TMP_Text …）。
#
# 用法：
#   powershell -File dump-prefab-hierarchy.ps1 -Path <prefab> [-MaxDepth 12] [-OnlyDepth N]
# =====================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$MaxDepth = 12,
    [int]$OnlyDepth = -1
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) { throw "prefab not found: $Path" }

# 只用显式 Regex，不依赖 $Matches（避免作用域污染导致的诡异空索引）
function M1([string]$text, [string]$pattern) {
    $m = [regex]::Match($text, $pattern)
    if ($m.Success -and $m.Groups.Count -gt 1) { return $m.Groups[1].Value }
    return ''
}
function Has([string]$text, [string]$pattern) {
    return [regex]::IsMatch($text, $pattern)
}

$text = [System.IO.File]::ReadAllText($Path)

$blocks = New-Object System.Collections.Generic.List[object]
foreach ($b in [regex]::Split($text, '(?m)^--- !u!')) {
    if ([string]::IsNullOrWhiteSpace($b)) { continue }
    $nl = $b.IndexOf("`n")
    if ($nl -lt 0) { continue }
    $head = $b.Substring(0, $nl).Trim()
    $hm = [regex]::Match($head, '^(\d+)\s+&(-?\d+)')
    if (-not $hm.Success) { continue }
    $blocks.Add([pscustomobject]@{
        Class  = [int]$hm.Groups[1].Value
        FileID = $hm.Groups[2].Value
        Body   = $b.Substring($nl + 1)
    })
}

$goName     = @{}
$goComps    = @{}
$trOf       = @{}
$trFather   = @{}
$trChildren = @{}
$compKind   = @{}

foreach ($blk in $blocks) {
    $id = $blk.FileID
    $body = $blk.Body

    if ($blk.Class -eq 1) {
        $goName[$id] = (M1 $body '(?m)^\s*m_Name:\s*(.*)$')
        $list = New-Object System.Collections.Generic.List[string]
        foreach ($m in [regex]::Matches($body, '(?m)^\s*-\s*component:\s*\{fileID:\s*(-?\d+)\}')) {
            $list.Add($m.Groups[1].Value)
        }
        $goComps[$id] = $list
        continue
    }

    if ($blk.Class -eq 4 -or $blk.Class -eq 224) {
        $gid = M1 $body '(?m)^\s*m_GameObject:\s*\{fileID:\s*(-?\d+)\}'
        if ([string]::IsNullOrEmpty($gid)) { continue }
        $trOf[$gid] = $id
        $father = M1 $body '(?m)^\s*m_Father:\s*\{fileID:\s*(-?\d+)\}'
        if ([string]::IsNullOrEmpty($father)) { $father = '0' }
        $trFather[$id] = $father

        $kids = New-Object System.Collections.Generic.List[string]
        $inChildren = $false
        foreach ($line in ($body -split "`n")) {
            if (Has $line '^\s*m_Children:') { $inChildren = $true; continue }
            if ($inChildren) {
                $cm = [regex]::Match($line, '^\s*-\s*\{fileID:\s*(-?\d+)\}')
                if ($cm.Success) { $kids.Add($cm.Groups[1].Value) }
                elseif (Has $line '^\s*m_') { $inChildren = $false }
            }
        }
        $trChildren[$id] = $kids
        if ($blk.Class -eq 224) { $compKind[$id] = 'RectTransform' } else { $compKind[$id] = 'Transform' }
        continue
    }

    $kind = $null
    switch ($blk.Class) {
        114 {
            if     (Has $body '(?m)^\s*m_IsOn:')                     { $kind = 'Toggle' }
            elseif (Has $body '(?m)^\s*m_Options:\s*$')              { $kind = 'Dropdown' }
            elseif (Has $body '(?m)^\s*m_OnClick:')                  { $kind = 'Button' }
            elseif (Has $body '(?m)^\s*m_TextComponent:' -and (Has $body '(?m)^\s*m_CharacterLimit:')) { $kind = 'InputField' }
            elseif (Has $body '(?m)^\s*m_Viewport:\s*\{fileID:')     { $kind = 'ScrollRect' }
            elseif (Has $body '(?m)^\s*m_CellSize:')                 { $kind = 'GridLayoutGroup' }
            elseif (Has $body '(?m)^\s*m_Spacing:' -and (Has $body '(?m)^\s*m_ChildAlignment:')) { $kind = 'LayoutGroup' }
            elseif (Has $body '(?m)^\s*m_PreferredHeight:')          { $kind = 'LayoutElement' }
            elseif (Has $body '(?m)^\s*m_HorizontalFit:')            { $kind = 'ContentSizeFitter' }
            elseif (Has $body '(?m)^\s*m_Sprite:')                   { $kind = 'Image' }
            elseif (Has $body '(?m)^\s*m_fontAsset:')                { $kind = 'TMP_Text' }
            else                                                     { $kind = 'Mono_' + (M1 $body '(?m)^\s*m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{8})') }
        }
        222 { $kind = 'CanvasRenderer' }
        223 { $kind = 'Canvas' }
        225 { $kind = 'CanvasGroup' }
        default { $kind = "Class$($blk.Class)" }
    }
    $compKind[$id] = $kind
}

$roots = New-Object System.Collections.Generic.List[string]
foreach ($kv in $trFather.GetEnumerator()) {
    if ($kv.Value -eq '0') { $roots.Add($kv.Key) }
}

function Show-Node {
    param([string]$TrId, [int]$Depth)
    if ($Depth -gt $MaxDepth) { return }

    $gid = $null
    foreach ($kv in $trOf.GetEnumerator()) { if ($kv.Value -eq $TrId) { $gid = $kv.Key; break } }

    $name = '?'
    if ($gid -ne $null -and $goName.ContainsKey($gid)) { $name = $goName[$gid] }

    $kinds = @()
    if ($gid -ne $null -and $goComps.ContainsKey($gid)) {
        foreach ($c in $goComps[$gid]) { if ($compKind.ContainsKey($c)) { $kinds += $compKind[$c] } }
    }

    if ($OnlyDepth -lt 0 -or $Depth -eq $OnlyDepth) {
        $indent = '  ' * $Depth
        $k = (($kinds | Where-Object { $_ -ne 'CanvasRenderer' }) -join ', ')
        Write-Host ("$indent$name" + "  |  " + $k)
    }
    if ($trChildren.ContainsKey($TrId)) {
        foreach ($kid in $trChildren[$TrId]) { Show-Node -TrId $kid -Depth ($Depth + 1) }
    }
}

Write-Host ("=== " + (Split-Path -Leaf $Path) + "  blocks=" + $blocks.Count + "  roots=" + $roots.Count)
foreach ($r in $roots) { Show-Node -TrId $r -Depth 0 }
