<#
.SYNOPSIS
    Builds and tests the compiled production runtime.

.DESCRIPTION
    Restores the locked solution, builds it warning-clean, runs the deterministic MSTest suite, then
    exercises the boundaries an in-process test cannot reach: the shipping worker's production-only
    invocation surface, the separate worker process protocol, the compiled action flow, the WPF
    startup surface, and a non-mutating live rehearsal. It installs, updates and uninstalls nothing.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'AVWorkstationToolkit.slnx'

$lockedRestoreOutput = (& dotnet restore $solutionPath --locked-mode --nologo 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "The dependency lock is stale. Run: dotnet restore .\AVWorkstationToolkit.slnx --force-evaluate; review every packages.lock.json change before committing.`r`n$lockedRestoreOutput"
}
& dotnet build $solutionPath -c Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Compiled runtime solution build failed.' }
& dotnet test $solutionPath -c Release --no-build --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Compiled runtime deterministic tests failed.' }

& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Test-ProductionWorkerBoundary.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Production-only compiled worker boundary validation failed.' }
& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Test-CompiledWorkerProcess.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Compiled worker process-boundary validation failed.' }

$isElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isElevated) {
    Write-Output 'COMPILED_ACTION_FLOW_SKIPPED reason=elevated-host'
}
else {
    $integrationPath = Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.IntegrationTests\bin\Release\net10.0-windows\AVWorkstationToolkit.IntegrationTests.exe'
    & $integrationPath --compiled-action-flow $repositoryRoot
    if ($LASTEXITCODE -ne 0) { throw 'Compiled App action-flow integration failed.' }
}

& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File (Join-Path $PSScriptRoot 'Test-CompiledWpfSmoke.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Compiled WPF smoke failed.' }
& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Test-CompiledLiveRehearsal.ps1') -NoBuild
if ($LASTEXITCODE -ne 0) { throw 'Compiled-stack live rehearsal failed.' }
Write-Output 'COMPILED_RUNTIME_OK'
