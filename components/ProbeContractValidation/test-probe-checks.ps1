$ErrorActionPreference = 'Stop'

$contractDirectory = Join-Path $PSScriptRoot 'contracts'
$files = Get-ChildItem -LiteralPath $contractDirectory -File -Filter '*.cfg' | Sort-Object Name
$failures = [Collections.Generic.List[string]]::new()
$integrityCount = 0
$transmissionCount = 0

foreach ($file in $files) {
    $text = [IO.File]::ReadAllText($file.FullName)
    $targetMatch = [regex]::Match($text, '(?m)^    targetBody\s*=\s*(\S+)\s*$')
    if (-not $targetMatch.Success) {
        $failures.Add("$($file.Name): missing targetBody")
        continue
    }
    $targetBody = $targetMatch.Groups[1].Value

    $integrityMatches = [regex]::Matches($text, '(?m)^\s*name\s*=\s*ProbeIntegrity\s*$')
    $transmissionMatches = [regex]::Matches($text, '(?m)^\s*name\s*=\s*ProbeTransmission\s*$')
    $expected = if ($file.BaseName -match '1$') { 2 } else { 1 }
    if ($integrityMatches.Count -ne $expected) {
        $failures.Add("$($file.Name): expected $expected integrity checks, found $($integrityMatches.Count)")
    }
    if ($transmissionMatches.Count -ne $expected) {
        $failures.Add("$($file.Name): expected $expected transmission checks, found $($transmissionMatches.Count)")
    }

    $integrityCount += $integrityMatches.Count
    $transmissionCount += $transmissionMatches.Count
    if ([regex]::Matches($text, '(?m)^\s*type\s*=\s*VesselNotDestroyed\s*$').Count -ne $expected) {
        $failures.Add("$($file.Name): VesselNotDestroyed count mismatch")
    }
    if ([regex]::Matches($text, '(?m)^\s*recoveryMethod\s*=\s*Transmit\s*$').Count -ne $expected) {
        $failures.Add("$($file.Name): Transmit recovery count mismatch")
    }
    if ([regex]::Matches($text, '(?m)^\s*location\s*=\s*Surface\s*$').Count -lt $expected) {
        $failures.Add("$($file.Name): surface transmission filter missing")
    }
    if ([regex]::Matches($text, "(?m)^\s*targetBody\s*=\s*$([regex]::Escape($targetBody))\s*$").Count -lt ($expected + 1)) {
        $failures.Add("$($file.Name): transmission target body mismatch")
    }

    $balance = 0
    foreach ($character in $text.ToCharArray()) {
        if ($character -eq '{') { $balance++ }
        elseif ($character -eq '}') { $balance-- }
        if ($balance -lt 0) { break }
    }
    if ($balance -ne 0) {
        $failures.Add("$($file.Name): unbalanced braces")
    }
}

if ($integrityCount -ne 39 -or $transmissionCount -ne 39) {
    $failures.Add("Global count mismatch: integrity=$integrityCount transmission=$transmissionCount")
}

$humanContracts = @(
    'D:\CODEX\Moddare_KSP_Project\IotaU1.cfg',
    'D:\CODEX\Moddare_KSP_Project\CetiU1.cfg'
)
foreach ($path in $humanContracts) {
    $text = [IO.File]::ReadAllText($path)
    if ($text -match 'ProbeIntegrity|ProbeTransmission') {
        $failures.Add("Human contract unexpectedly modified: $path")
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "$($failures.Count) probe contract checks failed."
}

Write-Output "PASS: 26 contracts, 39 integrity checks, 39 vessel-bound surface transmissions."
Write-Output 'PASS: human rover contracts remain unchanged.'
