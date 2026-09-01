<#
.SYNOPSIS
    Proves the shipping worker exposes production activation only.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workerRoot = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Worker'
$developmentRoot = Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.Worker.DevHost'
$developmentSupportRoot = Join-Path $repositoryRoot 'tests\AVWorkstationToolkit.Development'
$workerSource = @(Get-ChildItem -LiteralPath $workerRoot -Recurse -File |
    Where-Object { $_.Extension -in @('.cs','.csproj') -and $_.FullName -notmatch '[\/](?:bin|obj)[\/]' } |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$developmentSource = @(Get-ChildItem -LiteralPath $developmentRoot -Recurse -File |
    Where-Object { $_.Extension -in @('.cs','.csproj') -and $_.FullName -notmatch '[\/](?:bin|obj)[\/]' } |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$developmentSupportSource = @(Get-ChildItem -LiteralPath $developmentSupportRoot -Recurse -File |
    Where-Object { $_.Extension -in @('.cs','.csproj') -and $_.FullName -notmatch '[\/](?:bin|obj)[\/]' } |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$buildSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build\Build-Release.ps1') -Raw
$launcherProject = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Launcher\AVWorkstationToolkit.Launcher.csproj') -Raw
$shippingInfrastructure = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.Infrastructure.Windows') -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\/](?:bin|obj)[\/]' } |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$shippingApp = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App') -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\/](?:bin|obj)[\/]' } |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"

function Invoke-WorkerBoundary {
    param([string]$Executable,[string[]]$Arguments)
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $Executable @Arguments 2>&1 | Out-String | Out-Null
        return [int]$LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

if ($workerSource -notmatch '--production' -or
    $workerSource -match '--test-mode|--live-rehearsal|--repository-root|DeterministicFakePackageExecutor|WorkerFixtureLoader|LiveRehearsalRootPolicy') {
    throw 'Shipping worker source is not production-only.'
}
if ($developmentSource -notmatch '--test-mode' -or $developmentSource -notmatch '--live-rehearsal' -or
    $developmentSource -notmatch 'DeterministicFakePackageExecutor' -or $developmentSource -match '--production') {
    throw 'The separate worker development host does not own the expected non-shipping modes.'
}
if ($developmentSupportSource -notmatch 'CompiledMigrationWorkerLauncher' -or
    $developmentSupportSource -notmatch 'CompiledLiveRehearsalWorkerLauncher' -or
    $developmentSupportSource -notmatch 'LiveRehearsalRootPolicy') {
    throw 'The separate development-support project does not own the rehearsal launch and root policies.'
}
if (($shippingInfrastructure + $shippingApp) -match '--test-mode|--live-rehearsal|--repository-root|CompiledMigrationWorkerLauncher|CompiledLiveRehearsalWorkerLauncher|LiveRehearsalRootPolicy') {
    throw 'A shipping runtime assembly still contains worker development activation support.'
}
if ($buildSource -notmatch '\$workerProject\s*=.*src\\AVWorkstationToolkit\.Worker\\AVWorkstationToolkit\.Worker\.csproj' -or
    $buildSource -match 'AVWorkstationToolkit\.Worker\.DevHost') {
    throw 'Release build composition does not select only the shipping worker project.'
}
if ($launcherProject -match 'AVWorkstationToolkit\.Worker\.DevHost') {
    throw 'The launcher project embeds or references the worker development host.'
}

$workerExe = Join-Path $workerRoot 'bin\Release\net10.0-windows\AVWorkstationToolkit.Worker.exe'
$developmentExe = Join-Path $developmentRoot 'bin\Release\net10.0-windows\AVWorkstationToolkit.Worker.DevHost.exe'
foreach ($path in @($workerExe,$developmentExe)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required worker boundary binary was not built: $path" }
}

$testExit = Invoke-WorkerBoundary -Executable $workerExe -Arguments @('--test-mode','--root',$repositoryRoot,'--request',(Join-Path $repositoryRoot 'request.json'))
if ($testExit -ne 2) { throw "Shipping worker did not reject --test-mode at its invocation boundary: $testExit" }
$rehearsalExit = Invoke-WorkerBoundary -Executable $workerExe -Arguments @('--live-rehearsal','--root',$repositoryRoot,'--request',(Join-Path $repositoryRoot 'request.json'),'--repository-root',$repositoryRoot)
if ($rehearsalExit -ne 2) { throw "Shipping worker did not reject --live-rehearsal at its invocation boundary: $rehearsalExit" }
$productionExit = Invoke-WorkerBoundary -Executable $workerExe -Arguments @('--production','--root',$repositoryRoot,'--request',(Join-Path $repositoryRoot 'request.json'),'--application-root',$repositoryRoot)
if ($productionExit -eq 2) { throw 'Shipping worker did not recognize the exact production invocation shape.' }

$workerIdentity = (Get-Item -LiteralPath $workerExe).VersionInfo
$developmentIdentity = (Get-Item -LiteralPath $developmentExe).VersionInfo
if ([string]$workerIdentity.ProductName -ne 'AV Workstation Toolkit compiled worker' -or
    [string]$developmentIdentity.ProductName -ne 'AV Workstation Toolkit worker development host') {
    throw 'Production and development worker identities are not distinct.'
}
$workerBinaryText = Get-Content -LiteralPath $workerExe -Raw -Encoding Unicode
if ($workerBinaryText -match '--test-mode|--live-rehearsal|--repository-root|CompiledMigrationWorkerLauncher|CompiledLiveRehearsalWorkerLauncher|LiveRehearsalRootPolicy|DeterministicFakePackageExecutor') {
    throw 'The shipping worker binary still contains a developer activation or fake-executor boundary.'
}

Write-Output 'PRODUCTION_WORKER_BOUNDARY_OK shipping=production-only development-host=separate'
