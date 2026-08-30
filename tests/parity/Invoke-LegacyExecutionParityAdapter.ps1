<#[.SYNOPSIS] Evaluates WinGet mutation arguments through the shipping PowerShell policy. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixturePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$resolvedFixture = [IO.Path]::GetFullPath($FixturePath)
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'execution-fixtures')).TrimEnd('\') + '\'
if (-not $resolvedFixture.StartsWith($fixtureRoot,[StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $resolvedFixture -PathType Leaf)) {
    throw 'Execution parity fixture must be an existing file under tests\parity\execution-fixtures.'
}

function Assert-ExactProperties {
    param([Parameter(Mandatory)]$Value,[Parameter(Mandatory)][string[]]$Expected,[Parameter(Mandatory)][string]$Context)
    $actual = @($Value.PSObject.Properties.Name)
    $unknown = @($actual | Where-Object { $_ -notin $Expected })
    $missing = @($Expected | Where-Object { $_ -notin $actual })
    if ($unknown.Count -gt 0 -or $missing.Count -gt 0) {
        throw "$Context fields differ. Unknown=[$($unknown -join ',')] Missing=[$($missing -join ',')]."
    }
}

$fixture = Get-Content -LiteralPath $resolvedFixture -Raw | ConvertFrom-Json -ErrorAction Stop
Assert-ExactProperties $fixture @('SchemaVersion','ScenarioId','Cases') 'Fixture'
if ($fixture.SchemaVersion -isnot [int] -or [int]$fixture.SchemaVersion -ne 1) { throw 'Unsupported execution parity schema.' }
Import-Module (Join-Path $repositoryRoot 'scripts\AVWorkstationToolkit.Core.psd1') -Force
$cases = foreach ($item in @($fixture.Cases)) {
    Assert-ExactProperties $item @('CaseId','Action','PackageId','Risk') "Case $($item.CaseId)"
    $package = [pscustomobject]@{ Id=[string]$item.PackageId; Risk=[string]$item.Risk }
    [ordered]@{
        CaseId = [string]$item.CaseId
        Arguments = @(Get-AVWorkstationToolkitWingetArguments -Action ([string]$item.Action) -Package $package)
    }
}

[ordered]@{ SchemaVersion=1; ScenarioId=[string]$fixture.ScenarioId; Cases=@($cases) } |
    ConvertTo-Json -Depth 8 -Compress
