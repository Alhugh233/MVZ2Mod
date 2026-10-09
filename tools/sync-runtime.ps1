# =====================================================================
# 维护脚本：把测试游戏目录里的 BepInEx 运行环境刷新回仓库 runtime/
#
# 什么时候需要它？
#   runtime/ 是「自包含的编译引用 + 打包源」，但它不入库（含游戏派生的
#   interop 程序集）。新克隆仓库后、或游戏本体更新后，都要用它重建：
#
#     .\tools\sync-runtime.ps1                      # 用默认游戏目录
#     .\tools\sync-runtime.ps1 -GameDir "D:\MVZ2"   # 指定游戏目录
#
# 前置条件：游戏目录里已经有可用的 BepInEx 环境
#   （BepInEx be.788 x86 IL2CPP，且 BepInEx/interop 已按当前游戏生成）。
#   若 interop 缺失/过期：删掉 BepInEx/interop，把 BepInEx.cfg 里的
#   [IL2CPP] UpdateInteropAssemblies 改成 true，启动一次游戏让它重新生成。
# =====================================================================
param(
    [string]$GameDir = ""
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\build-common.ps1"

$repo    = Get-RepoRoot
$runtime = Join-Path $repo 'runtime'

$GameDir = Normalize-GameDir $GameDir 'E:\Game\PVZ\MVZ2\MVZ2 0.7.0 test-11'
if (-not (Test-GameDir $GameDir)) {
    throw ("游戏目录无效（找不到 MinecraftVSZombies2.exe）：" + $GameDir)
}
if (-not (Test-Path (Join-Path $GameDir 'BepInEx'))) {
    throw ("游戏目录里没有 BepInEx：" + $GameDir)
}

Write-Host ("同步 " + $GameDir + "  ->  " + $runtime) -ForegroundColor White

Write-Step "[1/3] 结束游戏进程"
Stop-GameProcess

Write-Step "[2/3] 复制运行时文件"
$items = @(
    'winhttp.dll',
    'doorstop_config.ini',
    '.doorstop_version',
    'dotnet',
    'BepInEx'
)
foreach ($rel in $items) {
    $src = Join-Path $GameDir $rel
    $dst = Join-Path $runtime $rel
    if (-not (Test-Path $src)) { Write-Warn2 ("游戏目录缺少 " + $rel + "，跳过"); continue }
    if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
    Copy-Item $src $dst -Recurse -Force
    Write-Ok $rel
}

Write-Step "[3/3] 清理运行时垃圾"
$bep = Join-Path $runtime 'BepInEx'

# 目录：都是运行期产生的状态，不该进 runtime\（更不该跟着分发包出去）
foreach ($junk in @('cache', '_rp_export', 'replays', 'MVZ2ModManager')) {
    $p = Join-Path $bep $junk
    if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue; Write-Info ("删除 " + $junk + "/") }
}

# 文件：日志 + 自测产物（自测脚本统一往 BepInEx\ 根写 '_' 前缀的 txt/截图）
foreach ($junk in @('LogOutput.log', 'ErrorLog.log')) {
    $p = Join-Path $bep $junk
    if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue; Write-Info ("删除 " + $junk) }
}
$stray = @(Get-ChildItem $bep -File -Filter '_*' -ErrorAction SilentlyContinue)
if ($stray.Count -gt 0) {
    $stray | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Info ("删除自测产物 " + $stray.Count + " 个（_*.txt / _*.png）")
}

# 插件本体：由构建生成
$rp = Join-Path $bep 'plugins'
if (Test-Path $rp) {
    Get-ChildItem $rp -File | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Info "清空 BepInEx/plugins/（插件 DLL 由构建生成，不入 runtime）"
}

# 配置：只留框架自己的 BepInEx.cfg。
# 插件配置是「在这台机器上跑过一次」才生成的用户状态（含路径、开关），
# 跟着包发出去会把别人的设置带给新装的玩家。
$cfgDir = Join-Path $bep 'config'
if (Test-Path $cfgDir) {
    $cfgJunk = @(Get-ChildItem $cfgDir -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'BepInEx.cfg' })
    if ($cfgJunk.Count -gt 0) {
        $cfgJunk | Remove-Item -Force -ErrorAction SilentlyContinue
        Write-Info ("删除插件配置 " + $cfgJunk.Count + " 个（只保留 BepInEx.cfg）")
    }
}

# 校验
$coreCount = (Get-ChildItem (Join-Path $bep 'core') -Filter '*.dll' -ErrorAction SilentlyContinue).Count
$interopCount = (Get-ChildItem (Join-Path $bep 'interop') -Filter '*.dll' -ErrorAction SilentlyContinue).Count
Write-Host ""
Write-Ok ("runtime/BepInEx/core    : " + $coreCount + " dll")
Write-Ok ("runtime/BepInEx/interop : " + $interopCount + " dll")
if ($interopCount -lt 100) {
    Write-Warn2 "interop 程序集偏少，编译可能失败 —— 确认游戏目录的 interop 是完整的。"
}
Write-Host ""
Write-Ok "runtime/ 已刷新，现在可以构建了。"
