# Kerbal Space Race Releases

Official release channel and automatic-update metadata for Kerbal Space Race.

This repository publishes versioned packages consumed by the KSR Launcher. It does not contain player saves, campaign databases, authentication tokens, logs, local configuration files, or development backups.

The confirmed product boundary is recorded in [PLATFORM_V1_SCOPE.md](PLATFORM_V1_SCOPE.md).

## Release components

- KSR Core
- KSR Nation Selector
- KSR Suit Pack
- KSR Contract Pack
- KSR Parameter Logger
- KSR Disable DBS UI
- Tarsier Space Technology KSR event-leak patch
- KSR Remote Logger Server
- KSR Launcher

Every published asset is versioned and verified through the `ksr-release.json` manifest and a SHA-256 digest.

## Automatic updates

The launcher reads the latest published GitHub Release, validates the manifest and package digest, installs through a temporary staging area, and restores the previous version if installation fails.

Player-generated files and server data are preserved during updates.

The ordinary update path only updates declared KSR components. Missing components are skipped unless the user explicitly requests install/repair. A narrowly scoped third-party compatibility patch may use `overlay` mode to replace only its declared files while preserving the rest of the mod; Tarsier Space Technology uses this mode so ChemCam assets and configuration remain untouched.

The first tested implementation of the update engine is available in [`launcher/`](launcher/README.md). It currently provides the reusable .NET core, a safe command-line interface, persistent backups and automated rollback tests.

## Release builds

Package generation is handled by the safety-focused PowerShell script documented in [BUILDING.md](BUILDING.md). Generated archives are published as GitHub Release assets and are not committed to the repository.
