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

function Get-FolderBytes {
    param([Parameter(Mandatory)][string]$Path)
    @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force | Sort-Object FullName |
        ForEach-Object { '{0}|{1}|{2}' -f $_.FullName,$_.LastWriteTimeUtc.Ticks,(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join "`n"
}

function Assert-Replaceable {
    # A refusal must also leave every byte of the existing folder as it was.
    param([Parameter(Mandatory)][string]$Description,[Parameter(Mandatory)][string]$Name,[Parameter(Mandatory)][bool]$Expected)
    $root = Join-Path $releaseParent $Name
    $before = if (Test-Path -LiteralPath $root) { Get-FolderBytes -Path $root } else { '' }
    $refused = $false
    try { Assert-ReleaseFolderReplaceable -ReleaseRoot $root -ReleaseName $Name } catch { $refused = $true }
    if ($refused -eq $Expected) { throw "Release folder policy failed '$Description': expected replaceable=$Expected." }
    $after = if (Test-Path -LiteralPath $root) { Get-FolderBytes -Path $root } else { '' }
    if ($before -cne $after) { throw "Release folder policy changed the folder while checking '$Description'." }
}

function New-CompletedFolder {
    param([Parameter(Mandatory)][string]$Name,[Parameter(Mandatory)][string]$Channel,[Parameter(Mandatory)][string]$Commit)
    $root = Join-Path $releaseParent $Name
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    [pscustomobject]@{ BuildChannel = $Channel; CommitSha = $Commit } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $root "AV-Workstation-Toolkit-$Name-release.json")
    Set-Content -LiteralPath (Join-Path $root "AV-Workstation-Toolkit-$Name-win-x64.exe") -Value "fixture $Name"
}

$releaseParent = Join-Path ([IO.Path]::GetTempPath()) ('avwt-release-policy-' + [guid]::NewGuid().ToString('N'))
try {
    $commit = 'a' * 40
    New-CompletedFolder -Name '1.1.2' -Channel 'ReleaseCandidate' -Commit $commit
    New-CompletedFolder -Name '1.1.3-rc.1' -Channel 'ReleaseCandidate' -Commit $commit
    New-CompletedFolder -Name '1.1.4' -Channel 'Development' -Commit ('b' * 40)
    New-CompletedFolder -Name '2.0.0' -Channel 'Production' -Commit $commit
    New-Item -ItemType Directory -Path (Join-Path $releaseParent '1.1.3-rc.2') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $releaseParent '1.1.3-rc.2\partial.tmp') -Value 'an unfinished build'
    New-Item -ItemType Directory -Path (Join-Path $releaseParent '1.1.3-rc.3') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $releaseParent '1.1.3-rc.3\AV-Workstation-Toolkit-1.1.3-rc.3-release.json') -Value '{ not json'

    Assert-Replaceable 'new target' -Name '1.1.3-rc.9' -Expected $true
    Assert-Replaceable 'unfinished build' -Name '1.1.3-rc.2' -Expected $true
    Assert-Replaceable 'local Development build' -Name '1.1.4' -Expected $true
    # The commit a later build comes from never matters: the policy doesn't take one.
    Assert-Replaceable 'completed release candidate' -Name '1.1.3-rc.1' -Expected $false
    Assert-Replaceable 'completed unsigned release' -Name '1.1.2' -Expected $false
    Assert-Replaceable 'completed signed release' -Name '2.0.0' -Expected $false
    Assert-Replaceable 'unreadable release manifest' -Name '1.1.3-rc.3' -Expected $false

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

Write-Output 'RELEASE_PATH_POLICY_OK powershell=5.1 drive=accepted unc=accepted relative=rejected drive-relative=rejected root-relative=rejected device-namespace=rejected completed-releases=immutable'
