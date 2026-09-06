[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MsiPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$ExpectedSignerSubject
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedMsi = [IO.Path]::GetFullPath($MsiPath)
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $resolvedMsi -PathType Leaf) -or
    [IO.Path]::GetExtension($resolvedMsi) -cne '.msi') {
    throw 'MsiPath must identify an existing MSI file.'
}
if (((Get-Item -LiteralPath $resolvedMsi -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'MsiPath cannot be a reparse point.'
}

$msiSignature = Get-AuthenticodeSignature -LiteralPath $resolvedMsi
if ($msiSignature.Status -ne 'Valid' -or $null -eq $msiSignature.TimeStamperCertificate) {
    throw 'The MSI must have a valid Authenticode signature and timestamp before extraction.'
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedSignerSubject) -and
    -not $msiSignature.SignerCertificate.Subject.Equals($ExpectedSignerSubject,[StringComparison]::Ordinal)) {
    throw 'The MSI signer does not match ExpectedSignerSubject.'
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-MsiExtract-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $msiExec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
    $process = Start-Process -FilePath $msiExec -ArgumentList @('/a',('"{0}"' -f $resolvedMsi),'/qn',('TARGETDIR="{0}"' -f $temporaryRoot)) -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Administrative MSI extraction failed with exit code $($process.ExitCode)."
    }

    $launchers = @(Get-ChildItem -LiteralPath $temporaryRoot -Recurse -File -Filter 'AVWorkstationToolkit.exe')
    if ($launchers.Count -ne 1) {
        throw "The signed MSI must contain exactly one AVWorkstationToolkit.exe; found $($launchers.Count)."
    }
    if (((Get-Item -LiteralPath $launchers[0].FullName -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The MSI-contained launcher cannot be a reparse point.'
    }
    $launcherSignature = Get-AuthenticodeSignature -LiteralPath $launchers[0].FullName
    if ($launcherSignature.Status -ne 'Valid' -or $null -eq $launcherSignature.TimeStamperCertificate) {
        throw 'The MSI-contained launcher must have a valid Authenticode signature and timestamp.'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSignerSubject) -and
        -not $launcherSignature.SignerCertificate.Subject.Equals($ExpectedSignerSubject,[StringComparison]::Ordinal)) {
        throw 'The MSI-contained launcher signer does not match ExpectedSignerSubject.'
    }

    $outputParent = Split-Path -Parent $resolvedOutput
    if (-not (Test-Path -LiteralPath $outputParent -PathType Container)) {
        New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
    }
    if (Test-Path -LiteralPath $resolvedOutput) {
        throw 'OutputPath already exists; refusing to overwrite it.'
    }
    Copy-Item -LiteralPath $launchers[0].FullName -Destination $resolvedOutput
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Get-Item -LiteralPath $resolvedOutput
