<#[.SYNOPSIS] Characterizes shipping read-only detail and diagnostics semantics. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixturePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Import-Module (Join-Path $repositoryRoot 'scripts\AVWorkstationToolkit.Core.psd1') -Force
$fixture = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json -ErrorAction Stop
if ([int]$fixture.SchemaVersion -ne 1) { throw "Unsupported read-only surface parity schema: $($fixture.SchemaVersion)." }
$catalog = @(Get-AVWorkstationToolkitCatalog)
$packages = foreach ($inputPackage in @($fixture.Packages)) {
    $package = @($catalog | Where-Object Id -eq ([string]$inputPackage.Id) | Select-Object -First 1)
    if ($package.Count -ne 1) { throw "Fixture package is not in the shipping catalog: $($inputPackage.Id)" }
    [pscustomobject]@{
        Id=[string]$package[0].Id; Provider=[string]$package[0].Provider; DeploymentClass=[string]$package[0].DeploymentClass
        Status=[string]$inputPackage.Status; Installed=[bool]$inputPackage.Installed; InstalledVersion=[string]$inputPackage.InstalledVersion
        AvailableVersion=[string]$inputPackage.AvailableVersion; InventoryQuality=[string]$inputPackage.InventoryQuality
        Name=[string]$package[0].Name; Vendor=[string]$package[0].Vendor; ProductFamily=[string]$package[0].ProductFamily
        Purpose=[string]$package[0].Note; Priority=[string]$package[0].Priority; Lifecycle=[string]$package[0].CurrentOrLegacy
        ParentProviderId=[string]$package[0].ParentProviderId; OfficialProductUri=[string]$package[0].OfficialProductUri
        OfficialDownloadUri=[string]$package[0].OfficialDownloadUri; VerificationState=[string]$package[0].MetadataVerificationState
        Quarantined=[bool]$package[0].MetadataQuarantined; SideBySide=[string]$package[0].SideBySideSupported
    }
}
$summary = [pscustomobject]@{
    Awareness=@($packages | Where-Object Status -eq 'Awareness').Count
    Current=@($packages | Where-Object Status -eq 'Current').Count
    Missing=@($packages | Where-Object Status -eq 'Missing').Count
    Updates=@($packages | Where-Object Status -eq 'UpdateAvailable').Count
    Manual=@($packages | Where-Object Status -eq 'Manual').Count
    ManualUpdates=@($packages | Where-Object Status -eq 'ManualUpdate').Count
    Held=@($packages | Where-Object Status -eq 'Held').Count
    InventoryIncomplete=@($packages | Where-Object Status -eq 'InventoryIncomplete').Count
    InventoryUnavailable=@($packages | Where-Object Status -eq 'InventoryUnavailable').Count
    CheckUnavailable=@($packages | Where-Object Status -eq 'CheckUnavailable').Count
    Errors=@($packages | Where-Object Status -eq 'Error').Count
}
$sources = @($fixture.RegistrySources | ForEach-Object { [pscustomobject]@{ Name=[string]$_.Name; Label=[string]$_.Name; Available=[bool]$_.Available; EntryCount=[int]$_.EntryCount; Detail='' } })
$reasonLabels = @($fixture.RebootReasons | ForEach-Object { if ([string]$_ -eq 'WindowsUpdate') { 'Windows Update' } elseif ([string]$_ -eq 'ComponentBasedServicing') { 'Component Based Servicing' } else { [string]$_ } })
$plan = [pscustomobject]@{
    Packages=$packages; Summary=$summary; WingetVersion='fixture'; WingetAvailable=$true; InventoryMode='Structured'
    Elevated=$false
    Reboot=[pscustomobject]@{ Pending=($reasonLabels.Count -gt 0); Reasons=$reasonLabels; Summary=($reasonLabels -join ', ') }
    ExternalInventory=[pscustomobject]@{ Quality='Partial'; Sources=$sources; WarningCount=1; ErrorCount=0; Detail='One source unavailable.' }
}
$diagnostics = Get-AVWorkstationToolkitDiagnostics -Plan $plan -DataRoot 'C:\fixture\data' -LogsPath 'C:\fixture\logs' -ExecutionMode Source -SelectedSdkVersion '10.0.fixture'
[ordered]@{
    SchemaVersion=1; ScenarioId=[string]$fixture.ScenarioId
    Details=@($packages | ForEach-Object { [ordered]@{
        Id=$_.Id; Name=$_.Name; Vendor=$_.Vendor; ProductFamily=$_.ProductFamily; Purpose=$_.Purpose; Priority=$_.Priority
        Lifecycle=$_.Lifecycle; ParentProviderId=$_.ParentProviderId; OfficialProductUri=$_.OfficialProductUri
        OfficialDownloadUri=$_.OfficialDownloadUri; VerificationState=$_.VerificationState; Quarantined=$_.Quarantined; SideBySide=$_.SideBySide
        Status=$_.Status; InventoryQuality=$_.InventoryQuality
    } })
    Diagnostics=[ordered]@{
        Total=[int]$diagnostics.Catalog.TotalRecords; OperationalExternal=[int]$diagnostics.Catalog.OperationalExternalRecords
        Awareness=[int]$diagnostics.Catalog.AwarenessRecords; InventoryWarnings=[int]$diagnostics.Catalog.InventoryWarnings
        RebootPending=[bool]$diagnostics.Reboot.Pending; WindowsUpdate=[bool]$diagnostics.Reboot.WindowsUpdate
        ComponentBasedServicing=[bool]$diagnostics.Reboot.ComponentBasedServicing
        RegistrySources=@($diagnostics.ExternalInventory.Sources | ForEach-Object { [ordered]@{ Name=$_.Name; Status=$_.Status; EntryCount=[int]$_.EntryCount } })
    }
} | ConvertTo-Json -Depth 12 -Compress
