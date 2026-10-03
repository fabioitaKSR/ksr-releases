param([Parameter(Mandatory=$true)][string]$KspPath, [Parameter(Mandatory=$true)][string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$workspace = Split-Path -Parent $root
$managed = Join-Path $KspPath 'KSP_x64_Data\Managed'
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
New-Item -ItemType Directory -Force (Join-Path $root 'build') | Out-Null
$references = @('mscorlib.dll','System.dll','System.Core.dll','Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','UnityEngine.dll','UnityEngine.CoreModule.dll') | ForEach-Object { Join-Path $managed $_ }
$references += Join-Path $KspPath 'GameData\000_Harmony\0Harmony.dll'
$references += Join-Path $KspPath 'GameData\SpaceAge\SpaceAge.dll'
$out = Join-Path $root 'build\KerbalSpaceRace.LoggerPerformance.dll'
$compilerArguments = @('/nologo','/noconfig','/nostdlib+','/target:library','/optimize+','/langversion:7.3',('/out:' + $out))
$compilerArguments += $references | ForEach-Object { '/reference:' + $_ }
$compilerArguments += Join-Path $root 'src\ExportCore.cs'
$compilerArguments += Join-Path $root 'src\LoggerPerformance.cs'
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'Logger performance compilation failed.' }
Write-Output "Built $out"
