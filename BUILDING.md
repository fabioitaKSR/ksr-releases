# Building a KSR release

`tools/build-release.ps1` creates separate ZIP assets for every KSR-owned installation root and generates `ksr-release.json` plus `SHA256SUMS.txt`.

The script reads the current KSP installation but never modifies it.

## Safety rules

The build excludes:

- `PluginData` runtime state, except the managed difficulty reference;
- nation selections and generated nation patches;
- Parameter Logger campaign exports;
- Remote Logger databases, paths, configuration and logs;
- `.bak`, `.backup`, `.old`, `.disabled`, `.rar`, cache and temporary files;
- C# source files from client packages.

The Remote Logger Server package uses an explicit file allowlist.

Before the first public production release, replace the development token `dev-token` in the Remote Logger Server with a first-run token setup. It is a placeholder, not a private credential, but must not remain the production default. The server no longer exposes an admin login or admin password.

## Dry run

Run a dry check first. It validates every required source and prints file counts and uncompressed sizes without creating output:

```powershell
.\tools\build-release.ps1 `
  -Version 1.0.0 `
  -KspRoot 'F:\Path\To\Kerbal Space Program' `
  -ServerSource 'C:\Path\To\KSRRemoteLoggerServer' `
  -DryRun
```

## Create packages

Remove `-DryRun` to create the release under `artifacts/v1.0.0`:

```powershell
.\tools\build-release.ps1 `
  -Version 1.0.0 `
  -KspRoot 'F:\Path\To\Kerbal Space Program' `
  -ServerSource 'C:\Path\To\KSRRemoteLoggerServer'
```

Build only selected components when iterating:

```powershell
.\tools\build-release.ps1 `
  -Version 1.0.0 `
  -KspRoot 'F:\Path\To\Kerbal Space Program' `
  -Component ksr-core,contract-pack `
  -DryRun
```

Use `-Force` only to replace an already generated output directory for the same version.

Do not commit `artifacts`; publish the generated files as GitHub Release assets.
