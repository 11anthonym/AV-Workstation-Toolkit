## Summary

<!-- What changes and why. Link the issue it resolves. -->

## Validation

<!-- The exact commands you ran on Windows as a standard user, and their results. -->

- [ ] `tests\Run-Tests.ps1`
- [ ] `tests\Test-EndpointTrust.ps1`
- [ ] `tests\Test-CompiledRuntime.ps1`
- [ ] `Build-AVWorkstationToolkit.cmd` and `tests\Test-Package.ps1` (packaging or release changes)

## Safety boundaries touched

<!-- Name any that apply, or write "None". See CONTRIBUTING.md and
docs/AV-Workstation-Toolkit-Architecture-and-Safety.md. -->

- Process creation, WinGet invocation, or the worker request schema
- Network providers, allowed hosts, SFTP, or credentials
- Catalog authority, package IDs, or risk classes
- Signing, SBOM, checksums, release manifest, MSI identity, or workflows

## Documentation

<!-- Docs updated with this change, or "None needed". -->
