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
    'frontend-powershell','action-worker-powershell','winget','vendor-bridge-self',
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
$coreSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'scripts\AVWorkstationToolkit.Core.psm1') -Raw
$vendorSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'scripts\AVWorkstationToolkit.Vendor.psm1') -Raw
$uiSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'scripts\Start-AVWorkstationToolkit.ps1') -Raw
$projectSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj') -Raw
$readOnlyRunnerPath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Processes\WinGetReadOnlyProcessRunner.cs'
$readOnlyRunnerSource = Get-Content -LiteralPath $readOnlyRunnerPath -Raw
$resolverSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\WinGet\WindowsWinGetResolver.cs') -Raw
$compiledAppSource = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App') -Recurse -File -Filter '*.cs' |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$compiledReadOnlySurfaceSource = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Application\Details') -File -Filter '*.cs'
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Application\Diagnostics') -File -Filter '*.cs'
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Application\Providers') -File -Filter '*.cs'
) | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
$compiledReadOnlySurfaceSource = $compiledReadOnlySurfaceSource -join "`n"
$protocolStorePath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Files\ActionProtocolStore.cs'
$protocolStoreSource = Get-Content -LiteralPath $protocolStorePath -Raw
$compiledActionSource = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Application\Actions') -File -Filter '*.cs'
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Files') -File -Filter '*.cs' |
        Where-Object FullName -ne $protocolStorePath
) | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
$compiledActionSource = $compiledActionSource -join "`n"
if ($uiSource -match '(?i)Start-Process|ProcessStartInfo|Process\.Start') {
    throw 'The presentation script regained direct process-launch behavior.'
}
if ($launcherSource -notmatch 'UseShellExecute\s*=\s*false' -or
    $launcherSource -notmatch 'CreateNoWindow\s*=\s*true' -or
    $launcherSource -match 'WindowStyle\s*=\s*ProcessWindowStyle\.Hidden' -or
    $launcherSource -match '"-WindowStyle"') {
    throw 'Packaged PowerShell launch semantics differ from the reviewed direct-process contract.'
}
if ($coreSource -notmatch 'function Start-AVWorkstationToolkitDirectProcess' -or
    $coreSource -notmatch 'UseShellExecute\s*=\s*\$false' -or
    $coreSource -notmatch 'direct request JSON child' -or
    $vendorSource -notmatch "Arguments\s*=\s*'--vendor-bridge'") {
    throw 'Bounded PowerShell, Explorer, or vendor-bridge launch implementation is incomplete.'
}
if ($projectSource -match '<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>') {
    throw 'The release launcher re-enabled compressed single-file content.'
}
if ($launcherSource -match '(?i)GetTempPath|SpecialFolder\.LocalApplicationData[^\r\n]+\.ps1' -or
    $coreSource -match '(?is)powershell(?:\.exe)?.{0,500}GetTempPath') {
    throw 'Packaged PowerShell can be staged or executed from the system temporary directory.'
}
$migrationProcessSources = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' -and $_.FullName -notlike '*\AVWorkstationToolkit.Launcher\*' })
$directMigrationLaunchers = @($migrationProcessSources | Where-Object {
    $_.FullName -ne $readOnlyRunnerPath -and (Get-Content -LiteralPath $_.FullName -Raw) -match 'ProcessStartInfo|Process\.Start'
})
if ($directMigrationLaunchers.Count -ne 0) {
    throw 'A C# migration component outside the reviewed read-only WinGet runner gained direct process-launch behavior.'
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
if ($compiledAppSource -match 'ActionRequestFactory|ActionRequestFilePolicy|ActionProtocolStore|ActionArtifactPathPolicy|Invoke-AVWorkstationToolkitAction|Start-AVWorkstationToolkitWorker') {
    throw 'The compiled WPF migration app gained action-request persistence or worker-launch authority.'
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
