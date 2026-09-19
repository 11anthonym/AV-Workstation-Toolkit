<#[.SYNOPSIS] Launches and closes the compiled production WPF app against deterministic data. #>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appPath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\bin\Release\net10.0-windows\win-x64\AVWorkstationToolkit.App.exe'
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) { throw "Compiled WPF executable was not built: $appPath" }
$process = Start-Process -FilePath $appPath -ArgumentList '--smoke-test' -PassThru
if (-not $process.WaitForExit(30000)) {
    try { $process.Kill($true) } catch { }
    throw 'Compiled WPF smoke timed out.'
}
if ($process.ExitCode -ne 0) { throw "Compiled WPF smoke failed with exit code $($process.ExitCode)." }
Write-Output 'COMPILED_WPF_SMOKE_OK controls=compatibility-extended bindings=loaded find-apps-device=rendered selection=toggle details=loaded diagnostics=loaded official-intent=read-only mutation=absent'
