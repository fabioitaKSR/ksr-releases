$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '..\KSRBonVoyageRoverTools.cs') -Raw
$split = $source.IndexOf('    public sealed class KSRBonVoyageRoverTools : PartModule')
if ($split -lt 0) { throw 'Protection class boundary missing' }
$out = Join-Path $PSScriptRoot 'output'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$extracted = Join-Path $out 'ProtectionUnderTest.cs'
[IO.File]::WriteAllText($extracted, $source.Substring(0,$split) + "}`r`n")
$exe = Join-Path $out 'ProtectionHarness.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /optimize "/out:$exe" $extracted (Join-Path $PSScriptRoot 'ProtectionHarness.cs')
if ($LASTEXITCODE) { throw 'Harness compilation failed' }
& $exe
if ($LASTEXITCODE) { throw 'Regression tests failed' }
