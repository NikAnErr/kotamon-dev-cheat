param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$pluginBuilder = Join-Path $PSScriptRoot 'build.ps1'
& powershell -NoProfile -ExecutionPolicy Bypass -File $pluginBuilder -GameRoot $GameRoot -SkipInstall
if ($LASTEXITCODE -ne 0) {
    throw "Plugin compilation failed with exit code $LASTEXITCODE"
}

$compiler = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$framework = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.1'
$source = Join-Path $PSScriptRoot 'Launcher\Program.cs'
$plugin = Join-Path $PSScriptRoot 'bin\KotamonDevCheat.compiled.dll'
$releaseDirectory = Join-Path $PSScriptRoot 'release'
$output = Join-Path $releaseDirectory 'KotamonDevCheat.exe'
$payload = Join-Path $releaseDirectory '.KotamonDevCheat-BepInExPayload.zip'
$bepInExArchive = Join-Path $GameRoot 'BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.781+637bc7e.zip'
$patchedBepInEx = Join-Path $GameRoot 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'
$unityBaseLibraries = Join-Path $GameRoot 'BepInEx\unity-libs\6000.4.1.zip'
$interopDirectory = Join-Path $GameRoot 'BepInEx\interop'
$thirdPartyNotices = Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.txt'
$bepInExLicense = 'C:\Program Files\Git\mingw64\share\licenses\gcc-libs\COPYING.LIB'
$loadOrderPatch = Join-Path $PSScriptRoot 'patch-bepinex-load-order.ps1'
$interopPatch = Join-Path $PSScriptRoot 'patch-unity6-interop.ps1'
$bepInExConfig = Join-Path $PSScriptRoot 'BepInEx.Kotamon.cfg'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "Roslyn compiler not found: $compiler"
}
if (-not (Test-Path -LiteralPath $framework)) {
    throw ".NET Framework 4.7.1 reference assemblies not found: $framework"
}
foreach ($required in @($bepInExArchive, $patchedBepInEx, $unityBaseLibraries, $interopDirectory,
    $thirdPartyNotices, $bepInExLicense, $loadOrderPatch, $interopPatch, $bepInExConfig)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "BepInEx payload source not found: $required"
    }
}

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Add-FileToArchive(
    [System.IO.Compression.ZipArchive]$Archive,
    [string]$Source,
    [string]$EntryName
) {
    $entry = $Archive.CreateEntry($EntryName, [System.IO.Compression.CompressionLevel]::Optimal)
    $entry.LastWriteTime = (Get-Item -LiteralPath $Source).LastWriteTime
    $input = [System.IO.File]::OpenRead($Source)
    $outputStream = $entry.Open()
    try {
        $input.CopyTo($outputStream)
    }
    finally {
        $outputStream.Dispose()
        $input.Dispose()
    }
}

if (Test-Path -LiteralPath $payload) {
    Remove-Item -LiteralPath $payload -Force
}

$payloadStream = [System.IO.File]::Open($payload, [System.IO.FileMode]::CreateNew)
$payloadArchive = [System.IO.Compression.ZipArchive]::new(
    $payloadStream,
    [System.IO.Compression.ZipArchiveMode]::Create,
    $false
)
$sourceArchive = [System.IO.Compression.ZipFile]::OpenRead($bepInExArchive)
try {
    foreach ($sourceEntry in $sourceArchive.Entries) {
        if ([string]::IsNullOrEmpty($sourceEntry.Name)) {
            continue
        }
        if ($sourceEntry.FullName -ieq 'BepInEx/core/BepInEx.Unity.IL2CPP.dll') {
            continue
        }

        $targetEntry = $payloadArchive.CreateEntry(
            $sourceEntry.FullName,
            [System.IO.Compression.CompressionLevel]::Optimal
        )
        $targetEntry.LastWriteTime = $sourceEntry.LastWriteTime
        $input = $sourceEntry.Open()
        $outputStream = $targetEntry.Open()
        try {
            $input.CopyTo($outputStream)
        }
        finally {
            $outputStream.Dispose()
            $input.Dispose()
        }
    }

    Add-FileToArchive $payloadArchive $patchedBepInEx 'BepInEx/core/BepInEx.Unity.IL2CPP.dll'
    Add-FileToArchive $payloadArchive $unityBaseLibraries 'BepInEx/unity-libs/6000.4.1.zip'
    Add-FileToArchive $payloadArchive $bepInExConfig 'BepInEx/config/BepInEx.cfg'

    Get-ChildItem -LiteralPath $interopDirectory -Recurse -File |
        Where-Object { $_.Name -notlike '*.kotamon-original' } |
        ForEach-Object {
            $relative = $_.FullName.Substring($interopDirectory.Length).TrimStart('\', '/')
            Add-FileToArchive $payloadArchive $_.FullName ('BepInEx/interop/' + $relative.Replace('\', '/'))
        }

    Add-FileToArchive $payloadArchive $thirdPartyNotices 'BepInEx/THIRD_PARTY_NOTICES-Kotamon.txt'
    Add-FileToArchive $payloadArchive $bepInExLicense 'BepInEx/LICENSE-BepInEx-LGPL-2.1.txt'
    Add-FileToArchive $payloadArchive $loadOrderPatch 'BepInEx/Kotamon-Source/patch-bepinex-load-order.ps1'
    Add-FileToArchive $payloadArchive $interopPatch 'BepInEx/Kotamon-Source/patch-unity6-interop.ps1'
}
finally {
    $sourceArchive.Dispose()
    $payloadArchive.Dispose()
    $payloadStream.Dispose()
}

$references = @(
    'mscorlib.dll',
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.IO.Compression.dll',
    'System.IO.Compression.FileSystem.dll',
    'System.Windows.Forms.dll'
) | ForEach-Object { Join-Path $framework $_ }

$arguments = @(
    '/nologo',
    '/noconfig',
    '/nostdlib+',
    '/target:winexe',
    '/platform:anycpu',
    '/langversion:latest',
    '/optimize+',
    '/deterministic+',
    "/out:$output",
    "/resource:$plugin,KotamonDevCheat.EmbeddedPlugin.dll",
    "/resource:$payload,KotamonDevCheat.BepInExPayload.zip"
)
$arguments += $references | ForEach-Object { "/reference:$_" }
$arguments += $source

& $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Launcher compilation failed with exit code $LASTEXITCODE"
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $output).Hash
$payloadSize = (Get-Item -LiteralPath $payload).Length
Remove-Item -LiteralPath $payload -Force
Write-Output "Built portfolio EXE: $output"
Write-Output "Embedded BepInEx payload: $([Math]::Round($payloadSize / 1MB, 2)) MB"
Write-Output "SHA-256: $hash"
