# Code signing policy

## Current state

AV Workstation Toolkit is preparing an application to SignPath Foundation.
The project has not been accepted, no SignPath integration is configured, and
current development/release-candidate artifacts are not SignPath Foundation
signed. No sponsorship, certificate issuance, or approval is implied.

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

## Planned hosted-signing model

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

1. Build and verify the unsigned `AVWorkstationToolkit.exe` from the reviewed
   commit and locked dependencies.
2. Submit that exact executable for signing and require manual approval.
3. Put the returned signed executable into the MSI and portable ZIP; do not
   rebuild the executable.
4. Build and verify the MSI around that signed executable.
5. Submit that exact MSI for signing and require manual approval.
6. Generate the final ZIP, SBOM, release manifest, and checksums from the
   returned signed artifacts.
7. Run signature-required package QA and publish those exact bytes without
   rebuilding or replacing them.

This preserves both the signed inner executable and signed outer MSI. A future
SignPath integration belongs after reproducible unsigned artifact verification,
with a second signing boundary after MSI construction. It must not use a
separate unreviewed source checkout or regenerate application binaries during
signing.

## Roles

Current repository access shows one maintainer:

- Author / committer: GitHub user `@11anthonym`
- Reviewer: GitHub user `@11anthonym` for externally contributed changes
- Signing approver: GitHub user `@11anthonym`

For self-authored changes, automated source/package QA and explicit signing
approval remain separate gates even though one maintainer currently performs
both responsibilities. Repository access changes require this section and the
SignPath configuration to be reviewed.

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
