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

## Internal catalog tests before publication

The desktop launcher accepts `KSR_INTERNAL_SIGNED_CATALOG` as a process-local
environment variable containing an absolute local path to `ksr-release.json`.
Its `.sig` sidecar must be signed by the same trusted release key. Metadata
size limits, channel checks, manifest validation and installed-version checks
still apply. Every package must have an authenticated absolute HTTPS asset URL;
the local catalog does not authorize arbitrary local packages or unsigned data.

Start a freshly built launcher from a wrapper that sets the variable only in
its own process and restores it after `Start-Process`. Do not persist the
variable in user/system settings. The window title identifies the internal
test, and launcher self-update is suspended for that process so it cannot
replace the test executable mid-run. Component checks and installation use
the normal updater, including backups and preservation.

Without the variable, public GitHub discovery is unchanged. This mode tests
the signed catalog and component updater before publication; it does not
establish that an older launcher discovers, downloads and installs the final
public release. Rebuild and sign the final executable metadata after source
changes, rather than reusing signatures for an earlier binary.

## Selective game-component updates, launcher 0.7.35

Automatic updates and UPGRADE reuse unchanged official file bytes. REPAIR acquires the complete immutable ZIP. A first installation into a missing component destination also uses the complete ZIP. Older catalogs without file indexes retain their ZIP transport while they are migrated. Launcher self-updates remain a single EXE.

Before signing any new public or private component catalog, generate its file indexes from the exact official ZIPs. Use the Python 3 standard-library tool below, with an immutable HTTPS asset base for the release that will contain these indexes. Private-beta indexes must stay in the private release/storage for their own module. The authenticated beta client remaps all package and index URLs to that same module.

```powershell
python tools/build-selective-catalog.py --manifest input/ksr-release.json --packages official-zips --output release-assets --base-url https://github.com/OWNER/REPO/releases/download/TAG
./tools/sign-release.ps1 -Path release-assets/ksr-release.json
dotnet run --project launcher/src/KsrLauncher.Cli -c Release -- verify-file-indexes --directory release-assets
```

The builder checks each ZIP checksum, reads the original entry offsets and streams the file checksums. It writes deterministic gzip indexes and a catalog that binds each index's exact size/SHA-256. Sign only after this step. Publish all generated `.files.json.gz` assets along with the signed catalog. Keep all referenced ZIP assets available for REPAIR and old clients. `build-release.ps1` invokes the builder automatically. It accepts `-Python` and `-IndexAssetBaseUrl` for the build environment and private release destination. `publish-github-release.ps1` verifies the signed index bindings before uploading.

The client requires exact HTTP 206 byte ranges and independently checks every downloaded file's signed size and SHA-256. A server returning the entire ZIP to a range request is rejected. For supported overlapping component paths, conflicting official variants are installed in catalog order. Other files, saves and existing preservation rules remain protected. Comparisons and decompression run on workers only during updates. No periodic file hashing is introduced.

The updater saves and flushes the previous-state backup and an in-progress journal before replacement. Its journal also identifies generated staging directories. An interrupted transaction is restored before another applied update, and only recorded staging directories are removed. Completed backups are cleaned only after verified completion. Test simulated process termination, cancellation, bad ranges/checksums, ordinary and beta transports, existing-installation preservation and full REPAIR. Process-termination tests are distinct from physical power-loss testing.
