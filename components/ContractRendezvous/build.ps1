param([string]$KspPath='F:\SteamLibrary\steamapps\common\Kerbal Space Race')
$ErrorActionPreference='Stop'
$managed=Join-Path $KspPath 'KSP_x64_Data\Managed'
$references=@('mscorlib.dll','System.dll','System.Core.dll','Assembly-CSharp.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.UI.dll') |
    ForEach-Object { Join-Path $managed $_ }
$references+=Join-Path $KspPath 'GameData\ContractConfigurator\ContractConfigurator.dll'
$output=Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$arguments=@('/nologo','/noconfig','/nostdlib','/target:library','/optimize+',
    ('/out:'+(Join-Path $output 'KerbalSpaceRace.ContractRendezvous.dll')))
$arguments+=@($references | ForEach-Object { '/reference:'+$_ })
$arguments+=@(Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' @arguments
if($LASTEXITCODE){throw 'Build failed'}
Get-FileHash (Join-Path $output 'KerbalSpaceRace.ContractRendezvous.dll')
