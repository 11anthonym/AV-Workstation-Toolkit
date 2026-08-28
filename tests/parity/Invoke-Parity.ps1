<#[.SYNOPSIS] Runs deterministic package-state fixtures through both migration engines. #>
[CmdletBinding()]
param([switch]$NoBuild)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$solutionPath = Join-Path $repositoryRoot 'AVWorkstationToolkit.slnx'
$integrationDll = Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.IntegrationTests\bin\Release\net10.0\AVWorkstationToolkit.IntegrationTests.dll'

if (-not $NoBuild) {
    & dotnet restore $solutionPath --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore for the migration solution failed.' }
    & dotnet build $solutionPath -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Migration solution build failed.' }
}
foreach ($path in @($integrationDll)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Migration test assembly was not built: $path" }
}

$scenarioCount = 0
$packageCount = 0
foreach ($fixture in @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'fixtures') -File -Filter '*.json' | Sort-Object Name)) {
    $legacy = (& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Invoke-LegacyParityAdapter.ps1') -FixturePath $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Legacy adapter failed for $($fixture.Name)." }
    $compiled = (& dotnet $integrationDll $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "C# adapter failed for $($fixture.Name)." }
    $legacyObject = $legacy | ConvertFrom-Json -ErrorAction Stop
    $compiledObject = $compiled | ConvertFrom-Json -ErrorAction Stop
    $legacyCanonical = $legacyObject | ConvertTo-Json -Depth 8 -Compress
    $compiledCanonical = $compiledObject | ConvertTo-Json -Depth 8 -Compress
    if ($legacyCanonical -cne $compiledCanonical) {
        throw "Parity mismatch for $($fixture.Name).`r`nLEGACY: $legacyCanonical`r`nCSHARP: $compiledCanonical"
    }
    $scenarioCount++
    $packageCount += @($legacyObject.Packages).Count
    Write-Output "PARITY_PASS fixture=$($fixture.Name) packages=$(@($legacyObject.Packages).Count)"
}
Write-Output "PARITY_OK scenarios=$scenarioCount packages=$packageCount"

$coreScenarioCount = 0
$coreCaseCount = 0
foreach ($fixture in @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'core-fixtures') -File -Filter '*.json' | Sort-Object Name)) {
    $legacy = (& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Invoke-LegacyCoreParityAdapter.ps1') -FixturePath $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Legacy core adapter failed for $($fixture.Name)." }
    $compiled = (& dotnet $integrationDll --core $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "C# core adapter failed for $($fixture.Name)." }
    $legacyObject = $legacy | ConvertFrom-Json -ErrorAction Stop
    $compiledObject = $compiled | ConvertFrom-Json -ErrorAction Stop
    $legacyCanonical = $legacyObject | ConvertTo-Json -Depth 12 -Compress
    $compiledCanonical = $compiledObject | ConvertTo-Json -Depth 12 -Compress
    if ($legacyCanonical -cne $compiledCanonical) { throw "Core parity mismatch for $($fixture.Name).`r`nLEGACY: $legacyCanonical`r`nCSHARP: $compiledCanonical" }
    $coreScenarioCount++
    $coreCaseCount += @($legacyObject.Versions).Count + @($legacyObject.Filters).Count + @($legacyObject.ExternalStates).Count + @($legacyObject.Catalogs).Count
    Write-Output "CORE_PARITY_PASS fixture=$($fixture.Name) cases=$coreCaseCount"
}
Write-Output "CORE_PARITY_OK scenarios=$coreScenarioCount cases=$coreCaseCount"
