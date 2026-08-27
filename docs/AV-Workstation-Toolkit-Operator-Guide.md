# AV Workstation Toolkit 1.1.1 Operator Guide

AV Workstation Toolkit is the primary interface for installing and maintaining explicitly approved user applications. It is a local Windows desktop tool with no remote installation endpoint. Refresh performs read-only WinGet inventory, uninstall-registry inventory, and bounded HTTPS version checks for catalogued external vendors. SFTP is contacted only after the operator selects the Crestron delivery workflow.

## Before launch

- Use Windows 10 or Windows 11 with App Installer/winget available.
- Launch from a standard-user session. Do not use **Run as administrator**; AV Workstation Toolkit refuses elevated startup, while individual installers can still request elevation through Windows.
- Save work before beginning an install or update wave.
- Treat a Windows Update or Component Based Servicing reboot banner as an actionable warning. Ordinary low-risk application changes remain available, but driver-, service-, and listener-bearing actions are blocked until Windows is restarted and the plan is refreshed. Generic queued file-renames are intentionally ignored because they are frequently stale and do not provide an operator-resolution path.
- For the direct release, run `AV-Workstation-Toolkit-1.1.1-win-x64.exe`. For an installed build, use the Start-menu shortcut created by the reviewed MSI. The optional ZIP contains the same one-file executable.
- For source development, confirm catalog changes have passed `tests/Run-Tests.ps1`.

## Launch

Run the downloaded release EXE directly, or open **AV Workstation Toolkit** from the Start menu after MSI installation. No companion folder is required. The executable restores and hash-verifies its embedded runtime beneath `%LOCALAPPDATA%\AVWorkstationToolkit\runtime\1.1.1` before loading the frontend.

From a source checkout, double-click `Launch-AVWorkstationToolkit.cmd`, or run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\scripts\Start-AVWorkstationToolkit.ps1
```

The initial refresh is read-only. It runs WinGet inventory commands, inspects standard Windows uninstall-registry entries for externally detectable applications, checks only the operational catalog's bounded release pages, and assigns one status to every record. Broad awareness records do not cause AV Workstation Toolkit to crawl hundreds of vendor sites.

## Status meanings

| Status | Meaning | Selectable |
|---|---|---|
| Current | Installed with no allowlisted update reported | No |
| Missing | Not installed and approved for automated deployment | Yes, install |
| Update available | Installed and an allowlisted update is reported | Yes, update |
| Manual update | An external vendor reports a newer catalogued version | No; use the delivery button |
| Held | Installed, but automated maintenance is prohibited by catalog policy | No |
| Manual | Deployment requires a separately verified manual artifact or decision | No |
| Detected | An inventory-only dependency or OEM component was detected | No |
| Not detected | An inventory-only dependency or OEM component was not detected; no install is implied | No |
| Inventory incomplete | One or more uninstall-registry sources were unavailable and the remaining sources could not prove whether this product is installed | No |
| Inventory unavailable | No uninstall-registry source was available for this detector-backed product | No |
| Check unavailable | A product-specific release/version check could not produce usable comparison data | No |
| Catalog only | AV Workstation Toolkit knows the product, service, server, embedded interface, or legacy tool but has no reliable Windows detection action | No |
| Error | A package-specific detector/version failure or complete managed WinGet inventory failure occurred | No |

Use search, profile, catalog-policy, manufacturer, and discipline filters to narrow the table. Manufacturer choices are generated from the loaded catalog and compose with every other filter. Catalog presets cover P1/onsite candidates, free/public tools, dealer access, licensing, drivers, services/listeners, firmware utilities, lifecycle, unmanaged records, and installed software with a limited source. Disciplines are metadata-based and separate from manufacturer ecosystems. Select a row and choose `View details`, or double-click it, to inspect its identity, installed state, access restrictions, compatibility fields, system impact, evidence, and official links. This view is read-only and does not grant installation authority. Purpose and restriction text uses a compact two-line row preview; hover it or open details for the complete value. `Diagnostics` shows sanitized runtime, WinGet, reboot, catalog, and per-registry-source health; it can copy text or export JSON without reading credentials.

Click Application, Vendor, Priority, Status, Versions, or Risk to sort ascending; click the same header again to reverse it. Priority uses `P1`, `P2`, `UTILITY`, `DEV`; risk uses `Low`, `Service`, `Listener`, `Driver`; status uses an attention-first sequence beginning with managed updates and missing apps, followed by manual/held/error and incomplete-inventory states, then current, inventory, manual, and catalog-only states. Numeric version segments sort correctly, so 1.9 precedes 1.10. Unknown labels and missing or unevaluated versions remain deterministic. Sorting, search, filters, checkbox selection, and the selected row survive view changes; the chosen sort is restored after refresh.

`Missing apps` and `Available updates` are quick views, not hidden bulk commands. They narrow the currently filtered list and select only eligible managed actions; relevant manual or held matches remain visible without becoming actionable. The highlighted button and `Showing:` label identify the active view. `All apps` returns to the normal list without resetting search, profiles, catalog policy, manufacturer, discipline, or sorting. `Clear selection` clears only action checkboxes and leaves the current quick view, filters, and sorting intact.

Only missing or updateable allowlisted WinGet applications have enabled selection boxes. Current, manual, external, inventory-only, held, and catalog-only rows remain deliberately non-actionable; their disabled selection box explains the reason in its tooltip and accessibility help text.

The conventional menu keeps less-frequent commands out of the workstation action area. `File` contains plan export, logs, and exit; `View` contains refresh and Diagnostics; `Tools` checks system state; and `Help` contains the read-only Safety & Security explanation and About. About identifies the AV Workstation Toolkit product version and whether the app is packaged or running from source. WinGet, launcher/runtime, privilege, and inventory health remain in Diagnostics instead of occupying the normal header. The refresh and Diagnostics buttons remain available as common shortcuts. If the left filter area exceeds the window height, its own vertical scrollbar provides access to every filter, quick view, selection command, and plan count.

## External and offline packages

Select an external application row to enable its delivery button:

- `Open vendor download` opens only the HTTPS address embedded in the reviewed catalog. The recipient downloads and accepts vendor terms directly.
- `Open official product` is an awareness-only link. It does not mean the product is downloadable, licensed, supported on Windows, or approved for this workstation.
- `Download verified package` downloads from an allowlisted HTTPS host into AV Workstation Toolkit's per-user cache, then enables handoff only after version, size, hash, and Authenticode publisher checks pass.
- `Browse Crestron software` opens the curated Crestron product picker and authenticated SFTP workflow described below.
- `Show verified package` opens Explorer with a bundled installer selected. AV Workstation Toolkit has already matched the file to the SHA-256 hash and optional Authenticode publisher embedded in the application, but it does not execute the installer.
- `Show cached installer` reveals the exact previously downloaded and validated vendor installer in Explorer. It does not verify the installed application, reinstall it, or execute the cached file. The button tooltip explains the selected row's handoff before it is opened.

External applications never enter the automated WinGet worker and remain on manual deployment and maintenance holds. The exported plan includes role, lifecycle, licensing, access, account/training requirements, impact, platform, validation method, and official URLs so the handoff can be reviewed outside the UI. If a live operational vendor check is unavailable, AV Workstation Toolkit retains the catalog baseline, reports the check failure in Activity and exported plans, and does not invent a newer version.

Q-SYS Designer Software LTS is vendor-managed. AV Workstation Toolkit detects installed Designer versions, compares them with the LTS version on the [official Q-SYS release page](https://www.qsys.com/products-solutions/q-sys/software/q-sys-designer-software/), and opens that page for delivery. The installer is not bundled because the [Q-SYS EULA](https://help.qsys.com/q-sys_9.13/Content/Legal.htm) restricts external redistribution. Match Designer to deployed Core firmware and review release notes before upgrading.

### Crestron SFTP

The Crestron workflow is an authorized-user download aid, not a credential bypass or software repository:

1. Review the seven products returned from the public MasterInstaller feed and select only the required tool.
2. On first use, independently verify the displayed `SHA256:` SFTP host-key fingerprint before choosing **Trust and continue**. A later mismatch is a stop condition; do not replace trust merely to make the connection work.
3. Enter the authorized Crestron username and password. Leave the password blank only when that exact host, port, and username already has a saved credential.
4. Choose **Test credentials** before downloading. **Save on this computer** writes to Windows Credential Manager only after successful authentication. **Forget saved** removes only the entered username's AV Workstation Toolkit credential.
5. Choose **Download**. AV Workstation Toolkit restricts the SFTP path and size, validates the Crestron Authenticode publisher, records the file hash, and opens Explorer with the verified installer selected. AV Workstation Toolkit does not launch it.

Credentials are sent to the packaged launcher over redirected standard input, not arguments or environment variables. Source-only launch can perform read-only inventory and vendor-page checks, but credentialed/direct downloads require a compiled AV Workstation Toolkit executable. See [External-Provider-Guide.md](External-Provider-Guide.md) for provider and cache details.

## Install or update

1. Refresh the plan.
2. Review the reboot banner, package status, version, risk, and restriction note.
3. Select only the apps required for this workstation or role.
4. Choose `Install selected` or `Update selected`.
5. Review the exact name and package ID list in the confirmation dialog.
6. If a driver, service, or listener is selected, approve the second run-specific risk prompt only after reviewing its impact.
7. Allow each installer to finish. Progress and winget output appear in the activity pane.
8. Review the final verification result, then refresh the plan.

The worker executes one exact winget package ID at a time through a verified Microsoft Desktop App Installer binary. Low-risk packages request silent, noninteractive installation. Driver-, service-, and listener-bearing packages remain interactive so their installer choices stay visible. In a multi-package request, AV Workstation Toolkit refreshes reboot and eligibility state before each package after the first. If a reboot becomes pending, a low-risk next package may continue; a risk-bearing next package is rejected before execution.

The main banner describes this condition in plain language: `Restart recommended. Windows is waiting for a restart to finish an update. You can still install most apps, but some system-level changes are paused until you restart.` Select `Check again` after restarting or after Windows finishes servicing. Technical reboot signals remain available in Diagnostics.

`Stop after current` creates a cancellation marker. It never kills an active installer; the worker checks the marker before starting the next package.

## Verification and evidence

An install succeeds only if winget returns success and the exact package ID is found afterward. An update succeeds only if the package remains installed and is no longer listed as upgradeable.

Use `Open logs` to inspect local JSONL progress, final JSON results, and winget output. Installed and portable builds use `%LOCALAPPDATA%\AVWorkstationToolkit\logs\requests`; source checkouts use `logs\requests`. `Export plan` defaults to the corresponding `reports` folder. These files can contain device and application metadata.

No automatic rollback or uninstall is attempted. If an action fails or cannot be verified:

1. Preserve the result and winget log.
2. Refresh the plan.
3. Check the package's validation rule in the selected-item details or exported plan.
4. Resolve the package-specific issue before submitting another change wave.
5. Do not substitute a similar package ID.

## Current holds

- RealVNC is viewer-only and remains on both deployment and maintenance hold because WinGet still advertises a download that returns 404. AV Workstation Toolkit must not offer that update; never substitute a VNC server or combined viewer/server package.
- tftpd64 remains on automated-maintenance hold. Keep the existing standard edition on-demand; never deploy or configure its service edition through this tool.
- Q-SYS Designer LTS remains vendor-managed and manual. AV Workstation Toolkit reports version drift but does not download, redistribute, or silently install it.
- Every other external provider also remains on manual deployment and maintenance hold. A cached Biamp or Crestron installer is a verified handoff file, not permission for unattended installation.

## Permanent scope boundary

This tool manages only allowlisted user applications. It must not install, update, remove, repair, configure, enable, disable, or otherwise manage encryption, antivirus/EDR, Windows security, device-management agents, SCCM/Intune, VPN/security clients, corporate remote-support agents, vulnerability-management products, device firmware, Windows components, or base drivers. Catalog awareness of a firmware utility, driver-bearing AV tool, server, or embedded product does not authorize that operation. Network diagnostics are limited to authorized AV/IT networks and retain their documented driver/listener controls.

## Uninstall and local data

For an MSI installation, use **Windows Settings -> Apps -> Installed apps** or
the classic **Programs and Features** control panel, select **AV Workstation
Toolkit**, and choose **Uninstall**. Windows Installer removes the Program Files
payload and Start-menu shortcut.

For the direct executable or portable ZIP, close the application and delete the
downloaded executable or extracted directory. The portable application does
not register a service or separate uninstaller.

Uninstall does not delete `%LOCALAPPDATA%\AVWorkstationToolkit`. That directory
can contain logs, plan/diagnostic exports, trusted SFTP host records, and
verified cache evidence. Removing it is optional and destructive; review
operational retention requirements first. Historical
`%LOCALAPPDATA%\AVinite` material may remain intentionally after the bounded
migration and should not be deleted automatically merely to complete the
rename.

## QA after a change

Run the non-installing suite:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Run-Tests.ps1
```

Then render the UI for visual review:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\scripts\Start-AVWorkstationToolkit.ps1 -SmokeTest -RenderPreviewPath "$env:TEMP\AV-Workstation-Toolkit-preview.png"
```

For deterministic geometry checks at all supported release viewports, run `tests\Test-VisualLayout.ps1`. A `PREVIEW_UNAVAILABLE` result means the current Windows session could lay out the WPF tree but could not capture a meaningful desktop frame; it is not a visual approval. Inspect the default, minimum, ComboBox popup, selection, focus, disabled, and details states on an interactive Windows desktop before publication.

Do not use a catalog edit for a live install until both checks pass and the package decision register is updated.

For packaging or launcher changes, also run:

```powershell
.\Build-AVWorkstationToolkit.cmd
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File .\tests\Test-Package.ps1
```

The MSI installs the standalone executable beneath `%ProgramFiles%\AVWorkstationToolkit` and creates a Start-menu shortcut. Uninstall removes the executable and shortcut but deliberately preserves `%LOCALAPPDATA%\AVWorkstationToolkit` operational evidence and runtime cache.

See [AV Workstation Toolkit Security Audit](AV-Workstation-Toolkit-Security-Audit.md) for the v1 threat model, remediated findings, and residual release risks.
