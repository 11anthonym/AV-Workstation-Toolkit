# AV Workstation Toolkit repository contract

AV Workstation Toolkit is a local Windows application for planning, installing, and maintaining a controlled AV/IT workstation software baseline. The compiled C#/.NET 10/WPF App and the independent compiled worker are the production runtime. PowerShell remains repository tooling — build, catalog authoring, QA, and operator scripts — and is not part of the packaged runtime.

## Read first

Before changing runtime behavior, read `docs/AV-Workstation-Toolkit-Architecture-and-Safety.md`, `docs/Endpoint-Security-Behavior.md`, and the tests that cover the area you are touching. Packaging or release changes also require `docs/Packaging-and-Release.md` and `docs/Code-Signing-Policy.md`. `docs/CSharp-Migration-Architecture.md` and `docs/CSharp-Migration-Coverage.md` are historical records of the completed PowerShell-to-C# migration; consult them for background, but they do not impose obligations on ordinary product work.

## Source map and dependency rules

- `src/AVWorkstationToolkit.Launcher/`: self-contained production bootstrap and embedded-runtime integrity. Deliberately absent from `AVWorkstationToolkit.slnx` — see the comment on its `ProjectReference`.
- `src/AVWorkstationToolkit.App/`: production compiled WPF composition and presentation.
- `src/AVWorkstationToolkit.Worker/`: independently validating production worker host.
- `src/AVWorkstationToolkit.Application/`: use cases and infrastructure abstractions.
- `src/AVWorkstationToolkit.Domain/`: deterministic catalog, package, version, planning, policy, risk, and result logic.
- `src/AVWorkstationToolkit.Infrastructure.Windows/`: Windows adapters implementing Application abstractions.
- `tests/AVWorkstationToolkit.Tests/`: the deterministic MSTest suite. New behavior belongs here by default.
- `tests/AVWorkstationToolkit.IntegrationTests/`: boundary harnesses for what an in-process test cannot reach — live read-only providers, the worker process protocol, the compiled action flow, and a non-mutating live rehearsal.
- `tests/AVWorkstationToolkit.Worker.DevHost/` and `tests/AVWorkstationToolkit.Development/`: the non-shipping worker host and launch support that make real-process worker testing possible without mutating the machine. No shipping assembly may reference either.
- `tests/fixtures/`: deterministic inputs shared by the suites.
- `manifests/`: compiled runtime policy and catalog artifacts. `managed-applications.json` is the canonical managed-package definition. Awareness is knowledge, not execution authority.

Dependencies flow App -> Application -> Domain and Infrastructure.Windows -> Application abstractions -> Domain. Domain must never depend on WPF, Registry, Process, HTTP, filesystem, Credential Manager, or PowerShell.

## Non-negotiable safety rules

Keep standard-user UI and worker execution; exact-ID WinGet allowlisting; one package per `winget` invocation with `--id`, `--exact`, and `--source winget`; independent worker request and live-state validation; risk-sensitive pending-reboot enforcement; explicit incomplete/unavailable inventory; contained, versioned, strict request files; reparse rejection; bounded files and network responses; approved HTTPS hosts and redirects; signed/hash/publisher validation; SFTP host-key verification before credentials; Credential Manager isolation; and credential redaction.

External and awareness records never acquire worker authority. Policy grants authority; providers only supply inventory or an approved handoff. There is no uninstall, generic command, local web server, automatic rollback, or security/MDM/VPN/EDR/OS-management scope.

Do not introduce generic shell execution, `Process.Start("powershell ...")` as a convenience layer, `cmd.exe` command construction, process-name polling, `GetProcessesByName`-style worker ownership, unknown-field-tolerant request parsing, silent fallback from failed inventory, or UI-side package authorization.

> Never weaken a validation, alter a fixture, or change catalog data to make a test pass. A failing test is either a real defect or an intentional behavior change; an intentional change requires explicit approval and a deliberate, reviewed test update in the same commit.

## Build and test

Use targeted tests while developing — the MSTest class covering your change, or the specific QA script for the surface you touched. Run the full chain once the change is coherent, from a standard-user Windows PowerShell session:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Run-Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-EndpointTrust.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-CompiledRuntime.ps1
.\Build-AVWorkstationToolkit.cmd
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-Package.ps1
```

`Build-AVWorkstationToolkit.cmd` already runs `Run-Tests.ps1` and `Test-CompiledRuntime.ps1` unless `-SkipTests` is passed, so do not run those separately immediately before a full build. `Test-Package.ps1` is separate package/artifact QA that the build does not perform.

Use locked restore. Review any `packages.lock.json` change intentionally. Never update catalogs, fixtures, request schemas, package IDs, or safety policy solely to satisfy a code change.

## Change reports

For an ordinary product change, report the files touched, the exact validation commands and their results, the security invariants involved if any, and any documentation that had to change with the code. Never claim interactive visual validation without an actual desktop review.
