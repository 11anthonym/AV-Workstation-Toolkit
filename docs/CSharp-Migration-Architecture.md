# C# migration architecture contract

Status: authoritative migration contract for the incremental move to compiled C#/.NET 10/WPF. It does not authorize a production cutover.

## Scope and reference behavior

The shipping AV Workstation Toolkit 1.1.1 implementation remains the behavioral and security reference. Phase 1 added architecture seams and test adapters. Phase 2 adds a non-shipping typed C# implementation of deterministic versions, catalog normalization/validation, query filtering, package planning, selection eligibility, and reboot/risk policy. `AVWorkstationToolkit.exe`, its embedded PowerShell/WPF runtime, MSI, ZIP, request schema, data root, worker, providers, and vendor bridge remain unchanged.

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
    App.xaml               (future)
    Views/                 (future)
    ViewModels/            (future)
    Commands/              (future)
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

The Domain project now contains typed deterministic migration implementations. Application, Infrastructure.Windows, and App remain non-shipping seams; folder names shown as future should be created only when the first owned responsibility moves.

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

`--worker` is not implemented by the migration projects. A future worker must remain an independently validating process boundary, not an in-process service owned by the UI.

## Compiled WPF publishing constraint

The current launcher safely uses trimming because it is not the WPF application. The final compiled WPF application must remain `net10.0-windows`, `win-x64`, self-contained, and single-file, but must not blindly inherit full trimming. The non-shipping App scaffold makes the intended baseline explicit:

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

### Files and providers

- Data and request paths remain contained, bounded, versioned, and reparse-safe; unknown request fields fail.
- HTTPS hosts, redirects, response sizes, hashes, signatures, and publishers remain bounded by policy.
- SFTP validates the pinned host key before credentials. Changed keys fail closed.
- Credentials remain isolated and redacted; secrets never enter arguments, environment variables, or logs.

## Dual-engine parity contract

`tests/parity/Invoke-Parity.ps1` sends each active fixture to:

1. a legacy adapter that invokes the shipping `Get-AVWorkstationToolkitPlan` and `Assert-AVWorkstationToolkitRequest`; and
2. a compiled adapter backed by the typed Domain implementation.

Both emit schema-versioned canonical JSON. Planning fields are:

`Id`, `Provider`, `Installed`, `InstalledVersion`, `AvailableVersion`, `Status`, `StatusDetail`, `ReasonCode`, `Risk`, `CanSelect`, `Action`, `DeliveryMode`, `InventoryQuality`, and `WorkerEligible`.

Strict fields are IDs, versions, status/detail, reason, risk, selection/action, delivery, inventory quality, and worker eligibility. The harness omits non-contractual timestamps, computer identity, elevation observation, and fixture-root paths rather than normalizing their differences away. Package order is fixture order. Unknown fixture fields fail both adapters.

`ReasonCode` is a harness contract introduced to keep future domain logic independent of prose. It is not a production request/result schema change. `CanSelect` records presentation eligibility, while `WorkerEligible` records independent request-policy authorization with risk acknowledgement supplied; their distinction is meaningful.

Phase 2 adds a second canonical fixture schema for strict numeric version behavior, version sort keys, managed/external/awareness catalog acceptance and authority, composed profile/priority/manufacturer/discipline/role/search/Quick View filters, and every current package status. Inventory records and release evidence are fixture inputs; neither adapter contacts WinGet, Registry, HTTP, SFTP, Credential Manager, or a worker. Volatile machine data is absent rather than normalized away. Worker request-file validation and Windows/provider behavior remain legacy-only until their dedicated phases.

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

## Current shipping, migration-present, and target states

- **Current shipping architecture:** the .NET launcher starts the embedded Windows PowerShell 5.1 WPF application and isolated PowerShell worker.
- **Migration implementation present but not active:** typed C# deterministic domain logic and dual-engine tests compile into non-shipping projects. No launcher or package references those assemblies.
- **Target architecture:** the compiled WPF App and isolated compiled worker use the proven Domain/Application layers after explicit, responsibility-by-responsibility cutover approval.
- The current launcher exits after starting the GUI in normal mode, while the worker is independently launched and tracked by request/result files. A compiled App lifecycle needs explicit worker detachment and cooperative-cancellation design before cutover.

## Cutover and retirement rule

A PowerShell component is removable only when the coverage matrix's responsibility-specific retirement condition is met, dual-engine characterization is green, security review is complete, source/package QA is green, and the cutover is explicitly approved. “The C# version exists” is never sufficient.
