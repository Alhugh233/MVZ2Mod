# MVZ2LabMod 一键构建与打包脚本
# 功能：构建插件 → 从仓库 runtime/ 收集运行环境 → 打包为可分发 zip
#
# 构建与打包完全不依赖测试游戏目录（环境自包含在仓库 runtime/）：
#   runtime\                          完整安装根（打包源，也是 csproj 的引用来源）
#     winhttp.dll / doorstop_config.ini / .doorstop_version
#     dotnet\                         .NET 6 x86 CoreCLR 运行时
#     BepInEx\core|interop|unity-libs|patchers|config\
#
# 打包内容（安装 = 把 zip 解压到 0.7.0 游戏根目录）：
#   上述 runtime\ 全部 + BepInEx\plugins\MVZ2LabMod.dll
#   + MinecraftVSZombies2_Data\StreamingAssets\Mods\mvz2_lab\
#
# 参数：
#   -OutDir         输出目录（默认 <仓库>\dist）
#   -SkipRuntime    排除 dotnet/ 运行时（接收方需自备 BepInEx 6 完整环境）
#   -SkipBuild      跳过 dotnet build（用已有产物）
#   -GameDir        测试用游戏目录（可选）。提供时执行：部署插件+资源、可 -Launch 冒烟
#   -SyncRuntime    维护模式：把 GameDir 里的 BepInEx 环境刷新回仓库 runtime/
#   -Launch         打包后启动游戏做冒烟验证（需 -GameDir）
param(
    [string]$OutDir = "",
    [switch]$SkipRuntime,
    [switch]$SkipBuild,
    [string]$GameDir = "",
    [switch]$SyncRuntime,
    [switch]$Launch
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($OutDir)) { $OutDir = Join-Path $root "dist" }
$runtime = Join-Path $root "runtime"
$pluginSource = Join-Path $root "MVZ2LabMod\bin\Debug\MVZ2LabMod.dll"
$assetSource = Join-Path $root "ModAssets\mvz2_lab"

function Step($title) { Write-Host "" ; Write-Host "== $title ==" -ForegroundColor Cyan }

# 版本号从 Plugin.cs 读取（与插件元数据保持一致）。
$pluginCs = Join-Path $root "MVZ2LabMod\Plugin.cs"
$versionLine = Select-String -Path $pluginCs -Pattern 'public const string VERSION = "([^"]+)"' | Select-Object -First 1
$version = if ($versionLine) { $versionLine.Matches[0].Groups[1].Value } else { "dev" }
$zipName = "MVZ2LabMod_v${version}_0.7.0.zip"
$zipPath = Join-Path $OutDir $zipName
$stageDir = Join-Path $OutDir "stage"

# ---------- 0. 维护模式：把游戏目录环境刷新回仓库 runtime/ ----------
if ($SyncRuntime) {
    Step "[0/5] 同步 runtime/ ← GameDir"
    if ([string]::IsNullOrEmpty($GameDir) -or -not (Test-Path (Join-Path $GameDir "BepInEx"))) {
        throw "-SyncRuntime 需要 -GameDir 指向已安装 BepInEx 的游戏目录"
    }
    Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    foreach ($rel in @("winhttp.dll", "doorstop_config.ini", ".doorstop_version", "dotnet", "BepInEx")) {
        $src = Join-Path $GameDir $rel
        if (-not (Test-Path $src)) { Write-Warning "跳过（游戏目录缺失）: $rel"; continue }
        if (Test-Path (Join-Path $runtime $rel)) { Remove-Item (Join-Path $runtime $rel) -Recurse -Force }
        Copy-Item $src (Join-Path $runtime $rel) -Recurse -Force
    }
    # 清理运行时垃圾
    Remove-Item (Join-Path $runtime "BepInEx\cache") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $runtime "BepInEx\LogOutput.log") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $runtime "BepInEx\ErrorLog.log") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $runtime "BepInEx\plugins\*") -Force -ErrorAction SilentlyContinue
    Write-Host "  runtime/ 已刷新" -ForegroundColor Green
    exit 0
}

# ---------- 1. 构建插件（引用仓库 runtime/，不依赖 GameDir） ----------
if (-not $SkipBuild) {
    Step "[1/5] 构建插件"
    Push-Location (Join-Path $root "MVZ2LabMod")
    try {
        dotnet build MVZ2LabMod.csproj -c Debug
        if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败（exit $LASTEXITCODE）" }
    }
    finally { Pop-Location }
} else {
    Step "[1/5] 跳过构建（-SkipBuild）"
}
if (-not (Test-Path $pluginSource)) { throw "未找到插件产物：$pluginSource（先运行一次构建）" }

# ---------- 2. 可选：部署到测试游戏目录 ----------
$hasGame = -not [string]::IsNullOrEmpty($GameDir) -and (Test-Path (Join-Path $GameDir "MinecraftVSZombies2.exe"))
if ($hasGame) {
    Step "[2/5] 部署到游戏目录：$GameDir"
    Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Copy-Item $pluginSource (Join-Path $GameDir "BepInEx\plugins\MVZ2LabMod.dll") -Force
    Copy-Item $assetSource (Join-Path $GameDir "MinecraftVSZombies2_Data\StreamingAssets\Mods\mvz2_lab") -Recurse -Force
} else {
    Step "[2/5] 跳过游戏部署（未提供有效 -GameDir）"
}

# ---------- 3. 准备暂存目录 ----------
Step "[3/5] 收集安装文件 → $stageDir"
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

function CopyTo($src, $dstRel) {
    if (-not (Test-Path $src)) { Write-Warning "跳过（不存在）: $src"; return }
    $dst = Join-Path $stageDir $dstRel
    New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
    Copy-Item $src $dst -Recurse -Force
}

# 门挡三件套 + BepInEx 运行时（从仓库 runtime/，与编译引用同源）
CopyTo (Join-Path $runtime "winhttp.dll") "winhttp.dll"
CopyTo (Join-Path $runtime "doorstop_config.ini") "doorstop_config.ini"
CopyTo (Join-Path $runtime ".doorstop_version") ".doorstop_version"
if (-not $SkipRuntime) {
    CopyTo (Join-Path $runtime "dotnet") "dotnet"
} else {
    Write-Host "  跳过 dotnet/ 运行时（-SkipRuntime）" -ForegroundColor Yellow
}
foreach ($dir in @("core", "interop", "unity-libs", "patchers")) {
    CopyTo (Join-Path $runtime "BepInEx\$dir") "BepInEx\$dir"
}
CopyTo (Join-Path $runtime "BepInEx\config\BepInEx.cfg") "BepInEx\config\BepInEx.cfg"
# 插件
CopyTo $pluginSource "BepInEx\plugins\MVZ2LabMod.dll"
# Mod 资源（meta XML 等）
CopyTo $assetSource "MinecraftVSZombies2_Data\StreamingAssets\Mods\mvz2_lab"

# 安装说明
$readme = Join-Path $stageDir "安装说明.txt"
@"
MVZ2 Laboratory Content Mod v$version
适用版本：Minecraft VS Zombies 2  0.7.0 test-1（Windows x86）

【安装步骤】
1. 关闭游戏。
2. 把本压缩包内所有内容解压到游戏根目录
   （即 MinecraftVSZombies2.exe 所在目录，直接覆盖同名文件）。
3. 启动游戏即可。BepInEx 控制台会随游戏弹出。

【验证】
- 主菜单 → 小游戏 → 列表末尾应有 3 个新关卡：
  真正的序章 / 真理伪证殿堂 / 零度空间
- 控制台输入 /gotolevel mvz2:real_prologue 可直接进入"真正的序章"。

【卸载】
删除以下文件/目录即可完全移除（不影响原版存档）：
  winhttp.dll  doorstop_config.ini  .doorstop_version  dotnet/
  BepInEx/  MinecraftVSZombies2_Data\StreamingAssets\Mods\mvz2_lab\

【故障排查】
- 游戏无法启动：确认没有遗漏 winhttp.dll 与 doorstop_config.ini。
- 控制台报 interop 相关错误：确认 BepInEx\interop\ 完整解压
  （共 120+ 个 dll，约 48MB，缺少会导致游戏崩溃）。
- 日志位置：游戏目录\BepInEx\LogOutput.log
"@ | Set-Content $readme -Encoding UTF8

# ---------- 4. 打包 ----------
Step "[4/5] 打包 zip"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

# ---------- 5. 汇总 ----------
Step "[5/5] 完成"
$zipItem = Get-Item $zipPath
$files = Get-ChildItem $stageDir -Recurse -File
Write-Host ("  产物: " + $zipPath) -ForegroundColor Green
Write-Host ("  大小: {0:N1} MB  ({1} 个文件)" -f ($zipItem.Length / 1MB), $files.Count)
Write-Host ("  运行时: {0}" -f ($(if ($SkipRuntime) { "不含（需自备 BepInEx 环境）" } else { "已包含（完整独立安装包）" })))
Remove-Item $stageDir -Recurse -Force

if ($Launch) {
    if (-not $hasGame) { Write-Warning "-Launch 需要有效的 -GameDir，已跳过" }
    else {
        Write-Host "  启动游戏冒烟验证..." -ForegroundColor Cyan
        Start-Process -FilePath (Join-Path $GameDir "MinecraftVSZombies2.exe") -WorkingDirectory $GameDir
    }
}
