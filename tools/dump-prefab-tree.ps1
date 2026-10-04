# =====================================================================
# 转储 Unity prefab 的逻辑层级树（含嵌套 PrefabInstance）。
#
# 本 Unity 工程没有 .meta 文件，所以既无法把 MonoBehaviour 的 guid 映射回
# C# 类型名，也无法直接把 PrefabInstance 的 guid 映射回源 prefab 路径。
# 于是：
#   1) 内置 UI 组件按**序列化字段指纹**识别；
#   2) PrefabInstance 作为树上的一个节点，名字取它覆盖的 m_Name
#      （嵌套 prefab 的真实内容在源文件里，这里只看得到名字与父节点）；
#   3) 加 -ResolveSources 时，用「第一个 target fileID」在兄弟 prefab 文件里
#      反查是哪个文件定义了它 —— 这就是源 prefab。
#
# 用法：
#   powershell -File dump-prefab-tree.ps1 -Path <prefab> [-MaxDepth 12] [-ResolveSources]
# =====================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$MaxDepth = 12,
    [switch]$ResolveSources
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Path)) { throw "prefab not found: $Path" }

function M1([string]$text, [string]$pattern) {
    $m = [regex]::Match($text, $pattern)
    if ($m.Success -and $m.Groups.Count -gt 1) { return $m.Groups[1].Value }
    return ''
}
function Has([string]$text, [string]$pattern) { return [regex]::IsMatch($text, $pattern) }

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
$trOf       = @{}      # 变换 fileID -> GameObject fileID
$trFather   = @{}      # 变换 fileID -> 父变换 fileID
$trKids     = @{}      # 变换 fileID -> 子变换 fileID 列表
$compKind   = @{}

# PrefabInstance：实例 fileID -> @{ Name; Parent; TargetFileID; Guid }
$piInfo = @{}
$piKids = @{}          # 父变换 fileID -> 实例 fileID 列表

foreach ($blk in $blocks) {
    $fid = $blk.FileID
    $body = $blk.Body

    switch ($blk.Class) {
        1 {
            $goName[$fid] = (M1 $body '(?m)^\s*m_Name:\s*(.*)$')
            $list = New-Object System.Collections.Generic.List[string]
            foreach ($m in [regex]::Matches($body, '(?m)^\s*-\s*component:\s*\{fileID:\s*(-?\d+)\}')) {
                $list.Add($m.Groups[1].Value)
            }
            $goComps[$fid] = $list
        }
        { $_ -eq 4 -or $_ -eq 224 } {
            $gid = M1 $body '(?m)^\s*m_GameObject:\s*\{fileID:\s*(-?\d+)\}'
            $isStripped = Has $body 'stripped'
            if ([string]::IsNullOrEmpty($gid) -and -not $isStripped) { break }
            $trOf[$fid] = $gid
            $father = M1 $body '(?m)^\s*m_Father:\s*\{fileID:\s*(-?\d+)\}'
            if ([string]::IsNullOrEmpty($father)) { $father = '0' }
            $trFather[$fid] = $father

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
            $trKids[$fid] = $kids
            $compKind[$fid] = $(if ($blk.Class -eq 224) { 'RectTransform' } else { 'Transform' })
        }
        1001 {
            $parent = M1 $body '(?m)^\s*m_TransformParent:\s*\{fileID:\s*(-?\d+)\}'
            $nm = M1 $body '(?ms)propertyPath:\s*m_Name\s*\r?\n\s*value:\s*(.*?)\r?\n'
            $tgt = M1 $body '(?m)^\s*- target:\s*\{fileID:\s*(-?\d+),\s*guid:\s*([0-9a-f]+)'
            $gd = M1 $body '(?m)^\s*- target:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]+)'
            $piInfo[$fid] = [pscustomobject]@{ Name = $nm; Parent = $parent; Target = $tgt; Guid = $gd }
            if (-not [string]::IsNullOrEmpty($parent)) {
                if (-not $piKids.ContainsKey($parent)) {
                    $piKids[$parent] = New-Object System.Collections.Generic.List[string]
                }
                $piKids[$parent].Add($fid)
            }
        }
        114 {
            $kind = $null
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
            $compKind[$fid] = $kind
        }
        222 { $compKind[$fid] = 'CanvasRenderer' }
        223 { $compKind[$fid] = 'Canvas' }
        225 { $compKind[$fid] = 'CanvasGroup' }
        95  { $compKind[$fid] = 'Animator' }
        default { $compKind[$fid] = "Class$($blk.Class)" }
    }
}

function Get-NodeLabel([string]$trId) {
    if ($piInfo.ContainsKey($trId)) {
        $pi = $piInfo[$trId]
        return @{ Sort = 'PrefabInstance'; Name = $(if ([string]::IsNullOrEmpty($pi.Name)) { '<inst>' } else { $pi.Name }); Kinds = @('PrefabInstance') }
    }
    $gid = $trOf[$trId]
    $name = '?'
    if (-not [string]::IsNullOrEmpty($gid) -and $goName.ContainsKey($gid)) { $name = $goName[$gid] }
    $kinds = @()
    if (-not [string]::IsNullOrEmpty($gid) -and $goComps.ContainsKey($gid)) {
        foreach ($c in $goComps[$gid]) { if ($compKind.ContainsKey($c)) { $kinds += $compKind[$c] } }
    }
    return @{ Sort = 'Transform'; Name = $name; Kinds = $kinds }
}

function Get-Kids([string]$trId) {
    $out = New-Object System.Collections.Generic.List[string]
    if ($trKids.ContainsKey($trId)) { foreach ($k in $trKids[$trId]) { $out.Add($k) } }
    if ($piKids.ContainsKey($trId)) { foreach ($k in $piKids[$trId]) { $out.Add($k) } }
    return $out
}

function Resolve-Source([string]$fileId) {
    if ([string]::IsNullOrEmpty($fileId)) { return '' }
    $dir = Split-Path -Parent $Path
    $needle = "(?m)^--- !u!\d+ &$fileId\b"
    foreach ($f in Get-ChildItem -LiteralPath $dir -Filter *.prefab -File) {
        if ($f.FullName -eq $Path) { continue }
        if ([regex]::IsMatch([System.IO.File]::ReadAllText($f.FullName), $needle)) { return $f.Name }
    }
    # 再往上找一层
    $up = Split-Path -Parent $dir
    if ($up) {
        foreach ($f in Get-ChildItem -LiteralPath $up -Filter *.prefab -File) {
            if ([regex]::IsMatch([System.IO.File]::ReadAllText($f.FullName), $needle)) { return '(../)' + $f.Name }
        }
    }
    return '?'
}

# 根：非 stripped、且父为 0 的变换；另外把 m_TransformParent 指向根的实例也算进来
$roots = New-Object System.Collections.Generic.List[string]
foreach ($kv in $trFather.GetEnumerator()) {
    if ($kv.Value -eq '0' -and -not [string]::IsNullOrEmpty($trOf[$kv.Key])) { $roots.Add($kv.Key) }
}
foreach ($kv in $piInfo.GetEnumerator()) {
    $p = $kv.Value.Parent
    if ([string]::IsNullOrEmpty($p) -or $p -eq '0') { $roots.Add($kv.Key) }
}
if ($roots.Count -eq 0) {
    # 兜底：没有明显根时，把所有 "没有非 stripped 父节点" 的节点当根
    foreach ($kv in $trFather.GetEnumerator()) { $roots.Add($kv.Key) }
}

function Show-Node {
    param([string]$TrId, [int]$Depth)
    if ($Depth -gt $MaxDepth) { return }
    $info = Get-NodeLabel $TrId
    $indent = '  ' * $Depth
    $k = ($info.Kinds | Where-Object { $_ -ne 'CanvasRenderer' }) -join ', '
    $extra = ''
    if ($ResolveSources -and $piInfo.ContainsKey($TrId)) {
        $extra = '   <= ' + (Resolve-Source $piInfo[$TrId].Target)
    }
    Write-Host ("$indent" + $info.Name + "  |  " + $k + $extra)
    foreach ($kid in (Get-Kids $TrId)) { Show-Node -TrId $kid -Depth ($Depth + 1) }
}

$classes = ($blocks | Group-Object Class | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ', '
Write-Host ("=== " + (Split-Path -Leaf $Path) + "   [" + $classes + "]")
foreach ($r in ($roots | Select-Object -Unique)) { Show-Node -TrId $r -Depth 0 }
