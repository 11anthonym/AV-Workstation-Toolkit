# C# migration architecture contract

Status: authoritative migration contract and completed compiled-runtime record. Phase 14 completed stabilization and retired the legacy application runtime from shipping.

## Scope and reference behavior

Phases 1-12 incrementally characterized and proved the typed Domain/Application core, Windows providers, compiled WPF presentation, request/IPC lifecycle, independent worker, exact-ID WinGet executor, and vendor/credential boundaries. Phase 13 made that compiled stack the production default. Phase 14 completed stabilization and removed the PowerShell/WPF implementation, worker, and launcher vendor bridge from the packaged runtime. Legacy sources remain only as repository characterization and tooling evidence.

When documentation and code disagree, the observed shipping behavior is characterized before any decision. A parity mismatch is evidence to investigate, not permission to relax either implementation.

## Current runtime trace

1. The self-contained, single-file `AVWorkstationToolkit.exe` bootstrap resolves the canonical `%LOCALAPPDATA%\AVWorkstationToolkit` root, rejects elevated normal startup, validates embedded resources, and repairs a deterministic version-scoped runtime cache.
2. Normal launch hosts the compiled C# WPF `App` in-process. The App loads validated catalogs, typed WinGet/registry/reboot evidence, compiled vendor services, and sanitized diagnostics through Application/Domain policy.
3. Selection stays in the ViewModel; authorization is rebuilt from validated plan state. A confirmed managed action is atomically persisted as a strict request below `logs\requests`.
4. The App starts only the hash-pinned, product-identified `worker\AVWorkstationToolkit.Worker.exe` from the versioned runtime using fixed production arguments and `UseShellExecute=false`.
5. The standard-user compiled worker validates path/schema/identity, rebuilds live state before every package, enforces action/hold/risk/reboot policy, invokes one exact managed WinGet ID at a time, and requires fresh post-action verification. Progress, result, cancellation, and logs remain correlated by request identity.
6. External records remain outside the worker. Compiled HTTPS/SFTP/Credential Manager/cache services supply only catalog-authorized handoffs; downloaded payloads are not execution authority.
7. Verification, diagnostics, and smoke modes remain bounded bootstrap operations. No legacy application-runtime fallback exists.

The process boundary, not the lifetime of a WPF window, owns an active worker or installer operation. A compiled replacement must not regress that property or substitute process-name polling for explicit request/result ownership.

## Target source organization

```text
src/
  AVWorkstationToolkit.App/
    App.xaml
    MainWindow.xaml
    ViewModels/
    Commands/
    Services/
  AVWorkstationToolkit.Domain/
    Catalog/ Packages/ Versions/ Planning/ Policies/ Risks/ Results/
  AVWorkstationToolkit.Application/
    Inventory/ Planning/ Actions/ Workers/ Vendors/ Diagnostics/ Abstractions/
  AVWorkstationToolkit.Infrastructure.Windows/
    WinGet/ Registry/ Reboot/ Processes/ Authenticode/ Paths/ Credentials/ Files/
tests/
  AVWorkstationToolkit.Tests/
  AVWorkstationToolkit.IntegrationTests/
  parity/
  fixtures/
```

Domain contains typed deterministic production logic. Application owns use cases and provider abstractions. Infrastructure.Windows implements production WinGet, Registry, reboot, Authenticode, vendor, file, credential, and constrained-process adapters. App is the conventional compiled WPF presentation/composition root with `x:Class`, ViewModels, and commands. The bootstrap hosts it and embeds the independently published worker.

## Dependency direction

```text
WPF App
   |
   v
Application  <---  Infrastructure.Windows
   |                 |
   +-------> Domain <-+
```

- Domain contains deterministic values, validation, comparison, planning, policy, risks, and results.
- Application coordinates use cases and defines ports required from Windows/provider infrastructure.
- Infrastructure.Windows implements those ports using Windows APIs and constrained processes.
- App composes dependencies and maps presentation state; it does not authorize packages.

Domain must not reference WPF, Registry, Process, HTTP, filesystem, Credential Manager, or PowerShell. Infrastructure supplies facts; Application and Domain policy grant or reject authority.

## Executable modes

The shipping bootstrap and packaged worker use these explicit modes:

```text
AVWorkstationToolkit.exe                 compiled C# WPF application
runtime\<version>\worker\AVWorkstationToolkit.Worker.exe --production ...
AVWorkstationToolkit.exe --verify ...    integrity/package verification
AVWorkstationToolkit.exe --diagnostics   bounded diagnostics
```

The worker is a separate production process, not an in-process UI service. The shipped `AVWorkstationToolkit.Worker.exe` accepts only `--production` with canonical packaged/data roots and request paths. Fake `--test-mode` and isolated `--live-rehearsal` activation live exclusively in the separately identified non-shipping `tests/AVWorkstationToolkit.Worker.DevHost` project; its fixed launch/root support lives in `tests/AVWorkstationToolkit.Development`. Neither project is referenced by a shipping assembly or release composition.

## Compiled WPF publishing constraint

The shipping compiled WPF application remains `net10.0-windows`, `win-x64`, self-contained, single-file, and explicitly untrimmed:

```xml
<UseWPF>true</UseWPF>
<SelfContained>true</SelfContained>
<PublishSingleFile>true</PublishSingleFile>
<PublishTrimmed>false</PublishTrimmed>
```

Any later trimming proposal requires WPF/reflection-specific evidence and package QA; it is not a default optimization.

## Security contract

### Authority and WinGet

- Requests are typed operations plus exact catalog IDs, never command strings.
- Only validated WinGet catalog records can be managed.
- External and awareness records never become worker packages.
- Each managed invocation is one package and includes `--id`, `--exact`, and `--source winget`.
- There is no import, `--all`, uninstall, arbitrary package ID, arbitrary argument, or generic execution route.

### Privilege and worker

- Normal App and worker execution are standard-user.
- User-writable application code is never preloaded into an elevated Toolkit process.
- Installer elevation remains normal Windows/UAC behavior.
- The worker independently parses strictly, revalidates live state, enforces holds and risk, re-plans between packages, and verifies post-state.

### Inventory and reboot

- Unknown, incomplete, unavailable, and package-specific errors remain distinct from not installed and current.
- Windows Update or CBS pending reboot permits low-risk work but blocks driver-, service-, and listener-bearing changes.
- Reboot state is reevaluated between packages.

### Windows read-only boundary

- WinGet is resolved only from the current-user registered `Microsoft.DesktopAppInstaller` package location, then checked for protected WindowsApps containment, regular-file/reparse safety, WinTrust validity, and Microsoft publisher identity. PATH, current-directory, `where.exe`, and configurable executable resolution are not used.
- The compiled process runner accepts a `WinGetReadOnlyOperation` enum, not an executable or command string. It exposes only fixed version, structured export, and update-list vectors with `UseShellExecute=false`, redirected bounded output, timeout/cancellation, and process-tree termination.
- Registry inventory reads HKLM 64-bit, HKLM 32-bit, and HKCU independently and returns per-source quality. One failed source cannot turn successful evidence into an all-source failure.
- Reboot infrastructure reports Windows Update and Component Based Servicing facts only. Domain policy, not Infrastructure, decides whether a package operation is allowed.
- These adapters cannot install, update, uninstall, import, execute installers, invoke PowerShell/cmd, or grant catalog execution authority.

### Files and providers

- Data and request paths remain contained, bounded, versioned, and reparse-safe; unknown request fields fail.
- HTTPS hosts, redirects, response sizes, hashes, signatures, and publishers remain bounded by policy.
- SFTP validates the pinned host key before credentials. Changed keys fail closed.
- Credentials remain isolated and redacted; secrets never enter arguments, environment variables, or logs.

### Compiled action-request and IPC boundary

The characterized shipping schema is version 1 with exactly `SchemaVersion`, `RequestId`, `Action`, `PackageIds`, `RiskAcknowledged`, and `DryRun`. Request IDs and direct-child filenames use `request-yyyyMMdd-HHmmss-<8 lowercase hex>.json`; payloads are limited to 65,536 bytes and 100 IDs. Only Install and Update are valid actions. The writer removes blank IDs while preserving the remaining submitted order and duplicates; authorization again removes blanks, deduplicates case-insensitively, sorts, and resolves each ID exactly once against the live validated plan. Request, progress, result, and cancel names share the request ID.

The compiled Application model serializes only this schema, applies strict JSON types, rejects unknown/duplicate/missing properties, validates IDs, and reuses Domain selection/reboot/risk policy against exact managed plan actions. One Infrastructure path policy derives request, progress, result, cancel, and WinGet-log paths exclusively from a valid RequestId beneath an explicit absolute root. It enforces `logs\requests` direct-child containment, exact extensions, bounded reads, regular-file and reparse checks, and request/body correlation.

Phase 7 originally introduced `ActionProtocolStore` as uncomposed infrastructure. The production App now supplies only the canonical contained LocalAppData root after typed authorization. The store accepts only an `AuthorizedActionRequest`, writes UTF-8 request bytes to a non-executable same-directory create-new temporary file, flushes them to disk, and atomically moves without overwrite to the canonical request name. Cancellation-marker creation is create-new, idempotent, and requires the correlated request file to exist. Tests continue to inject isolated temporary roots.

Shipping progress is append-only UTF-8 JSONL with five fields (`Timestamp`, `Level`, `Stage`, `PackageId`, `Message`), a 20 MiB UI read cap, and a retained incomplete trailing line. Shipping final results are schema 1 JSON with status, exit code, correlated request/progress/WinGet-log paths, and package outcomes. The compiled parser preserves complete-line streaming, split UTF-8 sequences, result/status/exit/verification semantics, and cooperative cancellation as intent/observation/final confirmation. It bounds individual progress records, text, arguments, cancellation markers, and final results; malformed complete records become explicit issues and never authority.

Phase 8 first proved the same strict request and IPC contracts in a separate test-only process. The production worker uses that orchestrator with the constrained real WinGet executor; tests supply `DeterministicFakePackageExecutor` only through the non-shipping worker development host. The Application orchestrator authorizes the complete request, then obtains and authorizes a fresh plan before every package; action changes, holds, unknown packages, risk acknowledgement changes, and pending-reboot risk become correlated Blocked results. The file protocol appends strict progress, writes one atomic no-overwrite final result, and observes cancellation markers cooperatively between packages.

The production `WinGetPackageActionExecutor` accepts only a typed already-authorized Install/Update package request, resolves WinGet through the Desktop App Installer path/signature/publisher policy, constructs the exact one-ID argument vector internally, captures bounded output with a 30-minute ceiling, and maps nonzero/timeout outcomes explicitly. The orchestrator obtains fresh, complete WinGet evidence after exit zero and reports Unverified unless Install is detected or Update is no longer update-eligible. Deterministic process tests inject a capturing host and never execute WinGet.

Phase 10 retains validated delivery fields on the typed catalog definition and requires them to create HTTPS or SFTP authorization. Infrastructure revalidates every HTTPS redirect, probes and compares SFTP host identity before credential lookup, scopes Credential Manager targets by host/port/username with legacy read/delete compatibility, and treats cache files as Downloaded until SHA-256, Authenticode, and publisher policy produce Verified evidence. These services have no installer/process API and are production-composed as constrained handoff services.

The production App atomically persists only authorized requests beneath the canonical data root and launches only the exact hash-pinned packaged worker. That production-only worker composes the real constrained executor; the separately built development host composes the fake executor for tests. Request/progress/result correlation, cooperative cancellation, and fresh verification remain the authority boundary.

## Dual-engine parity contract

`tests/parity/Invoke-Parity.ps1` sends each active fixture to:

1. a legacy adapter that invokes the shipping `Get-AVWorkstationToolkitPlan` and `Assert-AVWorkstationToolkitRequest`; and
2. a compiled adapter backed by the typed Domain implementation.

Both emit schema-versioned canonical JSON. Planning fields are:

`Id`, `Provider`, `Installed`, `InstalledVersion`, `AvailableVersion`, `Status`, `StatusDetail`, `ReasonCode`, `Risk`, `CanSelect`, `Action`, `DeliveryMode`, `InventoryQuality`, and `WorkerEligible`.

Strict fields are IDs, versions, status/detail, reason, risk, selection/action, delivery, inventory quality, and worker eligibility. The harness omits non-contractual timestamps, computer identity, elevation observation, and fixture-root paths rather than normalizing their differences away. Package order is fixture order. Unknown fixture fields fail both adapters.

`ReasonCode` is a harness contract introduced to keep future domain logic independent of prose. It is not a production request/result schema change. `CanSelect` records presentation eligibility, while `WorkerEligible` records independent request-policy authorization with risk acknowledgement supplied; their distinction is meaningful.

Phase 2 adds a second canonical fixture schema for strict numeric version behavior, version sort keys, managed/external/awareness catalog acceptance and authority, composed profile/priority/manufacturer/discipline/role/search/Quick View filters, and every current package status.

Phase 3 adds a third canonical provider schema covering structured WinGet export parsing, update-table parsing, per-source registry evidence, generic detector matching, supported reboot facts, and trusted-WinGet candidate policy. Both adapters consume the same deterministic records; neither launches a live process during parity. IDs, versions, source quality, failure categories, source counts, detection results, reboot reasons, and trust dispositions compare strictly. Volatile paths and signer details exist only in deterministic fixture form. `Test-CSharpReadOnlyIntegration.ps1` separately exercises the host without using workstation contents as a golden baseline.

Phase 4 adds a presentation fixture evaluated through the shipping PowerShell filter/controller semantics and the compiled C# ViewModel. Visible order, selected IDs, install/update counts, button enabled state and labels, selection summary, Quick View, and warning visibility compare strictly. ViewModel unit tests separately cover asynchronous refresh, stale-result suppression, source warnings, provider failure, checkbox selection retention, and the mutation refusal. A compiled-process smoke opens the deterministic WPF window, validates critical bindings and controls, and closes without provider or action execution.

Phase 5 adds a read-only-surface fixture for catalog details and diagnostics. It compares catalog identity, lifecycle, parent relationship, official URI metadata, verification/quarantine state, explicit unknown values, status/inventory quality, catalog counts, supported reboot reasons, and per-registry-source availability/counts. Runtime versions and workstation paths are deliberately supplied as deterministic fixture values or omitted from comparison. Official URI commands create validated intents only; they do not open a browser, download content, or grant delivery authority.

Phase 6 adds an action-request fixture evaluated by the shipping PowerShell request builder/parser/policy and the compiled Application boundary. The strict semantic comparison covers Install/Update, ordered one/multiple-package requests, dry-run and risk Boolean state, hostile JSON/schema/type/ID/size cases, exact planned-action agreement, authority, holds, risk acknowledgement, and pending-reboot risk blocking. Timestamps, random request nonces, JSON whitespace, and temporary test roots are omitted. The C# parser deliberately rejects duplicate JSON property names; Windows PowerShell 5.1 cannot expose duplicates after `ConvertFrom-Json` and keeps the last value. The compiled parser remains fail-closed, and no production request schema or shipping behavior was changed to manufacture parity.

Phase 7 adds 27 file/IPC lifecycle cases. Valid cases compare persisted request meaning and artifact identity; one/multiple/incremental progress records; successful, failed, cancelled, and dry-run results; cancellation intent versus observation; and idempotent duplicate final evidence. Hostile cases cover malformed/wrong-typed/unknown/oversized progress and result data, foreign package or request identity, nested/wrong-extension/foreign-ID paths, reparse evidence, and progress after correlation failure. Request timestamps, temporary roots, atomic temporary names, computer identity, JSON whitespace, and formatting are not compared. Request ID, artifact kind, record/issue counts, result status, lifecycle/cancellation state, and package IDs remain strict.

## SignPath parallel workstream

The completed migration does not imply Foundation acceptance. A fail-closed repository SignPath workflow now prepares this production chain:

```text
reviewed source/tag
  -> GitHub-hosted build
  -> verified unsigned EXE
  -> SignPath signs the exact EXE
  -> verify signed EXE
  -> build MSI and ZIP with those exact EXE bytes
  -> SignPath signs MSI
  -> verify signed MSI
  -> finalize SBOM, release manifest, provenance, checksums
  -> signature-required package QA
  -> publish exact tested bytes
```

Technical readiness and SignPath Foundation acceptance/configuration are separate. Organization IDs, project/policy/artifact slugs, tokens, and certificate details remain external future inputs.

## Observed documentation and implementation differences

- Current compiled geometry and UI-state checks pass at the four supported viewports, while the 2026-09-01 noninteractive capture returned unusable blank/white frames. Automated behavior remains proven, but no unavailable frame constitutes an interactive desktop review.
- Existing endpoint-security documentation calls the current PowerShell retention work “Phase 1.” That predates this C# migration phase; the term is historical context, not evidence of a compiled cutover.
- The shipping plan's `CanSelect` reflects package state, while pending-reboot risk enforcement occurs independently in request validation. The parity contract preserves both `CanSelect` and `WorkerEligible`; collapsing them would move authorization into presentation.
- Shipping `StatusDetail` is user-facing prose rather than a stable machine reason. The C# package state pairs it with a stable `ReasonCode`; the legacy adapter maps the same reasons for parity. This is not a production request/result schema change.
- Current WinGet 1.29 can emit update tables without a `Source` column and can append a second explicit-target table. Phase 3 characterization exposed that the first strict parser draft rejected this legitimate output; both adapters now parse column-aligned tables with or without `Source` and reject nonempty output without a validated table.
- The shipping request writer preserves submitted package order and duplicates, while `Assert-AVWorkstationToolkitRequest` removes blank IDs, deduplicates, and sorts before authorization. The C# factory preserves writer semantics and the C# authorization service applies the same case-insensitive deduplicate/sort boundary.
- The legacy Windows PowerShell 5.1 characterization parser accepts duplicate object properties and retains the last value. The production compiled parser rejects duplicates before materialization. This documented defense-in-depth difference is not permission to weaken C# validation.
- The legacy worker's PowerShell property-name comparisons are case-insensitive. The production compiled parser requires the six schema property names with exact casing and treats a differently cased name as unknown.
- The shipping writer accepts any nonblank package-ID string, but the complete shipping boundary later rejects IDs that do not resolve to the validated catalog plan. The compiled request boundary applies the catalog package-ID grammar earlier and still requires exact plan authorization.
- The shipping writer drops blank ID values before enforcing its nonempty count. The compiled factory rejects blank IDs immediately so callers cannot mistake discarded selections for a successfully encoded request; the complete valid-request semantics remain equivalent.
- The shipping request writer uses direct `Set-Content`, while the uncomposed compiled persistence primitive uses a same-directory create-new/flush/atomic-move pattern and refuses overwrite. This is intentional fail-closed hardening; it does not change the production writer in Phase 7.
- The legacy UI stopped waiting after 1,800 seconds while its PowerShell worker imposed no internal WinGet timeout. The production compiled mutation runner has a reviewed hard per-process ceiling of 30 minutes.
- The legacy characterization UI displays a malformed complete progress line as raw text and reads only `Level` and `Message`; the production compiled parser requires all five exact fields, validates package correlation, and reports malformed complete lines as typed issues. The compiled result reader likewise validates the full writer schema, paths, exit codes, package outcomes, and verification semantics. These stricter readers prevent evidence from being mistaken for authoritative success and are not normalized away.
- Shipping result JSON has no `RequestId` field. Correlation therefore uses the canonical result filename plus exact `RequestPath`, `ProgressPath`, and `WingetLogPath` values; adding a new field would be a schema change and was not done.

## Current shipping state

- **Current shipping architecture:** the single-file bootstrap hosts the compiled WPF App, which uses typed Domain/Application services and launches the exact independently validating compiled worker for authorized managed actions.
- **Production provider architecture:** typed WinGet/registry/reboot evidence, compiled vendor transport/Credential Manager/cache verification, diagnostics, browser/Explorer handoffs, request persistence, progress/results, cancellation, and fresh verification are production-composed.
- **Retired application architecture:** PowerShell/XAML/core/worker/vendor bridge files are not embedded or reachable from the launcher. Repository copies remain only for characterization, development, and operator tooling.
- **Compatibility retained:** documented pre-rebrand data, credential-target, and MSI upgrade compatibility remains independent of the retired runtime.

## Cutover and retirement rule

The Phase 14 retirement met the responsibility-specific matrix conditions, retained dual-engine characterization, and passed source/package/security review. Future cleanup of repository characterization sources requires an explicit review proving equivalent compiled coverage; “the C# version exists” is never sufficient.
