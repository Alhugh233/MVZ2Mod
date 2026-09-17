# =====================================================================
# 查看测试游戏目录的 BepInEx 日志尾部
#
#     .\tools\show-log.ps1                 # LogOutput.log（托管日志）
#     .\tools\show-log.ps1 -Which error    # ErrorLog.log（原生崩溃的托管栈）
#     .\tools\show-log.ps1 -Which both     # 两个都看
#     .\tools\show-log.ps1 -GameDir "D:\MVZ2" -Tail 120
#
# 游戏目录的解析与构建脚本一致（见 tools\build-common.ps1）：
#   参数 -GameDir > 环境变量 MVZ2_GAME_DIR > Directory.Build.local.props
# 这样任务/脚本里就不会再出现任何人的本机路径。
# =====================================================================
param(
    [ValidateSet('log', 'error', 'both')]
    [string]$Which = 'log',
    [string]$GameDir = "",
    [int]$Tail = 60
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\build-common.ps1"

$repo = Get-RepoRoot
$GameDir = Normalize-GameDir $GameDir (Get-DefaultGameDir -RepoRoot $repo)

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    Write-Warn2 "没有配置游戏目录。请传 -GameDir，或设置环境变量 MVZ2_GAME_DIR，"
    Write-Warn2 "或复制 Directory.Build.local.props.example 为 Directory.Build.local.props。"
    exit 0
}

$targets = switch ($Which) {
    'log' { @('LogOutput.log') }
    'error' { @('ErrorLog.log') }
    'both' { @('LogOutput.log', 'ErrorLog.log') }
}

foreach ($name in $targets) {
    $p = Join-Path $GameDir ('BepInEx\' + $name)
    Write-Host ""
    Write-Host ("== " + $p + " ==") -ForegroundColor Cyan
    if (Test-Path $p) {
        Get-Content $p -Tail $Tail
    } else {
        Write-Warn2 "找不到该文件"
    }
}
