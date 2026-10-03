# Logger performance patch

The original KSRParameterLogger DLL is retained. The companion `KerbalSpaceRace.LoggerPerformance.dll` applies scoped Harmony patches to the Parameter Logger and Remote Logger at startup.

Configuration contents are cached using file length and UTC last-write timestamp, checked at most once a second per path. SpaceAge achievements are copied from the current in-memory list on the Unity thread and compared by key and value on the logger's existing two-second cadence. Only changes trigger the single background CSV writer. The worker handles sorting, campaign-relative dates and atomic file replacement using plain copied data, with no Unity/KSP objects on the worker thread.

KSR legacy mode and Launch & Logs campaign origins are both supported. The 13-column CSV schema, paths, record fields and synchronization are retained. SpaceAge reads current records instead of the previously loaded save copy. Snapshot times now represent an actual export; explicit synchronization still forces a fresh snapshot and flushes it before returning. Other contract, payload, science and crew readers, server endpoints, authentication and campaign selection are unchanged.

## Build and test

Requires Windows with .NET Framework 4.8, a C# 7.3-or-newer `csc.exe` (for example the net472 compiler from Microsoft.Net.Compilers.Toolset), and a KSP installation with Harmony 2.2.1 and SpaceAge 1.3.8.

```powershell
./build.ps1 -KspPath 'F:\Path\To\KSP' -CompilerPath 'C:\Path\To\csc.exe'
./test.ps1 -KspPath 'F:\Path\To\KSP' -CompilerPath 'C:\Path\To\csc.exe'
```

The tests compile the production source with offline KSP/Unity stand-ins and the real installed Harmony library. All 25 checks passed, covering metadata caching, same-size changes, live additions/updates/removals, reloads, save separation, CSV/date compatibility, non-legacy campaign origins, serialized jobs, startup fallback and error reporting. They are not a measurement of in-game frame times.

The live flight test verified `UniqueOrbits/Kerbin` changing from 45 to 46 and `HeaviestVesselInOrbit/Kerbin` changing to the new vessel, alongside live science updates. No patch errors were observed; the user reported no evident flight stutter after the patch.

## Installation and rollback

Use the versioned Parameter Logger package from the GitHub release, or copy the companion DLL to `GameData/KSRParameterLogger/Plugins`. Keep PluginData and restart KSP. Remove only the companion DLL and restart to return to original behavior.

An unsupported target causes activation to be rolled back. Export errors are logged on the main thread and retried. Explicit flush reports a timeout after ten seconds if background writing has not completed.

The release manifest retains its existing global version because current launchers use that version for every component's installed state. Only the Parameter Logger package hash, component version and required file list change, preventing unrelated packages from being reinstalled.
