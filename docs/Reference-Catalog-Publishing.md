# Publishing signed reference catalogs

## Purpose and boundary

`AVWorkstationToolkit.CatalogPublisher` is the private-side, offline build tool for the public descriptive reference-catalog feed. It reads only the approved `hardware-identities.json` and `software-compatibility.json` manifests, validates their existing schema and cross-reference rules, derives counts and change metadata, and emits an immutable signed snapshot plus signed stable-channel metadata. It cannot upload files, modify another repository, create signing keys, or change application, package, vendor-delivery, credential, WinGet, or worker authority.

The catalog signing key is distinct from application code-signing credentials. Keep its ECDSA P-256 private key in an externally protected signing boundary. The publisher accepts the private key only through an absolute PEM file path outside the source repository; it rejects repository-resident, empty, oversized, and reparse-point key files. Never place a production private key in a checkout, build artifact, log, command output, or public feed.

## Build a feed

Choose a monotonically increasing positive revision, a three- or four-part numeric catalog version, UTC creation/expiration timestamps no more than 31 days apart, a stable signing-key ID, and the eventual public HTTPS feed root. The base URI must end in `/`; generated channel metadata points to the immutable `catalogs/<revision>/AVWT-Catalog-<version>.avwtcatalog` path, never a mutable source-control file.

```powershell
dotnet run --project .\tools\AVWorkstationToolkit.CatalogPublisher\AVWorkstationToolkit.CatalogPublisher.csproj -c Release -- `
  --version 2026.9.12.1 `
  --revision 1 `
  --minimum-app-version 1.1.1 `
  --created-utc 2026-09-12T12:00:00Z `
  --expires-utc 2026-09-19T12:00:00Z `
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

## Production key handoff

The intended first production key ID is `avwt-catalog-2026-a`. Anthony must generate the keypair locally in an access-controlled directory outside every Git checkout. One concise OpenSSL 3 procedure is:

```powershell
$keyRoot = 'C:\SecureExternalPath\AVWT-Catalog'
New-Item -ItemType Directory -Path $keyRoot -ErrorAction Stop | Out-Null
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out (Join-Path $keyRoot 'avwt-catalog-2026-a-private.pem')
openssl pkey -in (Join-Path $keyRoot 'avwt-catalog-2026-a-private.pem') -pubout -out (Join-Path $keyRoot 'avwt-catalog-2026-a-public.pem')
openssl pkey -in (Join-Path $keyRoot 'avwt-catalog-2026-a-private.pem') -check -noout
```

Restrict and back up the private PEM using the owner's approved secret-storage process. Never paste, print, upload, or add it to either repository. Return only `avwt-catalog-2026-a-public.pem` to the private AVWT repository for review and compilation into `ProductionReferenceCatalogTrustAnchors`. No real Revision 1 may be signed or published before that public-key handoff is complete.

Publisher success means both the generated bundle and channel signature round-trip through the same runtime verifiers, catalog counts match, and the operational authority manifests remain byte-for-byte unchanged. Live updates additionally require the compiled production public trust anchor, owner approval of the first signed snapshot, and packaged-app verification described in [Reference-Catalog-Updates.md](Reference-Catalog-Updates.md). The exact public origin and host allowlist are already source-controlled.
