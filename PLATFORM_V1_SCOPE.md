# KSR Platform V1 scope

The confirmed V1 architecture is:

`KSR Launcher -> local KSP with KSR Start/Logger -> KSR Server`

The server is authoritative for accounts, campaigns, Master Saves, members, nation assignments, achievements, scores and standings. Campaign creation and nation selection happen inside KSP. The launcher handles account access, the user's campaigns, Master Save download, updates and game startup.

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
