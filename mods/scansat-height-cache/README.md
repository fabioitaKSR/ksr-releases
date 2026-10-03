# SCANsat 21.1 KSR persistent height cache — 21.1.0-ksr.1

Stable KSR distribution of SCANsat 21.1 with persistent terrain-height grids. Based on upstream commit 2b660e6d3986b5ceaa11bf8fe7f24cb874fe232b; existing assembly version and API retained, with one additional setting. The full modified source and original license are included. This is a KSR modification, not an upstream SCANsat release.

Requires an existing SCANsat 21.1 installation. Merge the package GameData into KSP while the game is closed. Only SCANsat.dll and these documentation/license files are supplied; retain SCANsat.Unity.dll, assets, settings and scan coverage. The launcher installs this component in overlay mode.

PersistentHeightMapCacheEnabled defaults to True. Completed 360x180 maps are saved as .scnhmap files in GameData/SCANsat/PluginData/TerrainCache. Reads, validation, file-content hashing and writes run on worker tasks. Unity/PQS terrain queries remain on the main thread. Stable configuration, assemblies and referenced terrain asset contents determine the cache signature; corrupt or stale files fall back to normal generation. SCANsat scanning and save coverage are unchanged.

Live verification after full KSP restarts, same CCCP save and Space Center: 11 maps reused, zero regeneration/rejection, zero pending writes, repeated on a second restart. Initial map preparation with generation: 234.779 seconds. Warm preparation: 15.898 seconds including 14.201 seconds of worker fingerprint hashing. These are SCANsat phase timings, not total game startup or FPS benchmarks. Other existing mod errors were observed in the installation; no cache failure was observed.

After terrain/mod/config changes the conservative signature can invalidate all grids. Custom runtime terrain changes not represented in the fingerprint require disabling or clearing the cache. Disable using PersistentHeightMapCacheEnabled = False in the existing SCANsat/PluginData/Settings.cfg while KSP is closed. First creation still uses normal terrain generation.

Rollback: close KSP and restore the previous SCANsat.dll from the launcher backup or the original SCANsat 21.1 installation. Cached files may remain unused. Do not replace PluginData or saves.

Build on Windows using .NET Framework and Roslyn 4.8 net472 csc.exe, with a local KSP installation containing SCANsat.Unity.dll:

    ./build.ps1 -KspRoot 'path/to/KSP' -CompilerPath 'path/to/csc.exe'
    ./test.ps1 -CompilerPath 'path/to/csc.exe'

Game assemblies and the compiler are external dependencies and are not redistributed. Vendored KSPBuildTools 0.0.4 Log.cs is from commit 57833c7ad598e21f8e95898805e0681fbbd29cc7, MIT licensed.

Published DLL SHA256: 4d4ddeed1a42e69e32d9acbca28f2530e27d7eeac25a7add5120631760c0c999
