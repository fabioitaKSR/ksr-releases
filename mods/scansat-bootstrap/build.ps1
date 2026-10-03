param([string]$KspRoot='F:\SteamLibrary\steamapps\common\Kerbal Space Race beta', [string]$CompilerPath)
$ErrorActionPreference='Stop'
$compiler=Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\microsoft.net.compilers.toolset-4.8.0\tasks\net472\csc.exe'
if($CompilerPath){$compiler=$CompilerPath}
$managed=Join-Path $KspRoot 'KSP_x64_Data\Managed'
New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot 'build') | Out-Null
$arguments=@('/nologo','/target:library','/optimize+','/langversion:latest',('/out:'+(Join-Path $PSScriptRoot 'build\KerbalSpaceRace.SCANsatBootstrap.dll')))
$arguments+=@(Get-ChildItem $managed -Filter 'UnityEngine*.dll' | ForEach-Object {'/reference:'+ $_.FullName})
$arguments+='/reference:'+(Join-Path $managed 'Assembly-CSharp.dll')
$arguments+=@('Installer.cs','Bootstrap.cs') | ForEach-Object {Join-Path $PSScriptRoot ('src\'+$_)}
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'Bootstrap compilation failed'}
& $compiler /nologo /target:exe /optimize+ /langversion:latest ('/out:'+(Join-Path $PSScriptRoot 'build\KSR.SCANsatInstaller.exe')) (Join-Path $PSScriptRoot 'src\Installer.cs') (Join-Path $PSScriptRoot 'src\Helper.cs')
if($LASTEXITCODE -ne 0){throw 'Deferred helper compilation failed'}
