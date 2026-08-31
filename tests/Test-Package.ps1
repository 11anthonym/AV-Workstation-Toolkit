<#
.SYNOPSIS
    Verifies AVWorkstationToolkit release artifacts without installing applications.

.DESCRIPTION
    Validates checksums and versions, copies the standalone executable into an
    otherwise empty download directory, exercises runtime extraction and WPF
    smoke behavior, verifies cache repair, and administratively extracts the
    MSI. It does not register or install the MSI and never invokes a WinGet
    change action.
#>

[CmdletBinding()]
param(
    [string]$ReleaseRoot,
    [switch]$RequireSignature,
    [switch]$SignaturePolicyOnly,
    [switch]$SkipDesktopSmoke,
    [ValidateRange(10,600)]
    [int]$ProcessTimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'VERSION') -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($ReleaseRoot)) {
    $ReleaseRoot = Join-Path $repositoryRoot (Join-Path 'artifacts\release' $version)
}
$ReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$standalonePath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-win-x64.exe" -f $version)
$msiPath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-x64.msi" -f $version)
$portablePath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-win-x64.zip" -f $version)
$checksumPath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-SHA256SUMS.txt" -f $version)
$releaseManifestPath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-release.json" -f $version)
$sbomPath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-sbom.cdx.json" -f $version)
$projectLicensePath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-LICENSE.txt" -f $version)
$thirdPartyNoticesPath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-THIRD-PARTY-NOTICES.md" -f $version)

$script:Passed = 0
$script:Failed = 0
$script:Skipped = 0
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:LastLauncherStdErr = ''

function Assert-True {
    param([bool]$Condition,[string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param($Expected,$Actual,[string]$Message)
    if ($Expected -ne $Actual) { throw "$Message Expected: [$Expected] Actual: [$Actual]" }
}

function Invoke-Check {
    param([string]$Name,[scriptblock]$Test)
    try {
        & $Test
        $script:Passed++
        Write-Host ("PASS  {0}" -f $Name) -ForegroundColor Green
    }
    catch {
        $script:Failed++
        $detail = "{0}: {1}" -f $Name,$_.Exception.Message
        $script:Failures.Add($detail) | Out-Null
        Write-Host ("FAIL  {0}" -f $detail) -ForegroundColor Red
    }
}

function Wait-AVWorkstationToolkitProcess {
    param(
        [Parameter(Mandatory)]
        [Diagnostics.Process]$Process,
        [Parameter(Mandatory)]
        [string]$Description
    )

    if (-not $Process.WaitForExit($ProcessTimeoutSeconds * 1000)) {
        $processId = $Process.Id
        try {
            & (Join-Path $env:SystemRoot 'System32\taskkill.exe') /PID $processId /T /F 2>$null | Out-Null
        }
        catch {
            Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
        }
        throw "$Description exceeded the $ProcessTimeoutSeconds-second timeout."
    }

    return $Process.ExitCode
}

function Invoke-PackagedLauncher {
    param([string]$Launcher,[string[]]$Arguments)
    $quotedArguments = @($Arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' '
    $nonce = [guid]::NewGuid().ToString('N')
    $stdoutPath = Join-Path $temporaryRoot "launcher-$nonce.stdout.log"
    $stderrPath = Join-Path $temporaryRoot "launcher-$nonce.stderr.log"
    $process = Start-Process -FilePath $Launcher -ArgumentList $quotedArguments -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
    Wait-AVWorkstationToolkitProcess -Process $process -Description 'Packaged launcher' | Out-Null
    $process.Refresh()
    $exitCode = [int]$process.ExitCode
    $stderrContent = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { $null }
    $script:LastLauncherStdErr = if ($null -eq $stderrContent) { '' } else { ([string]$stderrContent).Trim() }
    return $exitCode
}

function Invoke-PackagedVendorBridge {
    param(
        [Parameter(Mandatory)][string]$Launcher,
        [Parameter(Mandatory)]$Request,
        [switch]$ExpectFailure
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Launcher
    $startInfo.Arguments = '--vendor-bridge'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        Assert-True $process.Start() 'Packaged vendor bridge did not start.'
        $process.StandardInput.Write(($Request | ConvertTo-Json -Depth 6 -Compress))
        $process.StandardInput.Close()
        $exitCode = Wait-AVWorkstationToolkitProcess -Process $process -Description 'Packaged vendor bridge'
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        try { $response = $stdout | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "Packaged vendor bridge returned malformed JSON: $stdout" }
        if ($ExpectFailure) {
            Assert-True (-not [bool]$response.Success -and $exitCode -ne 0) 'Packaged vendor bridge unexpectedly accepted an invalid request.'
        }
        else {
            Assert-True ([bool]$response.Success -and $exitCode -eq 0) "Packaged vendor bridge failed: $($response.Message) $stderr"
        }
        return $response
    }
    finally { $process.Dispose() }
}

function Get-MsiProperty {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$Name)

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    $record = $null
    try {
        $database = $installer.OpenDatabase([IO.Path]::GetFullPath($Path),0)
        $query = 'SELECT `Value` FROM `Property` WHERE `Property`=''{0}''' -f $Name.Replace("'","''")
        $view = $database.OpenView($query)
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) { return '' }
        return [string]$record.StringData(1)
    }
    finally {
        if ($null -ne $record) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        if ($null -ne $view) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        if ($null -ne $installer) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
    }
}

$requiredPaths = if ($SignaturePolicyOnly) { @($standalonePath,$msiPath) } else { @($standalonePath,$msiPath,$portablePath,$checksumPath,$releaseManifestPath,$sbomPath,$thirdPartyNoticesPath) }
foreach ($path in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Release artifact is missing: $path" }
}

if ($SignaturePolicyOnly) {
    Invoke-Check 'Executable and MSI signatures satisfy the selected release policy' {
        foreach ($artifactPath in @($standalonePath,$msiPath)) {
            $status = (Get-AuthenticodeSignature -LiteralPath $artifactPath).Status
            if ($RequireSignature) { Assert-Equal 'Valid' ([string]$status) "Signature is not valid: $artifactPath" }
            else { Assert-True ($status -in @('Valid','NotSigned','UnknownError')) ("Unexpected signature state for {0}: {1}" -f $artifactPath,$status) }
        }
    }
    Write-Host ("Package QA summary: {0} passed; {1} skipped; {2} failed" -f $script:Passed,$script:Skipped,$script:Failed) -ForegroundColor Cyan
    if ($script:Failed -gt 0) { Write-Host ($script:Failures -join "`n") -ForegroundColor Red; exit 1 }
    exit 0
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-package-qa-' + [guid]::NewGuid().ToString('N'))
$localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($localApplicationData)) { throw 'Local application data directory is unavailable.' }
$runtimeTestRoot = Join-Path $localApplicationData (Join-Path 'AVWorkstationToolkit\package-qa' ([guid]::NewGuid().ToString('N')))
$portableExtract = Join-Path $temporaryRoot 'portable'
$offlineExtract = Join-Path $temporaryRoot 'offline'
$msiExtract = Join-Path $temporaryRoot 'msi'
$downloadedRoot = Join-Path $temporaryRoot 'downloaded'
$dataRoot = Join-Path $runtimeTestRoot 'data'
$downloadedExecutable = Join-Path $downloadedRoot 'AVWorkstationToolkit.exe'
New-Item -ItemType Directory -Path $portableExtract,$offlineExtract,$msiExtract,$downloadedRoot,$dataRoot -Force | Out-Null
Copy-Item -LiteralPath $standalonePath -Destination $downloadedExecutable
$script:runtimeApplicationRoot = ''

try {
    Invoke-Check 'Release manifest identity and artifact hashes are valid' {
        $manifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
        Assert-Equal 3 $manifest.SchemaVersion 'Release schema differs.'
        Assert-Equal 'AV Workstation Toolkit' $manifest.Product 'Release product differs.'
        Assert-Equal 'Apache-2.0' ([string]$manifest.ProjectLicense) 'Release project license differs.'
        Assert-Equal $version $manifest.Version 'Release version differs.'
        Assert-Equal 'standalone-executable' $manifest.Distribution 'Release distribution differs.'
        Assert-Equal 'x64' ([string]$manifest.Architecture) 'Release architecture differs.'
        Assert-Equal 'net10.0-windows' ([string]$manifest.TargetFramework) 'Release target framework differs.'
        Assert-Equal 'Microsoft.NETCore.App.Runtime.win-x64/10.0.11' ([string]$manifest.TargetRuntime) 'Release target runtime differs.'
        Assert-Equal 'Microsoft.NETCore.App.Host.win-x64/10.0.11' ([string]$manifest.TargetHost) 'Release target host differs.'
        Assert-True ([string]$manifest.SelectedSdk -match '^10\.0\.\d{3}$') 'Release selected SDK is missing or invalid.'
        Assert-Equal 'Release' ([string]$manifest.BuildMode) 'Release build mode differs.'
        Assert-True ([string]$manifest.BuildChannel -in @('Development','ReleaseCandidate','Production')) 'Release channel is invalid.'
        Assert-True ([string]$manifest.CommitSha -match '^[a-f0-9]{40}$') 'Release commit SHA is invalid.'
        Assert-True ($manifest.SourceDirty -is [bool]) 'Release source-dirty state is not Boolean.'
        Assert-Equal 'Passed' ([string]$manifest.NuGetAudit.Status) 'NuGet audit did not pass.'
        Assert-Equal 0 ([int]$manifest.NuGetAudit.VulnerablePackages) 'Release reports vulnerable NuGet packages.'
        Assert-Equal 'net10.0-windows' ([string]$manifest.Launcher.TargetFramework) 'Release launcher target framework differs.'
        Assert-Equal '10.0.11' ([string]$manifest.Launcher.RuntimeFrameworkVersion) 'Release launcher runtime patch differs.'
        Assert-True ([int]$manifest.EmbeddedPayloadFiles -gt 10) 'Release manifest reports too few embedded runtime files.'
        Assert-Equal (Split-Path -Leaf $sbomPath) ([string]$manifest.Sbom.Name) 'Release SBOM filename differs.'
        Assert-Equal (Get-FileHash -LiteralPath $sbomPath -Algorithm SHA256).Hash ([string]$manifest.Sbom.Sha256) 'Release SBOM hash differs.'
        Assert-Equal (Split-Path -Leaf $checksumPath) ([string]$manifest.Checksums.Name) 'Release checksum filename differs.'
        Assert-Equal 'SHA-256' ([string]$manifest.Checksums.Algorithm) 'Release checksum algorithm differs.'
        Assert-Equal 1 @($manifest.Checksums.ExcludedFiles).Count 'Checksum exclusion list is broader than one file.'
        Assert-Equal (Split-Path -Leaf $checksumPath) ([string]@($manifest.Checksums.ExcludedFiles)[0]) 'Checksum file must exclude only itself.'
        Assert-True ((Split-Path -Leaf $releaseManifestPath) -in @($manifest.Checksums.CoveredFiles)) 'Release manifest is not declared as checksum-covered.'
        $expectedArtifactCount = if (@($manifest.BundledExternalPackages).Count -gt 0) { 7 } else { 6 }
        Assert-Equal $expectedArtifactCount @($manifest.Artifacts).Count 'Release artifact count differs.'
        foreach ($artifact in @($manifest.Artifacts)) {
            $artifactPath = Join-Path $ReleaseRoot ([string]$artifact.Name)
            Assert-True (Test-Path -LiteralPath $artifactPath -PathType Leaf) "Manifest artifact is missing: $($artifact.Name)"
            Assert-Equal ([string]$artifact.Sha256) (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash "Manifest hash differs for $($artifact.Name)."
        }
    }

    Invoke-Check 'Published SHA-256 checksum file matches all distributables' {
        $lines = @(Get-Content -LiteralPath $checksumPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $manifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
        Assert-Equal (@($manifest.Artifacts).Count + 1) $lines.Count 'Checksum entry count differs.'
        $coveredNames = [System.Collections.Generic.List[string]]::new()
        foreach ($line in $lines) {
            if ($line -notmatch '^([A-F0-9]{64}) \*(.+)$') { throw "Malformed checksum line: $line" }
            $coveredNames.Add($Matches[2]) | Out-Null
            $artifactPath = Join-Path $ReleaseRoot $Matches[2]
            Assert-Equal $Matches[1] (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash "Checksum differs for $($Matches[2])."
        }
        Assert-True ((Split-Path -Leaf $releaseManifestPath) -in @($coveredNames)) 'Checksum list omits the release manifest.'
        Assert-True ((Split-Path -Leaf $checksumPath) -notin @($coveredNames)) 'Checksum list contains a circular self-digest.'
        Assert-Equal (@($manifest.Checksums.CoveredFiles | Sort-Object) -join '|') (@($coveredNames | Sort-Object) -join '|') 'Checksum-covered files differ from release metadata.'
    }

    Invoke-Check 'Published third-party notices match the reviewed source inventory' {
        $sourceNoticesPath = Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md'
        Assert-Equal (Get-FileHash -LiteralPath $sourceNoticesPath -Algorithm SHA256).Hash (Get-FileHash -LiteralPath $thirdPartyNoticesPath -Algorithm SHA256).Hash 'Published third-party notices differ from the reviewed source file.'
        $noticeText = Get-Content -LiteralPath $thirdPartyNoticesPath -Raw
        foreach ($requiredNotice in @('SSH.NET','BouncyCastle.Cryptography','Microsoft.NETCore.App.Runtime.win-x64','Microsoft.NETCore.App.Host.win-x64','WixToolset.Sdk','Open Source Maintenance Fee')) {
            Assert-True ($noticeText.Contains($requiredNotice)) "Third-party notices omit: $requiredNotice"
        }
        Assert-True ($noticeText -notmatch '(?i)(?:[A-Z]:\\Users\\|/Users/|/home/)') 'Third-party notices contain a local developer path.'
    }

    Invoke-Check 'Published Apache-2.0 project license matches the reviewed source' {
        $sourceLicensePath = Join-Path $repositoryRoot 'LICENSE'
        Assert-True (Test-Path -LiteralPath $projectLicensePath -PathType Leaf) 'Published Apache-2.0 project license is missing.'
        Assert-Equal (Get-FileHash -LiteralPath $sourceLicensePath -Algorithm SHA256).Hash (Get-FileHash -LiteralPath $projectLicensePath -Algorithm SHA256).Hash 'Published project license differs from the root LICENSE.'
        $licenseText = Get-Content -LiteralPath $projectLicensePath -Raw
        Assert-True ($licenseText -match 'Apache License\s+Version 2\.0, January 2004') 'Published project license is not Apache License 2.0.'
    }

    Invoke-Check 'Executable and MSI signatures satisfy the selected release policy' {
        $manifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
        foreach ($artifactPath in @($standalonePath,$msiPath)) {
            $signature = Get-AuthenticodeSignature -LiteralPath $artifactPath
            if ($RequireSignature) {
                Assert-Equal 'Valid' ([string]$signature.Status) "Signature is not valid: $artifactPath"
                Assert-True ($null -ne $signature.TimeStamperCertificate) "RFC3161 timestamp is missing: $artifactPath"
            }
            else { Assert-True ($signature.Status -in @('Valid','NotSigned')) ("Unexpected signature state for {0}: {1}" -f $artifactPath,$signature.Status) }
            $artifactMetadata = @($manifest.Artifacts | Where-Object Name -eq (Split-Path -Leaf $artifactPath) | Select-Object -First 1)
            Assert-Equal 1 $artifactMetadata.Count "Release metadata omitted signature state: $artifactPath"
            Assert-Equal ([string]$signature.Status) ([string]$artifactMetadata[0].SignatureStatus) "Manifest signature status differs: $artifactPath"
            $expectedTimestamp = if ($signature.Status -eq 'Valid' -and $null -ne $signature.TimeStamperCertificate) { 'Valid' } elseif ($signature.Status -eq 'Valid') { 'Missing' } else { 'NotApplicable' }
            Assert-Equal $expectedTimestamp ([string]$artifactMetadata[0].TimestampStatus) "Manifest timestamp status differs: $artifactPath"
        }
        if ($RequireSignature) { Assert-True ([bool]$manifest.Signed -and [string]$manifest.SignaturePolicy -eq 'Required') 'Signature-required release metadata is not explicit.' }
        elseif (-not [bool]$manifest.Signed) { Assert-Equal 'Optional' ([string]$manifest.SignaturePolicy) 'Unsigned development release policy is not explicit.' }
    }

    Invoke-Check 'CycloneDX SBOM is valid, deterministic, and sanitized' {
        $sbomText = Get-Content -LiteralPath $sbomPath -Raw
        $sbom = $sbomText | ConvertFrom-Json
        Assert-Equal 'CycloneDX' ([string]$sbom.bomFormat) 'SBOM format differs.'
        Assert-Equal '1.6' ([string]$sbom.specVersion) 'SBOM specification version differs.'
        Assert-Equal 'AV Workstation Toolkit' ([string]$sbom.metadata.component.name) 'SBOM root component differs.'
        Assert-Equal 'Apache-2.0' ([string]$sbom.metadata.component.licenses[0].license.id) 'SBOM root project license differs.'
        Assert-Equal $version ([string]$sbom.metadata.component.version) 'SBOM version differs.'
        $componentNames = @($sbom.components.name)
        foreach ($expectedComponent in @('SSH.NET','BouncyCastle.Cryptography','Microsoft.Extensions.Logging.Abstractions','Microsoft.NETCore.App.Runtime.win-x64','Microsoft.NETCore.App.Host.win-x64','WixToolset.Sdk')) {
            Assert-True ($expectedComponent -in $componentNames) "SBOM omits reviewed dependency: $expectedComponent"
        }
        Assert-True ('Microsoft.WindowsDesktop.App.Runtime.win-x64' -notin $componentNames) 'SBOM retains the incorrect Windows Desktop runtime identity.'
        foreach ($component in @($sbom.components)) {
            Assert-True (@($component.licenses).Count -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$component.licenses[0].license.id)) "SBOM component has no reviewed license expression: $($component.name)"
            Assert-True (@($component.properties | Where-Object name -eq 'avworkstationtoolkit:dependency:distribution').Count -eq 1) "SBOM component has no distribution classification: $($component.name)"
        }
        foreach ($expectedLicense in @(@('SSH.NET','MIT'),@('BouncyCastle.Cryptography','MIT'),@('WixToolset.Sdk','MS-RL'))) {
            $component = @($sbom.components | Where-Object name -eq $expectedLicense[0])
            Assert-Equal 1 $component.Count "SBOM component is missing or duplicated: $($expectedLicense[0])"
            Assert-Equal $expectedLicense[1] ([string]$component[0].licenses[0].license.id) "Third-party license changed unexpectedly: $($expectedLicense[0])"
        }
        $dependencyInjection = @($sbom.components | Where-Object name -eq 'Microsoft.Extensions.DependencyInjection.Abstractions')
        Assert-Equal 1 $dependencyInjection.Count 'SBOM dependency-injection component is missing or duplicated.'
        Assert-Equal 'required' ([string]$dependencyInjection[0].scope) 'Untrimmed compiled runtime dependency is not represented as distributed.'
        Assert-Equal 'embedded' ([string]@($dependencyInjection[0].properties | Where-Object name -eq 'avworkstationtoolkit:dependency:distribution')[0].value) 'Compiled runtime dependency distribution reason differs.'
        $wix = @($sbom.components | Where-Object name -eq 'WixToolset.Sdk')
        Assert-Equal 1 $wix.Count 'WiX SBOM component is missing or duplicated.'
        Assert-Equal 'excluded' ([string]$wix[0].scope) 'Build-only WiX dependency is incorrectly represented as distributed.'
        Assert-True (@($wix[0].properties | Where-Object name -eq 'avworkstationtoolkit:wix:binary-terms').Count -eq 1) 'WiX binary terms are absent from the SBOM.'
        Assert-True ($sbomText -notmatch '(?i)(?:[A-Z]:\\Users\\|/Users/|/home/)' -and
            ([string]::IsNullOrWhiteSpace($env:USERNAME) -or $sbomText -notmatch [regex]::Escape($env:USERNAME))) 'SBOM contains a local user or developer path.'
    }

    Invoke-Check 'Downloaded standalone executable runs with no adjacent payload' {
        Assert-Equal 1 @(Get-ChildItem -LiteralPath $downloadedRoot -File).Count 'Downloaded executable directory contains unexpected companion files.'
        $versionInfo = (Get-Item -LiteralPath $downloadedExecutable).VersionInfo
        Assert-True ([string]$versionInfo.FileVersion -like "$version*" -and [string]$versionInfo.ProductVersion -like "$version*") 'Launcher file or product version differs.'
        Assert-Equal 'AV Workstation Toolkit' ([string]$versionInfo.ProductName) 'Launcher product name differs.'
        Assert-Equal 'AV Workstation Toolkit Project' ([string]$versionInfo.CompanyName) 'Launcher project-publisher identity differs.'
        Assert-True (-not [string]::IsNullOrWhiteSpace([string]$versionInfo.FileDescription)) 'Launcher file description is missing.'
        Assert-Equal 'AVWorkstationToolkit.dll' ([string]$versionInfo.OriginalFilename) 'Launcher original filename differs from its managed assembly identity.'
        Assert-Equal 'AVWorkstationToolkit.dll' ([string]$versionInfo.InternalName) 'Launcher internal name differs from its managed assembly identity.'
        Assert-True ([string]$versionInfo.LegalCopyright -match 'AV Workstation Toolkit contributors') 'Launcher copyright metadata is missing.'
        $launcherSignature = (Get-AuthenticodeSignature -LiteralPath $downloadedExecutable).Status
        if ($RequireSignature) { Assert-Equal 'Valid' ([string]$launcherSignature) 'Packaged launcher signature is not valid.' }
        else { Assert-True ($launcherSignature -in @('Valid','NotSigned')) "Unexpected launcher signature state: $launcherSignature" }
        $diagnosticPath = Join-Path $temporaryRoot 'downloaded-diagnostic.json'
        $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @('--verify',$diagnosticPath,'--data-root',$dataRoot)
        Assert-Equal 0 $exitCode 'Launcher verification exited unsuccessfully.'
        $diagnostic = Get-Content -LiteralPath $diagnosticPath -Raw | ConvertFrom-Json
        Assert-True ([bool]$diagnostic.Success) "Launcher diagnostic failed: $($diagnostic.IntegrityMessage)"
        Assert-Equal 2 ([int]$diagnostic.SchemaVersion) 'Launcher diagnostic schema differs.'
        Assert-Equal $version ([string]$diagnostic.Version) 'Launcher diagnostic version differs.'
        Assert-True (([string]$diagnostic.RuntimeFramework) -match '^\.NET 10\.0\.') 'Launcher is not running its embedded .NET 10 runtime.'
        Assert-True (([version]$diagnostic.RuntimeVersion) -ge [version]'10.0.11') 'Launcher embedded runtime predates the reviewed .NET 10 security baseline.'
        Assert-Equal 'X64' ([string]$diagnostic.RuntimeArchitecture) 'Launcher runtime architecture differs.'
        Assert-True ([int]$diagnostic.FilesVerified -gt 10) 'Launcher verified too few embedded runtime files.'
        Assert-True ([bool]$diagnostic.FrontendPresent) 'Extracted frontend is missing.'
        Assert-Equal 'Compiled C# WPF' ([string]$diagnostic.FrontendArchitecture) 'Packaged default frontend is not compiled WPF.'
        Assert-True ([bool]$diagnostic.WorkerPresent) 'Extracted compiled worker is missing.'
        Assert-True ([bool]$diagnostic.LegacyRecoveryPresent) 'Explicit legacy recovery runtime is missing.'
        $expectedRuntimeRoot = [IO.Path]::GetFullPath((Join-Path $dataRoot (Join-Path 'runtime' $version)))
        $actualRuntimeRoot = [IO.Path]::GetFullPath([string]$diagnostic.ApplicationRoot)
        Assert-Equal $expectedRuntimeRoot $actualRuntimeRoot 'Embedded runtime does not use the deterministic versioned location.'
        $workerPath = [IO.Path]::GetFullPath([string]$diagnostic.WorkerPath)
        Assert-Equal (Join-Path $actualRuntimeRoot 'worker\AVWorkstationToolkit.Worker.exe') $workerPath 'Compiled worker is not at the exact packaged runtime path.'
        Assert-Equal (Get-FileHash -LiteralPath $workerPath -Algorithm SHA256).Hash ([string]$diagnostic.WorkerSha256) 'Compiled worker diagnostic hash differs.'
        $releaseManifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
        Assert-Equal 'Compiled C# WPF' ([string]$releaseManifest.CompiledRuntime.Primary) 'Release manifest primary runtime differs.'
        Assert-Equal 'AVWorkstationToolkit.Worker.exe' ([string]$releaseManifest.CompiledRuntime.WorkerName) 'Release manifest worker identity differs.'
        Assert-Equal ([string]$diagnostic.WorkerSha256) ([string]$releaseManifest.CompiledRuntime.WorkerSha256) 'Release manifest worker hash differs.'
        $workerSignature = Get-AuthenticodeSignature -LiteralPath $workerPath
        Assert-Equal ([string]$workerSignature.Status) ([string]$releaseManifest.CompiledRuntime.WorkerSignatureStatus) 'Release manifest worker signature state differs.'
        if ($RequireSignature) { Assert-Equal 'Valid' ([string]$workerSignature.Status) 'Packaged compiled worker signature is not valid.' }
        foreach ($relativeNotice in @('notices\THIRD-PARTY-NOTICES.md','notices\PROJECT-LICENSE.txt','notices\DOTNET-LICENSE.txt','notices\DOTNET-THIRD-PARTY-NOTICES.txt')) {
            $runtimeNoticePath = Join-Path ([string]$diagnostic.ApplicationRoot) $relativeNotice
            Assert-True (Test-Path -LiteralPath $runtimeNoticePath -PathType Leaf) "Extracted runtime notice is missing: $relativeNotice"
            Assert-True ((Get-Item -LiteralPath $runtimeNoticePath).Length -gt 500) "Extracted runtime notice is unexpectedly small: $relativeNotice"
        }
        $script:runtimeApplicationRoot = [string]$diagnostic.ApplicationRoot
        Add-Type -AssemblyName PresentationFramework
        $packagedXamlPath = Join-Path ([string]$diagnostic.ApplicationRoot) 'app\AVWorkstationToolkit.xaml'
        [xml]$packagedXaml = Get-Content -LiteralPath $packagedXamlPath -Raw
        $packagedReader = New-Object System.Xml.XmlNodeReader $packagedXaml
        $packagedWindow = [Windows.Markup.XamlReader]::Load($packagedReader)
        try {
            Assert-True ($null -ne $packagedWindow.FindName('DiagnosticsButton')) 'Packaged XAML omitted the diagnostics view entry point.'
            Assert-True ($null -ne $packagedWindow.FindName('ManufacturerFilter')) 'Packaged XAML omitted the manufacturer filter.'
            Assert-True ($null -ne $packagedWindow.FindName('TopMenu')) 'Packaged XAML omitted the conventional application menu.'
            Assert-True ($null -ne $packagedWindow.FindName('SafetySecurityMenuItem')) 'Packaged XAML omitted the Safety & Security entry point.'
            Assert-Equal 'Check again' ([string]$packagedWindow.FindName('RecheckButton').Content) 'Packaged XAML retained the technical reboot-check label.'
            Assert-True ((Get-Content -LiteralPath $packagedXamlPath -Raw) -match '(?s)Header="Purpose / restriction".+?TextWrapping="Wrap".+?ToolTip="\{Binding Note\}"') 'Packaged XAML does not expose full purpose or restriction text.'
            foreach ($name in @('ModePill','WingetPill','PrivilegePill')) {
                Assert-True ($null -eq $packagedWindow.FindName($name)) "Packaged header retained telemetry control: $name"
            }
            Assert-True ($null -ne $packagedWindow.FindName('AllAppsButton') -and $null -ne $packagedWindow.FindName('QuickViewState')) 'Packaged XAML omitted composable quick-view controls.'
            $packagedGrid = $packagedWindow.FindName('PackageGrid')
            Assert-True (-not $packagedGrid.Columns[0].CanUserSort -and -not $packagedGrid.Columns[7].CanUserSort) 'Packaged non-sortable grid columns changed.'
            Assert-Equal 'ApplicationSortKey|VendorSortKey|PrioritySortKey|StatusSortKey|VersionSortKey|RiskSortKey' (($packagedGrid.Columns[1..6] | ForEach-Object SortMemberPath) -join '|') 'Packaged sortable columns differ from source.'
        }
        finally { $packagedWindow.Close() }
        Assert-Equal (Get-FileHash -LiteralPath $downloadedExecutable -Algorithm SHA256).Hash ([string]$releaseManifest.Launcher.Sha256) 'Release launcher hash differs.'
        Assert-Equal ([string]$launcherSignature) ([string]$releaseManifest.Launcher.SignatureStatus) 'Release launcher signature state differs.'
    }

    Invoke-Check 'Second launcher verification leaves an unchanged runtime cache untouched' {
        Assert-True (-not [string]::IsNullOrWhiteSpace($script:runtimeApplicationRoot)) 'Initial launcher verification did not expose the runtime directory.'
        $before = @{}
        foreach ($file in @(Get-ChildItem -LiteralPath $script:runtimeApplicationRoot -Recurse -File)) {
            $relative = $file.FullName.Substring($script:runtimeApplicationRoot.Length).TrimStart('\')
            $before[$relative] = [pscustomobject]@{
                Hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
                LastWriteTimeUtc = $file.LastWriteTimeUtc
            }
        }
        Assert-True ($before.Count -gt 10) 'Versioned runtime contains too few files for no-rewrite validation.'
        $secondDiagnosticPath = Join-Path $temporaryRoot 'downloaded-diagnostic-second.json'
        $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @('--verify',$secondDiagnosticPath,'--data-root',$dataRoot)
        Assert-Equal 0 $exitCode 'Second launcher verification failed.'
        foreach ($file in @(Get-ChildItem -LiteralPath $script:runtimeApplicationRoot -Recurse -File)) {
            $relative = $file.FullName.Substring($script:runtimeApplicationRoot.Length).TrimStart('\')
            Assert-True $before.ContainsKey($relative) "Second verification added an unexpected runtime file: $relative"
            Assert-Equal $before[$relative].Hash (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash "Second verification changed runtime content: $relative"
            Assert-Equal $before[$relative].LastWriteTimeUtc $file.LastWriteTimeUtc "Second verification rewrote unchanged runtime content: $relative"
        }
        $embeddedPolicy = Join-Path $script:runtimeApplicationRoot 'manifests\process-launch-policy.json'
        Assert-True (Test-Path -LiteralPath $embeddedPolicy -PathType Leaf) 'Packaged runtime omitted the process-launch policy contract.'
    }

    Invoke-Check 'MSI and executable product identities agree' {
        Assert-Equal 'AV Workstation Toolkit' (Get-MsiProperty -Path $msiPath -Name 'ProductName') 'MSI product name differs.'
        Assert-Equal '{7A3A4978-78F0-5824-B93F-A2C741BF853E}' (Get-MsiProperty -Path $msiPath -Name 'UpgradeCode') 'MSI upgrade family no longer matches the released legacy package family.'
        Assert-True ((Get-MsiProperty -Path $msiPath -Name 'ProductCode') -ne '{8030A656-389C-46B3-8D17-F7F1912C2864}') 'MSI ProductCode did not change for the 1.1.1 major upgrade.'
        Assert-Equal 'AV Workstation Toolkit Project' (Get-MsiProperty -Path $msiPath -Name 'Manufacturer') 'MSI manufacturer differs.'
        Assert-Equal $version (Get-MsiProperty -Path $msiPath -Name 'ProductVersion') 'MSI product version differs.'
        $versionInfo = (Get-Item -LiteralPath $standalonePath).VersionInfo
        Assert-Equal 'AV Workstation Toolkit' ([string]$versionInfo.FileDescription) 'Launcher file description differs.'
        Assert-Equal 'AV Workstation Toolkit Project' ([string]$versionInfo.CompanyName) 'Launcher project-publisher metadata differs.'
        Assert-Equal 'AVWorkstationToolkit' ([IO.Path]::GetFileNameWithoutExtension([string]$versionInfo.OriginalFilename)) 'Launcher original filename base differs.'
        Assert-Equal 'AVWorkstationToolkit' ([IO.Path]::GetFileNameWithoutExtension([string]$versionInfo.InternalName)) 'Launcher internal-name base differs.'
        Assert-True ([string]$versionInfo.ProductVersion -like "$version*") 'EXE product version differs from MSI/release identity.'
    }

    Invoke-Check 'Downloaded standalone executable includes the credential and vendor transport bridge' {
        $operation = if ($SkipDesktopSmoke) { 'SelfTest' } else { 'CredentialRoundTripTest' }
        $response = Invoke-PackagedVendorBridge -Launcher $downloadedExecutable -Request ([pscustomobject]@{ Operation=$operation })
        Assert-True ([bool]$response.Success) "Vendor bridge self-test failed: $($response.Message)"
        Assert-Equal $operation ([string]$response.Operation) 'Vendor bridge self-test operation differs.'
        if ($SkipDesktopSmoke) {
            Assert-True ([string]$response.Message -match 'Credential Manager API.*HTTPS.*SFTP') 'CI-safe bridge self-test did not confirm all required libraries.'
        }
        else {
            Assert-True ([string]$response.Message -match 'write, read, and delete') 'Local bridge self-test did not complete the Credential Manager round trip.'
        }
    }

    Invoke-Check 'Packaged vendor bridge rejects unknown request properties' {
        $response = Invoke-PackagedVendorBridge -Launcher $downloadedExecutable -ExpectFailure -Request ([pscustomobject]@{
            Operation='SelfTest'
            Unexpected='reject me'
        })
        Assert-True ([string]$response.Message -match '(?i)unmapped|unexpected') 'Strict vendor request-schema rejection was not reported.'
    }

    Expand-Archive -LiteralPath $portablePath -DestinationPath $portableExtract
    $portableLauncher = Join-Path $portableExtract 'AVWorkstationToolkit.exe'
    Invoke-Check 'Portable ZIP contains the same single standalone executable' {
        $portableFiles = @(Get-ChildItem -LiteralPath $portableExtract -Recurse -File)
        Assert-Equal 1 $portableFiles.Count 'Portable ZIP contains unexpected companion files.'
        Assert-True (Test-Path -LiteralPath $portableLauncher -PathType Leaf) 'Portable ZIP is missing AVWorkstationToolkit.exe.'
        Assert-Equal (Get-FileHash -LiteralPath $standalonePath -Algorithm SHA256).Hash (Get-FileHash -LiteralPath $portableLauncher -Algorithm SHA256).Hash 'Portable executable differs from the direct download.'
    }

    if ($SkipDesktopSmoke) {
        $script:Skipped++
        Write-Host 'SKIP  Packaged WPF control and workflow smoke test requires an interactive Windows desktop.' -ForegroundColor Yellow
    }
    else {
        Invoke-Check 'Packaged production compiled WPF smoke runs through AVWorkstationToolkit.exe' {
            $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @('--production-smoke')
            Assert-Equal 0 $exitCode "Packaged production compiled WPF smoke process failed. $script:LastLauncherStdErr"
        }
        Invoke-Check 'Explicit legacy PowerShell recovery requires its deliberate switch' {
            $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @(
                '--legacy-powershell-recovery','--smoke-test','--wait','--data-root',$dataRoot)
            Assert-Equal 0 $exitCode 'Explicit packaged legacy recovery smoke process failed.'
        }
    }

    Invoke-Check 'Standalone launcher repairs a modified runtime cache from embedded content' {
        $diagnosticPath = Join-Path $temporaryRoot 'repair-before.json'
        $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @('--verify',$diagnosticPath,'--data-root',$dataRoot)
        Assert-Equal 0 $exitCode 'Initial runtime preparation failed.'
        $diagnostic = Get-Content -LiteralPath $diagnosticPath -Raw | ConvertFrom-Json
        $cachedXaml = Join-Path ([string]$diagnostic.ApplicationRoot) 'app\AVWorkstationToolkit.xaml'
        $expectedHash = (Get-FileHash -LiteralPath $cachedXaml -Algorithm SHA256).Hash
        $cachedWorker = Join-Path ([string]$diagnostic.ApplicationRoot) 'worker\AVWorkstationToolkit.Worker.exe'
        $expectedWorkerHash = (Get-FileHash -LiteralPath $cachedWorker -Algorithm SHA256).Hash
        Add-Content -LiteralPath $cachedXaml -Value '<!-- tamper test -->' -Encoding UTF8
        Add-Content -LiteralPath $cachedWorker -Value 'tamper test' -Encoding UTF8
        Assert-True ((Get-FileHash -LiteralPath $cachedXaml -Algorithm SHA256).Hash -ne $expectedHash) 'Runtime cache tamper setup did not alter the file.'
        Assert-True ((Get-FileHash -LiteralPath $cachedWorker -Algorithm SHA256).Hash -ne $expectedWorkerHash) 'Compiled worker tamper setup did not alter the file.'
        $repairDiagnosticPath = Join-Path $temporaryRoot 'repair-after.json'
        $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @('--verify',$repairDiagnosticPath,'--data-root',$dataRoot)
        Assert-Equal 0 $exitCode 'Launcher did not repair the modified runtime cache.'
        Assert-Equal $expectedHash (Get-FileHash -LiteralPath $cachedXaml -Algorithm SHA256).Hash 'Repaired runtime cache hash differs.'
        Assert-Equal $expectedWorkerHash (Get-FileHash -LiteralPath $cachedWorker -Algorithm SHA256).Hash 'Repaired compiled worker hash differs.'
    }

    Invoke-Check 'MSI administratively extracts a verifiable standalone AVWorkstationToolkit executable' {
        $arguments = '/a "{0}" /qn TARGETDIR="{1}" /L*v "{2}"' -f $msiPath,$msiExtract,(Join-Path $temporaryRoot 'msi-extract.log')
        $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $arguments -PassThru
        $exitCode = Wait-AVWorkstationToolkitProcess -Process $process -Description 'MSI administrative extraction'
        Assert-Equal 0 $exitCode 'MSI administrative extraction failed.'
        $extractedLauncher = @(Get-ChildItem -LiteralPath $msiExtract -Recurse -File -Filter 'AVWorkstationToolkit.exe') | Select-Object -First 1
        Assert-True ($null -ne $extractedLauncher) 'MSI did not contain AVWorkstationToolkit.exe.'
        Assert-Equal (Get-FileHash -LiteralPath $standalonePath -Algorithm SHA256).Hash (Get-FileHash -LiteralPath $extractedLauncher.FullName -Algorithm SHA256).Hash 'MSI-contained launcher differs from the standalone release executable.'
        $extractedSignature = Get-AuthenticodeSignature -LiteralPath $extractedLauncher.FullName
        if ($RequireSignature) {
            Assert-Equal 'Valid' ([string]$extractedSignature.Status) 'MSI-contained launcher signature is not valid.'
            Assert-True ($null -ne $extractedSignature.TimeStamperCertificate) 'MSI-contained launcher is missing its RFC3161 timestamp.'
        }
        else { Assert-True ($extractedSignature.Status -in @('Valid','NotSigned')) "Unexpected MSI-contained launcher signature state: $($extractedSignature.Status)" }
        $diagnosticPath = Join-Path $temporaryRoot 'msi-diagnostic.json'
        $exitCode = Invoke-PackagedLauncher -Launcher $extractedLauncher.FullName -Arguments @('--verify',$diagnosticPath,'--data-root',$dataRoot)
        Assert-Equal 0 $exitCode 'MSI-extracted launcher verification failed.'
    }

    Invoke-Check 'Optional offline bundle is absent or contains only verified catalogued payloads' {
        $manifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
        $bundledIds = @($manifest.BundledExternalPackages)
        $offlineBundlePath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-offline-bundle.zip" -f $version)
        if ($bundledIds.Count -eq 0) {
            Assert-True (-not (Test-Path -LiteralPath $offlineBundlePath)) 'An undeclared offline bundle is present.'
            return
        }

        Assert-True (Test-Path -LiteralPath $offlineBundlePath -PathType Leaf) 'Declared offline bundle is missing.'
        Expand-Archive -LiteralPath $offlineBundlePath -DestinationPath $offlineExtract
        $offlineLauncher = Join-Path $offlineExtract 'AVWorkstationToolkit.exe'
        $offlineIndexPath = Join-Path $offlineExtract 'AVWorkstationToolkit-offline-bundle.json'
        Assert-True (Test-Path -LiteralPath $offlineLauncher -PathType Leaf) 'Offline bundle launcher is missing.'
        Assert-Equal (Get-FileHash -LiteralPath $standalonePath -Algorithm SHA256).Hash (Get-FileHash -LiteralPath $offlineLauncher -Algorithm SHA256).Hash 'Offline launcher differs from the standalone artifact.'
        Assert-True (Test-Path -LiteralPath $offlineIndexPath -PathType Leaf) 'Offline bundle index is missing.'
        $offlineIndex = Get-Content -LiteralPath $offlineIndexPath -Raw | ConvertFrom-Json
        Assert-Equal ($bundledIds -join '|') (@($offlineIndex.Packages.Id) -join '|') 'Offline bundle package IDs differ from release metadata.'
        foreach ($package in @($offlineIndex.Packages)) {
            $relative = ([string]$package.RelativePath).Replace('/',[IO.Path]::DirectorySeparatorChar)
            $payloadPath = [IO.Path]::GetFullPath((Join-Path $offlineExtract $relative))
            $offlinePrefix = [IO.Path]::GetFullPath($offlineExtract).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            Assert-True ($payloadPath.StartsWith($offlinePrefix,[StringComparison]::OrdinalIgnoreCase)) "Offline payload escaped extraction: $($package.Id)"
            Assert-True (Test-Path -LiteralPath $payloadPath -PathType Leaf) "Offline payload is missing: $($package.Id)"
            Assert-Equal ([string]$package.Sha256) (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash "Offline payload hash differs: $($package.Id)"
        }
        if (-not $SkipDesktopSmoke) {
            $offlineDataRoot = Join-Path $runtimeTestRoot 'offline-data'
            $exitCode = Invoke-PackagedLauncher -Launcher $offlineLauncher -Arguments @('--smoke-test','--wait','--data-root',$offlineDataRoot)
            Assert-Equal 0 $exitCode 'Offline bundle WPF smoke process failed.'
        }
    }
}
finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    $systemTemporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTemporary.StartsWith($systemTemporary,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedTemporary)) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force -ErrorAction SilentlyContinue
    }
    $resolvedRuntimeTestRoot = [IO.Path]::GetFullPath($runtimeTestRoot)
    $runtimeTestPrefix = [IO.Path]::GetFullPath((Join-Path $localApplicationData 'AVWorkstationToolkit\package-qa')).TrimEnd('\') + '\'
    if ($resolvedRuntimeTestRoot.StartsWith($runtimeTestPrefix,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedRuntimeTestRoot)) {
        Remove-Item -LiteralPath $resolvedRuntimeTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host ("Package QA summary: {0} passed; {1} skipped; {2} failed" -f $script:Passed,$script:Skipped,$script:Failed) -ForegroundColor Cyan
if ($script:Failed -gt 0) {
    Write-Host ($script:Failures -join "`n") -ForegroundColor Red
    exit 1
}
exit 0
