# =====================================================================
# 跑 Replay 的内置自测（不需要任何人工操作）。
#
#   .\tools\run-replay-selftest.ps1 [-GameDir <dir>] [-TimeoutSec 420] [-KeepSelfTest]
#   .\tools\run-replay-selftest.ps1 -PlayFile <回放名> [-TickLimit 1500]   # 只播放并出报告
#
# 它做四件事：
#   1. 把 BepInEx/config/qoznos.mvz2.replay.cfg 的 [SelfTest] Enabled 打开
#      （其余键缺失也无所谓，BepInEx 会用默认值补上）
#   2. 启动游戏（**不驱动鼠标键盘、不抢前台**；游戏自己用 /gotolevel 进关卡）
#   3. 等游戏自己退出（配置里 AutoQuit=true），或超时后结束它
#   4. 打印 <游戏目录>\BepInEx\_replay_selftest.txt
#
# 自测内容与报告格式见 Replay/ReplaySelfTest.cs。
# ASCII only（见 run-selftest.ps1 里的说明）。
# =====================================================================
param(
    [string]$GameDir = "",
    [int]$TimeoutSec = 420,
    [string]$Stage = "mvz2:halloween_2",
    [string]$Area = "mvz2:halloween",
    [string]$PlayFile = "",
    [int]$TickLimit = 0,
    # 热身 / 冷却阶段的 tick 数。默认很小（60 / 90），够验证基本功能；
    # 但要复现「关卡跑一阵之后才出现的发散」就得把它们拉大（例如 900 / 600），
    # 否则录制→回放窗口只有 200 tick 左右，什么都看不出来。
    [int]$WarmupTicks = -1,
    [int]$CooldownTicks = -1,
    # 只把 [SelfTest] 段关掉（不启动游戏）。
    # 用于「自测开关卡在 true、一启动游戏就自动进关卡/自动退出」的情况。
    [switch]$Off,
    [switch]$KeepSelfTest,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility
. "$PSScriptRoot\build-common.ps1"

$repo = Get-RepoRoot
$GameDir = Normalize-GameDir $GameDir (Get-DefaultGameDir -RepoRoot $repo)
if (-not (Test-GameDir $GameDir)) { throw ("游戏目录无效: " + $GameDir) }

$cfg = Join-Path $GameDir 'BepInEx\config\qoznos.mvz2.replay.cfg'
$report = Join-Path $GameDir 'BepInEx\_replay_selftest.txt'
$replayDir = @(Get-ReplayDirs $GameDir) | Select-Object -First 1
if (-not $replayDir) { $replayDir = Join-Path $GameDir 'BepInEx\replays' }

function Set-SelfTest([bool]$on, [string]$stage, [string]$area, [string]$playFile, [int]$tickLimit,
    [int]$warmup, [int]$cooldown) {
    # 把 [SelfTest] 段整个去掉再按需要重写：不用去猜旧文件里写了什么，
    # 剩下的键由 BepInEx 用代码里的默认值补上。
    $lines = @()
    if (Test-Path -LiteralPath $cfg) {
        $lines = @([IO.File]::ReadAllText($cfg) -split "`r?`n")
    }

    $kept = New-Object System.Collections.Generic.List[string]
    $inSec = $false
    foreach ($l in $lines) {
        if ($l -match '^\s*\[') { $inSec = ($l -match '^\s*\[SelfTest\]') }
        if (-not $inSec) { $kept.Add($l) }
    }
    while ($kept.Count -gt 0 -and [string]::IsNullOrWhiteSpace($kept[$kept.Count - 1])) {
        $kept.RemoveAt($kept.Count - 1)
    }

    if ($on) {
        $kept.Add('')
        $kept.Add('[SelfTest]')
        $kept.Add('Enabled = true')
        $kept.Add('Stage = ' + $stage)
        $kept.Add('Area = ' + $area)
        $kept.Add('PlayFile = ' + $playFile)
        # 回放本身可能很长（分钟级），模块内的等待上限要跟着命令行一起放宽，
        # 否则会在“等回放播完超时”那里被误判成失败。
        $kept.Add('PlaybackTimeoutSec = ' + $TimeoutSec)
        $kept.Add('PlayTickLimit = ' + $tickLimit)
        if ($warmup -ge 0) { $kept.Add('WarmupTicks = ' + $warmup) }
        if ($cooldown -ge 0) { $kept.Add('CooldownTicks = ' + $cooldown) }
    }
    [IO.File]::WriteAllText($cfg, (($kept -join "`r`n") + "`r`n"))
}

Write-Host ("Replay self-test") -ForegroundColor White
Write-Info ("game dir : " + $GameDir)
Write-Info ("config   : " + $cfg)

if ($Off) {
    Write-Step "[1/1] 关闭 SelfTest（不启动游戏）"
    # 游戏运行时改配置是白改：BepInEx 退出时会把内存里的配置整个写回去，
    # 所以这里宁可拒绝，也不要“看起来改了其实没改”。
    if (Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue) {
        Write-Warn2 "游戏正在运行 —— 请先**完全退出游戏**再执行（否则配置会被写回去）。"
        exit 1
    }
    Set-SelfTest $false $Stage $Area "" 0 -1 -1
    Write-Info "  [SelfTest] 段已移除（BepInEx 下次启动会用内置默认值补上，即 Enabled=false）"
    Write-Info "  其余段（[General] 等）原样保留"
    Write-Host "自测已关闭，现在可以正常游玩了。" -ForegroundColor Green
    exit 0
}

Write-Step ("[1/4] 打开 SelfTest（stage=" + $Stage + " area=" + $Area + " playFile=" + $PlayFile + " tickLimit=" + $TickLimit + " warmup=" + $WarmupTicks + " cooldown=" + $CooldownTicks + "）")
Set-SelfTest $true $Stage $Area $PlayFile $TickLimit $WarmupTicks $CooldownTicks
Select-String -LiteralPath $cfg -Pattern '^\[|^Enabled|^Stage|^Area|^PlayFile|^PlaybackTimeoutSec|^PlayTickLimit|^WarmupTicks|^CooldownTicks' | ForEach-Object { Write-Info ("  " + $_.Line) }

if ($NoLaunch) { Write-Warn2 "-NoLaunch: 配置已就绪，不启动游戏"; exit 0 }

Write-Step "[2/4] 启动游戏"
Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report -Force }
Start-Process -FilePath (Join-Path $GameDir 'MinecraftVSZombies2.exe') -WorkingDirectory $GameDir
Write-Info "已启动，等待自测完成（最长 $TimeoutSec 秒）"

Write-Step "[3/4] 等待自测结束"
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$ended = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    if (-not (Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue)) { $ended = $true; break }
}
Get-Process MinecraftVSZombies2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Write-Info ("游戏已结束: " + $ended + "（超时未结束则为 False）")

if (-not $KeepSelfTest) {
    Write-Step "[4/4] 关掉 SelfTest"
    Set-SelfTest $false $Stage $Area "" 0 -1 -1
} else {
    Write-Step "[4/4] 保留 SelfTest 开关（-KeepSelfTest）"
}

Write-Host ""
Write-Host "================ 自测报告 ================" -ForegroundColor Cyan
$reportCopy = Join-Path $repo '_replay_report.txt'
if (Test-Path -LiteralPath $report) {
    Copy-Item -LiteralPath $report -Destination $reportCopy -Force
    Get-Content -LiteralPath $report -Encoding UTF8 | ForEach-Object { Write-Host $_ }
} else {
    Write-Warn2 ("没有生成报告: " + $report)
}

Write-Host ""
Write-Host "================ 回放文件 ================" -ForegroundColor Cyan
if (Test-Path -LiteralPath $replayDir) {
    $files = @(Get-ChildItem -LiteralPath $replayDir -File)
    if ($files.Count -eq 0) { Write-Warn2 "(目录为空)" }
    foreach ($f in $files) { Write-Host ("  " + $f.Name + "  " + $f.Length + " bytes") }
} else {
    Write-Warn2 "(还没有 replays 目录)"
}
