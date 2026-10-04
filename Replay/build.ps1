# =====================================================================
# MVZ2 Replay —— 构建 / 部署
#
#   .\build.ps1                   构建（若游戏目录存在则自动部署）
#   .\build.ps1 -Clean            先清 bin/ obj/ 再构建
#   .\build.ps1 -KeepGame         不结束正在运行的游戏
#   .\build.ps1 -Launch           构建完启动游戏
#
# 说明：
#   - 编译引用来自 runtime\（仓库内那份 test-9 的 BepInEx core + interop）。
#   - DSHCore 前置的引用方式由 ..\Directory.Build.props 决定。
#   - 本 mod 没有需要部署的资源（没有 assets\），只有插件 DLL。
# =====================================================================
param(
    [string]$GameDir = "",
    [switch]$SkipBuild,
    [switch]$Clean,
    [switch]$KeepGame,
    [switch]$Launch
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\tools\build-common.ps1"

$repo      = Get-RepoRoot
$modDir    = $PSScriptRoot
$csproj    = Join-Path $modDir 'Replay.csproj'
$pluginCs  = Join-Path $modDir 'Plugin.cs'
$pluginDll = Join-Path $modDir 'bin\Debug\Replay.dll'

$GameDir = Normalize-GameDir $GameDir (Get-DefaultGameDir -RepoRoot $repo)
$version = Get-PluginVersion $pluginCs
$dotnet  = Find-Dotnet
$hasGame = Test-GameDir $GameDir

Write-Host ("MVZ2 Replay v" + $version) -ForegroundColor White
Write-Info ("dotnet   : " + $dotnet)
Write-Info ("game dir : " + $GameDir + $(if ($hasGame) { '' } else { '   [不存在 -> 只构建]' }))

# ---------- 1. 结束游戏（它锁住插件 DLL） ----------
Write-Step "[1/3] 结束游戏进程"
Stop-GameProcess -Keep:$KeepGame

# ---------- 2. 构建（含前置 DSHCore） ----------
Write-Step "[2/3] 构建 Replay.csproj -c Debug"
Invoke-DotnetBuild -Csproj $csproj -Dotnet $dotnet -GameDir $GameDir -Clean:$Clean -SkipBuild:$SkipBuild
if (-not (Test-Path $pluginDll)) { throw ("插件产物不存在：" + $pluginDll) }
$dll = Get-Item $pluginDll
Write-Ok ("DLL: " + $dll.Length + " bytes  " + $dll.LastWriteTime)

# ---------- 3. 校验自动部署 ----------
Write-Step "[3/3] 校验部署"
$dshDll = Resolve-DshCoreDll -RepoRoot $repo
if (-not $hasGame) {
    Write-Warn2 "游戏目录不存在，未部署"
} else {
    $ok1 = Test-DeployedFile -Source $pluginDll `
        -Target (Join-Path $GameDir 'BepInEx\plugins\Replay.dll') -Label 'Replay.dll'
    $ok2 = Test-DeployedFile -Source $dshDll `
        -Target (Join-Path $GameDir 'BepInEx\plugins\DSHCore.dll') -Label 'DSHCore.dll（前置）'
    if ($ok1 -and $ok2) { Write-Ok "部署一致" } else { Write-Warn2 "有文件过期（构建时游戏在运行？）" }
}

if ($Launch) {
    if (-not $hasGame) {
        Write-Warn2 "-Launch 需要有效的 -GameDir"
    } else {
        Write-Host ("启动 " + (Join-Path $GameDir 'MinecraftVSZombies2.exe')) -ForegroundColor Cyan
        Start-Process -FilePath (Join-Path $GameDir 'MinecraftVSZombies2.exe') -WorkingDirectory $GameDir
    }
}
