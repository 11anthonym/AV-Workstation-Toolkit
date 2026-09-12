# Privacy

AV Workstation Toolkit is a local Windows workstation manager. It has no
telemetry, analytics, advertising, crash-reporting service, cloud account, or
inbound command server. It does not send diagnostics, logs, snapshots, plans,
credentials, or software inventory to the project maintainers.

This policy describes version 1.1.1 as implemented in the maintained source.
It deliberately does not claim that the application never uses the network:
inventory and vendor-provider workflows can make bounded outbound requests.

## Information stored locally

Packaged execution keeps application-owned mutable state beneath
`%LOCALAPPDATA%\AVWorkstationToolkit`, including:

- the deterministic, versioned, hash-verified runtime cache;
- action requests, progress records, results, and operational logs;
- user-exported plans, diagnostics, and workstation snapshots;
- trusted SFTP host fingerprints;
- downloaded vendor payloads and their hash/publisher metadata, when an
  operator explicitly requests an allowed download;
- signed descriptive reference-catalog revisions, validation state, staging,
  and quarantine evidence when catalog updates are configured or imported.

The installer deliberately leaves this per-user evidence after uninstall so
an operator can review retention requirements. A packaged launch can read a
bounded set of recognized files from legacy `%LOCALAPPDATA%\AVinite` state and
copy only validated items into the new root. It does not move or delete the
legacy directory.

Source-checkout execution uses repository-local `logs`, `reports`, and
`snapshots` locations unless an explicit data root is supplied. These folders
are ignored by Git because they may identify the workstation, user paths,
installed software, devices, services, drivers, listeners, and network state.

## Network operations

| Operation | Trigger | Destination and protocol | Data sent | Data received |
|---|---|---|---|---|
| WinGet inventory and update check | Automatic during startup refresh and any manual refresh | Microsoft Desktop App Installer/WinGet and its configured `winget` source over the network behavior implemented by WinGet | Exact inventory command options, normal source request metadata, and information WinGet itself requires | Installed-package export and current package-source metadata |
| Official vendor release check | Automatic during startup refresh and any manual refresh for configured records | Catalogued HTTPS vendor page; redirects remain HTTPS and on the original host | Normal TLS/HTTP request metadata and the `AVWorkstationToolkit/1.1` user agent | Bounded vendor HTML or XML used only for release awareness |
| Vendor page handoff | Explicit **Open vendor download** action | Catalogued official HTTPS URI opened in the user's default browser | Browser-controlled request data | Vendor web page |
| Controlled HTTPS package download | Explicit confirmation for an eligible manual external package | Catalogued HTTPS source and allowlisted redirect hosts | Normal TLS/HTTP request metadata and product user agent | One size-bounded installer written first as a scoped temporary cache file, then accepted only after publisher validation |
| SFTP catalog metadata | Startup/manual refresh for the configured parent-provider catalog | Catalogued HTTPS catalog endpoint | Normal TLS/HTTP request metadata | Size-bounded product metadata |
| Signed reference-catalog check/update | Explicit **Check now** or **Update catalog** action after an owner configures the production channel | Two exact signed-metadata URLs and one signed-bundle URL on a compiled HTTPS host allowlist | Normal TLS/HTTP GET request metadata; no inventory, credentials, diagnostics, or user data | Bounded signed metadata/signature and a bounded descriptive `.avwtcatalog` bundle |
| SFTP host probe | Explicit authenticated-provider workflow | Catalogued SFTP host and port | SSH negotiation without a saved password | Presented SSH host key/fingerprint |
| Authenticated SFTP test or download | Explicit operator action after host-key approval and credential entry or saved-credential selection | Catalogued SFTP host, port, remote root, and allowlisted product path | Username and password through encrypted SSH authentication, plus the bounded file request | Authentication result or one size-bounded installer |
| WinGet install/update | Explicit approved package action | WinGet's pinned `winget` source and package-specific installer path | One exact allowlisted package ID and normal WinGet/source request data | Package metadata and the publisher's installer through WinGet |
| Build/release operations | Developer or hosted workflow action, not application runtime | NuGet, GitHub Actions/GitHub Releases, configured RFC3161 timestamp service, and optional local Microsoft Defender | Source/dependency/artifact requests appropriate to the selected build step; a signing digest/request as implemented by the signing service or local tool | Locked dependencies, build actions, timestamp response, and release-service responses |

AV Workstation Toolkit has no runtime GitHub update checker. Repository and
release links are opened only through the user's browser. It has no generic
HTTP upload, POST, PUT, PATCH, web-hook, telemetry, or analytics path.

The signed reference-catalog transport is currently fail-closed and
unconfigured because this repository is private and the application must not
embed a GitHub credential. If enabled later, it can contact only the exact
public HTTPS origins compiled into the signed application, follows no
redirects, uses no ambient credentials, and runs only after an explicit user
check. Offline import performs no network request.

Because startup can automatically query WinGet and configured official vendor
release pages, the project does not use the broader statement that every
network transfer requires a separate, specific user request. Launching the
application starts a documented inventory refresh.

## Credentials

Authenticated SFTP credentials are handled by the compiled vendor-delivery
services and Windows Credential Manager. The compiled UI passes a scoped
in-memory credential only to the typed SFTP service; passwords do not enter
process arguments, environment variables, logs, diagnostics, or protocol
artifacts. A saved secret remains protected in the current Windows user's
Credential Manager. During an explicit SFTP authentication, the credential is
necessarily presented to the configured server through the encrypted SSH protocol.
It is not sent to AV Workstation Toolkit maintainers, GitHub, WinGet,
another vendor, or release artifacts.

Host-key validation is mandatory. A first-use or changed host identity requires
an explicit decision and remains scoped to the configured host and port.

## Logs, diagnostics, plans, and snapshots

Operational text passes through credential-oriented redaction for passwords,
tokens, API keys, bearer authorization values, and URI user information.
Diagnostics replace known user-profile path prefixes with `%USERPROFILE%` or
`%LOCALAPPDATA%` and omit the workstation name. They can still contain OS and
runtime versions, package counts, provider status, and non-profile tool paths.

Plan exports and the optional snapshot tool are intentionally more detailed.
They can include a computer name, installed packages, hardware, domain/network
metadata, services, drivers, listeners, and local paths. They are created only
by an explicit local export/snapshot action and are never uploaded by the
application. Treat them as sensitive operational evidence and inspect them
before sharing.

Redaction reduces accidental credential disclosure; it is not a guarantee that
arbitrary free-form installer or operating-system error text contains no
personal information. Operators remain responsible for reviewing exported
evidence before transferring it.

## Personal information

The application does not ask for a name, email address, account with this
project, analytics identifier, or advertising identifier. Windows, WinGet,
vendor sites, or an authenticated vendor account can process network metadata
under their own terms when the corresponding documented operation runs.

## Changes to this policy

Any future telemetry, crash reporting, cloud service, upload path, or material
change to automatic network behavior requires an explicit source change,
security review, documentation update, and regression-test update. It must not
be introduced silently.
