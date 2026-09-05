# AV Software, Version & Device Compatibility Catalog Roadmap

**Status:** Phase 4 Batch 6 complete (2026-09-05)
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
| 2 | **Complete** | 7thSense; Adamson; AFMG; AJA Video Systems; Alcorn McBride; Allen & Heath; AMX; Analog Way; Angry IP Scanner Project; Ashly Audio; AtlasIED; Atlona |
| 3 | **Complete** | Audinate; Audio-Technica; AV Stumpfl; AVer; Avolites; Barco; Blackmagic Design; Bose Professional; BrightSign; Brompton Technology; BSS; Capture Visualisation |
| 4 | Complete | ChamSys; Christie; Cisco; Clear-Com; ClearOne; Colorlight; d&b audiotechnik; Datapath; Dataton; Dell / Waves; DELTACAST; Disguise |
| 5 | Complete | Epson; ETC; Extron; Figure 53; FileZilla Project; Flachmann und Heggelbacher; Green Hippo; Green-GO; HP Poly; Huddly; HW group; Intermodulation Analysis |
| 6 | **Complete** | Jabra; JBL Professional; Kramer; L-Acoustics; Lake; LEA Professional; Lectrosonics; LG; Lightware; Logitech; Luminex; MA Lighting |
| 7 | Unfinished | Magewell; Martin Audio; Matrox Video; Medialon; Mersive; Meyer Sound; Microsoft; Milan Manager; Multiple vendors; NagleCode; NDI; NETGEAR |
| 8 | Unfinished | NEXO; NovaStar; Nureva; OBS Project; Obsidian Control Systems; Open Sound Meter; Panasonic; Pingman Tools; Planar; Powersoft; Professional Wireless Systems; QLC+ Project |
| 9 | Unfinished | Rane Commercial; Rational Acoustics; RealTerm Project; Resolume; RF Explorer; Riedel Communications; Room EQ Wizard; Ross Video; RTS Intercoms; sACNView Project; Samsung; ScreenBeam |
| 10 | Unfinished | Sennheiser; Sharp NEC Display Solutions; Shure; Sony Professional; SoundBase; StudioCoast; Symetrix; TeraTerm Project; Unity Intercom; Uwe Sieber; Vaddio; Wisycom |
| 11 | Unfinished | WolfVision; Xilica; Yamaha Professional Audio; Yealink; ZeeVee |

Before closing a later batch, record its source links, missing field/service software, release-family behavior, device relations, confidence, and unresolved install/login evidence in the Decision Register. A batch is not complete merely because its current catalog row exists.

## Phase 4 Batch 2 — completion record

This batch adds only read-only descriptive compatibility evidence. Existing catalog deployment classes, provider policies, detection rules, vendor delivery, and worker authorization are unchanged.

| Manufacturer | Existing catalog audit and source decision | Descriptive products / relations | Release-family and detection decision | Unresolved or intentionally excluded |
|---|---|---|---|---|
| 7thSense | Retained the direct [Delta Media Server page](https://7thsense.one/product/delta-media-server). | Delta Media Server aliases include DeltaServer and DeltaGUI. | No release family or Windows detection claim; Delta is a controlled hardware-host/server deployment. | No supported workstation installer, host-image, license, or device-family evidence added. |
| Adamson | Retained the official [Blueprint AV legacy download page](https://legacy.adamson.ai/support/downloads-directory/design-and-control/blueprint-av). | Blueprint AV is discoverable as a legacy design/prediction product. | No family or installed-evidence claim. | Exact supported loudspeaker/model mapping, current acquisition, and Windows detection remain unresolved. |
| AFMG | Retained direct product pages for [SysTune](https://www.afmg.eu/en/systune), EASE, EASE Focus, EASERA, SoundFlow, and SpeakerLab. | The six existing specialist design/measurement products are searchable as descriptive products. | No vendor branch/coexistence assertion was found for this pass. | No manufacturer-specific device relation is inferred from AFMG simulation data. |
| AJA Video Systems | Retained [AJA Desktop Software](https://www.aja.com/family/software) and added direct [Mini-Config](https://www.aja.com/products/mini-config-software) evidence. | Added Mini-Config; related it to vendor-supported USB Mini-Converters for configuration and firmware evidence. | Mini-Config has Current and Archived branches from its vendor download/archive page; local detection remains Unknown. | Do not infer support for every Mini-Converter or execute firmware actions. |
| Alcorn McBride | Retained direct [WinScript Live](https://alcorn.com/products/winscript-live/) and added the version-scope [support article](https://support.alcorn.com/hc/en-us/articles/115003178786-What-are-the-differences-between-WinScript-WinScript-Live-WinScript-Live-4-and-WinScript-Live-5). | WinScript Live is related to the named V16/V4/VCore controller families for programming and diagnostics. | Current and legacy families are explicit; controller/firmware pairing is retained as a constraint. | Exact installation identities and broader controller revisions require controlled-install evidence. |
| Allen & Heath | Retained direct AHM resources and added the official [dLive page](https://www.allen-heath.com/hardware/dlive-series/). | AHM System Manager, Custom Control Editor, and dLive Director are searchable; AHM and dLive relations are evidence-scoped. | AHM Current/Previous Versions are recorded; coexistence and installed detection are Unknown. | No broad console-family or Custom Control runtime mapping is inferred. |
| AMX | Replaced the NetLinx Studio generic link with the direct [NetLinx Studio page](https://www.amx.com/en-US/products/netlinx-studio.html); TPDesign5 uses official release evidence. | NetLinx Studio, TPDesign5, and SVSI N-Able PC are descriptive products; NetLinx and G5 Touch Panel relations are documented. | No branch/coexistence claim is made. | Legacy AMX utility exact scope and installed detection remain unresolved. |
| Analog Way | Retained the direct [AW EDID Editor page](https://www.analogway.com/products/aw-edid-editor) and added LivePremier Web RCS training evidence. | AW EDID Editor is related to Aquilon/Alta/Midra presentation systems; embedded Web RCS is descriptive only. | No local Web RCS package, detection, or release family is claimed. | No arbitrary device write or download path is added. |
| Angry IP Scanner Project | Retained the official [download page](https://angryip.org/download/) rather than a third-party package source. | The existing managed package is mirrored as descriptive search metadata only; no device relation applies. | Current 3.x and Legacy 2.x branches are explicit. | Network scanning remains governed by the existing approved network-scope policy. |
| Ashly Audio | Retained direct [AquaControl Portal](https://ashly.com/aquacontrol-portal/) and Protea product pages. | AquaControl Portal is related to AQZ32/AQM1208/AQM408 for configuration/discovery; Protea remains legacy service information. | No side-by-side or installed-detection claim. | Protea acquisition, firmware, and exact Windows inventory remain unresolved. |
| AtlasIED | Retained direct [BlueBridge software](https://www.atlasied.com/bluebridge-software) evidence. | BlueBridge Designer II is related to BlueBridge DSP/BB-816 for configuration and firmware evidence. | Current and Old Versions are recorded; local detection remains Unknown. | Atmosphere is browser-based; no new local installer or relation is inferred. |
| Atlona | Corrected Velocity Device Manager to its direct [product page](https://ts.atlona.com/velocity-device-manager/) and retained the discontinued [AMS page](https://ts.atlona.com/product/at-ams-hw/). | VDM relates to vendor-described IP-controllable products for discovery/configuration; AMS is LegacyService only. | VDM Current and AMS Legacy are explicit; both remain infrastructure/manual deployment surfaces. | No ordinary workstation installer, VM detection, or generic device compatibility claim is added. |

All Batch 2 products receive the existing explicit `Unknown` installed-version evidence until a controlled installation or authoritative vendor inventory contract justifies a detector.

## Phase 4 Batch 3 — completion record

Batch 3 adds read-only product, release-family, and device-relation evidence only. It does not change package records, provider/delivery policy, credentials, WinGet allowlists, or worker authorization. All Batch 3 products receive explicit `Unknown` installed-version evidence until controlled-install or vendor inventory evidence is available.

| Manufacturer | Existing catalog audit and link decision | Products / relations | Release-family decision | Unresolved or intentionally excluded |
|---|---|---|---|---|
| Audinate | Retained direct software/product pages and used the official [software downloads](https://www.getdante.com/resources/software-downloads/) page for current/previous release evidence. | Added Controller, Virtual Soundcard, Via, Studio, and Domain Manager; Controller relates to Dante-enabled devices for discovery/configuration/diagnostics and its included Updater for firmware. | Controller has Current and Archived families; no local detection or side-by-side claim. | DVS/Via/Studio/Domain Manager are separate workstation/server products; no broad endpoint or installer-detection inference. |
| Audio-Technica | Retained the official [Wireless Manager manual](https://docs.audio-technica.com/all/WirelessManager_UM_V16_web_241006.pdf) as the stable product/install evidence. | Added Wireless Manager descriptive product and aliases. | No branch evidence recorded. | Exact wireless-system scope, installer acquisition page, and local detection need vendor or controlled-install evidence. |
| AV Stumpfl | Corrected PIXERA to the direct [downloads/archive](https://pixera.one/en/downloads) page. | Added PIXERA with server-family configuration/commissioning relations. | Current and Archived families are explicit from the download page. | Server image, license, project pairing, side-by-side behavior, and Windows detection remain unresolved. |
| AVer | Corrected Room Management to the direct [vendor download entry](https://www.aver.com/Downloads/search?q=Room+Management), which documents it as PTZApp2's successor and lists its supported models. | Added Room Management and PTZApp2; current cameras/videobars receive configuration/diagnostic/firmware/monitoring relations, with PTZApp2 legacy-service only. | No branch/coexistence claim. | Legacy PTZApp2 acquisition and all local detection are unresolved. Other existing AVer utilities were retained as distinct catalog rows without invented device relations. |
| Avolites | Corrected Titan PC Suite to the direct [current/previous-version page](https://www.avolites.com/support/titan-pc-suite/). | Added Titan PC Suite and a scoped Titan-console programming relation. | Current and Archived families are explicit. | Show-file/console compatibility, broader console scope, and local detection remain release-specific/unresolved. |
| Barco | Corrected Projector Toolset to its direct [product page](https://www.barco.com/en/product/projector-toolset); retained WallConnect; recorded the official [ClickShare Configurator EOL/support page](https://www.barco.com/en/support/clickshare-configurator). | Added Projector Toolset, WallConnect, and legacy Configurator; projector and ClickShare relations are evidence-scoped. | No product family is asserted. | WallConnect model scope, XMS deployment details, and local detection remain unresolved. Configurator is a device web UI, not a new downloader. |
| Blackmagic Design | Corrected Desktop Video to the official [support center](https://www.blackmagicdesign.com/support/) and retained ATEM's direct product page. | Added Desktop Video and ATEM Software Control; related them respectively to DeckLink/UltraStudio and ATEM switcher families. | No branch/coexistence claim. | Exact hardware/driver pairing and installed detection are release-specific and unresolved. |
| Bose Professional | Corrected ControlSpace Designer to the direct [product/download page](https://boseprofessional.com/products/power-amplifiers/software-power-amplifiers/controlspace-designer-software). | Added ControlSpace Designer for scoped configuration, commissioning, and diagnostics of named ControlSpace devices. | No branch/coexistence claim. | Individual processor/amplifier release pairing and local detection remain unresolved. |
| BrightSign | Retained official current [BrightAuthor:connected release](https://support.brightsign.biz/hc/en-us/articles/34145328681243-BrightAuthor-connected-1-55-1-Available), release, guide, and legacy sources. | Added BrightAuthor:connected, BrightAuthor Classic, BrightSignOS, and BrightSign App; player relations distinguish current configuration/commissioning, legacy service, and firmware. | Current and legacy products are distinct; no patch families. | Player model/OS/content pairing, credentialed network workflows, and local detection are unresolved. Other developer/cloud utilities were intentionally not represented as device relations. |
| Brompton Technology | Retained official support and the vendor [Tessera Remote manual](https://dl.bromptontech.com/tessera/docs/manual/1.2.pdf). | Added Tessera Remote configuration/diagnostic relations for Tessera processors. | No branch claim; the manual requires matching Remote and processor firmware. | Exact processor variants, acquisition, and local detection remain unresolved. |
| BSS | Retained direct [AVX Architect](https://bssaudio.com/en-US/products/avx-architect), AVX Suite, and support sources. | Added AVX Architect/Control/Manager/NAV Router plus legacy Audio Architect/London Architect; Soundweb OMNI relations are limited to Architect configuration and Manager discovery/monitoring/firmware. | Current AVX and separate legacy products; no patch/coexistence claim. | Exact legacy installed-base scopes and local detection remain unresolved. |
| Capture Visualisation | Corrected Capture to the direct [Windows download/archive page](https://www.capture.se/Downloads/Download-Capture). | Added Capture as a lighting design/documentation/visualisation product; no manufacturer-device relation was inferred. | Current and Archived families are explicit from vendor download/archive evidence. | Fixture/console mapping, side-by-side behavior, and local detection remain unresolved. |

## Phase 4 Batch 4 — completion record

Batch 4 adds 26 descriptive products, 12 evidence-backed release families, and 29 purpose-specific relations. Every added product has explicit `Unknown` installed-version evidence; no package, vendor-delivery, credential, WinGet, or worker authority changed.

| Manufacturer | Existing catalog audit and source decision | Products / relations | Release-family decision | Unresolved or intentionally excluded |
|---|---|---|---|---|
| ChamSys | Replaced MediaMaster's generic software link with the official [MagicQ downloads](https://chamsyslighting.com/software/magicq-downloads/) page, which exposes MagicQ, QuickQ, MediaMaster, change-log, and archive links. | Added MagicQ, MediaMaster, and QuickQ Designer; the console relations are programming/configuration evidence. | MagicQ Current and Archived families; no coexistence claim. | MediaMaster server/license scope and Windows inventory remain unresolved. |
| Christie | Replaced Conductor's all-projector link with its direct [product page](https://www.christiedigital.com/products/projector-management/conductor/); retained direct Twist/Mystique sources. | Added Twist, Mystique, Conductor, and Guardian with scoped 3DLP projector relations. | No branch/coexistence claim. | Projector edition, camera, and firmware compatibility remain vendor/project-specific. |
| Cisco | Retained the official [Webex Device Connector article](https://help.webex.com/en-us/article/383gbd/Cisco-Webex-Device-Connector). | Added Device Connector for Webex Room/Desk/Board onboarding evidence. | No family claim. | Control Hub is web-only; account, network, and local detection are unresolved. |
| Clear-Com | Retained the official [software versions chart](https://clearcom.com/Download-Center/Software-Versions-Chart) and direct Station-IC page. | Added EHX, Dynam-EC, Station-IC, and FreeSpeak II Configuration Editor relations. | EHX Current and Legacy families are recorded from the chart. | License, matrix/card/firmware pairing and installed detection remain unresolved. |
| ClearOne | Retained CONSOLE AI and [resource-library](https://www.clearone.com/resource-library-search) sources. | Added CONSOLE AI and legacy CONVERGE Pro Console for Converge Pro 2 and legacy service. | No coexistence claim. | Console/firmware pairing and Windows inventory require controlled evidence. |
| Colorlight | Replaced the generic home page with direct [LEDVISION](https://en.colorlightinside.com/product/download/381?language=en) evidence. | Added LEDVISION and LEDSetting for Colorlight LED controller configuration/firmware. | No family claim. | Exact controller/model scope, installer identity, and local detection remain unresolved. |
| d&b audiotechnik | Retained product pages and direct [downloads/archive](https://www.dbaudio.com/global/en/products/software/software-archive/). | Added ArrayCalc, R1, and NoizCalc; R1 is scoped to d&b system control/monitoring. | Current and Archived families recorded for ArrayCalc and R1. | System firmware pairing, licensing, and Windows detection remain unresolved. |
| Datapath | Retained WallControl/Aetria pages and the official [Wall Designer](https://walldesigner.datapath.co.uk/) application. | Added WallControl 10, Aetria, and Wall Designer; Wall Designer is related to Fx4/Hx4/x4 controllers. | No family claim. | Server deployment, device scope, and local detection remain unresolved. |
| Dataton | Retained direct [WATCHOUT 7 download](https://www.dataton.com/downloads/watchout7) and legacy-download sources. | Added WATCHOUT as the production/media-server product. | Current 7 and Legacy 6 families are recorded; no side-by-side claim. | WATCHPAX/server switching, license, and local inventory remain unresolved. |
| Dell / Waves | Replaced Dell's generic support home with the OEM [Waves MaxxAudio package record](https://www.dell.com/support/home/en-us/drivers/driversdetails?driverid=0t5nn). | Added OEM audio-processing descriptive metadata only. | No family claim. | Model-specific OEM applicability and installation detection remain unresolved; it is intentionally not an AV device relation. |
| DELTACAST | Retained direct [E-EDID Editor](https://www.deltacast.tv/products/free-software/e-edid-editor/) evidence. | Added E-EDID Editor for scoped Deltacast I/O/EDID configuration and diagnostics. | No family claim. | Exact cards/modules and local detection remain unresolved. |
| Disguise | Retained Designer and direct [r30 release notes](https://help.disguise.one/designer/release-notes/r30). | Added Designer for VX media-server configuration/commissioning. | Current r30 and Legacy r29 families are recorded. | License/server variant compatibility and Windows inventory remain unresolved. |

## Phase 4 Batch 5 — completion record

Batch 5 adds 31 descriptive products, 10 evidence-backed release families, and 27 purpose-specific relations using the same read-only model. The data is not a downloader, installer, firmware executor, or device probe; every added product remains explicit `Unknown` installed-version evidence.

| Manufacturer | Existing catalog audit and source decision | Products / relations | Release-family decision | Unresolved or intentionally excluded |
|---|---|---|---|---|
| Epson | Replaced the generic support landing page with official [installation-tools](https://epson.com/advanced-projector-installation-tools) and management sources. | Added Projector Professional Tool, Projector Management, and Content Manager; projector relations are purpose-scoped. | No family claim. | Model compatibility and local detection remain unresolved. |
| ETC | Retained direct [system configuration software](https://www.etcconnect.com/Products/Networking/System-Configuration/Software/Software.aspx) and [Eos history](https://support.etcconnect.com/ETC/Consoles/Eos_Family/Software_and_Programming/All_Eos_Family_Software_Versions). | Added Eos, Concert, and UpdaterAtor with Eos/Net3 relations. | Eos Current and Archived families are recorded; Concert has no coexistence claim. | Console/device package pairing and local detection remain unresolved. |
| Extron | Replaced generic product/software links with direct pages for [DSP Configurator Pro](https://www.extron.com/product/software/dspcpro), [VCS](https://www.extron.com/product/software/vcs), [XTP](https://www.extron.com/product/software/xtpscs?src=software), [EDID Manager](https://www.extron.com/product/software/edidmanager30), [Firmware Loader](https://www.extron.com/product/software/fwloader), and DataViewer. | Added distinct current and legacy field tools; DMP, XTP, Quantum Ultra, and Pro-control relations are evidence-scoped. | No unproven family/coexistence claim. | Insider-gated acquisition, model compatibility, and installed detection remain unresolved. |
| Figure 53 | Retained official [QLab download/archive](https://qlab.app/download/). | Added QLab as a macOS-only show-control product. | Current and Legacy QLab 4 families are recorded. | No Windows runtime or AV hardware relation is inferred. |
| FileZilla Project | Retained direct [Client download](https://filezilla-project.org/download.php?show_all=1). | Added Client as a manual transfer utility only. | No family claim. | It is intentionally not a device relation or vendor-delivery authority. |
| Flachmann und Heggelbacher | Retained direct [Docklight](https://docklight.de/) and Scripting manual sources. | Added Docklight and Docklight Scripting as protocol diagnostic/service tools. | No family claim. | No manufacturer/device relation is inferred from generic serial/network tooling. |
| Green Hippo | Retained current [Hippotizer downloads](https://support.green-hippo.com/hc/en-gb/sections/16360938723730-Hippotizer) and legacy sources. | Added Hippotizer for media-server configuration/commissioning. | Current and Legacy families recorded. | Server, license, show-file, and local detection remain unresolved. |
| Green-GO | Retained direct [Control](https://www.greengocom.com/software-products/control), [Update Connection](https://www.greengocom.com/software-products/updateconnection), and downloads sources. | Added Control and Update Connection for Green-GO systems/switches. | Current and Legacy v4 families are recorded for Control. | Device revisions and installed detection remain unresolved. |
| HP Poly | Replaced generic support links with official [Poly Studio Desktop release notes](https://support.hp.com/us-en/document/ish_13663054-13662994-16) and Camera Control download guidance. | Added Studio Desktop, Camera Control, and legacy Lens Desktop relations for supported Studio devices. | Current Studio Desktop and Legacy Lens families recorded. | Tenant/login, model-specific scope, and local detection remain unresolved. |
| Huddly | Retained direct [Connect App](https://www.huddly.com/app/) and driver support guidance. | Added Connect App and legacy Desktop App for Huddly camera configuration/firmware. | No family claim. | Driver-manager Windows scope and local inventory remain unresolved. |
| HW group | Replaced the unstable Hercules page with vendor HWg-Config manual evidence. | Added Hercules and HWg-Config for bounded discovery/configuration of HW-group network devices. | No family claim. | Exact product/download page and installed detection remain unresolved. |
| Intermodulation Analysis | Retained official [IntermodExplorer](https://www.intermodulationanalysis.com/) and documentation. | Added web-based IntermodExplorer descriptive metadata. | No family claim. | It is intentionally web-only with no local installer or device relation. |

## Delivery phases

1. **Phase 1 — research and ledger:** complete for reference batch; no runtime or manifest change.
2. **Phase 2 — schema and fixtures:** **complete.** `SoftwareCompatibilityCatalog` and `CompatibilityCatalogParser` implement standalone schema version 1 under `src/AVWorkstationToolkit.Domain/Catalog`. The schema requires `Products`, `ReleaseFamilies`, `InstalledVersions`, and `DeviceSoftwareRelations` arrays, rejects unknown and duplicate JSON properties, and is not automatically loaded by the existing production catalog loader.
3. **Phase 3A — bounded reference-vendor pilot:** **complete.** `manifests/software-compatibility.json` contains only the accepted Q-SYS, Biamp, and Crestron evidence. The compiled composition loads it independently and exposes read-only Application queries; all external/manual authority boundaries remain unchanged.
4. **Phase 3B — compiled WPF compatibility workflow:** **complete.** The existing Find Apps search now also surfaces read-only product/alias and device/model/alias matches from `CompatibilityCatalogQueryService`. The existing details surface presents device software by purpose and product release families, installed evidence, applicable devices, and validated evidence links without creating package rows or action authority.
5. **Phase 4 — catalog-wide completion:** Batches 4–6 are **complete**. Batch 6 adds 21 descriptive products, 9 durable release-family records, and 12 evidence-backed relations. They do not change package authority. Process only frozen Batches 7–11; do not re-audit Batches 1–6 unless a source change or concrete conflict requires it.

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
- [x] Phase 3B integrates product and device compatibility search into Find Apps and the existing compiled details surface while preserving read-only authority isolation.

## Phase 4 Batch 6 — completion record

Batch 6 adds 21 descriptive products, 9 release-family records, and 12 purpose-specific device/software relations. All added products retain explicit `Unknown` installed evidence. No package, WinGet, delivery, credential, or worker authority changed.

| Manufacturer | Link/product/relation decision | Family decision | Excluded or unresolved |
|---|---|---|---|
| Jabra | Retained [Direct](https://www.jabra.com/software-and-services/jabra-direct); added Direct relation for supported professional headsets/speakerphones. | Direct current/legacy branches from Jabra support guidance. | Xpress/Plus tenant/device scope and local detection unresolved. |
| JBL Professional | Retained direct [Performance Manager](https://jblpro.com/en-US/products/performance-manager); added Performance Manager and Venue Synthesis. | No branch claim. | Exact JBL/Crown model and inventory scope unresolved. |
| Kramer | Retained direct K-Config/K-Router product sources; added their descriptive identities and scoped relations. | No branch claim. | Controller/matrix revision and installed detection unresolved. |
| L-Acoustics | Retained [Soundvision](https://www.l-acoustics.com/products/soundvision/) and [LA Network Manager](https://www.l-acoustics.com/products/network-manager/); added controller relation. | Current/archive families are vendor-backed. | Controlled-install coexistence and firmware pairing remain unresolved. |
| Lake | Retained Controller support source; added processor/amplifier configuration relation. | No branch claim. | Exact model/firmware and local inventory unresolved. |
| LEA Professional | Retained [SharkWare](https://leaprofessional.com/products/sharkware/page/2/); added Connect Series relation. | No branch claim. | Firmware, installer identity, and detection remain unresolved. |
| Lectrosonics | Retained [Wireless Designer](https://lectrosonics.com/wireless-designer/); added documented receiver/IEM discovery relation. | No branch claim. | USB/firmware workflow and installation evidence unresolved. |
| LG | Corrected/retained direct [SuperSign downloads](https://solutions.lg.com/us/software/supersign/supersign-downloads); added SuperSign CMS and LED Assistant, with DVLED relation only. | No branch claim. | CMS licensing/server deployment and model/firmware scope unresolved. |
| Lightware | Retained direct [Device Controller](https://www.lightware.com/en/products/software/lightware-device-controller); added LDC/LDU2 product data and LDC relation. | No branch claim. | Device-specific coverage and installed evidence unresolved. |
| Logitech | Retained Sync/CollabOS and added Logi Tune descriptive identity; added Sync relation for documented CollabOS room devices. | No branch claim. | Account/service and endpoint firmware pairing unresolved. |
| Luminex | Retained [Araneo](https://www.luminex.be/products/software/araneo/) and [Araneo Studio](https://www.luminex.be/products/software/araneo-studio/); added GigaCore relation. | No branch claim. | License, model coverage, and local detection unresolved. |
| MA Lighting | Corrected/retained direct [grandMA3 downloads/archive](https://www.malighting.com/downloads/products/grandMA3/); added grandMA3/grandMA2 onPC and MA3 relation. | grandMA3 current/archive and grandMA2 legacy are vendor-backed. | Console project pairing, multi-install behavior, and inventory evidence unresolved. |
