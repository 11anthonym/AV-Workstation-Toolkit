<#[.SYNOPSIS] Exercises the compiled WPF composition with live read-only Windows providers and exits. #>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appPath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\bin\Release\net10.0-windows\win-x64\AVWorkstationToolkit.App.exe'
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) { throw "Compiled WPF executable was not built: $appPath" }
# Started directly rather than through Start-Process so the sanitized startup diagnostic on standard
# error is captured and reported; a failure that only prints an exit code is not actionable.
$startInfo = [Diagnostics.ProcessStartInfo]::new($appPath)
$startInfo.Arguments = '--read-only-check'
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($startInfo)
$standardError = $process.StandardError.ReadToEndAsync()
if (-not $process.WaitForExit(180000)) {
    try { $process.Kill($true) } catch { }
    throw 'Compiled WPF live read-only integration timed out. The application did not exit; check for a blocking dialog or a provider that never completed.'
}
$diagnostic = $standardError.Result.Trim()
if ($process.ExitCode -ne 0) {
    throw "Compiled WPF live read-only integration failed with exit code $($process.ExitCode). $diagnostic"
}
Write-Output 'COMPILED_APP_READONLY_OK catalog=complete providers=bounded mutation=absent'
