param([Parameter(Mandatory=$true)][string]$KspPath, [Parameter(Mandatory=$true)][string]$CompilerPath)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$workspace=Split-Path -Parent $root
$compiler=(Resolve-Path -LiteralPath $CompilerPath).Path
New-Item -ItemType Directory -Force (Join-Path $root 'build') | Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$harmony=Join-Path $KspPath 'GameData\000_Harmony\0Harmony.dll'
$argsList=@('/nologo','/noconfig','/nostdlib+','/target:exe','/optimize+','/langversion:7.3',('/out:'+(Join-Path $root 'build\PerformanceTests.exe')))
foreach($file in @('mscorlib.dll','System.dll','System.Core.dll')) { $argsList+=('/reference:'+(Join-Path $framework $file)) }
$argsList+=('/reference:'+$harmony)
foreach($file in @('src\ExportCore.cs','src\LoggerPerformance.cs','tests\RuntimeStubs.cs','tests\PerformanceTests.cs')) { $argsList+=Join-Path $root $file }
& $compiler @argsList
if($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
Copy-Item -LiteralPath $harmony -Destination (Join-Path $root 'build\0Harmony.dll')
$run=Join-Path $root ('build\test-run-'+[guid]::NewGuid().ToString('N'))
& (Join-Path $root 'build\PerformanceTests.exe') $run
if($LASTEXITCODE -ne 0) { throw 'Performance tests failed' }
