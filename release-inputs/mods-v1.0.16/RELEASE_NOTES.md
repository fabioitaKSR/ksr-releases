Reduces recurring logger work during gameplay while retaining the original Parameter Logger DLL and existing record schema.

- SpaceAge exports are regenerated when achievements change; sorting, date formatting and CSV writing run on a single background worker.
- Configuration contents are reread only after length or last-write metadata changes.
- Supports KSR legacy campaigns and Launch & Logs campaign origins. Contract logging, other record readers and server synchronization/authentication are unchanged.
- SpaceAge reads current in-memory achievements; snapshot timestamps represent the latest export. Explicit synchronization forces a fresh export and waits for completion.
- Validated with 25 offline checks using the real Harmony library and an in-game orbit test: UniqueOrbits/Kerbin increased from 45 to 46 and the heaviest-vessel record updated. No patch errors observed; the user reported no evident flight stutter.

Update via the KSR Launcher or merge the package's GameData folder into KSP, preserving PluginData. Restart KSP after installing. The launcher executable and other component packages are unchanged. Remove only KerbalSpaceRace.LoggerPerformance.dll to roll back this optimization.
