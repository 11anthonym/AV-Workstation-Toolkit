# Publishing signed reference catalogs

## Purpose and boundary

`AVWorkstationToolkit.CatalogPublisher` is the private-side, offline build tool for the public descriptive reference-catalog feed. It reads only the approved `hardware-identities.json` and `software-compatibility.json` manifests, validates their existing schema and cross-reference rules, derives counts and change metadata, and emits an immutable signed snapshot plus signed stable-channel metadata. It cannot upload files, modify another repository, create signing keys, or change application, package, vendor-delivery, credential, WinGet, or worker authority.

The catalog signing key is distinct from application code-signing credentials. Keep its ECDSA P-256 private key in an externally protected signing boundary. The publisher accepts the private key only through an absolute PEM file path outside the source repository; it rejects repository-resident, empty, oversized, and reparse-point key files. Never place a production private key in a checkout, build artifact, log, command output, or public feed.

## Build a feed

Choose a monotonically increasing positive revision, a three- or four-part numeric catalog version, a UTC creation timestamp, a stable signing-key ID, and the eventual public HTTPS feed root. The base URI must end in `/`; generated channel metadata points to the immutable `catalogs/<revision>/AVWT-Catalog-<version>.avwtcatalog` path, never a mutable source-control file.

```powershell
dotnet run --project .\tools\AVWorkstationToolkit.CatalogPublisher\AVWorkstationToolkit.CatalogPublisher.csproj -c Release -- `
  --version 2026.9.12.1 `
  --revision 1 `
  --minimum-app-version 1.1.1 `
  --created-utc 2026-09-12T12:00:00Z `
  --signing-key-id avwt-catalog-2026-a `
  --private-key C:\SecureExternalPath\avwt-catalog-private.pem `
  --public-base-uri https://11anthonym.github.io/AVWT-Catalog/
```

The default output is `artifacts/catalog-feed`, which must not already exist. Output is staged beside that directory and moved into place only after bundle and channel round-trip verification succeeds. Given identical catalog inputs and metadata, payload, change-summary, and manifest bytes are deterministic. ECDSA signing may produce a different valid signature on a later independent run, so the signed bundle and its channel hash become one immutable publication unit and must never be mixed across runs.

For a later snapshot, supply the prior signed bundle. `PreviousRevision` is derived from that verified bundle; bootstrap revision 1 needs no artificial revision-zero bundle. If the previous bundle used another still-trusted key, supply its public key separately:

```powershell
  --previous-catalog C:\PrivateBaseline\AVWT-Catalog-2026.9.12.1.avwtcatalog `
  --trusted-public-key avwt-catalog-2026-a=C:\PublicKeys\avwt-catalog-2026-a.pem
```

Additions are reported mechanically. Destructive removals or broadening an existing `DeviceSoftwareRelation` scope fail unless the operator reviews the report and repeats the command with `--acknowledge-risk`. Operational-authority-shaped fields always hard fail and cannot be acknowledged.

## Generated layout

```text
artifacts/catalog-feed/
├── stable/
│   ├── catalog-channel.json
│   └── catalog-channel.sig
└── catalogs/<revision>/
    ├── AVWT-Catalog-<version>.avwtcatalog
    └── catalog-changes.json
```

All four generated files are safe to publish publicly after approval. The bundle already contains its manifest, detached signature, descriptive manifests, and change summary. The private key, private-key backups, signing-service configuration, unpublished review material, and credentials must remain private.

## Public static-feed handoff

The public distribution-only repository is <https://github.com/11anthonym/AVWT-Catalog>; GitHub Pages serves it at <https://11anthonym.github.io/AVWT-Catalog/>. It receives the generated directory without rebuilding or resigning it. Publish the immutable revision directory first, verify its anonymous HTTPS URL and hash, and update the two stable channel files last. The public repository's validation workflow rejects changes to committed revision files and unexpected distribution content. Publisher credentials remain owner-controlled and outside the application.

## Production key handoff and first publication

The owner-supplied ECDSA P-256 public key is compiled into `ProductionReferenceCatalogTrustAnchors` under `avwt-catalog-2026-a`; its SHA-256 SPKI fingerprint is `D4A0306618232F9D2218CA4B6679E9FA0B00F417FEAB609F64222984574CB31C`. The corresponding private key remains owner-controlled at `$HOME\Documents\Keys\AVWT-Catalog\avwt-catalog-2026-a-private.pem` and must never be pasted, printed, uploaded, committed, or copied into a build or repository.

From the private AVWT checkout root, Anthony should generate Revision 1 with this command. It captures the actual operator run time in UTC as the publication timestamp; the signed pointer does not expire. The output directory must not already exist; the publisher deliberately refuses replacement.

```powershell
$repositoryRoot = (Get-Location).Path
$privateKeyPath = Join-Path $HOME 'Documents\Keys\AVWT-Catalog\avwt-catalog-2026-a-private.pem'
$createdUtc = [DateTimeOffset]::UtcNow

dotnet run --project .\tools\AVWorkstationToolkit.CatalogPublisher\AVWorkstationToolkit.CatalogPublisher.csproj -c Release -- `
  --repository-root $repositoryRoot `
  --output-root (Join-Path $repositoryRoot 'artifacts\catalog-feed') `
  --version 2026.9.13.1 `
  --revision 1 `
  --minimum-app-version 1.1.1 `
  --created-utc ($createdUtc.ToString('O')) `
  --signing-key-id avwt-catalog-2026-a `
  --private-key $privateKeyPath `
  --public-base-uri https://11anthonym.github.io/AVWT-Catalog/
```

This command is an owner action and must not be run by an agent or CI job. Review all generated files and the change analysis before copying them to the public feed. Publish `catalogs/1/` first, verify its anonymous immutable HTTPS URL and hash, then publish `stable/catalog-channel.json` and `stable/catalog-channel.sig` last.

Publisher success means both the generated bundle and channel signature round-trip through the same runtime verifiers, catalog counts match, and the operational authority manifests remain byte-for-byte unchanged. The production public trust anchor is configured; live updates still require owner approval and publication of the first signed snapshot plus packaged-app verification described in [Reference-Catalog-Updates.md](Reference-Catalog-Updates.md). The exact public origin and host allowlist are source-controlled.

The exact approved bundle published for a release can be reviewed into `catalog/reference/AVWT-Reference-Catalog.avwtcatalog` or supplied to `Build-Release.ps1 -BuildChannel Production -ReferenceCatalogBaselinePath <absolute-path>`. The build embeds those bytes as the offline baseline; it does not rebuild or resign the catalog. Production release construction fails if neither baseline source exists. The runtime verifies that embedded snapshot with the same compiled public trust anchors used for imports and channel downloads.
