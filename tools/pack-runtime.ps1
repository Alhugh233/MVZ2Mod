# =====================================================================
# MVZ2 ModRuntime 打包脚本
#
# 打一个「开箱即用」的整合包：BepInEx 运行环境 + MVZ2ModManager。
# 解压到**游戏根目录**后，玩家直接双击 ModManager 管理模组，游戏侧
# 的注入环境也已经就位 —— 不需要再单独装前置包、也不用装 .NET。
#
#   .\tools\pack-runtime.ps1
#       默认：BepInEx 环境 + 自包含版 ModManager（整包约 106MB，免装 .NET）
#
#   .\tools\pack-runtime.ps1 -ModManager Framework
#       轻量：ModManager 用框架依赖版（exe 约 2MB、整包约 45MB），要求
#       目标机器有 .NET 8 Desktop Runtime。安装说明里会写清这条前置。
#
#   .\tools\pack-runtime.ps1 -WithMods
#       额外带上各 mod 已构建的插件 DLL 与内容资源（省得玩家再一个个装）
#
#   .\tools\pack-runtime.ps1 -ModManager Existing
#       不再编译，直接复用已有的 MVZ2ModManager*.exe（默认在 runtime\ 里找）
#
#   .\tools\pack-runtime.ps1 -NoDotnet
#       不打 dotnet\（目标机器自己会装 BepInEx，只需换插件时用）
#
# 产物：dist\MVZ2-ModRuntime_v<版本>_<full|lite>.zip
#
# ── 两套 .NET 别搞混 ─────────────────────────────────────────────────
#   ① 游戏侧：dotnet\ 里的 **.NET 6 x86 CoreCLR**，是 BepInEx 6 IL2CPP
#      的托管宿主。跟着包走，玩家**不用装**任何 .NET。
#   ② ModManager 侧：net8.0-windows 的桌面程序。
#      - 自包含发布 → 包内自带 .NET 8，玩家不用装；
#      - 框架依赖发布 → 玩家必须先装 .NET 8 Desktop Runtime。
#   本脚本默认选①那套自包含的，就是为了做到「一份包什么前置都不要」。
# =====================================================================
[CmdletBinding()]
param(
    # 输出目录（默认 <仓库>\dist）
    [string]$OutDir = "",

    # ModManager 的打包方式
    #   SelfContained 自包含单文件，免装 .NET（默认，exe 约 150MB）
    #   Framework     框架依赖单文件，需 .NET 8 Desktop Runtime（exe 约 2MB）
    #   Existing      复用已有 exe，不编译（配合 -ExistingExe）
    #   Skip          完全不带 ModManager，只打 BepInEx 环境
    [ValidateSet('SelfContained', 'Framework', 'Existing', 'Skip')]
    [string]$ModManager = 'SelfContained',

    # -ModManager Existing 时使用的 exe；留空则在 runtime\ 里自动找最新的
    [string]$ExistingExe = "",

    # 编译引用 / 打包来源（默认走 build-common 的解析顺序）
    [string]$RuntimeDir = "",

    # 发布配置（ModManager 编译用）。发布场景默认 Release
    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Release',

    # 不带 dotnet\（0.7.0 的 BepInEx 宿主运行时）
    [switch]$NoDotnet,

    # 额外带上各 mod 已构建的插件 DLL 与内容资源
    [switch]$WithMods,

    # 打包前清掉 ModManager 的 bin\ obj\
    [switch]$Clean,

    # 包名前缀
    [string]$Name = 'MVZ2-ModRuntime',

    # 可选：打包后顺带把包解出来做一次产物校验
    [switch]$Verify,

    # 可选：打包后打开 dist 目录
    [switch]$Open
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\build-common.ps1"

# --- 本脚本专用的辅助函数 ----------------------------------------------

function Set-IniValue {
    <#
      在 INI 文本行数组里改一个「指定 section 下的 key」的值。
      只改第一个匹配到的赋值行，其余（注释、别的 section）原样保留。
      找不到该 key 时返回原数组 —— 不硬塞新配置，BepInEx 自己有默认值。

      ⚠ 注意 $Lines 不能用 [Parameter(Mandatory)] 修饰。
        PS 5.1 在 Mandatory + [string[]] 的组合下会把「数组里含空字符串」
        当成绑定失败（Cannot bind ... because it is an empty string），
        而 INI 文件里处处是空行 —— 一定会踩到。这里改为手动校验。
    #>
    param(
        [AllowEmptyCollection()][string[]]$Lines,
        [Parameter(Mandatory)][string]$Section,
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Value
    )
    if ($null -eq $Lines) { throw "Set-IniValue: Lines 不能为 null。" }
    $out = New-Object System.Collections.Generic.List[string]
    $inSection = $false
    $done = $false
    foreach ($line in $Lines) {
        if ($line -match '^\s*\[(.+?)\]\s*$') {
            $inSection = ($Matches[1].Trim() -eq $Section)
            $out.Add($line)
            continue
        }
        if (-not $done -and $inSection -and $line -match ('^\s*' + [regex]::Escape($Key) + '\s*=')) {
            $out.Add($Key + ' = ' + $Value)
            $done = $true
            continue
        }
        $out.Add($line)
    }
    if (-not $done) { Write-Warn2 ("BepInEx.cfg 里没找到 [" + $Section + "] " + $Key + "，沿用默认值") }
    return $out.ToArray()
}

function New-DistributionBepInExConfig {
    <#
      把 runtime 里的 BepInEx.cfg 加工成「适合发给玩家」的版本：

        [Logging.Console] Enabled = false        不弹 cmd 黑窗
        [IL2CPP] UpdateInteropAssemblies = true  游戏更新后自动重建 interop

      第二条为什么必须是 true：
      interop 程序集绑定在游戏的 metadata token 上。游戏哪怕只改一行 C#，
      token 也可能重排，于是旧 interop 会在任意调用点引发原生 AV
      （0xC0000005）—— 表现就是「更新游戏后大面积崩溃」，而且崩在哪
      完全随机，极难排查。开着自动重建，BepInEx 一检测到游戏变了就重新
      生成，正好把这类崩溃挡在门外。（本仓库用的 BepInEx 能处理 0.7.0 的
      metadata v31，实测游戏正常运行；把它关掉反而是在埋「换版本就崩」的雷。）

      这条与 runtime\ 里的原值一致（那里本来就是 true）。这里显式写一遍，
      只是防止某台机器上被手工改成 false 之后被打进包里。
    #>
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Target
    )
    if (-not (Test-Path -LiteralPath $Source)) {
        throw ("缺少 BepInEx.cfg 模板：" + $Source +
               "`n它是运行环境的一部分，runtime 目录不完整。")
    }
    $lines = [System.IO.File]::ReadAllLines($Source)
    $lines = Set-IniValue -Lines $lines -Section 'Logging.Console' -Key 'Enabled' -Value 'false'
    $lines = Set-IniValue -Lines $lines -Section 'IL2CPP' -Key 'UpdateInteropAssemblies' -Value 'true'

    $dir = Split-Path -Parent $Target
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    # 无 BOM 的 UTF-8：BepInEx 的 IniParser 对 BOM 处理不稳妥
    [System.IO.File]::WriteAllLines($Target, $lines, (New-Object System.Text.UTF8Encoding($false)))
}

function New-ZipFromDirectory {
    <#
      把目录压成 zip，**条目名一律用正斜杠**。

      为什么不用 ZipFile.CreateFromDirectory？
      PowerShell 5.1 跑在 .NET Framework 上，那条路径会把 Windows 的路径
      分隔符原样写进 zip（即 BepInEx\core\x.dll）。Windows 资源管理器
      能容忍，但 unzip / 7-Zip 的命令行、跨平台解压工具会把它当成一个
      文件名里带反斜杠的怪条目 —— 解出来就是一堆平铺的错误文件。
      仓库里的 New-Mvz2Pack 也踩过同一个坑，这里手动拼更稳。

      另外：空的 BepInEx\plugins\ 不会被 -File 遍历到，所以额外补一条
      目录条目，保证解压后这个目录就存在。
    #>
    param(
        [Parameter(Mandatory)][string]$SourceDir,
        [Parameter(Mandatory)][string]$ZipPath,
        [string[]]$EnsureDirs = @()
    )
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $srcRoot = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\', '/')
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }

    $level = [System.IO.Compression.CompressionLevel]::Optimal

    # 用 ZipFile.Open + ZipFileExtensions.CreateEntryFromFile（与仓库
    # New-Mvz2Pack 同一套写法）。不要自己 new ZipArchive 再拷流 ——
    # 那样在 PS 5.1 上会直接让进程硬崩溃（不是抛异常，catch 都抓不到）。
    $archive = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        # Create 模式下 $archive.Entries 不可查询（GetEntry/Entries 都会抛
        # "无法访问处于创建模式的条目"），所以自己记一份已写入的相对路径。
        $written = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($f in (Get-ChildItem -LiteralPath $srcRoot -Recurse -File -Force)) {
            $rel = $f.FullName.Substring($srcRoot.Length).TrimStart('\', '/').Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $f.FullName, $rel, $level)
            [void]$written.Add($rel)
        }
        # 补空目录条目：目录下没有任何文件时，解压后那个目录就不会存在，
        # 而 BepInEx\plugins\ 之类的目录是玩家要往里放东西的。
        foreach ($d in $EnsureDirs) {
            $prefix = $d.TrimEnd('/', '\') + '/'
            $occupied = $false
            foreach ($w in $written) {
                if ($w.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { $occupied = $true; break }
            }
            if (-not $occupied) { [void]$archive.CreateEntry($prefix) }
        }
    } finally {
        $archive.Dispose()
    }
}

$repo = Get-RepoRoot
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $repo 'dist' }

if ([string]::IsNullOrWhiteSpace($RuntimeDir)) {
    $RuntimeDir = Get-DefaultRuntimeDir -RepoRoot $repo
}
$RuntimeDir = $RuntimeDir.TrimEnd('\', '/')

$mmDir  = Join-Path $repo 'MVZ2ModManager'
$mmProj = Join-Path $mmDir 'MVZ2ModManager.csproj'

Write-Host "MVZ2 ModRuntime 打包" -ForegroundColor White
Write-Info ("仓库    : " + $repo)
Write-Info ("runtime : " + $RuntimeDir)
Write-Info ("ModMgr  : " + $ModManager)

# ---------- 0. 前置校验 -------------------------------------------------
# 这里只做「能不能继续」的硬校验，具体缺什么要能一眼看出怎么补。
Write-Step "[1/6] 校验运行环境"

if (-not (Test-Path (Join-Path $RuntimeDir 'BepInEx\core\BepInEx.Core.dll'))) {
    throw ("runtime 里没有 BepInEx 环境：" + $RuntimeDir + "`n" +
           "补法：.\tools\sync-runtime.ps1 -GameDir <游戏目录>（先把游戏目录的 BepInEx 同步回来）")
}
Write-Ok "BepInEx.Core.dll 就位"

foreach ($f in @('winhttp.dll', 'doorstop_config.ini')) {
    if (-not (Test-Path (Join-Path $RuntimeDir $f))) {
        # winhttp.dll 是注入入口，缺了整套 BepInEx 都不会启动 —— 必须中断
        throw ("runtime 缺少关键文件：" + $f + "（在 " + $RuntimeDir + " 下）")
    }
}
Write-Ok "门挡三件套就位"

if (-not $NoDotnet) {
    if (-not (Test-Path (Join-Path $RuntimeDir 'dotnet\coreclr.dll'))) {
        throw ("runtime 缺少 dotnet\ 运行时（找不到 dotnet\coreclr.dll）：" + $RuntimeDir + "`n" +
               "若目标机器自带 BepInEx 环境，可加 -NoDotnet 跳过")
    }
    Write-Ok "dotnet\ (.NET 6 x86 宿主) 就位"
} else {
    Write-Warn2 "按 -NoDotnet 跳过 dotnet\ —— 玩家必须已经有 BepInEx 环境"
}

# ---------- 1. 版本号 ---------------------------------------------------
# 包名里的版本取 ModManager 的 <Version>：它是这套整合包里唯一的
# 版本化桌面程序，比插件版本更能代表「这一次发布」。
# dotnet 只在真的要编译 ModManager 时才必需，所以这里不硬性要求 ——
# -ModManager Skip / Existing 的场合不该因为没装 SDK 就打包失败。
$dotnet = $null
try { $dotnet = Find-Dotnet } catch { $dotnet = $null }
if (-not $dotnet) { Write-Warn2 "找不到 dotnet（ModManager 现场编译需要它；用 -ModManager Existing / Skip 可绕过）" }

$mmVersion = '0.0.0'
if ($dotnet -and (Test-Path $mmProj)) {
    $v = (& $dotnet msbuild $mmProj -getProperty:Version -nologo 2>$null | Select-Object -First 1)
    if (-not [string]::IsNullOrWhiteSpace($v)) { $mmVersion = $v.Trim() }
}
Write-Info ("ModManager 版本: " + $mmVersion)

# full = 自带 .NET（免装），lite = 框架依赖（需 .NET 8），
# reuse = 复用已有 exe（性质未知，说明里得留活口）
# runtime-only = 完全不带 ModManager
$variant = 'runtime-only'
switch ($ModManager) {
    'SelfContained' { $variant = 'full' }
    'Framework'     { $variant = 'lite' }
    'Existing'      { $variant = 'reuse' }
    'Skip'          { $variant = 'runtime-only' }
}

$packageName = $Name + '_v' + $mmVersion + '_' + $variant
$stage = Join-Path $OutDir $packageName
$zip   = Join-Path $OutDir ($packageName + '.zip')

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# ---------- 2. BepInEx 运行环境 -----------------------------------------
Write-Step "[2/6] 收集 BepInEx 运行环境"

# 游戏根目录该有的三件套
foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')) {
    $src = Join-Path $RuntimeDir $f
    if (Test-Path $src) { Copy-Item $src $stage -Force; Write-Info $f }
    else { Write-Warn2 ("runtime 缺少 " + $f + "（游戏可能仍能启动，但不保证）") }
}

if (-not $NoDotnet) {
    Copy-Item (Join-Path $RuntimeDir 'dotnet') $stage -Recurse -Force
    Write-Info "dotnet\"
}

# BepInEx 只带运行必需的子目录；cache / 日志 / plugins 一律不带
$bepSrc = Join-Path $RuntimeDir 'BepInEx'
$bepDst = Join-Path $stage 'BepInEx'
foreach ($d in @('core', 'interop', 'unity-libs', 'patchers')) {
    $src = Join-Path $bepSrc $d
    if (-not (Test-Path $src)) { Write-Warn2 ("BepInEx 缺少 " + $d + "\"); continue }
    New-Item -ItemType Directory -Force -Path (Join-Path $bepDst $d) | Out-Null
    Copy-Item (Join-Path $src '*') (Join-Path $bepDst $d) -Recurse -Force
    Write-Info ("BepInEx\" + $d + "\")
}

$cfg = Join-Path $bepSrc 'config\BepInEx.cfg'
if (Test-Path $cfg) {
    # 关控制台黑窗；interop 保持自动重建（见函数注释：这是防「换版本就崩」的关键）
    New-DistributionBepInExConfig -Source $cfg -Target (Join-Path $bepDst 'config\BepInEx.cfg')
    Write-Info "BepInEx\config\BepInEx.cfg（已关控制台、已开 interop 自动重建）"
} else {
    Write-Warn2 "runtime 里没有 BepInEx\config\BepInEx.cfg —— 游戏会用默认配置（会弹控制台）"
}

# plugins 目录必须存在：BepInEx 对缺失的 plugins 会报警告，
# 而且玩家第一次装 mod 时就该有地方放。
New-Item -ItemType Directory -Force -Path (Join-Path $bepDst 'plugins') | Out-Null

# ---------- 3. 可选：各 mod 的插件与资源 --------------------------------
if ($WithMods) {
    Write-Step "[3/6] 收集 mod 插件与资源"

    # 与仓库根 build.ps1 的 $all 表保持一致（Key 只用于日志）。
    # Dll 相对于各 mod 目录，Assets 相对同样目录。
    $mods = @(
        @{ Dir = 'DSHCore';       Dll = 'bin\Debug\DSHCore.dll';       Assets = '';                        Ns = '' },
        @{ Dir = 'LaboratoryMod'; Dll = 'bin\Debug\LaboratoryMod.dll'; Assets = 'assets';                  Ns = 'mvz2_lab' },
        @{ Dir = 'ModularCurse';  Dll = 'bin\Debug\ModularCurse.dll';  Assets = 'assets';                  Ns = 'modular_curse' },
        @{ Dir = 'Loadout';       Dll = 'bin\Debug\Loadout.dll';       Assets = '';                        Ns = '' },
        @{ Dir = 'Replay';        Dll = 'bin\Debug\Replay.dll';        Assets = '';                        Ns = '' }
    )
    $pluginsDst = Join-Path $bepDst 'plugins'
    $added = 0
    foreach ($m in $mods) {
        $dll = Join-Path (Join-Path $repo $m.Dir) $m.Dll
        if (-not (Test-Path $dll)) { Write-Warn2 ($m.Dir + " 尚未构建，跳过（先在仓库根跑 .\build.ps1）"); continue }
        Copy-Item $dll $pluginsDst -Force
        $added++
        Write-Info ("plugins\" + (Split-Path -Leaf $dll))

        if ([string]::IsNullOrWhiteSpace($m.Ns) -or [string]::IsNullOrWhiteSpace($m.Assets)) { continue }
        $assets = Join-Path (Join-Path $repo $m.Dir) $m.Assets
        if (-not (Test-Path $assets)) { Write-Warn2 ($m.Dir + " 声明了命名空间但没有资源目录，只打插件"); continue }
        $dst = Join-Path $stage ('MinecraftVSZombies2_Data\StreamingAssets\Mods\' + $m.Ns)
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        # 拷「内容」而非目录本身，避免多套一层命名空间
        Copy-Item (Join-Path $assets '*') $dst -Recurse -Force
        Write-Info ("StreamingAssets\Mods\" + $m.Ns + "\")
    }
    if ($added -eq 0) { Write-Warn2 "一个插件也没打进去（都还没构建？）" }
    else { Write-Ok ("共 " + $added + " 个插件") }
} else {
    Write-Step "[3/6] 跳过 mod 插件（加 -WithMods 启用）"
}

# ---------- 4. ModManager ----------------------------------------------
# exe 直接放在包根目录（与 MinecraftVSZombies2.exe 同级），玩家解压后
# 一眼就能看到、双击即用，不用再进一层子目录去找。
# 代价是解压时会和游戏目录里的文件平铺在一起，所以命名上用完整产品名、
# 且不加任何会把玩家绕晕的层。
Write-Step "[4/6] ModManager"

switch ($ModManager) {
    'Skip' {
        Write-Warn2 "按 -ModManager Skip 不打包 ModManager"
    }

    'Existing' {
        $exe = $ExistingExe
        if ([string]::IsNullOrWhiteSpace($exe)) {
            # runtime\ 里通常就放着一份已发布的 exe，直接捡最新的那个
            $cand = @(Get-ChildItem -LiteralPath $RuntimeDir -Filter 'MVZ2ModManager*.exe' -File -ErrorAction SilentlyContinue |
                      Sort-Object LastWriteTime -Descending)
            if ($cand.Count -gt 0) { $exe = $cand[0].FullName }
        }
        if ([string]::IsNullOrWhiteSpace($exe) -or -not (Test-Path -LiteralPath $exe)) {
            throw ("找不到可复用的 ModManager exe。" +
                   "`n用 -ExistingExe <exe 路径> 指定，或改用 -ModManager SelfContained 现场编译。")
        }
        Copy-Item -LiteralPath $exe (Join-Path $stage 'MVZ2ModManager.exe') -Force
        Write-Ok ("复用: " + $exe)
        Write-Warn2 "复用方式不打进 .NET 运行时 —— 若该 exe 是框架依赖版，玩家需要 .NET 8 Desktop Runtime"
    }

    default {
        if (-not (Test-Path $mmProj)) { throw ("找不到 ModManager 工程：" + $mmProj) }
        if (-not $dotnet) {
            throw ("需要 dotnet 才能发布 ModManager（目标框架 net8.0-windows，要求 .NET SDK 8+）。`n" +
                   "装好 SDK，或改用 -ModManager Existing 复用已有 exe。")
        }

        if ($Clean) {
            foreach ($d in @('bin', 'obj')) {
                $p = Join-Path $mmDir $d
                if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force; Write-Info ("清理 " + $d) }
            }
        }

        # 先发布到临时目录，再只把 exe 捞进包根。
        # 不要直接 -o $stage：发布过程可能往输出目录塞 deps.json / *.pdb 之类的
        # 边角文件，落到包根就会跟游戏本体的同名文件混在一起（甚至覆盖）。
        $mmTemp = Join-Path $OutDir ('_mm_' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Force -Path $mmTemp | Out-Null

        try {
            # 两种模式产出的都是**单个 exe**，差别只在要不要带 .NET 运行时。
            if ($ModManager -eq 'SelfContained') {
                Write-Info "自包含单文件（自带 .NET 8，玩家免装；exe 约 150MB）"
                & $dotnet publish $mmProj -c $Config -r win-x64 --self-contained true `
                    -p:PublishSingleFile=true `
                    -p:IncludeNativeLibrariesForSelfExtract=true `
                    -p:DebugType=none `
                    -o $mmTemp --nologo
            } else {
                Write-Info "框架依赖单文件（约 2MB；玩家需装 .NET 8 Desktop Runtime）"
                & $dotnet publish $mmProj -c $Config -r win-x64 --self-contained false `
                    -p:PublishSingleFile=true `
                    -p:DebugType=none `
                    -o $mmTemp --nologo
            }
            if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit " + $LASTEXITCODE + "）" }

            $exe = Join-Path $mmTemp 'MVZ2ModManager.exe'
            if (-not (Test-Path -LiteralPath $exe)) { throw ("发布产物缺失：" + $exe) }

            Copy-Item -LiteralPath $exe (Join-Path $stage 'MVZ2ModManager.exe') -Force
            $mb = [math]::Round((Get-Item -LiteralPath (Join-Path $stage 'MVZ2ModManager.exe')).Length / 1MB, 2)
            Write-Ok ("MVZ2ModManager.exe  " + $mb + " MB   (包根目录)")
        } finally {
            Remove-Item $mmTemp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# ---------- 5. 安装说明 -------------------------------------------------
Write-Step "[5/6] 安装说明"

$hasModManager = Test-Path (Join-Path $stage 'MVZ2ModManager.exe')

$readmePath = Get-Content (Join-Path $PSScriptRoot 'pack-runtime-readme.txt') -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
if ([string]::IsNullOrWhiteSpace($readmePath)) {
    throw ("缺少说明模板：" + (Join-Path $PSScriptRoot 'pack-runtime-readme.txt'))
}

# ModManager 的说明分四种情况写 —— 复用来的 exe 无法判断要不要 .NET，
# 与其猜错、不如把两种情况都告诉玩家。
$mmLine =
    if (-not $hasModManager) {
        '   本包不含 ModManager，只提供游戏侧注入环境。'
    } elseif ($variant -eq 'lite') {
        "   ModManager 为框架依赖版 —— 需要先安装 .NET 8 Desktop Runtime (x64)：" + "`r`n" +
        "     https://dotnet.microsoft.com/download/dotnet/8.0/runtime" + "`r`n" +
        "   不装的话双击 ModManager 会弹「需要 .NET 才能运行此应用程序」。"
    } elseif ($variant -eq 'reuse') {
        "   ModManager 为复用的既有版本，自带/依赖 .NET 取决于它的发布方式：" + "`r`n" +
        "   双击能打开就无需额外操作；提示缺少 .NET 时装一下 .NET 8 Desktop Runtime (x64)。"
    } else {
        '   ModManager 为自包含版 —— 已内置 .NET 8，双击即可运行，无需安装任何东西。'
    }

$readme = $readmePath
$readme = $readme.Replace('{VERSION}', $mmVersion)
$readme = $readme.Replace('{BUILD_DATE}', (Get-Date).ToString('yyyy-MM-dd HH:mm'))
$readme = $readme.Replace('{DOTNET_LINE}', $(if ($NoDotnet) {
            '   （本包不含 dotnet\ —— 复用了你机器上已有的 BepInEx 环境）'
        } else {
            '   （本包已自带 dotnet\ 宿主运行时，无需安装 .NET）'
        }))
$readme = $readme.Replace('{MODMANAGER_LINE}', $mmLine)
$readme = $readme.Replace('{MODS_LINE}', $(if ($WithMods) {
            '   本包已附带若干已构建的 mod（见 BepInEx\plugins\ 与 StreamingAssets\Mods\），' + "`r`n" +
            '   打开 ModManager 即可看到并启用。'
        } else {
            '   本包不含任何 mod 本体 —— 用 ModManager 导入 .mvz2pack，或手动把插件' + "`r`n" +
            '   放进 BepInEx\plugins\。'
        }))

# 文件名用 char 码构造，避免脚本自身编码影响（与 build-common 里一致）
$txtName = (-join [char[]](0x5B89, 0x88C5, 0x8BF4, 0x660E)) + '.txt'
[System.IO.File]::WriteAllText((Join-Path $stage $txtName), $readme,
    (New-Object System.Text.UTF8Encoding($true)))

# ---------- 6. 压缩 + 校验 ---------------------------------------------
Write-Step "[6/6] 压缩"

# 打包前再确认关键产物都在：宁可在这里中断，也别让玩家拿到半个包
$must = @(
    'winhttp.dll',
    'doorstop_config.ini',
    'BepInEx\core\BepInEx.Core.dll',
    'BepInEx\interop'
)
if (-not $NoDotnet) { $must += 'dotnet\coreclr.dll' }
if ($hasModManager) { $must += 'MVZ2ModManager.exe' }

foreach ($rel in $must) {
    $p = Join-Path $stage $rel
    if (-not (Test-Path -LiteralPath $p)) { throw ("打包产物缺项：" + $rel) }
}

if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# includeBaseDirectory 的等价效果：压缩时以 stage 为根，
# 解压后直接是 winhttp.dll / BepInEx\ ... 这一层，
# 不会多套一个包名目录（玩家只需「全选 → 解压到游戏根目录」）。
New-ZipFromDirectory -SourceDir $stage -ZipPath $zip `
    -EnsureDirs @('BepInEx/plugins', 'BepInEx/config')

$sizeMb = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 2)
$fileCount = (Get-ChildItem $stage -Recurse -File).Count
$interopCount = 0
if (Test-Path (Join-Path $bepDst 'interop')) {
    $interopCount = @(Get-ChildItem (Join-Path $bepDst 'interop') -Filter *.dll -File -ErrorAction SilentlyContinue).Count
}

Remove-Item $stage -Recurse -Force

Write-Ok ("包体: " + $zip)
Write-Info ("大小: " + $sizeMb + " MB   文件数: " + $fileCount + "   interop: " + $interopCount + " 个 dll")
if (-not $NoDotnet) {
    Write-Info ("宿主运行时: 含 dotnet\ (.NET 6 x86)")
} else {
    Write-Info ("宿主运行时: 不含")
}
if ($hasModManager) {
    $mmNote =
        if ($variant -eq 'lite') { '框架依赖（需 .NET 8 Desktop Runtime）' }
        elseif ($variant -eq 'reuse') { '复用既有 exe（.NET 需求未知，见说明）' }
        else { '自包含（免装 .NET）' }
    Write-Info ("ModManager : " + $mmNote)
} else {
    Write-Info ("ModManager : 未包含")
}

# 可选：解出来核对一遍目录结构（避免出现「包能打开但装上去没用」）
if ($Verify) {
    Write-Step "校验解包结构"
    $tmp = Join-Path $OutDir ('_verify_' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $tmp)
        $checks = @('winhttp.dll', 'doorstop_config.ini', 'BepInEx\core\BepInEx.Core.dll', 'BepInEx\interop')
        if (-not $NoDotnet) { $checks += 'dotnet\coreclr.dll' }
        if ($hasModManager) { $checks += 'MVZ2ModManager.exe' }
        $bad = 0
        foreach ($rel in $checks) {
            if (Test-Path (Join-Path $tmp $rel)) { Write-Ok ("OK  " + $rel) }
            else { Write-Warn2 ("缺  " + $rel); $bad++ }
        }
        if ($bad -eq 0) { Write-Ok "结构校验通过" } else { Write-Warn2 ("有 " + $bad + " 项不符") }
    } finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($Open) { Start-Process -FilePath $OutDir }

Write-Host ""
Write-Ok ("完成。" + $packageName)
