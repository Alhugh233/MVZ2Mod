# =====================================================================
# MVZ2 Mod 工作区 —— 总构建脚本
#
# 依次构建三个 mod。每个 mod 自己的 build.ps1 负责构建 / 部署 / 打包，
# 本脚本只是编排（依赖顺序：DSHCore → LaboratoryMod / ModularCurse）。
#
#   .\build.ps1                        构建全部（有游戏目录则自动部署）
#   .\build.ps1 -Mod dsh               只构建 DSHCore
#   .\build.ps1 -Mod lab -Pack         只构建 LaboratoryMod 并打包
#   .\build.ps1 -Clean -Pack           全部：先清再建，并打包
#   .\build.ps1 -Mod dsh -PackDevKit   打「不含 DSHCore 源码」的便携开发环境包
#   .\build.ps1 -Pack -NoRuntime       全部：打「只含插件」的轻量包（不带 BepInEx）
#   .\build.ps1 -Pack -Launch          全部：打包后启动游戏
#
# 参数会原样透传给各 mod 的 build.ps1（只有 -PackDevKit 是 DSHCore 专属）。
# 只想构建单个 mod 时，直接运行该 mod 目录下的 build.ps1 更直观。
#
# 注：在精简工作区（例如 DevKit 包里剔掉了 DSHCore 源码）里跑 -Mod all 时，
#     缺失的 mod 会被优雅跳过，不算失败。
# =====================================================================
[CmdletBinding()]
param(
    [ValidateSet('all', 'dsh', 'lab', 'curse')]
    [string]$Mod = 'all',
    [string]$GameDir = "",
    [switch]$SkipBuild,
    [switch]$Clean,
    [switch]$KeepGame,
    [switch]$Pack,
    [switch]$Launch,
    [switch]$PackDevKit,
    [switch]$NoRuntime
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\tools\build-common.ps1"

# 依赖顺序：前置在前
$all = @(
    @{ Key = 'dsh';   Dir = 'DSHCore';       Title = 'DSHCore' },
    @{ Key = 'lab';   Dir = 'LaboratoryMod'; Title = 'MVZ2 Laboratory Content' },
    @{ Key = 'curse'; Dir = 'ModularCurse';  Title = 'MVZ2 Modular Curse' }
)

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
if ($Pack) {
    Write-Host ("包体输出目录: " + (Join-Path $PSScriptRoot 'dist')) -ForegroundColor Green
}
