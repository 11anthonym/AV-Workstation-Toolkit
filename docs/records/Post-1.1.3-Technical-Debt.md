# Post-1.1.3 technical debt

> **Dated record, 2026-09-27.** Maintainability findings from the 1.1.3 release-hardening pass that are deliberately left for after 1.1.3. None is a known defect or a security boundary gap; each is kept because removing it now would destabilize the release. Current behavior is described in the maintained documents this record links to.

## PowerShell operator module

`scripts/AVWorkstationToolkit.Core.psm1` (about 3,700 lines and 68 functions) still carries the supported command-line workflow that the 2026-09-25 cleanup deliberately kept: `Invoke-AVWorkstationToolkitDeployment.ps1`, `Invoke-AVWorkstationToolkitMaintenance.ps1`, their PowerShell action worker `Invoke-AVWorkstationToolkitAction.ps1`, `Test-DeploymentReadiness.ps1`, `Get-WorkstationSnapshot.ps1`, `Export-AVWorkstationToolkitBaselineManifest.ps1`, and `Add-AVWorkstationToolkitExternalPackage.ps1` (see [scripts/README.md](../../scripts/README.md)). It is repository and operator tooling, not part of the packaged runtime, and source QA covers it. It overlaps conceptually with the compiled planning, inventory, and worker code, so catalog and policy rules are implemented twice.

After 1.1.3: decide which of these workflows the compiled runtime should own, move shared catalog and policy parsing to one implementation, and keep the exact-ID, one-package-per-invocation, and independent-validation rules intact in whatever remains.

## Smoke contracts in the shipping WPF code

The shipping App contains the checks its smoke modes run: `MainWindow.VerifySmokeContractAsync`, `VerifyProductionSmokeContractAsync`, and its migration persistence round trip, plus the `VerifySmokeContract` methods of the migration, detail, diagnostics, safety, and About windows. They are there so package QA exercises the exact final binary, and the production smoke accepts only a disposable package QA data root (see [Package QA](../Packaging-and-Release.md#package-qa)).

After 1.1.3: consider moving the contract checks behind a small hook or a test-only assembly that the package QA harness loads, provided package QA still drives the final signed binary rather than a separate test build.

## Addressed in this pass

The reviewed .NET runtime patch was repeated across three projects, three build scripts, and three QA scripts, which slowed security updates. It is now set once in `build/ReviewedDotNetRuntime.props`, and source QA refuses a project or script that pins its own patch (see [Packaging and release](../Packaging-and-Release.md)).
