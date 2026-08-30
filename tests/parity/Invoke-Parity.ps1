<#[.SYNOPSIS] Runs deterministic package-state fixtures through both migration engines. #>
[CmdletBinding()]
param([switch]$NoBuild)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$solutionPath = Join-Path $repositoryRoot 'AVWorkstationToolkit.slnx'
$integrationDll = Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.IntegrationTests\bin\Release\net10.0-windows\AVWorkstationToolkit.IntegrationTests.dll'

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

$providerScenarioCount = 0
$providerCaseCount = 0
foreach ($fixture in @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'provider-fixtures') -File -Filter '*.json' | Sort-Object Name)) {
    $legacy = (& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Invoke-LegacyProviderParityAdapter.ps1') -FixturePath $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Legacy provider adapter failed for $($fixture.Name)." }
    $compiled = (& dotnet $integrationDll --providers $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "C# provider adapter failed for $($fixture.Name)." }
    $legacyObject = $legacy | ConvertFrom-Json -ErrorAction Stop
    $compiledObject = $compiled | ConvertFrom-Json -ErrorAction Stop
    $legacyCanonical = $legacyObject | ConvertTo-Json -Depth 30 -Compress
    $compiledCanonical = $compiledObject | ConvertTo-Json -Depth 30 -Compress
    if ($legacyCanonical -cne $compiledCanonical) { throw "Provider parity mismatch for $($fixture.Name).`r`nLEGACY: $legacyCanonical`r`nCSHARP: $compiledCanonical" }
    $cases = @($legacyObject.InstalledInventory).Count + @($legacyObject.Updates).Count + @($legacyObject.Registry).Count + @($legacyObject.Reboot).Count + @($legacyObject.Trust).Count
    $providerScenarioCount++
    $providerCaseCount += $cases
    Write-Output "PROVIDER_PARITY_PASS fixture=$($fixture.Name) cases=$cases"
}
Write-Output "PROVIDER_PARITY_OK scenarios=$providerScenarioCount cases=$providerCaseCount"

$presentationScenarioCount = 0
$presentationCaseCount = 0
foreach ($fixture in @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'presentation-fixtures') -File -Filter '*.json' | Sort-Object Name)) {
    $legacy = (& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Invoke-LegacyPresentationParityAdapter.ps1') -FixturePath $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Legacy presentation adapter failed for $($fixture.Name)." }
    $compiled = (& dotnet $integrationDll --presentation $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "C# presentation adapter failed for $($fixture.Name)." }
    $legacyObject = $legacy | ConvertFrom-Json -ErrorAction Stop
    $compiledObject = $compiled | ConvertFrom-Json -ErrorAction Stop
    $legacyCanonical = $legacyObject | ConvertTo-Json -Depth 20 -Compress
    $compiledCanonical = $compiledObject | ConvertTo-Json -Depth 20 -Compress
    if ($legacyCanonical -cne $compiledCanonical) { throw "Presentation parity mismatch for $($fixture.Name).`r`nLEGACY: $legacyCanonical`r`nCSHARP: $compiledCanonical" }
    $cases = @($legacyObject.Cases).Count
    $presentationScenarioCount++
    $presentationCaseCount += $cases
    Write-Output "PRESENTATION_PARITY_PASS fixture=$($fixture.Name) cases=$cases"
}
Write-Output "PRESENTATION_PARITY_OK scenarios=$presentationScenarioCount cases=$presentationCaseCount"

$readOnlySurfaceScenarioCount = 0
$readOnlySurfaceCaseCount = 0
foreach ($fixture in @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'read-only-surfaces-fixtures') -File -Filter '*.json' | Sort-Object Name)) {
    $legacy = (& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Invoke-LegacyReadOnlySurfacesParityAdapter.ps1') -FixturePath $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Legacy read-only surface adapter failed for $($fixture.Name)." }
    $compiled = (& dotnet $integrationDll --read-only-surfaces $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "C# read-only surface adapter failed for $($fixture.Name)." }
    $legacyObject = $legacy | ConvertFrom-Json -ErrorAction Stop
    $compiledObject = $compiled | ConvertFrom-Json -ErrorAction Stop
    $legacyCanonical = $legacyObject | ConvertTo-Json -Depth 20 -Compress
    $compiledCanonical = $compiledObject | ConvertTo-Json -Depth 20 -Compress
    if ($legacyCanonical -cne $compiledCanonical) { throw "Read-only surface parity mismatch for $($fixture.Name).`r`nLEGACY: $legacyCanonical`r`nCSHARP: $compiledCanonical" }
    $cases = @($legacyObject.Details).Count + 1
    $readOnlySurfaceScenarioCount++
    $readOnlySurfaceCaseCount += $cases
    Write-Output "READONLY_SURFACE_PARITY_PASS fixture=$($fixture.Name) cases=$cases"
}
Write-Output "READONLY_SURFACE_PARITY_OK scenarios=$readOnlySurfaceScenarioCount cases=$readOnlySurfaceCaseCount"

$actionRequestScenarioCount = 0
$actionRequestCaseCount = 0
foreach ($fixture in @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'action-request-fixtures') -File -Filter '*.json' | Sort-Object Name)) {
    $legacy = (& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Invoke-LegacyActionRequestParityAdapter.ps1') -FixturePath $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Legacy action-request adapter failed for $($fixture.Name)." }
    $compiled = (& dotnet $integrationDll --action-requests $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "C# action-request adapter failed for $($fixture.Name)." }
    $legacyObject = $legacy | ConvertFrom-Json -ErrorAction Stop
    $compiledObject = $compiled | ConvertFrom-Json -ErrorAction Stop
    $legacyCanonical = $legacyObject | ConvertTo-Json -Depth 20 -Compress
    $compiledCanonical = $compiledObject | ConvertTo-Json -Depth 20 -Compress
    if ($legacyCanonical -cne $compiledCanonical) { throw "Action-request parity mismatch for $($fixture.Name).`r`nLEGACY: $legacyCanonical`r`nCSHARP: $compiledCanonical" }
    $cases = @($legacyObject.Cases).Count
    $actionRequestScenarioCount++
    $actionRequestCaseCount += $cases
    Write-Output "ACTION_REQUEST_PARITY_PASS fixture=$($fixture.Name) cases=$cases"
}
Write-Output "ACTION_REQUEST_PARITY_OK scenarios=$actionRequestScenarioCount cases=$actionRequestCaseCount"

$ipcLifecycleScenarioCount = 0
$ipcLifecycleCaseCount = 0
foreach ($fixture in @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'ipc-lifecycle-fixtures') -File -Filter '*.json' | Sort-Object Name)) {
    $legacy = (& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Invoke-LegacyIpcLifecycleParityAdapter.ps1') -FixturePath $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Legacy IPC lifecycle adapter failed for $($fixture.Name)." }
    $compiled = (& dotnet $integrationDll --ipc-lifecycle $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "C# IPC lifecycle adapter failed for $($fixture.Name)." }
    $legacyObject = $legacy | ConvertFrom-Json -ErrorAction Stop
    $compiledObject = $compiled | ConvertFrom-Json -ErrorAction Stop
    $legacyCanonical = $legacyObject | ConvertTo-Json -Depth 20 -Compress
    $compiledCanonical = $compiledObject | ConvertTo-Json -Depth 20 -Compress
    if ($legacyCanonical -cne $compiledCanonical) { throw "IPC lifecycle parity mismatch for $($fixture.Name).`r`nLEGACY: $legacyCanonical`r`nCSHARP: $compiledCanonical" }
    $cases = @($legacyObject.Cases).Count
    $ipcLifecycleScenarioCount++
    $ipcLifecycleCaseCount += $cases
    Write-Output "IPC_LIFECYCLE_PARITY_PASS fixture=$($fixture.Name) cases=$cases"
}
Write-Output "IPC_LIFECYCLE_PARITY_OK scenarios=$ipcLifecycleScenarioCount cases=$ipcLifecycleCaseCount"

$executionScenarioCount = 0
$executionCaseCount = 0
foreach ($fixture in @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'execution-fixtures') -File -Filter '*.json' | Sort-Object Name)) {
    $legacy = (& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Invoke-LegacyExecutionParityAdapter.ps1') -FixturePath $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Legacy execution adapter failed for $($fixture.Name)." }
    $compiled = (& dotnet $integrationDll --execution $fixture.FullName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "C# execution adapter failed for $($fixture.Name)." }
    $legacyObject = $legacy | ConvertFrom-Json -ErrorAction Stop
    $compiledObject = $compiled | ConvertFrom-Json -ErrorAction Stop
    $legacyCanonical = $legacyObject | ConvertTo-Json -Depth 10 -Compress
    $compiledCanonical = $compiledObject | ConvertTo-Json -Depth 10 -Compress
    if ($legacyCanonical -cne $compiledCanonical) { throw "Execution parity mismatch for $($fixture.Name).`r`nLEGACY: $legacyCanonical`r`nCSHARP: $compiledCanonical" }
    $cases = @($legacyObject.Cases).Count
    $executionScenarioCount++
    $executionCaseCount += $cases
    Write-Output "EXECUTION_PARITY_PASS fixture=$($fixture.Name) cases=$cases"
}
Write-Output "EXECUTION_PARITY_OK scenarios=$executionScenarioCount cases=$executionCaseCount"
