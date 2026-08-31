# Snapshot Script

For the primary graphical package-selection workflow, see [the desktop app operator guide](../docs/AV-Workstation-Toolkit-Operator-Guide.md). The commands below remain supported for snapshot and terminal-based operation.

Run from the repository root. Packaged runs default to `%LOCALAPPDATA%\AVWorkstationToolkit\snapshots`; source checkouts default to `snapshots`:

```powershell
.\scripts\Get-WorkstationSnapshot.ps1 -IncludeDirectorySizes
```

The script writes a timestamped report folder, ZIP, and SHA-256 checksum beneath `snapshots`. It does not install, update, remove, enable, disable, start, or stop workstation components.

Run every repository script as a standard user. Admin-only collections are recorded as `Skipped`, not failures. AV Workstation Toolkit intentionally refuses elevated execution because the source tree is user-writable; collect any admin-only evidence through a separately approved, protected process.

`-IncludeIdentityMetadata` additionally stores the full `dsregcmd /status` output and should be used only when the snapshot will be handled as sensitive device/tenant metadata.

Before an installation or update wave, run:

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

## Application deployment

Plan all approved profiles without installing:

```powershell
.\scripts\Invoke-AVWorkstationToolkitDeployment.ps1 -Profile All
```

Install a profile only after reviewing the plan and clearing any reboot-pending state:

```powershell
.\scripts\Invoke-AVWorkstationToolkitDeployment.ps1 -Profile Standard -Install
```

Drivers, services, and listeners require their own explicit switches. The script accepts no arbitrary package IDs and contains a hard guard against security/management products.

The repository operator scripts use the reviewed `AppProfiles.psd1` catalog. The compiled product uses `..\manifests\managed-applications.json`, and source QA requires exact field parity between them. Do not add corporate security, device-management, VPN, or remote-support software to either representation.

Operational external vendor applications are defined in `..\manifests\external-applications.json`; broad non-deployable commercial AV knowledge is in `..\manifests\commercial-av-catalog.json`. Schema 3 records distinguish role, lifecycle, licensing, download access, account/training gates, system impact, platform, and official source. They support registry detection, bounded official-page checks, signed direct-download caching, one authenticated SFTP provider with child applications, approved offline bundles, vendor handoff, inventory-only reporting, and awareness-only state. They can never enter the automated action worker and are never executed by AVWorkstationToolkit. The compiled product owns these vendor boundaries; `AVWorkstationToolkit.Vendor.psm1` remains legacy characterization source and is not packaged. Q-SYS Designer LTS remains vendor-page-only because its license restricts external redistribution.

For a third-party installer that you are authorized to redistribute, use `Add-AVWorkstationToolkitExternalPackage.ps1 -RedistributionAuthorized` to capture its hash and signer into the external catalog and copy it into the git-ignored local depot. Then run `..\Build-AVWorkstationToolkit.cmd -BuildOfflineBundle`. AV Workstation Toolkit exposes a valid bundled installer in Explorer but does not execute it.

## Application maintenance

Review updates for all approved profiles without applying them:

```powershell
.\scripts\Invoke-AVWorkstationToolkitMaintenance.ps1 -Profile All
```

After a reboot and human review, apply only a selected profile:

```powershell
.\scripts\Invoke-AVWorkstationToolkitMaintenance.ps1 -Profile Standard -Apply
```

The maintenance script ignores Windows components and every package outside the fixed app catalog. Only the three explicitly approved developer runtime entries can enter the WinGet plan; Java and Dell/Waves remain inventory-only, and VirtualBox remains excluded. Driver-, service-, and listener-bearing apps remain blocked unless their dedicated risk switch is supplied.

An app can also carry a catalog-level `Maintenance='Hold'`. A hold cannot be bypassed by a risk switch. tftpd64 is held because the current winget 4.74 metadata points to the 4.71 installer; keep the existing standard edition on-demand and never deploy its service edition.
