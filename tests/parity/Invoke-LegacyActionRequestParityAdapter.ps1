<#[.SYNOPSIS] Evaluates deterministic action-request fixtures through the shipping PowerShell boundary. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixturePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$resolvedFixture = [IO.Path]::GetFullPath($FixturePath)
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'action-request-fixtures')).TrimEnd('\') + '\'
if (-not $resolvedFixture.StartsWith($fixtureRoot,[StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $resolvedFixture -PathType Leaf)) {
    throw 'Action-request parity fixture must be an existing file under tests\parity\action-request-fixtures.'
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

function New-BasePayload {
    param($Case)
    [ordered]@{
        SchemaVersion = 1
        RequestId = 'request-20260829-142233-0123abcd'
        Action = [string]$Case.Action
        PackageIds = @($Case.PackageIds)
        RiskAcknowledged = [bool]$Case.RiskAcknowledged
        DryRun = [bool]$Case.DryRun
    } | ConvertTo-Json -Depth 5
}

function Get-MutatedPayload {
    param($Case)
    $json = New-BasePayload $Case
    switch ([string]$Case.Mutation) {
        None { return $json }
        MalformedJson { return '{' }
        MissingDryRun { return ($json -replace ',\s*"DryRun"\s*:\s*false\s*','') }
        ExtraField { return ($json -replace '\s*}$', ",`r`n    `"Extra`": true`r`n}") }
        UnsupportedSchema { return ($json -replace '"SchemaVersion"\s*:\s*1','"SchemaVersion": 2') }
        WrongRiskType { return ($json -replace '"RiskAcknowledged"\s*:\s*false','"RiskAcknowledged": "true"') }
        WrongPackageType { return ($json -replace '"7zip\.7zip"','123') }
        NullPackageId { return ($json -replace '"7zip\.7zip"','null') }
        MalformedRequestId { return ($json -replace 'request-20260829-142233-0123abcd','request-invalid') }
        UnsupportedAction { return ($json -replace '"Action"\s*:\s*"Install"','"Action": "Uninstall"') }
        MalformedPackageId { return ($json -replace '"7zip\.7zip"','"bad id"') }
        EmptyIds { return ($json -replace '"PackageIds"\s*:\s*\[[\s\S]*?\]','"PackageIds": []') }
        Oversized { return (' ' * 65537) }
        default { throw "Unknown action-request mutation: $($Case.Mutation)." }
    }
}

function ConvertFrom-ShippingActionRequest {
    param([Parameter(Mandatory)][string]$Json)
    if ([Text.Encoding]::UTF8.GetByteCount($Json) -gt 65536) { throw 'The action request exceeds the maximum permitted size.' }
    $request = $Json | ConvertFrom-Json -ErrorAction Stop
    Assert-ExactProperties $request @('SchemaVersion','RequestId','Action','PackageIds','RiskAcknowledged','DryRun') 'Action request'
    if ($request.SchemaVersion -isnot [int] -or [int]$request.SchemaVersion -ne 1) { throw 'Invalid action request schema.' }
    if ($request.RequestId -isnot [string] -or [string]$request.RequestId -cnotmatch '^request-\d{8}-\d{6}-[a-f0-9]{8}$') { throw 'Invalid action request ID.' }
    if ($request.Action -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$request.Action) -or [string]$request.Action -notin @('Install','Update')) { throw 'Invalid action request operation.' }
    if ($request.RiskAcknowledged -isnot [bool]) { throw 'RiskAcknowledged must be Boolean.' }
    if ($request.DryRun -isnot [bool]) { throw 'DryRun must be Boolean.' }
    $ids = @($request.PackageIds)
    if ($ids.Count -lt 1 -or $ids.Count -gt 100) { throw 'Invalid package count.' }
    foreach ($id in $ids) {
        if ($id -isnot [string] -or [string]$id -cnotmatch '^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$') {
            # The worker parser accepts this syntax initially, but the complete shipping
            # boundary rejects it when it cannot resolve to a validated catalog package.
            throw 'Invalid package ID.'
        }
    }
    return [pscustomobject]@{
        Action = [string]$request.Action
        PackageIds = @($ids | ForEach-Object { [string]$_ })
        RiskAcknowledged = [bool]$request.RiskAcknowledged
        DryRun = [bool]$request.DryRun
    }
}

function New-Canonical {
    param([string]$CaseId,[bool]$Accepted,$Request,[string[]]$AuthorizedIds=@())
    $packageIds = [Collections.Generic.List[object]]::new()
    if ($null -ne $Request) { foreach ($id in @($Request.PackageIds)) { $packageIds.Add([string]$id) } }
    $authorizedPackageIds = [Collections.Generic.List[object]]::new()
    foreach ($id in @($AuthorizedIds)) { $authorizedPackageIds.Add([string]$id) }
    [ordered]@{
        CaseId = $CaseId
        Accepted = $Accepted
        Action = $(if ($null -ne $Request) { [string]$Request.Action } else { '' })
        PackageIds = $packageIds
        RiskAcknowledged = $(if ($null -ne $Request) { [bool]$Request.RiskAcknowledged } else { $false })
        DryRun = $(if ($null -ne $Request) { [bool]$Request.DryRun } else { $false })
        AuthorizedIds = $authorizedPackageIds
    }
}

$fixture = Get-Content -LiteralPath $resolvedFixture -Raw | ConvertFrom-Json -ErrorAction Stop
Assert-ExactProperties $fixture @('SchemaVersion','ScenarioId','Cases') 'Fixture'
if ($fixture.SchemaVersion -isnot [int] -or [int]$fixture.SchemaVersion -ne 1) { throw 'Unsupported action-request parity schema.' }
foreach ($item in @($fixture.Cases)) {
    Assert-ExactProperties $item @('CaseId','Kind','Action','PackageIds','RiskAcknowledged','DryRun','Mutation','PlanPackageId','PlanAuthority','PlanProvider','PlanStatus','PlanAction','PlanRisk','RebootPending') "Case $($item.CaseId)"
}

Import-Module (Join-Path $repositoryRoot 'scripts\AVWorkstationToolkit.Core.psd1') -Force
$results = foreach ($item in @($fixture.Cases)) {
    try {
        $request = $null
        if ([string]$item.Kind -ceq 'Creation') {
            $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('awt-request-parity-' + [guid]::NewGuid().ToString('N'))
            try {
                $files = New-AVWorkstationToolkitActionRequest -Action ([string]$item.Action) -PackageId @($item.PackageIds) `
                    -RiskAcknowledged:([bool]$item.RiskAcknowledged) -DryRun:([bool]$item.DryRun) -RequestsRoot $temporaryRoot
                $request = ConvertFrom-ShippingActionRequest (Get-Content -LiteralPath $files.RequestPath -Raw)
            }
            finally {
                if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
            }
        }
        else {
            $request = ConvertFrom-ShippingActionRequest (Get-MutatedPayload $item)
        }

        $authorized = @()
        if ([string]$item.Kind -ceq 'Authorization') {
            $plan = [pscustomobject]@{
                Packages = @([pscustomobject]@{
                    Id = [string]$item.PlanPackageId
                    Action = [string]$item.PlanAction
                    Risk = [string]$item.PlanRisk
                })
                Reboot = [pscustomobject]@{ Pending=[bool]$item.RebootPending; Reasons=@(); Summary='' }
            }
            $authorized = @(Assert-AVWorkstationToolkitRequest -Action ([string]$request.Action) -PackageId @($request.PackageIds) `
                -Plan $plan -RiskAcknowledged:([bool]$request.RiskAcknowledged) | ForEach-Object { [string]$_.Id })
        }
        New-Canonical ([string]$item.CaseId) $true $request $authorized
    }
    catch {
        New-Canonical ([string]$item.CaseId) $false $null @()
    }
}

[ordered]@{
    SchemaVersion = 1
    ScenarioId = [string]$fixture.ScenarioId
    Cases = @($results)
} | ConvertTo-Json -Depth 10 -Compress
