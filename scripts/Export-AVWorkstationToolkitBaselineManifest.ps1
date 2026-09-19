<#
.SYNOPSIS
    Regenerates the low-risk Standard winget baseline from the canonical managed catalog.

.DESCRIPTION
    manifests\managed-applications.json is the canonical managed-package definition and the only
    input this script reads. The retired scripts\AppProfiles.psd1 fixture cannot influence the
    generated baseline. This script writes only Standard-profile, low-risk, allowlisted packages to
    the reusable winget baseline. It does not invoke winget or change workstation state.

    Validation is a hand-maintained approximation of the compiled managed-catalog path -
    RepositoryCatalogLoader.ParseManagedCatalog, CatalogParser.NormalizeManaged, CatalogParser.Text,
    CatalogTokens.Parse and the PackageCatalog duplicate-ID guard - intended to stop this script
    generating a baseline from a catalog the product would refuse to load. It is an approximation,
    not a proof: the guarantee holds for the conditions enumerated in the Run-Tests equivalence suite
    and no further. Where exact replication is impractical in Windows PowerShell 5.1 the script is
    deliberately the stricter side; see EQUIVALENCE NOTES at the end of this file. The compiled
    loader remains the authority and revalidates at runtime.
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

# Exactly the property sets the compiled ManagedCatalogDocument/ManagedCatalogPackage records accept.
# Comparison is ordinal: the compiled deserializer uses PropertyNameCaseInsensitive = false.
$allowedTopLevelFields = @('SchemaVersion','ForbiddenPattern','Packages')
$allowedPackageFields = @('Profile','Name','Id','Vendor','Risk','Note','Deployment','Maintenance')
$requiredPackageFields = @('Profile','Id','Note')
# CatalogTokens.Parse uses case-sensitive Enum.TryParse, so these sets are compared ordinally.
$allowedProfiles = @('Standard','Field','Developer','Optional')
$allowedRisks = @('None','Driver','Service','Listener')
$allowedDeployments = @('Allowlisted','ManualHold')
$allowedMaintenance = @('Allowlisted','Hold')
$packageIdPattern = '^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$'

function Test-OrdinalMember {
    param([string[]]$Set,[string]$Value)
    foreach ($candidate in $Set) { if ([string]::Equals($candidate, $Value, [StringComparison]::Ordinal)) { return $true } }
    return $false
}

function Assert-CatalogText {
    <# Mirrors CatalogParser.Text: required-unless-allowEmpty, bounded length, already trimmed, no control characters. #>
    param([string]$Value,[string]$Field,[int]$MaximumLength,[switch]$AllowEmpty)
    if ($null -eq $Value) { $Value = '' }
    $hasControlCharacter = $false
    foreach ($character in $Value.ToCharArray()) {
        if ([char]::IsControl($character)) { $hasControlCharacter = $true; break }
    }
    if ((-not $AllowEmpty -and [string]::IsNullOrWhiteSpace($Value)) -or
        $Value.Length -gt $MaximumLength -or
        $Value -cne $Value.Trim() -or
        $hasControlCharacter) {
        throw "$Field contains invalid text."
    }
    return $Value
}

function ConvertFrom-JsonPropertyName {
    <#
        Decodes one raw JSON string literal to the property name the compiled loader compares.
        JsonDocument exposes decoded names, so a Unicode-escaped spelling of a name is the same
        property as its literal spelling, and comparing raw spellings would let an escape disguise a
        duplicate. Decoding is delegated to the platform's own JSON reader rather than a
        hand-written unescaper; a malformed escape fails closed.
    #>
    param([string]$RawLiteral)

    if ($RawLiteral.IndexOf([char]92) -lt 0) { return $RawLiteral }
    try { return ('{"name":"' + $RawLiteral + '"}' | ConvertFrom-Json).name }
    catch { throw "Managed catalog contains an invalid JSON property-name escape: '$RawLiteral'." }
}

function Assert-NoDuplicateJsonProperty {
    <#
        Mirrors RepositoryCatalogLoader.RejectDuplicateProperties, which rejects repeated decoded
        property names ordinally at every object depth. Windows PowerShell 5.1 ConvertFrom-Json
        silently keeps one of a duplicated pair, so the raw document is scanned before deserialization.
    #>
    param([string]$Json)

    $objectKeys = [System.Collections.Generic.Stack[System.Collections.Generic.HashSet[string]]]::new()
    $pendingKey = $null
    $index = 0
    while ($index -lt $Json.Length) {
        $character = $Json[$index]
        if ($character -eq '"') {
            $literal = [Text.StringBuilder]::new()
            $index++
            while ($index -lt $Json.Length -and $Json[$index] -ne '"') {
                if ($Json[$index] -eq '\') {
                    $null = $literal.Append($Json[$index])
                    $index++
                    if ($index -ge $Json.Length) { break }
                }
                $null = $literal.Append($Json[$index])
                $index++
            }
            $pendingKey = $literal.ToString()
            $index++
            continue
        }
        switch ($character) {
            '{' { $objectKeys.Push([System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)); $pendingKey = $null }
            '}' { if ($objectKeys.Count -gt 0) { $null = $objectKeys.Pop() }; $pendingKey = $null }
            ':' {
                # Only a literal followed by ':' is a property name, so decode at this point: escapes
                # inside ordinary string values are never treated as names.
                if ($null -ne $pendingKey -and $objectKeys.Count -gt 0) {
                    $propertyName = ConvertFrom-JsonPropertyName -RawLiteral $pendingKey
                    if (-not $objectKeys.Peek().Add($propertyName)) {
                        throw "Managed catalog repeats JSON property '$propertyName'."
                    }
                }
                $pendingKey = $null
            }
            ',' { $pendingKey = $null }
        }
        $index++
    }
}

$catalogFile = Get-Item -LiteralPath $resolvedCatalog -Force -ErrorAction Stop
if ($catalogFile.PSIsContainer -or $catalogFile.Length -le 0 -or $catalogFile.Length -gt 1MB -or
    ($catalogFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "The canonical managed catalog must be a non-empty regular file no larger than 1 MiB: $resolvedCatalog"
}
$catalogJson = Get-Content -LiteralPath $resolvedCatalog -Raw -Encoding UTF8
Assert-NoDuplicateJsonProperty -Json $catalogJson
# ConvertFrom-Json already rejects trailing commas and comments, matching the compiled JsonDocument options.
try { $catalog = $catalogJson | ConvertFrom-Json }
catch { throw "The canonical managed catalog is not valid JSON: $($_.Exception.Message)" }
if ($null -eq $catalog) { throw 'The canonical managed catalog is empty.' }

$topLevelFields = @($catalog.PSObject.Properties.Name)
$unknownTopLevel = @($topLevelFields | Where-Object { -not (Test-OrdinalMember -Set $allowedTopLevelFields -Value $_) })
if ($unknownTopLevel.Count -gt 0) {
    throw "The canonical managed catalog declares unsupported top-level field(s): $($unknownTopLevel -join ', ')"
}
foreach ($required in $allowedTopLevelFields) {
    if (-not (Test-OrdinalMember -Set $topLevelFields -Value $required)) {
        throw "The canonical managed catalog is missing top-level field '$required'."
    }
}
if ([string]$catalog.SchemaVersion -cne '1') {
    throw "The canonical managed catalog schema version is unsupported: $($catalog.SchemaVersion)"
}
$forbiddenPattern = [string]$catalog.ForbiddenPattern
if ([string]::IsNullOrWhiteSpace($forbiddenPattern)) {
    throw 'The canonical managed catalog must declare a non-empty ForbiddenPattern.'
}
try {
    $forbidden = [regex]::new(
        $forbiddenPattern,
        [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant,
        [TimeSpan]::FromSeconds(2))
}
catch { throw "The canonical managed catalog ForbiddenPattern is not a valid regular expression: $($_.Exception.Message)" }

$catalogPackages = @($catalog.Packages)
if ($catalogPackages.Count -eq 0) { throw 'The canonical managed catalog contains no packages.' }

# Normalize exactly once, applying the compiled loader's Optional() trim and NormalizeManaged defaults.
$normalized = [System.Collections.Generic.List[object]]::new()
$seenIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$index = -1
foreach ($package in $catalogPackages) {
    $index++
    $fields = @($package.PSObject.Properties.Name)
    $unknown = @($fields | Where-Object { -not (Test-OrdinalMember -Set $allowedPackageFields -Value $_) })
    if ($unknown.Count -gt 0) {
        throw "Managed catalog entry $index declares unsupported field(s): $($unknown -join ', ')"
    }
    foreach ($required in $requiredPackageFields) {
        if (-not (Test-OrdinalMember -Set $fields -Value $required) -or
            [string]::IsNullOrWhiteSpace([string]$package.$required)) {
            throw "Managed catalog entry $index is missing required field '$required'."
        }
    }

    # Required fields reach the compiled parser untrimmed; optional fields are trimmed by the loader.
    $id = Assert-CatalogText -Value ([string]$package.Id) -Field "Catalog entry $index.Id" -MaximumLength 128
    if ($id -notmatch $packageIdPattern) { throw "Managed catalog entry $index has invalid package ID '$id'." }
    $note = Assert-CatalogText -Value ([string]$package.Note) -Field "Catalog entry $index.Note" -MaximumLength 1024
    $rawName = if (Test-OrdinalMember -Set $fields -Value 'Name') { ([string]$package.Name).Trim() } else { '' }
    $name = Assert-CatalogText -Value $(if ([string]::IsNullOrEmpty($rawName)) { $id } else { $rawName }) -Field "Catalog entry $index.Name" -MaximumLength 256
    $vendor = Assert-CatalogText -Value $(if (Test-OrdinalMember -Set $fields -Value 'Vendor') { ([string]$package.Vendor).Trim() } else { '' }) `
        -Field "Catalog entry $index.Vendor" -MaximumLength 128 -AllowEmpty

    $profileToken = [string]$package.Profile
    $riskToken = if (Test-OrdinalMember -Set $fields -Value 'Risk') { ([string]$package.Risk).Trim() } else { 'None' }
    $deploymentToken = if (Test-OrdinalMember -Set $fields -Value 'Deployment') { ([string]$package.Deployment).Trim() } else { 'Allowlisted' }
    $maintenanceToken = if (Test-OrdinalMember -Set $fields -Value 'Maintenance') { ([string]$package.Maintenance).Trim() } else { 'Allowlisted' }
    if (-not (Test-OrdinalMember -Set $allowedProfiles -Value $profileToken)) { throw "Catalog entry $index.Profile contains unsupported value '$profileToken'." }
    if (-not (Test-OrdinalMember -Set $allowedRisks -Value $riskToken)) { throw "Catalog entry $index.Risk contains unsupported value '$riskToken'." }
    if (-not (Test-OrdinalMember -Set $allowedDeployments -Value $deploymentToken)) { throw "Catalog entry $index.Deployment contains unsupported value '$deploymentToken'." }
    if (-not (Test-OrdinalMember -Set $allowedMaintenance -Value $maintenanceToken)) { throw "Catalog entry $index.Maintenance contains unsupported value '$maintenanceToken'." }

    # Same combined vector and ordering the compiled parser screens.
    if ($forbidden.IsMatch(($name, $id, $vendor, $note) -join ' ')) {
        throw "Managed catalog entry $index ('$id') matches the configured forbidden-product policy."
    }
    # PackageCatalog rejects duplicate IDs case-insensitively.
    if (-not $seenIds.Add($id)) { throw "Managed catalog contains duplicate package ID '$id'." }

    $normalized.Add([pscustomobject]@{ Id = $id; Profile = $profileToken; Risk = $riskToken; Deployment = $deploymentToken }) | Out-Null
}

# Baseline eligibility uses the normalized/defaulted tokens, compared ordinally.
$packages = @($normalized | Where-Object {
    [string]::Equals($_.Profile,'Standard',[StringComparison]::Ordinal) -and
    [string]::Equals($_.Risk,'None',[StringComparison]::Ordinal) -and
    [string]::Equals($_.Deployment,'Allowlisted',[StringComparison]::Ordinal)
} | ForEach-Object { [ordered]@{ PackageIdentifier = $_.Id } })
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

<#
    EQUIVALENCE NOTES

    This validation re-implements another implementation's rules by hand, so it can drift from them.
    An earlier revision compared raw escaped property spellings and therefore accepted a catalog
    whose duplicate key was disguised as a Unicode escape - a catalog the compiled loader rejects.
    Treat the Run-Tests equivalence suite as the contract: when the compiled managed-catalog path
    changes, re-verify against it and extend the suite rather than assuming this script still agrees.

    Within the conditions that suite enumerates, anything the compiled path rejects is rejected here.
    In two cases this script is deliberately stricter, because replicating Enum.TryParse exactly
    would mean compiling the Domain enums into Windows PowerShell 5.1:

    1. Numeric enum strings. Enum.TryParse accepts "0" for Risk and Enum.IsDefined then passes, so
       production tolerates it; this script requires the declared token name.
    2. Whitespace-padded Profile. Profile reaches the compiled parser untrimmed and Enum.TryParse
       trims, so production tolerates " Standard"; this script requires the exact token.

    Both are malformed authoring that the canonical catalog does not contain, and being stricter can
    only refuse to generate - it can never emit a baseline the product would reject.
#>
