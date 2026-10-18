# Local release signing

Use PowerShell 7 on the owner's Windows PC. `sign-release.ps1 -Initialize`
creates a 3072-bit RSA key encrypted with Windows DPAPI CurrentUser outside
the repository, under `%LOCALAPPDATA%/KSR/ReleaseSigning`. Do not initialize
again to replace a key. No private key is stored in GitHub or on the game server.

The public SPKI key is embedded in `ReleaseSignatures.cs`. Sign the exact bytes
of the approved `ksr-release.json` and `ksr-launcher-update.json`:

```powershell
./tools/sign-release.ps1 -Path ./release-output/ksr-release.json
./tools/sign-release.ps1 -Path ./release-output/ksr-launcher-update.json
dotnet run --project launcher/src/KsrLauncher.Cli -c Release -- verify-release --directory release-output
```

The launcher authorization contains schema version 1, product `KSRLauncher`,
three-part version, executable name, exact HTTPS URL, SHA-256 and byte size.
Publish both JSON files and their `.sig` sidecars, the executable and the legacy
`SHA256SUMS.txt`. SHA256SUMS remains for older clients; new clients require
RSA-PSS-SHA256 signatures and reject missing signatures without a fallback.

Build and sign locally. CI only verifies an existing draft, with read-only
repository permissions; it must not rebuild and replace signed executables.
Future component-only releases must also sign the component manifest before
publication. The publishing helper checks launcher releases before upload.

Version 0.7.14 is the initial trust bootstrap. Older launchers do not verify
signatures during this first upgrade. The owner can independently verify the
download using the local verifier; all subsequent updates use the embedded
public key. Protect this initial distribution accordingly.

Key rotation requires an overlapping transition: ship a release trusted by the
old key that contains the next public key before using the next private key.
Do not simply overwrite the key store or the trust anchor. Downgrades through
normal update discovery are refused; component catalogs older than the recorded
installed catalog are also refused. Explicit backup rollback remains available.

Recovery limitation: a copy of `release-key.dpapi` alone is not portable to a
fresh Windows account or installation. A separately encrypted, portable recovery
export and offline storage are still to be arranged with the owner. No external
backup has been claimed or created by the initial setup. A compromised logged-in
Windows account can use DPAPI; the scheme isolates GitHub/server compromise,
not compromise of the signing PC.
