# KSR Platform V1 scope

The confirmed V1 architecture is:

`KSR Launcher -> local KSP with KSR Start/Logger -> KSR Server`

The server is authoritative for accounts, campaigns, Master Saves, members, nation assignments, achievements, scores and standings. Campaign creation and nation selection happen inside KSP. The launcher handles account access, the user's campaigns, Master Save download, updates and game startup.

## Log & Launch

`Log & Launch` ("log and launch") is the first KSR gameplay workflow. It is campaign-specific; KSR does not impose one universal clean KSP installation.

The campaign administrator creates and loads a new Career save inside KSP, chooses the difficulty and configures the installed gameplay mods. From the future KSR in-game administrator interface, the administrator selects `CREATE RACE`. KSR then captures and uploads an immutable campaign baseline containing:

- the complete starting Master Save;
- the KSP version;
- a snapshot of `GameData`, including relevant mod versions and file hashes;
- save-scoped difficulty and mod settings contained in the save;
- approved gameplay configuration outside the save, excluding caches, credentials, tokens, logs and personal preferences.

When a player joins, KSR compares that player's installation with the campaign baseline, downloads and verifies the Master Save, and installs it under a name derived from `KSR <Campaign ID> Start`. The player loads that save, selects an available nation through the existing Nation Selector and begins the race.

The in-game administrator interface is a confirmed subsequent deliverable. It must provide campaign creation from the currently loaded Career save, a summary and confirmation of the captured baseline, upload progress, the generated Campaign ID and clear server error handling.

## Updater boundary

The updater manages only these KSR-owned roots:

- KSR Core, including the in-game logger;
- Nation Selector;
- KSR Suit Pack;
- KSR Contract Pack;
- KSR Parameter Logger;
- KSRDisableDBSUI;
- KSR Remote Logger Server outside `GameData`.

Ordinary updates operate only on roots already present on the machine. Missing components are skipped. Installing or repairing missing components is a separate explicit action. Third-party mods are never managed. Local configuration, selections, databases, logs and campaign data are preserved.

KSRDisableDBSUI is retained because Dynamic Battery Storage disables its simulation when Kerbalism is detected but can still create an unnecessary toolbar UI.

## Authentication boundary

V1 uses personal KSR accounts and user sessions. A shared development token such as `dev-token` is not a production authentication design. Competitive events must resolve unambiguously through `UserID -> CampaignID -> NationID -> Achievement`.

Scoring constants and delayed/offline event placement remain explicit open decisions and do not block the updater core.
