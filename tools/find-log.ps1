# =====================================================================
# Grep the game's BepInEx logs with context.
#
#     .\tools\find-log.ps1 -Pattern "Exception" -Pattern "NullReference"
#     .\tools\find-log.ps1 -Which both -Pattern "Error :" -Context 12 -Last 40
#
# Exists because passing regex patterns through nested `powershell -Command`
# inside a VS Code task mangles the quoting (single quotes get stripped), so
# the patterns must live in a -File script. ASCII only on purpose: a .ps1
# without a BOM is read as GBK by PS 5.1, which breaks non-ASCII literals.
# =====================================================================
param(
    [ValidateSet('log', 'error', 'both', 'player', 'player-all')]
    [string]$Which = 'log',
    [string[]]$Pattern = @('Exception', 'NullReference', 'Error :'),
    [int]$Context = 8,
    [int]$Last = 40,
    [string]$GameDir = ""
)

Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\build-common.ps1"

$repo = Get-RepoRoot
$GameDir = Normalize-GameDir $GameDir (Get-DefaultGameDir -RepoRoot $repo)

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    Write-Host "no game dir configured"
    exit 0
}

$proc = Get-Process MinecraftVsZombies2 -ErrorAction SilentlyContinue
if ($null -ne $proc) {
    Write-Host ("game running: pid " + $proc.Id + " (log may be buffered)")
} else {
    Write-Host "game not running"
}

$targets = switch ($Which) {
    'log' { @('LogOutput.log') }
    'error' { @('ErrorLog.log') }
    'both' { @('LogOutput.log', 'ErrorLog.log') }
    'player' { @('__player__') }
    'player-all' { @('__player__') }
}

foreach ($name in $targets) {
    if ($name -eq '__player__') {
        # Unity's own log: uncaught managed exceptions land here, not in BepInEx's one.
        $candidates = @()
        $candidates += Get-ChildItem -Path (Join-Path $env:USERPROFILE 'AppData\LocalLow') -Recurse -Filter 'Player.log' -ErrorAction SilentlyContinue
        $candidates += Get-ChildItem -Path (Join-Path $env:USERPROFILE 'AppData\LocalLow') -Recurse -Filter 'Player-prev.log' -ErrorAction SilentlyContinue
        Write-Host ""
        Write-Host "== Unity Player.log ==" -ForegroundColor Cyan
        if (@($candidates).Count -eq 0) {
            Write-Host "  (not found under AppData\LocalLow)"
            continue
        }
        foreach ($f in ($candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 3)) {
            Write-Host ("  " + $f.FullName + "  mtime=" + $f.LastWriteTime.ToString('MM-dd HH:mm:ss') + "  bytes=" + $f.Length)
        }

        if ($Which -eq 'player-all') {
            # Count-only overview across every Player log found (useful to tell
            # "our bug" from "pre-existing engine noise").
            foreach ($f in ($candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 6)) {
                $n = @(Select-String -LiteralPath $f.FullName -Pattern $Pattern).Count
                Write-Host ("  count=" + $n + "  " + $f.Name + "  mtime=" + $f.LastWriteTime.ToString('MM-dd HH:mm:ss'))
                foreach ($pat in $Pattern) {
                    $c = @(Select-String -LiteralPath $f.FullName -Pattern $pat).Count
                    Write-Host ("      " + $pat + " = " + $c)
                }
            }
            continue
        }

        $newest = $candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $hits = Select-String -LiteralPath $newest.FullName -Pattern $Pattern -Context $Context, $Context
        if ($null -eq $hits) {
            Write-Host "  (no match in " + $newest.Name + ")"
            continue
        }
        Write-Host ("  matches: " + @($hits).Count)
        @($hits) | Select-Object -Last $Last | ForEach-Object {
            Write-Host ("  --- line " + $_.LineNumber + " ---")
            foreach ($pre in $_.Context.PreContext) { Write-Host ("  | " + $pre) }
            Write-Host ("  > " + $_.Line)
            foreach ($post in $_.Context.PostContext) { Write-Host ("  | " + $post) }
        }
        continue
    }

    $p = Join-Path $GameDir ('BepInEx\' + $name)
    Write-Host ""
    Write-Host ("== " + $name + " ==") -ForegroundColor Cyan
    if (-not (Test-Path -LiteralPath $p)) {
        Write-Host "  (missing)"
        continue
    }

    $fi = Get-Item -LiteralPath $p
    Write-Host ("  mtime=" + $fi.LastWriteTime.ToString('HH:mm:ss') + "  bytes=" + $fi.Length)

    $hits = Select-String -LiteralPath $p -Pattern $Pattern -Context $Context, $Context
    if ($null -eq $hits) {
        Write-Host "  (no match)"
        continue
    }

    $total = @($hits).Count
    Write-Host ("  matches: " + $total)
    @($hits) | Select-Object -Last $Last | ForEach-Object {
        Write-Host ("  --- line " + $_.LineNumber + " ---")
        foreach ($pre in $_.Context.PreContext) { Write-Host ("  | " + $pre) }
        Write-Host ("  > " + $_.Line)
        foreach ($post in $_.Context.PostContext) { Write-Host ("  | " + $post) }
    }
}
