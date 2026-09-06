# SignPath readiness

AV Workstation Toolkit has a repository-controlled GitHub Actions to SignPath
release path. It is **prepared but not operational** until the owner completes
the external configuration below. No SignPath account, acceptance, certificate,
signing request, or signed release is claimed.

## Project and current state

- Project: AV Workstation Toolkit
- Repository: <https://github.com/11anthonym/AV-Workstation-Toolkit>
- Repository state during this review: private GitHub repository with canonical
  `origin` and default branch `main`
- Current executable: `AVWorkstationToolkit.exe`
- Release EXE pattern: `AV-Workstation-Toolkit-<version>-win-x64.exe`
- MSI pattern: `AV-Workstation-Toolkit-<version>-x64.msi`
- ZIP pattern: `AV-Workstation-Toolkit-<version>-win-x64.zip`
- Build command: `Build-AVWorkstationToolkit.cmd`
- Current release artifacts: unsigned; no SignPath signing has occurred

The Apache-2.0 license is committed. The repository remains private and has no
public release. Paid or customer-certificate SignPath service can be configured
for a private repository. The SignPath Foundation open-source route is blocked
at present because the project is not publicly reviewable and has not already
been publicly released in the form to be signed. Foundation acceptance remains
discretionary and separate from technical integration readiness.

## Resulting signing architecture

`.github/workflows/release.yml` is the single tagged Production release path.
It runs only on GitHub-hosted Windows runners and uses immutable commit pins for
every third-party Action. The workflow starts with no permissions; its signing
job receives only `contents: read` and `actions: read`, while the isolated final
publication job alone receives `contents: write`. Checkout credentials are not
persisted.

The workflow accepts only a `vX.Y.Z` tag that exactly matches `VERSION` and the
current `origin/main` commit. The `signpath-production` GitHub environment is the
owner-controlled approval and secret boundary. The API token is never available
to pull-request or application code, and SignPath receives only an artifact
already stored by GitHub Actions.

The exact two-request chain is:

1. Build and test an unsigned candidate from the tagged checkout and locked
   dependencies.
2. Upload only `AVWorkstationToolkit.Worker.exe`; SignPath signs it using the
   reviewed worker artifact configuration.
3. Verify that signed worker, embed its exact bytes in the launcher, and build
   an unsigned MSI containing the launcher.
4. Upload only that MSI; SignPath deep-signs the nested
   `AVWorkstationToolkit.exe` and then the MSI envelope.
5. Verify the returned MSI and extract its exact signed launcher. Use those same
   launcher bytes for the direct EXE and portable ZIP. No executable is rebuilt
   after signing.
6. Finalize the SBOM, release manifest, and checksums from the returned signed
   bytes; run signature-required package QA and endpoint-trust checks.
7. Transfer exactly eight standard assets to a separate least-privilege
   publication job. Published release assets are immutable.

The worker, launcher, and MSI each carry Authenticode signatures. The ZIP itself
is not signed but contains the exact signed launcher. No other executable or DLL
is a separate shipping payload.

Source-controlled artifact configuration contracts live at:

- `.signpath/artifact-configurations/worker.xml`
- `.signpath/artifact-configurations/installer.xml`

Their SignPath-side configurations must match these files and use explicit,
versioned slugs. The workflow does not accept arbitrary input paths, executable
names, signing policies, or certificate identifiers from a tag author.

## GitHub settings the owner must configure

- Create environment `signpath-production` and require a deliberate reviewer;
  prevent administrators from bypassing it if the account plan supports that.
- Add environment secret `SIGNPATH_API_TOKEN` for a SignPath user limited to
  submitter authority for this project and signing policy.
- Add environment variables:
  `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`,
  `SIGNPATH_SIGNING_POLICY_SLUG`,
  `SIGNPATH_WORKER_ARTIFACT_CONFIGURATION_SLUG`,
  `SIGNPATH_INSTALLER_ARTIFACT_CONFIGURATION_SLUG`, and
  `SIGNPATH_EXPECTED_SIGNER_SUBJECT`.
- Keep default workflow token permissions read-only. The workflow grants write
  permission only to its final publication job.
- Install and authorize the official SignPath GitHub App for this repository so
  SignPath can verify source/build origin. For a private repository, retain the
  explicit `actions: read` and `contents: read` signing-job permissions.
- When account capability permits, protect `main` and release tags against force
  pushes/deletion and require `core-qa`, `package`, and the checked-in
  `.github/CODEOWNERS` review boundary before merging release-trust changes.

## SignPath settings the owner must configure

- Create or select the organization and enable MFA for every maintainer and
  signing approver.
- Add the predefined **GitHub.com** trusted build system to the organization and
  link it to the AV Workstation Toolkit project.
- Restrict origin to `11anthonym/AV-Workstation-Toolkit`, this release workflow,
  GitHub-hosted runners, tags matching `v*.*.*`, and the default branch policy
  supported by the selected SignPath edition.
- Create a release signing policy that requires manual approval and RFC3161
  timestamping. Limit the API-token principal to submission, not approval or
  project administration.
- Import `.signpath/artifact-configurations/worker.xml` as a versioned worker
  artifact configuration.
- Import `.signpath/artifact-configurations/installer.xml` as a versioned MSI
  artifact configuration. Confirm against an unsigned sample that the nested
  path is exactly `AVWorkstationToolkit.exe` and its metadata matches.
- Assign the approved Authenticode certificate to the policy and record its exact
  signer subject. Foundation, paid, and bring-your-own-certificate choices are
  external governance decisions; the repository does not invent one.

## Values to provide back to Codex

Provide the exact organization ID, project slug, release signing-policy slug,
worker artifact-configuration slug, installer artifact-configuration slug, and
certificate signer subject. Do not send the API token, certificate private key,
or any password to Codex; set secrets directly in the protected environment.

## Definition of DONE

Repository preparation is complete when source/config validation is green.
Operational SignPath integration is complete only after all external settings
exist, a deliberately approved test tag completes both signing requests, the
returned worker/launcher/MSI signatures and timestamps match the expected
signer, signature-required package QA passes, and the exact tested eight assets
are published. Until then, tagged Production releases fail closed.

The project will not fabricate users, stars, downloads, testimonials, or public
reputation to obtain Foundation acceptance.
