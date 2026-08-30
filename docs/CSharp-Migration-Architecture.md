# C# migration architecture contract

Status: authoritative migration contract for the incremental move to compiled C#/.NET 10/WPF. It does not authorize a production cutover.

## Scope and reference behavior

The shipping AV Workstation Toolkit 1.1.1 implementation remains the behavioral and security reference. Phase 1 added architecture seams and test adapters. Phase 2 added a non-shipping typed C# implementation of deterministic versions, catalog normalization/validation, query filtering, package planning, selection eligibility, and reboot/risk policy. Phase 3 added non-shipping read-only C# providers for trusted WinGet resolution, installed/update inventory, uninstall-registry inventory, and supported reboot signals. Phase 4 added a non-shipping compiled WPF executable, ViewModels, read-only planning composition, and presentation parity tests. Phase 5 added typed sanitized diagnostics, catalog detail/provenance retention, and a read-only external-provider projection. Phase 6 added strict compiled action-request creation, parsing, path validation, and plan-authorization parity. Phase 7 added a non-shipping contained persistence primitive, canonical artifact identity, strict progress/result parsing, cooperative-cancellation semantics, and a pure lifecycle model. Phase 8 added a separate non-shipping compiled worker test host, live per-package reauthorization, fake-executor-only orchestration, and real process-boundary tests under isolated roots. Phase 9 added an uncomposed exact-ID WinGet mutation runner/executor and fresh post-action verification. Phase 10 adds uncomposed typed HTTPS/SFTP delivery, scoped Credential Manager, contained cache, and payload-verification services. None of these action or delivery services is composed into the compiled App, test worker host, or shipping launcher. `AVWorkstationToolkit.exe`, its embedded PowerShell/WPF runtime, MSI, ZIP, production request writer, production worker, shipping providers, and vendor bridge remain unchanged.

When documentation and code disagree, the observed shipping behavior is characterized before any decision. A parity mismatch is evidence to investigate, not permission to relax either implementation.

## Current runtime trace

1. The self-contained, single-file `AVWorkstationToolkit.exe` launcher resolves `%LOCALAPPDATA%\AVWorkstationToolkit`, rejects elevated normal startup, validates the embedded payload manifest, and repairs a deterministic version-scoped runtime cache.
2. Normal launch starts the fixed Windows PowerShell 5.1 executable with bounded arguments and the extracted static `Start-AVWorkstationToolkit.ps1`. PowerShell loads the static XAML and core module.
3. The core reads the compiled WinGet catalog, operational external catalog, and commercial awareness catalog. It obtains structured WinGet inventory, source-aware uninstall-registry inventory, bounded vendor release information, and reboot state, then builds a read-only plan.
4. The UI filters and presents the plan. Selection is a presentation property; it does not grant execution authority.
5. A user-approved managed action creates a strict, versioned JSON request under the contained data-root request directory and starts the fixed PowerShell worker script as a separate standard-user process.
6. The worker independently validates the request path, schema, unknown fields, IDs, action, acknowledgement, live inventory, holds, and reboot/risk policy. It re-plans between packages, invokes one exact WinGet ID at a time, and verifies resulting state. Cancellation is cooperative between packages.
7. External records remain outside the worker. Approved vendor handoffs use constrained HTTPS, authenticated SFTP, parent-provider, cached-payload, or awareness behavior. The compiled launcher already hosts the bounded `--vendor-bridge` mode.
8. The launcher also exposes bounded verification, diagnostics, smoke, and wait modes used by packaging and QA.

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

Domain contains typed deterministic migration implementations. Application owns typed read-only inventory ports, generic external-evidence matching, and read-only workstation-plan coordination. Infrastructure.Windows implements the non-shipping WinGet, Registry, reboot, Authenticode, and constrained-process adapters. App is now a conventional compiled WPF presentation with `x:Class`, ViewModels, commands, and a source-checkout catalog composition root. It is deliberately absent from the launcher and release package.

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

## Eventual executable modes

The intended single product executable will eventually dispatch explicit modes:

```text
AVWorkstationToolkit.exe                 compiled C# WPF application
AVWorkstationToolkit.exe --worker ...    isolated compiled action worker
AVWorkstationToolkit.exe --vendor-bridge constrained vendor bridge
AVWorkstationToolkit.exe --verify ...    integrity/package verification
AVWorkstationToolkit.exe --diagnostics   bounded diagnostics
```

The eventual product `--worker` mode is not implemented or wired into shipping composition. Phase 8's separate `AVWorkstationToolkit.Worker.exe --test-mode` host is a fake-executor-only migration harness: it requires an explicit isolated root and canonical request path, has no WinGet/process/network implementation, and is absent from launcher, MSI, ZIP, and release composition. A future product worker must remain an independently validating process boundary, not an in-process service owned by the UI.

## Compiled WPF publishing constraint

The current launcher safely uses trimming because it is not the WPF application. The final compiled WPF application must remain `net10.0-windows`, `win-x64`, self-contained, and single-file, but must not blindly inherit full trimming. The non-shipping compiled App makes the intended baseline explicit:

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

### Non-shipping read-only Windows boundary

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

### Compiled action-request and IPC boundary (non-shipping)

The characterized shipping schema is version 1 with exactly `SchemaVersion`, `RequestId`, `Action`, `PackageIds`, `RiskAcknowledged`, and `DryRun`. Request IDs and direct-child filenames use `request-yyyyMMdd-HHmmss-<8 lowercase hex>.json`; payloads are limited to 65,536 bytes and 100 IDs. Only Install and Update are valid actions. The writer removes blank IDs while preserving the remaining submitted order and duplicates; authorization again removes blanks, deduplicates case-insensitively, sorts, and resolves each ID exactly once against the live validated plan. Request, progress, result, and cancel names share the request ID.

The compiled Application model serializes only this schema, applies strict JSON types, rejects unknown/duplicate/missing properties, validates IDs, and reuses Domain selection/reboot/risk policy against exact managed plan actions. One Infrastructure path policy derives request, progress, result, cancel, and WinGet-log paths exclusively from a valid RequestId beneath an explicit absolute root. It enforces `logs\requests` direct-child containment, exact extensions, bounded reads, regular-file and reparse checks, and request/body correlation.

Phase 7 adds an uncomposed `ActionProtocolStore` with no default or LocalAppData root. It accepts only an `AuthorizedActionRequest`, writes UTF-8 request bytes to a non-executable same-directory create-new temporary file, flushes them to disk, and atomically moves without overwrite to the canonical request name. Cancellation-marker creation is create-new, idempotent, and requires the correlated request file to exist. Tests inject isolated temporary roots; the compiled App cannot resolve or compose this store and still refuses mutation.

Shipping progress is append-only UTF-8 JSONL with five fields (`Timestamp`, `Level`, `Stage`, `PackageId`, `Message`), a 20 MiB UI read cap, and a retained incomplete trailing line. Shipping final results are schema 1 JSON with status, exit code, correlated request/progress/WinGet-log paths, and package outcomes. The compiled parser preserves complete-line streaming, split UTF-8 sequences, result/status/exit/verification semantics, and cooperative cancellation as intent/observation/final confirmation. It bounds individual progress records, text, arguments, cancellation markers, and final results; malformed complete records become explicit issues and never authority.

Phase 8 composes the same strict request and IPC contracts into a separate test-only process. The Application orchestrator authorizes the complete request, then obtains and authorizes a fresh plan before every package; action changes, holds, unknown packages, risk acknowledgement changes, and pending-reboot risk become correlated Blocked results. The test host still supplies only `DeterministicFakePackageExecutor`, with success, failure, verification-failure, and bounded delay outcomes. The file protocol appends strict progress, writes one atomic no-overwrite final result, and observes cancellation markers cooperatively between packages. All process tests use isolated temporary roots.

Phase 9 implements a separate, non-shipping `WinGetPackageActionExecutor`. It accepts only a typed already-authorized Install/Update package request, resolves WinGet through the existing Desktop App Installer path/signature/publisher policy, constructs the exact one-ID argument vector internally, captures bounded output with a 30-minute ceiling, and maps nonzero/timeout outcomes explicitly. The orchestrator now obtains fresh, complete WinGet evidence after exit zero and reports Unverified unless Install is detected or Update is no longer update-eligible. Deterministic process tests inject a capturing host and never execute WinGet.

Phase 10 retains validated delivery fields on the typed catalog definition and requires them to create HTTPS or SFTP authorization. Infrastructure revalidates every HTTPS redirect, probes and compares SFTP host identity before credential lookup, scopes Credential Manager targets by host/port/username with legacy read/delete compatibility, and treats cache files as Downloaded until SHA-256, Authenticode, and publisher policy produce Verified evidence. These services have no installer/process API and are not present in production composition.

No worker launcher exists in production source, the compiled App cannot write live requests, and the fake-only worker host does not compose the real executor. Production request persistence, worker launch/execution, live mutation, progress/result generation, cancellation handling, and post-action verification remain in shipping PowerShell.

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

The migration does not configure SignPath or imply Foundation acceptance. The target production chain remains:

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

- The dated security-audit visual section records black/unavailable automated frames, while the newer QA report records four meaningful captures. The reports describe different validation snapshots; neither constitutes an interactive desktop review.
- Existing endpoint-security documentation calls the current PowerShell retention work “Phase 1.” That predates this C# migration phase; the term is historical context, not evidence of a compiled cutover.
- The shipping plan's `CanSelect` reflects package state, while pending-reboot risk enforcement occurs independently in request validation. The parity contract preserves both `CanSelect` and `WorkerEligible`; collapsing them would move authorization into presentation.
- Shipping `StatusDetail` is user-facing prose rather than a stable machine reason. The C# package state pairs it with a stable `ReasonCode`; the legacy adapter maps the same reasons for parity. This is not a production request/result schema change.
- Current WinGet 1.29 can emit update tables without a `Source` column and can append a second explicit-target table. Phase 3 characterization exposed that the first strict parser draft rejected this legitimate output; both adapters now parse column-aligned tables with or without `Source` and reject nonempty output without a validated table.
- The shipping request writer preserves submitted package order and duplicates, while `Assert-AVWorkstationToolkitRequest` removes blank IDs, deduplicates, and sorts before authorization. The C# factory preserves writer semantics and the C# authorization service applies the same case-insensitive deduplicate/sort boundary.
- Windows PowerShell 5.1 `ConvertFrom-Json` accepts duplicate object properties and retains the last value. The compiled parser rejects duplicates before materialization. This is a documented defense-in-depth difference, not permission to weaken C# validation; changing the shipping parser requires a separately approved worker change.
- The shipping worker's PowerShell property-name comparisons are case-insensitive. The compiled parser requires the six schema property names with exact casing and treats a differently cased name as unknown; this keeps the new boundary unambiguous without changing the shipping worker in this phase.
- The shipping writer accepts any nonblank package-ID string, but the complete shipping boundary later rejects IDs that do not resolve to the validated catalog plan. The compiled request boundary applies the catalog package-ID grammar earlier and still requires exact plan authorization.
- The shipping writer drops blank ID values before enforcing its nonempty count. The compiled factory rejects blank IDs immediately so callers cannot mistake discarded selections for a successfully encoded request; the complete valid-request semantics remain equivalent.
- The shipping request writer uses direct `Set-Content`, while the uncomposed compiled persistence primitive uses a same-directory create-new/flush/atomic-move pattern and refuses overwrite. This is intentional fail-closed hardening; it does not change the production writer in Phase 7.
- The shipping UI stops waiting after 1,800 seconds but the PowerShell worker does not impose an internal WinGet timeout. The uncomposed compiled mutation runner has a hard per-process ceiling of 30 minutes as required by the migration contract; this stricter behavior requires explicit review at production cutover.
- The shipping UI displays a malformed complete progress line as raw text and reads only `Level` and `Message`; the compiled parser requires all five exact fields, validates package correlation, and reports malformed complete lines as typed issues. The shipping result reader likewise displays only bounded message/status, while the compiled parser validates the full writer schema, paths, exit codes, package outcomes, and verification semantics. These stricter non-shipping readers prevent evidence from being mistaken for authoritative success and are not normalized away.
- Shipping result JSON has no `RequestId` field. Correlation therefore uses the canonical result filename plus exact `RequestPath`, `ProgressPath`, and `WingetLogPath` values; adding a new field would be a schema change and was not done.

## Current shipping, migration-present, and target states

- **Current shipping architecture:** the .NET launcher starts the embedded Windows PowerShell 5.1 WPF application and isolated PowerShell worker.
- **Migration implementation present but not active:** typed C# deterministic domain logic, read-only Windows providers, sanitized diagnostics, catalog/provider detail projections, strict action-request and IPC contracts, a compiled WPF presentation, a separate fake-executor-only worker process harness, and an uncomposed exact-ID WinGet executor compile into non-shipping projects. The worker harness reauthorizes every package and writes correlated progress/results only beneath an injected test root; it does not compose the real executor. The compiled App still does not compose the request factory, path policy, protocol store, worker, or mutation executor; cannot persist a live request; has no action-worker or vendor-transport dependency; and explicitly refuses install/update requests. No launcher or package references the worker, mutation executor, or migration App.
- **Target architecture:** the compiled WPF App and isolated compiled worker use the proven Domain/Application layers after explicit, responsibility-by-responsibility cutover approval.
- The current launcher exits after starting the GUI in normal mode, while the worker is independently launched and tracked by request/result files. A compiled App lifecycle needs explicit worker detachment and cooperative-cancellation design before cutover.
- Vendor release checks, delivery, browser handoffs, diagnostics copy/export, and the independently validating action worker remain shipping-PowerShell responsibilities. The compiled preview uses validated catalog baseline versions, represents official links as non-executing intents, and reports unavailable evidence rather than inventing a release result.

## Cutover and retirement rule

A PowerShell component is removable only when the coverage matrix's responsibility-specific retirement condition is met, dual-engine characterization is green, security review is complete, source/package QA is green, and the cutover is explicitly approved. “The C# version exists” is never sufficient.
