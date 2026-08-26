# AV Workstation Toolkit External Provider and Credential Guide

AV Workstation Toolkit 1.1.1 has 25 operational external records and 281 commercial AV awareness records in addition to its 29 exact-ID WinGet entries. Every external record is permanently held from the automated WinGet worker. AV Workstation Toolkit may detect a product, evaluate a reviewed version, open an official page, or place a verified installer in its per-user cache, but it never launches that installer.

## Provider modes

| Mode | Behavior | Current use |
|---|---|---|
| `VendorPage` | Read a bounded official release page when configured, then open a reviewed HTTPS vendor page | Q-SYS, Biamp Canvas/Vocia, Dante, Shure, Sennheiser, Extron, FileZilla |
| `DirectDownload` | Extract a version-matched installer URL from the reviewed page, enforce redirect host and size allowlists, then require a valid Authenticode publisher | Biamp Tesira |
| `AuthenticatedSftp` | Read a public curated catalog, confirm SSH host identity, authenticate as the current user, and download a selected allowlisted product | Crestron MasterInstaller toolchain |
| `ParentProvider` | Detect and version a child application independently while inheriting one authenticated provider's host, feed, allowlist, root, size, and publisher policy | Seven Crestron MasterInstaller child applications |
| `Bundled` | Expose a locally authored, redistribution-approved payload only after path, SHA-256, and optional signer validation | Organization-authored offline bundles |
| `InventoryOnly` | Detect an installed version without claiming that a release is current or offering a delivery action | Java runtimes and Dell/Waves; also used for release state where an official account portal does not expose reliable public version data |
| `Awareness` | Represent workstation, server, web, mobile, or embedded software and link only to its official product surface | Broad commercial AV catalog |

## Current provider map

The embedded baselines and patterns were reviewed on 2026-08-16. Vendor pages can change, so a failed parser keeps the embedded baseline and reports the live check as unavailable.

| Provider | Release behavior | Delivery behavior |
|---|---|---|
| Q-SYS Designer LTS | Official page, baseline 9.13.2 LTS | Official page only; no redistribution |
| Crestron MasterInstaller | Installed inventory plus public MasterInstaller XML | Authorized SFTP with explicit host trust and Credential Manager |
| Seven Crestron child applications | Independent registry detection plus the common parent XML | Parent-provider handoff; no independent credentials or source |
| Biamp Tesira | Official support page, baseline 5.7.0 | Signed direct download from `downloads.biamp.com` |
| Biamp Canvas | Tesira support page, baseline 5.7.0 | Official page; use the release matching the Tesira software/firmware line |
| Biamp Vocia | Official page, baseline 1.9.0 | Official page; back up system data and custom audio before separate firmware work |
| Dante Controller | Official downloads page, baseline 4.18.1.2 | Official Audinate handoff |
| Shure Designer | Official archive, baseline 6.10.0 | Official Shure handoff |
| Shure Wireless Workbench | Official archive, baseline 7.8.3 | Official Shure handoff |
| Shure Update Utility | Official archive, baseline 2.8.16 | Official Shure handoff; device firmware remains a deliberate operator action |
| Shure Discovery | Official archive, baseline 2.8.16 | Official Shure handoff for same-subnet device discovery |
| Shure Microflex Wireless | Official archive, baseline 1.2.0 | Optional legacy MXWAPT2/4/8 service-tool handoff |
| Sennheiser Control Cockpit | Official release notes, baseline 9.2 | Official installation page |
| Sennheiser Wireless Systems Manager | Official documentation, baseline 4.9.0 | Official RF-planning and wireless-management handoff |
| Extron Toolbelt and PCS | Installed inventory only | Official Extron account pages |
| FileZilla Client | Installed inventory only | Official FileZilla page because no reviewed WinGet ID currently resolves |
| Java | Dependency inventory only | No default vendor or runtime is chosen |
| Dell/Waves audio | OEM inventory only | No generic installer; preserve the Dell-approved hardware baseline |

The authoritative operational configuration is [external-applications.json](../manifests/external-applications.json); broad product knowledge is in [commercial-av-catalog.json](../manifests/commercial-av-catalog.json). See the [commercial AV catalog model](Commercial-AV-Catalog.md) for metadata, source authority, and query rules. Current operational references include the [Biamp Tesira and Canvas support page](https://support.biamp.com/Tesira/Software-Firmware), [Biamp Vocia software page](https://www.biamp.com/products/families/vocia/vocia-software), [Dante downloads](https://www.getdante.com/resources/software-downloads/), [Shure Designer archive](https://www.shure.com/en-US/support/downloads/software-firmware-archive/designer), [Shure Wireless Workbench archive](https://www.shure.com/en-US/support/downloads/software-firmware-archive/wireless-workbench), [Shure Update Utility](https://www.shure.com/en-US/products/software/shure_update_utility), [Shure Discovery](https://www.shure.com/en-US/products/software/shure-discovery), [legacy Microflex Wireless Software](https://www.shure.com/en-US/products/software/microflex_wireless_software), [Sennheiser Control Cockpit release notes](https://docs.cloud.sennheiser.com/en-us/control-cockpit/control-cockpit/release-notes.html), [Sennheiser WSM](https://www.sennheiser.com/en-us/catalog/products/software/wireless-systems-manager/wsm-111113), [Extron Toolbelt](https://www.extron.com/product/software/toolbelt), and [Extron PCS](https://www.extron.com/article/sw_79-562-01).

## AV vendor operating boundaries

- Canvas uses `SameMajorMinor` detection because current Canvas releases are paired with the corresponding Tesira software and firmware line. AV Workstation Toolkit surfaces the paired vendor release, but the operator must confirm the deployed Tesira line before using the handoff.
- Vocia is life-safety paging software. AV Workstation Toolkit reports software currency and opens Biamp's page, but firmware installation, configuration extraction, system backup, and custom-audio restoration remain outside AV Workstation Toolkit.
- Shure Designer, Wireless Workbench, Update Utility, and Discovery are separate tools: system design/configuration, RF coordination, firmware transfer, and lightweight device discovery respectively. AV Workstation Toolkit does not collapse those roles or initiate hardware firmware changes.
- The legacy Microflex Wireless application is Optional because it exists for original MXWAPT2/4/8 systems whose Flash-based web interface is no longer usable.
- Sennheiser Control Cockpit remains the normal installed-system manager. WSM is the additional RF-planning/live-wireless tool for compatible EW-DX, G3/G4, Digital 6000, and related systems. Plain EW-D uses Sennheiser Smart Assist rather than WSM; Smart Assist is a mobile application and is not packaged by this Windows catalog.

## Crestron workflow

1. AV Workstation Toolkit models MasterInstaller as the parent and VisionTools Pro-e, SIMPL Windows, Crestron Database, Device Database, Toolbox, Smart Graphics, and DM NVX Tool as independently detectable children. It reads Crestron's public `MasterInstallerSFTP.xml` feed over HTTPS and accepts only their product IDs `1`, `2`, `9`, `10`, `137`, `400`, and `406`.
2. The XML parser prohibits DTDs, caps the response at 2 MiB, requires every allowlisted product, constrains files beneath `/software`, matches each file path to its numeric version, and accepts only `.exe` payloads within the configured size limit.
3. Before any credential is sent, the packaged launcher connects with a deliberately rejected no-authentication probe and displays the server's SHA-256 host-key fingerprint. First use requires explicit trust. A changed fingerprint blocks authentication until the user explicitly replaces the saved host identity.
4. The user enters an authorized Crestron username and password. The password crosses the process boundary only through redirected standard input. It is not included in command-line arguments, environment variables, exported plans, or logs.
5. Selecting **Save on this computer** writes a generic credential to Windows Credential Manager only after authentication succeeds. The target is scoped to AV Workstation Toolkit, host, port, and username. **Forget saved** deletes only that exact target.
6. The SFTP download is restricted to the selected catalog path and a near-catalogued size bound. The result remains a `.download` file until the PowerShell core confirms the configured Crestron Authenticode publisher, records SHA-256 metadata, and atomically finalizes the cache entry.

Crestron's [MasterInstaller documentation](https://docs.crestron.com/en-us/9450/Content/Master%20Installer.htm) remains the operational reference. AV Workstation Toolkit does not provide credentials, bypass account requirements, redistribute Crestron software, or install a downloaded product.

## Download cache and trust data

Packaged runs use:

```text
%LOCALAPPDATA%\AVWorkstationToolkit
|-- vendor-cache\<package-id>\<version>\<installer>
|-- vendor-cache\<package-id>\<version>\<installer>.avworkstationtoolkit.json
`-- trusted-sftp-hosts.json
```

Each reusable payload must still match its recorded SHA-256 hash and the provider's Authenticode publisher rule. Reparse-point paths are rejected. Credentials are not stored in these files; Windows Credential Manager owns them. The bridge uses the Windows Credential APIs documented by Microsoft for [writing](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew), [reading](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credreadw), and [deleting](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-creddeletew) generic credentials.

Deleting a cached installer only removes the local handoff copy; it does not uninstall software. Remove trusted host data or a saved credential only when the exact target is understood. A host-key mismatch should be verified through an independent Crestron channel before trust is replaced.

## Catalog rules

- Do not add VirtualBox. It is explicitly excluded.
- Do not turn account-gated or redistribution-restricted software into a bundle without written rights.
- Do not weaken an HTTPS host, SFTP root, product ID, size, version, or publisher constraint to work around a vendor failure.
- Do not add a second Crestron credential or download implementation for a child already delivered by MasterInstaller.
- Keep broad awareness records in `commercial-av-catalog.json`; move one into the operational manifest only with detection and provider evidence.
- When a vendor page changes, update the smallest relevant pattern and add a deterministic fixture before accepting the new live result.
- Uninstall inventory reads HKLM 64-bit, HKLM 32-bit, and HKCU independently. A failed source produces incomplete/unavailable warning semantics for only the records whose conclusion depends on it; a successful match from another source remains valid.
- Java remains dependency inventory until a specific application identifies its required vendor and JDK/JRE line.
- Dell/Waves remains OEM inventory because generic replacement can break the laptop's approved audio stack.
