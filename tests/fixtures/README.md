# Deterministic migration fixtures

These files are reusable, non-installing inputs for characterizing the shipping PowerShell implementation and compiled replacements. `tests/parity/fixtures/` contains active managed planning/reboot scenarios, while `tests/parity/core-fixtures/` contains active Phase 2 version, catalog, filtering, and external-status scenarios. The directories below establish remaining provider/worker fixture contracts; staged entries are not presented as executed coverage.

Fixtures must contain no timestamps, machine names, user paths, credentials, live URLs, or generated command strings. Unknown fields are rejected by active adapters. A mismatch is investigated; fixtures are not weakened to hide it.

| Area | Fixture | Current state |
|---|---|---|
| WinGet installed inventory | `winget/inventory-scenarios.json` | Staged; existing raw fixtures remain used by source QA |
| WinGet upgrades | `winget/upgrade-scenarios.json` | Staged; package-state subset active in parity fixtures |
| Registry inventory | `registry/uninstall-source-scenarios.json` | Staged; source-aware behavior remains covered by PowerShell QA |
| Catalog validation | `catalog/validation-scenarios.json` | Focused active cases in `tests/parity/core-fixtures/`; larger hostile corpus remains staged |
| Planning/status | `planning/status-scenarios.json` | All current semantic statuses active across both parity fixture sets |
| Reboot/risk | `reboot/risk-scenarios.json` | Clear/WU/CBS and None/Driver/Service/Listener active through `tests/parity/fixtures/` |
| Worker request validation | `worker/request-scenarios.json` | Staged definitions; current worker tests remain authoritative |

An active fixture must name its schema version and be validated by both adapters before it can count toward a PowerShell component's retirement condition.
