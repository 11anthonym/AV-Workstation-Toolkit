<#
.SYNOPSIS
    Verifies the final compiled production composition and Phase 14 legacy-runtime retirement.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Require-Match([string]$Text, [string]$Pattern, [string]$Message) {
    if ($Text -notmatch $Pattern) { throw $Message }
}

function Reject-Match([string]$Text, [string]$Pattern, [string]$Message) {
    if ($Text -match $Pattern) { throw $Message }
}

$launcherProject = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj') -Raw
$launcher = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\Program.cs') -Raw
$catalogLoader = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Catalog\RepositoryCatalogLoader.cs') -Raw
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
Require-Match $launcherProject 'AVWorkstationToolkit\.Payload\.manifests/' 'Shipping bootstrap does not embed reviewed data manifests.'
Reject-Match $launcherProject 'Payload\.(?:app|scripts)/' 'Shipping bootstrap still embeds the legacy PowerShell/XAML runtime.'

Require-Match $launcher 'new PackagedAppStartupContext' 'Normal startup does not create the packaged compiled App context.'
Require-Match $launcher 'RunCompiledApp\(new AVWorkstationToolkit\.App\.App\(context\)\)' 'Normal startup does not run initialized compiled WPF.'
Require-Match $launcher 'app\.InitializeComponent\(\)' 'Shipping bootstrap does not initialize compiled App.xaml resources.'
Require-Match $launcher 'RemoveRetiredRuntimeFiles' 'Shipping bootstrap does not remove recognized stale legacy runtime files.'
Reject-Match $launcher '(?i)--legacy-powershell-recovery|--vendor-bridge|powershell\.exe|StartLegacyRecovery|ProcessStartInfo' 'Shipping bootstrap still exposes a legacy PowerShell/vendor runtime path.'

Require-Match $catalogLoader 'manifests", "managed-applications\.json' 'Compiled catalog loader does not use the shipping JSON managed catalog.'
Require-Match $catalogLoader 'JsonUnmappedMemberHandling\.Disallow' 'Managed catalog JSON does not reject unknown fields.'
Require-Match $catalogLoader 'RejectDuplicateProperties' 'Managed catalog JSON does not reject duplicate properties.'
Reject-Match $catalogLoader 'AppProfiles\.psd1|Import-PowerShellDataFile|System\.Management\.Automation' 'Compiled production catalog loading still depends on PowerShell data/runtime behavior.'

Require-Match $composition 'CreateProduction' 'Compiled App lacks a production composition root.'
Require-Match $composition 'ProductionRuntimePolicy\.RequireDataRoot' 'Production App does not enforce the canonical data root.'
Require-Match $composition 'ProductionCompiledWorkerLauncher' 'Production App does not use the exact compiled worker boundary.'
foreach ($service in @('VendorInteractionCoordinator','VendorHttpsDownloader','VendorSftpDeliveryService','WindowsVendorCredentialStore','VendorPayloadVerificationService')) {
    Require-Match $composition ([regex]::Escape($service)) "Production App omits compiled vendor service: $service"
}

foreach ($boundary in @('ProductionRuntimePolicy.RequireApplicationRoot','CryptographicOperations.FixedTimeEquals','UseShellExecute = false','ArgumentList.Add("--production")','ActionRequestFilePolicy')) {
    Require-Match $workerLauncher ([regex]::Escape($boundary)) "Production worker launcher omits boundary: $boundary"
}
Reject-Match $workerLauncher '(?i)\b(?:powershell|pwsh|cmd)\.exe\b|ShellExecute\s*=\s*true|Kill\(' 'Production worker launcher gained a shell, unsafe shell execution, or GUI-owned kill path.'
Require-Match $worker 'args\[0\] == "--production"' 'Compiled worker lacks its exact production invocation.'
Require-Match $worker 'ProductionRuntimePolicy\.RequireApplicationRoot' 'Compiled worker does not validate the packaged application root.'
Require-Match $workerComposition 'WinGetPackageActionExecutor' 'Production worker composition does not use the constrained real executor.'
Require-Match $workerComposition 'WorkstationPlanningCoordinator' 'Production worker composition does not obtain fresh planning evidence.'

Require-Match $build '\$workerProject\s*=.*AVWorkstationToolkit\.Worker\\AVWorkstationToolkit\.Worker\.csproj' 'Release build does not identify the compiled worker project.'
Require-Match $build 'publish \$workerProject' 'Release build does not publish the compiled worker.'
Require-Match $build 'WorkerPayloadPath' 'Release build does not pass the exact compiled worker to the bootstrap.'
Require-Match $build "LegacyFallback = 'Retired from shipping'" 'Release provenance does not record final legacy retirement.'
Reject-Match $build 'embeddedPayloadFiles[\s\S]{0,500}(?:app\\AVWorkstationToolkit\.xaml|Get-ChildItem[^\r\n]+scripts)' 'Release payload accounting still includes legacy runtime source.'

$ids = @($policy.Launches.Id)
foreach ($id in @('compiled-action-worker','winget','explorer-handoff','https-shell-handoff','snapshot-dsregcmd')) {
    if ($id -notin $ids) { throw "Process policy omits production/tooling boundary: $id" }
}
foreach ($retiredId in @('legacy-recovery-powershell','legacy-action-worker-powershell','vendor-bridge-self')) {
    if ($retiredId -in $ids) { throw "Process policy still includes retired runtime boundary: $retiredId" }
}

Write-Output 'COMPILED_RETIREMENT_OK primary=compiled-wpf worker=compiled vendor=compiled legacy=retired'
