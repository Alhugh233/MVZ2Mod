# =====================================================================
# MVZ2 Mod 工作区 —— 共享构建辅助函数
#
# 用法：各 mod 自己的 build.ps1 里
#     . "$PSScriptRoot\..\tools\build-common.ps1"
# 然后调用下面的函数。本文件只定义函数，不产生副作用。
#
# 约定：
#   - 编译引用一律来自仓库自包含的 runtime\（不依赖游戏目录）
#   - GameDir 只是「可选部署目标」：不存在时只构建、不部署
#   - 每个 mod 的资源放在自己目录下的 assets\，
#     其内容直接映射到 <游戏>\MinecraftVSZombies2_Data\StreamingAssets\Mods\<命名空间>\
# =====================================================================

$ErrorActionPreference = 'Stop'

# --- 控制台小工具 ------------------------------------------------------

function Write-Step {
    param([Parameter(Mandatory)][string]$Text)
    Write-Host ""
    Write-Host ("== " + $Text + " ==") -ForegroundColor Cyan
}

function Write-Ok {
    param([Parameter(Mandatory)][string]$Text)
    Write-Host ("  " + $Text) -ForegroundColor Green
}

function Write-Info {
    param([Parameter(Mandatory)][string]$Text)
    Write-Host ("  " + $Text)
}

function Write-Warn2 {
    param([Parameter(Mandatory)][string]$Text)
    Write-Host ("  " + $Text) -ForegroundColor Yellow
}

# --- 环境探测 ----------------------------------------------------------

function Get-RepoRoot {
    # tools\build-common.ps1 -> 仓库根
    return (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Find-Dotnet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @(
            (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe'))) {
        if ($p -and (Test-Path $p)) { return $p }
    }
    throw "找不到 dotnet（已查 PATH 与 Program Files）。请先安装 .NET 6 SDK。"
}

function Get-PluginVersion {
    # 从 Plugin.cs 读 VERSION 常量，保证脚本版本号不会与插件元数据脱节
    param(
        [Parameter(Mandatory)][string]$PluginCs,
        [string]$Fallback = 'dev'
    )
    if (-not (Test-Path $PluginCs)) { return $Fallback }
    $m = Select-String -Path $PluginCs -Pattern 'VERSION\s*=\s*"([^"]+)"' | Select-Object -First 1
    if ($m) { return $m.Matches[0].Groups[1].Value }
    return $Fallback
}

function Normalize-GameDir {
    param([string]$GameDir, [Parameter(Mandatory)][string]$Default)
    if ([string]::IsNullOrWhiteSpace($GameDir)) { $GameDir = $Default }
    return $GameDir.TrimEnd('\', '/')
}

function Test-GameDir {
    param([Parameter(Mandatory)][string]$GameDir)
    return (Test-Path (Join-Path $GameDir 'MinecraftVSZombies2.exe'))
}

# --- 进程管理 ----------------------------------------------------------

function Stop-GameProcess {
    # 游戏运行时会锁住 BepInEx\plugins\*.dll，构建/部署前必须先结束它
    param([switch]$Keep)
    if ($Keep) { Write-Warn2 "保留游戏进程（-KeepGame）"; return }
    $procs = @(Get-Process -Name MinecraftVSZombies2 -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { Write-Info "游戏未在运行"; return }
    foreach ($p in $procs) { try { $p.Kill() } catch { } }
    Write-Info ("已结束 " + $procs.Count + " 个游戏进程（释放插件 DLL 锁）")
    Start-Sleep -Milliseconds 800
}

# --- 构建 --------------------------------------------------------------

function Invoke-DotnetBuild {
    param(
        [Parameter(Mandatory)][string]$Csproj,
        [Parameter(Mandatory)][string]$Dotnet,
        [string]$GameDir = "",
        [switch]$Clean,
        [switch]$SkipBuild
    )
    if ($Clean) {
        $projDir = Split-Path -Parent $Csproj
        foreach ($d in @('bin', 'obj')) {
            $p = Join-Path $projDir $d
            if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
        }
        Write-Info "已清理 bin/ obj/"
    }
    if ($SkipBuild) { Write-Warn2 "跳过构建（-SkipBuild）"; return }

    $args = @('build', $Csproj, '-c', 'Debug')
    if (-not [string]::IsNullOrWhiteSpace($GameDir)) {
        # csproj 把 GameDir 直接与文件名拼接，故统一用正斜杠并以 / 结尾
        $args += ('-p:GameDir=' + ($GameDir -replace '\\', '/') + '/')
    }
    & $Dotnet @args
    if ($LASTEXITCODE -ne 0) { throw ("dotnet build 失败（exit " + $LASTEXITCODE + "）") }
}

function Get-FileHashSafe {
    param([string]$Path)
    if (Test-Path $Path) { return (Get-FileHash $Path -Algorithm SHA256).Hash }
    return ""
}

# --- 部署校验 ----------------------------------------------------------

function Test-DeployedFile {
    # 比较「构建产物」与「游戏目录里的部署副本」是否一致
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string]$Label
    )
    $exists = Test-Path $Target
    $same = $exists -and ((Get-FileHashSafe $Source) -eq (Get-FileHashSafe $Target))
    $mark = if ($same) { 'OK  ' } else { 'STALE' }
    Write-Host ("  [" + $mark + "] " + $Label + "  存在=" + $exists + " 一致=" + $same) `
        -ForegroundColor $(if ($same) { 'Green' } else { 'Yellow' })
    return $same
}

function Test-ModAssetDeploy {
    # 逐个比对 assets\ 与 StreamingAssets\Mods\<nsp>\ 下的文件
    param(
        [Parameter(Mandatory)][string]$AssetsDir,
        [Parameter(Mandatory)][string]$GameDir,
        [Parameter(Mandatory)][string]$Namespace
    )
    if (-not (Test-Path $AssetsDir)) { Write-Warn2 "资源目录不存在：$AssetsDir"; return $false }
    $live = Join-Path $GameDir ("MinecraftVSZombies2_Data\StreamingAssets\Mods\" + $Namespace)
    $allOk = $true
    $count = 0
    foreach ($f in (Get-ChildItem $AssetsDir -Recurse -File)) {
        $rel = $f.FullName.Substring((Resolve-Path $AssetsDir).Path.Length).TrimStart('\')
        $ok = Test-DeployedFile -Source $f.FullName -Target (Join-Path $live $rel) `
            -Label ("assets\" + $rel)
        if (-not $ok) { $allOk = $false }
        $count++
    }
    Write-Info ("共 " + $count + " 个资源文件")
    return $allOk
}

# --- 打包 --------------------------------------------------------------

function New-ModPackage {
    <#
      把 mod 打包成「解压到游戏根目录即可」的 zip。

        -IncludeRuntime  额外带上 BepInEx 运行环境（winhttp / doorstop / dotnet /
                         BepInEx\core|interop|unity-libs|patchers|config）。
                         仅前置核心 mod 需要；其余 mod 的包体只含插件 + 资源。
    #>
    param(
        [Parameter(Mandatory)][string]$PackageName,
        [Parameter(Mandatory)][string]$OutDir,
        [Parameter(Mandatory)][string]$RuntimeDir,
        [Parameter(Mandatory)][string[]]$PluginDlls,
        [string]$AssetsDir = "",
        [string]$Namespace = "",
        [switch]$IncludeRuntime,
        [string]$InstallReadme = ""
    )
    $stage = Join-Path $OutDir $PackageName
    $zip = Join-Path $OutDir ($PackageName + '.zip')

    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    if (Test-Path $zip) { Remove-Item $zip -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    # 1) BepInEx 运行环境（可选）
    if ($IncludeRuntime) {
        $rt = $RuntimeDir
        if (-not (Test-Path $rt)) { throw ("runtime 目录不存在：" + $rt) }
        foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')) {
            $src = Join-Path $rt $f
            if (Test-Path $src) { Copy-Item $src $stage -Force }
            else { Write-Warn2 ("runtime 缺少 " + $f) }
        }
        foreach ($d in @('dotnet', 'BepInEx')) {
            $src = Join-Path $rt $d
            if (-not (Test-Path $src)) { throw ("runtime 缺少目录：" + $d) }
        }
        Copy-Item (Join-Path $rt 'dotnet') $stage -Recurse -Force
        $bepSrc = Join-Path $rt 'BepInEx'
        $bepDst = Join-Path $stage 'BepInEx'
        foreach ($d in @('core', 'interop', 'unity-libs', 'patchers')) {
            $src = Join-Path $bepSrc $d
            if (Test-Path $src) {
                New-Item -ItemType Directory -Force -Path (Join-Path $bepDst $d) | Out-Null
                Copy-Item (Join-Path $src '*') (Join-Path $bepDst $d) -Recurse -Force
            }
        }
        $cfg = Join-Path $bepSrc 'config\BepInEx.cfg'
        if (Test-Path $cfg) {
            New-Item -ItemType Directory -Force -Path (Join-Path $bepDst 'config') | Out-Null
            Copy-Item $cfg (Join-Path $bepDst 'config\BepInEx.cfg') -Force
        }
        # 运行时垃圾一律不带
        Remove-Item (Join-Path $bepDst 'cache') -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $bepDst 'LogOutput.log') -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $bepDst 'ErrorLog.log') -Force -ErrorAction SilentlyContinue
    } else {
        New-Item -ItemType Directory -Force -Path (Join-Path $stage 'BepInEx\plugins') | Out-Null
    }

    # 2) 插件 DLL
    $pluginsDir = Join-Path $stage 'BepInEx\plugins'
    New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
    foreach ($dll in $PluginDlls) {
        if (-not (Test-Path $dll)) { throw ("插件产物不存在：" + $dll) }
        Copy-Item $dll $pluginsDir -Force
    }

    # 3) Mod 资源
    if (-not [string]::IsNullOrEmpty($AssetsDir) -and -not [string]::IsNullOrEmpty($Namespace)) {
        if (-not (Test-Path $AssetsDir)) { throw ("资源目录不存在：" + $AssetsDir) }
        $dst = Join-Path $stage ("MinecraftVSZombies2_Data\StreamingAssets\Mods\" + $Namespace)
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        # 注意：拷「内容」而非目录本身，避免多套一层命名空间目录
        Copy-Item (Join-Path $AssetsDir '*') $dst -Recurse -Force
    }

    # 4) 安装说明（UTF-8 BOM，Windows 记事本友好）
    #    注意：文件名用 char 码构造，避免脚本自身编码影响；整段拼接必须包在括号里，
    #    否则 PowerShell 参数模式会把 "+" 当成一个额外的位置参数。
    if (-not [string]::IsNullOrEmpty($InstallReadme)) {
        $txtName = (-join [char[]](0x5B89, 0x88C5, 0x8BF4, 0x660E)) + '.txt'
        $txt = Join-Path $stage $txtName
        [System.IO.File]::WriteAllText($txt, $InstallReadme, (New-Object System.Text.UTF8Encoding($true)))
    }

    # 5) 压缩
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stage, $zip,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 2)
    $fileCount = (Get-ChildItem $stage -Recurse -File).Count
    Remove-Item $stage -Recurse -Force

    Write-Ok ("包体: " + $zip)
    Write-Info ("大小: " + $sizeMb + " MB   文件数: " + $fileCount)
    return $zip
}
