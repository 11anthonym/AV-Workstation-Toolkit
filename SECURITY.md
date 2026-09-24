# Security policy

AV Workstation Toolkit treats release provenance and the boundary between
catalog knowledge and execution authority as security controls. Please report
suspected vulnerabilities responsibly.

## Supported versions

The first public release is the unsigned beta `1.1.1-beta.1`. Security fixes
are applied to the current `main` branch and released in the next beta or
release. Historical 1.1.0 and earlier packages are unsupported and must not be
treated as current release artifacts. Reports against an older release may be
asked to reproduce against the latest release before a fix is prepared.

## Reporting a vulnerability

GitHub Private Vulnerability Reporting is not currently verifiable for this
repository, and no dedicated security email address has been established.

- For a non-sensitive security question or hardening suggestion, open a
  [GitHub issue](https://github.com/11anthonym/AV-Workstation-Toolkit/issues).
- For a potentially exploitable vulnerability, first check the repository's
  **Security** tab for a **Report a vulnerability** option. Use it only if
  GitHub actually presents that private form.
- If no private form is available, open a minimal issue asking `@11anthonym`
  to establish a private reporting channel. Do not include exploit details,
  credentials, customer data, private hostnames, proprietary logs, or other
  sensitive evidence in that issue.

Include the affected version or commit, Windows version, concise impact,
reproduction prerequisites, and a minimal proof of concept only through the
agreed private channel. Never include passwords, tokens, SSH credentials,
certificate private keys, customer data, or an unredacted diagnostic/snapshot
export.

Please allow reasonable time for validation and a coordinated fix before
publishing exploitable details. The maintainer will acknowledge the report,
assess affected boundaries, and coordinate disclosure timing when a private
channel exists. This is a request for responsible coordination, not a promise
of a specific response deadline or bounty.

## Security boundaries

AV Workstation Toolkit intentionally preserves these boundaries:

- standard-user application execution and rejection of elevated startup;
- exact-ID WinGet allowlisting and one-package-at-a-time revalidation;
- no uninstall, rollback, arbitrary command, bulk-install, or generic
  download-and-execute interface;
- manual/non-actionable commercial-catalog and external-provider records;
- risk-sensitive pending-reboot enforcement;
- strict request, path, reparse-point, hash, Authenticode publisher, provider,
  and package-ID validation;
- SFTP host-key validation and Credential Manager isolation;
- credential redaction in operational and diagnostic text;
- deterministic, versioned, hash-verified embedded-runtime extraction;
- explicit unsigned-development versus signature-required-production release
  modes;
- no security/MDM/VPN/EDR/operating-system management or endpoint-protection
  bypass behavior.

An entry in the commercial AV catalog is knowledge, not permission to download,
install, update, or execute the corresponding software.

See the [architecture and safety model](docs/AV-Workstation-Toolkit-Architecture-and-Safety.md),
[security audit](docs/AV-Workstation-Toolkit-Security-Audit.md),
[endpoint-security behavior](docs/Endpoint-Security-Behavior.md),
[privacy policy](PRIVACY.md), and
[code signing policy](docs/Code-Signing-Policy.md) for the maintained design and
release controls.
