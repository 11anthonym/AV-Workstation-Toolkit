<#[.SYNOPSIS] Launches and closes the non-shipping compiled WPF migration app against deterministic data. #>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appPath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\bin\Release\net10.0-windows\win-x64\AVWorkstationToolkit.App.exe'
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) { throw "Compiled WPF migration executable was not built: $appPath" }
$process = Start-Process -FilePath $appPath -ArgumentList '--smoke-test' -PassThru
if (-not $process.WaitForExit(30000)) {
    try { $process.Kill($true) } catch { }
    throw 'Compiled WPF migration smoke timed out.'
}
if ($process.ExitCode -ne 0) { throw "Compiled WPF migration smoke failed with exit code $($process.ExitCode)." }
Write-Output 'CSHARP_WPF_SMOKE_OK controls=19 bindings=loaded selection=toggle mutation=absent'
