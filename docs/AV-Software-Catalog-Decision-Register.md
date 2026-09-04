# AV Software Catalog Decision Register

**Status:** Phase 1 reference research — 2026-09-04
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
