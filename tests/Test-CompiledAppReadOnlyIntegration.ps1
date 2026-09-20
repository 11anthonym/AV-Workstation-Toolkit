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
        # overload. The application starts winget as a direct child and owns that child's timeout
        # watchdog in its own process, so ending only the application orphans a winget that is still
        # running. taskkill.exe takes the tree down instead; it is an operating-system executable, so
        # it does not depend on the .NET version this host runs on.
        $termination = ''
        if ($process.HasExited) {
            # The application finished between the timeout expiring and this termination attempt.
            # Naming its process tree now would describe a process that no longer exists.
            $termination = ' The application exited on its own after the timeout expired, so no process tree was terminated and a winget child it had started would have been left orphaned.'
        }
        else {
            # The Process object still holds an open handle, so Windows cannot recycle this PID while
            # this call runs and the exact PID below cannot name an unrelated process. taskkill reads
            # the tree from the live parent, which is why it runs before the finally block releases
            # that handle. Windows PowerShell promotes native stderr to a terminating ErrorRecord
            # under $ErrorActionPreference = 'Stop', so the exit code is read instead.
            $previousPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $taskkillOutput = (& "$env:SystemRoot\System32\taskkill.exe" /PID $process.Id /T /F 2>&1 | Out-String).Trim()
                $taskkillExit = $LASTEXITCODE
            }
            finally { $ErrorActionPreference = $previousPreference }
            if ($taskkillExit -ne 0) {
                $termination = " taskkill /T on PID $($process.Id) returned $taskkillExit and did not terminate a process tree, so a winget child may have survived: $taskkillOutput"
            }
        }
        # Termination is a request; this wait is what establishes that the application itself is gone
        # and is no longer holding the compiled executable open.
        if (-not $process.WaitForExit(15000)) {
            $termination += ' The application was still running 15s after termination; a later build may fail on a locked executable.'
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
