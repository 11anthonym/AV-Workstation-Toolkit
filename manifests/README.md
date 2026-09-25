# Winget Manifests

`managed-applications.json` is the **canonical** managed-package definition: the strict schema-v1
artifact that the compiled application loads and embeds. Every managed exact-ID record is authored
here first. The supported PowerShell deployment and maintenance workflows load this JSON directly.

`winget-team-baseline.json` is the approved low-risk Standard profile for AV/IT workstations, kept
as an operator `winget import` deliverable. It is **not** embedded in the shipping runtime because no
compiled code parses it. Regenerate it after an approved Standard-profile catalog edit:

```powershell
.\scripts\Export-AVWorkstationToolkitBaselineManifest.ps1
```

`process-launch-policy.json` is a reviewed repository regression input for the child-process
contract. It is descriptive only, is read exclusively by QA, and is likewise not embedded.

The managed WinGet catalog is an explicit allowlist. Packages outside the reviewed catalog are not
eligible for managed deployment, and entries matching the configured forbidden-product policy are
rejected. Two distinct controls enforce this:

- **Allowlist.** Only records in this file receive managed WinGet execution authority. A package that
  is absent, or whose `Deployment` is not `Allowlisted`, can never be selected for install or update.
- **Forbidden-product policy.** `ForbiddenPattern` is a defense-in-depth denylist of specific named
  products. It is matched against each entry's name, ID, vendor, and note, and a match fails the
  whole catalog load rather than skipping the entry.

The forbidden policy names specific products — it is not an enforced category taxonomy. Keeping
endpoint-security, device-management, VPN, and corporate remote-support software out of this catalog
is a curation rule for reviewers; do not assume an unnamed product in one of those categories is
blocked by the pattern.

Separately, packages carrying a `Driver`, `Service`, or `Listener` risk class may appear in the
managed catalog but remain subject to their configured risk classification, deployment policy, and
maintenance holds, and to risk-sensitive pending-reboot enforcement at action time.

Do not run `winget import` as a discovery or dry-run command; import installs packages. AV Workstation Toolkit validates IDs and reports existing/missing packages before offering an install mode.

## External and commercial AV applications

Both JSON manifests use the backward-compatible schema 3 metadata model:

- `external-applications.json` contains 25 operational external records with reviewed detection, release, and delivery behavior;
- `commercial-av-catalog.json` contains 283 broad awareness records that describe commercial AV products and built-in Windows capabilities without approving an installer path.

The authoritative broad-awareness sources live in `catalog\vendors\*.json`. Run `build\Compile-CommercialCatalog.ps1` after a source edit; it validates and normalizes every vendor file and rewrites the tracked runtime artifact. Release builds use `-Check` and fail on drift. AV Workstation Toolkit embeds only `commercial-av-catalog.json` and never loads the loose vendor files at runtime.

Schema 3 validates vendor, application type, priority, role, deployment class, maintenance and version policy, lifecycle, licensing, download access and difficulty, distribution policy, workflow categories, installation forms, metadata verification/provenance, account/training/license requirements, driver/service/listener/firmware impact, platform, official HTTPS sources, and validation method. Missing uncertain facts normalize to explicit unknown values rather than guesses. Licensing, access, and redistribution policy remain separate dimensions.

Every external entry is forced to manual deployment and maintenance hold and is excluded from the WinGet action worker. Delivery modes are:

- `VendorPage`: official HTTPS handoff;
- `DirectDownload`: version/host/size-bounded download with Authenticode validation;
- `AuthenticatedSftp`: host-pinned, credentialed, allowlisted provider;
- `ParentProvider`: independently detectable child using an existing authenticated provider;
- `Bundled`: redistribution-approved, path-contained, hash-pinned payload;
- `InventoryOnly`: detection without acquisition or version-current claims; and
- `Awareness`: product knowledge and official-page handoff without a Windows deployment action.

Crestron MasterInstaller remains the sole authenticated SFTP provider. Its seven child applications inherit the fixed host, HTTPS feed, `/software` root, product scope, size bound, and publisher policy. A catalog edit cannot give a child an independent credential path.

Downloaded files are cached beneath the per-user data root, hash-recorded, revalidated before reuse, and shown in Explorer without execution. Q-SYS Designer LTS remains vendor-page-only because the [Q-SYS 9.13 EULA](https://help.qsys.com/q-sys_9.13/Content/Legal.htm) restricts external distribution.

Use `scripts\Add-AVWorkstationToolkitExternalPackage.ps1` only for software you are authorized to give to recipients. The authoring command requires schema 3 vendor and application-type metadata in addition to the redistribution assertion, hash, and signer review. Payload files live beneath the local `external-packages\packages` depot, which is ignored by Git. `Build-AVWorkstationToolkit.cmd -BuildOfflineBundle` validates every configured payload before producing an offline bundle ZIP.

See the [commercial catalog model](../docs/Commercial-AV-Catalog.md) for taxonomy and source rules and the [external provider guide](../docs/External-Provider-Guide.md) before changing live detection, credentials, signers, or provider allowlists.
