<#
.SYNOPSIS
    Generates AV Workstation Toolkit's deterministic CycloneDX release SBOM.

.DESCRIPTION
    Reads the reviewed NuGet lock and pinned launcher/installer projects, emits
    package identities and content hashes without local paths, usernames, or
    machine metadata, and writes a stable CycloneDX 1.6 JSON document.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$CommitSha,
    [Parameter(Mandatory)][string]$LauncherSha256,
    [Parameter(Mandatory)][string]$WorkerSha256,
    [Parameter(Mandatory)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "SBOM version is invalid: $Version" }
if ($CommitSha -notmatch '^[a-fA-F0-9]{40}$') { throw 'SBOM commit SHA must contain exactly 40 hexadecimal characters.' }
if ($LauncherSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'SBOM launcher SHA-256 is invalid.' }
if ($WorkerSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'SBOM worker SHA-256 is invalid.' }

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$launcherProjectPath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj'
$lockPath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\packages.lock.json'
$installerProjectPath = Join-Path $repositoryRoot 'installer\AVWorkstationToolkit.Installer.wixproj'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$target = $lock.dependencies.'net10.0-windows7.0'
if ($null -eq $target) { throw 'NuGet lock does not contain the reviewed .NET 10 Windows dependency target.' }

$reviewedPackageMetadata = @{
    'BouncyCastle.Cryptography@2.7.0' = [ordered]@{
        License = 'MIT'
        Distribution = 'embedded'
        LicenseUrl = 'https://github.com/bcgit/bc-csharp/blob/4007498b13582d90ee1eda5d9920c324428b98b3/LICENSE.md'
    }
    'Microsoft.Extensions.DependencyInjection.Abstractions@8.0.2' = [ordered]@{
        License = 'MIT'
        Distribution = 'embedded'
        LicenseUrl = 'https://github.com/dotnet/runtime/blob/81cabf2857a01351e5ab578947c7403a5b128ad1/LICENSE.TXT'
    }
    'Microsoft.Extensions.Logging.Abstractions@8.0.3' = [ordered]@{
        License = 'MIT'
        Distribution = 'embedded'
        LicenseUrl = 'https://github.com/dotnet/runtime/blob/eba546b0f0d448e0176a2222548fd7a2fbf464c0/LICENSE.TXT'
    }
    'Microsoft.NET.ILLink.Tasks@10.0.11' = [ordered]@{
        License = 'MIT'
        Distribution = 'build-only'
        LicenseUrl = 'https://github.com/dotnet/dotnet/blob/e2f47b0110ed922f21a1522da67279133ce28f32/LICENSE.TXT'
    }
    'SSH.NET@2026.0.0' = [ordered]@{
        License = 'MIT'
        Distribution = 'embedded'
        LicenseUrl = 'https://github.com/sshnet/SSH.NET/blob/7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e/LICENSE'
    }
}

$components = [System.Collections.Generic.List[object]]::new()
$dependencyRecords = [System.Collections.Generic.List[object]]::new()
$packageRefs = @{}
foreach ($property in @($target.PSObject.Properties | Where-Object { $_.Value.PSObject.Properties.Name -contains 'resolved' } | Sort-Object Name)) {
    $name = [string]$property.Name
    $package = $property.Value
    $resolved = [string]$package.resolved
    if ([string]::IsNullOrWhiteSpace($resolved)) { throw "NuGet lock entry has no resolved version: $name" }
    $metadataKey = '{0}@{1}' -f $name,$resolved
    if (-not $reviewedPackageMetadata.ContainsKey($metadataKey)) {
        throw "NuGet dependency license and distribution status have not been reviewed: $metadataKey"
    }
    $reviewedMetadata = $reviewedPackageMetadata[$metadataKey]
    $reference = 'pkg:nuget/{0}@{1}' -f [Uri]::EscapeDataString($name),[Uri]::EscapeDataString($resolved)
    $packageRefs[$name] = $reference
    $hashes = @()
    if (-not [string]::IsNullOrWhiteSpace([string]$package.contentHash)) {
        $hashes = @([ordered]@{
            alg = 'SHA-512'
            content = [BitConverter]::ToString([Convert]::FromBase64String([string]$package.contentHash)).Replace('-','')
        })
    }
    $components.Add([ordered]@{
        type = 'library'
        'bom-ref' = $reference
        name = $name
        version = $resolved
        purl = $reference
        scope = $(if ($reviewedMetadata.Distribution -eq 'build-only') { 'excluded' } else { 'required' })
        hashes = $hashes
        licenses = @([ordered]@{ license = [ordered]@{ id = [string]$reviewedMetadata.License } })
        externalReferences = @([ordered]@{ type='license'; url=[string]$reviewedMetadata.LicenseUrl })
        properties = @(
            [ordered]@{ name='avworkstationtoolkit:dependency:type'; value=[string]$package.type },
            [ordered]@{ name='avworkstationtoolkit:dependency:distribution'; value=[string]$reviewedMetadata.Distribution }
        )
    }) | Out-Null
}

foreach ($property in @($target.PSObject.Properties | Where-Object { $_.Value.PSObject.Properties.Name -contains 'resolved' } | Sort-Object Name)) {
    $dependsOn = @()
    if ($property.Value.PSObject.Properties.Name -contains 'dependencies' -and $null -ne $property.Value.dependencies) {
        $dependsOn = @($property.Value.dependencies.PSObject.Properties.Name | Sort-Object | ForEach-Object { $packageRefs[[string]$_] })
    }
    $dependencyRecords.Add([ordered]@{
        ref = $packageRefs[[string]$property.Name]
        dependsOn = $dependsOn
    }) | Out-Null
}

[xml]$launcherProject = Get-Content -LiteralPath $launcherProjectPath -Raw
$runtimeNode = $launcherProject.SelectSingleNode('/Project/PropertyGroup/RuntimeFrameworkVersion')
$runtimeVersion = $(if ($null -eq $runtimeNode) { '' } else { [string]$runtimeNode.InnerText })
$reviewedRuntimeMetadata = @{
    '10.0.11' = [ordered]@{
        License = 'MIT'
        LicenseUrl = 'https://github.com/dotnet/dotnet/blob/e2f47b0110ed922f21a1522da67279133ce28f32/LICENSE.TXT'
        NoticesUrl = 'https://github.com/dotnet/dotnet/blob/e2f47b0110ed922f21a1522da67279133ce28f32/THIRD-PARTY-NOTICES.txt'
    }
}
if (-not $reviewedRuntimeMetadata.ContainsKey($runtimeVersion)) {
    throw "The .NET runtime license and notice set has not been reviewed: $runtimeVersion"
}
$runtimeMetadata = $reviewedRuntimeMetadata[$runtimeVersion]
$dotnetLicenseUrl = [string]$runtimeMetadata.LicenseUrl
$dotnetNoticesUrl = [string]$runtimeMetadata.NoticesUrl
$runtimeRef = 'pkg:nuget/Microsoft.NETCore.App.Runtime.win-x64@' + $runtimeVersion
$components.Add([ordered]@{
    type = 'framework'
    'bom-ref' = $runtimeRef
    name = 'Microsoft.NETCore.App.Runtime.win-x64'
    version = $runtimeVersion
    purl = $runtimeRef
    scope = 'required'
    licenses = @([ordered]@{ license = [ordered]@{ id='MIT' } })
    externalReferences = @(
        [ordered]@{ type='license'; url=$dotnetLicenseUrl },
        [ordered]@{ type='other'; url=$dotnetNoticesUrl; comment='.NET 10.0.11 third-party notices' }
    )
    properties = @(
        [ordered]@{ name='avworkstationtoolkit:dependency:phase'; value='runtime' },
        [ordered]@{ name='avworkstationtoolkit:dependency:distribution'; value='embedded' }
    )
}) | Out-Null

$hostRef = 'pkg:nuget/Microsoft.NETCore.App.Host.win-x64@' + $runtimeVersion
$components.Add([ordered]@{
    type = 'framework'
    'bom-ref' = $hostRef
    name = 'Microsoft.NETCore.App.Host.win-x64'
    version = $runtimeVersion
    purl = $hostRef
    scope = 'required'
    licenses = @([ordered]@{ license = [ordered]@{ id='MIT' } })
    externalReferences = @(
        [ordered]@{ type='license'; url=$dotnetLicenseUrl },
        [ordered]@{ type='other'; url=$dotnetNoticesUrl; comment='.NET 10.0.11 third-party notices' }
    )
    properties = @(
        [ordered]@{ name='avworkstationtoolkit:dependency:phase'; value='runtime' },
        [ordered]@{ name='avworkstationtoolkit:dependency:distribution'; value='embedded' }
    )
}) | Out-Null

[xml]$installerProject = Get-Content -LiteralPath $installerProjectPath -Raw
$wixSdk = [string]$installerProject.Project.Sdk
if ($wixSdk -notmatch '^WixToolset\.Sdk/(?<Version>\d+\.\d+\.\d+)$') { throw 'Pinned WiX SDK identity could not be read.' }
$wixVersion = $Matches.Version
$reviewedWixMetadata = @{
    '6.0.2' = [ordered]@{
        License = 'MS-RL'
        LicenseUrl = 'https://github.com/wixtoolset/wix/blob/v6.0.2/LICENSE.TXT'
        BinaryTerms = 'Open Source Maintenance Fee Agreement'
        BinaryTermsUrl = 'https://github.com/wixtoolset/wix/blob/v6.0.2/OSMFEULA.txt'
    }
}
if (-not $reviewedWixMetadata.ContainsKey($wixVersion)) {
    throw "The WiX SDK license and binary terms have not been reviewed: $wixVersion"
}
$wixMetadata = $reviewedWixMetadata[$wixVersion]
$wixRef = 'pkg:nuget/WixToolset.Sdk@' + $wixVersion
$components.Add([ordered]@{
    type = 'application'
    'bom-ref' = $wixRef
    name = 'WixToolset.Sdk'
    version = $wixVersion
    purl = $wixRef
    scope = 'excluded'
    licenses = @([ordered]@{ license = [ordered]@{ id=[string]$wixMetadata.License } })
    externalReferences = @(
        [ordered]@{ type='license'; url=[string]$wixMetadata.LicenseUrl },
        [ordered]@{ type='other'; url=[string]$wixMetadata.BinaryTermsUrl; comment=([string]$wixMetadata.BinaryTerms + ' for the downloaded WiX binary package') }
    )
    properties = @(
        [ordered]@{ name='avworkstationtoolkit:dependency:phase'; value='build' },
        [ordered]@{ name='avworkstationtoolkit:dependency:distribution'; value='build-only' },
        [ordered]@{ name='avworkstationtoolkit:wix:binary-terms'; value=[string]$wixMetadata.BinaryTerms }
    )
}) | Out-Null

$rootRef = 'pkg:generic/AVWorkstationToolkit@' + $Version
$rootDependencies = @($hostRef,$runtimeRef,$packageRefs['SSH.NET'])
$dependencyRecords.Insert(0,[ordered]@{ ref=$rootRef; dependsOn=$rootDependencies })
$dependencyRecords.Add([ordered]@{ ref=$hostRef; dependsOn=@() }) | Out-Null
$dependencyRecords.Add([ordered]@{ ref=$runtimeRef; dependsOn=@() }) | Out-Null
$dependencyRecords.Add([ordered]@{ ref=$wixRef; dependsOn=@() }) | Out-Null

$document = [ordered]@{
    '$schema' = 'https://cyclonedx.org/schema/bom-1.6.schema.json'
    bomFormat = 'CycloneDX'
    specVersion = '1.6'
    version = 1
    metadata = [ordered]@{
        component = [ordered]@{
            type = 'application'
            'bom-ref' = $rootRef
            name = 'AV Workstation Toolkit'
            version = $Version
            manufacturer = [ordered]@{ name='AV Workstation Toolkit Project' }
            hashes = @([ordered]@{ alg='SHA-256'; content=$LauncherSha256.ToUpperInvariant() })
            licenses = @([ordered]@{ license = [ordered]@{ id='Apache-2.0' } })
            properties = @(
                [ordered]@{ name='avworkstationtoolkit:build:commit'; value=$CommitSha.ToLowerInvariant() },
                [ordered]@{ name='avworkstationtoolkit:target:architecture'; value='win-x64' },
                [ordered]@{ name='avworkstationtoolkit:target:framework'; value='net10.0-windows' },
                [ordered]@{ name='avworkstationtoolkit:runtime:version'; value='10.0.11' }
                [ordered]@{ name='avworkstationtoolkit:runtime:primary'; value='compiled-csharp-wpf' }
                [ordered]@{ name='avworkstationtoolkit:runtime:worker-sha256'; value=$WorkerSha256.ToUpperInvariant() }
            )
        }
    }
    components = @($components)
    dependencies = @($dependencyRecords)
}

$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
$parent = [IO.Path]::GetDirectoryName($resolvedOutputPath)
if ([string]::IsNullOrWhiteSpace($parent)) { throw 'SBOM output path has no parent directory.' }
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$json = $document | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($resolvedOutputPath,$json + [Environment]::NewLine,[Text.UTF8Encoding]::new($false))

$roundTrip = Get-Content -LiteralPath $resolvedOutputPath -Raw | ConvertFrom-Json
if ($roundTrip.bomFormat -ne 'CycloneDX' -or $roundTrip.specVersion -ne '1.6') {
    throw 'Generated SBOM failed its CycloneDX identity check.'
}
Write-Output ("SBOM_OK components={0} file={1}" -f @($components).Count,(Split-Path -Leaf $resolvedOutputPath))
