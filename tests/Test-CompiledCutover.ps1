<#
.SYNOPSIS
    Verifies the Phase 13 compiled production composition without launching a mutation.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Require-Match([string]$Text, [string]$Pattern, [string]$Message) {
    if ($Text -notmatch $Pattern) { throw $Message }
}

$launcherProject = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj') -Raw
$launcher = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\Program.cs') -Raw
$composition = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\Services\CompiledAppComposition.cs') -Raw
$workerLauncher = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Processes\ProductionCompiledWorkerLauncher.cs') -Raw
$worker = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Worker\Program.cs') -Raw
$workerComposition = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Worker\LiveRehearsalWorkerComposition.cs') -Raw
$build = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build\Build-Release.ps1') -Raw
$policy = Get-Content -LiteralPath (Join-Path $repositoryRoot 'manifests\process-launch-policy.json') -Raw | ConvertFrom-Json

Require-Match $launcherProject '<UseWPF>true</UseWPF>' 'Shipping bootstrap is not a compiled WPF host.'
Require-Match $launcherProject '<PublishTrimmed>false</PublishTrimmed>' 'Shipping WPF host incorrectly enables trimming.'
Require-Match $launcherProject 'ProjectReference Include="\.\.\\AVWorkstationToolkit\.App' 'Shipping bootstrap does not reference the compiled App.'
Require-Match $launcherProject 'AVWorkstationToolkit\.Payload\.worker/AVWorkstationToolkit\.Worker\.exe' 'Shipping bootstrap does not embed the compiled worker.'
Require-Match $launcher 'new PackagedAppStartupContext' 'Normal startup does not create the packaged compiled App context.'
Require-Match $launcher 'RunCompiledApp\(new AVWorkstationToolkit\.App\.App\(context\)\)' 'Normal startup does not run initialized compiled WPF.'
Require-Match $launcher 'app\.InitializeComponent\(\)' 'Shipping bootstrap does not initialize compiled App.xaml resources.'
Require-Match $launcher 'if \(options\.LegacyPowerShellRecovery\)' 'Legacy runtime is not isolated behind its explicit option.'
Require-Match $launcher 'Packaged preview rendering is available only with --legacy-powershell-recovery' 'Legacy preview can activate without the recovery switch.'
if ([regex]::Matches($launcher, 'StartLegacyRecovery\(').Count -ne 2) { throw 'Legacy recovery has an unexpected call surface.' }

Require-Match $composition 'CreateProduction' 'Compiled App lacks a production composition root.'
Require-Match $composition 'ProductionRuntimePolicy\.RequireDataRoot' 'Production App does not enforce the canonical data root.'
Require-Match $composition 'ProductionCompiledWorkerLauncher' 'Production App does not use the exact compiled worker boundary.'
foreach ($service in @('VendorInteractionCoordinator','VendorHttpsDownloader','VendorSftpDeliveryService','WindowsVendorCredentialStore','VendorPayloadVerificationService')) {
    Require-Match $composition ([regex]::Escape($service)) "Production App omits compiled vendor service: $service"
}

foreach ($boundary in @('ProductionRuntimePolicy.RequireApplicationRoot','CryptographicOperations.FixedTimeEquals','UseShellExecute = false','ArgumentList.Add("--production")','ActionRequestFilePolicy')) {
    Require-Match $workerLauncher ([regex]::Escape($boundary)) "Production worker launcher omits boundary: $boundary"
}
if ($workerLauncher -match '(?i)\b(?:powershell|pwsh|cmd)\.exe\b|ShellExecute\s*=\s*true|Kill\(') {
    throw 'Production worker launcher gained a shell, arbitrary shell execution, or GUI-owned kill path.'
}
Require-Match $worker 'args\[0\] == "--production"' 'Compiled worker lacks its exact production invocation.'
Require-Match $worker 'ProductionRuntimePolicy\.RequireApplicationRoot' 'Compiled worker does not validate the packaged application root.'
Require-Match $workerComposition 'WinGetPackageActionExecutor' 'Production worker composition does not use the constrained real executor.'
Require-Match $workerComposition 'WorkstationPlanningCoordinator' 'Production worker composition does not obtain fresh planning evidence.'

Require-Match $build '\$workerProject\s*=.*AVWorkstationToolkit\.Worker\\AVWorkstationToolkit\.Worker\.csproj' 'Release build does not identify the compiled worker project.'
Require-Match $build 'publish \$workerProject' 'Release build does not publish the compiled worker.'
Require-Match $build 'WorkerPayloadPath' 'Release build does not pass the exact compiled worker to the bootstrap.'
Require-Match $build 'WorkerSignatureStatus' 'Release provenance omits compiled worker signature state.'

$ids = @($policy.Launches.Id)
foreach ($id in @('compiled-action-worker','winget','legacy-recovery-powershell')) {
    if ($id -notin $ids) { throw "Process policy omits production/recovery boundary: $id" }
}

Write-Output 'COMPILED_CUTOVER_OK primary=compiled-wpf worker=compiled vendor=compiled fallback=explicit'
