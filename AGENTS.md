# AV Workstation Toolkit repository contract

AV Workstation Toolkit is a local Windows application for planning, installing, and maintaining a controlled AV/IT workstation software baseline. The current PowerShell-hosted WPF application is the behavioral and security reference while the runtime is migrated incrementally to compiled C#/.NET 10/WPF.

## Read first

Before changing runtime behavior, read `docs/AV-Workstation-Toolkit-Architecture-and-Safety.md`, `docs/CSharp-Migration-Architecture.md`, `docs/CSharp-Migration-Coverage.md`, `docs/Endpoint-Security-Behavior.md`, and the relevant current tests. Packaging or release changes also require `docs/Packaging-and-Release.md` and `docs/Code-Signing-Policy.md`.

## Source map and dependency rules

- `app/` and `scripts/`: shipping PowerShell/WPF implementation and isolated worker.
- `src/AVWorkstationToolkit.Launcher/`: current compiled launcher and vendor bridge.
- `src/AVWorkstationToolkit.App/`: future WPF composition/presentation layer; non-shipping until an approved cutover.
- `src/AVWorkstationToolkit.Application/`: use cases and infrastructure abstractions.
- `src/AVWorkstationToolkit.Domain/`: deterministic catalog, package, version, planning, policy, risk, and result logic.
- `src/AVWorkstationToolkit.Infrastructure.Windows/`: Windows adapters implementing Application abstractions.
- `tests/parity/` and `tests/fixtures/`: dual-engine characterization and deterministic inputs.
- `manifests/`: compiled runtime policy/catalog artifacts. Awareness is knowledge, not execution authority.

Dependencies flow App -> Application -> Domain and Infrastructure.Windows -> Application abstractions -> Domain. Domain must never depend on WPF, Registry, Process, HTTP, filesystem, Credential Manager, or PowerShell.

## Non-negotiable safety rules

Keep standard-user UI and worker execution; exact-ID WinGet allowlisting; one package per `winget` invocation with `--id`, `--exact`, and `--source winget`; independent worker request and live-state validation; risk-sensitive pending-reboot enforcement; explicit incomplete/unavailable inventory; contained, versioned, strict request files; reparse rejection; bounded files and network responses; approved HTTPS hosts and redirects; signed/hash/publisher validation; SFTP host-key verification before credentials; Credential Manager isolation; and credential redaction.

External and awareness records never acquire worker authority. Policy grants authority; providers only supply inventory or an approved handoff. There is no uninstall, generic command, local web server, automatic rollback, or security/MDM/VPN/EDR/OS-management scope.

Do not introduce generic shell execution, `Process.Start("powershell ...")` as a convenience layer, `cmd.exe` command construction, process-name polling, `GetProcessesByName`-style worker ownership, unknown-field-tolerant request parsing, silent fallback from failed inventory, or UI-side package authorization.

> If the new implementation disagrees with the current implementation, do not weaken validation, alter fixtures, or change catalog data merely to make parity pass. Determine whether the difference is a new implementation defect, an undocumented legacy behavior, or an intentional behavior change. Intentional behavior changes require explicit approval and a documented test change.

## Build and test

Run from a standard-user Windows PowerShell session:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Run-Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-EndpointTrust.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-CSharpMigration.ps1
.\Build-AVWorkstationToolkit.cmd
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-Package.ps1
```

Use locked restore. Review any `packages.lock.json` change intentionally. Never update catalogs, fixtures, request schemas, package IDs, or safety policy solely to satisfy a new implementation.

## Migration discipline and completion report

Move one deterministic responsibility at a time. Add fixture-backed characterization before replacement, run both engines, compare canonical semantic output strictly, and keep the legacy implementation until the coverage matrix's retirement condition is met. A C# type existing is not a cutover.

Every migration change report must list files, exact validation commands/results, parity scenarios and normalization, remaining coverage gaps, security invariants touched, implementation/documentation disagreements, and the next narrowly scoped migration candidate. Never claim interactive visual validation without an actual desktop review.
