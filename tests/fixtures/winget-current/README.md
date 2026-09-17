# Current WinGet update output fixtures

`1.29.290-redirected.txt` is a bounded, non-sensitive subset of the Windows
development machine's redirected fixed production `list --upgrade-available
--source winget --disable-interactivity --accept-source-agreements` output,
including its long-name spacing and separate explicit-target table. The appended
count summaries exercise Microsoft's emitted resource strings, not local pins.

`1.29.290-firewire-spaced-version.txt` retains the exact production package row
that exposed a spaced installed-version cell (`< 0.0.1`). Its width-sentinel row
only preserves the surrounding table's observed header boundaries; assertions
identify the FireWire record by package ID through both parser entry points.

The other fixtures isolate Source/no-Source tables, explicit-target-only results,
no eligible updates, and a blocked-pin table. The blocked table is defensive
coverage: the production invocation does not request `--include-pinned`.

Summary wording/section behavior comes from Microsoft's
[WinGet resources](https://github.com/microsoft/winget-cli/blob/master/src/AppInstallerCLIPackage/Shared/Strings/en-us/winget.resw)
and [ReportListResult](https://github.com/microsoft/winget-cli/blob/master/src/AppInstallerCLICore/Workflows/WorkflowBase.cpp).
Column behavior follows WinGet 1.29.290
[TableOutput](https://github.com/microsoft/winget-cli/blob/v1.29.290/src/AppInstallerCLICore/TableOutput.h),
which pads cells by Unicode display width after computing widths from the full
result set.
These fixtures contain no machine paths, logs, accounts, or credentials. They are
parser evidence, not execution-policy inputs or permission to change pins.
