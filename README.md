# AV Workstation Toolkit — AV/IT Workstation Setup and Maintenance

AV Workstation Toolkit is a Windows application for planning, installing, and
maintaining a controlled AV/IT workstation software baseline. The repository
contains the reusable product, its policy catalogs, build pipeline, operator
guidance, and verification evidence. Personal workstation assessments and
other machine-specific operational evidence are excluded from maintained source
and release artifacts.

## What AV Workstation Toolkit does

- inventories approved WinGet applications and supported external AV tools;
- distinguishes managed packages, manual vendor handoffs, inventory-only
  records, and non-actionable commercial AV awareness;
- builds a read-only workstation plan before any change;
- permits only exact allowlisted WinGet install/update requests, one package at
  a time, with live revalidation and risk-sensitive reboot policy;
- preserves version, lifecycle, access, licensing, and system-impact knowledge
  without turning that knowledge into uncontrolled software execution;
- keeps diagnostics, logs, credentials, and vendor-cache evidence local.

## Start here

- [Desktop app operator guide](docs/AV-Workstation-Toolkit-Operator-Guide.md)
- [Packaging and release guide](docs/Packaging-and-Release.md)
- [Endpoint-security behavior baseline](docs/Endpoint-Security-Behavior.md)
- [Security policy](SECURITY.md)
- [Privacy policy](PRIVACY.md)
- [Code signing policy](docs/Code-Signing-Policy.md)
- [SignPath readiness record](docs/SignPath-Readiness.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)
- [Contributing](CONTRIBUTING.md)
- [Architecture and safety model](docs/AV-Workstation-Toolkit-Architecture-and-Safety.md)
- [C# migration architecture contract](docs/CSharp-Migration-Architecture.md)
- [C# migration coverage and retirement matrix](docs/CSharp-Migration-Coverage.md)
- [Commercial AV catalog model](docs/Commercial-AV-Catalog.md)
- [Workstation research reconciliation and backlog](docs/Workstation-Research-Backlog.md)
- [Security audit](docs/AV-Workstation-Toolkit-Security-Audit.md)
- [QA report](docs/AV-Workstation-Toolkit-QA-Report.md)
- [Team application-onboarding playbook](docs/Team-Onboarding-Playbook.md)
- [Change log](docs/CHANGELOG.md)
- [Reusable snapshot script](scripts/Get-WorkstationSnapshot.ps1)
- [Allowlisted deployment script](scripts/Invoke-AVWorkstationToolkitDeployment.ps1)
- [Allowlisted maintenance script](scripts/Invoke-AVWorkstationToolkitMaintenance.ps1)
- [Managed application catalog](manifests/managed-applications.json)
- [Managed catalog signing contract](docs/Managed-Catalog-Publishing.md)
- [External application catalog](manifests/external-applications.json)
- [Commercial AV catalog model and source workflow](docs/Commercial-AV-Catalog.md)
- [External provider and credential guide](docs/External-Provider-Guide.md)
- [Team winget baseline](manifests/winget-team-baseline.json)
- [Manifest usage notes](manifests/README.md)

## Download / releases

Releases are published on the
[GitHub releases page](https://github.com/11anthonym/AV-Workstation-Toolkit/releases).
The current release is the unsigned public beta
[`1.1.1-beta.1`](docs/releases/1.1.1-beta.1.md). Verify each download against
the published SHA-256 checksum list before running it.

The 1.1.1 outputs are:

- run `AV-Workstation-Toolkit-1.1.1-win-x64.exe` directly;
- install `AV-Workstation-Toolkit-1.1.1-x64.msi`, then open **AV Workstation Toolkit** from the Start menu; or
- extract `AV-Workstation-Toolkit-1.1.1-win-x64.zip` and run `AVWorkstationToolkit.exe`.

The tagged workflow's standard release set contains exactly eight assets: the
three delivery formats above, the Apache-2.0 `LICENSE`, third-party notices, a
CycloneDX SBOM, a release manifest, and a SHA-256 checksum list. The checksum
list covers the other seven assets and does not hash itself.

AV Workstation Toolkit has prepared a fail-closed SignPath release workflow.
Current artifacts remain unsigned, including the public beta, until external
configuration and approval are completed. Authenticode signing does not
guarantee that SmartScreen or an organization's endpoint policy will accept a
new binary.

The executable carries the .NET 10 LTS compiled WPF application and an independently
validating compiled worker. On launch it restores and hash-verifies its versioned
runtime cache beneath `%LOCALAPPDATA%\AVWorkstationToolkit\runtime` and stores mutable
data beneath `%LOCALAPPDATA%\AVWorkstationToolkit`. PowerShell is not part of the
packaged application runtime. The bootstrap removes only specifically recognized
retired runtime files left by earlier versions and never falls back to them.

## Build from source

After cloning the repository, compile every release artifact with one command
from the repository root:

```cmd
Build-AVWorkstationToolkit.cmd
```

The standalone result is written to
`artifacts\release\1.1.1\AV-Workstation-Toolkit-1.1.1-win-x64.exe`. Double-click
`Launch-AVWorkstationToolkit.cmd` after building to run that compiled executable. Before the
first build, the launcher starts the compiled App project directly.

AV Workstation Toolkit combines 30 exact-ID WinGet applications, 25 operational external records, and 283 non-deployable commercial AV awareness records. The resulting 338-record catalog can describe role, discipline, workflow, lifecycle, licensing, distribution policy, installation form, metadata verification, provenance, access restrictions, account/training requirements, workstation impact, supported platform, version policy, and official source without turning catalog knowledge into installation permission.

WinGet apps can be selected for managed install or update. External apps use fail-closed vendor-page, signed direct-download, authenticated-SFTP, parent-provider, rights-approved offline-bundle, inventory-only, or awareness modes. Every external app remains on manual deployment and maintenance hold, never enters the WinGet action worker, and is never executed by AV Workstation Toolkit. A pending Windows reboot remains prominent: low-risk applications may continue, while driver-, service-, and listener-bearing actions are blocked until restart.

The commercial catalog spans control, DSP, AVoIP, Dante/Milan/AVB, RF, measurement, prediction, conferencing, cameras, displays, signage, BrightSign, dvLED, intercom, media servers/show control, broadcast/NDI, lighting, field diagnostics, network/serial/USB/EDID tools, firmware utilities, servers, web services, embedded interfaces, and legacy support. Use the [catalog model](docs/Commercial-AV-Catalog.md) for taxonomy and filtering behavior and the [external provider guide](docs/External-Provider-Guide.md) for exact acquisition boundaries.

Crestron retains one common secure MasterInstaller provider. Toolbox, SIMPL Windows, VT Pro-e, Database, Device Database, Smart Graphics, and DM NVX Tool are independently detectable children, but all inherit the same seven-product allowlist, host-key trust, constrained SFTP root, signer policy, and per-user Credential Manager workflow. AV Workstation Toolkit does not create seven independent credential or download paths.

Q-SYS Designer Software LTS remains vendor-page-only. AV Workstation Toolkit recognizes installed versions, checks the official Q-SYS page for the current LTS version, and opens the vendor download page when a newer or missing version needs attention. Q-SYS 9.13.2 is intentionally not embedded because the [Q-SYS EULA](https://help.qsys.com/q-sys_9.13/Content/Legal.htm) prohibits external redistribution.

For software that your organization is authorized to redistribute, add a hash-pinned local payload and build an offline delivery ZIP:

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

The authoring command copies the payload into the git-ignored local depot and embeds its SHA-256 hash and signer policy in the catalog. The offline ZIP contains `AVWorkstationToolkit.exe` plus the verified package. AV Workstation Toolkit shows the file in Explorer for deliberate user handoff; it does not silently execute third-party installers.

Run the complete non-installing QA suite after any code or catalog edit:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Run-Tests.ps1
```

The shipping implementation is the compiled C# WPF App and independent compiled
worker. Its typed Domain/Application layers own catalog, filtering, planning,
policy, request/IPC, Windows inventory, vendor delivery, diagnostics, and exact-ID
worker behavior. Build it, run the deterministic test suite, and exercise the
worker, action-flow, WPF and live-rehearsal boundaries with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-CompiledRuntime.ps1
```

An optional non-mutating host integration check reports which read-only providers
were actually exercised and which were unavailable; workstation package contents
are not treated as golden test data:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-CompiledReadOnlyIntegration.ps1 -NoBuild
```

GitHub Actions also runs the host-independent safety subset and targeted PSScriptAnalyzer policy on a clean Windows runner for every push to `main` and every pull request.

Build and verify all three distributables:

```powershell
.\Build-AVWorkstationToolkit.cmd
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-Package.ps1
```

Release outputs are written beneath `artifacts\release\1.1.1` and are ignored by Git.

The command-line deployment and maintenance scripts remain available for operators who prefer a terminal, but the desktop app is the primary workflow.

## Repository map

| Path | Purpose |
|---|---|
| `src/AVWorkstationToolkit.Launcher/` | Self-contained compiled WPF bootstrap and embedded-runtime integrity |
| `src/AVWorkstationToolkit.Domain/` | Production typed catalog, version, filtering, planning, and policy implementation |
| `src/AVWorkstationToolkit.Application/` | Production use-case and infrastructure-abstraction layer |
| `src/AVWorkstationToolkit.Infrastructure.Windows/` | Production Windows inventory, process, file, vendor, credential, and trust adapters |
| `src/AVWorkstationToolkit.App/` | Production compiled WPF composition and presentation |
| `src/AVWorkstationToolkit.Worker/` | Production independent compiled action worker |
| `AVWorkstationToolkit.slnx` | Locked, warning-clean .NET 10 migration solution |
| `installer/` | Pinned WiX x64 MSI project |
| `build/` | Reproducible staging, signing, packaging, and checksum workflow |
| `catalog/vendors/` | Authoritative per-manufacturer awareness sources; never loaded directly at runtime |
| `Build-AVWorkstationToolkit.cmd` | One-command release build from a fresh clone |
| `.github/workflows/release.yml` | Tag-validated GitHub release build and asset publication |
| `docs/` | Operator guidance, architecture, security audit, QA evidence, onboarding playbook, and change log |
| `manifests/managed-applications.json` | Canonical approved exact-ID WinGet catalog; the only managed-package source the product loads |
| `manifests/external-applications.json` | Operational external detection/version/provider policy |
| `manifests/commercial-av-catalog.json` | Deterministically compiled, embedded, non-deployable commercial AV awareness metadata |
| `scripts/Add-AVWorkstationToolkitExternalPackage.ps1` | Rights-gated authoring command for hash-pinned offline payloads |
| `external-packages/` | Local third-party payload depot; always ignored by Git |
| `scripts/AVWorkstationToolkit.Core.psd1` / `.psm1` | Legacy behavior characterization plus development/operator tooling; not packaged |
| `scripts/Invoke-AVWorkstationToolkitAction.ps1` | Legacy worker characterization source; not packaged or launched in production |
| `tests/` | Non-installing safety, parser, policy, UI smoke, deterministic fixtures, and process/provider boundary tests |
| `%LOCALAPPDATA%\AVWorkstationToolkit\logs` | Installed/portable execution evidence |
| `%LOCALAPPDATA%\AVWorkstationToolkit\reports` | Installed/portable exported plans |
| `logs/`, `reports/` | Source-checkout evidence; ignored by Git |

## Operating rules

- Inventory before changing state.
- Install the MSI beneath Program Files, or run the standalone release executable from any normal user-writable directory.
- Launch AV Workstation Toolkit and every repository script as a standard user. Individual installers may request elevation through Windows; the repository code itself never runs elevated.
- Separate standard software from role-specific and client-specific tools.
- Treat services, drivers, protocol bindings, listeners, licensing, and firmware compatibility as explicit decisions.
- Record every install with source, version rule, owner, validation, and exception reason.
- Never use a workstation's raw winget export as an unattended deployment manifest.
- Never manage BitLocker, EDR/antivirus, SCCM/Intune, VPN/security clients, or corporate remote-support agents through this project. The deployment tool is limited to explicitly approved user applications.
- Installation and maintenance are plan-only unless an explicit change switch is supplied. Explicit Windows Update or Component Based Servicing reboot state blocks risk-bearing driver, service, and listener actions but is a warning for ordinary low-risk applications; generic queued file cleanup is not treated as a reboot signal.

## Local evidence

Raw snapshots and execution logs are intentionally ignored by Git. Packaged
runs place them under `%LOCALAPPDATA%\AVWorkstationToolkit`; source checkouts retain
repository-local folders. Treat this output as sensitive operational evidence
and store it only in an approved restricted location.

## Uninstallation

For the MSI, open **Windows Settings -> Apps -> Installed apps**, find
**AV Workstation Toolkit**, and choose **Uninstall**. The classic **Programs
and Features** control panel provides the same Windows Installer removal path.

For the direct EXE or portable ZIP, close AV Workstation Toolkit and delete the
downloaded executable or extracted portable directory. There is no background
service to remove.

Application-owned data is stored beneath
`%LOCALAPPDATA%\AVWorkstationToolkit`. Removing that directory is optional and
destructive: it deletes local logs, plans, diagnostics, trusted host records,
and verified vendor-cache evidence. Review retention needs first. Historical
`%LOCALAPPDATA%\AVinite` material may remain intentionally after migration as
retained evidence; branding cleanup does not authorize automatic deletion.

## Security and privacy

Read [SECURITY.md](SECURITY.md) before reporting a vulnerability and do not put
secrets or unredacted operational evidence in a public issue. The
[privacy policy](PRIVACY.md) documents actual local storage and bounded network
behavior, including automatic WinGet/vendor release checks and explicit HTTPS
or authenticated-SFTP handoffs. AV Workstation Toolkit has no telemetry,
analytics, or crash-reporting service.

The maintained execution and release boundaries are described in the
[architecture and safety model](docs/AV-Workstation-Toolkit-Architecture-and-Safety.md),
[signed managed-catalog update model](docs/Managed-Catalog-Updates.md),
[endpoint-security baseline](docs/Endpoint-Security-Behavior.md), and
[security audit](docs/AV-Workstation-Toolkit-Security-Audit.md).

## Code signing

Current development, release-candidate, and beta artifacts are unsigned. The build
retains a fail-closed organizational Authenticode path and prepared SignPath
workflow, while the project has not submitted to or been accepted by SignPath
Foundation. See the [code signing policy](docs/Code-Signing-Policy.md) and
[SignPath readiness record](docs/SignPath-Readiness.md). No private key,
certificate password, or signing-service credential belongs in this
repository.

## License

AV Workstation Toolkit is licensed under the [Apache License 2.0](LICENSE)
([SPDX: Apache-2.0](https://spdx.org/licenses/Apache-2.0.html)). Third-party
components remain governed by their own licenses; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Third-party notices

[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) identifies runtime, build,
test, hosted-service, and system dependencies; their roles and versions; what
is actually distributed; and relevant notice requirements. Commercial AV
products in the catalog are metadata only and are not redistributed project
dependencies.

## Contributing and project status

AV Workstation Toolkit is in public beta. Bug reports and catalog corrections
are welcome as [GitHub issues](https://github.com/11anthonym/AV-Workstation-Toolkit/issues).
Review [CONTRIBUTING.md](CONTRIBUTING.md) for the Windows build/test commands,
dependency-lock rules, catalog boundaries, and security-sensitive review
expectations. Contributions are submitted under the project Apache-2.0 license
unless separately stated.
