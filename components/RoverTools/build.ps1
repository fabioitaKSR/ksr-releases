param([string]$KspPath = 'F:\SteamLibrary\steamapps\common\Kerbal Space Race')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$managed = Join-Path $KspPath 'KSP_x64_Data\Managed'
$output = Join-Path $root 'KerbalSpaceRace.RoverTools.dll'
$references = @(
    'System.dll', 'System.Core.dll', 'Assembly-CSharp.dll', 'Assembly-CSharp-firstpass.dll',
    'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.AnimationModule.dll',
    'UnityEngine.ImageConversionModule.dll', 'UnityEngine.IMGUIModule.dll',
    'UnityEngine.PhysicsModule.dll', 'UnityEngine.UI.dll'
) | ForEach-Object { Join-Path $managed $_ }
$parameters = New-Object System.CodeDom.Compiler.CompilerParameters
$parameters.GenerateExecutable = $false
$parameters.GenerateInMemory = $false
$parameters.OutputAssembly = $output
$parameters.CompilerOptions = '/target:library /optimize'
foreach ($reference in $references) { [void]$parameters.ReferencedAssemblies.Add($reference) }
$results = (New-Object Microsoft.CSharp.CSharpCodeProvider).CompileAssemblyFromFile($parameters, (Join-Path $root 'KSRBonVoyageRoverTools.cs'))
if ($results.Errors.Count -gt 0) {
    $results.Errors | ForEach-Object { Write-Error $_.ToString() }
    throw 'Compilation failed.'
}
Write-Host "Built $output"
