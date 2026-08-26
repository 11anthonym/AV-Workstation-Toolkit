<#
.SYNOPSIS
    Regenerates the low-risk Standard winget baseline from the AV Workstation Toolkit catalog.

.DESCRIPTION
    AppProfiles.psd1 remains the sole package-policy source. This script writes
    only Standard-profile, low-risk, allowlisted packages to the reusable
    winget baseline. It does not invoke winget or change workstation state.
#>

[CmdletBinding()]
param(
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$launchIsElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($launchIsElevated) { throw 'AV Workstation Toolkit scripts must be launched from a standard-user PowerShell session.' }

Import-Module (Join-Path $PSScriptRoot 'AVWorkstationToolkit.Core.psd1') -Force

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path (Get-AVWorkstationToolkitDataRoot) 'manifests\winget-team-baseline.json'
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$creationDate = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
if (Test-Path -LiteralPath $resolvedOutput -PathType Leaf) {
    try {
        $existingManifest = Get-Content -LiteralPath $resolvedOutput -Raw | ConvertFrom-Json
        if (-not [string]::IsNullOrWhiteSpace([string]$existingManifest.CreationDate)) {
            $creationDate = [string]$existingManifest.CreationDate
        }
    }
    catch { }
}

$packages = @(Get-AVWorkstationToolkitCatalog | Where-Object {
    $_.Profile -eq 'Standard' -and $_.Risk -eq 'None' -and $_.Deployment -eq 'Allowlisted'
} | Sort-Object Order | ForEach-Object {
    [ordered]@{ PackageIdentifier = $_.Id }
})

$manifest = [ordered]@{
    '$schema' = 'https://aka.ms/winget-packages.schema.2.0.json'
    CreationDate = $creationDate
    Sources = @(
        [ordered]@{
            Packages = $packages
            SourceDetails = [ordered]@{
                Argument = 'https://cdn.winget.microsoft.com/cache'
                Identifier = 'Microsoft.Winget.Source_8wekyb3d8bbwe'
                Name = 'winget'
                Type = 'Microsoft.PreIndexed.Package'
            }
        }
    )
}

$outputDirectory = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedOutput -Encoding UTF8
Write-Host "Generated AV Workstation Toolkit baseline: $resolvedOutput" -ForegroundColor Green
