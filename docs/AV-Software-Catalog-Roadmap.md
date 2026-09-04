# AV Software, Version & Device Compatibility Catalog Roadmap

**Status:** Phase 3A reference-vendor compatibility pilot complete (2026-09-04)
**Scope:** this document and the companion [decision register](AV-Software-Catalog-Decision-Register.md) are the source of truth for subsequent catalog-roadmap work. They do not authorize a download, installation, firmware update, or catalog-manifest change.

## Guardrails

The existing catalog separates product knowledge from deployment authority. That remains unchanged:

- A catalog or device relationship never grants managed WinGet authority.
- `External`, `Awareness`, vendor-download, and authenticated-SFTP records remain manual-only; a cached payload is not executable authority.
- Compatibility evidence is recorded only when supported by a vendor source or a documented physical-install observation. Manufacturer matching alone is never compatibility evidence.
- Later work must update this roadmap and decision register before changing `catalog/vendors/*.json`, `manifests/*.json`, or application code.

Phase 1 made no application-code or production-manifest changes. Phase 2 added the typed Domain model and strict standalone schema parser. Phase 3A composes a separate read-only production compatibility document for Q-SYS, Biamp, and Crestron; it does not merge that metadata into `PackageCatalog`, package planning, delivery authorization, selection policy, or worker authorization.

## Phase 1 reference batch — Q-SYS, Biamp, and Crestron

### Official-link review

| Vendor | Topic | Best verified source | Current-catalog assessment and later action |
|---|---|---|---|
| Q-SYS | Designer product and current software | [Q-SYS Designer](https://www.qsys.com/products-solutions/q-sys/software/q-sys-designer-software/) and [software/firmware resources](https://www.qsys.com/resources/software-and-firmware/) | The existing product and download pages are appropriate. Keep delivery as `VendorPage`; do not scrape or redistribute its installer. |
| Q-SYS | release notes and archived versions | [Q-SYS Designer archive](https://www.qsys.com/resources/software-and-firmware/q-sys-designer-software/archives/), [LTS archive](https://www.qsys.com/resources/software-and-firmware/q-sys-designer-software/archives/long-term-support-archive/), and [compatibility matrix](https://help.qsys.com/Content/Q-SYS_Compatibility/Q-SYS_Compatibility_Overview.htm) | The current LTS-only operational row is intentionally narrow but insufficient as a product-history view. Later data work should use these direct archive/compatibility sources rather than a generic product page for archive facts. |
| Q-SYS | current and historical release notes | [current release notes](https://help.qsys.com/Content/Q-Sys_Designer/QDS_Release_Notes.htm) and [archive-note guidance](https://support.qsys.com/faq-%7C-where-can-i-find-q-sys-designer-software-release-notes) | Add source references to release-family records, not one row per patch. |
| Biamp | Tesira and Canvas current download, release, and legacy links | [Tesira Software & Firmware](https://support.biamp.com/Tesira/Software-Firmware) and [Tesira release notes](https://support.biamp.com/Tesira/Miscellaneous/Tesira_release_notes) | Existing Tesira and Canvas links are direct and remain appropriate. The vendor page expressly routes older Tesira/Canvas software to release notes, so it should also be the archive source. |
| Biamp | Canvas/Tesira coupling | [Canvas getting started](https://support.biamp.com/Tesira/Control/Getting_Started_with_Canvas) | Existing same-major/minor coupling is supported for a Canvas control surface sent with a Tesira configuration. Later data must retain the evidence scope; it is not evidence of a universal side-by-side installation rule. |
| Biamp | Nexia legacy service | [Getting Started with Nexia](https://support.biamp.com/Audia-Nexia/Getting_Started_with_Nexia) and [Nexia firmware procedure](https://downloads.biamp.com/assets/docs/default-source/discontinued/biamp_upgrade_procedure_nexia_firmware.pdf?sfvrsn=61e8f289_6) | Nexia Software is a real Windows configuration/service dependency and is missing from the current catalog. No current public installer/archive index was verified; proposed record remains awareness/manual-only pending vendor-supported acquisition evidence. |
| Crestron | software/account landing page and MasterInstaller feed | [Crestron software](https://www.crestron.com/Software-Firmware/Software) and the existing curated `MasterInstallerSFTP.xml` parent feed | The generic software page is acceptable as an account-gated parent-provider landing page, but is indirect for child-product details. Preserve the current constrained parent provider; do not give child products independent credentials or transport. |
| Crestron | Toolbox | [Crestron Toolbox product page](https://www.crestron.com/Products/Catalog/Control-and-Management/Software/Programming-Commissioning/SW-TB) | Replace a generic child detail link with this product page only during a reviewed manifest update. |
| Crestron | SIMPL, databases, and legacy programming tools | [SIMPL release notes](https://www.crestron.com/release_notes/simpl_release_notes.html) and [SIMPL help](https://help.crestron.com/simpl/) | Existing generic child links are suboptimal for support and dependency context. The official release notes confirm SIMPL depends on the Crestron and Device Databases and identifies Toolbox/MasterInstaller. |
| Crestron | DM NVX commissioning | [DM NVX Tool](https://www.crestron.com/getmedia/09678b09-2665-445a-acfd-084bc638fe02/ss_SW-DMNVXTOOL) and [DM NVX discovery guidance](https://docs.crestron.com/en-us/8425/Content/Topics/Product-Manual/Appendix-Device-Discovery.htm) | Existing DM NVX Tool product link is direct. The tool is specifically a configuration/diagnostic/firmware tool for DM NVX encoders and decoders; Toolbox is also relevant for discovery. |

### Software-completeness findings

The following is a **research inventory**, not a request to add every item to a production manifest.

| Vendor/product | Existing catalog state | Field-service or programming purpose | Roadmap disposition |
|---|---|---|---|
| Q-SYS Designer | Operational LTS row only | design, configuration, commissioning, firmware pairing, diagnostics | Add one product with release-family evidence for Current, LTS, and Archived. Do not create patch rows. |
| Q-SYS UCI Viewer | awareness | Windows control-interface client | Retain as a separate product; relate to compatible Core/Designer family only where Q-SYS documentation names a version pairing. |
| Q-SYS MP Install | awareness | commissioning/firmware utility for MP systems | Retain as a device-scoped product; obtain model and version evidence before expanding relations. |
| Q-SYS Reflect | web-only awareness | monitoring/management service | Keep non-Windows/web-only; it is not a local-install candidate. |
| Biamp Tesira Software | operational | DSP design, configuration, commissioning, firmware workflow | Model as a product with release families and firmware compatibility evidence. |
| Biamp Canvas | operational | end-user control-surface authoring for Tesira | Retain separately from Tesira; model a documented same-version relationship, not an assumed shared installer. |
| **Biamp Nexia Software** | missing | legacy Nexia design, configuration, real-time control, and firmware service | Add a proposed legacy-service product only after download/version/detection evidence is reviewed. Do not label it current or distributable. |
| Biamp Discovery Tool (Devio SCX/TesiraFORTÉ X) | missing as a distinct record | discovery/commissioning for the vendor-named device scope | Candidate awareness/manual record; confirm installer identity and detection before implementation. |
| Biamp Vocia / Workplace / SageVue | already cataloged | paging, workplace deployment, monitoring | Keep in later Biamp review; not part of a Tesira/Nexia inference. |
| Crestron SIMPL Windows, Crestron Database, Device Database, Toolbox | operational children | programming, definitions, upload/diagnostics/firmware | Retain independent child identities under the existing parent provider. |
| Crestron DM NVX Tool | operational child | DM NVX configuration, diagnostics, firmware | Retain as device-family-scoped. |
| Crestron VT Pro-e, Smart Graphics, Construct, D3 Pro, Studio, SIMPL# dependencies | cataloged | UI authoring/development/legacy service | Preserve their existing lifecycle and manual-only status; relate only to evidence-supported control/panel families later. |

## Version and installation-family model

### Product, release family, and installed evidence

Later schema work should introduce these first-class concepts while preserving the existing catalog record as the deployment-policy authority:

| Concept | Stable identity | Key facts | Why it is separate |
|---|---|---|---|
| `Product` | vendor + normalized product ID | purpose, lifecycle, official source, deployment boundary | Represents one service/programming product without multiplying patch rows. |
| `ReleaseFamily` | product + channel/family ID | `Current`, `LTS`, `Archived`, or `Legacy`; major/minor range; archive source; project-pinning rule | Makes project compatibility and archive selection visible without equating a maintenance build with a different product. |
| `InstalledVersion` | installation evidence ID | exact raw display name/version, normalized version, source(s), install architecture/location if available, detection confidence | Captures multiple observed installs and preserves incomplete/unknown inventory rather than choosing one silently. |
| `DeviceSoftwareRelation` | device-family/model + product/family + purpose | applicability, constraints, evidence URL, confidence, lifecycle | Supports both device-to-software and software-to-device discovery without inferring from vendor. |

`InstalledVersion` is evidence, not a currentness decision. Inventory failure or an unrecognized display name remains incomplete/unavailable, never "not installed." `ReleaseFamily` may exist with no known current numeric version when a vendor portal, account, or physical installation is required.

### Q-SYS rules

- Q-SYS documents that the PC Designer and system hardware firmware must use the same release version for maintaining a design; its compatibility matrix also identifies minimum and discontinued hardware support. See [installation/update guidance](https://help.qsys.com/Content/Q-SYS_Designer/0015_Installing_Q-SYS.htm) and [compatibility overview](https://help.qsys.com/Content/Q-SYS_Compatibility/Q-SYS_Compatibility_Overview.htm).
- The roadmap therefore defines `QSYSDesigner.Current`, `QSYSDesigner.LTS`, and `QSYSDesigner.Archived` release families. A maintenance patch belongs in its family unless the vendor compatibility/upgrade/downgrade material makes it a distinct operational boundary.
- Q-SYS explicitly permits multiple Designer copies on one PC when an older design must be opened. Record all observed installs; do not collapse them to a single "latest" value. Exact display names, uninstall keys, install locations, and coexistence behavior still require physical-install characterization before making detection authoritative.
- Family selection is project and Core firmware compatibility work, not a generic automatic update recommendation. The existing `SameMajorMinor`/firmware-paired policy remains conservative until a later evidence review proves a more exact rule per family.

### Biamp Tesira, Canvas, and Nexia rules

- Biamp publishes Tesira Software and Biamp Canvas together and says the Canvas version used to send a control surface must match the Tesira Software version sending the configuration. This supports the current documented coupling, but does **not** establish that all Tesira/Canvas releases coexist side by side.
- Model Tesira Current and legacy/archived families only when release notes identify them. Model Canvas as a separate product with an evidence-scoped compatible-family relationship to Tesira. Do not assume its installer, registry identity, or coexistence behavior matches Tesira.
- Nexia is legacy service software. Treat its family, downloadable versions, registry evidence, coexistence behavior, and supported operating systems as unresolved until a physical install or authorized Biamp source is reviewed.

## Authoritative device-to-software mapping

One later `DeviceSoftwareRelation` dataset is the sole mapping authority. It supports reverse indexes:

```text
software -> applicable device families/models grouped by purpose
device/model -> applicable software grouped by purpose
```

Required fields: `DeviceFamilyId`, optional exact `ModelIds`, `ProductId`, optional `ReleaseFamilyId`, `Purpose`, `Applicability`, `EvidenceUrl`, `EvidenceKind`, `Confidence`, `Constraints`, and `Lifecycle`. Valid purposes are `Programming`, `Configuration`, `Commissioning`, `Discovery`, `Diagnostics`, `Firmware`, `Monitoring`, and `LegacyService`. `Confidence` must be `VendorDocumented`, `PhysicalInstallVerified`, or `Unresolved`; there is no manufacturer-inference value.

| Device family / representative model | Relevant product | Purpose | Evidence and confidence | Constraint |
|---|---|---|---|---|
| Crestron 4-Series — CP4N | SIMPL Windows; Crestron Database; Device Database | Programming | [CP4N release notes](https://www.crestron.com/release_notes/cp4n_2.8000.00017.01_release_notes.pdf) name the minimum tool/database versions. **VendorDocumented**. | Treat the cited release requirements as version-scoped; do not generalize to every CP4N firmware release without its notes. |
| Crestron 4-Series — CP4N | Crestron Toolbox | Diagnostics; firmware | The same CP4N release notes name Toolbox, and [CP4N resources](https://docs.crestron.com/en-us/9214/Content/Topics/Resources/Resources.htm) identify SIMPL and Toolbox as programming resources. **VendorDocumented**. | Account-gated acquisition remains through the constrained parent provider. |
| Crestron DM NVX encoders/decoders | DM NVX Tool | Configuration; commissioning; diagnostics; firmware | [DM NVX Tool](https://www.crestron.com/getmedia/09678b09-2665-445a-acfd-084bc638fe02/ss_SW-DMNVXTOOL) states its encoder/decoder scope and configuration/diagnostic/firmware functions. **VendorDocumented**. | Do not claim applicability to a model not within the vendor tool's supported scope. |
| Crestron DM NVX families | Crestron Toolbox | Discovery; diagnostics | [DM NVX discovery documentation](https://docs.crestron.com/en-us/8425/Content/Topics/Product-Manual/Appendix-Device-Discovery.htm) directs operators to Toolbox Device Discovery Tool. **VendorDocumented**. | Discovery relevance does not make Toolbox the sole configuration method. |
| Q-SYS Core families | Q-SYS Designer | Programming; configuration; commissioning; firmware | Q-SYS says Designer and system firmware must match for design maintenance and save/run operations. **VendorDocumented**. | Add exact model-to-release ranges from the compatibility matrix; no broad product-family shortcut. |
| Biamp Tesira families | Tesira Software | Programming; configuration; commissioning; firmware | Biamp's Tesira download/release material describes the software/firmware workflow. **VendorDocumented**. | Device-specific upgrade paths and intermediate versions must stay attached to vendor evidence. |
| Biamp Tesira families using Canvas | Biamp Canvas | Configuration | Biamp documents Canvas as creating control surfaces and requires matching the Tesira version that sends the configuration. **VendorDocumented**. | Not every Tesira deployment necessarily uses Canvas. |
| Biamp Nexia families | Nexia Software | LegacyService; configuration; diagnostics; firmware | Biamp says Nexia software is required to communicate with a Nexia device. **VendorDocumented**. | Exact model/version and installer/detection data remain unresolved. |

## UI and data implications

- **Find Apps:** search normalized product names, aliases, and device-family/model identifiers. A device result opens a grouped "Relevant software" view; it does not turn a device search into install selection.
- **Rows:** show one product row by default. Show release-family chips/sections and the complete installed-evidence list in Details. Do not create one DataGrid row for each patch.
- **Details:** present installed versions and evidence quality, release-family status (current/LTS/archive/legacy), vendor compatibility/source links, and applicable devices grouped by purpose. "Unknown" stays visible.
- **Filters:** add non-authorizing `Purpose`, `Device family`, `Lifecycle`, and `Release family` filters only after the mapping dataset is implemented.
- **Device results:** group software by purpose and state the evidence confidence. A relation with missing compatible version evidence should be shown as an unresolved/manual research cue, not an update action.

## Frozen manufacturer audit ledger

This is the finite Phase 4 completion ledger. It enumerates every manufacturer currently represented by the manifest inventory (116) exactly once. A later prompt processes **only unfinished batches**; it does not re-open a completed batch absent a documented conflict or vendor-source change. Batch 1 is the Phase 1 reference batch and is complete for research, not for manifest implementation.

| Batch | Status | Manufacturers |
|---|---|---|
| 1 — reference | **Phase 1 research complete** | Biamp; Crestron; Q-SYS |
| 2 | Unfinished | 7thSense; Adamson; AFMG; AJA Video Systems; Alcorn McBride; Allen & Heath; AMX; Analog Way; Angry IP Scanner Project; Ashly Audio; AtlasIED; Atlona |
| 3 | Unfinished | Audinate; Audio-Technica; AV Stumpfl; AVer; Avolites; Barco; Blackmagic Design; Bose Professional; BrightSign; Brompton Technology; BSS; Capture Visualisation |
| 4 | Unfinished | ChamSys; Christie; Cisco; Clear-Com; ClearOne; Colorlight; d&b audiotechnik; Datapath; Dataton; Dell / Waves; DELTACAST; Disguise |
| 5 | Unfinished | Epson; ETC; Extron; Figure 53; FileZilla Project; Flachmann und Heggelbacher; Green Hippo; Green-GO; HP Poly; Huddly; HW group; Intermodulation Analysis |
| 6 | Unfinished | Jabra; JBL Professional; Kramer; L-Acoustics; Lake; LEA Professional; Lectrosonics; LG; Lightware; Logitech; Luminex; MA Lighting |
| 7 | Unfinished | Magewell; Martin Audio; Matrox Video; Medialon; Mersive; Meyer Sound; Microsoft; Milan Manager; Multiple vendors; NagleCode; NDI; NETGEAR |
| 8 | Unfinished | NEXO; NovaStar; Nureva; OBS Project; Obsidian Control Systems; Open Sound Meter; Panasonic; Pingman Tools; Planar; Powersoft; Professional Wireless Systems; QLC+ Project |
| 9 | Unfinished | Rane Commercial; Rational Acoustics; RealTerm Project; Resolume; RF Explorer; Riedel Communications; Room EQ Wizard; Ross Video; RTS Intercoms; sACNView Project; Samsung; ScreenBeam |
| 10 | Unfinished | Sennheiser; Sharp NEC Display Solutions; Shure; Sony Professional; SoundBase; StudioCoast; Symetrix; TeraTerm Project; Unity Intercom; Uwe Sieber; Vaddio; Wisycom |
| 11 | Unfinished | WolfVision; Xilica; Yamaha Professional Audio; Yealink; ZeeVee |

Before closing a later batch, record its source links, missing field/service software, release-family behavior, device relations, confidence, and unresolved install/login evidence in the Decision Register. A batch is not complete merely because its current catalog row exists.

## Delivery phases

1. **Phase 1 — research and ledger:** complete for reference batch; no runtime or manifest change.
2. **Phase 2 — schema and fixtures:** **complete.** `SoftwareCompatibilityCatalog` and `CompatibilityCatalogParser` implement standalone schema version 1 under `src/AVWorkstationToolkit.Domain/Catalog`. The schema requires `Products`, `ReleaseFamilies`, `InstalledVersions`, and `DeviceSoftwareRelations` arrays, rejects unknown and duplicate JSON properties, and is not automatically loaded by the existing production catalog loader.
3. **Phase 3A — bounded reference-vendor pilot:** **complete.** `manifests/software-compatibility.json` contains only the accepted Q-SYS, Biamp, and Crestron evidence. The compiled composition loads it independently and exposes read-only Application queries; all external/manual authority boundaries remain unchanged.
4. **Phase 4 — catalog-wide completion:** process the frozen unfinished batches once each, recording evidence and unresolved facts. Do not re-audit Batch 1 unless a source change or identified conflict requires it.

### Phase 2 implemented schema contract

- `Product` is a descriptive identity with vendor, lifecycle, HTTPS official source, and aliases. Its identifier is deliberately separate from a package ID.
- `ReleaseFamily` belongs to one Product and has a unique `(Product, Kind, Branch)` identity. Version bounds are optional numeric evidence; a family is not a patch row.
- `InstalledVersion` records independent local evidence. `Observed` requires matching raw and normalized numeric versions plus a source. `Unknown`, `Incomplete`, and `Unavailable` cannot claim a version, so incomplete inventory cannot silently become not installed.
- `DeviceSoftwareRelation` is the only mapping record. Its semantic uniqueness key is `(DeviceFamilyId, ProductId, optional ReleaseFamilyId, Purpose)`; both reverse indexes are derived from this one collection. It carries exact models, device aliases, product/family scope, purpose, applicability, lifecycle, HTTPS evidence URI/kind, confidence, and constraints.
- Only `VendorDocumented`, `PhysicalInstallVerified`, and `Unresolved` relation confidence values are accepted. The schema has no inferred-by-manufacturer state.
- Compatibility documents contain no `Provider`, `Authority`, `Deployment`, executable, command, or delivery fields. They cannot modify `PackageDefinition`, managed WinGet eligibility, external/manual boundaries, or worker authorization.

### Phase 3A implemented production pilot

- `manifests/software-compatibility.json` is the schema-version-1 production source for the reference batch: 9 products, 7 release-family records, and 24 purpose-specific relations. It contains no local installed-version claims.
- Q-SYS Designer is one `QSYSDesigner` product with `Current`, `LTS`, and `Archived` families. Its Q-SYS Core relations retain project/Core-firmware matching constraints and do not turn a family or patch into an update recommendation.
- Biamp Tesira and Canvas remain distinct products. Their `Current` and `Archived` evidence branches carry no invented version bounds; the conditional Canvas relation records only the documented same-version configuration workflow. Nexia is a legacy product with manual informational relations and no package, acquisition, detection, or execution record.
- Crestron CP4N and DM NVX relations point to existing child product identities. The compatibility document does not reproduce or replace MasterInstaller provider metadata, so credential, transport, and acquisition authority remain exclusively in the existing package catalog.
- `RepositoryCompatibilityCatalogLoader` loads the fixed document from the application root with strict UTF-8, size, containment, and direct-file reparse checks. `CompiledAppComposition` exposes `CompatibilityCatalogQueryService` separately from `PackageCatalog`; the launcher embeds and requires the document as a runtime resource.
- Read-only queries provide product/alias search, release families, explicit installed-version evidence, device/model/alias search, software grouped by purpose, and applicable devices for a product. They contain no WPF or execution types.
- `UnresolvedInstalledVersionEvidenceProvider` returns one explicit `Unknown` record per product. Existing package inventory intentionally was not adapted because it collapses package state and does not establish authoritative multi-install/release-family identity for the pilot vendors.

## Phase 1 completion criteria

- [x] Q-SYS, Biamp, and Crestron were researched from authoritative vendor sources.
- [x] Nexia was explicitly investigated and classified as a missing legacy-service candidate with unresolved acquisition/detection facts.
- [x] A product/release-family/installed-evidence/device-relation design was recorded without changing production schema.
- [x] Representative device-to-software relationships were recorded with evidence and confidence.
- [x] Every current manufacturer was assigned exactly once to a frozen future audit batch.
- [x] Phase 1 changed no code or production manifest.
- [x] Phase 2 standalone version-1 Product/ReleaseFamily/InstalledVersion/DeviceSoftwareRelation model and strict parser were implemented without converting production records.
- [x] Phase 2 focused tests prove reverse indexes, aliases, identity/relationship validation, incomplete inventory semantics, authority isolation, and backward-compatible production manifest loading.
- [x] Phase 3A production reference data, compiled read-only composition, alias/device queries, explicit unknown installed evidence, and authority-isolation regressions are implemented.
