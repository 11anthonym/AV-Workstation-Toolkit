# AV Workstation Toolkit

AV Workstation Toolkit is a Windows desktop application for planning, installing,
and maintaining a controlled AV/IT workstation software baseline. It knows the
engineering tools AV and IT teams use, shows what is installed and out of date,
and installs or updates only applications your team has explicitly approved.

The project is in **public beta**. The current release is the unsigned
[`1.1.1-beta.2`](docs/releases/1.1.1-beta.2.md); use it on test or pilot
workstations until you have reviewed it against your organization's software
policy.

## What it does

- inventories approved WinGet applications and supported external AV tools;
- distinguishes managed packages, manual vendor handoffs, inventory-only
  records, and non-actionable commercial AV awareness;
- builds a read-only workstation plan before any change;
- permits only exact allowlisted WinGet install/update requests, one package at
  a time, with live revalidation and risk-sensitive reboot policy;
- preserves version, lifecycle, access, licensing, and system-impact knowledge
  without turning that knowledge into uncontrolled software execution;
- keeps diagnostics, logs, credentials, and vendor-cache evidence local.

## Download

Releases are published on the
[GitHub releases page](https://github.com/11anthonym/AV-Workstation-Toolkit/releases):
[download the latest release](https://github.com/11anthonym/AV-Workstation-Toolkit/releases/latest).
Verify each download against the published SHA-256 checksum list before running
it.

Each release offers three delivery formats. A beta's file names carry its
label ([beta naming](docs/Packaging-and-Release.md#beta-naming)); for Beta 2:

- run `AV-Workstation-Toolkit-1.1.1-beta.2-win-x64.exe` directly;
- install `AV-Workstation-Toolkit-1.1.1-beta.2-x64.msi`, then open **AV Workstation Toolkit** from the Start menu; or
- extract `AV-Workstation-Toolkit-1.1.1-beta.2-win-x64.zip` and run `AVWorkstationToolkit.exe`.

The tagged workflow's standard release set contains exactly eight assets: the
three delivery formats above, the Apache-2.0 `LICENSE`, third-party notices, a
CycloneDX SBOM, a release manifest, and a SHA-256 checksum list. The checksum
list covers the other seven assets and does not hash itself.

AV Workstation Toolkit has prepared a fail-closed SignPath release workflow.
Current artifacts remain unsigned, including the public beta, until external
configuration and approval are completed. Authenticode signing does not
guarantee that SmartScreen or an organization's endpoint policy will accept a
new binary.

## Requirements

- Windows 10 or Windows 11 x64.
- A standard-user session. The app refuses to start elevated; individual
  installers can still request elevation through Windows.
- App Installer/WinGet for managed applications.

The download is self-contained, so no separate .NET installation is needed. The
executable carries the .NET 10 LTS compiled WPF application and an independently
validating compiled worker. On launch it restores and hash-verifies its
versioned runtime cache beneath `%LOCALAPPDATA%\AVWorkstationToolkit\runtime` and
stores mutable data beneath `%LOCALAPPDATA%\AVWorkstationToolkit`. PowerShell is
not part of the packaged application runtime.

## Quick start

1. Download and verify a release, then launch it as a standard user.
2. Let the first read-only refresh finish. It checks installed software and
   bounded vendor release pages; it changes nothing.
3. Use **Missing apps** or **Available updates**, select only the applications
   you need, and choose **Install selected** or **Update selected**.
4. Review the exact package list, and the separate risk prompt for any driver-,
   service-, or listener-bearing application, before confirming.

The [operator guide](docs/AV-Workstation-Toolkit-Operator-Guide.md) explains
every status, filter, delivery button, and failure path. Operators who prefer a
terminal can plan, install, and update approved applications with the
allowlisted [command-line deployment and maintenance scripts](scripts/README.md);
the desktop app is the primary workflow.

## The catalog

AV Workstation Toolkit combines 30 exact-ID WinGet applications, 25 operational
external records, and 283 non-deployable commercial AV awareness records. The
resulting 338-record catalog can describe role, discipline, workflow, lifecycle,
licensing, distribution policy, installation form, metadata verification,
provenance, access restrictions, account/training requirements, workstation
impact, supported platform, version policy, and official source without turning
catalog knowledge into installation permission.

WinGet apps can be selected for managed install or update. External apps use
fail-closed vendor-page, signed direct-download, authenticated-SFTP,
parent-provider, rights-approved offline-bundle, inventory-only, or awareness
modes. Every external app remains on manual deployment and maintenance hold,
never enters the WinGet action worker, and is never executed by AV Workstation
Toolkit. For example, Q-SYS Designer LTS stays vendor-page-only because its
license restricts redistribution, and Crestron's seven MasterInstaller tools
share one host-key-verified SFTP provider and credential workflow.

The **Device Lookup** view answers "what software does this device need?" from a
separately signed reference catalog that updates in the app.

## Documentation

**Using the app**

- [Operator guide](docs/AV-Workstation-Toolkit-Operator-Guide.md)
- [Team application-onboarding playbook](docs/Team-Onboarding-Playbook.md)
- [Repository scripts: command-line deployment and maintenance, snapshot, readiness, and catalog authoring](scripts/README.md)
- Release notes for [1.1.1 Beta 2](docs/releases/1.1.1-beta.2.md) and [Beta 1](docs/releases/1.1.1-beta.1.md), and the [change log](docs/CHANGELOG.md)

**Security, privacy, and trust**

- [Security policy](SECURITY.md) and [privacy policy](PRIVACY.md)
- [Architecture and safety model](docs/AV-Workstation-Toolkit-Architecture-and-Safety.md)
- [Endpoint-security behavior](docs/Endpoint-Security-Behavior.md)
- [Code signing policy](docs/Code-Signing-Policy.md) and [SignPath readiness](docs/SignPath-Readiness.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)
- Dated records: the [1.1.1 security audit](docs/records/AV-Workstation-Toolkit-Security-Audit.md),
  [1.1.1 packaging QA report](docs/records/AV-Workstation-Toolkit-QA-Report.md), and
  [Defender false-positive investigation](docs/records/Defender-False-Positive-Investigation.md)

**Catalogs**

- [Commercial AV catalog model](docs/Commercial-AV-Catalog.md)
- [External provider and credential guide](docs/External-Provider-Guide.md)
- [Manifest notes](manifests/README.md)
- Signed catalog updates: [managed apps](docs/Managed-Catalog-Updates.md) and [device reference](docs/Reference-Catalog-Updates.md)
- Catalog publishing: [managed apps](docs/Managed-Catalog-Publishing.md) and [device reference](docs/Reference-Catalog-Publishing.md)

**Building and contributing**

- [Contributing](CONTRIBUTING.md): build from source, tests, offline bundles, and the repository map
- [Packaging and release](docs/Packaging-and-Release.md)

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

Logs, exported plans, diagnostics, and snapshots stay on the workstation.
Packaged runs place them under `%LOCALAPPDATA%\AVWorkstationToolkit`; source
checkouts use repository-local folders that Git ignores. Treat this output as
sensitive operational evidence, store it only in an approved restricted
location, and never attach it unredacted to a public issue.

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
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which identifies runtime,
build, test, hosted-service, and system dependencies and what is actually
distributed. Commercial AV products in the catalog are metadata only and are
not redistributed project dependencies.

## Contributing and project status

AV Workstation Toolkit is in public beta. Bug reports and catalog corrections
are welcome as [GitHub issues](https://github.com/11anthonym/AV-Workstation-Toolkit/issues).
Review [CONTRIBUTING.md](CONTRIBUTING.md) for the Windows build/test commands,
dependency-lock rules, catalog boundaries, and security-sensitive review
expectations. Contributions are submitted under the project Apache-2.0 license
unless separately stated.
