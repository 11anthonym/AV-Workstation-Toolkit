# Deterministic test fixtures

These files are reusable, non-installing inputs for the compiled runtime's tests and for source QA. They contain no timestamps, machine names, user paths, credentials, live URLs, or generated command strings. Unknown fields are rejected by the consuming parsers. A mismatch is investigated; fixtures are not weakened to hide it.

`winget-installed.txt`, `winget-upgrades.txt`, `winget-export.json` and `winget-current/` are the raw WinGet output vectors used by source QA. `software-compatibility/schema-v1-representative.json` is a representative reference-catalog document.

The scenario files below are staged contracts that describe intended coverage shape. They are not presented as executed coverage; the authoritative behavior tests live in `tests/AVWorkstationToolkit.Tests`.

| Area | Fixture | Where the behavior is actually tested |
|---|---|---|
| WinGet installed inventory | `winget/inventory-scenarios.json` | `ProviderInfrastructureTests`, `WinGetUpdateParserTests`, and raw vectors in source QA |
| WinGet upgrades | `winget/upgrade-scenarios.json` | `WinGetUpdateParserTests`, `WorkstationPlanningCoordinatorTests` |
| Registry inventory | `registry/uninstall-source-scenarios.json` | `ProviderInfrastructureTests`, `DiagnosticsAndDetailsTests` |
| Catalog validation | `catalog/validation-scenarios.json` | `ProviderInfrastructureTests`, `DomainCoreTests` |
| Planning/status | `planning/status-scenarios.json` | `DomainCoreTests`, `WorkstationPlanningCoordinatorTests`, `CompiledPresentationTests` |
| Reboot/risk | `reboot/risk-scenarios.json` | `DomainCoreTests` covers the full reboot-reason by risk-class matrix |
| Worker request validation | `worker/request-scenarios.json` | `ActionRequestTests`, `ActionProtocolTests`, `ActionWorkerOrchestratorTests`, `Test-ProductionWorkerBoundary.ps1` |

A fixture promoted into executed coverage must name its schema version and be consumed by a test in `tests/AVWorkstationToolkit.Tests` or a named QA script.
