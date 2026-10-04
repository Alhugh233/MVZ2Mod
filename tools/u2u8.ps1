# 把 PowerShell 重定向（>）写出的 UTF-16 文本转成 UTF-8，方便直接查看。
#   tools\u2u8.ps1 -Path _st.txt [-Keep]
param(
    [Parameter(Mandatory = $true)][string]$Path
)
$ErrorActionPreference = 'Stop'
$t = Get-Content -LiteralPath $Path -Raw
$out = $Path
if ($Path -match '\.txt$') { $out = $Path -replace '\.txt$', '.u8.txt' }
if (-not [IO.Path]::IsPathRooted($out)) { $out = Join-Path (Get-Location) $out }
[IO.File]::WriteAllText($out, $t, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("-> " + $out)
