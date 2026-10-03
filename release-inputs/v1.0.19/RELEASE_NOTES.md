Are still kerbal — v1.0.19

SCANsat's terrain height maps previously had to be regenerated after restarting KSP. The verified height-cache patch now installs automatically from inside KerbalSpaceRace at the first game startup.

- The first-start installer verifies both DLLs, backs up the supported original SCANsat 21.1 DLL, and replaces it atomically.
- If Windows locks the loaded DLL, a hidden helper waits for KSP to close and completes the installation. The main menu asks you to restart KSP; it never closes the game itself.
- Later startups leave the installed DLL untouched. Settings, scan coverage and other SCANsat files are preserved. Unknown DLL versions are skipped.
- The payload is byte-for-byte identical to the beta-verified SCANsat patch: SHA256 4d4ddeed1a42e69e32d9acbca28f2530e27d7eeac25a7add5120631760c0c999.
- The launcher package installs only into GameData/KerbalSpaceRace. It does not directly overwrite SCANsat during the launcher update.
- Checks passed: 8 installer cases, replacement using the real original DLL, deferred installation after a parent process exits, and previous live SCANsat tests showing 11 cache hits and zero regenerations/rejections. First-start UI verification in KSP remains pending.

Requires an existing supported SCANsat 21.1 installation. After the first-start notice, close KSP, wait a few seconds and relaunch. Original DLL backup and installation record are stored in KerbalSpaceRace/PluginData/SCANsatHeightCache. Full source and licenses are included.
