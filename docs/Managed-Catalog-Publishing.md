# Publishing signed managed application catalogs

## Publisher boundary

`AVWorkstationToolkit.CatalogPublisher managed` creates and verifies a local signed snapshot of the canonical
`manifests/managed-applications.json`. The publisher remains offline: it cannot upload, activate, or add a trust
anchor. Packaged application and worker runtimes can consume its output only when the signing public key was compiled
into their managed-catalog trust policy. No production managed-catalog key is currently configured.

The managed authority is separate from the descriptive Device Lookup authority:

- catalog identity: `avwt-managed`
- bundle extension: `.avwtmanaged`
- expected signing keys: supplied explicitly to the managed verifier; no reference key is accepted implicitly
- approved payload: exactly `managed-applications.json`
- forbidden-product policy: must exactly match the current application contract

The catalog cannot carry commands, scripts, executable paths, installer URLs, package sources, environment expansion,
or WinGet arguments. Unknown fields fail through the existing strict managed JSON parser. The existing `CatalogParser`
continues to validate exact package IDs, profiles, risks, deployment and maintenance policies, installer modes, and
duplicate IDs.

## Artifact contract

```text
managed-catalog-feed/
├── stable/
│   ├── managed-catalog-channel.json
│   └── managed-catalog-channel.sig
└── catalogs/<revision>/
    └── AVWT-Managed-Catalog-<version>.avwtmanaged
```

The ZIP-compatible bundle contains exactly:

```text
managed-applications.json
managed-catalog-manifest.json
managed-catalog-manifest.sig
```

The ECDSA P-256 manifest signature authorizes the manifest bytes. The manifest hashes the one approved payload and
records `CatalogId`, schema, version, monotonic revision history, UTC creation time, minimum application version,
signing-key ID, and validated package count. The separately signed channel identifies the immutable revision artifact
and hashes its exact bytes. Managed verification checks both signatures, the channel-to-bundle hash, the manifest-to-
payload hash, exact ZIP containment, application compatibility, strict managed JSON, and policy normalization.

Payload, manifest, ZIP entry ordering/timestamps, and all channel fields before the bundle hash are deterministic for
identical inputs. As with the existing reference publisher, platform ECDSA signatures are intentionally nonce-bearing;
two independent signing runs may produce different valid signatures and therefore different final bundle hashes. Each
signed bundle and channel pair is one indivisible immutable publication unit.

## Development-only invocation

Use an isolated temporary P-256 key outside the repository. Do not use the reference-catalog production key and do not
provision a production managed key during development.

```powershell
dotnet run --project .\tools\AVWorkstationToolkit.CatalogPublisher\AVWorkstationToolkit.CatalogPublisher.csproj -c Release -- managed `
  --version 2026.9.20.1 `
  --revision 1 `
  --minimum-app-version 1.1.1 `
  --created-utc 2026-09-20T12:00:00Z `
  --signing-key-id managed-development-only `
  --private-key C:\TemporaryIsolatedKeys\managed-development-only.pem `
  --public-base-uri https://catalog.example.test/avwt/managed/
```

The default output is `artifacts/managed-catalog-feed` and must not already exist. The operation is offline and cannot
upload or activate its output. Runtime update behavior, the independent worker boundary, and the manual operator
workflow are documented in [Signed managed application catalog updates](Managed-Catalog-Updates.md).

An owner-approved release phase must still provision a distinct production managed signing authority, review and
compile the public key, publish and verify the fixed production channel, and embed the first approved signed bundle as
the offline baseline.
