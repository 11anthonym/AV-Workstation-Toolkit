<#
.SYNOPSIS
    Launches the local AV Workstation Toolkit desktop frontend.

.DESCRIPTION
    Loads the WPF/XAML interface, builds a plan from the shared allowlist, and
    launches validated background action requests. The UI never accepts raw
    package IDs or winget arguments from the user.
#>

[CmdletBinding()]
param(
    [switch]$SmokeTest,
    [string]$RenderPreviewPath,
    [int]$RenderWidth = 0,
    [int]$RenderHeight = 0,
    [ValidateSet('All','Missing','Updates')][string]$RenderQuickView = 'All',
    [string]$DataRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'AV Workstation Toolkit requires an STA PowerShell process. Use Launch-AVWorkstationToolkit.cmd or run powershell.exe -STA.'
}
$launchIsElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($launchIsElevated) {
    Add-Type -AssemblyName PresentationFramework
    [Windows.MessageBox]::Show('For safety, AV Workstation Toolkit must be launched as a standard user. Close this copy and start it normally; individual installers can request elevation through Windows.','Standard-user launch required','OK','Warning') | Out-Null
    throw 'AV Workstation Toolkit refuses to load repository code in an elevated PowerShell process.'
}

Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if (-not [string]::IsNullOrWhiteSpace($RenderPreviewPath)) {
    [System.Windows.Media.RenderOptions]::ProcessRenderMode = [System.Windows.Interop.RenderMode]::SoftwareOnly
}
Import-Module (Join-Path $PSScriptRoot 'AVWorkstationToolkit.Core.psd1') -Force
Import-Module (Join-Path $PSScriptRoot 'AVWorkstationToolkit.Vendor.psm1') -Force

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$xamlPath = Join-Path $repositoryRoot 'app\AVWorkstationToolkit.xaml'
$sourceVersionPath = Join-Path $repositoryRoot 'VERSION'
$resolvedDataRoot = Get-AVWorkstationToolkitDataRoot -Path $DataRoot
$logsRoot = Join-Path $resolvedDataRoot 'logs'
$reportsRoot = Join-Path $resolvedDataRoot 'reports'
$executionMode = if ([Environment]::GetEnvironmentVariable('AVWORKSTATIONTOOLKIT_PACKAGED','Process') -eq '1') { 'Packaged app' } else { 'Source checkout' }
$productVersion = [Environment]::GetEnvironmentVariable('AVWORKSTATIONTOOLKIT_LAUNCHER_VERSION','Process')
if ([string]::IsNullOrWhiteSpace($productVersion) -and (Test-Path -LiteralPath $sourceVersionPath -PathType Leaf)) {
    $productVersion = (Get-Content -LiteralPath $sourceVersionPath -Raw).Trim()
}
if ([string]::IsNullOrWhiteSpace($productVersion)) { $productVersion = '1.1.1' }

$legacyMigration = $null
if ($executionMode -eq 'Packaged app') {
    try { $legacyMigration = Invoke-AVWorkstationToolkitLegacyDataMigration -DataRoot $resolvedDataRoot }
    catch {
        $legacyMigration = [pscustomobject]@{ Status='Failed'; MigratedFiles=0; Detail=Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message }
    }
}

if (-not (Test-Path -LiteralPath $xamlPath -PathType Leaf)) { throw "Frontend XAML not found: $xamlPath" }

[xml]$xaml = Get-Content -LiteralPath $xamlPath -Raw
$reader = New-Object System.Xml.XmlNodeReader $xaml
$window = [Windows.Markup.XamlReader]::Load($reader)

$controlNames = @(
    'TopMenu','ExportPlanMenuItem','OpenLogsMenuItem','ExitMenuItem','RefreshPlanMenuItem','DiagnosticsMenuItem','CheckSystemMenuItem','SafetySecurityMenuItem','AboutMenuItem',
    'RebootBanner','RebootText','RecheckButton','MainContent','SidebarScroll',
    'SearchBox','StandardFilter','FieldFilter','DeveloperFilter','OptionalFilter','CatalogPresetFilter','ManufacturerFilter','DisciplineFilter',
    'QuickViewState','AllAppsButton','SelectMissingButton','SelectUpdatesButton','ClearSelectionButton','CurrentCount','ActionCount',
    'PlanSummaryText','DetailsButton','DiagnosticsButton','RefreshButton','PackageGrid','ActivityState','ActivityLog','FooterGrid','SelectionPanel','ActionProgress',
    'SelectionSummary','ActionBar','GetPackageButton','CancelButton','InstallButton','UpdateButton'
)
$controls = @{}
foreach ($name in $controlNames) {
    $control = $window.FindName($name)
    if ($null -eq $control) { throw "Required XAML control not found: $name" }
    $controls[$name] = $control
}

$state = @{
    Busy = $false
    Plan = $null
    Items = New-Object 'System.Collections.ObjectModel.ObservableCollection[object]'
    View = $null
    Process = $null
    Timer = $null
    ProgressPath = ''
    ResultPath = ''
    CancelPath = ''
    ProgressByteOffset = [int64]0
    ProgressRemainder = ''
    ProgressReadWarningShown = $false
    Closing = $false
    UpdatingManufacturers = $false
    QuickView = 'All'
    SortMemberPath = ''
    SortDirection = $null
}

function Add-ActivityLine {
    param([string]$Message, [ValidateSet('Info','Success','Warning','Error')][string]$Level = 'Info')
    $Message = Protect-AVWorkstationToolkitSensitiveText -Text $Message
    if ($Message.Length -gt 4000) { $Message = $Message.Substring(0,4000) + '...[truncated]' }
    $prefix = switch ($Level) { 'Success' {'OK'} 'Warning' {'WARN'} 'Error' {'ERROR'} default {'INFO'} }
    $controls.ActivityLog.AppendText(('[{0}] {1,-5} {2}' -f (Get-Date -Format 'HH:mm:ss'),$prefix,$Message) + "`r`n")
    $controls.ActivityLog.ScrollToEnd()
}

function Get-MockAVWorkstationToolkitPlan {
    $catalog = @(Get-AVWorkstationToolkitCatalog)
    $wingetCatalog = @($catalog | Where-Object Provider -eq 'WinGet')
    $externalCatalog = @($catalog | Where-Object Provider -eq 'External')
    $installedLines = [System.Collections.Generic.List[string]]::new()
    $installedLines.Add('Name Id Version Available Source')
    $installedLines.Add('------------------------------------------------')
    foreach ($package in $wingetCatalog) {
        if ($package.Id -notin @('Notepad++.Notepad++','voidtools.Everything','RealVNC.VNCViewer')) {
            $available = if ($package.Id -in @('Microsoft.VisualStudioCode','PJO2.tftpd64')) { ' 1.1' } else { '' }
            $installedLines.Add(('{0} {1} 1.0{2} winget' -f $package.Name,$package.Id,$available))
        }
    }
    $upgradeText = @'
Name Id Version Available Source
------------------------------------------------
Visual Studio Code Microsoft.VisualStudioCode 1.0 1.1 winget
tftpd64 PJO2.tftpd64 1.0 1.1 winget
'@
    $reboot = [pscustomobject]@{ Pending=$false; Reasons=@(); Summary='No pending reboot signals' }
    $externalInventory = @($externalCatalog | ForEach-Object {
        [pscustomobject]@{ Id=$_.Id; Reliable=$true; Installed=$true; InstalledVersion='9.13.1'; InstalledVersions=@('9.13.1'); Detail='Preview fixture' }
    })
    $externalReleases = @($externalCatalog | ForEach-Object {
        [pscustomobject]@{ Id=$_.Id; AvailableVersion=$_.KnownVersion; ObservedVersion=$_.KnownVersion; OnlineChecked=($_.ReleaseMode -eq 'VendorPage'); OnlineAvailable=($_.ReleaseMode -eq 'VendorPage'); ReleaseUri=$_.ReleaseUri; DownloadUri=''; Detail='Preview fixture' }
    })
    Get-AVWorkstationToolkitPlan -InstalledText ($installedLines -join "`r`n") -UpgradeText $upgradeText -ExternalInventory $externalInventory -ExternalReleaseInfo $externalReleases -RebootState $reboot -WingetVersion 'v1.29-preview'
}

if ($null -ne $legacyMigration) {
    if ([string]$legacyMigration.Status -eq 'Completed' -and [int]$legacyMigration.MigratedFiles -gt 0) {
        Add-ActivityLine ("Migrated {0} validated legacy data file(s) into the new application data directory." -f $legacyMigration.MigratedFiles) -Level Success
    }
    elseif ([string]$legacyMigration.Status -in @('Failed','UnsafeLegacyState')) {
        $detail = if ($legacyMigration.PSObject.Properties.Name -contains 'Detail') { ': ' + [string]$legacyMigration.Detail } else { '.' }
        Add-ActivityLine ('Legacy data migration was skipped safely' + $detail) -Level Warning
    }
}

$prioritySortOrder = @{ P1=0; P2=1; UTILITY=2; DEV=3 }
$statusSortOrder = @{
    UpdateAvailable=0; Missing=1; ManualUpdate=2; Held=3; Error=4
    InventoryIncomplete=5; InventoryUnavailable=6; CheckUnavailable=7
    Current=8; Inventory=9; NotDetected=10; Manual=11; Awareness=12
}
$riskSortOrder = @{ None=0; Service=1; Listener=2; Driver=3 }

function Get-AVWorkstationToolkitSortRank {
    param([Parameter(Mandatory)][hashtable]$Order, [AllowEmptyString()][string]$Value)
    if ($Order.ContainsKey($Value)) { return [int]$Order[$Value] }
    return 99
}

function Get-AVWorkstationToolkitVersionSortKey {
    param([AllowEmptyString()][string]$Label)

    if ([string]::IsNullOrWhiteSpace($Label)) { return '4|' }
    $value = $Label.Trim()
    if ($value.StartsWith('Known: ',[StringComparison]::OrdinalIgnoreCase)) { $value = $value.Substring(7).Trim() }
    if ($value.Equals('Not installed',[StringComparison]::OrdinalIgnoreCase)) { return '2|' }
    if ($value.Equals('Not evaluated',[StringComparison]::OrdinalIgnoreCase)) { return '3|' }

    $match = [regex]::Match($value,'^\s*[vV]?(?<numeric>\d+(?:\.\d+){0,7})(?<suffix>(?:[-+][0-9A-Za-z.-]+)?)\s*$')
    if (-not $match.Success) { return '1|' + $value.ToUpperInvariant() }

    $segments = [System.Collections.Generic.List[string]]::new()
    $parts = @($match.Groups['numeric'].Value.Split('.'))
    for ($index = 0; $index -lt 8; $index++) {
        $part = if ($index -lt $parts.Count) { $parts[$index].TrimStart('0') } else { '' }
        if ([string]::IsNullOrEmpty($part)) { $part = '0' }
        $segments.Add(('{0:D3}:{1}' -f $part.Length,$part))
    }
    return '0|' + ($segments -join '|') + '|' + $match.Groups['suffix'].Value.ToUpperInvariant()
}

function ConvertTo-DisplayItem {
    param([Parameter(Mandatory)]$Package)

    $visual = switch ($Package.Status) {
        'Current'         { @('Current',          '#11372D','#226C57','#66E1B5') }
        'Missing'         { @('Missing',          '#123454','#245B8D','#79BEFF') }
        'UpdateAvailable' { @('Update available', '#33215C','#6745A2','#C4A4FF') }
        'ManualUpdate'    { @('Manual update',    '#3B2B13','#7A5B20','#FFD27A') }
        'Held'            { @('Held',             '#3C2C10','#805D17','#FFD27A') }
        'Manual'          { @('Manual',           '#252D39','#485568','#B8C3D2') }
        'Inventory'       { @('Detected',         '#173349','#2C617E','#8BD2FF') }
        'NotDetected'     { @('Not detected',     '#252D39','#485568','#B8C3D2') }
        'InventoryIncomplete' { @('Inventory incomplete','#3B2B13','#7A5B20','#FFD27A') }
        'InventoryUnavailable' { @('Inventory unavailable','#2D2B45','#55517A','#C8C3FF') }
        'CheckUnavailable' { @('Check unavailable','#2D2B45','#55517A','#C8C3FF') }
        'Awareness'       { @('Catalog only',     '#1D2F45','#365A7B','#9AC7EF') }
        'Error'           { @('Error',            '#451A22','#893044','#FF9AAA') }
        default           { @($Package.Status,    '#252D39','#485568','#B8C3D2') }
    }
    $risk = switch ($Package.Risk) {
        'Driver'   { @('Driver','#FFB86B') }
        'Service'  { @('Service','#E6A6FF') }
        'Listener' { @('Listener','#FF9C9C') }
        default    { @('Low','#7FCFAF') }
    }
    $versionLabel = if ($Package.Status -eq 'Awareness' -and -not [string]::IsNullOrWhiteSpace([string]$Package.KnownVersion)) {
        'Known: ' + [string]$Package.KnownVersion
    }
    elseif ($Package.Status -eq 'Awareness') { 'Not evaluated' }
    elseif ([string]::IsNullOrWhiteSpace($Package.InstalledVersion)) { 'Not installed' }
    else { $Package.InstalledVersion }
    $availableLabel = if ([string]::IsNullOrWhiteSpace($Package.AvailableVersion)) { '' } else { '-> ' + $Package.AvailableVersion }
    $stableSortKey = ([string]$Package.Id).ToUpperInvariant()

    $selectionHint = if ($Package.CanSelect) {
        if ($Package.Action -eq 'Update') { 'Select this managed application for update.' }
        else { 'Select this managed application for installation.' }
    }
    else {
        'Selection is unavailable because this item has no currently permitted automated action. ' + [string]$Package.StatusDetail
    }

    [pscustomobject]@{
        Selected = [bool]$Package.Selected
        CanSelect = [bool]$Package.CanSelect
        SelectionHint = $selectionHint.Trim()
        Installed = [bool]$Package.Installed
        Order = $Package.Order
        Profile = $Package.Profile
        Name = $Package.Name
        Id = $Package.Id
        Provider = $Package.Provider
        KnownVersion = $Package.KnownVersion
        Risk = $Package.Risk
        RiskLabel = $risk[0]
        RiskForeground = $risk[1]
        Note = $Package.Note
        Status = $Package.Status
        StatusLabel = $visual[0]
        StatusBrush = $visual[1]
        StatusBorder = $visual[2]
        StatusForeground = $visual[3]
        StatusDetail = $Package.StatusDetail
        InventoryQuality = $Package.InventoryQuality
        Action = $Package.Action
        InstalledVersion = $Package.InstalledVersion
        AvailableVersion = $Package.AvailableVersion
        VersionLabel = $versionLabel
        AvailableLabel = $availableLabel
        ReleaseCheckDetail = $Package.ReleaseCheckDetail
        ReleaseMode = $Package.ReleaseMode
        ReleaseUri = $Package.ReleaseUri
        ReleaseChannel = $Package.ReleaseChannel
        DeliveryMode = $Package.DeliveryMode
        DeliveryAction = $Package.DeliveryAction
        DeliveryAvailable = [bool]$Package.DeliveryAvailable
        DeliveryLabel = $Package.DeliveryLabel
        DeliveryUri = $Package.DeliveryUri
        DeliveryPath = $Package.DeliveryPath
        DeliveryDetail = $Package.DeliveryDetail
        DeliveryProviderId = $Package.DeliveryProviderId
        DeliveryProductId = $Package.DeliveryProductId
        Vendor = $Package.Vendor
        ProductFamily = $Package.ProductFamily
        ApplicationType = @($Package.ApplicationType)
        SupportedOS = @($Package.SupportedOS)
        Priority = $Package.Priority
        Roles = @($Package.Roles)
        DeploymentClass = $Package.DeploymentClass
        CatalogMaintenancePolicy = $Package.CatalogMaintenancePolicy
        VersionRule = $Package.VersionRule
        VersionCoupling = $Package.VersionCoupling
        VersionCouplingTargetId = $Package.VersionCouplingTargetId
        VersionCouplingNotes = $Package.VersionCouplingNotes
        CurrentOrLegacy = $Package.CurrentOrLegacy
        LicensingModel = @($Package.LicensingModel)
        DownloadAccess = @($Package.DownloadAccess)
        DownloadDifficulty = $Package.DownloadDifficulty
        RequiresVendorAccount = $Package.RequiresVendorAccount
        RequiresDealerAccount = $Package.RequiresDealerAccount
        RequiresTraining = $Package.RequiresTraining
        RequiresLicense = $Package.RequiresLicense
        RequiresSubscription = $Package.RequiresSubscription
        InstallsDriver = $Package.InstallsDriver
        InstallsService = $Package.InstallsService
        OpensListener = $Package.OpensListener
        FirmwareUtility = $Package.FirmwareUtility
        Architecture = @($Package.Architecture)
        CatalogTags = @($Package.CatalogTags)
        SideBySideSupported = $Package.SideBySideSupported
        OfficialDownloadUri = $Package.OfficialDownloadUri
        OfficialProductUri = $Package.OfficialProductUri
        ValidationMethod = @($Package.ValidationMethod)
        CatalogNotes = $Package.CatalogNotes
        DistributionPolicy = $Package.DistributionPolicy
        WorkflowCategories = @($Package.WorkflowCategories)
        InstallationForms = @($Package.InstallationForms)
        MetadataVerifiedOn = $Package.MetadataVerifiedOn
        MetadataVerificationState = $Package.MetadataVerificationState
        MetadataReviewTriggers = @($Package.MetadataReviewTriggers)
        MetadataQuarantined = $Package.MetadataQuarantined
        MetadataQuarantineReason = $Package.MetadataQuarantineReason
        AuthoritativeDomain = $Package.AuthoritativeDomain
        ExpectedPublisher = $Package.ExpectedPublisher
        SignatureValidation = $Package.SignatureValidation
        VendorHashAvailability = $Package.VendorHashAvailability
        DownloadStrategy = $Package.DownloadStrategy
        SearchText = (@($Package.Name,$Package.Id,$Package.Vendor,$Package.ProductFamily,$Package.Note,$Package.CatalogNotes,$Package.DistributionPolicy,$Package.AuthoritativeDomain) + @($Package.ApplicationType) + @($Package.Roles) + @($Package.SupportedOS) + @($Package.CatalogTags) + @($Package.WorkflowCategories) + @($Package.InstallationForms)) -join ' '
        StableSortKey = $stableSortKey
        ApplicationSortKey = (([string]$Package.Name).ToUpperInvariant() + '|' + $stableSortKey)
        VendorSortKey = (([string]$Package.Vendor).ToUpperInvariant() + '|' + $stableSortKey)
        PrioritySortKey = Get-AVWorkstationToolkitSortRank -Order $prioritySortOrder -Value ([string]$Package.Priority)
        StatusSortKey = Get-AVWorkstationToolkitSortRank -Order $statusSortOrder -Value ([string]$Package.Status)
        VersionSortKey = Get-AVWorkstationToolkitVersionSortKey -Label $versionLabel
        RiskSortKey = Get-AVWorkstationToolkitSortRank -Order $riskSortOrder -Value ([string]$Package.Risk)
    }
}

function Get-EnabledProfiles {
    $profiles = [System.Collections.Generic.List[string]]::new()
    if ($controls.StandardFilter.IsChecked) { $profiles.Add('Standard') }
    if ($controls.FieldFilter.IsChecked) { $profiles.Add('Field') }
    if ($controls.DeveloperFilter.IsChecked) { $profiles.Add('Developer') }
    if ($controls.OptionalFilter.IsChecked) { $profiles.Add('Optional') }
    return @($profiles)
}

function Get-AVWorkstationToolkitSelectedActionItems {
    param([Parameter(Mandatory)][ValidateSet('Install','Update')][string]$Action)

    return @($state.Items | Where-Object {
        [bool]$_.Selected -and [bool]$_.CanSelect -and [string]$_.Action -eq $Action
    })
}

function Update-SelectionState {
    $install = @(Get-AVWorkstationToolkitSelectedActionItems -Action Install)
    $update = @(Get-AVWorkstationToolkitSelectedActionItems -Action Update)
    $total = $install.Count + $update.Count
    $rebootPending = $null -ne $state.Plan -and $state.Plan.Reboot.Pending
    $unsafeElevation = $null -ne $state.Plan -and $state.Plan.Elevated
    $installBlockedByReboot = $rebootPending -and @($install | Where-Object Risk -ne 'None').Count -gt 0
    $updateBlockedByReboot = $rebootPending -and @($update | Where-Object Risk -ne 'None').Count -gt 0

    $controls.InstallButton.Content = if ($install.Count -gt 0) { "Install selected ($($install.Count))" } else { 'Install selected' }
    $controls.UpdateButton.Content = if ($update.Count -gt 0) { "Update selected ($($update.Count))" } else { 'Update selected' }
    $controls.InstallButton.IsEnabled = -not $state.Busy -and -not $installBlockedByReboot -and -not $unsafeElevation -and $install.Count -gt 0
    $controls.UpdateButton.IsEnabled = -not $state.Busy -and -not $updateBlockedByReboot -and -not $unsafeElevation -and $update.Count -gt 0
    $controls.SelectionSummary.Text = if ($total -eq 0) { 'Nothing selected' } else { "$total selected | $($install.Count) install | $($update.Count) update" }
    Update-DeliveryState
}

function Sync-AVWorkstationToolkitSelectionFromToggle {
    param([Parameter(Mandatory)]$ToggleEventArgs)

    if ($ToggleEventArgs.Property -ne [Windows.Controls.Primitives.ToggleButton]::IsCheckedProperty) { return }
    $checkBox = $ToggleEventArgs.TargetObject -as [Windows.Controls.CheckBox]
    if ($null -eq $checkBox -or $null -eq $checkBox.DataContext) { return }
    $item = $checkBox.DataContext
    if ($item.PSObject.Properties.Name -notcontains 'Selected' -or $item.PSObject.Properties.Name -notcontains 'CanSelect') { return }

    # SourceUpdated is raised while WPF is still completing the target toggle.
    # Reading IsChecked inline can therefore observe the previous value even
    # though the visual check mark changes immediately after the event returns.
    # Queue exactly one synchronization after the binding turn and retain the
    # original row identity so virtualization cannot redirect the update.
    [void]$checkBox.Dispatcher.BeginInvoke(
        [Windows.Threading.DispatcherPriority]::DataBind,
        [Action]{
            if (-not [object]::ReferenceEquals($checkBox.DataContext,$item)) { return }
            $requested = [bool]$checkBox.IsChecked
            if ($requested -and ($state.Busy -or -not [bool]$item.CanSelect)) {
                $item.Selected = $false
                $checkBox.IsChecked = $false
            }
            else {
                $item.Selected = $requested
            }
            Update-SelectionState
        }.GetNewClosure())
}

function Update-DeliveryState {
    $item = $controls.PackageGrid.SelectedItem
    $controls.DetailsButton.IsEnabled = -not $state.Busy -and $null -ne $item
    $available = $null -ne $item -and [bool]$item.DeliveryAvailable
    $controls.GetPackageButton.IsEnabled = -not $state.Busy -and $available
    $controls.GetPackageButton.Content = if ($available -and -not [string]::IsNullOrWhiteSpace([string]$item.DeliveryLabel)) {
        [string]$item.DeliveryLabel
    }
    else { 'Get package' }
    $deliveryHelp = if ($available -and -not [string]::IsNullOrWhiteSpace([string]$item.DeliveryDetail)) {
        [string]$item.DeliveryDetail
    }
    else { 'Select an external application with an available manual handoff.' }
    $controls.GetPackageButton.ToolTip = $deliveryHelp
    [Windows.Automation.AutomationProperties]::SetName($controls.GetPackageButton,([string]$controls.GetPackageButton.Content + '. ' + $deliveryHelp))
}

function ConvertTo-AVWorkstationToolkitDetailValue {
    param([AllowNull()]$Value)

    if ($null -eq $Value) { return 'Unknown' }
    if ($Value -is [bool]) { return $(if ($Value) { 'Yes' } else { 'No' }) }
    if ($Value -is [Array]) {
        $values = @($Value | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        return $(if ($values.Count -eq 0) { 'Unknown' } else { $values -join ', ' })
    }
    $text = [string]$Value
    return $(if ([string]::IsNullOrWhiteSpace($text)) { 'Unknown' } else { $text.Trim() })
}

function Open-AVWorkstationToolkitOfficialCatalogUri {
    param([Parameter(Mandatory)][string]$Value)

    $uri = $null
    if (-not [uri]::TryCreate($Value,[UriKind]::Absolute,[ref]$uri) -or
        $uri.Scheme -ne [Uri]::UriSchemeHttps -or -not [string]::IsNullOrEmpty($uri.UserInfo)) {
        throw 'The catalogued official address is not a safe HTTPS URI.'
    }
    Open-AVWorkstationToolkitHttpsUri -Uri $uri.AbsoluteUri
}

function Show-AVWorkstationToolkitCatalogDetail {
    param([switch]$BuildOnly)

    $item = $controls.PackageGrid.SelectedItem
    if ($null -eq $item -or $state.Busy) { return }

    $versionCoupling = ConvertTo-AVWorkstationToolkitDetailValue $item.VersionCoupling
    if (-not [string]::IsNullOrWhiteSpace([string]$item.VersionCouplingTargetId)) {
        $versionCoupling += ' -> ' + [string]$item.VersionCouplingTargetId
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$item.VersionCouplingNotes)) {
        $versionCoupling += ' (' + [string]$item.VersionCouplingNotes + ')'
    }
    $knownVersion = if ([string]::IsNullOrWhiteSpace([string]$item.AvailableVersion)) { $item.KnownVersion } else { $item.AvailableVersion }
    $detailLines = @(
        'Identity'
        '  Name: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.Name)
        '  Package ID: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.Id)
        '  Manufacturer: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.Vendor)
        '  Product family: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.ProductFamily)
        '  Purpose / restriction: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.Note)
        '  Application type: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.ApplicationType)
        '  Priority: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.Priority)
        '  Roles: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.Roles)
        '  Workflows: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.WorkflowCategories)
        ''
        'Workstation state'
        '  Catalog status: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.StatusLabel)
        '  Installed: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.Installed)
        '  Installed version: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.InstalledVersion)
        '  Available / known version: ' + (ConvertTo-AVWorkstationToolkitDetailValue $knownVersion)
        '  Status detail: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.StatusDetail)
        '  Inventory quality: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.InventoryQuality)
        ''
        'Policy and compatibility'
        '  Provider: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.Provider)
        '  Deployment class: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.DeploymentClass)
        '  Maintenance policy: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.CatalogMaintenancePolicy)
        '  Version rule: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.VersionRule)
        '  Version coupling: ' + $versionCoupling
        '  Lifecycle: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.CurrentOrLegacy)
        '  Side-by-side supported: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.SideBySideSupported)
        ''
        'Access and platform'
        '  Licensing: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.LicensingModel)
        '  Download access: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.DownloadAccess)
        '  Download difficulty: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.DownloadDifficulty)
        '  Distribution policy: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.DistributionPolicy)
        '  Installation forms: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.InstallationForms)
        '  Vendor account required: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.RequiresVendorAccount)
        '  Dealer account required: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.RequiresDealerAccount)
        '  Training required: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.RequiresTraining)
        '  License required: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.RequiresLicense)
        '  Subscription required: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.RequiresSubscription)
        '  Supported OS: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.SupportedOS)
        '  Architecture: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.Architecture)
        ''
        'System impact'
        '  Installs driver: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.InstallsDriver)
        '  Installs service: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.InstallsService)
        '  Opens listener: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.OpensListener)
        '  Firmware utility: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.FirmwareUtility)
        ''
        'Evidence'
        '  Validation: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.ValidationMethod)
        '  Metadata verified on: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.MetadataVerifiedOn)
        '  Verification state: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.MetadataVerificationState)
        '  Review triggers: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.MetadataReviewTriggers)
        '  Quarantine reason: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.MetadataQuarantineReason)
        '  Authoritative domain: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.AuthoritativeDomain)
        '  Expected publisher: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.ExpectedPublisher)
        '  Signature validation: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.SignatureValidation)
        '  Vendor hash availability: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.VendorHashAvailability)
        '  Download strategy: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.DownloadStrategy)
        '  Release check: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.ReleaseCheckDetail)
        '  Delivery: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.DeliveryDetail)
        '  Notes: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.CatalogNotes)
        '  Official product: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.OfficialProductUri)
        '  Official download: ' + (ConvertTo-AVWorkstationToolkitDetailValue $item.OfficialDownloadUri)
    )

    $brushConverter = [Windows.Media.BrushConverter]::new()
    $dialog = [Windows.Window]::new()
    $dialog.Title = 'Application details - ' + [string]$item.Name
    if ($window.IsVisible) { $dialog.Owner = $window }
    $dialog.WindowStartupLocation = 'CenterOwner'
    $dialog.Width = 760
    $dialog.Height = 700
    $dialog.MinWidth = 560
    $dialog.MinHeight = 460
    $dialog.Background = $brushConverter.ConvertFromString('#0A0F1C')
    $dialog.Foreground = $brushConverter.ConvertFromString('#E8EEF8')
    $dialog.FontFamily = 'Segoe UI'
    $dialog.FontSize = 13

    $layout = [Windows.Controls.Grid]::new()
    $layout.Margin = [Windows.Thickness]::new(20)
    foreach ($height in @('Auto','*','Auto')) {
        $row = [Windows.Controls.RowDefinition]::new()
        $row.Height = [Windows.GridLengthConverter]::new().ConvertFromString($height)
        $layout.RowDefinitions.Add($row)
    }

    $heading = [Windows.Controls.StackPanel]::new()
    $title = [Windows.Controls.TextBlock]::new()
    $title.Text = [string]$item.Name
    $title.FontSize = 22
    $title.FontWeight = 'SemiBold'
    $subtitle = [Windows.Controls.TextBlock]::new()
    $subtitle.Text = ('{0} | {1} | {2}' -f (ConvertTo-AVWorkstationToolkitDetailValue $item.Vendor),(ConvertTo-AVWorkstationToolkitDetailValue $item.StatusLabel),(ConvertTo-AVWorkstationToolkitDetailValue $item.DeploymentClass))
    $subtitle.Foreground = $brushConverter.ConvertFromString('#94A4BB')
    $subtitle.Margin = [Windows.Thickness]::new(0,5,0,12)
    [void]$heading.Children.Add($title)
    [void]$heading.Children.Add($subtitle)
    [Windows.Controls.Grid]::SetRow($heading,0)
    [void]$layout.Children.Add($heading)

    $detail = [Windows.Controls.TextBox]::new()
    $detail.Text = $detailLines -join [Environment]::NewLine
    $detail.IsReadOnly = $true
    $detail.AcceptsReturn = $true
    $detail.TextWrapping = 'Wrap'
    $detail.VerticalScrollBarVisibility = 'Auto'
    $detail.HorizontalScrollBarVisibility = 'Disabled'
    $detail.Background = $brushConverter.ConvertFromString('#0C1423')
    $detail.Foreground = $brushConverter.ConvertFromString('#DDE6F3')
    $detail.BorderBrush = $brushConverter.ConvertFromString('#30415D')
    $detail.Padding = [Windows.Thickness]::new(14)
    $detail.FontFamily = 'Segoe UI'
    [Windows.Controls.Grid]::SetRow($detail,1)
    [void]$layout.Children.Add($detail)

    $buttons = [Windows.Controls.StackPanel]::new()
    $buttons.Orientation = 'Horizontal'
    $buttons.HorizontalAlignment = 'Right'
    $buttons.Margin = [Windows.Thickness]::new(0,14,0,0)
    foreach ($link in @(
        @{ Label='Open product page'; Uri=[string]$item.OfficialProductUri },
        @{ Label='Open download page'; Uri=[string]$item.OfficialDownloadUri }
    )) {
        if (-not [string]::IsNullOrWhiteSpace($link.Uri)) {
            $button = [Windows.Controls.Button]::new()
            $button.Content = $link.Label
            $button.Margin = [Windows.Thickness]::new(0,0,8,0)
            $sharedButtonStyle = $window.TryFindResource([Windows.Controls.Button])
            if ($null -ne $sharedButtonStyle) { $button.Style = $sharedButtonStyle }
            $uriValue = $link.Uri
            $button.Add_Click({
                try { Open-AVWorkstationToolkitOfficialCatalogUri -Value $uriValue }
                catch { [Windows.MessageBox]::Show($_.Exception.Message,'Unable to open link','OK','Error') | Out-Null }
            }.GetNewClosure())
            [void]$buttons.Children.Add($button)
        }
    }
    $close = [Windows.Controls.Button]::new()
    $close.Content = 'Close'
    $close.IsDefault = $true
    $close.IsCancel = $true
    $close.MinWidth = 90
    $sharedButtonStyle = $window.TryFindResource([Windows.Controls.Button])
    if ($null -ne $sharedButtonStyle) { $close.Style = $sharedButtonStyle }
    $close.Add_Click({ $dialog.Close() })
    [void]$buttons.Children.Add($close)
    [Windows.Controls.Grid]::SetRow($buttons,2)
    [void]$layout.Children.Add($buttons)
    $dialog.Content = $layout
    if ($BuildOnly) { return $dialog }
    [void]$dialog.ShowDialog()
}

function Set-AVWorkstationToolkitBusy {
    param([bool]$Busy, [string]$Message = '')
    $state.Busy = $Busy
    $controls.ActionProgress.Visibility = if ($Busy) { 'Visible' } else { 'Collapsed' }
    $canCancel = $Busy -and $null -ne $state.Process -and -not [string]::IsNullOrWhiteSpace($state.CancelPath) -and -not (Test-Path -LiteralPath $state.CancelPath)
    $controls.CancelButton.Visibility = if ($Busy -and $null -ne $state.Process) { 'Visible' } else { 'Collapsed' }
    $controls.CancelButton.IsEnabled = $canCancel
    $controls.RefreshButton.IsEnabled = -not $Busy
    $controls.DiagnosticsButton.IsEnabled = -not $Busy -and $null -ne $state.Plan
    $controls.RecheckButton.IsEnabled = -not $Busy
    $controls.RefreshPlanMenuItem.IsEnabled = -not $Busy
    $controls.CheckSystemMenuItem.IsEnabled = -not $Busy
    $controls.DiagnosticsMenuItem.IsEnabled = -not $Busy -and $null -ne $state.Plan
    $controls.ExportPlanMenuItem.IsEnabled = -not $Busy -and $null -ne $state.Plan
    $controls.AllAppsButton.IsEnabled = -not $Busy
    $controls.SelectMissingButton.IsEnabled = -not $Busy
    $controls.SelectUpdatesButton.IsEnabled = -not $Busy
    $controls.ClearSelectionButton.IsEnabled = -not $Busy
    $controls.GetPackageButton.IsEnabled = $false
    $controls.ActivityState.Text = if ($Busy) { $Message } else { 'Ready' }
    Update-SelectionState
    $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
}

function Update-AVWorkstationToolkitFilter {
    if ($null -ne $state.View) {
        $state.View.Refresh()
        Update-AVWorkstationToolkitPlanSummary
    }
    Update-AVWorkstationToolkitQuickViewAppearance
}

function Update-AVWorkstationToolkitQuickViewAppearance {
    $inactiveStyle = $window.FindResource('QuickViewButton')
    $activeStyle = $window.FindResource('ActiveQuickViewButton')
    $controls.AllAppsButton.Style = if ($state.QuickView -eq 'All') { $activeStyle } else { $inactiveStyle }
    $controls.SelectMissingButton.Style = if ($state.QuickView -eq 'Missing') { $activeStyle } else { $inactiveStyle }
    $controls.SelectUpdatesButton.Style = if ($state.QuickView -eq 'Updates') { $activeStyle } else { $inactiveStyle }
    $controls.QuickViewState.Text = switch ($state.QuickView) {
        'Missing' { 'Showing: Missing apps' }
        'Updates' { 'Showing: Available updates' }
        default { 'Showing: All apps' }
    }
}

function Test-AVWorkstationToolkitQuickViewFilter {
    param([Parameter(Mandatory)]$Item)

    switch ($state.QuickView) {
        'Missing' {
            return $Item.Status -eq 'Missing' -or
                (-not $Item.Installed -and $Item.Status -in @('Manual','NotDetected','Held'))
        }
        'Updates' {
            return $Item.Status -in @('UpdateAvailable','ManualUpdate') -or
                ($Item.Status -eq 'Held' -and $Item.Installed -and -not [string]::IsNullOrWhiteSpace([string]$Item.AvailableVersion))
        }
        default { return $true }
    }
}

function Clear-AVWorkstationToolkitSelection {
    foreach ($item in @($state.Items)) { $item.Selected = $false }
    $controls.PackageGrid.Items.Refresh()
    Update-SelectionState
}

function Set-AVWorkstationToolkitQuickView {
    param([ValidateSet('All','Missing','Updates')][string]$View)

    $state.QuickView = $View
    Update-AVWorkstationToolkitFilter
    if ($View -eq 'All') { return }

    foreach ($item in @($state.Items)) { $item.Selected = $false }
    $requiredAction = if ($View -eq 'Missing') { 'Install' } else { 'Update' }
    foreach ($item in @($state.View)) {
        if ($item.CanSelect -and $item.Action -eq $requiredAction) { $item.Selected = $true }
    }
    $controls.PackageGrid.Items.Refresh()
    Update-SelectionState
}

function Apply-AVWorkstationToolkitSort {
    if ($null -eq $state.View) { return }
    $state.View.SortDescriptions.Clear()
    foreach ($column in @($controls.PackageGrid.Columns)) { $column.SortDirection = $null }
    if ([string]::IsNullOrWhiteSpace([string]$state.SortMemberPath)) { return }

    $direction = [ComponentModel.ListSortDirection][Enum]::Parse([ComponentModel.ListSortDirection],[string]$state.SortDirection)
    $state.View.SortDescriptions.Add([ComponentModel.SortDescription]::new($state.SortMemberPath,$direction))
    if ($state.SortMemberPath -ne 'StableSortKey') {
        $state.View.SortDescriptions.Add([ComponentModel.SortDescription]::new('StableSortKey',[ComponentModel.ListSortDirection]::Ascending))
    }
    $column = @($controls.PackageGrid.Columns | Where-Object SortMemberPath -eq $state.SortMemberPath | Select-Object -First 1)
    if ($column.Count -gt 0) { $column[0].SortDirection = $direction }
}

function Set-AVWorkstationToolkitSort {
    param(
        [Parameter(Mandatory)][string]$MemberPath,
        [ValidateSet('Ascending','Descending')][string]$Direction
    )

    if ([string]::IsNullOrWhiteSpace($Direction)) {
        $Direction = if ($state.SortMemberPath -eq $MemberPath -and $state.SortDirection -eq 'Ascending') { 'Descending' } else { 'Ascending' }
    }
    $state.SortMemberPath = $MemberPath
    $state.SortDirection = $Direction
    Apply-AVWorkstationToolkitSort
}

function Get-AVWorkstationToolkitFilterKey {
    param([Parameter(Mandatory)]$Control)
    if ($null -eq $Control.SelectedItem -or $null -eq $Control.SelectedItem.Tag) { return 'All' }
    return [string]$Control.SelectedItem.Tag
}

function Update-AVWorkstationToolkitManufacturerFilter {
    $selectedVendor = Get-AVWorkstationToolkitFilterKey -Control $controls.ManufacturerFilter
    $vendors = @(Get-AVWorkstationToolkitCatalogVendors -Catalog $state.Items)
    $selectedVendor = Resolve-AVWorkstationToolkitCatalogVendorSelection -Catalog $state.Items -SelectedVendor $selectedVendor
    $state.UpdatingManufacturers = $true
    try {
        $controls.ManufacturerFilter.Items.Clear()
        foreach ($entry in @(@{ Content='All manufacturers'; Tag='All' }) + @($vendors | ForEach-Object { @{ Content=$_; Tag=$_ } })) {
            $item = [Windows.Controls.ComboBoxItem]::new()
            $item.Content = [string]$entry.Content
            $item.Tag = [string]$entry.Tag
            [void]$controls.ManufacturerFilter.Items.Add($item)
        }
        $retained = @($controls.ManufacturerFilter.Items | Where-Object { ([string]$_.Tag).Equals($selectedVendor,[StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)
        $controls.ManufacturerFilter.SelectedItem = if ($retained.Count -gt 0) { $retained[0] } else { $controls.ManufacturerFilter.Items[0] }
    }
    finally { $state.UpdatingManufacturers = $false }
}

function Test-AVWorkstationToolkitUiFilter {
    param([Parameter(Mandatory)]$Item)
    $catalogMatch = Test-AVWorkstationToolkitCatalogFilter -Item $Item -Profiles @(Get-EnabledProfiles) `
        -Preset (Get-AVWorkstationToolkitFilterKey -Control $controls.CatalogPresetFilter) `
        -Vendor (Get-AVWorkstationToolkitFilterKey -Control $controls.ManufacturerFilter) `
        -Discipline (Get-AVWorkstationToolkitFilterKey -Control $controls.DisciplineFilter) `
        -Search $controls.SearchBox.Text
    return $catalogMatch -and (Test-AVWorkstationToolkitQuickViewFilter -Item $Item)
}

function Update-AVWorkstationToolkitPlanSummary {
    if ($null -eq $state.Plan) { return }
    $visible = 0
    if ($null -ne $state.View) { foreach ($item in $state.View) { $visible++ } }
    $summary = $state.Plan.Summary
    $inventoryWarnings = $summary.InventoryIncomplete + $summary.InventoryUnavailable + $summary.CheckUnavailable
    $controls.PlanSummaryText.Text = ('{0} shown of {1} | {2} current | {3} managed actions | {4} manual | {5} inventory | {6} warnings | {7} awareness' -f $visible,$summary.Total,$summary.Current,($summary.Missing+$summary.Updates),($summary.Manual+$summary.ManualUpdates+$summary.Held),($summary.Inventory+$summary.NotDetected),$inventoryWarnings,$summary.Awareness)
}

function Update-AVWorkstationToolkitGridLayout {
    if ($null -eq $controls.PackageGrid -or $controls.PackageGrid.Columns.Count -lt 8) { return }
    $wideLayout = $controls.PackageGrid.ActualWidth -ge 920
    [Windows.Controls.ScrollViewer]::SetHorizontalScrollBarVisibility(
        $controls.PackageGrid,
        $(if ($wideLayout) { [Windows.Controls.ScrollBarVisibility]::Disabled } else { [Windows.Controls.ScrollBarVisibility]::Auto }))
    if ($wideLayout) {
        $controls.PackageGrid.Columns[1].Width = [Windows.Controls.DataGridLength]::new(1.5,[Windows.Controls.DataGridLengthUnitType]::Star)
        $controls.PackageGrid.Columns[2].Width = [Windows.Controls.DataGridLength]::new(0.9,[Windows.Controls.DataGridLengthUnitType]::Star)
        $controls.PackageGrid.Columns[7].Width = [Windows.Controls.DataGridLength]::new(2,[Windows.Controls.DataGridLengthUnitType]::Star)
        $controls.FooterGrid.RowDefinitions[1].Height = [Windows.GridLength]::new(0)
        [Windows.Controls.Grid]::SetRow($controls.ActionBar,0)
        [Windows.Controls.Grid]::SetColumn($controls.ActionBar,1)
        [Windows.Controls.Grid]::SetColumnSpan($controls.ActionBar,1)
        [Windows.Controls.Grid]::SetColumnSpan($controls.SelectionPanel,1)
    }
    else {
        $controls.PackageGrid.Columns[1].Width = [Windows.Controls.DataGridLength]::new(190)
        $controls.PackageGrid.Columns[2].Width = [Windows.Controls.DataGridLength]::new(95)
        $controls.PackageGrid.Columns[7].Width = [Windows.Controls.DataGridLength]::new(220)
        $controls.FooterGrid.RowDefinitions[1].Height = [Windows.GridLength]::Auto
        [Windows.Controls.Grid]::SetRow($controls.ActionBar,1)
        [Windows.Controls.Grid]::SetColumn($controls.ActionBar,0)
        [Windows.Controls.Grid]::SetColumnSpan($controls.ActionBar,2)
        [Windows.Controls.Grid]::SetColumnSpan($controls.SelectionPanel,2)
    }
}

function Set-AVWorkstationToolkitPlan {
    param([Parameter(Mandatory)]$Plan)
    $state.Plan = $Plan
    $state.Items.Clear()
    foreach ($package in @($Plan.Packages | Sort-Object Order)) {
        $state.Items.Add((ConvertTo-DisplayItem -Package $package))
    }
    Update-AVWorkstationToolkitManufacturerFilter
    $controls.PackageGrid.ItemsSource = $state.Items
    $state.View = [Windows.Data.CollectionViewSource]::GetDefaultView($state.Items)
    $state.View.Filter = [Predicate[object]]{
        param($item)
        return Test-AVWorkstationToolkitUiFilter -Item $item
    }

    $controls.CurrentCount.Text = [string]$Plan.Summary.Current
    $controls.ActionCount.Text = [string]($Plan.Summary.Missing + $Plan.Summary.Updates + $Plan.Summary.Manual + $Plan.Summary.ManualUpdates)
    Apply-AVWorkstationToolkitSort
    if ($state.QuickView -eq 'All') { Update-AVWorkstationToolkitFilter } else { Set-AVWorkstationToolkitQuickView -View $state.QuickView }
    $controls.RebootBanner.Visibility = if ($Plan.Reboot.Pending -or $Plan.Elevated) { 'Visible' } else { 'Collapsed' }
    $controls.RecheckButton.Visibility = if ($Plan.Elevated) { 'Collapsed' } else { 'Visible' }
    $controls.RebootText.Text = if ($Plan.Elevated) {
        'Changes are disabled because AV Workstation Toolkit is running as administrator. Close it and launch normally; individual installers can request elevation.'
    }
    elseif ($Plan.Reboot.Pending) { 'Restart recommended. Windows is waiting for a restart to finish an update. You can still install most apps, but some system-level changes are paused until you restart.' }
    else { '' }
    Update-SelectionState
    if ($state.Items.Count -gt 0 -and $controls.PackageGrid.Columns.Count -gt 0) {
        # Refreshes and programmatic checkbox changes can leave WPF's DataGrid
        # at its previous horizontal offset. Always return a new plan to the
        # selection column so application names and statuses open in view.
        $controls.PackageGrid.Dispatcher.BeginInvoke([Action]{
            $firstVisible = @($state.View | Select-Object -First 1)
            if ($firstVisible.Count -gt 0) { $controls.PackageGrid.ScrollIntoView($firstVisible[0],$controls.PackageGrid.Columns[0]) }
        },[Windows.Threading.DispatcherPriority]::Loaded) | Out-Null
    }
}

function Refresh-AVWorkstationToolkitPlan {
    param([switch]$UseMock)
    if ($state.Busy) { return }
    Set-AVWorkstationToolkitBusy -Busy $true -Message 'Starting refresh...'
    try {
        Add-ActivityLine 'Refreshing allowlisted application state.'
        $stageCallback = {
            param([string]$Stage)
            $controls.ActivityState.Text = $Stage
            Add-ActivityLine $Stage
            $window.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::Background)
        }
        $plan = if ($UseMock) { Get-MockAVWorkstationToolkitPlan } else { Get-AVWorkstationToolkitPlan -RefreshExternal -DistributionRoot (Get-AVWorkstationToolkitDistributionRoot) -DataRoot $resolvedDataRoot -StageCallback $stageCallback }
        Set-AVWorkstationToolkitPlan -Plan $plan
        Add-ActivityLine ("Plan ready: {0} current, {1} missing, {2} managed updates, {3} manual updates, {4} holds/manual." -f $plan.Summary.Current,$plan.Summary.Missing,$plan.Summary.Updates,$plan.Summary.ManualUpdates,($plan.Summary.Held+$plan.Summary.Manual)) -Level Success
        foreach ($external in @($plan.Packages | Where-Object { $_.Provider -eq 'External' -and ($_.ReleaseMode -ne 'InventoryOnly' -or $_.Status -eq 'Error') })) {
            Add-ActivityLine ("{0}: {1}" -f $external.Name,$external.ReleaseCheckDetail) -Level $(if ($external.ReleaseCheckDetail -match 'unavailable') { 'Warning' } else { 'Info' })
        }
        if ($plan.ExternalInventory.Quality -in @('Partial','Unavailable')) {
            Add-ActivityLine ('External inventory completed with warnings. ' + $plan.ExternalInventory.Detail + ' View Diagnostics for details.') -Level Warning
        }
        if ($plan.Reboot.Pending) { Add-ActivityLine ('Pending reboot warning: ' + $plan.Reboot.Summary + '. Risk-bearing actions are blocked; low-risk actions remain available.') -Level Warning }
    }
    catch {
        Add-ActivityLine $_.Exception.Message -Level Error
        [Windows.MessageBox]::Show($_.Exception.Message,'Unable to refresh plan','OK','Error') | Out-Null
    }
    finally { Set-AVWorkstationToolkitBusy -Busy $false }
}

function Open-AVWorkstationToolkitLogs {
    New-Item -ItemType Directory -Path $logsRoot -Force | Out-Null
    Open-AVWorkstationToolkitExplorerPath -Path $logsRoot
}

function Export-AVWorkstationToolkitPlan {
    if ($null -eq $state.Plan) { return }
    New-Item -ItemType Directory -Path $reportsRoot -Force | Out-Null
    $dialog = New-Object Microsoft.Win32.SaveFileDialog
    $dialog.Title = 'Export application plan'
    $dialog.Filter = 'JSON report (*.json)|*.json'
    $dialog.InitialDirectory = $reportsRoot
    $dialog.FileName = 'AppPlan-{0}-{1}.json' -f $env:COMPUTERNAME,(Get-Date -Format 'yyyyMMdd-HHmmss')
    if ($dialog.ShowDialog($window)) {
        $report = [ordered]@{
            SchemaVersion = 3
            GeneratedAt = (Get-Date).ToString('o')
            Computer = $state.Plan.Computer
            WingetVersion = $state.Plan.WingetVersion
            Elevated = $state.Plan.Elevated
            Reboot = $state.Plan.Reboot
            ExternalInventory = $state.Plan.ExternalInventory
            Summary = $state.Plan.Summary
            Packages = @($state.Items | Select-Object Profile,Priority,Vendor,ProductFamily,Name,Id,Provider,Risk,ApplicationType,Roles,WorkflowCategories,DeploymentClass,CatalogMaintenancePolicy,VersionRule,VersionCoupling,VersionCouplingTargetId,VersionCouplingNotes,CurrentOrLegacy,LicensingModel,DownloadAccess,DownloadDifficulty,DistributionPolicy,InstallationForms,RequiresVendorAccount,RequiresDealerAccount,RequiresTraining,RequiresLicense,RequiresSubscription,InstallsDriver,InstallsService,OpensListener,FirmwareUtility,Architecture,SupportedOS,SideBySideSupported,Status,StatusDetail,InventoryQuality,InstalledVersion,AvailableVersion,ReleaseCheckDetail,DeliveryMode,DeliveryProviderId,DeliveryProductId,DeliveryDetail,OfficialDownloadUri,OfficialProductUri,ValidationMethod,MetadataVerifiedOn,MetadataVerificationState,MetadataReviewTriggers,MetadataQuarantined,MetadataQuarantineReason,AuthoritativeDomain,ExpectedPublisher,SignatureValidation,VendorHashAvailability,DownloadStrategy,CatalogNotes,Note)
        }
        $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $dialog.FileName -Encoding UTF8
        Add-ActivityLine ('Exported plan: ' + $dialog.FileName) -Level Success
    }
}

function Show-AVWorkstationToolkitSafetySecurity {
    param([switch]$BuildOnly)

    $brushConverter = [Windows.Media.BrushConverter]::new()
    $dialog = [Windows.Window]::new()
    $dialog.Title = 'Safety & Security - AV Workstation Toolkit'
    if ($window.IsVisible) { $dialog.Owner = $window }
    $dialog.WindowStartupLocation = 'CenterOwner'
    $dialog.Width = 680
    $dialog.Height = 560
    $dialog.MinWidth = 520
    $dialog.MinHeight = 400
    $dialog.Background = $brushConverter.ConvertFromString('#0A0F1C')
    $dialog.Foreground = $brushConverter.ConvertFromString('#E8EEF8')
    $dialog.FontFamily = 'Segoe UI'
    $dialog.FontSize = 13

    $layout = [Windows.Controls.Grid]::new()
    $layout.Margin = [Windows.Thickness]::new(22)
    foreach ($height in @('Auto','*','Auto')) {
        $row = [Windows.Controls.RowDefinition]::new()
        $row.Height = [Windows.GridLengthConverter]::new().ConvertFromString($height)
        $layout.RowDefinitions.Add($row)
    }

    $heading = [Windows.Controls.TextBlock]::new()
    $heading.Text = 'Safety & Security'
    $heading.FontSize = 22
    $heading.FontWeight = 'SemiBold'
    $heading.Margin = [Windows.Thickness]::new(0,0,0,14)
    [Windows.Controls.Grid]::SetRow($heading,0)
    [void]$layout.Children.Add($heading)

    $body = [Windows.Controls.TextBlock]::new()
    $body.Text = @'
AV Workstation Toolkit is a local workstation manager. It runs as a standard user and does not expose a local web or command server.

Managed actions are limited to exact, approved WinGet package IDs. AV Workstation Toolkit validates the live workstation plan immediately before each package and submits one package at a time. An individual installer may ask for elevation through the normal Windows consent prompt.

Catalog-only and manual applications provide read-only knowledge, inventory, or an approved handoff. They are never turned into automatic downloads or installations. Verified cached packages are shown in File Explorer; AV Workstation Toolkit does not execute them.

AV Workstation Toolkit does not provide uninstall, rollback, security-management, MDM, VPN, EDR, driver-management, or operating-system management features. Pending Windows restarts continue to block driver-, service-, and listener-bearing changes while ordinary low-risk apps remain available.

Credentials remain isolated from the PowerShell UI, authenticated SFTP validates host keys, and package paths, hashes, signatures, providers, and request fields are checked before an allowed handoff or action.
'@
    $body.TextWrapping = 'Wrap'
    $body.Foreground = $brushConverter.ConvertFromString('#C7D3E3')
    $body.LineHeight = 21
    $body.Padding = [Windows.Thickness]::new(2)
    $scroll = [Windows.Controls.ScrollViewer]::new()
    $scroll.VerticalScrollBarVisibility = 'Auto'
    $scroll.HorizontalScrollBarVisibility = 'Disabled'
    $scroll.Content = $body
    [Windows.Controls.Grid]::SetRow($scroll,1)
    [void]$layout.Children.Add($scroll)

    $close = [Windows.Controls.Button]::new()
    $close.Content = 'Close'
    $close.IsDefault = $true
    $close.IsCancel = $true
    $close.MinWidth = 90
    $close.HorizontalAlignment = 'Right'
    $close.Margin = [Windows.Thickness]::new(0,14,0,0)
    $sharedButtonStyle = $window.TryFindResource([Windows.Controls.Button])
    if ($null -ne $sharedButtonStyle) { $close.Style = $sharedButtonStyle }
    $close.Add_Click({ $dialog.Close() })
    [Windows.Controls.Grid]::SetRow($close,2)
    [void]$layout.Children.Add($close)

    $dialog.Content = $layout
    if ($BuildOnly) { return $dialog }
    [void]$dialog.ShowDialog()
}

function Show-AVWorkstationToolkitAbout {
    param([switch]$BuildOnly)

    $brushConverter = [Windows.Media.BrushConverter]::new()
    $dialog = [Windows.Window]::new()
    $dialog.Title = 'About AV Workstation Toolkit'
    if ($window.IsVisible) { $dialog.Owner = $window }
    $dialog.WindowStartupLocation = 'CenterOwner'
    $dialog.Width = 480
    $dialog.Height = 350
    $dialog.MinWidth = 420
    $dialog.MinHeight = 320
    $dialog.ResizeMode = 'NoResize'
    $dialog.Background = $brushConverter.ConvertFromString('#0A0F1C')
    $dialog.Foreground = $brushConverter.ConvertFromString('#E8EEF8')
    $dialog.FontFamily = 'Segoe UI'
    $dialog.FontSize = 13

    $layout = [Windows.Controls.Grid]::new()
    $layout.Margin = [Windows.Thickness]::new(24)
    foreach ($height in @('*','Auto')) {
        $row = [Windows.Controls.RowDefinition]::new()
        $row.Height = [Windows.GridLengthConverter]::new().ConvertFromString($height)
        $layout.RowDefinitions.Add($row)
    }

    $content = [Windows.Controls.StackPanel]::new()
    $content.HorizontalAlignment = 'Center'
    $content.VerticalAlignment = 'Center'
    $content.MaxWidth = 390

    $logo = [Windows.Controls.Border]::new()
    $logo.Width = 56
    $logo.Height = 56
    $logo.CornerRadius = [Windows.CornerRadius]::new(13)
    $logo.Background = $brushConverter.ConvertFromString('#2563EB')
    $logo.HorizontalAlignment = 'Center'
    $mark = [Windows.Controls.TextBlock]::new()
    $mark.Text = 'AV'
    $mark.FontSize = 20
    $mark.FontWeight = 'Bold'
    $mark.HorizontalAlignment = 'Center'
    $mark.VerticalAlignment = 'Center'
    $logo.Child = $mark
    [void]$content.Children.Add($logo)

    $title = [Windows.Controls.TextBlock]::new()
    $title.Text = 'AV Workstation Toolkit'
    $title.FontSize = 24
    $title.FontWeight = 'SemiBold'
    $title.HorizontalAlignment = 'Center'
    $title.Margin = [Windows.Thickness]::new(0,12,0,0)
    [void]$content.Children.Add($title)

    $identity = [Windows.Controls.TextBlock]::new()
    $identity.Text = 'Version {0}  |  {1}' -f $productVersion,$executionMode
    $identity.Foreground = $brushConverter.ConvertFromString('#8FB9EC')
    $identity.HorizontalAlignment = 'Center'
    $identity.Margin = [Windows.Thickness]::new(0,6,0,0)
    [void]$content.Children.Add($identity)

    $description = [Windows.Controls.TextBlock]::new()
    $description.Text = 'Local commercial-AV workstation management, inventory, catalog knowledge, and diagnostics.'
    $description.Foreground = $brushConverter.ConvertFromString('#B8C6D8')
    $description.TextWrapping = 'Wrap'
    $description.TextAlignment = 'Center'
    $description.LineHeight = 20
    $description.Margin = [Windows.Thickness]::new(0,18,0,0)
    [void]$content.Children.Add($description)
    [Windows.Controls.Grid]::SetRow($content,0)
    [void]$layout.Children.Add($content)

    $close = [Windows.Controls.Button]::new()
    $close.Content = 'Close'
    $close.IsDefault = $true
    $close.IsCancel = $true
    $close.MinWidth = 90
    $close.HorizontalAlignment = 'Right'
    $close.Margin = [Windows.Thickness]::new(0,16,0,0)
    $sharedButtonStyle = $window.TryFindResource([Windows.Controls.Button])
    if ($null -ne $sharedButtonStyle) { $close.Style = $sharedButtonStyle }
    $close.Add_Click({ $dialog.Close() })
    [Windows.Controls.Grid]::SetRow($close,1)
    [void]$layout.Children.Add($close)

    $dialog.Content = $layout
    $dialog.Tag = [pscustomobject]@{ Product='AV Workstation Toolkit'; Version=$productVersion; Mode=$executionMode; Description=$description.Text }
    if ($BuildOnly) { return $dialog }
    [void]$dialog.ShowDialog()
}

function Show-AVWorkstationToolkitDiagnostics {
    param([switch]$BuildOnly)

    if ($null -eq $state.Plan -or $state.Busy) { return }
    $diagnostics = Get-AVWorkstationToolkitDiagnostics -Plan $state.Plan -DataRoot $resolvedDataRoot -LogsPath $logsRoot
    $diagnosticText = ConvertTo-AVWorkstationToolkitDiagnosticsText -Diagnostics $diagnostics
    $brushConverter = [Windows.Media.BrushConverter]::new()
    $dialog = [Windows.Window]::new()
    $dialog.Title = 'AV Workstation Toolkit diagnostics'
    if ($window.IsVisible) { $dialog.Owner = $window }
    $dialog.WindowStartupLocation = 'CenterOwner'
    $dialog.Width = 820
    $dialog.Height = 720
    $dialog.MinWidth = 580
    $dialog.MinHeight = 460
    $dialog.Background = $brushConverter.ConvertFromString('#0A0F1C')
    $dialog.Foreground = $brushConverter.ConvertFromString('#E8EEF8')
    $dialog.FontFamily = 'Segoe UI'
    $dialog.FontSize = 13

    $layout = [Windows.Controls.Grid]::new()
    $layout.Margin = [Windows.Thickness]::new(20)
    foreach ($height in @('Auto','*','Auto')) {
        $row = [Windows.Controls.RowDefinition]::new()
        $row.Height = [Windows.GridLengthConverter]::new().ConvertFromString($height)
        $layout.RowDefinitions.Add($row)
    }
    $heading = [Windows.Controls.StackPanel]::new()
    $title = [Windows.Controls.TextBlock]::new()
    $title.Text = 'Diagnostics'
    $title.FontSize = 22
    $title.FontWeight = 'SemiBold'
    $subtitle = [Windows.Controls.TextBlock]::new()
    $subtitle.Text = 'Read-only, sanitized runtime and inventory health'
    $subtitle.Foreground = $brushConverter.ConvertFromString('#94A4BB')
    $subtitle.Margin = [Windows.Thickness]::new(0,5,0,12)
    [void]$heading.Children.Add($title)
    [void]$heading.Children.Add($subtitle)
    [Windows.Controls.Grid]::SetRow($heading,0)
    [void]$layout.Children.Add($heading)

    $detail = [Windows.Controls.TextBox]::new()
    $detail.Text = $diagnosticText
    $detail.IsReadOnly = $true
    $detail.AcceptsReturn = $true
    $detail.TextWrapping = 'NoWrap'
    $detail.VerticalScrollBarVisibility = 'Auto'
    $detail.HorizontalScrollBarVisibility = 'Auto'
    $detail.Background = $brushConverter.ConvertFromString('#0C1423')
    $detail.Foreground = $brushConverter.ConvertFromString('#DDE6F3')
    $detail.BorderBrush = $brushConverter.ConvertFromString('#30415D')
    $detail.Padding = [Windows.Thickness]::new(14)
    $detail.FontFamily = 'Consolas'
    $detail.FontSize = 11.5
    [Windows.Controls.Grid]::SetRow($detail,1)
    [void]$layout.Children.Add($detail)

    $buttons = [Windows.Controls.StackPanel]::new()
    $buttons.Orientation = 'Horizontal'
    $buttons.HorizontalAlignment = 'Right'
    $buttons.Margin = [Windows.Thickness]::new(0,14,0,0)
    $sharedButtonStyle = $window.TryFindResource([Windows.Controls.Button])

    $copy = [Windows.Controls.Button]::new()
    $copy.Content = 'Copy diagnostics'
    $copy.Margin = [Windows.Thickness]::new(0,0,8,0)
    if ($null -ne $sharedButtonStyle) { $copy.Style = $sharedButtonStyle }
    $copy.Add_Click({
        [Windows.Clipboard]::SetText($diagnosticText)
        Add-ActivityLine 'Copied sanitized diagnostics to the clipboard.' -Level Success
    })
    [void]$buttons.Children.Add($copy)

    $export = [Windows.Controls.Button]::new()
    $export.Content = 'Export diagnostics'
    $export.Margin = [Windows.Thickness]::new(0,0,8,0)
    if ($null -ne $sharedButtonStyle) { $export.Style = $sharedButtonStyle }
    $export.Add_Click({
        New-Item -ItemType Directory -Path $reportsRoot -Force | Out-Null
        $save = New-Object Microsoft.Win32.SaveFileDialog
        $save.Title = 'Export sanitized AV Workstation Toolkit diagnostics'
        $save.Filter = 'JSON report (*.json)|*.json'
        $save.InitialDirectory = $reportsRoot
        $save.FileName = 'AV-Workstation-Toolkit-Diagnostics-{0}.json' -f (Get-Date -Format 'yyyyMMdd-HHmmss')
        if ($save.ShowDialog($dialog)) {
            $safeJson = Protect-AVWorkstationToolkitSensitiveText -Text ($diagnostics | ConvertTo-Json -Depth 8)
            $safeJson | Set-Content -LiteralPath $save.FileName -Encoding UTF8
            Add-ActivityLine ('Exported sanitized diagnostics: ' + $save.FileName) -Level Success
        }
    })
    [void]$buttons.Children.Add($export)

    $close = [Windows.Controls.Button]::new()
    $close.Content = 'Close'
    $close.IsDefault = $true
    $close.IsCancel = $true
    $close.MinWidth = 90
    if ($null -ne $sharedButtonStyle) { $close.Style = $sharedButtonStyle }
    $close.Add_Click({ $dialog.Close() })
    [void]$buttons.Children.Add($close)
    [Windows.Controls.Grid]::SetRow($buttons,2)
    [void]$layout.Children.Add($buttons)
    $dialog.Content = $layout
    if ($BuildOnly) { return $dialog }
    [void]$dialog.ShowDialog()
}

function Read-AVWorkstationToolkitProgress {
    param([switch]$Flush)

    if ([string]::IsNullOrWhiteSpace($state.ProgressPath)) { return }
    try {
        if (-not (Test-Path -LiteralPath $state.ProgressPath -PathType Leaf)) { return }
        $progressFile = Get-Item -LiteralPath $state.ProgressPath
        if ($progressFile.Length -gt 20MB) {
            Add-ActivityLine 'Progress display stopped because the progress file exceeded 20 MiB. The worker log continues on disk.' -Level Warning
            $state.ProgressPath = ''
            return
        }
        if ($progressFile.Length -lt $state.ProgressByteOffset) {
            $state.ProgressByteOffset = [int64]0
            $state.ProgressRemainder = ''
        }

        $chunk = ''
        $stream = [IO.File]::Open($state.ProgressPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        try {
            [void]$stream.Seek($state.ProgressByteOffset,[IO.SeekOrigin]::Begin)
            $remaining = [int]($stream.Length - $state.ProgressByteOffset)
            if ($remaining -gt 0) {
                $buffer = New-Object byte[] $remaining
                $bytesRead = 0
                while ($bytesRead -lt $remaining) {
                    $count = $stream.Read($buffer,$bytesRead,$remaining-$bytesRead)
                    if ($count -le 0) { break }
                    $bytesRead += $count
                }
                if ($bytesRead -gt 0) {
                    $chunk = [Text.Encoding]::UTF8.GetString($buffer,0,$bytesRead)
                    if ($state.ProgressByteOffset -eq 0) { $chunk = $chunk.TrimStart([char]0xFEFF) }
                    $state.ProgressByteOffset += $bytesRead
                }
            }
        }
        finally { $stream.Dispose() }

        $text = $state.ProgressRemainder + $chunk
        if ([string]::IsNullOrEmpty($text)) { return }
        $lines = @($text -split "`r?`n")
        $completeCount = if ($Flush -or $text.EndsWith("`n")) { $lines.Count } else { $lines.Count - 1 }
        $state.ProgressRemainder = if ($completeCount -lt $lines.Count) { $lines[-1] } else { '' }
        for ($i = 0; $i -lt $completeCount; $i++) {
            if ([string]::IsNullOrWhiteSpace($lines[$i])) { continue }
            try {
                $progressEvent = $lines[$i] | ConvertFrom-Json
                $level = if ([string]$progressEvent.Level -in @('Info','Success','Warning','Error')) { [string]$progressEvent.Level } else { 'Info' }
                Add-ActivityLine -Message ([string]$progressEvent.Message) -Level $level
            }
            catch { Add-ActivityLine $lines[$i] }
        }
        $state.ProgressReadWarningShown = $false
    }
    catch {
        if (-not $state.ProgressReadWarningShown) {
            Add-ActivityLine ('Progress is temporarily unavailable; AV Workstation Toolkit will retry: ' + $_.Exception.Message) -Level Warning
            $state.ProgressReadWarningShown = $true
        }
    }
}

function Complete-AVWorkstationToolkitAction {
    $state.Timer.Stop()
    Read-AVWorkstationToolkitProgress -Flush
    $exitCode = $state.Process.ExitCode
    $message = "Worker exited with code $exitCode."
    $status = if ($exitCode -eq 0) { 'Succeeded' } else { 'Failed' }
    if (Test-Path -LiteralPath $state.ResultPath) {
        try {
            $resultFile = Get-Item -LiteralPath $state.ResultPath
            if ($resultFile.Length -gt 2MB) { throw 'Result report exceeds the 2 MiB safety limit.' }
            $result = Get-Content -LiteralPath $state.ResultPath -Raw | ConvertFrom-Json
            $message = Protect-AVWorkstationToolkitSensitiveText -Text ([string]$result.Message)
            if ($message.Length -gt 4000) { $message = $message.Substring(0,4000) + '...[truncated]' }
            $status = if ([string]$result.Status -in @('Succeeded','Failed','Rejected','Cancelled','Blocked')) { [string]$result.Status } else { 'Failed' }
        }
        catch { Add-ActivityLine ('Could not read result report: ' + $_.Exception.Message) -Level Error }
    }
    Add-ActivityLine $message -Level $(if ($status -eq 'Succeeded') {'Success'} elseif ($status -in @('Cancelled','Blocked')) {'Warning'} else {'Error'})
    $state.Process = $null
    Set-AVWorkstationToolkitBusy -Busy $false
    Refresh-AVWorkstationToolkitPlan
    if (-not $state.Closing) {
        $icon = if ($status -eq 'Succeeded') { 'Information' } else { 'Warning' }
        [Windows.MessageBox]::Show($message,'Application action complete','OK',$icon) | Out-Null
    }
}

function Start-AVWorkstationToolkitAction {
    param([ValidateSet('Install','Update')][string]$Action)
    if ($state.Busy -or $null -eq $state.Plan) { return }
    if ($state.Plan.Elevated) {
        [Windows.MessageBox]::Show('Close AV Workstation Toolkit and launch it normally. For safety, the action worker refuses to run with an administrator token. Individual installers can still request elevation through Windows.','Standard-user launch required','OK','Warning') | Out-Null
        return
    }
    $selected = @(Get-AVWorkstationToolkitSelectedActionItems -Action $Action)
    if ($selected.Count -eq 0) { return }
    Add-ActivityLine ('Action selection: action={0}; count={1}; packageIds={2}' -f $Action,$selected.Count,(($selected | ForEach-Object Id) -join ', '))
    $risky = @($selected | Where-Object Risk -ne 'None')
    if ($state.Plan.Reboot.Pending -and $risky.Count -gt 0) {
        [Windows.MessageBox]::Show('Windows reports a pending reboot. Driver, service, and listener packages are blocked until Windows is restarted and the plan is refreshed. Low-risk applications may still be changed.','Risk-bearing action blocked','OK','Warning') | Out-Null
        return
    }
    $names = ($selected | ForEach-Object { '- {0} ({1})' -f $_.Name,$_.Id }) -join "`r`n"
    $confirmation = "Review the exact $($Action.ToLowerInvariant()) request:`r`n`r`n$names`r`n`r`nEach package will be verified afterward. Continue?"
    if ([Windows.MessageBox]::Show($confirmation,"Confirm $Action",'YesNo','Question') -ne 'Yes') { return }

    $riskAcknowledged = $false
    if ($risky.Count -gt 0) {
        $riskText = ($risky | ForEach-Object { '- {0} ({1}) - {2}' -f $_.Name,$_.Id,$_.Risk }) -join "`r`n"
        if ([Windows.MessageBox]::Show("These packages have explicit system impact:`r`n`r`n$riskText`r`n`r`nApprove this risk for this run only?",'Explicit risk acknowledgement','YesNo','Warning') -ne 'Yes') { return }
        $riskAcknowledged = $true
    }

    $request = New-AVWorkstationToolkitActionRequest -Action $Action -PackageId @($selected.Id) -RiskAcknowledged $riskAcknowledged -RequestsRoot (Join-Path $logsRoot 'requests')
    $state.ProgressPath = $request.ProgressPath
    $state.ResultPath = $request.ResultPath
    $state.CancelPath = $request.CancelPath
    $state.ProgressByteOffset = [int64]0
    $state.ProgressRemainder = ''
    $state.ProgressReadWarningShown = $false

    $state.Process = Start-AVWorkstationToolkitWorker -RequestPath $request.RequestPath -DataRoot $resolvedDataRoot
    Add-ActivityLine ("Started $($Action.ToLowerInvariant()) request for $($selected.Count) package(s).")
    Set-AVWorkstationToolkitBusy -Busy $true -Message "$Action in progress..."

    $timer = New-Object Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromMilliseconds(450)
    $timer.Add_Tick({
        Read-AVWorkstationToolkitProgress
        if ($null -ne $state.Process) {
            $state.Process.Refresh()
            if ($state.Process.HasExited) { Complete-AVWorkstationToolkitAction }
        }
    })
    $state.Timer = $timer
    $timer.Start()
}

function Invoke-AVWorkstationToolkitUiVendorBridge {
    param([Parameter(Mandatory)]$Request,[int]$TimeoutSeconds = 1800)
    return Invoke-AVWorkstationToolkitVendorBridge -Request $Request -TimeoutSeconds $TimeoutSeconds -PumpEvents {
        $window.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::Background)
    }
}

function Get-AVWorkstationToolkitExternalCatalogItem {
    param([Parameter(Mandatory)][string]$Id)
    $catalogMatches = @(Get-AVWorkstationToolkitCatalog | Where-Object { $_.Provider -eq 'External' -and $_.Id -eq $Id })
    if ($catalogMatches.Count -ne 1) { throw "External catalog package could not be resolved: $Id" }
    return $catalogMatches[0]
}

function Show-AVWorkstationToolkitCrestronProductPicker {
    param([Parameter(Mandatory)][object[]]$Product)
    $dialog = New-Object Windows.Window
    $dialog.Title = 'Choose Crestron software'
    $dialog.Owner = $window
    $dialog.WindowStartupLocation = 'CenterOwner'
    $dialog.SizeToContent = 'Manual'
    $dialog.Width = 720
    $dialog.Height = 480
    $dialog.MinWidth = 620
    $dialog.MinHeight = 400
    $dialog.Background = [Windows.Media.BrushConverter]::new().ConvertFromString('#0B1220')
    $dialog.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#E7EDF6')

    $grid = New-Object Windows.Controls.Grid
    $grid.Margin = '18'
    $grid.RowDefinitions.Add((New-Object Windows.Controls.RowDefinition -Property @{Height='Auto'}))
    $grid.RowDefinitions.Add((New-Object Windows.Controls.RowDefinition -Property @{Height='*'}))
    $grid.RowDefinitions.Add((New-Object Windows.Controls.RowDefinition -Property @{Height='Auto'}))
    $intro = New-Object Windows.Controls.TextBlock
    $intro.Text = 'Select one product from Crestron''s public MasterInstaller catalog. Only the approved product IDs shown below can be requested.'
    $intro.TextWrapping = 'Wrap'
    $intro.Margin = '0,0,0,12'
    [Windows.Controls.Grid]::SetRow($intro,0)
    $grid.Children.Add($intro) | Out-Null

    $list = New-Object Windows.Controls.ListBox
    $display = @($Product | ForEach-Object {
        [pscustomobject]@{ Label=('{0}  |  {1}  |  {2}{3}' -f $_.Name,$_.Version,$_.SizeLabel,$(if ($_.RebootRequired) {'  |  reboot may be required'} else {''})); Product=$_ }
    })
    $list.ItemsSource = $display
    $list.DisplayMemberPath = 'Label'
    if ($display.Count -gt 0) { $list.SelectedIndex = 0 }
    [Windows.Controls.Grid]::SetRow($list,1)
    $grid.Children.Add($list) | Out-Null

    $buttons = New-Object Windows.Controls.StackPanel
    $buttons.Orientation = 'Horizontal'
    $buttons.HorizontalAlignment = 'Right'
    $buttons.Margin = '0,14,0,0'
    $cancel = New-Object Windows.Controls.Button
    $cancel.Content = 'Cancel'
    $cancel.MinWidth = 90
    $cancel.Margin = '0,0,8,0'
    $choose = New-Object Windows.Controls.Button
    $choose.Content = 'Continue'
    $choose.MinWidth = 100
    $choose.IsDefault = $true
    $choose.IsEnabled = $display.Count -gt 0
    $buttons.Children.Add($cancel) | Out-Null
    $buttons.Children.Add($choose) | Out-Null
    [Windows.Controls.Grid]::SetRow($buttons,2)
    $grid.Children.Add($buttons) | Out-Null
    $dialog.Content = $grid

    $script:selectedCrestronProduct = $null
    $cancel.Add_Click({ $dialog.DialogResult = $false; $dialog.Close() })
    $choose.Add_Click({
        if ($null -ne $list.SelectedItem) {
            $script:selectedCrestronProduct = $list.SelectedItem.Product
            $dialog.DialogResult = $true
            $dialog.Close()
        }
    })
    [void]$dialog.ShowDialog()
    $selected = $script:selectedCrestronProduct
    Remove-Variable -Name selectedCrestronProduct -Scope Script -ErrorAction SilentlyContinue
    return $selected
}

function Show-AVWorkstationToolkitSftpCredentialDialog {
    param(
        [Parameter(Mandatory)]$Package,
        [Parameter(Mandatory)]$Product,
        [Parameter(Mandatory)][string]$Fingerprint,
        [Parameter(Mandatory)][string]$CachePackageId
    )

    $dialog = New-Object Windows.Window
    $dialog.Title = 'Crestron account'
    $dialog.Owner = $window
    $dialog.WindowStartupLocation = 'CenterOwner'
    $dialog.SizeToContent = 'WidthAndHeight'
    $dialog.ResizeMode = 'NoResize'
    $dialog.Background = [Windows.Media.BrushConverter]::new().ConvertFromString('#0B1220')
    $dialog.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#E7EDF6')
    $panel = New-Object Windows.Controls.StackPanel
    $panel.Width = 560
    $panel.Margin = '20'

    $title = New-Object Windows.Controls.TextBlock
    $title.Text = ('Download {0} {1}' -f $Product.Name,$Product.Version)
    $title.FontSize = 18
    $title.FontWeight = 'SemiBold'
    $panel.Children.Add($title) | Out-Null
    $trust = New-Object Windows.Controls.TextBlock
    $trust.Text = "Trusted host: $($Package.SftpHost):$($Package.SftpPort)`r`n$Fingerprint"
    $trust.FontFamily = 'Consolas'
    $trust.FontSize = 11
    $trust.TextWrapping = 'Wrap'
    $trust.Margin = '0,8,0,14'
    $panel.Children.Add($trust) | Out-Null

    $userLabel = New-Object Windows.Controls.TextBlock
    $userLabel.Text = 'Crestron username'
    $panel.Children.Add($userLabel) | Out-Null
    $username = New-Object Windows.Controls.TextBox
    $username.Margin = '0,5,0,10'
    $username.MaxLength = 256
    $panel.Children.Add($username) | Out-Null
    $passwordLabel = New-Object Windows.Controls.TextBlock
    $passwordLabel.Text = 'Password (leave blank to use a saved credential)'
    $panel.Children.Add($passwordLabel) | Out-Null
    $password = New-Object Windows.Controls.PasswordBox
    $password.Margin = '0,5,0,10'
    $password.MaxLength = 1280
    $panel.Children.Add($password) | Out-Null
    $save = New-Object Windows.Controls.CheckBox
    $save.Content = 'Save on this computer in Windows Credential Manager'
    $save.IsChecked = $false
    $save.Margin = '0,0,0,12'
    $panel.Children.Add($save) | Out-Null
    $status = New-Object Windows.Controls.TextBlock
    $status.Text = 'Credentials are sent only to the packaged vendor bridge over redirected standard input and are never logged.'
    $status.TextWrapping = 'Wrap'
    $status.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#9FB0C5')
    $status.Margin = '0,0,0,14'
    $panel.Children.Add($status) | Out-Null

    $buttons = New-Object Windows.Controls.StackPanel
    $buttons.Orientation = 'Horizontal'
    $buttons.HorizontalAlignment = 'Right'
    $test = New-Object Windows.Controls.Button
    $test.Content = 'Test credentials'
    $test.Margin = '0,0,8,0'
    $forget = New-Object Windows.Controls.Button
    $forget.Content = 'Forget saved'
    $forget.Margin = '0,0,8,0'
    $cancel = New-Object Windows.Controls.Button
    $cancel.Content = 'Cancel'
    $cancel.Margin = '0,0,8,0'
    $download = New-Object Windows.Controls.Button
    $download.Content = 'Download'
    $download.IsDefault = $true
    foreach ($button in @($test,$forget,$cancel,$download)) { $button.MinWidth = 100; $buttons.Children.Add($button) | Out-Null }
    $panel.Children.Add($buttons) | Out-Null
    $dialog.Content = $panel

    $script:sftpDownloadResponse = $null
    $setEnabled = {
        param([bool]$Enabled)
        foreach ($button in @($test,$forget,$cancel,$download)) { $button.IsEnabled = $Enabled }
    }
    $newCredentialRequest = {
        param([string]$Operation)
        $name = $username.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($name)) { throw 'Enter the Crestron username.' }
        [pscustomobject]@{
            Operation = $Operation
            Host = [string]$Package.SftpHost
            Port = [int]$Package.SftpPort
            Username = $name
            Password = $password.Password
            SaveCredential = [bool]$save.IsChecked
            ExpectedFingerprint = $Fingerprint
            RemoteRoot = [string]$Package.SftpRemoteRoot
            RemotePath = [string]$Product.RemotePath
            DataRoot = $resolvedDataRoot
            PackageId = $CachePackageId
            Version = [string]$Product.Version
            MaxBytes = [int64][Math]::Min([double]$Package.DownloadMaxBytes,[Math]::Ceiling([double]$Product.SizeBytes * 1.20 + 10MB))
        }
    }
    $test.Add_Click({
        try {
            & $setEnabled $false
            $status.Text = 'Testing SFTP credentials...'
            $request = & $newCredentialRequest 'TestSftp'
            $response = Invoke-AVWorkstationToolkitUiVendorBridge -Request $request -TimeoutSeconds 60
            $password.Clear()
            $status.Text = [string]$response.Message
            $status.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#66E1B5')
        }
        catch {
            $password.Clear()
            $status.Text = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
            $status.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#FF9AAA')
        }
        finally { & $setEnabled $true }
    })
    $forget.Add_Click({
        try {
            & $setEnabled $false
            $name = $username.Text.Trim()
            if ([string]::IsNullOrWhiteSpace($name)) { throw 'Enter the Crestron username whose saved credential should be removed.' }
            $request = [pscustomobject]@{ Operation='DeleteCredential'; Host=[string]$Package.SftpHost; Port=[int]$Package.SftpPort; Username=$name }
            $response = Invoke-AVWorkstationToolkitUiVendorBridge -Request $request -TimeoutSeconds 30
            $password.Clear()
            $status.Text = [string]$response.Message
            $status.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#66E1B5')
        }
        catch {
            $status.Text = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
            $status.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#FF9AAA')
        }
        finally { & $setEnabled $true }
    })
    $cancel.Add_Click({ $password.Clear(); $dialog.DialogResult = $false; $dialog.Close() })
    $download.Add_Click({
        try {
            & $setEnabled $false
            $status.Text = 'Downloading from Crestron SFTP...'
            $request = & $newCredentialRequest 'DownloadSftp'
            $script:sftpDownloadResponse = Invoke-AVWorkstationToolkitUiVendorBridge -Request $request
            $password.Clear()
            $dialog.DialogResult = $true
            $dialog.Close()
        }
        catch {
            $password.Clear()
            $status.Text = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
            $status.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#FF9AAA')
            & $setEnabled $true
        }
    })
    [void]$dialog.ShowDialog()
    $response = $script:sftpDownloadResponse
    Remove-Variable -Name sftpDownloadResponse -Scope Script -ErrorAction SilentlyContinue
    return $response
}

function Invoke-AVWorkstationToolkitDirectDownloadDelivery {
    param([Parameter(Mandatory)]$Item)
    $package = Get-AVWorkstationToolkitExternalCatalogItem -Id ([string]$Item.Id)
    if ([Windows.MessageBox]::Show("AV Workstation Toolkit will download $($Item.Name) $($Item.AvailableVersion) from the catalogued vendor host, validate its Authenticode publisher, and place it in your per-user cache. It will not run the installer.`r`n`r`nReview device/software compatibility before installation. Continue?",'Download vendor package','YesNo','Question') -ne 'Yes') { return }
    Set-AVWorkstationToolkitBusy -Busy $true -Message ('Downloading ' + $Item.Name + '...')
    try {
        $request = [pscustomobject]@{
            Operation = 'DownloadHttps'
            SourceUri = [string]$Item.DeliveryUri
            AllowedHosts = @($package.DownloadAllowedHosts)
            DataRoot = $resolvedDataRoot
            PackageId = [string]$package.Id
            Version = [string]$Item.AvailableVersion
            MaxBytes = [int64]$package.DownloadMaxBytes
        }
        $response = Invoke-AVWorkstationToolkitUiVendorBridge -Request $request
        $verified = Complete-AVWorkstationToolkitVendorDownload -Package $package -Version ([string]$Item.AvailableVersion) -DownloadPath ([string]$response.DownloadPath) -DataRoot $resolvedDataRoot -Source ([string]$Item.DeliveryUri)
        Open-AVWorkstationToolkitExplorerPath -Path $verified.Path -SelectFile
        Add-ActivityLine ("Downloaded and verified {0}; AV Workstation Toolkit did not execute the installer. SHA-256: {1}" -f $Item.Name,$verified.Sha256) -Level Success
    }
    catch {
        $message = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
        Add-ActivityLine $message -Level Error
        [Windows.MessageBox]::Show($message,'Vendor download failed','OK','Error') | Out-Null
    }
    finally { Set-AVWorkstationToolkitBusy -Busy $false }
}

function Invoke-AVWorkstationToolkitAuthenticatedSftpDelivery {
    param([Parameter(Mandatory)]$Item)
    $package = Get-AVWorkstationToolkitExternalCatalogItem -Id ([string]$Item.Id)
    $providerId = [string]$Item.DeliveryProviderId
    $provider = if ([string]::IsNullOrWhiteSpace($providerId)) { $package } else { Get-AVWorkstationToolkitExternalCatalogItem -Id $providerId }
    if ([Windows.MessageBox]::Show('This workflow uses an authorized Crestron account to download one allowlisted product directly from ftp.crestron.com. AV Workstation Toolkit does not redistribute Crestron software, does not run the installer, and requires host-key and Authenticode validation. Continue?','Crestron software access','YesNo','Warning') -ne 'Yes') { return }
    try {
        Set-AVWorkstationToolkitBusy -Busy $true -Message 'Loading Crestron product catalog...'
        $products = @(Get-AVWorkstationToolkitAuthenticatedSftpCatalog -Package $provider)
        if (-not [string]::IsNullOrWhiteSpace([string]$Item.DeliveryProductId)) {
            $products = @($products | Where-Object { [string]$_.ProductId -eq [string]$Item.DeliveryProductId })
            if ($products.Count -ne 1) { throw 'The configured Crestron child product is absent from the approved parent catalog.' }
        }
    }
    catch {
        $message = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
        Add-ActivityLine $message -Level Error
        [Windows.MessageBox]::Show($message,'Crestron catalog unavailable','OK','Error') | Out-Null
        return
    }
    finally { Set-AVWorkstationToolkitBusy -Busy $false }
    $product = Show-AVWorkstationToolkitCrestronProductPicker -Product $products
    if ($null -eq $product) { return }

    try {
        Set-AVWorkstationToolkitBusy -Busy $true -Message 'Checking Crestron SFTP host identity...'
        $probe = Invoke-AVWorkstationToolkitUiVendorBridge -Request ([pscustomobject]@{ Operation='ProbeSftpHost'; Host=[string]$provider.SftpHost; Port=[int]$provider.SftpPort }) -TimeoutSeconds 60
        $fingerprint = [string]$probe.Fingerprint
        $trusted = Get-AVWorkstationToolkitTrustedSftpHost -HostName ([string]$provider.SftpHost) -Port ([int]$provider.SftpPort) -DataRoot $resolvedDataRoot
        if ($null -eq $trusted) {
            $answer = [Windows.MessageBox]::Show("First connection to $($provider.SftpHost):$($provider.SftpPort).`r`n`r`nPresented host identity:`r`n$fingerprint`r`n`r`nTrust this identity for future AV Workstation Toolkit connections on this Windows account?",'Trust Crestron SFTP host','YesNo','Warning')
            if ($answer -ne 'Yes') { return }
            [void](Set-AVWorkstationToolkitTrustedSftpHost -HostName ([string]$provider.SftpHost) -Port ([int]$provider.SftpPort) -Fingerprint $fingerprint -DataRoot $resolvedDataRoot)
        }
        elseif (-not $fingerprint.Equals([string]$trusted.Fingerprint,[StringComparison]::Ordinal)) {
            $answer = [Windows.MessageBox]::Show("The Crestron SFTP host identity changed.`r`n`r`nSaved:`r`n$($trusted.Fingerprint)`r`n`r`nPresented:`r`n$fingerprint`r`n`r`nDo not continue unless Crestron or your administrator confirmed this change. Replace the saved identity?",'SFTP host identity changed','YesNo','Error')
            if ($answer -ne 'Yes') { throw 'Crestron SFTP host identity change was not approved.' }
            [void](Set-AVWorkstationToolkitTrustedSftpHost -HostName ([string]$provider.SftpHost) -Port ([int]$provider.SftpPort) -Fingerprint $fingerprint -DataRoot $resolvedDataRoot)
        }
    }
    catch {
        $message = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
        Add-ActivityLine $message -Level Error
        [Windows.MessageBox]::Show($message,'Crestron host verification failed','OK','Error') | Out-Null
        return
    }
    finally { Set-AVWorkstationToolkitBusy -Busy $false }

    $response = Show-AVWorkstationToolkitSftpCredentialDialog -Package $provider -Product $product -Fingerprint $fingerprint -CachePackageId ([string]$package.Id)
    if ($null -eq $response) { return }
    try {
        $verified = Complete-AVWorkstationToolkitVendorDownload -Package $package -Version ([string]$product.Version) -DownloadPath ([string]$response.DownloadPath) -DataRoot $resolvedDataRoot -Source ("SFTP {0}:{1}{2}" -f $package.SftpHost,$package.SftpPort,$product.RemotePath)
        Open-AVWorkstationToolkitExplorerPath -Path $verified.Path -SelectFile
        Add-ActivityLine ("Downloaded and verified {0} {1}; AV Workstation Toolkit did not execute the installer. SHA-256: {2}" -f $product.Name,$product.Version,$verified.Sha256) -Level Success
    }
    catch {
        $message = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
        Add-ActivityLine $message -Level Error
        [Windows.MessageBox]::Show($message,'Crestron package validation failed','OK','Error') | Out-Null
    }
}

function Open-AVWorkstationToolkitPackageDelivery {
    if ($state.Busy) { return }
    $item = $controls.PackageGrid.SelectedItem
    if ($null -eq $item -or -not [bool]$item.DeliveryAvailable) { return }

    if ([string]$item.DeliveryAction -eq 'DownloadHttps') {
        Invoke-AVWorkstationToolkitDirectDownloadDelivery -Item $item
        return
    }
    if ([string]$item.DeliveryAction -eq 'AuthenticatedSftp') {
        Invoke-AVWorkstationToolkitAuthenticatedSftpDelivery -Item $item
        return
    }
    if ([string]$item.DeliveryAction -eq 'ShowFile' -and -not [string]::IsNullOrWhiteSpace([string]$item.DeliveryPath)) {
        $path = [IO.Path]::GetFullPath([string]$item.DeliveryPath)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            [Windows.MessageBox]::Show('The verified package is no longer present. Refresh the plan and try again.','Package unavailable','OK','Warning') | Out-Null
            return
        }
        Open-AVWorkstationToolkitExplorerPath -Path $path -SelectFile
        Add-ActivityLine ("Revealed the cached or bundled installer for {0}; AV Workstation Toolkit did not execute it." -f $item.Name) -Level Success
        return
    }

    if ([string]$item.DeliveryAction -ne 'OpenUri') { return }
    $uri = $null
    if (-not [uri]::TryCreate([string]$item.DeliveryUri,[UriKind]::Absolute,[ref]$uri) -or
        $uri.Scheme -ne [Uri]::UriSchemeHttps -or -not [string]::IsNullOrEmpty($uri.UserInfo)) {
        [Windows.MessageBox]::Show('The catalogued vendor download address is invalid.','Invalid vendor address','OK','Error') | Out-Null
        return
    }
    Open-AVWorkstationToolkitHttpsUri -Uri $uri.AbsoluteUri
    Add-ActivityLine ("Opened the official vendor download page for {0}." -f $item.Name) -Level Success
}

$controls.SearchBox.Add_TextChanged({ Update-AVWorkstationToolkitFilter })
foreach ($filterName in @('StandardFilter','FieldFilter','DeveloperFilter','OptionalFilter')) {
    $controls[$filterName].Add_Checked({ Update-AVWorkstationToolkitFilter })
    $controls[$filterName].Add_Unchecked({ Update-AVWorkstationToolkitFilter })
}
$controls.CatalogPresetFilter.Add_SelectionChanged({ Update-AVWorkstationToolkitFilter })
$controls.ManufacturerFilter.Add_SelectionChanged({ if (-not $state.UpdatingManufacturers) { Update-AVWorkstationToolkitFilter } })
$controls.DisciplineFilter.Add_SelectionChanged({ Update-AVWorkstationToolkitFilter })
$controls.RefreshButton.Add_Click({ Refresh-AVWorkstationToolkitPlan })
$controls.DetailsButton.Add_Click({ Show-AVWorkstationToolkitCatalogDetail })
$controls.DiagnosticsButton.Add_Click({ Show-AVWorkstationToolkitDiagnostics })
$controls.RecheckButton.Add_Click({ Refresh-AVWorkstationToolkitPlan })
$controls.ExportPlanMenuItem.Add_Click({ Export-AVWorkstationToolkitPlan })
$controls.OpenLogsMenuItem.Add_Click({ Open-AVWorkstationToolkitLogs })
$controls.ExitMenuItem.Add_Click({ $window.Close() })
$controls.RefreshPlanMenuItem.Add_Click({ Refresh-AVWorkstationToolkitPlan })
$controls.DiagnosticsMenuItem.Add_Click({ Show-AVWorkstationToolkitDiagnostics })
$controls.CheckSystemMenuItem.Add_Click({ Refresh-AVWorkstationToolkitPlan })
$controls.SafetySecurityMenuItem.Add_Click({ Show-AVWorkstationToolkitSafetySecurity })
$controls.AboutMenuItem.Add_Click({ Show-AVWorkstationToolkitAbout })
$controls.AllAppsButton.Add_Click({ Set-AVWorkstationToolkitQuickView -View All })
$controls.SelectMissingButton.Add_Click({ Set-AVWorkstationToolkitQuickView -View Missing })
$controls.SelectUpdatesButton.Add_Click({ Set-AVWorkstationToolkitQuickView -View Updates })
$controls.ClearSelectionButton.Add_Click({ Clear-AVWorkstationToolkitSelection })
$controls.GetPackageButton.Add_Click({ Open-AVWorkstationToolkitPackageDelivery })
$controls.InstallButton.Add_Click({ Start-AVWorkstationToolkitAction -Action Install })
$controls.UpdateButton.Add_Click({ Start-AVWorkstationToolkitAction -Action Update })
$controls.CancelButton.Add_Click({
    if (-not [string]::IsNullOrWhiteSpace($state.CancelPath)) {
        'Stop requested by user.' | Set-Content -LiteralPath $state.CancelPath -Encoding UTF8
        Add-ActivityLine 'Stop requested; the worker will stop before the next package.' -Level Warning
        $controls.CancelButton.IsEnabled = $false
    }
})
# SourceUpdated is raised only when an intentional mouse, keyboard, or UI
# Automation toggle writes through the TwoWay binding. WPF cell creation,
# recycling, filtering, and target refreshes must not rewrite the model.
$controls.PackageGrid.AddHandler([Windows.Data.Binding]::SourceUpdatedEvent,[System.EventHandler[Windows.Data.DataTransferEventArgs]]{ param($toggleSender,$toggleEventArgs) Sync-AVWorkstationToolkitSelectionFromToggle -ToggleEventArgs $toggleEventArgs },$true)
$controls.PackageGrid.Add_SelectionChanged({ Update-DeliveryState })
$controls.PackageGrid.Add_MouseDoubleClick({ if ($null -ne $controls.PackageGrid.SelectedItem) { Show-AVWorkstationToolkitCatalogDetail } })
$controls.PackageGrid.Add_SizeChanged({ Update-AVWorkstationToolkitGridLayout })
$controls.PackageGrid.Add_Sorting({
    param($gridSender,$sortingEventArgs)
    $sortingEventArgs.Handled = $true
    $memberPath = [string]$sortingEventArgs.Column.SortMemberPath
    if (-not $sortingEventArgs.Column.CanUserSort -or [string]::IsNullOrWhiteSpace($memberPath)) { return }
    Set-AVWorkstationToolkitSort -MemberPath $memberPath
})
$window.Add_PreviewKeyDown({
    param($keySender,$keyEventArgs)
    if ($keyEventArgs.Key -eq [Windows.Input.Key]::F5 -and -not $state.Busy) {
        $keyEventArgs.Handled = $true
        Refresh-AVWorkstationToolkitPlan
    }
})

$window.Add_Closing({
    param($windowSender,$closingEventArgs)
    if ($state.Busy -and $null -ne $state.Process) {
        $answer = [Windows.MessageBox]::Show('An application action is still running. Closing the interface will not terminate the current installer. Close anyway?','Action still running','YesNo','Warning')
        if ($answer -ne 'Yes') { $closingEventArgs.Cancel = $true; return }
        $state.Closing = $true
    }
})

if ($SmokeTest -or -not [string]::IsNullOrWhiteSpace($RenderPreviewPath)) {
    Set-AVWorkstationToolkitPlan -Plan (Get-MockAVWorkstationToolkitPlan)
    $state.Items | Where-Object { $_.Action -in @('Install','Update') } | Select-Object -First 2 | ForEach-Object { $_.Selected = $true }
    $controls.PackageGrid.Items.Refresh()
    Update-SelectionState

    if ($SmokeTest) {
        $expectedCatalogCount = @(Get-AVWorkstationToolkitCatalog).Count
        if ($state.Items.Count -ne $expectedCatalogCount) { throw "Smoke test expected $expectedCatalogCount catalog items; found $($state.Items.Count)." }
        $controls.PackageGrid.SelectedItem = @($state.Items | Where-Object Provider -eq 'External' | Select-Object -First 1)[0]
        $detailSmokeWindow = Show-AVWorkstationToolkitCatalogDetail -BuildOnly
        try {
            if ($null -eq $detailSmokeWindow -or $detailSmokeWindow.Title -notmatch '^Application details - ' -or $null -eq $detailSmokeWindow.Content) {
                throw 'Smoke test could not construct the read-only catalog detail window.'
            }
        }
        finally { if ($null -ne $detailSmokeWindow) { $detailSmokeWindow.Close() } }

        $diagnosticSmokeWindow = Show-AVWorkstationToolkitDiagnostics -BuildOnly
        try {
            if ($null -eq $diagnosticSmokeWindow -or $diagnosticSmokeWindow.Title -ne 'AV Workstation Toolkit diagnostics' -or $null -eq $diagnosticSmokeWindow.Content) {
                throw 'Smoke test could not construct the read-only diagnostics window.'
            }
        }
        finally { if ($null -ne $diagnosticSmokeWindow) { $diagnosticSmokeWindow.Close() } }

        $safetySmokeWindow = Show-AVWorkstationToolkitSafetySecurity -BuildOnly
        try {
            if ($null -eq $safetySmokeWindow -or $safetySmokeWindow.Title -ne 'Safety & Security - AV Workstation Toolkit' -or $null -eq $safetySmokeWindow.Content) {
                throw 'Smoke test could not construct the read-only safety and security window.'
            }
        }
        finally { if ($null -ne $safetySmokeWindow) { $safetySmokeWindow.Close() } }

        $aboutSmokeWindow = Show-AVWorkstationToolkitAbout -BuildOnly
        try {
            if ($null -eq $aboutSmokeWindow -or $aboutSmokeWindow.Title -ne 'About AV Workstation Toolkit' -or
                [string]$aboutSmokeWindow.Tag.Product -ne 'AV Workstation Toolkit' -or [string]$aboutSmokeWindow.Tag.Version -ne $productVersion -or
                [string]$aboutSmokeWindow.Tag.Mode -notin @('Source checkout','Packaged app')) {
                throw 'Smoke test could not construct the dark product-identity About window.'
            }
        }
        finally { if ($null -ne $aboutSmokeWindow) { $aboutSmokeWindow.Close() } }

        function Assert-AVWorkstationToolkitSmokeOrder {
            param([Parameter(Mandatory)][object[]]$Values, [ValidateSet('Ascending','Descending')][string]$Direction)
            for ($index = 1; $index -lt $Values.Count; $index++) {
                $comparison = [Collections.Comparer]::DefaultInvariant.Compare($Values[$index - 1],$Values[$index])
                if (($Direction -eq 'Ascending' -and $comparison -gt 0) -or ($Direction -eq 'Descending' -and $comparison -lt 0)) {
                    throw "Smoke sort is not $Direction at index $index."
                }
            }
        }

        $selectedIds = @($state.Items | Where-Object Selected | ForEach-Object Id)
        $selectedGridItem = $controls.PackageGrid.SelectedItem
        foreach ($memberPath in @('ApplicationSortKey','VendorSortKey','PrioritySortKey','StatusSortKey','VersionSortKey','RiskSortKey')) {
            Set-AVWorkstationToolkitSort -MemberPath $memberPath -Direction Ascending
            Assert-AVWorkstationToolkitSmokeOrder -Values @($state.View | ForEach-Object { $_.$memberPath }) -Direction Ascending
            if (@($selectedIds | Where-Object { $_ -notin @($state.Items | Where-Object Selected | ForEach-Object Id) }).Count -gt 0) {
                throw "Sorting by $memberPath changed row checkbox selection."
            }
            if (-not [object]::ReferenceEquals($selectedGridItem,$controls.PackageGrid.SelectedItem)) {
                throw "Sorting by $memberPath changed the selected grid row."
            }
        }
        Set-AVWorkstationToolkitSort -MemberPath ApplicationSortKey -Direction Ascending
        Set-AVWorkstationToolkitSort -MemberPath ApplicationSortKey
        Assert-AVWorkstationToolkitSmokeOrder -Values @($state.View | ForEach-Object ApplicationSortKey) -Direction Descending
        if ($state.SortDirection -ne 'Descending' -or [string]$controls.PackageGrid.Columns[1].SortDirection -ne 'Descending') {
            throw 'Repeated column sorting did not toggle or expose the descending indicator.'
        }
        if ($controls.PackageGrid.Columns[0].CanUserSort -or $controls.PackageGrid.Columns[7].CanUserSort) {
            throw 'Selection or purpose/restriction columns unexpectedly allow sorting.'
        }
        if ([string]::CompareOrdinal((Get-AVWorkstationToolkitVersionSortKey '1.9'),(Get-AVWorkstationToolkitVersionSortKey '1.10')) -ge 0 -or
            [string]::CompareOrdinal((Get-AVWorkstationToolkitVersionSortKey '2.5.3'),(Get-AVWorkstationToolkitVersionSortKey '10.0')) -ge 0) {
            throw 'Version-aware sorting does not order numeric segments correctly.'
        }

        Set-AVWorkstationToolkitQuickView -View Missing
        $missingItems = @($state.View)
        if ($missingItems.Count -eq 0 -or @($missingItems | Where-Object { -not (Test-AVWorkstationToolkitQuickViewFilter -Item $_) }).Count -gt 0 -or
            @($missingItems | Where-Object { $_.CanSelect -and $_.Action -eq 'Install' -and -not $_.Selected }).Count -gt 0) {
            throw 'Missing apps quick view did not filter and select eligible rows.'
        }
        if ($controls.QuickViewState.Text -ne 'Showing: Missing apps' -or $controls.SelectMissingButton.Style -ne $window.FindResource('ActiveQuickViewButton')) {
            throw 'Missing apps quick view is not visibly active.'
        }

        $qsysManufacturer = @($controls.ManufacturerFilter.Items | Where-Object { [string]$_.Tag -eq 'Q-SYS' } | Select-Object -First 1)
        if ($qsysManufacturer.Count -ne 1) { throw 'Smoke test could not locate the normalized Q-SYS manufacturer.' }
        $controls.ManufacturerFilter.SelectedItem = $qsysManufacturer[0]
        $controls.SearchBox.Text = 'Designer'
        Set-AVWorkstationToolkitQuickView -View Updates
        $updateItems = @($state.View)
        if ($updateItems.Count -eq 0 -or @($updateItems | Where-Object { $_.Vendor -ne 'Q-SYS' -or $_.SearchText -notmatch 'Designer' }).Count -gt 0 -or
            @($updateItems | Where-Object { $_.CanSelect -and $_.Action -eq 'Update' -and -not $_.Selected }).Count -gt 0 -or
            @($updateItems | Where-Object { -not $_.CanSelect -and $_.Selected }).Count -gt 0) {
            throw 'Available updates quick view did not compose filters or preserve eligibility.'
        }
        $sortBeforeClear = @($state.SortMemberPath,$state.SortDirection) -join '|'
        Clear-AVWorkstationToolkitSelection
        if ($state.QuickView -ne 'Updates' -or $controls.SearchBox.Text -ne 'Designer' -or
            (Get-AVWorkstationToolkitFilterKey -Control $controls.ManufacturerFilter) -ne 'Q-SYS' -or
            (@($state.SortMemberPath,$state.SortDirection) -join '|') -ne $sortBeforeClear -or @($state.Items | Where-Object Selected).Count -ne 0) {
            throw 'Clear selection unexpectedly changed the quick view, filters, or sort.'
        }

        Set-AVWorkstationToolkitPlan -Plan (Get-MockAVWorkstationToolkitPlan)
        if ($state.QuickView -ne 'Updates' -or $state.SortDirection -ne 'Descending' -or
            (Get-AVWorkstationToolkitFilterKey -Control $controls.ManufacturerFilter) -ne 'Q-SYS' -or $controls.SearchBox.Text -ne 'Designer') {
            throw 'Refresh did not preserve quick-view filters and sorting.'
        }
        Set-AVWorkstationToolkitQuickView -View All
        if ($state.QuickView -ne 'All' -or (Get-AVWorkstationToolkitFilterKey -Control $controls.ManufacturerFilter) -ne 'Q-SYS' -or $controls.SearchBox.Text -ne 'Designer') {
            throw 'All apps did not return to the normal list while preserving existing filters.'
        }

        $controls.SearchBox.Text = ''
        $controls.ManufacturerFilter.SelectedItem = $controls.ManufacturerFilter.Items[0]
        Set-AVWorkstationToolkitQuickView -View All
        Set-AVWorkstationToolkitSort -MemberPath ApplicationSortKey -Direction Ascending
        Clear-AVWorkstationToolkitSelection

        function Get-AVWorkstationToolkitSmokeDescendant {
            param([Parameter(Mandatory)][Windows.DependencyObject]$Root, [Parameter(Mandatory)][type]$Type)

            if ($Type.IsInstanceOfType($Root)) { return $Root }
            for ($childIndex = 0; $childIndex -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($Root); $childIndex++) {
                $found = Get-AVWorkstationToolkitSmokeDescendant -Root ([Windows.Media.VisualTreeHelper]::GetChild($Root,$childIndex)) -Type $Type
                if ($null -ne $found) { return $found }
            }
            return $null
        }

        function Get-AVWorkstationToolkitSmokeSelectionCheckBox {
            param([Parameter(Mandatory)]$Item)

            $controls.PackageGrid.ScrollIntoView($Item,$controls.PackageGrid.Columns[0])
            $controls.PackageGrid.UpdateLayout()
            $window.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::Render)
            $row = $controls.PackageGrid.ItemContainerGenerator.ContainerFromItem($Item) -as [Windows.Controls.DataGridRow]
            if ($null -eq $row) { throw "Smoke test could not generate a DataGrid row for $($Item.Id)." }
            $checkBox = Get-AVWorkstationToolkitSmokeDescendant -Root $row -Type ([Windows.Controls.CheckBox])
            if ($null -eq $checkBox) { throw "Smoke test could not locate the generated selection checkbox for $($Item.Id)." }
            return [Windows.Controls.CheckBox]$checkBox
        }

        function Invoke-AVWorkstationToolkitSmokeToggle {
            param([Parameter(Mandatory)][Windows.Controls.CheckBox]$CheckBox)

            $peer = [Windows.Automation.Peers.CheckBoxAutomationPeer]::new($CheckBox)
            $provider = $peer.GetPattern([Windows.Automation.Peers.PatternInterface]::Toggle)
            if ($null -eq $provider) { throw 'Generated selection checkbox does not expose the standard toggle pattern.' }
            $provider.Toggle()
            $window.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::DataBind)
        }

        $window.ShowInTaskbar = $false
        $window.WindowStartupLocation = [Windows.WindowStartupLocation]::Manual
        if ([string]::IsNullOrWhiteSpace($RenderPreviewPath)) {
            $window.ShowActivated = $false
            $window.Left = -32000
            $window.Top = -32000
        }
        else {
            $window.ShowActivated = $true
            $window.Left = 0
            $window.Top = 0
            $window.Topmost = $true
            if ($RenderWidth -gt 0) { $window.Width = $RenderWidth }
            if ($RenderHeight -gt 0) { $window.Height = $RenderHeight }
        }
        $window.Show()
        $window.UpdateLayout()

        $sourceUpdateState = [pscustomobject]@{ Count=0; TargetChecked=$null; ContextSelected=$null; ContextId='' }
        $sourceUpdateHandler = [System.EventHandler[Windows.Data.DataTransferEventArgs]]{
            param($sourceUpdatedSender,$sourceUpdatedEventArgs)
            if ($sourceUpdatedEventArgs.Property -eq [Windows.Controls.Primitives.ToggleButton]::IsCheckedProperty -and
                $sourceUpdatedEventArgs.TargetObject -is [Windows.Controls.CheckBox]) {
                $sourceUpdateState.Count++
                $sourceUpdateState.TargetChecked = [bool]$sourceUpdatedEventArgs.TargetObject.IsChecked
                $sourceUpdateState.ContextSelected = [bool]$sourceUpdatedEventArgs.TargetObject.DataContext.Selected
                $sourceUpdateState.ContextId = [string]$sourceUpdatedEventArgs.TargetObject.DataContext.Id
            }
        }.GetNewClosure()
        $controls.PackageGrid.AddHandler([Windows.Data.Binding]::SourceUpdatedEvent,$sourceUpdateHandler,$true)
        $originalReboot = $state.Plan.Reboot
        try {
            $state.Plan.Reboot = [pscustomobject]@{
                Pending = $true
                Reasons = @('Component Based Servicing')
                Summary = 'Pending reboot smoke fixture'
            }
            Update-SelectionState

            $updateItem = @($state.Items | Where-Object { $_.Id -eq 'Microsoft.VisualStudioCode' -and $_.CanSelect -and $_.Action -eq 'Update' })[0]
            if ($null -eq $updateItem -or $updateItem.Risk -ne 'None') { throw 'Smoke test could not locate the low-risk Visual Studio Code update fixture.' }
            $updateCheckBox = Get-AVWorkstationToolkitSmokeSelectionCheckBox -Item $updateItem
            $eventCountBefore = $sourceUpdateState.Count
            Invoke-AVWorkstationToolkitSmokeToggle -CheckBox $updateCheckBox
            if (-not [bool]$updateItem.Selected -or -not [bool]$updateCheckBox.IsChecked -or
                $sourceUpdateState.Count -ne ($eventCountBefore + 1) -or $controls.SelectionSummary.Text -ne '1 selected | 0 install | 1 update' -or
                $controls.InstallButton.IsEnabled -or -not $controls.UpdateButton.IsEnabled -or $controls.UpdateButton.Content -ne 'Update selected (1)') {
                throw ('Selecting a low-risk update did not synchronize the generated checkbox, model, footer, and action buttons: model={0}; visual={1}; sourceUpdates={2}->{3}; eventTargetChecked={4}; eventContextSelected={5}; eventContextId={6}; summary={7}; installEnabled={8}; updateEnabled={9}; updateContent={10}.' -f
                    [bool]$updateItem.Selected,[bool]$updateCheckBox.IsChecked,$eventCountBefore,$sourceUpdateState.Count,
                    $sourceUpdateState.TargetChecked,$sourceUpdateState.ContextSelected,$sourceUpdateState.ContextId,$controls.SelectionSummary.Text,
                    $controls.InstallButton.IsEnabled,$controls.UpdateButton.IsEnabled,$controls.UpdateButton.Content)
            }

            $eventCountBefore = $sourceUpdateState.Count
            Invoke-AVWorkstationToolkitSmokeToggle -CheckBox $updateCheckBox
            if ([bool]$updateItem.Selected -or [bool]$updateCheckBox.IsChecked -or
                $sourceUpdateState.Count -ne ($eventCountBefore + 1) -or $controls.SelectionSummary.Text -ne 'Nothing selected' -or
                $controls.InstallButton.IsEnabled -or $controls.UpdateButton.IsEnabled) {
                throw 'Deselecting an update did not clear the generated checkbox, model, footer, and action buttons.'
            }

            $installItem = @($state.Items | Where-Object { $_.CanSelect -and $_.Action -eq 'Install' -and $_.Risk -eq 'None' } | Select-Object -First 1)[0]
            if ($null -eq $installItem) { throw 'Smoke test could not locate a low-risk install fixture.' }
            $installCheckBox = Get-AVWorkstationToolkitSmokeSelectionCheckBox -Item $installItem
            Invoke-AVWorkstationToolkitSmokeToggle -CheckBox $installCheckBox
            if (-not [bool]$installItem.Selected -or $controls.SelectionSummary.Text -ne '1 selected | 1 install | 0 update' -or
                -not $controls.InstallButton.IsEnabled -or $controls.UpdateButton.IsEnabled -or $controls.InstallButton.Content -ne 'Install selected (1)') {
                throw 'Selecting a low-risk install did not synchronize the shared action-selection model.'
            }

            $updateCheckBox = Get-AVWorkstationToolkitSmokeSelectionCheckBox -Item $updateItem
            Invoke-AVWorkstationToolkitSmokeToggle -CheckBox $updateCheckBox
            if ($controls.SelectionSummary.Text -ne '2 selected | 1 install | 1 update' -or
                -not $controls.InstallButton.IsEnabled -or -not $controls.UpdateButton.IsEnabled) {
                throw 'Combined install/update selection did not keep both action buttons synchronized.'
            }

            $selectedIdsBeforeRefresh = @($state.Items | Where-Object Selected | ForEach-Object Id | Sort-Object)
            $sourceUpdatesBeforeRefresh = $sourceUpdateState.Count
            $controls.PackageGrid.Items.Refresh()
            $controls.SearchBox.Text = [string]$updateItem.Name
            $controls.SearchBox.Text = ''
            Set-AVWorkstationToolkitSort -MemberPath VendorSortKey -Direction Descending
            $controls.PackageGrid.ItemsSource = $null
            $controls.PackageGrid.ItemsSource = $state.View
            $controls.PackageGrid.Items.Refresh()
            $window.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::DataBind)
            $selectedIdsAfterRefresh = @($state.Items | Where-Object Selected | ForEach-Object Id | Sort-Object)
            if (($selectedIdsAfterRefresh -join '|') -ne ($selectedIdsBeforeRefresh -join '|') -or
                $sourceUpdateState.Count -ne $sourceUpdatesBeforeRefresh -or $controls.SelectionSummary.Text -ne '2 selected | 1 install | 1 update' -or
                -not $controls.InstallButton.IsEnabled -or -not $controls.UpdateButton.IsEnabled) {
                throw 'Refresh, filtering, sorting, or DataGrid rebinding changed selection state or emitted a user-toggle update.'
            }

            $blockedItem = @($state.Items | Where-Object { -not $_.CanSelect } | Select-Object -First 1)[0]
            if ($null -eq $blockedItem) { throw 'Smoke test could not locate a non-actionable catalog fixture.' }
            $blockedItem.Selected = $false
            $blockedCheckBox = Get-AVWorkstationToolkitSmokeSelectionCheckBox -Item $blockedItem
            if ($blockedCheckBox.IsEnabled -or [bool]$blockedCheckBox.IsChecked -or [bool]$blockedItem.Selected) {
                throw 'A non-actionable catalog checkbox was enabled or selected.'
            }
            $blockedEventCount = $sourceUpdateState.Count
            try { Invoke-AVWorkstationToolkitSmokeToggle -CheckBox $blockedCheckBox } catch [System.Windows.Automation.ElementNotEnabledException] {}
            if ([bool]$blockedItem.Selected -or $sourceUpdateState.Count -ne $blockedEventCount) {
                throw 'A disabled catalog checkbox changed the selection model.'
            }

            Clear-AVWorkstationToolkitSelection
            if ($controls.SelectionSummary.Text -ne 'Nothing selected' -or $controls.InstallButton.IsEnabled -or $controls.UpdateButton.IsEnabled) {
                throw 'Selection cleanup did not restore the footer and action buttons.'
            }
            Write-Output ('UI_SELECTION_FLOW_OK sourceUpdates={0} refresh=stable pendingReboot=lowRiskAllowed actions=shared' -f $sourceUpdateState.Count)
        }
        finally {
            $state.Plan.Reboot = $originalReboot
            $controls.PackageGrid.RemoveHandler([Windows.Data.Binding]::SourceUpdatedEvent,$sourceUpdateHandler)
            Clear-AVWorkstationToolkitSelection
        }
        Write-Output 'UI_BEHAVIOR_OK sorting=6 quickViews=3 persistence=passed selectionToggle=realGrid'

        $progressSmokePath = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-progress-{0}.jsonl' -f [guid]::NewGuid().ToString('N'))
        $cancelSmokePath = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-cancel-{0}.tmp' -f [guid]::NewGuid().ToString('N'))
        try {
            $progressStream = [IO.File]::Open($progressSmokePath,[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite)
            try {
                $writer = New-Object IO.StreamWriter($progressStream,(New-Object Text.UTF8Encoding($true)))
                $writer.WriteLine('{"Level":"Info","Message":"progress-sharing-smoke"}')
                $writer.Flush()
                $state.ProgressPath = $progressSmokePath
                $state.ProgressByteOffset = [int64]0
                $state.ProgressRemainder = ''
                Read-AVWorkstationToolkitProgress -Flush
                if ($controls.ActivityLog.Text -notmatch 'progress-sharing-smoke') { throw 'Smoke test could not read progress while the writer held the file open.' }
            }
            finally { $progressStream.Dispose() }

            $state.Process = [pscustomobject]@{}
            $state.CancelPath = $cancelSmokePath
            Set-AVWorkstationToolkitBusy -Busy $true -Message 'Cancellation smoke test'
            if (-not $controls.CancelButton.IsEnabled) { throw 'Smoke test could not re-enable Stop for a new action.' }
        }
        finally {
            $state.Process = $null
            $state.ProgressPath = ''
            $state.CancelPath = ''
            Set-AVWorkstationToolkitBusy -Busy $false
            Remove-Item -LiteralPath $progressSmokePath,$cancelSmokePath -Force -ErrorAction SilentlyContinue
        }
        Write-Output ('SMOKE_OK controls={0} packages={1}' -f $controls.Count,$state.Items.Count)
        if ([string]::IsNullOrWhiteSpace($RenderPreviewPath)) { $window.Close() }
    }
    if (-not [string]::IsNullOrWhiteSpace($RenderPreviewPath)) {
        Set-AVWorkstationToolkitQuickView -View $RenderQuickView
        if ($RenderWidth -ne 0) {
            if ($RenderWidth -lt $window.MinWidth -or $RenderWidth -gt 4096) { throw "RenderWidth must be between $($window.MinWidth) and 4096." }
            $window.Width = $RenderWidth
        }
        if ($RenderHeight -ne 0) {
            if ($RenderHeight -lt $window.MinHeight -or $RenderHeight -gt 2160) { throw "RenderHeight must be between $($window.MinHeight) and 2160." }
            $window.Height = $RenderHeight
        }
        $previewFullPath = [IO.Path]::GetFullPath($RenderPreviewPath)
        $previewDirectory = [IO.Path]::GetDirectoryName($previewFullPath)
        New-Item -ItemType Directory -Path $previewDirectory -Force | Out-Null
        # RenderTargetBitmap can return an all-black HWND frame in locked or
        # noninteractive sessions. Visual QA therefore presents a borderless,
        # topmost test window and captures its exact desktop rectangle. The
        # pixel-content test fails closed if a usable desktop is unavailable.
        $window.WindowStyle = [Windows.WindowStyle]::None
        $window.ResizeMode = [Windows.ResizeMode]::NoResize
        $window.WindowStartupLocation = [Windows.WindowStartupLocation]::Manual
        $window.ShowInTaskbar = $false
        $window.Left = 0
        $window.Top = 0
        $window.Topmost = $true
        $previewItem = $null
        $visiblePreviewItems = @($state.View)
        if ($visiblePreviewItems.Count -gt 0 -and $controls.PackageGrid.Columns.Count -gt 0) {
            $previewItems = @($visiblePreviewItems | Where-Object Provider -eq 'External' | Select-Object -First 1)
            $previewItem = if ($previewItems.Count -gt 0) { $previewItems[0] } else { $visiblePreviewItems[0] }
            $controls.PackageGrid.SelectedItem = $previewItem
            Update-DeliveryState
        }

        # Validate layout against the requested logical viewport before showing
        # the HWND. Windows can clamp an oversized test window to the current
        # interactive work area, which is a capture limitation rather than a
        # different layout target.
        $layoutWidth = if ($RenderWidth -gt 0) { [double]$RenderWidth } else { [double]$window.Width }
        $layoutHeight = if ($RenderHeight -gt 0) { [double]$RenderHeight } else { [double]$window.Height }
        $layoutSize = [Windows.Size]::new($layoutWidth,$layoutHeight)
        $window.Measure($layoutSize)
        $window.Arrange([Windows.Rect]::new([Windows.Point]::new(0,0),$layoutSize))
        $window.UpdateLayout()
        $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Render)
        foreach ($name in @('TopMenu','MainContent','SidebarScroll','DetailsButton','RefreshButton','PackageGrid','ActivityLog','FooterGrid','SelectionPanel','ActionBar')) {
            $element = $controls[$name]
            if ($element.Visibility -ne [Windows.Visibility]::Visible -or $element.ActualWidth -le 0 -or $element.ActualHeight -le 0) { continue }
            $origin = $element.TranslatePoint([Windows.Point]::new(0,0),$window)
            $right = $origin.X + $element.ActualWidth
            $bottom = $origin.Y + $element.ActualHeight
            if ($origin.X -lt -0.75 -or $origin.Y -lt -0.75 -or $right -gt $window.ActualWidth + 0.75 -or $bottom -gt $window.ActualHeight + 0.75) {
                throw ("Layout element {0} is outside the {1}x{2} viewport: x={3}, y={4}, right={5}, bottom={6}." -f $name,$window.ActualWidth,$window.ActualHeight,$origin.X,$origin.Y,$right,$bottom)
            }
        }
        $actionRequiredWidth = [double]0
        foreach ($child in @($controls.ActionBar.Children | Where-Object Visibility -eq 'Visible')) {
            $actionRequiredWidth += $child.DesiredSize.Width
        }
        if ($controls.ActionBar.ActualWidth + 0.75 -lt $actionRequiredWidth) {
            throw ("Action controls require {0}px but received {1}px." -f $actionRequiredWidth,$controls.ActionBar.ActualWidth)
        }
        if ([Windows.Controls.ScrollViewer]::GetVerticalScrollBarVisibility($controls.SidebarScroll) -ne [Windows.Controls.ScrollBarVisibility]::Auto) {
            throw 'Sidebar does not expose intentional automatic vertical scrolling.'
        }
        if ($SmokeTest) {
            Write-Output ('GRID_LAYOUT viewport={0} columns={1}' -f ([Math]::Round($controls.PackageGrid.ActualWidth,1)),(($controls.PackageGrid.Columns | ForEach-Object { '{0}:width={1};actual={2};min={3};max={4}' -f $_.Header,$_.Width,([Math]::Round($_.ActualWidth,1)),$_.MinWidth,$_.MaxWidth }) -join ','))
            Write-Output ('LAYOUT_OK viewport={0}x{1} quickView={2} footer={3} action={4} actionRequired={5} sidebarViewport={6} sidebarExtent={7}' -f ([Math]::Round($window.ActualWidth,1)),([Math]::Round($window.ActualHeight,1)),$state.QuickView,([Math]::Round($controls.FooterGrid.ActualHeight,1)),([Math]::Round($controls.ActionBar.ActualWidth,1)),([Math]::Round($actionRequiredWidth,1)),([Math]::Round($controls.SidebarScroll.ViewportHeight,1)),([Math]::Round($controls.SidebarScroll.ExtentHeight,1)))
        }

        $window.Show()
        [void]$window.Activate()
        $window.UpdateLayout()
        if ($null -ne $previewItem) {
            $controls.PackageGrid.ScrollIntoView($previewItem,$controls.PackageGrid.Columns[0])
            $controls.PackageGrid.UpdateLayout()
        }
        $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Render)
        $width = [Math]::Max(1,[int][Math]::Ceiling($window.ActualWidth))
        $height = [Math]::Max(1,[int][Math]::Ceiling($window.ActualHeight))
        Start-Sleep -Milliseconds 250
        Add-Type -AssemblyName System.Drawing
        $captureError = if ($width -ne [int]$layoutWidth -or $height -ne [int]$layoutHeight) {
            'desktop work area clamped capture to {0}x{1}; requested {2}x{3}' -f $width,$height,[int]$layoutWidth,[int]$layoutHeight
        }
        else { '' }
        if ([string]::IsNullOrWhiteSpace($captureError)) {
            try {
                $bitmap = [Drawing.Bitmap]::new($width,$height,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
                $graphics = [Drawing.Graphics]::FromImage($bitmap)
                try {
                    $screenOrigin = $window.PointToScreen([Windows.Point]::new(0,0))
                    $sourcePoint = [Drawing.Point]::new([int][Math]::Floor($screenOrigin.X),[int][Math]::Floor($screenOrigin.Y))
                    $graphics.CopyFromScreen($sourcePoint,[Drawing.Point]::Empty,[Drawing.Size]::new($width,$height),[Drawing.CopyPixelOperation]::SourceCopy)
                    $bitmap.Save($previewFullPath,[Drawing.Imaging.ImageFormat]::Png)
                }
                finally {
                    $graphics.Dispose()
                    $bitmap.Dispose()
                }
            }
            catch {
                $captureError = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($captureError)) {
            $renderBitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($width,$height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
            $renderBitmap.Render($window)
            $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
            $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($renderBitmap))
            $stream = [IO.File]::Open($previewFullPath,[IO.FileMode]::Create)
            try { $encoder.Save($stream) } finally { $stream.Dispose() }
        }
        $window.Close()
        if ([string]::IsNullOrWhiteSpace($captureError)) { Write-Output ('PREVIEW_OK ' + $previewFullPath) }
        else { Write-Output ('PREVIEW_UNAVAILABLE reason={0} fallback={1}' -f $captureError,$previewFullPath) }
    }
    return
}

$window.Add_ContentRendered({
    if ($null -eq $state.Plan) { Refresh-AVWorkstationToolkitPlan }
})
[void]$window.ShowDialog()
