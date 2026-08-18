# Kerbal Space Race Releases

Official release channel and automatic-update metadata for Kerbal Space Race.

This repository publishes versioned packages consumed by the KSR Launcher. It does not contain player saves, campaign databases, authentication tokens, logs, local configuration files, or development backups.

## Release components

- KSR Core
- KSR Nation Selector
- KSR Suit Pack
- KSR Contract Pack
- KSR Parameter Logger
- KSR Disable DBS UI
- KSR Remote Logger Server
- KSR Launcher

Every published asset is versioned and verified through the `ksr-release.json` manifest and a SHA-256 digest.

## Automatic updates

The launcher reads the latest published GitHub Release, validates the manifest and package digest, installs through a temporary staging area, and restores the previous version if installation fails.

Player-generated files and server data are preserved during updates.

## Release builds

Package generation is handled by the safety-focused PowerShell script documented in [BUILDING.md](BUILDING.md). Generated archives are published as GitHub Release assets and are not committed to the repository.
