param([string]$KspPath='F:\SteamLibrary\steamapps\common\Kerbal Space Race')
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -KspPath $KspPath
$managed=Join-Path $KspPath 'KSP_x64_Data\Managed'
$output=Join-Path $PSScriptRoot 'tests-output'
New-Item -ItemType Directory -Force -Path $output | Out-Null
foreach($name in @('Assembly-CSharp.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.UI.dll')){
    Copy-Item -LiteralPath (Join-Path $managed $name) -Destination $output -Force
}
Get-ChildItem -LiteralPath $managed -Filter 'UnityEngine.*.dll' |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $output -Force }
Copy-Item -LiteralPath (Join-Path $KspPath 'GameData\ContractConfigurator\ContractConfigurator.dll') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build\KerbalSpaceRace.ContractRendezvous.dll') -Destination $output -Force
$references=@(
    (Join-Path $output 'KerbalSpaceRace.ContractRendezvous.dll'),
    (Join-Path $output 'ContractConfigurator.dll'),
    (Join-Path $managed 'Assembly-CSharp.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $managed 'UnityEngine.UI.dll'))
$arguments=@('/nologo','/target:exe',('/out:'+(Join-Path $output 'RulesTests.exe')))
$arguments+=@($references | ForEach-Object { '/reference:'+$_ })
$arguments+=Join-Path $PSScriptRoot 'tests\RulesTests.cs'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' @arguments
if($LASTEXITCODE){throw 'Test compilation failed'}
& (Join-Path $output 'RulesTests.exe') | Tee-Object (Join-Path $PSScriptRoot 'test-results.txt')
if($LASTEXITCODE){throw 'Tests failed'}
