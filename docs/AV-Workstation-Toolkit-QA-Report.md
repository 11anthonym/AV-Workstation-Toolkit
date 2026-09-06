# AV Workstation Toolkit 1.1.1 Packaging QA Report

**Validation date:** 2026-09-01
**Target runtime:** Windows 10/11 x64, self-contained .NET 10.0.11 compiled WPF App and worker, Desktop App Installer/WinGet 1.29; PowerShell is repository build/QA/operator tooling only
**Change activity during QA:** No application install, update, uninstall, reboot, service, driver, listener, or security-management change

## Release result

Phase 14 completes the compiled-runtime migration. Package QA verifies the embedded worker identity/hash, five strict runtime manifests, deterministic extraction/no-rewrite/repair, recognized stale-runtime cleanup, compiled production open/reopen smoke, and unchanged single-file EXE/ZIP/MSI identity. PowerShell UI, worker, vendor bridge, and recovery switches are absent from the packaged runtime and process policy.

Interactive release QA runs all fifteen package checks. Hosted CI uses the explicit `-SkipDesktopSmoke` mode because an Actions runner does not provide a reliable interactive WPF desktop; it runs fourteen checks and reports the compiled-production desktop check as skipped. Launcher and MSI waits are bounded so runner-specific desktop or installer stalls fail with a diagnostic instead of consuming the full job lifetime.

AV Workstation Toolkit is functionally packaged for direct download but remains unsigned. It is preparing an application to SignPath Foundation; it has not been accepted or integrated. Windows may therefore show an unknown-publisher warning, and organization-authenticated distribution still requires an approved code-signing path. Workstation readiness is independent of packaging: explicit Windows Update and Component Based Servicing reboot states are prominent warnings, permit ordinary low-risk applications, and block driver-, service-, and listener-bearing changes. Generic queued file-renames are not treated as reboot states.

Technical QA does not authorize publication. AV Workstation Toolkit is licensed under [Apache-2.0](../LICENSE), but the repository remains private and no public release or SignPath submission was made during this pass. Owner approval, publication review, and the documented signing/release gates remain required.

## Packaging result

| Deliverable | Result |
|---|---|
| Direct-download `AV-Workstation-Toolkit-1.1.1-win-x64.exe` | Pass; carries AV Workstation Toolkit identity metadata, was copied into an empty directory, and ran with no companion files or separate .NET runtime |
| Portable ZIP | Pass; contains only `AVWorkstationToolkit.exe`, byte-identical to the direct release asset |
| Per-machine MSI | Pass; installs the same standalone executable under Program Files with Start-menu lifecycle and major upgrades |
| Embedded runtime integrity | Pass; required resources extracted and hash-verified; modified cache content automatically restored |
| Runtime data isolation | Pass; packaged data resolves beneath `%LOCALAPPDATA%\AVWorkstationToolkit`; source checkout remains repository-local |
| Third-party notices | Pass; reviewed project notices plus the exact .NET 10.0.11 license/notices are embedded and hash-verified, with a versioned notice published beside release artifacts |
| Legacy data migration | Pass; only recognized host trust, bounded logs, and hash-matching cache records migrate; arbitrary files and runtime scripts remain excluded; repeated runs do not rewrite state; reparse roots are rejected |
| MSI upgrade identity | Pass; the released UpgradeCode is preserved, 1.1.1 uses a new ProductCode, and the displayed product/shortcut identity is AV Workstation Toolkit |
| Release metadata | Pass; EXE/MSI/ZIP/notices/SBOM hashes, commit, dirty-tree state, selected SDK, actual runtime/apphost, architecture, channel, and signature state agree across the manifest-inclusive checksums and schema-v3 JSON manifest |
| Standard release assets | Pass; the workflow publishes exactly eight assets: EXE, MSI, ZIP, Apache-2.0 license, third-party notices, CycloneDX SBOM, release manifest, and checksum list; the checksum list covers the other seven and not itself |
| CycloneDX SBOM | Pass; deterministic schema 1.6 JSON identifies eight reviewed runtime/build components with license and distribution scope, including the actual .NET runtime/apphost, without local paths or usernames |
| Signing | Repository-side SignPath flow prepared but not executed; artifacts remain unsigned, Production fails closed without external configuration and approval, no private key is stored, and no SignPath approval is implied |
| Optional offline bundle | Supported for locally supplied redistributable payloads; every file is path-constrained and hash/signer verified before ZIP creation |
| Compiled vendor boundary | Pass; production composition uses typed Credential Manager, HTTPS, and SSH.NET/SFTP services without a launcher bridge; deterministic tests do not contact an authenticated vendor |
| Build reproducibility | Pass; NuGet dependency content is locked and audited for known vulnerabilities, Actions use immutable commit SHAs, release assets cannot be replaced in place, the manifest is checksum-covered, and a build succeeds while another process holds the release directory as its working directory |
| Catalog reproducibility | Pass; 113 authoritative vendor files compile to the tracked 281-record runtime artifact, and release preflight rejects source/artifact drift before clearing prior output |

## Automated coverage

| Area | Result | Evidence |
|---|---|---|
| Catalog schema, policies, unique IDs, profiles, risks, and forbidden products | Pass | 29 managed WinGet, 25 operational external, and 281 awareness records aggregate to 335 unique records |
| Official catalog source endpoints | Reviewed | Product facts and lifecycle/access distinctions use manufacturer pages; new coverage includes Atlona, Kramer, Planar, Ross Video, and LEA Professional |
| Structured installed-package JSON and malformed/truncated inventory rejection | Pass | Fixtures plus live read-only export |
| Schema 3 commercial AV metadata and queries | Pass | Validated disciplines, role/priority, lifecycle, licensing/access/distribution separation, workflow/form metadata, verification age/quarantine, provenance, account gates, impact, platform, official URI, explicit unknowns, and composed query filters across 306 external records |
| External registry detection, numeric version parsing, HTTPS vendor awareness, and fallback | Pass | Independent HKLM64/HKLM32/HKCU source fixtures, partial and complete failure, package-local malformed version, awareness isolation, and representative registry evidence |
| Bounded vendor metadata transport | Pass | Manual same-host HTTPS redirects, decompressed streaming byte cap, timeouts, and deterministic content fixtures |
| Direct-download host/version/signature/cache controls | Pass | Biamp fixtures, lookalike and stale-link rejection, signed Microsoft fixture finalization, and post-cache tamper rejection |
| Crestron parent provider, SFTP catalog, host trust, credential transport, and packaged libraries | Pass | Seven independently detectable children resolved through one feed; inherited policy, DTD/traversal/incomplete fixtures, scoped trust-store fixtures, static credential audit, and packaged bridge self-test |
| Offline payload traversal, schema 3 metadata, hash, signer policy, and redistribution gate | Pass | Deterministic payload tests plus a synthetic signed rights-authorized authoring run that preserves schema 3 |
| Exact package matching, holds, risk acknowledgement, and risk-sensitive reboot enforcement | Pass | Low-risk, driver/service/listener, and mid-run re-plan request-policy tests |
| C# migration architecture and semantic parity | Pass, production core with parity retained | Locked .NET 10 solution, 125 MSTest cases, 19 managed planning results, 36 focused Domain cases, 37 read-only provider cases, 4 presentation cases, 4 read-only details/diagnostics cases, 26 strict action-request cases, and 27 IPC/file-lifecycle cases compare with the retained PowerShell reference semantics |
| C# read-only Windows integration | Pass, production | Trusted Desktop App Installer WinGet resolved; installed/update inventory, all three uninstall-registry sources, and reboot detection were exercised without workstation mutation |
| Compiled C# WPF presentation | Pass, production | `x:Class` App/MainWindow plus details/diagnostics windows, MVVM bindings, deterministic compiled-process and packaged-production smokes, composed filters/sorting, selection retention, warning presentation, sanitized diagnostics, provenance detail groups, and validated official-link intents |
| Exact one-package WinGet arguments; no bulk/import/uninstall path | Pass | Argument tests plus PowerShell AST audit |
| Trusted Microsoft Desktop App Installer resolution and signature | Pass | Live read-only check |
| Standard-user guards | Pass | Compiled App/worker/launcher plus separately maintained operator scripts |
| Runtime data-root resolution and worker request containment | Pass | Explicit, environment, source, traversal, reparse, size, and schema tests |
| Credential redaction, terminal-control removal, and embedded-secret scan | Pass | Deterministic and static checks |
| Windows PowerShell 5.1 tooling and characterization parsing | Pass | Every retained PowerShell source plus legacy characterization XAML; none is packaged or launched by the application runtime |
| Embedded compiled frontend/worker | Pass | Static review plus isolated-download diagnostics, exact worker hash verification, strict manifests, deterministic cache repair/stale-file cleanup, and compiled-production open/reopen smoke |
| Endpoint-trust process and packaging policy | Pass | Five reviewed direct-launch categories across 97 scanned maintained files plus static rejection of encoded/bypass, security-tampering, proxy-binary, generic-shell, temporary-script, and packer patterns |
| Runtime cache stability | Pass | A second launcher verification preserves every extracted file hash and timestamp while tampered content is repaired on the next run |
| Supply-chain metadata | Pass | Locked NuGet vulnerability audit, deterministic license-aware CycloneDX 1.6 SBOM, reviewed notices, schema-v3 release provenance, manifest-inclusive artifact hashes, and explicit signing/timestamp state |
| Publication privacy and history | Pass for current private source | The fresh source tree passed identifier/path review and Gitleaks 8.30.1; the canonical private GitHub repository begins at the reviewed zero-parent root, has no tags or releases, and current `main` has successful hosted QA |
| Strict compiled-bridge request schema and shared path containment | Pass | Static source checks plus hostile packaged request rejection |
| Compiled action-request boundary | Pass, production | Typed schema-only model; strict types/unknown/duplicate/schema/null/ID/size rejection; direct-child/reparse path policy; exact plan-action/authority/risk checks; duplicate IDs authorized once; live persistence is contained beneath the canonical production root and consumed only by the exact compiled worker |
| Compiled IPC/file lifecycle | Pass, production | Create-new/flush/atomic-move persistence, canonical artifact derivation, bounded incremental progress, strict result correlation, cooperative cancellation, independent worker lifetime, and restart recovery |
| MSI upgrade, Program Files, Start-menu cleanup, and standalone executable | Pass | WiX validation plus administrative extraction |
| Release hashes and portable contents | Pass | Package suite |
| Current rebuilt artifact malware scan | Pass | The 2026-09-01 release-gate build was scanned with the repository's supported Defender custom-scan path and reported no detection. The exact artifact hash remains authoritative in the generated checksum/release-manifest evidence and the gate output rather than mutable prose. This clean result applies only to the scanned bytes. |
| Historical Defender specimen | Recorded separately | SHA-256 `47DF422857F9A7B92902469ABB841E6E7942DD2978C0998548C00F7419AD95A8` at root commit `e5300e490f997301b8d6c7bfb51bcca3f46c2dae` was unsigned and classified `Trojan:Win32/Bearfoos.A!ml`; the current different-hash clean scan neither reproduces nor disproves that result. See [Defender false-positive investigation](Defender-False-Positive-Investigation.md). |

## Visual QA

Shipping automated WPF checks verify 46 named controls, all 335 catalog records, catalog/manufacturer/discipline composition, the File/View/Tools/Help menu and shared command wiring, the cleaned telemetry-free header, dark About identity, six domain-aware sortable columns and visible direction, numeric version ordering, real generated-row checkbox/model/footer/button agreement, Missing/Updates/All quick views, filter/sort/refresh/rebind selection persistence, intentional sidebar scrolling, closed ComboBox selected-value visibility, narrow-window horizontal access, compact purpose/restriction wrapping with full-text access, the read-only detail/safety/diagnostics surfaces including workflow/provenance metadata, and the packaged workflow smoke path. Deterministic layout metrics pass at 1040x760, 1280x860, 1440x900, and 1920x1080 with Missing, Updates, and All states represented. The compiled test composition adds a deterministic executable smoke for critical main-window bindings, actual checkbox toggling, default-on activity following plus paused scroll-position retention, details and diagnostics windows, official-link intent refusal, warning presentation, and mutation refusal; the separate packaged-production smoke validates the real compiled catalog/provider composition. These are structural validation, not an interactive visual sign-off.

The 2026-09-01 noninteractive capture attempt could start and resize the compiled production window, but the available Windows capture surface returned blank/white client frames. Those files are classified unavailable and are not visual evidence. Compiled rendering and behavioral checks still passed at 1040x760, 1280x860, 1440x900, and 1920x1080, but a trustworthy interactive review of default/minimum layout, ComboBox popups, selection, focus, disabled states, activity follow/pause, dialogs, and DPI transitions remains required before production publication.

The packaged compiled-production WPF open/reopen smoke is automated. It exercises catalog loading, search, filters, Quick Views, sorting, checkbox selection, details, diagnostics, keyboard focus, and minimum/normal viewport layouts without entering credentials, saving trust, downloading vendor software, or launching an installer.

## Commands

Full and CI-safe source suites:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Run-Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Run-Tests.ps1 -CoreOnly
```

Compiled runtime solution and retained semantic parity:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-CSharpMigration.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-CSharpReadOnlyIntegration.ps1 -NoBuild
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-CSharpWpfSmoke.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-CSharpAppReadOnlyIntegration.ps1
```

Build and package suite:

```powershell
.\Build-AVWorkstationToolkit.cmd
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-Package.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-EndpointTrust.ps1 -ReleaseRoot .\artifacts\release\1.1.1 -ScanWithDefender
```

Signed-release verification:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\build\Build-Release.ps1 -CertificateThumbprint <approved-thumbprint> -RequireSignature -BuildChannel Production
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-Package.ps1 -RequireSignature
```

## Release acceptance criteria

- All source and package checks pass under Windows PowerShell 5.1 as a standard user.
- Targeted PSScriptAnalyzer returns no diagnostics.
- Direct EXE, MSI, and one-file ZIP reproduce from the pinned .NET/WiX toolchain with matching checksums.
- A lone downloaded EXE launches with no adjacent payload, repairs modified cache content, and runs the WPF control/workflow smoke path.
- A trusted production release is Authenticode-signed and package QA passes with `-RequireSignature`.
- An interactive Windows desktop pass confirms default and minimum layouts before publication.
- A fresh readiness plan confirms no Windows Update or Component Based Servicing reboot before any application change wave.

Security findings and residual conditions are documented in [AV Workstation Toolkit Security Audit](AV-Workstation-Toolkit-Security-Audit.md). Packaging procedures are in [Packaging-and-Release.md](Packaging-and-Release.md).
