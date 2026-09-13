# Signed reference-catalog updates

## Status

AV Workstation Toolkit contains a compiled, non-executing update boundary for descriptive hardware identity and software-compatibility data. Device Lookup is offline-first: startup selects and fully validates local data before any network work is scheduled. Installed catalogs do not expire. A channel check can make newer reference data available after restart, but it is never required for startup or continued use.

A production release must embed a complete signed `.avwtcatalog` at `reference-catalog/AVWT-Reference-Catalog.avwtcatalog`. It uses the same bundle verifier, signing-key allowlist, schema, revision, `CatalogVersion`, and `MinimumAppVersion` contract as imported/downloaded snapshots. The exact publicly distributable bundle may be reviewed into `catalog/reference/AVWT-Reference-Catalog.avwtcatalog`, or supplied explicitly with `-ReferenceCatalogBaselinePath`; `Build-Release.ps1 -BuildChannel Production` fails when neither is present. Development and CI builds may omit it and use the raw embedded manifests as an explicitly labelled **Development bootstrap** only; that path is not production-ready. No production private key is generated, stored, or consumed by the application build.

The public production distribution origin is fixed at <https://11anthonym.github.io/AVWT-Catalog/>. The owner-supplied ECDSA P-256 public trust anchor `avwt-catalog-2026-a` is compiled into production composition with the exact metadata URL, signature URL, and sole approved host. No caller, environment variable, or mutable configuration can add keys, URLs, or hosts. The production private key remains owner-controlled outside every repository and is never consumed by the application build. The online channel will remain empty until the first signed production revision is approved and published.

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

When a valid previous signed revision exists, **Restore previous catalog** changes only the atomic active-state pointer. The rolled-back revision is suppressed so it is not immediately selected or offered again, while a revision newer than the suppressed revision remains eligible. A prior local revision can be restored after a newer embedded baseline is introduced, and the signed embedded baseline can itself be the restore target. Online installation, offline import, and restore become effective for Device Lookup after the application restarts; startup revalidates the selected complete snapshot before use.

Activation writes all approved files into a same-root unique staging directory, flushes them, revalidates the directory, moves the complete directory into `catalogs`, and then atomically replaces `state.json`. Existing revisions are never overwritten. Same/lower-revision activation is rejected; the only rollback is explicit restore. A per-user named mutex serializes install, import, restore, state replacement, quarantine, and cleanup across AVWT processes; downloads and removable/network-share reads occur before that lock is acquired. A complete directory left by interruption before the state-pointer write is recovered by the next local scan. Orphan staging and state-temporary files are removed under the lock, corrupt data alone is quarantined, and quarantine is bounded to eight entries. Active, previous, suppressed, selected, all valid-but-incompatible, and the three newest other compatible local revisions are retained; only older verified compatible snapshots outside that safety set are obsolete. A newer catalog that is merely incompatible with the running older app is never deleted.

### Deterministic local selection

Startup performs no HTTP operation and never merges revisions:

1. Read the atomic per-user state. Quarantine malformed state, not signed catalogs.
2. Fully verify the embedded signed baseline when present.
3. Enumerate every retained positive revision and verify its signature, payload hashes, strict schemas, cross-references, counts, and app compatibility.
4. Quarantine only tampered, corrupt, or structurally invalid revisions. A fully valid catalog whose `MinimumAppVersion` is newer than the running application remains in place as **valid but incompatible**.
5. Exclude the explicitly suppressed rollback revision and older alternatives, except the explicitly selected active revision. Revisions newer than the suppressed revision remain eligible.
6. Choose the highest compatible eligible complete snapshot across embedded and retained catalogs. A newer embedded baseline therefore outranks obsolete cached data; a newer compatible retained catalog outranks the embedded baseline.
7. If no signed embedded baseline exists, development/bootstrap builds use their embedded raw manifests. A packaged signed baseline that exists but cannot be validated is a release error and never falls through to mutable raw data.

Revision state and retained directories remain under the user profile. Replacing, moving, or deleting the standalone EXE neither moves nor deletes that state. Running the EXE from another folder or USB on the same account sees the same compatible per-user catalog; running it on a clean offline profile uses its embedded signed baseline. Catalog updates require no elevation and never write beside the executable.

### Lifecycle decision matrix

| Scenario | Effective catalog | Retention / network behavior |
|---|---|---|
| First launch, clean profile, offline | Signed embedded baseline | No request is made; Device Lookup is immediately usable. |
| Compatible retained revision is newer | Highest compatible retained revision | Revalidated locally on every startup. |
| App upgrade embeds a newer baseline | Newer embedded revision | Older signed local data remains available for explicit restore. |
| Older app encounters newer valid local revision | Highest other compatible local/embedded revision | Newer incompatible revision stays intact and becomes eligible again after app upgrade. |
| Active revision is corrupt | Previous/other compatible signed revision, then embedded baseline | Corrupt revision is quarantined; valid incompatible data is not. |
| Online channel unavailable, invalid, expired, future-dated, or captive | Existing effective local catalog | Automatic failure is quiet/non-destructive; manual Check now reports failure. |
| Signed import from USB, disk, or accessible share | Current catalog until restart | Exact normal verification/rollback/authority rules apply; tamper leaves current catalog untouched. |
| Explicit restore | Valid prior local or embedded signed revision after restart | Rolled-back revision is suppressed; a genuinely newer revision remains offerable. |

The update operation writes descriptive JSON and state only. It does not call WinGet, a worker, an installer, PowerShell, a shell, vendor delivery, Credential Manager, or firmware tooling.

## User flow

`Help > Catalog updates` displays current and available revisions and a signed change summary. `Check now` is asynchronous. After the main window and local Device Lookup are available, a quiet background freshness check may run if no attempt has been recorded in approximately 24 hours. A failed attempt is recorded to prevent retry loops; an implausibly future timestamp suppresses automatic I/O rather than causing a storm. Automatic checking never activates or switches the running projection. `Update catalog` is enabled only after a channel bundle has been downloaded and fully verified. `Import signed catalog` accepts only `.avwtcatalog` from USB, local disk, or a normally accessible network share. A completed activation takes effect after restart so all read-only lookup projections switch together; partial in-memory merges are not allowed.

Expected states are Current, Checking, Update available, Validating, Completed, Offline, Rejected, Application update required, and Online channel not configured. Offline or rejected updates leave the active catalog unchanged.

## External prerequisites

The public `11anthonym/AVWT-Catalog` repository, HTTPS-enforced GitHub Pages origin, and production public trust anchor now exist. Before publishing the first production update, the repository owner must still complete the following:

1. Run the documented Revision 1 publisher command locally with the existing owner-controlled private key. The private key must stay in its external protected signing boundary and must not be copied into either repository.
2. Review and approve the generated initial revision, version, change summary, seven-day channel interval, and immutable retention.
3. Copy the publisher's already-tested immutable revision into the public repository, verify it anonymously over HTTPS, and update the two stable channel files last without rebuilding or resigning.

The compiled URLs are:

- `https://11anthonym.github.io/AVWT-Catalog/stable/catalog-channel.json`
- `https://11anthonym.github.io/AVWT-Catalog/stable/catalog-channel.sig`
- immutable bundles beneath `https://11anthonym.github.io/AVWT-Catalog/catalogs/<revision>/`

The offline publisher, exact first-publication command, and immutable output contract are documented in [Reference-Catalog-Publishing.md](Reference-Catalog-Publishing.md). Production composition now instantiates the fixed channel client with the owner-supplied public trust anchor. No schema or UI redesign is required.

## Verification

Automated coverage exercises publisher bootstrap and previous-catalog builds, deterministic unsigned payloads, change/risk analysis, runtime bundle/channel round trips, successful signed import, signed online check/download/activation, offline behavior, rollback, invalid signature, corrupt hash, unsafe/extra ZIP entries, oversized input, unknown execution-shaped JSON, unsupported application version, unapproved bundle origin, active-file tampering, malformed state, previous/embedded fallback, and the actual WPF menu/ViewModel command path.

Human packaged-app verification remains pending: open `Help > Catalog updates`, verify the disabled/unconfigured online state, keyboard access, resize/high-DPI layout, rejection of an unsigned test bundle, and—after real trust anchors are provided—a successful signed update followed by restart and exact-model lookup.
