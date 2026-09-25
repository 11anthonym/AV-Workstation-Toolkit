# Endpoint-security behavior

This document describes the process, file, registry, credential, network, and elevation activity an endpoint-security analyst should expect from a normal AV Workstation Toolkit 1.x release. It is an operational baseline, not an exhaustive security audit and not a request for security-product exclusions.

AV Workstation Toolkit is a local-only, standard-user commercial-AV workstation manager. It has no HTTP command listener, service, scheduled task, telemetry endpoint, generic command runner, uninstall path, or mechanism for disabling endpoint protection. Knowledge in the commercial catalog does not grant download or execution authority.

The repository is public, and its first public release is the unsigned beta
`1.1.1-beta.1`. The project is licensed under [Apache-2.0](../LICENSE), and
current development, release-candidate, and beta artifacts are not signed by
SignPath Foundation. These are release-governance
facts, not changes to the runtime safety model. See the project
[privacy policy](../PRIVACY.md), [security policy](../SECURITY.md),
[code signing policy](Code-Signing-Policy.md), and
[third-party notices](../THIRD-PARTY-NOTICES.md).

## Normal startup and inventory

The primary packaged process is `AVWorkstationToolkit.exe`, a self-contained x64 .NET 10 Windows executable whose version, product, project-publisher description, and original filename are carried in normal Windows version resources. The MSI installs the same executable as `%ProgramFiles%\AVWorkstationToolkit\AVWorkstationToolkit.exe`; the standalone EXE and portable ZIP run it from the operator-selected location.

On startup the launcher:

1. rejects elevated execution;
2. prepares the deterministic `%LOCALAPPDATA%\AVWorkstationToolkit\runtime\<AVWorkstationToolkit-version>` directory;
3. compares every embedded compiled worker, catalog, and notice with its embedded SHA-256 value;
4. leaves matching files untouched and atomically repairs missing or modified files with a non-executable `.tmp` file in the same controlled directory;
5. rejects traversal and reparse-point paths; and
6. starts the compiled C# WPF App in-process with the canonical data root and embedded compiled-worker SHA-256.

Normal startup does not launch PowerShell, and the packaged runtime contains no PowerShell UI, worker, vendor bridge, or recovery switch. The launcher removes only an exact allowlist of obsolete extracted runtime files from earlier versions; it does not execute them or delete unrelated application data. Application-owned runtime content is always the versioned LocalAppData tree above.

Read-only inventory can directly start only the Microsoft-signed `winget.exe` resolved from the installed `Microsoft.DesktopAppInstaller` package under protected `Program Files\WindowsApps`. Expected inventory arguments are fixed combinations of `--version`, `export`, `list`, source selection, agreement acceptance, and disabled interactivity. A temporary `AVWorkstationToolkit-winget-export-*.json` data file may be created beneath the current Windows temporary directory and is removed after parsing; it is never executable.

External application inventory reads these uninstall locations independently:

- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`
- `HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall`
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall`

Pending-reboot diagnostics read the Windows Update `RebootRequired` and Component Based Servicing `RebootPending` keys. AV Workstation Toolkit does not write those keys. The optional workstation snapshot command can directly run `%SystemRoot%\System32\dsregcmd.exe /status`; this is not part of ordinary desktop startup.

## User-requested install or update

The compiled WPF App atomically writes one constrained JSON request beneath `%LOCALAPPDATA%\AVWorkstationToolkit\logs\requests` and directly starts the exact extracted `worker\AVWorkstationToolkit.Worker.exe`. The worker must be a regular non-reparse file at the expected versioned-runtime location, match the hash supplied by the embedded bootstrap, and carry the reviewed compiled-worker product identity. Its fixed argument vector contains only `--production`, the canonical data/application roots, and the correlated canonical request path. No raw executable, command line, URL, or WinGet argument is accepted from the UI.

The compiled worker remains a standard-user process. It rebuilds live inventory and policy, permits only `Install` or `Update`, revalidates each exact catalogued WinGet package ID before every package, applies reboot/risk/hold policy, constructs the exact one-ID WinGet vector internally, and post-verifies resulting state. AV Workstation Toolkit never requests its own elevation. An individual installer selected and launched by WinGet can request normal Windows/UAC elevation; descendants created by that installer are controlled by WinGet, Windows Installer, and the installer itself, not chosen by a generic AV Workstation Toolkit process API.

Expected action evidence includes the request JSON, progress JSONL, result JSON, cancellation marker when requested, and a redacted `.winget.log` in `logs\requests`. Output is sanitized before it reaches UI activity text or logs. AV Workstation Toolkit does not implement uninstall or automatic rollback.

## External package handoff

Manual and external-provider records never enter the WinGet worker. Explicit operator actions can cause only these direct handoffs:

- `%SystemRoot%\explorer.exe` opens the logs directory or selects a verified cached/bundled file. AV Workstation Toolkit does not execute that file.
- The Windows HTTPS association opens a validated absolute HTTPS catalog/vendor page without embedded credentials.
- Compiled HTTPS/SFTP/Credential Manager services handle catalog-authorized delivery in-process.

Compiled vendor services can perform bounded HTTPS or authenticated SFTP information/delivery operations allowed by validated catalog metadata. HTTPS uses explicit allowed hosts, HTTPS-only bounded redirects, response-size limits, timeouts, version extraction rules, and publisher/hash validation. SFTP validates the exact host-key fingerprint before credential lookup, then applies the configured host, port, remote root, product IDs, and size bounds. No generic runtime URL becomes an execution path.

SFTP credentials use Windows Credential Manager generic credentials named `AVWorkstationToolkit:VendorSftp:<host>:<port>:<username>`. Compiled services scope exact host/port/username lookups, clear unmanaged password memory after Credential Manager calls, and exclude secrets from logs, diagnostics, reports, release metadata, and process arguments. Saving or deleting a credential is an explicit operator action. Trusted SFTP host fingerprints are stored separately in `%LOCALAPPDATA%\AVWorkstationToolkit\trusted-sftp-hosts.json`.

During the rename transition, the compiled credential service may read a matching legacy `AVinite:VendorSftp:` target only after the new target is absent. New saves always use the canonical prefix, and an explicit delete removes both targets. No credential secret is migrated into the filesystem.

## Files, reports, and logs

Normal packaged operation can create or update only bounded application-owned content below `%LOCALAPPDATA%\AVWorkstationToolkit` plus an explicit report destination selected by the operator:

```text
%LOCALAPPDATA%\AVWorkstationToolkit
├── runtime\<version>       verified worker, catalogs, and notices
├── logs
│   └── requests            constrained requests, progress, result, cancel, and WinGet logs
├── reports                 exported plan and sanitized diagnostics JSON
├── vendor-cache            bounded downloads, `.download` staging, hashes, and verified files
├── ReferenceCatalog        signed descriptive catalog revision, atomic state, and staging
├── trusted-sftp-hosts.json pinned host identities
├── snapshots               optional read-only workstation evidence bundles
└── launcher-error.log      launcher failures when startup cannot continue
```

Unchanged runtime files are not rewritten on every launch. Runtime repair uses same-directory atomic replacement and removes its transient `.tmp` file. Vendor downloads stage as non-final `.download` files under `vendor-cache`, never under arbitrary paths, and are not renamed to their final name until policy, size, path, hash, and Authenticode checks succeed.

AV Workstation Toolkit itself does not write application configuration to the registry. A per-machine MSI uses normal Windows Installer registration, Add/Remove Programs, Program Files, and Start-menu records and therefore normally requests administrative approval. MSI uninstall removes AV Workstation Toolkit's installed program files and shortcut; it does not implement application-management uninstall features. Windows Installer controls its own transaction and temporary-file behavior.

## Diagnostics

The compiled in-app Diagnostics view is read-only. It reports sanitized application/runtime identity, Windows/process architecture, WinGet path/version/inventory availability, privilege state, reboot signals, each uninstall-registry source, and catalog/status counts. Copy/export uses structured redaction and does not read Credential Manager secrets. The optional PowerShell snapshot script is separate operator tooling; it collects a broader bounded evidence set and deliberately excludes credentials, environment dumps, command history, project files, and application payloads.

## Network categories

AV Workstation Toolkit introduces no analytics, cloud telemetry, update beacon, local listener, or background service. Network-capable paths are limited to:

- WinGet's configured Microsoft/community package-source behavior during the
  automatic startup inventory refresh, a manual refresh, or an authorized
  exact-ID action;
- official HTTPS vendor/product/release pages and the configured public parent
  catalog compiled into operational provider policy; these bounded checks run
  during startup and manual refresh for the configured operational records;
- allowlisted HTTPS installer hosts only after an explicit operator download
  request, with redirect, size, version, hash, and publisher controls;
- exact authenticated SFTP provider hosts with host-key validation, contacted
  for probing, credential testing, or download only through the explicit
  provider workflow; and
- an operator-opened official HTTPS page handled by the user's normal browser.

The broad awareness catalog is non-actionable and does not cause hundreds of
vendor sites to be crawled. Launching the application starts the documented
inventory refresh, so the privacy policy does not claim that every outbound
request requires a separate click. Release and process policy is
code/compiled-data controlled; a provider supplies information or an approved
handoff but never decides that an application is permitted to install.

## Build and release behavior

Build/release behavior occurs only on a developer or CI machine, not on a workstation managed by AV Workstation Toolkit during normal use. `Build-AVWorkstationToolkit.cmd` starts the reviewed static build script with inbox Windows PowerShell and `RemoteSigned`. The build directly runs the selected .NET 10 SDK for locked restore, vulnerability reporting, publish, and pinned WiX packaging; optionally runs the Windows SDK `signtool.exe`; and can explicitly request a local Defender custom scan of the completed release directory. It does not alter Defender policy or create exclusions.

Unsigned development builds are identified as such. A production-channel build requires either a validated external certificate-store signer or the complete expected-signer-validated SignPath artifact set. The tagged workflow signs and RFC3161-timestamps the embedded worker, EXE, and MSI, verifies them, and records signer/timestamp state. Current artifacts remain unsigned and repository preparation is not SignPath approval. Hosted signing preserves the exact signed EXE from inside the signed MSI and requires deliberate approval as described in the [code signing policy](Code-Signing-Policy.md).

## Release distribution boundary

The normal release set contains exactly eight assets: the standalone EXE, MSI,
one-file portable ZIP, versioned Apache-2.0 project license, versioned
third-party notices, CycloneDX SBOM, release manifest, and SHA-256 checksum
list. The checksum list covers the other seven assets and intentionally does
not hash itself. The manifest records the
source commit, dirty-tree state, selected SDK, actual .NET runtime/apphost,
dependency-audit result, artifact hashes, and explicit signature/timestamp
state. The tagged release workflow refuses to replace existing release assets
in place.

Commercial AV catalog records are descriptive metadata. Normal releases do
not contain catalogued vendor applications, external installers, vendor-cache
payloads, credentials, logs, reports, diagnostics, snapshots, customer data,
or signing material. Optional offline bundles are separate controlled
artifacts and may contain only locally supplied payloads for which an operator
has separately confirmed redistribution authority and passed the existing
path, hash, and publisher checks. No private key, certificate password, local
username, or developer path is written to release metadata.

## SmartScreen, EDR, and false positives

Authenticode signing does not guarantee zero Microsoft SmartScreen, Defender, CrowdStrike, application-control, or EDR warnings. Reputation and organizational policy are external to AV Workstation Toolkit; a new certificate or binary can initially have limited reputation. Deterministic, timestamped, consistently identified releases help establish a trustworthy publisher history, but a security team can still block a legitimate artifact under local policy.

The maintained [Defender false-positive investigation](records/Defender-False-Positive-Investigation.md)
separates the exact historical detection from later rebuilt artifacts. A clean
scan of different bytes is useful current evidence, but it neither reproduces
nor disproves the historical classification.

For a suspected false positive, a release operator should:

1. verify the artifact SHA-256 against the official checksum and release manifest;
2. verify the Authenticode signature, signer, and timestamp;
3. reproduce the detection with the exact unmodified release binary;
4. record the detection name, security product, engine/signature version, and policy context;
5. verify that the binary came from the official locked build and commit recorded in the manifest;
6. review the expected process tree and AV Workstation Toolkit's redacted logs against this document;
7. submit the exact sample through the security vendor's official false-positive process when appropriate; and
8. never instruct users to disable endpoint protection or add an exclusion.

## Compiled-runtime migration status

The compiled C# WPF App, Domain/Application core, Windows inventory providers, vendor services, diagnostics, and independently validating worker are the complete production runtime. The former PowerShell application runtime is retired from packaging and the process-launch contract. PowerShell remains only for build, QA, catalog authoring, optional snapshot and readiness tooling, and legacy behavior characterization; no repository script installs or updates software. The isolated one-package action worker remains independently constrained.
