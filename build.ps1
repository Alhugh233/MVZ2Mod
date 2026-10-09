# =====================================================================
# MVZ2 Mod 工作区 —— 总构建脚本
#
# 依次构建全部 mod。每个 mod 自己的 build.ps1 负责构建 / 部署 / 打包，
# 本脚本只是编排（依赖顺序：DSHCore → LaboratoryMod / ModularCurse / Loadout）。
#
#   .\build.ps1                        构建全部（有游戏目录则自动部署）
#   .\build.ps1 -Mod dsh               只构建 DSHCore
#   .\build.ps1 -Mod lab -Pack         只构建 LaboratoryMod 并打包
#   .\build.ps1 -Clean -Pack           全部：先清再建，并打包
#   .\build.ps1 -Mod dsh -PackDevKit   打「不含 DSHCore 源码」的便携开发环境包
#   .\build.ps1 -Pack -NoRuntime       全部：打「只含插件」的轻量包（不带 BepInEx）
#   .\build.ps1 -Pack -Launch          全部：打包后启动游戏
#   .\build.ps1 -Mvz2Pack              全部：再打一个 MVZ2ModManager 的模组包（.mvz2pack）
#
# -Mvz2Pack 打的是「除 UI 画廊外的全部 mod」，只含模组本体（不带 BepInEx 运行环境），
# 专给 MVZ2ModManager 导入；它不能和 -Mod 一起用。
#
# 参数会原样透传给各 mod 的 build.ps1（只有 -PackDevKit 是 DSHCore 专属）。
# 只想构建单个 mod 时，直接运行该 mod 目录下的 build.ps1 更直观。
#
# 注：在精简工作区（例如 DevKit 包里剔掉了 DSHCore 源码）里跑 -Mod all 时，
#     缺失的 mod 会被优雅跳过，不算失败。
# =====================================================================
[CmdletBinding()]
param(
    [ValidateSet('all', 'dsh', 'lab', 'curse', 'ld', 'uig', 'rp')]
    [string]$Mod = 'all',
    [string]$GameDir = "",
    [switch]$SkipBuild,
    [switch]$Clean,
    [switch]$KeepGame,
    [switch]$Pack,
    [switch]$Mvz2Pack,
    [string]$Mvz2PackName = "",
    [string]$Mvz2PackAuthor = "QoZnoS",
    [switch]$Launch,
    [switch]$PackDevKit,
    [switch]$NoRuntime
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\tools\build-common.ps1"

# 依赖顺序：前置在前。
# Dll / Ns 是给「MVZ2 模组包」（-Mvz2Pack）用的：Dll = 插件产物文件名，
# Ns = 内容模组的原生命名空间（也就是 StreamingAssets\Mods\ 下的目录名，纯工具 mod 为空）。
# InMvz2Pack = $false 表示这个 mod 不进模组包。
$all = @(
    @{ Key = 'dsh';   Dir = 'DSHCore';       Title = 'DSHCore';                 Dll = 'DSHCore.dll';       Ns = '' },
    @{ Key = 'lab';   Dir = 'LaboratoryMod'; Title = 'MVZ2 Laboratory Content'; Dll = 'LaboratoryMod.dll'; Ns = 'mvz2_lab' },
    @{ Key = 'curse'; Dir = 'ModularCurse';  Title = 'MVZ2 Modular Curse';      Dll = 'ModularCurse.dll';  Ns = 'modular_curse' },
    @{ Key = 'ld';    Dir = 'Loadout';       Title = 'MVZ2 Loadout';            Dll = 'Loadout.dll';       Ns = '' },
    @{ Key = 'uig';   Dir = 'UiGallery';     Title = 'MVZ2 UI Gallery';         Dll = 'UiGallery.dll';     Ns = 'uig'; InMvz2Pack = $false },
    @{ Key = 'rp';    Dir = 'Replay';        Title = 'MVZ2 Replay';             Dll = 'Replay.dll';        Ns = '' }
)

# 模组包打的是「全套」，单独挑一个 mod 打没有意义 —— 而且缺了文件会在
# New-Mvz2Pack 里直接 throw，不如在这种误用上就地拦住。
if ($Mvz2Pack -and $Mod -ne 'all') {
    throw "-Mvz2Pack 打的是「除 UI 画廊外的全部 mod」，不能和 -Mod 一起用。"
}


$selected = if ($Mod -eq 'all') { $all } else { @($all | Where-Object { $_.Key -eq $Mod }) }

# 透传参数
$passthru = @{}
if (-not [string]::IsNullOrWhiteSpace($GameDir)) { $passthru['GameDir'] = $GameDir }
if ($SkipBuild) { $passthru['SkipBuild'] = $true }
if ($Clean)     { $passthru['Clean']     = $true }
if ($KeepGame)  { $passthru['KeepGame']  = $true }
if ($Pack)      { $passthru['Pack']      = $true }
if ($Launch)    { $passthru['Launch']    = $true }
if ($PackDevKit) { $passthru['PackDevKit'] = $true }
if ($NoRuntime) { $passthru['NoRuntime'] = $true }

Write-Host ""
Write-Host ("构建目标: " + (($selected | ForEach-Object { $_.Key }) -join ', ')) -ForegroundColor White

$failed = @()
foreach ($m in $selected) {
    $script = Join-Path (Join-Path $PSScriptRoot $m.Dir) 'build.ps1'
    if (-not (Test-Path $script)) {
        if ($Mod -eq 'all') {
            # 精简工作区（例如 DevKit 包剔掉了 DSHCore 源码）—— 跳过不算失败
            Write-Warn2 ("工作区未包含 " + $m.Dir + "，跳过")
            continue
        }
        Write-Host ""
        Write-Warn2 ("找不到 " + $script)
        $failed += $m.Key
        continue
    }

    # -PackDevKit 只有 DSHCore 的 build.ps1 认识，别传给其它 mod。
    # （-NoRuntime 三个 mod 都认识：DSHCore 用它跳过运行环境，
    #   内容 mod 用它把包体从「自包含」切成「只含插件」。）
    $p = @{}
    foreach ($k in $passthru.Keys) { $p[$k] = $passthru[$k] }
    if ($m.Key -ne 'dsh') {
        [void]$p.Remove('PackDevKit')
    }

    Write-Host ""
    Write-Host ("################  " + $m.Title + "  ################") -ForegroundColor Magenta
    # 子脚本对硬错误一律 throw（编译失败 / 产物缺失 / 打包失败），
    # 所以这里用 try/catch 判定成败。
    # 注意：不要用 $LASTEXITCODE 判定 —— 子脚本在 -SkipBuild 等路径下不会调用
    # 原生命令（dotnet），此时 $LASTEXITCODE 为 $null，$null -ne 0 会误判为失败。
    try {
        & $script @p
    } catch {
        Write-Warn2 ($m.Title + " 构建失败：" + $_.Exception.Message)
        $failed += $m.Key
        continue
    }
}

Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host ("失败的 mod: " + ($failed -join ', ')) -ForegroundColor Red
    exit 1
}
Write-Host "全部完成。" -ForegroundColor Green

# ---------- MVZ2 模组包（MVZ2ModManager 的 .mvz2pack） ----------
#
# 与 -Pack 打的那几个 zip 是两回事：
#   zip        = 自包含（含 BepInEx 运行环境），解压到游戏根目录即用，给手工安装
#   .mvz2pack  = 只含模组本体，丢给 MVZ2ModManager 导入/还原（运行环境由它负责）
if ($Mvz2Pack) {
    Write-Host ""
    Write-Host "################  MVZ2 模组包（MVZ2ModManager）  ################" -ForegroundColor Magenta

    # 从各 mod 的**构建产物**取文件（不读游戏目录）：这样在没有游戏目录（或游戏目录
    # 刚被重置）的机器上也能打包，而且打出来的就是这一次构建的东西。
    $items = @()
    foreach ($m in $all) {
        if ($m.ContainsKey('InMvz2Pack') -and (-not $m.InMvz2Pack)) {
            Write-Info ($m.Title + " -> 不进模组包（开发者工具）")
            continue
        }
        $modDirAbs = Join-Path $PSScriptRoot $m.Dir
        $items += @{
            Name      = [System.IO.Path]::GetFileNameWithoutExtension($m.Dll)
            Dll       = Join-Path $modDirAbs ('bin\Debug\' + $m.Dll)
            Namespace = $m.Ns
            AssetsDir = Join-Path $modDirAbs 'assets'
        }
    }

    # 包名沿用各 mod build.ps1 的约定 `<名字>_v<版本>_<游戏版本>`；
    # 一份合集没有单一版本号，所以那一段用打包日期。
    $packName = $Mvz2PackName
    if ([string]::IsNullOrWhiteSpace($packName)) {
        $packName = ('MVZ2Mods_v' + (Get-Date -Format 'yyyyMMdd') + '_0.7.0')
    }

    try {
        New-Mvz2Pack -OutDir (Join-Path $PSScriptRoot 'dist') `
            -Items $items `
            -PackageName $packName `
            -DisplayName ('MVZ2 模组合集 v' + (Get-Date -Format 'yyyy-MM-dd')) `
            -Author $Mvz2PackAuthor | Out-Null
    } catch {
        Write-Host ""
        Write-Warn2 ("模组包打包失败：" + $_.Exception.Message)
        exit 1
    }
}

if ($Pack -or $Mvz2Pack) {
    Write-Host ""
    Write-Host ("包体输出目录: " + (Join-Path $PSScriptRoot 'dist')) -ForegroundColor Green
}
