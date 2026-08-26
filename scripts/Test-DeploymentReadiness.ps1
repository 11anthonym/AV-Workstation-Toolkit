<#
.SYNOPSIS
    Performs read-only readiness checks before a workstation deployment wave.

.DESCRIPTION
    Reports reboot state, winget version/upgrades, and selected application
    configuration paths. It does not
    install, upgrade, remove, enable, disable, start, stop, or reboot anything.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$launchIsElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($launchIsElevated) { throw 'AV Workstation Toolkit scripts must be launched from a standard-user PowerShell session.' }

Import-Module (Join-Path $PSScriptRoot 'AVWorkstationToolkit.Core.psd1') -Force

$rebootState = Get-AVWorkstationToolkitRebootState
$versionResult = Invoke-AVWorkstationToolkitWingetCapture -Arguments @('--version')
$wingetVersion = if ($versionResult.ExitCode -eq 0) { $versionResult.Output.Trim() } else { 'Unavailable' }
$upgradeResult = Invoke-AVWorkstationToolkitWingetCapture -Arguments @('upgrade','--source','winget','--disable-interactivity','--accept-source-agreements')
$upgradeOutput = if ($upgradeResult.ExitCode -eq 0) { $upgradeResult.Output } else { 'winget unavailable or trust validation failed: ' + $upgradeResult.Output }

$configurationPaths = @(
    [pscustomobject]@{ Area='KeePass config'; Path=(Join-Path $env:APPDATA 'KeePass') },
    [pscustomobject]@{ Area='Q-SYS documents'; Path=(Join-Path $env:USERPROFILE 'Documents\QSC') },
    [pscustomobject]@{ Area='SSH'; Path=(Join-Path $env:USERPROFILE '.ssh') },
    [pscustomobject]@{ Area='mRemoteNG'; Path=(Join-Path $env:APPDATA 'mRemoteNG') },
    [pscustomobject]@{ Area='Wireshark'; Path=(Join-Path $env:APPDATA 'Wireshark') },
    [pscustomobject]@{ Area='FileZilla'; Path=(Join-Path $env:APPDATA 'FileZilla') },
    [pscustomobject]@{ Area='Crestron local'; Path=(Join-Path $env:LOCALAPPDATA 'Crestron') }
) | Select-Object Area,Path,@{n='Exists';e={Test-Path -LiteralPath $_.Path}}

Write-Host 'Deployment readiness' -ForegroundColor Cyan
[pscustomobject]@{
    Computer = $env:COMPUTERNAME
    ExecutionContext = 'Standard user'
    RebootPending = $rebootState.Pending
    RebootReasons = $rebootState.Summary
    WingetVersion = $wingetVersion
} | Format-List

Write-Host 'Application configuration paths' -ForegroundColor Cyan
$configurationPaths | Format-Table -AutoSize

Write-Host 'Available winget upgrades' -ForegroundColor Cyan
$upgradeOutput

if ($rebootState.Pending) {
    Write-Host ('NOT READY: reboot pending ({0})' -f $rebootState.Summary) -ForegroundColor Yellow
}
else {
    Write-Host 'READY FOR REVIEW: no reboot-pending signal detected. Review upgrades before installing.' -ForegroundColor Green
}
