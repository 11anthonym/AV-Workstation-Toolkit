<#
.SYNOPSIS
    Runs AVWorkstationToolkit endpoint-trust regression checks and an optional Defender scan.

.DESCRIPTION
    Statically rejects suspicious process/packaging patterns in executable
    production source, validates the bounded child-process contract, and can
    ask an available local Microsoft Defender installation to scan a completed
    release directory. It never changes Defender settings or exclusions.
#>

[CmdletBinding()]
param(
    [string]$ReleaseRoot,
    [switch]$ScanWithDefender,
    [switch]$RequireDefender
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($RequireDefender -and -not $ScanWithDefender) { throw 'RequireDefender requires ScanWithDefender.' }

$policyPath = Join-Path $repositoryRoot 'manifests\process-launch-policy.json'
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
if ([int]$policy.SchemaVersion -ne 1 -or [string]$policy.Product -ne 'AV Workstation Toolkit') {
    throw 'Process-launch policy identity is invalid.'
}
$expectedLaunchIds = @(
    'compiled-action-worker','winget',
    'explorer-handoff','https-shell-handoff','snapshot-dsregcmd'
)
$actualLaunchIds = @($policy.Launches.Id)
if (($expectedLaunchIds -join '|') -ne ($actualLaunchIds -join '|') -or
    $actualLaunchIds.Count -ne @($actualLaunchIds | Sort-Object -Unique).Count) {
    throw 'Process-launch policy entries differ from the reviewed child-process contract.'
}
foreach ($launch in @($policy.Launches)) {
    if ([string]::IsNullOrWhiteSpace([string]$launch.Executable) -or
        [string]::IsNullOrWhiteSpace([string]$launch.Boundary) -or
        $launch.ShellExecute -isnot [bool]) {
        throw "Process-launch policy entry is incomplete: $($launch.Id)"
    }
}

$productionFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'scripts') -File |
        Where-Object Extension -in @('.ps1','.psm1','.psd1')
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -File |
        Where-Object { $_.Extension -in @('.cs','.csproj') -and $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' }
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'build') -File |
        Where-Object Extension -in @('.ps1','.psm1')
    Get-Item -LiteralPath (Join-Path $repositoryRoot 'Launch-AVWorkstationToolkit.cmd')
    Get-Item -LiteralPath (Join-Path $repositoryRoot 'Build-AVWorkstationToolkit.cmd')
)
$forbiddenPatterns = [ordered]@{
    'encoded PowerShell command' = '(?i)-EncodedCommand\b'
    'execution-policy bypass' = '(?i)ExecutionPolicy(?:"|''|\s|,)+Bypass\b'
    'Defender exclusion change' = '(?i)\b(?:Add|Set)-MpPreference\b[^\r\n]*(?:Exclusion|Disable|Realtime|Cloud|Sample)'
    'AMSI bypass or patch' = '(?i)\bamsi(?:scanbuffer|utils|initfailed|bypass|patch)\b'
    'ETW bypass or patch' = '(?i)\betw(?:eventwrite|bypass|patch)\b'
    'proxy-binary execution' = '(?i)\b(?:rundll32|regsvr32|mshta)(?:\.exe)?\b'
    'generic command-shell execution' = '(?i)\bcmd(?:\.exe)?\s+/c\b'
    'dynamic PowerShell evaluation' = '(?i)\b(?:Invoke-Expression|iex)\b'
    'executable packer integration' = '(?i)\b(?:upx|confuserex|themida)\b'
}
foreach ($file in $productionFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($entry in $forbiddenPatterns.GetEnumerator()) {
        if ($text -match $entry.Value) { throw "Production source contains $($entry.Key): $($file.Name)" }
    }
}

$launcherSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\Program.cs') -Raw
$projectSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj') -Raw
$readOnlyRunnerPath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Processes\WinGetReadOnlyProcessRunner.cs'
$readOnlyRunnerSource = Get-Content -LiteralPath $readOnlyRunnerPath -Raw
$mutationRunnerPath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Processes\WinGetMutationProcessRunner.cs'
$mutationRunnerSource = Get-Content -LiteralPath $mutationRunnerPath -Raw
$migrationWorkerLauncherPath = Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.Development\CompiledMigrationWorkerLauncher.cs'
$migrationWorkerLauncherSource = Get-Content -LiteralPath $migrationWorkerLauncherPath -Raw
$liveWorkerLauncherPath = Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.Development\CompiledLiveRehearsalWorkerLauncher.cs'
$liveWorkerLauncherSource = Get-Content -LiteralPath $liveWorkerLauncherPath -Raw
$productionWorkerLauncherPath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Processes\ProductionCompiledWorkerLauncher.cs'
$productionWorkerLauncherSource = Get-Content -LiteralPath $productionWorkerLauncherPath -Raw
$productionWorkerCompositionPath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Processes\ProductionWorkerComposition.cs'
$productionWorkerCompositionSource = Get-Content -LiteralPath $productionWorkerCompositionPath -Raw
$userHandoffPath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Processes\WindowsValidatedUserHandoffService.cs'
$userHandoffSource = Get-Content -LiteralPath $userHandoffPath -Raw
$resolverSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\WinGet\WindowsWinGetResolver.cs') -Raw
$compiledAppSource = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App') -Recurse -File -Filter '*.cs' |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$compiledMainWindowXaml = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\MainWindow.xaml') -Raw
$compiledWorkerSource = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Worker') -Recurse -File |
    Where-Object { $_.Extension -in @('.cs','.csproj') -and $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' } |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$developmentWorkerSource = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.Worker.DevHost') -Recurse -File |
    Where-Object { $_.Extension -in @('.cs','.csproj') -and $_.FullName -notmatch '[\/](?:bin|obj)[\/]' } |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$compiledReadOnlySurfaceSource = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Application\Details') -File -Filter '*.cs'
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Application\Diagnostics') -File -Filter '*.cs'
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Application\Providers') -File -Filter '*.cs'
) | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
$compiledReadOnlySurfaceSource = $compiledReadOnlySurfaceSource -join "`n"
$compiledVendorSource = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Vendors') -File -Filter '*.cs' |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$protocolStorePath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Files\ActionProtocolStore.cs'
$protocolStoreSource = Get-Content -LiteralPath $protocolStorePath -Raw
$workerProtocolPath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Files\ActionWorkerFileProtocol.cs'
$workerProtocolSource = Get-Content -LiteralPath $workerProtocolPath -Raw
$compiledActionSource = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Application\Actions') -File -Filter '*.cs'
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Files') -File -Filter '*.cs' |
        Where-Object { $_.FullName -notin @($protocolStorePath,$workerProtocolPath) }
) | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
$compiledActionSource = $compiledActionSource -join "`n"
if ($launcherSource -match '(?i)--legacy-powershell-recovery|--vendor-bridge|powershell\.exe|pwsh\.exe|cmd\.exe|ProcessStartInfo|Process\.Start' -or
    $projectSource -match 'AVWorkstationToolkit\.Payload\.(?:app|scripts)/') {
    throw 'The shipping launcher retains a retired PowerShell, vendor-bridge, shell, or legacy payload path.'
}
if ($projectSource -match '<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>') {
    throw 'The release launcher re-enabled compressed single-file content.'
}
if ($launcherSource -match '(?i)GetTempPath|SpecialFolder\.LocalApplicationData[^\r\n]+\.ps1') {
    throw 'The packaged runtime can stage a script in a temporary or user-data path.'
}
$migrationProcessSources = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' -and $_.FullName -notlike '*\AVWorkstationToolkit.Launcher\*' })
$directMigrationLaunchers = @($migrationProcessSources | Where-Object {
    $_.FullName -notin @($readOnlyRunnerPath,$mutationRunnerPath,$migrationWorkerLauncherPath,$liveWorkerLauncherPath,$productionWorkerLauncherPath,$userHandoffPath) -and (Get-Content -LiteralPath $_.FullName -Raw) -match 'ProcessStartInfo|Process\.Start'
})
if ($directMigrationLaunchers.Count -ne 0) {
    throw 'A C# migration component outside the reviewed WinGet boundaries gained direct process-launch behavior.'
}
if ($compiledAppSource -match 'System\.Management\.Automation|XamlReader|ProcessStartInfo|Process\.Start|(?i)\b(?:powershell|pwsh|cmd)\.exe\b' -or
    $compiledAppSource -match 'IActionWorkerBoundary\s+[A-Za-z_]') {
    throw 'The compiled WPF migration app gained dynamic XAML, shell/process, or action-worker execution behavior.'
}
if ($compiledReadOnlySurfaceSource -match 'ProcessStartInfo|Process\.Start|HttpClient|WebRequest|WebClient|IActionWorkerBoundary\s+[A-Za-z_]' -or
    $compiledReadOnlySurfaceSource -match '(?i)\b(?:install|upgrade|uninstall)async\s*\(') {
    throw 'The compiled diagnostics/detail/provider projection gained process, network, worker, or mutation behavior.'
}
if (($compiledActionSource + $protocolStoreSource) -match 'ProcessStartInfo|Process\.Start|HttpClient|WebRequest|WebClient|System\.Management\.Automation' -or
    ($compiledActionSource + $protocolStoreSource) -match '(?i)\b(?:powershell|pwsh|cmd)\.exe\b|\b(?:winget\s+)?(?:install|upgrade|uninstall|import)async\s*\(' -or
    $compiledActionSource -match 'File\.(?:Write|Create|Append)|FileMode\.(?:Create|CreateNew|OpenOrCreate|Append)') {
    throw 'The compiled action protocol gained an unreviewed process, network, worker-launch, mutation, or persistence behavior.'
}
if ($protocolStoreSource -notmatch 'explicitDataRoot' -or
    $protocolStoreSource -notmatch 'FileMode\.CreateNew' -or
    $protocolStoreSource -notmatch 'File\.Move\(temporaryPath, paths\.RequestPath, overwrite:\s*false\)' -or
    $protocolStoreSource -notmatch 'Flush\(flushToDisk:\s*true\)' -or
    $protocolStoreSource -match 'Environment\.GetFolderPath|LocalApplicationData|GetTempPath|FileMode\.(?:Create(?!New)|OpenOrCreate|Append)|ProcessStartInfo|Process\.Start') {
    throw 'The non-shipping action-protocol store lost its explicit-root, create-new, atomic-move, or durable-write boundary.'
}
if (($workerProtocolSource -match 'ProcessStartInfo|Process\.Start|HttpClient|WebRequest|WebClient|System\.Management\.Automation|LocalApplicationData|GetTempPath') -or
    $workerProtocolSource -notmatch 'explicitDataRoot' -or
    $workerProtocolSource -notmatch 'FileMode\.CreateNew' -or
    $workerProtocolSource -notmatch 'FileMode\.Append' -or
    $workerProtocolSource -notmatch 'File\.Move\(temporaryPath, Paths\.ResultPath, overwrite:\s*false\)' -or
    $workerProtocolSource -notmatch 'RejectStaleArtifact' -or
    $workerProtocolSource -notmatch 'Flush\(flushToDisk:\s*true\)') {
    throw 'The fake-worker file protocol lost its explicit-root, append-only progress, stale-artifact, or final-result no-overwrite boundary.'
}
if ($compiledAppSource -match 'Invoke-AVWorkstationToolkitAction|Start-AVWorkstationToolkitWorker|WinGetMutationProcessRunner|WinGetPackageActionExecutor' -or
    $compiledAppSource -match '--migration-action-test-root|--live-rehearsal-root|CreateLiveRehearsal|CompiledMigrationWorkerLauncher|CompiledLiveRehearsalWorkerLauncher' -or
    $compiledAppSource -notmatch 'CreateProduction' -or
    $compiledAppSource -notmatch 'ProductionCompiledWorkerLauncher' -or
    $compiledAppSource -notmatch 'if \(production\)') {
    throw 'The compiled WPF App lost its production-only action composition boundary or gained development/mutation authority.'
}
if ($migrationWorkerLauncherSource -notmatch 'WorkerFileName\s*=\s*"AVWorkstationToolkit\.Worker\.DevHost\.exe"' -or
    $migrationWorkerLauncherSource -notmatch 'awt-phase11-' -or
    $migrationWorkerLauncherSource -notmatch 'UseShellExecute\s*=\s*false' -or
    $migrationWorkerLauncherSource -notmatch 'ArgumentList\.Add\("--test-mode"\)' -or
    $migrationWorkerLauncherSource -notmatch 'ActionRequestFilePolicy' -or
    $migrationWorkerLauncherSource -match '(?i)\b(?:powershell|pwsh|cmd|winget)\.exe\b|\b(?:install|upgrade|uninstall|import)async\s*\(|Kill\(') {
    throw 'The compiled migration worker launcher lost its exact test-host, canonical-request, or independent-lifetime boundary.'
}
if ($liveWorkerLauncherSource -notmatch 'WorkerFileName\s*=\s*"AVWorkstationToolkit\.Worker\.DevHost\.exe"' -or
    $liveWorkerLauncherSource -notmatch 'LiveRehearsalRootPolicy\.RequireExisting' -or
    $liveWorkerLauncherSource -notmatch 'UseShellExecute\s*=\s*false' -or
    $liveWorkerLauncherSource -notmatch 'ArgumentList\.Add\("--live-rehearsal"\)' -or
    $liveWorkerLauncherSource -notmatch 'ActionRequestFilePolicy' -or
    $liveWorkerLauncherSource -match '(?i)\b(?:powershell|pwsh|cmd|winget)\.exe\b|\b(?:install|upgrade|uninstall|import)async\s*\(|Kill\(') {
    throw 'The compiled live-rehearsal launcher lost its exact worker, isolated-root, canonical-request, or independent-lifetime boundary.'
}
if ($productionWorkerLauncherSource -notmatch 'WorkerFileName\s*=\s*"AVWorkstationToolkit\.Worker\.exe"' -or
    $productionWorkerLauncherSource -notmatch 'ProductionRuntimePolicy\.RequireDataRoot' -or
    $productionWorkerLauncherSource -notmatch 'ProductionRuntimePolicy\.RequireApplicationRoot' -or
    $productionWorkerLauncherSource -notmatch 'CryptographicOperations\.FixedTimeEquals' -or
    $productionWorkerLauncherSource -notmatch 'UseShellExecute\s*=\s*false' -or
    $productionWorkerLauncherSource -notmatch 'ArgumentList\.Add\("--production"\)' -or
    $productionWorkerLauncherSource -notmatch 'ActionRequestFilePolicy' -or
    $productionWorkerLauncherSource -match '(?i)\b(?:powershell|pwsh|cmd|winget)\.exe\b|\b(?:install|upgrade|uninstall|import)async\s*\(|Kill\(') {
    throw 'The production compiled worker launcher lost its exact path, hash, request, or argument boundary.'
}
if ($userHandoffSource -notmatch 'OpenOfficialUriIntent' -or
    $userHandoffSource -notmatch 'UriSchemeHttps' -or
    $userHandoffSource -notmatch 'VendorPayloadState\.Verified' -or
    $userHandoffSource -notmatch 'ResolveCached' -or
    $userHandoffSource -notmatch 'SpecialFolder\.Windows' -or
    $userHandoffSource -match '(?i)\b(?:powershell|pwsh|cmd|winget)\.exe\b|\b(?:install|upgrade|uninstall|import)async\s*\(') {
    throw 'The compiled browser/Explorer handoff lost validated URI, verified-cache, or exact Windows Explorer containment.'
}
if ($compiledWorkerSource -match 'ProcessStartInfo|Process\.Start|System\.Management\.Automation|HttpClient|WebRequest|WebClient|ShellExecute|WindowsIdentity\.Impersonate' -or
    $compiledWorkerSource -match '(?i)\b(?:powershell|pwsh|cmd|winget)\.exe\b|\b(?:install|upgrade|uninstall|import)async\s*\(' -or
    $compiledWorkerSource -match 'LocalApplicationData|GetTempPath|Environment\.GetEnvironmentVariable') {
    throw 'The compiled worker gained an unreviewed process, shell, network, production-root, or mutation behavior.'
}
if ($compiledWorkerSource -notmatch '--production' -or
    $compiledWorkerSource -notmatch 'ActionWorkerFileProtocol' -or
    $compiledWorkerSource -notmatch 'ProductionRuntimePolicy\.RequireDataRoot' -or
    $compiledWorkerSource -notmatch 'ProductionRuntimePolicy\.RequireApplicationRoot' -or
    $compiledWorkerSource -notmatch 'WindowsBuiltInRole\.Administrator' -or
    $compiledWorkerSource -match '--test-mode|--live-rehearsal|--repository-root|DeterministicFakePackageExecutor|WorkerFixtureLoader|LiveRehearsalRootPolicy') {
    throw 'The shipping compiled worker lost its production-only activation, standard-user, path, or protocol boundary.'
}
if ($productionWorkerCompositionSource -notmatch 'WinGetPackageActionExecutor' -or
    $productionWorkerCompositionSource -notmatch 'WorkstationPlanningCoordinator' -or
    $productionWorkerCompositionSource -notmatch 'ReadFreshPlanAsync' -or
    $productionWorkerCompositionSource -match 'DeterministicFakePackageExecutor|--test-mode|--live-rehearsal') {
    throw 'The production worker composition lost its real constrained executor/fresh-plan boundary or gained a developer activation.'
}
if ($developmentWorkerSource -notmatch '--test-mode' -or
    $developmentWorkerSource -notmatch '--live-rehearsal' -or
    $developmentWorkerSource -notmatch 'DeterministicFakePackageExecutor' -or
    $developmentWorkerSource -notmatch 'AV Workstation Toolkit worker development host' -or
    $developmentWorkerSource -match '--production') {
    throw 'The non-shipping worker development host no longer owns the isolated fake/rehearsal activations.'
}
if ($compiledVendorSource -match 'ProcessStartInfo|Process\.Start|ShellExecute|System\.Management\.Automation|(?i)\b(?:powershell|pwsh|cmd|winget)\.exe\b' -or
    $compiledVendorSource -match '(?i)\b(?:install|upgrade|uninstall|import)async\s*\(') {
    throw 'The compiled vendor boundary gained process, shell, or package-mutation behavior.'
}
if ($compiledVendorSource -notmatch 'AllowAutoRedirect\s*=\s*false' -or
    $compiledVendorSource -notmatch 'AllowedHosts\.Contains' -or
    $compiledVendorSource -notmatch 'ProbeHostFingerprintAsync' -or
    $compiledVendorSource.IndexOf('ProbeHostFingerprintAsync',[StringComparison]::Ordinal) -gt $compiledVendorSource.IndexOf('credentials.Read',[StringComparison]::Ordinal) -or
    $compiledVendorSource -notmatch 'WinVerifyTrust|IAuthenticodeSignatureInspector') {
    throw 'The compiled vendor boundary lost redirect, host-key-before-credential, or signature-verification controls.'
}
$shippingCompositionSource = @(
    Get-Content -LiteralPath (Join-Path $repositoryRoot 'build\Build-Release.ps1') -Raw
    Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\Product.wxs') -Raw
    Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\Program.cs') -Raw
) -join "`n"
if ($compiledWorkerSource -match 'VendorHttpsDownloader|VendorSftpDeliveryService|WindowsVendorCredentialStore|VendorPayloadVerificationService' -or
    $compiledAppSource -notmatch 'VendorInteractionCoordinator' -or
    $compiledAppSource -notmatch 'WindowsVendorCredentialStore' -or
    $compiledAppSource -notmatch 'VendorPayloadVerificationService' -or
    $compiledAppSource -notmatch 'VendorExternalReleaseInventory' -or
    $compiledAppSource -notmatch 'PackageDeliveryWorkflow' -or
    $compiledMainWindowXaml -notmatch 'x:Name="GetPackageButton"[^>]+Command="\{Binding GetPackageCommand\}"' -or
    $compiledMainWindowXaml -match 'Content="Get package"[^>]+Command="\{Binding DetailsCommand\}"') {
    throw 'The compiled vendor boundary is missing from the production App or entered the worker composition.'
}
if ($shippingCompositionSource -notmatch 'AVWorkstationToolkit\.Worker' -or
    $shippingCompositionSource -notmatch 'WorkerPayloadPath' -or
    $launcherSource -notmatch 'PackagedAppStartupContext' -or
    $launcherSource -match '(?i)LegacyPowerShellRecovery|--vendor-bridge|powershell\.exe' -or
    $projectSource -match 'AVWorkstationToolkit\.Payload\.(?:app|scripts)/') {
    throw 'The release composition does not contain only the compiled App/worker and reviewed data runtime.'
}
if ($readOnlyRunnerSource -notmatch 'UseShellExecute\s*=\s*false' -or
    $readOnlyRunnerSource -notmatch 'RedirectStandardOutput\s*=\s*true' -or
    $readOnlyRunnerSource -notmatch 'RedirectStandardError\s*=\s*true' -or
    $readOnlyRunnerSource -notmatch 'ArgumentList\.Add' -or
    $readOnlyRunnerSource -notmatch 'Kill\(entireProcessTree:\s*true\)' -or
    $readOnlyRunnerSource -match '(?i)\b(?:cmd|powershell|pwsh)(?:\.exe)?\b') {
    throw 'The compiled read-only WinGet process boundary lost reviewed shell, stream, argument, or timeout containment.'
}
if ($readOnlyRunnerSource -notmatch 'WinGetReadOnlyOperation\.Version' -or
    $readOnlyRunnerSource -notmatch 'WinGetReadOnlyOperation\.InstalledInventory' -or
    $readOnlyRunnerSource -notmatch 'WinGetReadOnlyOperation\.AvailableUpdates' -or
    $readOnlyRunnerSource -match '(?i)"(?:install|upgrade|uninstall|import)"|"--all"') {
    throw 'The compiled read-only WinGet policy gained an unreviewed action vector.'
}
if ($mutationRunnerSource -notmatch 'UseShellExecute\s*=\s*false' -or
    $mutationRunnerSource -notmatch 'RedirectStandardOutput\s*=\s*true' -or
    $mutationRunnerSource -notmatch 'RedirectStandardError\s*=\s*true' -or
    $mutationRunnerSource -notmatch 'ArgumentList\.Add' -or
    $mutationRunnerSource -notmatch 'WindowsWinGetResolver\.IsExpectedExecutablePath' -or
    $mutationRunnerSource -notmatch 'ManagedWinGetArgumentPolicy\.Create' -or
    $mutationRunnerSource -notmatch 'WindowsBuiltInRole\.Administrator' -or
    $mutationRunnerSource -notmatch 'Kill\(entireProcessTree:\s*true\)' -or
    $mutationRunnerSource -match '(?i)\b(?:cmd|powershell|pwsh)(?:\.exe)?\b|ShellExecute\s*=\s*true') {
    throw 'The non-shipping WinGet mutation boundary lost trusted resolution, typed arguments, standard-user enforcement, or bounded direct-process behavior.'
}
$argumentPolicySource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Application\Workers\ManagedWinGetArgumentPolicy.cs') -Raw
if ($argumentPolicySource -notmatch '"install"\s*:\s*"upgrade"' -or
    $argumentPolicySource -notmatch '"--id"' -or
    $argumentPolicySource -notmatch '"--exact"' -or
    $argumentPolicySource -notmatch '"--source",\s*"winget"' -or
    $argumentPolicySource -match '(?i)"(?:uninstall|import)"|"--all"|ProcessStartInfo|Process\.Start') {
    throw 'The compiled WinGet mutation argument policy is no longer exact-ID Install/Update only.'
}
if ($compiledAppSource -match 'WinGetMutationProcessRunner|WinGetPackageActionExecutor|ManagedWinGetArgumentPolicy') {
    throw 'The compiled WPF App gained direct WinGet mutation authority instead of delegating to the constrained worker.'
}
if ($resolverSource -match '(?i)GetEnvironmentVariable\s*\(\s*["'']PATH["'']|where\.exe|App Paths' -or
    $resolverSource -notmatch 'Microsoft\.DesktopAppInstaller_' -or
    $resolverSource -notmatch 'WinGetCandidatePolicy\.Evaluate' -or
    $resolverSource -notmatch 'IsReparseFree' -or
    $resolverSource -notmatch 'signatureVerifier\.Verify') {
    throw 'The compiled WinGet resolver no longer follows the registered-package, protected-path, reparse, and signature trust boundary.'
}

$defenderStatus = 'not-requested'
if (-not [string]::IsNullOrWhiteSpace($ReleaseRoot)) {
    $releasePath = [IO.Path]::GetFullPath($ReleaseRoot)
    if (-not (Test-Path -LiteralPath $releasePath -PathType Container)) { throw "Release directory was not found: $releasePath" }
    $version = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'VERSION') -Raw).Trim()
    $manifestPath = Join-Path $releasePath ("AV-Workstation-Toolkit-{0}-release.json" -f $version)
    $sbomPath = Join-Path $releasePath ("AV-Workstation-Toolkit-{0}-sbom.cdx.json" -f $version)
    $checksumPath = Join-Path $releasePath ("AV-Workstation-Toolkit-{0}-SHA256SUMS.txt" -f $version)
    $projectLicensePath = Join-Path $releasePath ("AV-Workstation-Toolkit-{0}-LICENSE.txt" -f $version)
    $noticesPath = Join-Path $releasePath ("AV-Workstation-Toolkit-{0}-THIRD-PARTY-NOTICES.md" -f $version)
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $sbomText = Get-Content -LiteralPath $sbomPath -Raw
    $sbom = $sbomText | ConvertFrom-Json
    if ([int]$manifest.SchemaVersion -lt 3 -or $sbom.bomFormat -ne 'CycloneDX' -or $sbom.specVersion -ne '1.6') {
        throw 'Release manifest or SBOM identity is invalid.'
    }
    if ([string]$manifest.TargetRuntime -ne 'Microsoft.NETCore.App.Runtime.win-x64/10.0.11' -or
        [string]$manifest.TargetHost -ne 'Microsoft.NETCore.App.Host.win-x64/10.0.11' -or
        [string]$manifest.SelectedSdk -notmatch '^10\.0\.\d{3}$') {
        throw 'Release runtime, host, or selected SDK provenance is invalid.'
    }
    if ([string]$manifest.ProjectLicense -ne 'Apache-2.0' -or [string]$sbom.metadata.component.licenses[0].license.id -ne 'Apache-2.0') {
        throw 'Release project Apache-2.0 metadata is incomplete.'
    }
    if ((Get-FileHash -LiteralPath $sbomPath -Algorithm SHA256).Hash -ne [string]$manifest.Sbom.Sha256) {
        throw 'Release manifest SBOM hash differs from the completed SBOM.'
    }
    if ($sbomText -match '(?i)(?:[A-Z]:\\Users\\|/Users/|/home/)' -or
        (-not [string]::IsNullOrWhiteSpace($env:USERNAME) -and $sbomText -match [regex]::Escape($env:USERNAME))) {
        throw 'SBOM contains a local user or developer path.'
    }
    if ('Microsoft.WindowsDesktop.App.Runtime.win-x64' -in @($sbom.components.name) -or
        'Microsoft.NETCore.App.Runtime.win-x64' -notin @($sbom.components.name) -or
        'Microsoft.NETCore.App.Host.win-x64' -notin @($sbom.components.name) -or
        @($sbom.components | Where-Object { @($_.licenses).Count -eq 0 }).Count -gt 0) {
        throw 'SBOM runtime identity or reviewed license metadata is incomplete.'
    }
    if (-not (Test-Path -LiteralPath $noticesPath -PathType Leaf) -or
        @($manifest.Artifacts | Where-Object Name -eq (Split-Path -Leaf $noticesPath)).Count -ne 1) {
        throw 'Release third-party notices are missing from the artifact boundary.'
    }
    if (-not (Test-Path -LiteralPath $projectLicensePath -PathType Leaf) -or
        @($manifest.Artifacts | Where-Object Name -eq (Split-Path -Leaf $projectLicensePath)).Count -ne 1) {
        throw 'Release Apache-2.0 project license is missing from the artifact boundary.'
    }
    $checksumEntries = @(Get-Content -LiteralPath $checksumPath | ForEach-Object {
        if ($_ -match '^[A-F0-9]{64} \*(.+)$') { $Matches[1] }
    })
    if ((Split-Path -Leaf $manifestPath) -notin $checksumEntries -or
        (Split-Path -Leaf $checksumPath) -in $checksumEntries) {
        throw 'Release checksum coverage does not include the manifest or contains a circular self-digest.'
    }
}

if ($ScanWithDefender) {
    if ([string]::IsNullOrWhiteSpace($ReleaseRoot)) { throw 'ScanWithDefender requires ReleaseRoot.' }
    $candidates = [System.Collections.Generic.List[string]]::new()
    $platformRoot = Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform'
    if (Test-Path -LiteralPath $platformRoot -PathType Container) {
        foreach ($directory in @(Get-ChildItem -LiteralPath $platformRoot -Directory | Sort-Object Name -Descending)) {
            $candidates.Add((Join-Path $directory.FullName 'MpCmdRun.exe'))
        }
    }
    $candidates.Add((Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'))
    $defenderPath = @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
    if ($defenderPath.Count -eq 0) {
        if ($RequireDefender) { throw 'Microsoft Defender command-line scanner is unavailable.' }
        $defenderStatus = 'unavailable'
        Write-Output 'DEFENDER_SCAN_SKIPPED reason=unavailable'
    }
    else {
        $resolvedDefenderPath = [IO.Path]::GetFullPath([string]$defenderPath[0])
        $signature = Get-AuthenticodeSignature -LiteralPath $resolvedDefenderPath
        $signer = if ($null -ne $signature.SignerCertificate) { [string]$signature.SignerCertificate.Subject } else { '' }
        if ($signature.Status -ne 'Valid' -or $signer -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
            throw 'The resolved Microsoft Defender scanner failed publisher validation.'
        }
        $scanOutput = (& $resolvedDefenderPath -Scan -ScanType 3 -File ([IO.Path]::GetFullPath($ReleaseRoot)) 2>&1 | Out-String).Trim()
        $scanExitCode = $LASTEXITCODE
        if ($scanExitCode -ne 0) {
            throw "Microsoft Defender release scan failed or reported a detection (exit $scanExitCode): $scanOutput"
        }
        $defenderStatus = 'passed'
        Write-Output 'DEFENDER_SCAN_OK result=no-detection-reported'
    }
}

Write-Output ("ENDPOINT_TRUST_OK launches={0} files={1} defender={2}" -f $actualLaunchIds.Count,$productionFiles.Count,$defenderStatus)
