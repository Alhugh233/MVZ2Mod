# =====================================================================
# 用 Mono.Cecil 静态读取 interop 程序集里的类型 / 成员签名。
#
#   powershell -File dump-interop-signature.ps1 -Type UnityEngine.UI.Button [-Member onClick]
#   powershell -File dump-interop-signature.ps1 -Search UnityAction
#
# 为什么必须这么查：interop 里的属性/方法形状与游戏源码里的写法**不一定一致**
# （典型：C# 的 `public event Action<T> OnClick` 会变成 `Il2CppSystem.Action<T>` 属性
#  + add_/remove_ 方法）。猜签名是 mod 静默失效的头号原因。
# =====================================================================
[CmdletBinding()]
param(
    [string]$Type,
    [string]$Member,
    [string]$Search,
    [string]$InteropDir = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($InteropDir)) {
    $InteropDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'runtime\BepInEx\interop'
}
if (-not (Test-Path -LiteralPath $InteropDir)) { throw "interop dir not found: $InteropDir" }

$cecil = Join-Path (Split-Path -Parent $PSScriptRoot) 'runtime\BepInEx\core\Mono.Cecil.dll'
if (-not (Test-Path -LiteralPath $cecil)) { throw "Mono.Cecil not found: $cecil" }
Add-Type -Path $cecil

$dlls = Get-ChildItem -LiteralPath $InteropDir -Filter *.dll -File | Select-Object -ExpandProperty FullName

function Format-Type($t) {
    if ($null -eq $t) { return '?' }
    return $t.FullName
}

if (-not [string]::IsNullOrWhiteSpace($Search)) {
    Write-Host "=== 搜索含 '$Search' 的类型 ==="
    foreach ($dll in $dlls) {
        try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll) } catch { continue }
        $all = New-Object System.Collections.Generic.List[object]
        function Collect($types) {
            foreach ($t in $types) {
                [void]$all.Add($t)
                if ($t.HasNestedTypes) { Collect $t.NestedTypes }
            }
        }
        Collect $asm.MainModule.Types
        foreach ($t in $all) {
            if ($t.FullName -like "*$Search*") {
                Write-Host ("  " + (Split-Path -Leaf $dll) + "  ->  " + $t.FullName)
            }
        }
    }
    return
}

if ([string]::IsNullOrWhiteSpace($Type)) { throw "-Type or -Search required" }

$found = $false
foreach ($dll in $dlls) {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll) } catch { continue }
    $stack = New-Object System.Collections.Generic.Stack[object]
    foreach ($t in $asm.MainModule.Types) { $stack.Push($t) }
    while ($stack.Count -gt 0) {
        $t = $stack.Pop()
        if ($t.HasNestedTypes) { foreach ($n in $t.NestedTypes) { $stack.Push($n) } }
        if ($t.FullName -ne $Type) { continue }

        $found = $true
        Write-Host ("=== " + $t.FullName + "   [" + (Split-Path -Leaf $dll) + "]")
        Write-Host ("    base = " + (Format-Type $t.BaseType))
        Write-Host ("    final=" + $t.IsFinal + "  interface=" + $t.IsInterface + "  abstract=" + $t.IsAbstract)

        Write-Host "  --- fields"
        foreach ($f in $t.Fields) {
            if ($Member -and $f.Name -notlike "*$Member*") { continue }
            Write-Host ("    " + (Format-Type $f.FieldType) + "  " + $f.Name + "  (public=" + $f.IsPublic + ")")
        }
        Write-Host "  --- properties"
        foreach ($p in $t.Properties) {
            if ($Member -and $p.Name -notlike "*$Member*") { continue }
            Write-Host ("    " + (Format-Type $p.PropertyType) + "  " + $p.Name)
        }
        Write-Host "  --- methods"
        foreach ($m in $t.Methods) {
            if ($Member -and $m.Name -notlike "*$Member*") { continue }
            $ps = ($m.Parameters | ForEach-Object { (Format-Type $_.ParameterType) + ' ' + $_.Name }) -join ', '
            Write-Host ("    " + (Format-Type $m.ReturnType) + "  " + $m.Name + "(" + $ps + ")")
        }
        if ($t.HasNestedTypes) {
            Write-Host "  --- nested types"
            foreach ($n in $t.NestedTypes) { Write-Host ("    " + $n.FullName) }
        }
    }
}
if (-not $found) { Write-Host "NOT FOUND: $Type" }
