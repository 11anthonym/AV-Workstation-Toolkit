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
unsigned artifacts are published only as clearly labeled betas under the
[beta publication procedure](Packaging-and-Release.md#beta-publication), each
with the owner's explicit approval; the owner approved `1.1.1-beta.1` on
2026-09-24 and `1.1.1-beta.2` on 2026-09-25. Production mode must not be weakened or described as unsigned to
create that path.

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
