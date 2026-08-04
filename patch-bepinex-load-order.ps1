param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$target = Join-Path $GameRoot 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'
$backup = "$target.kotamon-original"
$temporary = "$target.kotamon-patched"
$cecil = Join-Path $GameRoot 'BepInEx\core\Mono.Cecil.dll'

if (-not (Test-Path -LiteralPath $backup)) {
    Copy-Item -LiteralPath $target -Destination $backup
}

Add-Type -Path $cecil
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($target)
$type = $assembly.MainModule.Types | Where-Object FullName -EQ 'BepInEx.Unity.IL2CPP.IL2CPPChainloader'
$method = $type.Methods | Where-Object Name -EQ 'OnInvokeMethod'
$instructions = @($method.Body.Instructions)

$setupCall = $instructions | Where-Object {
    $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and
    $_.Operand.FullName -eq 'System.Void BepInEx.Unity.IL2CPP.IL2CPPChainloader::SetupUnityLogging()'
}

$preloadCall = $instructions | Where-Object {
    $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and
    $_.Operand.FullName -eq 'System.Void BepInEx.Unity.IL2CPP.Il2CppInteropManager::PreloadInteropAssemblies()'
}

if ($setupCall.Count -ne 1 -or $preloadCall.Count -ne 1) {
    throw 'Could not identify unique SetupUnityLogging and PreloadInteropAssemblies calls.'
}

if ($preloadCall.Offset -lt $setupCall.Offset) {
    Write-Output 'BepInEx load order is already patched.'
    $assembly.Dispose()
    exit 0
}

$setupOperand = $setupCall.Operand
$setupCall.Operand = $preloadCall.Operand
$preloadCall.Operand = $setupOperand

$assembly.Write($temporary)
$assembly.Dispose()
Move-Item -LiteralPath $temporary -Destination $target -Force

$verification = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($target)
$verificationType = $verification.MainModule.Types | Where-Object FullName -EQ 'BepInEx.Unity.IL2CPP.IL2CPPChainloader'
$verificationMethod = $verificationType.Methods | Where-Object Name -EQ 'OnInvokeMethod'
$orderedCalls = @($verificationMethod.Body.Instructions | Where-Object {
    $_.Operand.FullName -match 'PreloadInteropAssemblies|SetupUnityLogging'
} | ForEach-Object { $_.Operand.Name })
$verification.Dispose()

if (($orderedCalls -join ',') -ne 'PreloadInteropAssemblies,SetupUnityLogging') {
    throw "Patched call order verification failed: $($orderedCalls -join ', ')"
}

Write-Output "Patched: $target"
Write-Output "Backup: $backup"
Write-Output "Call order: $($orderedCalls -join ' -> ')"
