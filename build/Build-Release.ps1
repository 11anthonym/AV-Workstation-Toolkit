<#
.SYNOPSIS
    Builds the standalone AV Workstation Toolkit executable, portable package, x64 Windows Installer, and optional offline package bundle.

.DESCRIPTION
    Runs non-installing QA, publishes the self-contained compiled WPF runtime
    with its integrity-pinned compiled worker and explicit legacy recovery runtime embedded, builds an MSI with pinned WiX
    tooling, creates a one-file portable ZIP, and emits SHA-256 checksums. If a
    code-signing certificate thumbprint is supplied, the executable and MSI are
    signed before their hashes are recorded. BuildOfflineBundle additionally
    validates and packages catalogued redistributable installers from the local
    external package depot.
#>

[CmdletBinding()]
param(
    [string]$Version,
    [string]$CertificateThumbprint,
    [ValidateSet('Auto','CurrentUser','LocalMachine')]
    [string]$CertificateStore = 'Auto',
    [uri]$TimestampServer = 'https://timestamp.digicert.com',
    [string]$SignToolPath,
    [switch]$RequireSignature,
    [ValidateSet('Development','ReleaseCandidate','Production')]
    [string]$BuildChannel = 'Development',
    [switch]$BuildOfflineBundle,
    [string]$ExternalPackageRoot,
    [switch]$ScanWithDefender,
    [switch]$RequireDefender,
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($TimestampServer.Scheme -ne [Uri]::UriSchemeHttps -or -not [string]::IsNullOrEmpty($TimestampServer.UserInfo)) {
    throw 'TimestampServer must be an absolute HTTPS URI without embedded credentials.'
}
if ($RequireDefender -and -not $ScanWithDefender) { throw 'RequireDefender requires ScanWithDefender.' }
if (($RequireSignature -or $BuildChannel -eq 'Production') -and [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw 'A signed production release requires CertificateThumbprint; unsigned output is supported only for explicit development or release-candidate builds.'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$versionPath = Join-Path $repositoryRoot 'VERSION'
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content -LiteralPath $versionPath -Raw).Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Release version must use three numeric fields: $Version"
}
$declaredVersion = (Get-Content -LiteralPath $versionPath -Raw).Trim()
if ($Version -ne $declaredVersion) {
    throw "Requested version $Version does not match VERSION $declaredVersion."
}
$buildTimestamp = (Get-Date).ToUniversalTime().ToString('o')

$gitCommand = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $gitCommand) { throw 'Git is required to record the exact release commit in build metadata.' }
$commitSha = (& $gitCommand.Source -C $repositoryRoot rev-parse HEAD 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $commitSha -notmatch '^[a-fA-F0-9]{40}$') { throw 'The current Git commit could not be resolved for release metadata.' }
$sourceStatus = (& $gitCommand.Source -C $repositoryRoot status --porcelain 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Git working-tree status could not be resolved for release metadata.' }
$sourceDirty = -not [string]::IsNullOrWhiteSpace($sourceStatus)
if ($BuildChannel -eq 'Production' -and $sourceDirty) {
    throw 'Production release builds require a clean working tree so the recorded commit exactly identifies the source.'
}

$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot (Join-Path 'staging' $Version)))
$workerStagingRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot (Join-Path 'staging' ($Version + '-worker'))))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot (Join-Path 'release' $Version)))
$intermediateRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot (Join-Path 'obj' $Version)))
$offlineStagingRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot (Join-Path 'staging' ($Version + '-offline-bundle'))))
if ([string]::IsNullOrWhiteSpace($ExternalPackageRoot)) {
    $ExternalPackageRoot = Join-Path $repositoryRoot 'external-packages'
}

function Assert-ArtifactChildPath {
    param([Parameter(Mandatory)][string]$Path)
    $rootWithSeparator = $artifactsRoot.TrimEnd('\') + '\'
    if (-not $Path.StartsWith($rootWithSeparator,[StringComparison]::OrdinalIgnoreCase)) {
        throw "Build target is outside the repository artifacts directory: $Path"
    }
}

function Reset-BuildDirectory {
    param([Parameter(Mandatory)][string]$Path)

    Assert-ArtifactChildPath -Path $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
        return
    }

    try {
        Get-ChildItem -LiteralPath $Path -Force | Remove-Item -Recurse -Force
    }
    catch {
        throw "Build output could not be cleaned. Close AV Workstation Toolkit and any process using '$Path', then retry. $($_.Exception.Message)"
    }
}

function Resolve-AVWorkstationToolkitSignTool {
    param([string]$RequestedPath)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) { $candidates.Add($RequestedPath) }
    $command = Get-Command signtool.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) { $candidates.Add($command.Source) }
    $windowsKitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $windowsKitsRoot -PathType Container) {
        foreach ($directory in @(Get-ChildItem -LiteralPath $windowsKitsRoot -Directory | Sort-Object Name -Descending)) {
            $candidates.Add((Join-Path $directory.FullName 'x64\signtool.exe'))
        }
    }

    $resolved = @($candidates | ForEach-Object {
        if (-not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf)) { [IO.Path]::GetFullPath($_) }
    } | Select-Object -First 1)
    if ($resolved.Count -ne 1) { throw 'Windows SDK signtool.exe is required for RFC3161 Authenticode signing but was not found. Install a Windows SDK or pass -SignToolPath.' }
    $toolSignature = Get-AuthenticodeSignature -LiteralPath $resolved[0]
    $toolSigner = if ($null -ne $toolSignature.SignerCertificate) { [string]$toolSignature.SignerCertificate.Subject } else { '' }
    if ($toolSignature.Status -ne 'Valid' -or $toolSigner -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
        throw "The resolved signing tool is not a valid Microsoft-signed Windows SDK executable: $($resolved[0])"
    }
    return $resolved[0]
}

function Get-AVWorkstationToolkitSignatureMetadata {
    param([Parameter(Mandatory)][string]$Path)

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    [ordered]@{
        Status = [string]$signature.Status
        SignerSubject = if ($null -ne $signature.SignerCertificate) { [string]$signature.SignerCertificate.Subject } else { '' }
        SignerThumbprint = if ($null -ne $signature.SignerCertificate) { [string]$signature.SignerCertificate.Thumbprint } else { '' }
        TimestampStatus = if ($signature.Status -eq 'Valid' -and $null -ne $signature.TimeStamperCertificate) { 'Valid' } elseif ($signature.Status -eq 'Valid') { 'Missing' } else { 'NotApplicable' }
        TimestampSignerSubject = if ($null -ne $signature.TimeStamperCertificate) { [string]$signature.TimeStamperCertificate.Subject } else { '' }
    }
}

function Invoke-AVWorkstationToolkitArtifactSigning {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Thumbprint,
        [Parameter(Mandatory)][ValidateSet('CurrentUser','LocalMachine')][string]$Store,
        [Parameter(Mandatory)][string]$ToolPath
    )

    $arguments = @('sign','/sha1',$Thumbprint,'/s','My','/fd','SHA256','/tr',$TimestampServer.AbsoluteUri,'/td','SHA256')
    if ($Store -eq 'LocalMachine') { $arguments += '/sm' }
    $arguments += @('/v',[IO.Path]::GetFullPath($Path))
    $signOutput = (& $ToolPath @arguments 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for $(Split-Path -Leaf $Path): $signOutput" }

    $verifyOutput = (& $ToolPath verify /pa /all /v ([IO.Path]::GetFullPath($Path)) 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for $(Split-Path -Leaf $Path): $verifyOutput" }
    $metadata = Get-AVWorkstationToolkitSignatureMetadata -Path $Path
    if ($metadata.Status -ne 'Valid' -or $metadata.TimestampStatus -ne 'Valid' -or
        -not $metadata.SignerThumbprint.Equals($Thumbprint,[StringComparison]::OrdinalIgnoreCase)) {
        throw "Signed artifact identity or RFC3161 timestamp validation failed for $(Split-Path -Leaf $Path)."
    }
}

$globalJsonPath = Join-Path $repositoryRoot 'global.json'
$globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
if ([string]$globalJson.sdk.version -ne '10.0.100' -or [string]$globalJson.sdk.rollForward -ne 'latestFeature' -or [bool]$globalJson.sdk.allowPrerelease) {
    throw 'global.json must select stable .NET 10 SDKs from 10.0.100 through the latest installed .NET 10 feature band.'
}

$dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $dotnetCommand) {
    $knownDotnet = 'C:\Program Files\dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $knownDotnet -PathType Leaf)) {
        throw 'A stable .NET 10 SDK (10.0.100 or a later .NET 10 feature band) is required. Install the .NET 10 SDK, then run dotnet --list-sdks.'
    }
    $dotnetPath = $knownDotnet
}
else {
    $dotnetPath = $dotnetCommand.Source
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$nativeErrorPreference = $ErrorActionPreference
try {
    # Windows PowerShell 5.1 promotes native stderr to ErrorRecord objects.
    # Capture the tool's exit code and text explicitly so a missing SDK reaches
    # the actionable prerequisite error below instead of terminating early.
    $ErrorActionPreference = 'Continue'
    $installedSdkText = (& $dotnetPath --list-sdks 2>&1 | Out-String).Trim()
    $sdkListExitCode = $LASTEXITCODE
    Push-Location $repositoryRoot
    try { $dotnetVersionText = (& $dotnetPath --version 2>&1 | Out-String).Trim(); $sdkVersionExitCode = $LASTEXITCODE }
    finally { Pop-Location }
}
finally { $ErrorActionPreference = $nativeErrorPreference }
if ($sdkListExitCode -ne 0 -or $sdkVersionExitCode -ne 0 -or $dotnetVersionText -notmatch '^10\.0\.\d{3}$') {
    $installedDescription = if ([string]::IsNullOrWhiteSpace($installedSdkText)) { 'No SDKs were reported.' } else { $installedSdkText }
    throw "A stable .NET 10 SDK (10.0.100 or a later .NET 10 feature band) is required by global.json. Resolved host: '$dotnetPath'. Selected SDK: '$dotnetVersionText'. Installed SDKs:`r`n$installedDescription`r`nInstall the .NET 10 SDK and retry."
}

$launcherProject = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj'
$workerProject = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Worker\AVWorkstationToolkit.Worker.csproj'
$projectLicenseSourcePath = Join-Path $repositoryRoot 'LICENSE'
if (-not (Test-Path -LiteralPath $projectLicenseSourcePath -PathType Leaf)) {
    throw 'LICENSE is required for every release build.'
}
$thirdPartyNoticesSourcePath = Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md'
if (-not (Test-Path -LiteralPath $thirdPartyNoticesSourcePath -PathType Leaf)) {
    throw 'THIRD-PARTY-NOTICES.md is required for every release build.'
}
Push-Location $repositoryRoot
try {
    $nativeErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $dotnetPath restore $launcherProject --locked-mode --nologo 2>&1 | Out-String | Out-Null
        $launcherRestoreExitCode = $LASTEXITCODE
        & $dotnetPath restore $workerProject --locked-mode --nologo 2>&1 | Out-String | Out-Null
        $workerRestoreExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $nativeErrorPreference }
}
finally { Pop-Location }
if ($launcherRestoreExitCode -ne 0 -or $workerRestoreExitCode -ne 0) {
    throw "The locked dependency graph is stale or could not be restored. Run:`r`n  dotnet restore .\src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj --force-evaluate`r`n  dotnet restore .\src\AVWorkstationToolkit.Worker\AVWorkstationToolkit.Worker.csproj --force-evaluate`r`nReview packages.lock.json before committing. Release builds never rewrite the dependency lock files."
}

Push-Location $repositoryRoot
try {
    $nativeErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $nugetAuditText = (& $dotnetPath list $launcherProject package --vulnerable --include-transitive --format json --no-restore 2>&1 | Out-String).Trim()
        $nugetAuditExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $nativeErrorPreference }
}
finally { Pop-Location }
if ($nugetAuditExitCode -ne 0) { throw "NuGet vulnerability audit could not complete: $nugetAuditText" }
$jsonStart = $nugetAuditText.IndexOf('{')
if ($jsonStart -lt 0) { throw 'NuGet vulnerability audit did not return machine-readable JSON.' }
try { $nugetAudit = $nugetAuditText.Substring($jsonStart) | ConvertFrom-Json -ErrorAction Stop }
catch { throw "NuGet vulnerability audit JSON is invalid: $($_.Exception.Message)" }
$vulnerablePackages = [System.Collections.Generic.List[object]]::new()
foreach ($project in @($nugetAudit.projects)) {
    if ($project.PSObject.Properties.Name -notcontains 'frameworks') { continue }
    foreach ($framework in @($project.frameworks)) {
        $topLevel = if ($framework.PSObject.Properties.Name -contains 'topLevelPackages') { @($framework.topLevelPackages) } else { @() }
        $transitive = if ($framework.PSObject.Properties.Name -contains 'transitivePackages') { @($framework.transitivePackages) } else { @() }
        foreach ($package in @($topLevel + $transitive)) {
            if ($null -ne $package -and $package.PSObject.Properties.Name -contains 'vulnerabilities' -and @($package.vulnerabilities).Count -gt 0) {
                $vulnerablePackages.Add($package) | Out-Null
            }
        }
    }
}
if ($vulnerablePackages.Count -gt 0) {
    $details = @($vulnerablePackages | ForEach-Object { '{0} {1}' -f $_.id,$_.resolvedVersion }) -join ', '
    throw "NuGet vulnerability audit reported vulnerable release dependencies: $details"
}

$catalogCompiler = Join-Path $repositoryRoot 'build\Compile-CommercialCatalog.ps1'
& $catalogCompiler -Check
if ($LASTEXITCODE -ne 0) { throw 'Commercial catalog compilation check failed.' }

$moduleManifest = Test-ModuleManifest -Path (Join-Path $repositoryRoot 'scripts\AVWorkstationToolkit.Core.psd1')
if ([string]$moduleManifest.Version -ne $Version) {
    throw "PowerShell module version $($moduleManifest.Version) does not match release version $Version."
}
$xamlIdentity = Get-Content -LiteralPath (Join-Path $repositoryRoot 'app\AVWorkstationToolkit.xaml') -Raw
if ($xamlIdentity -notmatch ('AV Workstation Toolkit ' + [regex]::Escape($Version))) {
    throw "WPF identity does not contain release version $Version."
}
$launcherProjectIdentity = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj') -Raw
$launcherProjectXml = [xml]$launcherProjectIdentity
$launcherProperties = @($launcherProjectXml.Project.PropertyGroup | Where-Object { $null -ne $_.TargetFramework } | Select-Object -First 1)
if ($launcherProperties.Count -ne 1 -or [string]$launcherProperties[0].TargetFramework -ne 'net10.0-windows' -or
    [string]$launcherProperties[0].RuntimeFrameworkVersion -ne '10.0.11') {
    throw 'Launcher must target the reviewed .NET 10.0.11 self-contained runtime.'
}
if ([string]$launcherProperties[0].Company -ne 'AV Workstation Toolkit Project' -or [string]$launcherProperties[0].Product -ne 'AV Workstation Toolkit' -or
    [string]$launcherProperties[0].Title -ne 'AV Workstation Toolkit' -or [string]$launcherProperties[0].AssemblyTitle -ne 'AV Workstation Toolkit' -or
    [string]::IsNullOrWhiteSpace([string]$launcherProperties[0].Description) -or
    [string]::IsNullOrWhiteSpace([string]$launcherProperties[0].Copyright) -or
    [string]$launcherProperties[0].EnableCompressionInSingleFile -ne 'false' -or
    [string]$launcherProperties[0].UseWPF -ne 'true' -or
    [string]$launcherProperties[0].PublishTrimmed -ne 'false') {
    throw 'Launcher identity metadata or compiled-WPF single-file policy is incomplete.'
}
$workerProjectIdentity = Get-Content -LiteralPath $workerProject -Raw
$workerProjectXml = [xml]$workerProjectIdentity
$workerProperties = @($workerProjectXml.Project.PropertyGroup | Where-Object { $null -ne $_.TargetFramework } | Select-Object -First 1)
if ($workerProperties.Count -ne 1 -or [string]$workerProperties[0].TargetFramework -ne 'net10.0-windows' -or
    [string]$workerProperties[0].RuntimeFrameworkVersion -ne '10.0.11' -or
    [string]$workerProperties[0].SelfContained -ne 'true' -or
    [string]$workerProperties[0].PublishSingleFile -ne 'true' -or
    [string]$workerProperties[0].PublishTrimmed -ne 'false' -or
    [string]$workerProperties[0].Product -ne 'AV Workstation Toolkit compiled worker') {
    throw 'Compiled worker identity or self-contained single-file policy is incomplete.'
}
$launcherManifestIdentity = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\app.manifest') -Raw
if ($launcherProjectIdentity -notmatch ('<Version>' + [regex]::Escape($Version) + '</Version>') -or
    $launcherManifestIdentity -notmatch ('version="' + [regex]::Escape($Version) + '\.0"')) {
    throw "Launcher project or application manifest does not match release version $Version."
}

$releaseSourceRoots = @('app','catalog','scripts','manifests','docs','src','installer','build','tests')
$sourceReparsePoints = @($releaseSourceRoots | ForEach-Object {
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot $_) -Recurse -Force -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }
})
if ($sourceReparsePoints.Count -gt 0) {
    throw ('Release source contains unsupported reparse points: {0}' -f ($sourceReparsePoints.FullName -join ', '))
}

foreach ($target in @($stagingRoot,$workerStagingRoot,$releaseRoot,$intermediateRoot,$offlineStagingRoot)) {
    Reset-BuildDirectory -Path $target
}

if (-not $SkipTests) {
    & powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File (Join-Path $repositoryRoot 'tests\Run-Tests.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'AV Workstation Toolkit source QA failed.' }
    & powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $repositoryRoot 'tests\Test-CSharpMigration.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'AV Workstation Toolkit C# migration QA failed.' }
}

$certificate = $null
$certificateStoreName = ''
$resolvedSignToolPath = ''
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $normalizedThumbprint = ($CertificateThumbprint -replace '\s','').ToUpperInvariant()
    $storeRecords = switch ($CertificateStore) {
        'CurrentUser' { @([pscustomobject]@{ Name='CurrentUser'; Path='Cert:\CurrentUser\My' }) }
        'LocalMachine' { @([pscustomobject]@{ Name='LocalMachine'; Path='Cert:\LocalMachine\My' }) }
        default { @(
            [pscustomobject]@{ Name='CurrentUser'; Path='Cert:\CurrentUser\My' },
            [pscustomobject]@{ Name='LocalMachine'; Path='Cert:\LocalMachine\My' }
        ) }
    }
    $certificateMatches = @($storeRecords | ForEach-Object {
        $storeRecord = $_
        Get-ChildItem -LiteralPath $storeRecord.Path | Where-Object { $_.Thumbprint -eq $normalizedThumbprint -and $_.HasPrivateKey } |
            ForEach-Object { [pscustomobject]@{ Certificate=$_; Store=$storeRecord.Name } }
    } | Select-Object -First 1)
    if ($certificateMatches.Count -eq 1) {
        $certificate = $certificateMatches[0].Certificate
        $certificateStoreName = [string]$certificateMatches[0].Store
    }
    if ($null -eq $certificate -or -not $certificate.HasPrivateKey) {
        throw "A code-signing certificate with an accessible private key was not found in the selected store scope: $normalizedThumbprint"
    }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    if ($codeSigningOid -notin @($certificate.EnhancedKeyUsageList.ObjectId.Value)) {
        throw "Certificate $normalizedThumbprint is not valid for code signing."
    }
    $now = Get-Date
    if ($certificate.NotBefore -gt $now -or $certificate.NotAfter -le $now) {
        throw "Certificate $normalizedThumbprint is outside its validity period."
    }
    $resolvedSignToolPath = Resolve-AVWorkstationToolkitSignTool -RequestedPath $SignToolPath
}

& $dotnetPath publish $workerProject -c Release -r win-x64 --self-contained true --nologo --no-restore `
    -p:Version=$Version -p:AssemblyVersion="$Version.0" -p:FileVersion="$Version.0" `
    -p:ContinuousIntegrationBuild=true -p:DebugSymbols=false -p:DebugType=None -o $workerStagingRoot
if ($LASTEXITCODE -ne 0) { throw 'AV Workstation Toolkit compiled worker publish failed.' }
$workerPayloadPath = Join-Path $workerStagingRoot 'AVWorkstationToolkit.Worker.exe'
if (-not (Test-Path -LiteralPath $workerPayloadPath -PathType Leaf) -or
    @(Get-ChildItem -LiteralPath $workerStagingRoot -File).Count -ne 1) {
    throw 'The compiled worker publish did not produce exactly one self-contained executable.'
}
if ($null -ne $certificate) {
    Invoke-AVWorkstationToolkitArtifactSigning -Path $workerPayloadPath -Thumbprint $normalizedThumbprint -Store $certificateStoreName -ToolPath $resolvedSignToolPath
}

$embeddedPayloadFiles = @(
    Get-Item -LiteralPath (Join-Path $repositoryRoot 'app\AVWorkstationToolkit.xaml')
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'scripts') -File |
        Where-Object Extension -in @('.ps1','.psd1','.psm1')
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'manifests') -File -Filter '*.json'
    Get-Item -LiteralPath $workerPayloadPath
)
if ($embeddedPayloadFiles.Count -lt 10) {
    throw "The standalone executable would embed too few runtime files: $($embeddedPayloadFiles.Count)"
}

& $dotnetPath publish $launcherProject -c Release -r win-x64 --self-contained true --nologo --no-restore `
    -p:Version=$Version -p:AssemblyVersion="$Version.0" -p:FileVersion="$Version.0" `
    -p:ContinuousIntegrationBuild=true -p:DebugSymbols=false -p:DebugType=None `
    "-p:WorkerPayloadPath=$workerPayloadPath" -o $stagingRoot
if ($LASTEXITCODE -ne 0) { throw 'AV Workstation Toolkit launcher publish failed.' }

$launcherPath = Join-Path $stagingRoot 'AVWorkstationToolkit.exe'
if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw 'Published AVWorkstationToolkit.exe was not produced.'
}

$unexpectedPublishFiles = @(Get-ChildItem -LiteralPath $stagingRoot -Recurse -File |
    Where-Object FullName -ne $launcherPath)
if ($unexpectedPublishFiles.Count -gt 0) {
    throw ('Single-file publish produced unexpected files: {0}' -f ($unexpectedPublishFiles.Name -join ', '))
}

if ($null -ne $certificate) {
    Invoke-AVWorkstationToolkitArtifactSigning -Path $launcherPath -Thumbprint $normalizedThumbprint -Store $certificateStoreName -ToolPath $resolvedSignToolPath
}

$standalonePath = Join-Path $releaseRoot ("AV-Workstation-Toolkit-{0}-win-x64.exe" -f $Version)
Copy-Item -LiteralPath $launcherPath -Destination $standalonePath

$installerProject = Join-Path $repositoryRoot 'installer\AVWorkstationToolkit.Installer.wixproj'
$installerArguments = @(
    'build',$installerProject,'-c','Release','--nologo',
    "-p:ProductVersion=$Version",
    "-p:PayloadDir=$stagingRoot",
    "-p:OutputPath=$releaseRoot",
    "-p:IntermediateOutputPath=$intermediateRoot/"
)
& $dotnetPath @installerArguments
if ($LASTEXITCODE -ne 0) { throw 'AV Workstation Toolkit MSI build failed.' }

$msiPath = Join-Path $releaseRoot ("AV-Workstation-Toolkit-{0}-x64.msi" -f $Version)
if (-not (Test-Path -LiteralPath $msiPath -PathType Leaf)) {
    throw "Expected MSI was not produced: $msiPath"
}
$wixSymbolsPath = [IO.Path]::ChangeExtension($msiPath,'wixpdb')
if (Test-Path -LiteralPath $wixSymbolsPath -PathType Leaf) {
    Remove-Item -LiteralPath $wixSymbolsPath -Force
}
if ($null -ne $certificate) {
    Invoke-AVWorkstationToolkitArtifactSigning -Path $msiPath -Thumbprint $normalizedThumbprint -Store $certificateStoreName -ToolPath $resolvedSignToolPath
}

$portablePath = Join-Path $releaseRoot ("AV-Workstation-Toolkit-{0}-win-x64.zip" -f $Version)
Compress-Archive -LiteralPath $launcherPath -DestinationPath $portablePath -CompressionLevel Optimal

$thirdPartyNoticesPath = Join-Path $releaseRoot ("AV-Workstation-Toolkit-{0}-THIRD-PARTY-NOTICES.md" -f $Version)
Copy-Item -LiteralPath $thirdPartyNoticesSourcePath -Destination $thirdPartyNoticesPath
$projectLicensePath = Join-Path $releaseRoot ("AV-Workstation-Toolkit-{0}-LICENSE.txt" -f $Version)
Copy-Item -LiteralPath $projectLicenseSourcePath -Destination $projectLicensePath

$artifactFiles = [System.Collections.Generic.List[string]]::new()
foreach ($path in @($standalonePath,$msiPath,$portablePath,$projectLicensePath,$thirdPartyNoticesPath)) { $artifactFiles.Add($path) }
$bundledExternalPackages = @()
if ($BuildOfflineBundle) {
    Import-Module (Join-Path $repositoryRoot 'scripts\AVWorkstationToolkit.Core.psd1') -Force -ErrorAction Stop
    $bundledExternalPackages = @(Get-AVWorkstationToolkitCatalog | Where-Object { $_.Provider -eq 'External' -and $_.DeliveryMode -eq 'Bundled' })
    if ($bundledExternalPackages.Count -eq 0) {
        throw 'BuildOfflineBundle was requested, but the external catalog contains no bundled packages.'
    }
    $externalPackageRootFull = [IO.Path]::GetFullPath($ExternalPackageRoot)
    if (-not (Test-Path -LiteralPath $externalPackageRootFull -PathType Container)) {
        throw "External package depot was not found: $externalPackageRootFull"
    }

    Copy-Item -LiteralPath $launcherPath -Destination (Join-Path $offlineStagingRoot 'AVWorkstationToolkit.exe')
    $bundleRecords = [System.Collections.Generic.List[object]]::new()
    foreach ($package in $bundledExternalPackages) {
        $payload = Resolve-AVWorkstationToolkitExternalPayload -Package $package -DistributionRoot $externalPackageRootFull
        if (-not $payload.Valid) { throw "Bundled payload validation failed for $($package.Id): $($payload.Detail)" }
        $relative = ([string]$package.PayloadRelativePath).Replace('/',[IO.Path]::DirectorySeparatorChar)
        $destination = [IO.Path]::GetFullPath((Join-Path (Join-Path $offlineStagingRoot 'packages') $relative))
        $offlinePrefix = $offlineStagingRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $destination.StartsWith($offlinePrefix,[StringComparison]::OrdinalIgnoreCase)) {
            throw "Offline bundle destination escaped staging for $($package.Id)."
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $payload.Path -Destination $destination
        $bundleRecords.Add([ordered]@{
            Id = $package.Id
            Version = $package.KnownVersion
            RelativePath = 'packages/' + $package.PayloadRelativePath
            Sha256 = $package.PayloadSha256
            PublisherSubject = $package.PayloadPublisher
        }) | Out-Null
    }
    $bundleIndex = [ordered]@{
        SchemaVersion = 1
        Product = 'AV Workstation Toolkit Offline Package Bundle'
        AVWorkstationToolkitVersion = $Version
        GeneratedAt = (Get-Date).ToUniversalTime().ToString('o')
        Packages = @($bundleRecords)
    }
    $bundleIndex | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $offlineStagingRoot 'AVWorkstationToolkit-offline-bundle.json') -Encoding UTF8
    $offlineBundlePath = Join-Path $releaseRoot ("AV-Workstation-Toolkit-{0}-offline-bundle.zip" -f $Version)
    Compress-Archive -Path (Join-Path $offlineStagingRoot '*') -DestinationPath $offlineBundlePath -CompressionLevel Optimal
    $artifactFiles.Add($offlineBundlePath)
}

$launcherSha256 = (Get-FileHash -LiteralPath $standalonePath -Algorithm SHA256).Hash
$workerPayloadSha256 = (Get-FileHash -LiteralPath $workerPayloadPath -Algorithm SHA256).Hash
$sbomPath = Join-Path $releaseRoot ("AV-Workstation-Toolkit-{0}-sbom.cdx.json" -f $Version)
& (Join-Path $repositoryRoot 'build\New-ReleaseSbom.ps1') -Version $Version -CommitSha $commitSha -LauncherSha256 $launcherSha256 -WorkerSha256 $workerPayloadSha256 -OutputPath $sbomPath
if (-not (Test-Path -LiteralPath $sbomPath -PathType Leaf)) { throw 'CycloneDX SBOM generation did not produce the expected file.' }
$artifactFiles.Add($sbomPath)

$releaseManifestPath = Join-Path $releaseRoot ("AV-Workstation-Toolkit-{0}-release.json" -f $Version)
$checksumPath = Join-Path $releaseRoot ("AV-Workstation-Toolkit-{0}-SHA256SUMS.txt" -f $Version)
$launcherSignature = Get-AVWorkstationToolkitSignatureMetadata -Path $standalonePath
$sbomHash = (Get-FileHash -LiteralPath $sbomPath -Algorithm SHA256).Hash
$lockDocument = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\packages.lock.json') -Raw | ConvertFrom-Json
$nugetPackageCount = @($lockDocument.dependencies.'net10.0-windows7.0'.PSObject.Properties | Where-Object { $_.Value.PSObject.Properties.Name -contains 'resolved' }).Count
$artifactEntries = @($artifactFiles | ForEach-Object {
    $item = Get-Item -LiteralPath $_
    $signatureMetadata = if ($item.Extension -in @('.exe','.msi')) {
        Get-AVWorkstationToolkitSignatureMetadata -Path $item.FullName
    }
    else {
        [ordered]@{ Status='NotApplicable'; SignerSubject=''; SignerThumbprint=''; TimestampStatus='NotApplicable'; TimestampSignerSubject='' }
    }
    [ordered]@{
        Name = $item.Name
        Size = $item.Length
        Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        SignatureStatus = $signatureMetadata.Status
        SignerSubject = $signatureMetadata.SignerSubject
        TimestampStatus = $signatureMetadata.TimestampStatus
    }
})
$releaseManifest = [ordered]@{
    SchemaVersion = 3
    Product = 'AV Workstation Toolkit'
    ProjectLicense = 'Apache-2.0'
    Version = $Version
    Platform = 'win-x64'
    Architecture = 'x64'
    TargetFramework = 'net10.0-windows'
    TargetRuntime = 'Microsoft.NETCore.App.Runtime.win-x64/10.0.11'
    TargetHost = 'Microsoft.NETCore.App.Host.win-x64/10.0.11'
    SelectedSdk = $dotnetVersionText
    Distribution = 'standalone-executable'
    GeneratedAt = $buildTimestamp
    BuildTimestamp = $buildTimestamp
    BuildMode = 'Release'
    BuildChannel = $BuildChannel
    CommitSha = $commitSha.ToLowerInvariant()
    SourceDirty = $sourceDirty
    Signed = ($null -ne $certificate)
    SignaturePolicy = $(if ($RequireSignature -or $BuildChannel -eq 'Production') { 'Required' } else { 'Optional' })
    EmbeddedPayloadFiles = $embeddedPayloadFiles.Count
    BundledExternalPackages = @($bundledExternalPackages | ForEach-Object { $_.Id })
    NuGetAudit = [ordered]@{
        Status = 'Passed'
        VulnerablePackages = 0
        LockedPackages = $nugetPackageCount
    }
    Sbom = [ordered]@{
        Name = (Split-Path -Leaf $sbomPath)
        Format = 'CycloneDX'
        SpecVersion = '1.6'
        Sha256 = $sbomHash
    }
    Checksums = [ordered]@{
        Name = (Split-Path -Leaf $checksumPath)
        Algorithm = 'SHA-256'
        CoveredFiles = @($artifactFiles | ForEach-Object { Split-Path -Leaf $_ }) + @(Split-Path -Leaf $releaseManifestPath)
        ExcludedFiles = @((Split-Path -Leaf $checksumPath))
        ExclusionReason = 'The checksum list excludes only itself to avoid a circular digest.'
    }
    Launcher = [ordered]@{
        Name = (Split-Path -Leaf $standalonePath)
        TargetFramework = 'net10.0-windows'
        RuntimeFrameworkVersion = '10.0.11'
        Size = (Get-Item -LiteralPath $standalonePath).Length
        FileVersion = (Get-Item -LiteralPath $standalonePath).VersionInfo.FileVersion
        ProductVersion = (Get-Item -LiteralPath $standalonePath).VersionInfo.ProductVersion
        Sha256 = $launcherSha256
        SignatureStatus = $launcherSignature.Status
        SignerSubject = $launcherSignature.SignerSubject
        SignerThumbprint = $launcherSignature.SignerThumbprint
        TimestampStatus = $launcherSignature.TimestampStatus
        TimestampSignerSubject = $launcherSignature.TimestampSignerSubject
    }
    CompiledRuntime = [ordered]@{
        Primary = 'Compiled C# WPF'
        WorkerName = 'AVWorkstationToolkit.Worker.exe'
        WorkerSha256 = $workerPayloadSha256
        WorkerSignatureStatus = (Get-AVWorkstationToolkitSignatureMetadata -Path $workerPayloadPath).Status
        LegacyFallback = 'Explicit --legacy-powershell-recovery only'
    }
    Artifacts = $artifactEntries
}
$releaseManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $releaseManifestPath -Encoding UTF8

$checksumCoveredFiles = @($artifactFiles) + @($releaseManifestPath)
$checksumLines = @($checksumCoveredFiles | ForEach-Object {
    $item = Get-Item -LiteralPath $_
    '{0} *{1}' -f (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash,$item.Name
})
$checksumLines | Set-Content -LiteralPath $checksumPath -Encoding ASCII

if ($ScanWithDefender) {
    & (Join-Path $repositoryRoot 'tests\Test-EndpointTrust.ps1') -ReleaseRoot $releaseRoot -ScanWithDefender -RequireDefender:$RequireDefender
}

Get-ChildItem -LiteralPath $releaseRoot -File | Sort-Object Name | Select-Object Name,Length,LastWriteTime
