param([string]$CompilerPath)
$ErrorActionPreference='Stop'
$compiler=Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\microsoft.net.compilers.toolset-4.8.0\tasks\net472\csc.exe'
if($CompilerPath){$compiler=$CompilerPath}
New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot 'build') | Out-Null
$exe=Join-Path $PSScriptRoot 'build\InstallerTests.exe'
& $compiler /nologo /target:exe /langversion:latest ('/out:'+$exe) (Join-Path $PSScriptRoot 'src\Installer.cs') (Join-Path $PSScriptRoot 'tests\InstallerTests.cs')
if($LASTEXITCODE -ne 0){throw 'Test compilation failed'}
& $exe (Join-Path $PSScriptRoot ('build\test-'+[Guid]::NewGuid().ToString('N')))
if($LASTEXITCODE -ne 0){throw 'Installer tests failed'}
