<#[.SYNOPSIS] Runs the non-mutating standard-user compiled-stack live rehearsal. #>
[CmdletBinding()]
param([switch]$NoBuild)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$isElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isElevated) {
    Write-Output 'COMPILED_LIVE_REHEARSAL_SKIPPED reason=elevated-host'
    return
}
if (-not $NoBuild) {
    & dotnet build (Join-Path $repositoryRoot 'AVWorkstationToolkit.slnx') -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Compiled runtime build failed before live rehearsal.' }
}
$integrationPath = Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.IntegrationTests\bin\Release\net10.0-windows\AVWorkstationToolkit.IntegrationTests.exe'
$output = (& $integrationPath --live-rehearsal $repositoryRoot 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "Compiled-stack live rehearsal failed.`r`n$output" }
$result = $output | ConvertFrom-Json -ErrorAction Stop
if ([string]$result.Status -ne 'Succeeded' -or -not [bool]$result.WorkerIndependent -or
    -not [bool]$result.RecoveredFromFreshStore -or -not [bool]$result.PostCompletionRefresh -or
    -not [bool]$result.TrustedWinGet -or [bool]$result.MutationPerformed) {
    throw 'Compiled-stack live rehearsal returned an unexpected result.'
}
Write-Output ("COMPILED_LIVE_REHEARSAL_OK package={0} action={1} installed={2} available={3} progress={4} worker={5} mutation=no" -f
    [string]$result.CandidateId,[string]$result.CandidateAction,[string]$result.InstalledVersion,
    [string]$result.AvailableVersion,[int]$result.ProgressRecords,[int]$result.WorkerProcessId)
