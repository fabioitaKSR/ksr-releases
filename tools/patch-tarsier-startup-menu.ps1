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
    throw 'InputDll and OutputDll must be different files.'
}

$outputDirectory = Split-Path -Parent $outputFull
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

Add-Type -LiteralPath $cecilFull

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($inputFull)
try {
    $type = $assembly.MainModule.Types |
        Where-Object FullName -eq 'TarsierSpaceTech.TSTMenu' |
        Select-Object -First 1
    if ($null -eq $type) {
        throw 'TarsierSpaceTech.TSTMenu not found.'
    }

    $awake = $type.Methods |
        Where-Object { $_.Name -eq 'Awake' -and -not $_.HasParameters } |
        Select-Object -First 1
    if ($null -eq $awake -or -not $awake.HasBody -or $awake.Body.ExceptionHandlers.Count -ne 0) {
        throw 'TSTMenu.Awake is missing or has an unexpected body.'
    }

    $destroyCalls = @($awake.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq 'Destroy' -and
        $_.Operand.DeclaringType.FullName -eq 'UnityEngine.Object'
    })
    if ($destroyCalls.Count -ne 1 -or
        $destroyCalls[0].Previous.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ldarg_0 -or
        $destroyCalls[0].Next.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Nop) {
        throw 'Expected TSTMenu.Awake Destroy(this) sequence not found.'
    }

    $textureCalls = @($awake.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq 'GetTexture' -and
        $_.Operand.DeclaringType.FullName -eq 'GameDatabase'
    })
    if ($textureCalls.Count -ne 2) {
        throw "Expected two TSTMenu.Awake texture lookups; found $($textureCalls.Count)."
    }

    $awake.Body.GetILProcessor().InsertAfter(
        $destroyCalls[0],
        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
    $assembly.Write($outputFull)
}
finally {
    $assembly.Dispose()
}

[pscustomobject]@{
    Input = $inputFull
    Output = $outputFull
    Sha256 = (Get-FileHash -LiteralPath $outputFull -Algorithm SHA256).Hash.ToLowerInvariant()
    PatchedMethod = 'TarsierSpaceTech.TSTMenu.Awake'
    Change = 'Return immediately after Destroy(this) outside SpaceCenter and Flight'
}
