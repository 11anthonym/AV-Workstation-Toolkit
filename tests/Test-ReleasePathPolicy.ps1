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

Write-Output 'RELEASE_PATH_POLICY_OK powershell=5.1 drive=accepted unc=accepted relative=rejected drive-relative=rejected root-relative=rejected device-namespace=rejected'
