<#[.SYNOPSIS] Evaluates deterministic read-only provider fixtures through the shipping PowerShell semantics. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixturePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Import-Module (Join-Path $repositoryRoot 'scripts\AVWorkstationToolkit.Core.psd1') -Force
$fixture = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json -ErrorAction Stop
if ([int]$fixture.SchemaVersion -ne 1) { throw "Unsupported provider parity schema: $($fixture.SchemaVersion)." }

function Get-SourceToken([string]$Name) {
    if ($Name -notin @('HKLM64','HKLM32','HKCU')) { throw "Unsupported registry source: $Name." }
    return $Name
}

$installedResults = foreach ($item in @($fixture.InstalledInventory)) {
    $quality = 'Complete'; $failure = 'None'; $records = @()
    if (-not [bool]$item.Available) { $quality='Unavailable'; $failure='ProviderUnavailable' }
    elseif ([int]$item.ExitCode -ne 0) { $quality='Unavailable'; $failure='ExecutionFailed' }
    else {
        try {
            $packages = @(ConvertFrom-AVWorkstationToolkitWingetExportJson -Json ([string]$item.ExportJson))
            $records = @($packages | Sort-Object Id | ForEach-Object { '{0}|{1}' -f $_.Id,$_.InstalledVersion })
            $coverage = Get-AVWorkstationToolkitWingetStructuredInventoryQuality -InstalledPackages $packages -CatalogPackages @($item.Catalog) -DiagnosticText ([string]$item.Output)
            $quality = [string]$coverage.Quality
            $failure = [string]$coverage.Failure
        }
        catch { $quality='Malformed'; $failure='MalformedOutput'; $records=@() }
    }
    [ordered]@{ Id=[string]$item.Id; Quality=$quality; Failure=$failure; Records=@($records) }
}

$updateResults = foreach ($item in @($fixture.Updates)) {
    $quality = 'Complete'; $failure = 'None'; $records = @()
    if (-not [bool]$item.Available) { $quality='Unavailable'; $failure='ProviderUnavailable' }
    elseif ([int]$item.ExitCode -ne 0) { $quality='Unavailable'; $failure='ExecutionFailed' }
    else {
        try {
            $updates = @(ConvertFrom-AVWorkstationToolkitWingetUpgradeText -Text ([string]$item.Output))
            $records = @($updates | Sort-Object Id | ForEach-Object { '{0}|{1}|{2}' -f $_.Id,$_.InstalledVersion,$_.AvailableVersion })
        }
        catch { $quality='Malformed'; $failure='MalformedOutput'; $records=@() }
    }
    [ordered]@{ Id=[string]$item.Id; Quality=$quality; Failure=$failure; Records=@($records) }
}

$registryResults = foreach ($item in @($fixture.Registry)) {
    $sources = @($item.Sources | ForEach-Object {
        [pscustomobject]@{ Name=[string]$_.Name; Available=[bool]$_.Available; Detail=[string]$_.Detail; Entries=@($_.Entries) }
    })
    $available = @($sources | Where-Object Available).Count
    $quality = if ($available -eq $sources.Count) { 'Complete' } elseif ($available -eq 0) { 'Unavailable' } else { 'Partial' }
    $failure = if ($quality -eq 'Complete') { 'None' } elseif ($quality -eq 'Partial') { 'PartialInventory' } else { 'ProviderUnavailable' }
    $packages = @($item.Packages | ForEach-Object {
        [pscustomobject]@{
            Id=[string]$_.Id; Provider='External'; DetectionMode=[string]$_.DetectionMode
            DetectionDisplayNamePattern=[string]$_.DisplayPattern; DetectionVersionPattern=[string]$_.VersionPattern
        }
    })
    $inventory = @(Get-AVWorkstationToolkitExternalInventory -Package $packages -RegistrySourceResult $sources)
    $sourceRecords = @($sources | ForEach-Object {
        $source = $_
        if ($source.Available) {
            @($source.Entries | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.DisplayName) } | ForEach-Object {
                '{0}|{1}|{2}' -f (Get-SourceToken $source.Name),([string]$_.DisplayName),([string]$_.DisplayVersion)
            })
        }
    })
    $statuses = @($sources | ForEach-Object {
        $entryCount = if ($_.Available) { @($_.Entries | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.DisplayName) }).Count } else { 0 }
        '{0}|{1}|{2}' -f (Get-SourceToken $_.Name),([bool]$_.Available),$entryCount
    })
    $packageRecords = @($inventory | ForEach-Object {
        '{0}|{1}|{2}|{3}|{4}|{5}' -f $_.Id,([bool]$_.Reliable),([bool]$_.Installed),$_.InstalledVersion,$_.InventoryQuality,(@($_.InstalledVersions) -join '/')
    })
    [ordered]@{ Id=[string]$item.Id; Quality=$quality; Failure=$failure; Sources=@($statuses); Records=@($sourceRecords); Packages=@($packageRecords) }
}

$rebootResults = foreach ($item in @($fixture.Reboot)) {
    $reasons = @()
    if ([bool]$item.WindowsUpdate) { $reasons += 'WindowsUpdate' }
    if ([bool]$item.ComponentBasedServicing) { $reasons += 'ComponentBasedServicing' }
    [ordered]@{ Id=[string]$item.Id; Pending=($reasons.Count -gt 0); Reasons=@($reasons) }
}

$trustResults = foreach ($item in @($fixture.Trust)) {
    $packagePublisher = @(([string]$item.PackagePublisher -split ',') | ForEach-Object Trim)
    $signer = @(([string]$item.SignerSubject -split ',') | ForEach-Object Trim)
    $trusted = [string]$item.PackageName -eq 'Microsoft.DesktopAppInstaller' -and
        @($packagePublisher | Where-Object { $_ -ieq 'O=Microsoft Corporation' }).Count -gt 0 -and
        [bool]$item.FileExists -and [bool]$item.RegularFile -and [bool]$item.PathContained -and [bool]$item.ReparseFree -and
        [bool]$item.SignatureValid -and @($signer | Where-Object { $_ -ieq 'O=Microsoft Corporation' }).Count -gt 0 -and
        [IO.Path]::GetFileName([string]$item.ExecutablePath) -ieq 'winget.exe'
    [ordered]@{ Id=[string]$item.Id; Trusted=$trusted; Failure=$(if ($trusted) { 'None' } else { 'TrustFailure' }) }
}

[ordered]@{
    SchemaVersion=1; ScenarioId=[string]$fixture.ScenarioId
    InstalledInventory=@($installedResults); Updates=@($updateResults); Registry=@($registryResults)
    Reboot=@($rebootResults); Trust=@($trustResults)
} | ConvertTo-Json -Depth 20 -Compress
