# 在 VS Code 的本地历史（Timeline）里找某个文件的所有历史版本，按时间倒序输出。
#
#   tools\find-local-history.ps1 -Name ReplayRecorder.cs

param(
    [Parameter(Mandatory = $true)][string]$Name,
    [int]$Max = 20
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $env:APPDATA 'Code\User\History'
if (-not (Test-Path $root)) { throw "没有本地历史目录: $root" }

$hits = New-Object System.Collections.Generic.List[object]
foreach ($dir in Get-ChildItem -LiteralPath $root -Directory) {
    $entries = Join-Path $dir.FullName 'entries.json'
    if (-not (Test-Path $entries)) { continue }
    $json = [IO.File]::ReadAllText($entries)
    if ($json -notlike ('*' + $Name + '*')) { continue }
    $obj = $json | ConvertFrom-Json
    foreach ($e in $obj.entries) {
        $p = Join-Path $dir.FullName ($e.id + '.')
        # VS Code 用 <id> 或 <id>.<ext> 命名
        $file = Get-ChildItem -LiteralPath $dir.FullName -File | Where-Object { $_.Name -like ($e.id + '*') } | Select-Object -First 1
        if (-not $file) { continue }
        $hits.Add([pscustomobject]@{
                File = $file.FullName
                Time = [datetime]::FromFileTime([int64]$e.timestamp)
                Size = $file.Length
                Res  = $obj.resource
            })
    }
}

if ($hits.Count -eq 0) { Write-Host "（本地历史里没有 $Name）"; exit 0 }
$hits | Sort-Object Time -Descending | Select-Object -First $Max | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
