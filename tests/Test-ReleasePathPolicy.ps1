[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'Release path-policy regression must run under Windows PowerShell 5.1 (powershell.exe).'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repositoryRoot 'build\Release-PathPolicy.ps1')

function Assert-PathPolicy {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][bool]$Expected
    )

    $actual = Test-FullyQualifiedWindowsPath -Path $Path
    if ($actual -ne $Expected) {
        throw "Release path policy failed '$Description' for '$Path': expected $Expected, observed $actual."
    }
}

Assert-PathPolicy -Description 'drive-qualified path' -Path 'C:\Catalog\AVWT-Reference-Catalog.avwtcatalog' -Expected $true
Assert-PathPolicy -Description 'UNC path' -Path '\\catalog-server\approved-share\AVWT-Reference-Catalog.avwtcatalog' -Expected $true
Assert-PathPolicy -Description 'relative path' -Path 'catalog\AVWT-Reference-Catalog.avwtcatalog' -Expected $false
Assert-PathPolicy -Description 'drive-relative path' -Path 'C:catalog\AVWT-Reference-Catalog.avwtcatalog' -Expected $false
Assert-PathPolicy -Description 'root-relative path' -Path '\catalog\AVWT-Reference-Catalog.avwtcatalog' -Expected $false
Assert-PathPolicy -Description 'malformed path' -Path 'C:\Catalog\bad|name.avwtcatalog' -Expected $false
Assert-PathPolicy -Description 'extended device namespace' -Path '\\?\C:\Catalog\AVWT-Reference-Catalog.avwtcatalog' -Expected $false
Assert-PathPolicy -Description 'device namespace' -Path '\\.\C:\Catalog\AVWT-Reference-Catalog.avwtcatalog' -Expected $false

function Assert-Replaceable {
    param([Parameter(Mandatory)][string]$Description,[Parameter(Mandatory)][scriptblock]$Call,[Parameter(Mandatory)][bool]$Expected)
    $refused = $false
    try { & $Call } catch { $refused = $true }
    if ($refused -eq $Expected) { throw "Release folder policy failed '$Description': expected replaceable=$Expected." }
}

$releaseParent = Join-Path ([IO.Path]::GetTempPath()) ('avwt-release-policy-' + [guid]::NewGuid().ToString('N'))
try {
    $commit = 'a' * 40
    $otherCommit = 'b' * 40
    foreach ($name in @('1.1.2','1.1.3-alpha.1','1.1.3-alpha.2','1.1.4')) {
        New-Item -ItemType Directory -Path (Join-Path $releaseParent $name) -Force | Out-Null
    }
    foreach ($name in @('1.1.2','1.1.3-alpha.1')) {
        [pscustomobject]@{ CommitSha = $commit } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseParent "$name\AV-Workstation-Toolkit-$name-release.json")
    }
    Assert-Replaceable 'missing folder' { Assert-ReleaseFolderReplaceable -ReleaseRoot (Join-Path $releaseParent '9.9.9') -ReleaseName '9.9.9' -PrereleaseLabel '' -CommitSha $commit -Published $false } $true
    Assert-Replaceable 'completed pre-release label' { Assert-ReleaseFolderReplaceable -ReleaseRoot (Join-Path $releaseParent '1.1.3-alpha.1') -ReleaseName '1.1.3-alpha.1' -PrereleaseLabel 'alpha.1' -CommitSha $commit -Published $false } $false
    Assert-Replaceable 'unfinished pre-release label' { Assert-ReleaseFolderReplaceable -ReleaseRoot (Join-Path $releaseParent '1.1.3-alpha.2') -ReleaseName '1.1.3-alpha.2' -PrereleaseLabel 'alpha.2' -CommitSha $commit -Published $false } $true
    Assert-Replaceable 'published release from other source' { Assert-ReleaseFolderReplaceable -ReleaseRoot (Join-Path $releaseParent '1.1.2') -ReleaseName '1.1.2' -PrereleaseLabel '' -CommitSha $otherCommit -Published $true } $false
    Assert-Replaceable 'published release from its own commit' { Assert-ReleaseFolderReplaceable -ReleaseRoot (Join-Path $releaseParent '1.1.2') -ReleaseName '1.1.2' -PrereleaseLabel '' -CommitSha $commit -Published $true } $true
    Assert-Replaceable 'unpublished development folder' { Assert-ReleaseFolderReplaceable -ReleaseRoot (Join-Path $releaseParent '1.1.2') -ReleaseName '1.1.2' -PrereleaseLabel '' -CommitSha $otherCommit -Published $false } $true

    $before = Get-ReleaseFolderState -ReleaseParent $releaseParent -ExcludedName '1.1.4'
    Set-Content -LiteralPath (Join-Path $releaseParent '1.1.4\building.txt') -Value 'the folder being built may change'
    if (@(Compare-Object $before (Get-ReleaseFolderState -ReleaseParent $releaseParent -ExcludedName '1.1.4')).Count -ne 0) {
        throw 'Release folder state reported a change in the folder being built.'
    }
    $published = Get-Item -LiteralPath (Join-Path $releaseParent '1.1.2\AV-Workstation-Toolkit-1.1.2-release.json')
    $published.LastWriteTimeUtc = $published.LastWriteTimeUtc.AddMinutes(-5)
    if (@(Compare-Object $before (Get-ReleaseFolderState -ReleaseParent $releaseParent -ExcludedName '1.1.4')).Count -eq 0) {
        throw 'Release folder state missed a rewritten file in another release folder.'
    }
}
finally {
    if (Test-Path -LiteralPath $releaseParent) { Remove-Item -LiteralPath $releaseParent -Recurse -Force }
}

Write-Output 'RELEASE_PATH_POLICY_OK powershell=5.1 drive=accepted unc=accepted relative=rejected drive-relative=rejected root-relative=rejected device-namespace=rejected release-folders=protected'
