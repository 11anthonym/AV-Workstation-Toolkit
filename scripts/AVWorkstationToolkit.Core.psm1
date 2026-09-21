Set-StrictMode -Version Latest

# Resolve the catalog parser dependency against this PowerShell host instead of
# accepting an incompatible same-named module from an inherited PSModulePath.
$utilityModulePath = Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1'
Microsoft.PowerShell.Core\Import-Module -Name $utilityModulePath -Force -ErrorAction Stop
$ErrorActionPreference = 'Stop'

function Get-AVWorkstationToolkitDataRoot {
    [CmdletBinding()]
    param([string]$Path)

    $candidate = $Path
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = [Environment]::GetEnvironmentVariable('AVWORKSTATIONTOOLKIT_DATA_ROOT','Process')
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        # Legacy compatibility only: honor an explicit data-root override used
        # by the pre-rename product before selecting the new canonical root.
        $candidate = [Environment]::GetEnvironmentVariable('AVINITE_DATA_ROOT','Process')
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $applicationRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        if (Test-Path -LiteralPath (Join-Path $applicationRoot '.git') -PathType Container) {
            $candidate = $applicationRoot
        }
        else {
            $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
            if ([string]::IsNullOrWhiteSpace($localAppData)) {
                throw 'The current user LocalAppData folder could not be resolved.'
            }
            $candidate = Join-Path $localAppData 'AVWorkstationToolkit'
        }
    }
    if (-not [IO.Path]::IsPathRooted($candidate)) {
        throw 'AV Workstation Toolkit data root must be an absolute path.'
    }

    $resolved = [IO.Path]::GetFullPath($candidate).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
    $volumeRoot = [IO.Path]::GetPathRoot($resolved).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
    if ([string]::IsNullOrWhiteSpace($resolved) -or $resolved.Equals($volumeRoot,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'AV Workstation Toolkit data root cannot be a filesystem or volume root.'
    }
    return $resolved
}

function Get-AVWorkstationToolkitValue {
    param(
        [Parameter(Mandatory)]$InputObject,
        [Parameter(Mandatory)][string]$Name,
        $Default = $null
    )

    if ($InputObject -is [System.Collections.IDictionary] -and $InputObject.Contains($Name)) {
        return $InputObject[$Name]
    }
    if ($InputObject.PSObject.Properties.Name -contains $Name) {
        return $InputObject.$Name
    }
    return $Default
}

function Resolve-AVWorkstationToolkitInstallerMode {
    <#
        Installer mode decides whether --silent is passed, so a wrong-typed value must never reach a
        conversion. Windows PowerShell turns @('InstallerDefault') into 'InstallerDefault', 1 into
        '1' and $true into 'True', which would let a malformed catalog record authorize omitting the
        flag. The raw value is therefore type-checked before anything converts it. An absent member
        keeps the Silent default, and the token comparison is ordinal because the compiled loader
        parses it case-sensitively while PowerShell -in does not.
    #>
    param([Parameter(Mandatory)]$InputObject,[string]$PackageId = '')

    # The member is read directly rather than through Get-AVWorkstationToolkitValue: PowerShell
    # unrolls a single-element array when a function returns it, so that accessor would hand back
    # 'InstallerDefault' and the type check below would never see the array. Assigning from the
    # member preserves the original object.
    $raw = $null
    if ($InputObject -is [System.Collections.IDictionary]) {
        if ($InputObject.Contains('InstallerMode')) { $raw = $InputObject['InstallerMode'] }
    }
    elseif ($InputObject.PSObject.Properties.Name -contains 'InstallerMode') {
        $raw = $InputObject.InstallerMode
    }
    $label = if ([string]::IsNullOrWhiteSpace($PackageId)) { 'the catalog entry' } else { $PackageId }
    if ($null -eq $raw) { return 'Silent' }
    if ($raw -isnot [string]) { throw "Installer mode for $label must be a single string value." }
    if ([string]::Equals($raw,'Silent',[StringComparison]::Ordinal) -or
        [string]::Equals($raw,'InstallerDefault',[StringComparison]::Ordinal)) { return $raw }
    throw "Invalid installer mode '$raw' for $label."
}

function ConvertTo-AVWorkstationToolkitVersion {
    param([Parameter(Mandatory)][string]$Value)

    if ($Value -notmatch '^\d+(?:\.\d+){1,3}$') {
        throw "Version must contain two to four numeric fields: $Value"
    }
    $parts = [System.Collections.Generic.List[int]]::new()
    foreach ($text in $Value.Split('.')) {
        $number = 0
        if (-not [int]::TryParse($text,[ref]$number) -or $number -lt 0) {
            throw "Version contains an invalid numeric field: $Value"
        }
        $parts.Add($number)
    }
    while ($parts.Count -lt 4) { $parts.Add(0) }
    return [version]::new($parts[0],$parts[1],$parts[2],$parts[3])
}

function Compare-AVWorkstationToolkitVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Left,
        [Parameter(Mandatory)][string]$Right
    )

    return (ConvertTo-AVWorkstationToolkitVersion -Value $Left).CompareTo((ConvertTo-AVWorkstationToolkitVersion -Value $Right))
}

function ConvertTo-AVWorkstationToolkitVersionSortKey {
    [CmdletBinding()]
    param([AllowEmptyString()][string]$Label)

    if ([string]::IsNullOrWhiteSpace($Label)) { return '4|' }
    $value = $Label.Trim()
    if ($value.StartsWith('Known: ',[StringComparison]::OrdinalIgnoreCase)) { $value = $value.Substring(7).Trim() }
    if ($value.Equals('Not installed',[StringComparison]::OrdinalIgnoreCase)) { return '2|' }
    if ($value.Equals('Not evaluated',[StringComparison]::OrdinalIgnoreCase)) { return '3|' }
    $match = [regex]::Match($value,'^\s*[vV]?(?<numeric>\d+(?:\.\d+){0,7})(?<suffix>(?:[-+][0-9A-Za-z.-]+)?)\s*$')
    if (-not $match.Success) { return '1|' + $value.ToUpperInvariant() }
    $segments = [Collections.Generic.List[string]]::new()
    $parts = @($match.Groups['numeric'].Value.Split('.'))
    for ($index = 0; $index -lt 8; $index++) {
        $part = if ($index -lt $parts.Count) { $parts[$index].TrimStart('0') } else { '' }
        if ([string]::IsNullOrEmpty($part)) { $part = '0' }
        $segments.Add(('{0:D3}:{1}' -f $part.Length,$part))
    }
    return '0|' + ($segments -join '|') + '|' + $match.Groups['suffix'].Value.ToUpperInvariant()
}

function Test-AVWorkstationToolkitQuickViewMatch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Item,
        [ValidateSet('All','Missing','Updates')][string]$QuickView = 'All'
    )

    switch ($QuickView) {
        'Missing' { return $Item.Status -eq 'Missing' -or (-not $Item.Installed -and $Item.Status -in @('Manual','NotDetected','Held')) }
        'Updates' { return $Item.Status -in @('UpdateAvailable','ManualUpdate') -or ($Item.Status -eq 'Held' -and $Item.Installed -and -not [string]::IsNullOrWhiteSpace([string]$Item.AvailableVersion)) }
        default { return $true }
    }
}

function Assert-AVWorkstationToolkitHttpsUri {
    param([Parameter(Mandatory)][string]$Value,[Parameter(Mandatory)][string]$Field)

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 2048 -or $Value -match '[\x00-\x20\x7F]') {
        throw "$Field must be a bounded absolute HTTPS URI."
    }
    $uri = $null
    if (-not [uri]::TryCreate($Value,[UriKind]::Absolute,[ref]$uri) -or
        $uri.Scheme -ne [Uri]::UriSchemeHttps -or
        [string]::IsNullOrWhiteSpace($uri.DnsSafeHost) -or
        -not [string]::IsNullOrEmpty($uri.UserInfo)) {
        throw "$Field must be an absolute HTTPS URI without embedded credentials."
    }
    return $uri.AbsoluteUri
}

function New-AVWorkstationToolkitVersionRegex {
    param([Parameter(Mandatory)][string]$Pattern,[Parameter(Mandatory)][string]$Field)

    if ([string]::IsNullOrWhiteSpace($Pattern) -or $Pattern.Length -gt 1024 -or $Pattern -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') {
        throw "$Field must be a bounded regular expression."
    }
    try {
        $regex = [regex]::new(
            $Pattern,
            [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant,
            [TimeSpan]::FromSeconds(2))
    }
    catch { throw "$Field is invalid: $($_.Exception.Message)" }
    if ('Version' -notin $regex.GetGroupNames()) {
        throw "$Field must contain a named Version capture group."
    }
    return $regex
}

function Copy-AVWorkstationToolkitLegacyFile {
    param(
        [Parameter(Mandatory)][string]$LegacyRoot,
        [Parameter(Mandatory)][string]$DataRoot,
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$DestinationPath,
        [Parameter(Mandatory)][long]$MaximumBytes
    )

    $source = [IO.Path]::GetFullPath($SourcePath)
    $destination = [IO.Path]::GetFullPath($DestinationPath)
    $legacyPrefix = [IO.Path]::GetFullPath($LegacyRoot).TrimEnd('\') + '\'
    $dataPrefix = [IO.Path]::GetFullPath($DataRoot).TrimEnd('\') + '\'
    if (-not $source.StartsWith($legacyPrefix,[StringComparison]::OrdinalIgnoreCase) -or
        -not $destination.StartsWith($dataPrefix,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Legacy migration file escaped an approved data root.'
    }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { return $false }
    $sourceItem = Get-Item -LiteralPath $source -Force -ErrorAction Stop
    if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $sourceItem.Length -gt $MaximumBytes -or $sourceItem.Length -lt 0) {
        throw 'Legacy migration file is a reparse point or exceeds its size limit.'
    }
    if (Test-AVWorkstationToolkitPathForReparsePoint -Root $LegacyRoot -Path $source) {
        throw 'Legacy migration source contains an unsupported reparse point.'
    }
    if (Test-Path -LiteralPath $destination -PathType Leaf) { return $false }

    $parent = [IO.Path]::GetDirectoryName($destination)
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    if (-not $parent.Equals([IO.Path]::GetFullPath($DataRoot).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase) -and
        (Test-AVWorkstationToolkitPathForReparsePoint -Root $DataRoot -Path $parent)) {
        throw 'Legacy migration destination contains an unsupported reparse point.'
    }
    $temporary = Join-Path $parent ('.' + [IO.Path]::GetFileName($destination) + '.migration.tmp')
    try {
        if (Test-Path -LiteralPath $temporary) {
            $temporaryItem = Get-Item -LiteralPath $temporary -Force -ErrorAction Stop
            if (($temporaryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Legacy migration temporary path is an unsupported reparse point.'
            }
            Remove-Item -LiteralPath $temporary -Force
        }
        [IO.File]::Copy($source,$temporary,$false)
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $temporaryHash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
        if (-not $sourceHash.Equals($temporaryHash,[StringComparison]::OrdinalIgnoreCase)) {
            throw 'Legacy migration copy failed SHA-256 verification.'
        }
        [IO.File]::Move($temporary,$destination)
        return $true
    }
    finally { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
}

function Invoke-AVWorkstationToolkitLegacyDataMigration {
    [CmdletBinding()]
    param([string]$DataRoot,[string]$LegacyRoot)

    $data = Get-AVWorkstationToolkitDataRoot -Path $DataRoot
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localAppData)) { throw 'The current user LocalAppData folder could not be resolved.' }
    $canonicalRoot = [IO.Path]::GetFullPath((Join-Path $localAppData 'AVWorkstationToolkit')).TrimEnd('\')
    if ([string]::IsNullOrWhiteSpace($LegacyRoot)) {
        if (-not $data.Equals($canonicalRoot,[StringComparison]::OrdinalIgnoreCase)) {
            return [pscustomobject]@{ Status='NotApplicable'; MigratedFiles=0; LegacyEvidenceRetained=$false }
        }
        # This is the one historical LocalAppData identity accepted for
        # migration. Runtime scripts are never copied from it.
        $LegacyRoot = Join-Path $localAppData 'AVinite'
    }
    if (-not [IO.Path]::IsPathRooted($LegacyRoot)) { throw 'Legacy data root must be absolute.' }
    $legacy = [IO.Path]::GetFullPath($LegacyRoot).TrimEnd('\')
    if ($legacy.Equals($data,[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $legacy -PathType Container)) {
        return [pscustomobject]@{ Status='NoLegacyData'; MigratedFiles=0; LegacyEvidenceRetained=$false }
    }
    foreach ($root in @($legacy,$data)) {
        if (Test-Path -LiteralPath $root) {
            $item = Get-Item -LiteralPath $root -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                return [pscustomobject]@{ Status='UnsafeLegacyState'; MigratedFiles=0; LegacyEvidenceRetained=$true }
            }
        }
    }

    New-Item -ItemType Directory -Path $data -Force | Out-Null
    $markerPath = Join-Path $data 'legacy-data-migration-v1.json'
    if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
        if ((Get-Item -LiteralPath $markerPath -Force).Length -gt 65536 -or
            (Test-AVWorkstationToolkitPathForReparsePoint -Root $data -Path $markerPath)) {
            throw 'Legacy migration marker is unsafe.'
        }
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json -ErrorAction Stop
        if ([int]$marker.SchemaVersion -ne 1) { throw 'Legacy migration marker schema is unsupported.' }
        return [pscustomobject]@{ Status='AlreadyCompleted'; MigratedFiles=[int]$marker.MigratedFiles; LegacyEvidenceRetained=$true }
    }

    $migrated = 0
    $trustedSource = Join-Path $legacy 'trusted-sftp-hosts.json'
    if (Test-Path -LiteralPath $trustedSource -PathType Leaf) {
        if ((Get-Item -LiteralPath $trustedSource -Force).Length -gt 65536) { throw 'Legacy trusted-host store exceeds its size limit.' }
        $trustedDocument = Get-Content -LiteralPath $trustedSource -Raw | ConvertFrom-Json -ErrorAction Stop
        if ([int]$trustedDocument.SchemaVersion -ne 1 -or $trustedDocument.PSObject.Properties.Name -notcontains 'Hosts') {
            throw 'Legacy trusted-host store schema is unsupported.'
        }
        if (Copy-AVWorkstationToolkitLegacyFile -LegacyRoot $legacy -DataRoot $data -SourcePath $trustedSource -DestinationPath (Join-Path $data 'trusted-sftp-hosts.json') -MaximumBytes 65536) { $migrated++ }
    }
    $launcherLogSource = Join-Path $legacy 'launcher-error.log'
    if (Copy-AVWorkstationToolkitLegacyFile -LegacyRoot $legacy -DataRoot $data -SourcePath $launcherLogSource -DestinationPath (Join-Path $data 'launcher-error.log') -MaximumBytes 10MB) { $migrated++ }

    $legacyRequests = Join-Path $legacy 'logs\requests'
    if ((Test-Path -LiteralPath $legacyRequests -PathType Container) -and
        -not (Test-AVWorkstationToolkitPathForReparsePoint -Root $legacy -Path $legacyRequests)) {
        foreach ($file in @(Get-ChildItem -LiteralPath $legacyRequests -File -Force -ErrorAction Stop)) {
            if ($file.Name -notmatch '^request-\d{8}-\d{6}-[a-f0-9]{8}\.(?:json|progress\.jsonl|result\.json|cancel|winget\.log)$') { continue }
            $limit = if ($file.Name.EndsWith('.json',[StringComparison]::OrdinalIgnoreCase)) { 2MB } else { 20MB }
            if (Copy-AVWorkstationToolkitLegacyFile -LegacyRoot $legacy -DataRoot $data -SourcePath $file.FullName -DestinationPath (Join-Path (Join-Path $data 'logs\requests') $file.Name) -MaximumBytes $limit) { $migrated++ }
        }
    }

    $legacyCache = Join-Path $legacy 'vendor-cache'
    if ((Test-Path -LiteralPath $legacyCache -PathType Container) -and
        -not (Test-AVWorkstationToolkitPathForReparsePoint -Root $legacy -Path $legacyCache)) {
        foreach ($metadataFile in @(Get-ChildItem -LiteralPath $legacyCache -File -Recurse -Filter '*.avinite.json' -Force -ErrorAction Stop)) {
            if ($metadataFile.Length -gt 65536 -or (Test-AVWorkstationToolkitPathForReparsePoint -Root $legacy -Path $metadataFile.FullName)) { continue }
            try { $metadata = Get-Content -LiteralPath $metadataFile.FullName -Raw | ConvertFrom-Json -ErrorAction Stop }
            catch { continue }
            $packageId = [string]$metadata.PackageId
            $version = [string]$metadata.Version
            $payloadName = [string]$metadata.FileName
            if ([int]$metadata.SchemaVersion -ne 1 -or $packageId -notmatch '^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$' -or
                $version -notmatch '^\d+(?:\.\d+){1,3}$' -or $payloadName -ne [IO.Path]::GetFileName($payloadName) -or
                [string]$metadata.Sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $metadataFile.Name -ne ($payloadName + '.avinite.json')) { continue }
            $expectedLegacyDirectory = [IO.Path]::GetFullPath((Join-Path (Join-Path $legacyCache $packageId) $version))
            if (-not $metadataFile.DirectoryName.Equals($expectedLegacyDirectory,[StringComparison]::OrdinalIgnoreCase)) { continue }
            $payloadSource = Join-Path $expectedLegacyDirectory $payloadName
            if (-not (Test-Path -LiteralPath $payloadSource -PathType Leaf) -or (Get-Item -LiteralPath $payloadSource -Force).Length -gt 4GB -or
                -not (Get-FileHash -LiteralPath $payloadSource -Algorithm SHA256).Hash.Equals([string]$metadata.Sha256,[StringComparison]::OrdinalIgnoreCase)) { continue }
            $newDirectory = Join-Path (Join-Path (Join-Path $data 'vendor-cache') $packageId) $version
            if (Copy-AVWorkstationToolkitLegacyFile -LegacyRoot $legacy -DataRoot $data -SourcePath $payloadSource -DestinationPath (Join-Path $newDirectory $payloadName) -MaximumBytes 4GB) { $migrated++ }
            if (Copy-AVWorkstationToolkitLegacyFile -LegacyRoot $legacy -DataRoot $data -SourcePath $metadataFile.FullName -DestinationPath (Join-Path $newDirectory ($payloadName + '.avworkstationtoolkit.json')) -MaximumBytes 65536) { $migrated++ }
        }
    }

    $marker = [ordered]@{
        SchemaVersion = 1
        LegacyProduct = 'AVinite'
        MigratedFiles = $migrated
        RuntimeCopied = $false
        ReportsAndSnapshots = 'RetainedInLegacyRoot'
    }
    $temporaryMarker = $markerPath + '.migration.tmp'
    try {
        $marker | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $temporaryMarker -Encoding UTF8
        Move-Item -LiteralPath $temporaryMarker -Destination $markerPath
    }
    finally { Remove-Item -LiteralPath $temporaryMarker -Force -ErrorAction SilentlyContinue }
    return [pscustomobject]@{ Status='Completed'; MigratedFiles=$migrated; LegacyEvidenceRetained=$true }
}

function New-AVWorkstationToolkitCaptureRegex {
    param(
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Field,
        [Parameter(Mandatory)][string]$CaptureName
    )

    if ([string]::IsNullOrWhiteSpace($Pattern) -or $Pattern.Length -gt 2048 -or $Pattern -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') {
        throw "$Field must be a bounded regular expression."
    }
    try {
        $regex = [regex]::new(
            $Pattern,
            [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant,
            [TimeSpan]::FromSeconds(2))
    }
    catch { throw "$Field is invalid: $($_.Exception.Message)" }
    if ($CaptureName -notin $regex.GetGroupNames()) {
        throw "$Field must contain a named $CaptureName capture group."
    }
    return $regex
}

function ConvertTo-AVWorkstationToolkitCatalogText {
    param(
        [AllowNull()]$Value,
        [Parameter(Mandatory)][string]$Field,
        [ValidateRange(1,2048)][int]$MaximumLength = 256,
        [switch]$AllowEmpty
    )

    $text = if ($null -eq $Value) { '' } else { [string]$Value }
    if ((-not $AllowEmpty -and [string]::IsNullOrWhiteSpace($text)) -or
        $text.Length -gt $MaximumLength -or $text -ne $text.Trim() -or
        $text -match '[\x00-\x1F\x7F]') {
        throw "$Field contains invalid text."
    }
    return $text
}

function ConvertTo-AVWorkstationToolkitCatalogEnumArray {
    param(
        [AllowNull()]$Value,
        [Parameter(Mandatory)][string[]]$Allowed,
        [Parameter(Mandatory)][string]$Field,
        [switch]$AllowEmpty
    )

    $values = @(if ($null -ne $Value) { $Value })
    if ((-not $AllowEmpty -and $values.Count -eq 0) -or $values.Count -gt 32) {
        throw "$Field must contain an approved value set."
    }
    $result = [System.Collections.Generic.List[string]]::new()
    foreach ($value in $values) {
        if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$value) -or [string]$value -notin $Allowed) {
            throw "$Field contains unsupported value '$value'."
        }
        if ([string]$value -in $result) { throw "$Field contains duplicate value '$value'." }
        $result.Add([string]$value)
    }
    return @($result)
}

function Get-AVWorkstationToolkitMetadataVerificationState {
    [CmdletBinding()]
    param(
        [AllowNull()][string]$VerifiedOn,
        [datetime]$AsOf = [datetime]::UtcNow.Date,
        [bool]$Quarantined = $false
    )

    if ([string]::IsNullOrWhiteSpace($VerifiedOn)) {
        return $(if ($Quarantined) { 'Quarantined' } else { 'VerificationRequired' })
    }

    $verifiedDate = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($VerifiedOn,'yyyy-MM-dd',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None,[ref]$verifiedDate)) {
        throw "VerifiedOn must use the ISO date format yyyy-MM-dd."
    }
    $referenceDate = $AsOf.Date
    if ($verifiedDate.Date -gt $referenceDate) { throw 'VerifiedOn cannot be in the future.' }
    if ($Quarantined) { return 'Quarantined' }

    $ageDays = [int]($referenceDate - $verifiedDate.Date).TotalDays
    if ($ageDays -lt 60) { return 'Current' }
    if ($ageDays -le 180) { return 'ReviewSoon' }
    return 'VerificationRequired'
}

function ConvertFrom-AVWorkstationToolkitCatalogMetadata {
    param(
        [AllowNull()]$Metadata,
        [Parameter(Mandatory)][int]$EntryIndex,
        [Parameter(Mandatory)][int]$SchemaVersion
    )

    $applicationTypes = @(
        'ControlSystem','DSPAudio','AVoIP','AudioNetworking','WirelessRF','AudioMeasurement',
        'LoudspeakerPrediction','AmplifierManagement','Conferencing','CameraPTZ','DisplayProjector',
        'DigitalSignage','DvLEDVideoWall','Intercom','MediaServerShowControl','BroadcastVideo',
        'LightingControl','FieldUtility','NetworkUtility','SerialUtility','UsbDiagnostic','EDIDHDCP',
        'FirmwareUtility','Development','Driver','Service','Server','WebApplication','EmbeddedSoftware',
        'LegacySupport'
    )
    $roles = @(
        'AVEngineer','FieldService','ControlProgramming','DSPEngineering','NetworkEngineering',
        'RFCoordination','Commissioning','DesignEngineering','BroadcastVideo','DigitalSignage',
        'LightingProgramming','SystemAdministration','Development'
    )
    $licensingModels = @('FREE','FREEMIUM','PAID','LICENSE','SUBSCRIPTION','HARDWARE-LICENSE','DEALER-LICENSE','UNKNOWN-COST')
    $downloadAccessValues = @('PUBLIC-DL','PUBLIC-PAGE','EMAIL-FORM','ACCOUNT','REGISTERED','DEALER','TRAINING','PORTAL','CONTACT','LICENSE-PORTAL','LEGACY-ARCHIVE','NO-DL','UNKNOWN-ACCESS')
    $validationMethods = @('Registry','WinGet','OfficialVersionPage','ParentProviderCatalog','ManualInventory','WebPresence','EmbeddedInterface','Unknown')
    $architectures = @('x86','x64','Arm64','Web','Embedded','Server','Unknown')
    $supportedOperatingSystems = @('Windows','macOS','Linux','iOS','Android','Web','Embedded','Server','Unknown')
    $distributionPolicies = @('Unknown','LinkOnly','VendorDownloadAllowed','Redistributable','PackageManagerOnly','ManualInstall','ReviewBeforeBundling')
    $workflowCategories = @(
        'NetworkCaptureTiming','DiscoveryReachability','ProtocolSocketTesting','SerialConsole','RemoteFileTransfer',
        'UsbConferencing','VideoEdidSignal','AudioMeasurementAoIP','AVoIP','WindowsDiagnostics',
        'FilesFirmwareComparison','ControlApis','ManufacturerPack','LegacyService'
    )
    $installationForms = @('Installed','Portable','MSI','EXE','ZIP','Store','WinGet','VendorPortal','WindowsInbox','Web','Embedded')
    $reviewTriggers = @('DomainChange','PublisherChange','ProductDiscontinued','DownloadStrategyChange','SignaturePolicyChange')
    $metadataKeys = @(
        'Vendor','ProductFamily','ApplicationType','ParentProviderId','Priority','Roles','DeploymentClass',
        'MaintenancePolicy','VersionRule','VersionCoupling','CurrentOrLegacy','LicensingModel',
        'DownloadAccess','DownloadDifficulty','Requirements','SystemImpact','Architecture','SupportedOS',
        'SideBySideSupported','OfficialDownloadUri','OfficialProductUri','ValidationMethod','Notes',
        'DistributionPolicy','WorkflowCategories','InstallationForms','Verification','Provenance'
    )

    if ($null -eq $Metadata) {
        if ($SchemaVersion -ge 3) { throw "External catalog entry $EntryIndex requires Metadata." }
        return [pscustomobject]@{
            Vendor=''; ProductFamily=''; ApplicationType=@(); ParentProviderId=''; Priority='P2'; Roles=@()
            DeploymentClass='ManualHandoff'; CatalogMaintenancePolicy='Manual'; VersionRule='Unknown'
            VersionCoupling='Unknown'; VersionCouplingTargetId=''; VersionCouplingNotes=''; CurrentOrLegacy='Unknown'
            LicensingModel=@('UNKNOWN-COST'); DownloadAccess=@('UNKNOWN-ACCESS'); DownloadDifficulty='HARD'
            RequiresVendorAccount=$null; RequiresDealerAccount=$null; RequiresTraining=$null; RequiresLicense=$null; RequiresSubscription=$null
            InstallsDriver=$null; InstallsService=$null; OpensListener=$null; FirmwareUtility=$null
            Architecture=@('Unknown'); SupportedOS=@('Unknown'); SideBySideSupported='Unknown'; OfficialDownloadUri=''; OfficialProductUri=''
            ValidationMethod=@('Unknown'); CatalogNotes=''; DistributionPolicy='Unknown'; WorkflowCategories=@(); InstallationForms=@()
            MetadataVerifiedOn=''; MetadataVerificationState='VerificationRequired'; MetadataReviewTriggers=@(); MetadataQuarantined=$false; MetadataQuarantineReason=''
            AuthoritativeDomain=''; ExpectedPublisher=''; SignatureValidation='Unknown'; VendorHashAvailability='Unknown'; DownloadStrategy='Unknown'
            CatalogTags=@('P2','UNKNOWN-COST','UNKNOWN-ACCESS','VerificationRequired')
        }
    }
    if ($Metadata -is [string] -or $null -eq $Metadata.PSObject) { throw "External catalog entry $EntryIndex Metadata must be an object." }
    $keys = @($Metadata.PSObject.Properties.Name)
    $unexpected = @($keys | Where-Object { $_ -notin $metadataKeys })
    if ($unexpected.Count -gt 0) {
        throw ("External catalog entry {0} Metadata contains unsupported keys: {1}" -f $EntryIndex,($unexpected -join ', '))
    }
    if ($SchemaVersion -ge 3) {
        # Awareness records stay concise. Missing policy fields normalize to
        # conservative/unknown values; operational providers override the
        # values that affect detection, acquisition, or maintenance.
        $required = @('Vendor','ApplicationType','OfficialProductUri')
        $missing = @($required | Where-Object { $_ -notin $keys })
        if ($missing.Count -gt 0) { throw ("External catalog entry {0} Metadata is missing keys: {1}" -f $EntryIndex,($missing -join ', ')) }
    }

    $vendor = ConvertTo-AVWorkstationToolkitCatalogText -Value (Get-AVWorkstationToolkitValue $Metadata 'Vendor' '') -Field "External catalog entry $EntryIndex Metadata.Vendor" -MaximumLength 128
    $productFamily = ConvertTo-AVWorkstationToolkitCatalogText -Value (Get-AVWorkstationToolkitValue $Metadata 'ProductFamily' '') -Field "External catalog entry $EntryIndex Metadata.ProductFamily" -MaximumLength 128 -AllowEmpty
    $types = @(ConvertTo-AVWorkstationToolkitCatalogEnumArray -Value (Get-AVWorkstationToolkitValue $Metadata 'ApplicationType' @()) -Allowed $applicationTypes -Field "External catalog entry $EntryIndex Metadata.ApplicationType")
    $priority = [string](Get-AVWorkstationToolkitValue $Metadata 'Priority' 'P2')
    if ($priority -notin @('P1','P2','UTILITY','DEV')) { throw "External catalog entry $EntryIndex Metadata.Priority is invalid." }
    $roleValues = @(ConvertTo-AVWorkstationToolkitCatalogEnumArray -Value (Get-AVWorkstationToolkitValue $Metadata 'Roles' @()) -Allowed $roles -Field "External catalog entry $EntryIndex Metadata.Roles" -AllowEmpty)
    $deploymentClass = [string](Get-AVWorkstationToolkitValue $Metadata 'DeploymentClass' $(if ($SchemaVersion -ge 3) { 'AwarenessOnly' } else { 'ManualHandoff' }))
    if ($deploymentClass -notin @('Managed','ManualHandoff','ParentProvider','InventoryOnly','AwarenessOnly','WebOnly','ServerOnly','Embedded')) {
        throw "External catalog entry $EntryIndex Metadata.DeploymentClass is invalid."
    }
    $catalogMaintenance = [string](Get-AVWorkstationToolkitValue $Metadata 'MaintenancePolicy' 'Manual')
    if ($catalogMaintenance -notin @('Managed','Manual','ProjectPinned','LegacyHold','VendorManaged','NotApplicable')) {
        throw "External catalog entry $EntryIndex Metadata.MaintenancePolicy is invalid."
    }
    $versionRule = [string](Get-AVWorkstationToolkitValue $Metadata 'VersionRule' 'Unknown')
    if ($versionRule -notin @('Latest','SameMajorMinor','ProjectPinned','ParentCatalog','InventoryOnly','EmbeddedFirmware','WebManaged','Unknown')) {
        throw "External catalog entry $EntryIndex Metadata.VersionRule is invalid."
    }
    $currentOrLegacy = [string](Get-AVWorkstationToolkitValue $Metadata 'CurrentOrLegacy' 'Unknown')
    if ($currentOrLegacy -notin @('Current','Legacy','Transition','CompatibilityUnverified','Discontinued','Unknown')) { throw "External catalog entry $EntryIndex Metadata.CurrentOrLegacy is invalid." }
    $licenseValues = @(ConvertTo-AVWorkstationToolkitCatalogEnumArray -Value (Get-AVWorkstationToolkitValue $Metadata 'LicensingModel' @('UNKNOWN-COST')) -Allowed $licensingModels -Field "External catalog entry $EntryIndex Metadata.LicensingModel")
    $accessValues = @(ConvertTo-AVWorkstationToolkitCatalogEnumArray -Value (Get-AVWorkstationToolkitValue $Metadata 'DownloadAccess' @('UNKNOWN-ACCESS')) -Allowed $downloadAccessValues -Field "External catalog entry $EntryIndex Metadata.DownloadAccess")
    $difficulty = [string](Get-AVWorkstationToolkitValue $Metadata 'DownloadDifficulty' 'HARD')
    if ($difficulty -notin @('EASY','MODERATE','RESTRICTED','HARD')) { throw "External catalog entry $EntryIndex Metadata.DownloadDifficulty is invalid." }
    $architectureValues = @(ConvertTo-AVWorkstationToolkitCatalogEnumArray -Value (Get-AVWorkstationToolkitValue $Metadata 'Architecture' @('Unknown')) -Allowed $architectures -Field "External catalog entry $EntryIndex Metadata.Architecture")
    $supportedOsValues = @(ConvertTo-AVWorkstationToolkitCatalogEnumArray -Value (Get-AVWorkstationToolkitValue $Metadata 'SupportedOS' @('Unknown')) -Allowed $supportedOperatingSystems -Field "External catalog entry $EntryIndex Metadata.SupportedOS")
    $sideBySide = [string](Get-AVWorkstationToolkitValue $Metadata 'SideBySideSupported' 'Unknown')
    if ($sideBySide -notin @('Yes','No','Unknown')) { throw "External catalog entry $EntryIndex Metadata.SideBySideSupported is invalid." }
    $validationValues = @(ConvertTo-AVWorkstationToolkitCatalogEnumArray -Value (Get-AVWorkstationToolkitValue $Metadata 'ValidationMethod' @('Unknown')) -Allowed $validationMethods -Field "External catalog entry $EntryIndex Metadata.ValidationMethod")
    $distributionPolicy = [string](Get-AVWorkstationToolkitValue $Metadata 'DistributionPolicy' 'Unknown')
    if ($distributionPolicy -notin $distributionPolicies) { throw "External catalog entry $EntryIndex Metadata.DistributionPolicy is invalid." }
    $workflowValues = @(ConvertTo-AVWorkstationToolkitCatalogEnumArray -Value (Get-AVWorkstationToolkitValue $Metadata 'WorkflowCategories' @()) -Allowed $workflowCategories -Field "External catalog entry $EntryIndex Metadata.WorkflowCategories" -AllowEmpty)
    $installationFormValues = @(ConvertTo-AVWorkstationToolkitCatalogEnumArray -Value (Get-AVWorkstationToolkitValue $Metadata 'InstallationForms' @()) -Allowed $installationForms -Field "External catalog entry $EntryIndex Metadata.InstallationForms" -AllowEmpty)
    $parentProviderId = ConvertTo-AVWorkstationToolkitCatalogText -Value (Get-AVWorkstationToolkitValue $Metadata 'ParentProviderId' '') -Field "External catalog entry $EntryIndex Metadata.ParentProviderId" -MaximumLength 128 -AllowEmpty
    if (-not [string]::IsNullOrWhiteSpace($parentProviderId) -and $parentProviderId -notmatch '^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$') {
        throw "External catalog entry $EntryIndex Metadata.ParentProviderId is invalid."
    }

    $couplingMode = 'Independent'
    $couplingTarget = ''
    $couplingNotes = ''
    $coupling = Get-AVWorkstationToolkitValue $Metadata 'VersionCoupling' $null
    if ($null -ne $coupling) {
        $couplingKeys = @($coupling.PSObject.Properties.Name)
        $unexpectedCoupling = @($couplingKeys | Where-Object { $_ -notin @('Mode','PackageId','Notes') })
        if ($unexpectedCoupling.Count -gt 0) { throw "External catalog entry $EntryIndex Metadata.VersionCoupling contains unsupported keys." }
        $couplingMode = [string](Get-AVWorkstationToolkitValue $coupling 'Mode' 'Independent')
        if ($couplingMode -notin @('Independent','ParentProvider','SameMajorMinor','ProjectPinned','FirmwarePaired','Unknown')) {
            throw "External catalog entry $EntryIndex Metadata.VersionCoupling.Mode is invalid."
        }
        $couplingTarget = ConvertTo-AVWorkstationToolkitCatalogText -Value (Get-AVWorkstationToolkitValue $coupling 'PackageId' '') -Field "External catalog entry $EntryIndex Metadata.VersionCoupling.PackageId" -MaximumLength 128 -AllowEmpty
        $couplingNotes = ConvertTo-AVWorkstationToolkitCatalogText -Value (Get-AVWorkstationToolkitValue $coupling 'Notes' '') -Field "External catalog entry $EntryIndex Metadata.VersionCoupling.Notes" -MaximumLength 512 -AllowEmpty
        if (-not [string]::IsNullOrWhiteSpace($couplingTarget) -and $couplingTarget -notmatch '^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$') {
            throw "External catalog entry $EntryIndex Metadata.VersionCoupling.PackageId is invalid."
        }
    }

    $requirements = Get-AVWorkstationToolkitValue $Metadata 'Requirements' $null
    $requirementValues = @{}
    foreach ($name in @('RequiresVendorAccount','RequiresDealerAccount','RequiresTraining','RequiresLicense','RequiresSubscription')) { $requirementValues[$name] = $null }
    if ($null -ne $requirements) {
        $requirementKeys = @($requirements.PSObject.Properties.Name)
        $unexpectedRequirements = @($requirementKeys | Where-Object { $_ -notin @($requirementValues.Keys) })
        if ($unexpectedRequirements.Count -gt 0) { throw "External catalog entry $EntryIndex Metadata.Requirements contains unsupported keys." }
        foreach ($name in $requirementKeys) {
            $value = Get-AVWorkstationToolkitValue $requirements $name $null
            if ($null -ne $value -and $value -isnot [bool]) { throw "External catalog entry $EntryIndex Metadata.Requirements.$name must be Boolean or null." }
            $requirementValues[$name] = $value
        }
    }

    $impact = Get-AVWorkstationToolkitValue $Metadata 'SystemImpact' $null
    $impactValues = @{}
    foreach ($name in @('InstallsDriver','InstallsService','OpensListener','FirmwareUtility')) { $impactValues[$name] = $null }
    if ($null -ne $impact) {
        $impactKeys = @($impact.PSObject.Properties.Name)
        $unexpectedImpact = @($impactKeys | Where-Object { $_ -notin @($impactValues.Keys) })
        if ($unexpectedImpact.Count -gt 0) { throw "External catalog entry $EntryIndex Metadata.SystemImpact contains unsupported keys." }
        foreach ($name in $impactKeys) {
            $value = Get-AVWorkstationToolkitValue $impact $name $null
            if ($null -ne $value -and $value -isnot [bool]) { throw "External catalog entry $EntryIndex Metadata.SystemImpact.$name must be Boolean or null." }
            $impactValues[$name] = $value
        }
    }

    $officialProductUri = [string](Get-AVWorkstationToolkitValue $Metadata 'OfficialProductUri' '')
    if (-not [string]::IsNullOrWhiteSpace($officialProductUri)) { $officialProductUri = Assert-AVWorkstationToolkitHttpsUri -Value $officialProductUri -Field "External catalog entry $EntryIndex Metadata.OfficialProductUri" }
    elseif ($SchemaVersion -ge 3) { throw "External catalog entry $EntryIndex requires Metadata.OfficialProductUri." }
    $officialDownloadUri = [string](Get-AVWorkstationToolkitValue $Metadata 'OfficialDownloadUri' '')
    if (-not [string]::IsNullOrWhiteSpace($officialDownloadUri)) { $officialDownloadUri = Assert-AVWorkstationToolkitHttpsUri -Value $officialDownloadUri -Field "External catalog entry $EntryIndex Metadata.OfficialDownloadUri" }
    $catalogNotes = ConvertTo-AVWorkstationToolkitCatalogText -Value (Get-AVWorkstationToolkitValue $Metadata 'Notes' '') -Field "External catalog entry $EntryIndex Metadata.Notes" -MaximumLength 1024 -AllowEmpty

    $verifiedOn = ''
    $reviewTriggerValues = @()
    $quarantined = $false
    $quarantineReason = ''
    $verification = Get-AVWorkstationToolkitValue $Metadata 'Verification' $null
    if ($null -ne $verification) {
        $verificationKeys = @($verification.PSObject.Properties.Name)
        $unexpectedVerification = @($verificationKeys | Where-Object { $_ -notin @('VerifiedOn','ReviewTriggers','Quarantined','QuarantineReason') })
        if ($unexpectedVerification.Count -gt 0) { throw "External catalog entry $EntryIndex Metadata.Verification contains unsupported keys." }
        $verifiedOn = ConvertTo-AVWorkstationToolkitCatalogText -Value (Get-AVWorkstationToolkitValue $verification 'VerifiedOn' '') -Field "External catalog entry $EntryIndex Metadata.Verification.VerifiedOn" -MaximumLength 10 -AllowEmpty
        $reviewTriggerValues = @(ConvertTo-AVWorkstationToolkitCatalogEnumArray -Value (Get-AVWorkstationToolkitValue $verification 'ReviewTriggers' @()) -Allowed $reviewTriggers -Field "External catalog entry $EntryIndex Metadata.Verification.ReviewTriggers" -AllowEmpty)
        $quarantinedValue = Get-AVWorkstationToolkitValue $verification 'Quarantined' $false
        if ($quarantinedValue -isnot [bool]) { throw "External catalog entry $EntryIndex Metadata.Verification.Quarantined must be Boolean." }
        $quarantined = [bool]$quarantinedValue
        $quarantineReason = ConvertTo-AVWorkstationToolkitCatalogText -Value (Get-AVWorkstationToolkitValue $verification 'QuarantineReason' '') -Field "External catalog entry $EntryIndex Metadata.Verification.QuarantineReason" -MaximumLength 512 -AllowEmpty
        if ($quarantined -and [string]::IsNullOrWhiteSpace($quarantineReason)) { throw "External catalog entry $EntryIndex quarantined metadata requires a reason." }
    }
    $verificationState = Get-AVWorkstationToolkitMetadataVerificationState -VerifiedOn $verifiedOn -Quarantined $quarantined

    $authoritativeDomain = ''
    $expectedPublisher = ''
    $signatureValidation = 'Unknown'
    $vendorHashAvailability = 'Unknown'
    $downloadStrategy = 'Unknown'
    $provenance = Get-AVWorkstationToolkitValue $Metadata 'Provenance' $null
    if ($null -ne $provenance) {
        $provenanceKeys = @($provenance.PSObject.Properties.Name)
        $unexpectedProvenance = @($provenanceKeys | Where-Object { $_ -notin @('AuthoritativeDomain','ExpectedPublisher','SignatureValidation','VendorHashAvailability','DownloadStrategy') })
        if ($unexpectedProvenance.Count -gt 0) { throw "External catalog entry $EntryIndex Metadata.Provenance contains unsupported keys." }
        $authoritativeDomain = (ConvertTo-AVWorkstationToolkitCatalogText -Value (Get-AVWorkstationToolkitValue $provenance 'AuthoritativeDomain' '') -Field "External catalog entry $EntryIndex Metadata.Provenance.AuthoritativeDomain" -MaximumLength 253 -AllowEmpty).ToLowerInvariant()
        if (-not [string]::IsNullOrWhiteSpace($authoritativeDomain) -and $authoritativeDomain -notmatch '^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$') {
            throw "External catalog entry $EntryIndex Metadata.Provenance.AuthoritativeDomain is invalid."
        }
        $expectedPublisher = ConvertTo-AVWorkstationToolkitCatalogText -Value (Get-AVWorkstationToolkitValue $provenance 'ExpectedPublisher' '') -Field "External catalog entry $EntryIndex Metadata.Provenance.ExpectedPublisher" -MaximumLength 256 -AllowEmpty
        $signatureValidation = [string](Get-AVWorkstationToolkitValue $provenance 'SignatureValidation' 'Unknown')
        if ($signatureValidation -notin @('Required','Optional','NotApplicable','Unknown')) { throw "External catalog entry $EntryIndex Metadata.Provenance.SignatureValidation is invalid." }
        $vendorHashAvailability = [string](Get-AVWorkstationToolkitValue $provenance 'VendorHashAvailability' 'Unknown')
        if ($vendorHashAvailability -notin @('Available','Unavailable','Unknown')) { throw "External catalog entry $EntryIndex Metadata.Provenance.VendorHashAvailability is invalid." }
        $downloadStrategy = [string](Get-AVWorkstationToolkitValue $provenance 'DownloadStrategy' 'Unknown')
        if ($downloadStrategy -notin @('PackageManager','VendorPage','DirectVendor','AuthenticatedVendor','ParentProvider','Bundled','None','Unknown')) { throw "External catalog entry $EntryIndex Metadata.Provenance.DownloadStrategy is invalid." }
        foreach ($sourceUri in @($officialProductUri,$officialDownloadUri)) {
            if (-not [string]::IsNullOrWhiteSpace($sourceUri) -and -not [string]::IsNullOrWhiteSpace($authoritativeDomain)) {
                $sourceHost = ([uri]$sourceUri).DnsSafeHost.ToLowerInvariant()
                if ($sourceHost -ne $authoritativeDomain -and -not $sourceHost.EndsWith('.' + $authoritativeDomain,[StringComparison]::OrdinalIgnoreCase)) {
                    throw "External catalog entry $EntryIndex official source is outside Metadata.Provenance.AuthoritativeDomain."
                }
            }
        }
    }

    $tags = [System.Collections.Generic.List[string]]::new()
    $tags.Add($priority)
    foreach ($value in @($licenseValues + $accessValues)) { if ($value -notin $tags) { $tags.Add($value) } }
    if ($impactValues.InstallsDriver -eq $true) { $tags.Add('DRIVER') }
    if ($impactValues.InstallsService -eq $true) { $tags.Add('SERVICE') }
    if ($deploymentClass -eq 'ServerOnly' -or 'Server' -in $types) { $tags.Add('SERVER') }
    if ($deploymentClass -eq 'WebOnly' -or 'WebApplication' -in $types) { $tags.Add('WEB') }
    if ($currentOrLegacy -eq 'Legacy') { $tags.Add('LEGACY') }
    if ($currentOrLegacy -eq 'Transition') { $tags.Add('TRANSITION') }
    if ($currentOrLegacy -eq 'CompatibilityUnverified') { $tags.Add('COMPATIBILITY-UNVERIFIED') }
    if ($currentOrLegacy -eq 'Discontinued') { $tags.Add('DISCONTINUED') }
    foreach ($value in $workflowValues) { if ($value -notin $tags) { $tags.Add($value) } }
    if ($verificationState -notin $tags) { $tags.Add($verificationState) }

    return [pscustomobject]@{
        Vendor=$vendor; ProductFamily=$productFamily; ApplicationType=@($types); ParentProviderId=$parentProviderId
        Priority=$priority; Roles=@($roleValues); DeploymentClass=$deploymentClass; CatalogMaintenancePolicy=$catalogMaintenance
        VersionRule=$versionRule; VersionCoupling=$couplingMode; VersionCouplingTargetId=$couplingTarget; VersionCouplingNotes=$couplingNotes
        CurrentOrLegacy=$currentOrLegacy; LicensingModel=@($licenseValues); DownloadAccess=@($accessValues); DownloadDifficulty=$difficulty
        RequiresVendorAccount=$requirementValues.RequiresVendorAccount; RequiresDealerAccount=$requirementValues.RequiresDealerAccount
        RequiresTraining=$requirementValues.RequiresTraining; RequiresLicense=$requirementValues.RequiresLicense
        RequiresSubscription=$requirementValues.RequiresSubscription; InstallsDriver=$impactValues.InstallsDriver
        InstallsService=$impactValues.InstallsService; OpensListener=$impactValues.OpensListener; FirmwareUtility=$impactValues.FirmwareUtility
        Architecture=@($architectureValues); SupportedOS=@($supportedOsValues); SideBySideSupported=$sideBySide; OfficialDownloadUri=$officialDownloadUri
        OfficialProductUri=$officialProductUri; ValidationMethod=@($validationValues); CatalogNotes=$catalogNotes
        DistributionPolicy=$distributionPolicy; WorkflowCategories=@($workflowValues); InstallationForms=@($installationFormValues)
        MetadataVerifiedOn=$verifiedOn; MetadataVerificationState=$verificationState; MetadataReviewTriggers=@($reviewTriggerValues)
        MetadataQuarantined=$quarantined; MetadataQuarantineReason=$quarantineReason; AuthoritativeDomain=$authoritativeDomain
        ExpectedPublisher=$expectedPublisher; SignatureValidation=$signatureValidation; VendorHashAvailability=$vendorHashAvailability
        DownloadStrategy=$downloadStrategy; CatalogTags=@($tags | Sort-Object -Unique)
    }
}

function ConvertFrom-AVWorkstationToolkitExternalCatalogJson {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Json)

    try { $document = $Json | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "External application catalog is not valid JSON: $($_.Exception.Message)" }
    if ($null -eq $document) { throw 'External application catalog is empty.' }

    $rootKeys = @($document.PSObject.Properties.Name)
    $unexpectedRootKeys = @($rootKeys | Where-Object { $_ -notin @('SchemaVersion','Packages') })
    if ($unexpectedRootKeys.Count -gt 0) {
        throw ('External application catalog contains unsupported keys: {0}' -f ($unexpectedRootKeys -join ', '))
    }
    if ('SchemaVersion' -notin $rootKeys -or $document.SchemaVersion -isnot [int] -or [int]$document.SchemaVersion -notin @(1,2,3)) {
        throw 'External application catalog SchemaVersion must be the integer 1, 2, or 3.'
    }
    if ('Packages' -notin $rootKeys) { throw 'External application catalog requires Packages.' }
    $schemaVersion = [int]$document.SchemaVersion

    $allowedPackageKeys = @('Profile','Name','Id','Risk','Note','Deployment','Maintenance','KnownVersion','Detection','Release','Delivery','Metadata')
    $packages = [System.Collections.Generic.List[object]]::new()
    $index = 0
    foreach ($raw in @($document.Packages)) {
        if ($null -eq $raw) { throw "External catalog entry $index is null." }
        $packageKeys = @($raw.PSObject.Properties.Name)
        $unexpectedPackageKeys = @($packageKeys | Where-Object { $_ -notin $allowedPackageKeys })
        if ($unexpectedPackageKeys.Count -gt 0) {
            throw ("External catalog entry {0} contains unsupported keys: {1}" -f $index,($unexpectedPackageKeys -join ', '))
        }
        $requiredPackageKeys = if ($schemaVersion -ge 3) { @('Name','Id','Note','Metadata') } else { @('Profile','Name','Id','Risk','Note','Deployment','Maintenance','KnownVersion','Detection','Release','Delivery') }
        $missingPackageKeys = @($requiredPackageKeys | Where-Object { $_ -notin $packageKeys })
        if ($missingPackageKeys.Count -gt 0) {
            throw ("External catalog entry {0} is missing keys: {1}" -f $index,($missingPackageKeys -join ', '))
        }

        $metadata = ConvertFrom-AVWorkstationToolkitCatalogMetadata -Metadata (Get-AVWorkstationToolkitValue $raw 'Metadata' $null) -EntryIndex $index -SchemaVersion $schemaVersion
        $rawDetection = Get-AVWorkstationToolkitValue $raw 'Detection' ([pscustomobject]@{ Mode='None' })
        $rawRelease = Get-AVWorkstationToolkitValue $raw 'Release' ([pscustomobject]@{ Mode='InventoryOnly'; Channel='Catalog awareness' })
        $rawDelivery = Get-AVWorkstationToolkitValue $raw 'Delivery' ([pscustomobject]@{ Mode='Awareness' })

        $detectionKeys = @($rawDetection.PSObject.Properties.Name)
        $unexpectedDetectionKeys = @($detectionKeys | Where-Object { $_ -notin @('Mode','RegistryDisplayNamePattern','RegistryVersionPattern','VersionPolicy') })
        if ($unexpectedDetectionKeys.Count -gt 0) {
            throw ("External catalog entry {0} detection contains unsupported keys: {1}" -f $index,($unexpectedDetectionKeys -join ', '))
        }
        $defaultDetectionMode = if ('RegistryDisplayNamePattern' -in $detectionKeys) { 'Registry' } elseif ($schemaVersion -ge 3) { 'None' } else { 'Registry' }
        $detectionMode = [string](Get-AVWorkstationToolkitValue $rawDetection 'Mode' $defaultDetectionMode)
        if ($detectionMode -notin @('Registry','None')) { throw "External catalog entry $index has invalid Detection.Mode '$detectionMode'." }
        $displayNamePattern = ''
        $registryVersionPattern = [string](Get-AVWorkstationToolkitValue $rawDetection 'RegistryVersionPattern' '')
        $versionPolicy = 'None'
        if ($detectionMode -eq 'Registry') {
            foreach ($required in @('RegistryDisplayNamePattern','VersionPolicy')) {
                if ($required -notin $detectionKeys) { throw "External catalog entry $index requires Detection.$required." }
            }
            $displayNamePattern = [string]$rawDetection.RegistryDisplayNamePattern
            if ([string]::IsNullOrWhiteSpace($displayNamePattern) -or $displayNamePattern.Length -gt 1024) {
                throw "External catalog entry $index has an invalid registry display-name pattern."
            }
            try {
                [void][regex]::new($displayNamePattern,[Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant,[TimeSpan]::FromSeconds(2))
            }
            catch { throw "External catalog entry $index registry display-name pattern is invalid: $($_.Exception.Message)" }
            if (-not [string]::IsNullOrWhiteSpace($registryVersionPattern)) {
                [void](New-AVWorkstationToolkitVersionRegex -Pattern $registryVersionPattern -Field "External catalog entry $index registry version pattern")
            }
            $versionPolicy = [string]$rawDetection.VersionPolicy
            if ($versionPolicy -notin @('AtLeast','SameMajorMinor')) {
                throw "External catalog entry $index has invalid Detection.VersionPolicy '$versionPolicy'."
            }
        }
        elseif (@($detectionKeys | Where-Object { $_ -ne 'Mode' }).Count -gt 0) {
            throw "External catalog entry $index Detection.None cannot contain registry matching fields."
        }

        $releaseKeys = @($rawRelease.PSObject.Properties.Name)
        $releaseMode = [string](Get-AVWorkstationToolkitValue $rawRelease 'Mode' $(if ($schemaVersion -ge 3) { 'InventoryOnly' } else { 'VendorPage' }))
        if ($releaseMode -notin @('VendorPage','ParentCatalog','InventoryOnly')) {
            throw "External catalog entry $index has invalid Release.Mode '$releaseMode'."
        }
        $allowedReleaseKeys = if ($releaseMode -eq 'VendorPage') { @('Mode','Uri','VersionPattern','Channel') } else { @('Mode','Channel') }
        $unexpectedReleaseKeys = @($releaseKeys | Where-Object { $_ -notin $allowedReleaseKeys })
        if ($unexpectedReleaseKeys.Count -gt 0) {
            throw ("External catalog entry {0} release contains unsupported keys: {1}" -f $index,($unexpectedReleaseKeys -join ', '))
        }
        if ('Channel' -notin $releaseKeys) { throw "External catalog entry $index requires Release.Channel." }
        $knownVersion = [string](Get-AVWorkstationToolkitValue $raw 'KnownVersion' '')
        $releaseUri = ''
        $releasePattern = ''
        if ($releaseMode -eq 'VendorPage') {
            foreach ($required in @('Uri','VersionPattern')) {
                if ($required -notin $releaseKeys) { throw "External catalog entry $index requires Release.$required." }
            }
            if ([string]::IsNullOrWhiteSpace($knownVersion)) { throw "External catalog entry $index requires KnownVersion for a vendor release page." }
            [void](ConvertTo-AVWorkstationToolkitVersion -Value $knownVersion)
            $releaseUri = Assert-AVWorkstationToolkitHttpsUri -Value ([string]$rawRelease.Uri) -Field "External catalog entry $index release URI"
            $releasePattern = [string]$rawRelease.VersionPattern
            [void](New-AVWorkstationToolkitVersionRegex -Pattern $releasePattern -Field "External catalog entry $index release version pattern")
        }
        elseif (-not [string]::IsNullOrWhiteSpace($knownVersion)) {
            [void](ConvertTo-AVWorkstationToolkitVersion -Value $knownVersion)
        }
        $releaseChannel = [string]$rawRelease.Channel
        if ([string]::IsNullOrWhiteSpace($releaseChannel) -or $releaseChannel.Length -gt 64 -or $releaseChannel -ne $releaseChannel.Trim() -or $releaseChannel -match '[\x00-\x1F\x7F]') {
            throw "External catalog entry $index has an invalid release channel."
        }

        $deliveryKeys = @($rawDelivery.PSObject.Properties.Name)
        if ('Mode' -notin $deliveryKeys) { throw "External catalog entry $index requires Delivery.Mode." }
        $deliveryMode = [string]$rawDelivery.Mode
        if ($deliveryMode -notin @('VendorPage','DirectDownload','AuthenticatedSftp','ParentProvider','Bundled','Awareness','InventoryOnly')) {
            throw "External catalog entry $index has invalid Delivery.Mode '$deliveryMode'."
        }
        $allowedDeliveryKeys = switch ($deliveryMode) {
            'VendorPage'       { @('Mode','Uri') }
            'DirectDownload'   { @('Mode','Uri','DownloadUriPattern','AllowedHosts','PublisherPattern','MaxBytes') }
            'AuthenticatedSftp'{ @('Mode','Host','Port','CatalogUri','RemoteRoot','AllowedProductIds','PublisherPattern','MaxBytes') }
            'ParentProvider'   { @('Mode','ProductId') }
            'Bundled'          { @('Mode','Uri','RelativePath','Sha256','PublisherSubject') }
            'Awareness'        { @('Mode') }
            'InventoryOnly'    { @('Mode') }
        }
        $unexpectedDeliveryKeys = @($deliveryKeys | Where-Object { $_ -notin $allowedDeliveryKeys })
        if ($unexpectedDeliveryKeys.Count -gt 0) {
            throw ("External catalog entry {0} delivery contains unsupported keys for {1}: {2}" -f $index,$deliveryMode,($unexpectedDeliveryKeys -join ', '))
        }
        $deliveryUri = [string](Get-AVWorkstationToolkitValue $rawDelivery 'Uri' '')
        if (-not [string]::IsNullOrWhiteSpace($deliveryUri)) {
            $deliveryUri = Assert-AVWorkstationToolkitHttpsUri -Value $deliveryUri -Field "External catalog entry $index delivery URI"
        }
        $payloadRelativePath = [string](Get-AVWorkstationToolkitValue $rawDelivery 'RelativePath' '')
        $payloadSha256 = [string](Get-AVWorkstationToolkitValue $rawDelivery 'Sha256' '')
        $payloadPublisher = [string](Get-AVWorkstationToolkitValue $rawDelivery 'PublisherSubject' '')
        $downloadUriPattern = [string](Get-AVWorkstationToolkitValue $rawDelivery 'DownloadUriPattern' '')
        $allowedHosts = @()
        $publisherPattern = [string](Get-AVWorkstationToolkitValue $rawDelivery 'PublisherPattern' '')
        $maxBytes = [int64]0
        $sftpHost = [string](Get-AVWorkstationToolkitValue $rawDelivery 'Host' '')
        $sftpPort = 0
        $sftpCatalogUri = [string](Get-AVWorkstationToolkitValue $rawDelivery 'CatalogUri' '')
        $sftpRemoteRoot = [string](Get-AVWorkstationToolkitValue $rawDelivery 'RemoteRoot' '')
        $sftpProductIds = @()
        $deliveryProductId = [string](Get-AVWorkstationToolkitValue $rawDelivery 'ProductId' '')
        if ($deliveryMode -eq 'VendorPage' -and [string]::IsNullOrWhiteSpace($deliveryUri)) {
            throw "External catalog entry $index vendor delivery requires Delivery.Uri."
        }
        if ($deliveryMode -eq 'Bundled') {
            if ([string]::IsNullOrWhiteSpace($payloadRelativePath) -or $payloadRelativePath.Length -gt 512 -or
                [IO.Path]::IsPathRooted($payloadRelativePath) -or $payloadRelativePath -match '\\' -or
                @($payloadRelativePath.Split('/') | Where-Object { $_ -in @('','.', '..') }).Count -gt 0) {
                throw "External catalog entry $index has an unsafe bundled payload relative path."
            }
            if ($payloadSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
                throw "External catalog entry $index bundled payload requires a SHA-256 hash."
            }
        }
        if ($deliveryMode -eq 'DirectDownload') {
            foreach ($required in @('Uri','DownloadUriPattern','AllowedHosts','PublisherPattern','MaxBytes')) {
                if ($required -notin $deliveryKeys) { throw "External catalog entry $index requires Delivery.$required." }
            }
            [void](New-AVWorkstationToolkitCaptureRegex -Pattern $downloadUriPattern -Field "External catalog entry $index download URI pattern" -CaptureName 'Uri')
            [void](New-AVWorkstationToolkitCaptureRegex -Pattern $downloadUriPattern -Field "External catalog entry $index download version pattern" -CaptureName 'Version')
            $allowedHosts = @($rawDelivery.AllowedHosts | ForEach-Object { [string]$_ })
            if ($allowedHosts.Count -eq 0 -or $allowedHosts.Count -gt 16) { throw "External catalog entry $index requires one to sixteen allowed download hosts." }
            foreach ($hostName in $allowedHosts) {
                if ($hostName -notmatch '^(?=.{1,253}$)(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(?:\.(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?))*$') {
                    throw "External catalog entry $index contains an invalid allowed download host."
                }
            }
        }
        if ($deliveryMode -eq 'AuthenticatedSftp') {
            foreach ($required in @('Host','Port','CatalogUri','RemoteRoot','AllowedProductIds','PublisherPattern','MaxBytes')) {
                if ($required -notin $deliveryKeys) { throw "External catalog entry $index requires Delivery.$required." }
            }
            if ($sftpHost -notmatch '^(?=.{1,253}$)(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(?:\.(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?))*$') {
                throw "External catalog entry $index contains an invalid SFTP host."
            }
            $sftpPort = [int]$rawDelivery.Port
            if ($sftpPort -lt 1 -or $sftpPort -gt 65535) { throw "External catalog entry $index contains an invalid SFTP port." }
            $sftpCatalogUri = Assert-AVWorkstationToolkitHttpsUri -Value $sftpCatalogUri -Field "External catalog entry $index SFTP catalog URI"
            if ($sftpRemoteRoot -notmatch '^/[A-Za-z0-9._/-]+$' -or $sftpRemoteRoot -match '(?:^|/)\.\.(?:/|$)') {
                throw "External catalog entry $index contains an invalid SFTP remote root."
            }
            $sftpProductIds = @($rawDelivery.AllowedProductIds | ForEach-Object { [string]$_ })
            if ($sftpProductIds.Count -eq 0 -or $sftpProductIds.Count -gt 64 -or @($sftpProductIds | Where-Object { $_ -notmatch '^\d{1,8}$' }).Count -gt 0) {
                throw "External catalog entry $index contains an invalid SFTP product allowlist."
            }
            if (@($sftpProductIds | Sort-Object -Unique).Count -ne $sftpProductIds.Count) {
                throw "External catalog entry $index contains duplicate SFTP product IDs."
            }
        }
        if ($deliveryMode -eq 'ParentProvider' -and $deliveryProductId -notmatch '^\d{1,8}$') {
            throw "External catalog entry $index ParentProvider delivery requires a numeric ProductId."
        }
        if ($deliveryMode -in @('DirectDownload','AuthenticatedSftp')) {
            if ([string]::IsNullOrWhiteSpace($publisherPattern) -or $publisherPattern.Length -gt 512) {
                throw "External catalog entry $index requires a bounded publisher pattern."
            }
            try { [void][regex]::new($publisherPattern,[Text.RegularExpressions.RegexOptions]::IgnoreCase,[TimeSpan]::FromSeconds(2)) }
            catch { throw "External catalog entry $index publisher pattern is invalid: $($_.Exception.Message)" }
            $maxText = [string]$rawDelivery.MaxBytes
            if (-not [int64]::TryParse($maxText,[ref]$maxBytes) -or $maxBytes -lt 1048576 -or $maxBytes -gt 4294967296) {
                throw "External catalog entry $index MaxBytes must be from 1 MiB through 4 GiB."
            }
        }
        if ($payloadPublisher.Length -gt 512 -or $payloadPublisher -match '[\x00-\x1F\x7F]') {
            throw "External catalog entry $index has an invalid publisher subject."
        }

        $packages.Add([pscustomobject]@{
            Profile = [string](Get-AVWorkstationToolkitValue $raw 'Profile' 'Optional')
            Name = [string]$raw.Name
            Id = [string]$raw.Id
            Risk = [string](Get-AVWorkstationToolkitValue $raw 'Risk' 'None')
            Note = [string]$raw.Note
            Deployment = [string](Get-AVWorkstationToolkitValue $raw 'Deployment' 'ManualHold')
            Maintenance = [string](Get-AVWorkstationToolkitValue $raw 'Maintenance' 'Hold')
            # External packages have no WinGet installer vector; the property exists so every catalog
            # record has one uniform shape, and InstallerMode is not an authorable external field.
            InstallerMode = 'Silent'
            Provider = 'External'
            KnownVersion = $knownVersion
            DetectionMode = $detectionMode
            DetectionDisplayNamePattern = $displayNamePattern
            DetectionVersionPattern = $registryVersionPattern
            DetectionVersionPolicy = $versionPolicy
            ReleaseMode = $releaseMode
            ReleaseUri = $releaseUri
            ReleaseVersionPattern = $releasePattern
            ReleaseChannel = $releaseChannel
            DeliveryMode = $deliveryMode
            DeliveryUri = $deliveryUri
            PayloadRelativePath = $payloadRelativePath
            PayloadSha256 = $payloadSha256.ToUpperInvariant()
            PayloadPublisher = $payloadPublisher
            DownloadUriPattern = $downloadUriPattern
            DownloadAllowedHosts = @($allowedHosts)
            DownloadPublisherPattern = $publisherPattern
            DownloadMaxBytes = $maxBytes
            SftpHost = $sftpHost
            SftpPort = $sftpPort
            SftpCatalogUri = $sftpCatalogUri
            SftpRemoteRoot = $sftpRemoteRoot
            SftpAllowedProductIds = @($sftpProductIds)
            DeliveryProviderId = [string]$metadata.ParentProviderId
            DeliveryProductId = $deliveryProductId
            Vendor = [string]$metadata.Vendor
            ProductFamily = [string]$metadata.ProductFamily
            ApplicationType = @($metadata.ApplicationType)
            ParentProviderId = [string]$metadata.ParentProviderId
            Priority = [string]$metadata.Priority
            Roles = @($metadata.Roles)
            DeploymentClass = [string]$metadata.DeploymentClass
            CatalogMaintenancePolicy = [string]$metadata.CatalogMaintenancePolicy
            VersionRule = [string]$metadata.VersionRule
            VersionCoupling = [string]$metadata.VersionCoupling
            VersionCouplingTargetId = [string]$metadata.VersionCouplingTargetId
            VersionCouplingNotes = [string]$metadata.VersionCouplingNotes
            CurrentOrLegacy = [string]$metadata.CurrentOrLegacy
            LicensingModel = @($metadata.LicensingModel)
            DownloadAccess = @($metadata.DownloadAccess)
            DownloadDifficulty = [string]$metadata.DownloadDifficulty
            RequiresVendorAccount = $metadata.RequiresVendorAccount
            RequiresDealerAccount = $metadata.RequiresDealerAccount
            RequiresTraining = $metadata.RequiresTraining
            RequiresLicense = $metadata.RequiresLicense
            RequiresSubscription = $metadata.RequiresSubscription
            InstallsDriver = $metadata.InstallsDriver
            InstallsService = $metadata.InstallsService
            OpensListener = $metadata.OpensListener
            FirmwareUtility = $metadata.FirmwareUtility
            Architecture = @($metadata.Architecture)
            SupportedOS = @($metadata.SupportedOS)
            SideBySideSupported = [string]$metadata.SideBySideSupported
            OfficialDownloadUri = [string]$metadata.OfficialDownloadUri
            OfficialProductUri = [string]$metadata.OfficialProductUri
            ValidationMethod = @($metadata.ValidationMethod)
            CatalogNotes = [string]$metadata.CatalogNotes
            DistributionPolicy = [string]$metadata.DistributionPolicy
            WorkflowCategories = @($metadata.WorkflowCategories)
            InstallationForms = @($metadata.InstallationForms)
            MetadataVerifiedOn = [string]$metadata.MetadataVerifiedOn
            MetadataVerificationState = [string]$metadata.MetadataVerificationState
            MetadataReviewTriggers = @($metadata.MetadataReviewTriggers)
            MetadataQuarantined = [bool]$metadata.MetadataQuarantined
            MetadataQuarantineReason = [string]$metadata.MetadataQuarantineReason
            AuthoritativeDomain = [string]$metadata.AuthoritativeDomain
            ExpectedPublisher = [string]$metadata.ExpectedPublisher
            SignatureValidation = [string]$metadata.SignatureValidation
            VendorHashAvailability = [string]$metadata.VendorHashAvailability
            DownloadStrategy = [string]$metadata.DownloadStrategy
            CatalogTags = @($metadata.CatalogTags)
        }) | Out-Null
        $index++
    }
    $byId = [System.Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($package in $packages) {
        if ($byId.ContainsKey([string]$package.Id)) { throw "External catalog contains duplicate package ID '$($package.Id)'." }
        $byId.Add([string]$package.Id,$package)
    }
    foreach ($package in $packages) {
        if (-not [string]::IsNullOrWhiteSpace([string]$package.ParentProviderId)) {
            if ([string]$package.ParentProviderId -ieq [string]$package.Id) { throw "External package $($package.Id) cannot be its own parent provider." }
            if (-not $byId.ContainsKey([string]$package.ParentProviderId)) { throw "External package $($package.Id) references an unknown parent provider." }
            $parent = $byId[[string]$package.ParentProviderId]
            if ($parent.DeliveryMode -ne 'AuthenticatedSftp') { throw "External package $($package.Id) parent must use AuthenticatedSftp delivery." }
            if ($package.DeliveryMode -ne 'ParentProvider' -or [string]::IsNullOrWhiteSpace([string]$package.DeliveryProductId)) {
                throw "External package $($package.Id) with a parent provider must use ParentProvider delivery."
            }
            if ([string]$package.DeliveryProductId -notin @($parent.SftpAllowedProductIds)) {
                throw "External package $($package.Id) references a product outside its parent provider allowlist."
            }
            if ($package.ReleaseMode -eq 'ParentCatalog' -and $package.VersionRule -ne 'ParentCatalog') {
                throw "External package $($package.Id) ParentCatalog release requires the ParentCatalog version rule."
            }
            $package.SftpHost = $parent.SftpHost
            $package.SftpPort = $parent.SftpPort
            $package.SftpCatalogUri = $parent.SftpCatalogUri
            $package.SftpRemoteRoot = $parent.SftpRemoteRoot
            $package.SftpAllowedProductIds = @($package.DeliveryProductId)
            $package.DownloadPublisherPattern = $parent.DownloadPublisherPattern
            $package.DownloadMaxBytes = $parent.DownloadMaxBytes
        }
        elseif ($package.DeliveryMode -eq 'ParentProvider' -or $package.ReleaseMode -eq 'ParentCatalog') {
            throw "External package $($package.Id) requires Metadata.ParentProviderId."
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$package.VersionCouplingTargetId)) {
            if ([string]$package.VersionCouplingTargetId -ieq [string]$package.Id -or -not $byId.ContainsKey([string]$package.VersionCouplingTargetId)) {
                throw "External package $($package.Id) has an invalid version-coupling target."
            }
        }
    }
    return @($packages)
}

function Get-AVWorkstationToolkitCatalog {
    [CmdletBinding()]
    param(
        [string]$CatalogPath = (Join-Path $PSScriptRoot '..\manifests\managed-applications.json'),
        [string]$ExternalCatalogPath,
        [string]$AwarenessCatalogPath
    )

    $defaultCatalogPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\manifests\managed-applications.json'))
    $CatalogPath = [IO.Path]::GetFullPath($CatalogPath)
    if (-not (Test-Path -LiteralPath $CatalogPath -PathType Leaf)) {
        throw "Application catalog was not found: $CatalogPath"
    }

    if ([IO.Path]::GetExtension($CatalogPath) -ieq '.json') {
        $data = Get-Content -LiteralPath $CatalogPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $catalogKeys = @($data.PSObject.Properties.Name)
        if ('SchemaVersion' -notin $catalogKeys -or $data.SchemaVersion -isnot [int] -or [int]$data.SchemaVersion -ne 1) {
            throw 'Application catalog SchemaVersion must be the integer 1.'
        }
        $allowedCatalogKeys = @('SchemaVersion','Packages','ForbiddenPattern')
    }
    else {
        # Explicit non-default PSD1 inputs remain available for retained characterization fixtures.
        $data = Import-PowerShellDataFile -LiteralPath $CatalogPath
        $catalogKeys = @($data.Keys)
        $allowedCatalogKeys = @('Packages','ForbiddenPattern')
    }
    if ('Packages' -notin $catalogKeys -or 'ForbiddenPattern' -notin $catalogKeys) {
        throw 'Application catalog must contain Packages and ForbiddenPattern.'
    }
    $unexpectedCatalogKeys = @($catalogKeys | Where-Object { $_ -notin $allowedCatalogKeys })
    if ($unexpectedCatalogKeys.Count -gt 0) {
        throw ('Application catalog contains unsupported keys: {0}' -f ($unexpectedCatalogKeys -join ', '))
    }
    if ([string]::IsNullOrWhiteSpace([string]$data.ForbiddenPattern)) {
        throw 'Application catalog ForbiddenPattern must not be empty.'
    }
    try { [void][regex]::new([string]$data.ForbiddenPattern) }
    catch { throw "Application catalog ForbiddenPattern is invalid: $($_.Exception.Message)" }

    if (-not $PSBoundParameters.ContainsKey('ExternalCatalogPath')) {
        $ExternalCatalogPath = if ($CatalogPath.Equals($defaultCatalogPath,[StringComparison]::OrdinalIgnoreCase)) {
            [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\manifests\external-applications.json'))
        }
        else { '' }
    }
    if (-not $PSBoundParameters.ContainsKey('AwarenessCatalogPath')) {
        $AwarenessCatalogPath = if (
            $CatalogPath.Equals($defaultCatalogPath,[StringComparison]::OrdinalIgnoreCase) -and
            -not $PSBoundParameters.ContainsKey('ExternalCatalogPath')) {
            [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\manifests\commercial-av-catalog.json'))
        }
        else { '' }
    }

    $validProfiles = @('Standard','Field','Developer','Optional')
    $validRisks = @('None','Driver','Service','Listener')
    $validDeploymentPolicies = @('Allowlisted','ManualHold')
    $validMaintenancePolicies = @('Allowlisted','Hold')
    # Installer execution mode is independent of Risk: it selects the WinGet installer vector only,
    # so an installer requirement can never be expressed by misclassifying a package's risk. Its
    # type and token are enforced by Resolve-AVWorkstationToolkitInstallerMode as the record is read.
    $allowedPackageKeys = @('Profile','Name','Id','Vendor','Risk','Note','Deployment','Maintenance','InstallerMode')
    $packages = [System.Collections.Generic.List[object]]::new()
    $index = 0

    foreach ($raw in @($data.Packages)) {
        if ($raw -isnot [System.Collections.IDictionary] -and $null -eq $raw.PSObject) {
            throw "Catalog entry $index must be a catalog object."
        }
        $rawKeys = if ($raw -is [System.Collections.IDictionary]) { @($raw.Keys) } else { @($raw.PSObject.Properties.Name) }
        $unexpectedPackageKeys = @($rawKeys | Where-Object { $_ -notin $allowedPackageKeys })
        if ($unexpectedPackageKeys.Count -gt 0) {
            throw ("Catalog entry {0} contains unsupported keys: {1}" -f $index,($unexpectedPackageKeys -join ', '))
        }
        $package = [pscustomobject]@{
            Profile     = [string](Get-AVWorkstationToolkitValue $raw 'Profile' '')
            Name        = [string](Get-AVWorkstationToolkitValue $raw 'Name' (Get-AVWorkstationToolkitValue $raw 'Id' ''))
            Id          = [string](Get-AVWorkstationToolkitValue $raw 'Id' '')
            Risk        = [string](Get-AVWorkstationToolkitValue $raw 'Risk' 'None')
            Note        = [string](Get-AVWorkstationToolkitValue $raw 'Note' '')
            Deployment  = [string](Get-AVWorkstationToolkitValue $raw 'Deployment' 'Allowlisted')
            Maintenance = [string](Get-AVWorkstationToolkitValue $raw 'Maintenance' 'Allowlisted')
            InstallerMode = Resolve-AVWorkstationToolkitInstallerMode -InputObject $raw -PackageId ([string](Get-AVWorkstationToolkitValue $raw 'Id' ''))
            Provider = 'WinGet'
            KnownVersion = ''
            DetectionMode = 'WinGet'
            DetectionDisplayNamePattern = ''
            DetectionVersionPattern = ''
            DetectionVersionPolicy = ''
            ReleaseMode = ''
            ReleaseUri = ''
            ReleaseVersionPattern = ''
            ReleaseChannel = ''
            DeliveryMode = 'WinGet'
            DeliveryUri = ''
            PayloadRelativePath = ''
            PayloadSha256 = ''
            PayloadPublisher = ''
            DownloadUriPattern = ''
            DownloadAllowedHosts = @()
            DownloadPublisherPattern = ''
            DownloadMaxBytes = [int64]0
            SftpHost = ''
            SftpPort = 0
            SftpCatalogUri = ''
            SftpRemoteRoot = ''
            SftpAllowedProductIds = @()
            DeliveryProviderId = ''
            DeliveryProductId = ''
            Vendor = [string](Get-AVWorkstationToolkitValue $raw 'Vendor' '')
            ProductFamily = [string](Get-AVWorkstationToolkitValue $raw 'Name' '')
            ApplicationType = if ([string](Get-AVWorkstationToolkitValue $raw 'Profile' '') -eq 'Developer') { @('Development') } else { @('FieldUtility') }
            ParentProviderId = ''
            Priority = if ([string](Get-AVWorkstationToolkitValue $raw 'Profile' '') -eq 'Developer') { 'DEV' } elseif ([string](Get-AVWorkstationToolkitValue $raw 'Profile' '') -in @('Standard','Field')) { 'UTILITY' } else { 'P2' }
            Roles = if ([string](Get-AVWorkstationToolkitValue $raw 'Profile' '') -eq 'Developer') { @('Development') } elseif ([string](Get-AVWorkstationToolkitValue $raw 'Profile' '') -eq 'Field') { @('FieldService') } else { @('AVEngineer') }
            DeploymentClass = 'Managed'
            CatalogMaintenancePolicy = if ([string](Get-AVWorkstationToolkitValue $raw 'Maintenance' 'Allowlisted') -eq 'Allowlisted') { 'Managed' } else { 'Manual' }
            VersionRule = 'Latest'
            VersionCoupling = 'Independent'
            VersionCouplingTargetId = ''
            VersionCouplingNotes = ''
            CurrentOrLegacy = 'Current'
            LicensingModel = @('UNKNOWN-COST')
            DownloadAccess = @('PUBLIC-DL')
            DownloadDifficulty = 'EASY'
            RequiresVendorAccount = $false
            RequiresDealerAccount = $false
            RequiresTraining = $false
            RequiresLicense = $null
            RequiresSubscription = $null
            InstallsDriver = ([string](Get-AVWorkstationToolkitValue $raw 'Risk' 'None') -eq 'Driver')
            InstallsService = ([string](Get-AVWorkstationToolkitValue $raw 'Risk' 'None') -eq 'Service')
            OpensListener = ([string](Get-AVWorkstationToolkitValue $raw 'Risk' 'None') -eq 'Listener')
            FirmwareUtility = $false
            Architecture = @('x64')
            SupportedOS = @('Windows')
            SideBySideSupported = 'Unknown'
            OfficialDownloadUri = ''
            OfficialProductUri = ''
            ValidationMethod = @('WinGet')
            CatalogNotes = ''
            DistributionPolicy = 'PackageManagerOnly'
            WorkflowCategories = @()
            InstallationForms = @('WinGet')
            MetadataVerifiedOn = ''
            MetadataVerificationState = 'VerificationRequired'
            MetadataReviewTriggers = @()
            MetadataQuarantined = $false
            MetadataQuarantineReason = ''
            AuthoritativeDomain = ''
            ExpectedPublisher = ''
            SignatureValidation = 'Unknown'
            VendorHashAvailability = 'Unknown'
            DownloadStrategy = 'PackageManager'
            CatalogTags = @($(if ([string](Get-AVWorkstationToolkitValue $raw 'Profile' '') -eq 'Developer') { 'DEV' } elseif ([string](Get-AVWorkstationToolkitValue $raw 'Profile' '') -in @('Standard','Field')) { 'UTILITY' } else { 'P2' }),'UNKNOWN-COST','PUBLIC-DL','VerificationRequired')
        }

        $packages.Add($package) | Out-Null
        $index++
    }

    if (-not [string]::IsNullOrWhiteSpace($ExternalCatalogPath)) {
        $ExternalCatalogPath = [IO.Path]::GetFullPath($ExternalCatalogPath)
        if (-not (Test-Path -LiteralPath $ExternalCatalogPath -PathType Leaf)) {
            throw "External application catalog was not found: $ExternalCatalogPath"
        }
        $externalJson = Get-Content -LiteralPath $ExternalCatalogPath -Raw -ErrorAction Stop
        foreach ($externalPackage in @(ConvertFrom-AVWorkstationToolkitExternalCatalogJson -Json $externalJson)) {
            $packages.Add($externalPackage) | Out-Null
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($AwarenessCatalogPath)) {
        $AwarenessCatalogPath = [IO.Path]::GetFullPath($AwarenessCatalogPath)
        if (-not (Test-Path -LiteralPath $AwarenessCatalogPath -PathType Leaf)) {
            throw "Commercial AV awareness catalog was not found: $AwarenessCatalogPath"
        }
        $awarenessJson = Get-Content -LiteralPath $AwarenessCatalogPath -Raw -ErrorAction Stop
        foreach ($awarenessPackage in @(ConvertFrom-AVWorkstationToolkitExternalCatalogJson -Json $awarenessJson)) {
            $packages.Add($awarenessPackage) | Out-Null
        }
    }

    if ($packages.Count -eq 0) {
        throw 'Application catalog must contain at least one package.'
    }

    $orderedPackages = [System.Collections.Generic.List[object]]::new()
    $index = 0
    foreach ($package in $packages) {
        $package | Add-Member -NotePropertyName Order -NotePropertyValue $index

        if ([string]::IsNullOrWhiteSpace($package.Id) -or [string]::IsNullOrWhiteSpace($package.Name) -or [string]::IsNullOrWhiteSpace($package.Note)) {
            throw "Catalog entry $index is missing Name, Id, or Note."
        }
        if ($package.Id -notmatch '^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$') {
            throw "Invalid package ID '$($package.Id)' at catalog entry $index."
        }
        foreach ($field in @('Name','Id','Note')) {
            $value = [string]$package.$field
            if ($value -ne $value.Trim() -or $value -match '[\x00-\x1F\x7F]') {
                throw "Catalog field $field contains leading/trailing whitespace or control characters for $($package.Id)."
            }
        }
        if ($package.Profile -notin $validProfiles) {
            throw "Invalid profile '$($package.Profile)' for $($package.Id)."
        }
        if ($package.Risk -notin $validRisks) {
            throw "Invalid risk '$($package.Risk)' for $($package.Id)."
        }
        if ($package.Deployment -notin $validDeploymentPolicies) {
            throw "Invalid deployment policy '$($package.Deployment)' for $($package.Id)."
        }
        if ($package.Maintenance -notin $validMaintenancePolicies) {
            throw "Invalid maintenance policy '$($package.Maintenance)' for $($package.Id)."
        }
        if ($package.Provider -notin @('WinGet','External')) {
            throw "Invalid provider '$($package.Provider)' for $($package.Id)."
        }
        if ($package.Provider -eq 'External' -and ($package.Deployment -ne 'ManualHold' -or $package.Maintenance -ne 'Hold')) {
            throw "External package $($package.Id) must remain on manual deployment and maintenance hold."
        }
        $isAwarenessOnlySecurityReference = $package.Provider -eq 'External' -and
            $package.DeploymentClass -eq 'AwarenessOnly' -and $package.DeliveryMode -eq 'Awareness'
        if (($package.Id + ' ' + $package.Name + ' ' + $package.Note) -match $data.ForbiddenPattern -and -not $isAwarenessOnlySecurityReference) {
            throw "Out-of-scope security/management package detected in catalog: $($package.Id)"
        }

        $orderedPackages.Add($package) | Out-Null
        $index++
    }

    $duplicates = @($orderedPackages | Group-Object { $_.Id.ToLowerInvariant() } | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) {
        throw ('Duplicate package IDs in catalog: {0}' -f (($duplicates.Name | Sort-Object) -join ', '))
    }

    return @($orderedPackages)
}

function Get-AVWorkstationToolkitRebootState {
    [CmdletBinding()]
    param()

    $reasons = [System.Collections.Generic.List[string]]::new()
    if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') {
        $reasons.Add('Windows Update')
    }
    if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') {
        $reasons.Add('Component Based Servicing')
    }

    [pscustomobject]@{
        Pending = $reasons.Count -gt 0
        Reasons = @($reasons)
        Summary = if ($reasons.Count -gt 0) { $reasons -join '; ' } else { 'No pending reboot signals' }
    }
}

function Test-AVWorkstationToolkitElevated {
    [CmdletBinding()]
    param()

    $principal = [Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-AVWorkstationToolkitWingetCommand {
    [CmdletBinding()]
    param()

    # Resolve the executable from the signed Desktop App Installer package,
    # rather than trusting a same-named alias, function, or PATH entry.
    $windowsAppsRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'WindowsApps')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $packages = @(Appx\Get-AppxPackage -Name Microsoft.DesktopAppInstaller -ErrorAction SilentlyContinue |
        Where-Object { $_.Status -eq 'Ok' -and $_.Publisher -match '(^|,\s*)O=Microsoft Corporation(,|$)' } |
        Sort-Object -Property Version -Descending)

    foreach ($package in $packages) {
        $candidate = Join-Path $package.InstallLocation 'winget.exe'
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $candidate = [IO.Path]::GetFullPath($candidate)
        if (-not $candidate.StartsWith($windowsAppsRoot, [StringComparison]::OrdinalIgnoreCase)) { continue }

        try {
            $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $candidate -ErrorAction Stop
            $signer = if ($null -ne $signature.SignerCertificate) { [string]$signature.SignerCertificate.Subject } else { '' }
            if ($signature.Status -eq 'Valid' -and $signer -match '(^|,\s*)O=Microsoft Corporation(,|$)') {
                return [IO.Path]::GetFullPath($candidate)
            }
        }
        catch { continue }
    }

    throw 'A trusted Microsoft winget.exe could not be resolved from Desktop App Installer.'
}

function Invoke-AVWorkstationToolkitWingetCapture {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Arguments
    )

    try { $commandPath = Get-AVWorkstationToolkitWingetCommand }
    catch { return [pscustomobject]@{ ExitCode = 127; Output = $_.Exception.Message; Arguments = @($Arguments) } }

    $output = (& $commandPath @Arguments 2>&1 | Out-String).TrimEnd()
    $exitCode = $LASTEXITCODE
    $output = Protect-AVWorkstationToolkitSensitiveText -Text $output

    [pscustomobject]@{
        ExitCode = $exitCode
        Output    = $output
        Arguments = @($Arguments)
    }
}

function ConvertFrom-AVWorkstationToolkitWingetExportJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Json
    )

    try { $document = $Json | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "WinGet export JSON is invalid: $($_.Exception.Message)" }

    if ($null -eq $document -or $document.PSObject.Properties.Name -notcontains 'Sources') {
        throw 'WinGet export JSON does not contain the required Sources collection.'
    }

    $byId = [System.Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($source in @($document.Sources)) {
        if ($null -eq $source -or $source.PSObject.Properties.Name -notcontains 'Packages') { continue }
        foreach ($rawPackage in @($source.Packages)) {
            if ($null -eq $rawPackage -or $rawPackage.PSObject.Properties.Name -notcontains 'PackageIdentifier') {
                throw 'WinGet export JSON contains a package without PackageIdentifier.'
            }

            $id = [string]$rawPackage.PackageIdentifier
            if ([string]::IsNullOrWhiteSpace($id) -or $id -ne $id.Trim() -or $id -notmatch '^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$') {
                throw "WinGet export JSON contains an invalid package identifier: '$id'."
            }

            $version = if ($rawPackage.PSObject.Properties.Name -contains 'Version') { [string]$rawPackage.Version } else { '' }
            if ($version.Length -gt 256 -or $version -match '[\x00-\x1F\x7F]') {
                throw "WinGet export JSON contains an invalid version for '$id'."
            }

            if (-not $byId.ContainsKey($id)) {
                $byId.Add($id, [pscustomobject]@{
                    Id = $id
                    InstalledVersion = $version.Trim()
                })
            }
            elseif (-not [string]::IsNullOrWhiteSpace($version)) {
                $existingVersions = @(([string]$byId[$id].InstalledVersion -split '\s+/\s+') | Where-Object { $_ })
                if ($version.Trim() -notin $existingVersions) {
                    $byId[$id].InstalledVersion = (@($existingVersions + $version.Trim()) | Sort-Object -Unique) -join ' / '
                }
            }
        }
    }

    return @($byId.Values | Sort-Object Id)
}

function Get-AVWorkstationToolkitWingetStructuredInventoryQuality {
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()][object[]]$InstalledPackages = @(),
        [AllowEmptyCollection()][object[]]$CatalogPackages = @(),
        [AllowEmptyString()][string]$DiagnosticText = ''
    )

    $installedIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($package in @($InstalledPackages)) {
        if ($null -ne $package -and -not [string]::IsNullOrWhiteSpace([string]$package.Id)) {
            [void]$installedIds.Add([string]$package.Id)
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($DiagnosticText)) {
        foreach ($package in @($CatalogPackages)) {
            if ($null -eq $package -or $installedIds.Contains([string]$package.Id)) { continue }
            $warningPattern = '(?i)(?:' + [regex]::Escape([string]$package.Id) + '|' + [regex]::Escape([string]$package.Name) + ')'
            if ([regex]::IsMatch($DiagnosticText,$warningPattern)) {
                return [pscustomobject]@{ Quality='Partial'; Failure='PartialInventory'; Detail='WinGet reported a catalog package that could not be mapped into structured inventory.' }
            }
        }
    }
    return [pscustomobject]@{ Quality='Complete'; Failure='None'; Detail='Structured WinGet inventory covers all catalog identities named by diagnostics.' }
}

function Get-AVWorkstationToolkitWingetInventory {
    [CmdletBinding()]
    param()

    $exportPath = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-winget-export-{0}.json' -f [guid]::NewGuid().ToString('N'))
    try {
        $result = Invoke-AVWorkstationToolkitWingetCapture -Arguments @(
            'export','--output',$exportPath,'--source','winget','--include-versions',
            '--accept-source-agreements','--disable-interactivity'
        )
        if ($result.ExitCode -ne 0) {
            return [pscustomobject]@{
                Available = $false
                ExitCode = $result.ExitCode
                Packages = @()
                Detail = $result.Output
                RawOutput = $result.Output
            }
        }
        if (-not (Test-Path -LiteralPath $exportPath -PathType Leaf)) {
            return [pscustomobject]@{
                Available = $false
                ExitCode = 1
                Packages = @()
                Detail = 'WinGet reported success but did not create the export JSON file.'
                RawOutput = $result.Output
            }
        }

        try {
            $json = Get-Content -LiteralPath $exportPath -Raw -ErrorAction Stop
            $packages = @(ConvertFrom-AVWorkstationToolkitWingetExportJson -Json $json)
        }
        catch {
            return [pscustomobject]@{
                Available = $false
                ExitCode = 1
                Packages = @()
                Detail = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
                RawOutput = $result.Output
            }
        }

        return [pscustomobject]@{
            Available = $true
            ExitCode = 0
            Packages = $packages
            Detail = 'Installed-package inventory loaded from validated WinGet export JSON.'
            RawOutput = $result.Output
        }
    }
    finally {
        # The path is generated locally beneath the OS temporary directory and
        # never accepts caller input. Delete only that exact ephemeral file.
        if (Test-Path -LiteralPath $exportPath -PathType Leaf) {
            [IO.File]::Delete([IO.Path]::GetFullPath($exportPath))
        }
    }
}

function Get-AVWorkstationToolkitDistributionRoot {
    [CmdletBinding()]
    param([string]$Path)

    $candidate = $Path
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = [Environment]::GetEnvironmentVariable('AVWORKSTATIONTOOLKIT_DISTRIBUTION_ROOT','Process')
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    }
    if (-not [IO.Path]::IsPathRooted($candidate)) {
        throw 'AV Workstation Toolkit distribution root must be an absolute path.'
    }
    $resolved = [IO.Path]::GetFullPath($candidate).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
    $volumeRoot = [IO.Path]::GetPathRoot($resolved).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
    if ([string]::IsNullOrWhiteSpace($resolved) -or $resolved.Equals($volumeRoot,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'AV Workstation Toolkit distribution root cannot be a filesystem or volume root.'
    }
    return $resolved
}

function Get-AVWorkstationToolkitExternalInventory {
    [CmdletBinding()]
    param(
        [object[]]$Package,
        [AllowNull()][object[]]$RegistryEntry,
        [AllowNull()][object[]]$RegistrySourceResult
    )

    if (-not $PSBoundParameters.ContainsKey('Package')) {
        $Package = @(Get-AVWorkstationToolkitCatalog | Where-Object Provider -eq 'External')
    }
    $externalPackages = @($Package | Where-Object Provider -eq 'External')

    if ($PSBoundParameters.ContainsKey('RegistryEntry') -and $PSBoundParameters.ContainsKey('RegistrySourceResult')) {
        throw 'Specify either RegistryEntry or RegistrySourceResult, not both.'
    }

    $sourceDefinitions = @(
        [pscustomobject]@{ Name='HKLM64'; Label='HKLM 64-bit uninstall inventory'; Path='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' },
        [pscustomobject]@{ Name='HKLM32'; Label='HKLM 32-bit uninstall inventory'; Path='HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' },
        [pscustomobject]@{ Name='HKCU'; Label='HKCU uninstall inventory'; Path='HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' }
    )
    $entries = [System.Collections.Generic.List[object]]::new()
    $sourceStates = [System.Collections.Generic.List[object]]::new()

    if ($PSBoundParameters.ContainsKey('RegistryEntry')) {
        foreach ($entry in @($RegistryEntry)) {
            if ($null -eq $entry -or [string]::IsNullOrWhiteSpace([string](Get-AVWorkstationToolkitValue $entry 'DisplayName' ''))) { continue }
            $entries.Add([pscustomobject]@{
                Source = 'Fixture'
                DisplayName = [string](Get-AVWorkstationToolkitValue $entry 'DisplayName' '')
                DisplayVersion = [string](Get-AVWorkstationToolkitValue $entry 'DisplayVersion' '')
            }) | Out-Null
        }
        foreach ($definition in $sourceDefinitions) {
            $sourceStates.Add([pscustomobject]@{
                Name=$definition.Name; Label=$definition.Label; Path=$definition.Path; Available=$true
                EntryCount=if ($definition.Name -eq 'HKLM64') { $entries.Count } else { 0 }
                Detail='Fixture registry source available.'
            }) | Out-Null
        }
    }
    elseif ($PSBoundParameters.ContainsKey('RegistrySourceResult')) {
        foreach ($definition in $sourceDefinitions) {
            $provided = @($RegistrySourceResult | Where-Object { [string](Get-AVWorkstationToolkitValue $_ 'Name' '') -eq $definition.Name })
            if ($provided.Count -ne 1) {
                $sourceStates.Add([pscustomobject]@{
                    Name=$definition.Name; Label=$definition.Label; Path=$definition.Path; Available=$false; EntryCount=0
                    Detail='Registry source result was not supplied.'
                }) | Out-Null
                continue
            }
            $provided = $provided[0]
            $available = [bool](Get-AVWorkstationToolkitValue $provided 'Available' $false)
            $sourceEntries = if ($available) { @((Get-AVWorkstationToolkitValue $provided 'Entries' @())) } else { @() }
            foreach ($entry in $sourceEntries) {
                if ($null -eq $entry -or [string]::IsNullOrWhiteSpace([string](Get-AVWorkstationToolkitValue $entry 'DisplayName' ''))) { continue }
                $entries.Add([pscustomobject]@{
                    Source = $definition.Name
                    DisplayName = [string](Get-AVWorkstationToolkitValue $entry 'DisplayName' '')
                    DisplayVersion = [string](Get-AVWorkstationToolkitValue $entry 'DisplayVersion' '')
                }) | Out-Null
            }
            $detail = [string](Get-AVWorkstationToolkitValue $provided 'Detail' $(if ($available) { 'Registry source available.' } else { 'Registry source unavailable.' }))
            $sourceStates.Add([pscustomobject]@{
                Name=$definition.Name; Label=$definition.Label; Path=$definition.Path; Available=$available
                EntryCount=@($sourceEntries | Where-Object { $null -ne $_ -and -not [string]::IsNullOrWhiteSpace([string](Get-AVWorkstationToolkitValue $_ 'DisplayName' '')) }).Count
                Detail=Protect-AVWorkstationToolkitSensitiveText -Text $detail
            }) | Out-Null
        }
    }
    else {
        foreach ($definition in $sourceDefinitions) {
            $sourceEntries = [System.Collections.Generic.List[object]]::new()
            try {
                foreach ($entry in @(Get-ItemProperty -Path $definition.Path -ErrorAction Stop)) {
                    $displayName = [string](Get-AVWorkstationToolkitValue $entry 'DisplayName' '')
                    if (-not [string]::IsNullOrWhiteSpace($displayName)) {
                        $record = [pscustomobject]@{
                            Source = $definition.Name
                            DisplayName = $displayName
                            DisplayVersion = [string](Get-AVWorkstationToolkitValue $entry 'DisplayVersion' '')
                        }
                        $sourceEntries.Add($record) | Out-Null
                        $entries.Add($record) | Out-Null
                    }
                }
                $sourceStates.Add([pscustomobject]@{
                    Name=$definition.Name; Label=$definition.Label; Path=$definition.Path; Available=$true
                    EntryCount=$sourceEntries.Count; Detail='Registry source read successfully.'
                }) | Out-Null
            }
            catch [System.Management.Automation.ItemNotFoundException] {
                $sourceStates.Add([pscustomobject]@{
                    Name=$definition.Name; Label=$definition.Label; Path=$definition.Path; Available=$true
                    EntryCount=0; Detail='Registry source contains no uninstall records.'
                }) | Out-Null
            }
            catch {
                $sourceStates.Add([pscustomobject]@{
                    Name=$definition.Name; Label=$definition.Label; Path=$definition.Path; Available=$false
                    EntryCount=0; Detail=Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
                }) | Out-Null
            }
        }
    }

    $availableSourceCount = @($sourceStates | Where-Object Available).Count
    $overallQuality = if ($availableSourceCount -eq $sourceStates.Count) { 'Complete' } elseif ($availableSourceCount -gt 0) { 'Partial' } else { 'Unavailable' }
    $sourceWarning = if ($overallQuality -eq 'Partial') { '{0} of {1} registry sources unavailable.' -f ($sourceStates.Count-$availableSourceCount),$sourceStates.Count } elseif ($overallQuality -eq 'Unavailable') { 'All registry sources are unavailable.' } else { '' }

    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $externalPackages) {
        if ([string]$item.DetectionMode -eq 'None') {
            $results.Add([pscustomobject]@{
                Id = $item.Id
                Reliable = $true
                Installed = $false
                InstalledVersion = ''
                InstalledVersions = @()
                InventoryQuality = 'NotApplicable'
                SourceStatuses = @($sourceStates)
                Detail = 'Catalog-awareness record; no Windows installation detector is defined.'
            }) | Out-Null
            continue
        }
        $displayRegex = [regex]::new(
            [string]$item.DetectionDisplayNamePattern,
            [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant,
            [TimeSpan]::FromSeconds(2))
        $versionRegex = if ([string]::IsNullOrWhiteSpace([string]$item.DetectionVersionPattern)) {
            $null
        }
        else { New-AVWorkstationToolkitVersionRegex -Pattern ([string]$item.DetectionVersionPattern) -Field "Registry version pattern for $($item.Id)" }

        $matchedEntries = [System.Collections.Generic.List[object]]::new()
        foreach ($entry in @($entries)) {
            if ($null -eq $entry) { continue }
            $displayName = [string](Get-AVWorkstationToolkitValue $entry 'DisplayName' '')
            if ([string]::IsNullOrWhiteSpace($displayName) -or -not $displayRegex.IsMatch($displayName)) { continue }
            $versionText = [string](Get-AVWorkstationToolkitValue $entry 'DisplayVersion' '')
            if ($versionText -notmatch '^\d+(?:\.\d+){1,3}$' -and $null -ne $versionRegex) {
                $match = $versionRegex.Match($displayName)
                if ($match.Success) { $versionText = $match.Groups['Version'].Value }
            }
            $matchedEntries.Add([pscustomobject]@{ DisplayName=$displayName; Version=$versionText }) | Out-Null
        }

        $validVersions = [System.Collections.Generic.List[string]]::new()
        foreach ($match in $matchedEntries) {
            if ([string]$match.Version -match '^\d+(?:\.\d+){1,3}$') {
                try {
                    [void](ConvertTo-AVWorkstationToolkitVersion -Value ([string]$match.Version))
                    if ([string]$match.Version -notin $validVersions) { $validVersions.Add([string]$match.Version) }
                }
                catch { }
            }
        }
        $highest = ''
        foreach ($candidate in $validVersions) {
            if ([string]::IsNullOrWhiteSpace($highest) -or (Compare-AVWorkstationToolkitVersion -Left $candidate -Right $highest) -gt 0) {
                $highest = $candidate
            }
        }
        $itemQuality = if ($matchedEntries.Count -gt 0 -and $validVersions.Count -eq 0) {
            'PackageError'
        }
        elseif ($overallQuality -eq 'Unavailable') { 'Unavailable' }
        elseif ($overallQuality -eq 'Partial') { 'Partial' }
        else { 'Complete' }
        $itemReliable = $validVersions.Count -gt 0 -or ($matchedEntries.Count -eq 0 -and $overallQuality -eq 'Complete')
        $detail = if ($matchedEntries.Count -gt 0 -and $validVersions.Count -eq 0) {
            'A matching installation was found, but its version could not be validated.'
        }
        elseif ($matchedEntries.Count -gt 0) {
            ('Detected {0} installed version(s).{1}' -f $validVersions.Count,$(if ($sourceWarning) { ' ' + $sourceWarning } else { '' }))
        }
        elseif ($overallQuality -eq 'Unavailable') { 'Installation state is unavailable because all uninstall-registry sources failed.' }
        elseif ($overallQuality -eq 'Partial') { 'No matching installation was found in the available registry sources. ' + $sourceWarning + ' Installation state may be incomplete.' }
        else { 'No matching installation was found.' }

        $results.Add([pscustomobject]@{
            Id = $item.Id
            Reliable = $itemReliable
            Installed = $matchedEntries.Count -gt 0
            InstalledVersion = $highest
            InstalledVersions = @($validVersions)
            InventoryQuality = $itemQuality
            SourceStatuses = @($sourceStates)
            Detail = $detail
        }) | Out-Null
    }
    return @($results)
}

function ConvertFrom-AVWorkstationToolkitExternalReleaseContent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Package,
        [Parameter(Mandatory)][string]$Content
    )

    if ($Package.Provider -ne 'External') { throw 'Release content can be parsed only for an external package.' }
    if ([string]$Package.ReleaseMode -ne 'VendorPage') { throw 'This external package does not define an online release page.' }
    if ($Content.Length -gt 2097152) { throw 'External release response exceeds the 2 MiB safety limit.' }
    $regex = New-AVWorkstationToolkitVersionRegex -Pattern ([string]$Package.ReleaseVersionPattern) -Field "Release version pattern for $($Package.Id)"
    $releaseMatches = $regex.Matches($Content)
    if ($releaseMatches.Count -eq 0) { throw "The $($Package.ReleaseChannel) version was not found on the vendor release page." }

    $highestVersion = ''
    foreach ($releaseMatch in $releaseMatches) {
        $versionText = $releaseMatch.Groups['Version'].Value
        [void](ConvertTo-AVWorkstationToolkitVersion -Value $versionText)
        if ([string]::IsNullOrWhiteSpace($highestVersion) -or
            (Compare-AVWorkstationToolkitVersion -Left $versionText -Right $highestVersion) -gt 0) {
            $highestVersion = $versionText
        }
    }
    return $highestVersion
}

function ConvertFrom-AVWorkstationToolkitExternalDownloadContent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Package,
        [Parameter(Mandatory)][string]$Content,
        [string]$ExpectedVersion
    )

    if ($Package.Provider -ne 'External' -or $Package.DeliveryMode -ne 'DirectDownload') {
        throw 'Download content can be parsed only for a direct-download external package.'
    }
    if ($Content.Length -gt 2097152) { throw 'External release response exceeds the 2 MiB safety limit.' }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) { [void](ConvertTo-AVWorkstationToolkitVersion -Value $ExpectedVersion) }
    $regex = New-AVWorkstationToolkitCaptureRegex -Pattern ([string]$Package.DownloadUriPattern) -Field "Download URI pattern for $($Package.Id)" -CaptureName 'Uri'
    $uriMatches = $regex.Matches($Content)
    if ($uriMatches.Count -eq 0) { throw 'The official direct-download URI was not found on the vendor release page.' }

    $allowedHosts = @($Package.DownloadAllowedHosts | ForEach-Object { ([string]$_).ToLowerInvariant() })
    foreach ($match in $uriMatches) {
        $value = [Net.WebUtility]::HtmlDecode([string]$match.Groups['Uri'].Value)
        try { $absolute = Assert-AVWorkstationToolkitHttpsUri -Value $value -Field "Direct download URI for $($Package.Id)" }
        catch { continue }
        $uri = [uri]$absolute
        if ($uri.DnsSafeHost.ToLowerInvariant() -notin $allowedHosts) { continue }
        if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
            $capturedVersion = [string]$match.Groups['Version'].Value
            if ([string]::IsNullOrWhiteSpace($capturedVersion)) { continue }
            $capturedVersion = $capturedVersion.Replace('-','.')
            try { [void](ConvertTo-AVWorkstationToolkitVersion -Value $capturedVersion) }
            catch { continue }
            if ((Compare-AVWorkstationToolkitVersion -Left $capturedVersion -Right $ExpectedVersion) -ne 0) { continue }
        }
        return $uri.AbsoluteUri
    }
    throw 'The vendor page exposed no direct-download URI on an allowlisted host.'
}

function Invoke-AVWorkstationToolkitBoundedHttpsText {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Uri,
        [ValidateRange(1024,2097152)][int]$MaximumBytes = 2097152,
        [ValidateRange(1,60)][int]$TimeoutSeconds = 15,
        [ValidateRange(0,5)][int]$MaximumRedirects = 5
    )

    $initialUri = [uri](Assert-AVWorkstationToolkitHttpsUri -Value $Uri -Field 'Vendor content URI')
    $currentUri = $initialUri
    for ($redirectCount = 0; $redirectCount -le $MaximumRedirects; $redirectCount++) {
        $request = [Net.HttpWebRequest]::CreateHttp($currentUri)
        $request.AllowAutoRedirect = $false
        $request.AutomaticDecompression = [Net.DecompressionMethods]::GZip -bor [Net.DecompressionMethods]::Deflate
        $request.Timeout = $TimeoutSeconds * 1000
        $request.ReadWriteTimeout = $TimeoutSeconds * 1000
        $request.UserAgent = 'AVWorkstationToolkit/1.1'
        $response = $null
        try {
            $response = [Net.HttpWebResponse]$request.GetResponse()
            $statusCode = [int]$response.StatusCode
            if ($statusCode -in @(301,302,303,307,308)) {
                $location = [string]$response.Headers['Location']
                if ($redirectCount -eq $MaximumRedirects -or [string]::IsNullOrWhiteSpace($location)) {
                    throw 'Vendor content request exceeded the redirect limit.'
                }
                $nextUri = [uri]::new($currentUri,$location)
                $nextUri = [uri](Assert-AVWorkstationToolkitHttpsUri -Value $nextUri.AbsoluteUri -Field 'Vendor content redirect URI')
                if (-not $nextUri.DnsSafeHost.Equals($initialUri.DnsSafeHost,[StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Vendor content redirected to an unapproved host.'
                }
                $currentUri = $nextUri
                continue
            }
            if ($statusCode -lt 200 -or $statusCode -gt 299) {
                throw "Vendor content request returned HTTP $statusCode."
            }
            if ($response.ContentLength -gt $MaximumBytes) {
                throw "Vendor content response exceeds the $MaximumBytes-byte limit."
            }

            $source = $response.GetResponseStream()
            $buffer = [byte[]]::new(32768)
            $memory = [IO.MemoryStream]::new()
            try {
                [int64]$totalBytes = 0
                while (($read = $source.Read($buffer,0,$buffer.Length)) -gt 0) {
                    $totalBytes += $read
                    if ($totalBytes -gt $MaximumBytes) {
                        throw "Vendor content response exceeds the $MaximumBytes-byte limit."
                    }
                    $memory.Write($buffer,0,$read)
                }
                if ($totalBytes -eq 0) { throw 'Vendor content response was empty.' }
                $memory.Position = 0
                $encoding = [Text.Encoding]::UTF8
                if (-not [string]::IsNullOrWhiteSpace($response.CharacterSet)) {
                    try { $encoding = [Text.Encoding]::GetEncoding($response.CharacterSet) }
                    catch { $encoding = [Text.Encoding]::UTF8 }
                }
                $reader = [IO.StreamReader]::new($memory,$encoding,$true)
                try { $content = $reader.ReadToEnd() }
                finally { $reader.Dispose() }
            }
            finally {
                $source.Dispose()
                $memory.Dispose()
            }
            return [pscustomobject]@{ Content=$content; FinalUri=$currentUri.AbsoluteUri; Bytes=$totalBytes }
        }
        finally {
            if ($null -ne $response) { $response.Dispose() }
        }
    }
    throw 'Vendor content request did not return a successful response.'
}

function Get-AVWorkstationToolkitExternalReleaseInfo {
    [CmdletBinding()]
    param(
        [object[]]$Package,
        [hashtable]$ContentByUri
    )

    if (-not $PSBoundParameters.ContainsKey('Package')) {
        $Package = @(Get-AVWorkstationToolkitCatalog | Where-Object Provider -eq 'External')
    }
    if ($null -eq $ContentByUri) { $ContentByUri = @{} }
    $contentCache = @{}
    $errorCache = @{}
    $results = [System.Collections.Generic.List[object]]::new()
    $packageLookup = [System.Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($catalogPackage in @($Package | Where-Object Provider -eq 'External')) {
        if (-not $packageLookup.ContainsKey([string]$catalogPackage.Id)) { $packageLookup.Add([string]$catalogPackage.Id,$catalogPackage) }
    }

    foreach ($item in @($Package | Where-Object Provider -eq 'External')) {
        $knownVersion = [string]$item.KnownVersion
        $availableVersion = $knownVersion
        $observedVersion = ''
        $downloadUri = ''
        $onlineAvailable = $false
        $detail = if ([string]$item.ReleaseMode -eq 'InventoryOnly') { 'Inventory-only provider; no online version comparison is performed.' } else { "Using catalog baseline $knownVersion." }
        $uri = [string]$item.ReleaseUri
        if ([string]$item.ReleaseMode -eq 'InventoryOnly') {
            $results.Add([pscustomobject]@{
                Id = $item.Id
                AvailableVersion = ''
                ObservedVersion = ''
                OnlineChecked = $false
                OnlineAvailable = $false
                ReleaseUri = ''
                DownloadUri = ''
                Detail = $detail
            }) | Out-Null
            continue
        }
        if ([string]$item.ReleaseMode -eq 'ParentCatalog') {
            $parent = if ($packageLookup.ContainsKey([string]$item.ParentProviderId)) { $packageLookup[[string]$item.ParentProviderId] } else { $null }
            if ($null -eq $parent -or $parent.DeliveryMode -ne 'AuthenticatedSftp') {
                throw "Parent provider is unavailable for $($item.Id)."
            }
            $uri = [string]$parent.SftpCatalogUri
            try {
                if ($ContentByUri.ContainsKey($uri)) {
                    $content = [string]$ContentByUri[$uri]
                }
                elseif ($contentCache.ContainsKey($uri)) {
                    $content = [string]$contentCache[$uri]
                }
                elseif ($errorCache.ContainsKey($uri)) {
                    throw [InvalidOperationException]::new([string]$errorCache[$uri])
                }
                else {
                    try {
                        $response = Invoke-AVWorkstationToolkitBoundedHttpsText -Uri $uri
                        $content = [string]$response.Content
                        $contentCache[$uri] = $content
                    }
                    catch {
                        $errorCache[$uri] = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
                        throw
                    }
                }
                $parentProducts = @(Get-AVWorkstationToolkitAuthenticatedSftpCatalog -Package $parent -Content $content)
                $product = @($parentProducts | Where-Object { [string]$_.ProductId -eq [string]$item.DeliveryProductId })
                if ($product.Count -ne 1) { throw 'The parent provider catalog did not return the configured product.' }
                $observedVersion = [string]$product[0].Version
                $availableVersion = $observedVersion
                $onlineAvailable = $true
                $detail = "Parent provider catalog reports $observedVersion for product $($item.DeliveryProductId)."
            }
            catch {
                $availableVersion = $knownVersion
                $detail = 'Parent-provider version check unavailable; ' + (Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message)
                if (-not [string]::IsNullOrWhiteSpace($knownVersion)) { $detail += " Catalog baseline $knownVersion remains in use." }
            }
            $results.Add([pscustomobject]@{
                Id = $item.Id
                AvailableVersion = $availableVersion
                ObservedVersion = $observedVersion
                OnlineChecked = $true
                OnlineAvailable = $onlineAvailable
                ReleaseUri = $uri
                DownloadUri = ''
                Detail = $detail
            }) | Out-Null
            continue
        }
        try {
            if ($ContentByUri.ContainsKey($uri)) {
                $content = [string]$ContentByUri[$uri]
            }
            elseif ($contentCache.ContainsKey($uri)) {
                $content = [string]$contentCache[$uri]
            }
            elseif ($errorCache.ContainsKey($uri)) {
                throw [InvalidOperationException]::new([string]$errorCache[$uri])
            }
            else {
                try {
                    $response = Invoke-AVWorkstationToolkitBoundedHttpsText -Uri $uri
                    $content = [string]$response.Content
                    $contentCache[$uri] = $content
                }
                catch {
                    $errorCache[$uri] = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
                    throw
                }
            }
            $observedVersion = ConvertFrom-AVWorkstationToolkitExternalReleaseContent -Package $item -Content $content
            $onlineAvailable = $true
            if ((Compare-AVWorkstationToolkitVersion -Left $observedVersion -Right $knownVersion) -gt 0) {
                $availableVersion = $observedVersion
            }
            $detail = "Vendor $($item.ReleaseChannel) page reports $observedVersion."
            if ($item.DeliveryMode -eq 'DirectDownload') {
                try { $downloadUri = ConvertFrom-AVWorkstationToolkitExternalDownloadContent -Package $item -Content $content -ExpectedVersion $observedVersion }
                catch { $detail += ' Direct installer link unavailable; vendor-page handoff remains available.' }
            }
        }
        catch {
            $detail = 'Online version check unavailable; ' + (Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message) + " Catalog baseline $knownVersion remains in use."
        }
        $results.Add([pscustomobject]@{
            Id = $item.Id
            AvailableVersion = $availableVersion
            ObservedVersion = $observedVersion
            OnlineChecked = $true
            OnlineAvailable = $onlineAvailable
            ReleaseUri = $uri
            DownloadUri = $downloadUri
            Detail = $detail
        }) | Out-Null
    }
    return @($results)
}

function Test-AVWorkstationToolkitPathForReparsePoint {
    param([Parameter(Mandatory)][string]$Root,[Parameter(Mandatory)][string]$Path)

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($rootFull,[StringComparison]::OrdinalIgnoreCase)) { return $true }
    $current = $pathFull
    while (-not [string]::IsNullOrWhiteSpace($current) -and $current.StartsWith($rootFull,[StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $true }
        }
        $current = Split-Path -Parent $current
    }
    return $false
}

function Resolve-AVWorkstationToolkitExternalPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Package,
        [string]$DistributionRoot
    )

    if ($Package.Provider -ne 'External' -or $Package.DeliveryMode -ne 'Bundled') {
        return [pscustomobject]@{ Available=$false; Valid=$false; Path=''; Detail='No bundled payload is configured.' }
    }
    $root = Get-AVWorkstationToolkitDistributionRoot -Path $DistributionRoot
    $relative = ([string]$Package.PayloadRelativePath).Replace('/',[IO.Path]::DirectorySeparatorChar)
    $path = [IO.Path]::GetFullPath((Join-Path (Join-Path $root 'packages') $relative))
    $packagesRoot = [IO.Path]::GetFullPath((Join-Path $root 'packages')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($packagesRoot,[StringComparison]::OrdinalIgnoreCase)) {
        throw "Bundled payload path escapes the package root for $($Package.Id)."
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return [pscustomobject]@{ Available=$false; Valid=$false; Path=$path; Detail='The configured bundled payload is not present.' }
    }
    if (Test-AVWorkstationToolkitPathForReparsePoint -Root $root -Path $path) {
        return [pscustomobject]@{ Available=$true; Valid=$false; Path=$path; Detail='The bundled payload path contains an unsupported reparse point.' }
    }
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if (-not $actualHash.Equals([string]$Package.PayloadSha256,[StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{ Available=$true; Valid=$false; Path=$path; Detail='The bundled payload SHA-256 hash does not match the embedded catalog.' }
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$Package.PayloadPublisher)) {
        $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $path -ErrorAction Stop
        $subject = if ($null -ne $signature.SignerCertificate) { [string]$signature.SignerCertificate.Subject } else { '' }
        if ($signature.Status -ne 'Valid' -or -not $subject.Equals([string]$Package.PayloadPublisher,[StringComparison]::OrdinalIgnoreCase)) {
            return [pscustomobject]@{ Available=$true; Valid=$false; Path=$path; Detail='The bundled payload Authenticode publisher does not match the embedded catalog.' }
        }
    }
    return [pscustomobject]@{ Available=$true; Valid=$true; Path=$path; Detail='Bundled payload hash and publisher policy are valid.' }
}

function Get-AVWorkstationToolkitVendorCacheRoot {
    param([string]$DataRoot)
    return [IO.Path]::GetFullPath((Join-Path (Get-AVWorkstationToolkitDataRoot -Path $DataRoot) 'vendor-cache'))
}

function Resolve-AVWorkstationToolkitVendorCachePayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Package,
        [Parameter(Mandatory)][string]$Version,
        [string]$DataRoot
    )

    if ($Package.Provider -ne 'External' -or $Package.DeliveryMode -notin @('DirectDownload','AuthenticatedSftp','ParentProvider')) {
        return [pscustomobject]@{ Available=$false; Valid=$false; Path=''; Detail='No downloaded vendor payload is configured.' }
    }
    [void](ConvertTo-AVWorkstationToolkitVersion -Value $Version)
    $cacheRoot = Get-AVWorkstationToolkitVendorCacheRoot -DataRoot $DataRoot
    $packageRoot = [IO.Path]::GetFullPath((Join-Path (Join-Path $cacheRoot $Package.Id) $Version))
    $cachePrefix = $cacheRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $packageRoot.StartsWith($cachePrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Vendor cache package path escaped the data root.' }
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        return [pscustomobject]@{ Available=$false; Valid=$false; Path=''; Detail='No verified vendor download is cached.' }
    }
    if (Test-AVWorkstationToolkitPathForReparsePoint -Root $cacheRoot -Path $packageRoot) {
        return [pscustomobject]@{ Available=$true; Valid=$false; Path=''; Detail='The vendor cache contains an unsupported reparse point.' }
    }

    $metadataFiles = @(Get-ChildItem -LiteralPath $packageRoot -File -Filter '*.avworkstationtoolkit.json' -ErrorAction Stop)
    foreach ($metadataFile in $metadataFiles) {
        if ($metadataFile.Length -gt 65536) { continue }
        try {
            $metadata = Get-Content -LiteralPath $metadataFile.FullName -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            if ([int]$metadata.SchemaVersion -ne 1 -or [string]$metadata.PackageId -ne [string]$Package.Id -or [string]$metadata.Version -ne $Version) { continue }
            $payloadName = [string]$metadata.FileName
            if ([string]::IsNullOrWhiteSpace($payloadName) -or $payloadName -ne [IO.Path]::GetFileName($payloadName)) { continue }
            $payloadPath = [IO.Path]::GetFullPath((Join-Path $packageRoot $payloadName))
            if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf) -or (Test-AVWorkstationToolkitPathForReparsePoint -Root $cacheRoot -Path $payloadPath)) { continue }
            $hash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
            if (-not $hash.Equals([string]$metadata.Sha256,[StringComparison]::OrdinalIgnoreCase)) { continue }
            $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $payloadPath -ErrorAction Stop
            $subject = if ($null -ne $signature.SignerCertificate) { [string]$signature.SignerCertificate.Subject } else { '' }
            $publisherRegex = [regex]::new([string]$Package.DownloadPublisherPattern,[Text.RegularExpressions.RegexOptions]::IgnoreCase,[TimeSpan]::FromSeconds(2))
            if ($signature.Status -ne 'Valid' -or -not $publisherRegex.IsMatch($subject)) { continue }
            return [pscustomobject]@{ Available=$true; Valid=$true; Path=$payloadPath; Detail='Cached vendor payload hash and Authenticode publisher are valid.' }
        }
        catch { continue }
    }
    return [pscustomobject]@{ Available=($metadataFiles.Count -gt 0); Valid=$false; Path=''; Detail='No cached vendor payload passed hash and publisher validation.' }
}

function Complete-AVWorkstationToolkitVendorDownload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Package,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$DownloadPath,
        [string]$DataRoot,
        [string]$Source = 'Vendor'
    )

    if ($Package.Provider -ne 'External' -or $Package.DeliveryMode -notin @('DirectDownload','AuthenticatedSftp','ParentProvider')) {
        throw 'Only a configured vendor-download provider can finalize a downloaded payload.'
    }
    [void](ConvertTo-AVWorkstationToolkitVersion -Value $Version)
    $cacheRoot = Get-AVWorkstationToolkitVendorCacheRoot -DataRoot $DataRoot
    $expectedRoot = [IO.Path]::GetFullPath((Join-Path (Join-Path $cacheRoot $Package.Id) $Version))
    $path = [IO.Path]::GetFullPath($DownloadPath)
    $expectedPrefix = $expectedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($expectedPrefix,[StringComparison]::OrdinalIgnoreCase) -or -not $path.EndsWith('.download',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Downloaded vendor payload is outside its package/version cache or lacks the temporary suffix.'
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Downloaded vendor payload is missing.' }
    if (Test-AVWorkstationToolkitPathForReparsePoint -Root $cacheRoot -Path $path) { throw 'Downloaded vendor payload path contains an unsupported reparse point.' }
    $file = Get-Item -LiteralPath $path -ErrorAction Stop
    if ($file.Length -le 0 -or $file.Length -gt [int64]$Package.DownloadMaxBytes) { throw 'Downloaded vendor payload size violates the catalogued limit.' }
    $finalPath = $path.Substring(0,$path.Length - '.download'.Length)
    if ([IO.Path]::GetExtension($finalPath).ToLowerInvariant() -notin @('.exe','.msi','.msix','.msixbundle')) {
        throw 'Downloaded vendor payload has an unsupported installer extension.'
    }
    $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $path -ErrorAction Stop
    $subject = if ($null -ne $signature.SignerCertificate) { [string]$signature.SignerCertificate.Subject } else { '' }
    $publisherRegex = [regex]::new([string]$Package.DownloadPublisherPattern,[Text.RegularExpressions.RegexOptions]::IgnoreCase,[TimeSpan]::FromSeconds(2))
    if ($signature.Status -ne 'Valid' -or -not $publisherRegex.IsMatch($subject)) {
        throw 'Downloaded vendor payload failed Authenticode publisher validation.'
    }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    Move-Item -LiteralPath $path -Destination $finalPath -Force
    $metadata = [ordered]@{
        SchemaVersion = 1
        PackageId = [string]$Package.Id
        Version = $Version
        FileName = [IO.Path]::GetFileName($finalPath)
        Sha256 = $hash
        PublisherSubject = $subject
        Source = $Source
        VerifiedAt = (Get-Date).ToString('o')
    }
    $metadataPath = $finalPath + '.avworkstationtoolkit.json'
    $temporaryMetadata = $metadataPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporaryMetadata -Encoding UTF8
        Move-Item -LiteralPath $temporaryMetadata -Destination $metadataPath -Force
    }
    finally { Remove-Item -LiteralPath $temporaryMetadata -Force -ErrorAction SilentlyContinue }
    return [pscustomobject]@{ Available=$true; Valid=$true; Path=$finalPath; Sha256=$hash; PublisherSubject=$subject; Detail='Vendor payload signature and cache metadata are valid.' }
}

function Get-AVWorkstationToolkitAuthenticatedSftpCatalog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Package,
        [string]$Content
    )

    if ($Package.Provider -ne 'External' -or $Package.DeliveryMode -ne 'AuthenticatedSftp') {
        throw 'SFTP catalog parsing requires an authenticated-SFTP external package.'
    }
    if (-not $PSBoundParameters.ContainsKey('Content')) {
        $response = Invoke-AVWorkstationToolkitBoundedHttpsText -Uri ([string]$Package.SftpCatalogUri)
        $Content = [string]$response.Content
    }
    if ([string]::IsNullOrWhiteSpace($Content) -or $Content.Length -gt 2097152) { throw 'SFTP product catalog is empty or exceeds the 2 MiB limit.' }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create((New-Object IO.StringReader($Content)),$settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally { $reader.Dispose() }
    if ($document.DocumentElement.Name -ne 'UpdateInformation') { throw 'SFTP product catalog has an unexpected root element.' }

    $allowedIds = @($Package.SftpAllowedProductIds | ForEach-Object { [string]$_ })
    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($node in @($document.SelectNodes('/UpdateInformation/Product'))) {
        $id = [string]$node.GetAttribute('Id')
        if ($id -notin $allowedIds) { continue }
        $version = [string]$node.GetAttribute('Version')
        [void](ConvertTo-AVWorkstationToolkitVersion -Value $version)
        $name = [string]$node.Name
        $remotePath = [string]$node.Download
        if ([string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 128 -or $name -match '[\x00-\x1F\x7F]') { throw "SFTP product $id has an invalid name." }
        $remoteRoot = ([string]$Package.SftpRemoteRoot).TrimEnd('/')
        if (-not $remotePath.StartsWith($remoteRoot + '/', [StringComparison]::Ordinal) -or $remotePath -match '(?:^|/)\.\.(?:/|$)' -or [IO.Path]::GetExtension($remotePath) -ne '.exe') {
            throw "SFTP product $id has an unsafe remote path."
        }
        if ($remotePath.IndexOf($version,[StringComparison]::Ordinal) -lt 0) { throw "SFTP product $id path does not match its version." }
        [double]$sizeMb = 0
        if ([string]$node.Size.Units -ne 'MB' -or -not [double]::TryParse([string]$node.Size.'#text',[Globalization.NumberStyles]::Number,[Globalization.CultureInfo]::InvariantCulture,[ref]$sizeMb) -or $sizeMb -le 0) {
            throw "SFTP product $id has an invalid size."
        }
        $sizeBytes = [int64][Math]::Ceiling($sizeMb * 1MB)
        if ($sizeBytes -gt [int64]$Package.DownloadMaxBytes) { throw "SFTP product $id exceeds the catalogued size limit." }
        $results.Add([pscustomobject]@{
            ProductId = $id
            Name = $name
            Version = $version
            RemotePath = $remotePath
            FileName = [IO.Path]::GetFileName($remotePath)
            SizeBytes = $sizeBytes
            SizeLabel = ('{0:N1} MB' -f $sizeMb)
            RebootRequired = ([string]$node.Reboot -eq '1')
        }) | Out-Null
    }
    $missing = @($allowedIds | Where-Object { $_ -notin @($results.ProductId) })
    if ($missing.Count -gt 0) { throw ('SFTP product catalog omitted allowlisted product IDs: ' + ($missing -join ', ')) }
    return @($results | Sort-Object Name)
}

function Get-AVWorkstationToolkitTrustedSftpHost {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$HostName,[Parameter(Mandatory)][int]$Port,[string]$DataRoot)

    $path = Join-Path (Get-AVWorkstationToolkitDataRoot -Path $DataRoot) 'trusted-sftp-hosts.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    if ((Get-Item -LiteralPath $path).Length -gt 65536) { throw 'Trusted SFTP host store exceeds the 64 KiB limit.' }
    $document = Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ([int]$document.SchemaVersion -ne 1) { throw 'Trusted SFTP host store has an unsupported schema.' }
    return @($document.Hosts | Where-Object { [string]$_.Host -ieq $HostName -and [int]$_.Port -eq $Port } | Select-Object -First 1)
}

function Set-AVWorkstationToolkitTrustedSftpHost {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$HostName,
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string]$Fingerprint,
        [string]$DataRoot
    )

    if ($HostName -notmatch '^(?=.{1,253}$)(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(?:\.(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?))*$' -or $Port -lt 1 -or $Port -gt 65535) {
        throw 'Trusted SFTP host identity is invalid.'
    }
    if ($Fingerprint -notmatch '^SHA256:[A-Za-z0-9+/]{43}$') { throw 'Trusted SFTP fingerprint must use SHA256 OpenSSH format.' }
    $data = Get-AVWorkstationToolkitDataRoot -Path $DataRoot
    $path = Join-Path $data 'trusted-sftp-hosts.json'
    $existing = @()
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $document = Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        if ([int]$document.SchemaVersion -ne 1) { throw 'Trusted SFTP host store has an unsupported schema.' }
        $existing = @($document.Hosts | Where-Object { -not ([string]$_.Host -ieq $HostName -and [int]$_.Port -eq $Port) })
    }
    $updated = [ordered]@{
        SchemaVersion = 1
        Hosts = @($existing) + @([ordered]@{ Host=$HostName; Port=$Port; Fingerprint=$Fingerprint; TrustedAt=(Get-Date).ToString('o') })
    }
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    $temporary = $path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $updated | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporary -Encoding UTF8
        Move-Item -LiteralPath $temporary -Destination $path -Force
    }
    finally { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    return Get-AVWorkstationToolkitTrustedSftpHost -HostName $HostName -Port $Port -DataRoot $data
}

function Test-AVWorkstationToolkitInventoryTextReliable {
    param([AllowNull()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    # A Unicode ellipsis in winget table output indicates a truncated field.
    # Treat the entire inventory as unreliable so an installed package can
    # never be misclassified as missing and offered for duplicate install.
    return $Text.IndexOf([char]0x2026) -lt 0
}

function ConvertFrom-AVWorkstationToolkitWingetUpgradeText {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { throw 'WinGet update output is empty.' }
    if ($Text.IndexOf([char]0x2026) -ge 0) { throw 'WinGet update output contains a truncation marker.' }
    $lines = @($Text -split "`r?`n")
    if (@($lines | Where-Object { $_ -match '(?i)No (?:applicable|available) (?:upgrade|update)s? (?:found|available)|No installed package found matching input criteria' }).Count -gt 0) {
        return @()
    }
    $results = [Collections.Generic.List[object]]::new()
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $columns = $null
    $foundTable = $false
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index].TrimEnd()
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $idColumn = $line.IndexOf('Id', [StringComparison]::Ordinal)
        $versionColumn = $line.IndexOf('Version', [StringComparison]::Ordinal)
        $availableColumn = $line.IndexOf('Available', [StringComparison]::Ordinal)
        $sourceColumn = $line.IndexOf('Source', [StringComparison]::Ordinal)
        if ($line.StartsWith('Name', [StringComparison]::Ordinal) -and $idColumn -gt 4 -and $versionColumn -gt $idColumn -and $availableColumn -gt $versionColumn) {
            if ($index + 1 -ge $lines.Count -or $lines[$index + 1].Trim() -notmatch '^-{8,}$') {
                throw 'WinGet update output contains a table header without a valid separator.'
            }
            $columns = [pscustomobject]@{ HasSource=($sourceColumn -gt $availableColumn) }
            $foundTable = $true
            $index++
            continue
        }
        if ($line -match '(?i)^\s*\d+\s+upgrades?\s+available\.\s*$' -or
            $line -match '(?i)^\s*\d+\s+packages?\s+have\s+version\s+numbers?.*$' -or
            $line -eq 'The following packages have an upgrade available, but require explicit targeting for upgrade:') { continue }
        if ($null -eq $columns) { continue }
        $tokens = @($line -split '\s+' | Where-Object { $_ })
        $minimum = if ($columns.HasSource) { 5 } else { 4 }
        if ($tokens.Count -lt $minimum) { throw 'WinGet update output contains a malformed package row.' }
        if ($columns.HasSource) {
            $id = $tokens[$tokens.Count - 4]
            $installed = $tokens[$tokens.Count - 3]
            $available = $tokens[$tokens.Count - 2]
        }
        else {
            $id = $tokens[$tokens.Count - 3]
            $installed = $tokens[$tokens.Count - 2]
            $available = $tokens[$tokens.Count - 1]
        }
        if ($id -notmatch '^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$' -or [string]::IsNullOrEmpty($installed) -or [string]::IsNullOrEmpty($available)) {
            throw 'WinGet update output contains a malformed package row.'
        }
        if ($installed.Length -gt 256 -or $available.Length -gt 256 -or $installed -match '[\x00-\x1F\x7F]' -or $available -match '[\x00-\x1F\x7F]') {
            throw "WinGet update output contains an invalid version for '$id'."
        }
        if ($ids.Add($id)) {
            $results.Add([pscustomobject]@{ Id=$id; InstalledVersion=$installed; AvailableVersion=$available }) | Out-Null
        }
    }
    if (-not $foundTable) { throw 'WinGet update output does not contain a valid table header.' }
    return @($results | Sort-Object Id)
}

function Test-AVWorkstationToolkitIdInText {
    param(
        [AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][string]$Id
    )

    if ([string]::IsNullOrEmpty($Text)) { return $false }
    $pattern = '(?im)(^|\s)' + [regex]::Escape($Id) + '(\s|$)'
    return [regex]::IsMatch($Text, $pattern)
}

function Get-AVWorkstationToolkitInventoryLine {
    param(
        [AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][string]$Id
    )

    if ([string]::IsNullOrEmpty($Text)) { return $null }
    foreach ($line in ($Text -split "`r?`n")) {
        if (Test-AVWorkstationToolkitIdInText -Text $line -Id $Id) { return $line }
    }
    return $null
}

function Get-AVWorkstationToolkitVersionFromLine {
    param(
        [AllowNull()][string]$Line,
        [Parameter(Mandatory)][string]$Id,
        [ValidateSet('Installed','Available')][string]$Kind = 'Installed'
    )

    if ([string]::IsNullOrWhiteSpace($Line)) { return '' }
    $index = $Line.IndexOf($Id, [StringComparison]::OrdinalIgnoreCase)
    if ($index -lt 0) { return '' }
    $tail = $Line.Substring($index + $Id.Length).Trim()
    $tokens = @($tail -split '\s+' | Where-Object { $_ })
    if ($Kind -eq 'Installed' -and $tokens.Count -ge 1) { return $tokens[0] }
    if ($Kind -eq 'Available' -and $tokens.Count -ge 2) { return $tokens[1] }
    return ''
}

function Get-AVWorkstationToolkitWinGetPackageState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Package,
        [Parameter(Mandatory)]$InstalledLookup,
        [bool]$UsesStructuredInventory,
        [bool]$WingetAvailable,
        [AllowNull()][string]$InstalledText,
        [AllowNull()][string]$UpgradeText
    )

    $installed = if ($UsesStructuredInventory) {
        $InstalledLookup.ContainsKey($Package.Id)
    }
    else {
        Test-AVWorkstationToolkitIdInText -Text $InstalledText -Id $Package.Id
    }
    $upgradeAvailable = $installed -and (Test-AVWorkstationToolkitIdInText -Text $UpgradeText -Id $Package.Id)
    $status = 'Current'
    $action = 'None'
    $statusDetail = 'Installed and current'

    if (-not $WingetAvailable) {
        $status = 'Error'
        $statusDetail = 'winget inventory unavailable'
    }
    elseif (-not $installed) {
        if ($Package.Deployment -eq 'ManualHold') {
            $status = 'Manual'
            $action = 'Manual'
            $statusDetail = 'Manual review required'
        }
        else {
            $status = 'Missing'
            $action = 'Install'
            $statusDetail = 'Available for approved installation'
        }
    }
    elseif ($upgradeAvailable) {
        if ($Package.Maintenance -eq 'Hold') {
            $status = 'Held'
            $statusDetail = 'Automated maintenance hold'
        }
        else {
            $status = 'UpdateAvailable'
            $action = 'Update'
            $statusDetail = 'Allowlisted update available'
        }
    }

    $installedLine = if ($UsesStructuredInventory) { $null } else { Get-AVWorkstationToolkitInventoryLine -Text $InstalledText -Id $Package.Id }
    $upgradeLine = Get-AVWorkstationToolkitInventoryLine -Text $UpgradeText -Id $Package.Id
    $installedVersion = if ($UsesStructuredInventory -and $installed) {
        [string]$InstalledLookup[$Package.Id].InstalledVersion
    }
    else {
        Get-AVWorkstationToolkitVersionFromLine -Line $installedLine -Id $Package.Id -Kind Installed
    }

    [pscustomobject]@{
        Installed = $installed
        InstalledVersions = if ($installed -and -not [string]::IsNullOrWhiteSpace($installedVersion)) { @($installedVersion) } else { @() }
        UpgradeAvailable = $upgradeAvailable
        InstalledVersion = $installedVersion
        AvailableVersion = Get-AVWorkstationToolkitVersionFromLine -Line $upgradeLine -Id $Package.Id -Kind Available
        Status = $status
        Action = $action
        StatusDetail = $statusDetail
        ReleaseCheckDetail = ''
    }
}

function Get-AVWorkstationToolkitExternalPackageState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Package,
        [AllowNull()]$InventoryRecord,
        [AllowNull()]$ReleaseRecord
    )

    $inventoryReliable = $null -ne $InventoryRecord -and [bool](Get-AVWorkstationToolkitValue $InventoryRecord 'Reliable' $false)
    $inventoryQuality = if ($null -ne $InventoryRecord) {
        [string](Get-AVWorkstationToolkitValue $InventoryRecord 'InventoryQuality' $(if ($inventoryReliable) { 'Complete' } else { 'PackageError' }))
    }
    else { 'Unavailable' }
    $installed = $null -ne $InventoryRecord -and [bool](Get-AVWorkstationToolkitValue $InventoryRecord 'Installed' $false)
    $installedVersion = if ($null -ne $InventoryRecord) { [string](Get-AVWorkstationToolkitValue $InventoryRecord 'InstalledVersion' '') } else { '' }
    $installedVersions = if ($null -ne $InventoryRecord) { @((Get-AVWorkstationToolkitValue $InventoryRecord 'InstalledVersions' @())) } else { @() }
    $availableVersion = if ($null -ne $ReleaseRecord) { [string](Get-AVWorkstationToolkitValue $ReleaseRecord 'AvailableVersion' $Package.KnownVersion) } else { [string]$Package.KnownVersion }
    $releaseCheckDetail = if ($null -ne $ReleaseRecord) { [string](Get-AVWorkstationToolkitValue $ReleaseRecord 'Detail' '') } else { 'External release metadata is unavailable.' }
    $upgradeAvailable = $false
    $status = 'Error'
    $action = 'None'
    $statusDetail = ''

    if ([string]$Package.DetectionMode -eq 'None') {
        $availableVersion = if ($Package.ReleaseMode -eq 'ParentCatalog') { $availableVersion } else { '' }
        $status = 'Awareness'
        $action = 'None'
        $statusDetail = 'Known catalog record; this product is not treated as a detectable Windows application.'
    }
    elseif ($Package.ReleaseMode -eq 'InventoryOnly') {
        $availableVersion = ''
        $status = if ($inventoryReliable -and $installed) { 'Inventory' }
            elseif ($inventoryReliable) { 'NotDetected' }
            elseif ($inventoryQuality -eq 'Partial') { 'InventoryIncomplete' }
            elseif ($inventoryQuality -eq 'Unavailable') { 'InventoryUnavailable' }
            else { 'Error' }
        $statusDetail = if (-not $inventoryReliable) {
            if ($null -ne $InventoryRecord) { [string](Get-AVWorkstationToolkitValue $InventoryRecord 'Detail' 'External application inventory is unreliable.') } else { 'External application inventory is unavailable.' }
        }
        elseif ($installed) { 'Installed application recorded for inventory; AV Workstation Toolkit will not change it.' }
        else { 'No matching installation detected; AV Workstation Toolkit will not install this inventory-only item.' }
    }
    elseif ($availableVersion -notmatch '^\d+(?:\.\d+){1,3}$') {
        $inventoryReliable = $false
        $status = 'CheckUnavailable'
        $statusDetail = 'External release version is invalid.'
    }
    else {
        if ($inventoryReliable -and $Package.DetectionVersionPolicy -eq 'SameMajorMinor') {
            $targetParts = $availableVersion.Split('.')
            $matchingVersions = @($installedVersions | Where-Object {
                $candidateParts = ([string]$_).Split('.')
                $candidateParts.Count -ge 2 -and $candidateParts[0] -eq $targetParts[0] -and $candidateParts[1] -eq $targetParts[1]
            })
            $installed = $matchingVersions.Count -gt 0
            $installedVersion = ''
            foreach ($candidate in $matchingVersions) {
                if ([string]::IsNullOrWhiteSpace($installedVersion) -or (Compare-AVWorkstationToolkitVersion -Left ([string]$candidate) -Right $installedVersion) -gt 0) {
                    $installedVersion = [string]$candidate
                }
            }
        }

        if (-not $inventoryReliable) {
            $status = if ($inventoryQuality -eq 'Partial') { 'InventoryIncomplete' }
                elseif ($inventoryQuality -eq 'Unavailable') { 'InventoryUnavailable' }
                else { 'Error' }
            $statusDetail = if ($null -ne $InventoryRecord) { [string](Get-AVWorkstationToolkitValue $InventoryRecord 'Detail' 'External application inventory is unreliable.') } else { 'External application inventory is unavailable.' }
        }
        elseif (-not $installed) {
            $status = 'Manual'
            $action = 'Manual'
            $statusDetail = "The $($Package.ReleaseChannel) channel is not installed; release $availableVersion is available."
        }
        else {
            try { $upgradeAvailable = (Compare-AVWorkstationToolkitVersion -Left $availableVersion -Right $installedVersion) -gt 0 }
            catch {
                $inventoryReliable = $false
                $statusDetail = 'Installed external application version could not be compared safely.'
            }
            if ($inventoryReliable -and $upgradeAvailable) {
                $status = 'ManualUpdate'
                $action = 'Manual'
                $statusDetail = "Vendor-managed update $availableVersion is available."
            }
            elseif ($inventoryReliable) {
                $status = 'Current'
                $statusDetail = "Installed external application satisfies the $($Package.ReleaseChannel) baseline."
            }
        }
    }

    [pscustomobject]@{
        Installed = $installed
        InstalledVersions = @($installedVersions)
        UpgradeAvailable = $upgradeAvailable
        InstalledVersion = $installedVersion
        AvailableVersion = $availableVersion
        Status = $status
        Action = $action
        StatusDetail = $statusDetail
        ReleaseCheckDetail = $releaseCheckDetail
        InventoryQuality = $inventoryQuality
    }
}

function Get-AVWorkstationToolkitExternalDeliveryState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Package,
        [AllowNull()]$ReleaseRecord,
        [AllowEmptyString()][string]$AvailableVersion,
        [string]$DistributionRoot,
        [string]$DataRoot
    )

    $result = [ordered]@{
        Action = 'None'
        Available = $false
        Label = ''
        Uri = ''
        Path = ''
        Detail = ''
        ProviderId = [string](Get-AVWorkstationToolkitValue $Package 'DeliveryProviderId' '')
        ProductId = [string](Get-AVWorkstationToolkitValue $Package 'DeliveryProductId' '')
    }

    switch ($Package.DeliveryMode) {
        'VendorPage' {
            $result.Action = 'OpenUri'
            $result.Available = $true
            $result.Label = 'Open vendor download'
            $result.Uri = [string]$Package.DeliveryUri
            $result.Detail = 'The package is downloaded directly from the vendor.'
        }
        'Bundled' {
            try { $payload = Resolve-AVWorkstationToolkitExternalPayload -Package $Package -DistributionRoot $DistributionRoot }
            catch { $payload = [pscustomobject]@{ Valid=$false; Path=''; Detail=$_.Exception.Message } }
            $result.Detail = [string]$payload.Detail
            if ($payload.Valid) {
                $result.Action = 'ShowFile'
                $result.Available = $true
                $result.Label = 'Show verified package'
                $result.Path = [string]$payload.Path
            }
            elseif (-not [string]::IsNullOrWhiteSpace([string]$Package.DeliveryUri)) {
                $result.Action = 'OpenUri'
                $result.Available = $true
                $result.Label = 'Open vendor download'
                $result.Uri = [string]$Package.DeliveryUri
            }
        }
        'DirectDownload' {
            try { $cached = Resolve-AVWorkstationToolkitVendorCachePayload -Package $Package -Version $AvailableVersion -DataRoot $DataRoot }
            catch { $cached = [pscustomobject]@{ Valid=$false; Path=''; Detail=$_.Exception.Message } }
            if ($cached.Valid) {
                $result.Action = 'ShowFile'
                $result.Available = $true
                $result.Label = 'Show cached installer'
                $result.Path = [string]$cached.Path
                $result.Detail = 'Reveal the previously downloaded installer in Explorer. ' + [string]$cached.Detail
            }
            else {
                $result.Detail = [string]$cached.Detail
                $observedDownloadUri = if ($null -ne $ReleaseRecord) { [string](Get-AVWorkstationToolkitValue $ReleaseRecord 'DownloadUri' '') } else { '' }
                if (-not [string]::IsNullOrWhiteSpace($observedDownloadUri)) {
                    $result.Action = 'DownloadHttps'
                    $result.Available = $true
                    $result.Label = 'Download verified package'
                    $result.Uri = $observedDownloadUri
                    $result.Detail = 'AV Workstation Toolkit will download to its per-user cache and require a valid catalogued Authenticode publisher.'
                }
                elseif (-not [string]::IsNullOrWhiteSpace([string]$Package.DeliveryUri)) {
                    $result.Action = 'OpenUri'
                    $result.Available = $true
                    $result.Label = 'Open vendor download'
                    $result.Uri = [string]$Package.DeliveryUri
                    $result.Detail = 'The direct installer link could not be validated; use the official vendor page.'
                }
            }
        }
        'AuthenticatedSftp' {
            $result.Action = 'AuthenticatedSftp'
            $result.Available = $true
            $result.Label = 'Browse vendor software'
            $result.Detail = 'Uses a curated public catalog and an authorized per-user SFTP credential; installers are not redistributed.'
        }
        'ParentProvider' {
            $cached = $null
            if ($AvailableVersion -match '^\d+(?:\.\d+){1,3}$') {
                try { $cached = Resolve-AVWorkstationToolkitVendorCachePayload -Package $Package -Version $AvailableVersion -DataRoot $DataRoot }
                catch { $cached = [pscustomobject]@{ Valid=$false; Path=''; Detail=$_.Exception.Message } }
            }
            if ($null -ne $cached -and $cached.Valid) {
                $result.Action = 'ShowFile'
                $result.Available = $true
                $result.Label = 'Show cached installer'
                $result.Path = [string]$cached.Path
                $result.Detail = 'Reveal the previously downloaded installer in Explorer. ' + [string]$cached.Detail
            }
            else {
                $result.Action = 'AuthenticatedSftp'
                $result.Available = $true
                $result.Label = 'Get from parent provider'
                $result.Detail = 'Uses the shared authenticated provider, its product allowlist, and the same host-key and publisher validation controls.'
            }
        }
        'Awareness' {
            if (-not [string]::IsNullOrWhiteSpace([string]$Package.OfficialProductUri)) {
                $result.Action = 'OpenUri'
                $result.Available = $true
                $result.Label = if ($Package.DeploymentClass -eq 'WebOnly') { 'Open official service' } else { 'Open official product' }
                $result.Uri = [string]$Package.OfficialProductUri
                $result.Detail = 'Catalog awareness only; AV Workstation Toolkit does not download or execute this product.'
            }
        }
    }

    [pscustomobject]$result
}

function New-AVWorkstationToolkitPlanItem {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Package,
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)]$Delivery
    )

    $effectiveRisk = [string]$Package.Risk
    if ($effectiveRisk -eq 'None') {
        if ($Package.InstallsDriver -eq $true) { $effectiveRisk = 'Driver' }
        elseif ($Package.InstallsService -eq $true) { $effectiveRisk = 'Service' }
        elseif ($Package.OpensListener -eq $true) { $effectiveRisk = 'Listener' }
    }

    [pscustomobject]@{
        Selected = $false
        CanSelect = $State.Action -in @('Install','Update')
        Order = $Package.Order
        Profile = $Package.Profile
        Name = $Package.Name
        Id = $Package.Id
        Provider = $Package.Provider
        KnownVersion = $Package.KnownVersion
        Risk = $effectiveRisk
        Note = $Package.Note
        Deployment = $Package.Deployment
        Maintenance = $Package.Maintenance
        InstallerMode = [string](Get-AVWorkstationToolkitValue $Package 'InstallerMode' 'Silent')
        Installed = $State.Installed
        UpgradeAvailable = $State.UpgradeAvailable
        InstalledVersion = $State.InstalledVersion
        InstalledVersions = @($State.InstalledVersions)
        AvailableVersion = $State.AvailableVersion
        Status = $State.Status
        StatusDetail = $State.StatusDetail
        Action = $State.Action
        ReleaseCheckDetail = $State.ReleaseCheckDetail
        InventoryQuality = [string](Get-AVWorkstationToolkitValue $State 'InventoryQuality' '')
        ReleaseMode = $Package.ReleaseMode
        ReleaseUri = $Package.ReleaseUri
        ReleaseChannel = $Package.ReleaseChannel
        DeliveryMode = $Package.DeliveryMode
        DeliveryAction = $Delivery.Action
        DeliveryAvailable = $Delivery.Available
        DeliveryLabel = $Delivery.Label
        DeliveryUri = $Delivery.Uri
        DeliveryPath = $Delivery.Path
        DeliveryDetail = $Delivery.Detail
        DeliveryProviderId = $Delivery.ProviderId
        DeliveryProductId = $Delivery.ProductId
        Vendor = $Package.Vendor
        ProductFamily = $Package.ProductFamily
        ApplicationType = @($Package.ApplicationType)
        ParentProviderId = $Package.ParentProviderId
        Priority = $Package.Priority
        Roles = @($Package.Roles)
        DeploymentClass = $Package.DeploymentClass
        CatalogMaintenancePolicy = $Package.CatalogMaintenancePolicy
        VersionRule = $Package.VersionRule
        VersionCoupling = $Package.VersionCoupling
        VersionCouplingTargetId = $Package.VersionCouplingTargetId
        VersionCouplingNotes = $Package.VersionCouplingNotes
        CurrentOrLegacy = $Package.CurrentOrLegacy
        LicensingModel = @($Package.LicensingModel)
        DownloadAccess = @($Package.DownloadAccess)
        DownloadDifficulty = $Package.DownloadDifficulty
        RequiresVendorAccount = $Package.RequiresVendorAccount
        RequiresDealerAccount = $Package.RequiresDealerAccount
        RequiresTraining = $Package.RequiresTraining
        RequiresLicense = $Package.RequiresLicense
        RequiresSubscription = $Package.RequiresSubscription
        InstallsDriver = $Package.InstallsDriver
        InstallsService = $Package.InstallsService
        OpensListener = $Package.OpensListener
        FirmwareUtility = $Package.FirmwareUtility
        Architecture = @($Package.Architecture)
        SupportedOS = @($Package.SupportedOS)
        SideBySideSupported = $Package.SideBySideSupported
        OfficialDownloadUri = $Package.OfficialDownloadUri
        OfficialProductUri = $Package.OfficialProductUri
        ValidationMethod = @($Package.ValidationMethod)
        CatalogNotes = $Package.CatalogNotes
        DistributionPolicy = $Package.DistributionPolicy
        WorkflowCategories = @($Package.WorkflowCategories)
        InstallationForms = @($Package.InstallationForms)
        MetadataVerifiedOn = $Package.MetadataVerifiedOn
        MetadataVerificationState = $Package.MetadataVerificationState
        MetadataReviewTriggers = @($Package.MetadataReviewTriggers)
        MetadataQuarantined = $Package.MetadataQuarantined
        MetadataQuarantineReason = $Package.MetadataQuarantineReason
        AuthoritativeDomain = $Package.AuthoritativeDomain
        ExpectedPublisher = $Package.ExpectedPublisher
        SignatureValidation = $Package.SignatureValidation
        VendorHashAvailability = $Package.VendorHashAvailability
        DownloadStrategy = $Package.DownloadStrategy
        CatalogTags = @($Package.CatalogTags)
    }
}

function Get-AVWorkstationToolkitCatalogVendors {
    [CmdletBinding()]
    param([AllowNull()][object[]]$Catalog)

    if (-not $PSBoundParameters.ContainsKey('Catalog') -or $null -eq $Catalog) {
        $Catalog = @(Get-AVWorkstationToolkitCatalog)
    }

    $vendors = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in @($Catalog)) {
        $vendor = [string](Get-AVWorkstationToolkitValue $item 'Vendor' '')
        if (-not [string]::IsNullOrWhiteSpace($vendor)) {
            [void]$vendors.Add($vendor.Trim())
        }
    }
    return @($vendors | Sort-Object)
}

function Resolve-AVWorkstationToolkitCatalogVendorSelection {
    [CmdletBinding()]
    param(
        [AllowNull()][object[]]$Catalog,
        [AllowNull()][string]$SelectedVendor
    )

    $vendors = @(Get-AVWorkstationToolkitCatalogVendors -Catalog $Catalog)
    if ([string]::IsNullOrWhiteSpace($SelectedVendor) -or $SelectedVendor -eq 'All') { return 'All' }
    $match = @($vendors | Where-Object { $_.Equals($SelectedVendor.Trim(),[StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)
    return $(if ($match.Count -eq 1) { $match[0] } else { 'All' })
}

function Test-AVWorkstationToolkitCatalogFilter {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Item,
        [string[]]$Profiles = @('Standard','Field','Developer','Optional'),
        [ValidateSet('All','P1','Onsite','Free','FreePublic','Dealer','Licensed','Drivers','Services','Firmware','Current','Legacy','Unmanaged','InstalledSourceLimited')]
        [string]$Preset = 'All',
        [string]$Vendor = 'All',
        [ValidateSet('All','DSP','AudioNetworking','AVoIP','RF','Conferencing','Displays','DvLED','Control','Broadcast','MediaShow','Lighting','Intercom','Utilities','Measurement','FirmwareCommissioning','Development')]
        [string]$Discipline = 'All',
        [string]$Search = ''
    )

    if ([string](Get-AVWorkstationToolkitValue $Item 'Profile' '') -notin @($Profiles)) { return $false }

    if (-not [string]::IsNullOrWhiteSpace($Vendor) -and $Vendor -ne 'All') {
        $itemVendor = [string](Get-AVWorkstationToolkitValue $Item 'Vendor' '')
        if (-not $itemVendor.Trim().Equals($Vendor.Trim(),[StringComparison]::OrdinalIgnoreCase)) { return $false }
    }

    $licenses = @((Get-AVWorkstationToolkitValue $Item 'LicensingModel' @()))
    $access = @((Get-AVWorkstationToolkitValue $Item 'DownloadAccess' @()))
    $priority = [string](Get-AVWorkstationToolkitValue $Item 'Priority' '')
    $deploymentClass = [string](Get-AVWorkstationToolkitValue $Item 'DeploymentClass' '')
    $presetMatch = switch ($Preset) {
        'P1'       { $priority -eq 'P1' }
        'Onsite'   { $priority -eq 'P1' -and 'Windows' -in @((Get-AVWorkstationToolkitValue $Item 'SupportedOS' @())) -and $deploymentClass -notin @('WebOnly','ServerOnly','Embedded') }
        'Free'     { 'FREE' -in $licenses }
        'FreePublic' {
            $gated = @($access | Where-Object { $_ -in @('EMAIL-FORM','ACCOUNT','REGISTERED','DEALER','TRAINING','PORTAL','CONTACT','LICENSE-PORTAL') })
            'FREE' -in $licenses -and 'PUBLIC-DL' -in $access -and $gated.Count -eq 0 -and
                (Get-AVWorkstationToolkitValue $Item 'RequiresVendorAccount' $null) -ne $true -and
                (Get-AVWorkstationToolkitValue $Item 'RequiresDealerAccount' $null) -ne $true -and
                (Get-AVWorkstationToolkitValue $Item 'RequiresTraining' $null) -ne $true
        }
        'Dealer'   { (Get-AVWorkstationToolkitValue $Item 'RequiresDealerAccount' $null) -eq $true -or 'DEALER' -in $access }
        'Licensed' { @($licenses | Where-Object { $_ -in @('LICENSE','SUBSCRIPTION','HARDWARE-LICENSE','DEALER-LICENSE') }).Count -gt 0 }
        'Drivers'  { (Get-AVWorkstationToolkitValue $Item 'InstallsDriver' $null) -eq $true }
        'Services' { (Get-AVWorkstationToolkitValue $Item 'InstallsService' $null) -eq $true -or (Get-AVWorkstationToolkitValue $Item 'OpensListener' $null) -eq $true }
        'Firmware' { (Get-AVWorkstationToolkitValue $Item 'FirmwareUtility' $null) -eq $true }
        'Current'  { [string](Get-AVWorkstationToolkitValue $Item 'CurrentOrLegacy' '') -eq 'Current' }
        'Legacy'   { [string](Get-AVWorkstationToolkitValue $Item 'CurrentOrLegacy' '') -in @('Legacy','Transition','CompatibilityUnverified','Discontinued') }
        'Unmanaged'{ [string](Get-AVWorkstationToolkitValue $Item 'Provider' '') -eq 'External' -and $deploymentClass -ne 'Managed' }
        'InstalledSourceLimited' {
            (Get-AVWorkstationToolkitValue $Item 'Installed' $false) -eq $true -and @($access | Where-Object { $_ -in @('NO-DL','LEGACY-ARCHIVE','UNKNOWN-ACCESS') }).Count -gt 0
        }
        default    { $true }
    }
    if (-not $presetMatch) { return $false }

    $types = @((Get-AVWorkstationToolkitValue $Item 'ApplicationType' @()))
    $disciplineTypes = switch ($Discipline) {
        'DSP'                   { @('DSPAudio','AmplifierManagement') }
        'AudioNetworking'       { @('AudioNetworking') }
        'AVoIP'                 { @('AVoIP') }
        'RF'                    { @('WirelessRF') }
        'Conferencing'          { @('Conferencing','CameraPTZ') }
        'Displays'              { @('DisplayProjector','DigitalSignage') }
        'DvLED'                 { @('DvLEDVideoWall') }
        'Control'               { @('ControlSystem') }
        'Broadcast'             { @('BroadcastVideo') }
        'MediaShow'             { @('MediaServerShowControl') }
        'Lighting'              { @('LightingControl') }
        'Intercom'              { @('Intercom') }
        'Utilities'             { @('FieldUtility','NetworkUtility','SerialUtility','UsbDiagnostic','EDIDHDCP') }
        'Measurement'           { @('AudioMeasurement','LoudspeakerPrediction') }
        'FirmwareCommissioning' { @('FirmwareUtility') }
        'Development'           { @('Development') }
        default                 { @() }
    }
    if ($Discipline -ne 'All' -and @($types | Where-Object { $_ -in $disciplineTypes }).Count -eq 0) {
        if ($Discipline -ne 'FirmwareCommissioning' -or (Get-AVWorkstationToolkitValue $Item 'FirmwareUtility' $null) -ne $true) { return $false }
    }

    if (-not [string]::IsNullOrWhiteSpace($Search)) {
        $searchText = @(
            [string](Get-AVWorkstationToolkitValue $Item 'Name' ''),[string](Get-AVWorkstationToolkitValue $Item 'Id' ''),
            [string](Get-AVWorkstationToolkitValue $Item 'Vendor' ''),[string](Get-AVWorkstationToolkitValue $Item 'ProductFamily' ''),
            [string](Get-AVWorkstationToolkitValue $Item 'Note' ''),[string](Get-AVWorkstationToolkitValue $Item 'CatalogNotes' ''),
            $types,@((Get-AVWorkstationToolkitValue $Item 'Roles' @())),@((Get-AVWorkstationToolkitValue $Item 'SupportedOS' @())),
            @((Get-AVWorkstationToolkitValue $Item 'CatalogTags' @()))
        ) -join ' '
        if ($searchText.IndexOf($Search.Trim(),[StringComparison]::OrdinalIgnoreCase) -lt 0) { return $false }
    }
    return $true
}

function Find-AVWorkstationToolkitCatalog {
    [CmdletBinding()]
    param(
        [AllowNull()][object[]]$Catalog,
        [string]$Search,
        [string[]]$Vendor,
        [ValidateSet('P1','P2','UTILITY','DEV')][string[]]$Priority,
        [Alias('Profile')]
        [ValidateSet('Standard','Field','Developer','Optional')][string[]]$PackageProfile,
        [ValidateSet('ControlSystem','DSPAudio','AVoIP','AudioNetworking','WirelessRF','AudioMeasurement','LoudspeakerPrediction','AmplifierManagement','Conferencing','CameraPTZ','DisplayProjector','DigitalSignage','DvLEDVideoWall','Intercom','MediaServerShowControl','BroadcastVideo','LightingControl','FieldUtility','NetworkUtility','SerialUtility','UsbDiagnostic','EDIDHDCP','FirmwareUtility','Development','Driver','Service','Server','WebApplication','EmbeddedSoftware','LegacySupport')][string[]]$ApplicationType,
        [ValidateSet('AVEngineer','FieldService','ControlProgramming','DSPEngineering','NetworkEngineering','RFCoordination','Commissioning','DesignEngineering','BroadcastVideo','DigitalSignage','LightingProgramming','SystemAdministration','Development')][string[]]$Role,
        [ValidateSet('Managed','ManualHandoff','ParentProvider','InventoryOnly','AwarenessOnly','WebOnly','ServerOnly','Embedded')][string[]]$DeploymentClass,
        [ValidateSet('Latest','SameMajorMinor','ProjectPinned','ParentCatalog','InventoryOnly','EmbeddedFirmware','WebManaged','Unknown')][string[]]$VersionRule,
        [ValidateSet('Independent','ParentProvider','SameMajorMinor','ProjectPinned','FirmwarePaired','Unknown')][string[]]$VersionCoupling,
        [ValidateSet('FREE','FREEMIUM','PAID','LICENSE','SUBSCRIPTION','HARDWARE-LICENSE','DEALER-LICENSE','UNKNOWN-COST')][string[]]$LicensingModel,
        [ValidateSet('PUBLIC-DL','PUBLIC-PAGE','EMAIL-FORM','ACCOUNT','REGISTERED','DEALER','TRAINING','PORTAL','CONTACT','LICENSE-PORTAL','LEGACY-ARCHIVE','NO-DL','UNKNOWN-ACCESS')][string[]]$DownloadAccess,
        [ValidateSet('EASY','MODERATE','RESTRICTED','HARD')][string[]]$DownloadDifficulty,
        [ValidateSet('Windows','macOS','Linux','iOS','Android','Web','Embedded','Server','Unknown')][string[]]$SupportedOS,
        [ValidateSet('Current','Legacy','Transition','CompatibilityUnverified','Discontinued','Unknown')][string[]]$CurrentOrLegacy,
        [ValidateSet('Unknown','LinkOnly','VendorDownloadAllowed','Redistributable','PackageManagerOnly','ManualInstall','ReviewBeforeBundling')][string[]]$DistributionPolicy,
        [ValidateSet('NetworkCaptureTiming','DiscoveryReachability','ProtocolSocketTesting','SerialConsole','RemoteFileTransfer','UsbConferencing','VideoEdidSignal','AudioMeasurementAoIP','AVoIP','WindowsDiagnostics','FilesFirmwareComparison','ControlApis','ManufacturerPack','LegacyService')][string[]]$WorkflowCategory,
        [ValidateSet('Installed','Portable','MSI','EXE','ZIP','Store','WinGet','VendorPortal','WindowsInbox','Web','Embedded')][string[]]$InstallationForm,
        [ValidateSet('Current','ReviewSoon','VerificationRequired','Quarantined')][string[]]$MetadataVerificationState,
        [switch]$Free,
        [switch]$PublicWithoutAccount,
        [switch]$RequiresDealerAccount,
        [switch]$Licensed,
        [switch]$InstallsDriver,
        [switch]$InstallsService,
        [switch]$OpensListener,
        [switch]$FirmwareUtility,
        [switch]$KnownButUnmanaged,
        [switch]$InstalledOnly,
        [switch]$SourceUnavailable
    )

    if (-not $PSBoundParameters.ContainsKey('Catalog') -or $null -eq $Catalog) { $Catalog = @(Get-AVWorkstationToolkitCatalog) }
    $result = @($Catalog)
    if (-not [string]::IsNullOrWhiteSpace($Search)) {
        $needle = $Search.Trim()
        $result = @($result | Where-Object {
            $searchText = @(
                [string](Get-AVWorkstationToolkitValue $_ 'Name' ''),[string](Get-AVWorkstationToolkitValue $_ 'Id' ''),
                [string](Get-AVWorkstationToolkitValue $_ 'Vendor' ''),[string](Get-AVWorkstationToolkitValue $_ 'ProductFamily' ''),
                [string](Get-AVWorkstationToolkitValue $_ 'Note' ''),[string](Get-AVWorkstationToolkitValue $_ 'CatalogNotes' ''),
                @((Get-AVWorkstationToolkitValue $_ 'ApplicationType' @())),@((Get-AVWorkstationToolkitValue $_ 'Roles' @())),
                @((Get-AVWorkstationToolkitValue $_ 'CatalogTags' @())),@((Get-AVWorkstationToolkitValue $_ 'SupportedOS' @())),
                @((Get-AVWorkstationToolkitValue $_ 'WorkflowCategories' @())),@((Get-AVWorkstationToolkitValue $_ 'InstallationForms' @())),
                [string](Get-AVWorkstationToolkitValue $_ 'DistributionPolicy' ''),[string](Get-AVWorkstationToolkitValue $_ 'AuthoritativeDomain' '')
            ) -join ' '
            $searchText.IndexOf($needle,[StringComparison]::OrdinalIgnoreCase) -ge 0
        })
    }
    if ($PSBoundParameters.ContainsKey('Vendor')) { $result = @($result | Where-Object { [string](Get-AVWorkstationToolkitValue $_ 'Vendor' '') -in $Vendor }) }
    if ($PSBoundParameters.ContainsKey('Priority')) { $result = @($result | Where-Object { [string](Get-AVWorkstationToolkitValue $_ 'Priority' '') -in $Priority }) }
    if ($PSBoundParameters.ContainsKey('PackageProfile')) { $result = @($result | Where-Object { [string](Get-AVWorkstationToolkitValue $_ 'Profile' '') -in $PackageProfile }) }
    if ($PSBoundParameters.ContainsKey('ApplicationType')) { $result = @($result | Where-Object { @((Get-AVWorkstationToolkitValue $_ 'ApplicationType' @()) | Where-Object { $_ -in $ApplicationType }).Count -gt 0 }) }
    if ($PSBoundParameters.ContainsKey('Role')) { $result = @($result | Where-Object { @((Get-AVWorkstationToolkitValue $_ 'Roles' @()) | Where-Object { $_ -in $Role }).Count -gt 0 }) }
    if ($PSBoundParameters.ContainsKey('DeploymentClass')) { $result = @($result | Where-Object { [string](Get-AVWorkstationToolkitValue $_ 'DeploymentClass' '') -in $DeploymentClass }) }
    if ($PSBoundParameters.ContainsKey('VersionRule')) { $result = @($result | Where-Object { [string](Get-AVWorkstationToolkitValue $_ 'VersionRule' '') -in $VersionRule }) }
    if ($PSBoundParameters.ContainsKey('VersionCoupling')) { $result = @($result | Where-Object { [string](Get-AVWorkstationToolkitValue $_ 'VersionCoupling' '') -in $VersionCoupling }) }
    if ($PSBoundParameters.ContainsKey('LicensingModel')) { $result = @($result | Where-Object { @((Get-AVWorkstationToolkitValue $_ 'LicensingModel' @()) | Where-Object { $_ -in $LicensingModel }).Count -gt 0 }) }
    if ($PSBoundParameters.ContainsKey('DownloadAccess')) { $result = @($result | Where-Object { @((Get-AVWorkstationToolkitValue $_ 'DownloadAccess' @()) | Where-Object { $_ -in $DownloadAccess }).Count -gt 0 }) }
    if ($PSBoundParameters.ContainsKey('DownloadDifficulty')) { $result = @($result | Where-Object { [string](Get-AVWorkstationToolkitValue $_ 'DownloadDifficulty' '') -in $DownloadDifficulty }) }
    if ($PSBoundParameters.ContainsKey('SupportedOS')) { $result = @($result | Where-Object { @((Get-AVWorkstationToolkitValue $_ 'SupportedOS' @()) | Where-Object { $_ -in $SupportedOS }).Count -gt 0 }) }
    if ($PSBoundParameters.ContainsKey('CurrentOrLegacy')) { $result = @($result | Where-Object { [string](Get-AVWorkstationToolkitValue $_ 'CurrentOrLegacy' '') -in $CurrentOrLegacy }) }
    if ($PSBoundParameters.ContainsKey('DistributionPolicy')) { $result = @($result | Where-Object { [string](Get-AVWorkstationToolkitValue $_ 'DistributionPolicy' '') -in $DistributionPolicy }) }
    if ($PSBoundParameters.ContainsKey('WorkflowCategory')) { $result = @($result | Where-Object { @((Get-AVWorkstationToolkitValue $_ 'WorkflowCategories' @()) | Where-Object { $_ -in $WorkflowCategory }).Count -gt 0 }) }
    if ($PSBoundParameters.ContainsKey('InstallationForm')) { $result = @($result | Where-Object { @((Get-AVWorkstationToolkitValue $_ 'InstallationForms' @()) | Where-Object { $_ -in $InstallationForm }).Count -gt 0 }) }
    if ($PSBoundParameters.ContainsKey('MetadataVerificationState')) { $result = @($result | Where-Object { [string](Get-AVWorkstationToolkitValue $_ 'MetadataVerificationState' '') -in $MetadataVerificationState }) }
    if ($Free) { $result = @($result | Where-Object { 'FREE' -in @((Get-AVWorkstationToolkitValue $_ 'LicensingModel' @())) }) }
    if ($PublicWithoutAccount) {
        $result = @($result | Where-Object {
            $access = @((Get-AVWorkstationToolkitValue $_ 'DownloadAccess' @()))
            $gatedAccess = @('EMAIL-FORM','ACCOUNT','REGISTERED','DEALER','TRAINING','PORTAL','CONTACT','LICENSE-PORTAL')
            'PUBLIC-DL' -in $access -and
            @($access | Where-Object { $_ -in $gatedAccess }).Count -eq 0 -and
            (Get-AVWorkstationToolkitValue $_ 'RequiresVendorAccount' $null) -ne $true -and
            (Get-AVWorkstationToolkitValue $_ 'RequiresDealerAccount' $null) -ne $true -and
            (Get-AVWorkstationToolkitValue $_ 'RequiresTraining' $null) -ne $true
        })
    }
    if ($RequiresDealerAccount) { $result = @($result | Where-Object { (Get-AVWorkstationToolkitValue $_ 'RequiresDealerAccount' $null) -eq $true -or 'DEALER' -in @((Get-AVWorkstationToolkitValue $_ 'DownloadAccess' @())) }) }
    if ($Licensed) { $result = @($result | Where-Object { @((Get-AVWorkstationToolkitValue $_ 'LicensingModel' @()) | Where-Object { $_ -in @('LICENSE','SUBSCRIPTION','HARDWARE-LICENSE','DEALER-LICENSE') }).Count -gt 0 }) }
    if ($InstallsDriver) { $result = @($result | Where-Object { (Get-AVWorkstationToolkitValue $_ 'InstallsDriver' $null) -eq $true }) }
    if ($InstallsService) { $result = @($result | Where-Object { (Get-AVWorkstationToolkitValue $_ 'InstallsService' $null) -eq $true }) }
    if ($OpensListener) { $result = @($result | Where-Object { (Get-AVWorkstationToolkitValue $_ 'OpensListener' $null) -eq $true }) }
    if ($FirmwareUtility) { $result = @($result | Where-Object { (Get-AVWorkstationToolkitValue $_ 'FirmwareUtility' $null) -eq $true }) }
    if ($KnownButUnmanaged) { $result = @($result | Where-Object { [string](Get-AVWorkstationToolkitValue $_ 'Provider' '') -eq 'External' -and [string](Get-AVWorkstationToolkitValue $_ 'DeploymentClass' '') -ne 'Managed' }) }
    if ($InstalledOnly) { $result = @($result | Where-Object { (Get-AVWorkstationToolkitValue $_ 'Installed' $false) -eq $true }) }
    if ($SourceUnavailable) { $result = @($result | Where-Object { 'NO-DL' -in @((Get-AVWorkstationToolkitValue $_ 'DownloadAccess' @())) }) }
    return @($result | Sort-Object Vendor,ProductFamily,Name)
}

function Get-AVWorkstationToolkitPlan {
    [CmdletBinding()]
    param(
        [string]$CatalogPath = (Join-Path $PSScriptRoot '..\manifests\managed-applications.json'),
        [string]$ExternalCatalogPath,
        [string]$AwarenessCatalogPath,
        [AllowNull()][object[]]$InstalledPackages,
        [AllowNull()][string]$InstalledDiagnosticText,
        [AllowNull()][string]$InstalledText,
        [AllowNull()][string]$UpgradeText,
        [AllowNull()][object[]]$ExternalInventory,
        [AllowNull()][object[]]$ExternalReleaseInfo,
        [switch]$RefreshExternal,
        [string]$DistributionRoot,
        [string]$DataRoot,
        [AllowNull()]$RebootState,
        [string]$WingetVersion,
        [scriptblock]$StageCallback
    )

    $reportStage = {
        param([string]$Stage)
        if ($null -ne $StageCallback) { & $StageCallback $Stage }
    }

    $catalogArguments = @{ CatalogPath=$CatalogPath }
    if ($PSBoundParameters.ContainsKey('ExternalCatalogPath')) { $catalogArguments.ExternalCatalogPath = $ExternalCatalogPath }
    if ($PSBoundParameters.ContainsKey('AwarenessCatalogPath')) { $catalogArguments.AwarenessCatalogPath = $AwarenessCatalogPath }
    $catalog = @(Get-AVWorkstationToolkitCatalog @catalogArguments)
    $wingetCatalog = @($catalog | Where-Object Provider -eq 'WinGet')
    $externalCatalog = @($catalog | Where-Object Provider -eq 'External')
    $structuredInventory = $null
    $upgradeResult = $null

    & $reportStage 'Reading WinGet inventory...'
    if (-not $PSBoundParameters.ContainsKey('InstalledPackages') -and -not $PSBoundParameters.ContainsKey('InstalledText')) {
        $structuredInventory = Get-AVWorkstationToolkitWingetInventory
        $InstalledPackages = @($structuredInventory.Packages)
        $InstalledDiagnosticText = [string]$structuredInventory.RawOutput
    }
    if (-not $PSBoundParameters.ContainsKey('UpgradeText')) {
        $upgradeResult = Invoke-AVWorkstationToolkitWingetCapture -Arguments @('list','--upgrade-available','--source','winget','--disable-interactivity','--accept-source-agreements')
        $UpgradeText = $upgradeResult.Output
    }
    if ([string]::IsNullOrWhiteSpace($WingetVersion)) {
        $versionResult = Invoke-AVWorkstationToolkitWingetCapture -Arguments @('--version')
        $WingetVersion = if ($versionResult.ExitCode -eq 0) { $versionResult.Output.Trim() } else { 'Unavailable' }
    }

    $usesStructuredInventory = $PSBoundParameters.ContainsKey('InstalledPackages') -or $null -ne $structuredInventory
    $installedLookup = [System.Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $structuredInventoryValid = $true
    if ($usesStructuredInventory) {
        try {
            foreach ($record in @($InstalledPackages)) {
                if ($null -eq $record) { throw 'Structured inventory contains a null package.' }
                $id = if ($record.PSObject.Properties.Name -contains 'Id') { [string]$record.Id } elseif ($record.PSObject.Properties.Name -contains 'PackageIdentifier') { [string]$record.PackageIdentifier } else { '' }
                $version = if ($record.PSObject.Properties.Name -contains 'InstalledVersion') { [string]$record.InstalledVersion } elseif ($record.PSObject.Properties.Name -contains 'Version') { [string]$record.Version } else { '' }
                if ([string]::IsNullOrWhiteSpace($id) -or $id -ne $id.Trim() -or $id -notmatch '^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$') {
                    throw "Structured inventory contains an invalid package identifier: '$id'."
                }
                if ($version.Length -gt 256 -or $version -match '[\x00-\x1F\x7F]') {
                    throw "Structured inventory contains an invalid version for '$id'."
                }
                if (-not $installedLookup.ContainsKey($id)) {
                    $installedLookup.Add($id,[pscustomobject]@{ Id=$id; InstalledVersion=$version.Trim() })
                }
            }
        }
        catch { $structuredInventoryValid = $false }

        # winget export reports installed applications that it could not map
        # to a source. If one of those warnings names an otherwise-absent
        # catalog package, do not interpret the omission as permission to
        # install another copy.
        if ($structuredInventoryValid) {
            $structuredQuality = Get-AVWorkstationToolkitWingetStructuredInventoryQuality -InstalledPackages @($installedLookup.Values) -CatalogPackages $wingetCatalog -DiagnosticText $InstalledDiagnosticText
            if ($structuredQuality.Quality -ne 'Complete') { $structuredInventoryValid = $false }
        }
    }

    $installedInventoryAvailable = if ($usesStructuredInventory) {
        $structuredInventoryValid -and ($null -eq $structuredInventory -or $structuredInventory.Available)
    }
    else {
        Test-AVWorkstationToolkitInventoryTextReliable -Text $InstalledText
    }
    $upgradeInventoryAvailable = ($null -eq $upgradeResult -or $upgradeResult.ExitCode -eq 0)
    if ($upgradeInventoryAvailable) {
        try { [void]@(ConvertFrom-AVWorkstationToolkitWingetUpgradeText -Text $UpgradeText) }
        catch { $upgradeInventoryAvailable = $false }
    }
    $wingetAvailable = $WingetVersion -ne 'Unavailable' -and $installedInventoryAvailable -and $upgradeInventoryAvailable

    & $reportStage 'Reading installed AV software...'
    if (-not $PSBoundParameters.ContainsKey('ExternalInventory')) {
        $ExternalInventory = @(Get-AVWorkstationToolkitExternalInventory -Package $externalCatalog)
    }
    & $reportStage 'Checking vendor release information...'
    if (-not $PSBoundParameters.ContainsKey('ExternalReleaseInfo')) {
        if ($RefreshExternal) {
            $ExternalReleaseInfo = @(Get-AVWorkstationToolkitExternalReleaseInfo -Package $externalCatalog)
        }
        else {
            $ExternalReleaseInfo = @($externalCatalog | ForEach-Object {
                [pscustomobject]@{
                    Id = $_.Id
                    AvailableVersion = $_.KnownVersion
                    ObservedVersion = ''
                    OnlineChecked = $false
                    OnlineAvailable = $false
                    ReleaseUri = $_.ReleaseUri
                    DownloadUri = ''
                    Detail = if ($_.ReleaseMode -eq 'InventoryOnly') { 'Inventory-only provider; no online version comparison is performed.' } elseif ($_.ReleaseMode -eq 'ParentCatalog') { 'Parent-provider check not requested; the catalog baseline remains in use.' } else { "Online check not requested; catalog baseline $($_.KnownVersion) is in use." }
                }
            })
        }
    }
    & $reportStage 'Checking reboot state...'
    if (-not $PSBoundParameters.ContainsKey('RebootState') -or $null -eq $RebootState) {
        $RebootState = Get-AVWorkstationToolkitRebootState
    }
    & $reportStage 'Building workstation plan...'
    $externalInventoryLookup = [System.Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($record in @($ExternalInventory)) {
        if ($null -eq $record) { continue }
        $id = [string](Get-AVWorkstationToolkitValue $record 'Id' '')
        if (-not [string]::IsNullOrWhiteSpace($id) -and -not $externalInventoryLookup.ContainsKey($id)) {
            $externalInventoryLookup.Add($id,$record)
        }
    }
    $externalReleaseLookup = [System.Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($record in @($ExternalReleaseInfo)) {
        if ($null -eq $record) { continue }
        $id = [string](Get-AVWorkstationToolkitValue $record 'Id' '')
        if (-not [string]::IsNullOrWhiteSpace($id) -and -not $externalReleaseLookup.ContainsKey($id)) {
            $externalReleaseLookup.Add($id,$record)
        }
    }

    $items = [System.Collections.Generic.List[object]]::new()
    $noDelivery = [pscustomobject]@{ Action='None'; Available=$false; Label=''; Uri=''; Path=''; Detail=''; ProviderId=''; ProductId='' }

    foreach ($package in $catalog) {
        if ($package.Provider -eq 'WinGet') {
            $state = Get-AVWorkstationToolkitWinGetPackageState -Package $package -InstalledLookup $installedLookup `
                -UsesStructuredInventory:$usesStructuredInventory -WingetAvailable:$wingetAvailable `
                -InstalledText $InstalledText -UpgradeText $UpgradeText
            $delivery = $noDelivery
        }
        else {
            $inventoryRecord = if ($externalInventoryLookup.ContainsKey($package.Id)) { $externalInventoryLookup[$package.Id] } else { $null }
            $releaseRecord = if ($externalReleaseLookup.ContainsKey($package.Id)) { $externalReleaseLookup[$package.Id] } else { $null }
            $state = Get-AVWorkstationToolkitExternalPackageState -Package $package -InventoryRecord $inventoryRecord -ReleaseRecord $releaseRecord
            $delivery = Get-AVWorkstationToolkitExternalDeliveryState -Package $package -ReleaseRecord $releaseRecord `
                -AvailableVersion $state.AvailableVersion -DistributionRoot $DistributionRoot -DataRoot $DataRoot
        }
        $items.Add((New-AVWorkstationToolkitPlanItem -Package $package -State $state -Delivery $delivery)) | Out-Null
    }

    $summary = [pscustomobject]@{
        Total            = $items.Count
        Current          = @($items | Where-Object Status -eq 'Current').Count
        Missing          = @($items | Where-Object Status -eq 'Missing').Count
        Updates          = @($items | Where-Object Status -eq 'UpdateAvailable').Count
        ManualUpdates    = @($items | Where-Object Status -eq 'ManualUpdate').Count
        Held             = @($items | Where-Object Status -eq 'Held').Count
        Manual           = @($items | Where-Object Status -eq 'Manual').Count
        Inventory        = @($items | Where-Object Status -eq 'Inventory').Count
        NotDetected      = @($items | Where-Object Status -eq 'NotDetected').Count
        Awareness        = @($items | Where-Object Status -eq 'Awareness').Count
        InventoryIncomplete = @($items | Where-Object Status -eq 'InventoryIncomplete').Count
        InventoryUnavailable = @($items | Where-Object Status -eq 'InventoryUnavailable').Count
        CheckUnavailable = @($items | Where-Object Status -eq 'CheckUnavailable').Count
        Errors           = @($items | Where-Object Status -eq 'Error').Count
        Selectable       = @($items | Where-Object CanSelect).Count
    }

    $externalSourceStatuses = @()
    foreach ($record in @($ExternalInventory)) {
        $candidateStatuses = @((Get-AVWorkstationToolkitValue $record 'SourceStatuses' @()))
        if ($candidateStatuses.Count -gt 0) { $externalSourceStatuses = $candidateStatuses; break }
    }
    $availableExternalSources = @($externalSourceStatuses | Where-Object Available).Count
    $externalInventoryQuality = if ($externalSourceStatuses.Count -eq 0) { 'Provided' }
        elseif ($availableExternalSources -eq $externalSourceStatuses.Count) { 'Complete' }
        elseif ($availableExternalSources -gt 0) { 'Partial' }
        else { 'Unavailable' }
    $externalInventorySummary = [pscustomobject]@{
        Mode = 'WindowsUninstallRegistry'
        Quality = $externalInventoryQuality
        Sources = @($externalSourceStatuses)
        AvailableSourceCount = $availableExternalSources
        TotalSourceCount = $externalSourceStatuses.Count
        WarningCount = @($items | Where-Object { $_.Provider -eq 'External' -and $_.Status -in @('InventoryIncomplete','InventoryUnavailable','CheckUnavailable') }).Count
        ErrorCount = @($items | Where-Object { $_.Provider -eq 'External' -and $_.Status -eq 'Error' }).Count
        Detail = if ($externalInventoryQuality -eq 'Partial') {
            '{0} of {1} registry sources unavailable. Some installation states may be incomplete.' -f ($externalSourceStatuses.Count-$availableExternalSources),$externalSourceStatuses.Count
        }
        elseif ($externalInventoryQuality -eq 'Unavailable') { 'All uninstall-registry sources are unavailable.' }
        elseif ($externalInventoryQuality -eq 'Complete') { 'All uninstall-registry sources were read successfully.' }
        else { 'External inventory was supplied by the caller without source diagnostics.' }
    }

    $plan = [pscustomobject]@{
        GeneratedAt       = (Get-Date).ToString('o')
        Computer          = $env:COMPUTERNAME
        Elevated          = Test-AVWorkstationToolkitElevated
        WingetVersion     = $WingetVersion
        WingetAvailable   = $wingetAvailable
        InventoryMode     = if ($usesStructuredInventory) { 'WinGetExportJson' } else { 'LegacyTextFixture' }
        ExternalInventoryMode = 'WindowsUninstallRegistry'
        ExternalInventory = $externalInventorySummary
        Reboot            = $RebootState
        Summary           = $summary
        Packages          = @($items)
        InstalledRaw      = $InstalledText
        UpgradesRaw       = $UpgradeText
    }
    & $reportStage 'Ready'
    return $plan
}

function Assert-AVWorkstationToolkitRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Install','Update')][string]$Action,
        [Parameter(Mandatory)][string[]]$PackageId,
        [Parameter(Mandatory)]$Plan,
        [switch]$RiskAcknowledged
    )

    $ids = @($PackageId | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    if ($ids.Count -eq 0) { throw 'No packages were selected.' }

    $selected = [System.Collections.Generic.List[object]]::new()
    foreach ($id in $ids) {
        $item = @($Plan.Packages | Where-Object Id -eq $id)
        if ($item.Count -ne 1) { throw "Package is not in the approved catalog: $id" }
        $item = $item[0]

        if ($Action -eq 'Install' -and $item.Action -ne 'Install') {
            throw "Package is not eligible for installation: $id ($($item.Status))."
        }
        if ($Action -eq 'Update' -and $item.Action -ne 'Update') {
            throw "Package is not eligible for update: $id ($($item.Status))."
        }
        if ($Plan.Reboot.Pending -and $item.Risk -ne 'None') {
            throw "Risk-bearing package is blocked while reboot pending: $id ($($item.Risk); $($Plan.Reboot.Summary))."
        }
        if ($item.Risk -ne 'None' -and -not $RiskAcknowledged) {
            throw "Explicit risk acknowledgement is required for $id ($($item.Risk))."
        }
        $selected.Add($item) | Out-Null
    }
    return @($selected)
}

function Get-AVWorkstationToolkitWingetArguments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Install','Update')][string]$Action,
        [Parameter(Mandatory)]$Package
    )

    $verb = if ($Action -eq 'Install') { 'install' } else { 'upgrade' }
    $arguments = @(
        $verb,'--id',$Package.Id,'--exact','--source','winget',
        '--accept-package-agreements','--accept-source-agreements'
    )
    if ($Package.Risk -eq 'None') {
        # --silent makes WinGet pass /quiet to the installer, including to 'msiexec /x <ProductCode>'
        # for a manifest that upgrades by uninstalling the previous version. A machine-scope MSI
        # uninstall cannot obtain elevation under /quiet for a standard user, so msiexec returns 1603
        # and WinGet reports 0x8A150030. A reviewed InstallerDefault package omits the flag.
        # Re-resolved here rather than trusted: this function is exported, so a caller can supply a
        # Package object that never passed the catalog loader. The same contract therefore applies at
        # both boundaries, including the wrong-type rejection.
        $mode = Resolve-AVWorkstationToolkitInstallerMode -InputObject $Package -PackageId ([string]$Package.Id)
        $isSilent = [string]::Equals($mode,'Silent',[StringComparison]::Ordinal)
        if ($isSilent) { $arguments += '--silent' }
        # Always retained for a low-risk package: WinGet itself never waits on a prompt.
        $arguments += '--disable-interactivity'
    }
    return @($arguments)
}

function New-AVWorkstationToolkitActionRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Install','Update')][string]$Action,
        [Parameter(Mandatory)][string[]]$PackageId,
        [bool]$RiskAcknowledged = $false,
        [bool]$DryRun = $false,
        [string]$RequestsRoot = (Join-Path (Get-AVWorkstationToolkitDataRoot) 'logs\requests')
    )

    $ids = @($PackageId | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($ids.Count -eq 0) { throw 'An action request requires at least one package ID.' }
    if ($ids.Count -gt 100) { throw 'An action request cannot contain more than 100 package IDs.' }

    $requestDirectory = [IO.Path]::GetFullPath($RequestsRoot)
    New-Item -ItemType Directory -Path $requestDirectory -Force | Out-Null
    $requestName = 'request-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'),([guid]::NewGuid().ToString('N').Substring(0,8))
    $requestPath = Join-Path $requestDirectory ($requestName + '.json')
    $request = [ordered]@{
        SchemaVersion = 2
        RequestId = $requestName
        Action = $Action
        PackageIds = @($ids)
        RiskAcknowledged = $RiskAcknowledged
        DryRun = $DryRun
        ManagedCatalogRevision = 0
    }
    $request | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $requestPath -Encoding UTF8

    [pscustomobject]@{
        Name = $requestName
        RequestPath = $requestPath
        ProgressPath = Join-Path $requestDirectory ($requestName + '.progress.jsonl')
        ResultPath = Join-Path $requestDirectory ($requestName + '.result.json')
        CancelPath = Join-Path $requestDirectory ($requestName + '.cancel')
    }
}

function ConvertTo-AVWorkstationToolkitProcessArgument {
    param([AllowEmptyString()][string]$Value)

    if ($null -eq $Value) { throw 'Process arguments cannot be null.' }
    if ($Value -match '[\x00\r\n]') { throw 'Process arguments cannot contain nulls or line breaks.' }

    # Windows CommandLineToArgvW quoting: double backslashes that precede a
    # quote, escape the quote, and double trailing backslashes before the
    # closing quote. AV Workstation Toolkit always quotes every argument for one predictable
    # representation under Windows PowerShell 5.1/.NET Framework.
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * ($backslashes * 2 + 1)))
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$builder.Append(('\' * $backslashes))
            $backslashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashes -gt 0) { [void]$builder.Append(('\' * ($backslashes * 2))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Start-AVWorkstationToolkitDirectProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory,
        [switch]$CreateNoWindow
    )

    if (-not [IO.Path]::IsPathRooted($FilePath)) { throw 'Direct process paths must be absolute.' }
    $resolvedFilePath = [IO.Path]::GetFullPath($FilePath)
    if (-not (Test-Path -LiteralPath $resolvedFilePath -PathType Leaf)) {
        throw "Approved process executable was not found: $resolvedFilePath"
    }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $resolvedFilePath
    $startInfo.Arguments = (@($Arguments | ForEach-Object { ConvertTo-AVWorkstationToolkitProcessArgument -Value ([string]$_) }) -join ' ')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = [bool]$CreateNoWindow
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $resolvedWorkingDirectory = [IO.Path]::GetFullPath($WorkingDirectory)
        if (-not (Test-Path -LiteralPath $resolvedWorkingDirectory -PathType Container)) {
            throw "Approved process working directory was not found: $resolvedWorkingDirectory"
        }
        $startInfo.WorkingDirectory = $resolvedWorkingDirectory
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        $process.Dispose()
        throw "Windows did not start the approved process: $resolvedFilePath"
    }
    return $process
}

function Get-AVWorkstationToolkitExplorerArgumentString {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$SelectFile
    )

    $quotedPath = ConvertTo-AVWorkstationToolkitProcessArgument -Value $Path
    if ($SelectFile) { return '/select,' + $quotedPath }
    return $quotedPath
}

function Open-AVWorkstationToolkitExplorerPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$SelectFile
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if ($SelectFile) {
        if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) { throw "The file to show in Explorer was not found: $resolvedPath" }
        # Explorer uses its own /select,<path> grammar rather than normal argv
        # tokenization. Keep the switch outside the quoted path so Explorer
        # selects the file instead of falling back to the current directory.
        $argumentString = Get-AVWorkstationToolkitExplorerArgumentString -Path $resolvedPath -SelectFile
    }
    else {
        if (-not (Test-Path -LiteralPath $resolvedPath -PathType Container)) { throw "The directory to open in Explorer was not found: $resolvedPath" }
        $argumentString = Get-AVWorkstationToolkitExplorerArgumentString -Path $resolvedPath
    }
    $explorerPath = [IO.Path]::GetFullPath((Join-Path $env:SystemRoot 'explorer.exe'))
    if (-not (Test-Path -LiteralPath $explorerPath -PathType Leaf)) { throw "Windows Explorer was not found: $explorerPath" }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $explorerPath
    $startInfo.Arguments = $argumentString
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $false
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        $process.Dispose()
        throw 'Windows did not open the approved Explorer handoff.'
    }
    $process.Dispose()
}

function Open-AVWorkstationToolkitHttpsUri {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Uri)

    $validatedUri = Assert-AVWorkstationToolkitHttpsUri -Value $Uri -Field 'Browser handoff URI'
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $validatedUri
    $startInfo.UseShellExecute = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        $process.Dispose()
        throw 'Windows did not open the approved HTTPS address.'
    }
    $process.Dispose()
}

function Start-AVWorkstationToolkitWorker {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RequestPath,
        [string]$DataRoot,
        [switch]$Wait
    )

    $WorkerPath = Join-Path $PSScriptRoot 'Invoke-AVWorkstationToolkitAction.ps1'
    $resolvedDataRoot = Get-AVWorkstationToolkitDataRoot -Path $DataRoot
    $requestsRoot = [IO.Path]::GetFullPath((Join-Path $resolvedDataRoot 'logs\requests'))
    $resolvedRequestPath = [IO.Path]::GetFullPath($RequestPath)
    if (-not (Test-Path -LiteralPath $WorkerPath -PathType Leaf)) { throw "AV Workstation Toolkit worker not found: $WorkerPath" }
    if (-not (Test-Path -LiteralPath $resolvedRequestPath -PathType Leaf)) { throw "AV Workstation Toolkit request not found: $resolvedRequestPath" }
    if (-not [IO.Path]::GetDirectoryName($resolvedRequestPath).Equals($requestsRoot,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetExtension($resolvedRequestPath) -ine '.json' -or
        [IO.Path]::GetFileNameWithoutExtension($resolvedRequestPath) -notmatch '^request-\d{8}-\d{6}-[a-f0-9]{8}$') {
        throw 'AV Workstation Toolkit workers accept only a direct request JSON child of the resolved logs\requests directory.'
    }
    if (Test-AVWorkstationToolkitElevated) { throw 'AV Workstation Toolkit workers must start from a standard-user PowerShell session.' }

    $powershellExe = [IO.Path]::GetFullPath((Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'))
    $arguments = @(
        '-NoProfile','-ExecutionPolicy','RemoteSigned','-File',[IO.Path]::GetFullPath($WorkerPath),
        '-RequestPath',$resolvedRequestPath,'-DataRoot',$resolvedDataRoot
    )
    $process = Start-AVWorkstationToolkitDirectProcess -FilePath $powershellExe -Arguments $arguments -WorkingDirectory $PSScriptRoot -CreateNoWindow
    if ($Wait) { $process.WaitForExit() }
    return $process
}

function Test-AVWorkstationToolkitInstalled {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Id)

    $inventory = Get-AVWorkstationToolkitWingetInventory
    return $inventory.Available -and @($inventory.Packages | Where-Object Id -eq $Id).Count -gt 0
}

function Test-AVWorkstationToolkitCurrent {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Id)

    if (-not (Test-AVWorkstationToolkitInstalled -Id $Id)) { return $false }
    $result = Invoke-AVWorkstationToolkitWingetCapture -Arguments @('list','--id',$Id,'--exact','--upgrade-available','--source','winget','--disable-interactivity','--accept-source-agreements')
    return $result.ExitCode -eq 0 -and (Test-AVWorkstationToolkitInventoryTextReliable -Text $result.Output) -and -not (Test-AVWorkstationToolkitIdInText -Text $result.Output -Id $Id)
}

function Remove-AVWorkstationToolkitAnsi {
    param([AllowNull()][string]$Text)
    if ($null -eq $Text) { return '' }
    $clean = [regex]::Replace($Text, '\x1B\][^\x07]*(?:\x07|\x1B\\)', '')
    $clean = [regex]::Replace($clean, '\x1B\[[0-?]*[ -/]*[@-~]', '')
    return [regex]::Replace($clean, '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]', ' ')
}

function Protect-AVWorkstationToolkitSensitiveText {
    param([AllowNull()][string]$Text)
    if ($null -eq $Text) { return '' }
    $redacted = Remove-AVWorkstationToolkitAnsi -Text $Text
    $redacted = [regex]::Replace($redacted, '(?i)(?<key>(?:password|passwd|pwd|token|secret|api[-_]?key|client[-_]?secret)\s*(?:=|:)\s*)(?:"[^"]*"|''[^'']*''|[^\s,;]+)', '${key}[REDACTED]')
    $redacted = [regex]::Replace($redacted, '(?i)(Authorization:\s*Bearer\s+)\S+', '$1[REDACTED]')
    return [regex]::Replace($redacted, '(?i)(://[^:/\s]+:)[^@\s]+@', '$1[REDACTED]@')
}

function Protect-AVWorkstationToolkitDiagnosticPath {
    param([Parameter(Mandatory)][string]$Path)

    $safePath = Protect-AVWorkstationToolkitSensitiveText -Text ([IO.Path]::GetFullPath($Path))
    $knownRoots = @(
        [pscustomobject]@{ Token='%LOCALAPPDATA%'; Path=[Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData) },
        [pscustomobject]@{ Token='%USERPROFILE%'; Path=[Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile) }
    )
    foreach ($root in $knownRoots) {
        if ([string]::IsNullOrWhiteSpace([string]$root.Path)) { continue }
        $resolvedRoot = [IO.Path]::GetFullPath([string]$root.Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if ($safePath.Equals($resolvedRoot,[StringComparison]::OrdinalIgnoreCase)) { return [string]$root.Token }
        $prefix = $resolvedRoot + [IO.Path]::DirectorySeparatorChar
        if ($safePath.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) {
            return [string]$root.Token + [IO.Path]::DirectorySeparatorChar + $safePath.Substring($prefix.Length)
        }
    }
    return $safePath
}

function Get-AVWorkstationToolkitDiagnostics {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Plan,
        [string]$DataRoot,
        [string]$LogsPath,
        [ValidateSet('Source','Packaged')][string]$ExecutionMode,
        [AllowNull()][string]$SelectedSdkVersion
    )

    if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = Get-AVWorkstationToolkitDataRoot }
    if ([string]::IsNullOrWhiteSpace($LogsPath)) { $LogsPath = Join-Path $DataRoot 'logs' }
    if ([string]::IsNullOrWhiteSpace($ExecutionMode)) {
        $ExecutionMode = if ([Environment]::GetEnvironmentVariable('AVWORKSTATIONTOOLKIT_PACKAGED','Process') -eq '1') { 'Packaged' } else { 'Source' }
    }
    if ($ExecutionMode -eq 'Source' -and -not $PSBoundParameters.ContainsKey('SelectedSdkVersion')) {
        try {
            $dotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
            $SelectedSdkVersion = [string](& $dotnet.Source --version 2>$null | Select-Object -First 1)
        }
        catch { $SelectedSdkVersion = 'Unavailable' }
    }
    elseif ($ExecutionMode -eq 'Packaged') { $SelectedSdkVersion = 'Not applicable' }

    $wingetPath = 'Unavailable'
    try { $wingetPath = [string](Get-AVWorkstationToolkitWingetCommand) }
    catch { }

    $external = Get-AVWorkstationToolkitValue $Plan 'ExternalInventory' ([pscustomobject]@{})
    $sources = if ($null -eq $external) { @() } else { @((Get-AVWorkstationToolkitValue $external 'Sources' @())) }
    $safeSources = @($sources | ForEach-Object {
        [pscustomobject]@{
            Name = [string](Get-AVWorkstationToolkitValue $_ 'Name' 'Unknown')
            Label = [string](Get-AVWorkstationToolkitValue $_ 'Label' (Get-AVWorkstationToolkitValue $_ 'Name' 'Unknown'))
            Status = if ([bool](Get-AVWorkstationToolkitValue $_ 'Available' $false)) { 'OK' } else { 'Failed' }
            EntryCount = [int](Get-AVWorkstationToolkitValue $_ 'EntryCount' 0)
            Detail = Protect-AVWorkstationToolkitSensitiveText -Text ([string](Get-AVWorkstationToolkitValue $_ 'Detail' ''))
        }
    })
    $summary = Get-AVWorkstationToolkitValue $Plan 'Summary' ([pscustomobject]@{})
    $packages = @((Get-AVWorkstationToolkitValue $Plan 'Packages' @()))
    $reboot = Get-AVWorkstationToolkitValue $Plan 'Reboot' ([pscustomobject]@{ Pending=$false; Reasons=@(); Summary='No supported pending reboot signals were found.' })
    $rebootReasons = @((Get-AVWorkstationToolkitValue $reboot 'Reasons' @()))
    $launcherVersion = [Environment]::GetEnvironmentVariable('AVWORKSTATIONTOOLKIT_LAUNCHER_VERSION','Process')
    $moduleVersion = if (-not [string]::IsNullOrWhiteSpace($launcherVersion)) { $launcherVersion }
        elseif ($null -ne $ExecutionContext.SessionState.Module) { $ExecutionContext.SessionState.Module.Version.ToString() }
        else { 'Unknown' }

    $diagnostics = [pscustomobject]@{
        SchemaVersion = 1
        GeneratedAt = (Get-Date).ToString('o')
        Application = [pscustomobject]@{
            Version = $moduleVersion
            ExecutionMode = $ExecutionMode
            DataRoot = Protect-AVWorkstationToolkitDiagnosticPath -Path $DataRoot
            LogsPath = Protect-AVWorkstationToolkitDiagnosticPath -Path $LogsPath
        }
        Runtime = [pscustomobject]@{
            WindowsVersion = [Environment]::OSVersion.VersionString
            WindowsPowerShellVersion = $PSVersionTable.PSVersion.ToString()
            LauncherFramework = Protect-AVWorkstationToolkitSensitiveText -Text $(if ($ExecutionMode -eq 'Packaged') { [Environment]::GetEnvironmentVariable('AVWORKSTATIONTOOLKIT_LAUNCHER_RUNTIME','Process') } else { 'Not applicable' })
            LauncherRuntimeVersion = Protect-AVWorkstationToolkitSensitiveText -Text $(if ($ExecutionMode -eq 'Packaged') { [Environment]::GetEnvironmentVariable('AVWORKSTATIONTOOLKIT_LAUNCHER_RUNTIME_VERSION','Process') } else { 'Not applicable' })
            SelectedDotNetSdk = Protect-AVWorkstationToolkitSensitiveText -Text $SelectedSdkVersion
            ProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        }
        WinGet = [pscustomobject]@{
            ExecutablePath = Protect-AVWorkstationToolkitSensitiveText -Text $wingetPath
            Version = Protect-AVWorkstationToolkitSensitiveText -Text ([string](Get-AVWorkstationToolkitValue $Plan 'WingetVersion' 'Unavailable'))
            InventoryAvailable = [bool](Get-AVWorkstationToolkitValue $Plan 'WingetAvailable' $false)
            InventoryMode = [string](Get-AVWorkstationToolkitValue $Plan 'InventoryMode' 'Unknown')
        }
        Privilege = [pscustomobject]@{
            State = if ([bool](Get-AVWorkstationToolkitValue $Plan 'Elevated' $false)) { 'Elevated' } else { 'Standard user' }
        }
        Reboot = [pscustomobject]@{
            Pending = [bool](Get-AVWorkstationToolkitValue $reboot 'Pending' $false)
            WindowsUpdate = 'Windows Update' -in $rebootReasons
            ComponentBasedServicing = 'Component Based Servicing' -in $rebootReasons
            Reasons = @($rebootReasons | ForEach-Object { Protect-AVWorkstationToolkitSensitiveText -Text ([string]$_) })
            Summary = Protect-AVWorkstationToolkitSensitiveText -Text ([string](Get-AVWorkstationToolkitValue $reboot 'Summary' ''))
        }
        ExternalInventory = [pscustomobject]@{
            Quality = [string](Get-AVWorkstationToolkitValue $external 'Quality' 'Unknown')
            Sources = $safeSources
            WarningCount = [int](Get-AVWorkstationToolkitValue $external 'WarningCount' 0)
            ErrorCount = [int](Get-AVWorkstationToolkitValue $external 'ErrorCount' 0)
            Detail = Protect-AVWorkstationToolkitSensitiveText -Text ([string](Get-AVWorkstationToolkitValue $external 'Detail' ''))
        }
        Catalog = [pscustomobject]@{
            TotalRecords = $packages.Count
            WinGetManagedRecords = @($packages | Where-Object Provider -eq 'WinGet').Count
            OperationalExternalRecords = @($packages | Where-Object { $_.Provider -eq 'External' -and $_.DeploymentClass -ne 'AwarenessOnly' }).Count
            AwarenessRecords = [int](Get-AVWorkstationToolkitValue $summary 'Awareness' 0)
            Current = [int](Get-AVWorkstationToolkitValue $summary 'Current' 0)
            Missing = [int](Get-AVWorkstationToolkitValue $summary 'Missing' 0)
            UpdateAvailable = [int](Get-AVWorkstationToolkitValue $summary 'Updates' 0)
            Manual = [int](Get-AVWorkstationToolkitValue $summary 'Manual' 0) + [int](Get-AVWorkstationToolkitValue $summary 'ManualUpdates' 0) + [int](Get-AVWorkstationToolkitValue $summary 'Held' 0)
            InventoryWarnings = [int](Get-AVWorkstationToolkitValue $summary 'InventoryIncomplete' 0) + [int](Get-AVWorkstationToolkitValue $summary 'InventoryUnavailable' 0) + [int](Get-AVWorkstationToolkitValue $summary 'CheckUnavailable' 0)
            Errors = [int](Get-AVWorkstationToolkitValue $summary 'Errors' 0)
        }
    }
    return $diagnostics
}

function ConvertTo-AVWorkstationToolkitDiagnosticsText {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Diagnostics)

    $sourceLines = @($Diagnostics.ExternalInventory.Sources | ForEach-Object {
        '  {0}: {1} ({2} entries){3}' -f $_.Label,$_.Status,$_.EntryCount,$(if ([string]::IsNullOrWhiteSpace([string]$_.Detail)) { '' } else { ' - ' + $_.Detail })
    })
    $lines = @(
        'AV Workstation Toolkit diagnostics',
        ('Generated: {0}' -f $Diagnostics.GeneratedAt),
        '',
        '[AVWorkstationToolkit]',
        ('Version: {0}' -f $Diagnostics.Application.Version),
        ('Execution: {0}' -f $Diagnostics.Application.ExecutionMode),
        ('Data root: {0}' -f $Diagnostics.Application.DataRoot),
        ('Logs: {0}' -f $Diagnostics.Application.LogsPath),
        '',
        '[Runtime]',
        ('Windows: {0}' -f $Diagnostics.Runtime.WindowsVersion),
        ('Windows PowerShell: {0}' -f $Diagnostics.Runtime.WindowsPowerShellVersion),
        ('Launcher framework: {0}' -f $Diagnostics.Runtime.LauncherFramework),
        ('Launcher runtime: {0}' -f $Diagnostics.Runtime.LauncherRuntimeVersion),
        ('.NET SDK: {0}' -f $Diagnostics.Runtime.SelectedDotNetSdk),
        ('Process architecture: {0}' -f $Diagnostics.Runtime.ProcessArchitecture),
        '',
        '[WinGet]',
        ('Path: {0}' -f $Diagnostics.WinGet.ExecutablePath),
        ('Version: {0}' -f $Diagnostics.WinGet.Version),
        ('Inventory available: {0}' -f $Diagnostics.WinGet.InventoryAvailable),
        ('Inventory mode: {0}' -f $Diagnostics.WinGet.InventoryMode),
        '',
        '[Privilege and reboot]',
        ('Privilege: {0}' -f $Diagnostics.Privilege.State),
        ('Pending reboot: {0}' -f $Diagnostics.Reboot.Pending),
        ('Windows Update: {0}' -f $Diagnostics.Reboot.WindowsUpdate),
        ('Component Based Servicing: {0}' -f $Diagnostics.Reboot.ComponentBasedServicing),
        ('Reboot detail: {0}' -f $Diagnostics.Reboot.Summary),
        '',
        '[External inventory]',
        ('Quality: {0}' -f $Diagnostics.ExternalInventory.Quality),
        $sourceLines,
        ('Warnings: {0}; errors: {1}' -f $Diagnostics.ExternalInventory.WarningCount,$Diagnostics.ExternalInventory.ErrorCount),
        ('Detail: {0}' -f $Diagnostics.ExternalInventory.Detail),
        '',
        '[Catalog]',
        ('Records: {0}; WinGet managed: {1}; operational external: {2}; awareness: {3}' -f $Diagnostics.Catalog.TotalRecords,$Diagnostics.Catalog.WinGetManagedRecords,$Diagnostics.Catalog.OperationalExternalRecords,$Diagnostics.Catalog.AwarenessRecords),
        ('Current: {0}; missing: {1}; updates: {2}; manual: {3}; inventory warnings: {4}; errors: {5}' -f $Diagnostics.Catalog.Current,$Diagnostics.Catalog.Missing,$Diagnostics.Catalog.UpdateAvailable,$Diagnostics.Catalog.Manual,$Diagnostics.Catalog.InventoryWarnings,$Diagnostics.Catalog.Errors)
    )
    return Protect-AVWorkstationToolkitSensitiveText -Text (($lines | ForEach-Object { [string]$_ }) -join "`r`n")
}

Export-ModuleMember -Function @(
    'Get-AVWorkstationToolkitDataRoot',
    'Invoke-AVWorkstationToolkitLegacyDataMigration',
    'Get-AVWorkstationToolkitDistributionRoot',
    'Get-AVWorkstationToolkitCatalog',
    'Find-AVWorkstationToolkitCatalog',
    'Get-AVWorkstationToolkitCatalogVendors',
    'Resolve-AVWorkstationToolkitCatalogVendorSelection',
    'Test-AVWorkstationToolkitCatalogFilter',
    'Test-AVWorkstationToolkitQuickViewMatch',
    'Get-AVWorkstationToolkitMetadataVerificationState',
    'ConvertFrom-AVWorkstationToolkitExternalCatalogJson',
    'Compare-AVWorkstationToolkitVersion',
    'ConvertTo-AVWorkstationToolkitVersionSortKey',
    'Get-AVWorkstationToolkitExternalInventory',
    'ConvertFrom-AVWorkstationToolkitExternalReleaseContent',
    'ConvertFrom-AVWorkstationToolkitExternalDownloadContent',
    'Get-AVWorkstationToolkitExternalReleaseInfo',
    'Resolve-AVWorkstationToolkitExternalPayload',
    'Resolve-AVWorkstationToolkitVendorCachePayload',
    'Complete-AVWorkstationToolkitVendorDownload',
    'Get-AVWorkstationToolkitAuthenticatedSftpCatalog',
    'Get-AVWorkstationToolkitTrustedSftpHost',
    'Set-AVWorkstationToolkitTrustedSftpHost',
    'Get-AVWorkstationToolkitRebootState',
    'Test-AVWorkstationToolkitElevated',
    'Get-AVWorkstationToolkitWingetCommand',
    'Invoke-AVWorkstationToolkitWingetCapture',
    'ConvertFrom-AVWorkstationToolkitWingetExportJson',
    'Get-AVWorkstationToolkitWingetStructuredInventoryQuality',
    'Get-AVWorkstationToolkitWingetInventory',
    'ConvertFrom-AVWorkstationToolkitWingetUpgradeText',
    'Test-AVWorkstationToolkitIdInText',
    'Get-AVWorkstationToolkitPlan',
    'Assert-AVWorkstationToolkitRequest',
    'Get-AVWorkstationToolkitWingetArguments',
    'New-AVWorkstationToolkitActionRequest',
    'Open-AVWorkstationToolkitExplorerPath',
    'Open-AVWorkstationToolkitHttpsUri',
    'Start-AVWorkstationToolkitWorker',
    'Test-AVWorkstationToolkitInstalled',
    'Test-AVWorkstationToolkitCurrent',
    'Remove-AVWorkstationToolkitAnsi',
    'Protect-AVWorkstationToolkitSensitiveText',
    'Get-AVWorkstationToolkitDiagnostics',
    'ConvertTo-AVWorkstationToolkitDiagnosticsText'
)
