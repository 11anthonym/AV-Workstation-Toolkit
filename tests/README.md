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

The suite is deliberately non-installing. It uses fixture WinGet output and external-provider fixtures to test package-state parsing; Q-SYS, Biamp, and Crestron version/catalog awareness; bounded vendor-page parsing and fallback; HTTPS host/version constraints; SFTP DTD/traversal/product controls; host trust replacement; signed download-cache tampering; offline payload hash enforcement; selection policy; holds; redistribution gating; risk acknowledgement; reboot rejection; exact WinGet arguments; parser compatibility; XAML loading; launcher configuration; trusted WinGet resolution; standard-user guards; credential redaction; embedded-secret patterns; and strict worker request containment/schema.

The suite also checks the authoritative vendor-source compiler, .NET 10 launcher contract, bounded child-process policy, endpoint-trust static rules, deterministic CycloneDX SBOM generation, explicit signing policy, normalized role/manufacturer overlays, read-only catalog details, domain-aware grid sorting, composable Missing/Updates/All quick views, filter and selection persistence, and responsive layout metrics at 1040x760, 1280x860, 1440x900, and 1920x1080. The exact passing totals are recorded in the current QA report after a release-candidate run.

Worker security tests use only out-of-scope or intentionally malformed requests. The worker rejects them before reading workstation inventory or calling winget.

After the automated suite passes, render the actual window for visual review:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\scripts\Start-AVWorkstationToolkit.ps1 -SmokeTest -RenderPreviewPath "$env:TEMP\AV-Workstation-Toolkit-preview.png"
```

Run the deterministic multi-viewport geometry and screenshot-quality gate directly when working on layout:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-VisualLayout.ps1
```

The geometry gate is mandatory. In a noninteractive Windows session, screenshot capture may explicitly report unavailable even when layout succeeds; that result never substitutes for the interactive desktop review required for a production release.

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

Package QA copies the direct release EXE into an otherwise empty directory, prepares and verifies its embedded runtime, runs the packaged HTTPS/SFTP/Credential Manager bridge self-test, confirms that unknown bridge request fields fail closed, runs the packaged WPF control/workflow smoke path, repairs deliberately modified cache content, confirms one-file ZIP parity, and administratively extracts the MSI without registering or installing it.

Hosted runners use `-SkipDesktopSmoke`, because they do not provide a reliable interactive WPF desktop. That mode still verifies the standalone download, release hashes, embedded-runtime extraction and repair, one-file ZIP parity, and the administratively extracted MSI executable. All launcher and MSI subprocess checks are bounded to 120 seconds by default; use `-ProcessTimeoutSeconds` only when a slower release host requires it.
