# Microsoft Defender false-positive investigation

**Record date:** 2026-08-26  
**Scope:** historical AV Workstation Toolkit 1.1.1 detection and a later rebuilt
development artifact  
**Safety boundary:** read-only Defender status/threat queries and a supported
custom scan; no exclusions, policy changes, quarantine restoration, execution,
or evasion work

This record keeps two different specimens separate. A clean scan of a rebuilt
file is current evidence about that file only. It does not prove that an older
detection was incorrect, identify which signature or model decision changed,
or make different hashes the same binary.

## Historical specimen

The following evidence was retained from the original incident record supplied
for this investigation:

| Field | Recorded value |
|---|---|
| Filename | `AV-Workstation-Toolkit-1.1.1-win-x64.exe` |
| SHA-256 | `47DF422857F9A7B92902469ABB841E6E7942DD2978C0998548C00F7419AD95A8` |
| Source commit | `e5300e490f997301b8d6c7bfb51bcca3f46c2dae` |
| Authenticode | `NotSigned` |
| Defender classification | `Trojan:Win32/Bearfoos.A!ml` |
| Threat ID | `2147731250` |
| Threat status | `ThreatStatusID=7` |
| Detection source | `DetectionSourceTypeID=2` |
| Execution evidence | `CurrentThreatExecutionStatusID=0`; `ProcessName=Unknown` |
| Remediation record | `ActionSuccess=True`; `CleaningActionID=9`; `ThreatStatusErrorCode=0`; `AdditionalActionsBitMask=0` |

The build-computer and destination-computer copies recorded during the incident
had the same SHA-256. The unknown process and zero current execution status,
together with the matching file hashes on both systems, are evidence consistent
with file/static or on-access classification before observed execution. That is
an engineering interpretation of the recorded fields, not proof of Defender's
internal model path or a claim about undocumented numeric enum meanings.

The exact historical file was not present in the current repository release
directory or the operator's Downloads directory during this pass. Current
`Get-MpThreatDetection` and `Get-MpThreat` queries also returned no record for
Threat ID `2147731250`. The quarantined specimen was not restored, rebuilt, or
executed. Therefore no byte-for-byte historical reproduction was performed.

A Microsoft false-positive submission was made separately. **Submission status
not independently verified during this repository pass.** No duplicate sample
was submitted.

## Current rebuilt specimen

The current specimen was produced by the ordinary development build and scanned
as part of the complete release directory using the repository's supported
endpoint-trust command:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tests\Test-EndpointTrust.ps1 `
  -ReleaseRoot .\artifacts\release\1.1.1 `
  -ScanWithDefender
```

The test resolves a Microsoft-signed `MpCmdRun.exe`, validates its publisher,
and requests custom scan type 3. It fails on a nonzero scanner exit code and
does not alter Microsoft Defender settings.

| Field | Observed value |
|---|---|
| Filename | `AV-Workstation-Toolkit-1.1.1-win-x64.exe` |
| SHA-256 | `A95321BE3C193C1CC67B1BC0C635CEDAD07D9DB9C3E61ADB80042589FDF5E7C5` |
| Size | 20,022,549 bytes |
| Source commit in release manifest | `5796c43f65d0076275af1d503682b955153316e5` |
| Build channel / source state | `Development`; clean |
| Authenticode | `NotSigned` |
| Defender platform and scanner | `4.18.26070.9` |
| Defender engine | `1.1.26070.7` |
| Defender signature | `1.457.350.0`, updated 2026-08-26 05:32:34 local time |
| Scan result | Scanner exit 0; `DEFENDER_SCAN_OK result=no-detection-reported` |

The historical and current SHA-256 values differ. The current clean result does
not reproduce or disprove the historical classification, and no controlled
evidence establishes whether signatures, cloud classification, reputation,
source changes, or another variable accounts for the difference.

## Engineering characteristic review

The historical commit and current launcher retain the same principal packaging
model: a trimmed, self-contained .NET 10 x64 single-file Windows executable with
compression disabled, native-library self-extraction enabled, and embedded
PowerShell, XAML, catalog, policy, notices, and managed SSH/SFTP dependencies.
At runtime the launcher deterministically restores hash-verified static content
under `%LOCALAPPDATA%\AVWorkstationToolkit\runtime\<version>` and starts the
fixed inbox Windows PowerShell frontend. A separately constrained worker process
performs exact-ID WinGet actions, and `--vendor-bridge` is a bounded self-helper
mode for approved HTTPS/SFTP/Credential Manager operations.

The rankings below describe plausible classifier contributors, not findings of
malicious behavior:

| Confidence | Observed fact | Engineering inference and limit |
|---|---|---|
| High | No individual characteristic has controlled evidence proving that it caused the historical classification. | A causal high-confidence claim would require the exact specimen and controlled variants or authoritative Microsoft disposition; neither is available. |
| Medium | The historical specimen was unsigned, and the product had no established signed release identity. | Missing publisher authentication and limited reputation plausibly increased ML uncertainty, but the investigation cannot show their weight or that signing alone would have changed the result. |
| Medium | One uncompressed self-contained executable bundles a native apphost, trimmed .NET runtime, managed networking/cryptography libraries, and many static resources; it writes verified PowerShell/XAML/catalog resources to a versioned LocalAppData cache and launches fixed PowerShell child processes. | That legitimate combination presents more static and process-tree features than a conventional small launcher. It may have contributed to a heuristic classification, but no Defender evidence identifies a particular feature. |
| Low | The same binary exposes a bounded vendor-bridge mode and uses HTTPS, SFTP, host-key validation, and Windows Credential Manager. | Networking and credential API imports expand the inspectable feature surface, but policy and tests constrain them; there is no evidence these capabilities caused the detection. |
| Low | The executable is approximately 20 MB and uses the standard .NET single-file bundle/overlay format. | Size or PE overlay shape alone is insufficient evidence, and changing bytes merely to alter classification would be inappropriate. |

## Conclusion and response boundary

No application or build change is justified solely by the historical alert.
The existing deterministic extraction, hash repair, fixed process-launch
contract, standard-user requirement, bounded provider transport, credential
isolation, signed-production gate, release manifest, SBOM, and checksum model
independently improve transparency and inspectability and should remain.

The exact historical sample and Microsoft submission remain the appropriate
vendor-remediation evidence. If the classification recurs, retain the exact
binary without executing it, record its hash and current Defender versions,
verify build provenance and Authenticode state, compare the observed process
tree with [Endpoint-Security-Behavior.md](Endpoint-Security-Behavior.md), and use
Microsoft's official sample-submission process. Do not add exclusions, disable
protection, obfuscate or pack payloads, mutate binaries to evade signatures, or
weaken the production signing and endpoint-trust gates.
