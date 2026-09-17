# MVZ2 Modular Curse - one click build / deploy / pack
#
#   .\build-curse.ps1                build + auto deploy to the test game dir
#   .\build-curse.ps1 -Pack          ... then pack the shareable zip (dist/pack.ps1)
#   .\build-curse.ps1 -Pack -Launch  ... then start the game (smoke)
#   .\build-curse.ps1 -KeepGame      do not kill a running game first
#   .\build-curse.ps1 -Clean         dotnet clean (bin/obj) before building
#
# ModularCurse.csproj already deploys after a successful build (see its
# CopyToPlugins / DeployModAssets targets):
#   <GameDir>BepInEx\plugins\ModularCurse.dll              <- plugin (+ CustomCore prerequisite)
#   <GameDir>...\StreamingAssets\Mods\modular_curse\**     <- XML metas + sprites
#
# Compile references come from the repo's self contained runtime\ folder,
# so building does NOT depend on GameDir (GameDir is only the deploy target).
param(
    [string]$GameDir = "",
    [switch]$SkipBuild,
    [switch]$Clean,
    [switch]$KeepGame,
    [switch]$Pack,
    [switch]$Launch
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = Join-Path $root "ModularCurse\ModularCurse.csproj"
$pluginDll = Join-Path $root "ModularCurse\bin\Debug\ModularCurse.dll"
$assetSource = Join-Path $root "ModAssets\modular_curse"

if ([string]::IsNullOrEmpty($GameDir)) { $GameDir = "E:\Game\PVZ\MVZ2\MVZ2 0.7.0 test-4" }
$GameDir = $GameDir.TrimEnd('\', '/')
$gameExe = Join-Path $GameDir "MinecraftVSZombies2.exe"
$hasGame = Test-Path $gameExe

function Step($t) { Write-Host "" ; Write-Host "== $t ==" -ForegroundColor Cyan }
function HashOf($p) { if (Test-Path $p) { (Get-FileHash $p -Algorithm SHA256).Hash } else { "" } }

# version is read from Plugin.cs so the script never drifts from the plugin metadata
$pluginCs = Join-Path $root "ModularCurse\Plugin.cs"
$verLine = Select-String -Path $pluginCs -Pattern 'VERSION = "([^"]+)"' | Select-Object -First 1
$version = if ($verLine) { $verLine.Matches[0].Groups[1].Value } else { "dev" }
$nsp = "modular_curse"

# ---------- 0. locate dotnet ----------
$dotnet = $null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) { $dotnet = $cmd.Source }
elseif (Test-Path (Join-Path $env:ProgramFiles "dotnet\dotnet.exe")) { $dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe" }
if (-not $dotnet) {
    throw "dotnet not found (looked in PATH and $env:ProgramFiles\dotnet)"
}
Write-Host ("dotnet   : " + $dotnet)
Write-Host ("game dir : " + $(if ($hasGame) { $GameDir } else { $GameDir + "  [NOT FOUND - build only]" }))
Write-Host ("version  : v" + $version)

# ---------- 1. stop the running game (it locks the plugin dll) ----------
Step "[1/4] stop running game"
if ($KeepGame) {
    Write-Host "  skipped (-KeepGame)" -ForegroundColor Yellow
} else {
    $procs = @(Get-Process -Name MinecraftVSZombies2 -ErrorAction SilentlyContinue)
    if ($procs.Count -gt 0) {
        foreach ($p in $procs) { try { $p.Kill() } catch { } }
        Write-Host ("  killed " + $procs.Count + " process(es)")
        Start-Sleep -Milliseconds 800
    } else {
        Write-Host "  nothing running"
    }
}

# ---------- 2. clean (optional) / build ----------
if ($Clean) {
    Step "[2/4] dotnet clean"
    & $dotnet clean $csproj -c Debug | Out-Null
    foreach ($d in @("bin", "obj")) {
        $p = Join-Path $root ("ModularCurse\" + $d)
        if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Write-Host "  bin/ obj/ removed"
} else {
    Step "[2/4] (no -Clean)"
}

if ($SkipBuild) {
    Step "[3/4] build skipped (-SkipBuild)"
} else {
    Step "[3/4] dotnet build ModularCurse.csproj -c Debug"
    # forward slashes: the csproj concatenates GameDir directly with a file name
    $gameArg = ($GameDir -replace '\\', '/') + '/'
    & $dotnet build $csproj -c Debug ("-p:GameDir=" + $gameArg)
    if ($LASTEXITCODE -ne 0) { throw ("dotnet build failed (exit " + $LASTEXITCODE + ")") }
}
if (-not (Test-Path $pluginDll)) { throw ("plugin dll not found: " + $pluginDll) }
$dllInfo = Get-Item $pluginDll
Write-Host ("  " + $pluginDll + "  " + $dllInfo.Length + " bytes  " + $dllInfo.LastWriteTime) -ForegroundColor Green

# ---------- 4. verify the auto deploy ----------
Step "[4/4] verify deploy"
if (-not $hasGame) {
    Write-Warning "  game dir not found, nothing was deployed"
} else {
    $ok = $true
    # plugins
    foreach ($name in @("ModularCurse.dll", "CustomCore.dll")) {
        $src = Join-Path $root ("ModularCurse\bin\Debug\" + $name)
        if ($name -eq "CustomCore.dll") { $src = Join-Path $root "CustomCore\bin\Debug\CustomCore.dll" }
        $dst = Join-Path $GameDir ("BepInEx\plugins\" + $name)
        $same = (Test-Path $dst) -and ((HashOf $src) -eq (HashOf $dst))
        if (-not $same) { $ok = $false }
        Write-Host ("  plugins\" + $name + "  deployed=" + (Test-Path $dst) + "  matchesBuild=" + $same)
    }
    # mod assets
    $liveRoot = Join-Path $GameDir ("MinecraftVSZombies2_Data\StreamingAssets\Mods\" + $nsp)
    $files = Get-ChildItem $assetSource -Recurse -File -ErrorAction SilentlyContinue
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($assetSource.Length).TrimStart('\')
        $dst = Join-Path $liveRoot $rel
        $same = (Test-Path $dst) -and ((HashOf $f.FullName) -eq (HashOf $dst))
        if (-not $same) { $ok = $false }
        Write-Host ("  assets\" + $rel + "  matchesSource=" + $same)
    }
    if ($ok) { Write-Host "  deploy OK" -ForegroundColor Green }
    else { Write-Warning "  some files are stale - check the log above (was the game running?)" }
}

# ---------- 5. optional pack ----------
if ($Pack) {
    Step "[5/5] pack (dist/pack.ps1)"
    $pack = Join-Path $root "dist\pack.ps1"
    if (-not (Test-Path $pack)) { throw ("missing " + $pack) }
    & $pack
    $zip = Join-Path $root ("dist\MVZ2ModularCurse_v" + $version + "_0.7.0.zip")
    if (Test-Path $zip) { Write-Host ("  " + $zip + "  " + [math]::Round((Get-Item $zip).Length / 1MB, 2) + " MB") -ForegroundColor Green }
} else {
    Step "[5/5] pack skipped (add -Pack)"
}

# ---------- 6. optional launch ----------
if ($Launch) {
    if (-not $hasGame) { Write-Warning "-Launch needs a valid -GameDir" }
    else {
        Write-Host ""
        Write-Host ("starting " + $gameExe) -ForegroundColor Cyan
        Start-Process -FilePath $gameExe -WorkingDirectory $GameDir
    }
}

Write-Host ""
Write-Host ("done. plugin v" + $version) -ForegroundColor Green
Write-Host ("  log: " + (Join-Path $GameDir "BepInEx\LogOutput.log"))
