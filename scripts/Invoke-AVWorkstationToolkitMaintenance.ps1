<#
.SYNOPSIS
    Plan or apply approved AV/IT team application updates with winget.

.DESCRIPTION
    Uses the same validated catalog, live plan, request schema, and isolated
    worker as the AV Workstation Toolkit desktop interface. Plan mode is the default. Runtime,
    OS, security, and management updates remain outside scope.
#>

[CmdletBinding()]
param(
    [ValidateSet('Standard','Field','Developer','Optional','All')]
    [Alias('Profile')][string[]]$ProfileName = @('Standard'),
    [switch]$Apply,
    [switch]$AllowServices,
    [switch]$AllowDrivers,
    [switch]$AllowListeners,
    [string]$DataRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$launchIsElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($launchIsElevated) { throw 'AV Workstation Toolkit scripts must be launched from a standard-user PowerShell session.' }

Import-Module (Join-Path $PSScriptRoot 'AVWorkstationToolkit.Core.psd1') -Force
$resolvedDataRoot = Get-AVWorkstationToolkitDataRoot -Path $DataRoot

$selectedProfiles = if ($ProfileName -contains 'All') {
    @('Standard','Field','Developer','Optional')
}
else {
    @($ProfileName | Sort-Object -Unique)
}

$plan = Get-AVWorkstationToolkitPlan
$items = @($plan.Packages | Where-Object { $_.Profile -in $selectedProfiles } | Sort-Object Order)
$results = [System.Collections.Generic.List[object]]::new()

foreach ($item in $items) {
    $status = if ($item.Status -eq 'Error') {
        'Error'
    }
    elseif (-not $item.Installed) {
        if ($item.Status -eq 'Manual') { 'Manual' } else { 'NotInstalled' }
    }
    elseif ($item.Status -eq 'Held') {
        'Held'
    }
    elseif ($item.Action -ne 'Update') {
        'Current'
    }
    else {
        $riskAllowed = switch ($item.Risk) {
            'Service'  { [bool]$AllowServices }
            'Driver'   { [bool]$AllowDrivers }
            'Listener' { [bool]$AllowListeners }
            default    { $true }
        }
        if ($riskAllowed) { 'Ready' } else { 'Blocked' }
    }

    $results.Add([pscustomobject]@{
        Profile = $item.Profile
        Name = $item.Name
        Id = $item.Id
        Risk = $item.Risk
        Status = $status
        InstalledVersion = $item.InstalledVersion
        AvailableVersion = $item.AvailableVersion
        Note = $item.Note
    }) | Out-Null
}

Write-Host ('Profiles: {0}' -f ($selectedProfiles -join ', ')) -ForegroundColor Cyan
Write-Host $(if ($Apply) { 'Mode: APPLY UPDATES' } else { 'Mode: PLAN ONLY' }) -ForegroundColor Cyan
$results | Format-Table Profile,Name,Id,Risk,Status,InstalledVersion,AvailableVersion,Note -AutoSize

$ready = @($results | Where-Object Status -eq 'Ready')
if (-not $Apply) {
    Write-Host ('Summary: current={0}; ready={1}; blocked={2}; held={3}; manual={4}; not-installed={5}; errors={6}' -f
        @($results | Where-Object Status -eq 'Current').Count,
        $ready.Count,
        @($results | Where-Object Status -eq 'Blocked').Count,
        @($results | Where-Object Status -eq 'Held').Count,
        @($results | Where-Object Status -eq 'Manual').Count,
        @($results | Where-Object Status -eq 'NotInstalled').Count,
        @($results | Where-Object Status -eq 'Error').Count) -ForegroundColor Cyan
    Write-Host 'Only allowlisted user applications are shown; runtime, OS, and security/management updates are intentionally ignored.' -ForegroundColor Cyan
    return
}

if ($plan.Elevated) {
    throw 'AV Workstation Toolkit changes must start from a standard-user PowerShell session. Individual installers can request elevation through Windows.'
}
if ($plan.Reboot.Pending) {
    throw "Maintenance refused: reboot pending ($($plan.Reboot.Summary))."
}
if ($plan.Summary.Errors -gt 0) {
    throw 'Maintenance refused because reliable winget inventory is unavailable.'
}
if ($ready.Count -eq 0) {
    Write-Host 'Nothing approved to update.' -ForegroundColor Green
    return
}

$riskAcknowledged = @($ready | Where-Object Risk -ne 'None').Count -gt 0
$request = New-AVWorkstationToolkitActionRequest -Action Update -PackageId @($ready.Id) -RiskAcknowledged $riskAcknowledged -RequestsRoot (Join-Path $resolvedDataRoot 'logs\requests')
$worker = Start-AVWorkstationToolkitWorker -RequestPath $request.RequestPath -DataRoot $resolvedDataRoot -Wait
if ($worker.ExitCode -ne 0) {
    throw "AV Workstation Toolkit maintenance worker failed with exit code $($worker.ExitCode). Request: $($request.RequestPath)"
}
