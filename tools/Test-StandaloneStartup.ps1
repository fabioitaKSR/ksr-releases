param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference='Stop'
$testRoot=Join-Path $PSScriptRoot ('smoke-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot|Out-Null
$target=Join-Path $testRoot 'KSR-Launcher.exe'
Copy-Item -LiteralPath $Executable -Destination $target
$oldExtract=$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$oldTrace=$env:COREHOST_TRACE
$oldTraceFile=$env:COREHOST_TRACEFILE
$process=$null
try {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=Join-Path $testRoot 'extracted'
    $env:COREHOST_TRACE='1'
    $env:COREHOST_TRACEFILE=Join-Path $testRoot 'host-trace.txt'
    $process=Start-Process -FilePath $target -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if($process.HasExited){throw 'Standalone executable exited before opening its window'}
    } while($process.MainWindowTitle -ne 'KSR Platform' -and [DateTime]::UtcNow -lt $deadline)
    if($process.MainWindowTitle -ne 'KSR Platform'){throw 'Main window did not appear'}
    foreach($name in @('D3DCompiler_47_cor3.dll','PenImc_cor3.dll','PresentationNative_cor3.dll','vcruntime140_cor3.dll','wpfgfx_cor3.dll')) {
        if(-not (Get-ChildItem (Join-Path $testRoot 'extracted') -Recurse -Filter $name)){throw "Native library was not extracted: $name"}
    }
    Start-Sleep -Seconds 5
    $process.Refresh()
    if($process.HasExited -or $process.MainWindowTitle -ne 'KSR Platform'){throw 'Main window did not remain open'}
    [pscustomobject]@{Passed=$true;Executable=$Executable;SHA256=(Get-FileHash $target).Hash;Title=$process.MainWindowTitle;NativeLibraries=5;Evidence=$testRoot}|ConvertTo-Json|Tee-Object (Join-Path $PSScriptRoot 'standalone-result.json')
} finally {
    if($process -and -not $process.HasExited){$null=$process.CloseMainWindow()}
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=$oldExtract
    $env:COREHOST_TRACE=$oldTrace
    $env:COREHOST_TRACEFILE=$oldTraceFile
}
