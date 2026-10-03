param([Parameter(Mandatory=$true)][string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$compiler = $CompilerPath
New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot 'build') | Out-Null
$exe = Join-Path $PSScriptRoot 'build\CacheTests.exe'
& $compiler /nologo /target:exe /langversion:latest "/out:$exe" `
    (Join-Path $PSScriptRoot 'tests\CacheTests.cs') (Join-Path $PSScriptRoot 'tests\KspStubs.cs') `
    (Join-Path $PSScriptRoot 'source\SCANsat\SCAN_Data\SCANheightMapCacheFile.cs') `
    (Join-Path $PSScriptRoot 'source\SCANsat\SCAN_Data\SCANheightMapCache.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
$testRoot = Join-Path $PSScriptRoot ('build\sc-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
& $exe $testRoot
if ($LASTEXITCODE -ne 0) { throw 'Codec tests failed' }
$integration = Join-Path $testRoot 'integration'
$assetDir = Join-Path $integration 'GameData\GPP'
New-Item -ItemType Directory -Path $assetDir -Force | Out-Null
$asset = Join-Path $assetDir 'height.dds'
[IO.File]::WriteAllBytes($asset, [byte[]](1,2,3,4))
foreach ($mode in @('save', 'load', 'load-extra-runtime')) {
    & $exe $integration $mode
    if ($LASTEXITCODE -ne 0) { throw "Integration test failed: $mode" }
}
Set-Content -LiteralPath (Join-Path $integration 'GameData\ModuleManager.ConfigCache') -Value 'regenerated output; no terrain changes'
& $exe $integration load
if ($LASTEXITCODE -ne 0) { throw 'Transient ModuleManager output changed stable fingerprint' }
$stamp = (Get-Item -LiteralPath $asset).LastWriteTimeUtc
[IO.File]::WriteAllBytes($asset, [byte[]](1,2,3,5))
(Get-Item -LiteralPath $asset).LastWriteTimeUtc = $stamp
# Identical size and timestamp: content change must still invalidate the cache.
foreach ($mode in @('invalidated', 'load', 'disabled')) {
    & $exe $integration $mode
    if ($LASTEXITCODE -ne 0) { throw "Integration test failed: $mode" }
}
Write-Output 'All cache tests passed.'


