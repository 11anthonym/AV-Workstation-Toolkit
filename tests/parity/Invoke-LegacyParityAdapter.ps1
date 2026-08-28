<#[.SYNOPSIS] Evaluates one deterministic parity fixture with the shipping PowerShell core. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixturePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$resolvedFixture = [IO.Path]::GetFullPath($FixturePath)
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'fixtures')).TrimEnd('\') + '\'
if (-not $resolvedFixture.StartsWith($fixtureRoot,[StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $resolvedFixture -PathType Leaf)) {
    throw 'Parity fixture must be an existing file under tests\parity\fixtures.'
}

function Assert-ExactProperties {
    param([Parameter(Mandatory)]$Value,[Parameter(Mandatory)][string[]]$Expected,[Parameter(Mandatory)][string]$Context)
    $actual = @($Value.PSObject.Properties.Name)
    $unknown = @($actual | Where-Object { $_ -notin $Expected })
    $missing = @($Expected | Where-Object { $_ -notin $actual })
    if ($unknown.Count -gt 0 -or $missing.Count -gt 0) {
        throw "$Context fields differ. Unknown=[$($unknown -join ',')] Missing=[$($missing -join ',')]."
    }
}

$fixture = Get-Content -LiteralPath $resolvedFixture -Raw | ConvertFrom-Json -ErrorAction Stop
Assert-ExactProperties $fixture @('SchemaVersion','ScenarioId','Reboot','Winget','Packages') 'Fixture'
Assert-ExactProperties $fixture.Reboot @('Pending','Reasons','Summary') 'Reboot'
Assert-ExactProperties $fixture.Winget @('Available','Installed','Upgrades') 'Winget'
foreach ($record in @($fixture.Winget.Installed)) { Assert-ExactProperties $record @('Id','Version') 'Installed package' }
foreach ($record in @($fixture.Winget.Upgrades)) { Assert-ExactProperties $record @('Id','InstalledVersion','AvailableVersion') 'Upgrade package' }
foreach ($record in @($fixture.Packages)) { Assert-ExactProperties $record @('Id','Provider','Deployment','Maintenance','Risk') 'Package' }
if ([int]$fixture.SchemaVersion -ne 1) { throw "Unsupported parity fixture schema: $($fixture.SchemaVersion)." }

Import-Module (Join-Path $repositoryRoot 'scripts\AVWorkstationToolkit.Core.psd1') -Force
$catalogPath = Join-Path $repositoryRoot 'scripts\AppProfiles.psd1'
$catalog = @(Get-AVWorkstationToolkitCatalog -CatalogPath $catalogPath -ExternalCatalogPath '' -AwarenessCatalogPath '')
$catalogById = @{}
foreach ($item in $catalog) { $catalogById[[string]$item.Id] = $item }
foreach ($expected in @($fixture.Packages)) {
    if (-not $catalogById.ContainsKey([string]$expected.Id)) { throw "Fixture package is not in the shipping catalog: $($expected.Id)." }
    $actual = $catalogById[[string]$expected.Id]
    foreach ($property in @('Provider','Deployment','Maintenance','Risk')) {
        if ([string]$actual.$property -cne [string]$expected.$property) {
            throw "Fixture policy drift for $($expected.Id).$property. Expected [$($expected.$property)] actual [$($actual.$property)]."
        }
    }
}

$installedPackages = @($fixture.Winget.Installed | ForEach-Object {
    [pscustomobject]@{ Id=[string]$_.Id; InstalledVersion=[string]$_.Version }
})
$upgradeLines = @($fixture.Winget.Upgrades | ForEach-Object {
    '{0,-20}{1,-35}{2,-15}{3,-15}{4}' -f 'Fixture',$_.Id,$_.InstalledVersion,$_.AvailableVersion,'winget'
})
$upgradeText = if ($upgradeLines.Count -gt 0) {
    @(
        ('{0,-20}{1,-35}{2,-15}{3,-15}{4}' -f 'Name','Id','Version','Available','Source'),
        ('-' * 95)
    ) + $upgradeLines -join "`r`n"
}
else { 'No installed package found matching input criteria.' }
$reboot = [pscustomobject]@{
    Pending = [bool]$fixture.Reboot.Pending
    Reasons = @($fixture.Reboot.Reasons)
    Summary = [string]$fixture.Reboot.Summary
}
$plan = Get-AVWorkstationToolkitPlan -CatalogPath $catalogPath -ExternalCatalogPath '' -AwarenessCatalogPath '' `
    -InstalledPackages $installedPackages -UpgradeText $upgradeText -RebootState $reboot `
    -WingetVersion $(if ([bool]$fixture.Winget.Available) { 'v-fixture' } else { 'Unavailable' })

$reasonCodes = @{
    Current='InstalledCurrent'; Missing='AllowlistedInstallAvailable'; UpdateAvailable='AllowlistedUpdateAvailable'
    Held='MaintenanceHold'; Manual='DeploymentManualHold'; Error='WingetInventoryUnavailable'
}
$canonicalPackages = @($fixture.Packages | ForEach-Object {
    $item = @($plan.Packages | Where-Object Id -eq $_.Id)
    if ($item.Count -ne 1) { throw "Legacy plan did not contain exactly one package: $($_.Id)." }
    $item = $item[0]
    $workerEligible = $false
    if ($item.Action -in @('Install','Update')) {
        try {
            [void](Assert-AVWorkstationToolkitRequest -Action $item.Action -PackageId @($item.Id) -Plan $plan -RiskAcknowledged)
            $workerEligible = $true
        }
        catch {
            if (-not ($plan.Reboot.Pending -and $item.Risk -ne 'None')) { throw }
        }
    }
    [ordered]@{
        Id=[string]$item.Id
        Provider=[string]$item.Provider
        Installed=[bool]$item.Installed
        InstalledVersion=[string]$item.InstalledVersion
        AvailableVersion=[string]$item.AvailableVersion
        Status=[string]$item.Status
        StatusDetail=[string]$item.StatusDetail
        ReasonCode=[string]$reasonCodes[[string]$item.Status]
        Risk=[string]$item.Risk
        CanSelect=[bool]$item.CanSelect
        Action=[string]$item.Action
        DeliveryMode='None'
        InventoryQuality=$(if ([bool]$fixture.Winget.Available) { 'Complete' } else { 'Unavailable' })
        WorkerEligible=$workerEligible
    }
})

[ordered]@{
    SchemaVersion=1
    ScenarioId=[string]$fixture.ScenarioId
    Packages=$canonicalPackages
} | ConvertTo-Json -Depth 8 -Compress
