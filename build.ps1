$ErrorActionPreference = 'Stop'

$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Gamble With Your Friends'
$Managed = Join-Path $GameDir 'Gamble With Your Friends_Data\Managed'
$BepInExCore = Join-Path $GameDir 'BepInEx\core'
$OutDir = Join-Path $PSScriptRoot 'dist'
$PluginDir = Join-Path $GameDir 'BepInEx\plugins'
$CompilerCandidates = @(
    'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe',
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
)
$Compiler = $CompilerCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Compiler) {
    throw 'No C# compiler found. Install Visual Studio or Visual Studio Build Tools.'
}

foreach ($path in @(
    $Managed,
    (Join-Path $BepInExCore 'BepInEx.dll'),
    (Join-Path $BepInExCore '0Harmony.dll'),
    (Join-Path $Managed 'Assembly-CSharp.dll'),
    (Join-Path $Managed 'Mirror.dll'),
    (Join-Path $Managed 'netstandard.dll'),
    (Join-Path $Managed 'UnityEngine.dll'),
    (Join-Path $Managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $Managed 'Unity.InputSystem.dll'),
    (Join-Path $Managed 'UnityEngine.IMGUIModule.dll'),
    (Join-Path $Managed 'UnityEngine.InputLegacyModule.dll'),
    (Join-Path $Managed 'UnityEngine.PhysicsModule.dll'),
    (Join-Path $Managed 'UnityEngine.UIModule.dll')
)) {
    if (-not (Test-Path $path)) { throw "Required file not found: $path" }
}

New-Item -ItemType Directory -Force $OutDir | Out-Null

$sources = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File | ForEach-Object FullName
$references = @(
    (Join-Path $BepInExCore 'BepInEx.dll'),
    (Join-Path $BepInExCore '0Harmony.dll'),
    (Join-Path $Managed 'Assembly-CSharp.dll'),
    (Join-Path $Managed 'Mirror.dll'),
    (Join-Path $Managed 'netstandard.dll'),
    (Join-Path $Managed 'UnityEngine.dll'),
    (Join-Path $Managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $Managed 'Unity.InputSystem.dll'),
    (Join-Path $Managed 'UnityEngine.IMGUIModule.dll'),
    (Join-Path $Managed 'UnityEngine.InputLegacyModule.dll'),
    (Join-Path $Managed 'UnityEngine.PhysicsModule.dll'),
    (Join-Path $Managed 'UnityEngine.UIModule.dll'),
    (Join-Path $Managed 'UnityEngine.TextRenderingModule.dll'),
    (Join-Path $Managed 'UnityEngine.UI.dll')
)

$args = @('/target:library', '/optimize+', '/debug:pdbonly', '/langversion:latest', "/out:$OutDir\CasinoMenu.dll")
$args += $references | ForEach-Object { "/reference:$($_)" }
$args += $sources
& $Compiler @args
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }
$dll = Join-Path $OutDir 'CasinoMenu.dll'
$pdb = Join-Path $OutDir 'CasinoMenu.pdb'
try {
    New-Item -ItemType Directory -Force $PluginDir -ErrorAction Stop | Out-Null
    Copy-Item $dll $PluginDir -Force -ErrorAction Stop
    if (Test-Path $pdb) { Copy-Item $pdb $PluginDir -Force -ErrorAction Stop }
    Write-Host "Build and install complete: $PluginDir\CasinoMenu.dll"
} catch [System.UnauthorizedAccessException] {
    Write-Warning "Build complete, but Windows denied writing to Program Files."
    Write-Host "DLL: $dll"
    Write-Host "Run PowerShell as Administrator, then copy it with:"
    Write-Host "Copy-Item -LiteralPath '$dll' -Destination '$PluginDir' -Force"
}

