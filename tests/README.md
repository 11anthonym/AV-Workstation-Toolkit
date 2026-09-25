# QA suite

Run from the repository root in Windows PowerShell 5.1:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Run-Tests.ps1
```

GitHub Actions runs the host-independent subset on a clean Windows runner:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Run-Tests.ps1 -CoreOnly
```

The workflow also installs pinned PSScriptAnalyzer 1.24.0 in the ephemeral runner and applies the targeted root `PSScriptAnalyzerSettings.psd1` policy.

Build the compiled runtime with locked restore, run the deterministic MSTest
suite, and exercise the worker, action-flow, WPF and live-rehearsal boundaries:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-CompiledRuntime.ps1
```

That chain installs, updates and uninstalls nothing. The deterministic suite uses
fixtures only; the boundary harnesses spawn the separate worker development host
rather than the shipping worker, which `Test-ProductionWorkerBoundary.ps1`
independently proves accepts production invocation only. Fixture contracts are
documented under `tests/fixtures`.

After that deterministic suite, optionally exercise the production read-only
Windows providers on the current host. The script labels unavailable sources
explicitly and never treats the workstation software list as expected data:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-CompiledReadOnlyIntegration.ps1 -NoBuild
```

This live check resolves only trusted Desktop App Installer WinGet, runs fixed
read-only export/list vectors, reads HKLM64/HKLM32/HKCU uninstall sources, and
checks the supported Windows Update/CBS reboot signals. It does not install,
update, uninstall, enable, disable, or otherwise modify workstation software.

The suite is deliberately non-installing. It uses fixture WinGet output and external-provider fixtures to test package-state parsing; Q-SYS, Biamp, and Crestron version/catalog awareness; bounded vendor-page parsing and fallback; HTTPS host/version constraints; SFTP DTD/traversal/product controls; host trust replacement; signed download-cache tampering; offline payload hash enforcement; holds; redistribution gating; parser compatibility; launcher configuration; trusted WinGet resolution; standard-user guards; credential redaction; and embedded-secret patterns. Request authorization, risk acknowledgement, reboot policy, exact WinGet arguments, and strict worker request containment and schema are covered by the compiled MSTest suite and the worker boundary harnesses.

The suite also checks the authoritative vendor-source compiler, .NET 10 launcher contract, bounded child-process policy, endpoint-trust static rules, deterministic CycloneDX SBOM generation, explicit signing policy, normalized role/manufacturer overlays, and the compiled production smoke's layout contract at 1040x760, 1280x860, 1440x900, and 1920x1080. The compiled MSTest suite covers read-only catalog details, grid sorting, composable Missing/Updates/All quick views, and filter and selection persistence. Release-candidate QA results are kept as dated records in `docs/records/`.

Worker boundary tests use the non-shipping development host or intentionally malformed requests; none of them installs, updates, or removes software.

After the automated suite passes, use the packaged production smoke in
`Test-Package.ps1` and perform an interactive review of the actual compiled
window when a material presentation change requires human visual evidence.

The packaged compiled-production smoke is the authoritative automated geometry gate. It measures the current WPF surface at 1040x760, 1280x860, 1440x900, and 1920x1080 and exercises current filter, selection, menu, icon, and activity-follow behavior.

Automated geometry and screenshot evidence never substitutes for an interactive desktop review required for a production release.

Neither command installs, updates, removes, enables, disables, starts, or stops software.

For launcher or MSI changes, build and run package QA:

```powershell
.\Build-AVWorkstationToolkit.cmd
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-Package.ps1
```

Run the focused production-source/process-policy scan independently, with an optional safe Defender scan of completed release artifacts:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-EndpointTrust.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-EndpointTrust.ps1 `
  -ReleaseRoot .\artifacts\release\1.1.1 -ScanWithDefender
```

Package QA copies the direct release EXE into an otherwise empty directory, prepares and verifies its PowerShell-free embedded compiled runtime and exact worker, runs the compiled-production WPF smoke twice to exercise reopen behavior, removes recognized stale runtime files, repairs deliberately modified cache content, confirms one-file ZIP parity, and administratively extracts the MSI without registering or installing it.

Hosted runners use `-SkipDesktopSmoke`, because they do not provide a reliable interactive WPF desktop. That mode still verifies the standalone download, release hashes, embedded-runtime extraction and repair, one-file ZIP parity, and the administratively extracted MSI executable. All launcher and MSI subprocess checks are bounded to 120 seconds by default; use `-ProcessTimeoutSeconds` only when a slower release host requires it.
