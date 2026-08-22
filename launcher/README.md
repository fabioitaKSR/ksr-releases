# KSR Launcher

KSR's Windows launcher and testable update engine for Kerbal Space Race. The CLI is intentionally cautious: `update` and `rollback` never modify files unless the explicit `--apply` option is present.

## Available features

- schema version 1 manifest and KSP directory validation;
- update plan for every component declared in the official manifest;
- local-folder and HTTP/HTTPS release assets;
- SHA-256 verification and safe ZIP extraction;
- transactional component groups;
- persistent backups, rollback and atomic installed state;
- `preserve` paths and `managedFilesInsidePreservedPaths` exceptions;
- automatic stable or beta discovery through GitHub Releases;
- automatic startup updates restricted to KSR-owned components that are already installed;
- missing component installation or repair only after explicit consent;
- no scanning, replacement or removal of third-party mods;
- WPF Player Area and Admin Area shell;
- KSR V1 registration, login, password recovery and dynamic campaign loading;
- one active administered campaign per account, with authenticated close and safe local-draft discard flows;
- optional automatic sign-in backed by a rotating refresh token in Windows Credential Manager (the password is never stored);
- local Log & Launch campaign baselines created from an administrator-selected Career or Science save;
- complete Master Save packaging with transient backups, quicksaves and logs excluded;
- `master-save.zip` and `baseline.json` stored under the selected save's `KSR_CampaignData` folder and excluded from the Master Save itself;
- GameData file inventory with SHA-256 integrity comparison;
- structured comparison of `persistent.sfs` difficulty values and save-root mod settings such as `KCT_Settings.cfg`;
- campaign launch blocking for missing, extra or modified GameData files;
- a multi-folder GameData picker for administrator-approved optional mods that campaign checks ignore, while protected KSP/KSR roots remain mandatory;
- settings alignment with an automatic backup while preserving save progress outside the `PARAMETERS` node;
- private-email account notice with no advertising use;
- explicit `SEND LOG` and `SEND SAVE` support reports with description and consent;
- safe diagnostic ZIP creation with UTC/player filenames, `report.txt`, `manifest.json` and SHA-256;
- authenticated HTTPS upload to the KSR server, with local queuing while server sign-in is unavailable.

## Build and test

From the project root:

```powershell
$env:DOTNET_CLI_HOME = "$PWD/.tools/dotnet-home"
& ./.tools/dotnet/dotnet.exe build ./github/ksr-releases-local/launcher/KsrLauncher.sln
& ./.tools/dotnet/dotnet.exe run --project ./github/ksr-releases-local/launcher/tests/KsrLauncher.Tests
```

## CLI example

```powershell
# Plan only
& dotnet run --project launcher/src/KsrLauncher.Cli -- plan `
  --repo "fabioitaKSR/ksr-releases" `
  --channel stable `
  --ksp "F:\SteamLibrary\steamapps\common\Kerbal Space Race" `
  --launcher-data "C:\KSR"

# Apply the update
& dotnet run --project launcher/src/KsrLauncher.Cli -- update `
  --repo "fabioitaKSR/ksr-releases" `
  --channel stable `
  --ksp "F:\SteamLibrary\steamapps\common\Kerbal Space Race" `
  --launcher-data "C:\KSR" `
  --apply
```

The ordinary update ignores KSR components that are not installed. Add `--install-missing` only for a first installation or an explicitly requested repair. This option never expands the launcher scope beyond components listed in the official KSR manifest.

The repository must publish `ksr-release.json` and its ZIP assets in the same GitHub Release. Until a release is published, GitHub mode correctly reports that no update is available.

## Support reports

The launcher never uploads diagnostics automatically. The player chooses `SEND LOG` or `SEND SAVE`, enters a description, reviews the included files, accepts the upload notice and presses `SEND REPORT`. Original files are never renamed, moved or modified.

When a server session is available, the launcher sends the package to `POST /api/v1/support/reports` using the signed-in player's bearer token. Until server authentication is connected, completed packages are retained in `%LOCALAPPDATA%\KSRLauncher\SupportQueue` for a controlled later upload.

## Log & Launch baseline drafts

The administrator prepares a Career or Science save in KSP and selects its folder from the launcher Admin Area. The launcher resolves the owning KSP installation, validates the save, creates `master-save.zip` and writes `baseline.json` inside the selected save under `KSR_CampaignData`.

The player-side installation check compares the selected campaign save and GameData with that baseline. KSP campaign launch remains disabled while gameplay mod files differ. If only save-scoped difficulty or mod settings differ, the launcher offers to align them after creating a timestamped backup under the player's save folder.

Campaign creation uploads `name`, `masterSave` and `baseline` to `POST /api/v1/campaigns` as authenticated multipart data. A deterministic `Idempotency-Key` makes a retry safe if the connection is interrupted after server creation. Failed uploads remain local drafts and expose a dedicated retry command; successful responses replace the draft code with the authoritative Campaign ID returned by the server.

Players can join an active campaign by entering its Campaign ID. The Launcher calls the authenticated `POST /api/v1/campaigns/{campaignCode}/join` endpoint, reports server validation errors, adds the returned player membership to the real campaign list and supports an explicit campaign refresh without signing out.
