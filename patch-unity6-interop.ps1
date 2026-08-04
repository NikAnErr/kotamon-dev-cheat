param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$target = Join-Path $GameRoot 'BepInEx\interop\UnityEngine.CoreModule.dll'
$backup = "$target.kotamon-original"
$temporary = "$target.kotamon-patched"
$cecil = Join-Path $GameRoot 'BepInEx\core\Mono.Cecil.dll'

if (-not (Test-Path -LiteralPath $backup)) {
    Copy-Item -LiteralPath $target -Destination $backup
}

Add-Type -Path $cecil

function Get-AllTypes([Mono.Cecil.TypeDefinition]$Type) {
    $Type
    foreach ($nested in $Type.NestedTypes) {
        Get-AllTypes $nested
    }
}

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($target)
$allTypes = @()
foreach ($type in $assembly.MainModule.Types) {
    $allTypes += @(Get-AllTypes $type)
}

$compilerDelegateCaches = @($allTypes | Where-Object Name -EQ '<>O')
if ($compilerDelegateCaches.Count -eq 0) {
    Write-Output 'No Unity 6 <>O compiler-generated types require renaming.'
    $assembly.Dispose()
    exit 0
}

for ($index = 0; $index -lt $compilerDelegateCaches.Count; $index++) {
    $compilerDelegateCaches[$index].Name = "__KotamonInteropDelegateCache_$('{0:D3}' -f $index)"
}

$assembly.Write($temporary)
$assembly.Dispose()
Move-Item -LiteralPath $temporary -Destination $target -Force

$verification = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($target)
$remaining = @()
foreach ($type in $verification.MainModule.Types) {
    $remaining += @(Get-AllTypes $type | Where-Object Name -EQ '<>O')
}
$verification.Dispose()

if ($remaining.Count -ne 0) {
    throw "Interop verification failed: $($remaining.Count) <>O types remain."
}

Write-Output "Patched: $target"
Write-Output "Backup: $backup"
Write-Output "Renamed compiler-generated types: $($compilerDelegateCaches.Count)"
