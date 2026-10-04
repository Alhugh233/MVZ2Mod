# =====================================================================
# 两张 PNG 的像素差分。
#
# 为什么需要它：本仓库已经吃过好几次「矢量数据全对、屏幕上一个像素都没有」的亏
# （节点 active、Image.enabled=1、色 alpha=1、屏幕矩形完全正确 —— 就是不画）。
# 这种问题**只有像素能回答**，而人看截图是主观的、也无法写进日志。
# 所以把判定变成一个数字：差异像素数与差异范围的包围盒。
#
# 用法：
#   powershell -File img-diff.ps1 -A 图1.png -B 图2.png [-Tolerance 8] [-SaveDiff 输出.png]
#
# 判读：
#   · 差异范围（包围盒）应该与「被判定对象」的屏幕矩形对得上；
#   · 游戏里背景会动，所以差异不会为 0 —— 看的是**量级**与**位置**。
#     差分像素集中在目标矩形内 → 那块东西确实画出来了；
#     全图散布且量级很小（几百像素）→ 目标没画。
# =====================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$A,
    [Parameter(Mandatory = $true)][string]$B,
    [int]$Tolerance = 8,
    [string]$SaveDiff = "",
    # 只看这块区域（**图像坐标** x,y,w,h；y 向下）。
    # 为什么需要：标题界面自己会动（背景动画/图标闪烁），全屏差分会被动画淹没 ——
    # 想判断“某个控件到底画出来没有”就必须限定在它自己的矩形里。
    # 拆成四个标量而不是 int[]：用 powershell -File 传数组绑不上。
    [int]$RegionX = -1,
    [int]$RegionY = -1,
    [int]$RegionW = -1,
    [int]$RegionH = -1
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# ⚠️ 局部变量不要叫 $a / $b：PowerShell 变量名大小写不敏感，而参数 $A / $B 已被声明为
# [string]，于是 $a = [Bitmap]::FromFile(...) 会被**强制转成字符串**（后面 .Dispose() 就报
# “String 不包含 Dispose”）。这就是本脚本第一版挂掉的原因，故一律用 $imgA / $imgB。

foreach ($p in @($A, $B)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "找不到图片: $p" }
}

$imgA = [System.Drawing.Bitmap]::FromFile((Resolve-Path -LiteralPath $A).Path)
$imgB = [System.Drawing.Bitmap]::FromFile((Resolve-Path -LiteralPath $B).Path)

try {
    if ($imgA.Width -ne $imgB.Width -or $imgA.Height -ne $imgB.Height) {
        throw ("尺寸不一致: {0}x{1} vs {2}x{3}" -f $imgA.Width, $imgA.Height, $imgB.Width, $imgB.Height)
    }

    $w = $imgA.Width
    $h = $imgA.Height
    $total = $w * $h
    $diff = 0

    # 用 LockBits 拿原始像素：逐像素 GetPixel 在这个尺寸上要几十秒。
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $fmt = [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
    $da = $imgA.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, $fmt)
    $db = $imgB.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, $fmt)

    $bytes = $w * $h * 4
    $bufA = New-Object byte[] $bytes
    $bufB = New-Object byte[] $bytes
    [System.Runtime.InteropServices.Marshal]::Copy($da.Scan0, $bufA, 0, $bytes)
    [System.Runtime.InteropServices.Marshal]::Copy($db.Scan0, $bufB, 0, $bytes)

    $imgA.UnlockBits($da)
    $imgB.UnlockBits($db)

    $minX = $w; $minY = $h; $maxX = -1; $maxY = -1

    # 统计范围：默认全屏，给定 -Region 就只扫那块。
    $rx0 = 0; $ry0 = 0; $rx1 = $w - 1; $ry1 = $h - 1
    $regionText = 'full'
    if ($RegionW -gt 0 -and $RegionH -gt 0 -and $RegionX -ge 0 -and $RegionY -ge 0) {
        $rx0 = [Math]::Max(0, $RegionX); $ry0 = [Math]::Max(0, $RegionY)
        $rx1 = [Math]::Min($w - 1, $RegionX + $RegionW - 1)
        $ry1 = [Math]::Min($h - 1, $RegionY + $RegionH - 1)
        $regionText = ('x{0} y{1} {2}x{3}' -f $rx0, $ry0, ($rx1 - $rx0 + 1), ($ry1 - $ry0 + 1))
    }
    $scanned = ($rx1 - $rx0 + 1) * ($ry1 - $ry0 + 1)

    for ($y = $ry0; $y -le $ry1; $y++) {
        $row = $y * $w * 4
        for ($x = $rx0; $x -le $rx1; $x++) {
            $i = $row + $x * 4
            $d = [Math]::Abs($bufA[$i] - $bufB[$i]) +
                 [Math]::Abs($bufA[$i + 1] - $bufB[$i + 1]) +
                 [Math]::Abs($bufA[$i + 2] - $bufB[$i + 2])
            if ($d -gt $Tolerance) {
                $diff++
                # 图像坐标 y 轴向下；转成屏幕坐标（y 轴向上）时再翻，这里先记图像坐标。
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }

    $pct = [Math]::Round(100.0 * $diff / [Math]::Max(1, $scanned), 3)

    # 纯 ASCII 键值行：脚本拼接时好解析（中文在无 BOM 的 ps1 里会被误读成乱码）。
    Write-Host ("IMAGE_A = {0}" -f (Split-Path -Leaf $A))
    Write-Host ("IMAGE_B = {0}" -f (Split-Path -Leaf $B))
    Write-Host ("DIFF_PIXELS = {0}" -f $diff)
    Write-Host ("DIFF_RATIO_PCT = {0}" -f $pct)
    Write-Host ("SCANNED = {0}  REGION = {1}" -f $scanned, $regionText)
    if ($diff -gt 0) {
        Write-Host ("DIFF_BBOX_IMAGE = x{0} y{1} {2}x{3}" -f $minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1))
        Write-Host ("DIFF_BBOX_SCREEN = x{0} y{1} {2}x{3}" -f $minX, ($h - 1 - $maxY), ($maxX - $minX + 1), ($maxY - $minY + 1))
    }

    Write-Host ("A = {0}" -f (Split-Path -Leaf $A))
    Write-Host ("B = {0}" -f (Split-Path -Leaf $B))
    Write-Host ("画布 = {0}x{1}  总像素 = {2}" -f $w, $h, $total)
    Write-Host ("差异像素 = {0}  ({1}%)  容差 = {2}" -f $diff, $pct, $Tolerance)

    if ($diff -gt 0) {
        $bw = $maxX - $minX + 1
        $bh = $maxY - $minY + 1
        Write-Host ("差异包围盒（图像坐标，y 向下）= x{0} y{1} {2}x{3}" -f $minX, $minY, $bw, $bh)
        Write-Host ("差异包围盒转屏幕坐标（y 向上，原点左下）= x{0} y{1} {2}x{3}" -f $minX, ($h - 1 - $maxY), $bw, $bh)
    } else {
        Write-Host "差异包围盒 = （无差异）"
    }

    if (-not [string]::IsNullOrWhiteSpace($SaveDiff)) {
        # 把 B 画出来，差异像素涂红 —— 一眼能看出"差在哪一块"。
        try {
            $target = [System.IO.Path]::GetFullPath($SaveDiff)
            $out = New-Object System.Drawing.Bitmap $w, $h
            $src = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $B).Path)
            $g = [System.Drawing.Graphics]::FromImage($out)
            $g.DrawImage($src, 0, 0, $w, $h)
            $g.Dispose()
            $src.Dispose()

            for ($y = 0; $y -lt $h; $y++) {
                for ($x = 0; $x -lt $w; $x++) {
                    $i = ($y * $w + $x) * 4
                    $d = [Math]::Abs($bufA[$i] - $bufB[$i]) +
                         [Math]::Abs($bufA[$i + 1] - $bufB[$i + 1]) +
                         [Math]::Abs($bufA[$i + 2] - $bufB[$i + 2])
                    if ($d -gt $Tolerance) { $out.SetPixel($x, $y, [System.Drawing.Color]::Red) }
                }
            }

            $out.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
            $out.Dispose()
            Write-Host ('SAVED_DIFF = ' + $target + '  exists=' + (Test-Path -LiteralPath $target))
        }
        catch {
            Write-Host ('SAVE_DIFF_FAILED = ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message)
        }
    }
}
finally {
    $imgA.Dispose()
    $imgB.Dispose()
}
