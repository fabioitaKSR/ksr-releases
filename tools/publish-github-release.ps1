[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$AssetsDirectory,
    [string]$Title = $Tag,
    [string]$Notes = '',
    [switch]$CreateDraft,
    [switch]$AllowPublished
)

$ErrorActionPreference = 'Stop'
$credentialInput = "protocol=https`nhost=github.com`n`n"
$credentialLines = @($credentialInput | git credential fill)
$passwordLine = $credentialLines | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($passwordLine)) {
    throw 'No GitHub credential is available through Git Credential Manager.'
}
$token = $passwordLine.Substring('password='.Length)
$headers = @{
    Authorization = "Bearer $token"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
}

$apiRoot = "https://api.github.com/repos/$Repository"
$matches = @()
foreach ($page in 1..10) {
    $batch = @(Invoke-RestMethod -Method Get -Uri "$apiRoot/releases?per_page=100&page=$page" -Headers $headers)
    $matches += @($batch | Where-Object { $_.tag_name -eq $Tag })
    if ($batch.Count -lt 100) { break }
}
if ($matches.Count -gt 1) { throw "Multiple releases use tag '$Tag'; resolve duplicates by release ID first." }
if ($matches.Count -eq 1) {
    $release = $matches[0]
} elseif ($CreateDraft) {
    $body = @{
        tag_name = $Tag
        name = $Title
        body = $Notes
        draft = $true
        prerelease = $false
    } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Post -Uri "$apiRoot/releases" -Headers $headers -ContentType 'application/json' -Body $body
} else {
    throw "Release '$Tag' not found. Use -CreateDraft only when creating a new draft."
}
if (-not $release.draft -and -not $AllowPublished) {
    throw "Release '$Tag' is public. Pass -AllowPublished only after explicit release review."
}

$uploadRoot = ($release.upload_url -replace '\{\?name,label\}$', '')
$existing = @($release.assets | ForEach-Object { $_.name })
foreach ($asset in Get-ChildItem -LiteralPath $AssetsDirectory -File | Sort-Object Name) {
    if ($asset.Name -in $existing) {
        Write-Host "Already uploaded: $($asset.Name)"
        continue
    }
    $escapedName = [Uri]::EscapeDataString($asset.Name)
    Write-Host "Uploading $($asset.Name) ($([Math]::Round($asset.Length / 1MB, 1)) MiB)..."
    Invoke-RestMethod -Method Post -Uri "${uploadRoot}?name=$escapedName" -Headers $headers -ContentType 'application/octet-stream' -InFile $asset.FullName | Out-Null
}

Write-Host "Release ID $($release.id) (draft=$($release.draft)): $($release.html_url)"
