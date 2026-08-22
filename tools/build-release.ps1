[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$KspRoot,

    [string]$ServerSource,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts'),

    [string[]]$Component = @('all'),

    [ValidateSet('stable', 'beta')]
    [string]$Channel = 'stable',

    [switch]$DryRun,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-ExistingDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label non trovata: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Convert-ToManifestPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return $Path.Replace('\', '/')
}

function Test-GeneralExclusion {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $path = Convert-ToManifestPath $RelativePath
    $name = [IO.Path]::GetFileName($path)

    if ($path -match '(^|/)__pycache__(/|$)') { return $true }
    if ($name -match '(?i)\.py[co]$') { return $true }
    if ($name -match '(?i)\.bak(?:$|[._-])') { return $true }
    if ($name -match '(?i)\.backup(?:$|[._-])') { return $true }
    if ($name -match '(?i)\.old(?:$|[._-]|\d+$)') { return $true }
    if ($name -match '(?i)\.disabled(?:$|\.)') { return $true }
    if ($name -match '(?i)\.(?:log|sqlite|sqlite3|db|rar|tmp)$') { return $true }
    if ($name -match '(?i)^remote_logger_paths\.json$') { return $true }
    if ($name -match '(?i)^RemoteLogger\.cfg$') { return $true }
    if ($name -match '(?i)\.cs$') { return $true }

    return $false
}

function Test-PathPrefix {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Prefix
    )

    $path = (Convert-ToManifestPath $RelativePath).TrimStart('/')
    $normalizedPrefix = (Convert-ToManifestPath $Prefix).Trim('/')
    return $path.Equals($normalizedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $path.StartsWith($normalizedPrefix + '/', [StringComparison]::OrdinalIgnoreCase)
}

function Test-ComponentInclusion {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Definition,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $path = Convert-ToManifestPath $RelativePath

    foreach ($explicitPath in $Definition.ExplicitFiles) {
        if ($path.Equals($explicitPath, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    if ($Definition.ExplicitOnly) {
        return $false
    }

    foreach ($prefix in $Definition.ExcludePrefixes) {
        if (Test-PathPrefix -RelativePath $path -Prefix $prefix) {
            return $false
        }
    }

    if (Test-GeneralExclusion -RelativePath $path) {
        return $false
    }

    if ($Definition.AllowedTopEntries.Count -gt 0) {
        $topEntry = $path.Split('/')[0]
        if ($Definition.AllowedTopEntries -notcontains $topEntry) {
            return $false
        }
    }

    return $true
}

function Assert-RequiredPaths {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Definition,
        [Parameter(Mandatory = $true)][string]$SourceRoot
    )

    foreach ($requiredPath in $Definition.RequiredPaths) {
        $fullPath = Join-Path $SourceRoot $requiredPath
        if (-not (Test-Path -LiteralPath $fullPath)) {
            throw "Componente '$($Definition.Id)' incompleto. File o cartella obbligatoria mancante: $fullPath"
        }
    }
}

function Get-IncludedFiles {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Definition,
        [Parameter(Mandatory = $true)][string]$SourceRoot
    )

    $result = [Collections.Generic.List[object]]::new()
    $files = Get-ChildItem -LiteralPath $SourceRoot -Recurse -File

    foreach ($file in $files) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -and
            -not [string]::IsNullOrWhiteSpace([string]$file.LinkType)) {
            throw "Link o reparse point non consentito nel pacchetto: $($file.FullName)"
        }

        $relativePath = [IO.Path]::GetRelativePath($SourceRoot, $file.FullName)
        if (Test-ComponentInclusion -Definition $Definition -RelativePath $relativePath) {
            $result.Add([PSCustomObject]@{
                Source       = $file.FullName
                RelativePath = Convert-ToManifestPath $relativePath
                Length       = $file.Length
            })
        }
    }

    return $result
}

function Assert-SafeStagingPath {
    param(
        [Parameter(Mandatory = $true)][string]$StagingPath,
        [Parameter(Mandatory = $true)][string]$OutputRoot
    )

    $stagingFull = [IO.Path]::GetFullPath($StagingPath).TrimEnd('\')
    $outputFull = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
    $expectedPrefix = $outputFull + [IO.Path]::DirectorySeparatorChar + '.staging-'

    if (-not $stagingFull.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Percorso staging non sicuro: $stagingFull"
    }
}

$kspRootFull = Resolve-ExistingDirectory -Path $KspRoot -Label 'Cartella KSP'
if (-not (Test-Path -LiteralPath (Join-Path $kspRootFull 'KSP_x64.exe') -PathType Leaf)) {
    throw "La cartella indicata non contiene KSP_x64.exe: $kspRootFull"
}

$gameDataRoot = Resolve-ExistingDirectory -Path (Join-Path $kspRootFull 'GameData') -Label 'GameData'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)

$definitions = @(
    @{
        Id = 'ksr-core'
        AssetPrefix = 'KSR-Core'
        SourceRoot = Join-Path $gameDataRoot 'KerbalSpaceRace'
        PackageRoot = 'GameData/KerbalSpaceRace'
        Target = 'GameData/KerbalSpaceRace'
        TargetKind = 'ksp'
        ExplicitOnly = $false
        ExplicitFiles = @('PluginData/KSRDifficultyReference.cfg')
        ExcludePrefixes = @('PluginData')
        AllowedTopEntries = @('KCT_Presets', 'KSPedia', 'LoadingScreens', 'MenuOverlay', 'Patches', 'Plugins', 'StationScience', 'Tools', 'README.txt')
        RequiredPaths = @('Plugins/KSRRemoteLogger.dll', 'Plugins/KerbalSpaceRace.MenuEditionOverlay.dll', 'Plugins/KerbalSpaceRace.MenuKerbalSuits.dll', 'Plugins/KerbalSpaceRace.RoverTools.dll', 'PluginData/KSRDifficultyReference.cfg')
    },
    @{
        Id = 'nation-selector'
        AssetPrefix = 'KSR-NationSelector'
        SourceRoot = Join-Path $gameDataRoot 'KerbalSpaceRaceNationSelector'
        PackageRoot = 'GameData/KerbalSpaceRaceNationSelector'
        Target = 'GameData/KerbalSpaceRaceNationSelector'
        TargetKind = 'ksp'
        ExplicitOnly = $false
        ExplicitFiles = @()
        ExcludePrefixes = @('PluginData', 'Patches/Generated')
        AllowedTopEntries = @()
        RequiredPaths = @('Plugins/KerbalSpaceRace.NationSelector.dll', 'Flags', 'NationSuits', 'ShuttleNameTextures')
    },
    @{
        Id = 'ksr-suit-pack'
        AssetPrefix = 'KSR-SuitPack'
        SourceRoot = Join-Path $gameDataRoot 'KerbalSpaceRaceSuite'
        PackageRoot = 'GameData/KerbalSpaceRaceSuite'
        Target = 'GameData/KerbalSpaceRaceSuite'
        TargetKind = 'ksp'
        ExplicitOnly = $false
        ExplicitFiles = @()
        ExcludePrefixes = @()
        AllowedTopEntries = @()
        RequiredPaths = @('Default', 'Default-MKS', 'Future', 'Future-MKS', 'Slim', 'Slim-MKS')
    },
    @{
        Id = 'contract-pack'
        AssetPrefix = 'KSR-ContractPack'
        SourceRoot = Join-Path $gameDataRoot 'ContractPacks/KerbalSpaceRace'
        PackageRoot = 'GameData/ContractPacks/KerbalSpaceRace'
        Target = 'GameData/ContractPacks/KerbalSpaceRace'
        TargetKind = 'ksp'
        ExplicitOnly = $false
        ExplicitFiles = @()
        ExcludePrefixes = @()
        AllowedTopEntries = @()
        RequiredPaths = @('KerbalSpaceRace_Agency.cfg', 'KerbalSpaceRace_Group.cfg', 'KSR_ContractDependencies.cfg')
    },
    @{
        Id = 'parameter-logger'
        AssetPrefix = 'KSR-ParameterLogger'
        SourceRoot = Join-Path $gameDataRoot 'KSRParameterLogger'
        PackageRoot = 'GameData/KSRParameterLogger'
        Target = 'GameData/KSRParameterLogger'
        TargetKind = 'ksp'
        ExplicitOnly = $true
        ExplicitFiles = @('Plugins/KSRParameterLogger.dll')
        ExcludePrefixes = @('PluginData')
        AllowedTopEntries = @()
        RequiredPaths = @('Plugins/KSRParameterLogger.dll')
    },
    @{
        Id = 'achievements'
        AssetPrefix = 'KSP-Achievements'
        SourceRoot = Join-Path $gameDataRoot 'Achievements'
        PackageRoot = 'GameData/Achievements'
        Target = 'GameData/Achievements'
        TargetKind = 'ksp'
        ExplicitOnly = $false
        ExplicitFiles = @()
        ExcludePrefixes = @()
        AllowedTopEntries = @()
        RequiredPaths = @('Plugins/Achievements.dll', 'Achievements.version', 'LICENSE.txt')
    },
    @{
        Id = 'click-through-blocker'
        AssetPrefix = 'KSP-ClickThroughBlocker'
        SourceRoot = Join-Path $gameDataRoot '000_ClickThroughBlocker'
        PackageRoot = 'GameData/000_ClickThroughBlocker'
        Target = 'GameData/000_ClickThroughBlocker'
        TargetKind = 'ksp'
        ExplicitOnly = $false
        ExplicitFiles = @()
        ExcludePrefixes = @()
        AllowedTopEntries = @()
        RequiredPaths = @('Plugins/ClickThroughBlocker.dll')
    },
    @{
        Id = 'toolbar-controller'
        AssetPrefix = 'KSP-ToolbarController'
        SourceRoot = Join-Path $gameDataRoot '001_ToolbarControl'
        PackageRoot = 'GameData/001_ToolbarControl'
        Target = 'GameData/001_ToolbarControl'
        TargetKind = 'ksp'
        ExplicitOnly = $false
        ExplicitFiles = @()
        ExcludePrefixes = @()
        AllowedTopEntries = @()
        RequiredPaths = @('Plugins/ToolbarControl.dll')
    },
    @{
        Id = 'spacetux-library'
        AssetPrefix = 'KSP-SpaceTuxLibrary'
        SourceRoot = Join-Path $gameDataRoot 'SpaceTuxLibrary'
        PackageRoot = 'GameData/SpaceTuxLibrary'
        Target = 'GameData/SpaceTuxLibrary'
        TargetKind = 'ksp'
        ExplicitOnly = $false
        ExplicitFiles = @()
        ExcludePrefixes = @()
        AllowedTopEntries = @()
        RequiredPaths = @('Plugins/SpaceTuxUtility.dll')
    },
    @{
        Id = 'disable-dbs-ui'
        AssetPrefix = 'KSR-DisableDBSUI'
        SourceRoot = Join-Path $gameDataRoot 'KSRDisableDBSUI'
        PackageRoot = 'GameData/KSRDisableDBSUI'
        Target = 'GameData/KSRDisableDBSUI'
        TargetKind = 'ksp'
        ExplicitOnly = $true
        ExplicitFiles = @('Plugins/KSRDisableDBSUI.dll')
        ExcludePrefixes = @()
        AllowedTopEntries = @()
        RequiredPaths = @('Plugins/KSRDisableDBSUI.dll')
    }
)

if (-not [string]::IsNullOrWhiteSpace($ServerSource)) {
    $serverSourceFull = Resolve-ExistingDirectory -Path $ServerSource -Label 'Sorgente Remote Logger Server'
    $serverProgramPath = Join-Path $serverSourceFull 'remote_logger_server.py'
    $serverProgramText = Get-Content -LiteralPath $serverProgramPath -Raw
    if ($serverProgramText -match 'DEFAULT_TOKEN\s*=\s*["'']dev-token["'']') {
        Write-Warning 'Il Remote Logger Server usa ancora il token predefinito di sviluppo. Sostituirlo con una configurazione al primo avvio prima della release pubblica.'
    }

    $definitions += @{
        Id = 'remote-logger-server'
        AssetPrefix = 'KSR-RemoteLoggerServer'
        SourceRoot = $serverSourceFull
        PackageRoot = 'RemoteLoggerServer'
        Target = 'RemoteLoggerServer'
        TargetKind = 'launcherData'
        ExplicitOnly = $true
        ExplicitFiles = @(
            'CLOUDFLARE_TUNNEL_KSR.md',
            'install_autostart.ps1',
            'ISTRUZIONI_SERVER_KSR_PER_ALTRO_PC.md',
            'ksr_sheet_catalog.csv',
            'README_INSTALLAZIONE_SERVER.md',
            'remote_logger_server.py',
            'start_remote_logger.ps1',
            'start_server.ps1',
            'stop_server.ps1',
            'uninstall_autostart.ps1'
        )
        ExcludePrefixes = @()
        AllowedTopEntries = @()
        RequiredPaths = @('remote_logger_server.py', 'ksr_sheet_catalog.csv', 'start_server.ps1', 'stop_server.ps1')
    }
}

$requestedIds = @($Component | ForEach-Object { $_.Trim().ToLowerInvariant() })
if ($requestedIds -contains 'all') {
    if ([string]::IsNullOrWhiteSpace($ServerSource)) {
        throw 'Per compilare tutti i componenti e obbligatorio specificare -ServerSource.'
    }
    $selectedDefinitions = @($definitions)
} else {
    $knownIds = @($definitions | ForEach-Object { $_.Id })
    $unknownIds = @($requestedIds | Where-Object { $_ -notin $knownIds })
    if ($unknownIds.Count -gt 0) {
        throw "Componenti sconosciuti: $($unknownIds -join ', '). Valori validi: $($knownIds -join ', ')"
    }
    $selectedDefinitions = @($definitions | Where-Object { $_.Id -in $requestedIds })
}

$buildPlan = [Collections.Generic.List[object]]::new()

foreach ($definition in $selectedDefinitions) {
    $sourceRoot = Resolve-ExistingDirectory -Path $definition.SourceRoot -Label "Sorgente $($definition.Id)"
    Assert-RequiredPaths -Definition $definition -SourceRoot $sourceRoot
    $includedFiles = @(Get-IncludedFiles -Definition $definition -SourceRoot $sourceRoot)

    if ($includedFiles.Count -eq 0) {
        throw "Nessun file distribuibile trovato per '$($definition.Id)'."
    }

    $buildPlan.Add([PSCustomObject]@{
        Definition = $definition
        SourceRoot = $sourceRoot
        Files = $includedFiles
        FileCount = $includedFiles.Count
        TotalBytes = ($includedFiles | Measure-Object -Property Length -Sum).Sum
    })
}

Write-Host ''
Write-Host "Piano release KSR v$Version ($Channel)" -ForegroundColor Cyan
$buildPlan | ForEach-Object {
    [PSCustomObject]@{
        Component = $_.Definition.Id
        Files = $_.FileCount
        MiB = [Math]::Round($_.TotalBytes / 1MB, 2)
        Asset = "$($_.Definition.AssetPrefix)-v$Version.zip"
    }
} | Format-Table -AutoSize

if ($DryRun) {
    Write-Host 'Simulazione completata: nessun file o archivio creato.' -ForegroundColor Green
    return
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$releaseOutput = Join-Path $outputRoot "v$Version"

if (Test-Path -LiteralPath $releaseOutput) {
    if (-not $Force) {
        throw "La destinazione esiste gia: $releaseOutput. Usare -Force solo per rigenerare questa versione."
    }

    $releaseOutputFull = [IO.Path]::GetFullPath($releaseOutput).TrimEnd('\')
    $expectedReleasePrefix = [IO.Path]::GetFullPath($outputRoot).TrimEnd('\') + [IO.Path]::DirectorySeparatorChar + 'v'
    if (-not $releaseOutputFull.StartsWith($expectedReleasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Destinazione release non sicura: $releaseOutputFull"
    }
    Remove-Item -LiteralPath $releaseOutputFull -Recurse -Force
}

New-Item -ItemType Directory -Path $releaseOutput | Out-Null
$stagingRoot = Join-Path $outputRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
Assert-SafeStagingPath -StagingPath $stagingRoot -OutputRoot $outputRoot
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

$manifestComponents = [Collections.Generic.List[object]]::new()
$checksumLines = [Collections.Generic.List[string]]::new()

try {
    foreach ($item in $buildPlan) {
        $definition = $item.Definition
        $assetName = "$($definition.AssetPrefix)-v$Version.zip"
        $componentStage = Join-Path $stagingRoot $definition.Id
        $packageRoot = Join-Path $componentStage ($definition.PackageRoot.Replace('/', [IO.Path]::DirectorySeparatorChar))
        New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

        foreach ($file in $item.Files) {
            $destination = Join-Path $packageRoot ($file.RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
            $destinationDirectory = Split-Path -Parent $destination
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
            Copy-Item -LiteralPath $file.Source -Destination $destination -Force
        }

        $assetPath = Join-Path $releaseOutput $assetName
        Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $componentStage -Force).FullName -DestinationPath $assetPath -CompressionLevel Optimal
        $assetHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $assetSize = (Get-Item -LiteralPath $assetPath).Length

        $componentManifest = [ordered]@{
            id = $definition.Id
            transactionGroup = if ($definition.Id -eq 'remote-logger-server') { 'logger-server' } else { 'ksp-client' }
            asset = $assetName
            size = $assetSize
            sha256 = $assetHash
            source = $definition.PackageRoot
            target = $definition.Target
            required = $true
            requiredFiles = @($definition.RequiredPaths)
        }
        if ($definition.TargetKind -ne 'ksp') {
            $componentManifest.targetKind = $definition.TargetKind
        }

        $manifestComponents.Add($componentManifest)
        $checksumLines.Add("$assetHash  $assetName")
        Write-Host "Creato $assetName" -ForegroundColor Green
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        product = 'KerbalSpaceRace'
        version = $Version
        channel = $Channel
        minimumLauncherVersion = '0.1.6'
        components = $manifestComponents
        preserve = @(
            'GameData/KerbalSpaceRace/PluginData/**',
            'GameData/KerbalSpaceRaceNationSelector/PluginData/**',
            'GameData/KerbalSpaceRaceNationSelector/Patches/Generated/**',
            'GameData/KSRParameterLogger/PluginData/**',
            'GameData/Achievements/PluginData/**',
            'GameData/000_ClickThroughBlocker/PluginData/**',
            'GameData/001_ToolbarControl/PluginData/**',
            'GameData/SpaceTuxLibrary/PluginData/**',
            'LauncherData/RemoteLoggerServer/remote_logger.sqlite3',
            'LauncherData/RemoteLoggerServer/remote_logger_paths.json',
            'LauncherData/RemoteLoggerServer/*.cfg',
            'LauncherData/RemoteLoggerServer/*.log'
        )
        managedFilesInsidePreservedPaths = @(
            'GameData/KerbalSpaceRace/PluginData/KSRDifficultyReference.cfg'
        )
        delete = @()
        ksp = [ordered]@{
            minimumVersion = '1.12.0'
            maximumVersion = '1.12.99'
        }
    }

    $manifestPath = Join-Path $releaseOutput 'ksr-release.json'
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumLines.Add("$manifestHash  ksr-release.json")
    $checksumLines | Set-Content -LiteralPath (Join-Path $releaseOutput 'SHA256SUMS.txt') -Encoding ascii

    Write-Host ''
    Write-Host "Release pronta in: $releaseOutput" -ForegroundColor Cyan
} finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Assert-SafeStagingPath -StagingPath $stagingRoot -OutputRoot $outputRoot
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
