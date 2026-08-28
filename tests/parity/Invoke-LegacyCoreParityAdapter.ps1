<#[.SYNOPSIS] Evaluates deterministic domain-core fixtures with the shipping PowerShell implementation. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixturePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'core-fixtures')).TrimEnd('\') + '\'
$resolvedFixture = [IO.Path]::GetFullPath($FixturePath)
if (-not $resolvedFixture.StartsWith($fixtureRoot,[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $resolvedFixture -PathType Leaf)) {
    throw 'Core parity fixture must be an existing file under tests\parity\core-fixtures.'
}

function Assert-ExactProperties {
    param([Parameter(Mandatory)]$Value,[Parameter(Mandatory)][string[]]$Expected,[Parameter(Mandatory)][string]$Context)
    $actual = @($Value.PSObject.Properties.Name)
    $unknown = @($actual | Where-Object { $_ -notin $Expected })
    $missing = @($Expected | Where-Object { $_ -notin $actual })
    if ($unknown.Count -gt 0 -or $missing.Count -gt 0) { throw "$Context fields differ. Unknown=[$($unknown -join ',')] Missing=[$($missing -join ',')]." }
}

function ConvertTo-Psd1String {
    param([AllowNull()]$Value)
    if ($null -eq $Value) { return '$null' }
    return "'" + ([string]$Value).Replace("'","''") + "'"
}

$fixture = Get-Content -LiteralPath $resolvedFixture -Raw | ConvertFrom-Json -ErrorAction Stop
Assert-ExactProperties $fixture @('SchemaVersion','ScenarioId','AsOfDate','Versions','Packages','Filters','ExternalStates','Catalogs') 'Core fixture'
if ([int]$fixture.SchemaVersion -ne 2) { throw "Unsupported core parity schema: $($fixture.SchemaVersion)." }
Import-Module (Join-Path $repositoryRoot 'scripts\AVWorkstationToolkit.Core.psd1') -Force
$coreModule = Get-Module AVWorkstationToolkit.Core

$versionResults = @($fixture.Versions | ForEach-Object {
    Assert-ExactProperties $_ @('Id','Operation','Value','Other') 'Version case'
    $case = $_
    $outcome = 'Accepted'; $value = ''
    try {
        switch ([string]$case.Operation) {
            'Normalize' { $value = [string](& $coreModule { param($inputValue) ConvertTo-AVWorkstationToolkitVersion -Value $inputValue } ([string]$case.Value)) }
            'Compare' { $value = [string][Math]::Sign((Compare-AVWorkstationToolkitVersion -Left ([string]$case.Value) -Right ([string]$case.Other))) }
            'SortKey' { $value = ConvertTo-AVWorkstationToolkitVersionSortKey -Label ([string]$case.Value) }
            default { throw "Unknown version operation: $($case.Operation)." }
        }
    }
    catch { Write-Verbose "Version case $($case.Id) rejected: $($_.Exception.Message)"; $outcome = 'Rejected'; $value = '' }
    [ordered]@{ Id=[string]$case.Id; Outcome=$outcome; Value=$value }
})

$packages = @($fixture.Packages | ForEach-Object {
    Assert-ExactProperties $_ @('Id','Name','Vendor','Profile','Priority','ApplicationTypes','Roles','Status','Installed','AvailableVersion') 'Filter package'
    [pscustomobject]@{
        Id=[string]$_.Id; Name=[string]$_.Name; Vendor=[string]$_.Vendor; Profile=[string]$_.Profile; Priority=[string]$_.Priority
        ApplicationType=@($_.ApplicationTypes); Roles=@($_.Roles); Status=[string]$_.Status; Installed=[bool]$_.Installed
        AvailableVersion=[string]$_.AvailableVersion; ProductFamily=''; Note='Fixture'; CatalogNotes=''; SupportedOS=@('Windows'); CatalogTags=@()
        LicensingModel=@('FREE'); DownloadAccess=@('PUBLIC-DL'); DeploymentClass='Managed'; DistributionPolicy='PackageManagerOnly'
        WorkflowCategories=@(); InstallationForms=@('WinGet'); FirmwareUtility=$false
    }
})
$filterResults = @($fixture.Filters | ForEach-Object {
    Assert-ExactProperties $_ @('Id','Profiles','Priorities','Vendor','Discipline','Roles','Search','QuickView') 'Filter query'
    $query = $_
    $filteredPackages = @($packages | Where-Object {
        (Test-AVWorkstationToolkitCatalogFilter -Item $_ -Profiles @($query.Profiles) -Vendor ([string]$query.Vendor) -Discipline ([string]$query.Discipline) -Search ([string]$query.Search)) -and
        (Test-AVWorkstationToolkitQuickViewMatch -Item $_ -QuickView ([string]$query.QuickView))
    })
    if (@($query.Priorities).Count -gt 0) { $filteredPackages = @(Find-AVWorkstationToolkitCatalog -Catalog $filteredPackages -Priority @($query.Priorities)) }
    if (@($query.Roles).Count -gt 0) { $filteredPackages = @(Find-AVWorkstationToolkitCatalog -Catalog $filteredPackages -Role @($query.Roles)) }
    [ordered]@{ Id=[string]$query.Id; Matches=@($filteredPackages | ForEach-Object { [string]$_.Id }) }
})

$externalReasonCodes = @{
    Awareness='AwarenessOnly'; Inventory='InventoryDetected'; NotDetected='InventoryNotDetected'
    InventoryIncomplete='InventoryIncomplete'; InventoryUnavailable='InventoryUnavailable'; CheckUnavailable='ExternalReleaseVersionInvalid'
    Manual='ExternalManualInstall'; ManualUpdate='ExternalManualUpdate'; Current='ExternalCurrent'; Error='InstalledVersionInvalid'
}
$externalStateResults = @($fixture.ExternalStates | ForEach-Object {
    Assert-ExactProperties $_ @('Id','DetectionMode','DetectionVersionPolicy','ReleaseMode','ReleaseChannel','KnownVersion','DeliveryMode','InventoryPresent','Reliable','InventoryQuality','Installed','InstalledVersion','InstalledVersions','AvailableVersion','InventoryDetail') 'External state'
    $case = $_
    $package = [pscustomobject]@{
        Id=[string]$case.Id; KnownVersion=[string]$case.KnownVersion; DetectionMode=[string]$case.DetectionMode
        DetectionVersionPolicy=[string]$case.DetectionVersionPolicy; ReleaseMode=[string]$case.ReleaseMode
        ReleaseChannel=[string]$case.ReleaseChannel; DeliveryMode=[string]$case.DeliveryMode
    }
    $inventory = if ([bool]$case.InventoryPresent) { [pscustomobject]@{
        Reliable=[bool]$case.Reliable; InventoryQuality=[string]$case.InventoryQuality; Installed=[bool]$case.Installed
        InstalledVersion=[string]$case.InstalledVersion; InstalledVersions=@($case.InstalledVersions); Detail=[string]$case.InventoryDetail
    }} else { $null }
    $release = [pscustomobject]@{ AvailableVersion=[string]$case.AvailableVersion; Detail='' }
    $state = & $coreModule { param($p,$i,$r) Get-AVWorkstationToolkitExternalPackageState -Package $p -InventoryRecord $i -ReleaseRecord $r } $package $inventory $release
    [ordered]@{
        Id=[string]$case.Id; Installed=[bool]$state.Installed; InstalledVersion=[string]$state.InstalledVersion
        AvailableVersion=[string]$state.AvailableVersion; Status=[string]$state.Status; StatusDetail=[string]$state.StatusDetail
        ReasonCode=[string]$externalReasonCodes[[string]$state.Status]; Action=[string]$state.Action
        InventoryQuality=[string]$state.InventoryQuality; WorkerEligible=$false
    }
})

$catalogResults = @($fixture.Catalogs | ForEach-Object {
    Assert-ExactProperties $_ @('Id','Source','ForbiddenPattern','Applications','Document') 'Catalog case'
    $case = $_; $outcome = 'Accepted'; $items = @()
    try {
        if ([string]$case.Source -eq 'External') {
            $json = $case.Document | ConvertTo-Json -Depth 20 -Compress
            $tempRoot = Join-Path $repositoryRoot 'artifacts\parity'
            [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
            $catalogPath = Join-Path $tempRoot (([string]$case.Id) + '-base.psd1')
            $externalPath = Join-Path $tempRoot (([string]$case.Id) + '.json')
            try {
                Set-Content -LiteralPath $catalogPath -Value "@{ForbiddenPattern='(?i)antivirus|vpn';Packages=@()}" -Encoding UTF8
                Set-Content -LiteralPath $externalPath -Value $json -Encoding UTF8
                $items = @(Get-AVWorkstationToolkitCatalog -CatalogPath $catalogPath -ExternalCatalogPath $externalPath -AwarenessCatalogPath '')
            }
            finally {
                Remove-Item -LiteralPath $catalogPath -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $externalPath -Force -ErrorAction SilentlyContinue
            }
        }
        elseif ([string]$case.Source -eq 'Managed') {
            $tempRoot = Join-Path $repositoryRoot 'artifacts\parity'
            [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
            $catalogPath = Join-Path $tempRoot (([string]$case.Id) + '.psd1')
            $entries = @($case.Applications | ForEach-Object {
                $fields = @(
                    "Profile=$(ConvertTo-Psd1String $_.Profile)","Name=$(ConvertTo-Psd1String $_.Name)","Id=$(ConvertTo-Psd1String $_.Id)",
                    "Vendor=$(ConvertTo-Psd1String $_.Vendor)","Risk=$(ConvertTo-Psd1String $_.Risk)","Note=$(ConvertTo-Psd1String $_.Note)",
                    "Deployment=$(ConvertTo-Psd1String $_.Deployment)","Maintenance=$(ConvertTo-Psd1String $_.Maintenance)"
                )
                '@{' + ($fields -join ';') + '}'
            })
            $content = '@{ForbiddenPattern=' + (ConvertTo-Psd1String $case.ForbiddenPattern) + ';Packages=@(' + ($entries -join ',') + ')}'
            try {
                Set-Content -LiteralPath $catalogPath -Value $content -Encoding UTF8
                $items = @(Get-AVWorkstationToolkitCatalog -CatalogPath $catalogPath -ExternalCatalogPath '' -AwarenessCatalogPath '')
            }
            finally { Remove-Item -LiteralPath $catalogPath -Force -ErrorAction SilentlyContinue }
        }
        else { throw "Unknown catalog source: $($case.Source)." }
    }
    catch { Write-Verbose "Catalog case $($case.Id) rejected: $($_.Exception.Message)"; $outcome = 'Rejected'; $items = @() }
    $authorities = @($items | ForEach-Object {
        if ([string]$_.Provider -eq 'WinGet') { 'ManagedWinGet' }
        elseif ([string]$_.DeliveryMode -eq 'Awareness' -or [string]$_.DeploymentClass -in @('AwarenessOnly','WebOnly','ServerOnly','Embedded')) { 'AwarenessOnly' }
        else { 'OperationalExternal' }
    } | Sort-Object)
    [ordered]@{ Id=[string]$case.Id; Outcome=$outcome; PackageCount=@($items).Count; Authorities=$authorities }
})

[ordered]@{ SchemaVersion=2; ScenarioId=[string]$fixture.ScenarioId; Versions=$versionResults; Filters=$filterResults; ExternalStates=$externalStateResults; Catalogs=$catalogResults } |
    ConvertTo-Json -Depth 12 -Compress
