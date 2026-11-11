$ErrorActionPreference = 'Stop'

$contractDirectory = Join-Path $PSScriptRoot 'contracts'
$files = Get-ChildItem -LiteralPath $contractDirectory -File -Filter '*.cfg' | Sort-Object Name
if ($files.Count -ne 26) {
    throw "Expected 26 contract files, found $($files.Count)."
}

$updatedGroups = 0
foreach ($file in $files) {
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
        $lines.Add($line)
    }

    $targetBodyLine = $lines | Where-Object { $_ -match '^    targetBody\s*=\s*(\S+)\s*$' } | Select-Object -First 1
    if (-not $targetBodyLine -or $targetBodyLine -notmatch '^    targetBody\s*=\s*(\S+)\s*$') {
        throw "Missing contract targetBody in $($file.Name)."
    }
    $targetBody = $Matches[1]

    $groups = [Collections.Generic.List[object]]::new()
    $stack = [Collections.Generic.List[object]]::new()
    $pendingNode = $null

    for ($index = 0; $index -lt $lines.Count; $index++) {
        $trimmed = $lines[$index].Trim()
        if ($trimmed -match '^[A-Z_]+$') {
            $pendingNode = [pscustomobject]@{ Kind = $trimmed; Start = $index; Name = $null; Type = $null }
            continue
        }
        if ($trimmed -eq '{') {
            if ($null -eq $pendingNode) {
                throw "Unexpected opening brace in $($file.Name) at line $($index + 1)."
            }
            $stack.Add($pendingNode)
            $pendingNode = $null
            continue
        }
        if ($trimmed -eq '}') {
            if ($stack.Count -eq 0) {
                throw "Unexpected closing brace in $($file.Name) at line $($index + 1)."
            }
            $node = $stack[$stack.Count - 1]
            $stack.RemoveAt($stack.Count - 1)
            if ($node.Kind -eq 'PARAMETER' -and $node.Type -eq 'VesselParameterGroup' -and
                ($node.Name -match '^Landing' -or $node.Name -match '^(Advanced)?Rover')) {
                $groups.Add([pscustomobject]@{ Start = $node.Start; End = $index; Name = $node.Name })
            }
            continue
        }

        if ($stack.Count -gt 0) {
            $current = $stack[$stack.Count - 1]
            if ($lines[$index] -match '^\s*name\s*=\s*(\S+)\s*$' -and -not $current.Name) {
                $current.Name = $Matches[1]
            }
            elseif ($lines[$index] -match '^\s*type\s*=\s*(\S+)\s*$' -and -not $current.Type) {
                $current.Type = $Matches[1]
            }
        }
    }

    if ($stack.Count -ne 0) {
        throw "Unclosed node in $($file.Name)."
    }

    $expected = if ($file.BaseName -match '1$') { 2 } else { 1 }
    if ($groups.Count -ne $expected) {
        throw "Expected $expected automatic lander/rover groups in $($file.Name), found $($groups.Count)."
    }

    foreach ($group in ($groups | Sort-Object End -Descending)) {
        $existing = ($lines[$group.Start..$group.End] -join "`n")
        if ($existing -match 'name\s*=\s*ProbeIntegrity' -or $existing -match 'name\s*=\s*ProbeTransmission') {
            throw "Probe checks already exist in $($file.Name), group $($group.Name)."
        }

        $vehicle = if ($group.Name -match '^Landing') { 'lander' } else { 'rover' }
        $bodyTitle = $targetBody
        $block = [string[]]@(
            '',
            '        PARAMETER',
            '        {',
            '            name = ProbeIntegrity',
            '            type = VesselNotDestroyed',
            "            title = Mantieni il $vehicle integro e operativo",
            '        }',
            '',
            '        PARAMETER',
            '        {',
            '            name = ProbeTransmission',
            '            type = CollectScience',
            "            targetBody = $targetBody",
            '            location = Surface',
            '            recoveryMethod = Transmit',
            "            title = Trasmetti dati scientifici dalla superficie di $bodyTitle con il $vehicle",
            '            disableOnStateChange = true',
            '        }'
        )
        $lines.InsertRange($group.End, $block)
        $updatedGroups++
    }

    [IO.File]::WriteAllLines($file.FullName, $lines, [Text.UTF8Encoding]::new($false))
}

if ($updatedGroups -ne 39) {
    throw "Expected to update 39 groups, updated $updatedGroups."
}

Write-Output "Updated $updatedGroups automatic lander/rover groups in $($files.Count) contracts."
