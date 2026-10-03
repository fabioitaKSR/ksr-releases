# KSR SCANsat first-start installer 1.0.0

Install the package inside `GameData/KerbalSpaceRace`. It contains a bootstrap plugin and the previously verified SCANsat height-cache DLL as `Tools/SCANsatHeightCache/SCANsat.dll.payload`. The payload extension prevents KSP from loading it as a second assembly.

At the first KSP startup, a worker verifies the payload and existing SCANsat DLL hashes. It replaces only the known original SCANsat 21.1 DLL, saving an exact backup under `KerbalSpaceRace/PluginData/SCANsatHeightCache`. The replacement is atomic. Unknown versions, invalid payloads, reparse points and invalid backups are rejected. If SCANsat is absent, it is skipped.

The main menu shows a restart notice. When Windows locks the loaded DLL, a hidden installer waits for that KSP process to exit, then installs the verified replacement. Close KSP, wait a few seconds, then relaunch. If the patch is already loaded, no restart notice is needed. Subsequent startups check the target hash and leave the DLL and marker untouched. Settings and scan coverage are preserved. No network connection is required by the installer.

The installer never terminates or restarts KSP itself. If permissions or a file lock still prevent the replacement, the next startup reports the problem and preserves the existing DLL. The helper waits up to 12 hours for KSP to close and retries transient locks for up to 10 seconds.

Patched DLL SHA256: `4d4ddeed1a42e69e32d9acbca28f2530e27d7eeac25a7add5120631760c0c999`.
Supported original DLL SHA256: `8ee4aa88956230bc828a5da1426eba922845b326e432bcb0c8ad5a78ccebbe90`.

Build: `./build.ps1 -KspRoot 'path/to/KSP' -CompilerPath 'path/to/Roslyn/net472/csc.exe'`. Tests: `./test.ps1 -CompilerPath 'path/to/csc.exe'`.

Validation: 8 filesystem checks, real original-to-patched DLL helper installation, hash equality against the beta. SCANsat itself previously passed repeated live restart tests with 11 disk hits and zero regenerations/rejections. The first-start UI still needs an in-game check on an installation containing the original DLL.

Rollback: close KSP and wait for any deferred installer to finish. Remove `Plugins/KerbalSpaceRace.SCANsatBootstrap.dll` and `Tools/SCANsatHeightCache`, then restore the original backup as `GameData/SCANsat/Plugins/SCANsat.dll`. Removing the bootstrap first prevents reinstallation. Keep your SCANsat settings and scans.

The SCANsat payload is a KSR modification of upstream SCANsat 21.1. Full modified source and original license are distributed with the release. KSPBuildTools logging uses its MIT license. KSP/Unity references and compiler binaries are not redistributed.
