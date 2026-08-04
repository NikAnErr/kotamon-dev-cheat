param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'

$compiler = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw "Roslyn compiler not found: $compiler"
}

$gameRootResolved = (Resolve-Path -LiteralPath $GameRoot).Path
$source = Join-Path $PSScriptRoot 'KotamonDevCheat.cs'
$outputDirectory = Join-Path $PSScriptRoot 'bin'
$output = Join-Path $outputDirectory 'KotamonDevCheat.compiled.dll'
$pluginDirectory = Join-Path $gameRootResolved 'BepInEx\plugins\KotamonDevCheat'

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
if (-not $SkipInstall) {
    New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
}

$references = @(
    'dotnet\System.Private.CoreLib.dll',
    'dotnet\System.Runtime.dll',
    'dotnet\netstandard.dll',
    'dotnet\System.Collections.dll',
    'dotnet\System.Console.dll',
    'dotnet\System.Linq.dll',
    'dotnet\System.ObjectModel.dll',
    'dotnet\System.Runtime.InteropServices.dll',
    'BepInEx\core\BepInEx.Core.dll',
    'BepInEx\core\BepInEx.Unity.IL2CPP.dll',
    'BepInEx\core\Il2CppInterop.Common.dll',
    'BepInEx\core\Il2CppInterop.Runtime.dll',
    'BepInEx\interop\Il2Cppmscorlib.dll',
    'BepInEx\interop\UnityEngine.CoreModule.dll',
    'BepInEx\interop\UnityEngine.IMGUIModule.dll',
    'BepInEx\interop\UnityEngine.InputLegacyModule.dll',
    'BepInEx\interop\UnityEngine.PhysicsModule.dll',
    'BepInEx\interop\UniTask.dll',
    'BepInEx\interop\Project.dll'
) | ForEach-Object { Join-Path $gameRootResolved $_ }

foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) {
        throw "Reference not found: $reference"
    }
}

$arguments = @(
    '/nologo',
    '/noconfig',
    '/nostdlib+',
    '/target:library',
    '/langversion:latest',
    '/optimize+',
    '/deterministic+',
    "/out:$output"
)

$arguments += $references | ForEach-Object { "/reference:$_" }
$arguments += $source

& $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

Write-Output "Built: $output"
if (-not $SkipInstall) {
    Copy-Item -LiteralPath $output -Destination (Join-Path $pluginDirectory 'KotamonDevCheat.dll') -Force
    Write-Output "Installed: $(Join-Path $pluginDirectory 'KotamonDevCheat.dll')"
}
