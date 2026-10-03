param([Parameter(Mandatory=$true)][string]$KspRoot, [Parameter(Mandatory=$true)][string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$project = $PSScriptRoot
$workspace = Split-Path -Parent $project
$compiler = $CompilerPath
$managed = Join-Path $KspRoot 'KSP_x64_Data\Managed'
$build = Join-Path $project 'build'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$references = @(Get-ChildItem -LiteralPath $managed -Filter 'UnityEngine*.dll' | ForEach-Object FullName)
$references += @('Assembly-CSharp.dll', 'Assembly-CSharp-firstpass.dll') | ForEach-Object { Join-Path $managed $_ }
$references += Join-Path $KspRoot 'GameData\SCANsat\Plugins\SCANsat.Unity.dll'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $project 'source\SCANsat') -Recurse -Filter '*.cs' | ForEach-Object FullName)
$sources += Join-Path $project 'vendor\KSPBuildTools\build\include\Log.cs'
$sources += Join-Path $project 'AssemblyInfo.cs'
$arguments = @('/nologo', '/target:library', '/langversion:latest', '/optimize+', '/debug-', ('/out:' + (Join-Path $build 'SCANsat.dll')))
$arguments += $references | ForEach-Object { '/reference:' + $_ }
$arguments += $sources
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
Get-FileHash -LiteralPath (Join-Path $build 'SCANsat.dll') -Algorithm SHA256

