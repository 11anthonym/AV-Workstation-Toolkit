<#[.SYNOPSIS] Proves a signed managed-catalog update across compiled application and worker processes without mutation. #>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$isElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isElevated) {
    Write-Output 'MANAGED_CATALOG_BINARY_UPDATE_SKIPPED reason=elevated-host'
    return
}

$applicationHost = Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.IntegrationTests\bin\Release\net10.0-windows\AVWorkstationToolkit.IntegrationTests.exe'
$workerHost = Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.Worker.DevHost\bin\Release\net10.0-windows\AVWorkstationToolkit.Worker.DevHost.exe'
foreach ($path in @($applicationHost,$workerHost)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required managed-catalog proof binary was not built: $path" }
}

$output = (& $applicationHost --managed-catalog-binary-update $repositoryRoot $workerHost 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "Managed-catalog binary update proof failed.`r`n$output" }
$result = $output | ConvertFrom-Json -ErrorAction Stop
if ([int64]$result.BaselineApplicationRevision -ne 1 -or [int64]$result.BaselineWorkerRevision -ne 1 -or
    [int64]$result.ActivatedRunningRevision -ne 1 -or -not [bool]$result.RestartRequiredAfterActivation -or
    [int64]$result.RestartedApplicationRevision -ne 2 -or -not [bool]$result.UpdatedPackagePresented -or
    [int64]$result.UpdatedWorkerRevision -ne 2 -or [string]$result.UpdatedWorkerStatus -ne 'Succeeded' -or
    -not [bool]$result.RevisionMismatchRejected -or -not [bool]$result.UnapprovedPackageRejected) {
    throw 'Managed-catalog binary update evidence differs from the required revision and authorization sequence.'
}
if ([string]$result.ApplicationSha256Before -cne [string]$result.ApplicationSha256After -or
    [string]$result.WorkerSha256Before -cne [string]$result.WorkerSha256After) {
    throw 'A compiled application or worker development-host binary changed during the catalog-only update proof.'
}
Write-Output ("MANAGED_CATALOG_BINARY_UPDATE_OK app={0} appSha256={1} worker={2} workerSha256={3} revision=1->2" -f
    $result.ApplicationExecutable,$result.ApplicationSha256After,$result.WorkerExecutable,$result.WorkerSha256After)
