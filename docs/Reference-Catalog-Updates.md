# Signed reference-catalog updates

## Status

AV Workstation Toolkit now contains the compiled, non-executing update boundary for descriptive hardware identity and software-compatibility data. The packaged application continues to start immediately from its embedded manifests. At startup it may instead load a previously activated external catalog only after revalidating that revision's signature, hashes, schemas, counts, and cross-references.

The online production channel is intentionally **not configured yet**. This repository is private, and a packaged desktop application cannot safely read a private GitHub release without embedding or soliciting a repository credential. `Help > Catalog updates` therefore reports **Online channel not configured** until the external prerequisites below are supplied. Offline import is implemented but likewise fails closed until at least one production catalog public key is compiled into the signed application.

## Trust and authority boundary

Application releases remain the only way to change package definitions, WinGet IDs, vendor-delivery policy, credentials, worker policy, or executable code. A `.avwtcatalog` can contain only:

- `hardware-identities.json`
- `software-compatibility.json`
- `catalog-changes.json`
- `catalog-manifest.json`
- `catalog-manifest.sig`

The current repository already stores reference software Products, ReleaseFamilies, InstalledVersions, and DeviceSoftwareRelations in `software-compatibility.json`; the updater deliberately does not introduce a duplicate `reference-software.json` schema.

The strict parsers reject unknown properties, invalid types, duplicate JSON properties, dangling references, duplicate identities, unsupported schemas, and non-HTTPS evidence. The update data has no executable, installer, command, argument, provider, credential, package-authority, or worker-action field. DeviceSoftwareRelation remains descriptive and cannot create PackageDefinition authority.

## Cryptographic and transport contract

- Detached ECDSA P-256/SHA-256 signatures use fixed-width IEEE P1363 encoding.
- `SigningKeyId` must resolve to an exact public key compiled into the signed application. The private key is never present in this repository or the application.
- The manifest hashes exactly the three approved JSON payloads with SHA-256 and carries a monotonically increasing `Revision`, `PreviousRevision`, schema/compatibility epochs, UTC creation time, record counts, and `MinimumAppVersion`. Each bundle is a complete snapshot, so clients may skip publisher revisions; `PreviousRevision` remains signed publisher history rather than an intermediate-install requirement. Activation still requires a revision greater than every revision the client has accepted.
- ZIP input is limited to five exact direct-child entries, 4 MiB compressed, 2 MiB per entry, and 3 MiB total expanded content. Nested paths, extra files, duplicate names, empty content, invalid UTF-8, and reparse points are rejected.
- A configured online channel uses two exact source-controlled HTTPS URLs for metadata and its detached signature. Redirects, ambient credentials, arbitrary headers, caller-selected hosts, non-default ports, and unrestricted destinations are prohibited. Signed channel metadata names one `.avwtcatalog` URL on the compiled allowlist and its SHA-256. Responses and timeouts are bounded; expired or implausibly future metadata is rejected.
- The downloaded bundle is verified in memory. It is not activated until its channel identity, bundle signature, hashes, JSON, cross-references, compatibility version, and revision chain all pass.

## Storage and recovery

Activated data is stored under `%LOCALAPPDATA%\AVWorkstationToolkit\ReferenceCatalog`:

```text
ReferenceCatalog\
├── state.json
├── catalogs\<revision>\
├── staging\
└── quarantine\
```

When a valid previous signed revision exists, **Restore previous catalog** changes only the atomic active-state pointer. The rolled-back revision is suppressed so it is not immediately offered again, while any newer signed revision remains eligible. Online installation, offline import, and restore become effective for Device Lookup after the application restarts; startup revalidates the selected stored catalog before use.

Activation writes all approved files into a same-root unique staging directory, flushes them, revalidates the directory, moves the complete directory into `catalogs`, and then atomically replaces `state.json`. Existing revisions are never overwritten. Same/lower-revision activation is rejected; the only rollback is the explicit restore of a retained, revalidated previous revision. On startup, a damaged active revision is quarantined; the previous signed revision is attempted, then the embedded catalog is used. Malformed state also fails closed to the embedded catalog.

The update operation writes descriptive JSON and state only. It does not call WinGet, a worker, an installer, PowerShell, a shell, vendor delivery, Credential Manager, or firmware tooling.

## User flow

`Help > Catalog updates` displays current and available revisions and a signed change summary. `Check now` is asynchronous. `Update catalog` is enabled only after a channel bundle has been downloaded and fully verified. `Import signed catalog` accepts only `.avwtcatalog`. A completed activation takes effect after restart so all read-only lookup projections switch together; partial in-memory merges are not allowed.

Expected states are Current, Checking, Update available, Validating, Completed, Offline, Rejected, Application update required, and Online channel not configured. Offline or rejected updates leave the active catalog unchanged.

## External prerequisites

Before enabling production updates, the repository owner must provide all of the following:

1. A public, unauthenticated, stable HTTPS origin dedicated to reference-catalog metadata, signatures, and bundles. A private GitHub repository/release is not a suitable packaged-client feed.
2. The exact production `catalog-channel.json` and `catalog-channel.sig` URLs and the complete approved host set.
3. An ECDSA P-256 catalog-signing public key, its `SigningKeyId`, and preferably a second rollover public key. Only public keys are committed. Private keys must stay in an external protected signing service or CI secret boundary.
4. A signing/publishing approval policy, initial revision/version, expiration interval, and immutable retention policy for published bundles.
5. A source-controlled publishing job that runs existing manifest/schema/authority tests, generates `catalog-changes.json`, verifies `PreviousRevision`, signs metadata with the external private key, and publishes the exact tested bytes. It must not reuse the application signing key.

After those values are supplied, production composition can instantiate `ReferenceCatalogChannelClient` with the exact policy and trusted public keys. This is the only remaining repository integration step; no schema or UI redesign is required.

## Verification

Automated coverage exercises successful signed import, signed online check/download/activation, offline behavior, rollback, invalid signature, corrupt hash, unsafe/extra ZIP entries, oversized input, unknown execution-shaped JSON, unsupported application version, unapproved bundle origin, active-file tampering, malformed state, previous/embedded fallback, and the actual WPF menu/ViewModel command path.

Human packaged-app verification remains pending: open `Help > Catalog updates`, verify the disabled/unconfigured online state, keyboard access, resize/high-DPI layout, rejection of an unsigned test bundle, and—after real trust anchors are provided—a successful signed update followed by restart and exact-model lookup.
