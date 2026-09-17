# =====================================================================
# MVZ2 Laboratory Content（实验室小游戏）—— 构建 / 部署 / 打包
#
#   .\build.ps1                   构建（若游戏目录存在则自动部署）
#   .\build.ps1 -Clean            先清 bin/ obj/ 再构建
#   .\build.ps1 -KeepGame         不结束正在运行的游戏
#   .\build.ps1 -Pack             额外打包可分发 zip 到 dist\
#   .\build.ps1 -Pack -Launch     打包后启动游戏冒烟
#
# 说明：
#   - 编译引用来自仓库自包含的 runtime\，构建不依赖游戏目录。
#   - 工程通过 ProjectReference 引用 DSHCore，构建它会一并构建前置。
#   - 本 mod 的包体「不含」BepInEx 运行环境：需先安装 DSHCore 的包。
# =====================================================================
param(
    [string]$GameDir = "",
    [switch]$SkipBuild,
    [switch]$Clean,
    [switch]$KeepGame,
    [switch]$Pack,
    [switch]$Launch
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\tools\build-common.ps1"

$repo      = Get-RepoRoot
$modDir    = $PSScriptRoot
$modName   = 'LaboratoryMod'
$namespace = 'mvz2_lab'
$csproj    = Join-Path $modDir 'LaboratoryMod.csproj'
$pluginCs  = Join-Path $modDir 'Plugin.cs'
$pluginDll = Join-Path $modDir 'bin\Debug\LaboratoryMod.dll'
$dshDll    = Join-Path $repo 'DSHCore\bin\Debug\DSHCore.dll'
$assetsDir = Join-Path $modDir 'assets'
$outDir    = Join-Path $repo 'dist'
$runtime   = Join-Path $repo 'runtime'

$GameDir = Normalize-GameDir $GameDir 'E:\Game\PVZ\MVZ2\MVZ2 0.7.0 test-4'
$version = Get-PluginVersion $pluginCs
$dotnet  = Find-Dotnet
$hasGame = Test-GameDir $GameDir

Write-Host ("MVZ2 Laboratory Content v" + $version) -ForegroundColor White
Write-Info ("命名空间 : " + $namespace)
Write-Info ("dotnet   : " + $dotnet)
Write-Info ("game dir : " + $GameDir + $(if ($hasGame) { '' } else { '   [不存在 -> 只构建]' }))

# ---------- 1. 结束游戏（它锁住插件 DLL） ----------
Write-Step "[1/4] 结束游戏进程"
Stop-GameProcess -Keep:$KeepGame

# ---------- 2. 构建（含前置 DSHCore） ----------
Write-Step "[2/4] 构建 LaboratoryMod.csproj -c Debug"
Invoke-DotnetBuild -Csproj $csproj -Dotnet $dotnet -GameDir $GameDir -Clean:$Clean -SkipBuild:$SkipBuild
if (-not (Test-Path $pluginDll)) { throw ("插件产物不存在：" + $pluginDll) }
$dll = Get-Item $pluginDll
Write-Ok ("DLL: " + $dll.Length + " bytes  " + $dll.LastWriteTime)

# ---------- 3. 校验自动部署 ----------
Write-Step "[3/4] 校验部署"
if (-not $hasGame) {
    Write-Warn2 "游戏目录不存在，未部署"
} else {
    $ok1 = Test-DeployedFile -Source $pluginDll `
        -Target (Join-Path $GameDir 'BepInEx\plugins\LaboratoryMod.dll') -Label 'LaboratoryMod.dll'
    $ok2 = Test-DeployedFile -Source $dshDll `
        -Target (Join-Path $GameDir 'BepInEx\plugins\DSHCore.dll') -Label 'DSHCore.dll（前置）'
    $ok3 = Test-ModAssetDeploy -AssetsDir $assetsDir -GameDir $GameDir -Namespace $namespace
    if ($ok1 -and $ok2 -and $ok3) { Write-Ok "部署一致" } else { Write-Warn2 "有文件过期（构建时游戏在运行？）" }
}

# ---------- 4. 打包 ----------
Write-Step "[4/4] 打包"
if (-not $Pack) {
    Write-Warn2 "跳过（加 -Pack 启用）"
} else {
    $readme = Get-Content (Join-Path $modDir 'install-readme.txt') -Raw -Encoding UTF8
    $readme = $readme.Replace('{VERSION}', $version)
    $pkgArgs = @{
        PackageName   = ($modName + '_v' + $version + '_0.7.0')
        OutDir        = $outDir
        RuntimeDir    = $runtime
        PluginDlls    = @($pluginDll, $dshDll)
        AssetsDir     = $assetsDir
        Namespace     = $namespace
        InstallReadme = $readme
    }
    New-ModPackage @pkgArgs | Out-Null
}

# ---------- 5. 启动（可选） ----------
if ($Launch) {
    if (-not $hasGame) { Write-Warn2 "-Launch 需要有效的 -GameDir" }
    else {
        Write-Host ""
        Write-Host ("启动 " + (Join-Path $GameDir 'MinecraftVSZombies2.exe')) -ForegroundColor Cyan
        Start-Process -FilePath (Join-Path $GameDir 'MinecraftVSZombies2.exe') -WorkingDirectory $GameDir
    }
}

Write-Host ""
Write-Ok ("完成。LaboratoryMod v" + $version)
