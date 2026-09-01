<#[.SYNOPSIS] Builds and tests the non-shipping C# migration scaffolding and parity harness. #>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'AVWorkstationToolkit.slnx'
$lockedRestoreOutput = (& dotnet restore $solutionPath --locked-mode --nologo 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "The C# migration dependency lock is stale. Run: dotnet restore .\AVWorkstationToolkit.slnx --force-evaluate; review every packages.lock.json change before committing.`r`n$lockedRestoreOutput"
}
& dotnet build $solutionPath -c Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'C# migration solution build failed.' }
& dotnet test $solutionPath -c Release --no-build --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'C# deterministic domain tests failed.' }
& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Test-ProductionWorkerBoundary.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Production-only compiled worker boundary validation failed.' }
& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'parity\Invoke-Parity.ps1') -NoBuild
if ($LASTEXITCODE -ne 0) { throw 'Dual-engine parity validation failed.' }
& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Test-CSharpWorkerProcess.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Compiled worker process-boundary validation failed.' }
$isElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isElevated) {
    Write-Output 'CSHARP_ACTION_FLOW_SKIPPED reason=elevated-host'
}
else {
    $integrationPath = Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.IntegrationTests\bin\Release\net10.0-windows\AVWorkstationToolkit.IntegrationTests.exe'
    & $integrationPath --compiled-action-flow $repositoryRoot
    if ($LASTEXITCODE -ne 0) { throw 'Compiled App migration action-flow integration failed.' }
}
& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File (Join-Path $PSScriptRoot 'Test-CSharpWpfSmoke.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Compiled WPF migration smoke failed.' }
& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Test-CSharpLiveRehearsal.ps1') -NoBuild
if ($LASTEXITCODE -ne 0) { throw 'Compiled-stack live rehearsal failed.' }
Write-Output 'CSHARP_MIGRATION_OK'
