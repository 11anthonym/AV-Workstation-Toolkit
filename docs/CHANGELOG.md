# Change Log

## 2026-09-25 — Public-repository cleanup

- Updated the README, contributing guide, endpoint-security baseline, SignPath readiness record, security audit, and QA report, which still described the repository as private and unreleased. The contributing guide now welcomes issues and states that outside pull requests wait until `main` requires pull requests and the `core-qa` and `package` checks.
- When no reference-catalog update channel is configured, the status detail now says so without calling the build's source a private repository.
- Removed the retired PowerShell/XAML desktop host (`app/AVWorkstationToolkit.xaml`, `scripts/Start-AVWorkstationToolkit.ps1`, `scripts/AVWorkstationToolkit.Vendor.psm1`), the unused `scripts/AppProfiles.psd1` fixture, and the two core-module helpers only that host called. None was packaged or launched. Source QA drops the checks that read the host's source or loaded its XAML; each maps to existing compiled presentation, delivery, protocol, and smoke coverage. The window-title identity check now reads the shipping `MainWindow.xaml`. The packaged launcher still deletes these files from older runtime caches.
- The release build's reparse-point check now also covers `assets/` and `tools/`.
- Removed the terminal install and update path: `Invoke-AVWorkstationToolkitDeployment.ps1`, `Invoke-AVWorkstationToolkitMaintenance.ps1`, their PowerShell action worker `Invoke-AVWorkstationToolkitAction.ps1`, and the core-module request builder, request validator, worker launcher, WinGet install/upgrade argument builder, and post-action checks that only they used. The compiled app and its independently validating worker are now the only code in the repository that installs or updates software. The remaining repository scripts are read-only evidence, readiness, and catalog-authoring tools, and the onboarding playbook now uses the desktop app. Source QA drops the checks of the removed PowerShell path; the compiled request, authorization, reboot-policy, argument-vector, and worker-protocol tests cover the same behavior.
- Added bug-report and catalog-correction issue forms that warn against posting diagnostics, snapshots, or credentials, a security contact link to SECURITY.md, and a pull request template that asks for Windows validation results and the safety boundaries touched.
- Reorganized the documentation for public readers. The README now leads with what the app does, download and verification, requirements, a quick start, and a grouped documentation index; build, test, offline-bundle, and repository-map material moved to CONTRIBUTING.md. The completed C# migration records were removed (git history keeps them); their still-current layering, executable-mode, and signing facts moved into the architecture and safety model. The 1.1.1 security audit, packaging QA report, and Defender investigation moved to `docs/records/` as dated records. The workstation research backlog became the [catalog research issues](https://github.com/11anthonym/AV-Workstation-Toolkit/issues?q=is%3Aissue+%22Catalog+research%22+in%3Atitle), and its two standing rules joined the catalog change-control checklist, which no longer points to the retired decision register.

## 2026-09-24 — 1.1.1 Beta 1

- First public release: the unsigned beta `1.1.1-beta.1`, built on the `ReleaseCandidate` channel and published as a GitHub pre-release under the new [beta publication procedure](Packaging-and-Release.md#beta-publication). Its [release packet](releases/1.1.1-beta.1.md) covers verification, requirements, and known limitations.
- New installations start from signed reference catalog revision 3 (`2026.9.24.1`) instead of revision 1.

## 2026-09-24 — One official product page per product

- Every product now opens the same official page from Device Lookup and from the Software table, and a test keeps the reference catalog, vendor sources, and operational catalog in agreement. Extron PCS, for example, opens its /product/software/pcscs page everywhere, including its vendor-page handoff.
- Replaced generic, moved, and mismatched product links for 118 products: sign-in and software-index pages (Crestron, AMX), download-search results (AVer), docs pages that now return "Not found" (BrightSign), PDFs used as product pages (Audio-Technica, Mersive, Crestron), and pages for the wrong product (JBL Venue Synthesis, ChamSys MediaMaster, Matrox ConductIP). Components without their own page link to the parent application's page.
- Marked AVer EZManager 2 and PTZApp 2 discontinued and JBL Line Array Calculator legacy in the vendor sources, as their vendors state.
- Reference-catalog links reach users through the next signed reference revision; vendor-source and operational-catalog links reach users with the next application build.

## 2026-09-24 — RDL usability and verified catalog links

- Software details now list every model a documented device relationship covers, so RDL Console shows DD-RN31, DDB-RN31, and DDS-RN31 together.
- Corrected reference-catalog official and evidence links found by the 440-URL audit, including RDL Console, which now opens RDL's Console Software page instead of a D-BTN21 device page. These reach users through the next signed reference revision.
- Added Key Digital KD-MS8x8G-2 with its product-documented KDMS Pro relationships, marked KD-MS8x8G discontinued, and added Barco's current "Video wall Manager" name for WallConnect.
- Corrected embedded vendor-source links, mainly Extron software pages, and marked Barco XMS Edge discontinued. These reach users with the next application build.
- Resolved the remaining audit findings that official sources could settle. Symetrix Composer 8.5 is no longer labelled LTS, because Symetrix does not call it that. The Soundvision archive release family is removed, because L-Acoustics publishes no archived Soundvision versions. HyperDeck Setup now links to the HyperDeck Studio specifications that name it, and VLite now links to the 5-Series page that distributes VLite 3. Barco ClickShare Configurator is marked discontinued in the vendor source.

## 2026-09-23 — Reference software in the Software table and managed revision-1 baseline

- Device Lookup searches now list documented reference-only software, such as RDL Console for DD-RN31, as informational rows in the main Software table. These rows have no install, update, version, or selection state, and catalog apps keep their own rows.
- Added the RDL DD-RN31, DDB-RN31, and DDS-RN31 finish variants with their RDL Console configuration relationship, as published in signed reference-catalog revision 2.
- Embedded the owner-signed managed-catalog revision 1 (`2026.9.22.1`, key `avwt-managed-2026-a`) as the production offline baseline; the public managed feed is not yet published.
- Source builds keep signed reference-catalog revisions they cannot verify instead of deleting them, a lost accepted revision can be downloaded again, and tests and QA use isolated data roots instead of the user's profile.
- Corrected the packaged production smoke so its selection, detail, and diagnostics checks hold for the full production catalog on any workstation.

## 2026-09-21 — Independently updateable managed application catalog

- Added a separately trusted signed `avwt-managed` baseline and fixed-origin manual update path with strict content verification, atomic activation, one retained download, restart-bound selection, and offline fallback.
- Bound action requests to the application's effective managed revision; the compiled worker independently verifies and loads the same authority and rejects revision drift before package planning or mutation.
- Added the minimal managed-catalog status UI and moved supported source-checkout PowerShell deployment, maintenance, and worker paths to canonical `manifests/managed-applications.json` without adding a PowerShell signature engine.
- Kept production fail closed pending owner provisioning of the distinct managed public key, Revision 1 baseline/publication, and normal signed release validation.

## 2026-09-16 — WinGet table-schema parsing

- Parse package rows from the detected `Name | Id | Version | Available | Source` column schema, preserving opaque spaced version values such as `< 0.0.1` instead of inferring cells from whitespace-token counts.
- Mirror WinGet 1.29.290 Unicode display-width alignment for non-ASCII names, retain explicit tab-delimited compatibility, and keep shifted, missing, trailing, truncated, duplicate-conflicting, or otherwise ambiguous rows fail closed.

## 2026-09-15 — Current WinGet update inventory

- Recognized current Source/no-Source, explicit-target, pinned, unknown-version, and blocked-upgrade output sections without treating summary text as package rows or skipping a later explicit-target table.
- Kept malformed rows and unfamiliar summaries fail closed, added current redirected-output fixtures, and retained bounded redacted line/section context for failed checks.
- Hardened quoted-credential redaction in process diagnostics; package, worker, vendor, and execution authority remain unchanged.

## 2026-09-14 — WPF responsiveness hardening

- Indexed immutable package search text once per refreshed plan, removed repeated preset-set allocation, and cached package sort keys while preserving the 175 ms debounce and newest-search protections.
- Kept stable package/compatibility collection bindings, reduced plan and result updates to bounded collection resets, batched bulk selection state, and coalesced action/activity presentation work.
- Enabled reviewed recycling row/column virtualization, limited grid column recalculation to actual layout-mode transitions, and added a 360-package dispatcher-latency regression covering typing, filters, quick views, sorting, and refresh overlap.

## 2026-09-14 — Plain-language application copy

- Reworked the compiled UI around technician tasks and outcomes, replacing implementation terms such as worker, handoff, evidence, and payload in routine status, action, download, diagnostics, catalog-update, and detail text.
- Corrected unknown-state presentation so a detected app with no version says `Version unknown`, failed inventory says `Couldn't check`, and only confirmed absence says `Not installed`.
- Added specific warning explanations and next steps, route-aware package buttons, per-operation system-impact confirmation, readable detail labels, and regression coverage without changing package, vendor, credential, worker, or execution authority.

## 2026-09-14 — Actionable inventory warnings and diagnostics

- Replaced the generic inventory-warning banner with check-specific severity, impact, sanitized technical detail, `View details`, and `Check again` actions; pending restart and provider failures can no longer hide one another.
- Added readable diagnostic issue cards that explain what failed, what remains usable, the safe next action, and the underlying detail while retaining copy/export of the full sanitized snapshot.
- Prevented a failed or malformed WinGet update check from presenting installed managed applications as `Current`; their state is now explicitly `Check unavailable` and non-actionable until update evidence succeeds.

## 2026-09-13 — Responsive compiled search

- Moved package and compatibility search off the WPF typing path behind a 175 ms cancellable debounce, with newest-query-only dispatcher updates and a small accessible search status.
- Added immutable compatibility search indexes, a single combined search pass, direct matched-relation counts, and a 24-result live presentation bound without changing ranking or execution authority.
- Invalidated and immediately recomputed pending searches when filters, profiles, presets, quick views, sort order, or refreshed package snapshots change, preventing stale background results from restoring obsolete rows.
- Debounced first-character and clear-query row rebuilding, removed a quadratic package-row remap, avoided identical result-list replacement, and applied completed searches below input/render dispatcher priority so queued typing remains responsive.
- Fit the initial compiled WPF window to the current monitor work area, including secondary-monitor coordinates and smaller taskbar-constrained viewports, so native title-bar controls remain reachable.

## 2026-09-13 — Windows PowerShell 5.1 release-path compatibility

- Replaced the unavailable `.NET Path.IsPathFullyQualified` release check with a Windows PowerShell 5.1-compatible strict drive/UNC path policy while preserving baseline reparse, extension, size, and fail-closed validation.
- Added an actual `powershell.exe` regression covering accepted drive/UNC inputs and rejected relative, drive-relative, root-relative, malformed, and device-namespace paths.

## 2026-09-13 — Production reference-catalog public trust handoff

- Compiled the owner-supplied ECDSA P-256 public key under the fixed `avwt-catalog-2026-a` identity and enabled the exact GitHub Pages metadata/signature channel in production composition.
- Added production-key identity, curve, fingerprint, and unrelated-signature rejection coverage while keeping all test signing keys isolated from production configuration.
- Documented the owner-only Revision 1 publisher command; no production private key was accessed and no production catalog was signed or published.

## 2026-09-12 — Offline-first reference-catalog lifecycle hardening

- Added production signed-embedded-baseline packaging, deterministic highest-compatible local selection, app-downgrade retention, quiet 24-hour background freshness checks, and offline import parity.
- Serialized per-user catalog mutations, recovered complete interrupted activations, bounded stale staging/quarantine/obsolete compatible snapshots, and kept valid-but-incompatible signed catalogs out of quarantine.

## 2026-09-12 — Public reference-catalog feed preparation

- Created the separate public, distribution-only `11anthonym/AVWT-Catalog` GitHub Pages feed with immutable revision layout checks and no signing keys or application source.
- Fixed the compiled production metadata/signature URLs and exact `11anthonym.github.io` host while retaining fail-closed behavior until the owner supplies the real ECDSA P-256 public key.

## 2026-09-12 — Signed reference-catalog publisher

- Added the private-side catalog publisher with deterministic snapshot/channel metadata, explicit change-risk acknowledgement, runtime-verifier round trips, and no remote publishing or operational authority.

## 2026-09-12 — Signed reference-catalog update boundary

- Added a compiled ECDSA P-256 signed `.avwtcatalog` verifier, strict manifest/channel schemas, bounded HTTPS and ZIP handling, anti-rollback revision chaining, atomic per-user activation, startup revalidation, quarantine, previous-revision recovery, and embedded fallback.
- Added `Help > Catalog updates` with asynchronous check, update, and signed offline-import commands; descriptive catalog updates remain unable to change package, download, credential, worker, or execution authority.
- Left production online updates fail-closed and unconfigured until the owner supplies a public unauthenticated HTTPS origin and externally protected catalog signing keys; the private repository is not used as a credentialed desktop feed.

## 2026-09-05 — SignPath release-path preparation

- Replaced tagged CI's secret PFX import with a least-privilege, immutable-pinned GitHub Actions to SignPath workflow that signs the worker, deep-signs the launcher inside the MSI, and signs the MSI envelope.
- Added reviewed worker/MSI artifact-configuration contracts and fail-closed external signed-artifact ingestion with exact signer and RFC3161 timestamp verification.
- Preserved exact launcher bytes across the direct EXE, portable ZIP, and MSI while generating final provenance only after signing.

## 2026-09-04 — AV software compatibility catalog Batch 3

- Completed the frozen Batch 3 evidence pass for Audinate, Audio-Technica, AV Stumpfl, AVer, Avolites, Barco, Blackmagic Design, Bose Professional, BrightSign, Brompton Technology, BSS, and Capture Visualisation.
- Added read-only product, release-family, alias, and evidence-backed device/software records while preserving explicit `Unknown` local installed-version evidence.
- Corrected selected catalog links to more direct official vendor destinations without changing package, delivery, or worker authorization.

## 2026-09-04 — AV software compatibility catalog Batch 2

- Completed the frozen Batch 2 evidence pass for 7thSense, Adamson, AFMG, AJA Video Systems, Alcorn McBride, Allen & Heath, AMX, Analog Way, Angry IP Scanner, Ashly Audio, AtlasIED, and Atlona.
- Added read-only product, release-family, alias, and evidence-backed device/software records while keeping local installed-version evidence explicit as `Unknown`.
- Preserved every existing package, delivery, and worker authorization boundary; compatibility data remains descriptive only.

## 2026-09-01 — Release-candidate activity follow control

- Added a default-on **Follow latest activity** control so live refresh and action output remains visible while allowing operators to pause the view and inspect earlier entries.
- Added rendered compiled-WPF coverage proving both automatic follow and paused scroll-position behavior.

## 2026-09-01 — Custom Windows application identity artwork

- Added a project-specific AV signal-path mark with a transparent PNG master and a multi-resolution Windows ICO.
- Applied the same identity to compiled WPF windows and taskbar presence, the standalone executable, Start-menu shortcut, and MSI Installed Apps entry.
- Added source, rendered-WPF, executable, and installer regressions so generic or inconsistent application icons cannot silently return.

## 2026-09-01 — Compiled WPF interaction parity restored

- Restored label-only closed filter selections and an actual F5 refresh binding in the compiled WPF surface.
- Restored per-run risk acknowledgement state and command reevaluation while retaining independent worker authorization.
- Restored compiled Export plan, Open logs, Safety & Security, and About workflows without reintroducing PowerShell or generic process authority.

## 2026-09-01 — Compiled vendor workflow integration repaired

- Restored catalog-authorized online release evidence to compiled production planning, including strict Crestron MasterInstaller parent-catalog parsing for its allowlisted child products.
- Replaced the compiled `Get package` button's details binding with the typed vendor delivery workflow for validated official pages, verified HTTPS downloads, and host-key-first authenticated SFTP retrieval.
- Preserved manual-only external package authority: downloaded payloads must pass cache, hash, Authenticode, and publisher checks and are only revealed in Explorer; AV Workstation Toolkit does not execute them.

## 2026-08-31 — Production worker release boundary hardened

- Restricted the shipped compiled worker to its exact `--production` invocation and canonical production data/application roots.
- Moved deterministic fake/live-rehearsal activation and its fixed launch/root policies into separately identified non-shipping development projects while preserving existing process-boundary coverage.
- Added source, endpoint-trust, and package assertions that reject developer switches in shipping worker bytes and exclude the development host from EXE, MSI, ZIP, and extracted runtime composition.
- Reconciled the security audit and completed migration matrix with the compiled production architecture. Live mutation remains deferred because no safe eligible managed update was available.

## 2026-08-31 — Compiled runtime migration completed

- Retired the PowerShell-hosted WPF application, PowerShell action worker, and launcher vendor bridge from the embedded runtime, command-line surface, process policy, and release package.
- Added a strict managed-application JSON runtime catalog with exact parity to the reviewed authoring source, plus deterministic cleanup of only recognized stale legacy runtime files.
- Expanded compiled production smoke coverage for catalog loading, filters, Quick Views, sorting, checkbox selection, details, diagnostics, keyboard focus, viewport sizing, and reopen behavior without mutating workstation software.
- Preserved AVinite-era data and Credential Manager read/delete compatibility, the MSI upgrade family, standard-user operation, exact-ID WinGet authority, and all vendor/path/signature controls. Live mutation remains deferred until a naturally safe eligible update is available.

## 2026-08-30 — Production compiled runtime cutover

- Made the compiled C# WPF App, typed production providers/vendor services, canonical data root, and independent compiled worker the default packaged runtime.
- Embedded and hash-pinned the separately published worker, preserved exact-ID one-package WinGet authority and fresh verification, and recorded the compiled runtime/worker identity in release provenance and the SBOM.
- Retained PowerShell/WPF only behind explicit `--legacy-powershell-recovery`; no compiled failure silently activates it.
- Live mutation validation deferred because no safe eligible managed update was available. Executor/process/orchestration behavior remains covered by prior real-process and deterministic validation.

## 2026-08-30 — Compiled-stack live cutover rehearsal (non-shipping)

- Added an explicit standard-user-only live rehearsal composition under an isolated temporary root, connecting the compiled App coordinator, strict IPC lifecycle, independent compiled worker, real read-only providers, trusted WinGet resolution, and the real exact-ID executor boundary.
- Proved a correlated dry-run across the real process boundary, including GUI-handle release, fresh-store result recovery, trusted WinGet resolution, and post-completion plan refresh. No package was mutated because the workstation had no clearly safe eligible managed update; unrelated software was not installed merely to satisfy the rehearsal.
- Kept normal compiled preview, the shipping PowerShell startup/worker, installer and release composition, and vendor payload execution unchanged.

## 2026-08-30 — Compiled App action-flow integration (non-shipping)

- Integrated typed selection authorization, contained request persistence, the exact fake compiled worker, correlated progress/result display, cooperative cancellation, and post-completion refresh behind an explicit isolated migration-test root.
- Connected the compiled migration composition to the existing vendor delivery/credential/cache boundary, validated official browser handoffs, reverified-cache Explorer reveal, and sanitized diagnostics copy/export without adding installer execution.
- Added focused coordinator, concurrency, cancellation, failure, redaction, handoff, export, and real fake-worker process-boundary coverage. Normal production startup, the shipping PowerShell worker, release packaging, and real WinGet/vendor installer execution remain unchanged.

## 2026-08-30 — Compiled vendor delivery boundary (non-shipping)

- Added catalog-derived typed authorization for bounded HTTPS and authenticated-SFTP delivery, including explicit redirect host checks and host-key validation before credential lookup.
- Added scoped Windows Credential Manager access with legacy read/delete compatibility, contained vendor-cache paths, and separate Downloaded/Verified/Rejected payload states with SHA-256 and Authenticode publisher validation.
- Added deterministic transport, credential-ordering, cache-corruption, authority, and verification tests. No real vendor system was contacted, no payload was executed, and the compiled services remain outside App, worker, launcher, MSI, and ZIP composition.

## 2026-08-29 — Compiled WinGet execution parity (non-shipping)

- Added an uncomposed standard-user C# WinGet executor that accepts only typed Install/Update requests, reuses trusted Desktop App Installer resolution, and constructs exact one-package argument vectors internally.
- Added bounded direct-process result handling and fresh post-action plan verification so exit zero cannot become success without reliable installed/current evidence.
- Added focused dual-engine argument parity, injected process-boundary tests, and endpoint-trust regression coverage. No automated validation executed WinGet mutation, and production startup, worker, MSI, and ZIP composition remain unchanged.

## 2026-08-29 — Compiled worker orchestration parity

- Added a separate non-shipping compiled worker test host that strictly reads one canonical request, reauthorizes the complete request and every package against fresh deterministic plans, and writes correlated progress and final-result artifacts.
- Added only a deterministic fake executor with success, failure, verification-failure, and bounded-delay outcomes plus focused tests for holds, action/risk/reboot changes, unknown packages, sequencing, cooperative cancellation, and no-overwrite lifecycle behavior.
- Added four real process-boundary scenarios under isolated temporary roots. The compiled App, shipping launcher, production PowerShell worker, WinGet mutation, installer/elevation behavior, MSI, ZIP, and release composition remain unchanged.

## 2026-08-29 — Compiled IPC lifecycle parity

- Added non-shipping typed request/progress/result/cancel artifact identity, bounded strict parsers, cooperative-cancellation semantics, and a deterministic action lifecycle model.
- Added an explicit-root-only request protocol store with create-new, durable same-directory atomic persistence, no overwrite, bounded reads, and reparse/path correlation controls; all tests write only beneath isolated temporary roots.
- Added 27 PowerShell-to-C# IPC/lifecycle cases and endpoint-trust regression proving the compiled App still cannot persist live requests, launch a worker, or mutate workstation software. Production startup and worker behavior remain unchanged.

## 2026-08-29 — Compiled action-request parity

- Added a non-shipping typed C# action-request model, deterministic schema-only serializer, strict parser, read-only contained request-file policy, and plan-authority validation.
- Added 26 PowerShell-to-C# request cases plus focused hostile JSON, path, reparse, authority, held, acknowledgement, and pending-reboot tests; documented the stricter compiled duplicate-property and early package-ID rejection boundaries.
- Kept request persistence, worker launch/lifecycle, live revalidation, progress/results, WinGet mutation, production startup, launcher, and packaging on the unchanged shipping PowerShell path.

## 2026-08-29 — Compiled diagnostics and provider detail parity

- Added non-shipping typed, sanitized diagnostics that preserve provider quality, per-registry-source status, reboot reasons, runtime availability, catalog counts, and explicit warnings/errors.
- Retained validated catalog provenance/detail metadata in the C# domain and added compiled read-only detail/diagnostics windows with non-executing official URI intents.
- Added focused redaction, provider relationship, detail coherence, hostile URI, and compiled WPF smoke coverage plus PowerShell-to-C# read-only surface parity. No browser, download, vendor transport, install/update, worker, launcher, or package cutover occurred.

## 2026-08-28 — Compiled C# WPF presentation parity

- Added a non-shipping, conventionally compiled .NET 10 WPF App with ViewModel-owned read-only refresh, filtering, sorting, selection, status, and warning state.
- Connected the compiled presentation to the typed Domain/Application layer and Phase 3 read-only Windows providers without adding an install, update, worker, vendor-delivery, or generic process path.
- Added deterministic ViewModel/coordinator tests, PowerShell-to-C# presentation parity, and a compiled-process WPF smoke test. The shipping launcher, PowerShell UI, worker, MSI, ZIP, and release artifacts remain unchanged.

## 2026-08-28 — Read-only C# Windows infrastructure parity

- Added non-shipping typed providers for trusted Desktop App Installer WinGet
  resolution, bounded read-only process execution, structured installed/update
  inventory, source-aware uninstall-registry inventory, and supported reboot
  signals.
- Added 37 dual-engine provider fixtures plus an optional non-mutating live
  integration check; endpoint-trust QA now rejects any new generic C# process
  launcher or action-bearing WinGet vector.
- Corrected WinGet update parsing to accept current column-aligned output with
  an optional Source column and explicit-target table while malformed nonempty
  output fails planning closed. The shipping GUI, action worker, install/update
  execution, vendor delivery, and package entry point remain unchanged.

## 2026-08-27 — Typed C# domain/core parity

- Added non-shipping typed C# models and deterministic implementations for
  numeric versions, strict catalog normalization and validation, composed
  catalog queries, package planning/status, selection eligibility, and
  risk-sensitive reboot policy.
- Expanded dual-engine characterization to every current package status plus
  version, catalog-authority, hostile catalog, filter composition, and reboot
  policy cases; parity remains independent of live Windows/provider state.
- Kept the launcher, PowerShell-hosted WPF application, inventory providers,
  action worker, vendor transports, request schemas, and release packages on
  the existing production path.

## 2026-08-27 — C# migration harness and architecture contract

- Added a repository contract, target C# dependency architecture, and explicit
  PowerShell retirement matrix without changing the shipping runtime.
- Added non-shipping .NET 10 Domain, Application, Windows infrastructure, WPF,
  unit-test, and integration-test scaffolding with nullable analysis, warnings
  as errors, deterministic builds, and locked restore.
- Added strict dual-engine canonical JSON parity for representative WinGet
  states and risk-sensitive Windows Update/CBS reboot policy, plus staged
  deterministic fixture contracts for later migration phases.
- Kept the existing launcher, embedded PowerShell/WPF application, worker,
  catalog, vendor bridge, MSI identity, and release artifacts unchanged.

## 2026-08-26 — Selection-state synchronization correction

- Corrected the WPF `SourceUpdated` ordering boundary that could leave a row
  visibly checked while the PowerShell selection model, footer summary, and
  Install/Update buttons still reported no selection.
- Unified footer/button calculations and action execution around the same
  eligible selected-item query, and added a sanitized action-selection trace
  containing the exact package IDs presented to the constrained action path.
- Replaced isolated checkbox smoke coverage with a generated DataGrid-row flow
  covering select, deselect, combined Install/Update selection, low-risk actions
  during a pending reboot, disabled catalog records, and selection stability
  across refresh, filtering, sorting, recycling, and rebinding.

## 2026-08-26 — Release-readiness and endpoint-trust reconciliation

- Reconciled current private-GitHub, Apache-2.0, CI, branch-protection, release,
  and SignPath readiness statements without changing runtime behavior or making
  the repository public.
- Derived the tagged workflow's exact eight standard assets, documented that the
  checksum list covers the other seven but not itself, and added regression
  coverage against workflow/documentation drift.
- Documented that tagged Production publication fails closed without the
  existing organizational Authenticode/PFX secrets; any future unsigned first
  public artifact requires a separately reviewed release-candidate procedure,
  not weakened Production policy.
- Recorded the exact historical unsigned Defender
  `Trojan:Win32/Bearfoos.A!ml` specimen separately from a different-hash current
  development artifact that passed a supported Defender custom scan. No
  exclusion, policy change, quarantine restoration, evasion technique, or
  application behavior change was introduced.

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
