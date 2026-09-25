# Repository scripts

Install and update applications with the desktop app; see [the desktop app operator guide](../docs/AV-Workstation-Toolkit-Operator-Guide.md). The scripts here are read-only workstation evidence and readiness tools, plus catalog-authoring helpers. None of them installs, updates, or removes software.

## Workstation snapshot

Run from the repository root. Packaged runs default to `%LOCALAPPDATA%\AVWorkstationToolkit\snapshots`; source checkouts default to `snapshots`:

```powershell
.\scripts\Get-WorkstationSnapshot.ps1 -IncludeDirectorySizes
```

The script writes a timestamped report folder, ZIP, and SHA-256 checksum beneath `snapshots`. It does not install, update, remove, enable, disable, start, or stop workstation components.

Run every repository script as a standard user. Admin-only collections are recorded as `Skipped`, not failures. AV Workstation Toolkit intentionally refuses elevated execution because the source tree is user-writable; collect any admin-only evidence through a separately approved, protected process.

`-IncludeIdentityMetadata` additionally stores the full `dsregcmd /status` output and should be used only when the snapshot will be handled as sensitive device/tenant metadata.

## Readiness check

Before an installation or update wave in the desktop app, run:

```powershell
.\scripts\Test-DeploymentReadiness.ps1
```

This reports reboot state, selected application-configuration paths, and available winget upgrades without applying changes.

Run the non-installing regression suite after changing the catalog or scripts:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Run-Tests.ps1
```

## Hard scope boundary

These scripts do not manage BitLocker, CrowdStrike, Defender/EDR, SCCM/Intune, VPN clients, corporate remote-support agents, device-management agents, or similar security/management software. They are outside the application-deployment scope.

## Catalog authoring

`..\manifests\managed-applications.json` is the one canonical managed-package authoring source. The compiled application and worker load it, and `Export-AVWorkstationToolkitBaselineManifest.ps1` regenerates the low-risk `winget-team-baseline.json` deliverable from it. Signed managed-catalog verification stays in the compiled application and worker; the repository scripts do not implement a second signature verifier. Author every managed-package change in the JSON.

Operational external vendor applications are defined in `..\manifests\external-applications.json`; broad non-deployable commercial AV knowledge is in `..\manifests\commercial-av-catalog.json`. Schema 3 records distinguish role, lifecycle, licensing, download access, account/training gates, system impact, platform, and official source. They support registry detection, bounded official-page checks, signed direct-download caching, one authenticated SFTP provider with child applications, approved offline bundles, vendor handoff, inventory-only reporting, and awareness-only state. They can never enter the automated action worker and are never executed by AV Workstation Toolkit. The compiled product owns these vendor boundaries. Q-SYS Designer LTS remains vendor-page-only because its license restricts external redistribution.

For a third-party installer that you are authorized to redistribute, use `Add-AVWorkstationToolkitExternalPackage.ps1 -RedistributionAuthorized` to capture its hash and signer into the external catalog and copy it into the git-ignored local depot. Then run `..\Build-AVWorkstationToolkit.cmd -BuildOfflineBundle`. AV Workstation Toolkit exposes a valid bundled installer in Explorer but does not execute it.

`AVWorkstationToolkit.Core.psd1` / `.psm1` is the shared module behind these scripts and the offline-bundle build. It is repository tooling and is not packaged with the application.
