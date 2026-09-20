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
$timeoutMilliseconds = 180000
$process = [Diagnostics.Process]::Start($startInfo)
try {
    $standardError = $process.StandardError.ReadToEndAsync()
    $exited = $process.WaitForExit($timeoutMilliseconds)
    if (-not $exited) {
        # Windows PowerShell 5.1 runs on .NET Framework, where Process.Kill has no tree-killing
        # overload, so Kill() ends the application process itself - which is what holds the compiled
        # executable open and fails the next build. Any winget child it started is read-only and
        # bounded by that provider's own timeout. The wait below is what makes the kill observable:
        # Kill() only requests termination, so returning immediately would report a timeout while the
        # process is still alive.
        $termination = ''
        try {
            $process.Kill()
            if (-not $process.WaitForExit(15000)) {
                $termination = ' The process was still running 15s after Kill; a later build may fail on a locked executable.'
            }
        } catch {
            $termination = " Terminating the process failed: $($_.Exception.Message)"
        }
    }
    # Read the redirected stream only once the pipe is able to close, so a process that survived
    # termination cannot replace the timeout report with an indefinite wait.
    $diagnostic = if ($standardError.Wait(5000)) { $standardError.Result.Trim() } else { '(standard error was not readable)' }
    if (-not $exited) {
        throw ('Compiled WPF live read-only integration timed out after {0}s. The application did not exit; check for a blocking dialog or a provider that never completed.{1} Captured diagnostic: {2}' -f
            ($timeoutMilliseconds / 1000), $termination, $diagnostic)
    }
    if ($process.ExitCode -ne 0) {
        throw "Compiled WPF live read-only integration failed with exit code $($process.ExitCode). $diagnostic"
    }
    # The per-provider evidence is reported on success too: a provider the contract classified as
    # not applicable must be visible here rather than passing silently.
    foreach ($line in @($diagnostic -split '\r?\n' | Where-Object { $_.Trim().Length -gt 0 })) { Write-Output $line.Trim() }
    Write-Output 'COMPILED_APP_READONLY_OK catalog=complete providers=bounded mutation=absent'
}
finally { $process.Dispose() }
