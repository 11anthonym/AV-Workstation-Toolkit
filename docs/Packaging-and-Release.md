# AV Workstation Toolkit 1.1.3 Packaging and Release

AV Workstation Toolkit builds a directly downloadable x64 executable, a per-machine MSI, and a one-file portable ZIP. Every standard format contains the same self-contained `AVWorkstationToolkit.exe`; no companion payload directory is required. The standard release set contains exactly eight assets: those three delivery formats, the versioned Apache-2.0 project license, third-party notices, a CycloneDX SBOM, a SHA-256 checksum list, and a release manifest. The checksum list hashes the other seven assets and intentionally does not hash itself. An optional offline bundle can add separately verified third-party installers when redistribution is authorized. Until hosted signing is operational, public releases are unsigned release-candidate builds published under the [unsigned publication procedure](#unsigned-publication); production releases remain signature-required.

## Runtime layout

The direct release executable can run from any normal user-writable directory. The MSI installs that executable beneath `%ProgramFiles%\AVWorkstationToolkit` and creates an all-users Start-menu shortcut. The portable ZIP contains only `AVWorkstationToolkit.exe`.

The launcher embeds the .NET 10.0.11 LTS compiled WPF application, the self-contained compiled worker, and reviewed catalogs/notices, so target systems do not need a separate .NET installation or adjacent scripts. Desktop App Installer/WinGet is the managed-package prerequisite. PowerShell is not required by the packaged application runtime. The launcher:

1. enumerates its compile-time embedded runtime resources;
2. restores changed or missing files into `%LOCALAPPDATA%\AVWorkstationToolkit\runtime\1.1.3`, verifies every extracted SHA-256 hash against the embedded bytes, and leaves matching files untouched;
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
├── runtime\1.1.3
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

The wrapper invokes the reviewed PowerShell build using inbox Windows PowerShell and `RemoteSigned`. Before deleting any prior output, the build enumerates installed SDKs, verifies that `global.json` selected a stable .NET 10 SDK under the supported feature-band policy, performs non-mutating locked restores, runs a machine-readable NuGet vulnerability audit, validates `VERSION` agreement and launcher/worker target settings, and runs the deterministic catalog compiler in `-Check` mode. It then runs source QA, publishes the self-contained untrimmed compiled worker, signs/verifies it when signing is configured, embeds those exact worker bytes plus the five strict runtime manifests into the self-contained untrimmed compiled-WPF bootstrap, builds the one-file MSI and ZIP, copies the reviewed third-party notices, creates a deterministic CycloneDX 1.6 SBOM containing the worker hash, and writes schema-v3 release metadata with compiled-runtime/signature identity plus SHA-256 checksums beneath `artifacts\release\1.1.3` (for a release candidate, `artifacts\release\1.1.3-rc.N`; see [release candidate naming](#beta-naming)).

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

`-BuildOfflineBundle` refuses an absent, changed, reparse-point, or signer-mismatched payload. It emits `AV-Workstation-Toolkit-1.1.3-offline-bundle.zip` with this layout:

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

Package QA never uses the operator's profile, so it is safe to run on a workstation that also runs AV Workstation Toolkit, without closing it or backing anything up:

- Every packaged launch, including the production smoke, uses a `%TEMP%\AVWorkstationToolkit-package-qa-<32 hex>` data root that the run creates fresh, prints, and deletes afterward even when a check fails. The launcher accepts `--production-smoke` only with such a root: `ProductionRuntimePolicy.RequirePackageQaDataRoot` requires a non-reparse folder with that exact name directly beneath the temporary folder and outside `%LOCALAPPDATA%\AVWorkstationToolkit`. The smoke composes the production services against it, validates the extracted worker by hash as production does, and never starts the worker, which itself still accepts only the canonical root.
- The run reads the real `%LOCALAPPDATA%\AVWorkstationToolkit` before and after and fails if anything in it changed. If a copy of the app was already open, the failure says so, because that copy may have written the change itself.
- The isolated root must show what the packaged app wrote: the extracted `runtime\<version>`, its `logs\requests` and `reports` folders, and a migration checklist that the first smoke saves and the second reloads from disk and finishes. That proves the smoke exercised persistence rather than skipping it.
- The MSI is only extracted with `msiexec /a`. Installing it would not be isolated: the package is per-machine, shares one upgrade code with every release, and allows same-version major upgrades, so installing a QA build would replace an installed release in `Program Files\AVWorkstationToolkit`, along with its Start menu shortcut and Installed Apps entry. The run reads the product's Windows Installer upgrade-family registration, Installed Apps entries, Start menu folder, and install folder before and after, and fails if any changed. An installation test belongs on a disposable virtual machine.

Package QA stays a separate, explicit step rather than part of every `ReleaseCandidate` build. Its desktop smoke refreshes live WinGet and vendor release state, which is bounded but not deterministic, and it adds several minutes. Run it on every release candidate and before publishing any release.

A completed release candidate or release is never rebuilt. Before it changes anything, the build refuses a target folder whose release manifest records the `ReleaseCandidate` or `Production` channel, whatever commit it came from: a candidate that needs changes gets the next `rc.N`, and an unpublished release whose QA failed is rebuilt only after someone moves its folder aside. A folder without a manifest (an unfinished build) and a `Development` build can be rebuilt. The build also records every other folder in `artifacts\release` before it starts and fails if any of them changed by the end. The tagged signing workflow removes its own intermediate unsigned build before each signed rebuild in its fresh workspace.

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
  -ReleaseRoot .\artifacts\release\1.1.3 `
  -ScanWithDefender
```

The optional scan uses an existing valid Microsoft-signed `MpCmdRun.exe`, reports unavailable versus passed, and fails on a nonzero scan/detection result. It does not change Defender policy, disable remediation, add exclusions, or upload artifacts to a public service. Pass `-RequireDefender` only on a release machine where Defender availability is an explicit prerequisite.

<a id="release-lifecycle"></a>

## Release lifecycle

`VERSION` names the release the source is working toward. Every build of it is one of four kinds:

| Stage | Build | Name people see | Published |
|---|---|---|---|
| Development | `Build-AVWorkstationToolkit.cmd` (the `Development` channel, no label) | `X.Y.Z`; product version `X.Y.Z+<commit>` | Never. It is a local working copy that the next build of the same version replaces. |
| Release candidate | `-BuildChannel ReleaseCandidate -PrereleaseLabel rc.N` | `X.Y.Z-rc.N`, shown as "X.Y.Z RC N" | Not by default. A candidate is the evidence the owner reviews before a release; it becomes a GitHub pre-release only if the owner decides so. |
| Unsigned release | `-BuildChannel ReleaseCandidate` without a label | `X.Y.Z` | Yes, with the owner's approval, until hosted signing is operational. |
| Signed release | the tagged `vX.Y.Z` workflow on the `Production` channel, signature required | `X.Y.Z` | Yes, by that workflow only. |

A candidate and the unsigned release of the same version are both `ReleaseCandidate` builds, so a candidate differs from the release it leads to only by its label. `Production` is never labeled, never unsigned, and never used for a local build.

<a id="beta-publication"></a>

## Unsigned publication

Until hosted signing is operational, public releases are unsigned `ReleaseCandidate` builds published as GitHub releases, each with the owner's explicit approval. They never use the production channel, the signature-required QA mode, or the `v*.*.*` production tag, so the fail-closed signing workflow does not run. The release title and notes state that the build is unsigned, and the newest release is marked as the latest so the repository's download link leads to it. An unsigned public build is either:

- a **release**, `X.Y.Z`, built without a label, whose files and window title read `X.Y.Z` (1.1.2 is the first); or
- a **release candidate**, `X.Y.Z-rc.N`, published as a GitHub pre-release only when the owner decides to.

A version number is never reused for different bytes: once `X.Y.Z` is published unsigned, the first signed release is a later version.

<a id="beta-naming"></a>

### Release candidate naming

A release candidate is a [Semantic Versioning](https://semver.org/) pre-release of the version in `VERSION`: `X.Y.Z-rc.N`, where `N` counts up from 1 for each version and is never reused. The dot matters: `rc.10` sorts after `rc.9`. A completed candidate is never rebuilt (see [Package QA](#package-qa)), so a candidate that needs changes becomes the next `rc.N`. The label appears wherever people identify a build:

| Where | Example |
|---|---|
| Artifact files and folder | `artifacts\release\1.1.3-rc.1\AV-Workstation-Toolkit-1.1.3-rc.1-win-x64.exe` |
| Window title and About box | 1.1.3 RC 1 |
| Diagnostics, SBOM, and release manifest `ReleaseName` | `1.1.3-rc.1` |
| Windows product version | `1.1.3-rc.1+<commit>` |
| Git tag and release title, only if the owner publishes it | `1.1.3-rc.1`; AV Workstation Toolkit 1.1.3 RC 1 (unsigned) |

Everything that Windows or the catalogs compare stays numeric: assembly and file versions, the MSI `ProductVersion`, the `runtime\X.Y.Z` folder, and the application version checked against a catalog's `MinimumAppVersion`. Windows Installer compares only the first three version fields, so the MSI allows same-version upgrades: a later candidate, and the final release, replace an installed candidate instead of registering beside it. The build accepts a label only on the `ReleaseCandidate` channel, so production releases never carry one.

`rc.N` is the only label the build, the SBOM, package QA, endpoint-trust QA, and the app accept. The historical labels are facts about earlier builds, not a current convention: the first public releases were the betas `1.1.1-beta.1` (whose files and window title read `1.1.1`, before labels existed) and `1.1.1-beta.2`, built from their own tagged sources, and the local QA builds `1.1.2-alpha.1`, `1.1.3-alpha.1`, and `1.1.3-alpha.2` were never published and use no candidate number.

### Publishing an unsigned release or release candidate

1. Start from a clean checkout of the commit to release, with AV Workstation Toolkit closed so the build does not replace an executable in use.
2. Build with `Build-AVWorkstationToolkit.cmd -BuildChannel ReleaseCandidate`, adding `-PrereleaseLabel rc.N` for a candidate. The release manifest then records the commit, clean source state, `ReleaseCandidate` channel, any label, and unsigned signature state.
3. Run full source QA and `tests\Test-Package.ps1`, adding `-PrereleaseLabel rc.N` for a candidate. Package QA runs in isolated data roots and proves the operator's profile and installed copy unchanged (see [Package QA](#package-qa)); the owner still inspects the UI interactively.
4. For a release (not a candidate), make the release commit that moves the current-release statements from the previous release to `X.Y.Z` together with its notes `docs\releases\X.Y.Z.md`: the README download section and its three file names, SECURITY.md, the endpoint-security baseline, the SignPath readiness record, and the bug-report form's version placeholder, and remove `(unreleased)` from the change log's `Version X.Y.Z` entry. Source QA fails if these disagree with the newest release notes, so a version bump alone can never move them ahead of publication. Build the release from that commit.
5. Tag the built commit `X.Y.Z`, or `X.Y.Z-rc.N` for a candidate the owner publishes. The tag deliberately has no leading `v`, so the production workflow does not run.
6. Create a GitHub release from that tag with exactly the eight standard assets from `artifacts\release\<tag>` and the release packet `docs\releases\<tag>.md` as its notes, followed by the asset checksums and QA results. A release is titled `AV Workstation Toolkit X.Y.Z (unsigned)` and marked latest; a published candidate is titled `AV Workstation Toolkit X.Y.Z RC N (unsigned)` and marked as a pre-release.
7. Never replace a published asset. A corrected build gets a new version or candidate number and a new tag.

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
