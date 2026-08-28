<#[.SYNOPSIS] Runs non-mutating live checks for the non-shipping C# Windows providers. #>
[CmdletBinding()]
param([switch]$NoBuild)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'AVWorkstationToolkit.slnx'
$assemblyPath = Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.IntegrationTests\bin\Release\net10.0-windows\AVWorkstationToolkit.IntegrationTests.dll'

if (-not $NoBuild) {
    & dotnet restore $solutionPath --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed before the live read-only checks.' }
    & dotnet build $solutionPath -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'C# migration build failed before the live read-only checks.' }
}
if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) { throw "Integration assembly was not found: $assemblyPath" }

$json = (& dotnet $assemblyPath --live-readonly 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "Live read-only provider harness failed: $json" }
$result = $json | ConvertFrom-Json
$statuses = [ordered]@{
    WinGetResolution = [string]$result.WinGetResolution
    InstalledInventory = [string]$result.InstalledInventory
    UpdateInventory = [string]$result.UpdateInventory
    RegistryInventory = [string]$result.RegistryInventory
    RebootDetection = [string]$result.RebootDetection
}
foreach ($entry in $statuses.GetEnumerator()) {
    $classification = if ($entry.Value -in @('Available','Complete')) { 'exercised' } else { 'unavailable' }
    Write-Output ("LIVE_READONLY_SOURCE name={0} status={1} classification={2}" -f $entry.Key,$entry.Value,$classification)
}
Write-Output ("LIVE_READONLY_RESULT installed={0} updates={1} registrySources={2} rebootPending={3}" -f
    [int]$result.InstalledCount,[int]$result.UpdateCount,@($result.RegistrySources).Count,[bool]$result.Pending)
