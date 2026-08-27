# Change Log

## 2026-08-26 — Cached-installer handoff clarity

- Corrected Explorer's `/select,<path>` argument construction so a verified cached installer is selected instead of Explorer falling back to the repository working directory.
- Renamed cached vendor handoffs to `Show cached installer` and added contextual tooltip/accessibility text clarifying that the action reveals a validated installer but does not verify the installed application or execute the file.
- Added a provider-wide delivery matrix covering official links, bounded HTTPS download, authenticated SFTP, bundled files, cached and uncached parent providers, awareness links, and inventory-only records.

## 2026-08-26 — Checkbox interaction correction

- Made the complete selection cell a consistent pointer target while keeping non-actionable catalog records visibly disabled and non-executable.
- Replaced binding-driven checked/unchecked synchronization with source-update synchronization, preventing DataGrid cell creation, recycling, filtering, or refresh from rewriting user selections.
- Added a regression that exercises the standard WPF automation toggle, verifies exactly one model update, and checks the minimum selection target dimensions.

## 2026-08-25 — Workstation research reconciliation

- Reconciled the external workstation-software report against the actual managed, operational-external, and awareness catalogs; no default profile or managed WinGet authority changed.
- Extended existing schema 3 metadata with bounded distribution policy, workflow categories, installation forms, provenance, verification dates, review triggers, and quarantine state while keeping omitted facts conservative.
- Added non-executable awareness for in-box Windows Pktmon and separate NDI Analysis, and quarantined NDI Remote as discontinued with no download action.
- Added reviewed metadata to Packet Sender, USB Device Tree Viewer, Tera Term, NETGEAR Engage, USBView, Sysinternals, and NDI Tools; refreshed the officially published Tera Term and USB Device Tree Viewer catalog versions.
- Added a prioritized research backlog for unresolved utilities, capture/video hardware, ST 2110/IPMX/PTP, display/projector fleets, legacy compatibility, and redistribution review.

## 2026-08-25 — Workstation selection reliability

- Fixed grid checkboxes so a user toggle is synchronized with the PowerShell-backed selection model instead of visually reverting without changing the planned action.
- Expanded the checkbox hit target, added accessible selection guidance for disabled rows, and added source and packaged workflow regressions for actionable and non-actionable items.

## 2026-08-25 — Publication and signing readiness

- Added publication-ready privacy, security, contributing, code-signing, and SignPath-readiness documents without making the repository public, publishing a release, submitting an application, or implying SignPath Foundation acceptance.
- Adopted the Apache License 2.0 for project source without changing third-party licenses or inventing a legal/publisher identity.
- Added a verified third-party dependency/license inventory that separates packaged runtime code from build/test tools, Windows platform integrations, commercial AV catalog metadata, and separately rights-approved offline payloads.
- Embedded the project notice plus the exact .NET runtime license/notices in the deterministic runtime and added a versioned third-party notice to the normal release set.
- Documented the actual privacy boundary: startup and manual refresh can perform bounded WinGet and configured operational vendor-release checks, while package downloads, browser handoffs, authenticated SFTP operations, diagnostics export, and software changes retain their documented operator gates.
- Strengthened release provenance by recording the selected SDK and actual .NET runtime/apphost, hashing the release manifest, publishing explicit checksum coverage, verifying the MSI-contained executable against the standalone artifact, and refusing replacement of an existing tagged release.
- Documented the future public distribution boundary so catalogued vendor software, caches, credentials, logs, reports, diagnostics, snapshots, signing material, and the external installer depot cannot be mistaken for project release content.
- Expanded static publication/privacy regression coverage and public-project README/uninstall guidance without changing workstation inventory, planning, provider, execution, reboot, credential, or endpoint-security behavior.
- Prepared a publication-audited source tree for a fresh zero-history repository;
  authoritative catalog metadata and exported plans remain the package-validation
  sources, while machine-specific operational evidence stays excluded.
- Removed the stale pre-rebrand preview images from maintained source because
  they no longer represented the current identity or UI. Automated layout
  geometry remains covered; trustworthy interactive visual review is still a
  publication gate.

## 2026-08-24 — AV Workstation Toolkit 1.1.1 rebrand

- Renamed AVinite to AV Workstation Toolkit across the desktop UI, launcher, PowerShell modules, build pipeline, packages, documentation, release provenance, and repository identity.
- Preserved the released MSI upgrade family so the renamed 1.1.1 package upgrades an installed 1.1.0 package instead of creating a second product.
- Added bounded, idempotent migration from the legacy LocalAppData identity and read compatibility for legacy SFTP Credential Manager targets; runtime scripts are always restored from current signed/hash-verified resources rather than migrated.
- Corrected inaccurate organizational attribution in launcher, MSI, PowerShell module, SBOM, install-path, documentation, and release metadata while keeping the released MSI UpgradeCode explicit and unchanged.

## 2026-08-22 - AV Workstation Toolkit 1.2-style workstation-manager foundations (unreleased)

- Added an endpoint-security behavior baseline, bounded process-launch contract, centralized Explorer/HTTPS/worker process handling, and static regression checks against encoded/bypass, security-tampering, proxy-binary, generic-shell, temporary-script, and packer patterns.
- Made embedded runtime behavior quieter and release identity more conventional: unchanged versioned runtime files are not rewritten, packaged PowerShell no longer carries a redundant hidden-window argument, the self-contained EXE is uncompressed, and Windows version resources now carry reviewed product/publisher/copyright metadata.
- Added deterministic CycloneDX 1.6 SBOM output, locked NuGet vulnerability auditing, schema-v3 release provenance/signature metadata, RFC3161 EXE/MSI signing through validated Windows SDK tooling, production-channel signing enforcement, and safe optional Microsoft Defender release-directory scanning.
- Removed version/build, WinGet, and normal privilege telemetry pills from the main header; product version and package mode now use a dark About dialog while runtime and WinGet data remain in Diagnostics.
- Added stable user-driven sorting for Application, Vendor, logical Priority, attention-ordered Status, numeric Versions, and operational Risk, with visible direction and persistence across filters and refresh.
- Changed Missing apps and Available updates into visibly active, composable quick views that select only eligible actions; added All apps and kept Clear selection independent from view, filters, and sorting.
- Added a conventional File/View/Tools/Help menu, moved logs and plan export out of the primary action bar, and made the full safety explanation discoverable through a read-only Help dialog.
- Replaced technical pending-reboot banner language with plain guidance and renamed `Recheck` to `Check again` without changing risk-sensitive reboot enforcement.
- Removed verbose safety-policy prose from the sidebar, retained intentional sidebar scrolling, and changed purpose/restriction cells to compact two-line wrapping with full text in tooltips and application details.
- Made external uninstall inventory source-aware across HKLM 64-bit, HKLM 32-bit, and HKCU so one failed source no longer poisons unrelated packages; incomplete coverage, unavailable inventory, and package-specific malformed versions now have distinct status semantics.
- Added a read-only sanitized Diagnostics view with copy/export, launcher/runtime/WinGet/reboot/source health, catalog/status counts, and per-source external-inventory results.
- Added staged refresh observability for WinGet, external inventory, vendor release information, reboot state, and plan construction, with non-modal partial-failure guidance.
- Replaced the global pending-reboot stop with a shared risk-sensitive policy: low-risk applications may continue under a warning, while driver-, service-, and listener-bearing actions remain blocked and are revalidated between packages.
- Changed `global.json` to the documented stable .NET 10 `10.0.100` plus `latestFeature` policy, added installed/selected SDK validation, and made release builds perform an actionable non-mutating locked restore before publishing with `--no-restore`.
- Completed a repository-wide maintainability and security pass without changing the 1.1.0 product version: removed personal workstation records and named-person metadata, reduced the planning engine's largest function from 374 to 178 lines, and centralized C# path containment.
- Added strict compiled-bridge JSON schema rejection, bounded streaming vendor-page reads with preflight redirect validation, content-hashed NuGet dependency locking, and immutable GitHub Action commit pins.
- Changed release cleanup to preserve output directories so builds work even when another shell uses the release folder as its working directory.
- Added a separately validated external-application catalog with uninstall-registry detection, numeric version comparison, bounded HTTPS release-page checks, and fail-safe catalog-baseline fallback.
- Added Q-SYS Designer Software LTS awareness using the official Q-SYS release page and the 9.13.2 LTS baseline.
- Expanded the active catalog to 29 exact-ID WinGet entries, 25 operational external records, and 278 non-deployable commercial AV awareness records while keeping VirtualBox excluded.
- Added the backward-compatible schema 3 metadata model with validated vendor, product family, discipline, role, priority, lifecycle, version policy/coupling, licensing, access, account/training requirements, system impact, platform, official-source, and validation fields. Unknown facts remain explicit rather than inferred.
- Added `Find-AVWorkstationToolkitCatalog` queries plus one composable desktop filter for search, profiles, catalog policy, dynamic manufacturer, and metadata-based discipline selection.
- Added deterministic manufacturer/role overlay tests as the domain seam for future role profiles and Field Kits without granting deployment authority.
- Reworked the WPF dark-control templates, responsive sidebar, DataGrid sizing, scrollbars, and action layout for 1040x760 through 1920x1080 viewports.
- Added a read-only selected-item detail surface for identity, installed state, access, compatibility, impact, evidence, and official HTTPS links without adding any execution path.
- Split the authoritative 278-record awareness catalog into 113 per-manufacturer source files and added a strict UTF-8 compiler that normalizes, rejects duplicates or weakened holds, and produces the single embedded runtime artifact. Release preflight fails on source/artifact drift.
- Migrated the self-contained, trimmed, compressed, single-file x64 launcher from .NET 8 to .NET 10.0.11 LTS with a stable .NET 10 feature-band policy, locked SSH.NET 2026.0.0 dependencies, runtime diagnostics, and unchanged embedded-resource/cache-repair behavior.
- Hardened organizational signing support across CurrentUser and LocalMachine stores with private-key, EKU, validity, timestamp, and post-signature checks; tagged CI can import an ephemeral external PFX and automatically require signed package QA without storing a key in the repository.
- Added official-source awareness for Atlona Velocity/AMS, Kramer K-Config/Network/K-Router Plus, Planar WallDirector OS, Ross DashBoard/Platform Manager, and LEA SharkWare/Web UI/Cloud without adding execution paths.
- Added broad commercial AV coverage across control, DSP/audio, AVoIP, Dante/Milan/AVB, RF, measurement, loudspeaker prediction, amplifier management, conferencing, PTZ, displays/projectors, signage/BrightSign, dvLED, intercom, media servers/show control, NDI/broadcast, lighting, network/serial/USB/EDID diagnostics, firmware utilities, servers, web services, embedded products, and installed-base legacy tools. Open Sound Meter is a P1 field utility.
- Modeled Toolbox, SIMPL Windows, VT Pro-e, Crestron Database, Device Database, Smart Graphics, and DM NVX Tool as independently detectable child applications that inherit the one secure MasterInstaller provider instead of duplicating credentials or download trust.
- Added read-only release awareness and official handoff for Biamp Tesira, Dante Controller, Shure Designer, Shure Wireless Workbench, Sennheiser Control Cockpit, Extron Toolbelt/PCS, and FileZilla, plus inventory-only Java and Dell/Waves state.
- Added manual official handoffs for Biamp Canvas 5.7.0 and Vocia 1.9.0, Sennheiser WSM 4.9.0, Shure Update Utility and Discovery 2.8.16, and optional legacy Microflex Wireless 1.2.0; Canvas remains Tesira-version-coupled and no hardware firmware action is automated.
- Added signed Biamp direct-download caching with page-version agreement, HTTPS redirect host allowlisting, bounded size, Authenticode publisher validation, atomic hash metadata, and revalidation before handoff.
- Added a compiled SSH.NET vendor bridge for Crestron's public MasterInstaller feed and authorized SFTP: seven curated product IDs, DTD/traversal/version/size controls, credential-free host-key probing, explicit first-use or replacement trust, and Credential Manager storage scoped by host, port, and username.
- Kept all external providers manual and non-executing. Cached and bundled installers are shown in Explorer only and never enter the WinGet action worker.
- Kept external applications permanently outside the automated WinGet worker; the UI now reports manual updates and opens either the official vendor page or a verified local package.
- Added rights-gated external package authoring, required schema 3 classification, SHA-256 and optional Authenticode publisher pinning, a git-ignored payload depot, and optional offline-bundle generation for software that may legally be redistributed.
- Kept Q-SYS vendor-managed because its current EULA restricts external redistribution; no Q-SYS installer is stored or packaged.
- Removed the generic pending-file-rename registry queue from reboot detection because stale updater cleanup entries could disable AV Workstation Toolkit indefinitely; explicit Windows Update and Component Based Servicing signals remain warnings and block risk-bearing actions.
- Extended the RealVNC Viewer hold to maintenance after a live update attempt confirmed WinGet still points 7.15.1.18 at a vendor ZIP that returns 404; installed copies can no longer be selected for that broken update.
- Explicitly excluded Oracle VirtualBox from the approved catalog after owner review; historical `vboxapi` evidence has no identified current consumer.
- Audited all 238 unique official product/download pointers, replaced dead vendor routes, corrected Color Expert LED from LG to Samsung, and refined current, transition, legacy, licensing, and public-access metadata where vendor documentation resolved earlier unknowns.
- Added deterministic catalog/provider/compiler/layout fixtures and packaged vendor-bridge self-tests; 100 full source checks, 94 CI-safe checks, 11 package checks, `dotnet format`, and targeted PSScriptAnalyzer all pass locally.
- Added four-viewport WPF geometry validation with explicit screenshot-quality detection. Geometry passes at 1040x760, 1280x860, 1440x900, and 1920x1080; the current noninteractive run rejected black fallback frames instead of claiming visual evidence, so final interactive control-state sign-off is still required before production publication.

## 2026-08-15 — AV Workstation Toolkit 1.1.0 standalone release correction

- Embedded the complete audited PowerShell/WPF runtime into the self-contained launcher so the release EXE runs without adjacent files or extraction.
- Added versioned runtime-cache restoration and SHA-256 verification beneath `%LOCALAPPDATA%\AVWorkstationToolkit\runtime`; changed cache files are repaired from embedded resources before launch.
- Added `Build-AVWorkstationToolkit.cmd` as the one-command fresh-clone build entry point and taught `Launch-AVWorkstationToolkit.cmd` to prefer its compiled output.
- Added a direct `AVWorkstationToolkit-1.1.0-win-x64.exe` release artifact, reduced the portable ZIP and MSI to the same standalone executable, and added a tag-driven release workflow that uploads all assets.
- Expanded package QA to run a lone copied EXE from an empty directory, verify ZIP byte parity and cache repair, and administratively extract the one-file MSI.
- Expanded source QA to 58 full checks and 53 CI-safe checks.

## 2026-08-11 — AV Workstation Toolkit 1.1.0 packaged application

- Added a trimmed, self-contained x64 `AVWorkstationToolkit.exe` launcher that requires no separately installed .NET runtime.
- Constrained the launcher to the audited Windows PowerShell 5.1 frontend, `RemoteSigned`, STA mode, and bounded diagnostics/smoke arguments.
- Added SHA-256 verification for every staged script, XAML, manifest, document, and launcher-adjacent runtime file before the frontend starts.
- Added a validated runtime-data resolver: packaged and portable runs use `%LOCALAPPDATA%\AVWorkstationToolkit`, while Git source checkouts retain repository-local evidence.
- Updated UI, terminal workers, snapshot defaults, and request containment to use the resolved data root without weakening reparse-point, schema, reboot, risk, or exact-ID controls.
- Added a pinned WiX 6.0.2 per-machine x64 MSI with Program Files deployment, major upgrades, Start-menu lifecycle, and clean Windows Installer validation.
- Added reproducible MSI/portable build automation, optional Authenticode signing, timestamping, JSON release metadata, and SHA-256 checksum output.
- Added package QA covering launcher version/integrity, packaged WPF control smoke, deliberate tamper rejection, portable contents, release hashes, and non-registering MSI administrative extraction.
- Added bounded package-test subprocess waits and an explicit hosted-CI mode that skips only interactive WPF smoke coverage while retaining all package integrity and MSI checks.
- Expanded source QA to 57 full checks and 52 CI-safe checks; added CI packaging and artifact publication.
- Preserved tracked v1.0.1 layout images as the last visually reviewed baseline; a fresh interactive-desktop v1.1 visual pass remains required before signed production publication.

## 2026-08-10 — AV Workstation Toolkit 1.0.1 review completion

- Replaced production installed-package console parsing with validated `winget export --include-versions` JSON and guaranteed cleanup of the unique temporary export.
- Kept update discovery fail-closed: empty, failed, or truncated update output disables the entire plan instead of enabling an unsafe action.
- Added structured-inventory fixtures covering installed versions, malformed JSON rejection, and both longest catalog package IDs.
- Consolidated project documentation beneath `docs/`, repaired internal links, and retained the root README as the repository index.
- Replaced the purpose-column star sizing that collapsed the other DataGrid columns during preview layout; the default view now shows every column and the minimum view retains explicit horizontal access.
- Updated the Windows CI action runtime and required a clean, annotation-free private-repository build before release.
- Expanded the completed suite to 54 full checks and 49 CI-safe checks, with 0 targeted PSScriptAnalyzer diagnostics.

## 2026-08-10 — Post-review hardening

- Restored `Stop after current` for every new action and added a behavioral smoke test for reusable cancellation.
- Made the pending-file-rename reboot signal require a non-empty operation while retaining the fail-closed reboot gate.
- Replaced full-file progress polling with shared-read, byte-offset streaming plus transient-read recovery; reduced source-side progress noise and humanized worker console output.
- Centralized action-request creation and worker startup in the versioned `AVWorkstationToolkit.Core` module.
- Failed the entire inventory closed when winget returns a truncated table and verified both longest catalog IDs against live exact-ID output.
- Removed unreachable elevated snapshot/readiness branches and clarified that privileged collections are intentionally out of scope.
- Derived the low-risk Standard winget baseline from the authoritative catalog and added agreement tests.
- Added a forbidden-product negative test, AST-based winget invocation guard, module manifest, pinned targeted PSScriptAnalyzer policy, and Windows GitHub Actions core QA.
- Tracked the reviewed UI preview images and excluded local Claude workspace data from Git.
- Expanded the full suite to 48 passing checks, the CI-safe core subset to 44 passing checks, and PSScriptAnalyzer to 0 diagnostics.

## 2026-08-10 — AV Workstation Toolkit 1.0 release candidate

- Fixed catalog refresh in long-lived or policy-hardened PowerShell hosts by explicitly loading and module-qualifying `Import-PowerShellDataFile`; added a fresh-process regression with implicit module autoloading disabled.
- Completed the AV Workstation Toolkit name/version pass across the launcher, WPF title and header, scripts, tests, and primary documentation.
- Added trusted resolution of Microsoft-signed `winget.exe` from Desktop App Installer under `Program Files\WindowsApps`.
- Added standard-user launch guards before any executable entry point loads local modules or XAML; installers retain their normal Windows UAC flow.
- Replaced permissive worker inputs with direct-child request containment, reparse-point rejection, a strict filename and 64 KiB limit, and a versioned/type-checked JSON schema.
- Revalidated live reboot and package state between packages in multi-package runs.
- Unified terminal deployment and maintenance under the same isolated action worker and post-action verification used by the desktop interface.
- Hardened catalog policy/schema validation and fail-closed update verification.
- Sanitized ANSI/OSC/control characters, redacted credential-like operational text, and bounded progress/result display sizes.
- Hardened standard-user snapshot collection against overwrites, PATH command shadowing, and obvious credential retention.
- Added horizontal table access at the minimum supported window size.
- Added a full security audit and expanded the dependency-free QA suite to 36 passing checks.
- Rendered and inspected default and 1040 × 760 previews; completed live read-only plans and a 33-success/0-failure snapshot integration run.
- Performed no application install, update, uninstall, reboot, or security/management change during finalization.

## 2026-08-10 — AV Workstation Toolkit desktop app manager

- Named the tool AV Workstation Toolkit and aligned its window, launcher, scripts, module, tests, and operator documentation under that identity.
- Added a local WPF package-selection interface with search, profile filters, per-app selection, status/version/risk display, activity output, plan export, and cooperative stop-after-current behavior.
- Added a shared catalog/planning module and isolated action worker that revalidates exact IDs, live state, reboot status, holds, and risk acknowledgement before each approved run.
- Added post-install and post-update verification with structured JSONL progress, final result JSON, and winget logs.
- Added fail-closed behavior when installed or upgrade inventory is unavailable.
- Added a dependency-free Windows PowerShell 5.1 QA suite; 27 of 27 automated checks pass.
- Added the frontend operator guide, architecture/safety model, QA report, and onboarding links.
- Rendered and visually inspected the WPF interface after correcting Windows PowerShell 5.1 encoding and DataGrid layout defects.
- Performed no install, update, uninstall, reboot, or security/management action while building and testing the frontend.

## 2026-08-10 — Initial catalog and safety model

- Replaced workstation-export replay with a curated, profile-based application catalog.
- Added plan-first deployment and maintenance with explicit change switches,
  reboot gates, and separate opt-ins for drivers, services, and listeners.
- Established the hard boundary excluding security, device-management, VPN,
  and corporate remote-support software.
- Added the reusable onboarding playbook, snapshot guidance, and Git exclusions
  for machine-specific evidence.
- Added package-level failure reporting and upstream holds for tftpd64 and
  RealVNC Viewer when their WinGet metadata or download assets proved unsafe.
