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

# --- 环境解析（必须与 Directory.Build.props 的顺序保持一致） -----------
#
# 优先级：脚本参数 > 环境变量 MVZ2_* > Directory.Build.local.props > 仓库内默认
# 这样脚本与 MSBuild 看到的是同一个游戏目录 / runtime，不会出现
# 「编译用的是 A、部署校验查的是 B」这种自欺欺人的结果。

function Get-LocalPropsValue {
    # 读 Directory.Build.local.props（本机私有，不入库）里的 <Name>值</Name>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$Name
    )
    $f = Join-Path $RepoRoot 'Directory.Build.local.props'
    if (-not (Test-Path $f)) { return '' }
    $pattern = '<' + $Name + '>([^<]*)</' + $Name + '>'
    foreach ($line in (Get-Content $f -Encoding UTF8)) {
        if ($line -match '^\s*<!--') { continue }   # 跳过整行注释
        $m = [regex]::Match($line, $pattern)
        if ($m.Success) { return $m.Groups[1].Value.Trim() }
    }
    return ''
}

function Get-DefaultGameDir {
    param([Parameter(Mandatory)][string]$RepoRoot)
    if (-not [string]::IsNullOrWhiteSpace($env:MVZ2_GAME_DIR)) { return $env:MVZ2_GAME_DIR }
    return (Get-LocalPropsValue -RepoRoot $RepoRoot -Name 'GameDir')
}

function Get-DefaultRuntimeDir {
    # 编译引用的来源。顺序与 Directory.Build.props 完全一致：
    #   环境变量 > local props > 仓库 runtime\ > 工作区根本身是 BepInEx 安装
    #   > 游戏目录（游戏目录本身就是一套合法的 BepInEx 环境，装了游戏就不必
    #   再同步一份 runtime\）
    param([Parameter(Mandatory)][string]$RepoRoot)

    if (-not [string]::IsNullOrWhiteSpace($env:MVZ2_RUNTIME_DIR)) {
        return $env:MVZ2_RUNTIME_DIR.TrimEnd('\', '/')
    }
    $local = Get-LocalPropsValue -RepoRoot $RepoRoot -Name 'RuntimeDir'
    if (-not [string]::IsNullOrWhiteSpace($local)) { return $local.TrimEnd('\', '/') }

    $repoRuntime = Join-Path $RepoRoot 'runtime'
    if (Test-Path (Join-Path $repoRuntime 'BepInEx\core\BepInEx.Core.dll')) { return $repoRuntime }

    # 工作区根本身就是一套 BepInEx 安装（DevKit 包 / 仓库直接放进游戏目录）
    if (Test-Path (Join-Path $RepoRoot 'BepInEx\core\BepInEx.Core.dll')) { return $RepoRoot }

    $game = Get-DefaultGameDir -RepoRoot $RepoRoot
    if (-not [string]::IsNullOrWhiteSpace($game) -and
        (Test-Path (Join-Path $game 'BepInEx\core\BepInEx.Core.dll'))) {
        return $game.TrimEnd('\', '/')
    }
    return $repoRuntime
}

function Resolve-DshCoreDll {
    # DSHCore 前置 DLL 的位置：优先源码构建产物，否则用 _refs 里的二进制。
    # 与 Directory.Build.props 的 DshCoreFromSource 判定保持一致。
    param([Parameter(Mandatory)][string]$RepoRoot)

    $srcProj = Join-Path $RepoRoot 'DSHCore\DSHCore.csproj'
    $srcDll = Join-Path $RepoRoot 'DSHCore\bin\Debug\DSHCore.dll'
    $refDll = Join-Path $RepoRoot '_refs\DSHCore\DSHCore.dll'

    if (Test-Path $srcProj) {
        if (Test-Path $srcDll) { return $srcDll }
        if (Test-Path $refDll) {
            Write-Warn2 "DSHCore 源码未构建，暂用 _refs 里的二进制（可能过期）"
            return $refDll
        }
        throw ("找不到 DSHCore.dll。请先构建 DSHCore：" + $srcProj)
    }
    if (Test-Path $refDll) { return $refDll }
    throw ("找不到 DSHCore.dll：" + $refDll + "（这是不含 DSHCore 源码的环境，需要一个 _refs 里的二进制引用）")
}

function Normalize-GameDir {
    # 允许 Default 为空 —— 表示「没有配置游戏目录，只构建不部署」
    param(
        [string]$GameDir,
        [AllowEmptyString()][string]$Default = ''
    )
    if ([string]::IsNullOrWhiteSpace($GameDir)) { $GameDir = $Default }
    if ([string]::IsNullOrWhiteSpace($GameDir)) { return '' }
    return $GameDir.TrimEnd('\', '/')
}

function Test-GameDir {
    param([string]$GameDir)
    if ([string]::IsNullOrWhiteSpace($GameDir)) { return $false }
    return (Test-Path (Join-Path $GameDir 'MinecraftVSZombies2.exe'))
}

# --- 回放目录 ----------------------------------------------------------

function Get-SaveUserDataRoot {
    # 原版存档根：%USERPROFILE%\AppData\LocalLow\<公司>\<游戏名>\userdata
    # 调试版游戏名是 MinecraftVSZombies2Test，正式版是 MinecraftVSZombies2。
    param([string]$GameDir)
    $company = 'Cuerzor'
    $names = @('MinecraftVSZombies2Test', 'MinecraftVSZombies2')
    foreach ($n in $names) {
        $p = Join-Path $env:USERPROFILE ("AppData\LocalLow\" + $company + "\" + $n + "\userdata")
        if (Test-Path -LiteralPath $p) { return $p }
    }
    return (Join-Path $env:USERPROFILE ("AppData\LocalLow\" + $company + "\" + $names[0] + "\userdata"))
}

function Get-ReplayDirs {
    # 回放现在放在存档目录里（按用户分），旧版在 <游戏根>\BepInEx\replays。
    # 两个都返回：读取时当前位置优先，写入/迁移时才需要区分。
    param([string]$GameDir)
    $list = New-Object System.Collections.Generic.List[string]
    $root = Get-SaveUserDataRoot $GameDir
    if (Test-Path -LiteralPath $root) {
        $users = @(Get-ChildItem -LiteralPath $root -Directory -Filter 'user*' -ErrorAction SilentlyContinue |
                   Sort-Object Name)
        for ($i = $users.Count - 1; $i -ge 0; $i--) {
            $r = Join-Path $users[$i].FullName 'replays'
            if (Test-Path -LiteralPath $r) { $list.Add($r) }
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($GameDir)) {
        $legacy = Join-Path $GameDir 'BepInEx\replays'
        if (Test-Path -LiteralPath $legacy) { $list.Add($legacy) }
    }
    return $list
}

function Find-ReplayFile {
    # 在当前位置 + 旧位置里找一个回放（返回 .mrep.json 优先，其次 .mrep）。
    param([string]$GameDir, [string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }
    if ($Name -match '[/\\]' -and (Test-Path -LiteralPath $Name)) { return $Name }
    $cands = @()
    if ($Name.EndsWith('.mrep')) { $cands += $Name }
    elseif ($Name.EndsWith('.json')) { $cands += $Name }
    else { $cands += ($Name + '.mrep.json'); $cands += ($Name + '.mrep') }
    foreach ($d in (Get-ReplayDirs $GameDir)) {
        foreach ($c in $cands) {
            $p = Join-Path $d $c
            if (Test-Path -LiteralPath $p) { return $p }
        }
    }
    return $null
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
        if (-not (Test-Path $rt)) {
            # 注意：PS 5.1 的 "..." 里没有 \" 转义，写 \" 会直接把字符串截断成语法错误。
            # 用 <游戏目录> 这样的尖括号占位，别加引号。
            throw ("要带 BepInEx 运行环境，但 runtime 目录不存在：" + $rt +
                   "`n解决办法（二选一）：`n" +
                   "  1) 准备 runtime\：.\tools\sync-runtime.ps1 -GameDir <游戏目录>`n" +
                   "  2) 不要运行环境，加 -NoRuntime（包体只含插件）")
        }
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
    Write-Info ("大小: " + $sizeMb + " MB   文件数: " + $fileCount +
                "   运行环境: " + $(if ($IncludeRuntime) { '含（解压到游戏根目录即可用）' } else { '不含（需先装 DSHCore 的包）' }))
    return $zip
}

# --- 便携开发环境（DevKit）---------------------------------------------

function Copy-TreeFiltered {
    # 复制一棵目录树，但跳过指定名字的中间目录 / 文件（用于剔除 bin/ obj/）
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Dest,
        [string[]]$ExcludeDirNames = @(),
        [string[]]$ExcludeFileNames = @()
    )
    if (-not (Test-Path $Source)) { return 0 }
    $srcRoot = (Resolve-Path $Source).Path
    $count = 0
    foreach ($f in (Get-ChildItem $srcRoot -Recurse -File -Force)) {
        $rel = $f.FullName.Substring($srcRoot.Length).TrimStart('\', '/')
        $parts = $rel -split '[\\/]'
        $skip = $false
        for ($i = 0; $i -lt ($parts.Length - 1); $i++) {
            if ($ExcludeDirNames -contains $parts[$i]) { $skip = $true; break }
        }
        if (-not $skip -and ($ExcludeFileNames -contains $f.Name)) { $skip = $true }
        if ($skip) { continue }
        $target = Join-Path $Dest $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item $f.FullName $target -Force
        $count++
    }
    return $count
}

function New-DevKitPackage {
    <#
      打「不附带 DSHCore 源码」的便携开发环境包（DevKit）。

      产物：dist\<PackageName>.zip，解压后即为一个自洽的工作区根：

        <DevKitRoot>\
          Directory.Build.props               共享编译环境解析
          Directory.Build.local.props.example 本机配置模板（玩家自己复制改名）
          MVZ2Mod.code-workspace  readme.md  .gitignore  build.ps1
          .vscode\tasks.json
          tools\                              构建辅助 + 日志查看
          _refs\DSHCore\DSHCore.dll           编译引用（← 没有 DSHCore 源码）
          runtime\                            编译引用：BepInEx（可选）
          DSHCore\                            仅文档（AI_GUIDE.md 等）
          LaboratoryMod\  ModularCurse\       范例 mod 源码

      其中的 mod 工程会因为找不到 DSHCore.csproj 而自动改用
      _refs\DSHCore\DSHCore.dll（见 Directory.Build.props 里的
      DshCoreFromSource 自动降级）。
    #>
    param(
        [Parameter(Mandatory)][string]$PackageName,
        [Parameter(Mandatory)][string]$OutDir,
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$RefDir,
        [string]$RuntimeDir = "",
        [switch]$NoRuntime,
        [string[]]$ModDirs = @(),
        [string]$ReadmeText = ""
    )
    $stage = Join-Path $OutDir $PackageName
    $zip = Join-Path $OutDir ($PackageName + '.zip')

    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    if (Test-Path $zip) { Remove-Item $zip -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    # 1) 工作区根文件
    #    注意：**不**带 Directory.Build.local.props（那是每台机器自己的事）
    foreach ($f in @('Directory.Build.props', 'Directory.Build.local.props.example',
                     'MVZ2Mod.code-workspace', 'readme.md', '.gitignore', 'build.ps1')) {
        $src = Join-Path $RepoRoot $f
        if (Test-Path $src) { Copy-Item $src $stage -Force }
        else { Write-Warn2 ("工作区缺少 " + $f + "，DevKit 里将没有它") }
    }

    # 2) .vscode\ 与 tools\（只带运行需要的，不带 Il2CppDumper 等大件）
    $vscodeSrc = Join-Path $RepoRoot '.vscode'
    if (Test-Path $vscodeSrc) {
        New-Item -ItemType Directory -Force -Path (Join-Path $stage '.vscode') | Out-Null
        Copy-Item (Join-Path $vscodeSrc '*.json') (Join-Path $stage '.vscode') -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'tools') | Out-Null
    foreach ($f in @('build-common.ps1', 'sync-runtime.ps1', 'show-log.ps1')) {
        $src = Join-Path (Join-Path $RepoRoot 'tools') $f
        if (Test-Path $src) { Copy-Item $src (Join-Path $stage 'tools') -Force }
    }

    # 3) _refs\DSHCore\（编译前置 —— DevKit 存在的理由）
    if (-not (Test-Path (Join-Path $RefDir 'DSHCore.dll'))) {
        throw ("缺少 DSHCore 二进制引用：" + (Join-Path $RefDir 'DSHCore.dll') +
               "（先构建 DSHCore，它会自动同步 _refs）")
    }
    $refOut = Join-Path $stage '_refs\DSHCore'
    New-Item -ItemType Directory -Force -Path $refOut | Out-Null
    Copy-Item (Join-Path $RefDir '*') $refOut -Force

    # 4) runtime\（可选）—— 不带的话，使用者靠自己的游戏目录当编译引用
    if ($NoRuntime) {
        Write-Warn2 "按 -NoRuntime 跳过 runtime\（使用者需要设置 MVZ2_GAME_DIR）"
    } elseif ([string]::IsNullOrWhiteSpace($RuntimeDir) -or
              -not (Test-Path (Join-Path $RuntimeDir 'BepInEx\core\BepInEx.Core.dll'))) {
        Write-Warn2 ("runtime 无效（" + $RuntimeDir + "），跳过")
    } else {
        foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')) {
            $src = Join-Path $RuntimeDir $f
            if (Test-Path $src) { Copy-Item $src $stage -Force }
        }
        foreach ($d in @('dotnet', 'BepInEx')) {
            if (-not (Test-Path (Join-Path $RuntimeDir $d))) { throw ("runtime 缺少目录：" + $d) }
        }
        Copy-Item (Join-Path $RuntimeDir 'dotnet') $stage -Recurse -Force
        $bepSrc = Join-Path $RuntimeDir 'BepInEx'
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
        # 运行期垃圾一律不带
        foreach ($junk in @('cache', 'LogOutput.log', 'ErrorLog.log', 'plugins')) {
            Remove-Item (Join-Path $bepDst $junk) -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # 5) 各 mod 的源码（剔掉 bin\ obj\）—— 每个 mod 目录自己带
    #    Directory.Build.props 转发文件，所以它们单独拎出去也能构建。
    foreach ($modDir in $ModDirs) {
        $src = Join-Path $RepoRoot $modDir
        $n = Copy-TreeFiltered -Source $src -Dest (Join-Path $stage $modDir) `
            -ExcludeDirNames @('bin', 'obj')
        if ($n -eq 0) { Write-Warn2 ("mod 目录为空或不存在：" + $modDir) }
        else { Write-Info ("  " + $modDir + " : " + $n + " 个文件") }
    }

    # 6) DSHCore\ 只放文档（**不放源码** —— 这正是本包的意义）
    $docOut = Join-Path $stage 'DSHCore'
    New-Item -ItemType Directory -Force -Path $docOut | Out-Null
    foreach ($f in (Get-ChildItem (Join-Path $RepoRoot 'DSHCore') -File -ErrorAction SilentlyContinue)) {
        if ($f.Name -eq 'devkit-readme.txt') { continue }   # 已作为根目录的 README-DevKit.txt
        if ($f.Name -like '*.md' -or $f.Name -like '*.txt') {
            Copy-Item $f.FullName $docOut -Force
        }
    }

    # 7) 开发者说明（UTF-8 BOM，记事本友好）
    if (-not [string]::IsNullOrEmpty($ReadmeText)) {
        $txt = Join-Path $stage 'README-DevKit.txt'
        [System.IO.File]::WriteAllText($txt, $ReadmeText, (New-Object System.Text.UTF8Encoding($true)))
    }

    # 8) 压缩
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stage, $zip,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 2)
    $fileCount = (Get-ChildItem $stage -Recurse -File).Count
    Remove-Item $stage -Recurse -Force

    Write-Ok ("DevKit: " + $zip)
    Write-Info ("大小: " + $sizeMb + " MB   文件数: " + $fileCount)
    return $zip
}

# --- MVZ2 模组包（MVZ2ModManager 的 .mvz2pack）-------------------------

function New-Mvz2Pack {
    <#
      把「本仓库这套 mod」打成一个 MVZ2ModManager 能直接导入的 .mvz2pack。

      包内布局（严格对着 MVZ2ModManager\Core\ModpackManager.cs 写）：

        mvz2pack.json                 清单（文件名固定，管理器只认它）
        BepInEx/plugins/*.dll         插件本体
        StreamingAssets/Mods/<nsp>/…  内容模组的资源（按命名空间分目录）

      三条硬约束 —— 违反了都表现为「导入后什么都没发生，而且一句错都不报」：

        1. 条目名一律用**正斜杠**。管理器靠 entry.FullName.StartsWith("BepInEx/plugins/")
           判断「这份包覆盖了哪些根」，一旦写成反斜杠就一条都匹配不上：于是既不清理
           也不解压，静默失败。（这也是这里手拼 ZipArchive 而不用 CreateFromDirectory
           的原因 —— .NET Framework 那条路在 Windows 上会写出反斜杠。）
        2. 清单里每个字段的名字/大小写都要对得上（System.Text.Json 默认按名字精确匹配）。
        3. 清单必须是**无 BOM 的 UTF-8**（带 BOM 时 JsonSerializer.Deserialize(Stream) 解析失败）。

      与 New-ModPackage 的分工：那个打的是「解压到游戏根目录」的自包含 zip（含 BepInEx
      运行环境），给手工安装用；这个打的是**只含模组本体**的 .mvz2pack，给管理器用 ——
      运行环境由管理器 / 游戏自己负责。

      ⚠ 还原这份包会先清空 BepInEx\plugins\ 与 StreamingAssets\Mods\ 两个根
      （这是格式本身的语义：一份包 = 一套 setup 的完整快照），所以别往那两个目录塞私货。
    #>
    param(
        [Parameter(Mandatory)][string]$OutDir,

        # 每项：@{ Name='DSHCore'; Dll='<插件 dll 绝对路径>'; AssetsDir='<可空>'; Namespace='<可空>' }
        [Parameter(Mandatory)][hashtable[]]$Items,

        [string]$PackageName = 'MVZ2Mods',
        [string]$DisplayName = '',
        [string]$Author = '',
        [string]$GameSlug = 'mvz2'
    )

    if ([string]::IsNullOrWhiteSpace($DisplayName)) { $DisplayName = $PackageName }

    # ---------- 1. 收集文件 → 包内条目名 ----------

    $entries = New-Object System.Collections.Generic.List[hashtable]
    $modNames = New-Object System.Collections.Generic.List[string]
    $assetNotes = New-Object System.Collections.Generic.List[string]

    foreach ($it in $Items) {
        $name = [string]$it['Name']
        $dll = [string]$it['Dll']
        $ns = [string]$it['Namespace']
        $assets = [string]$it['AssetsDir']

        if ([string]::IsNullOrWhiteSpace($name)) { throw "New-Mvz2Pack：有一项没写 Name。" }
        if ([string]::IsNullOrWhiteSpace($dll)) { throw ("New-Mvz2Pack：" + $name + " 没写 Dll。") }
        if (-not (Test-Path -LiteralPath $dll)) {
            throw ("插件产物不存在：" + $dll + "`n先在仓库根构建：.\build.ps1")
        }

        $dllAbs = (Resolve-Path -LiteralPath $dll).Path
        $entries.Add(@{ Source = $dllAbs; Entry = ('BepInEx/plugins/' + (Split-Path -Leaf $dllAbs)) })
        $modNames.Add($name)

        if ([string]::IsNullOrWhiteSpace($ns)) { continue }

        if ([string]::IsNullOrWhiteSpace($assets) -or -not (Test-Path -LiteralPath $assets)) {
            Write-Warn2 ($name + "：声明了命名空间 " + $ns + " 但没有资源目录（" + $assets + "），只打插件")
            continue
        }

        $assetsRoot = (Resolve-Path -LiteralPath $assets).Path
        $files = @(Get-ChildItem -LiteralPath $assetsRoot -Recurse -File -Force)
        if ($files.Count -eq 0) {
            Write-Warn2 ($name + "：资源目录是空的（" + $assetsRoot + "），跳过")
            continue
        }
        foreach ($f in $files) {
            $rel = $f.FullName.Substring($assetsRoot.Length).TrimStart('\', '/').Replace('\', '/')
            $entries.Add(@{ Source = $f.FullName; Entry = ('StreamingAssets/Mods/' + $ns + '/' + $rel) })
        }
        $assetNotes.Add($ns + "（" + $files.Count + " 个文件）")
    }

    if ($entries.Count -eq 0) { throw "New-Mvz2Pack：一个文件都没收进来，不打空包。" }

    # 条目不许重名：两个 mod 产出同一个路径的话，解压时后者会静默盖掉前者
    $seen = @{}
    foreach ($e in $entries) {
        if ($seen.ContainsKey($e['Entry'])) {
            throw ("包内条目重名：" + $e['Entry'] + "（两个 mod 产出了同一个路径）")
        }
        $seen[$e['Entry']] = $true
    }

    # ---------- 2. 清单 ----------
    #
    # 手拼 JSON，不用 ConvertTo-Json：PS 5.1 对「单元素数组」会退化成裸字符串，
    # 而 Mods 一旦不是数组，管理器那边反序列化直接抛异常 → 整份包被判成「清单读不出来」。
    # 字符串值逐个用 ConvertTo-Json 转义（引号 / 反斜杠 / 控制字符都照顾到了）。

    $sortedMods = @($modNames | Sort-Object -Unique)
    $createdUtc = [DateTime]::UtcNow.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)

    $nameLit   = ($DisplayName | ConvertTo-Json).Trim()
    $authorLit = ($Author | ConvertTo-Json).Trim()
    $slugLit   = ($GameSlug | ConvertTo-Json).Trim()
    $modsBlock = (@($sortedMods | ForEach-Object { '    ' + ($_ | ConvertTo-Json).Trim() }) -join (",`n"))

    $json = @"
{
  "FormatVersion": 1,
  "Name": $nameLit,
  "Author": $authorLit,
  "GameSlug": $slugLit,
  "CreatedUtc": "$createdUtc",
  "Mods": [
$modsBlock
  ]
}
"@

    # ---------- 3. 写包 ----------

    if (-not (Test-Path -LiteralPath $OutDir)) {
        New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    }
    $packPath = Join-Path $OutDir ($PackageName + '.mvz2pack')
    if (Test-Path -LiteralPath $packPath) { Remove-Item -LiteralPath $packPath -Force }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $level = [System.IO.Compression.CompressionLevel]::Optimal

    $zip = [System.IO.Compression.ZipFile]::Open($packPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        # 清单先写（顺序无所谓，只是看着顺眼）
        $manifest = $zip.CreateEntry('mvz2pack.json', $level)
        $stream = $manifest.Open()
        try {
            $bytes = $utf8NoBom.GetBytes($json)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush()
        } finally {
            $stream.Dispose()
        }

        foreach ($e in $entries) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $e['Source'], $e['Entry'], $level)
        }
    } finally {
        $zip.Dispose()
    }

    # ---------- 4. 自检（读回来验一遍，防的是「静默失败」这条）----------
    #
    # 管理器读包的每一步都在这里复刻一遍：清单在第一层、字段名对得上、Mods 是数组、
    # 每个条目都落在它认识的某个前缀下。任何一条不过就直接 throw —— 宁可构建失败，
    # 也不要交出去一份"导入后什么都没发生"的包。

    $problems = New-Object System.Collections.Generic.List[string]
    $itemCount = 0
    $probe = [System.IO.Compression.ZipFile]::OpenRead($packPath)
    try {
        $manifestEntry = $probe.GetEntry('mvz2pack.json')
        if ($null -eq $manifestEntry) {
            $problems.Add("包里找不到 mvz2pack.json")
        } else {
            # 先按字节读出来：BOM 检查必须在解码之前做 —— StreamReader 会偷偷跳过 BOM，
            # 用它会「看起来一切正常」但管理器那边照样解析失败。
            $buffer = New-Object System.IO.MemoryStream
            $entryStream = $manifestEntry.Open()
            try { $entryStream.CopyTo($buffer) } finally { $entryStream.Dispose() }
            $mbytes = $buffer.ToArray()
            $buffer.Dispose()

            if ($mbytes.Length -ge 3 -and $mbytes[0] -eq 0xEF -and $mbytes[1] -eq 0xBB -and $mbytes[2] -eq 0xBF) {
                $problems.Add("清单带了 UTF-8 BOM（管理器会读不出来）")
            }

            $readBack = $utf8NoBom.GetString($mbytes)

            $obj = $null
            try { $obj = $readBack | ConvertFrom-Json } catch { $problems.Add("清单不是合法 JSON：" + $_.Exception.Message) }

            if ($null -ne $obj) {
                if ($obj.FormatVersion -ne 1) { $problems.Add("FormatVersion 应为 1，实际 " + $obj.FormatVersion) }
                if ([string]::IsNullOrWhiteSpace([string]$obj.Name)) { $problems.Add("Name 是空的") }
                if ([string]$obj.GameSlug -ne $GameSlug) { $problems.Add("GameSlug 应为 " + $GameSlug) }
                if (-not $readBack.Contains('"Mods": [')) { $problems.Add("Mods 不是数组（单元素时最容易写错）") }
                if (@($obj.Mods).Count -ne $sortedMods.Count) {
                    $problems.Add("Mods 条数 " + @($obj.Mods).Count + " ≠ " + $sortedMods.Count)
                }
            }
        }

        foreach ($entry in $probe.Entries) {
            if ($entry.Name.Length -eq 0) { continue }
            if ($entry.FullName -eq 'mvz2pack.json') { continue }
            $itemCount++
            if ($entry.FullName.IndexOf('\') -ge 0) {
                $problems.Add("条目用了反斜杠：" + $entry.FullName)
            } elseif (-not ($entry.FullName.StartsWith('BepInEx/plugins/') -or
                            $entry.FullName.StartsWith('StreamingAssets/Mods/'))) {
                $problems.Add("条目不在管理器认识的根里：" + $entry.FullName)
            }
        }

        if ($itemCount -ne $entries.Count) {
            $problems.Add("条目数 " + $itemCount + " ≠ " + $entries.Count)
        }
    } finally {
        $probe.Dispose()
    }

    if ($problems.Count -gt 0) {
        Remove-Item -LiteralPath $packPath -Force -ErrorAction SilentlyContinue
        throw ("模组包自检没过（已删掉这个包）：`n  - " + ($problems -join "`n  - "))
    }

    # ---------- 5. 报告 ----------

    $sizeKb = [math]::Round((Get-Item -LiteralPath $packPath).Length / 1KB, 1)
    Write-Ok ("模组包: " + $packPath)
    Write-Info ("大小: " + $sizeKb + " KB   条目: " + ($itemCount + 1) + "（含清单）   自检: 通过")
    Write-Info ("插件 " + $sortedMods.Count + " 个: " + ($sortedMods -join '、'))
    if ($assetNotes.Count -gt 0) {
        Write-Info ("资源 " + $assetNotes.Count + " 套: " + ($assetNotes -join '、'))
    }
    Write-Info "覆盖的根: BepInEx/plugins/ 、StreamingAssets/Mods/（还原时会先清空这两处）"
    Write-Info "导入: MVZ2ModManager 双击这个文件，或丢进「模组包」页"
    return $packPath
}
