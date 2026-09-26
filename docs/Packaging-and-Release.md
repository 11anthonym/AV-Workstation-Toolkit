# AV Workstation Toolkit 1.1.2 Packaging and Release

AV Workstation Toolkit builds a directly downloadable x64 executable, a per-machine MSI, and a one-file portable ZIP. Every standard format contains the same self-contained `AVWorkstationToolkit.exe`; no companion payload directory is required. The standard release set contains exactly eight assets: those three delivery formats, the versioned Apache-2.0 project license, third-party notices, a CycloneDX SBOM, a SHA-256 checksum list, and a release manifest. The checksum list hashes the other seven assets and intentionally does not hash itself. An optional offline bundle can add separately verified third-party installers when redistribution is authorized. Until hosted signing is operational, public releases are unsigned release-candidate builds published under the [unsigned publication procedure](#unsigned-publication); production releases remain signature-required.

## Runtime layout

The direct release executable can run from any normal user-writable directory. The MSI installs that executable beneath `%ProgramFiles%\AVWorkstationToolkit` and creates an all-users Start-menu shortcut. The portable ZIP contains only `AVWorkstationToolkit.exe`.

The launcher embeds the .NET 10.0.11 LTS compiled WPF application, the self-contained compiled worker, and reviewed catalogs/notices, so target systems do not need a separate .NET installation or adjacent scripts. Desktop App Installer/WinGet is the managed-package prerequisite. PowerShell is not required by the packaged application runtime. The launcher:

1. enumerates its compile-time embedded runtime resources;
2. restores changed or missing files into `%LOCALAPPDATA%\AVWorkstationToolkit\runtime\1.1.2`, verifies every extracted SHA-256 hash against the embedded bytes, and leaves matching files untouched;
3. rejects invalid resource paths and reparse-point cache paths;
4. refuses an elevated operator token;
5. starts the compiled WPF App in-process for normal startup;
6. gives the App only the exact embedded worker identity/hash and canonical `%LOCALAPPDATA%\AVWorkstationToolkit` root; the App launches the worker with a fixed production argument vector and `UseShellExecute=false`;
7. removes only an exact allowlist of stale extracted application-runtime files left by earlier versions, without touching unrelated user data;
8. restores the Apache-2.0 project license, project third-party notices, and the exact .NET 10.0.11 license and upstream third-party notices under the verified runtime `notices` directory; and
9. never accepts a package ID, credential, URI, executable, worker path, or arbitrary command argument from its command line.

Installed and portable runs write mutable data beneath `%LOCALAPPDATA%\AVWorkstationToolkit`:

```text
%LOCALAPPDATA%\AVWorkstationToolkit
├── runtime\1.1.2
│   ├── worker\AVWorkstationToolkit.Worker.exe
│   ├── manifests
│   ├── notices
├── logs\requests
├── reports
├── vendor-cache
├── trusted-sftp-hosts.json
└── snapshots
```

MSI uninstall intentionally preserves this evidence. Remove it only through an approved data-retention process.

### Legacy data compatibility

On the first packaged launch, the runtime checks the historical `%LOCALAPPDATA%\AVinite` root and progressively establishes `%LOCALAPPDATA%\AVWorkstationToolkit`. Migration is intentionally non-destructive and allowlisted: AV Workstation Toolkit can copy a validated SFTP host store, bounded launcher/request logs, and hash-matching vendor-cache payload metadata. It never copies runtime scripts, arbitrary files, reports, or snapshots; rejects reparse-point roots and paths; does not overwrite existing new-state files; writes a completion marker; and leaves the historical directory in place for retention review. Repeated launches read the marker without rewriting migrated files.

New SFTP credentials use the `AVWorkstationToolkit:VendorSftp:` Credential Manager prefix. The compiled credential service can read the historical credential target only as a fallback, and an explicit **Forget saved** action removes both identities. Password material is never copied into files, arguments, logs, or diagnostics.

## Build

Requirements:

- Windows PowerShell 5.1;
- a stable .NET 10 SDK at 10.0.100 or any later installed .NET 10 feature band selected by the checked-in `global.json` (`latestFeature`, no prerelease SDKs);
- network access to restore the pinned `WixToolset.Sdk` 6.0.2 build dependency; and
- a standard-user token for the full local QA suite.

Signed builds additionally require a Microsoft-signed Windows SDK `signtool.exe` and an organization-approved code-signing certificate available by thumbprint. Unsigned development builds do not require signing tooling.

WiX source uses MS-RL. The official `WixToolset.Sdk` 6.0.2 NuGet binary also
carries the Open Source Maintenance Fee agreement, including a condition for
revenue-generating users. WiX remains build-only and no WiX binary/custom action
is included in the MSI, but the owner must review that binary-use condition
before revenue-generating builds. See [third-party notices](../THIRD-PARTY-NOTICES.md).

From the repository root, the supported fresh-clone build entry point is:

```cmd
Build-AVWorkstationToolkit.cmd
```

The wrapper invokes the reviewed PowerShell build using inbox Windows PowerShell and `RemoteSigned`. Before deleting any prior output, the build enumerates installed SDKs, verifies that `global.json` selected a stable .NET 10 SDK under the supported feature-band policy, performs non-mutating locked restores, runs a machine-readable NuGet vulnerability audit, validates `VERSION` agreement and launcher/worker target settings, and runs the deterministic catalog compiler in `-Check` mode. It then runs source QA, publishes the self-contained untrimmed compiled worker, signs/verifies it when signing is configured, embeds those exact worker bytes plus the five strict runtime manifests into the self-contained untrimmed compiled-WPF bootstrap, builds the one-file MSI and ZIP, copies the reviewed third-party notices, creates a deterministic CycloneDX 1.6 SBOM containing the worker hash, and writes schema-v3 release metadata with compiled-runtime/signature identity plus SHA-256 checksums beneath `artifacts\release\1.1.2` (for a beta, `artifacts\release\1.1.2-beta.N`; see [beta naming](#beta-naming)).

The release manifest records the build timestamp, commit SHA, clean/dirty source state, selected SDK, build channel, architecture, actual .NET runtime/apphost, NuGet audit state, artifact hashes, SBOM hash, checksum identity, and signer/timestamp state without local usernames or developer paths. The checksum list covers the EXE, MSI, ZIP, Apache-2.0 license, third-party notice, SBOM, and release manifest; only the checksum file itself is omitted to avoid a cycle. `Development` and `ReleaseCandidate` channels can be unsigned. `Production` requires a clean checkout and valid signed output.

The SDK baseline is intentionally `10.0.100` with `rollForward: latestFeature`: a machine with a stable 10.0.303 or 10.0.400 SDK can build, while .NET 11 and prerelease SDKs are not selected. This follows the [.NET `global.json` roll-forward policy](https://learn.microsoft.com/dotnet/core/tools/global-json) and matches CI's stable `10.0.x` installation.

Release builds never update `packages.lock.json`. If locked restore reports a stale dependency graph, run the following review command, inspect the lock-file diff, and commit it only when the package change is intentional:

```powershell
dotnet restore .\src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj --force-evaluate
```

## Authorized external package bundles

External applications use `manifests\external-applications.json`. `VendorPage` entries provide read-only version awareness and direct users to the official vendor. `DirectDownload` entries add a version-bearing URI pattern, redirect-host allowlist, size bound, and Authenticode publisher rule. `AuthenticatedSftp` entries add a public catalog URI, host/port, remote root, curated product IDs, size bound, and publisher rule. `InventoryOnly` entries detect state without claiming an available release. `Bundled` entries pin a payload path, SHA-256 hash, and optional Authenticode publisher.

Create a bundled entry only after confirming redistribution rights:

```powershell
.\scripts\Add-AVWorkstationToolkitExternalPackage.ps1 `
  -Name 'Example Designer' `
  -Id 'Vendor.ExampleDesigner' `
  -Version '1.2.3' `
  -Vendor 'Example Vendor' `
  -ApplicationType DSPAudio `
  -InstallerPath 'C:\ApprovedInstallers\ExampleDesigner.msi' `
  -RegistryDisplayNamePattern '^Example Designer(?:\s|$)' `
  -ReleaseVersionPattern 'Example Designer v(?<Version>\d+\.\d+\.\d+)' `
  -ReleaseUri 'https://vendor.example/downloads' `
  -Note 'Approved engineering application' `
  -RedistributionAuthorized

.\Build-AVWorkstationToolkit.cmd -BuildOfflineBundle
```

The authoring command writes the catalog metadata and copies the installer beneath `external-packages\packages`; that depot is ignored by Git. Executables and MSIs must have a valid Authenticode signature unless the operator explicitly supplies `-AllowUnsignedPayload` after an independent trust review. Archive payloads also require that explicit override because ZIP files cannot carry Authenticode signatures.

`-BuildOfflineBundle` refuses an absent, changed, reparse-point, or signer-mismatched payload. It emits `AV-Workstation-Toolkit-1.1.2-offline-bundle.zip` with this layout:

```text
AVWorkstationToolkit.exe
AVWorkstationToolkit-offline-bundle.json
packages\<package-id>\<version>\<installer>
```

At runtime AV Workstation Toolkit verifies the payload again and only shows it in Explorer. It does not run third-party installers or pass them into the WinGet worker. The normal direct EXE, MSI, and portable ZIP remain payload-free.

The normal open-source distribution boundary contains only AV Workstation
Toolkit artifacts, provenance, and notices. Commercial-catalog applications,
vendor caches, credentials, logs, diagnostics, workstation snapshots, signing
material, and the local external installer depot are excluded. An optional
offline bundle is a separate controlled distribution requiring payload-specific
rights review; it is never produced by hosted release CI.

Direct HTTPS and SFTP downloads are also absent from release assets. They are fetched only by an operator from the packaged app into `%LOCALAPPDATA%\AVWorkstationToolkit\vendor-cache`. A `.download` file is not finalized until its configured Authenticode publisher validates; AV Workstation Toolkit then records SHA-256 metadata and rechecks both properties before every reuse. SFTP credentials remain in Windows Credential Manager and are not part of the package, runtime cache, release manifest, or logs.

Q-SYS Designer LTS intentionally uses vendor-page delivery. The [current Q-SYS EULA](https://help.qsys.com/q-sys_9.13/Content/Legal.htm) restricts external redistribution, so its installer must not be placed in an AV Workstation Toolkit bundle without written permission from QSC.

## Package QA

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-Package.ps1
```

The suite copies the release EXE into an otherwise empty directory and runs it there, verifies deterministic runtime placement and the hash-identified compiled worker, proves a second launch does not rewrite unchanged files, repairs tampered recovery/worker cache files, checks EXE/MSI identity and SBOM/manifest/checksum agreement, confirms the ZIP contains the same single executable, runs the compiled production WPF smoke plus deliberate legacy-recovery smoke, and administratively extracts the MSI without registering it. The MSI-extracted launcher must be byte-identical to the standalone launcher and satisfy the same signature policy. QA does not contact an authenticated vendor, download or execute third-party software, install the MSI, or invoke a WinGet change action.

## Signing

The repository contains no private key. To sign with an organization-approved code-signing certificate in either the CurrentUser or LocalMachine certificate store:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\build\Build-Release.ps1 `
  -CertificateThumbprint <approved-certificate-thumbprint> `
  -CertificateStore Auto `
  -ReferenceCatalogBaselinePath C:\ApprovedCatalog\AVWT-Reference-Catalog.avwtcatalog `
  -RequireSignature `
  -BuildChannel Production

powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-Package.ps1 `
  -RequireSignature
```

The build requires the selected certificate to be currently valid, carry the code-signing EKU, and expose its private key. A validated Microsoft Windows SDK `signtool.exe` signs both artifacts with SHA-256, obtains a configurable HTTPS RFC3161 timestamp (`-TimestampServer`), verifies the signature and timestamp, and records signer and timestamp identity before release hashes are generated. The portable ZIP is authenticated by its published checksum and the signature on the contained executable. No private key or password is accepted in a command line or committed to the repository.

The tagged workflow uses the official immutable-pinned SignPath GitHub Action.
It signs the worker first, embeds those exact bytes, and then deep-signs the
launcher inside the MSI plus the MSI envelope. The exact signed launcher
extracted from the returned MSI is used for the direct EXE and ZIP. Missing
protected-environment configuration, approval, expected signer identity,
signature, or RFC3161 timestamp fails closed. Ordinary local and pull-request
builds remain explicitly unsigned. The local certificate-thumbprint path remains
available for controlled organizational builds but is not used by tagged CI.

Consequently, the tag-triggered workflow is not an unsigned first-release path:
without valid SignPath configuration and approval it fails before publication.
If the owner decides that an initial unsigned public artifact is needed to
establish public project history before a SignPath application, that artifact
must use a separately reviewed, explicitly release-candidate publication
procedure. Production mode and the tag-triggered production workflow must not
be weakened or relabeled to make that possible.

AV Workstation Toolkit has prepared—but not operationally configured or
executed—the hosted SignPath path. It signs the exact worker first, then
deep-signs the launcher inside the MSI and the exact MSI, and generates final
provenance without rebuilding application binaries. See the
[code signing policy](Code-Signing-Policy.md) and
[SignPath readiness record](SignPath-Readiness.md). The existing Authenticode
path remains valid until a reviewed replacement is operational.

## Endpoint-trust and Defender validation

Run the static endpoint-trust regression independently:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-EndpointTrust.ps1
```

On a Windows release machine, request a safe Defender custom scan of the completed release directory:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-EndpointTrust.ps1 `
  -ReleaseRoot .\artifacts\release\1.1.2 `
  -ScanWithDefender
```

The optional scan uses an existing valid Microsoft-signed `MpCmdRun.exe`, reports unavailable versus passed, and fails on a nonzero scan/detection result. It does not change Defender policy, disable remediation, add exclusions, or upload artifacts to a public service. Pass `-RequireDefender` only on a release machine where Defender availability is an explicit prerequisite.

<a id="beta-publication"></a>

## Unsigned publication

Until hosted signing is operational, public releases are unsigned `ReleaseCandidate` builds published as GitHub releases, each with the owner's explicit approval. They never use the production channel, the signature-required QA mode, or the `v*.*.*` production tag, so the fail-closed signing workflow does not run. The release title and notes state that the build is unsigned, and the newest release is marked as the latest so the repository's download link leads to it. An unsigned build is either:

- a **release**, `X.Y.Z`, built without a label, whose files and window title read `X.Y.Z` (1.1.2 is the first); or
- a **beta**, `X.Y.Z-beta.N`, a pre-release of that version, named as described below.

A version number is never reused for different bytes: once `X.Y.Z` is published unsigned, the first signed release is a later version.

### Beta naming

A beta is a [Semantic Versioning](https://semver.org/) pre-release of the version in `VERSION`: `X.Y.Z-beta.N`, where `N` counts up from 1 and is never reused. The dot matters: `beta.10` sorts after `beta.9`. The label appears wherever people identify a build:

| Where | Example |
|---|---|
| Git tag (no leading `v`) | `1.1.1-beta.2` |
| Release title | AV Workstation Toolkit 1.1.1 Beta 2 (unsigned) |
| Artifact files and folder | `artifacts\release\1.1.1-beta.2\AV-Workstation-Toolkit-1.1.1-beta.2-win-x64.exe` |
| Window title and About box | 1.1.1 Beta 2 |
| Diagnostics, SBOM, and release manifest `ReleaseName` | `1.1.1-beta.2` |
| Windows product version | `1.1.1-beta.2+<commit>` |

Everything that Windows or the catalogs compare stays numeric: assembly and file versions, the MSI `ProductVersion`, the `runtime\X.Y.Z` folder, and the application version checked against a catalog's `MinimumAppVersion`. Windows Installer compares only the first three version fields, so the MSI allows same-version upgrades: a later beta, and the final release, replace an installed beta instead of registering beside it. The build accepts a label only on the `ReleaseCandidate` channel, so production releases never carry one.

### Publishing an unsigned release or beta

1. Start from a clean checkout of current `main`, with AV Workstation Toolkit closed so the build does not replace an executable in use.
2. Build with `Build-AVWorkstationToolkit.cmd -BuildChannel ReleaseCandidate`, adding `-PrereleaseLabel beta.N` for a beta. The release manifest then records the commit, clean source state, `ReleaseCandidate` channel, any pre-release label, and unsigned signature state.
3. Run full source QA and `tests\Test-Package.ps1`, adding `-PrereleaseLabel beta.N` for a beta. The package desktop smoke uses the operator's real profile, so run it only with the owner's approval and the app closed; otherwise run `-SkipDesktopSmoke` and have the owner inspect the UI interactively.
4. Tag the built commit `X.Y.Z` or `X.Y.Z-beta.N`. The tag deliberately has no leading `v`, so the production workflow does not run.
5. Create a GitHub release from that tag, marked latest and titled `AV Workstation Toolkit X.Y.Z (unsigned)` or `AV Workstation Toolkit X.Y.Z Beta N (unsigned)`, with exactly the eight standard assets from `artifacts\release\<tag>` and the release packet `docs\releases\<tag>.md` as its notes, followed by the asset checksums and QA results.
6. Never replace a published asset. A corrected build gets a new version or beta number and a new tag.

`1.1.1-beta.1` predates the beta naming convention: its files and window title read `1.1.1`.

## Release checklist

1. Confirm `main` is clean. The repository has been public since 2026-09-24.
   `main` protection blocks force pushes and branch deletion for everyone,
   including administrators; the owner can still change the rule, which is the
   recovery path. By owner decision on 2026-09-24, pull requests and the
   `core-qa` and `package` checks are not yet required because the owner
   changes `main` directly. Require them before accepting outside
   contributions.
2. Run full source QA and targeted PSScriptAnalyzer with zero findings.
3. Build from a clean checkout using the pinned toolchain.
4. Run package QA and inspect the actual UI on an interactive Windows desktop.
5. Sign with the approved certificate and rerun package QA with `-RequireSignature`.
6. Publish the standalone EXE, MSI, portable ZIP, Apache-2.0 license, third-party notice, CycloneDX SBOM, checksum file, and release manifest together. A `vX.Y.Z` tag runs `.github/workflows/release.yml`, verifies the tag against `VERSION`, rebuilds and retests the artifacts, and creates a new immutable release with all eight standard assets. Existing release assets are never replaced in place; corrected bytes require a new version and tag. Offline bundles are separate controlled-delivery artifacts and are never created or uploaded by hosted CI because third-party payloads are not stored in Git.
7. Preserve CI logs and release provenance; never publish `wixpdb`, PDB, certificate, private-key material, local logs, diagnostics, snapshots, caches, or vendor payloads.
