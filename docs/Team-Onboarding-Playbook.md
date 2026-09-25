# AV/IT Workstation Application Onboarding Playbook

This is the repeatable application-deployment procedure for AV/IT escalation workstations. It is intentionally narrower than a full Windows build process.

## Scope

The compiled application installs and maintains only approved user applications in `manifests/managed-applications.json`, the canonical managed-package authoring source. The external and commercial AV manifests add inventory, provider, access, and role awareness; they do not expand the automated change boundary.

The following are permanently outside the managed change scope: BitLocker, antivirus/EDR, device-management agents, SCCM/Intune, VPN/security clients, corporate remote-support agents, Windows components, device firmware execution, base-driver replacement, and vulnerability-management products. AV Workstation Toolkit may catalogue a vendor firmware utility, embedded operating system, or driver-bearing engineering tool so an operator can identify its impact, but it does not flash devices or silently execute external installers. Catalogued network-diagnostic tools are limited to authorized AV/IT networks and retain their driver/listener controls.

Generic Windows and winget output may show that an externally managed product exists. That is inventory evidence only and is not an instruction to act on it.

## Profiles

| Profile | Intended use | Special controls |
|---|---|---|
| Standard | Low-risk everyday team utilities | None beyond normal review |
| Field | Network, remote-session, capture, and TFTP utilities | Drivers and listeners need explicit opt-in |
| Developer | Git and database/development utilities | Project runtimes are handled separately |
| Optional | Role- or policy-dependent apps | Services need explicit opt-in |

`All` selects all four profiles for planning. It does not grant permission to install or update them.

## Workstation onboarding procedure

Install and update applications with the desktop app: the directly downloaded standalone EXE, or the Start-menu **AV Workstation Toolkit** shortcut created by the MSI. The optional ZIP contains the same executable, and `Launch-AVWorkstationToolkit.cmd` runs it from a source checkout. Launch it and the repository scripts below as a standard user. Never use **Run as administrator**; individual installers can request UAC when necessary.

1. Capture an evidence checkpoint from a standard-user PowerShell session in the repository root:

   ```powershell
   .\scripts\Get-WorkstationSnapshot.ps1 -IncludeDirectorySizes
   ```

2. Check the application gate:

   ```powershell
   .\scripts\Test-DeploymentReadiness.ps1
   ```

3. If `RebootPending` is true, save work, complete the normal Windows reboot, and repeat the readiness check. Neither the app nor the repository scripts clear or remediate the reboot condition.

4. Open AV Workstation Toolkit and let the initial read-only refresh finish. Filter to the engineer's profile and choose **Missing apps**.

5. Review every missing application against the engineer's role. A disabled selection box explains why its row is not actionable. Never substitute a package ID the catalog does not approve.

6. Select only the approved applications, choose **Install selected**, and check the exact names and package IDs in the confirmation dialog.

7. Review the final verification result, refresh, and perform the validation recorded in each selected package's authoritative catalog metadata.

Never use another workstation's winget export with `winget import`; it is evidence, not a deployment manifest.

## Explicit-risk applications

Drivers, services, and listeners are never silently included in a change wave. When one is selected, AV Workstation Toolkit asks for a second, run-specific risk confirmation after the package list. Approve it only after a role decision and a review of that package's impact. While Windows reports a pending restart, these packages stay blocked even with the confirmation.

Select only the reviewed package. Scanning and discovery tools must be used only on authorized networks. RealVNC is viewer-only; do not substitute a server package. Wireshark and Nmap use Npcap; do not introduce WinPcap.

## Maintenance procedure

1. Refresh the plan and choose **Available updates**. Windows components, runtime libraries, and out-of-catalog packages are intentionally omitted, and held packages stay visible without becoming selectable.

2. If the restart banner is shown, restart before updating any driver-, service-, or listener-bearing app.

3. Select only the reviewed updates and choose **Update selected**. Approve the risk confirmation only for a specifically approved driver-, service-, or listener-bearing app.

4. Review the verification result and refresh the plan afterward.

Do not use `winget upgrade --all` as part of this playbook.

## Configuration transfer

Application installation does not restore user state. Transfer only the required files or settings for KeePass, SSH/PuTTY, mRemoteNG/MobaXterm, Wireshark, Q-SYS, Crestron, Extron, Git, PowerShell, and VS Code. Keep credentials, private keys, client exports, and license files out of repository snapshots and synchronized folders unless the approved process explicitly requires them.

Do not copy entire AppData trees. Validate one representative workflow for each restored application.

## Logs and failure handling

Install and update runs write timestamped request, progress, result, and winget logs beneath `%LOCALAPPDATA%\AVWorkstationToolkit\logs\requests`; source checkouts use `logs\requests`. Before each package after the first, the worker refreshes reboot and eligibility state: a newly pending reboot permits a low-risk next package but blocks a driver-, service-, or listener-bearing next package. There is no automatic uninstall or rollback. Preserve the log, refresh the plan, and review the package manually before another attempt.

## Catalog change control

First choose the catalog layer:

- Add an exact-ID WinGet record only when AV Workstation Toolkit is approved to manage it.
- Add an operational external record only when detection and a safe manual provider behavior are supported by evidence.
- Add a commercial AV awareness record when AV Workstation Toolkit should know about the product but should not acquire or manage it.

To propose a managed user application:

1. Add one entry to the canonical `manifests/managed-applications.json` with profile, exact winget ID, risk class, and validation note.
2. Record the validation rule in that authoritative catalog entry; do not maintain workstation-specific status in the repository.
3. Refresh a source-checkout build of the app and confirm the package's planned status.
4. Confirm that the package is a user application and not externally managed software.
5. Review the change before any install or update run.

6. Run the automated safety tests:

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Run-Tests.ps1
   ```

The forbidden-product guard fails closed if a future catalog edit contains a security or management product name.

For commercial AV metadata, verify exact name, lifecycle, platform, licensing, access restrictions, and official URL against vendor documentation. Use explicit unknown values when evidence is incomplete. Community sources may identify a candidate but cannot authorize a download URL or override vendor policy. See [Commercial-AV-Catalog.md](Commercial-AV-Catalog.md) for the schema, taxonomy, and review checklist.

See `AV-Workstation-Toolkit-Architecture-and-Safety.md` for the frontend trust boundaries,
`records/AV-Workstation-Toolkit-Security-Audit.md` for the 1.1.1 finding register, and
`AV-Workstation-Toolkit-Operator-Guide.md` for status meanings, selection, evidence, and
failure handling. Keep workstation-specific deployment results in the approved
evidence store rather than in reusable product documentation.
