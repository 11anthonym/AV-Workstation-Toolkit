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
- inbox Windows PowerShell 5.1 with WPF
- a stable .NET 10 SDK accepted by `global.json`
- Git; the pinned WiX SDK restores through the normal locked build

Build all standard release candidates from the repository root:

```cmd
Build-AVWorkstationToolkit.cmd
```

Run source and package QA:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Run-Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Run-Tests.ps1 -CoreOnly
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-EndpointTrust.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-Package.ps1
```

Use the repository PSScriptAnalyzer settings and run `dotnet format
--verify-no-changes` for the launcher. The packaging guide contains the exact
commands and release gates.

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
