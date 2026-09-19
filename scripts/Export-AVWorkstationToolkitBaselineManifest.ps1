<#
.SYNOPSIS
    Regenerates the low-risk Standard winget baseline from the canonical managed catalog.

.DESCRIPTION
    manifests\managed-applications.json is the canonical managed-package definition and the only
    input this script reads. The retired scripts\AppProfiles.psd1 fixture cannot influence the
    generated baseline. This script writes only Standard-profile, low-risk, allowlisted packages to
    the reusable winget baseline. It does not invoke winget or change workstation state.
#>

[CmdletBinding()]
param(
    [string]$OutputPath,
    [string]$ManagedCatalogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$launchIsElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($launchIsElevated) { throw 'AV Workstation Toolkit scripts must be launched from a standard-user PowerShell session.' }

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ManagedCatalogPath)) {
    $ManagedCatalogPath = Join-Path $repositoryRoot 'manifests\managed-applications.json'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot 'manifests\winget-team-baseline.json'
}
$resolvedCatalog = [IO.Path]::GetFullPath($ManagedCatalogPath)
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)

# Authoring-time validation. The compiled runtime revalidates this document strictly; these checks
# exist so a malformed or policy-violating catalog cannot silently produce a wrong baseline here.
$allowedPackageFields = @('Profile','Name','Id','Vendor','Risk','Note','Deployment','Maintenance')
$allowedProfiles = @('Standard','Field','Developer','Optional')
$allowedRisks = @('None','Driver','Service','Listener')
$allowedDeployments = @('Allowlisted','ManualHold')
$allowedMaintenance = @('Allowlisted','Hold')
$packageIdPattern = '^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$'

$catalogFile = Get-Item -LiteralPath $resolvedCatalog -Force -ErrorAction Stop
if ($catalogFile.PSIsContainer -or $catalogFile.Length -le 0 -or $catalogFile.Length -gt 1MB -or
    ($catalogFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "The canonical managed catalog must be a non-empty regular file no larger than 1 MiB: $resolvedCatalog"
}
try { $catalog = Get-Content -LiteralPath $resolvedCatalog -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "The canonical managed catalog is not valid JSON: $($_.Exception.Message)" }

if ($null -eq $catalog -or [int]$catalog.SchemaVersion -ne 1) {
    throw 'The canonical managed catalog must declare SchemaVersion 1.'
}
$forbiddenPattern = [string]$catalog.ForbiddenPattern
if ([string]::IsNullOrWhiteSpace($forbiddenPattern)) {
    throw 'The canonical managed catalog must declare a non-empty ForbiddenPattern.'
}
try { $forbidden = [regex]::new($forbiddenPattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase) }
catch { throw "The canonical managed catalog ForbiddenPattern is not a valid regular expression: $($_.Exception.Message)" }

$catalogPackages = @($catalog.Packages)
if ($catalogPackages.Count -eq 0) { throw 'The canonical managed catalog contains no packages.' }

$seenIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$index = -1
foreach ($package in $catalogPackages) {
    $index++
    $fields = @($package.PSObject.Properties.Name)
    $unknown = @($fields | Where-Object { $_ -notin $allowedPackageFields })
    if ($unknown.Count -gt 0) {
        throw "Managed catalog entry $index declares unsupported field(s): $($unknown -join ', ')"
    }
    foreach ($required in @('Profile','Name','Id','Vendor','Risk','Note')) {
        if ($required -notin $fields -or [string]::IsNullOrWhiteSpace([string]$package.$required)) {
            throw "Managed catalog entry $index is missing required field '$required'."
        }
    }
    $id = [string]$package.Id
    if ($id -notmatch $packageIdPattern) { throw "Managed catalog entry $index has invalid package ID '$id'." }
    if (-not $seenIds.Add($id)) { throw "Managed catalog declares duplicate package ID '$id'." }
    if ([string]$package.Profile -notin $allowedProfiles) { throw "Managed catalog entry $index has unsupported Profile '$($package.Profile)'." }
    if ([string]$package.Risk -notin $allowedRisks) { throw "Managed catalog entry $index has unsupported Risk '$($package.Risk)'." }
    if ('Deployment' -in $fields -and [string]$package.Deployment -notin $allowedDeployments) {
        throw "Managed catalog entry $index has unsupported Deployment '$($package.Deployment)'."
    }
    if ('Maintenance' -in $fields -and [string]$package.Maintenance -notin $allowedMaintenance) {
        throw "Managed catalog entry $index has unsupported Maintenance '$($package.Maintenance)'."
    }
    # Same defense-in-depth vector the compiled parser rejects.
    $policyText = @([string]$package.Name, $id, [string]$package.Vendor, [string]$package.Note) -join ' '
    if ($forbidden.IsMatch($policyText)) {
        throw "Managed catalog entry $index ('$id') matches the configured forbidden-product policy."
    }
}

# Deployment and Maintenance default to Allowlisted when absent, matching the compiled parser.
# Ordering is the canonical document order; no separate sort key is introduced.
$packages = @($catalogPackages | Where-Object {
    [string]$_.Profile -eq 'Standard' -and [string]$_.Risk -eq 'None' -and
    ($_.PSObject.Properties.Name -notcontains 'Deployment' -or [string]$_.Deployment -eq 'Allowlisted')
} | ForEach-Object {
    [ordered]@{ PackageIdentifier = [string]$_.Id }
})
if ($packages.Count -eq 0) { throw 'The canonical managed catalog yielded no eligible baseline packages.' }

# Reuse the recorded CreationDate so regeneration stays byte-stable when the package set is unchanged.
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
Write-Host ("Generated AV Workstation Toolkit baseline from {0}: {1} ({2} packages)" -f
    (Split-Path -Leaf $resolvedCatalog),$resolvedOutput,$packages.Count) -ForegroundColor Green
