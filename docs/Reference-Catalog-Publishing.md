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
  --public-base-uri https://catalog.example.org/avwt/
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

## Future static-feed handoff

A future separately administered static feed or GitHub Pages repository should receive the generated directory without rebuilding or resigning it. Publish the immutable revision directory first, verify its public hash, and update the two stable channel files last. That later automation must use owner-supplied repository credentials and approval controls outside the application repository; none are implemented here.

Publisher success means both the generated bundle and channel signature round-trip through the same runtime verifiers, catalog counts match, and the operational authority manifests remain byte-for-byte unchanged. Live updates additionally require the public origin, compiled public trust anchors and host allowlist, owner-controlled publishing workflow, and packaged-app verification described in [Reference-Catalog-Updates.md](Reference-Catalog-Updates.md).
