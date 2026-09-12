# Signed reference-catalog updates

## Status

AV Workstation Toolkit now contains the compiled, non-executing update boundary for descriptive hardware identity and software-compatibility data. The packaged application continues to start immediately from its embedded manifests. At startup it may instead load a previously activated external catalog only after revalidating that revision's signature, hashes, schemas, counts, and cross-references.

The public production distribution origin is fixed at <https://11anthonym.github.io/AVWT-Catalog/>. Production composition contains the exact metadata URL, signature URL, and sole approved host, but `Help > Catalog updates` continues to report **Online channel not configured** until the owner supplies the real ECDSA P-256 public key. Offline import likewise fails closed until that public trust anchor is compiled into the signed application. No repository credential is used or accepted.

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

The public `11anthonym/AVWT-Catalog` repository and HTTPS-enforced GitHub Pages origin now exist. Before enabling production updates, the repository owner must still provide all of the following:

1. Generate the production catalog keypair outside every Git repository and provide only the public PEM for `SigningKeyId` `avwt-catalog-2026-a`; preferably plan a second rollover public key. The private key must stay in an external protected signing boundary.
2. Approve the initial revision/version, expiration interval, and immutable retention policy.
3. Establish the owner-controlled transfer/approval procedure that invokes the private-side publisher and copies its already-tested bytes into the public repository without rebuilding them. It must not reuse the application signing key.

The compiled URLs are:

- `https://11anthonym.github.io/AVWT-Catalog/stable/catalog-channel.json`
- `https://11anthonym.github.io/AVWT-Catalog/stable/catalog-channel.sig`
- immutable bundles beneath `https://11anthonym.github.io/AVWT-Catalog/catalogs/<revision>/`

The offline publisher and its immutable output contract are documented in [Reference-Catalog-Publishing.md](Reference-Catalog-Publishing.md). Production composition already owns the exact channel policy and will instantiate its client only after the owner-supplied public trust anchor is compiled. No schema or UI redesign is required.

## Verification

Automated coverage exercises publisher bootstrap and previous-catalog builds, deterministic unsigned payloads, change/risk analysis, runtime bundle/channel round trips, successful signed import, signed online check/download/activation, offline behavior, rollback, invalid signature, corrupt hash, unsafe/extra ZIP entries, oversized input, unknown execution-shaped JSON, unsupported application version, unapproved bundle origin, active-file tampering, malformed state, previous/embedded fallback, and the actual WPF menu/ViewModel command path.

Human packaged-app verification remains pending: open `Help > Catalog updates`, verify the disabled/unconfigured online state, keyboard access, resize/high-DPI layout, rejection of an unsigned test bundle, and—after real trust anchors are provided—a successful signed update followed by restart and exact-model lookup.
