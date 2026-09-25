<#
.SYNOPSIS
    Plan or install approved AV/IT team user applications with winget.

.DESCRIPTION
    Uses the same validated catalog, live plan, request schema, and isolated
    worker as the AV Workstation Toolkit desktop interface. Plan mode is the default. No
    arbitrary package ID or winget argument is accepted.
#>

[CmdletBinding()]
param(
    [ValidateSet('Standard','Field','Developer','Optional','All')]
    [Alias('Profile')][string[]]$ProfileName = @('Standard'),
    [switch]$Install,
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
    elseif ($item.Installed) {
        'Present'
    }
    elseif ($item.Status -eq 'Manual') {
        'Manual'
    }
    elseif ($item.Action -ne 'Install') {
        'Blocked'
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
        Note = $item.Note
    }) | Out-Null
}

Write-Host ('Profiles: {0}' -f ($selectedProfiles -join ', ')) -ForegroundColor Cyan
Write-Host $(if ($Install) { 'Mode: INSTALL' } else { 'Mode: PLAN ONLY' }) -ForegroundColor Cyan
$results | Format-Table Profile,Name,Id,Risk,Status,Note -AutoSize

$ready = @($results | Where-Object Status -eq 'Ready')
if (-not $Install) {
    Write-Host ('Summary: present={0}; ready={1}; blocked={2}; manual={3}; errors={4}' -f
        @($results | Where-Object Status -eq 'Present').Count,
        $ready.Count,
        @($results | Where-Object Status -eq 'Blocked').Count,
        @($results | Where-Object Status -eq 'Manual').Count,
        @($results | Where-Object Status -eq 'Error').Count) -ForegroundColor Cyan
    return
}

if ($plan.Elevated) {
    throw 'AV Workstation Toolkit changes must start from a standard-user PowerShell session. Individual installers can request elevation through Windows.'
}
if ($plan.Reboot.Pending) {
    throw "Installation refused: reboot pending ($($plan.Reboot.Summary))."
}
if ($plan.Summary.Errors -gt 0) {
    throw 'Installation refused because reliable winget inventory is unavailable.'
}
if ($ready.Count -eq 0) {
    Write-Host 'Nothing approved to install.' -ForegroundColor Green
    return
}

$riskAcknowledged = @($ready | Where-Object Risk -ne 'None').Count -gt 0
$request = New-AVWorkstationToolkitActionRequest -Action Install -PackageId @($ready.Id) -RiskAcknowledged $riskAcknowledged -RequestsRoot (Join-Path $resolvedDataRoot 'logs\requests')
$worker = Start-AVWorkstationToolkitWorker -RequestPath $request.RequestPath -DataRoot $resolvedDataRoot -Wait
if ($worker.ExitCode -ne 0) {
    throw "AV Workstation Toolkit install worker failed with exit code $($worker.ExitCode). Request: $($request.RequestPath)"
}
