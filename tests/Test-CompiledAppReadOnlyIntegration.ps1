<#[.SYNOPSIS] Exercises the compiled WPF composition with live read-only Windows providers and exits. #>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appPath = Join-Path $repositoryRoot 'src\AVWorkstationToolkit.App\bin\Release\net10.0-windows\win-x64\AVWorkstationToolkit.App.exe'
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) { throw "Compiled WPF executable was not built: $appPath" }
$process = Start-Process -FilePath $appPath -ArgumentList '--read-only-check' -PassThru
if (-not $process.WaitForExit(180000)) {
    try { $process.Kill($true) } catch { }
    throw 'Compiled WPF live read-only integration timed out.'
}
if ($process.ExitCode -ne 0) { throw "Compiled WPF live read-only integration failed with exit code $($process.ExitCode)." }
Write-Output 'COMPILED_APP_READONLY_OK catalog=complete providers=bounded mutation=absent'
