# SignPath Foundation readiness

This is an engineering readiness record for a future SignPath Foundation
application. AV Workstation Toolkit has not been accepted by SignPath
Foundation, has no SignPath project configuration, and must not display the
future attribution as current sponsorship.

## Project and artifacts

- Project: AV Workstation Toolkit
- Repository: <https://github.com/11anthonym/AV-Workstation-Toolkit>
- Repository state during this review: local only; no remote configured
- Current executable: `AVWorkstationToolkit.exe`
- Release EXE pattern: `AV-Workstation-Toolkit-<version>-win-x64.exe`
- MSI pattern: `AV-Workstation-Toolkit-<version>-x64.msi`
- ZIP pattern: `AV-Workstation-Toolkit-<version>-win-x64.zip`
- Build command: `Build-AVWorkstationToolkit.cmd`

No public release or repository-visibility change is part of this readiness
work.

## Reproducible build path

The hosted workflows are `.github/workflows/qa.yml` and
`.github/workflows/release.yml` on a Windows GitHub Actions runner. The release
build requires:

- Windows 10/11 or a compatible hosted Windows x64 runner;
- inbox Windows PowerShell 5.1 for the application and QA entry points;
- a stable .NET 10 SDK selected from the `10.0.100` baseline through the latest
  installed stable .NET 10 feature band; CI installs `10.0.x`;
- self-contained .NET runtime and apphost version 10.0.11;
- `WixToolset.Sdk` 6.0.2 for the x64 MSI;
- locked NuGet restore from
  `src/AVWorkstationToolkit.Launcher/packages.lock.json`;
- a fail-closed NuGet vulnerability audit;
- deterministic commercial-catalog compilation and source/artifact parity;
- immutable GitHub Actions commit pins.

`build/Build-Release.ps1` records the source commit, dirty-tree state, selected
SDK, target framework/runtime, architecture, build channel, dependency-audit
result, SBOM identity, artifact hashes, and explicit signing/timestamp state.
Production mode rejects a dirty tree and rejects missing or invalid signatures.
Development builds remain visibly unsigned.

The build emits:

- self-contained single-file EXE;
- MSI containing that exact EXE;
- portable ZIP containing that exact EXE;
- CycloneDX 1.6 SBOM with locked dependency versions, package hashes where
  available, scope/distribution metadata, and reviewed license expressions;
- versioned third-party notices;
- schema-versioned release manifest;
- SHA-256 checksum list covering the distributables, notices, SBOM, and release
  manifest.

## Current signing capability

The current build supports an externally supplied Authenticode certificate by
thumbprint from the Windows certificate store. The tagged workflow can import
a secret-backed PFX into an ephemeral CurrentUser store. It signs the EXE
before MSI construction, signs the MSI, uses SHA-256 plus a configurable HTTPS
RFC3161 timestamp service, validates the selected thumbprint and timestamp, and
runs package QA in signature-required mode. No key or password belongs in this
repository.

This capability is retained as a working release-security path. Current
artifacts are unsigned, and it is not a SignPath integration.

## Future SignPath insertion point

Future SignPath signing should operate on the exact artifacts produced and
verified by the hosted build, without rebuilding application binaries:

1. verified unsigned `AVWorkstationToolkit.exe` -> signing request -> manual
   approval -> returned signed EXE;
2. MSI/ZIP construction using that exact signed EXE;
3. verified unsigned MSI -> signing request -> manual approval -> returned
   signed MSI;
4. final provenance generation and signature-required package QA over the
   returned signed EXE/MSI and ZIP containing the signed EXE;
5. publication of those exact bytes.

The precise signing policy is in [Code-Signing-Policy.md](Code-Signing-Policy.md).

The following values are future configuration inputs and are intentionally not
present or guessed:

- SignPath organization ID;
- SignPath project ID;
- signing policy ID;
- artifact configuration ID;
- API token or service credential;
- certificate configuration;
- SignPath-specific workflow permissions and approval configuration.

Do not add placeholder workflow values that resemble a working integration.

## Open-source distribution boundary

The normal project release boundary is the AV Workstation Toolkit EXE, MSI,
portable ZIP, SBOM, third-party notices, release manifest, and checksums.
Commercial AV catalog entries are descriptive metadata, not redistributed
applications. Vendor caches, authenticated credentials, local logs, plans,
diagnostics, workstation snapshots, signing material, and the local external
installer depot are excluded.

Optional offline bundles are separate, controlled artifacts created only from
locally supplied, rights-approved, hash/publisher-validated payloads. They are
not produced or uploaded by the hosted public-release workflow and require an
independent redistribution review for every payload.

## Current publication gates

- **Project license selected.** AV Workstation Toolkit is licensed under
  [Apache-2.0](../LICENSE). This resolves the license-selection gate only; it
  does not make the repository public or complete any SignPath gate.
- The sanitized source tree has been exported into a fresh local Git repository
  with no inherited history, tags, or remote. The intended root commit remains
  staged for owner review and has not yet been created.
- The owner must approve the staged source tree and Git attribution before the
  fresh root commit and any private GitHub repository setup.
- Privacy, security, third-party notices, and code-signing policy review remain
  explicit owner checklist items.
- Interactive Windows visual review, signed-artifact verification, and an
  initial public release remain human release gates.

## Reputation

SignPath Foundation acceptance is discretionary. Executable applications may
require verifiable project reputation in addition to technical correctness.
This local repository has no public release under the current identity, so
reputation remains a non-code acceptance factor. The project will
not fabricate users, stars, downloads, testimonials, adopters, press, reviews,
or community size, and will not attempt to game an acceptance requirement.

## Human checklist

Leave owner-review, public-state, account-security, application, acceptance,
and signing items unchecked until they are independently verified.

- [x] OSI-approved open-source license selected
- [ ] Root LICENSE committed
- [ ] GitHub repository made public
- [ ] Public repository history reviewed by owner
- [ ] GitHub MFA enabled for every maintainer
- [ ] Initial public release published
- [ ] Public release exists in the exact form intended for signing
- [ ] Privacy policy reviewed
- [ ] Security policy reviewed
- [ ] Third-party notices reviewed
- [ ] Code signing policy reviewed
- [ ] SignPath account created
- [ ] SignPath MFA enabled
- [ ] SignPath Foundation application submitted
- [ ] Project accepted by SignPath Foundation
- [ ] SignPath project/configuration identifiers received
- [ ] GitHub build-to-SignPath integration configured
- [ ] First signing request manually approved
- [ ] Signed EXE independently verified
- [ ] Signed MSI independently verified
