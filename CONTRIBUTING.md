# Contributing

AV Workstation Toolkit is a public open-source project in beta.
Contributions to AV Workstation Toolkit are submitted under the project's
[Apache-2.0 license](LICENSE) unless separately stated.

Bug reports and catalog corrections are welcome as
[GitHub issues](https://github.com/11anthonym/AV-Workstation-Toolkit/issues).
Pull requests from outside contributors are not being merged yet: `main` will
require pull requests and the `core-qa` and `package` checks before outside
contributions are accepted. Report vulnerabilities as described in
[SECURITY.md](SECURITY.md), not in a public issue.

## Development environment

- Windows 10 or Windows 11 x64
- standard-user PowerShell execution; do not run repository scripts elevated
- inbox Windows PowerShell 5.1
- a stable .NET 10 SDK accepted by `global.json`
- Git; the pinned WiX SDK restores through the normal locked build

## Build from source

Build every release artifact with one command from the repository root:

```cmd
Build-AVWorkstationToolkit.cmd
```

The build runs `tests\Run-Tests.ps1` and `tests\Test-CompiledRuntime.ps1` first
unless you pass `-SkipTests`. The standalone result is written to
`artifacts\release\1.1.2\AV-Workstation-Toolkit-1.1.2-win-x64.exe`, next to the
MSI and ZIP; `artifacts\` is ignored by Git. Double-click
`Launch-AVWorkstationToolkit.cmd` to run the built executable. Before the first
build, it starts the compiled App project directly.

## Tests

Run the non-installing source QA after any code or catalog edit. The full suite
needs an interactive standard-user desktop; GitHub Actions runs the
host-independent `-CoreOnly` subset, endpoint-trust QA, and targeted
PSScriptAnalyzer on every push to `main` and every pull request.

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Run-Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Run-Tests.ps1 -CoreOnly
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-EndpointTrust.ps1
```

Build the compiled runtime with locked restore, run the deterministic MSTest
suite, and exercise the worker, action-flow, WPF, and live-rehearsal boundaries:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-CompiledRuntime.ps1
```

After a full build, verify the three distributables:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-Package.ps1
```

An optional non-mutating host check reports which read-only providers were
exercised and which were unavailable; workstation package contents are never
treated as golden test data:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-CompiledReadOnlyIntegration.ps1 -NoBuild
```

Use the repository PSScriptAnalyzer settings and run `dotnet format
--verify-no-changes` for the launcher. [tests/README.md](tests/README.md)
describes each suite, and the [packaging guide](docs/Packaging-and-Release.md)
contains the exact release gates.

## Offline bundles for authorized redistribution

For software your organization is authorized to redistribute, add a hash-pinned
local payload and build an offline delivery ZIP:

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

The authoring command copies the payload into the git-ignored local depot and
embeds its SHA-256 hash and signer policy in the catalog. The offline ZIP
contains `AVWorkstationToolkit.exe` plus the verified package. AV Workstation
Toolkit shows the file in Explorer for deliberate user handoff; it does not
silently execute third-party installers.

## Repository map

| Path | Purpose |
|---|---|
| `src/AVWorkstationToolkit.Launcher/` | Self-contained bootstrap and embedded-runtime integrity; built by the release build rather than the solution |
| `src/AVWorkstationToolkit.App/` | Compiled WPF composition and presentation |
| `src/AVWorkstationToolkit.Worker/` | Independently validating action worker |
| `src/AVWorkstationToolkit.Application/` | Use cases and infrastructure abstractions |
| `src/AVWorkstationToolkit.Domain/` | Deterministic catalog, version, planning, policy, risk, and result logic |
| `src/AVWorkstationToolkit.Infrastructure.Windows/` | Windows inventory, process, file, vendor, credential, and trust adapters |
| `AVWorkstationToolkit.slnx` | Locked, warning-clean .NET 10 solution |
| `tests/AVWorkstationToolkit.Tests/` | Deterministic MSTest suite; new behavior is tested here by default |
| `tests/AVWorkstationToolkit.IntegrationTests/` | Worker-process, action-flow, provider, and live-rehearsal boundary harnesses |
| `tests/AVWorkstationToolkit.Worker.DevHost/`, `tests/AVWorkstationToolkit.Development/` | Non-shipping worker host and launch support for real-process tests |
| `tests/*.ps1`, `tests/fixtures/` | Source, endpoint-trust, compiled-runtime, and package QA scripts and their deterministic inputs |
| `tools/AVWorkstationToolkit.CatalogPublisher/` | Offline owner tool that builds and signs managed and device-reference catalog releases |
| `installer/` | Pinned WiX x64 MSI project |
| `build/` | Reproducible staging, signing, packaging, SBOM, and checksum workflow |
| `Build-AVWorkstationToolkit.cmd` | One-command release build from a fresh clone |
| `Launch-AVWorkstationToolkit.cmd` | Source-checkout launcher |
| `.github/` | QA and tag-validated release workflows, code owners, and issue and pull request templates |
| `.signpath/` | SignPath artifact configurations for the prepared signing workflow |
| `manifests/managed-applications.json` | Canonical approved exact-ID WinGet catalog |
| `manifests/external-applications.json` | Operational external detection, version, and provider policy |
| `manifests/commercial-av-catalog.json` | Compiled, embedded, non-deployable commercial AV awareness metadata |
| `manifests/hardware-identities.json`, `manifests/software-compatibility.json` | Device Lookup source data published through the signed reference catalog |
| `manifests/process-launch-policy.json`, `manifests/winget-team-baseline.json` | Process-launch QA contract and the team `winget import` deliverable; neither is embedded |
| `catalog/vendors/` | Per-manufacturer awareness sources compiled into `commercial-av-catalog.json`; never loaded at runtime |
| `catalog/managed/`, `catalog/reference/` | Embedded signed managed-catalog and device-reference baselines |
| `scripts/` | Allowlisted command-line deployment and maintenance workflow with its PowerShell action worker, read-only snapshot and readiness tools, catalog authoring, and their shared module; not packaged |
| `assets/branding/` | Application artwork |
| `docs/` | Operator, architecture, security, catalog, and release documentation; `docs/records/` holds dated audit and QA records |
| `external-packages/` | Local third-party payload depot; always ignored by Git |
| `logs/`, `reports/`, `snapshots/` | Source-checkout evidence; ignored by Git |

## Change expectations

- Preserve standard-user operation, exact-ID WinGet authority, external/manual
  boundaries, live revalidation, one-package-at-a-time execution, credential
  isolation, host-key validation, and package integrity checks.
- Do not add uninstall, arbitrary command execution, security-control bypasses,
  generic download-and-execute behavior, telemetry, or analytics.
- New managed software entries must follow the existing catalog/policy model.
  Catalog awareness alone must never create install or update permission.
- Do not commit proprietary vendor installers, customer data, workstation
  snapshots, diagnostic exports, credentials, tokens, private keys, signing
  material, or private infrastructure details.
- Do not add a vendor payload to an offline bundle without documented
  redistribution authority and the existing hash/publisher controls.
- Keep NuGet dependencies locked. If an intentional dependency change makes
  the lock stale, force-evaluate restore separately, review
  `packages.lock.json`, run the vulnerability audit, and commit the reviewed
  lock change. Release builds must not rewrite it.
- Treat process creation, network-provider, request-schema, MSI identity,
  signing, SBOM, checksum, release-manifest, and workflow changes as
  security-sensitive. Keep them small and include focused regression tests.
- Never change the MSI UpgradeCode
  `{7A3A4978-78F0-5824-B93F-A2C741BF853E}` without a separately reviewed
  installer migration design.

Before proposing a change, review [SECURITY.md](SECURITY.md),
[PRIVACY.md](PRIVACY.md), and the
[architecture and safety model](docs/AV-Workstation-Toolkit-Architecture-and-Safety.md).
Release/signing changes require deliberate maintainer review and complete
source/package provenance validation.
