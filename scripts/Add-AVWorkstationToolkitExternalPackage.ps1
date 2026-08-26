<#
.SYNOPSIS
    Adds a redistributable installer to AV Workstation Toolkit's verified offline package catalog.

.DESCRIPTION
    Copies one installer into the local, git-ignored external package depot and
    pins its SHA-256 hash plus optional Authenticode publisher in the embedded
    external application catalog. AV Workstation Toolkit only exposes the verified file to the
    recipient; it never silently executes an external installer.

    Do not use this command unless your organization has redistribution rights
    for the supplied software. Vendor-managed software should use a
    Delivery.Mode of VendorPage instead.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Id,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$RegistryDisplayNamePattern,
    [Parameter(Mandatory)][string]$ReleaseVersionPattern,
    [Parameter(Mandatory)][uri]$ReleaseUri,
    [Parameter(Mandatory)][string]$Note,
    [Parameter(Mandatory)][string]$Vendor,
    [Parameter(Mandatory)]
    [ValidateSet('ControlSystem','DSPAudio','AVoIP','AudioNetworking','WirelessRF','AudioMeasurement','LoudspeakerPrediction','AmplifierManagement','Conferencing','CameraPTZ','DisplayProjector','DigitalSignage','DvLEDVideoWall','Intercom','MediaServerShowControl','BroadcastVideo','LightingControl','FieldUtility','NetworkUtility','SerialUtility','UsbDiagnostic','EDIDHDCP','FirmwareUtility','Development','Driver','Service','Server','WebApplication','EmbeddedSoftware','LegacySupport')]
    [string[]]$ApplicationType,
    [string]$ProductFamily = '',
    [ValidateSet('P1','P2','UTILITY','DEV')][string]$Priority = 'P2',
    [ValidateSet('AVEngineer','FieldService','ControlProgramming','DSPEngineering','NetworkEngineering','RFCoordination','Commissioning','DesignEngineering','BroadcastVideo','DigitalSignage','LightingProgramming','SystemAdministration','Development')]
    [string[]]$Role = @(),
    [ValidateSet('Latest','SameMajorMinor','ProjectPinned','Unknown')][string]$CatalogVersionRule = 'Unknown',
    [ValidateSet('Current','Legacy','Transition','Unknown')][string]$CurrentOrLegacy = 'Unknown',
    [ValidateSet('FREE','FREEMIUM','PAID','LICENSE','SUBSCRIPTION','HARDWARE-LICENSE','DEALER-LICENSE','UNKNOWN-COST')]
    [string[]]$LicensingModel = @('UNKNOWN-COST'),
    [ValidateSet('PUBLIC-DL','PUBLIC-PAGE','EMAIL-FORM','ACCOUNT','REGISTERED','DEALER','TRAINING','PORTAL','CONTACT','LICENSE-PORTAL','LEGACY-ARCHIVE','NO-DL','UNKNOWN-ACCESS')]
    [string[]]$DownloadAccess = @('UNKNOWN-ACCESS'),
    [ValidateSet('EASY','MODERATE','RESTRICTED','HARD')][string]$DownloadDifficulty = 'HARD',
    [ValidateSet('x86','x64','Arm64','Unknown')][string[]]$Architecture = @('Unknown'),
    [ValidateSet('Windows','macOS','Linux','Unknown')][string[]]$SupportedOS = @('Windows'),
    [ValidateSet('Yes','No','Unknown')][string]$SideBySideSupported = 'Unknown',
    [uri]$OfficialProductUri,
    [switch]$FirmwareUtility,
    [Alias('Profile')]
    [ValidateSet('Standard','Field','Developer','Optional')][string]$PackageProfile = 'Optional',
    [ValidateSet('None','Driver','Service','Listener')][string]$Risk = 'None',
    [string]$Channel = 'Stable',
    [ValidateSet('AtLeast','SameMajorMinor')][string]$VersionPolicy = 'AtLeast',
    [string]$RegistryVersionPattern = '(?<Version>\d+\.\d+(?:\.\d+){0,2})',
    [string]$PublisherSubject,
    [string]$ManifestPath,
    [string]$PackageDepotRoot,
    [switch]$RedistributionAuthorized,
    [switch]$AllowUnsignedPayload,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $repositoryRoot 'manifests\external-applications.json'
}
if ([string]::IsNullOrWhiteSpace($PackageDepotRoot)) {
    $PackageDepotRoot = Join-Path $repositoryRoot 'external-packages'
}

if (-not $RedistributionAuthorized) {
    throw 'RedistributionAuthorized is required. Confirm that you have the right to give this installer to recipients.'
}
if ($Id -notmatch '^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$') { throw "Invalid package ID: $Id" }
if ($Version -notmatch '^\d+(?:\.\d+){1,3}$') { throw "Version must contain two to four numeric fields: $Version" }
foreach ($field in @($Name,$Note,$Channel)) {
    if ([string]::IsNullOrWhiteSpace($field) -or $field -ne $field.Trim() -or $field -match '[\x00-\x1F\x7F]') {
        throw 'Name, note, and channel must be non-empty trimmed text without control characters.'
    }
}
if ([string]::IsNullOrWhiteSpace($Vendor) -or $Vendor -ne $Vendor.Trim() -or $Vendor -match '[\x00-\x1F\x7F]') {
    throw 'Vendor must be non-empty trimmed text without control characters.'
}
if ($ReleaseUri.Scheme -ne [Uri]::UriSchemeHttps -or -not [string]::IsNullOrEmpty($ReleaseUri.UserInfo)) {
    throw 'ReleaseUri must use HTTPS and cannot contain credentials.'
}
if ($null -eq $OfficialProductUri) { $OfficialProductUri = $ReleaseUri }
if ($OfficialProductUri.Scheme -ne [Uri]::UriSchemeHttps -or -not [string]::IsNullOrEmpty($OfficialProductUri.UserInfo)) {
    throw 'OfficialProductUri must use HTTPS and cannot contain credentials.'
}

$installerFullPath = [IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $installerFullPath -PathType Leaf)) { throw "Installer was not found: $installerFullPath" }
$allowedExtensions = @('.exe','.msi','.msix','.msixbundle','.zip')
$extension = [IO.Path]::GetExtension($installerFullPath).ToLowerInvariant()
if ($extension -notin $allowedExtensions) { throw "Unsupported installer extension '$extension'." }

Import-Module (Join-Path $PSScriptRoot 'AVWorkstationToolkit.Core.psd1') -Force -ErrorAction Stop
[void](Compare-AVWorkstationToolkitVersion -Left $Version -Right $Version)

$signatureSubject = ''
if ($extension -in @('.exe','.msi','.msix','.msixbundle')) {
    $signature = Get-AuthenticodeSignature -LiteralPath $installerFullPath -ErrorAction Stop
    if ($signature.Status -eq 'Valid' -and $null -ne $signature.SignerCertificate) {
        $signatureSubject = [string]$signature.SignerCertificate.Subject
    }
    elseif (-not $AllowUnsignedPayload) {
        throw "Installer Authenticode validation failed ($($signature.Status)). Use AllowUnsignedPayload only after an independent trust review."
    }
}
elseif (-not $AllowUnsignedPayload) {
    throw 'Archive payloads are not Authenticode signed. Use AllowUnsignedPayload only after an independent trust review.'
}
if (-not [string]::IsNullOrWhiteSpace($PublisherSubject)) {
    if ([string]::IsNullOrWhiteSpace($signatureSubject) -or -not $signatureSubject.Equals($PublisherSubject,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'The installer signer does not match PublisherSubject.'
    }
    $signatureSubject = $PublisherSubject
}

$manifestFullPath = [IO.Path]::GetFullPath($ManifestPath)
if (-not (Test-Path -LiteralPath $manifestFullPath -PathType Leaf)) { throw "External catalog was not found: $manifestFullPath" }
$manifestText = Get-Content -LiteralPath $manifestFullPath -Raw
[void](ConvertFrom-AVWorkstationToolkitExternalCatalogJson -Json $manifestText)
$manifest = $manifestText | ConvertFrom-Json
$existing = @($manifest.Packages | Where-Object { [string]$_.Id -ieq $Id })
if ($existing.Count -gt 0 -and -not $Force) { throw "External package ID already exists: $Id. Use Force to replace it." }

$fileName = [IO.Path]::GetFileName($installerFullPath)
$relativePath = '{0}/{1}/{2}' -f $Id,$Version,$fileName
$depotRoot = [IO.Path]::GetFullPath($PackageDepotRoot)
$payloadRoot = [IO.Path]::GetFullPath((Join-Path $depotRoot 'packages'))
$destination = [IO.Path]::GetFullPath((Join-Path $payloadRoot $relativePath.Replace('/',[IO.Path]::DirectorySeparatorChar)))
$payloadPrefix = $payloadRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $destination.StartsWith($payloadPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Resolved package destination escaped the depot.' }

$hash = (Get-FileHash -LiteralPath $installerFullPath -Algorithm SHA256).Hash
$entry = [ordered]@{
    Profile = $PackageProfile
    Name = $Name
    Id = $Id
    Risk = $Risk
    Note = $Note
    Deployment = 'ManualHold'
    Maintenance = 'Hold'
    KnownVersion = $Version
    Detection = [ordered]@{
        RegistryDisplayNamePattern = $RegistryDisplayNamePattern
        RegistryVersionPattern = $RegistryVersionPattern
        VersionPolicy = $VersionPolicy
    }
    Release = [ordered]@{
        Mode = 'VendorPage'
        Uri = $ReleaseUri.AbsoluteUri
        VersionPattern = $ReleaseVersionPattern
        Channel = $Channel
    }
    Delivery = [ordered]@{
        Mode = 'Bundled'
        Uri = $ReleaseUri.AbsoluteUri
        RelativePath = $relativePath
        Sha256 = $hash
        PublisherSubject = $signatureSubject
    }
    Metadata = [ordered]@{
        Vendor = $Vendor
        ProductFamily = $ProductFamily
        ApplicationType = @($ApplicationType)
        Priority = $Priority
        Roles = @($Role)
        DeploymentClass = 'ManualHandoff'
        MaintenancePolicy = 'Manual'
        VersionRule = $CatalogVersionRule
        CurrentOrLegacy = $CurrentOrLegacy
        LicensingModel = @($LicensingModel)
        DownloadAccess = @($DownloadAccess)
        DownloadDifficulty = $DownloadDifficulty
        Architecture = @($Architecture)
        SupportedOS = @($SupportedOS)
        SideBySideSupported = $SideBySideSupported
        OfficialDownloadUri = $ReleaseUri.AbsoluteUri
        OfficialProductUri = $OfficialProductUri.AbsoluteUri
        ValidationMethod = @('Registry','OfficialVersionPage')
    }
}
if ($Risk -ne 'None' -or $FirmwareUtility) {
    $entry.Metadata['SystemImpact'] = [ordered]@{}
    if ($Risk -eq 'Driver') { $entry.Metadata.SystemImpact['InstallsDriver'] = $true }
    if ($Risk -eq 'Service') { $entry.Metadata.SystemImpact['InstallsService'] = $true }
    if ($Risk -eq 'Listener') { $entry.Metadata.SystemImpact['OpensListener'] = $true }
    if ($FirmwareUtility) { $entry.Metadata.SystemImpact['FirmwareUtility'] = $true }
}

$remaining = @($manifest.Packages | Where-Object { [string]$_.Id -ine $Id })
$updated = [ordered]@{ SchemaVersion=[int]$manifest.SchemaVersion; Packages=@($remaining) + @($entry) }
$updatedJson = $updated | ConvertTo-Json -Depth 8
[void](ConvertFrom-AVWorkstationToolkitExternalCatalogJson -Json $updatedJson)

New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
Copy-Item -LiteralPath $installerFullPath -Destination $destination -Force
$updatedJson | Set-Content -LiteralPath $manifestFullPath -Encoding UTF8

[pscustomobject]@{
    Id = $Id
    Version = $Version
    ManifestPath = $manifestFullPath
    PayloadPath = $destination
    Sha256 = $hash
    PublisherSubject = $signatureSubject
    RedistributionAuthorized = $true
}
