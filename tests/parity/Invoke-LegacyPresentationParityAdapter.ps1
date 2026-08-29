<#[.SYNOPSIS] Evaluates deterministic presentation semantics through the shipping PowerShell filter/controller rules. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixturePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Import-Module (Join-Path $repositoryRoot 'scripts\AVWorkstationToolkit.Core.psd1') -Force
$fixture = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json -ErrorAction Stop
if ([int]$fixture.SchemaVersion -ne 1) { throw "Unsupported presentation parity schema: $($fixture.SchemaVersion)." }

$results = foreach ($case in @($fixture.Cases)) {
    $items = @($fixture.Packages | ForEach-Object {
        $package = $_
        $canSelect = [string]$package.Action -in @('Install','Update')
        $priorityRank = @{ P1=0; P2=1; UTILITY=2; DEV=3 }
        $statusRank = @{ UpdateAvailable=0; Missing=1; ManualUpdate=2; Held=3; Error=4; InventoryIncomplete=5; InventoryUnavailable=6; CheckUnavailable=7; Current=8; Inventory=9; NotDetected=10; Manual=11; Awareness=12 }
        $riskRank = @{ None=0; Service=1; Listener=2; Driver=3 }
        [pscustomobject]@{
            Id = [string]$package.Id; Name = [string]$package.Name; Vendor = [string]$package.Vendor
            ProductFamily = [string]$package.Name; Note = [string]$package.Name; CatalogNotes = ''
            Profile = [string]$package.Profile; Priority = [string]$package.Priority; Risk = [string]$package.Risk
            Status = [string]$package.Status; Action = [string]$package.Action; CanSelect = $canSelect; Selected = $false
            Installed = [bool]$package.Installed; InstalledVersion = [string]$package.InstalledVersion; AvailableVersion = [string]$package.AvailableVersion
            ApplicationType = @($package.ApplicationTypes); Roles = @($package.Roles); SupportedOS = @('Windows'); CatalogTags = @()
            WorkflowCategories = @(); InstallationForms = @('WinGet'); LicensingModel = @('FREE'); DownloadAccess = @('PUBLIC-DL')
            DistributionPolicy = 'PackageManagerOnly'; DeploymentClass = 'Managed'; CurrentOrLegacy = 'Current'
            RequiresVendorAccount = $false; RequiresDealerAccount = $false; RequiresTraining = $false
            InstallsDriver = ([string]$package.Risk -eq 'Driver'); InstallsService = ([string]$package.Risk -eq 'Service'); OpensListener = ([string]$package.Risk -eq 'Listener'); FirmwareUtility = $false
            StableSortKey = ([string]$package.Id).ToUpperInvariant()
            ApplicationSortKey = (([string]$package.Name).ToUpperInvariant() + '|' + ([string]$package.Id).ToUpperInvariant())
            VendorSortKey = (([string]$package.Vendor).ToUpperInvariant() + '|' + ([string]$package.Id).ToUpperInvariant())
            PrioritySortKey = [int]$priorityRank[[string]$package.Priority]
            StatusSortKey = [int]$statusRank[[string]$package.Status]
            VersionSortKey = ConvertTo-AVWorkstationToolkitVersionSortKey -Label ([string]$package.InstalledVersion)
            RiskSortKey = [int]$riskRank[[string]$package.Risk]
        }
    })
    $profiles = @($case.Profiles | ForEach-Object { [string]$_ })
    $visible = @($items | Where-Object {
        Test-AVWorkstationToolkitCatalogFilter -Item $_ -Profiles $profiles -Preset All -Vendor ([string]$case.Manufacturer) -Discipline ([string]$case.Discipline) -Search ([string]$case.Search)
    } | Where-Object { Test-AVWorkstationToolkitQuickViewMatch -Item $_ -QuickView ([string]$case.QuickView) })
    if ([string]$case.Priority -ne 'All') { $visible = @($visible | Where-Object Priority -eq ([string]$case.Priority)) }
    if ([string]$case.Role -ne 'All') { $visible = @($visible | Where-Object { [string]$case.Role -in @($_.Roles) }) }
    if ([string]$case.QuickView -ne 'All') {
        $requiredAction = if ([string]$case.QuickView -eq 'Missing') { 'Install' } else { 'Update' }
        foreach ($item in $visible) { if ($item.CanSelect -and $item.Action -eq $requiredAction) { $item.Selected = $true } }
    }
    foreach ($id in @($case.SelectedIds)) {
        $target = @($items | Where-Object Id -eq ([string]$id) | Select-Object -First 1)
        if ($target.Count -eq 1 -and $target[0].CanSelect) { $target[0].Selected = $true }
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$case.SortMember)) {
        $descending = [string]$case.SortDirection -eq 'Descending'
        $sortMember = [string]$case.SortMember
        $visible = @($visible | Sort-Object -Property @{ Expression={ $_.$sortMember }; Descending=$descending },@{ Expression={ $_.StableSortKey }; Descending=$false })
    }
    $install = @($items | Where-Object { $_.Selected -and $_.CanSelect -and $_.Action -eq 'Install' })
    $update = @($items | Where-Object { $_.Selected -and $_.CanSelect -and $_.Action -eq 'Update' })
    $installBlocked = [bool]$case.RebootPending -and @($install | Where-Object Risk -ne 'None').Count -gt 0
    $updateBlocked = [bool]$case.RebootPending -and @($update | Where-Object Risk -ne 'None').Count -gt 0
    $total = $install.Count + $update.Count
    [ordered]@{
        Id = [string]$case.Id
        VisibleIds = @($visible | ForEach-Object Id)
        SelectedIds = @($items | Where-Object Selected | ForEach-Object Id)
        InstallCount = $install.Count
        UpdateCount = $update.Count
        InstallEnabled = ($install.Count -gt 0 -and -not $installBlocked)
        UpdateEnabled = ($update.Count -gt 0 -and -not $updateBlocked)
        InstallLabel = if ($install.Count -eq 0) { 'Install selected' } else { "Install selected ($($install.Count))" }
        UpdateLabel = if ($update.Count -eq 0) { 'Update selected' } else { "Update selected ($($update.Count))" }
        SelectionSummary = if ($total -eq 0) { 'Nothing selected' } else { "$total selected | $($install.Count) install | $($update.Count) update" }
        QuickView = [string]$case.QuickView
        WarningVisible = [bool]$case.RebootPending
    }
}

[ordered]@{ SchemaVersion=1; ScenarioId=[string]$fixture.ScenarioId; Cases=@($results) } | ConvertTo-Json -Depth 12 -Compress
