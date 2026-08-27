# AV Workstation Toolkit 1.1.1 Packaging and Release

AV Workstation Toolkit builds a directly downloadable x64 executable, a per-machine MSI, and a one-file portable ZIP. Every standard format contains the same self-contained `AVWorkstationToolkit.exe`; no companion payload directory is required. The standard release set contains exactly eight assets: those three delivery formats, the versioned Apache-2.0 project license, third-party notices, a CycloneDX SBOM, a SHA-256 checksum list, and a release manifest. The checksum list hashes the other seven assets and intentionally does not hash itself. An optional offline bundle can add separately verified third-party installers when redistribution is authorized. Current release candidates are unsigned and are not yet public artifacts.

## Runtime layout

The direct release executable can run from any normal user-writable directory. The MSI installs that executable beneath `%ProgramFiles%\AVWorkstationToolkit` and creates an all-users Start-menu shortcut. The portable ZIP contains only `AVWorkstationToolkit.exe`.

The launcher embeds the .NET 10.0.11 LTS runtime and the complete audited PowerShell/WPF runtime, so target systems do not need a separate .NET installation or adjacent scripts. Windows PowerShell 5.1 and Desktop App Installer/WinGet remain platform prerequisites. The launcher:

1. enumerates its compile-time embedded runtime resources;
2. restores changed or missing files into `%LOCALAPPDATA%\AVWorkstationToolkit\runtime\1.1.1`, verifies every extracted SHA-256 hash against the embedded bytes, and leaves matching files untouched;
3. rejects invalid resource paths and reparse-point cache paths;
4. refuses an elevated operator token;
5. resolves `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` directly;
6. directly starts only the extracted `scripts\Start-AVWorkstationToolkit.ps1` with `RemoteSigned`, STA mode, the fixed per-user data root, shell execution disabled, and no encoded/generated command text;
7. exposes one internal `--vendor-bridge` mode that accepts bounded JSON only on redirected standard input for the audited HTTPS, SFTP, and Credential Manager operations; and
8. restores the Apache-2.0 project license, project third-party notices, and the exact .NET 10.0.11 license and upstream third-party notices under the verified runtime `notices` directory; and
9. never accepts a package ID, credential, URI, or arbitrary PowerShell argument from its command line.

Installed and portable runs write mutable data beneath `%LOCALAPPDATA%\AVWorkstationToolkit`:

```text
%LOCALAPPDATA%\AVWorkstationToolkit
├── runtime\1.1.1
│   └── notices
├── logs\requests
├── reports
├── vendor-cache
├── trusted-sftp-hosts.json
└── snapshots
```

MSI uninstall intentionally preserves this evidence. Remove it only through an approved data-retention process.

### Legacy data compatibility

On the first packaged launch, the 1.1.1 runtime checks the historical `%LOCALAPPDATA%\AVinite` root and progressively establishes `%LOCALAPPDATA%\AVWorkstationToolkit`. Migration is intentionally non-destructive and allowlisted: AV Workstation Toolkit can copy a validated SFTP host store, bounded launcher/request logs, and hash-matching vendor-cache payload metadata. It never copies runtime scripts, arbitrary files, reports, or snapshots; rejects reparse-point roots and paths; does not overwrite existing new-state files; writes a completion marker; and leaves the historical directory in place for retention review. Repeated launches read the marker without rewriting migrated files.

New SFTP credentials use the `AVWorkstationToolkit:VendorSftp:` Credential Manager prefix. The vendor bridge can read the historical credential target only as a fallback, and an explicit **Forget saved** action removes both identities. Password material is never copied into files, arguments, logs, or diagnostics.

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

The wrapper invokes the reviewed PowerShell build using inbox Windows PowerShell and `RemoteSigned`. Before deleting any prior output, the build enumerates installed SDKs, verifies that `global.json` selected a stable .NET 10 SDK under the supported feature-band policy, performs a non-mutating locked restore, runs a machine-readable NuGet vulnerability audit, validates `VERSION` agreement and launcher target/runtime settings, and runs the deterministic catalog compiler in `-Check` mode. It then runs source QA, publishes a trimmed self-contained uncompressed `win-x64` single-file launcher with `--no-restore` and embedded resources, builds the one-file MSI and ZIP, copies the reviewed third-party notices, creates a deterministic CycloneDX 1.6 SBOM with reviewed license/scope metadata, and writes schema-v3 release metadata plus SHA-256 checksums beneath `artifacts\release\1.1.1`.

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

`-BuildOfflineBundle` refuses an absent, changed, reparse-point, or signer-mismatched payload. It emits `AV-Workstation-Toolkit-1.1.1-offline-bundle.zip` with this layout:

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

The suite copies the release EXE into an otherwise empty directory and runs it there, verifies deterministic runtime placement and embedded notices, proves a second launch does not rewrite unchanged files, verifies cache repair, checks EXE/MSI identity and SBOM/manifest/checksum agreement, runs the packaged vendor-bridge self-test, confirms the ZIP contains the same single executable, runs the packaged WPF control/workflow smoke path, and administratively extracts the MSI without registering it. The MSI-extracted launcher must be byte-identical to the standalone launcher and satisfy the same signature policy. QA does not connect to an authenticated vendor, download third-party software, install the MSI, or invoke a WinGet change action.

## Signing

The repository contains no private key. To sign with an organization-approved code-signing certificate in either the CurrentUser or LocalMachine certificate store:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\build\Build-Release.ps1 `
  -CertificateThumbprint <approved-certificate-thumbprint> `
  -CertificateStore Auto `
  -RequireSignature `
  -BuildChannel Production

powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-Package.ps1 `
  -RequireSignature
```

The build requires the selected certificate to be currently valid, carry the code-signing EKU, and expose its private key. A validated Microsoft Windows SDK `signtool.exe` signs both artifacts with SHA-256, obtains a configurable HTTPS RFC3161 timestamp (`-TimestampServer`), verifies the signature and timestamp, and records signer and timestamp identity before release hashes are generated. The portable ZIP is authenticated by its published checksum and the signature on the contained executable. No private key or password is accepted in a command line or committed to the repository.

The tagged release workflow requires a base64 PFX and password through `AVWORKSTATIONTOOLKIT_SIGNING_PFX_BASE64` and `AVWORKSTATIONTOOLKIT_SIGNING_PFX_PASSWORD` secrets. During the repository rename only, the historical `AVINITE_SIGNING_PFX_BASE64` and `AVINITE_SIGNING_PFX_PASSWORD` names remain accepted as a CI compatibility fallback because GitHub cannot rename or copy encrypted secrets through a workflow. Configure the canonical names and remove the fallback after the repository settings are migrated. The workflow imports the certificate into an ephemeral CurrentUser store, builds with `-RequireSignature -BuildChannel Production`, runs package QA with `-RequireSignature`, and removes the imported certificate and temporary PFX in an always-run cleanup step. Missing signing secrets fail the tagged release; they never silently produce a public unsigned artifact. Ordinary local and pull-request builds remain explicitly unsigned development builds.

Consequently, the current tag-triggered workflow is not an unsigned first-release
path: without valid organizational signing secrets it fails before publication.
If the owner decides that an initial unsigned public artifact is needed to
establish public project history before a SignPath application, that artifact
must use a separately reviewed, explicitly release-candidate publication
procedure. Production mode and the tag-triggered production workflow must not
be weakened or relabeled to make that possible.

AV Workstation Toolkit is preparing—but has not submitted or been accepted—for
SignPath Foundation signing. Future hosted signing must sign the exact verified
EXE, package that returned signed EXE into the MSI, sign the exact MSI, and
generate final provenance without rebuilding application binaries. See the
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
  -ReleaseRoot .\artifacts\release\1.1.1 `
  -ScanWithDefender
```

The optional scan uses an existing valid Microsoft-signed `MpCmdRun.exe`, reports unavailable versus passed, and fails on a nonzero scan/detection result. It does not change Defender policy, disable remediation, add exclusions, or upload artifacts to a public service. Pass `-RequireDefender` only on a release machine where Defender availability is an explicit prerequisite.

## Release checklist

1. Confirm `main` is clean. While the repository remains private under the
   current GitHub account capability, branch protection/rulesets are unavailable
   (`Upgrade to GitHub Pro or make this repository public to enable this
   feature`). Immediately after making the repository public, configure and
   verify `main` protection before normal public development continues: require
   pull-request changes and the existing `core-qa` and `package` checks, and
   prevent force pushes and branch deletion while retaining an owner recovery
   path.
2. Run full source QA and targeted PSScriptAnalyzer with zero findings.
3. Build from a clean checkout using the pinned toolchain.
4. Run package QA and inspect the actual UI on an interactive Windows desktop.
5. Sign with the approved certificate and rerun package QA with `-RequireSignature`.
6. Publish the standalone EXE, MSI, portable ZIP, Apache-2.0 license, third-party notice, CycloneDX SBOM, checksum file, and release manifest together. A `vX.Y.Z` tag runs `.github/workflows/release.yml`, verifies the tag against `VERSION`, rebuilds and retests the artifacts, and creates a new immutable release with all eight standard assets. Existing release assets are never replaced in place; corrected bytes require a new version and tag. Offline bundles are separate controlled-delivery artifacts and are never created or uploaded by hosted CI because third-party payloads are not stored in Git.
7. Preserve CI logs and release provenance; never publish `wixpdb`, PDB, certificate, private-key material, local logs, diagnostics, snapshots, caches, or vendor payloads.
