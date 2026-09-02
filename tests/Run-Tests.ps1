<#
.SYNOPSIS
    Runs the non-installing QA suite for AVWorkstationToolkit.

.DESCRIPTION
    Uses fixture winget output for behavioral tests, parses all PowerShell and
    XAML source, loads the WPF window, and verifies that the worker rejects a
    request outside its logs boundary. No install, update, or uninstall command
    is executed.
#>

[CmdletBinding()]
param([switch]$CoreOnly)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$scriptsRoot = Join-Path $repositoryRoot 'scripts'
$modulePath = Join-Path $scriptsRoot 'AVWorkstationToolkit.Core.psd1'
$moduleImplementationPath = Join-Path $scriptsRoot 'AVWorkstationToolkit.Core.psm1'
$catalogPath = Join-Path $scriptsRoot 'AppProfiles.psd1'
$xamlPath = Join-Path $repositoryRoot 'app\AVWorkstationToolkit.xaml'
$fixtureRoot = Join-Path $PSScriptRoot 'fixtures'
$externalManifestPath = Join-Path $repositoryRoot 'manifests\external-applications.json'
$awarenessManifestPath = Join-Path $repositoryRoot 'manifests\commercial-av-catalog.json'
$managedManifestPath = Join-Path $repositoryRoot 'manifests\managed-applications.json'
$processPolicyPath = Join-Path $repositoryRoot 'manifests\process-launch-policy.json'

$launchIsElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($launchIsElevated -and -not $CoreOnly) { throw 'Full AVWorkstationToolkit QA must run as a standard user. Use -CoreOnly only in an isolated CI runner.' }

Import-Module $modulePath -Force

$script:Passed = 0
$script:Failed = 0
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Assert-True {
    param([bool]$Condition,[string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param($Expected,$Actual,[string]$Message)
    if ($Expected -ne $Actual) {
        throw ("{0} Expected: [{1}] Actual: [{2}]" -f $Message,$Expected,$Actual)
    }
}

function Assert-Contains {
    param([object[]]$Collection,$Expected,[string]$Message)
    if ($Expected -notin @($Collection)) { throw ("{0} Missing: [{1}]" -f $Message,$Expected) }
}

function Assert-NotContains {
    param([object[]]$Collection,$Unexpected,[string]$Message)
    if ($Unexpected -in @($Collection)) { throw ("{0} Unexpected: [{1}]" -f $Message,$Unexpected) }
}

function Assert-Throws {
    param([scriptblock]$Operation,[string]$Pattern,[string]$Message)
    $thrown = $false
    $actualMessage = ''
    try { & $Operation | Out-Null }
    catch { $thrown = $true; $actualMessage = $_.Exception.Message }
    if (-not $thrown) { throw ("{0} Expected an exception." -f $Message) }
    if (-not [string]::IsNullOrWhiteSpace($Pattern) -and $actualMessage -notmatch $Pattern) {
        throw ("{0} Exception did not match [{1}]: {2}" -f $Message,$Pattern,$actualMessage)
    }
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

$winGetManifest = Microsoft.PowerShell.Utility\Import-PowerShellDataFile -LiteralPath $catalogPath
$managedCatalogDocument = Get-Content -LiteralPath $managedManifestPath -Raw | ConvertFrom-Json
$operationalExternalCatalog = @(ConvertFrom-AVWorkstationToolkitExternalCatalogJson -Json (Get-Content -LiteralPath $externalManifestPath -Raw))
$awarenessExternalCatalog = @(ConvertFrom-AVWorkstationToolkitExternalCatalogJson -Json (Get-Content -LiteralPath $awarenessManifestPath -Raw))
$expectedWinGetCount = @($winGetManifest.Packages).Count
$expectedOperationalExternalCount = $operationalExternalCatalog.Count
$expectedAwarenessCount = $awarenessExternalCatalog.Count
$expectedCatalogCount = $expectedWinGetCount + $expectedOperationalExternalCount + $expectedAwarenessCount
$catalog = @(Get-AVWorkstationToolkitCatalog -CatalogPath $catalogPath)
$installedText = Get-Content -LiteralPath (Join-Path $fixtureRoot 'winget-installed.txt') -Raw
$upgradeText = Get-Content -LiteralPath (Join-Path $fixtureRoot 'winget-upgrades.txt') -Raw
$exportJson = Get-Content -LiteralPath (Join-Path $fixtureRoot 'winget-export.json') -Raw
$structuredPackages = @(ConvertFrom-AVWorkstationToolkitWingetExportJson -Json $exportJson)
$clearReboot = [pscustomobject]@{ Pending=$false; Reasons=@(); Summary='No pending reboot signals' }
$externalCatalog = @($catalog | Where-Object Provider -eq 'External')
$externalInventoryAbsent = @($externalCatalog | ForEach-Object {
    [pscustomobject]@{ Id=$_.Id; Reliable=$true; Installed=$false; InstalledVersion=''; InstalledVersions=@(); Detail='Fixture reports not installed.' }
})
$externalReleaseBaseline = @($externalCatalog | ForEach-Object {
    [pscustomobject]@{ Id=$_.Id; AvailableVersion=$_.KnownVersion; ObservedVersion=$_.KnownVersion; OnlineChecked=$true; OnlineAvailable=$true; ReleaseUri=$_.ReleaseUri; DownloadUri=''; Detail='Fixture vendor version.' }
})
$plan = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -InstalledText $installedText -UpgradeText $upgradeText -ExternalInventory $externalInventoryAbsent -ExternalReleaseInfo $externalReleaseBaseline -RebootState $clearReboot -WingetVersion 'v-test'
$structuredPlan = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -InstalledPackages $structuredPackages -UpgradeText $upgradeText -ExternalInventory $externalInventoryAbsent -ExternalReleaseInfo $externalReleaseBaseline -RebootState $clearReboot -WingetVersion 'v-test'

Invoke-Check 'Catalog aggregates WinGet, operational providers, and commercial AV awareness records' {
    Assert-Equal $expectedCatalogCount $catalog.Count 'Catalog size differs from its source manifests.'
    Assert-Equal $expectedWinGetCount @($catalog | Where-Object Provider -eq 'WinGet').Count 'WinGet catalog size differs.'
    Assert-Equal ($expectedOperationalExternalCount + $expectedAwarenessCount) $externalCatalog.Count 'External catalog size differs.'
    Assert-True ($catalog.Count -ge 300) 'Commercial AV catalog breadth regressed below the reviewed baseline.'
}
Invoke-Check 'Shipping managed JSON catalog matches the retired PowerShell catalog fixture' {
    Assert-Equal 1 ([int]$managedCatalogDocument.SchemaVersion) 'Managed JSON catalog schema differs.'
    Assert-Equal ([string]$winGetManifest.ForbiddenPattern) ([string]$managedCatalogDocument.ForbiddenPattern) 'Managed catalog forbidden-product policy differs.'
    Assert-Equal $expectedWinGetCount @($managedCatalogDocument.Packages).Count 'Managed JSON catalog count differs from the characterized PowerShell fixture.'
    foreach ($legacy in @($winGetManifest.Packages)) {
        $current = @($managedCatalogDocument.Packages | Where-Object Id -eq ([string]$legacy.Id))
        Assert-Equal 1 $current.Count "Managed JSON catalog lost or duplicated $($legacy.Id)."
        foreach ($field in @('Profile','Name','Id','Vendor','Risk','Note','Deployment','Maintenance')) {
            $legacyValue = if ($legacy.ContainsKey($field)) { [string]$legacy[$field] } else { '' }
            $currentValue = if ($current[0].PSObject.Properties.Name -contains $field) { [string]$current[0].$field } else { '' }
            Assert-Equal $legacyValue $currentValue "Managed JSON catalog field differs for $($legacy.Id).$field"
        }
    }
}
Invoke-Check 'Vendor catalog sources compile deterministically to the only runtime artifact' {
    $compilerPath = Join-Path $repositoryRoot 'build\Compile-CommercialCatalog.ps1'
    $compilerOutput = (& $compilerPath -Check | Out-String)
    Assert-True ($compilerOutput -match 'CATALOG_OK vendors=113 packages=281') 'Catalog compiler did not validate the expected source set.'
    $vendorSources = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'catalog\vendors') -File -Filter '*.json')
    Assert-Equal 113 $vendorSources.Count 'Vendor source-file count differs.'
    Assert-Equal $expectedAwarenessCount @($vendorSources | ForEach-Object {
        @((Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).Packages)
    }).Count 'Vendor source package count differs from the compiled artifact.'
    $project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj') -Raw
    Assert-True ($project -match 'EmbeddedResource Include="\.\.\\\.\.\\manifests\\\*\.json"' -and $project -notmatch 'catalog\\vendors') 'Launcher runtime embedding depends on loose vendor sources.'
}
Invoke-Check 'Catalog package IDs are unique' {
    Assert-Equal $catalog.Count @($catalog.Id | Sort-Object -Unique).Count 'Duplicate IDs detected.'
}
Invoke-Check 'Catalog fields use approved profiles and risk classes' {
    foreach ($package in $catalog) {
        Assert-Contains @('Standard','Field','Developer','Optional') $package.Profile "Invalid profile for $($package.Id)."
        Assert-Contains @('None','Driver','Service','Listener') $package.Risk "Invalid risk for $($package.Id)."
        Assert-Contains @('Allowlisted','ManualHold') $package.Deployment "Invalid deployment policy for $($package.Id)."
        Assert-Contains @('Allowlisted','Hold') $package.Maintenance "Invalid maintenance policy for $($package.Id)."
        Assert-Contains @('WinGet','External') $package.Provider "Invalid provider for $($package.Id)."
        Assert-True (-not [string]::IsNullOrWhiteSpace($package.Name)) "Missing name for $($package.Id)."
        Assert-True (-not [string]::IsNullOrWhiteSpace($package.Vendor)) "Missing vendor for $($package.Id)."
        Assert-True (-not [string]::IsNullOrWhiteSpace($package.Note)) "Missing note for $($package.Id)."
    }
}
Invoke-Check 'Commercial AV metadata is normalized, bounded, and independently dimensional' {
    $types = @('ControlSystem','DSPAudio','AVoIP','AudioNetworking','WirelessRF','AudioMeasurement','LoudspeakerPrediction','AmplifierManagement','Conferencing','CameraPTZ','DisplayProjector','DigitalSignage','DvLEDVideoWall','Intercom','MediaServerShowControl','BroadcastVideo','LightingControl','FieldUtility','NetworkUtility','SerialUtility','UsbDiagnostic','EDIDHDCP','FirmwareUtility','Development','Driver','Service','Server','WebApplication','EmbeddedSoftware','LegacySupport')
    $licenses = @('FREE','FREEMIUM','PAID','LICENSE','SUBSCRIPTION','HARDWARE-LICENSE','DEALER-LICENSE','UNKNOWN-COST')
    $access = @('PUBLIC-DL','PUBLIC-PAGE','EMAIL-FORM','ACCOUNT','REGISTERED','DEALER','TRAINING','PORTAL','CONTACT','LICENSE-PORTAL','LEGACY-ARCHIVE','NO-DL','UNKNOWN-ACCESS')
    foreach ($item in $externalCatalog) {
        Assert-True (-not [string]::IsNullOrWhiteSpace($item.Vendor)) "External catalog vendor is missing: $($item.Id)"
        Assert-True (@($item.ApplicationType).Count -gt 0) "Application type is missing: $($item.Id)"
        foreach ($value in @($item.ApplicationType)) { Assert-Contains $types $value "Invalid application type on $($item.Id)." }
        foreach ($value in @($item.LicensingModel)) { Assert-Contains $licenses $value "Invalid licensing model on $($item.Id)." }
        foreach ($value in @($item.DownloadAccess)) { Assert-Contains $access $value "Invalid download access on $($item.Id)." }
        Assert-Contains @('P1','P2','UTILITY','DEV') $item.Priority "Invalid priority on $($item.Id)."
        Assert-Contains @('EASY','MODERATE','RESTRICTED','HARD') $item.DownloadDifficulty "Invalid download difficulty on $($item.Id)."
        Assert-Contains @('Current','Legacy','Transition','CompatibilityUnverified','Discontinued','Unknown') $item.CurrentOrLegacy "Invalid lifecycle on $($item.Id)."
        Assert-Contains @('Unknown','LinkOnly','VendorDownloadAllowed','Redistributable','PackageManagerOnly','ManualInstall','ReviewBeforeBundling') $item.DistributionPolicy "Invalid distribution policy on $($item.Id)."
        Assert-Contains @('Current','ReviewSoon','VerificationRequired','Quarantined') $item.MetadataVerificationState "Invalid metadata verification state on $($item.Id)."
        Assert-True ($item.OfficialProductUri -match '^https://') "Official product URI is missing or insecure: $($item.Id)"
    }
    $qsys = @($externalCatalog | Where-Object Id -eq 'QSC.QSYSDesigner.LTS')[0]
    Assert-Contains @($qsys.LicensingModel) 'FREE' 'Q-SYS licensing classification differs.'
    Assert-Contains @($qsys.DownloadAccess) 'PUBLIC-PAGE' 'Q-SYS access classification differs.'
    Assert-NotContains @($qsys.DownloadAccess) 'FREE' 'Licensing leaked into the access dimension.'
}
Invoke-Check 'Catalog verification metadata is deterministic and fail closed' {
    $asOf = [datetime]'2026-08-25'
    Assert-Equal 'Current' (Get-AVWorkstationToolkitMetadataVerificationState -VerifiedOn ($asOf.AddDays(-59).ToString('yyyy-MM-dd')) -AsOf $asOf) '59-day metadata should remain current.'
    Assert-Equal 'ReviewSoon' (Get-AVWorkstationToolkitMetadataVerificationState -VerifiedOn ($asOf.AddDays(-60).ToString('yyyy-MM-dd')) -AsOf $asOf) '60-day metadata should require review soon.'
    Assert-Equal 'ReviewSoon' (Get-AVWorkstationToolkitMetadataVerificationState -VerifiedOn ($asOf.AddDays(-180).ToString('yyyy-MM-dd')) -AsOf $asOf) '180-day metadata should require review soon.'
    Assert-Equal 'VerificationRequired' (Get-AVWorkstationToolkitMetadataVerificationState -VerifiedOn ($asOf.AddDays(-181).ToString('yyyy-MM-dd')) -AsOf $asOf) '181-day metadata should require verification.'
    Assert-Equal 'VerificationRequired' (Get-AVWorkstationToolkitMetadataVerificationState -VerifiedOn '' -AsOf $asOf) 'Missing verification evidence should fail closed.'
    Assert-Equal 'Quarantined' (Get-AVWorkstationToolkitMetadataVerificationState -VerifiedOn '' -AsOf $asOf -Quarantined $true) 'Explicit quarantine should take precedence.'
    Assert-Throws { Get-AVWorkstationToolkitMetadataVerificationState -VerifiedOn '08/25/2026' -AsOf $asOf } 'ISO date format' 'A locale-dependent verification date was accepted.'
    Assert-Throws { Get-AVWorkstationToolkitMetadataVerificationState -VerifiedOn '2026-08-26' -AsOf $asOf } 'future' 'A future verification date was accepted.'

    $ndiSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'catalog\vendors\ndi.json') -Raw | ConvertFrom-Json
    $invalid = $ndiSource.Packages[2]
    $invalid.Metadata.Verification.QuarantineReason = ''
    $invalidDocument = [ordered]@{ SchemaVersion=3; Packages=@($invalid) } | ConvertTo-Json -Depth 20
    Assert-Throws { ConvertFrom-AVWorkstationToolkitExternalCatalogJson -Json $invalidDocument } 'requires a reason' 'Quarantined metadata without a reason was accepted.'

    $domainInvalid = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'catalog\vendors\ndi.json') -Raw | ConvertFrom-Json).Packages[0]
    $domainInvalid.Metadata.Provenance.AuthoritativeDomain = 'example.com'
    $domainDocument = [ordered]@{ SchemaVersion=3; Packages=@($domainInvalid) } | ConvertTo-Json -Depth 20
    Assert-Throws { ConvertFrom-AVWorkstationToolkitExternalCatalogJson -Json $domainDocument } 'outside Metadata.Provenance.AuthoritativeDomain' 'A mismatched authoritative domain was accepted.'
}
Invoke-Check 'First-tranche workflow and provenance records stay non-executable' {
    $pktmon = @($externalCatalog | Where-Object Id -eq 'Microsoft.Pktmon')[0]
    Assert-Equal 'AwarenessOnly' $pktmon.DeploymentClass 'Pktmon became an installer target.'
    Assert-Equal 'Awareness' $pktmon.DeliveryMode 'Pktmon gained a delivery action.'
    Assert-Contains @($pktmon.InstallationForms) 'WindowsInbox' 'Pktmon is not modeled as an in-box capability.'
    Assert-Contains @($pktmon.WorkflowCategories) 'NetworkCaptureTiming' 'Pktmon capture workflow is missing.'
    Assert-Equal 'None' $pktmon.DownloadStrategy 'Pktmon invented a download strategy.'

    $analysis = @($externalCatalog | Where-Object Id -eq 'NDI.Analysis')[0]
    Assert-Equal 'AwarenessOnly' $analysis.DeploymentClass 'NDI Analysis became executable.'
    Assert-True ($analysis.Note -match 'not included') 'NDI Analysis is not explicitly modeled separately from NDI Tools.'
    Assert-Contains @($analysis.WorkflowCategories) 'AVoIP' 'NDI Analysis AVoIP workflow is missing.'

    $remote = @($externalCatalog | Where-Object Id -eq 'NDI.Remote')[0]
    Assert-Equal 'Discontinued' $remote.CurrentOrLegacy 'NDI Remote is not marked discontinued.'
    Assert-Equal 'Quarantined' $remote.MetadataVerificationState 'NDI Remote is not quarantined.'
    Assert-Contains @($remote.DownloadAccess) 'NO-DL' 'NDI Remote exposes a source despite discontinuation.'
    Assert-Equal 'None' $remote.DownloadStrategy 'NDI Remote exposes a download strategy.'

    foreach ($id in @('NagleCode.PacketSender','UweSieber.UsbTreeView','TeraTermProject.TeraTerm','Netgear.EngageController')) {
        $item = @($externalCatalog | Where-Object Id -eq $id)[0]
        Assert-True (@($item.WorkflowCategories).Count -gt 0) "Workflow metadata is missing: $id"
        Assert-True (@($item.InstallationForms).Count -gt 0) "Installation-form metadata is missing: $id"
        Assert-Equal '2026-08-25' $item.MetadataVerifiedOn "Reviewed metadata date differs: $id"
        Assert-Equal (Get-AVWorkstationToolkitMetadataVerificationState -VerifiedOn $item.MetadataVerifiedOn) $item.MetadataVerificationState "Reviewed metadata state does not follow the age policy: $id"
        Assert-Equal 'ManualHold' $item.Deployment "Reviewed record gained execution authority: $id"
    }
    Assert-Equal '5.6.2' @($externalCatalog | Where-Object Id -eq 'TeraTermProject.TeraTerm')[0].KnownVersion 'Tera Term reviewed version differs.'
    Assert-Equal '4.7.4' @($externalCatalog | Where-Object Id -eq 'UweSieber.UsbTreeView')[0].KnownVersion 'USB Device Tree Viewer reviewed version differs.'
}
Invoke-Check 'Commercial AV catalog covers every modeled engineering discipline and critical seed' {
    foreach ($type in @('ControlSystem','DSPAudio','AVoIP','AudioNetworking','WirelessRF','AudioMeasurement','LoudspeakerPrediction','AmplifierManagement','Conferencing','CameraPTZ','DisplayProjector','DigitalSignage','DvLEDVideoWall','Intercom','MediaServerShowControl','BroadcastVideo','LightingControl','FieldUtility','NetworkUtility','SerialUtility','UsbDiagnostic','EDIDHDCP','FirmwareUtility','Development','Driver','Service','Server','WebApplication','EmbeddedSoftware','LegacySupport')) {
        Assert-True (@($externalCatalog | Where-Object { $type -in @($_.ApplicationType) }).Count -gt 0) "Catalog discipline has no records: $type"
    }
    foreach ($id in @(
        'QSC.QSYSUCIViewer','Crestron.Toolbox','Extron.DSPConfiguratorPro','Biamp.WorkplaceTools',
        'Lightware.LDC','BrightSign.OS','Shure.Designer','Audinate.DanteVirtualSoundcard',
        'Sennheiser.ControlCockpit','AMX.NetLinxStudio4','Netgear.EngageController','Luminex.Araneo',
        'OpenSoundMeter.OpenSoundMeter','NDI.NDITools','NDI.Analysis','NDI.Remote','Microsoft.Pktmon','ETC.EosFamily','MALighting.grandMA3onPC',
        'Samsung.ColorExpertLED','LG.LEDAssistant','ZeeVee.ZyPerManagementPlatform',
        'Atlona.VelocityDeviceManager','Kramer.KConfig','Kramer.Network','Kramer.KRouterPlus',
        'Planar.WallDirectorOS','RossVideo.DashBoard','RossVideo.PlatformManager',
        'LEAProfessional.SharkWare','LEAProfessional.WebUI','LEAProfessional.Cloud'
    )) { Assert-Contains $catalog.Id $id "Critical seed record is missing: $id" }
    Assert-NotContains $catalog.Id 'Oracle.VirtualBox' 'VirtualBox entered the expanded catalog.'
    Assert-NotContains $catalog.Id 'LG.ColorExpertLED' 'Samsung Color Expert LED was misattributed to LG.'
    $colorExpert = @($externalCatalog | Where-Object Id -eq 'Samsung.ColorExpertLED')[0]
    Assert-Equal 'Samsung' $colorExpert.Vendor 'Color Expert LED vendor identity differs.'
    Assert-Contains @($colorExpert.DownloadAccess) 'CONTACT' 'Color Expert LED access policy differs.'
    $lgAssistant = @($externalCatalog | Where-Object Id -eq 'LG.LEDAssistant')[0]
    Assert-Contains @($lgAssistant.DownloadAccess) 'PUBLIC-DL' 'LG LED Assistant public access classification differs.'
}
Invoke-Check 'Open Sound Meter is a high-value public field utility without automated execution' {
    $item = @($externalCatalog | Where-Object Id -eq 'OpenSoundMeter.OpenSoundMeter')[0]
    Assert-Equal 'P1' $item.Priority 'Open Sound Meter priority differs.'
    Assert-Contains @($item.ApplicationType) 'AudioMeasurement' 'Open Sound Meter measurement classification is missing.'
    Assert-Contains @($item.ApplicationType) 'FieldUtility' 'Open Sound Meter field classification is missing.'
    Assert-Contains @($item.LicensingModel) 'FREE' 'Open Sound Meter licensing differs.'
    Assert-Contains @($item.DownloadAccess) 'PUBLIC-DL' 'Open Sound Meter access differs.'
    Assert-Equal 'ManualHold' $item.Deployment 'Open Sound Meter gained an automatic deployment path.'
    Assert-Equal 'VendorPage' $item.DeliveryMode 'Open Sound Meter must hand off to its official release page.'
}
Invoke-Check 'Milan Manager uses the current official channel and exposes driver impact' {
    $item = @($externalCatalog | Where-Object Id -eq 'Avnu.MilanManager')[0]
    Assert-Equal '2.5.3' $item.KnownVersion 'Milan Manager reviewed version differs.'
    Assert-Equal 'Milan Manager' $item.Vendor 'Milan Manager vendor identity differs.'
    Assert-Contains @($item.LicensingModel) 'FREE' 'Milan Manager licensing differs.'
    Assert-Contains @($item.DownloadAccess) 'PUBLIC-DL' 'Milan Manager access differs.'
    Assert-True ($item.InstallsDriver -eq $true) 'Milan Manager driver impact is missing.'
    Assert-Equal 'milanmanager.com' ([uri]$item.OfficialProductUri).Host 'Milan Manager official host differs.'
    $planItem = @($plan.Packages | Where-Object Id -eq $item.Id)[0]
    Assert-Equal 'Driver' $planItem.Risk 'Milan Manager effective plan risk differs.'
    Assert-True (-not $planItem.CanSelect) 'Milan Manager awareness record became selectable.'
}
Invoke-Check 'Catalog query API composes role, access, lifecycle, impact, and inventory filters' {
    $freePublic = @(Find-AVWorkstationToolkitCatalog -Catalog $catalog -Free -PublicWithoutAccount)
    Assert-True ($freePublic.Count -gt 25) 'Free public-download query returned too few reviewed records.'
    foreach ($item in $freePublic) {
        Assert-Contains @($item.LicensingModel) 'FREE' "Free query leaked a non-free record: $($item.Id)"
        Assert-Contains @($item.DownloadAccess) 'PUBLIC-DL' "Public-download query leaked a gated record: $($item.Id)"
        Assert-True ($item.RequiresVendorAccount -ne $true -and $item.RequiresDealerAccount -ne $true -and $item.RequiresTraining -ne $true) "Public-download query leaked an account-gated record: $($item.Id)"
    }
    Assert-True (@(Find-AVWorkstationToolkitCatalog -Catalog $catalog -RequiresDealerAccount).Count -gt 5) 'Dealer-access query returned too few records.'
    Assert-True (@(Find-AVWorkstationToolkitCatalog -Catalog $catalog -Licensed).Count -gt 25) 'Licensed-software query returned too few records.'
    Assert-True (@(Find-AVWorkstationToolkitCatalog -Catalog $catalog -InstallsDriver).Count -gt 5) 'Driver-impact query returned too few records.'
    Assert-True (@(Find-AVWorkstationToolkitCatalog -Catalog $catalog -InstallsService).Count -gt 10) 'Service-impact query returned too few records.'
    Assert-True (@(Find-AVWorkstationToolkitCatalog -Catalog $catalog -OpensListener).Count -gt 5) 'Listener-impact query returned too few records.'
    Assert-True (@(Find-AVWorkstationToolkitCatalog -Catalog $catalog -FirmwareUtility).Count -gt 25) 'Firmware-utility query returned too few records.'
    Assert-True (@(Find-AVWorkstationToolkitCatalog -Catalog $catalog -CurrentOrLegacy Legacy).Count -gt 15) 'Legacy query returned too few records.'
    Assert-True (@(Find-AVWorkstationToolkitCatalog -Catalog $catalog -ApplicationType DSPAudio -Role DSPEngineering).Count -gt 10) 'DSP role query returned too few records.'
    Assert-True (@(Find-AVWorkstationToolkitCatalog -Catalog $catalog -Vendor Crestron).Count -ge 10) 'Crestron vendor query returned too few records.'
    Assert-True (@(Find-AVWorkstationToolkitCatalog -Catalog $catalog -WorkflowCategory NetworkCaptureTiming -MetadataVerificationState @('Current','ReviewSoon','VerificationRequired')).Count -ge 3) 'Workflow plus verification-state query returned too few non-quarantined diagnostics.'
    Assert-True (@(Find-AVWorkstationToolkitCatalog -Catalog $catalog -InstallationForm WindowsInbox -DistributionPolicy LinkOnly).Id -contains 'Microsoft.Pktmon') 'Installation-form plus distribution-policy query omitted Pktmon.'
    $fieldOverlay = @(Find-AVWorkstationToolkitCatalog -Catalog $catalog -Role FieldService -Vendor @('Q-SYS','Crestron','Shure'))
    Assert-True ($fieldOverlay.Count -gt 5) 'Role plus manufacturer-overlay query returned too few records.'
    Assert-Equal 0 @($fieldOverlay | Where-Object { $_.Vendor -notin @('Q-SYS','Crestron','Shure') -or 'FieldService' -notin @($_.Roles) }).Count 'Role plus manufacturer-overlay query leaked unrelated records.'
    Assert-True (@(Find-AVWorkstationToolkitCatalog -Catalog $catalog -KnownButUnmanaged).Count -ge $expectedAwarenessCount) 'Known-but-unmanaged query omitted awareness records.'
    Assert-True (@(Find-AVWorkstationToolkitCatalog -Catalog $catalog -SourceUnavailable).Count -gt 5) 'Unavailable-source query returned too few records.'
}
Invoke-Check 'Desktop catalog filters compose manufacturer, discipline, policy, search, and profile criteria' {
    $vendors = @(Get-AVWorkstationToolkitCatalogVendors -Catalog $catalog)
    Assert-True ($vendors.Count -gt 100) 'Dynamic manufacturer list is unexpectedly small.'
    Assert-Equal $vendors.Count @($vendors | Sort-Object -Unique).Count 'Dynamic manufacturer list contains duplicates.'
    Assert-Equal (@($vendors | Sort-Object) -join '|') ($vendors -join '|') 'Dynamic manufacturer list is not sorted.'
    Assert-Contains $vendors 'Crestron' 'Dynamic manufacturer list omitted Crestron.'
    Assert-Contains $vendors 'Planar' 'Dynamic manufacturer list omitted a newly researched vendor.'

    $all = @($catalog | Where-Object { Test-AVWorkstationToolkitCatalogFilter -Item $_ })
    Assert-Equal $catalog.Count $all.Count 'Cleared catalog filters do not return the full catalog.'

    $crestron = @($catalog | Where-Object { Test-AVWorkstationToolkitCatalogFilter -Item $_ -Vendor Crestron })
    Assert-True ($crestron.Count -ge 10) 'Manufacturer filter returned too few Crestron records.'
    Assert-Equal 0 @($crestron | Where-Object Vendor -ne 'Crestron').Count 'Manufacturer filter leaked a different vendor.'

    $planar = @($catalog | Where-Object { Test-AVWorkstationToolkitCatalogFilter -Item $_ -Vendor Planar })
    Assert-True ($planar.Count -ge 1 -and $planar.Count -le 3) 'Small-manufacturer filter returned an implausible Planar set.'
    $leaDsp = @($catalog | Where-Object { Test-AVWorkstationToolkitCatalogFilter -Item $_ -Vendor 'LEA Professional' -Discipline DSP })
    Assert-True ($leaDsp.Count -gt 0) 'Manufacturer and discipline intersection returned no LEA DSP tools.'
    Assert-Equal 0 @($leaDsp | Where-Object { $_.Vendor -ne 'LEA Professional' -or @($_.ApplicationType | Where-Object { $_ -in @('DSPAudio','AmplifierManagement') }).Count -eq 0 }).Count 'Manufacturer and discipline intersection leaked a record.'

    $rossFree = @($catalog | Where-Object { Test-AVWorkstationToolkitCatalogFilter -Item $_ -Vendor 'Ross Video' -Preset Free })
    Assert-Contains $rossFree.Id 'RossVideo.DashBoard' 'Manufacturer and catalog-policy intersection omitted Ross DashBoard.'
    $kramerSearch = @($catalog | Where-Object { Test-AVWorkstationToolkitCatalogFilter -Item $_ -Vendor Kramer -Search router })
    Assert-Contains $kramerSearch.Id 'Kramer.KRouterPlus' 'Manufacturer and text-search intersection omitted K-Router Plus.'

    $fieldCrestron = @($catalog | Where-Object { Test-AVWorkstationToolkitCatalogFilter -Item $_ -Profiles Field -Vendor Crestron })
    Assert-True ($fieldCrestron.Count -gt 0) 'Manufacturer and profile intersection returned no records.'
    Assert-Equal 0 @($fieldCrestron | Where-Object Profile -ne 'Field').Count 'Manufacturer and profile intersection leaked a disabled profile.'
    Assert-Equal 0 @($catalog | Where-Object { Test-AVWorkstationToolkitCatalogFilter -Item $_ -Vendor Planar -Discipline RF }).Count 'Zero-result filter combination returned records.'

    Assert-Equal 'Crestron' (Resolve-AVWorkstationToolkitCatalogVendorSelection -Catalog $catalog -SelectedVendor 'crestron') 'Manufacturer selection was not retained canonically across refresh.'
    Assert-Equal 'All' (Resolve-AVWorkstationToolkitCatalogVendorSelection -Catalog $catalog -SelectedVendor 'Removed Vendor') 'Stale manufacturer selection did not fall back safely.'
}
Invoke-Check 'Catalog rejects unsupported policy values and fields' {
    $temporaryCatalog = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-catalog-{0}.psd1' -f [guid]::NewGuid().ToString('N'))
    try {
        @'
@{
    Packages = @(
        @{ Profile='Standard'; Name='Bad'; Id='Vendor.Bad'; Risk='None'; Note='Test'; Deployment='Anything'; Unexpected='value' }
    )
    ForbiddenPattern = '(?i)Forbidden'
}
'@ | Set-Content -LiteralPath $temporaryCatalog -Encoding ASCII
        Assert-Throws { Get-AVWorkstationToolkitCatalog -CatalogPath $temporaryCatalog } 'unsupported keys|Invalid deployment policy' 'Invalid catalog schema was accepted.'
    }
    finally { Remove-Item -LiteralPath $temporaryCatalog -Force -ErrorAction SilentlyContinue }
}
Invoke-Check 'Catalog actively rejects a forbidden product' {
    $temporaryCatalog = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-forbidden-{0}.psd1' -f [guid]::NewGuid().ToString('N'))
    try {
        @'
@{
    Packages = @(
        @{ Profile='Standard'; Name='TeamViewer Client'; Id='TeamViewer.TeamViewer'; Risk='None'; Note='Forbidden test fixture' }
    )
    ForbiddenPattern = '(?i)TeamViewer'
}
'@ | Set-Content -LiteralPath $temporaryCatalog -Encoding ASCII
        Assert-Throws { Get-AVWorkstationToolkitCatalog -CatalogPath $temporaryCatalog } 'Out-of-scope security/management package' 'Forbidden product was accepted.'
    }
    finally { Remove-Item -LiteralPath $temporaryCatalog -Force -ErrorAction SilentlyContinue }
}
Invoke-Check 'Catalog loading does not depend on implicit module autoloading' {
    $powershellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $command = 'Import-Module Microsoft.PowerShell.Management; $PSModuleAutoloadingPreference = ''None''; Import-Module ''' + $modulePath.Replace("'","''") + ''' -Force; @(Get-AVWorkstationToolkitCatalog).Count'
    $output = (& $powershellExe -NoProfile -ExecutionPolicy RemoteSigned -Command $command 2>&1 | Out-String).Trim()
    Assert-Equal 0 $LASTEXITCODE 'Fresh no-autoload catalog process failed.'
    Assert-Equal ([string]$expectedCatalogCount) $output 'Fresh no-autoload catalog count differs.'
}
Invoke-Check 'Module manifest is valid and versioned' {
    $manifest = Test-ModuleManifest -Path $modulePath
    Assert-Equal '1.1.1' ([string]$manifest.Version) 'Module version differs.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Get-AVWorkstationToolkitDataRoot' 'Data-root resolver is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Invoke-AVWorkstationToolkitLegacyDataMigration' 'Legacy data migration seam is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'New-AVWorkstationToolkitActionRequest' 'Request builder is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Open-AVWorkstationToolkitExplorerPath' 'Bounded Explorer handoff is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Open-AVWorkstationToolkitHttpsUri' 'Bounded HTTPS handoff is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Start-AVWorkstationToolkitWorker' 'Worker launcher is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Get-AVWorkstationToolkitWingetInventory' 'Structured inventory reader is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Get-AVWorkstationToolkitExternalInventory' 'External application inventory reader is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Get-AVWorkstationToolkitExternalReleaseInfo' 'External release checker is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Resolve-AVWorkstationToolkitExternalPayload' 'External payload verifier is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'ConvertFrom-AVWorkstationToolkitExternalDownloadContent' 'Direct-download parser is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Complete-AVWorkstationToolkitVendorDownload' 'Vendor-download finalizer is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Get-AVWorkstationToolkitAuthenticatedSftpCatalog' 'Authenticated-SFTP catalog reader is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Set-AVWorkstationToolkitTrustedSftpHost' 'SFTP host trust writer is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Find-AVWorkstationToolkitCatalog' 'Catalog query API is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Get-AVWorkstationToolkitCatalogVendors' 'Catalog manufacturer resolver is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Test-AVWorkstationToolkitCatalogFilter' 'Composable desktop catalog filter is not exported.'
    Assert-Contains @($manifest.ExportedFunctions.Keys) 'Get-AVWorkstationToolkitMetadataVerificationState' 'Metadata verification policy is not exported.'
}
Invoke-Check 'Data root is deterministic for source, package, and explicit paths' {
    Assert-Equal $repositoryRoot (Get-AVWorkstationToolkitDataRoot) 'Developer checkout data root differs.'
    $explicitRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-data-{0}' -f [guid]::NewGuid().ToString('N'))
    Assert-Equal ([IO.Path]::GetFullPath($explicitRoot)) (Get-AVWorkstationToolkitDataRoot -Path $explicitRoot) 'Explicit data root differs.'
    $previousDataRoot = [Environment]::GetEnvironmentVariable('AVWORKSTATIONTOOLKIT_DATA_ROOT','Process')
    try {
        [Environment]::SetEnvironmentVariable('AVWORKSTATIONTOOLKIT_DATA_ROOT',$explicitRoot,'Process')
        Assert-Equal ([IO.Path]::GetFullPath($explicitRoot)) (Get-AVWorkstationToolkitDataRoot) 'Environment data root differs.'
    }
    finally { [Environment]::SetEnvironmentVariable('AVWORKSTATIONTOOLKIT_DATA_ROOT',$previousDataRoot,'Process') }
    Assert-Throws { Get-AVWorkstationToolkitDataRoot -Path 'relative\data' } 'absolute path' 'Relative data root was accepted.'
    Assert-Throws { Get-AVWorkstationToolkitDataRoot -Path ([IO.Path]::GetPathRoot($repositoryRoot)) } 'volume root' 'Volume-root data path was accepted.'
}
Invoke-Check 'Legacy application data migration is bounded, verified, and idempotent' {
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-legacy-migration-{0}' -f [guid]::NewGuid().ToString('N'))
    $legacyRoot = Join-Path $temporaryRoot 'AVinite'
    $newRoot = Join-Path $temporaryRoot 'AVWorkstationToolkit'
    try {
        New-Item -ItemType Directory -Path (Join-Path $legacyRoot 'logs\requests'),(Join-Path $legacyRoot 'runtime\1.1.0'),(Join-Path $legacyRoot 'reports'),(Join-Path $legacyRoot 'vendor-cache\Vendor.Tool\1.2.3'),(Join-Path $legacyRoot 'vendor-cache\Vendor.Bad\1.2.3') -Force | Out-Null
        [ordered]@{ SchemaVersion=1; Hosts=@([ordered]@{ Host='vendor.example'; Port=22; Fingerprint='SHA256:fixture' }) } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $legacyRoot 'trusted-sftp-hosts.json') -Encoding UTF8
        'legacy launcher evidence' | Set-Content -LiteralPath (Join-Path $legacyRoot 'launcher-error.log') -Encoding UTF8
        '{"SchemaVersion":1}' | Set-Content -LiteralPath (Join-Path $legacyRoot 'logs\requests\request-20260824-120000-deadbeef.json') -Encoding UTF8
        'must not migrate' | Set-Content -LiteralPath (Join-Path $legacyRoot 'arbitrary.ps1') -Encoding UTF8
        'must not execute or migrate' | Set-Content -LiteralPath (Join-Path $legacyRoot 'runtime\1.1.0\Start-AVWorkstationToolkit.ps1') -Encoding UTF8
        'retained in old location' | Set-Content -LiteralPath (Join-Path $legacyRoot 'reports\historical.json') -Encoding UTF8

        $payloadPath = Join-Path $legacyRoot 'vendor-cache\Vendor.Tool\1.2.3\tool.exe'
        'fixture payload' | Set-Content -LiteralPath $payloadPath -Encoding ASCII
        $payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
        [ordered]@{ SchemaVersion=1; PackageId='Vendor.Tool'; Version='1.2.3'; FileName='tool.exe'; Sha256=$payloadHash } |
            ConvertTo-Json | Set-Content -LiteralPath ($payloadPath + '.avinite.json') -Encoding UTF8
        $badPayload = Join-Path $legacyRoot 'vendor-cache\Vendor.Bad\1.2.3\bad.exe'
        'tampered fixture' | Set-Content -LiteralPath $badPayload -Encoding ASCII
        [ordered]@{ SchemaVersion=1; PackageId='Vendor.Bad'; Version='1.2.3'; FileName='bad.exe'; Sha256=('0' * 64) } |
            ConvertTo-Json | Set-Content -LiteralPath ($badPayload + '.avinite.json') -Encoding UTF8

        $result = Invoke-AVWorkstationToolkitLegacyDataMigration -DataRoot $newRoot -LegacyRoot $legacyRoot
        Assert-Equal 'Completed' $result.Status 'Legacy migration did not complete.'
        Assert-Equal 5 $result.MigratedFiles 'Legacy migration copied an unexpected number of recognized files.'
        foreach ($relativePath in @('trusted-sftp-hosts.json','launcher-error.log','logs\requests\request-20260824-120000-deadbeef.json','vendor-cache\Vendor.Tool\1.2.3\tool.exe','vendor-cache\Vendor.Tool\1.2.3\tool.exe.avworkstationtoolkit.json')) {
            Assert-True (Test-Path -LiteralPath (Join-Path $newRoot $relativePath) -PathType Leaf) "Recognized legacy file was not migrated: $relativePath"
        }
        foreach ($relativePath in @('arbitrary.ps1','runtime\1.1.0\Start-AVWorkstationToolkit.ps1','reports\historical.json','vendor-cache\Vendor.Bad\1.2.3\bad.exe')) {
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $newRoot $relativePath))) "Unapproved or invalid legacy content was migrated: $relativePath"
        }
        $markerPath = Join-Path $newRoot 'legacy-data-migration-v1.json'
        $markerHash = (Get-FileHash -LiteralPath $markerPath -Algorithm SHA256).Hash
        $launcherHash = (Get-FileHash -LiteralPath (Join-Path $newRoot 'launcher-error.log') -Algorithm SHA256).Hash
        $second = Invoke-AVWorkstationToolkitLegacyDataMigration -DataRoot $newRoot -LegacyRoot $legacyRoot
        Assert-Equal 'AlreadyCompleted' $second.Status 'Repeated legacy migration did not use its completion marker.'
        Assert-Equal $markerHash (Get-FileHash -LiteralPath $markerPath -Algorithm SHA256).Hash 'Repeated migration rewrote its marker.'
        Assert-Equal $launcherHash (Get-FileHash -LiteralPath (Join-Path $newRoot 'launcher-error.log') -Algorithm SHA256).Hash 'Repeated migration rewrote migrated evidence.'
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
    }
}
Invoke-Check 'Legacy application data migration rejects a reparse-point root' {
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-legacy-reparse-{0}' -f [guid]::NewGuid().ToString('N'))
    $legacyTarget = Join-Path $temporaryRoot 'legacy-target'
    $legacyLink = Join-Path $temporaryRoot 'AVinite'
    try {
        New-Item -ItemType Directory -Path $legacyTarget -Force | Out-Null
        New-Item -ItemType Junction -Path $legacyLink -Target $legacyTarget | Out-Null
        $result = Invoke-AVWorkstationToolkitLegacyDataMigration -DataRoot (Join-Path $temporaryRoot 'AVWorkstationToolkit') -LegacyRoot $legacyLink
        Assert-Equal 'UnsafeLegacyState' $result.Status 'A reparse-point legacy root was accepted.'
        Assert-Equal 0 $result.MigratedFiles 'A reparse-point legacy root copied files.'
    }
    finally {
        if (Test-Path -LiteralPath $legacyLink) { Remove-Item -LiteralPath $legacyLink -Force }
        if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
    }
}
Invoke-Check 'Removed and prohibited packages are absent from the active catalog' {
    foreach ($id in @('PDFsam.PDFsam','NetSetMan.NetSetMan','Oracle.VirtualBox')) {
        Assert-NotContains $catalog.Id $id 'Removed package remains active.'
    }
    $catalogText = ($catalog | ForEach-Object { $_.Id + ' ' + $_.Name + ' ' + $_.Note }) -join "`n"
    $forbiddenPattern = (Microsoft.PowerShell.Utility\Import-PowerShellDataFile -LiteralPath $catalogPath).ForbiddenPattern
    Assert-True ($catalogText -notmatch $forbiddenPattern) 'Security or management software entered the active catalog.'
}
Invoke-Check 'RealVNC remains viewer-only on deployment and maintenance hold' {
    $item = @($catalog | Where-Object Id -eq 'RealVNC.VNCViewer')[0]
    Assert-Equal 'ManualHold' $item.Deployment 'RealVNC deployment hold changed.'
    Assert-Equal 'Hold' $item.Maintenance 'RealVNC maintenance hold changed.'
    Assert-True ($item.Note -match 'Viewer only') 'RealVNC viewer-only note is missing.'

    $installedWithRealVnc = $installedText.TrimEnd() + "`r`nRealVNC Viewer RealVNC.VNCViewer 7.15.0`r`n"
    $upgradesWithRealVnc = $upgradeText.TrimEnd() + "`r`nRealVNC Viewer RealVNC.VNCViewer 7.15.0 7.15.1.18`r`n"
    $heldPlan = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -InstalledText $installedWithRealVnc -UpgradeText $upgradesWithRealVnc -ExternalInventory $externalInventoryAbsent -ExternalReleaseInfo $externalReleaseBaseline -RebootState $clearReboot -WingetVersion 'v-test'
    $heldItem = @($heldPlan.Packages | Where-Object Id -eq 'RealVNC.VNCViewer')[0]
    Assert-Equal 'Held' $heldItem.Status 'Installed RealVNC update was not held.'
    Assert-True (-not $heldItem.CanSelect -and $heldItem.Action -eq 'None') 'Held RealVNC update remained selectable.'
    Assert-Throws { Assert-AVWorkstationToolkitRequest -Action Update -PackageId 'RealVNC.VNCViewer' -Plan $heldPlan } 'not eligible for update' 'RealVNC maintenance hold was bypassed.'
}
Invoke-Check 'Approved developer runtimes are present without VirtualBox' {
    foreach ($id in @('Python.Launcher','Python.Python.3.11','Microsoft.DotNet.SDK.8')) {
        $item = @($catalog | Where-Object Id -eq $id)
        Assert-Equal 1 $item.Count "Approved developer package is missing: $id"
        Assert-Equal 'Developer' $item[0].Profile "Approved developer package has the wrong profile: $id"
        Assert-Equal 'WinGet' $item[0].Provider "Approved developer package has the wrong provider: $id"
    }
    Assert-NotContains $catalog.Id 'Oracle.VirtualBox' 'VirtualBox entered the approved catalog.'
}
Invoke-Check 'tftpd64 remains an on-demand listener on maintenance hold' {
    $item = @($catalog | Where-Object Id -eq 'PJO2.tftpd64')[0]
    Assert-Equal 'Listener' $item.Risk 'tftpd64 risk changed.'
    Assert-Equal 'Hold' $item.Maintenance 'tftpd64 maintenance hold changed.'
    Assert-True ($item.Note -match 'never use service edition') 'tftpd64 service-edition prohibition is missing.'
}
Invoke-Check 'Q-SYS LTS uses official vendor awareness without redistribution' {
    $item = @($catalog | Where-Object Id -eq 'QSC.QSYSDesigner.LTS')[0]
    Assert-Equal 'External' $item.Provider 'Q-SYS provider differs.'
    Assert-Equal '9.13.2' $item.KnownVersion 'Q-SYS LTS baseline differs.'
    Assert-Equal 'VendorPage' $item.DeliveryMode 'Q-SYS delivery must remain vendor managed.'
    Assert-Equal 'SameMajorMinor' $item.DetectionVersionPolicy 'Q-SYS LTS channel detection policy differs.'
    Assert-Equal 'ManualHold' $item.Deployment 'Q-SYS deployment hold differs.'
    Assert-Equal 'Hold' $item.Maintenance 'Q-SYS maintenance hold differs.'
    Assert-True ($item.ReleaseUri -match '^https://www\.qsys\.com/') 'Q-SYS release check does not use the official HTTPS site.'
}
Invoke-Check 'Focused Biamp, Sennheiser, and Shure additions use manual official handoffs' {
    $expected = @{
        'Biamp.Canvas' = @{ Profile='Field'; Version='5.7.0'; Host='support.biamp.com' }
        'Biamp.Vocia' = @{ Profile='Field'; Version='1.9.0'; Host='www.biamp.com' }
        'Sennheiser.WirelessSystemsManager' = @{ Profile='Field'; Version='4.9.0'; Host='docs.cloud.sennheiser.com' }
        'Shure.UpdateUtility' = @{ Profile='Field'; Version='2.8.16'; Host='www.shure.com' }
        'Shure.Discovery' = @{ Profile='Field'; Version='2.8.16'; Host='www.shure.com' }
        'Shure.MicroflexWireless' = @{ Profile='Optional'; Version='1.2.0'; Host='www.shure.com' }
    }
    foreach ($id in $expected.Keys) {
        $item = @($externalCatalog | Where-Object Id -eq $id)
        Assert-Equal 1 $item.Count "Curated provider count differs: $id"
        Assert-Equal $expected[$id].Profile $item[0].Profile "Curated provider profile differs: $id"
        Assert-Equal $expected[$id].Version $item[0].KnownVersion "Curated provider baseline differs: $id"
        Assert-Equal 'VendorPage' $item[0].ReleaseMode "Curated provider release mode differs: $id"
        Assert-Equal 'VendorPage' $item[0].DeliveryMode "Curated provider delivery mode differs: $id"
        Assert-Equal $expected[$id].Host ([uri]$item[0].ReleaseUri).Host "Curated provider release host differs: $id"
        Assert-Equal 'ManualHold' $item[0].Deployment "Curated provider deployment hold differs: $id"
        Assert-Equal 'Hold' $item[0].Maintenance "Curated provider maintenance hold differs: $id"
    }
    Assert-Equal 'SameMajorMinor' @($externalCatalog | Where-Object Id -eq 'Biamp.Canvas')[0].DetectionVersionPolicy 'Canvas matched-line detection policy differs.'
    Assert-True (@($externalCatalog | Where-Object Id -eq 'Sennheiser.WirelessSystemsManager')[0].Note -match 'plain EW-D uses Smart Assist') 'WSM compatibility boundary is missing.'
    $designer = @($externalCatalog | Where-Object Id -eq 'Shure.Designer')
    Assert-Equal 1 $designer.Count 'Shure Designer is missing from the integrator catalog.'
    Assert-Equal 'Field' $designer[0].Profile 'Shure Designer is not in the Field profile.'
    Assert-Equal '6.10.0' $designer[0].KnownVersion 'Shure Designer baseline differs.'
    foreach ($id in @('Biamp.WorkplaceTools','Sennheiser.TransmitterManager','Shure.IntelliMixRoom','Shure.SystemOn')) {
        Assert-Contains $catalog.Id $id "Required commercial AV awareness record is missing: $id"
    }
    Assert-True (@($externalCatalog | Where-Object Id -like 'Biamp.*').Count -ge 10) 'Biamp catalog breadth regressed.'
    foreach ($id in @('Biamp.ProjectDesigner','Shure.SystemAPI','Oracle.VirtualBox')) {
        Assert-NotContains $catalog.Id $id "Out-of-scope provider entered the catalog: $id"
    }
}
Invoke-Check 'External catalog rejects insecure sources and unsafe bundled paths' {
    $validJson = Get-Content -LiteralPath $externalManifestPath -Raw
    Assert-Throws { ConvertFrom-AVWorkstationToolkitExternalCatalogJson -Json ($validJson.Replace('https://www.qsys.com/','http://www.qsys.com/')) } 'HTTPS' 'An insecure external URI was accepted.'

    $unsafe = $validJson | ConvertFrom-Json
    $unsafe.Packages[0].Delivery.Mode = 'Bundled'
    $unsafe.Packages[0].Delivery | Add-Member -NotePropertyName RelativePath -NotePropertyValue '../installer.exe'
    $unsafe.Packages[0].Delivery | Add-Member -NotePropertyName Sha256 -NotePropertyValue ('0' * 64)
    Assert-Throws { ConvertFrom-AVWorkstationToolkitExternalCatalogJson -Json ($unsafe | ConvertTo-Json -Depth 8) } 'unsafe bundled payload' 'A traversal payload path was accepted.'
}
Invoke-Check 'External providers remain manual and use only approved delivery modes' {
    foreach ($item in $externalCatalog) {
        Assert-Equal 'ManualHold' $item.Deployment "External deployment hold differs: $($item.Id)"
        Assert-Equal 'Hold' $item.Maintenance "External maintenance hold differs: $($item.Id)"
        Assert-Contains @('VendorPage','DirectDownload','AuthenticatedSftp','ParentProvider','Bundled','Awareness','InventoryOnly') $item.DeliveryMode "External delivery mode differs: $($item.Id)"
    }
    Assert-Equal 1 @($externalCatalog | Where-Object DeliveryMode -eq 'DirectDownload').Count 'Direct-download provider count differs.'
    Assert-Equal 1 @($externalCatalog | Where-Object DeliveryMode -eq 'AuthenticatedSftp').Count 'Authenticated-SFTP provider count differs.'
    Assert-Equal 7 @($externalCatalog | Where-Object DeliveryMode -eq 'ParentProvider').Count 'Crestron child-provider count differs.'
    foreach ($mode in @('VendorPage','DirectDownload','AuthenticatedSftp','ParentProvider','Bundled','Awareness','InventoryOnly')) {
        $expected = @(@($operationalExternalCatalog + $awarenessExternalCatalog) | Where-Object DeliveryMode -eq $mode).Count
        Assert-Equal $expected @($externalCatalog | Where-Object DeliveryMode -eq $mode).Count "Delivery-mode aggregation differs: $mode"
    }
}
Invoke-Check 'External delivery actions stay explicit and bounded across provider modes' {
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-delivery-matrix-{0}' -f [guid]::NewGuid().ToString('N'))
    try {
        $payloadDirectory = Join-Path $temporaryRoot 'packages\fixture'
        New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
        $payloadPath = Join-Path $payloadDirectory 'installer.exe'
        'delivery fixture' | Set-Content -LiteralPath $payloadPath -Encoding ASCII
        $payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
        $module = Get-Module AVWorkstationToolkit.Core
        $newPackage = {
            param($mode)
            [pscustomobject]@{
                Provider='External'; Id=('Example.' + $mode); DeliveryMode=$mode
                DeliveryUri='https://vendor.example/downloads'; OfficialProductUri='https://vendor.example/product'; DeploymentClass='ManualHandoff'
                DeliveryProviderId='Example.Parent'; DeliveryProductId='137'; PayloadRelativePath='fixture/installer.exe'; PayloadSha256=$payloadHash; PayloadPublisher=''
                DownloadPublisherPattern='Microsoft Corporation'; DownloadMaxBytes=10MB
            }
        }.GetNewClosure()
        $getDelivery = {
            param($package,$releaseRecord)
            & $module {
                param($deliveryPackage,$deliveryRelease,$distributionRoot,$dataRoot)
                Get-AVWorkstationToolkitExternalDeliveryState -Package $deliveryPackage -ReleaseRecord $deliveryRelease -AvailableVersion '1.2.3' -DistributionRoot $distributionRoot -DataRoot $dataRoot
            } $package $releaseRecord $temporaryRoot $temporaryRoot
        }.GetNewClosure()

        $vendorPage = & $getDelivery (& $newPackage 'VendorPage') $null
        $bundled = & $getDelivery (& $newPackage 'Bundled') $null
        $direct = & $getDelivery (& $newPackage 'DirectDownload') ([pscustomobject]@{ DownloadUri='https://vendor.example/installer.exe' })
        $sftp = & $getDelivery (& $newPackage 'AuthenticatedSftp') $null
        $parent = & $getDelivery (& $newPackage 'ParentProvider') $null
        $awareness = & $getDelivery (& $newPackage 'Awareness') $null
        $inventoryOnly = & $getDelivery (& $newPackage 'InventoryOnly') $null

        Assert-True ($vendorPage.Action -eq 'OpenUri' -and $vendorPage.Uri -eq 'https://vendor.example/downloads') 'Vendor-page delivery is not an explicit HTTPS handoff.'
        Assert-True ($bundled.Action -eq 'ShowFile' -and $bundled.Path -eq $payloadPath -and $bundled.Label -eq 'Show verified package') 'Bundled delivery is not an exact verified-file handoff.'
        Assert-True ($direct.Action -eq 'DownloadHttps' -and $direct.Uri -eq 'https://vendor.example/installer.exe') 'Direct delivery is not a bounded download handoff.'
        Assert-Equal 'AuthenticatedSftp' $sftp.Action 'Authenticated SFTP delivery action differs.'
        Assert-Equal 'AuthenticatedSftp' $parent.Action 'Uncached parent-provider delivery does not use its authenticated provider.'
        Assert-True ($awareness.Action -eq 'OpenUri' -and $awareness.Label -eq 'Open official product') 'Awareness delivery gained more than an official-link handoff.'
        Assert-True ($inventoryOnly.Action -eq 'None' -and -not $inventoryOnly.Available) 'Inventory-only delivery gained an actionable handoff.'

        $allowedActions = @('None','OpenUri','DownloadHttps','AuthenticatedSftp','ShowFile')
        foreach ($item in @($plan.Packages | Where-Object Provider -eq 'External')) {
            Assert-Contains $allowedActions $item.DeliveryAction "Plan produced an unknown delivery action: $($item.Id)"
        }
        $uiSource = Get-Content -LiteralPath (Join-Path $scriptsRoot 'Start-AVWorkstationToolkit.ps1') -Raw
        foreach ($action in @('DownloadHttps','AuthenticatedSftp','ShowFile','OpenUri')) {
            Assert-True ($uiSource -match [regex]::Escape("DeliveryAction -eq '$action'") -or $uiSource -match [regex]::Escape("DeliveryAction -ne '$action'")) "Desktop delivery router omits $action."
        }
        Assert-True ($uiSource -match 'Open-AVWorkstationToolkitExplorerPath\s+-Path\s+\$path\s+-SelectFile') 'Exact file handoffs do not request Explorer selection.'
    }
    finally { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
Invoke-Check 'Awareness-only records never become selectable deployment actions' {
    $awareness = @($plan.Packages | Where-Object DeliveryMode -eq 'Awareness')
    Assert-True ($awareness.Count -gt 200) 'Awareness catalog did not enter the plan.'
    Assert-Equal 0 @($awareness | Where-Object CanSelect).Count 'An awareness record became selectable.'
    Assert-Equal 0 @($awareness | Where-Object { $_.Action -in @('Install','Update') }).Count 'An awareness record gained an automated action.'
    foreach ($item in $awareness) { Assert-Contains @('Awareness','NotDetected','Inventory') $item.Status "Awareness state differs: $($item.Id)" }
    $uiSource = Get-Content -LiteralPath (Join-Path $scriptsRoot 'Start-AVWorkstationToolkit.ps1') -Raw
    Assert-True ($uiSource -notmatch 'Not a Windows app') 'Catalog-only UI still mislabels Windows tools without detection evidence.'
}
Invoke-Check 'Inventory-only providers report state without inventing versions or actions' {
    foreach ($id in @('Runtime.Java','Dell.WavesAudio')) {
        $item = @($plan.Packages | Where-Object Id -eq $id)[0]
        Assert-Equal 'NotDetected' $item.Status "Inventory-only state differs: $id"
        Assert-True (-not $item.CanSelect -and -not $item.DeliveryAvailable) "Inventory-only package gained a change path: $id"
        Assert-Equal '' $item.AvailableVersion "Inventory-only package invented an available version: $id"
    }
    foreach ($id in @('Extron.Toolbelt','Extron.PCS','FileZilla.Client')) {
        $item = @($plan.Packages | Where-Object Id -eq $id)[0]
        Assert-Equal 'NotDetected' $item.Status "Inventory-plus-handoff state differs: $id"
        Assert-True (-not $item.CanSelect -and $item.DeliveryAvailable) "Official vendor handoff differs: $id"
        Assert-Equal 'OpenUri' $item.DeliveryAction "Official vendor handoff action differs: $id"
    }
}
Invoke-Check 'Biamp release content yields only an allowlisted direct installer URI' {
    $item = @($externalCatalog | Where-Object Id -eq 'Biamp.Tesira')[0]
    $content = '<h2>Tesira Software v5.7.0</h2><a href="https://downloads.biamp.com/assets/docs/default-source/sw-fw/tesira-software-5-7-0.exe">Download</a>'
    Assert-Equal '5.7.0' (ConvertFrom-AVWorkstationToolkitExternalReleaseContent -Package $item -Content $content) 'Biamp release version differs.'
    $downloadUri = ConvertFrom-AVWorkstationToolkitExternalDownloadContent -Package $item -Content $content -ExpectedVersion '5.7.0'
    Assert-Equal 'https://downloads.biamp.com/assets/docs/default-source/sw-fw/tesira-software-5-7-0.exe' $downloadUri 'Biamp direct installer URI differs.'
    $release = @(Get-AVWorkstationToolkitExternalReleaseInfo -Package $item -ContentByUri @{ ([string]$item.ReleaseUri)=$content })[0]
    Assert-Equal $downloadUri $release.DownloadUri 'Biamp release provider lost the direct installer URI.'
    $hostile = $content.Replace('downloads.biamp.com','downloads.biamp.com.evil.example')
    Assert-Throws { ConvertFrom-AVWorkstationToolkitExternalDownloadContent -Package $item -Content $hostile } 'not found|allowlisted' 'A lookalike Biamp download host was accepted.'
    $staleInstaller = $content.Replace('tesira-software-5-7-0.exe','tesira-software-5-6-0.exe')
    Assert-Throws { ConvertFrom-AVWorkstationToolkitExternalDownloadContent -Package $item -Content $staleInstaller -ExpectedVersion '5.7.0' } 'allowlisted' 'A signed-vendor URL for the wrong Biamp version was accepted.'
}
Invoke-Check 'Curated AV vendor release patterns parse deterministic official-page fixtures' {
    $fixtures = @{
        'Biamp.Canvas' = @{ Version='5.7.0'; Content='<li>Biamp Canvas Software v5.7.0 (June 2026)</li>' }
        'Biamp.Vocia' = @{ Version='1.9.0'; Content='<div>Vocia Software v1.9.0</div>' }
        'Sennheiser.WirelessSystemsManager' = @{ Version='4.9.0'; Content='<main>Wireless Systems Manager <span>v4.9.0</span></main>' }
        'Shure.UpdateUtility' = @{ Version='2.8.16'; Content='<h1>Shure Update Utility</h1><h2>2.8.16</h2>' }
        'Shure.Discovery' = @{ Version='2.8.16'; Content='<h1>Shure Discovery</h1><h2>2.8.16</h2>' }
        'Shure.MicroflexWireless' = @{ Version='1.2.0'; Content='<h1>Microflex Wireless Software</h1><h2>1.2.0</h2>' }
    }
    foreach ($id in $fixtures.Keys) {
        $item = @($externalCatalog | Where-Object Id -eq $id)[0]
        Assert-Equal $fixtures[$id].Version (ConvertFrom-AVWorkstationToolkitExternalReleaseContent -Package $item -Content $fixtures[$id].Content) "Vendor version parse differs: $id"
    }
}
Invoke-Check 'Curated AV vendor registry patterns detect representative installations' {
    $entries = @(
        [pscustomobject]@{ DisplayName='Biamp Canvas Software 5.7.0'; DisplayVersion='5.7.0' },
        [pscustomobject]@{ DisplayName='Vocia Software 1.9.0'; DisplayVersion='1.9.0' },
        [pscustomobject]@{ DisplayName='Sennheiser Wireless Systems Manager'; DisplayVersion='4.9.0' },
        [pscustomobject]@{ DisplayName='Shure Update Utility'; DisplayVersion='2.8.16' },
        [pscustomobject]@{ DisplayName='Shure Web Device Discovery Application'; DisplayVersion='2.8.16' },
        [pscustomobject]@{ DisplayName='Microflex Wireless Software'; DisplayVersion='1.2.0' }
    )
    $ids = @('Biamp.Canvas','Biamp.Vocia','Sennheiser.WirelessSystemsManager','Shure.UpdateUtility','Shure.Discovery','Shure.MicroflexWireless')
    $items = @($externalCatalog | Where-Object Id -in $ids)
    $inventory = @(Get-AVWorkstationToolkitExternalInventory -Package $items -RegistryEntry $entries)
    Assert-Equal $ids.Count $inventory.Count 'Curated provider inventory count differs.'
    foreach ($id in $ids) {
        $state = @($inventory | Where-Object Id -eq $id)[0]
        Assert-True ($state.Installed -and $state.Reliable) "Representative installation was not detected: $id"
        Assert-Equal @($externalCatalog | Where-Object Id -eq $id)[0].KnownVersion $state.InstalledVersion "Representative installed version differs: $id"
    }
}
Invoke-Check 'Partial uninstall-registry failure preserves useful source results' {
    $ids = @('QSC.QSYSDesigner.LTS','Biamp.Tesira','7thSense.DeltaMediaServer')
    $items = @($externalCatalog | Where-Object Id -in $ids)
    $sources = @(
        [pscustomobject]@{ Name='HKLM64'; Available=$true; Entries=@([pscustomobject]@{DisplayName='Q-SYS Designer Software 9.13.1';DisplayVersion='9.13.1'}); Detail='Registry source available.' },
        [pscustomobject]@{ Name='HKLM32'; Available=$true; Entries=@(); Detail='Registry source available.' },
        [pscustomobject]@{ Name='HKCU'; Available=$false; Entries=@(); Detail='Access denied.' }
    )
    $inventory = @(Get-AVWorkstationToolkitExternalInventory -Package $items -RegistrySourceResult $sources)
    $qsys = @($inventory | Where-Object Id -eq 'QSC.QSYSDesigner.LTS')[0]
    $biamp = @($inventory | Where-Object Id -eq 'Biamp.Tesira')[0]
    $awareness = @($inventory | Where-Object Id -eq '7thSense.DeltaMediaServer')[0]
    Assert-True ($qsys.Reliable -and $qsys.Installed) 'HKLM detection was poisoned by the HKCU failure.'
    Assert-Equal '9.13.1' $qsys.InstalledVersion 'HKLM installed version was lost.'
    Assert-True (-not $biamp.Reliable -and $biamp.InventoryQuality -eq 'Partial') 'An unmatched detector did not report incomplete coverage.'
    Assert-True ($awareness.Reliable -and $awareness.InventoryQuality -eq 'NotApplicable') 'Awareness inventory was affected by registry coverage.'

    $combined = @($externalInventoryAbsent | Where-Object Id -notin $ids) + $inventory
    $partialPlan = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -InstalledText $installedText -UpgradeText $upgradeText -ExternalInventory $combined -ExternalReleaseInfo $externalReleaseBaseline -RebootState $clearReboot -WingetVersion 'v-test'
    Assert-Equal 'InventoryIncomplete' @($partialPlan.Packages | Where-Object Id -eq 'Biamp.Tesira')[0].Status 'Partial coverage was rendered as the wrong package status.'
    Assert-True (@($partialPlan.Packages | Where-Object Id -eq 'QSC.QSYSDesigner.LTS')[0].Status -ne 'Error') 'A successful HKLM match became Error.'
    Assert-Equal 'Awareness' @($partialPlan.Packages | Where-Object Id -eq '7thSense.DeltaMediaServer')[0].Status 'Awareness state changed under partial coverage.'
    Assert-Equal 'Partial' $partialPlan.ExternalInventory.Quality 'Plan source-quality summary differs.'
    Assert-Equal 2 $partialPlan.ExternalInventory.AvailableSourceCount 'Available source count differs.'
    $inventorySource = Get-Content -LiteralPath $moduleImplementationPath -Raw
    Assert-True ($inventorySource -notmatch '\$entry\.DisplayName|\$entry\.DisplayVersion') 'A registry entry missing optional uninstall properties can still abort its source.'
}
Invoke-Check 'Malformed external package version affects only the matching package' {
    $ids = @('QSC.QSYSDesigner.LTS','Biamp.Tesira','7thSense.DeltaMediaServer')
    $items = @($externalCatalog | Where-Object Id -in $ids)
    $entries = @(
        [pscustomobject]@{DisplayName='Q-SYS Designer Software 9.13.1';DisplayVersion='9.13.1'},
        [pscustomobject]@{DisplayName='Biamp Tesira Software';DisplayVersion='not-a-version'}
    )
    $inventory = @(Get-AVWorkstationToolkitExternalInventory -Package $items -RegistryEntry $entries)
    Assert-Equal 'PackageError' @($inventory | Where-Object Id -eq 'Biamp.Tesira')[0].InventoryQuality 'Malformed matching version quality differs.'
    Assert-True @($inventory | Where-Object Id -eq 'QSC.QSYSDesigner.LTS')[0].Reliable 'Malformed Biamp version poisoned Q-SYS inventory.'
    Assert-True @($inventory | Where-Object Id -eq '7thSense.DeltaMediaServer')[0].Reliable 'Malformed Biamp version poisoned awareness inventory.'

    $combined = @($externalInventoryAbsent | Where-Object Id -notin $ids) + $inventory
    $malformedPlan = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -InstalledText $installedText -UpgradeText $upgradeText -ExternalInventory $combined -ExternalReleaseInfo $externalReleaseBaseline -RebootState $clearReboot -WingetVersion 'v-test'
    Assert-Equal 'Error' @($malformedPlan.Packages | Where-Object Id -eq 'Biamp.Tesira')[0].Status 'Package-specific malformed version did not remain Error.'
    Assert-True (@($malformedPlan.Packages | Where-Object Id -eq 'QSC.QSYSDesigner.LTS')[0].Status -ne 'Error') 'Unrelated Q-SYS package became Error.'
    Assert-Equal 'Awareness' @($malformedPlan.Packages | Where-Object Id -eq '7thSense.DeltaMediaServer')[0].Status 'Awareness record became Error.'
}
Invoke-Check 'Complete uninstall-registry failure is distinct from package errors' {
    $ids = @('QSC.QSYSDesigner.LTS','Biamp.Tesira','7thSense.DeltaMediaServer')
    $items = @($externalCatalog | Where-Object Id -in $ids)
    $sources = @(
        [pscustomobject]@{ Name='HKLM64'; Available=$false; Entries=@(); Detail='Failed.' },
        [pscustomobject]@{ Name='HKLM32'; Available=$false; Entries=@(); Detail='Failed.' },
        [pscustomobject]@{ Name='HKCU'; Available=$false; Entries=@(); Detail='Failed.' }
    )
    $inventory = @(Get-AVWorkstationToolkitExternalInventory -Package $items -RegistrySourceResult $sources)
    $combined = @($externalInventoryAbsent | Where-Object Id -notin $ids) + $inventory
    $unavailablePlan = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -InstalledText $installedText -UpgradeText $upgradeText -ExternalInventory $combined -ExternalReleaseInfo $externalReleaseBaseline -RebootState $clearReboot -WingetVersion 'v-test'
    foreach ($id in @('QSC.QSYSDesigner.LTS','Biamp.Tesira')) {
        Assert-Equal 'InventoryUnavailable' @($unavailablePlan.Packages | Where-Object Id -eq $id)[0].Status "Complete registry failure state differs: $id"
    }
    Assert-Equal 'Awareness' @($unavailablePlan.Packages | Where-Object Id -eq '7thSense.DeltaMediaServer')[0].Status 'Awareness was affected by complete registry failure.'
    Assert-Equal 'Unavailable' $unavailablePlan.ExternalInventory.Quality 'Complete registry failure plan quality differs.'
    Assert-Equal 0 $unavailablePlan.ExternalInventory.AvailableSourceCount 'Unavailable source count differs.'
}
Invoke-Check 'Vendor metadata fetches are byte-bounded and reject cross-host redirects' {
    $source = Get-Content -LiteralPath (Join-Path $scriptsRoot 'AVWorkstationToolkit.Core.psm1') -Raw
    Assert-True ($source -match 'function Invoke-AVWorkstationToolkitBoundedHttpsText' -and $source -match 'AllowAutoRedirect\s*=\s*\$false') 'Vendor metadata does not use the bounded manual-redirect reader.'
    Assert-True ($source -match 'redirected to an unapproved host' -and $source -match 'totalBytes\s+-gt\s+\$MaximumBytes') 'Vendor metadata host or byte limit is not enforced before parsing.'
    Assert-True ($source -notmatch 'Invoke-WebRequest\s+-Uri\s+\$uri' -and $source -notmatch 'Invoke-WebRequest\s+-Uri\s+\(\[string\]\$Package\.SftpCatalogUri\)') 'A vendor metadata path still uses an unbounded web response.'
}
Invoke-Check 'Crestron product feed accepts only the complete curated software set' {
    $item = @($externalCatalog | Where-Object Id -eq 'Crestron.MasterInstaller')[0]
    $products = @(
        @{ Id='1'; Name='VisionTools Pro-e'; Version='6.2.02.08'; File='vt_pro-e/vt_pro-e_6.2.02.08.exe' },
        @{ Id='2'; Name='SIMPL Windows'; Version='4.3200.02.01'; File='simpl_windows/simpl_windows_4.3200.02.01.exe' },
        @{ Id='9'; Name='Crestron Database'; Version='228.55.001.00'; File='crestron_database/crestron_database_228.55.001.00.exe' },
        @{ Id='10'; Name='Device Database'; Version='200.465.001.00'; File='device_database/device_database_200.465.001.00.exe' },
        @{ Id='137'; Name='Toolbox'; Version='3.1390.0008.3'; File='crestron_toolbox/crestron_toolbox_3.1390.0008.3.exe' },
        @{ Id='400'; Name='Smart Graphics'; Version='2.19.01.04'; File='vt_pro-e/core_3_ui/crestron_smartgraphics_2.19.01.04.exe' },
        @{ Id='406'; Name='DM NVX Tool'; Version='4.3.2.0737'; File='dm_nvx_tool/dmnvxtool_installer_4.3.2.0737.exe' }
    )
    $nodes = @($products | ForEach-Object {
        '<Product Id="{0}" Version="{1}"><Name>{2}</Name><Download>/software/{3}</Download><Size Units="MB">1.0</Size><Reboot>0</Reboot></Product>' -f $_.Id,$_.Version,$_.Name,$_.File
    }) -join ''
    $content = '<UpdateInformation>' + $nodes + '<Product Id="999" Version="1.0"><Name>Unapproved</Name><Download>/software/unapproved.exe</Download><Size Units="MB">1.0</Size><Reboot>0</Reboot></Product></UpdateInformation>'
    $parsed = @(Get-AVWorkstationToolkitAuthenticatedSftpCatalog -Package $item -Content $content)
    Assert-Equal 7 $parsed.Count 'Curated Crestron product count differs.'
    Assert-NotContains $parsed.ProductId '999' 'An unapproved Crestron product entered the picker.'
    Assert-Throws { Get-AVWorkstationToolkitAuthenticatedSftpCatalog -Package $item -Content $content.Replace('/software/vt_pro-e/vt_pro-e_6.2.02.08.exe','/software/../escape.exe') } 'unsafe remote path' 'Crestron path traversal was accepted.'
    Assert-Throws { Get-AVWorkstationToolkitAuthenticatedSftpCatalog -Package $item -Content $content.Replace($nodes.Substring(0,$nodes.IndexOf('</Product>') + 10),'') } 'omitted allowlisted' 'An incomplete Crestron product feed was accepted.'
    Assert-Throws { Get-AVWorkstationToolkitAuthenticatedSftpCatalog -Package $item -Content '<!DOCTYPE UpdateInformation [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]><UpdateInformation />' } 'DTD|prohibited' 'An XML DTD was accepted.'

    $children = @($externalCatalog | Where-Object ParentProviderId -eq $item.Id)
    Assert-Equal 7 $children.Count 'Crestron child application count differs.'
    Assert-Equal 1 @($externalCatalog | Where-Object DeliveryMode -eq 'AuthenticatedSftp').Count 'More than one independent SFTP provider exists.'
    foreach ($child in $children) {
        Assert-Equal 'ParentProvider' $child.DeliveryMode "Crestron child bypasses the parent provider: $($child.Id)"
        Assert-Equal 'ParentCatalog' $child.ReleaseMode "Crestron child bypasses the parent release feed: $($child.Id)"
        Assert-Equal $item.SftpHost $child.SftpHost "Crestron child SFTP host differs: $($child.Id)"
        Assert-Equal $item.SftpCatalogUri $child.SftpCatalogUri "Crestron child catalog URI differs: $($child.Id)"
        Assert-Equal $item.SftpRemoteRoot $child.SftpRemoteRoot "Crestron child remote root differs: $($child.Id)"
        Assert-Equal $item.DownloadPublisherPattern $child.DownloadPublisherPattern "Crestron child publisher policy differs: $($child.Id)"
        Assert-Equal 1 @($child.SftpAllowedProductIds).Count "Crestron child product scope is not singular: $($child.Id)"
        Assert-Equal $child.DeliveryProductId @($child.SftpAllowedProductIds)[0] "Crestron child product scope differs: $($child.Id)"
    }
    $providerSet = @($item) + @($children)
    $release = @(Get-AVWorkstationToolkitExternalReleaseInfo -Package $providerSet -ContentByUri @{ ([string]$item.SftpCatalogUri)=$content })
    foreach ($child in $children) {
        $state = @($release | Where-Object Id -eq $child.Id)[0]
        $expectedVersion = [string]@($products | Where-Object Id -eq $child.DeliveryProductId)[0].Version
        Assert-Equal $expectedVersion $state.AvailableVersion "Crestron child version did not resolve through the parent feed: $($child.Id)"
        Assert-True $state.OnlineAvailable "Crestron child parent-feed resolution was not marked available: $($child.Id)"
    }
}
Invoke-Check 'SFTP host trust is explicit, scoped, and replaceable' {
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-host-trust-{0}' -f [guid]::NewGuid().ToString('N'))
    try {
        $first = 'SHA256:' + ('A' * 43)
        $replacement = 'SHA256:' + ('B' * 43)
        $other = 'SHA256:' + ('C' * 43)
        [void](Set-AVWorkstationToolkitTrustedSftpHost -HostName 'ftp.crestron.com' -Port 22 -Fingerprint $first -DataRoot $temporaryRoot)
        [void](Set-AVWorkstationToolkitTrustedSftpHost -HostName 'sftp.example.test' -Port 2222 -Fingerprint $other -DataRoot $temporaryRoot)
        [void](Set-AVWorkstationToolkitTrustedSftpHost -HostName 'FTP.CRESTRON.COM' -Port 22 -Fingerprint $replacement -DataRoot $temporaryRoot)
        $trusted = Get-AVWorkstationToolkitTrustedSftpHost -HostName 'ftp.crestron.com' -Port 22 -DataRoot $temporaryRoot
        $untouched = Get-AVWorkstationToolkitTrustedSftpHost -HostName 'sftp.example.test' -Port 2222 -DataRoot $temporaryRoot
        Assert-Equal $replacement ([string]$trusted.Fingerprint) 'Per-host SFTP fingerprint was not replaced.'
        Assert-Equal $other ([string]$untouched.Fingerprint) 'Unrelated SFTP host trust was changed.'
        Assert-Throws { Set-AVWorkstationToolkitTrustedSftpHost -HostName 'ftp.crestron.com' -Port 22 -Fingerprint 'MD5:unsafe' -DataRoot $temporaryRoot } 'SHA256' 'A weak SFTP fingerprint was accepted.'
    }
    finally { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
Invoke-Check 'Downloaded vendor installers require a valid publisher and tamper-evident cache metadata' {
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-vendor-cache-{0}' -f [guid]::NewGuid().ToString('N'))
    $downloadDirectory = Join-Path $temporaryRoot 'vendor-cache\Example.Direct\1.2.3'
    $downloadPath = Join-Path $downloadDirectory 'notepad.exe.download'
    try {
        New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\notepad.exe') -Destination $downloadPath
        $package = [pscustomobject]@{
            Id='Example.Direct'; Provider='External'; DeliveryMode='DirectDownload'; DownloadPublisherPattern='Microsoft Corporation'; DownloadMaxBytes=10MB
        }
        $completed = Complete-AVWorkstationToolkitVendorDownload -Package $package -Version '1.2.3' -DownloadPath $downloadPath -DataRoot $temporaryRoot -Source 'Fixture'
        Assert-True ($completed.Valid -and (Test-Path -LiteralPath $completed.Path -PathType Leaf)) 'Signed vendor fixture was not finalized.'
        $cached = Resolve-AVWorkstationToolkitVendorCachePayload -Package $package -Version '1.2.3' -DataRoot $temporaryRoot
        Assert-True ($cached.Valid -and $cached.Path -eq $completed.Path) 'Verified vendor fixture was not resolved from cache.'
        $package.DeliveryMode = 'ParentProvider'
        $package | Add-Member -NotePropertyName DeliveryProviderId -NotePropertyValue 'Example.Parent'
        $package | Add-Member -NotePropertyName DeliveryProductId -NotePropertyValue '137'
        $delivery = & (Get-Module AVWorkstationToolkit.Core) {
            param($deliveryPackage,$deliveryRoot)
            Get-AVWorkstationToolkitExternalDeliveryState -Package $deliveryPackage -AvailableVersion '1.2.3' -DistributionRoot $deliveryRoot -DataRoot $deliveryRoot
        } $package $temporaryRoot
        Assert-True ($delivery.Action -eq 'ShowFile' -and $delivery.Path -eq $completed.Path) 'Verified parent-provider cache did not produce an exact file handoff.'
        Assert-Equal 'Show cached installer' $delivery.Label 'Cached installer handoff is mislabeled as an installed-package verification action.'
        Assert-True ($delivery.Detail -match 'previously downloaded installer') 'Cached installer handoff does not explain its purpose.'
        Add-Content -LiteralPath $completed.Path -Value 'tamper' -Encoding ASCII
        $tampered = Resolve-AVWorkstationToolkitVendorCachePayload -Package $package -Version '1.2.3' -DataRoot $temporaryRoot
        Assert-True (-not $tampered.Valid) 'Tampered vendor cache payload was accepted.'
    }
    finally { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
Invoke-Check 'Q-SYS release parser extracts the bounded LTS version' {
    $item = @($externalCatalog | Where-Object Id -eq 'QSC.QSYSDesigner.LTS')[0]
    $content = '<p>Q-SYS Designer Software v9.13.1 LTS</p><h2>Q-SYS Designer Software v9.13.2 LTS (long-term support)</h2>'
    Assert-Equal '9.13.2' (ConvertFrom-AVWorkstationToolkitExternalReleaseContent -Package $item -Content $content) 'Q-SYS LTS version parse differs.'
    Assert-True ((Compare-AVWorkstationToolkitVersion -Left '9.13.2' -Right '9.13.1') -gt 0) 'External version comparison differs.'
    Assert-Throws { ConvertFrom-AVWorkstationToolkitExternalReleaseContent -Package $item -Content '<html>No release here</html>' } 'was not found' 'Missing vendor version was accepted.'
}
Invoke-Check 'External registry inventory drives a non-automated Q-SYS update' {
    $item = @($externalCatalog | Where-Object Id -eq 'QSC.QSYSDesigner.LTS')[0]
    $inventory = @(Get-AVWorkstationToolkitExternalInventory -Package $item -RegistryEntry @(
        [pscustomobject]@{ DisplayName='Q-SYS Designer Software 9.12.1'; DisplayVersion='9.12.1' },
        [pscustomobject]@{ DisplayName='Q-SYS Designer Software 9.13.1'; DisplayVersion='9.13.1' },
        [pscustomobject]@{ DisplayName='Q-SYS Designer Software 10.4.1'; DisplayVersion='10.4.1' }
    ))
    Assert-Equal '10.4.1' $inventory[0].InstalledVersion 'Highest installed Q-SYS version differs.'
    $manualPlan = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -InstalledText $installedText -UpgradeText $upgradeText -ExternalInventory $inventory -ExternalReleaseInfo $externalReleaseBaseline -RebootState $clearReboot -WingetVersion 'v-test'
    $qsys = @($manualPlan.Packages | Where-Object Id -eq $item.Id)[0]
    Assert-Equal 'ManualUpdate' $qsys.Status 'Q-SYS update state differs.'
    Assert-Equal '9.13.1' $qsys.InstalledVersion 'Q-SYS LTS channel selected a non-LTS installed version.'
    Assert-Equal '9.13.2' $qsys.AvailableVersion 'Q-SYS available version differs.'
    Assert-True (-not $qsys.CanSelect) 'Q-SYS vendor update became automatically selectable.'
    Assert-True $qsys.DeliveryAvailable 'Q-SYS vendor handoff is unavailable.'
    Assert-Throws { Assert-AVWorkstationToolkitRequest -Action Update -PackageId $item.Id -Plan $manualPlan } 'not eligible for update' 'External vendor update entered the automated worker.'
}
Invoke-Check 'External online-check failure retains the catalog baseline' {
    $item = @($externalCatalog | Where-Object Id -eq 'QSC.QSYSDesigner.LTS')[0]
    $contentByUri = @{}
    $contentByUri[[string]$item.ReleaseUri] = '<html>Vendor page changed</html>'
    $release = @(Get-AVWorkstationToolkitExternalReleaseInfo -Package $item -ContentByUri $contentByUri)[0]
    Assert-Equal '9.13.2' $release.AvailableVersion 'Catalog baseline was lost after online parse failure.'
    Assert-True (-not $release.OnlineAvailable) 'Failed online parse was reported as available.'
    Assert-True ($release.Detail -match 'unavailable') 'Online failure detail is missing.'
}
Invoke-Check 'Bundled external payloads require the embedded hash' {
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-external-payload-{0}' -f [guid]::NewGuid().ToString('N'))
    $relativePath = 'Example.Package/1.2.3/installer.zip'
    $payloadPath = Join-Path $temporaryRoot ('packages\' + $relativePath.Replace('/','\'))
    try {
        New-Item -ItemType Directory -Path (Split-Path -Parent $payloadPath) -Force | Out-Null
        'verified fixture payload' | Set-Content -LiteralPath $payloadPath -Encoding ASCII
        $hash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
        $package = [pscustomobject]@{
            Id='Example.Package'; Provider='External'; DeliveryMode='Bundled'; PayloadRelativePath=$relativePath; PayloadSha256=$hash; PayloadPublisher=''
        }
        $verified = Resolve-AVWorkstationToolkitExternalPayload -Package $package -DistributionRoot $temporaryRoot
        Assert-True ($verified.Available -and $verified.Valid) 'Valid external payload was rejected.'
        'tampered fixture payload' | Set-Content -LiteralPath $payloadPath -Encoding ASCII
        $tampered = Resolve-AVWorkstationToolkitExternalPayload -Package $package -DistributionRoot $temporaryRoot
        Assert-True ($tampered.Available -and -not $tampered.Valid) 'Tampered external payload was accepted.'
    }
    finally { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
Invoke-Check 'Fixture plan classifies current, missing, update, held, and manual states' {
    Assert-Equal 'Current' @($plan.Packages | Where-Object Id -eq '7zip.7zip')[0].Status '7-Zip state differs.'
    Assert-Equal 'Missing' @($plan.Packages | Where-Object Id -eq 'Notepad++.Notepad++')[0].Status 'Notepad++ state differs.'
    Assert-Equal 'UpdateAvailable' @($plan.Packages | Where-Object Id -eq 'Microsoft.VisualStudioCode')[0].Status 'VS Code state differs.'
    Assert-Equal 'Held' @($plan.Packages | Where-Object Id -eq 'PJO2.tftpd64')[0].Status 'tftpd64 state differs.'
    Assert-Equal 'Manual' @($plan.Packages | Where-Object Id -eq 'RealVNC.VNCViewer')[0].Status 'RealVNC state differs.'
}
Invoke-Check 'Fixture plan summary is internally consistent' {
    Assert-Equal $catalog.Count $plan.Summary.Total 'Total differs.'
    Assert-Equal 1 $plan.Summary.Current 'Current count differs.'
    Assert-Equal 25 $plan.Summary.Missing 'Missing count differs.'
    Assert-Equal 1 $plan.Summary.Updates 'Update count differs.'
    Assert-Equal 0 $plan.Summary.ManualUpdates 'Manual update count differs.'
    Assert-Equal 1 $plan.Summary.Held 'Held count differs.'
    Assert-Equal 0 $plan.Summary.Inventory 'Inventory count differs.'
    Assert-Equal 26 $plan.Summary.Selectable 'Selectable count differs.'
    Assert-Equal @($plan.Packages | Where-Object CanSelect).Count $plan.Summary.Selectable 'Selectable summary is not derived from package state.'
    $classified = $plan.Summary.Current + $plan.Summary.Missing + $plan.Summary.Updates + $plan.Summary.ManualUpdates +
        $plan.Summary.Held + $plan.Summary.Manual + $plan.Summary.Inventory + $plan.Summary.NotDetected +
        $plan.Summary.Awareness + $plan.Summary.InventoryIncomplete + $plan.Summary.InventoryUnavailable +
        $plan.Summary.CheckUnavailable + $plan.Summary.Errors
    Assert-Equal $plan.Summary.Total $classified 'Plan status buckets do not cover every package exactly once.'
}
Invoke-Check 'Plan refresh reports deterministic subsystem stages' {
    $stages = [System.Collections.Generic.List[string]]::new()
    $stagedPlan = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -InstalledText $installedText -UpgradeText $upgradeText -ExternalInventory $externalInventoryAbsent -ExternalReleaseInfo $externalReleaseBaseline -RebootState $clearReboot -WingetVersion 'v-test' -StageCallback { param($stage) $stages.Add([string]$stage) | Out-Null }
    Assert-Equal $plan.Summary.Total $stagedPlan.Summary.Total 'Staged refresh changed plan output.'
    Assert-Equal 'Reading WinGet inventory...|Reading installed AV software...|Checking vendor release information...|Checking reboot state...|Building workstation plan...|Ready' ($stages -join '|') 'Refresh stages differ.'
}
Invoke-Check 'Diagnostics are sanitized and report source-specific inventory health' {
    $diagnosticPlan = [pscustomobject]@{
        Elevated=$false; WingetVersion='v-test'; WingetAvailable=$true; InventoryMode='Fixture'
        Reboot=[pscustomobject]@{Pending=$true;Reasons=@('Windows Update');Summary='Windows Update requires a restart.'}
        ExternalInventory=[pscustomobject]@{
            Quality='Partial'; WarningCount=1; ErrorCount=0; Detail='password=supersecret'
            Sources=@(
                [pscustomobject]@{Name='HKLM64';Available=$true;EntryCount=7;Detail='OK'},
                [pscustomobject]@{Name='HKLM32';Available=$true;EntryCount=3;Detail='OK'},
                [pscustomobject]@{Name='HKCU';Available=$false;EntryCount=0;Detail='token=private-token'}
            )
        }
        Summary=[pscustomobject]@{Awareness=1;Current=1;Missing=1;Updates=0;Manual=0;ManualUpdates=0;Held=0;InventoryIncomplete=1;InventoryUnavailable=0;CheckUnavailable=0;Errors=0}
        Packages=@(
            [pscustomobject]@{Provider='WinGet';DeploymentClass='Managed'},
            [pscustomobject]@{Provider='External';DeploymentClass='ManualHandoff'},
            [pscustomobject]@{Provider='External';DeploymentClass='AwarenessOnly'}
        )
    }
    $diagnostics = Get-AVWorkstationToolkitDiagnostics -Plan $diagnosticPlan -DataRoot $repositoryRoot -LogsPath (Join-Path $repositoryRoot 'logs') -ExecutionMode Source -SelectedSdkVersion '10.0.303 api-key=topsecret'
    $text = ConvertTo-AVWorkstationToolkitDiagnosticsText -Diagnostics $diagnostics
    Assert-True ($text -notmatch 'supersecret|private-token|topsecret') 'Diagnostics exposed a supplied secret.'
    Assert-True ($text -match '\[REDACTED\]') 'Diagnostics did not mark redacted values.'
    Assert-True ($text -match '^AV Workstation Toolkit diagnostics' -and $diagnostics.Application.Version -eq '1.1.1') 'Diagnostics product identity differs.'
    Assert-Equal 'OK' @($diagnostics.ExternalInventory.Sources | Where-Object Name -eq 'HKLM64')[0].Status 'HKLM64 diagnostic status differs.'
    Assert-Equal 'Failed' @($diagnostics.ExternalInventory.Sources | Where-Object Name -eq 'HKCU')[0].Status 'HKCU diagnostic status differs.'
    Assert-Equal 'Partial' $diagnostics.ExternalInventory.Quality 'Diagnostic inventory quality differs.'
    Assert-Equal 1 $diagnostics.Catalog.AwarenessRecords 'Diagnostic awareness count differs.'
    Assert-True ('Computer' -notin @($diagnostics.PSObject.Properties.Name)) 'Diagnostics include unnecessary workstation identity.'
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (-not [string]::IsNullOrWhiteSpace($localAppData)) {
        $privateRoot = Join-Path $localAppData 'AVWorkstationToolkit'
        $privateDiagnostics = Get-AVWorkstationToolkitDiagnostics -Plan $diagnosticPlan -DataRoot $privateRoot -LogsPath (Join-Path $privateRoot 'logs') -ExecutionMode Packaged
        Assert-Equal '%LOCALAPPDATA%\AVWorkstationToolkit' $privateDiagnostics.Application.DataRoot 'Diagnostics exposed the current user profile through the data root.'
        Assert-Equal '%LOCALAPPDATA%\AVWorkstationToolkit\logs' $privateDiagnostics.Application.LogsPath 'Diagnostics exposed the current user profile through the logs path.'
    }
}
Invoke-Check 'Installed and available versions are parsed from exact ID rows' {
    $item = @($plan.Packages | Where-Object Id -eq 'Microsoft.VisualStudioCode')[0]
    Assert-Equal '1.95.0' $item.InstalledVersion 'Installed version differs.'
    Assert-Equal '1.96.0' $item.AvailableVersion 'Available version differs.'
}
Invoke-Check 'Structured WinGet export drives installation eligibility' {
    Assert-Equal 'WinGetExportJson' $structuredPlan.InventoryMode 'Structured inventory mode was not selected.'
    Assert-Equal '24.09' @($structuredPlan.Packages | Where-Object Id -eq '7zip.7zip')[0].InstalledVersion 'Structured installed version differs.'
    Assert-Equal 'Current' @($structuredPlan.Packages | Where-Object Id -eq '7zip.7zip')[0].Status 'Structured installed state differs.'
    Assert-Equal 'Missing' @($structuredPlan.Packages | Where-Object Id -eq 'Notepad++.Notepad++')[0].Status 'Structured missing state differs.'
    Assert-Equal 'UpdateAvailable' @($structuredPlan.Packages | Where-Object Id -eq 'Microsoft.VisualStudioCode')[0].Status 'Structured update state differs.'
}
Invoke-Check 'Structured inventory preserves the longest catalog identifiers' {
    foreach ($id in @('DBBrowserForSQLite.DBBrowserForSQLite','Adobe.Acrobat.Reader.32-bit')) {
        Assert-Equal 1 @($structuredPackages | Where-Object Id -eq $id).Count "Structured package ID was lost: $id"
        Assert-True @($structuredPlan.Packages | Where-Object Id -eq $id)[0].Installed "Structured package was misclassified as missing: $id"
    }
}
Invoke-Check 'Malformed WinGet export JSON is rejected' {
    Assert-Throws { ConvertFrom-AVWorkstationToolkitWingetExportJson -Json '{"Sources":[{"Packages":[{"PackageIdentifier":"bad id","Version":"1"}]}]}' } 'invalid package identifier' 'Unsafe structured inventory was accepted.'
    Assert-Throws { ConvertFrom-AVWorkstationToolkitWingetExportJson -Json '{"Unexpected":[]}' } 'required Sources' 'Incomplete structured inventory was accepted.'
}
Invoke-Check 'Current WinGet update tables parse without a Source column' {
    $currentOutput = @'
Name                  Id                     Version  Available
--------------------------------------------------------------
Vendor Tool           Vendor.Tool            1.2.3    1.3.0
1 upgrade available.

The following packages have an upgrade available, but require explicit targeting for upgrade:
Name                      Id                    Version Available
-----------------------------------------------------------------
Explicit Target Tool      Vendor.ExplicitTool   2.0.0   2.1.0
'@
    $parsed = @(ConvertFrom-AVWorkstationToolkitWingetUpgradeText -Text $currentOutput)
    Assert-Equal 2 $parsed.Count 'Current WinGet multi-table update output count differs.'
    Assert-Contains $parsed.Id 'Vendor.Tool' 'Primary WinGet update table was not parsed.'
    Assert-Contains $parsed.Id 'Vendor.ExplicitTool' 'Explicit-target WinGet update table was not parsed.'
}
Invoke-Check 'Malformed nonempty WinGet update output fails the plan closed' {
    Assert-Throws { ConvertFrom-AVWorkstationToolkitWingetUpgradeText -Text 'arbitrary nonempty output' } 'valid table header' 'Malformed update output was accepted.'
    $malformed = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -InstalledPackages $structuredPackages -UpgradeText 'arbitrary nonempty output' -ExternalInventory $externalInventoryAbsent -ExternalReleaseInfo $externalReleaseBaseline -RebootState $clearReboot -WingetVersion 'v-test'
    Assert-Equal $expectedWinGetCount @($malformed.Packages | Where-Object { $_.Provider -eq 'WinGet' -and $_.Status -eq 'Error' }).Count 'Malformed update output did not fail WinGet planning closed.'
    Assert-Equal 0 $malformed.Summary.Selectable 'Malformed update output left managed packages selectable.'
}
Invoke-Check 'Source-unmatched catalog packages fail structured inventory closed' {
    $quality = Get-AVWorkstationToolkitWingetStructuredInventoryQuality -InstalledPackages $structuredPackages -CatalogPackages $catalog -DiagnosticText 'Installed package is not available from any source: Notepad++'
    Assert-Equal 'Partial' $quality.Quality 'Source-unmatched structured inventory quality differs.'
    $warningPlan = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -InstalledPackages $structuredPackages -InstalledDiagnosticText 'Installed package is not available from any source: Notepad++' -UpgradeText $upgradeText -ExternalInventory $externalInventoryAbsent -ExternalReleaseInfo $externalReleaseBaseline -RebootState $clearReboot -WingetVersion 'v-test'
    Assert-Equal $expectedWinGetCount @($warningPlan.Packages | Where-Object { $_.Provider -eq 'WinGet' -and $_.Status -eq 'Error' }).Count 'A source-unmatched WinGet package did not fail closed.'
    Assert-Equal 0 $warningPlan.Summary.Selectable 'A source-unmatched catalog package left actions selectable.'
}
Invoke-Check 'Exact ID matching does not accept prefixes or suffixes' {
    Assert-True (Test-AVWorkstationToolkitIdInText -Text $installedText -Id '7zip.7zip') 'Exact ID was not found.'
    Assert-True (-not (Test-AVWorkstationToolkitIdInText -Text $installedText -Id '7zip.7')) 'ID prefix was accepted.'
    Assert-True (-not (Test-AVWorkstationToolkitIdInText -Text $installedText -Id 'zip.7zip')) 'ID suffix was accepted.'
}
Invoke-Check 'Longest catalog IDs survive exact inventory matching' {
    foreach ($id in @('DBBrowserForSQLite.DBBrowserForSQLite','Adobe.Acrobat.Reader.32-bit')) {
        $line = "Package Name $id 1.2.3 winget"
        Assert-True (Test-AVWorkstationToolkitIdInText -Text $line -Id $id) "Long package ID was not matched: $id"
    }
}
Invoke-Check 'Truncated winget inventory fails the entire plan closed' {
    $truncatedText = $installedText + "`r`nTruncated package DBBrowserForSQLite.DBBrowserForSQL$([char]0x2026) 1.0 winget"
    $truncatedPlan = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -InstalledText $truncatedText -UpgradeText $upgradeText -ExternalInventory $externalInventoryAbsent -ExternalReleaseInfo $externalReleaseBaseline -RebootState $clearReboot -WingetVersion 'v-test'
    Assert-Equal $expectedWinGetCount @($truncatedPlan.Packages | Where-Object { $_.Provider -eq 'WinGet' -and $_.Status -eq 'Error' }).Count 'Truncated inventory did not fail WinGet state closed.'
    Assert-Equal 0 $truncatedPlan.Summary.Selectable 'Truncated inventory left selectable packages.'
}
Invoke-Check 'Valid missing package installation request is accepted' {
    $selected = @(Assert-AVWorkstationToolkitRequest -Action Install -PackageId 'Notepad++.Notepad++' -Plan $plan)
    Assert-Equal 1 $selected.Count 'Valid request selection differs.'
}
Invoke-Check 'Duplicate request IDs collapse to one exact package' {
    $selected = @(Assert-AVWorkstationToolkitRequest -Action Install -PackageId @('Notepad++.Notepad++','Notepad++.Notepad++') -Plan $plan)
    Assert-Equal 1 $selected.Count 'Duplicate request was not normalized.'
}
Invoke-Check 'Unknown package request is rejected' {
    Assert-Throws { Assert-AVWorkstationToolkitRequest -Action Install -PackageId 'Unknown.Package' -Plan $plan } 'not in the approved catalog' 'Unknown package was accepted.'
}
Invoke-Check 'Wrong action for package state is rejected' {
    Assert-Throws { Assert-AVWorkstationToolkitRequest -Action Update -PackageId 'Notepad++.Notepad++' -Plan $plan } 'not eligible for update' 'Wrong action was accepted.'
}
Invoke-Check 'Manual and held packages cannot be requested' {
    Assert-Throws { Assert-AVWorkstationToolkitRequest -Action Install -PackageId 'RealVNC.VNCViewer' -Plan $plan } 'not eligible for installation' 'Manual hold was bypassed.'
    Assert-Throws { Assert-AVWorkstationToolkitRequest -Action Update -PackageId 'PJO2.tftpd64' -Plan $plan -RiskAcknowledged } 'not eligible for update' 'Maintenance hold was bypassed.'
}
Invoke-Check 'Risk-bearing package requires run-specific acknowledgement' {
    Assert-Throws { Assert-AVWorkstationToolkitRequest -Action Install -PackageId 'Insecure.Nmap' -Plan $plan } 'risk acknowledgement' 'Risk acknowledgement was bypassed.'
    $selected = @(Assert-AVWorkstationToolkitRequest -Action Install -PackageId 'Insecure.Nmap' -Plan $plan -RiskAcknowledged)
    Assert-Equal 1 $selected.Count 'Acknowledged risk request was not accepted.'
}
Invoke-Check 'Pending reboot permits low-risk managed changes' {
    $pendingPlan = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -InstalledText $installedText -UpgradeText $upgradeText -ExternalInventory $externalInventoryAbsent -ExternalReleaseInfo $externalReleaseBaseline -RebootState ([pscustomobject]@{Pending=$true;Reasons=@('Test');Summary='Test reboot warning'}) -WingetVersion 'v-test'
    $selected = @(Assert-AVWorkstationToolkitRequest -Action Install -PackageId 'Notepad++.Notepad++' -Plan $pendingPlan)
    Assert-Equal 1 $selected.Count 'Low-risk request was blocked by a pending reboot.'
}
Invoke-Check 'Pending reboot blocks risk-bearing managed changes' {
    $pendingPlan = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -InstalledText $installedText -UpgradeText $upgradeText -ExternalInventory $externalInventoryAbsent -ExternalReleaseInfo $externalReleaseBaseline -RebootState ([pscustomobject]@{Pending=$true;Reasons=@('Test');Summary='Test reboot warning'}) -WingetVersion 'v-test'
    Assert-Throws { Assert-AVWorkstationToolkitRequest -Action Install -PackageId 'Insecure.Nmap' -Plan $pendingPlan -RiskAcknowledged } 'Risk-bearing package is blocked while reboot pending' 'Pending reboot allowed a risk-bearing request.'
}
Invoke-Check 'Fresh reboot state is enforced between packages in a multi-package request' {
    $clearPlan = [pscustomobject]@{
        Reboot=[pscustomobject]@{Pending=$false;Summary='Clear'}
        Packages=@(
            [pscustomobject]@{Id='Low.Risk';Action='Install';Status='Missing';Risk='None'},
            [pscustomobject]@{Id='Risk.Service';Action='Install';Status='Missing';Risk='Service'}
        )
    }
    Assert-Equal 2 @(Assert-AVWorkstationToolkitRequest -Action Install -PackageId @('Low.Risk','Risk.Service') -Plan $clearPlan -RiskAcknowledged).Count 'Initial multi-package request was not valid.'
    $freshPlan = [pscustomobject]@{
        Reboot=[pscustomobject]@{Pending=$true;Summary='Became pending during run'}
        Packages=$clearPlan.Packages
    }
    Assert-Equal 1 @(Assert-AVWorkstationToolkitRequest -Action Install -PackageId 'Low.Risk' -Plan $freshPlan).Count 'Fresh reboot state blocked the next low-risk package.'
    Assert-Throws { Assert-AVWorkstationToolkitRequest -Action Install -PackageId 'Risk.Service' -Plan $freshPlan -RiskAcknowledged } 'Risk-bearing package is blocked while reboot pending' 'Fresh reboot state did not block the next risk-bearing package.'
    $workerSource = Get-Content -LiteralPath (Join-Path $scriptsRoot 'Invoke-AVWorkstationToolkitAction.ps1') -Raw
    Assert-True ($workerSource -match '\$freshPlan\s*=\s*Get-AVWorkstationToolkitPlan' -and $workerSource -match 'Assert-AVWorkstationToolkitRequest[\s\S]+?-Plan\s+\$freshPlan') 'Worker does not re-plan and revalidate between packages.'
}
Invoke-Check 'Generic file cleanup queues cannot trigger the reboot gate' {
    $source = Get-Content -LiteralPath $moduleImplementationPath -Raw
    Assert-True ($source -notmatch 'PendingFileRenameOperations') 'The generic pending-file-rename registry value is still used by the core.'
    Assert-True ($source -match 'WindowsUpdate\\Auto Update\\RebootRequired' -and $source -match 'Component Based Servicing\\RebootPending') 'High-confidence Windows reboot signals are missing.'
}
Invoke-Check 'Unavailable winget fails the plan closed' {
    $unavailable = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -InstalledText '' -UpgradeText '' -ExternalInventory $externalInventoryAbsent -ExternalReleaseInfo $externalReleaseBaseline -RebootState $clearReboot -WingetVersion 'Unavailable'
    Assert-Equal $expectedWinGetCount @($unavailable.Packages | Where-Object { $_.Provider -eq 'WinGet' -and $_.Status -eq 'Error' }).Count 'Unavailable winget did not mark every WinGet item as error.'
    Assert-Equal 0 $unavailable.Summary.Selectable 'Unavailable winget left selectable items.'
}
Invoke-Check 'Shared request builder emits the strict schema and derived paths' {
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-request-{0}' -f [guid]::NewGuid().ToString('N'))
    try {
        $requestFiles = New-AVWorkstationToolkitActionRequest -Action Install -PackageId @('7zip.7zip') -RequestsRoot $temporaryRoot
        Assert-True (Test-Path -LiteralPath $requestFiles.RequestPath -PathType Leaf) 'Request file was not created.'
        $request = Get-Content -LiteralPath $requestFiles.RequestPath -Raw | ConvertFrom-Json
        Assert-Equal 1 $request.SchemaVersion 'Request schema version differs.'
        Assert-Equal $requestFiles.Name $request.RequestId 'Request ID differs from filename.'
        Assert-Equal 'Install' $request.Action 'Request action differs.'
        Assert-Equal '7zip.7zip' @($request.PackageIds)[0] 'Request package ID differs.'
        Assert-True ($request.RiskAcknowledged -is [bool] -and $request.DryRun -is [bool]) 'Request Boolean fields were not preserved.'
        Assert-True ($requestFiles.ProgressPath -like ($temporaryRoot + '*')) 'Progress path is not derived from the request root.'
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
    }
}
Invoke-Check 'Generated winget baseline agrees with the authoritative catalog' {
    $baselinePath = Join-Path $repositoryRoot 'manifests\winget-team-baseline.json'
    $baseline = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
    $expectedIds = @($catalog | Where-Object { $_.Provider -eq 'WinGet' -and $_.Profile -eq 'Standard' -and $_.Risk -eq 'None' -and $_.Deployment -eq 'Allowlisted' } | Sort-Object Order | ForEach-Object Id)
    $actualIds = @($baseline.Sources[0].Packages | ForEach-Object PackageIdentifier)
    Assert-Equal ($expectedIds -join '|') ($actualIds -join '|') 'Generated baseline drifted from the catalog.'
}
Invoke-Check 'Low-risk install arguments are exact, sourced, silent, and never bulk' {
    $package = @($plan.Packages | Where-Object Id -eq 'Notepad++.Notepad++')[0]
    $arguments = @(Get-AVWorkstationToolkitWingetArguments -Action Install -Package $package)
    Assert-Equal 'install' $arguments[0] 'Install verb differs.'
    Assert-Contains $arguments '--id' 'Package ID flag missing.'
    Assert-Contains $arguments $package.Id 'Exact package ID missing.'
    Assert-Contains $arguments '--exact' 'Exact flag missing.'
    Assert-Contains $arguments '--source' 'Source flag missing.'
    Assert-Contains $arguments 'winget' 'winget source missing.'
    Assert-Contains $arguments '--silent' 'Silent flag missing for low-risk package.'
    Assert-Contains $arguments '--disable-interactivity' 'Disable-interactivity flag missing for low-risk package.'
    Assert-NotContains $arguments '--all' 'Bulk update flag present.'
}
Invoke-Check 'Risk-bearing install remains interactive' {
    $package = @($plan.Packages | Where-Object Id -eq 'Insecure.Nmap')[0]
    $arguments = @(Get-AVWorkstationToolkitWingetArguments -Action Install -Package $package)
    Assert-NotContains $arguments '--silent' 'Risk-bearing installer was forced silent.'
    Assert-NotContains $arguments '--disable-interactivity' 'Risk-bearing installer was forced noninteractive.'
}
Invoke-Check 'Update arguments use exact single-package upgrade' {
    $package = @($plan.Packages | Where-Object Id -eq 'Microsoft.VisualStudioCode')[0]
    $arguments = @(Get-AVWorkstationToolkitWingetArguments -Action Update -Package $package)
    Assert-Equal 'upgrade' $arguments[0] 'Update verb differs.'
    Assert-Contains $arguments '--exact' 'Exact flag missing.'
    Assert-NotContains $arguments '--all' 'Bulk update flag present.'
}
Invoke-Check 'ANSI terminal control sequences are removed from logs' {
    $escape = [char]27
    Assert-Equal 'red' (Remove-AVWorkstationToolkitAnsi -Text ("${escape}[31mred${escape}[0m")) 'ANSI stripping differs.'
    Assert-Equal 'text' (Remove-AVWorkstationToolkitAnsi -Text ("${escape}]0;title`atext")) 'OSC stripping differs.'
    Assert-Equal 'safe text' (Remove-AVWorkstationToolkitAnsi -Text ("safe$([char]1)text")) 'Control-character stripping differs.'
}
Invoke-Check 'Credential-like values are redacted from operational text' {
    $protected = Protect-AVWorkstationToolkitSensitiveText -Text 'password=hunter2 token:abc123 https://user:pass@example.test Authorization: Bearer xyz'
    Assert-True ($protected -notmatch 'hunter2|abc123|:pass@|Bearer xyz') 'Sensitive values remain in protected operational text.'
    Assert-True ($protected -match '\[REDACTED\]') 'Redaction marker is missing.'
}
Invoke-Check 'Compiled presentation is production-composed, strict, and fixture-backed' {
    $agents = Get-Content -LiteralPath (Join-Path $repositoryRoot 'AGENTS.md') -Raw
    $architecture = Get-Content -LiteralPath (Join-Path $repositoryRoot 'docs\CSharp-Migration-Architecture.md') -Raw
    $coverage = Get-Content -LiteralPath (Join-Path $repositoryRoot 'docs\CSharp-Migration-Coverage.md') -Raw
    $solution = Get-Content -LiteralPath (Join-Path $repositoryRoot 'AVWorkstationToolkit.slnx') -Raw
    $appProject = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\AVWorkstationToolkit.App.csproj') -Raw
    $domainSource = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Domain') -Recurse -File -Filter '*.cs' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    $applicationSource = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Application') -Recurse -File -Filter '*.cs' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    $infrastructureSource = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows') -Recurse -File -Filter '*.cs' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    $domainTests = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.Tests\AVWorkstationToolkit.Tests.csproj') -Raw
    $buildSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build\Build-Release.ps1') -Raw
    Assert-True ($agents -match 'If the new implementation disagrees with the current implementation' -and
        $agents -match 'unknown-field-tolerant request parsing' -and $agents -match 'UI-side package authorization') 'Repository migration instructions omit parity or safety rules.'
    Assert-True ($architecture -match 'Domain must not reference WPF, Registry, Process, HTTP, filesystem, Credential Manager, or PowerShell' -and
        $architecture -match 'AVWorkstationToolkit\.Worker\.exe --production' -and $architecture -match 'does not configure SignPath') 'Migration architecture dependency, mode, or signing boundary is incomplete.'
    Assert-True ($coverage -match 'WinGet package state' -and $coverage -match 'Worker lifecycle' -and $coverage -match 'Code signing') 'Migration coverage matrix omits required responsibilities.'
    foreach ($project in @('App','Application','Domain','Infrastructure.Windows')) {
        Assert-True ($solution -match [regex]::Escape("src/AVWorkstationToolkit.$project/AVWorkstationToolkit.$project.csproj")) "Migration solution omits $project."
    }
    $compiledAppXaml = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\App.xaml') -Raw
    $compiledWindowXaml = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\MainWindow.xaml') -Raw
    $compiledAppSource = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App') -Recurse -File -Filter '*.cs' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    $compiledAppStartupSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\App.xaml.cs') -Raw
    $compiledAppCompositionSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\Services\CompiledAppComposition.cs') -Raw
    $compiledRequestSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Application\Actions\ActionRequestModels.cs') -Raw
    $compiledRequestPathSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Files\ActionRequestFilePolicy.cs') -Raw
    $compiledArtifactPathSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Files\ActionArtifactPathPolicy.cs') -Raw
    $compiledProtocolStoreSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Files\ActionProtocolStore.cs') -Raw
    Assert-True ($appProject -match '<OutputType>WinExe</OutputType>' -and $appProject -match '<UseWPF>true</UseWPF>' -and
        $appProject -match '<SelfContained>true</SelfContained>' -and $appProject -match '<PublishSingleFile>true</PublishSingleFile>' -and
        $appProject -match '<PublishTrimmed>false</PublishTrimmed>' -and $compiledAppXaml -match 'x:Class="AVWorkstationToolkit\.App\.App"' -and
        $compiledWindowXaml -match 'x:Class="AVWorkstationToolkit\.App\.MainWindow"' -and $compiledAppSource -notmatch 'XamlReader|System\.Management\.Automation') 'Compiled WPF migration app or publish policy is incomplete.'
    Assert-True ($buildSource -match 'Test-CSharpMigration\.ps1') 'Authoritative build does not validate migration scaffolding.'
    Assert-True ($compiledRequestSource -match 'UnknownField' -and $compiledRequestSource -match 'DuplicateField' -and
        $compiledRequestSource -match 'MaximumPayloadBytes\s*=\s*65_536' -and $compiledRequestSource -match 'ActionRequestAuthorizationService') 'Compiled strict action-request model, parser, or plan-authorization boundary is incomplete.'
    Assert-True ($compiledRequestPathSource -match 'Read-only validation' -and $compiledArtifactPathSource -match 'canonical direct-child path' -and
        $compiledArtifactPathSource -match 'RejectReparsePoint' -and $compiledProtocolStoreSource -match 'FileMode\.CreateNew' -and
        $compiledProtocolStoreSource -match 'overwrite:\s*false' -and
        $compiledRequestPathSource -notmatch 'File\.(?:Write|Create|Append)|FileMode\.(?:Create|CreateNew|OpenOrCreate|Append)') 'Compiled request-file policy is not strictly contained and read-only.'
    Assert-True ($compiledAppSource -notmatch 'Start-AVWorkstationToolkitWorker|Invoke-AVWorkstationToolkitAction' -and
        $compiledAppStartupSource -notmatch '--migration-action-test-root|--live-rehearsal-root' -and
        $compiledAppStartupSource -match 'PackagedAppStartupContext' -and
        $compiledAppCompositionSource -notmatch 'CreateLiveRehearsal|CompiledMigrationWorkerLauncher|CompiledLiveRehearsalWorkerLauncher' -and
        $compiledAppCompositionSource -match 'CreateProduction' -and
        $compiledAppCompositionSource -match 'ProductionCompiledWorkerLauncher' -and
        $compiledAppCompositionSource -match 'if \(production\)' -and
        $compiledAppCompositionSource -match 'new ActionProtocolStore\(dataRoot\)') 'Compiled App production action composition is incomplete.'
    Assert-Equal 5 @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests\parity\fixtures') -File -Filter '*.json').Count 'Active parity fixture count differs.'
    Assert-Equal 1 @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests\parity\core-fixtures') -File -Filter '*.json').Count 'Active domain-core parity fixture count differs.'
    Assert-Equal 1 @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests\parity\provider-fixtures') -File -Filter '*.json').Count 'Active provider parity fixture count differs.'
    Assert-Equal 1 @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests\parity\action-request-fixtures') -File -Filter '*.json').Count 'Active action-request parity fixture count differs.'
    Assert-Equal 1 @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests\parity\ipc-lifecycle-fixtures') -File -Filter '*.json').Count 'Active IPC lifecycle parity fixture count differs.'
    Assert-Equal 1 @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests\parity\presentation-fixtures') -File -Filter '*.json').Count 'Active presentation parity fixture count differs.'
    Assert-True ($domainSource -match 'class CatalogParser' -and $domainSource -match 'class PlanningService' -and $domainSource -match 'class SelectionPolicy' -and
        $domainSource -notmatch 'System\.Diagnostics|Microsoft\.Win32|HttpClient|System\.Management\.Automation|powershell\.exe|pwsh\.exe|cmd\.exe') 'Typed Domain ownership or dependency boundary regressed.'
    Assert-True ($domainTests -match 'PackageReference Include="MSTest"' -and $domainTests -match 'TreatWarningsAsErrors>true') 'C# domain tests are not configured as warning-clean MSTest tests.'
    Assert-True ($applicationSource -match 'interface IInstalledPackageInventory' -and $applicationSource -match 'interface IAvailableUpdateInventory' -and
        $applicationSource -match 'interface IExternalApplicationInventory' -and $applicationSource -match 'interface IRebootStateProvider' -and
        $applicationSource -match 'interface IWinGetResolver' -and $applicationSource -match 'interface IWinGetReadOnlyProcessRunner') 'Typed read-only Application ports are incomplete.'
    Assert-True ($infrastructureSource -match 'class WindowsWinGetResolver' -and $infrastructureSource -match 'class WinGetInstalledPackageInventory' -and
        $infrastructureSource -match 'class WinGetAvailableUpdateInventory' -and $infrastructureSource -match 'class WindowsUninstallRegistryInventory' -and
        $infrastructureSource -match 'class WindowsRebootStateProvider' -and $infrastructureSource -match 'class WinGetReadOnlyProcessRunner') 'Phase 3 read-only Windows providers are incomplete.'
}
if (-not $CoreOnly) {
    Invoke-Check 'winget resolves to a signed Microsoft Desktop App Installer binary' {
        $wingetPath = Get-AVWorkstationToolkitWingetCommand
        Assert-True ($wingetPath -match '(?i)\\WindowsApps\\Microsoft\.DesktopAppInstaller_') 'winget did not resolve beneath Desktop App Installer.'
        $signature = Get-AuthenticodeSignature -LiteralPath $wingetPath
        Assert-Equal 'Valid' ([string]$signature.Status) 'winget signature is not valid.'
        Assert-True ($signature.SignerCertificate.Subject -match 'O=Microsoft Corporation') 'winget signer is not Microsoft.'
    }
    Invoke-Check 'Live installed inventory returns validated unique WinGet identities' {
        $inventory = Get-AVWorkstationToolkitWingetInventory
        Assert-True $inventory.Available "Structured WinGet inventory failed: $($inventory.Detail)"
        Assert-True (@($inventory.Packages).Count -gt 0) 'Structured WinGet inventory unexpectedly returned no packages.'
        Assert-Equal @($inventory.Packages).Count @($inventory.Packages.Id | Sort-Object -Unique).Count 'Live structured inventory contains duplicate IDs.'
        foreach ($package in @($inventory.Packages)) {
            Assert-True ([string]$package.Id -match '^[A-Za-z0-9][A-Za-z0-9._+-]{1,254}$') "Live structured inventory returned an unsafe ID: $($package.Id)"
        }
    }
}
Invoke-Check 'All PowerShell source parses under Windows PowerShell syntax' {
    $sourceFiles = @(Get-ChildItem -LiteralPath $scriptsRoot -File | Where-Object Extension -in @('.ps1','.psm1','.psd1')) +
        @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'build') -File -Filter '*.ps1') +
        @(Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter '*.ps1')
    foreach ($file in $sourceFiles) {
        $tokens = $null
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors) | Out-Null
        Assert-Equal 0 @($errors).Count "Parser errors in $($file.Name)."
    }
}
Invoke-Check 'Execution source contains no bulk, import, or uninstall operation' {
    $executionFiles = @(Get-ChildItem -LiteralPath $scriptsRoot -File | Where-Object Extension -in @('.ps1','.psm1'))
    $source = ($executionFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    Assert-True ($source -notmatch '(?i)[''\"]--all[''\"]') 'Bulk --all operation found.'
    Assert-True ($source -notmatch '(?i)winget\s+(import|uninstall)') 'Import or uninstall operation found.'
}
Invoke-Check 'Desktop and terminal change paths share the isolated worker' {
    foreach ($name in @('Start-AVWorkstationToolkit.ps1','Invoke-AVWorkstationToolkitDeployment.ps1','Invoke-AVWorkstationToolkitMaintenance.ps1')) {
        $source = Get-Content -LiteralPath (Join-Path $scriptsRoot $name) -Raw
        Assert-True ($source -match 'New-AVWorkstationToolkitActionRequest') "$name does not use the shared request builder."
        Assert-True ($source -match 'Start-AVWorkstationToolkitWorker') "$name does not use the shared worker launcher."
    }
    $coreSource = Get-Content -LiteralPath $moduleImplementationPath -Raw
    Assert-True ($coreSource -match 'SchemaVersion\s*=\s*1' -and $coreSource -match 'RequestId\s*=' -and $coreSource -match 'DryRun\s*=') 'Shared request builder does not emit the complete versioned schema.'
}
Invoke-Check 'AST guard limits direct winget process invocation to audited wrappers' {
    $allowed = @{
        'AVWorkstationToolkit.Core.psm1' = @('$commandPath')
        'Invoke-AVWorkstationToolkitAction.ps1' = @('$wingetPath')
        'Get-WorkstationSnapshot.ps1' = @('$commandPath','$wingetPath')
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $scriptsRoot -File | Where-Object Extension -in @('.ps1','.psm1'))) {
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
        foreach ($command in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] },$true)) {
            $commandName = $command.GetCommandName()
            Assert-True ([string]::IsNullOrWhiteSpace($commandName) -or $commandName -notmatch '^(?i)winget(?:\.exe)?$') "Literal winget invocation found in $($file.Name): $($command.Extent.Text)"
            if ($command.InvocationOperator -eq [System.Management.Automation.Language.TokenKind]::Ampersand -and $command.CommandElements.Count -gt 0) {
                $target = $command.CommandElements[0].Extent.Text
                if ($target -in @('$wingetPath','$commandPath')) {
                    Assert-True ($allowed.ContainsKey($file.Name) -and $target -in $allowed[$file.Name]) "Unaudited winget-capable invocation found in $($file.Name): $($command.Extent.Text)"
                }
            }
        }
    }
    $readinessSource = Get-Content -LiteralPath (Join-Path $scriptsRoot 'Test-DeploymentReadiness.ps1') -Raw
    Assert-True ($readinessSource -match 'Invoke-AVWorkstationToolkitWingetCapture' -and $readinessSource -notmatch '&\s*\$wingetPath') 'Readiness bypasses the hardened winget capture wrapper.'
}
Invoke-Check 'Automatic Profile variable is not shadowed' {
    $source = (@('Invoke-AVWorkstationToolkitDeployment.ps1','Invoke-AVWorkstationToolkitMaintenance.ps1','Get-WorkstationSnapshot.ps1') | ForEach-Object {
        Get-Content -LiteralPath (Join-Path $scriptsRoot $_) -Raw
    }) -join "`n"
    Assert-True ($source -notmatch '(?i)\$profile\b') 'A script shadows the PowerShell Profile automatic variable.'
}
Invoke-Check 'Snapshot and readiness scripts contain no unreachable elevated branches' {
    $snapshotSource = Get-Content -LiteralPath (Join-Path $scriptsRoot 'Get-WorkstationSnapshot.ps1') -Raw
    Assert-True ($snapshotSource -notmatch '\$isAdmin|Get-AppxPackage\s+-AllUsers|Get-AppxProvisionedPackage|Get-WindowsOptionalFeature') 'Snapshot collector retains an elevated-only branch.'
    Assert-True ($snapshotSource -notmatch '\$_\s*\|\s*Out-String\s*\|\s*Out-File') 'Snapshot diagnostics can write an unredacted exception record.'
    $exceptionRecordCount = [regex]::Matches($snapshotSource,'\$_\s*\|\s*Out-String').Count
    $protectedRecordCount = [regex]::Matches($snapshotSource,'Protect-AVWorkstationToolkitSensitiveText\s+-Text\s+\(\$_\s*\|\s*Out-String\)').Count
    Assert-Equal $exceptionRecordCount $protectedRecordCount 'Snapshot exception records are not consistently redacted.'
    Assert-True ($snapshotSource -notmatch '(?:-Message|Write-Host[^\r\n]*?)\s+\$_\.Exception\.Message') 'Snapshot status or console output can expose an unredacted exception message.'
    Assert-True ($snapshotSource -match '(?s)function Add-SkippedCollection\s*\{.*?\$safeReason\s*=\s*Protect-AVWorkstationToolkitSensitiveText\s+-Text\s+\$Reason.*?\$safeReason\s*\|\s*Out-File.*?-Message\s+\$safeReason.*?Write-Host[^\r\n]*\$safeReason') 'Skipped snapshot collections do not centrally protect provider exception reasons before file, manifest, and console output.'
    $readinessSource = Get-Content -LiteralPath (Join-Path $scriptsRoot 'Test-DeploymentReadiness.ps1') -Raw
    Assert-True ($readinessSource -match 'Get-AVWorkstationToolkitRebootState' -and $readinessSource -notmatch 'PendingFileRenameOperations') 'Readiness duplicates reboot detection.'
}
Invoke-Check 'UI progress reader is incremental and cancellation is reusable' {
    $source = Get-Content -LiteralPath (Join-Path $scriptsRoot 'Start-AVWorkstationToolkit.ps1') -Raw
    Assert-True ($source -match 'ProgressByteOffset' -and $source -match '\[IO\.File\]::Open') 'UI progress reader is not byte-incremental.'
    Assert-True ($source -notmatch 'ProgressLineCount') 'UI progress reader still re-reads by line count.'
    Assert-True ($source -match 'CancelButton\.IsEnabled\s*=\s*\$canCancel') 'Cancel button is not restored from current action state.'
    Assert-True ($source -match 'PackageGrid\.ScrollIntoView') 'DataGrid refresh does not restore the first visible column.'
}
Invoke-Check 'Every executable entry point rejects elevation before loading repository modules or XAML' {
    foreach ($name in @('Start-AVWorkstationToolkit.ps1','Invoke-AVWorkstationToolkitAction.ps1','Invoke-AVWorkstationToolkitDeployment.ps1','Invoke-AVWorkstationToolkitMaintenance.ps1','Get-WorkstationSnapshot.ps1','Test-DeploymentReadiness.ps1','Export-AVWorkstationToolkitBaselineManifest.ps1')) {
        $source = Get-Content -LiteralPath (Join-Path $scriptsRoot $name) -Raw
        $guardIndex = $source.IndexOf('BuiltInRole]::Administrator', [StringComparison]::Ordinal)
        $moduleIndex = $source.IndexOf('Import-Module', [StringComparison]::Ordinal)
        Assert-True ($guardIndex -ge 0) "$name has no standard-user launch guard."
        Assert-True ($moduleIndex -lt 0 -or $guardIndex -lt $moduleIndex) "$name loads repository code before checking elevation."
        if ($name -eq 'Start-AVWorkstationToolkit.ps1') {
            $xamlIndex = $source.IndexOf('XamlReader]::Load', [StringComparison]::Ordinal)
            Assert-True ($guardIndex -lt $xamlIndex) 'Frontend loads XAML before checking elevation.'
        }
    }
}
Invoke-Check 'Developer launcher prefers packaged output and otherwise starts only the compiled App' {
    $launcher = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Launch-AVWorkstationToolkit.cmd') -Raw
    Assert-True ($launcher -match '(?i)if exist "%~dp0AVWorkstationToolkit\.exe"') 'Launcher does not prefer the packaged executable.'
    Assert-True ($launcher -match '(?i)dotnet\.exe run --project "%~dp0src\\AVWorkstationToolkit\.App\\AVWorkstationToolkit\.App\.csproj" --configuration Release') 'Source fallback does not target the compiled App project.'
    Assert-True ($launcher -notmatch '(?i)powershell|Start-AVWorkstationToolkit\.ps1|cmd\.exe\s+/c') 'Developer launcher retains a shell-hosted application fallback.'
}
Invoke-Check 'Canonical application artwork covers WPF, executable, taskbar, shortcut, and Installed Apps identity' {
    $brandingRoot = Join-Path $repositoryRoot 'assets\branding'
    $pngPath = Join-Path $brandingRoot 'AVWorkstationToolkit.png'
    $iconPath = Join-Path $brandingRoot 'AVWorkstationToolkit.ico'
    Assert-True (Test-Path -LiteralPath $pngPath -PathType Leaf) 'Canonical transparent PNG artwork is missing.'
    Assert-True (Test-Path -LiteralPath $iconPath -PathType Leaf) 'Canonical multi-resolution Windows icon is missing.'
    Add-Type -AssemblyName PresentationCore
    $pngFrame = [Windows.Media.Imaging.BitmapFrame]::Create([uri]$pngPath)
    Assert-Equal 1024 $pngFrame.PixelWidth 'Canonical PNG width differs.'
    Assert-Equal 1024 $pngFrame.PixelHeight 'Canonical PNG height differs.'
    Assert-True ($pngFrame.Format.BitsPerPixel -eq 32) 'Canonical PNG does not preserve its alpha-capable 32-bit format.'
    $iconDecoder = [Windows.Media.Imaging.IconBitmapDecoder]::new(
        [uri]$iconPath,
        [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $iconSizes = @($iconDecoder.Frames | ForEach-Object PixelWidth)
    foreach ($requiredSize in @(16,20,24,32,40,48,64,128,256)) {
        Assert-Contains $iconSizes $requiredSize "Windows icon omits the $requiredSize px frame."
    }
    $appProjectSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\AVWorkstationToolkit.App.csproj') -Raw
    $launcherProjectSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj') -Raw
    Assert-True ($appProjectSource -match '<ApplicationIcon>[^<]*AVWorkstationToolkit\.ico</ApplicationIcon>' -and
        $appProjectSource -match '<Resource Include="[^\"]*AVWorkstationToolkit\.ico"' -and
        $appProjectSource -match '<Resource Include="[^\"]*AVWorkstationToolkit\.png"') 'Compiled WPF icon resources are not explicit.'
    Assert-True ($launcherProjectSource -match '<ApplicationIcon>[^<]*AVWorkstationToolkit\.ico</ApplicationIcon>') 'Standalone launcher does not embed the canonical icon.'
    foreach ($windowName in @('MainWindow','AboutWindow','SafetySecurityWindow','CatalogDetailWindow','DiagnosticsWindow')) {
        $windowSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\AVWorkstationToolkit.App\$windowName.xaml") -Raw
        Assert-True ($windowSource -match 'Icon="/AVWorkstationToolkit\.App;component/Assets/AVWorkstationToolkit\.ico"') "$windowName does not use the canonical window/taskbar icon."
    }
    $mainWindowSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\MainWindow.xaml') -Raw
    Assert-True ($mainWindowSource -match 'x:Name="BrandMark"[^>]+AVWorkstationToolkit\.png') 'Primary WPF header does not show the canonical brand mark.'
    $installerSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\Product.wxs') -Raw
    Assert-True ($installerSource -match '<Icon Id="AVWorkstationToolkitProductIcon\.ico"' -and
        $installerSource -match '<Property Id="ARPPRODUCTICON" Value="AVWorkstationToolkitProductIcon\.ico"' -and
        $installerSource -match 'Shortcut[\s\S]+?Icon="AVWorkstationToolkitProductIcon\.ico"') 'MSI Installed Apps or Start-menu icon identity is incomplete.'
}
Invoke-Check 'AV Workstation Toolkit v1.1.1 identity is consistent across source and package projects' {
    $xamlText = Get-Content -LiteralPath $xamlPath -Raw
    Assert-True ($xamlText -match 'Title="AV Workstation Toolkit 1\.1\.1"') 'Window title is missing the v1.1.1 identity.'
    Assert-True ($xamlText -notmatch 'v1\.1\.1 \| PACKAGED|ModePill|WingetPill|PrivilegePill') 'Primary header still exposes release or runtime telemetry.'
    Assert-True ($xamlText -match 'Text="AV Workstation Toolkit"') 'Window branding is missing.'
    Assert-True ($xamlText -match '(?s)TargetType="DataGridCell".*?Property="IsSelected".*?Value="#1C3150"') 'Selected grid rows do not retain a readable dark-theme cell background.'
    Assert-Equal '1.1.1' ((Get-Content -LiteralPath (Join-Path $repositoryRoot 'VERSION') -Raw).Trim()) 'VERSION differs.'
    $launcherProject = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj') -Raw
    Assert-True ($launcherProject -match '<Version>1\.1\.1</Version>' -and $launcherProject -match '<SelfContained>true</SelfContained>' -and
        $launcherProject -match '<Company>AV Workstation Toolkit Project</Company>' -and $launcherProject -match '<Product>AV Workstation Toolkit</Product>' -and
        $launcherProject -match '<Title>AV Workstation Toolkit</Title>' -and $launcherProject -match '<AssemblyTitle>AV Workstation Toolkit</AssemblyTitle>' -and
        $launcherProject -match '<Copyright>[^<]*AV Workstation Toolkit contributors</Copyright>') 'Launcher project release identity or deployment metadata differs.'
    Assert-True ($launcherProject -match '<EnableCompressionInSingleFile>false</EnableCompressionInSingleFile>') 'Scanner-friendly uncompressed single-file policy differs.'
    Assert-True ($launcherProject -match '<TargetFramework>net10\.0-windows</TargetFramework>' -and $launcherProject -match '<RuntimeFrameworkVersion>10\.0\.11</RuntimeFrameworkVersion>') 'Launcher does not target the reviewed .NET 10 runtime.'
    $globalSdk = Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json
    Assert-Equal '10.0.100' ([string]$globalSdk.sdk.version) '.NET SDK baseline differs.'
    Assert-Equal 'latestFeature' ([string]$globalSdk.sdk.rollForward) '.NET SDK roll-forward policy differs.'
    $installerProject = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\AVWorkstationToolkit.Installer.wixproj') -Raw
    Assert-True ($installerProject -match 'WixToolset\.Sdk/6\.0\.2' -and $installerProject -match 'InstallerPlatform>x64') 'Installer toolchain is not pinned for x64.'
    $installerSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\Product.wxs') -Raw
    Assert-True ($installerSource -match 'Name="AV Workstation Toolkit"' -and $installerSource -match 'Directory Id="AVWorkstationToolkitProgramMenuFolder" Name="AV Workstation Toolkit"' -and
        $installerSource -match 'Shortcut[\s\S]+?Name="AV Workstation Toolkit"' -and
        $installerSource -match 'Manufacturer="AV Workstation Toolkit Project"' -and
        $installerSource -match 'UpgradeCode="\{7A3A4978-78F0-5824-B93F-A2C741BF853E\}"') 'MSI product, Start-menu, or upgrade identity differs.'
    foreach ($path in @(
        'README.md','CONTRIBUTING.md','PRIVACY.md','SECURITY.md','THIRD-PARTY-NOTICES.md',
        'docs\AV-Workstation-Toolkit-Operator-Guide.md','docs\AV-Workstation-Toolkit-Architecture-and-Safety.md',
        'docs\AV-Workstation-Toolkit-Security-Audit.md','docs\AV-Workstation-Toolkit-QA-Report.md',
        'docs\Code-Signing-Policy.md','docs\SignPath-Readiness.md'
    )) {
        Assert-True (Test-Path -LiteralPath (Join-Path $repositoryRoot $path) -PathType Leaf) "Renamed documentation is missing: $path"
    }
}
Invoke-Check 'Old product branding is restricted to explicit legacy compatibility' {
    # Tests are deliberately excluded because this file contains the legacy fixtures
    # that exercise each production compatibility path. Every production or
    # documentation occurrence must match one exact allowlist rule below.
    $allowRules = @(
        [pscustomobject]@{ Path='installer/Product.wxs'; Pattern='Existing AVinite packages must remain'; Purpose='WiX compatibility comment' },
        [pscustomobject]@{ Path='scripts/AVWorkstationToolkit.Core.psm1'; Pattern='GetEnvironmentVariable\(''AVINITE_DATA_ROOT'''; Purpose='legacy explicit data-root override' },
        [pscustomobject]@{ Path='scripts/AVWorkstationToolkit.Core.psm1'; Pattern='Join-Path \$localAppData ''AVinite'''; Purpose='legacy LocalAppData migration source' },
        [pscustomobject]@{ Path='scripts/AVWorkstationToolkit.Core.psm1'; Pattern='Filter ''\*\.avinite\.json'''; Purpose='legacy vendor-cache metadata discovery' },
        [pscustomobject]@{ Path='scripts/AVWorkstationToolkit.Core.psm1'; Pattern='\$payloadName \+ ''\.avinite\.json'''; Purpose='legacy vendor-cache metadata validation' },
        [pscustomobject]@{ Path='scripts/AVWorkstationToolkit.Core.psm1'; Pattern='LegacyProduct = ''AVinite'''; Purpose='migration marker provenance' },
        [pscustomobject]@{ Path='src/AVWorkstationToolkit.Infrastructure.Windows/Vendors/WindowsVendorCredentialStore.cs'; Pattern='LegacyPrefix = "AVinite:VendorSftp:"'; Purpose='compiled legacy Credential Manager read/delete compatibility' },
        [pscustomobject]@{ Path='docs/CHANGELOG.md'; Pattern='Preserved AVinite-era data and Credential Manager read/delete compatibility'; Purpose='Phase 14 compatibility-retention record' },
        [pscustomobject]@{ Path='docs/CHANGELOG.md'; Pattern='Renamed AVinite to AV Workstation Toolkit'; Purpose='historical rename record' },
        [pscustomobject]@{ Path='README.md'; Pattern='%LOCALAPPDATA%\\AVinite'; Purpose='legacy data-retention removal guidance' },
        [pscustomobject]@{ Path='PRIVACY.md'; Pattern='%LOCALAPPDATA%\\AVinite'; Purpose='legacy data migration privacy disclosure' },
        [pscustomobject]@{ Path='docs/AV-Workstation-Toolkit-Operator-Guide.md'; Pattern='%LOCALAPPDATA%\\AVinite'; Purpose='legacy data-retention operator guidance' },
        [pscustomobject]@{ Path='docs/Packaging-and-Release.md'; Pattern='%LOCALAPPDATA%\\AVinite'; Purpose='operator migration documentation' },
        [pscustomobject]@{ Path='docs/Packaging-and-Release.md'; Pattern='`AVINITE_SIGNING_PFX_BASE64` and `AVINITE_SIGNING_PFX_PASSWORD`'; Purpose='CI secret migration documentation' },
        [pscustomobject]@{ Path='docs/Endpoint-Security-Behavior.md'; Pattern='legacy `AVinite:VendorSftp:`'; Purpose='endpoint credential compatibility disclosure' },
        [pscustomobject]@{ Path='.github/workflows/release.yml'; Pattern='secrets\.AVINITE_SIGNING_PFX_BASE64'; Purpose='legacy release signing secret fallback' },
        [pscustomobject]@{ Path='.github/workflows/release.yml'; Pattern='secrets\.AVINITE_SIGNING_PFX_PASSWORD'; Purpose='legacy release signing secret fallback' }
    )
    $hits = @{}
    for ($index = 0; $index -lt $allowRules.Count; $index++) { $hits[$index] = 0 }
    $maintainedPaths = @(& git -C $repositoryRoot ls-files --cached --others --exclude-standard)
    Assert-Equal 0 $LASTEXITCODE 'Maintained-file inventory for the rebrand audit failed.'
    foreach ($relativePath in $maintainedPaths) {
        $normalizedPath = $relativePath.Replace('\','/')
        Assert-True ($normalizedPath -notmatch '(?i)avinite') "Tracked filename still carries the old product brand: $normalizedPath"
        if ($normalizedPath.StartsWith('tests/',[StringComparison]::OrdinalIgnoreCase)) { continue }
        $fullPath = Join-Path $repositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
        $extension = [IO.Path]::GetExtension($fullPath).ToLowerInvariant()
        if ($extension -notin @('.cmd','.cs','.csproj','.csv','.json','.md','.ps1','.psd1','.psm1','.wixproj','.wxs','.xaml','.yml','.yaml')) { continue }
        foreach ($line in [IO.File]::ReadAllLines($fullPath)) {
            if ($line -notmatch '(?i)avinite') { continue }
            $matchingRules = @()
            for ($index = 0; $index -lt $allowRules.Count; $index++) {
                $rule = $allowRules[$index]
                if ($normalizedPath -eq $rule.Path -and $line -match $rule.Pattern) { $matchingRules += $index }
            }
            Assert-Equal 1 $matchingRules.Count "Unauthorized or ambiguous old-brand occurrence in ${normalizedPath}: $line"
            $hits[$matchingRules[0]]++
        }
    }
    for ($index = 0; $index -lt $allowRules.Count; $index++) {
        Assert-Equal 1 $hits[$index] ("Legacy-brand allowlist entry is missing or no longer narrow: {0} ({1})" -f $allowRules[$index].Path,$allowRules[$index].Purpose)
    }
}
Invoke-Check 'Maintained source contains no former organization attribution' {
    $forbiddenPublisher = 'Cene' + 'ro'
    $textExtensions = @('.cmd','.cs','.csproj','.csv','.json','.md','.ps1','.psd1','.psm1','.txt','.wixproj','.wxs','.xaml','.yml','.yaml')
    $trackedPaths = @(& git -C $repositoryRoot ls-files)
    Assert-Equal 0 $LASTEXITCODE 'Tracked-file inventory for the publisher audit failed.'
    foreach ($relativePath in $trackedPaths) {
        $fullPath = Join-Path $repositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
        if ([IO.Path]::GetExtension($fullPath).ToLowerInvariant() -notin $textExtensions) { continue }
        $content = [IO.File]::ReadAllText($fullPath)
        Assert-True ($content -notmatch [regex]::Escape($forbiddenPublisher)) "Former organization attribution remains in $relativePath"
    }
}
Invoke-Check 'Packaged launcher is compiled-only and retires stale legacy runtime files' {
    $source = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\Program.cs') -Raw
    $project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj') -Raw
    Assert-True ($project -match '<UseWPF>true</UseWPF>' -and $project -match '<PublishTrimmed>false</PublishTrimmed>' -and
        $project -match 'ProjectReference Include="\.\.\\AVWorkstationToolkit\.App' -and
        $project -match 'WorkerPayloadPath' -and $project -match 'AVWorkstationToolkit\.Payload\.worker/AVWorkstationToolkit\.Worker\.exe' -and
        $project -match '<EmbeddedResource' -and $project -match 'AVWorkstationToolkit\.Payload\.manifests/' -and
        $project -notmatch 'AVWorkstationToolkit\.Payload\.(?:app|scripts)/') 'Launcher project does not embed only the compiled App/worker and reviewed data resources.'
    Assert-True ($source -match 'GetManifestResourceNames' -and $source -match 'GetManifestResourceStream' -and $source -match 'SHA256\.HashData') 'Launcher does not extract and verify its embedded runtime.'
    Assert-True ($source -match 'new PackagedAppStartupContext' -and $source -match 'RunCompiledApp\(new AVWorkstationToolkit\.App\.App\(context\)\)' -and $source -match 'app\.InitializeComponent\(\)') 'Normal launcher startup does not enter the initialized compiled WPF App.'
    Assert-True ($source -match 'RemoveRetiredRuntimeFiles' -and $source -match 'scripts/Start-AVWorkstationToolkit\.ps1' -and $source -match 'File\.Delete') 'Launcher does not clean the recognized stale recovery payload from its versioned runtime.'
    Assert-True ($source -match 'SpecialFolder\.LocalApplicationData' -and $source -match 'managed-applications\.json' -and $source -match 'external-applications\.json' -and $source -match 'commercial-av-catalog\.json') 'Launcher does not isolate mutable data or require the compiled catalog manifests.'
    Assert-True ($source -notmatch '(?i)--legacy-powershell-recovery|--vendor-bridge|powershell\.exe|cmd\.exe|ProcessStartInfo|ExecutionPolicy') 'Launcher retains a retired shell, bridge, or PowerShell runtime surface.'
}
Invoke-Check 'Launcher path containment uses one reviewed implementation' {
    $safePath = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\SafePath.cs') -Raw
    $program = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\Program.cs') -Raw
    Assert-True ($safePath -match 'RequireAbsoluteNonRoot' -and $safePath -match 'IsStrictChild' -and $safePath -match 'ContainsReparsePoint') 'Shared safe-path implementation is incomplete.'
    Assert-True ($program -match 'SafePath\.' -and $program -notmatch 'private static bool IsChildPath') 'A launcher security boundary bypasses or duplicates the shared safe-path implementation.'
}
Invoke-Check 'Child-process policy is explicit, bounded, and complete' {
    $policy = Get-Content -LiteralPath $processPolicyPath -Raw | ConvertFrom-Json
    Assert-Equal 1 ([int]$policy.SchemaVersion) 'Process policy schema differs.'
    Assert-Equal 'AV Workstation Toolkit' ([string]$policy.Product) 'Process policy product differs.'
    $expectedIds = @('compiled-action-worker','winget','explorer-handoff','https-shell-handoff','snapshot-dsregcmd')
    Assert-Equal ($expectedIds -join '|') (@($policy.Launches.Id) -join '|') 'Allowed child-process categories differ.'
    Assert-Equal @($policy.Launches).Count @($policy.Launches.Id | Sort-Object -Unique).Count 'Process policy contains duplicate IDs.'
    foreach ($launch in @($policy.Launches)) {
        Assert-True (-not [string]::IsNullOrWhiteSpace([string]$launch.Executable) -and -not [string]::IsNullOrWhiteSpace([string]$launch.Boundary)) "Process policy entry is incomplete: $($launch.Id)"
    }
    $uiSource = Get-Content -LiteralPath (Join-Path $scriptsRoot 'Start-AVWorkstationToolkit.ps1') -Raw
    $coreSource = Get-Content -LiteralPath $moduleImplementationPath -Raw
    Assert-True ($uiSource -notmatch '(?i)Start-Process|ProcessStartInfo|Process\.Start') 'Presentation code can directly create a process.'
    Assert-True ($uiSource -match 'Open-AVWorkstationToolkitExplorerPath' -and $uiSource -match 'Open-AVWorkstationToolkitHttpsUri') 'Presentation handoffs do not use bounded core functions.'
    Assert-True ($coreSource -match 'function Start-AVWorkstationToolkitDirectProcess' -and $coreSource -match 'UseShellExecute\s*=\s*\$false' -and $coreSource -match 'CreateNoWindow') 'Direct process wrapper does not disable shell execution or expose explicit console behavior.'
}
Invoke-Check 'Worker launch arguments are deterministic and arbitrary request paths are rejected' {
    $module = Get-Module AVWorkstationToolkit.Core
    $quoted = & $module { ConvertTo-AVWorkstationToolkitProcessArgument -Value 'C:\Program Files\AVWorkstationToolkit\worker.ps1' }
    Assert-Equal '"C:\Program Files\AVWorkstationToolkit\worker.ps1"' $quoted 'Windows process argument quoting differs.'
    $explorerSelection = & $module { Get-AVWorkstationToolkitExplorerArgumentString -Path 'C:\Program Files\AVWorkstationToolkit\cached installer.exe' -SelectFile }
    Assert-Equal '/select,"C:\Program Files\AVWorkstationToolkit\cached installer.exe"' $explorerSelection 'Explorer file-selection grammar incorrectly quotes the /select switch.'
    $explorerDirectory = & $module { Get-AVWorkstationToolkitExplorerArgumentString -Path 'C:\Program Files\AVWorkstationToolkit' }
    Assert-Equal '"C:\Program Files\AVWorkstationToolkit"' $explorerDirectory 'Explorer directory handoff quoting differs.'
    Assert-Throws { & $module { ConvertTo-AVWorkstationToolkitProcessArgument -Value "bad`r`nargument" } } 'line breaks' 'Line-break process argument was accepted.'

    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-process-policy-{0}' -f [guid]::NewGuid().ToString('N'))
    $outsideRequest = Join-Path $temporaryRoot 'request-20260823-120000-abcdef12.json'
    $dataRoot = Join-Path $temporaryRoot 'data'
    try {
        New-Item -ItemType Directory -Path $temporaryRoot,$dataRoot -Force | Out-Null
        '{}' | Set-Content -LiteralPath $outsideRequest -Encoding UTF8
        Assert-Throws { Start-AVWorkstationToolkitWorker -RequestPath $outsideRequest -DataRoot $dataRoot } 'direct request JSON child' 'Worker accepted an arbitrary request path.'
        Assert-Throws { Open-AVWorkstationToolkitHttpsUri -Uri 'file:///C:/Windows/notepad.exe' } 'absolute HTTPS URI' 'Non-HTTPS shell handoff was accepted.'
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
    }
}
Invoke-Check 'Endpoint-trust static QA rejects suspicious production patterns' {
    $output = (& (Join-Path $PSScriptRoot 'Test-EndpointTrust.ps1') | Out-String)
    Assert-True ($output -match 'ENDPOINT_TRUST_OK launches=5') 'Endpoint-trust QA did not validate the reviewed process contract.'
}
Invoke-Check 'Phase 14 compiled runtime retirement contract is complete' {
    $output = (& (Join-Path $PSScriptRoot 'Test-CompiledCutover.ps1') | Out-String)
    Assert-True ($output -match 'COMPILED_RETIREMENT_OK') 'Compiled runtime retirement contract did not pass.'
}
Invoke-Check 'Clone build entry point and tagged-release workflow publish the standalone executable' {
    $buildEntryPath = Join-Path $repositoryRoot 'Build-AVWorkstationToolkit.cmd'
    Assert-True (Test-Path -LiteralPath $buildEntryPath -PathType Leaf) 'Root one-command build entry point is missing.'
    $buildEntry = Get-Content -LiteralPath $buildEntryPath -Raw
    Assert-True ($buildEntry -match '(?i)build\\Build-Release\.ps1' -and $buildEntry -match '(?i)WindowsPowerShell\\v1\.0\\powershell\.exe') 'Root build entry point does not invoke the reviewed build script with inbox Windows PowerShell.'
    Assert-True ($buildEntry -match '(?i)-ExecutionPolicy\s+RemoteSigned' -and $buildEntry -notmatch '(?i)-ExecutionPolicy\s+Bypass') 'Root build entry point weakens PowerShell execution policy.'
    Assert-True ($buildEntry -match '(?i)set\s+"PSModulePath="') 'Root build entry point can inherit an incompatible PowerShell 7 module path.'
    $releaseWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github\workflows\release.yml') -Raw
    Assert-True ($releaseWorkflow -match "tags:\s*\r?\n\s*- 'v\*\.\*\.\*'" -and $releaseWorkflow -match 'contents:\s*write') 'Tagged-release workflow trigger or permissions differ.'
    Assert-True ($releaseWorkflow -match 'AV-Workstation-Toolkit-\$version-win-x64\.exe' -and
        $releaseWorkflow -match 'AV-Workstation-Toolkit-\$version-sbom\.cdx\.json' -and
        $releaseWorkflow -match 'AV-Workstation-Toolkit-\$version-LICENSE\.txt' -and
        $releaseWorkflow -match 'AV-Workstation-Toolkit-\$version-THIRD-PARTY-NOTICES\.md' -and
        $releaseWorkflow -match 'gh release create') 'Tagged-release workflow does not publish the direct executable, Apache-2.0 license, notices, and SBOM.'
    Assert-True ($releaseWorkflow -match '\[Code signing policy\]\(https://github\.com/11anthonym/AV-Workstation-Toolkit/blob/\$env:GITHUB_REF_NAME/docs/Code-Signing-Policy\.md\)') 'Tagged-release notes do not visibly link the canonical code-signing policy.'
    Assert-True ($releaseWorkflow -match 'Run-Tests\.ps1\s+-CoreOnly' -and $releaseWorkflow -match 'Build-Release\.ps1' -and $releaseWorkflow -match 'SkipTests') 'Tagged-release workflow does not separate CI-safe source QA from the package build.'
    Assert-True ($releaseWorkflow -match 'gh release list\s+--limit' -and $releaseWorkflow -notmatch 'gh release view') 'Tagged-release workflow uses a failing first-release existence probe.'
    Assert-True ($releaseWorkflow -match 'Published release assets are immutable' -and
        $releaseWorkflow -notmatch '(?i)gh release (?:upload|edit)|--clobber') 'Tagged-release workflow can replace already-published release bytes.'
    Assert-True ($releaseWorkflow -match 'actions/checkout@[a-f0-9]{40}' -and $releaseWorkflow -match 'actions/setup-dotnet@[a-f0-9]{40}') 'Release workflow actions are not pinned to immutable commits.'
    Assert-True ($releaseWorkflow -match 'persist-credentials:\s*false') 'Release checkout retains an unnecessary repository credential.'
    Assert-True ($releaseWorkflow -match 'AVWORKSTATIONTOOLKIT_SIGNING_PFX_BASE64' -and $releaseWorkflow -match 'Import-PfxCertificate' -and
        $releaseWorkflow -match 'RequireSignature' -and $releaseWorkflow -match "BuildChannel','Production" -and
        $releaseWorkflow -match 'Test-EndpointTrust\.ps1[\s\S]+?-ScanWithDefender') 'Tagged release does not require organizational signing and endpoint-trust validation.'
    Assert-True ($releaseWorkflow -notmatch 'producing an unsigned release candidate') 'Tagged production workflow can silently publish unsigned artifacts.'
    $qaWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github\workflows\qa.yml') -Raw
    Assert-True ($qaWorkflow -match 'actions/upload-artifact@[a-f0-9]{40}' -and $qaWorkflow -notmatch 'uses:\s*actions/[^@]+@v\d+') 'QA workflow actions are not pinned to immutable commits.'
    Assert-True ($qaWorkflow -match 'persist-credentials:\s*false') 'QA checkout retains an unnecessary repository credential.'
}
Invoke-Check 'Tagged workflow and release documentation agree on eight standard assets' {
    $workflowPath = Join-Path $repositoryRoot '.github\workflows\release.yml'
    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $assetBlock = [regex]::Match($workflow,'(?s)\$assets\s*=\s*@\((?<Body>.*?)\r?\n\s*\)\r?\n\s*foreach')
    Assert-True $assetBlock.Success 'Tagged workflow release asset block was not found.'
    $actualAssets = @([regex]::Matches($assetBlock.Groups['Body'].Value,'"(?<Name>AV-Workstation-Toolkit-\$version-[^"]+)"') |
        ForEach-Object { $_.Groups['Name'].Value })
    $expectedAssets = @(
        'AV-Workstation-Toolkit-$version-win-x64.exe',
        'AV-Workstation-Toolkit-$version-x64.msi',
        'AV-Workstation-Toolkit-$version-win-x64.zip',
        'AV-Workstation-Toolkit-$version-LICENSE.txt',
        'AV-Workstation-Toolkit-$version-THIRD-PARTY-NOTICES.md',
        'AV-Workstation-Toolkit-$version-SHA256SUMS.txt',
        'AV-Workstation-Toolkit-$version-release.json',
        'AV-Workstation-Toolkit-$version-sbom.cdx.json'
    )
    Assert-Equal ($expectedAssets -join '|') ($actualAssets -join '|') 'Tagged workflow standard release asset set differs.'

    foreach ($relativePath in @(
        'README.md','docs\Packaging-and-Release.md','docs\Endpoint-Security-Behavior.md',
        'docs\SignPath-Readiness.md','docs\AV-Workstation-Toolkit-QA-Report.md'
    )) {
        $document = Get-Content -LiteralPath (Join-Path $repositoryRoot $relativePath) -Raw
        Assert-True ($document -match '(?i)(?:exactly|all) eight (?:standard )?assets') "$relativePath does not state the eight-asset release boundary."
        Assert-True ($document -notmatch '(?i)(?:exactly|all) seven standard assets') "$relativePath retains the stale seven-asset release count."
    }
}
Invoke-Check 'Release dependency graph is locked' {
    $project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj') -Raw
    $lockPath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\packages.lock.json'
    Assert-True ($project -match '<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>' -and $project -match '<RestoreLockedMode') 'Launcher restore does not enforce its dependency lock in CI builds.'
    Assert-True (Test-Path -LiteralPath $lockPath -PathType Leaf) 'Launcher dependency lock file is missing.'
    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    Assert-True ($null -ne $lock.dependencies.'net10.0-windows7.0'.'SSH.NET'.contentHash) 'SSH.NET lock entry does not include a .NET 10 content hash.'
}
Invoke-Check 'CycloneDX SBOM generation is deterministic and sanitized' {
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-sbom-{0}' -f [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        $first = Join-Path $temporaryRoot 'first.cdx.json'
        $second = Join-Path $temporaryRoot 'second.cdx.json'
        $generator = Join-Path $repositoryRoot 'build\New-ReleaseSbom.ps1'
        & $generator -Version '1.1.1' -CommitSha ('a' * 40) -LauncherSha256 ('b' * 64) -WorkerSha256 ('c' * 64) -OutputPath $first | Out-Null
        & $generator -Version '1.1.1' -CommitSha ('a' * 40) -LauncherSha256 ('b' * 64) -WorkerSha256 ('c' * 64) -OutputPath $second | Out-Null
        Assert-Equal (Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash (Get-FileHash -LiteralPath $second -Algorithm SHA256).Hash 'Repeated SBOM generation differs.'
        $text = Get-Content -LiteralPath $first -Raw
        $sbom = $text | ConvertFrom-Json
        Assert-Equal 'CycloneDX' ([string]$sbom.bomFormat) 'SBOM format differs.'
        Assert-Equal '1.6' ([string]$sbom.specVersion) 'SBOM version differs.'
        Assert-Equal 'AV Workstation Toolkit' ([string]$sbom.metadata.component.name) 'SBOM root component differs.'
        Assert-Equal 'Apache-2.0' ([string]$sbom.metadata.component.licenses[0].license.id) 'SBOM root project license differs.'
        Assert-Equal 8 @($sbom.components).Count 'SBOM component count differs from the reviewed dependency boundary.'
        foreach ($expectedComponent in @('SSH.NET','BouncyCastle.Cryptography','Microsoft.NETCore.App.Runtime.win-x64','Microsoft.NETCore.App.Host.win-x64','WixToolset.Sdk')) {
            Assert-True ($expectedComponent -in @($sbom.components.name)) "SBOM omits reviewed dependency: $expectedComponent"
        }
        Assert-True ('Microsoft.WindowsDesktop.App.Runtime.win-x64' -notin @($sbom.components.name)) 'SBOM retains the incorrect Windows Desktop runtime identity.'
        foreach ($component in @($sbom.components)) {
            Assert-True (@($component.licenses).Count -eq 1 -and -not [string]::IsNullOrWhiteSpace([string]$component.licenses[0].license.id)) "SBOM component has no reviewed license: $($component.name)"
            Assert-True (@($component.properties | Where-Object name -eq 'avworkstationtoolkit:dependency:distribution').Count -eq 1) "SBOM component has no distribution classification: $($component.name)"
        }
        foreach ($expectedLicense in @(@('SSH.NET','MIT'),@('BouncyCastle.Cryptography','MIT'),@('WixToolset.Sdk','MS-RL'))) {
            $component = @($sbom.components | Where-Object name -eq $expectedLicense[0])
            Assert-Equal 1 $component.Count "SBOM component is missing or duplicated: $($expectedLicense[0])"
            Assert-Equal $expectedLicense[1] ([string]$component[0].licenses[0].license.id) "Third-party license changed unexpectedly: $($expectedLicense[0])"
        }
        Assert-True ($text -notmatch '(?i)(?:[A-Z]:\\Users\\|/Users/|/home/)' -and
            ([string]::IsNullOrWhiteSpace($env:USERNAME) -or $text -notmatch [regex]::Escape($env:USERNAME))) 'SBOM contains developer identity or an absolute home path.'
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
    }
}
Invoke-Check 'Third-party notices match locked and hosted build dependencies' {
    $noticesPath = Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md'
    Assert-True (Test-Path -LiteralPath $noticesPath -PathType Leaf) 'Third-party notices are missing.'
    $notices = Get-Content -LiteralPath $noticesPath -Raw
    $lock = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\packages.lock.json') -Raw | ConvertFrom-Json
    $target = $lock.dependencies.'net10.0-windows7.0'
    Assert-True ($null -ne $target) 'The reviewed .NET 10 dependency target is missing from the lock.'
    foreach ($property in @($target.PSObject.Properties | Where-Object { $_.Value.PSObject.Properties.Name -contains 'resolved' })) {
        $name = [string]$property.Name
        $version = [string]$property.Value.resolved
        Assert-True (-not [string]::IsNullOrWhiteSpace($version)) "Locked dependency has no resolved version: $name"
        Assert-True ($notices.Contains("| $name | $version |")) "Third-party notices do not match locked dependency: $name $version"
    }

    $installerProject = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\AVWorkstationToolkit.Installer.wixproj') -Raw
    $wixMatch = [regex]::Match($installerProject,'WixToolset\.Sdk/(?<Version>\d+\.\d+\.\d+)')
    Assert-True ($wixMatch.Success -and $notices.Contains("| WixToolset.Sdk | $($wixMatch.Groups['Version'].Value) |")) 'Third-party notices do not match the pinned WiX SDK.'

    $workflowText = (@(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot '.github\workflows') -File -Filter '*.yml' | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw
    })) -join "`n"
    $actionMatches = [regex]::Matches($workflowText,'uses:\s*(?<Name>actions/[A-Za-z0-9._-]+)@(?<Sha>[a-f0-9]{40})')
    $actionKeys = @{}
    foreach ($match in $actionMatches) {
        $key = '{0}@{1}' -f $match.Groups['Name'].Value,$match.Groups['Sha'].Value
        if ($actionKeys.ContainsKey($key)) { continue }
        $actionKeys[$key] = $true
        Assert-True ($notices -match [regex]::Escape($match.Groups['Name'].Value)) "Third-party notices omit hosted action: $($match.Groups['Name'].Value)"
        Assert-True ($notices -match [regex]::Escape($match.Groups['Sha'].Value)) "Third-party notices omit hosted action pin: $key"
    }
    Assert-True ($actionKeys.Count -ge 3) 'Hosted-action notice parity did not inspect the expected pinned actions.'
    $analyzerVersion = [regex]::Match($workflowText,'PSScriptAnalyzer\s+-RequiredVersion\s+(?<Version>\d+\.\d+\.\d+)').Groups['Version'].Value
    Assert-True (-not [string]::IsNullOrWhiteSpace($analyzerVersion) -and $notices.Contains("| PSScriptAnalyzer | $analyzerVersion |")) 'Third-party notices do not match the pinned PSScriptAnalyzer version.'

    foreach ($identity in @('Microsoft.NETCore.App.Runtime.win-x64 | 10.0.11','Microsoft.NETCore.App.Host.win-x64 | 10.0.11')) {
        Assert-True ($notices.Contains("| $identity |")) "Third-party notices omit packaged runtime identity: $identity"
    }
    Assert-True ($notices -match 'Commercial AV products[\s\S]+metadata records only' -and $notices -match 'No third-party source, submodule, Git LFS object, font, icon, installer, or\s+vendor binary is vendored') 'Third-party notices blur catalog knowledge or vendored-source boundaries.'

    $launcherProject = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj') -Raw
    $launcherSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\Program.cs') -Raw
    $releaseBuild = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build\Build-Release.ps1') -Raw
    foreach ($name in @('THIRD-PARTY-NOTICES.md','PROJECT-LICENSE.txt','DOTNET-LICENSE.txt','DOTNET-THIRD-PARTY-NOTICES.txt')) {
        Assert-True ($launcherProject -match [regex]::Escape($name) -and $launcherSource -match [regex]::Escape($name)) "Packaged runtime notice is not embedded and integrity-required: $name"
    }
    Assert-True ($launcherProject -match '<PackageLicenseExpression>Apache-2\.0</PackageLicenseExpression>') 'Launcher project metadata does not declare Apache-2.0.'
    Assert-True ($releaseBuild -match 'AV-Workstation-Toolkit-\{0\}-THIRD-PARTY-NOTICES\.md' -and
        $releaseBuild -match 'AV-Workstation-Toolkit-\{0\}-LICENSE\.txt' -and
        $releaseBuild -match "ProjectLicense = 'Apache-2\.0'" -and
        $releaseBuild -match 'foreach\s*\(\$path\s+in\s+@\([^\r\n]+\$projectLicensePath[^\r\n]+\$thirdPartyNoticesPath[^\r\n]*\)\)\s*\{\s*\$artifactFiles\.Add\(\$path\)') 'Release provenance does not include the Apache-2.0 project license artifact.'
}
Invoke-Check 'Build enforces the documented .NET 10 SDK policy' {
    $globalSdk = Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json
    Assert-Equal '10.0.100' ([string]$globalSdk.sdk.version) 'SDK baseline differs.'
    Assert-Equal 'latestFeature' ([string]$globalSdk.sdk.rollForward) 'SDK feature-band policy differs.'
    Assert-True (-not [bool]$globalSdk.sdk.allowPrerelease) 'Prerelease SDKs are allowed.'
    $build = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build\Build-Release.ps1') -Raw
    Assert-True ($build -match '--list-sdks' -and $build -match '\^10\\\.0\\\.\\d\{3\}\$') 'Build does not validate a selected stable .NET 10 SDK.'
    if (-not $CoreOnly) {
        Push-Location $repositoryRoot
        try { $selectedSdk = (& dotnet.exe --version 2>&1 | Out-String).Trim() }
        finally { Pop-Location }
        Assert-True ($selectedSdk -match '^10\.0\.\d{3}$') "global.json did not select a supported .NET 10 SDK: $selectedSdk"
    }
}
Invoke-Check 'Stale lock failure reports non-mutating remediation' {
    $build = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build\Build-Release.ps1') -Raw
    Assert-True ($build -match 'restore\s+\$launcherProject\s+--locked-mode') 'Release preflight does not perform locked restore.'
    Assert-True ($build -match 'The locked dependency graph is stale' -and $build -match 'dotnet restore \.\\src\\AVWorkstationToolkit\.Launcher\\AVWorkstationToolkit\.Launcher\.csproj --force-evaluate' -and $build -match 'Review packages\.lock\.json before committing') 'Stale lock failure does not provide the reviewed remediation.'
    Assert-True ($build -match 'Release builds never rewrite the dependency lock' -and $build -match 'publish[\s\S]+?--no-restore') 'Release build can rewrite or silently restore beyond the lock preflight.'
    Assert-True ($build -match 'package --vulnerable --include-transitive --format json --no-restore' -and $build -match 'NuGet vulnerability audit reported vulnerable release dependencies') 'Release preflight omits actionable NuGet vulnerability auditing.'
}
Invoke-Check 'Release build cleans directory contents without deleting the output directory' {
    $build = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build\Build-Release.ps1') -Raw
    Assert-True ($build -match 'function Reset-BuildDirectory' -and $build -match 'Get-ChildItem\s+-LiteralPath\s+\$Path\s+-Force\s+\|\s+Remove-Item') 'Build output cleanup does not preserve an in-use working directory.'
    Assert-True ($build -notmatch 'Remove-Item\s+-LiteralPath\s+\$target\s+-Recurse') 'Build still deletes the release directory itself.'
    Assert-True ($build -match 'Get-Command\s+dotnet[\s\S]+?Select-Object\s+-First\s+1') 'Build does not deterministically choose the first resolved dotnet host.'
    Assert-True ($build -match "ValidateSet\('Auto','CurrentUser','LocalMachine'\)" -and $build -match 'EnhancedKeyUsageList' -and
        $build -match 'signtool\.exe' -and $build -match '(?s)''/tr'',\$TimestampServer\.AbsoluteUri,''/td'',''SHA256''' -and
        $build -match 'TimestampStatus -ne ''Valid''') 'Build does not support validated RFC3161 signing through an external organizational certificate.'
    Assert-True ($build -match '(?s)\(\$RequireSignature -or \$BuildChannel -eq ''Production''\).*CertificateThumbprint') 'Production builds can silently fall back to unsigned output.'
    Assert-True ($build -match "TargetRuntime\s*=\s*'Microsoft\.NETCore\.App\.Runtime\.win-x64/10\.0\.11'" -and
        $build -match "TargetHost\s*=\s*'Microsoft\.NETCore\.App\.Host\.win-x64/10\.0\.11'" -and
        $build -match 'SelectedSdk\s*=\s*\$dotnetVersionText') 'Release provenance does not record the actual runtime, apphost, and selected SDK.'
    $manifestWriteIndex = $build.IndexOf('$releaseManifest | ConvertTo-Json',[StringComparison]::Ordinal)
    $checksumWriteIndex = $build.IndexOf('$checksumLines | Set-Content',[StringComparison]::Ordinal)
    Assert-True ($manifestWriteIndex -ge 0 -and $checksumWriteIndex -gt $manifestWriteIndex -and
        $build -match '\$checksumCoveredFiles\s*=\s*@\(\$artifactFiles\)\s*\+\s*@\(\$releaseManifestPath\)') 'Release checksums are not generated after and over the release manifest.'
}
Invoke-Check 'Signature-required package policy rejects unsigned artifacts while development mode permits them' {
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-signature-policy-{0}' -f [guid]::NewGuid().ToString('N'))
    $powershellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    try {
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $temporaryRoot 'AV-Workstation-Toolkit-1.1.1-win-x64.exe'),(Join-Path $temporaryRoot 'AV-Workstation-Toolkit-1.1.1-x64.msi') -Force | Out-Null
        $packageTest = Join-Path $PSScriptRoot 'Test-Package.ps1'
        $developmentOutput = (& $powershellExe -NoProfile -ExecutionPolicy RemoteSigned -File $packageTest -ReleaseRoot $temporaryRoot -SignaturePolicyOnly 2>&1 | Out-String)
        Assert-Equal 0 $LASTEXITCODE 'Unsigned development signature policy unexpectedly failed.'
        Assert-True ($developmentOutput -match '1 passed.*0 failed') 'Unsigned development signature policy did not report success.'
        $productionOutput = (& $powershellExe -NoProfile -ExecutionPolicy RemoteSigned -File $packageTest -ReleaseRoot $temporaryRoot -SignaturePolicyOnly -RequireSignature 2>&1 | Out-String)
        Assert-True ($LASTEXITCODE -ne 0 -and $productionOutput -match 'Signature is not valid') 'Signature-required policy accepted unsigned artifacts.'
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
    }
}
Invoke-Check 'Offline package authoring requires redistribution and payload verification' {
    $authoringPath = Join-Path $repositoryRoot 'scripts\Add-AVWorkstationToolkitExternalPackage.ps1'
    Assert-True (Test-Path -LiteralPath $authoringPath -PathType Leaf) 'External package authoring command is missing.'
    $authoring = Get-Content -LiteralPath $authoringPath -Raw
    Assert-True ($authoring -match 'RedistributionAuthorized' -and $authoring -match 'Get-AuthenticodeSignature' -and $authoring -match 'Get-FileHash') 'External package authoring omits rights, signature, or hash validation.'
    Assert-True ($authoring -match '\[Parameter\(Mandatory\)\]\[string\]\$Vendor' -and $authoring -match '\[string\[\]\]\$ApplicationType' -and $authoring -match 'Metadata\s*=') 'External package authoring can emit an unclassified schema-3 record.'
    Assert-True ($authoring -notmatch '(?i)Start-Process|Invoke-Expression') 'External package authoring can execute supplied payloads.'
    $build = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build\Build-Release.ps1') -Raw
    Assert-True ($build -match 'BuildOfflineBundle' -and $build -match 'Resolve-AVWorkstationToolkitExternalPayload' -and $build -match 'offline-bundle\.zip') 'Release build does not create a verified offline bundle.'
    $ignore = Get-Content -LiteralPath (Join-Path $repositoryRoot '.gitignore') -Raw
    Assert-True ($ignore -match '(?m)^external-packages/') 'Local third-party payload depot is not excluded from source control.'
}
Invoke-Check 'Publication-sensitive local output is excluded without hiding reviewed source' {
    $ignorePath = Join-Path $repositoryRoot '.gitignore'
    $ignoreRules = @(Get-Content -LiteralPath $ignorePath | ForEach-Object { $_.Trim() } | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and -not $_.StartsWith('#')
    })
    foreach ($rule in @(
        'snapshots/*','logs/','reports/','/diagnostics/','diagnostic-exports/','workstation-snapshots/',
        'vendor-cache/','artifacts/','external-packages/','src/**/bin/','src/**/obj/','installer/bin/','installer/obj/',
        '*.pfx','*.p12','*.p8','*.pem','*.key','*.snk','signing-material/','credential-exports/',
        '*.credential-export*','.env','.env.*','!.env.example','.vs/','*.user','*.suo'
    )) {
        Assert-Contains $ignoreRules $rule "Publication-hygiene ignore rule is missing: $rule"
    }

    foreach ($sample in @(
        'diagnostics/operator-export.json','workstation-snapshots/site.json','vendor-cache/vendor-installer.exe',
        'artifacts/release/AVWorkstationToolkit.exe','external-packages/vendor.msi','signing-material/release.pfx',
        'credential-exports/provider.credential-export.json','.env.local','src/Fixture/bin/private.dll'
    )) {
        & git -C $repositoryRoot check-ignore --quiet --no-index -- $sample
        $ignored = $LASTEXITCODE -eq 0
        Assert-True $ignored "Publication-sensitive sample is not ignored: $sample"
    }
    foreach ($sample in @(
        '.env.example','src/AVWorkstationToolkit.Launcher/packages.lock.json',
        'manifests/process-launch-policy.json','.github/workflows/qa.yml','THIRD-PARTY-NOTICES.md'
    )) {
        & git -C $repositoryRoot check-ignore --quiet --no-index -- $sample
        $ignored = $LASTEXITCODE -eq 0
        Assert-True (-not $ignored) "Reviewed source or policy input is accidentally ignored: $sample"
    }
}
Invoke-Check 'Offline authoring preserves schema 3 and emits validated metadata' {
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-authoring-{0}' -f [guid]::NewGuid().ToString('N'))
    $temporaryManifest = Join-Path $temporaryRoot 'external-applications.json'
    try {
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        Copy-Item -LiteralPath $externalManifestPath -Destination $temporaryManifest
        $arguments = @{
            Name='Fixture Field Utility'; Id='Fixture.FieldUtility'; Version='1.2.3'
            InstallerPath=(Join-Path $env:SystemRoot 'System32\notepad.exe')
            RegistryDisplayNamePattern='^Fixture Field Utility(?:\s|$)'
            ReleaseVersionPattern='Fixture Field Utility v(?<Version>\d+\.\d+\.\d+)'
            ReleaseUri=[uri]'https://www.microsoft.com/'
            OfficialProductUri=[uri]'https://www.microsoft.com/'
            Note='Synthetic signed operating-system fixture for catalog-authoring QA'
            Vendor='Fixture Vendor'; ApplicationType=@('FieldUtility'); Priority='UTILITY'
            LicensingModel=@('UNKNOWN-COST'); DownloadAccess=@('UNKNOWN-ACCESS')
            ManifestPath=$temporaryManifest; PackageDepotRoot=(Join-Path $temporaryRoot 'depot')
            RedistributionAuthorized=$true
        }
        $result = & (Join-Path $scriptsRoot 'Add-AVWorkstationToolkitExternalPackage.ps1') @arguments
        Assert-Equal 'Fixture.FieldUtility' $result.Id 'Authored package ID differs.'
        $document = Get-Content -LiteralPath $temporaryManifest -Raw | ConvertFrom-Json
        Assert-Equal 3 $document.SchemaVersion 'Authoring downgraded the external manifest schema.'
        $raw = @($document.Packages | Where-Object Id -eq 'Fixture.FieldUtility')[0]
        Assert-Equal 'Fixture Vendor' $raw.Metadata.Vendor 'Authored vendor metadata differs.'
        Assert-Contains @($raw.Metadata.ApplicationType) 'FieldUtility' 'Authored application type differs.'
        $parsed = @(ConvertFrom-AVWorkstationToolkitExternalCatalogJson -Json (Get-Content -LiteralPath $temporaryManifest -Raw) | Where-Object Id -eq 'Fixture.FieldUtility')[0]
        Assert-Equal 'Bundled' $parsed.DeliveryMode 'Authored delivery mode differs.'
        Assert-Equal 'ManualHold' $parsed.Deployment 'Authored package left the manual hold.'
        Assert-True (Test-Path -LiteralPath $result.PayloadPath -PathType Leaf) 'Authored payload was not copied to the isolated depot.'
    }
    finally { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
Invoke-Check 'MSI installs per-machine and removes its Start menu directory' {
    $source = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\Product.wxs') -Raw
    Assert-True ($source -match 'Scope="perMachine"' -and $source -match 'ProgramFiles6432Folder') 'MSI is not a per-machine Program Files package.'
    Assert-True ($source -match 'RemoveFolder[^>]+On="uninstall"') 'MSI does not remove its Start menu directory.'
    Assert-True ($source -match 'MajorUpgrade') 'MSI does not define upgrade behavior.'
    Assert-True ($source -notmatch '<Files\s') 'MSI still depends on loose companion payload files.'
}
Invoke-Check 'Endpoint-security behavior documentation is complete and non-evasive' {
    $path = Join-Path $repositoryRoot 'docs\Endpoint-Security-Behavior.md'
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) 'Endpoint-security behavior baseline is missing.'
    $document = Get-Content -LiteralPath $path -Raw
    foreach ($heading in @('Normal startup and inventory','User-requested install or update','External package handoff','Diagnostics','Build and release behavior','SmartScreen, EDR, and false positives','Compiled-runtime migration status')) {
        Assert-True ($document -match [regex]::Escape($heading)) "Endpoint-security documentation section is missing: $heading"
    }
    Assert-True ($document -match 'never instruct users to disable endpoint protection or add an exclusion' -and
        $document -match 'does not guarantee zero.*SmartScreen.*Defender.*CrowdStrike') 'False-positive response guidance is incomplete or misleading.'
}
Invoke-Check 'Runtime privacy behavior remains bounded and documented' {
    $productionFiles = @(
        @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -File | Where-Object {
            $_.FullName -notmatch '\\(?:bin|obj)\\' -and $_.Extension -in @('.cs','.csproj')
        })
    )
    $productionText = ($productionFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    foreach ($pattern in @(
        'Microsoft\.ApplicationInsights','OpenTelemetry','Sentry','Datadog','NewRelic','Raygun','Bugsnag',
        'PostHog','Segment\.Analytics','Mixpanel','Amplitude'
    )) {
        Assert-True ($productionText -notmatch $pattern) "Production source added a telemetry or analytics dependency: $pattern"
    }
    foreach ($pattern in @(
        'dc\.services\.visualstudio\.com','applicationinsights\.azure\.com','o\d+\.ingest\.sentry\.io',
        'api\.segment\.io','api\.mixpanel\.com','api2?\.amplitude\.com',
        'browser-intake\.[^\s''\"]*datadoghq','api\.posthog\.com','notify\.bugsnag\.com'
    )) {
        Assert-True ($productionText -notmatch $pattern) "Production source added a known analytics endpoint: $pattern"
    }
    foreach ($pattern in @(
        '(?i)\b(?:PostAsync|PutAsync|PatchAsync|UploadFile|UploadString|UploadData)\s*\(',
        '(?i)Invoke-(?:RestMethod|WebRequest)[^\r\n]*-Method\s+(?:Post|Put|Patch)\b'
    )) {
        Assert-True ($productionText -notmatch $pattern) "Production source added an unreviewed generic upload path: $pattern"
    }

    $networkSourcePattern = '(?i)\b(?:HttpWebRequest|HttpClient|SftpClient|WebClient|TcpClient|UdpClient)\b|Invoke-(?:WebRequest|RestMethod)|\.(?:DownloadFile|GetAsync|GetResponse)\s*\('
    $networkFiles = @($productionFiles | Where-Object {
        (Get-Content -LiteralPath $_.FullName -Raw) -match $networkSourcePattern
    } | ForEach-Object {
        $_.FullName.Substring($repositoryRoot.Length).TrimStart('\').Replace('\','/')
    } | Sort-Object -Unique)
    Assert-Equal 'src/AVWorkstationToolkit.Infrastructure.Windows/Vendors/VendorExternalReleaseInventory.cs|src/AVWorkstationToolkit.Infrastructure.Windows/Vendors/VendorHttpsDownloader.cs|src/AVWorkstationToolkit.Infrastructure.Windows/Vendors/VendorSftpDeliveryService.cs' ($networkFiles -join '|') 'Runtime network-capable source expanded without privacy review.'
    $migrationHttps = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Vendors\VendorHttpsDownloader.cs') -Raw
    $migrationSftp = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows\Vendors\VendorSftpDeliveryService.cs') -Raw
    Assert-True ($migrationHttps -match 'AllowAutoRedirect\s*=\s*false' -and $migrationHttps -match 'AllowedHosts\.Contains' -and
        $migrationHttps -match 'MaximumRedirects\s*=\s*5' -and $migrationSftp -match 'ProbeHostFingerprintAsync' -and
        $migrationSftp.IndexOf('ProbeHostFingerprintAsync',[StringComparison]::Ordinal) -lt $migrationSftp.IndexOf('credentials.Read',[StringComparison]::Ordinal)) 'Compiled vendor runtime lost redirect revalidation or host-key-before-credential ordering.'

    $privacy = Get-Content -LiteralPath (Join-Path $repositoryRoot 'PRIVACY.md') -Raw
    Assert-True ($privacy -match 'no\s+telemetry, analytics, advertising, crash-reporting service' -and
        $privacy -match 'Automatic during startup refresh' -and
        $privacy -match 'no runtime GitHub update checker' -and
        $privacy -match 'no generic\s+HTTP upload, POST, PUT, PATCH' -and
        $privacy -match 'encrypted SSH protocol') 'Privacy policy does not describe the audited telemetry, startup-network, upload, or credential behavior.'
    Assert-True ($privacy -notmatch 'This program will not transfer any information to other networked systems unless specifically requested') 'Privacy policy makes a false user-request-only network claim despite automatic startup checks.'
}
Invoke-Check 'Apache-2.0 licensing and SignPath readiness remain factual' {
    $licenseFiles = @(Get-ChildItem -LiteralPath $repositoryRoot -File | Where-Object Name -match '^LICENSE(?:\.|$)')
    Assert-Equal 1 $licenseFiles.Count 'Exactly one root LICENSE file is required.'
    $license = Get-Content -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Raw
    Assert-True ($license -match 'Apache License\s+Version 2\.0, January 2004' -and
        $license -match 'TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION' -and
        $license -match 'END OF TERMS AND CONDITIONS') 'Root LICENSE is not the complete Apache License 2.0 text.'

    $readme = Get-Content -LiteralPath (Join-Path $repositoryRoot 'README.md') -Raw
    $contributing = Get-Content -LiteralPath (Join-Path $repositoryRoot 'CONTRIBUTING.md') -Raw
    $notices = Get-Content -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Raw
    $security = Get-Content -LiteralPath (Join-Path $repositoryRoot 'SECURITY.md') -Raw
    $signing = Get-Content -LiteralPath (Join-Path $repositoryRoot 'docs\Code-Signing-Policy.md') -Raw
    $readiness = Get-Content -LiteralPath (Join-Path $repositoryRoot 'docs\SignPath-Readiness.md') -Raw
    Assert-True ($readme -match 'Apache License 2\.0' -and $readme -match 'SPDX: Apache-2\.0' -and $readme -match '\(LICENSE\)') 'README does not identify and link the Apache-2.0 project license.'
    Assert-True ($contributing -match 'Contributions to AV Workstation Toolkit are submitted under the project' -and $contributing -match 'Apache-2\.0') 'CONTRIBUTING.md does not state the Apache-2.0 contribution terms.'
    Assert-True ($notices -match 'AV Workstation Toolkit itself is licensed under' -and $notices -match 'Apache-2\.0' -and $notices -match 'Third-party components remain governed by their own\s+licenses' -and $notices -match 'does not replace those licenses') 'Third-party notices do not distinguish the project Apache-2.0 license from component licenses.'
    Assert-True ($readme -notmatch 'License selection is pending' -and $contributing -notmatch 'License selection is still pending' -and $readiness -notmatch 'OPEN-SOURCE LICENSE SELECTION REQUIRED') 'Maintained publication documentation still reports license selection as pending.'
    Assert-True ($readme -match 'preparing an application for sponsored open-source\s+code signing through SignPath Foundation\. Current artifacts remain unsigned\s+until that process is approved and integrated\.') 'README overstates or omits the current SignPath status.'
    Assert-True ($security -match 'Private Vulnerability Reporting is not currently verifiable' -and
        $security -match 'no dedicated security email address has been established') 'Security policy invents or omits an unverified private reporting channel.'

    Assert-True ($signing.StartsWith('# Code signing policy')) 'Code-signing document is missing the required visible heading.'
    Assert-True ($signing -match 'has not been accepted' -and $signing -match 'Every production\s+signing request requires deliberate approval') 'Code-signing policy overstates current acceptance or omits manual approval.'
    $futureHeadingIndex = $signing.IndexOf('## Effective only after SignPath Foundation acceptance',[StringComparison]::Ordinal)
    $attribution = 'Free code signing provided by SignPath.io, certificate by SignPath Foundation'
    Assert-True ($futureHeadingIndex -ge 0 -and $signing.IndexOf($attribution,[StringComparison]::Ordinal) -gt $futureHeadingIndex) 'Future attribution is not isolated beneath its acceptance-only heading.'
    Assert-Equal 1 ([regex]::Matches($signing,[regex]::Escape($attribution)).Count) 'Future SignPath attribution is duplicated.'
    Assert-True ($signing -match '\(\.\./PRIVACY\.md\)' -and $signing -match '\(\.\./SECURITY\.md\)') 'Code-signing policy does not link to privacy and security policies.'

    foreach ($fact in @(
        'Project: AV Workstation Toolkit','https://github.com/11anthonym/AV-Workstation-Toolkit',
        'Current executable: `AVWorkstationToolkit.exe`','Release EXE pattern: `AV-Workstation-Toolkit-<version>-win-x64.exe`',
        'MSI pattern: `AV-Workstation-Toolkit-<version>-x64.msi`','ZIP pattern: `AV-Workstation-Toolkit-<version>-win-x64.zip`',
        'Build command: `Build-AVWorkstationToolkit.cmd`','Repository state during this review: private GitHub repository with canonical'
    )) {
        Assert-True ($readiness.Contains($fact)) "SignPath readiness identity differs: $fact"
    }
    Assert-True ($readiness -match 'acceptance is discretionary' -and
        $readiness -match 'reputation remains a non-code acceptance factor' -and
        $readiness -match 'not fabricate users, stars, downloads') 'SignPath readiness overstates acceptance or project reputation.'

    $expectedChecklist = @(
        'OSI-approved open-source license selected','Root LICENSE committed','GitHub repository made public',
        'Public repository history reviewed by owner','GitHub MFA enabled for every maintainer',
        'Initial public release published','Public release exists in the exact form intended for signing',
        'Privacy policy reviewed','Security policy reviewed','Third-party notices reviewed','Code signing policy reviewed',
        'SignPath account created','SignPath MFA enabled','SignPath Foundation application submitted',
        'Project accepted by SignPath Foundation','SignPath project/configuration identifiers received',
        'GitHub build-to-SignPath integration configured','First signing request manually approved',
        'Signed EXE independently verified','Signed MSI independently verified'
    )
    $checklistMatches = [regex]::Matches($readiness,'(?m)^- \[(?<Mark>[ xX])\] (?<Label>.+)$')
    Assert-Equal ($expectedChecklist -join '|') (@($checklistMatches | ForEach-Object { $_.Groups['Label'].Value.Trim() }) -join '|') 'SignPath human checklist differs from the required readiness gates.'
    $completedLabels = @($checklistMatches | Where-Object { $_.Groups['Mark'].Value -match '[xX]' } | ForEach-Object { $_.Groups['Label'].Value.Trim() })
    Assert-Equal 'OSI-approved open-source license selected|Root LICENSE committed' ($completedLabels -join '|') 'SignPath readiness marks an unverified gate complete or omits the committed project license.'
    $signPathWorkflows = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot '.github\workflows') -File | Where-Object {
        $_.Name -match '(?i)signpath' -or (Get-Content -LiteralPath $_.FullName -Raw) -match '(?i)signpath'
    })
    Assert-Equal 0 $signPathWorkflows.Count 'A placeholder or unverified SignPath workflow was added.'
}
Invoke-Check 'Defender investigation keeps historical and current specimens distinct' {
    $path = Join-Path $repositoryRoot 'docs\Defender-False-Positive-Investigation.md'
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) 'Maintained Defender investigation is missing.'
    $document = Get-Content -LiteralPath $path -Raw
    $historicalHash = '47DF422857F9A7B92902469ABB841E6E7942DD2978C0998548C00F7419AD95A8'
    $currentHash = 'A95321BE3C193C1CC67B1BC0C635CEDAD07D9DB9C3E61ADB80042589FDF5E7C5'
    Assert-True ($document.Contains($historicalHash) -and $document.Contains($currentHash) -and $historicalHash -ne $currentHash) 'Defender investigation omits or conflates specimen hashes.'
    Assert-True ($document -match 'Trojan:Win32/Bearfoos\.A!ml' -and
        $document -match 'Submission status\s+not independently verified during this repository pass' -and
        $document -match 'does\s+not reproduce or disprove the historical classification') 'Defender investigation overstates the historical classification or submission status.'
    Assert-True ($document -match 'no exclusions, policy changes, quarantine restoration, execution,\s+or evasion work' -and
        $document -match 'No application or build change is justified solely by the historical alert') 'Defender investigation does not preserve the non-evasion boundary.'
}
Invoke-Check 'Publication documentation is present and relative links resolve' {
    $rootMarkdown = @(Get-ChildItem -LiteralPath $repositoryRoot -File | Where-Object Extension -eq '.md')
    $expectedRootMarkdown = @('AGENTS.md','CONTRIBUTING.md','PRIVACY.md','README.md','SECURITY.md','THIRD-PARTY-NOTICES.md')
    Assert-Equal ($expectedRootMarkdown -join '|') (@($rootMarkdown.Name | Sort-Object) -join '|') 'Root publication-document set differs.'
    foreach ($file in @($rootMarkdown + @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'docs') -Recurse -File -Filter '*.md'))) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($match in [regex]::Matches($text, '\[[^\]]+\]\(([^)]+)\)')) {
            $target = $match.Groups[1].Value
            if ($target -match '^(?:https?:|mailto:|#)') { continue }
            $relativeTarget = ($target -split '#',2)[0]
            Assert-True (Test-Path -LiteralPath (Join-Path $file.DirectoryName $relativeTarget)) "Broken documentation link in $($file.Name): $target"
        }
    }
}
Invoke-Check 'Reusable product source contains no personal workstation record' {
    $files = @(Get-ChildItem -LiteralPath $repositoryRoot -File -Filter '*.md') +
        @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'docs') -Recurse -File -Filter '*.md') +
        @(Get-Item -LiteralPath (Join-Path $repositoryRoot 'scripts\AVWorkstationToolkit.Core.psd1')) +
        @(Get-Item -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj'))
    $text = ($files | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    $personalPatterns = @(
        ([string]::Concat('Anth','ony Moretti')),
        ([string]::Concat('Anth',"ony's")),
        ([string]::Concat('PF4A','F9BH')),
        ([string]::Concat('40HS','JM4'))
    )
    foreach ($pattern in $personalPatterns) {
        Assert-True ($text -notmatch [regex]::Escape($pattern)) "Personal workstation artifact remains in reusable source: $pattern"
    }

    $decisionRegisterPath = Join-Path $repositoryRoot 'Software-Decision-Register.csv'
    Assert-True (-not (Test-Path -LiteralPath $decisionRegisterPath)) 'The retired workstation-state decision register returned to maintained source.'
    $currentGuidance = ($files | Where-Object { $_.Extension -eq '.md' } | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw
    }) -join "`n"
    Assert-True ($currentGuidance -notmatch 'Software-Decision-Register\.csv') 'Current guidance points to the retired workstation-state decision register.'
}
Invoke-Check 'Tracked source contains no high-confidence embedded secret' {
    $auditFiles = @(Get-ChildItem -LiteralPath $repositoryRoot -File) +
        @(Get-ChildItem -LiteralPath $scriptsRoot -File) +
        @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'app') -File) +
        @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'manifests') -File) +
        @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -File | Where-Object { $_.FullName -notmatch '\\(?:bin|obj)\\' }) +
        @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'installer') -Recurse -File | Where-Object { $_.FullName -notmatch '\\(?:bin|obj)\\' }) +
        @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'build') -File)
    $auditText = ($auditFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    foreach ($pattern in @('AKIA[0-9A-Z]{16}','gh[pousr]_[A-Za-z0-9]{20,}','-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----')) {
        Assert-True ($auditText -notmatch $pattern) "Potential embedded secret matched: $pattern"
    }
}
Invoke-Check 'Stale pre-rebrand preview evidence is not published' {
    foreach ($name in @('AV-Workstation-Toolkit-v1-preview.png','AV-Workstation-Toolkit-v1-minimum-preview.png')) {
        $path = Join-Path $repositoryRoot ('docs\images\' + $name)
        Assert-True (-not (Test-Path -LiteralPath $path)) "Stale UI preview is still present: $name"
    }
}
if (-not $CoreOnly) {
    Invoke-Check 'XAML parses and all required named controls load' {
        Add-Type -AssemblyName PresentationFramework
        [xml]$xaml = Get-Content -LiteralPath $xamlPath -Raw
        $reader = New-Object System.Xml.XmlNodeReader $xaml
        $window = [Windows.Markup.XamlReader]::Load($reader)
        try {
            foreach ($name in @('TopMenu','ExportPlanMenuItem','OpenLogsMenuItem','ExitMenuItem','RefreshPlanMenuItem','DiagnosticsMenuItem','CheckSystemMenuItem','SafetySecurityMenuItem','AboutMenuItem','SidebarScroll','QuickViewState','AllAppsButton','SelectMissingButton','SelectUpdatesButton','ClearSelectionButton','PackageGrid','SearchBox','CatalogPresetFilter','ManufacturerFilter','DisciplineFilter','DetailsButton','DiagnosticsButton','RefreshButton','GetPackageButton','InstallButton','UpdateButton','CancelButton','ActivityLog','RebootBanner','RecheckButton')) {
                Assert-True ($null -ne $window.FindName($name)) "Missing control: $name"
            }
            foreach ($name in @('ModePill','WingetPill','PrivilegePill')) {
                Assert-True ($null -eq $window.FindName($name)) "Primary header still exposes telemetry control: $name"
            }
            Assert-True ($window.MinWidth -le 1040 -and $window.MinHeight -le 760) 'Minimum window size exceeds the v1 accessibility target.'
            $menu = $window.FindName('TopMenu')
            Assert-Equal 4 $menu.Items.Count 'Conventional File/View/Tools/Help menu structure differs.'
            Assert-Equal '_File' ([string]$menu.Items[0].Header) 'File menu is missing.'
            Assert-Equal '_View' ([string]$menu.Items[1].Header) 'View menu is missing.'
            Assert-Equal '_Tools' ([string]$menu.Items[2].Header) 'Tools menu is missing.'
            Assert-Equal '_Help' ([string]$menu.Items[3].Header) 'Help menu is missing.'
            $sidebar = $window.FindName('SidebarScroll')
            Assert-Equal 'Auto' ([string]$sidebar.VerticalScrollBarVisibility) 'Sidebar overflow is not intentionally scrollable.'
            $packageGrid = $window.FindName('PackageGrid')
            Assert-Equal 'Auto' ([string][Windows.Controls.ScrollViewer]::GetHorizontalScrollBarVisibility($packageGrid)) 'Narrow-window horizontal access is disabled.'
            Assert-True $packageGrid.Columns[1].Width.IsStar 'Application column does not adapt to wide viewports.'
            Assert-True $packageGrid.Columns[7].Width.IsStar 'Purpose column does not adapt to wide viewports.'
            Assert-True (-not $packageGrid.Columns[0].CanUserSort -and -not $packageGrid.Columns[7].CanUserSort) 'Selection or purpose/restriction column allows sorting.'
            Assert-True $packageGrid.Columns[0].IsReadOnly 'Selection column still enters DataGrid edit mode before its checkbox can respond.'
            Assert-Equal '0,0,0,0' ([string]$packageGrid.Columns[0].CellStyle.Setters[0].Value) 'Selection cell padding reduces the checkbox hit target.'
            $selectionTemplate = $packageGrid.Columns[0].CellTemplate
            $selectionCheckbox = [Windows.Controls.CheckBox]$selectionTemplate.LoadContent()
            $selectableItem = [pscustomobject]@{ Selected=$false; CanSelect=$true; SelectionHint='Select this managed application for installation.' }
            $selectionCheckbox.DataContext = $selectableItem
            $window.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::DataBind)
            [void]$selectionCheckbox.ApplyTemplate()
            Assert-True $selectionCheckbox.IsEnabled 'Actionable application checkbox did not become enabled.'
            Assert-True $selectionCheckbox.GetBindingExpression([Windows.Controls.Primitives.ToggleButton]::IsCheckedProperty).ParentBinding.NotifyOnSourceUpdated 'Selection binding does not distinguish user changes from target refreshes.'
            $sourceUpdateState = [pscustomobject]@{ Count=0 }
            $selectionCheckbox.AddHandler([Windows.Data.Binding]::SourceUpdatedEvent,[System.EventHandler[Windows.Data.DataTransferEventArgs]]{
                param($sourceUpdatedSender,$sourceUpdatedEventArgs)
                $sourceUpdateState.Count++
            }.GetNewClosure(),$true)
            $selectionPeer = [Windows.Automation.Peers.CheckBoxAutomationPeer]::new($selectionCheckbox)
            $toggleProvider = $selectionPeer.GetPattern([Windows.Automation.Peers.PatternInterface]::Toggle)
            Assert-True ($null -ne $toggleProvider) 'Selection checkbox does not expose a standard toggle interaction.'
            Assert-True ($selectionCheckbox.MinWidth -ge 32 -and $selectionCheckbox.MinHeight -ge 40) 'Selection checkbox does not expose a full-cell pointer target.'
            $toggleProvider.Toggle()
            $window.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::DataBind)
            Assert-True ([bool]$selectionCheckbox.IsChecked) 'Selection checkbox does not accept a standard toggle interaction.'
            Assert-True ([bool]$selectableItem.Selected -and $sourceUpdateState.Count -eq 1) 'Selection checkbox toggle did not produce one model-source update.'
            $disabledItem = [pscustomobject]@{ Selected=$false; CanSelect=$false; SelectionHint='Selection is unavailable.' }
            $selectionCheckbox.DataContext = $disabledItem
            $window.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::DataBind)
            Assert-True (-not $selectionCheckbox.IsEnabled -and -not [bool]$disabledItem.Selected) 'A non-actionable catalog item became selectable.'
            Assert-Equal 'ApplicationSortKey|VendorSortKey|PrioritySortKey|StatusSortKey|VersionSortKey|RiskSortKey' (($packageGrid.Columns[1..6] | ForEach-Object SortMemberPath) -join '|') 'Sortable grid columns do not use the reviewed display sort keys.'
            $uiSource = Get-Content -LiteralPath (Join-Path $scriptsRoot 'Start-AVWorkstationToolkit.ps1') -Raw
            Assert-True ($uiSource -match 'Update-AVWorkstationToolkitGridLayout') 'Responsive narrow/wide DataGrid layout handler is missing.'
            Assert-True ($uiSource -notmatch "'Crestron'\s*\{|'Extron'\s*\{") 'Manufacturer-specific discipline branches remain in the UI filter.'
            Assert-True ($uiSource -match 'Show-AVWorkstationToolkitCatalogDetail' -and $uiSource -match 'Open-AVWorkstationToolkitOfficialCatalogUri') 'Read-only catalog detail and official-link boundaries are missing.'
            Assert-True ($uiSource -match 'MetadataVerificationState' -and $uiSource -match 'DistributionPolicy' -and $uiSource -match 'WorkflowCategories') 'Read-only catalog detail omits verification, distribution, or workflow metadata.'
            Assert-True ($uiSource -match 'Show-AVWorkstationToolkitDiagnostics' -and $uiSource -match 'Copy diagnostics' -and $uiSource -match 'Export diagnostics') 'Read-only diagnostics actions are missing.'
            Assert-True ($uiSource -match 'GetPackageButton\.ToolTip\s*=\s*\$deliveryHelp' -and $uiSource -match 'AutomationProperties\]::SetName\(\$controls\.GetPackageButton') 'Dynamic package handoff lacks contextual tooltip or accessibility text.'
            $xamlSource = Get-Content -LiteralPath $xamlPath -Raw
            Assert-True ($xamlSource -match 'Grid Background="\{TemplateBinding Background\}"' -and $xamlSource -match 'x:Key="GridSelectionCheckBox"' -and $xamlSource -match 'Property="MinWidth" Value="32"' -and $xamlSource -match 'Property="MinHeight" Value="40"' -and $xamlSource -match 'ToolTipService.ShowOnDisabled="True"') 'Checkbox hit target or disabled-state explanation regressed.'
            Assert-True ($xamlSource -match 'Content="\{TemplateBinding SelectionBoxItem\}"' -and $xamlSource -match '<Style TargetType="ComboBox">[\s\S]+?<Setter Property="Foreground" Value="#E8EEF8"') 'ComboBox template does not render its selected value with the dark-theme foreground.'
            $compiledXamlSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\MainWindow.xaml') -Raw
            Assert-True ($compiledXamlSource -match 'x:Name="FollowActivityCheckBox"[^>]+Content="Follow latest activity"[^>]+IsChecked="True"' -and
                $compiledXamlSource -match 'x:Name="ActivityLog"[^>]+TextChanged="ActivityLog_TextChanged"') 'Compiled activity follow/pause UI contract is incomplete.'
            Assert-True ($xamlSource -notmatch 'Safety boundary') 'Verbose safety policy remains in the primary sidebar.'
            Assert-True ($uiSource -match 'function Show-AVWorkstationToolkitSafetySecurity' -and $uiSource -match "Title = 'Safety & Security - AV Workstation Toolkit'") 'Safety and security content is not available from a read-only surface.'
            Assert-True ($uiSource -match 'function Show-AVWorkstationToolkitAbout' -and $uiSource -match "Title = 'About AV Workstation Toolkit'" -and $uiSource -match 'Version \{0\}  \|  \{1\}' -and $uiSource -match 'Mode=\$executionMode') 'Dark About dialog does not expose product version and package mode.'
            Assert-True ($xamlSource -notmatch 'ModePill|WingetPill|PrivilegePill|Standard user|winget \.\.\.') 'Primary header still contains normal runtime telemetry.'
            Assert-True ($uiSource -match 'prioritySortOrder\s*=\s*@\{\s*P1=0;\s*P2=1;\s*UTILITY=2;\s*DEV=3' -and
                $uiSource -match 'riskSortOrder\s*=\s*@\{\s*None=0;\s*Service=1;\s*Listener=2;\s*Driver=3' -and
                $uiSource -match 'UpdateAvailable=0;\s*Missing=1;\s*ManualUpdate=2;\s*Held=3;\s*Error=4') 'Domain-aware priority, risk, or attention-status ordering differs.'
            Assert-True ($uiSource -match 'function Get-AVWorkstationToolkitVersionSortKey' -and
                $uiSource -match 'UI_BEHAVIOR_OK sorting=6 quickViews=3 persistence=passed selectionToggle=realGrid' -and
                $uiSource -match 'UI_SELECTION_FLOW_OK sourceUpdates=\{0\} refresh=stable pendingReboot=lowRiskAllowed actions=shared') 'Version-aware sorting or deterministic real-grid UI behavior smoke coverage is missing.'
            Assert-True ($uiSource -match 'function Set-AVWorkstationToolkitQuickView' -and $uiSource -match 'function Test-AVWorkstationToolkitQuickViewFilter' -and
                $uiSource -match "AllAppsButton\.Add_Click\(\{ Set-AVWorkstationToolkitQuickView -View All \}\)" -and
                $uiSource -match 'Clear-AVWorkstationToolkitSelection') 'Composable quick views or independent selection clearing are missing.'
            Assert-True ($uiSource -match 'function Apply-AVWorkstationToolkitSort' -and $uiSource -match 'PackageGrid\.Add_Sorting' -and $uiSource -match 'SortDirection') 'Persistent sort handling or visible sort direction is missing.'
            Assert-True ($uiSource -match 'function Sync-AVWorkstationToolkitSelectionFromToggle' -and $uiSource -match 'Binding\]::SourceUpdatedEvent.+?Sync-AVWorkstationToolkitSelectionFromToggle') 'Grid checkbox source updates do not synchronize with the PowerShell-backed selection model.'
            Assert-True ($uiSource -match '\$ToggleEventArgs\.Property\s+-ne\s+\[Windows\.Controls\.Primitives\.ToggleButton\]::IsCheckedProperty' -and
                $uiSource -match '\$ToggleEventArgs\.TargetObject\s+-as\s+\[Windows\.Controls\.CheckBox\]' -and
                $uiSource -notmatch '\$ToggleEventArgs\.OriginalSource') 'Selection synchronization does not use the reviewed IsChecked binding target contract.'
            Assert-True ($uiSource -match 'DispatcherPriority\]::DataBind' -and $uiSource -match 'ReferenceEquals\(\$checkBox\.DataContext,\$item\)') 'Selection synchronization is not deferred safely across the WPF source-update ordering boundary.'
            Assert-True (([regex]::Matches($uiSource,'Get-AVWorkstationToolkitSelectedActionItems -Action')).Count -eq 3 -and
                $uiSource -match 'Action selection: action=\{0\}; count=\{1\}; packageIds=\{2\}') 'Footer/button state and action launch do not share the reviewed selected-action model or action trace.'
            Assert-True ($uiSource -notmatch 'PackageGrid\.AddHandler\([^\r\n]+?(CheckedEvent|UncheckedEvent)') 'Grid selection still reacts to binding-driven Checked/Unchecked events.'
            $coreSource = Get-Content -LiteralPath (Join-Path $scriptsRoot 'AVWorkstationToolkit.Core.psm1') -Raw
            Assert-True ($coreSource -match '\(''Version: \{0\}'' -f \$Diagnostics\.WinGet\.Version\)' -and $coreSource -match '\(''Launcher runtime: \{0\}''') 'WinGet or launcher runtime information is missing from Diagnostics.'
            Assert-True ($xamlSource -match 'Content="Check again"' -and $uiSource -match [regex]::Escape('Restart recommended. Windows is waiting for a restart to finish an update. You can still install most apps, but some system-level changes are paused until you restart.')) 'Pending-reboot guidance is not plain language.'
            Assert-True ($xamlSource -notmatch 'Component Based Servicing|listener actions|internal risk') 'Primary XAML exposes implementation-specific reboot terms.'
            Assert-True ($xamlSource -match '(?s)Header="Purpose / restriction".+?TextWrapping="Wrap".+?MaxHeight="36".+?ToolTip="\{Binding Note\}"') 'Purpose or restriction text is not wrapped with full-text access.'
            foreach ($wiring in @(
                'ExportPlanMenuItem\.Add_Click\(\{ Export-AVWorkstationToolkitPlan \}\)',
                'OpenLogsMenuItem\.Add_Click\(\{ Open-AVWorkstationToolkitLogs \}\)',
                'RefreshPlanMenuItem\.Add_Click\(\{ Refresh-AVWorkstationToolkitPlan \}\)',
                'DiagnosticsMenuItem\.Add_Click\(\{ Show-AVWorkstationToolkitDiagnostics \}\)',
                'CheckSystemMenuItem\.Add_Click\(\{ Refresh-AVWorkstationToolkitPlan \}\)',
                'SafetySecurityMenuItem\.Add_Click\(\{ Show-AVWorkstationToolkitSafetySecurity \}\)')) {
                Assert-True ($uiSource -match $wiring) "Menu action is not wired to the shared handler: $wiring"
            }
            Assert-True ($uiSource -match 'Add_PreviewKeyDown' -and $uiSource -match '\[Windows\.Input\.Key\]::F5[\s\S]+?Refresh-AVWorkstationToolkitPlan') 'Advertised F5 refresh shortcut is not wired.'
            $manufacturer = $window.FindName('ManufacturerFilter')
            Assert-True ($manufacturer.Foreground.Color.ToString() -eq '#FFE8EEF8') 'Closed ComboBox selected-value foreground differs.'
        }
        finally { $window.Close() }
    }
    Invoke-Check 'Compiled production layout contract covers all required viewports' {
        $compiledWindowSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\MainWindow.xaml.cs') -Raw
        foreach ($viewport in @('1040, 760','1280, 860','1440, 900','1920, 1080')) {
            Assert-True ($compiledWindowSource.Contains("new Size($viewport)")) "Compiled production smoke omits the $viewport viewport."
        }
        Assert-True ($compiledWindowSource -match 'VerifyClosedComboBoxLabels\(\)' -and
            $compiledWindowSource -match 'VerifyActivityFollowContract\(\)') 'Compiled production smoke omits current filter-label or activity-follow rendering behavior.'
    }
    Invoke-Check 'Worker rejects a synthetic path outside the resolved data root before file access' {
        $worker = Join-Path $scriptsRoot 'Invoke-AVWorkstationToolkitAction.ps1'
        $powershellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        # This file intentionally does not exist: containment must fail before
        # the worker performs any request-file existence or content check.
        $syntheticOutsideRequestPath = Join-Path $fixtureRoot 'synthetic-outside-request-does-not-exist.json'
        $oldPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $output = (& $powershellExe -NoProfile -ExecutionPolicy RemoteSigned -File $worker -RequestPath $syntheticOutsideRequestPath 2>&1 | Out-String)
            $exitCode = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $oldPreference }
        Assert-True ($exitCode -ne 0) 'Out-of-bound request returned success.'
        Assert-True ($output -match 'direct children of the resolved AV Workstation Toolkit logs\\requests directory') 'Expected request-boundary rejection was not reported.'
    }
    Invoke-Check 'Worker rejects unknown request properties before live planning' {
        $worker = Join-Path $scriptsRoot 'Invoke-AVWorkstationToolkitAction.ps1'
        $powershellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $requestRoot = Join-Path $repositoryRoot 'logs\requests'
        New-Item -ItemType Directory -Path $requestRoot -Force | Out-Null
        $requestName = 'request-20000101-000000-' + [guid]::NewGuid().ToString('N').Substring(0,8)
        $requestPath = Join-Path $requestRoot ($requestName + '.json')
        $request = [ordered]@{ SchemaVersion=1; RequestId=$requestName; Action='Install'; PackageIds=@('7zip.7zip'); RiskAcknowledged=$false; DryRun=$true; Unexpected='reject me' }
        $request | ConvertTo-Json | Set-Content -LiteralPath $requestPath -Encoding UTF8
        try {
            $oldPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $output = (& $powershellExe -NoProfile -ExecutionPolicy RemoteSigned -File $worker -RequestPath $requestPath 2>&1 | Out-String)
                $exitCode = $LASTEXITCODE
            }
            finally { $ErrorActionPreference = $oldPreference }
            Assert-True ($exitCode -ne 0) 'Unknown request property returned success.'
            Assert-True ($output -match 'unsupported properties') 'Strict request-schema rejection was not reported.'
        }
        finally {
            Get-ChildItem -LiteralPath $requestRoot -Filter ($requestName + '.*') -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host ''
Write-Host ("QA summary: {0} passed; {1} failed" -f $script:Passed,$script:Failed) -ForegroundColor Cyan
if ($script:Failed -gt 0) {
    Write-Host ($script:Failures -join "`n") -ForegroundColor Red
    exit 1
}
exit 0
