# AV Software Catalog Decision Register

**Status:** Phase 4 Batches 4 and 5 complete — 2026-09-04
**Companion source of truth:** [AV Software, Version & Device Compatibility Catalog Roadmap](AV-Software-Catalog-Roadmap.md)

This register records evidence and decisions for later catalog-roadmap work. It does not amend a production manifest, authorize a package, or replace the existing exact-ID/explicit-provider security model.

## Entry classes

- **VERIFIED FACT** — supported by the cited vendor source or present, reviewable repository data.
- **DESIGN DECISION** — an adopted roadmap rule; it may be changed only by a later recorded decision.
- **RECOMMENDATION** — proposed follow-up work, not yet an implementation fact.
- **UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN** — intentionally unknown. Do not fill it from a brand, filename, or inference.

## Entries

### CAT-001 — Existing deployment boundary

**VERIFIED FACT**
The maintained catalog model distinguishes knowledge from deployment permission. External and awareness records cannot enter the managed WinGet worker; the existing Crestron child records inherit a constrained authenticated parent provider. Source: [Commercial AV catalog model](Commercial-AV-Catalog.md) and `manifests/external-applications.json` at the Phase 1 baseline.

**DESIGN DECISION**
Product, version, and device relations introduced by this roadmap are descriptive evidence. They cannot create installation, execution, credential, or download authority.

### CAT-002 — Q-SYS product families

**VERIFIED FACT**
Q-SYS says Designer on the PC and firmware on the system hardware must use the same release version when creating or maintaining a design. It also states that multiple Designer copies can be installed to work with older designs. [Q-SYS installation/update guidance](https://help.qsys.com/Content/Q-SYS_Designer/0015_Installing_Q-SYS.htm) and [Q-SYS compatibility overview](https://help.qsys.com/Content/Q-SYS_Compatibility/Q-SYS_Compatibility_Overview.htm) are the authoritative source pair.

**DESIGN DECISION**
Represent Q-SYS Designer as one product with `Current`, `LTS`, and `Archived` release families. Patch versions belong to their family unless compatibility or upgrade/downgrade documentation establishes a separate operating boundary. Project/Core compatibility stays explicit rather than becoming an automatic update rule.

**RECOMMENDATION**
Later implementation should inventory every recognized Designer uninstall record and retain raw name/version/source/location evidence. UI should show one product row and list all observed installations in Details.

**UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN**
The exact current uninstall identities, registry keys, executable locations, and side-by-side behavior across current, LTS, and archived Windows installers must be characterized on a controlled workstation. Do not promote the present display-name regex into a multi-family detector without that evidence.

### CAT-003 — Biamp Tesira and Canvas

**VERIFIED FACT**
Biamp publishes Tesira Software and Biamp Canvas releases on its [Tesira Software & Firmware page](https://support.biamp.com/Tesira/Software-Firmware), directs legacy releases to [Tesira release notes](https://support.biamp.com/Tesira/Miscellaneous/Tesira_release_notes), and says a Canvas control surface must match the Tesira Software version sending the configuration. [Canvas guidance](https://support.biamp.com/Tesira/Control/Getting_Started_with_Canvas) scopes that last statement.

**DESIGN DECISION**
Tesira and Canvas remain distinct products with an evidence-scoped compatible-family relation. The relation does not imply a shared installer, an identical registry identity, universal coexistence, or automatic firmware action.

**RECOMMENDATION**
Use explicit release-family/archive evidence from Biamp release notes before showing a legacy installation as serviceable. Preserve the existing project-pinned and manual-handoff posture.

**UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN**
Tesira and Canvas side-by-side installation behavior, exact installed-record patterns for multiple historical families, and any vendor-login-only historical downloads require physical-install or authorized Biamp review.

### CAT-004 — Biamp Nexia

**VERIFIED FACT**
Biamp states that an operator must install Nexia software to communicate with a Nexia device. Its discontinued-material firmware procedure directs the operator to use Nexia Software. See [Getting Started with Nexia](https://support.biamp.com/Audia-Nexia/Getting_Started_with_Nexia) and [Nexia firmware upgrade procedure](https://downloads.biamp.com/assets/docs/default-source/discontinued/biamp_upgrade_procedure_nexia_firmware.pdf?sfvrsn=61e8f289_6).

**RECOMMENDATION**
Create a legacy-service, awareness/manual-only Nexia product proposal in the later Biamp implementation batch. Its device relations should cover only documented Nexia families and use `LegacyService` plus configuration/diagnostics/firmware purposes.

**UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN**
No current public installer/archive index, supported Windows range, definitive installer signature/publisher facts, release lineage, registry identity, or side-by-side behavior was verified in this pass. Obtain these from Biamp support or a controlled physical install before adding an operational/download record.

### CAT-005 — Crestron software parent and child identities

**VERIFIED FACT**
The existing catalog contains one authenticated Crestron MasterInstaller parent and independent child identities for VT Pro-e, SIMPL Windows, Crestron Database, Device Database, Toolbox, Smart Graphics, and DM NVX Tool. The SIMPL release notes say SIMPL relies on the databases and identify MasterInstaller/Toolbox; [SIMPL release notes](https://www.crestron.com/release_notes/simpl_release_notes.html) are vendor evidence.

**DESIGN DECISION**
Keep the parent-provider relationship. A device/software mapping may point to a child product, but it may not create an independent Crestron host, credential target, download path, or executable authority.

**RECOMMENDATION**
During a reviewed later manifest pass, replace generic child *detail* links with product-specific official pages where stable, while retaining the shared constrained provider for acquisition.

**UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN**
The authoritative current MasterInstaller product IDs, child version meanings, account entitlements, and current installer detection identities require an authorized Crestron account and controlled workstation evidence.

### CAT-006 — Crestron CP4N

**VERIFIED FACT**
The CP4N firmware release notes name SIMPL Windows, Crestron Toolbox, Device Database, and Crestron Database minimum versions; CP4N resources identify SIMPL and Toolbox as programming tools. Sources: [CP4N release notes](https://www.crestron.com/release_notes/cp4n_2.8000.00017.01_release_notes.pdf) and [CP4N resources](https://docs.crestron.com/en-us/9214/Content/Topics/Resources/Resources.htm).

**DESIGN DECISION**
Create four relations for CP4N (programming: SIMPL/Databases; diagnostics/firmware: Toolbox) with `VendorDocumented` confidence and a version-scoped constraint. Do not declare every CP4N firmware release compatible with every listed tool version.

### CAT-007 — Crestron DM NVX

**VERIFIED FACT**
Crestron describes DM NVX Tool as a commissioning/diagnostic tool for DM NVX encoders/decoders and lists configuration, troubleshooting, and firmware functions. Its DM NVX documentation identifies Toolbox Device Discovery for network discovery. Sources: [DM NVX Tool](https://www.crestron.com/getmedia/09678b09-2665-445a-acfd-084bc638fe02/ss_SW-DMNVXTOOL) and [DM NVX discovery](https://docs.crestron.com/en-us/8425/Content/Topics/Product-Manual/Appendix-Device-Discovery.htm).

**DESIGN DECISION**
Model DM NVX Tool and Toolbox as separate relations by purpose; do not replace one with the other or infer the relationship for non-DM-NVX devices.

### CAT-008 — Canonical version/device data shape

**DESIGN DECISION**
Later schema work uses four linked entities: `Product`, `ReleaseFamily`, `InstalledVersion`, and `DeviceSoftwareRelation`. The relationship dataset is the sole authority for both software-to-device and device-to-software search indexes. Every relation has a purpose, evidence URL, evidence kind, confidence, constraints, and lifecycle.

**DESIGN DECISION**
Allowed relation confidence values are `VendorDocumented`, `PhysicalInstallVerified`, and `Unresolved`. There is intentionally no inferred/manufacturer-match confidence.

### CAT-009 — UI behavior

**DESIGN DECISION**
Find Apps will search device aliases as well as product aliases. Device results group relevant software by purpose and confidence. Product rows remain product-level; installed versions, release families, archives, and applicable devices appear in Details. No new UI state grants execution authority.

### CAT-010 — Frozen audit ledger

**DESIGN DECISION**
The manufacturer batch ledger in the roadmap is fixed for Phase 4. Batch 1 (Q-SYS, Biamp, Crestron) is research-complete. Each remaining manufacturer appears exactly once in an unfinished batch. Subsequent prompts process unfinished batches only, unless a vendor source change or recorded conflict reopens a completed batch.

### CAT-011 — Validation and evidence discipline

**DESIGN DECISION**
Later catalog changes require strict schema tests, reverse-index tests, source-link review, relation uniqueness checks, and explicit incomplete-inventory behavior. They must not add a generic downloader, a generic device probe, or a new managed execution route.

**UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN**
Before operational multi-version detection or device-aware currentness is enabled, validate on controlled installations: multiple Q-SYS Designer families; Tesira/Canvas family behavior; Nexia; and authorized Crestron MasterInstaller children. Record only sanitized, non-customer evidence.

### CAT-012 — Standalone compatibility schema version 1

**DESIGN DECISION**
Phase 2 implements a strict, standalone `CompatibilityCatalogParser` with `SchemaVersion: 1` and required `Products`, `ReleaseFamilies`, `InstalledVersions`, and `DeviceSoftwareRelations` arrays. It rejects unknown fields, duplicate JSON properties, malformed identifiers, non-HTTPS evidence URIs, unsupported vocabulary, duplicate identities, inconsistent release-family ownership, and invalid observed-version evidence.

**DESIGN DECISION**
The existing `RepositoryCatalogLoader` does not load this schema yet. Existing managed, operational-external, and awareness manifests remain their current schemas and load unchanged. Later adoption requires an explicit, reviewed composition decision and migration fixture; it must not silently reinterpret existing records.

### CAT-013 — Identity, inventory, and relationship normalization

**DESIGN DECISION**
`Product` owns `ReleaseFamily`; a release-family branch is unique within `(ProductId, Kind, Branch)`. `InstalledVersion` is independent local evidence and permits multiple observations for a Product. Only `Observed` evidence may contain a numeric version; `Unknown`, `Incomplete`, and `Unavailable` preserve the reason without claiming absence or currency.

**DESIGN DECISION**
`DeviceSoftwareRelation` is the single mapping source. Reverse software-to-device and device-to-software indexes are derived in memory from relations; they are not separately authored. Semantic duplicate relations are rejected by `(DeviceFamilyId, ProductId, optional ReleaseFamilyId, Purpose)`.

### CAT-014 — Compatibility metadata cannot authorize execution

**DESIGN DECISION**
The Phase 2 compatibility model deliberately has no package provider, delivery, deployment, execution, command, or authority field. It remains descriptive metadata. `PackageDefinition`, managed exact-ID WinGet authority, external/awareness/manual boundaries, and the production worker authorization path are unchanged.

### CAT-015 — Reference-vendor production compatibility document

**DESIGN DECISION**
Phase 3A adopts `manifests/software-compatibility.json` as the production schema-version-1 compatibility source for Q-SYS, Biamp, and Crestron only. It contains 9 descriptive Products, 7 release-family records, no authored machine installation observations, and 24 purpose-specific device/software relations derived from the accepted Phase 1 evidence. Scalar `Purpose` means the four accepted CP4N product associations are encoded as five records: three Programming records plus separate Toolbox Diagnostics and Firmware records.

**DESIGN DECISION**
Q-SYS Designer remains one Product with `Current`, `LTS`, and `Archived` families and no numeric family bounds invented by this pass. Tesira and Canvas are distinct Products with evidence-backed Current/Archived branches and an explicitly conditional Canvas-to-Tesira-family Configuration relation. Nexia is a Legacy Product with only manual informational relations; it has no ReleaseFamily claim, package record, delivery route, or detection rule. Crestron relations reuse child product identities but do not duplicate or modify the MasterInstaller parent-provider boundary.

**DESIGN DECISION**
Each separately displayed purpose is an independent `DeviceSoftwareRelation`. Device/model aliases and reverse indexes are derived solely from those records. Empty exact-model lists mean the accepted evidence is family-scoped; they must not be interpreted as every model made by the vendor.

### CAT-016 — Read-only runtime composition and installed evidence

**DESIGN DECISION**
`RepositoryCompatibilityCatalogLoader` loads the fixed compatibility document independently of `RepositoryCatalogLoader`. `CompiledAppComposition` exposes a `CompatibilityCatalogQueryService` next to, not inside, `PackageCatalog`. The query service provides product, alias, release-family, installed-evidence, device search, grouped software-purpose, and reverse applicable-device projections without WPF or worker types.

**DESIGN DECISION**
The launcher embeds and requires the compatibility document so source and packaged compiled composition use the same reviewed bytes. Loading and querying are read-only. No compatibility record is copied into package planning, vendor delivery, selection policy, action requests, or worker authorization.

**DESIGN DECISION**
Until a product has an authoritative multi-install detector, production queries return explicit `Unknown` `InstalledVersion` evidence. Existing package inventory is not adapted in Phase 3A because its package-level result does not establish release-family identity, coexistence, or every observed installation for Q-SYS, Tesira/Canvas, Nexia, or authenticated Crestron children.

**UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN**
The finite unresolved evidence remains unchanged: authoritative multi-install identities and locations for Q-SYS Designer; Tesira/Canvas coexistence and historical install identities; Nexia acquisition, release lineage, supported Windows versions, and installed detection; and authorized MasterInstaller child identities/version meanings. These are evidence gates for later detection work, not Phase 3A implementation blockers.

### CAT-017 — Compiled Find Apps compatibility presentation

**DESIGN DECISION**
Phase 3B extends the existing Find Apps field rather than adding a separate device utility. Package rows continue to use the existing catalog query. A compact read-only compatibility result area independently presents matches from the Phase 3A `CompatibilityCatalogQueryService`, labels each match as Device or Software, and opens the existing details window. Compatibility results have no selection state and are never copied into package planning or worker requests.

**DESIGN DECISION**
Device details group the single authoritative `DeviceSoftwareRelation` projection by readable field-service purpose and show applicability, confidence, optional release-family scope, constraints, and evidence. Relevant-software buttons navigate within the same details surface to the selected Product. Product details show lifecycle, aliases, every release family, explicit installed-version evidence, and reverse applicable-device groups. `Unknown` evidence is rendered as `Unknown / Not yet verified`, never as absent or not installed.

**DESIGN DECISION**
Compatibility product, release, and relationship URLs use the existing validated user browser-handoff service through typed, non-download intents. These links cannot invoke package delivery, reveal cached payloads, create downloads, or grant execution authority. Q-SYS Designer remains one product with Current, LTS, and Archived sections; no release family or patch becomes a top-level package row.

**VERIFIED FACT**
Focused presentation tests exercise CP4N and DM-NVX device results, Q-SYS release-family display, explicit unknown evidence, reverse software-to-device details, in-place relevant-software navigation, browser-only evidence intents, and unchanged package filtering/selection behavior.

### CAT-018 — Phase 4 Batch 2 evidence and implementation

**VERIFIED FACT**
Batch 2 reviewed all twelve frozen manufacturers: 7thSense, Adamson, AFMG, AJA Video Systems, Alcorn McBride, Allen & Heath, AMX, Analog Way, Angry IP Scanner Project, Ashly Audio, AtlasIED, and Atlona. The reviewed direct vendor sources are recorded in the Batch 2 table in the companion roadmap.

The following evidence supports the device-scoped records added in this pass: [AJA Mini-Config](https://www.aja.com/products/mini-config-software) identifies supported USB Mini-Converters; [Alcorn WinScript version guidance](https://support.alcorn.com/hc/en-us/articles/115003178786-What-are-the-differences-between-WinScript-WinScript-Live-WinScript-Live-4-and-WinScript-Live-5) scopes versions to V16/V4/VCore controller families; [AHM](https://www.allen-heath.com/hardware/ahm/) and [dLive](https://www.allen-heath.com/hardware/dlive-series/) identify their editor/control workflows; [AMX NetLinx Studio](https://www.amx.com/en-US/products/netlinx-studio.html) identifies the NetLinx programming scope; [AW EDID Editor](https://www.analogway.com/products/aw-edid-editor) scopes Aquilon/Alta/Midra compatibility analysis; [AquaControl Portal](https://ashly.com/aquacontrol-portal/) names AQZ32/AQM1208/AQM408; [BlueBridge software](https://www.atlasied.com/bluebridge-software) identifies supported DSP design; and [Velocity Device Manager](https://ts.atlona.com/velocity-device-manager/) scopes IP-controllable product discovery/configuration.

**DESIGN DECISION**
Batch 2 adds 26 descriptive `Product` records, 12 evidence-backed `ReleaseFamily` records, and 22 `DeviceSoftwareRelation` records to the existing production `software-compatibility.json` document. It does not add package IDs, provider fields, download routes, credentials, deployment authority, device probes, or worker authority. Existing managed/external/awareness manifest behavior is unchanged.

Version families are recorded only when the source identifies a durable branch: AJA Mini-Config Current/Archive; Alcorn WinScript Live Current/Legacy; Allen & Heath AHM Current/Previous Versions; Angry IP Scanner 3.x/2.x; AtlasIED BlueBridge Designer II Current/Old Versions; and Atlona VDM Current/AMS Legacy. These are product-history views, not patch rows, generic update targets, or coexistence claims.

7thSense Delta, Adamson Blueprint AV, AFMG specialist tools, AJA Desktop Software, Allen & Heath Custom Control, AMX SVSI N-Able, Analog Way Web RCS, and AtlasIED BlueBridge Control are searchable products where the workstation use case is supported, but are deliberately not given unproven device mappings or installed-version detectors. Angry IP Scanner has no AV device relation because it is a network utility rather than manufacturer compatibility metadata.

**UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN**
No Batch 2 product received a new authoritative Windows multi-version detector. Exact uninstall/registry identities, coexistence rules, and signed installer/publisher evidence remain unverified for the relevant vendor packages. Delta host images/licenses, Blueprint AV current acquisition and model scope, AFMG manufacturer data scope, AMX legacy utility scope, Protea acquisition/firmware, and Atlona VM inventory are explicit evidence gaps. They do not permit a fallback to "not installed," nor do they block the read-only product/device workflow.

### CAT-019 — Phase 4 Batch 3 evidence and implementation

**VERIFIED FACT**
Batch 3 reviewed all twelve frozen manufacturers: Audinate, Audio-Technica, AV Stumpfl, AVer, Avolites, Barco, Blackmagic Design, Bose Professional, BrightSign, Brompton Technology, BSS, and Capture Visualisation. The companion roadmap records the per-manufacturer link disposition, exclusions, release-family decision, relations, and unresolved evidence.

The added device-scoped evidence is from direct vendor material: [Dante Controller](https://www.getdante.com/products/software-essentials/dante-controller/) and [Dante Updater](https://www.getdante.com/products/software-essentials/dante-controller/dante-updater/) identify Dante-device routing, diagnostics, discovery, and updater scope; [AVer Room Management](https://www.aver.com/Downloads/search?q=Room+Management) identifies its PTZApp2-successor and model scope; [Barco Projector Toolset](https://www.barco.com/en/product/projector-toolset) lists supported projectors; [ClickShare Configurator guidance](https://www.barco.com/en/support/knowledge-base/9653-clickshare-configurator-usage) establishes its web-UI scope; [ATEM Software Control](https://www.blackmagicdesign.com/products/atemmini/software), [ControlSpace Designer](https://boseprofessional.com/products/power-amplifiers/software-power-amplifiers/controlspace-designer-software), [Tessera Remote](https://dl.bromptontech.com/tessera/docs/manual/1.2.pdf), and [AVX Architect](https://bssaudio.com/en-US/products/avx-architect) establish their respective vendor-device workflows.

**DESIGN DECISION**
Batch 3 adds 28 descriptive `Product` records, 8 evidence-backed `ReleaseFamily` records, and 35 purpose-specific `DeviceSoftwareRelation` records. The product data intentionally keeps distinct workstation/service tools separate: Audinate's Controller/DVS/Via/Studio/Domain Manager, AVer Room Management/PTZApp2, Barco desktop and web surfaces, Blackmagic Desktop Video/ATEM control, BrightSign current/legacy/firmware surfaces, and BSS current/legacy tools are not collapsed into one generic vendor row.

Durable current/archive families are recorded only for Dante Controller, PIXERA, Titan PC Suite, and Capture because the official sources expose those branches. BrightSign/BSS current-versus-legacy identities remain separate products instead of invented release branches. No relation implies an installer, endpoint probe, browser automation, firmware action, or side-by-side support.

**UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN**
No Batch 3 product receives an installed-version detector. Exact Windows inventory identities, multiple-install behavior, signed package/publisher evidence, account-gated acquisition, firmware compatibility, and workstation coexistence require controlled installations or authenticated vendor evidence. Audio-Technica wireless-system mapping, AVer legacy PTZApp2 acquisition, Barco WallConnect/XMS scope, BrightSign player/OS/content pairing, PIXERA server images/licenses, and Capture fixture/console mapping remain intentionally unresolved. These gaps preserve explicit `Unknown` evidence and do not create execution authority.

### CAT-020 — Phase 4 Batch 4 evidence and implementation

**VERIFIED FACT**
Batch 4 reviewed all twelve frozen manufacturers: ChamSys, Christie, Cisco, Clear-Com, ClearOne, Colorlight, d&b audiotechnik, Datapath, Dataton, Dell / Waves, DELTACAST, and Disguise. Their retained/corrected links, additions, exclusions, release-family decisions, and unresolved evidence are in the companion roadmap.

Direct vendor evidence supports the scoped records: [ChamSys MagicQ downloads](https://chamsyslighting.com/software/magicq-downloads/) exposes stable, beta, and archive material; [Christie Conductor](https://www.christiedigital.com/products/projector-management/conductor/) scopes 3DLP monitoring; [Cisco Device Connector](https://help.webex.com/en-us/article/383gbd/Cisco-Webex-Device-Connector) scopes workstation onboarding; [Clear-Com versions](https://clearcom.com/Download-Center/Software-Versions-Chart) identifies EHX/Dynam-EC/FreeSpeak status; [Colorlight LEDVISION](https://en.colorlightinside.com/product/download/381?language=en), [d&b archive](https://www.dbaudio.com/global/en/products/software/software-archive/), [Datapath Wall Designer](https://walldesigner.datapath.co.uk/), [WATCHOUT](https://www.dataton.com/downloads/watchout7), [E-EDID Editor](https://www.deltacast.tv/products/free-software/e-edid-editor/), and [Designer r30](https://help.disguise.one/designer/release-notes/r30) support the encoded product, branch, or relation claims.

**DESIGN DECISION**
Batch 4 adds descriptive product metadata and only relationship records whose hardware scope is identified by a vendor source. The Dell / Waves OEM record is deliberately descriptive only. Product metadata remains disjoint from package/provider/delivery fields; no relation creates download, credential, managed WinGet, or worker authority.

**UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN**
No Batch 4 product receives a Windows installed-version detector or coexistence claim. Account-gated downloads, license/server roles, exact product-model/firmware pairing, and installer identity/publisher evidence require controlled installation or vendor-login evidence.

### CAT-021 — Phase 4 Batch 5 evidence and implementation

**VERIFIED FACT**
Batch 5 reviewed all twelve frozen manufacturers: Epson, ETC, Extron, Figure 53, FileZilla Project, Flachmann und Heggelbacher, Green Hippo, Green-GO, HP Poly, Huddly, HW group, and Intermodulation Analysis. The companion roadmap is the completion ledger for all per-manufacturer source decisions.

The relevant direct sources include [Epson projector tools](https://epson.com/advanced-projector-installation-tools), [ETC configuration software](https://www.etcconnect.com/Products/Networking/System-Configuration/Software/Software.aspx), [Extron DSP Configurator Pro](https://www.extron.com/product/software/dspcpro), [QLab download/archive](https://qlab.app/download/), [Green-GO Control](https://www.greengocom.com/software-products/control), [HP Poly Studio Desktop release notes](https://support.hp.com/us-en/document/ish_13663054-13662994-16), [Huddly Connect](https://www.huddly.com/app/), and [IntermodExplorer](https://www.intermodulationanalysis.com/). Extron acquisition remains account-gated where its source says so; direct product links are browser evidence, not automated acquisition routes.

**DESIGN DECISION**
Batch 5 preserves distinct tools where their field purposes differ (for example Epson setup/monitoring/content, ETC Eos/Concert/UpdaterAtor, Extron configuration/diagnostic/firmware utilities, and Green-GO configuration/update). QLab and IntermodExplorer remain descriptive platform/web information; FileZilla and Docklight remain manual utilities without manufacturer compatibility claims. Product and relation data stay read-only and cannot alter any existing PackageDefinition, manual/external boundary, or production worker authorization.

**UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN**
No Batch 5 product receives multi-install detection, side-by-side support, a generic download route, or arbitrary device coverage. Exact installer inventory identities, signed payload/publisher data, vendor account rights, device firmware compatibility, and physical compatibility remain explicit unknowns pending controlled installation or vendor evidence.

### CAT-022 — Phase 4 Batch 6 evidence and implementation

**VERIFIED FACT**
Batch 6 reviewed all twelve frozen manufacturers: Jabra, JBL Professional, Kramer, L-Acoustics, Lake, LEA Professional, Lectrosonics, LG, Lightware, Logitech, Luminex, and MA Lighting. Direct sources support the scoped records: [Jabra Direct](https://www.jabra.com/software-and-services/jabra-direct), [JBL Performance Manager](https://jblpro.com/en-US/products/performance-manager), [LA Network Manager](https://www.l-acoustics.com/products/network-manager/), [SharkWare](https://leaprofessional.com/products/sharkware/page/2/), [Wireless Designer](https://lectrosonics.com/wireless-designer/), [LG SuperSign downloads](https://solutions.lg.com/us/software/supersign/supersign-downloads), [Lightware Device Controller](https://www.lightware.com/en/products/software/lightware-device-controller), [Araneo](https://www.luminex.be/products/software/araneo/), and [grandMA3 downloads](https://www.malighting.com/downloads/products/grandMA3/).

**DESIGN DECISION**
Batch 6 adds 21 descriptive Products, 9 evidence-backed ReleaseFamilies, and 12 purpose-specific DeviceSoftwareRelations. Device relations are limited to the vendor-supported Jabra professional devices, JBL/Crown networked systems, Kramer control/matrix products, L-Acoustics amplified controllers, Lake processing, LEA Connect Series, Lectrosonics listed wireless systems, LG DVLED, Lightware devices, Logitech CollabOS devices, Luminex GigaCore, and grandMA3 systems. No compatibility record adds a package ID, delivery route, credential, device probe, managed WinGet permission, or worker authority.

**UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN**
No Batch 6 product receives authoritative installed-version detection, coexistence behavior, or a generic installer route. Exact Windows inventory identities, signed installer/publisher evidence, licensing/account requirements, model/firmware/project pairing, and multi-version behavior require controlled installation or vendor-login evidence. These gaps retain explicit `Unknown` evidence and are not schema blockers.

### CAT-023 — Phase 4 Batch 7 evidence and implementation

**VERIFIED FACT**
Batch 7 reviewed all twelve frozen manufacturers: Magewell, Martin Audio, Matrox Video, Medialon, Mersive, Meyer Sound, Microsoft, Milan Manager, Multiple vendors, NagleCode, NDI, and NETGEAR. Direct evidence scopes [Magewell USB Capture Utility](https://www.magewell.com/downloads/usb-capture), [Martin Audio software](https://martin-audio.com/support/software), [Medialon Manager](https://medialon.com/products/medialon-manager/), [Milan Manager](https://milanmanager.com/), [NDI Tools](https://ndi.video/tools/), and [NETGEAR Engage](https://www.netgear.com/business/management/engage-software/).

**DESIGN DECISION**
Batch 7 adds 21 descriptive Products, 3 release families, and 8 purpose-specific relations. Relations remain strictly scoped to named Magewell, Martin Audio, Matrox ConvertIP, Medialon Showmaster, Mersive Solstice, Meyer Sound Galileo/GALAXY, and NETGEAR AV switch families. Microsoft, Milan, Multiple-vendor AVB, Packet Sender, and NDI protocol records stay descriptive unless a vendor source identifies a specific hardware relation. No package/provider/delivery/credential/worker authority is added.

**UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN**
No Batch 7 record claims authoritative Windows detection, coexistence, generic cross-vendor protocol compatibility, or release-to-device pairing. Account/licensing rights, signed payload identities, hardware firmware/project coupling, and actual workstation inventory require controlled-install or vendor evidence. These remain explicit `Unknown` evidence and are not schema blockers.

### CAT-024 — Phase 4 Batch 8 evidence and implementation

**VERIFIED FACT**
Batch 8 reviewed all twelve frozen manufacturers: NEXO, NovaStar, Nureva, OBS Project, Obsidian Control Systems, Open Sound Meter, Panasonic, Pingman Tools, Planar, Powersoft, Professional Wireless Systems, and QLC+ Project. Direct evidence scopes [NEXO software](https://www.nexo-sa.com/software/), [Nureva App](https://support.nureva.com/docs/get-started-with-the-nureva-app), [ONYX downloads](https://support.obsidiancontrol.com/Content/Support/Downloads.htm), [Planar WallDirector OS](https://www.planar.com/products/planar-walldirector/planar-walldirector-os/), [Powersoft releases](https://www.powersoft.com/en/armonia-official-releases), and [QLC+ downloads](https://qlcplus.org/download).

**DESIGN DECISION**
Batch 8 adds 23 descriptive Products, 6 release families, and 10 purpose-specific relations. Relations are limited to NEXO loudspeaker/controller, Nureva HDL, ONYX console, Panasonic camera/projector, Planar WallDirector controller, and Powersoft amplifier/controller workflows. NovaStar, OBS, Open Sound Meter, PingPlotter, Professional Wireless Systems IAS, and QLC+ remain descriptive where vendor evidence does not establish a specific device relationship. No package/provider/delivery/credential/worker authority is added.

**UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN**
No Batch 8 record claims authoritative Windows detection, side-by-side behavior, generic protocol compatibility, device firmware/project pairing, account/licensing rights, or installer/publisher identity. These retain explicit `Unknown` installed evidence and are not schema blockers.

### CAT-025 — Phase 4 Batch 9 evidence and implementation

**VERIFIED FACT**
Batch 9 reviewed all twelve frozen manufacturers: Rane Commercial, Rational Acoustics, RealTerm Project, Resolume, RF Explorer, Riedel Communications, Room EQ Wizard, Ross Video, RTS Intercoms, sACNView Project, Samsung, and ScreenBeam. Direct evidence scopes [Rane Halogen](https://www.ranecommercial.com/halogen), [Resolume downloads](https://www.resolume.com/download/), [ClearWaves](https://rfexplorer.com/clearwaves/), [RTS apps and tools](https://rtsintercoms.com/support/apps-and-tools/), and [ScreenBeam CMS Enterprise](https://www.screenbeam.com/products/screenbeam-cms-enterprise/).

**DESIGN DECISION**
Batch 9 adds 27 descriptive Products, 6 release families, and 11 purpose-specific relations. Relations are limited to Rane HAL processors, RF Explorer analyzers, Riedel Artist/Bolero, RTS matrix/OMNEO systems, Samsung LED signage, and ScreenBeam receivers. Rational Acoustics, RealTerm, Resolume, Room EQ Wizard, Ross Video, and sACNView remain descriptive where official evidence does not establish a specific hardware relation. No package/provider/delivery/credential/worker authority is added.

**UNRESOLVED / REQUIRES PHYSICAL INSTALL OR VENDOR LOGIN**
No Batch 9 record claims authoritative Windows detection, side-by-side behavior, generic protocol compatibility, installer/publisher identity, firmware/project pairing, or account/licensing rights. These retain explicit `Unknown` installed evidence and are not schema blockers.
