# KSR Platform V1 scope

The confirmed V1 architecture is:

`KSR Launcher -> local KSP with KSR Start/Logger -> KSR Server`

The server is authoritative for accounts, campaigns, Master Saves, members, nation assignments, achievements, scores and standings. Campaign creation happens in the launcher from a Career or Science save prepared in KSP; Sandbox saves are rejected. Nation selection happens inside KSP. The launcher also handles account access, the user's campaigns, Master Save download, updates and game startup.

## Log & Launch

`Log & Launch` ("log and launch") is the first KSR gameplay workflow. It is campaign-specific; KSR does not impose one universal clean KSP installation.

The campaign administrator creates a new Career or Science save inside KSP, chooses the difficulty and configures the installed gameplay mods. The administrator then closes or leaves KSP, opens the launcher Admin Area, selects `CREATE RACE` and browses to the desired save folder. Selecting `.../saves/<save name>` also identifies the owning KSP installation automatically. The launcher validates the save and captures an immutable campaign baseline containing:

- the complete starting Master Save;
- the KSP version;
- a snapshot of `GameData`, including relevant mod versions and file hashes;
- save-scoped difficulty and mod settings contained in the save;
- approved gameplay configuration outside the save, excluding caches, credentials, tokens, logs and personal preferences.

When a player joins, KSR compares that player's installation with the campaign baseline, downloads and verifies the Master Save, and installs it under a name derived from `KSR <Campaign ID> Start`. The player loads that save, selects an available nation through the existing Nation Selector and begins the race.

The launcher Admin Area must provide the campaign name, a Career/Science save browser rooted in the selected KSP installation, a multi-folder picker for optional GameData roots to ignore, the automatically resolved KSP destination, a summary and confirmation of the captured baseline, upload progress, the generated Campaign ID and clear server error handling. Core KSP and KSR folders cannot be ignored. Campaign creation must not require an in-game administrator interface.

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
