# Signed managed application catalog updates

AV Workstation Toolkit separates descriptive Device Lookup updates from the catalog that authorizes managed WinGet actions. A descriptive `.avwtcatalog` signature never authorizes software execution. Managed authority comes only from a verified `avwt-managed` `.avwtmanaged` bundle signed by a separately provisioned managed-catalog key.

## Runtime selection

A packaged application contains `managed-catalog/AVWT-Managed-Catalog.avwtmanaged` as its trusted offline baseline. On every application or worker start, `ManagedCatalogStore` independently verifies that baseline and the one retained download beneath `%LOCALAPPDATA%\AVWorkstationToolkit\ManagedCatalog\catalogs`. It selects the highest compatible verified revision. Invalid retained data is deleted and the signed baseline remains usable offline. A retained revision that another process holds open, or that requires a newer release sharing the same data root, is skipped for that start but kept. A missing or invalid packaged baseline fails production startup.

The store keeps no revision database or history. Staging is bounded, activation has one commit point (`Directory.Move`), and only one downloaded revision is retained. The manual update workflow checks the fixed compiled HTTPS origin, verifies the signed channel, verifies the bundle hash and managed signature, revalidates the strict catalog payload, stages the exact bundle bytes, verifies the staged file, and atomically activates it. The running process continues to use its original revision until restart.

## Worker authority and revision consistency

The UI never sends package policy, installer arguments, commands, URLs, or paths. Action-request schema 2 contains exact package IDs, the reviewed action flags, and `ManagedCatalogRevision`. The compiled worker independently loads and verifies the active managed catalog, rebuilds a fresh plan, and rejects the request unless its verified revision exactly matches the application revision. It then derives eligibility, risk, installer mode, deployment/maintenance policy, and fixed WinGet arguments from that worker-owned plan.

Catalog payloads retain the strict `managed-applications.json` schema. Unknown fields and invalid policy tokens are rejected. Remote catalogs cannot add commands, scripts, sources, installer URLs, executable paths, environment expansion, or WinGet arguments. `ForbiddenPattern` must exactly match the compiled reviewed contract, so a signed update cannot weaken that security policy.

## Operator workflow

Use **Help > Managed app catalog updates...**. The window reports the effective revision, source, update availability, verification result, and restart requirement.

1. Select **Check now**.
2. If a verified revision is available, select **Download update**.
3. Restart AV Workstation Toolkit.
4. Confirm the effective revision and newly approved rows.

An offline, rejected, tampered, incompatible, or same/older revision leaves the current verified catalog unchanged.

## Authoring and PowerShell boundary

`manifests/managed-applications.json` remains the single authoring source. The catalog publisher packages and signs those exact validated bytes. Source-checkout PowerShell deployment and maintenance workflows also load that JSON and their action worker reloads it independently. They identify this source authority as revision 0. They intentionally do not consume the installed signed feed or implement ECDSA verification in PowerShell.

## Production release gate

No production managed key is provisioned in this development phase. `ProductionManagedCatalogConfiguration` therefore fails closed. Before a production application can ship, the owner must:

1. provision a distinct ECDSA P-256 managed-catalog signing key under the approved signing policy;
2. add only its public key and approved key ID to the compiled managed trust anchors;
3. publish and independently verify revision 1 with the existing publisher;
4. supply that verified revision as `ManagedCatalogBaselinePath` to the release build;
5. verify the fixed public managed channel and immutable bundle;
6. perform normal application/worker/MSI signing and release validation.

Development private keys and artifacts must remain isolated and must never become production trust.
