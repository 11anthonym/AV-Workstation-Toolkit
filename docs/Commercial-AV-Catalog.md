# Commercial AV Catalog Model

AV Workstation Toolkit separates software knowledge from deployment permission. A product can be useful to an AV engineer and still be unavailable, account-gated, licensed, server-only, embedded, legacy, or deliberately outside AV Workstation Toolkit's installer path.

The decision sequence is:

```text
Known to AV Workstation Toolkit
  -> relevant to role
  -> relevant to installed client equipment
  -> approved for deployment
  -> available to the current user
  -> installed
  -> version evaluated
  -> current, project-compatible, legacy, or unknown
```

No earlier state grants a later one. In particular, a catalog record is not an approval to download, redistribute, install, update, license, or use a product.

## Catalog layers

| Source | Purpose | Current records | Can enter the WinGet worker |
|---|---|---:|---|
| `scripts/AppProfiles.psd1` | Exact-ID, approved WinGet applications | 29 | Yes, subject to profile, risk, hold, reboot, and live-state checks |
| `manifests/external-applications.json` | Operational external detection and reviewed provider behavior | 25 | Never |
| `catalog/vendors/*.json` | Authoritative, reviewable per-manufacturer awareness sources | 281 | Never |
| `manifests/commercial-av-catalog.json` | Deterministically compiled and embedded commercial AV awareness artifact | 281 | Never |

The combined catalog has 335 unique records. Seven operational records are independently detectable Crestron child applications delivered through one shared secure parent provider.

`external-applications.json` remains the small operational boundary. The vendor sources can grow broadly without increasing the automatic execution surface; only the validated compiled artifact is embedded at runtime.

## Authoring and compilation

`catalog/vendors/*.json` is the single source of truth for broad awareness records. Each source file owns exactly one normalized vendor identity. The build-time compiler reads every file using strict UTF-8, validates source/file/vendor agreement, normalizes records through the same core catalog parser used by AV Workstation Toolkit, rejects duplicate IDs or vendors, and confirms that every result remains `External`, `ManualHold`, `Hold`, and non-managed.

```text
catalog/vendors/*.json
  -> build/Compile-CommercialCatalog.ps1
  -> schema and duplicate validation
  -> manifests/commercial-av-catalog.json
  -> embedded AV Workstation Toolkit runtime
```

Run `build/Compile-CommercialCatalog.ps1` after editing a vendor source. The normal release build runs it with `-Check` and fails if the tracked compiled artifact is stale. Runtime code never loads the loose source files, so there are not two competing authorities or dozens of mutable production inputs.

## Schema 3 metadata

Schema 3 adds a normalized `Metadata` object while retaining parsers for schemas 1 and 2. Schema 3 records require only `Vendor`, `ApplicationType`, and an HTTPS `OfficialProductUri`; omitted facts normalize to an explicit conservative or unknown value.

The model groups related facts rather than flattening every possibility into uncontrolled text:

| Group | Fields |
|---|---|
| Identity and role | `Vendor`, `ProductFamily`, `ApplicationType`, `Priority`, `Roles`, existing `Profile` |
| Deployment policy | `DeploymentClass`, `MaintenancePolicy`, existing provider and hold fields |
| Version policy | `KnownVersion`, `Release.Channel`, `VersionRule`, `VersionCoupling`, `CurrentOrLegacy`, registry detection patterns |
| Commercial terms | `LicensingModel`, `RequiresLicense`, `RequiresSubscription` |
| Access | `DownloadAccess`, `DownloadDifficulty`, `RequiresVendorAccount`, `RequiresDealerAccount`, `RequiresTraining` |
| Distribution and form | `DistributionPolicy`, `InstallationForms` |
| Workflow | `WorkflowCategories` |
| Workstation impact | `InstallsDriver`, `InstallsService`, `OpensListener`, `FirmwareUtility`, `Architecture`, `SupportedOS`, `SideBySideSupported` |
| Authority and validation | `OfficialProductUri`, `OfficialDownloadUri`, `ValidationMethod`, `Verification`, `Provenance`, `Notes` |

Licensing and access are intentionally independent arrays. `FREE` does not imply `PUBLIC-DL`; a free tool may require `ACCOUNT`, `DEALER`, or `TRAINING`, while paid software may expose a public installer.

Boolean requirement and impact fields may be `true`, `false`, or unknown. Unknown is not treated as false by deployment policy. `KnownVersion` is populated only for an operational record with a reviewed numeric baseline or parser; awareness records do not invent a version.

## Validated vocabulary

Priority:

- `P1`: high-value field or service workstation candidate
- `P2`: useful but role, vendor, or project specific
- `UTILITY`: general engineering or troubleshooting utility
- `DEV`: programming or development dependency

Lifecycle:

- `Current`
- `Legacy`
- `Transition`
- `CompatibilityUnverified`
- `Discontinued`
- `Unknown`

Distribution policy:

- `LinkOnly`, `VendorDownloadAllowed`, `Redistributable`, `PackageManagerOnly`
- `ManualInstall`, `ReviewBeforeBundling`, `Unknown`

Installation form:

- `Installed`, `Portable`, `MSI`, `EXE`, `ZIP`, `Store`, `WinGet`
- `VendorPortal`, `WindowsInbox`, `Web`, `Embedded`

Workflow categories:

- `NetworkCaptureTiming`, `DiscoveryReachability`, `ProtocolSocketTesting`
- `SerialConsole`, `RemoteFileTransfer`, `UsbConferencing`, `VideoEdidSignal`
- `AudioMeasurementAoIP`, `AVoIP`, `WindowsDiagnostics`, `FilesFirmwareComparison`
- `ControlApis`, `ManufacturerPack`, `LegacyService`

`Verification.VerifiedOn` uses `yyyy-MM-dd`. At less than 60 days it normalizes to `Current`; from 60 through 180 days it becomes `ReviewSoon`; after 180 days, or when no evidence date exists, it becomes `VerificationRequired`. `Quarantined` overrides age and requires a bounded reason. Review triggers can identify domain, publisher, discontinuation, download-strategy, or signature-policy changes that require human review.

`Provenance` can record an authoritative domain, expected publisher, signature-validation requirement, vendor hash availability, and bounded download strategy. Unknown publisher or hash facts stay unknown. An authoritative domain, when supplied, must contain every official source URI for that record.

Licensing:

- `FREE`, `FREEMIUM`, `PAID`, `LICENSE`, `SUBSCRIPTION`
- `HARDWARE-LICENSE`, `DEALER-LICENSE`, `UNKNOWN-COST`

Access:

- `PUBLIC-DL`, `PUBLIC-PAGE`, `EMAIL-FORM`, `ACCOUNT`, `REGISTERED`
- `DEALER`, `TRAINING`, `PORTAL`, `CONTACT`, `LICENSE-PORTAL`
- `LEGACY-ARCHIVE`, `NO-DL`, `UNKNOWN-ACCESS`

Download difficulty:

- `EASY`, `MODERATE`, `RESTRICTED`, `HARD`

Application types cover control, DSP, AVoIP, audio networking, RF, measurement, prediction, amplifier management, conferencing, cameras, displays, signage, dvLED, intercom, show control, broadcast video, lighting, field/network/serial/USB/EDID utilities, firmware, development, drivers, services, servers, web applications, embedded software, and legacy support.

The parser rejects unknown enum values, unsupported object keys, duplicate IDs, insecure official URLs, invalid provenance domains, future or malformed verification dates, reasonless quarantine, malformed or unbounded regular expressions, and invalid parent references.

## Provider and deployment classes

Operational delivery modes remain fail closed:

| Mode | Meaning |
|---|---|
| `VendorPage` | Open a reviewed official HTTPS page; no automatic installer execution |
| `DirectDownload` | Cache only a version-matched installer from allowlisted HTTPS hosts and validate its Authenticode publisher |
| `AuthenticatedSftp` | Use one curated feed, explicit host-key trust, current-user credentials, constrained paths, and signer checks |
| `ParentProvider` | Resolve an independently detectable child through an existing authenticated provider and its product allowlist |
| `Bundled` | Expose only a redistribution-approved, path-contained, hash-pinned payload |
| `InventoryOnly` | Detect state without claiming availability or offering acquisition |
| `Awareness` | Represent a product, service, server, embedded interface, or unknown Windows application without a deployment action |

Every external record is forced to `ManualHold` deployment and `Hold` maintenance by catalog validation. A direct download or cached file is a verified handoff, not permission to run it. AV Workstation Toolkit never launches an externally sourced third-party installer.

## Crestron parent provider

`Crestron.MasterInstaller` is the only authenticated SFTP provider. These child applications are detected and versioned independently:

- `Crestron.VTProE`
- `Crestron.SIMPLWindows`
- `Crestron.Database`
- `Crestron.DeviceDatabase`
- `Crestron.Toolbox`
- `Crestron.SmartGraphics`
- `Crestron.DMNVXTool`

Each child uses `Release.Mode = ParentCatalog`, `Delivery.Mode = ParentProvider`, a single numeric product ID, and `Metadata.ParentProviderId = Crestron.MasterInstaller`. During validation, the child inherits the parent's fixed host, port, HTTPS catalog URI, `/software` root, publisher policy, and maximum size. A child cannot define an independent credential or download mechanism.

The common provider still requires the complete seven-product allowlist, DTD-disabled XML parsing, version-bound paths, explicit SSH host trust, current-user Credential Manager storage after successful authentication, and Authenticode verification before cache finalization.

## Querying the catalog

`Find-AVWorkstationToolkitCatalog` accepts a catalog or loads the embedded combined catalog. Filters compose, so the result is the intersection of selected criteria.

```powershell
$catalog = @(Get-AVWorkstationToolkitCatalog)

# Free products with a direct public download and no account/training gate.
Find-AVWorkstationToolkitCatalog -Catalog $catalog -Free -PublicWithoutAccount

# Dealer-gated Crestron products relevant to control programmers.
Find-AVWorkstationToolkitCatalog -Catalog $catalog `
  -Vendor Crestron `
  -Role ControlProgramming `
  -RequiresDealerAccount

# Current DSP applications that install services.
Find-AVWorkstationToolkitCatalog -Catalog $catalog `
  -ApplicationType DSPAudio `
  -CurrentOrLegacy Current `
  -InstallsService

# Legacy support tools with no current download source.
Find-AVWorkstationToolkitCatalog -Catalog $catalog `
  -CurrentOrLegacy Legacy `
  -SourceUnavailable

# Reviewed packet/timing tools represented as portable or in-box capabilities.
Find-AVWorkstationToolkitCatalog -Catalog $catalog `
  -WorkflowCategory NetworkCaptureTiming `
  -MetadataVerificationState Current
```

For installed-only queries, pass the package array from a plan because installation is live state rather than static catalog metadata:

```powershell
$plan = Get-AVWorkstationToolkitPlan
Find-AVWorkstationToolkitCatalog -Catalog $plan.Packages -InstalledOnly -SourceUnavailable
```

The desktop app composes text, profile, catalog-policy, manufacturer, and discipline filters. Manufacturers are derived from normalized catalog `Vendor` metadata and retained across refresh when still present. Discipline presets use `ApplicationType` only: DSP/audio, audio networking, AVoIP, RF, conferencing/PTZ, displays/signage, dvLED, control, broadcast/video, media/show control, lighting, intercom, field/network utilities, measurement/analysis, firmware/commissioning, and development. The read-only selected-item detail view surfaces catalog, installed-state, access, compatibility, impact, and official-source fields without adding an execution path.

Roles remain normalized, independently composable metadata. A query can intersect a role such as `FieldService` with any set of manufacturer identities without encoding vendors as roles or disciplines. This is the current domain seam for future role profiles and Field Kits; it does not yet calculate or approve a site-preparation bundle.

`MaintenancePolicy`, `VersionRule`, `VersionCoupling`, `CurrentOrLegacy`, and `SideBySideSupported` remain separate compatibility dimensions. They can later express project- or firmware-specific toolchain requirements, but AV Workstation Toolkit does not create compatibility rules without authoritative evidence.

Workflow metadata is additive and independently filterable; it does not replace manufacturer or discipline identity. This supports future workflow and manufacturer overlays without turning categories into profiles or action permission. The prioritized unresolved research is maintained in [Workstation-Research-Backlog.md](Workstation-Research-Backlog.md).

## Source policy

Each schema 3 record carries an official HTTPS product page. Official vendor documentation is authoritative for product name, supported platform, lifecycle, licensing, access restrictions, replacement status, current version, and system impact.

Community discussions may identify products that working integrators use, but they do not override vendor documentation and are not used to construct download URLs. Mirror, warez, aggregator, and guessed asset URLs are prohibited. If a fact cannot be verified, the record uses `UNKNOWN-COST`, `UNKNOWN-ACCESS`, `Unknown`, or an unknown Boolean instead of a guess.

Representative official research surfaces include [Q-SYS software and firmware](https://www.qsys.com/products-solutions/software-and-firmware/), [Crestron software](https://www.crestron.com/Software-Firmware/Software), [Extron software](https://www.extron.com/technology/software), [Biamp support](https://support.biamp.com/), [Audinate software downloads](https://www.getdante.com/resources/software-downloads/), [Shure software](https://www.shure.com/en-US/support/downloads/software-firmware), [Sennheiser software](https://www.sennheiser.com/en-us/support/software), [Atlona Velocity](https://ts.atlona.com/velocity-av-control-systems/), [Kramer software](https://www1.kramerav.com/product/k-config), [Planar WallDirector OS](https://www.planar.com/products/planar-walldirector/planar-walldirector-os/), [Ross DashBoard](https://www.rossvideo.com/products/automation-and-control/dashboard/), [LEA SharkWare](https://leaprofessional.com/products/sharkware/page/2/), [BrightSign documentation](https://docs.brightsign.biz/), [ETC software](https://www.etcconnect.com/Software/), [NDI Tools](https://ndi.video/tools/), and [Open Sound Meter](https://opensoundmeter.com/). The per-record `OfficialProductUri` remains the source pointer for the specific product.

## Change control

When adding or changing a record:

1. Decide whether it belongs in the managed WinGet allowlist, operational external manifest, or awareness manifest.
2. Verify the exact product identity and facts against official vendor sources.
3. Record unknowns explicitly; do not infer access or licensing from price alone.
4. Classify drivers, services, listeners, firmware behavior, server/web/embedded status, and account or dealer gates.
5. Add registry detection only when the installed display name and version behavior have representative evidence.
6. Add a live version parser only when an official credential-free page has a bounded, stable pattern. Otherwise use inventory or awareness state.
7. Reuse an existing parent provider when products share a secure feed. Do not duplicate credentials or trust policy.
8. Never add a direct or bundled installer without source, redistribution, hash, size, and signer controls.
9. Update the decision register for a deployment-policy decision; broad awareness records do not each need a deployment approval row.
10. Run the full source QA, package build, and package QA before release.

The catalog's breadth is not an installation target. Role, client equipment, project version, organizational approval, user entitlement, and an explicit operator action remain required.
