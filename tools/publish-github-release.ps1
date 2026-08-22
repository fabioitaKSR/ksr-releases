[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$AssetsDirectory,
    [string]$Title = $Tag,
    [string]$Notes = ''
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
try {
    $release = Invoke-RestMethod -Method Get -Uri "$apiRoot/releases/tags/$Tag" -Headers $headers
} catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
    $body = @{
        tag_name = $Tag
        name = $Title
        body = $Notes
        draft = $false
        prerelease = $false
    } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Post -Uri "$apiRoot/releases" -Headers $headers -ContentType 'application/json' -Body $body
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

Write-Host $release.html_url
