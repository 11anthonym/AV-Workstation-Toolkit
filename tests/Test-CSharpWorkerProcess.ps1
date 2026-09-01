<#[.SYNOPSIS] Exercises the non-shipping compiled worker with isolated roots and a fake executor. #>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$isElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isElevated) {
    Write-Output 'CSHARP_WORKER_PROCESS_SKIPPED reason=elevated-host'
    return
}
$integrationDll = Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.IntegrationTests\bin\Release\net10.0-windows\AVWorkstationToolkit.IntegrationTests.dll'
$workerExe = Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.Worker.DevHost\bin\Release\net10.0-windows\AVWorkstationToolkit.Worker.DevHost.exe'
foreach ($path in @($integrationDll,$workerExe)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required Phase 8 binary was not built: $path" }
}

$output = (& dotnet $integrationDll --worker-process $workerExe 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "Compiled worker process-boundary validation failed.`r`n$output" }
$result = $output | ConvertFrom-Json -ErrorAction Stop
if ([int]$result.ScenarioCount -ne 4 -or @($result.Scenarios).Count -ne 4) {
    throw 'Compiled worker process-boundary scenario count differs.'
}
$expected = [ordered]@{ success='Succeeded'; failure='Failed'; revalidation='Blocked'; cancellation='Cancelled' }
foreach ($entry in $expected.GetEnumerator()) {
    $actual = @($result.Scenarios | Where-Object Name -eq $entry.Key)
    if ($actual.Count -ne 1 -or [string]$actual[0].Status -ne $entry.Value) {
        throw "Compiled worker scenario differs: $($entry.Key)"
    }
}
Write-Output ("CSHARP_WORKER_PROCESS_OK scenarios={0}" -f $result.ScenarioCount)
