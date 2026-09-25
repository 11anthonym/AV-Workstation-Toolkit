# AV Workstation Toolkit 1.1.1 Security Audit

> **Dated record.** This is the security audit of the 1.1.1 release candidate, kept for provenance. Its statements about repository visibility and signing status were updated on 2026-09-25; its findings describe the audited build. Current boundaries are maintained in the [architecture and safety model](../AV-Workstation-Toolkit-Architecture-and-Safety.md) and [endpoint-security behavior](../Endpoint-Security-Behavior.md).

**Audit date:** 2026-08-26; compiled-runtime reconciliation: 2026-08-31; release-candidate reconciliation: 2026-09-01
**Scope:** standalone compiled launcher/WPF App, embedded-runtime extraction and verification, independent compiled worker, MSI/portable/offline-bundle packaging, WinGet, operational external and commercial AV awareness catalogs, normalized metadata and filtering, vendor release awareness, compiled HTTPS/SFTP/Credential Manager services, vendor cache, developer/QA tooling, logging, release artifacts, publication privacy, third-party notices, and release provenance
**Execution context reviewed:** self-contained .NET 10.0.11 compiled runtime, Desktop App Installer/winget, standard-user token; Windows PowerShell 5.1 only for retained build/QA and historical characterization

## Executive result

No open critical or high-severity code finding remains in the v1.1 implementation. The same independently runnable executable is delivered directly, through the per-machine MSI, and in the one-file ZIP. It restores and verifies its compiled WPF App, strict manifests/notices, and independently published compiled worker beneath the current user's LocalAppData directory before starting the App, and it refuses elevated startup. PowerShell-hosted WPF, the PowerShell action worker, and the launcher vendor bridge are retired from shipping. Unsigned artifacts do not provide publisher authentication.

This pass also separated broad commercial AV knowledge from operational providers. The combined catalog now has 338 records, but only the original exact-ID WinGet allowlist can reach a change request. All 308 external records remain on manual holds; 283 of them are the commercial awareness layer. Schema 3 validates licensing, access, distribution policy, workflow, installation form, lifecycle, provenance, verification age/quarantine, impact, platform, official source, and parent relationships without interpreting catalog knowledge as deployment approval.

The audit did not install, update, remove, reconfigure, or reboot anything. Live verification used read-only WinGet inventory, uninstall-registry detection, the official Q-SYS release page, and plan operations.

The publication-readiness pass did not change product functionality, make the
repository public, publish a release, submit a SignPath Foundation application,
or imply signing approval. The repository was private at the time; it became
public on 2026-09-24 with the unsigned `1.1.1-beta.1` pre-release. The project
is licensed under [Apache-2.0](../../LICENSE).

## Threat model

Protected outcomes:

- only catalogued user applications can reach a change command;
- exact package IDs and the `winget` community source are used;
- security, device-management, VPN, OS, unapproved runtime, VirtualBox, and corporate remote-support products remain outside the change scope;
- pending reboot state remains visible and blocks driver-, service-, and listener-bearing changes while allowing ordinary low-risk applications;
- driver, service, and listener impact requires a run-specific acknowledgement;
- action evidence does not unnecessarily retain credential-like values.

Threats considered:

- accidental bulk or wrong-package execution;
- malformed or edited request JSON;
- path traversal, nested request directories, and reparse-point redirection;
- PATH, alias, or function shadowing of `winget`;
- elevated execution of user-writable scripts or XAML;
- catalog-policy drift and duplicated CLI/UI logic;
- long-lived or policy-hardened PowerShell hosts with altered module-autoload state;
- failed inventory or failed post-update verification being treated as success;
- terminal escape/control-sequence and log-size abuse;
- credential-like values appearing in command metadata or installer output;
- an upstream winget manifest or installer becoming stale or compromised.
- malicious or malformed external-catalog JSON, version-page content, redirects, or regex input;
- path traversal, reparse-point redirection, hash drift, or signer substitution in an offline payload;
- lookalike HTTPS hosts, stale installer links, oversized payloads, or signer substitution in a direct download;
- hostile SFTP catalog XML, product/path substitution, SSH host-key changes, credential disclosure, or cross-user credential reuse;
- unauthorized redistribution of vendor software;
- a large awareness catalog accidentally expanding the executable allowlist;
- uncontrolled metadata values causing misleading access, licensing, lifecycle, or risk filters;
- duplicated child-provider credentials or trust settings weakening the common Crestron boundary;
- a web, server, mobile, or embedded product being misrepresented as an ordinary Windows installer.

Local administrators are outside the enforceable boundary because they can replace repository files, trusted binaries, or OS policy. Same-user tampering is reduced to same-user impact by the standard-user-only execution rule.

## Findings and disposition

| Severity | Finding | Disposition |
|---|---|---|
| High | A packaged launcher could become an elevation bridge if it accepted arbitrary scripts/arguments or ran with an administrator token. | Resolved. `AVWorkstationToolkit.exe` is `asInvoker`, refuses elevation, runs the compiled WPF App directly, embeds only reviewed manifests/notices and the hash-pinned compiled worker, and exposes only bounded production smoke/diagnostic switches. |
| Medium | Repository-local runtime writes are incompatible with Program Files and could mix mutable requests with installed code. | Resolved. One validated data-root resolver separates packaged runtime data beneath `%LOCALAPPDATA%\AVWorkstationToolkit`; source checkouts retain repository-local evidence. The worker containment boundary follows the resolved root. |
| Medium | A partial, stale, or modified package could load mismatched runtime components. | Resolved for accidental/cross-version drift. The executable carries the compiled App, strict manifests/notices, and exact compiled worker as reviewed build inputs/resources, rejects invalid or duplicate embedded paths and reparse-point cache paths, restores changed cache files, and verifies extracted SHA-256 hashes before use. Trusted publisher authentication still requires signing. |
| Medium | MSI shortcut and upgrade authoring could leave orphaned per-user directories or install side by side. | Resolved. Windows Installer validation passes with a per-machine Program Files package, major-upgrade policy, explicit Start-menu removal, and no warnings. |
| High | Repository scripts and loose XAML could previously be loaded by an elevated PowerShell process from a user-writable directory. | Historical finding, resolved. Shipping no longer packages or loads the PowerShell GUI, loose XAML, or PowerShell worker. The compiled launcher and worker independently reject elevated execution; individual installers may request UAC through their normal Windows behavior. |
| High | `winget` was resolved by command name/PATH. | Resolved. AV Workstation Toolkit resolves `winget.exe` from the installed `Microsoft.DesktopAppInstaller` Appx package, requires a protected `Program Files\WindowsApps` path, and validates a Microsoft Authenticode signature. |
| Medium | Worker containment accepted any file below `logs`, allowing nested paths and weak request provenance. | Resolved. Requests must be direct, non-reparse-point children of `logs\requests`, match a GUID-bearing filename format, and remain below 64 KiB. |
| Medium | Request JSON values were coerced and unknown properties were accepted. | Resolved. Schema version, request ID, action, string ID array, Boolean risk acknowledgement, and Boolean dry-run fields are type checked; unknown and missing fields fail closed. |
| Medium | Deployment and maintenance scripts duplicated execution/policy logic and could drift from the desktop worker. | Historical runtime finding, resolved by compiled cutover. Production requests now pass through the typed App coordinator and independent compiled worker. Retained PowerShell terminal scripts are non-shipping operator and characterization tooling. They are never launched by the packaged application, and the static process/AST audits in source QA continue to cover them; the dual-engine parity harness that formerly compared them to the compiled runtime was retired with the completed migration. |
| Medium | Update verification could report “current” after a failed inventory command. | Resolved. Verification now requires a successful exact, source-pinned inventory command before treating an update as current. |
| Medium | Catalog `Deployment` and `Maintenance` policy values and unknown fields were not fully validated. | Resolved. Top-level and package schemas, field character rules, package-ID syntax, policy enums, duplicate IDs, profiles, risks, and the exclusion regex are validated. |
| High | Expanding product knowledge could accidentally turn hundreds of vendor tools into approved automated installs. | Resolved structurally. The 283-record commercial AV manifest defaults to `Awareness`, every external record is forced to manual deployment and maintenance hold, and request validation accepts only eligible exact-ID WinGet plan actions. Awareness rows are regression-tested as non-selectable. |
| Medium | Free-form catalog metadata could collapse licensing into access, hide unknown facts, or produce unsafe filters. | Resolved. Schema 3 uses bounded text, HTTPS official URLs, validated enums, tri-state impact/requirement fields, separate licensing and access arrays, and explicit unknown values. Unsupported fields and values fail catalog loading. |
| High | Independently modeled Crestron applications could duplicate SFTP credentials, hosts, product scopes, or signer policy. | Resolved. Seven children must reference `Crestron.MasterInstaller`, use one allowlisted numeric product ID, and inherit the parent's host, feed, root, size, and publisher controls. The parser rejects missing, self, unknown, non-SFTP, or out-of-allowlist parents. |
| Medium | A multi-package request used one initial reboot/state decision for the entire run. | Resolved. The worker rebuilds and revalidates live state before every package after the first; a newly pending reboot permits only the next low-risk package and blocks a risk-bearing next package. |
| Low | Installer output could inject ANSI/OSC/control characters or grow the UI progress file indefinitely. | Resolved. Output is sanitized, messages are length-limited, credential-like values are redacted, and UI progress/result files have display-size limits. Raw change evidence remains local and Git-ignored. |
| Low | Snapshot command metadata could retain obvious password/token values, and development commands could be resolved from PATH during elevated collection. | Resolved. Credential-like values are redacted, uninstall command strings are no longer collected, native commands are resolved as applications, and all repository entry points refuse elevation. |
| Low | The legacy launcher used process-level `ExecutionPolicy Bypass`. | Historical finding, resolved before retirement; the compiled production launcher no longer starts PowerShell. Retained developer scripts use `RemoteSigned` in documented test/build commands and Group Policy still takes precedence. |
| Low | Legacy catalog refresh relied on implicit discovery of `Import-PowerShellDataFile`, causing availability failures in a long-lived host with missing Utility-module command discovery. | Historical finding, retained as characterization coverage. Production reads strict JSON manifests with unknown- and duplicate-field rejection; PowerShell authoring/compatibility tests still module-qualify the parser. |
| Medium | A truncated or source-unmatched winget ID could be interpreted as an absent package and offered for duplicate installation. | Resolved structurally. Production installed-package identity and versions now come from validated `winget export` JSON, not a console table; source-mapping warnings for otherwise-absent catalog packages also fail the plan closed. Update-table failure or truncation remains fail-closed, and the two longest IDs are regression tested through the structured path. |
| Low | Generic `PendingFileRenameOperations` entries could remain stale indefinitely and disable every action without an operator-resolution path. | Resolved. The generic rename queue is no longer a reboot input. Explicit Windows Update and Component Based Servicing reboot states remain non-overridable. |
| Medium | One failed uninstall-registry source could poison all detector-backed external applications. | Resolved. HKLM 64-bit, HKLM 32-bit, and HKCU are tracked independently; successful matches remain useful, incomplete coverage has warning semantics, total source loss is distinct, and malformed versions are package-local. |
| Low | Runtime and inventory troubleshooting required inspection of PowerShell output and local files. | Resolved. The compiled desktop exposes read-only sanitized diagnostics with per-source state, subsystem counts, copy, and export; it reads no credential values and grants no action authority. |
| Low | After one cancellation request, the Stop control stayed disabled for later actions in the same UI session. | Resolved. Its enabled state is derived anew for each active request and covered by a behavioral smoke test. |
| Low | The UI reread the full progress file on every timer tick and did not recover from a transient shared-file read error. | Resolved. It reads only new bytes with read/write sharing, preserves partial lines, retries transient failures, and bounds display size. |
| Low | Three legacy entry points duplicated request schema creation and worker launch behavior. | Historical finding, resolved. The production compiled App uses one typed coordinator for authorization, persistence, exact-worker launch, progress/result correlation, cancellation, and refresh; the worker independently validates again. |
| High | A configurable external package could become an arbitrary executable path into the worker. | Resolved. External packages are permanently manual, cannot be selected or submitted to the action worker, and are only shown in Explorer after validation. AV Workstation Toolkit never executes their payload. |
| Medium | A modified offline payload or path could be presented as trusted. | Resolved for the embedded-catalog boundary. Relative paths are constrained beneath `packages`, reparse points are rejected, SHA-256 is mandatory, and an optional exact Authenticode publisher is enforced before the delivery button is enabled. |
| Medium | Vendor release-page content could cause unbounded network or regex processing or authorize a downgrade. | Resolved. Only embedded credential-free HTTPS URIs are read with timeouts and redirect limits; content is capped at 2 MiB; regexes are bounded and timeout-limited; versions are numeric; and online data can advance but never lower the embedded baseline. |
| Medium | A third-party installer could be added without confirming redistribution rights or classifying its catalog impact. | Mitigated. The authoring command refuses to proceed without `RedistributionAuthorized`, requires schema 3 vendor and application-type metadata, keeps payloads out of Git, and records hashes/signers. Legal authority remains an organizational responsibility. Q-SYS is hard-coded as vendor-page delivery because its EULA restricts external distribution. |
| High | A direct-download URL or redirect could select an attacker host or a stale vendor installer and present it as current. | Resolved. The catalog requires a named version capture, exact version agreement, HTTPS at every hop, a DNS-host allowlist, bounded redirects and size, and a valid embedded Authenticode publisher rule before cache finalization. |
| High | Credentialed SFTP could send a password to an impersonated server. | Resolved. A credential-free probe obtains the host key first; first use and replacement require explicit UI trust, and authentication/download use fixed-time comparison with the saved SHA-256 fingerprint. A mismatch fails closed. |
| High | Vendor credentials could leak through process arguments, environment variables, logs, plans, or the legacy PowerShell/bridge transport. | Resolved. Production uses in-process typed C# vendor services behind a narrow Credential Manager abstraction; no bridge process or stdin request exists. Password references are cleared after use, operational errors are sanitized, and saving uses a Windows generic credential scoped to AV Workstation Toolkit, host, port, and username only after successful authentication. |
| Medium | A public SFTP catalog could use XML features, missing products, traversal, version/path substitution, or inflated downloads to escape the reviewed product set. | Resolved. DTDs are prohibited, content is capped, all seven curated product IDs are required, unknown IDs are ignored, numeric versions must appear in constrained `/software` `.exe` paths, and both feed-derived and global size bounds apply. |
| Medium | A cached vendor installer could be replaced after download or redirected through a reparse point. | Resolved. The cache is constrained beneath the per-user data root, rejects reparse points, finalizes only signed installer formats, records SHA-256 metadata atomically, and revalidates both hash and publisher before every handoff. |
| Medium | Vendor metadata responses were size-checked only after `Invoke-WebRequest` buffered the body, and automatic redirects were validated only after the destination had been contacted. | Resolved. A shared streaming HTTPS reader disables automatic redirects, validates same-host HTTPS destinations before following them, caps decompressed response bytes while reading, bounds redirects and timeouts, and is used by both release-page and SFTP-catalog retrieval. |
| Medium | The retired compiled vendor bridge accepted unknown JSON properties, allowing request-schema drift to pass silently. | Historical finding, resolved before bridge retirement. Its strict unmapped-member fix remains regression evidence; the current production App calls typed compiled vendor services directly and ships no vendor-bridge activation. |
| Medium | The production compiled worker still carried deterministic fake and live-rehearsal activation switches after runtime cutover. | Resolved post-migration. `AVWorkstationToolkit.Worker.exe` now accepts only the exact `--production` invocation. Fake/rehearsal modes and fixture loading live in the separately identified non-shipping `AVWorkstationToolkit.Worker.DevHost` project, while fixed development launch/root policies live in `AVWorkstationToolkit.Development`. Shipping assemblies do not reference either project, and release/package tests prove the development host and activation vocabulary are absent from EXE, MSI, ZIP, and extracted runtime composition. |
| Medium | Floating GitHub Action tags and an unlocked transitive NuGet graph weakened build reproducibility and supply-chain review. | Resolved. Workflow actions are pinned to full reviewed commit SHAs with checkout credential persistence disabled; the launcher commits a content-hashed lock file and enforces locked restore for CI builds. |
| Low | Launcher and vendor-download code duplicated absolute-path, child-path, and reparse-point checks. | Resolved. Both boundaries use one internal `SafePath` implementation, and a regression test rejects reintroduction of duplicate containment methods. |
| Low | Release cleanup deleted the output directory itself, so a shell using that directory could block every build. | Resolved. The build now preserves validated output directories and removes only their children; the package build passed while a separate process held the release directory as its working directory. |
| Medium | Release provenance did not hash its own release manifest, and an existing tagged GitHub release could be overwritten in place. | Resolved. The manifest is generated before the checksum list, the checksums cover the EXE, MSI, ZIP, Apache-2.0 license, third-party notice, SBOM, and manifest, and the tagged workflow refuses an existing release instead of uploading with replacement semantics. |
| Medium | The release manifest identified a Windows Desktop runtime pack that the compiled launcher does not carry and omitted the native apphost and selected SDK. | Resolved. Provenance now records the actual `Microsoft.NETCore.App.Runtime.win-x64` and `Microsoft.NETCore.App.Host.win-x64` 10.0.11 identities plus the selected stable .NET 10 SDK. |
| Medium | Publication material could omit runtime notices, overstate privacy, or imply that catalogued vendor products were redistributed. | Mitigated. The release publishes and embeds reviewed project/.NET notices, the privacy policy describes bounded automatic network checks, the Apache-2.0 root license is committed, and the normal distribution boundary explicitly excludes vendor software and workstation evidence. |

## Security controls verified

- 29-package exact-ID WinGet allowlist, 25 operational external records, and 283 awareness records, with cross-catalog duplicate detection and explicit VirtualBox exclusion;
- schema 3 enum, URI, unknown-state, licensing/access separation, impact, platform, and parent-provider validation;
- permanent forbidden-product guard;
- exact one-package `install` or `upgrade` arguments with `--source winget`;
- no bulk `--all`, import, or uninstall operation in the execution path;
- manual deployment and maintenance holds cannot be bypassed by risk switches;
- high-confidence Windows Update and Component Based Servicing warnings plus risk-bearing action gates at planning and during sequential execution;
- structured progress and final results with post-action verification;
- cooperative cancellation only between packages, avoiding forced installer termination;
- logs, reports, and raw snapshots excluded from Git;
- packaged launcher contains its self-contained compiled .NET WPF App, strict catalogs/notices, and exact compiled worker, performs only catalogued HTTPS/SFTP client operations, and exposes no PowerShell application runtime, inbound HTTP surface, service, driver, listener, or runtime package manager;
- external rows remain outside request JSON and the action worker;
- awareness records remain non-selectable even when they expose an official product link or registry evidence;
- verified offline payloads are hash-pinned, optionally signer-pinned, non-reparse-point files beneath the distribution package root;
- downloaded payloads require host/version/size controls plus Authenticode before atomic cache finalization and hash/publisher revalidation;
- Crestron credentials stay in Windows Credential Manager, while the public XML feed, product paths, host identity, and credential transport are independently constrained;
- high-confidence embedded-secret scan passed;
- the fresh source tree passed the same high-confidence token/private-key scan
  before its owner-reviewed zero-parent root commit was created;
- no reparse point or unexpected executable file was found in the repository during the audit;
- targeted PSScriptAnalyzer policy and host-independent Windows CI are part of the repository baseline;
- 238 unique commercial-catalog official product/download endpoints were audited
  after catalog authoring; 228 returned success or a redirect, seven official
  sites rejected the automated client with HTTP 403, and three current ETC pages
  rejected that client's TLS connection but were independently verified in ETC's
  official index. No unresolved 404 or 410 remains in that commercial-catalog
  set; the separately held RealVNC WinGet installer asset still returns 404 and
  remains outside automated maintenance;
- immutable GitHub Action commit pins, non-persistent checkout credentials, and a content-hashed NuGet dependency lock are enforced by regression tests;
- the tagged-release workflow rejects replacement of existing release assets;
- the release checksum list covers the distributables, versioned Apache-2.0 license, third-party notice, SBOM, and release manifest while excluding only itself to avoid a circular digest;
- current runtime dependencies and build/test tools are classified in the [third-party notices](../../THIRD-PARTY-NOTICES.md), while commercial AV catalog entries remain outside the software distribution boundary;
- documented automatic network behavior is limited to WinGet inventory/update metadata and configured operational vendor/parent release checks; downloads, browser handoffs, authenticated SFTP, and change actions retain their explicit operator gates;
- deterministic multi-viewport layout metrics and packaged WPF smoke behavior
  are retained while unreliable black-frame captures and stale preview images
  remain outside publication artifacts; tracked source and normal release
  artifacts contain no workstation assessment records, while local reports stay
  ignored and excluded.

## Residual risks and release conditions

1. **Unsigned release candidates.** The build can Authenticode-sign the launcher and MSI, but this repository contains no organization private key. Unsigned CI/local artifacts are functional release candidates, not publisher-authenticated production packages.
2. **Same-user and administrator integrity.** Program Files protects the MSI executable from a standard user, but a local administrator can replace it and a same-user process can alter the runtime cache, requests, or logs. Cache changes are repaired from the embedded executable on the next launch; this is not a security boundary against an attacker able to replace the executable itself.
3. **winget supply chain.** AV Workstation Toolkit validates the Microsoft client and pins the source and package ID, but still relies on winget manifests, publisher installers, hashes, and signatures. Holds remain necessary for stale or inconsistent upstream assets.
4. **No rollback.** A successful installer can still make package-specific changes that AV Workstation Toolkit cannot undo. Failures require review of the preserved result and winget log.
5. **Metadata sensitivity.** Logs, plan exports, and snapshots can contain device, path, software, and network metadata. Keep them in approved restricted storage.
6. **Pending reboot.** Generic queued file-renames are intentionally ignored because they are noisy and can persist after their originating updater has completed. Explicit Windows Update or Component Based Servicing reboot states remain prominent; ordinary low-risk applications may continue, but driver-, service-, and listener-bearing changes remain blocked until Windows is restarted and the plan is refreshed.
7. **Release provenance.** The repository starts from an owner-reviewed, zero-parent root commit with locked dependencies and a publication-audited source tree. It has a canonical public GitHub remote (public since 2026-09-24), successful hosted QA, and the `1.1.1-beta.1` pre-release tag. `main` blocks force pushes and deletion; pull requests and the `core-qa` and `package` checks are not yet required and must be required before outside contributions are accepted. Action SHAs and dependency locks must be deliberately reviewed when updated.
8. **Per-user evidence retention.** MSI uninstall deliberately leaves `%LOCALAPPDATA%\AVWorkstationToolkit` logs, reports, and snapshots. Retention or deletion remains an operator/governance decision outside the installer.
9. **Update metadata.** WinGet does not currently expose JSON for the compiled provider's `list --upgrade-available` query. Installed/missing decisions use structured export data, while update discovery retains guarded console parsing. Any empty, failed, or truncated update inventory makes the entire plan unselectable, so this residual can suppress an update but cannot authorize a duplicate install.
10. **External vendor-page drift.** A vendor can redesign a release page and break the embedded version pattern. AV Workstation Toolkit then retains the known baseline, reports the failure, and leaves delivery manual; a catalog update is required to restore live awareness.
11. **Redistribution authority.** Hash and signer validation establish file identity, not legal permission. The person creating an offline bundle must retain evidence that recipients may receive the software. Q-SYS must remain vendor-managed absent written QSC permission.
12. **Vendor identity rotation.** A legitimate vendor may rotate an SSH key, signing certificate subject, hostname, product ID, or account workflow. AV Workstation Toolkit intentionally blocks until the embedded trust policy is independently reviewed and updated; operators must not weaken it merely to restore availability.
13. **Credential lifecycle.** Windows Credential Manager protects a saved secret within the current Windows user's security context, but it does not replace organizational account controls, MFA, password rotation, vendor authorization, or endpoint security. The user can explicitly forget an AV Workstation Toolkit SFTP credential from the dialog.
14. **GitHub-native scanning.** The canonical public GitHub repository exists and its `core-qa` and `package` Actions jobs have passed. GitHub-hosted dependency, code, and secret-scanning coverage has not been independently established. Local dependency, source-secret, analyzer, compiler, package, and Defender checks therefore remain required.
15. **Open-source license.** AV Workstation Toolkit is licensed under Apache-2.0. Third-party components retain their independently documented licenses and obligations; the project license does not relicense them.
16. **Signing status.** Current development, release-candidate, and beta artifacts are unsigned and are not SignPath Foundation signed. The project is preparing an application only; acceptance, certificate issuance, workflow integration, and manual approval remain future external gates.
17. **Project reputation.** SignPath Foundation acceptance is discretionary and executable projects may need independently verifiable reputation. No technical control in this repository can satisfy or guarantee that non-code factor.

Publication policies and current operating boundaries are maintained in the
[privacy policy](../../PRIVACY.md), [security policy](../../SECURITY.md),
[code signing policy](../Code-Signing-Policy.md),
[SignPath readiness record](../SignPath-Readiness.md), and
[third-party notices](../../THIRD-PARTY-NOTICES.md).

## Verification evidence

- 124 of 124 full automated safety and regression checks passed at the 2026-09-01 release-candidate gate. The CI-safe subset remains a required hosted check; current results are maintained in the [QA report](AV-Workstation-Toolkit-QA-Report.md) and GitHub Actions.
- Package QA covers 15 checks for a lone EXE copied into an empty download directory, deterministic PowerShell-free runtime and compiled-worker extraction/repair, stale-runtime cleanup, EXE/MSI identity and byte parity, CycloneDX SBOM and schema-v3 release metadata, checksums, one-file ZIP parity, compiled-production open/reopen smoke, diagnostics, and non-registering MSI extraction.
- Endpoint-trust QA validates the five intentional child-process categories and rejects encoded or bypass PowerShell, security-control tampering, proxy-binary abuse, generic shell execution, temporary PowerShell staging, and packer integration in production sources.
- A synthetic non-vendor ZIP exercised the rights assertion, catalog authoring, payload depot, offline build, extraction, hash verification, and packaged WPF smoke path end to end; no third-party software was stored or executed.
- The self-contained, uncompressed .NET 10.0.11 launcher and WiX MSI compile with zero warnings; production-channel builds require verified RFC3161-timestamped Authenticode signatures while unsigned development builds remain explicit.
- Deterministic provider tests cover all 338 records, every normalized discipline, licensing/access/distribution composition, 283 commercial awareness holds, representative registry entries, direct-download lookalike/stale URLs, signed-cache tampering, and SFTP XML DTD/traversal/incomplete feeds.
- The official-source review corrected moved vendor pages, the Samsung ownership of Color Expert LED, current public access for LG LED Assistant, and transition/legacy status for Crestron D3 Pro, Barco WallConnect, Sennheiser Transmitter Manager, and Nureva Console Client without creating new executable download paths.
- Crestron regression tests resolve all seven child versions through the one parent feed, exercise five representative child planning states, and verify inherited host, root, size, product, publisher policy, and permanent lack of managed execution authority.
- Microsoft Defender completed a custom scan of a rebuilt release directory with no threat detection. This result concerns different bytes from the separately recorded historical `Trojan:Win32/Bearfoos.A!ml` specimen and does not explain or invalidate that detection; see the maintained [Defender false-positive investigation](Defender-False-Positive-Investigation.md).
- Automated layout inspection loads 46 named controls and all 338 records, verifies the telemetry-free header, conventional menu, intentional sidebar scrolling, default-on activity following with an explicit pause, six domain-aware sortable columns, composed quick views, wrapped purpose/restriction access, and narrow-window horizontal access, constructs the read-only details, Safety & Security, About, and Diagnostics dialogs, and exercises the packaged WPF workflow at 1040x760, 1280x860, 1440x900, and 1920x1080. Geometry and UI-state behavior passed; the current automated frames were classified unavailable, so interactive visual review remains a production-release gate.
- PowerShell 5.1 parser validation passed for every retained developer/build/QA script and characterization module; none is a packaged application-runtime dependency.
