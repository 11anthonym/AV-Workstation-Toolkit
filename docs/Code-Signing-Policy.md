# Code signing policy

## Current state

AV Workstation Toolkit contains a fail-closed, repository-controlled GitHub
Actions to SignPath release path. External configuration is not complete, the
project has not been accepted by SignPath Foundation, and current artifacts are
unsigned. No sponsorship, certificate issuance, or approval is implied.

The repository contains no private signing key or certificate password. Its
existing build can use an externally supplied organizational Authenticode
certificate from the Windows certificate store, apply SHA-256 signatures with
an HTTPS RFC3161 timestamp, and verify the signer thumbprint, signature, and
timestamp before producing final provenance. Unsigned development builds
remain supported and are explicitly recorded as unsigned. Production mode and
signature-required QA fail closed rather than silently publishing unsigned
output.

Production code signing is a release-security control. Every production
signing request requires deliberate approval; a successful build, tag, or
automated test does not itself grant signing authority.

## Tagged-release boundary

The `.github/workflows/release.yml` path submits only GitHub-hosted workflow
artifacts through an immutable-pinned SignPath action. A version tag must match
`VERSION` and current `origin/main`. Protected-environment configuration,
manual approval, signer identity, valid RFC3161 timestamps, and
signature-required package QA are mandatory. Missing configuration fails closed.

Development and release-candidate builds may remain explicitly unsigned. Public
unsigned artifacts are published only under the
[unsigned publication procedure](Packaging-and-Release.md#unsigned-publication),
with release titles and notes that state they are unsigned, each with the
owner's explicit approval. The owner approved the betas `1.1.1-beta.1` on
2026-09-24 and `1.1.1-beta.2` on 2026-09-25, and the unsigned releases `1.1.2`,
without a beta label, on 2026-09-25 and `1.1.3` on 2026-09-28. A version
published unsigned is never reused for signed bytes, so the first signed release
will be later than 1.1.3.
Production mode must not be weakened or described as unsigned to create that
path.

## Hosted-signing model

The intended high-level chain is:

```text
source commit
  -> automated hosted build
  -> verified unsigned artifact
  -> SignPath signing request
  -> manual signing approval
  -> signed release artifact
```

AV Workstation Toolkit has a nested Windows distribution chain, so a future
integration must preserve the exact artifact boundary:

1. Build and submit the exact compiled worker as a GitHub workflow artifact.
2. Embed the returned signed worker in the launcher and build the MSI.
3. Submit that exact MSI; SignPath deep-signs its nested launcher and MSI shell.
4. Extract the signed launcher from the returned MSI for the direct EXE and ZIP.
5. Generate provenance, run signature-required QA, and publish exact tested bytes.

This preserves the signed worker, signed inner executable, and signed outer MSI.
The repository integration operates after reproducible unsigned verification
and must not use a
separate unreviewed source checkout or regenerate application binaries during
signing.

## What is signed

Only binaries built from this repository's source are signed, and only by the
tagged release workflow on GitHub-hosted runners:

- `AVWorkstationToolkit.exe`, the application, in the direct download, the
  portable ZIP, and the MSI;
- `AVWorkstationToolkit.Worker.exe`, the install worker embedded in the
  application;
- `AV-Workstation-Toolkit-<version>-x64.msi`, the installer.

Every signed file names its product **AV Workstation Toolkit** and carries the
release's version, and the signing configuration rejects a file whose product
name or version differs. The application's own libraries travel inside the
signed executable, which checks each against its embedded SHA-256 hash when it
extracts them. This project never signs anyone else's binaries: the .NET runtime
and third-party libraries the application carries keep their publishers'
signatures, if any, and the applications it installs or downloads are signed, or
not, by their own publishers.

## Roles

Current repository access shows one maintainer:

- Committers and reviewers: GitHub user [`@11anthonym`](https://github.com/11anthonym)
  (author and committer; reviewer for externally contributed changes)
- Approvers: GitHub user [`@11anthonym`](https://github.com/11anthonym), who
  approves each signing request

For self-authored changes, automated source/package QA and explicit signing
approval remain separate gates even though one maintainer currently performs
both responsibilities. Repository access changes require this section and the
SignPath configuration to be reviewed.

Everyone who can commit to this repository or holds a role in its SignPath
project must use multi-factor authentication for both GitHub and SignPath.

## Privacy

AV Workstation Toolkit has no telemetry, analytics, or crash reporting, and it
sends no inventory, logs, or credentials to this project. It does use the
network: at startup it checks installed applications through WinGet, reads the
official release pages and feeds of catalogued vendor applications, and checks
for a newer signed device catalog; everything else happens only when you ask
for it.
The [privacy policy](../PRIVACY.md) lists every network operation, what
triggers it, and what it sends.

## System changes and removal

The application installs or updates software only after it shows the plan and
you confirm the run, and it names every selected app that installs a driver,
service, or network listener before that run starts. It never uninstalls
software. To remove AV Workstation Toolkit itself, uninstall it from Windows
**Settings > Apps > Installed apps**, or delete the downloaded executable; see
[Uninstallation](../README.md#uninstallation).

## Key and workflow requirements

- Never commit a PFX/P12 file, private key, certificate password, API token, or
  signing-service credential.
- Use secret-backed provisioning or an approved certificate-store reference.
- Require SHA-256 file digests and a trusted RFC3161 timestamp.
- Verify the expected certificate identity after signing.
- Keep development/unsigned and production/signature-required channels
  unambiguous in the release manifest.
- Record signature and timestamp status for the EXE and MSI.
- Treat any future SignPath organization, project, policy, artifact
  configuration, and API identifiers as deployment configuration—not source
  defaults to invent or guess.

See [SignPath readiness](SignPath-Readiness.md), [packaging and release](Packaging-and-Release.md),
[privacy](../PRIVACY.md), and [security](../SECURITY.md).

## Effective only after SignPath Foundation acceptance

The following attribution is prepared for use only after acceptance and a
working, verified sponsored-signing integration. It is not current sponsorship
text:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation
