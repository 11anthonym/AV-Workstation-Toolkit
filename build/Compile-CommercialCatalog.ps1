<#
.SYNOPSIS
    Compiles authoritative per-vendor commercial AV catalog sources.

.DESCRIPTION
    Reads catalog\vendors\*.json, validates vendor identity and duplicate
    package IDs, validates the normalized schema through AVWorkstationToolkit.Core, and
    writes the single runtime artifact at manifests\commercial-av-catalog.json.
    Use -Check in builds and CI to prove that the tracked compiled artifact is
    current without modifying it.
#>

[CmdletBinding()]
param(
    [string]$SourceRoot,
    [string]$OutputPath,
    [switch]$Check,
    [switch]$InitializeFromCompiled
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$utilityModulePath = Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1'
Microsoft.PowerShell.Core\Import-Module -Name $utilityModulePath -Force -ErrorAction Stop

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$utf8WithBom = [Text.UTF8Encoding]::new($true)
$utf8Strict = [Text.UTF8Encoding]::new($false,$true)
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Join-Path $repositoryRoot 'catalog\vendors'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot 'manifests\commercial-av-catalog.json'
}
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)

function ConvertTo-AVWorkstationToolkitVendorSlug {
    param([Parameter(Mandatory)][string]$Vendor)

    $slug = $Vendor.Trim().ToLowerInvariant().Replace('&',' and ')
    $slug = ($slug -replace '[^a-z0-9]+','-').Trim('-')
    if ([string]::IsNullOrWhiteSpace($slug)) {
        throw "Vendor name cannot produce a safe source filename: $Vendor"
    }
    return $slug
}

function ConvertTo-AVWorkstationToolkitCatalogJson {
    param([Parameter(Mandatory)]$Document)

    return (($Document | ConvertTo-Json -Depth 20) + [Environment]::NewLine)
}

function Assert-AVWorkstationToolkitObjectKeys {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string[]]$Allowed,
        [Parameter(Mandatory)][string]$Description
    )

    if ($null -eq $Object -or $Object -is [string] -or $null -eq $Object.PSObject) {
        throw "$Description must be a JSON object."
    }
    $unexpected = @($Object.PSObject.Properties.Name | Where-Object { $_ -notin $Allowed })
    if ($unexpected.Count -gt 0) {
        throw ("{0} contains unsupported keys: {1}" -f $Description,($unexpected -join ', '))
    }
}

if ($InitializeFromCompiled) {
    if ($Check) { throw '-InitializeFromCompiled cannot be combined with -Check.' }
    if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        throw "Compiled catalog was not found: $OutputPath"
    }
    $existingSources = @(if (Test-Path -LiteralPath $SourceRoot -PathType Container) {
        Get-ChildItem -LiteralPath $SourceRoot -File -Filter '*.json'
    })
    if ($existingSources.Count -gt 0) {
        throw "Catalog source initialization requires an empty source directory: $SourceRoot"
    }

    $compiled = [IO.File]::ReadAllText($OutputPath,$utf8Strict) | ConvertFrom-Json -ErrorAction Stop
    Assert-AVWorkstationToolkitObjectKeys -Object $compiled -Allowed @('SchemaVersion','Packages') -Description 'Compiled commercial catalog'
    if ([int]$compiled.SchemaVersion -ne 3) { throw 'Compiled commercial catalog must use schema version 3.' }
    New-Item -ItemType Directory -Path $SourceRoot -Force | Out-Null

    $usedSlugs = @{}
    foreach ($group in @($compiled.Packages | Group-Object { ([string]$_.Metadata.Vendor).Trim() } | Sort-Object Name)) {
        $vendor = [string]$group.Name
        if ([string]::IsNullOrWhiteSpace($vendor)) { throw 'A compiled package has no Metadata.Vendor value.' }
        $slug = ConvertTo-AVWorkstationToolkitVendorSlug -Vendor $vendor
        if ($usedSlugs.ContainsKey($slug)) {
            throw "Vendor source filename collision between '$vendor' and '$($usedSlugs[$slug])'."
        }
        $usedSlugs[$slug] = $vendor
        $sourceDocument = [ordered]@{
            SchemaVersion = 1
            Vendor = $vendor
            Packages = @($group.Group)
        }
        $sourcePath = Join-Path $SourceRoot ($slug + '.json')
        [IO.File]::WriteAllText($sourcePath,(ConvertTo-AVWorkstationToolkitCatalogJson -Document $sourceDocument),$utf8NoBom)
    }
}

if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
    throw "Commercial catalog source directory was not found: $SourceRoot"
}
$sourceFiles = @(Get-ChildItem -LiteralPath $SourceRoot -File -Filter '*.json' | Sort-Object Name)
if ($sourceFiles.Count -eq 0) { throw "Commercial catalog has no vendor source files: $SourceRoot" }

$vendors = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$packages = [Collections.Generic.List[object]]::new()
foreach ($file in $sourceFiles) {
    try { $source = [IO.File]::ReadAllText($file.FullName,$utf8Strict) | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "Catalog source '$($file.Name)' is not valid JSON: $($_.Exception.Message)" }
    Assert-AVWorkstationToolkitObjectKeys -Object $source -Allowed @('SchemaVersion','Vendor','Packages') -Description "Catalog source '$($file.Name)'"
    if ($source.SchemaVersion -isnot [int] -or [int]$source.SchemaVersion -ne 1) {
        throw "Catalog source '$($file.Name)' must use integer schema version 1."
    }
    $vendor = ([string]$source.Vendor).Trim()
    if ([string]::IsNullOrWhiteSpace($vendor) -or $vendor.Length -gt 128) {
        throw "Catalog source '$($file.Name)' has an invalid Vendor value."
    }
    if (-not $vendors.Add($vendor)) { throw "Commercial catalog vendor is defined more than once: $vendor" }
    $expectedName = (ConvertTo-AVWorkstationToolkitVendorSlug -Vendor $vendor) + '.json'
    if (-not $file.Name.Equals($expectedName,[StringComparison]::OrdinalIgnoreCase)) {
        throw "Catalog source '$($file.Name)' must be named '$expectedName' for vendor '$vendor'."
    }

    $vendorPackages = @($source.Packages)
    if ($vendorPackages.Count -eq 0) { throw "Catalog source '$($file.Name)' contains no packages." }
    foreach ($package in $vendorPackages) {
        $id = ([string]$package.Id).Trim()
        if ($id -notmatch '^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$') {
            throw "Catalog source '$($file.Name)' contains an invalid package ID: $id"
        }
        if (-not $ids.Add($id)) { throw "Commercial catalog package ID is duplicated: $id" }
        $packageVendor = ([string]$package.Metadata.Vendor).Trim()
        if (-not $packageVendor.Equals($vendor,[StringComparison]::Ordinal)) {
            throw "Package '$id' vendor '$packageVendor' does not exactly match source vendor '$vendor'."
        }
        $packages.Add($package)
    }
}

$document = [ordered]@{
    SchemaVersion = 3
    Packages = @($packages)
}
$json = ConvertTo-AVWorkstationToolkitCatalogJson -Document $document

$coreManifest = Join-Path $repositoryRoot 'scripts\AVWorkstationToolkit.Core.psd1'
Microsoft.PowerShell.Core\Import-Module -Name $coreManifest -Force -ErrorAction Stop
$normalized = @(ConvertFrom-AVWorkstationToolkitExternalCatalogJson -Json $json)
if ($normalized.Count -ne $packages.Count) {
    throw "Compiled catalog normalized $($normalized.Count) packages from $($packages.Count) source records."
}
if (@($normalized | Where-Object { $_.Provider -ne 'External' -or $_.Deployment -ne 'ManualHold' -or $_.Maintenance -ne 'Hold' -or $_.DeploymentClass -eq 'Managed' }).Count -gt 0) {
    throw 'Compiled commercial catalog would expand execution authority.'
}

if ($Check) {
    if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) { throw "Compiled catalog is missing: $OutputPath" }
    $outputBytes = [IO.File]::ReadAllBytes($OutputPath)
    if ($outputBytes.Length -lt 3 -or $outputBytes[0] -ne 0xEF -or $outputBytes[1] -ne 0xBB -or $outputBytes[2] -ne 0xBF) {
        throw 'Compiled commercial catalog must carry a UTF-8 BOM for Windows PowerShell 5.1 runtime compatibility.'
    }
    $existing = [IO.File]::ReadAllText($OutputPath,$utf8Strict)
    $expected = [string]$json
    if ($existing -cne $expected) {
        $firstDifference = -1
        for ($index = 0; $index -lt [Math]::Min($existing.Length,$expected.Length); $index++) {
            if ($existing[$index] -cne $expected[$index]) { $firstDifference = $index; break }
        }
        throw "Compiled commercial catalog is stale at character $firstDifference (tracked length $($existing.Length), expected length $($expected.Length)). Run build\Compile-CommercialCatalog.ps1 and commit the result."
    }
    Write-Output ("CATALOG_OK vendors={0} packages={1} output={2}" -f $sourceFiles.Count,$packages.Count,$OutputPath)
    return
}

$outputDirectory = [IO.Path]::GetDirectoryName($OutputPath)
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
[IO.File]::WriteAllText($OutputPath,$json,$utf8WithBom)
Write-Output ("CATALOG_COMPILED vendors={0} packages={1} output={2}" -f $sourceFiles.Count,$packages.Count,$OutputPath)
