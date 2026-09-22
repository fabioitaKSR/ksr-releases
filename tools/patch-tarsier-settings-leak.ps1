[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InputDll,
    [Parameter(Mandatory = $true)][string]$OutputDll,
    [Parameter(Mandatory = $true)][string]$CecilPath
)

$ErrorActionPreference = 'Stop'

$inputFull = (Resolve-Path -LiteralPath $InputDll).Path
$cecilFull = (Resolve-Path -LiteralPath $CecilPath).Path
$outputFull = [IO.Path]::GetFullPath($OutputDll)

if ($inputFull.Equals($outputFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'InputDll e OutputDll devono essere file differenti.'
}

$outputDirectory = Split-Path -Parent $outputFull
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

Add-Type -LiteralPath $cecilFull

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($inputFull)
try {
    $type = $assembly.MainModule.Types |
        Where-Object FullName -eq 'TarsierSpaceTech.TSTMstStgs' |
        Select-Object -First 1
    if ($null -eq $type) {
        throw 'Tipo TarsierSpaceTech.TSTMstStgs non trovato.'
    }

    $onDestroy = $type.Methods |
        Where-Object { $_.Name -eq 'OnDestroy' -and -not $_.HasParameters } |
        Select-Object -First 1
    if ($null -eq $onDestroy -or -not $onDestroy.HasBody) {
        throw 'Metodo TSTMstStgs.OnDestroy non trovato o privo di corpo IL.'
    }

    $addCalls = @($onDestroy.Body.Instructions | Where-Object {
        ($_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -or
         $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Callvirt) -and
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq 'Add' -and
        $_.Operand.DeclaringType.FullName -eq 'EventVoid'
    })
    if ($addCalls.Count -ne 1) {
        throw "Attesa una sola chiamata EventVoid.Add in OnDestroy; trovate $($addCalls.Count)."
    }

    $addReference = [Mono.Cecil.MethodReference]$addCalls[0].Operand
    $removeReference = $assembly.MainModule.GetMemberReferences() |
        Where-Object {
            $_ -is [Mono.Cecil.MethodReference] -and
            $_.Name -eq 'Remove' -and
            $_.DeclaringType.FullName -eq $addReference.DeclaringType.FullName -and
            $_.Parameters.Count -eq $addReference.Parameters.Count -and
            $_.ReturnType.FullName -eq $addReference.ReturnType.FullName
        } |
        Select-Object -First 1
    if ($null -eq $removeReference) {
        throw 'Riferimento EventVoid.Remove compatibile non trovato nella DLL.'
    }

    $addCalls[0].Operand = $removeReference
    $assembly.Write($outputFull)
}
finally {
    $assembly.Dispose()
}

$hash = (Get-FileHash -LiteralPath $outputFull -Algorithm SHA256).Hash.ToLowerInvariant()
[pscustomobject]@{
    Input = $inputFull
    Output = $outputFull
    Sha256 = $hash
    PatchedMethod = 'TarsierSpaceTech.TSTMstStgs.OnDestroy'
    Change = 'GameEvents.OnGameSettingsApplied.Add -> Remove'
}
