<#
.SYNOPSIS
    Verifies AVWorkstationToolkit release artifacts without installing applications.

.DESCRIPTION
    Validates checksums and versions, copies the standalone executable into an
    otherwise empty download directory, exercises runtime extraction and WPF
    smoke behavior, verifies cache repair, and administratively extracts the
    MSI. It does not register or install the MSI and never invokes a WinGet
    change action.

    Every packaged launch uses a disposable AVWorkstationToolkit-package-qa-<id>
    data root beneath the temporary folder, including the production smoke, so
    no packaged launch uses the user's %LOCALAPPDATA%\AVWorkstationToolkit
    profile. The run proves this: it only reads the real profile and the
    installed-product registration, before and after, to show they are
    unchanged, and the isolated root must hold the runtime, folders, and
    migration checklist the packaged app wrote there. The MSI is only
    administratively extracted, so nothing is installed or upgraded.
#>

[CmdletBinding()]
param(
    [string]$ReleaseRoot,
    [switch]$RequireSignature,
    [switch]$SignaturePolicyOnly,
    [switch]$SkipDesktopSmoke,
    [switch]$ProcessHelperSelfTest,
    [ValidateRange(10,600)]
    [int]$ProcessTimeoutSeconds = 120,
    [string]$PrereleaseLabel
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'VERSION') -Raw).Trim()
# A release candidate is checked as the exact candidate it was built as, e.g. 1.1.3-rc.1.
if ([string]::IsNullOrEmpty($PrereleaseLabel)) { $PrereleaseLabel = ''; $releaseName = $version }
elseif ($PrereleaseLabel -cmatch '^rc\.[1-9][0-9]{0,2}$') { $releaseName = '{0}-{1}' -f $version,$PrereleaseLabel }
else { throw "PrereleaseLabel must be rc.N, a release candidate of VERSION: $PrereleaseLabel" }
if ([string]::IsNullOrWhiteSpace($ReleaseRoot)) {
    $ReleaseRoot = Join-Path $repositoryRoot (Join-Path 'artifacts\release' $releaseName)
}
$ReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$standalonePath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-win-x64.exe" -f $releaseName)
$msiPath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-x64.msi" -f $releaseName)
$portablePath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-win-x64.zip" -f $releaseName)
$checksumPath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-SHA256SUMS.txt" -f $releaseName)
$releaseManifestPath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-release.json" -f $releaseName)
$sbomPath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-sbom.cdx.json" -f $releaseName)
$projectLicensePath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-LICENSE.txt" -f $releaseName)
$thirdPartyNoticesPath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-THIRD-PARTY-NOTICES.md" -f $releaseName)

$script:Passed = 0
$script:Failed = 0
$script:Skipped = 0
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:LastLauncherStdErr = ''

function Assert-True {
    param([bool]$Condition,[string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param($Expected,$Actual,[string]$Message)
    if ($Expected -ne $Actual) { throw "$Message Expected: [$Expected] Actual: [$Actual]" }
}

function Invoke-Check {
    param([string]$Name,[scriptblock]$Test)
    try {
        & $Test
        $script:Passed++
        Write-Host ("PASS  {0}" -f $Name) -ForegroundColor Green
    }
    catch {
        $script:Failed++
        $detail = "{0}: {1}" -f $Name,$_.Exception.Message
        $script:Failures.Add($detail) | Out-Null
        Write-Host ("FAIL  {0}" -f $detail) -ForegroundColor Red
    }
}

function Get-TreeSnapshot {
    # Read-only: one line per directory and per file (size, write time, SHA-256) beneath the root, or nothing if it is absent.
    param([Parameter(Mandatory)][string]$Root)
    $lines = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return ,$lines.ToArray() }
    foreach ($item in @(Get-ChildItem -LiteralPath $Root -Recurse -Force -ErrorAction Stop)) {
        $relative = $item.FullName.Substring($Root.Length).TrimStart('\')
        if ($item.PSIsContainer) { $lines.Add("dir|$relative") | Out-Null; continue }
        # Shared read access, so a log a running copy of the app holds open is still compared rather than failing the run.
        $hash = try {
            $stream = [IO.File]::Open($item.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            try {
                $sha256 = [Security.Cryptography.SHA256]::Create()
                try { [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-','') } finally { $sha256.Dispose() }
            }
            finally { $stream.Dispose() }
        }
        catch { 'unreadable' }
        $lines.Add(('file|{0}|{1}|{2}|{3}' -f $relative,$item.Length,$item.LastWriteTimeUtc.Ticks,$hash)) | Out-Null
    }
    return ,@($lines | Sort-Object)
}

function Compare-TreeSnapshot {
    param([string[]]$Before,[string[]]$After)
    $differences = @(Compare-Object -ReferenceObject @($Before) -DifferenceObject @($After) |
        ForEach-Object { '{0} {1}' -f $(if ($_.SideIndicator -eq '=>') { 'now' } else { 'was' }),$_.InputObject })
    return ,$differences
}

function ConvertTo-PackedGuid {
    # Windows Installer's registry index stores a GUID with each group's characters reversed.
    param([Parameter(Mandatory)][string]$Guid)
    $hex = $Guid.Trim('{}').Replace('-','').ToUpperInvariant()
    if ($hex -notmatch '^[0-9A-F]{32}$') { throw "Not a GUID: $Guid" }
    $packed = -join $hex.Substring(0,8).ToCharArray()[7..0]
    $packed += -join $hex.Substring(8,4).ToCharArray()[3..0]
    $packed += -join $hex.Substring(12,4).ToCharArray()[3..0]
    for ($index = 16; $index -lt 32; $index += 2) { $packed += [string]$hex[$index + 1] + [string]$hex[$index] }
    return $packed
}

function Get-InstalledProductState {
    # Read-only view of everything an MSI install or upgrade of this product would change: its Windows Installer
    # upgrade-family registration, its Installed Apps entries, the Start menu folder, and the installed launcher.
    $packedUpgradeCode = ConvertTo-PackedGuid -Guid $productUpgradeCode
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($hive in @(
        @{ Name = 'HKLM64'; Hive = [Microsoft.Win32.RegistryHive]::LocalMachine; View = [Microsoft.Win32.RegistryView]::Registry64; UpgradeCodes = 'SOFTWARE\Classes\Installer\UpgradeCodes' },
        @{ Name = 'HKLM32'; Hive = [Microsoft.Win32.RegistryHive]::LocalMachine; View = [Microsoft.Win32.RegistryView]::Registry32; UpgradeCodes = $null },
        @{ Name = 'HKCU'; Hive = [Microsoft.Win32.RegistryHive]::CurrentUser; View = [Microsoft.Win32.RegistryView]::Default; UpgradeCodes = 'Software\Microsoft\Installer\UpgradeCodes' })) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive.Hive,$hive.View)
        try {
            if ($null -ne $hive.UpgradeCodes) {
                $family = $base.OpenSubKey((Join-Path $hive.UpgradeCodes $packedUpgradeCode),$false)
                if ($null -ne $family) {
                    try { foreach ($product in @($family.GetValueNames())) { $lines.Add("upgrade-family|$($hive.Name)|$product") | Out-Null } }
                    finally { $family.Dispose() }
                }
            }
            $uninstall = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',$false)
            if ($null -eq $uninstall) { continue }
            try {
                foreach ($name in @($uninstall.GetSubKeyNames())) {
                    $entry = $uninstall.OpenSubKey($name,$false)
                    if ($null -eq $entry) { continue }
                    try {
                        $displayName = [string]$entry.GetValue('DisplayName')
                        if ($displayName -like 'AV Workstation Toolkit*' -or $displayName -like 'AVinite*') {
                            $lines.Add(('installed-app|{0}|{1}|{2}|{3}' -f $hive.Name,$name,$displayName,[string]$entry.GetValue('DisplayVersion'))) | Out-Null
                        }
                    }
                    finally { $entry.Dispose() }
                }
            }
            finally { $uninstall.Dispose() }
        }
        finally { $base.Dispose() }
    }
    foreach ($path in @(
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\AV Workstation Toolkit'),
        (Join-Path $env:ProgramFiles 'AVWorkstationToolkit'))) {
        $snapshot = Get-TreeSnapshot -Root $path
        foreach ($line in $snapshot) { $lines.Add("$path|$line") | Out-Null }
    }
    return ,@($lines | Sort-Object)
}

function Remove-IsolatedRoot {
    # Deletes only a folder this run created directly beneath the temporary folder with one of its own name prefixes.
    param([Parameter(Mandatory)][string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $systemTemporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $name = Split-Path -Leaf $resolved
    if ((Split-Path -Parent $resolved) -ne $systemTemporary -or $name -notmatch '^AVWorkstationToolkit-package-(?:qa|work)-[0-9a-f]{32}$') {
        throw "Refusing to delete a folder this run did not create: $resolved"
    }
    if (-not (Test-Path -LiteralPath $resolved)) { return }
    # A process that was just stopped can hold files briefly, so removal is retried before it is reported.
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try { Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop; return }
        catch { if ($attempt -eq 5) { Write-Warning "Package QA could not remove its isolated folder $resolved. $($_.Exception.Message)"; return }; Start-Sleep -Seconds 2 }
    }
}

function Get-BoundedProcessDiagnostic {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
    $reader = [IO.StreamReader]::new($Path,$true)
    try {
        $buffer = New-Object char[] 4096
        $count = $reader.ReadBlock($buffer,0,$buffer.Length)
        $text = if ($count -eq 0) { '' } else { -join $buffer[0..($count - 1)] }
        if ($reader.Peek() -ge 0) { $text += ' [diagnostic shortened]' }
    }
    finally { $reader.Dispose() }
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $text = [regex]::Replace($text,[regex]::Escape($env:USERPROFILE),'<user-profile>',[Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $text = [regex]::Replace($text,[regex]::Escape($env:LOCALAPPDATA),'<local-app-data>',[Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    $text = [regex]::Replace($text,'(?i)(?<key>(?:password|passwd|pwd|token|secret|api[-_]?key|client[-_]?secret)\s*(?:=|:)\s*)(?:"[^"]*"|''[^'']*''|[^\s,;]+)','${key}[REDACTED]')
    $text = [regex]::Replace($text,'(?i)(Authorization:\s*Bearer\s+)\S+','$1[REDACTED]')
    $text = [regex]::Replace($text,'(?i)(://[^:/\s]+:)[^@\s]+@','$1[REDACTED]@')
    return ([regex]::Replace($text,'\s+',' ')).Trim()
}

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [AllowEmptyString()][string]$RawArguments = '',
        [Parameter(Mandatory)][string]$Description,
        [int]$TimeoutSeconds = $ProcessTimeoutSeconds
    )
    $nonce = [guid]::NewGuid().ToString('N')
    $stdoutPath = Join-Path $temporaryRoot "process-$nonce.stdout.log"
    $stderrPath = Join-Path $temporaryRoot "process-$nonce.stderr.log"
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $RawArguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdout = [IO.File]::Open($stdoutPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    $stderr = [IO.File]::Open($stderrPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try {
        if (-not $process.Start()) { throw "$Description did not start." }
        $stdoutCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $stderrCopy = $process.StandardError.BaseStream.CopyToAsync($stderr)
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $processId = $process.Id
            $previousPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                & (Join-Path $env:SystemRoot 'System32\taskkill.exe') /PID $processId /T /F 2>$null | Out-Null
            }
            finally { $ErrorActionPreference = $previousPreference }
            if (-not $process.WaitForExit(15000)) {
                try { $process.Kill() } catch { }
                if (-not $process.WaitForExit(5000)) {
                    throw "$Description exceeded the $TimeoutSeconds-second timeout and could not be terminated."
                }
            }
            throw "$Description exceeded the $TimeoutSeconds-second timeout."
        }
        if (-not $stdoutCopy.Wait(15000) -or -not $stderrCopy.Wait(15000)) {
            throw "$Description output did not close after the process exited."
        }
        $process.Refresh()
        if (-not $process.HasExited -or $null -eq $process.ExitCode) {
            throw "$Description completed without an available process exit code."
        }
        $exitCode = $process.ExitCode
    }
    finally {
        $stdout.Dispose()
        $stderr.Dispose()
        $process.Dispose()
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        StandardError = Get-BoundedProcessDiagnostic -Path $stderrPath
    }
}

function Invoke-PackagedLauncher {
    param([string]$Launcher,[string[]]$Arguments)
    $quotedArguments = @($Arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' '
    $result = Invoke-BoundedProcess -FilePath $Launcher -RawArguments $quotedArguments -Description 'Packaged launcher'
    $script:LastLauncherStdErr = $result.StandardError
    return $result.ExitCode
}

function Invoke-ContainedExecutable {
    param([string]$Executable,[string[]]$Arguments,[string]$Description)
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

function Get-MsiProperty {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$Name)

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    $record = $null
    try {
        $database = $installer.OpenDatabase([IO.Path]::GetFullPath($Path),0)
        $query = 'SELECT `Value` FROM `Property` WHERE `Property`=''{0}''' -f $Name.Replace("'","''")
        $view = $database.OpenView($query)
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) { return '' }
        return [string]$record.StringData(1)
    }
    finally {
        if ($null -ne $record) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        if ($null -ne $view) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        if ($null -ne $installer) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
    }
}

if ($ProcessHelperSelfTest) {
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AVWorkstationToolkit-process-helper-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        $powershellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $success = Invoke-BoundedProcess -FilePath $powershellPath -RawArguments '-NoProfile -Command "exit 0"' -Description 'Exit-zero regression child' -TimeoutSeconds 10
        Assert-Equal 0 $success.ExitCode 'A real zero exit code did not pass.'
        $failure = Invoke-BoundedProcess -FilePath $powershellPath -RawArguments '-NoProfile -Command "[Console]::Error.WriteLine(''bounded failure password: fixture-secret''); exit 1"' -Description 'Exit-one regression child' -TimeoutSeconds 10
        Assert-Equal 1 $failure.ExitCode 'A real nonzero exit code was not preserved.'
        Assert-True ($failure.StandardError -eq 'bounded failure password: [REDACTED]') 'Redirected standard error was not captured and sanitized safely.'
        $timedOut = $false
        try { [void](Invoke-BoundedProcess -FilePath $powershellPath -RawArguments '-NoProfile -Command "Start-Sleep -Seconds 30"' -Description 'Timeout regression child' -TimeoutSeconds 1) }
        catch { $timedOut = $_.Exception.Message -match 'exceeded the 1-second timeout' }
        Assert-True $timedOut 'A process timeout was not reported as failure.'
        Write-Output 'PACKAGE_PROCESS_HELPER_OK exit0=0 exit1=1 timeout=rejected'
        exit 0
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
    }
}

$requiredPaths = if ($SignaturePolicyOnly) { @($standalonePath,$msiPath) } else { @($standalonePath,$msiPath,$portablePath,$checksumPath,$releaseManifestPath,$sbomPath,$thirdPartyNoticesPath) }
foreach ($path in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Release artifact is missing: $path" }
}

if ($SignaturePolicyOnly) {
    Invoke-Check 'Executable and MSI signatures satisfy the selected release policy' {
        foreach ($artifactPath in @($standalonePath,$msiPath)) {
            $status = (Get-AuthenticodeSignature -LiteralPath $artifactPath).Status
            if ($RequireSignature) { Assert-Equal 'Valid' ([string]$status) "Signature is not valid: $artifactPath" }
            else { Assert-True ($status -in @('Valid','NotSigned','UnknownError')) ("Unexpected signature state for {0}: {1}" -f $artifactPath,$status) }
        }
    }
    Write-Host ("Package QA summary: {0} passed; {1} skipped; {2} failed" -f $script:Passed,$script:Skipped,$script:Failed) -ForegroundColor Cyan
    if ($script:Failed -gt 0) { Write-Host ($script:Failures -join "`n") -ForegroundColor Red; exit 1 }
    exit 0
}

$productUpgradeCode = '{7A3A4978-78F0-5824-B93F-A2C741BF853E}'
$localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($localApplicationData)) { throw 'Local application data directory is unavailable.' }
$userProfileRoot = [IO.Path]::GetFullPath((Join-Path $localApplicationData 'AVWorkstationToolkit')).TrimEnd('\')
$systemTemporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$runIdentity = [guid]::NewGuid().ToString('N')
$temporaryRoot = Join-Path $systemTemporary ('AVWorkstationToolkit-package-work-' + $runIdentity)
# The packaged launcher accepts exactly this shape for the production smoke: a package-qa folder directly beneath the temporary folder.
$dataRoot = Join-Path $systemTemporary ('AVWorkstationToolkit-package-qa-' + $runIdentity)
foreach ($isolatedRoot in @($temporaryRoot,$dataRoot)) {
    if ($isolatedRoot.Equals($userProfileRoot,[StringComparison]::OrdinalIgnoreCase) -or
        $isolatedRoot.StartsWith($userProfileRoot + '\',[StringComparison]::OrdinalIgnoreCase)) {
        throw "Package QA isolation could not be established: $isolatedRoot is inside the user's AV Workstation Toolkit profile."
    }
    if (Test-Path -LiteralPath $isolatedRoot) { throw "Package QA isolation could not be established: $isolatedRoot already exists." }
}
$portableExtract = Join-Path $temporaryRoot 'portable'
$offlineExtract = Join-Path $temporaryRoot 'offline'
$offlineDataRoot = Join-Path $temporaryRoot 'offline-data'
$msiExtract = Join-Path $temporaryRoot 'msi'
$downloadedRoot = Join-Path $temporaryRoot 'downloaded'
$downloadedExecutable = Join-Path $downloadedRoot 'AVWorkstationToolkit.exe'
# Taken before anything is launched; a copy of AV Workstation Toolkit the user already has open is noted, not stopped.
$userToolkitProcesses = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'AVWorkstationToolkit*' } | ForEach-Object { $_.Id })
$profileBefore = Get-TreeSnapshot -Root $userProfileRoot
$installedBefore = Get-InstalledProductState
New-Item -ItemType Directory -Path $portableExtract,$offlineExtract,$msiExtract,$downloadedRoot,$dataRoot -Force | Out-Null
if (((Get-Item -LiteralPath $dataRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Package QA isolation could not be established: $dataRoot is a reparse point."
}
Write-Host "Package QA data root: $dataRoot"
Write-Host "User profile compared before and after, never written: $userProfileRoot"
Copy-Item -LiteralPath $standalonePath -Destination $downloadedExecutable
$script:runtimeApplicationRoot = ''
$script:migrationEvidence = ''

try {
    Invoke-Check 'Release manifest identity and artifact hashes are valid' {
        $manifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
        Assert-Equal 3 $manifest.SchemaVersion 'Release schema differs.'
        Assert-Equal 'AV Workstation Toolkit' $manifest.Product 'Release product differs.'
        Assert-Equal 'Apache-2.0' ([string]$manifest.ProjectLicense) 'Release project license differs.'
        Assert-Equal $version $manifest.Version 'Release version differs.'
        Assert-Equal $PrereleaseLabel ([string]$manifest.Prerelease) 'Release pre-release label differs.'
        Assert-Equal $releaseName ([string]$manifest.ReleaseName) 'Release name differs.'
        if ($PrereleaseLabel.Length -gt 0) { Assert-Equal 'ReleaseCandidate' ([string]$manifest.BuildChannel) 'A pre-release was not built on the ReleaseCandidate channel.' }
        Assert-Equal 'standalone-executable' $manifest.Distribution 'Release distribution differs.'
        Assert-Equal 'x64' ([string]$manifest.Architecture) 'Release architecture differs.'
        Assert-Equal 'net10.0-windows' ([string]$manifest.TargetFramework) 'Release target framework differs.'
        Assert-Equal 'Microsoft.NETCore.App.Runtime.win-x64/10.0.11' ([string]$manifest.TargetRuntime) 'Release target runtime differs.'
        Assert-Equal 'Microsoft.NETCore.App.Host.win-x64/10.0.11' ([string]$manifest.TargetHost) 'Release target host differs.'
        Assert-True ([string]$manifest.SelectedSdk -match '^10\.0\.\d{3}$') 'Release selected SDK is missing or invalid.'
        Assert-Equal 'Release' ([string]$manifest.BuildMode) 'Release build mode differs.'
        Assert-True ([string]$manifest.BuildChannel -in @('Development','ReleaseCandidate','Production')) 'Release channel is invalid.'
        Assert-True ([string]$manifest.CommitSha -match '^[a-f0-9]{40}$') 'Release commit SHA is invalid.'
        Assert-True ($manifest.SourceDirty -is [bool]) 'Release source-dirty state is not Boolean.'
        Assert-Equal 'Passed' ([string]$manifest.NuGetAudit.Status) 'NuGet audit did not pass.'
        Assert-Equal 0 ([int]$manifest.NuGetAudit.VulnerablePackages) 'Release reports vulnerable NuGet packages.'
        Assert-Equal 'net10.0-windows' ([string]$manifest.Launcher.TargetFramework) 'Release launcher target framework differs.'
        Assert-Equal '10.0.11' ([string]$manifest.Launcher.RuntimeFrameworkVersion) 'Release launcher runtime patch differs.'
        # The authoritative check is against the launcher's own verified extraction below, which is
        # independent of build configuration. Here only assert the field is a usable positive count.
        Assert-True ([int]$manifest.EmbeddedPayloadFiles -gt 0) 'Release manifest does not report an embedded payload count.'
        Assert-Equal (Split-Path -Leaf $sbomPath) ([string]$manifest.Sbom.Name) 'Release SBOM filename differs.'
        Assert-Equal (Get-FileHash -LiteralPath $sbomPath -Algorithm SHA256).Hash ([string]$manifest.Sbom.Sha256) 'Release SBOM hash differs.'
        Assert-Equal (Split-Path -Leaf $checksumPath) ([string]$manifest.Checksums.Name) 'Release checksum filename differs.'
        Assert-Equal 'SHA-256' ([string]$manifest.Checksums.Algorithm) 'Release checksum algorithm differs.'
        Assert-Equal 1 @($manifest.Checksums.ExcludedFiles).Count 'Checksum exclusion list is broader than one file.'
        Assert-Equal (Split-Path -Leaf $checksumPath) ([string]@($manifest.Checksums.ExcludedFiles)[0]) 'Checksum file must exclude only itself.'
        Assert-True ((Split-Path -Leaf $releaseManifestPath) -in @($manifest.Checksums.CoveredFiles)) 'Release manifest is not declared as checksum-covered.'
        $expectedArtifactCount = if (@($manifest.BundledExternalPackages).Count -gt 0) { 7 } else { 6 }
        Assert-Equal $expectedArtifactCount @($manifest.Artifacts).Count 'Release artifact count differs.'
        foreach ($artifact in @($manifest.Artifacts)) {
            $artifactPath = Join-Path $ReleaseRoot ([string]$artifact.Name)
            Assert-True (Test-Path -LiteralPath $artifactPath -PathType Leaf) "Manifest artifact is missing: $($artifact.Name)"
            Assert-Equal ([string]$artifact.Sha256) (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash "Manifest hash differs for $($artifact.Name)."
        }
    }

    Invoke-Check 'Published SHA-256 checksum file matches all distributables' {
        $lines = @(Get-Content -LiteralPath $checksumPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $manifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
        Assert-Equal (@($manifest.Artifacts).Count + 1) $lines.Count 'Checksum entry count differs.'
        $coveredNames = [System.Collections.Generic.List[string]]::new()
        foreach ($line in $lines) {
            if ($line -notmatch '^([A-F0-9]{64}) \*(.+)$') { throw "Malformed checksum line: $line" }
            $coveredNames.Add($Matches[2]) | Out-Null
            $artifactPath = Join-Path $ReleaseRoot $Matches[2]
            Assert-Equal $Matches[1] (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash "Checksum differs for $($Matches[2])."
        }
        Assert-True ((Split-Path -Leaf $releaseManifestPath) -in @($coveredNames)) 'Checksum list omits the release manifest.'
        Assert-True ((Split-Path -Leaf $checksumPath) -notin @($coveredNames)) 'Checksum list contains a circular self-digest.'
        Assert-Equal (@($manifest.Checksums.CoveredFiles | Sort-Object) -join '|') (@($coveredNames | Sort-Object) -join '|') 'Checksum-covered files differ from release metadata.'
    }

    Invoke-Check 'Published third-party notices match the reviewed source inventory' {
        $sourceNoticesPath = Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md'
        Assert-Equal (Get-FileHash -LiteralPath $sourceNoticesPath -Algorithm SHA256).Hash (Get-FileHash -LiteralPath $thirdPartyNoticesPath -Algorithm SHA256).Hash 'Published third-party notices differ from the reviewed source file.'
        $noticeText = Get-Content -LiteralPath $thirdPartyNoticesPath -Raw
        foreach ($requiredNotice in @('SSH.NET','BouncyCastle.Cryptography','Microsoft.NETCore.App.Runtime.win-x64','Microsoft.NETCore.App.Host.win-x64','WixToolset.Sdk','Open Source Maintenance Fee')) {
            Assert-True ($noticeText.Contains($requiredNotice)) "Third-party notices omit: $requiredNotice"
        }
        Assert-True ($noticeText -notmatch '(?i)(?:[A-Z]:\\Users\\|/Users/|/home/)') 'Third-party notices contain a local developer path.'
    }

    Invoke-Check 'Published Apache-2.0 project license matches the reviewed source' {
        $sourceLicensePath = Join-Path $repositoryRoot 'LICENSE'
        Assert-True (Test-Path -LiteralPath $projectLicensePath -PathType Leaf) 'Published Apache-2.0 project license is missing.'
        Assert-Equal (Get-FileHash -LiteralPath $sourceLicensePath -Algorithm SHA256).Hash (Get-FileHash -LiteralPath $projectLicensePath -Algorithm SHA256).Hash 'Published project license differs from the root LICENSE.'
        $licenseText = Get-Content -LiteralPath $projectLicensePath -Raw
        Assert-True ($licenseText -match 'Apache License\s+Version 2\.0, January 2004') 'Published project license is not Apache License 2.0.'
    }

    Invoke-Check 'Executable and MSI signatures satisfy the selected release policy' {
        $manifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
        foreach ($artifactPath in @($standalonePath,$msiPath)) {
            $signature = Get-AuthenticodeSignature -LiteralPath $artifactPath
            if ($RequireSignature) {
                Assert-Equal 'Valid' ([string]$signature.Status) "Signature is not valid: $artifactPath"
                Assert-True ($null -ne $signature.TimeStamperCertificate) "RFC3161 timestamp is missing: $artifactPath"
            }
            else { Assert-True ($signature.Status -in @('Valid','NotSigned')) ("Unexpected signature state for {0}: {1}" -f $artifactPath,$signature.Status) }
            $artifactMetadata = @($manifest.Artifacts | Where-Object Name -eq (Split-Path -Leaf $artifactPath) | Select-Object -First 1)
            Assert-Equal 1 $artifactMetadata.Count "Release metadata omitted signature state: $artifactPath"
            Assert-Equal ([string]$signature.Status) ([string]$artifactMetadata[0].SignatureStatus) "Manifest signature status differs: $artifactPath"
            $expectedTimestamp = if ($signature.Status -eq 'Valid' -and $null -ne $signature.TimeStamperCertificate) { 'Valid' } elseif ($signature.Status -eq 'Valid') { 'Missing' } else { 'NotApplicable' }
            Assert-Equal $expectedTimestamp ([string]$artifactMetadata[0].TimestampStatus) "Manifest timestamp status differs: $artifactPath"
        }
        if ($RequireSignature) { Assert-True ([bool]$manifest.Signed -and [string]$manifest.SignaturePolicy -eq 'Required') 'Signature-required release metadata is not explicit.' }
        elseif (-not [bool]$manifest.Signed) { Assert-Equal 'Optional' ([string]$manifest.SignaturePolicy) 'Unsigned development release policy is not explicit.' }
    }

    Invoke-Check 'CycloneDX SBOM is valid, deterministic, and sanitized' {
        $sbomText = Get-Content -LiteralPath $sbomPath -Raw
        $sbom = $sbomText | ConvertFrom-Json
        Assert-Equal 'CycloneDX' ([string]$sbom.bomFormat) 'SBOM format differs.'
        Assert-Equal '1.6' ([string]$sbom.specVersion) 'SBOM specification version differs.'
        Assert-Equal 'AV Workstation Toolkit' ([string]$sbom.metadata.component.name) 'SBOM root component differs.'
        Assert-Equal 'Apache-2.0' ([string]$sbom.metadata.component.licenses[0].license.id) 'SBOM root project license differs.'
        Assert-Equal $releaseName ([string]$sbom.metadata.component.version) 'SBOM version differs.'
        $componentNames = @($sbom.components.name)
        foreach ($expectedComponent in @('SSH.NET','BouncyCastle.Cryptography','Microsoft.Extensions.Logging.Abstractions','Microsoft.NETCore.App.Runtime.win-x64','Microsoft.NETCore.App.Host.win-x64','WixToolset.Sdk')) {
            Assert-True ($expectedComponent -in $componentNames) "SBOM omits reviewed dependency: $expectedComponent"
        }
        Assert-True ('Microsoft.WindowsDesktop.App.Runtime.win-x64' -notin $componentNames) 'SBOM retains the incorrect Windows Desktop runtime identity.'
        foreach ($component in @($sbom.components)) {
            Assert-True (@($component.licenses).Count -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$component.licenses[0].license.id)) "SBOM component has no reviewed license expression: $($component.name)"
            Assert-True (@($component.properties | Where-Object name -eq 'avworkstationtoolkit:dependency:distribution').Count -eq 1) "SBOM component has no distribution classification: $($component.name)"
        }
        foreach ($expectedLicense in @(@('SSH.NET','MIT'),@('BouncyCastle.Cryptography','MIT'),@('WixToolset.Sdk','MS-RL'))) {
            $component = @($sbom.components | Where-Object name -eq $expectedLicense[0])
            Assert-Equal 1 $component.Count "SBOM component is missing or duplicated: $($expectedLicense[0])"
            Assert-Equal $expectedLicense[1] ([string]$component[0].licenses[0].license.id) "Third-party license changed unexpectedly: $($expectedLicense[0])"
        }
        $dependencyInjection = @($sbom.components | Where-Object name -eq 'Microsoft.Extensions.DependencyInjection.Abstractions')
        Assert-Equal 1 $dependencyInjection.Count 'SBOM dependency-injection component is missing or duplicated.'
        Assert-Equal 'required' ([string]$dependencyInjection[0].scope) 'Untrimmed compiled runtime dependency is not represented as distributed.'
        Assert-Equal 'embedded' ([string]@($dependencyInjection[0].properties | Where-Object name -eq 'avworkstationtoolkit:dependency:distribution')[0].value) 'Compiled runtime dependency distribution reason differs.'
        $wix = @($sbom.components | Where-Object name -eq 'WixToolset.Sdk')
        Assert-Equal 1 $wix.Count 'WiX SBOM component is missing or duplicated.'
        Assert-Equal 'excluded' ([string]$wix[0].scope) 'Build-only WiX dependency is incorrectly represented as distributed.'
        Assert-True (@($wix[0].properties | Where-Object name -eq 'avworkstationtoolkit:wix:binary-terms').Count -eq 1) 'WiX binary terms are absent from the SBOM.'
        Assert-True ($sbomText -notmatch '(?i)(?:[A-Z]:\\Users\\|/Users/|/home/)' -and
            ([string]::IsNullOrWhiteSpace($env:USERNAME) -or $sbomText -notmatch [regex]::Escape($env:USERNAME))) 'SBOM contains a local user or developer path.'
    }

    Invoke-Check 'Downloaded standalone executable runs with no adjacent payload' {
        Assert-Equal 1 @(Get-ChildItem -LiteralPath $downloadedRoot -File).Count 'Downloaded executable directory contains unexpected companion files.'
        $versionInfo = (Get-Item -LiteralPath $downloadedExecutable).VersionInfo
        Assert-True ([string]$versionInfo.FileVersion -like "$version*" -and [string]$versionInfo.ProductVersion -like "$version*") 'Launcher file or product version differs.'
        Assert-Equal 'AV Workstation Toolkit' ([string]$versionInfo.ProductName) 'Launcher product name differs.'
        Assert-Equal 'AV Workstation Toolkit Project' ([string]$versionInfo.CompanyName) 'Launcher project-publisher identity differs.'
        Assert-True (-not [string]::IsNullOrWhiteSpace([string]$versionInfo.FileDescription)) 'Launcher file description is missing.'
        Assert-Equal 'AVWorkstationToolkit.dll' ([string]$versionInfo.OriginalFilename) 'Launcher original filename differs from its managed assembly identity.'
        Assert-Equal 'AVWorkstationToolkit.dll' ([string]$versionInfo.InternalName) 'Launcher internal name differs from its managed assembly identity.'
        Assert-True ([string]$versionInfo.LegalCopyright -match 'AV Workstation Toolkit contributors') 'Launcher copyright metadata is missing.'
        $launcherSignature = (Get-AuthenticodeSignature -LiteralPath $downloadedExecutable).Status
        if ($RequireSignature) { Assert-Equal 'Valid' ([string]$launcherSignature) 'Packaged launcher signature is not valid.' }
        else { Assert-True ($launcherSignature -in @('Valid','NotSigned')) "Unexpected launcher signature state: $launcherSignature" }
        $diagnosticPath = Join-Path $temporaryRoot 'downloaded-diagnostic.json'
        $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @('--verify',$diagnosticPath,'--data-root',$dataRoot)
        Assert-Equal 0 $exitCode 'Launcher verification exited unsuccessfully.'
        $diagnostic = Get-Content -LiteralPath $diagnosticPath -Raw | ConvertFrom-Json
        Assert-True ([bool]$diagnostic.Success) "Launcher diagnostic failed: $($diagnostic.IntegrityMessage)"
        Assert-Equal 3 ([int]$diagnostic.SchemaVersion) 'Launcher diagnostic schema differs.'
        Assert-Equal $version ([string]$diagnostic.Version) 'Launcher diagnostic version differs.'
        Assert-True (([string]$diagnostic.RuntimeFramework) -match '^\.NET 10\.0\.') 'Launcher is not running its embedded .NET 10 runtime.'
        Assert-True (([version]$diagnostic.RuntimeVersion) -ge [version]'10.0.11') 'Launcher embedded runtime predates the reviewed .NET 10 security baseline.'
        Assert-Equal 'X64' ([string]$diagnostic.RuntimeArchitecture) 'Launcher runtime architecture differs.'
        # Declared release provenance must equal what the launcher actually extracted and hash-verified.
        # This holds for both a development build and one carrying a signed reference-catalog baseline,
        # so it needs no configuration-specific number.
        $declaredPayloadFiles = [int](Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json).EmbeddedPayloadFiles
        Assert-Equal $declaredPayloadFiles ([int]$diagnostic.FilesVerified) 'Release manifest EmbeddedPayloadFiles does not match the payload the launcher verified.'
        Assert-True ([bool]$diagnostic.FrontendPresent) 'Extracted frontend is missing.'
        Assert-Equal 'Compiled C# WPF' ([string]$diagnostic.FrontendArchitecture) 'Packaged default frontend is not compiled WPF.'
        Assert-True ([bool]$diagnostic.WorkerPresent) 'Extracted compiled worker is missing.'
        Assert-True (-not [bool]$diagnostic.LegacyRecoveryPresent) 'Legacy recovery is still present in the packaged runtime.'
        Assert-True ('PowerShellPath' -notin @($diagnostic.PSObject.Properties.Name) -and 'PowerShellPresent' -notin @($diagnostic.PSObject.Properties.Name)) 'Launcher diagnostics still expose a PowerShell runtime dependency.'
        $expectedRuntimeRoot = [IO.Path]::GetFullPath((Join-Path $dataRoot (Join-Path 'runtime' $version)))
        $actualRuntimeRoot = [IO.Path]::GetFullPath([string]$diagnostic.ApplicationRoot)
        Assert-Equal $expectedRuntimeRoot $actualRuntimeRoot 'Embedded runtime does not use the deterministic versioned location.'
        $workerPath = [IO.Path]::GetFullPath([string]$diagnostic.WorkerPath)
        Assert-Equal (Join-Path $actualRuntimeRoot 'worker\AVWorkstationToolkit.Worker.exe') $workerPath 'Compiled worker is not at the exact packaged runtime path.'
        Assert-Equal (Get-FileHash -LiteralPath $workerPath -Algorithm SHA256).Hash ([string]$diagnostic.WorkerSha256) 'Compiled worker diagnostic hash differs.'
        Assert-Equal 0 @(Get-ChildItem -LiteralPath $actualRuntimeRoot -Recurse -File -Filter '*DevHost*').Count 'A worker development host was extracted from the release executable.'
        $workerBinaryText = Get-Content -LiteralPath $workerPath -Raw -Encoding Unicode
        Assert-True ($workerBinaryText -notmatch '--test-mode|--live-rehearsal|--repository-root|CompiledMigrationWorkerLauncher|CompiledLiveRehearsalWorkerLauncher|LiveRehearsalRootPolicy|DeterministicFakePackageExecutor') 'The packaged worker bytes contain a developer activation or fake-executor boundary.'
        $invalidRequestPath = Join-Path $actualRuntimeRoot 'request.json'
        Assert-Equal 2 (Invoke-ContainedExecutable -Executable $workerPath -Arguments @('--test-mode','--root',$actualRuntimeRoot,'--request',$invalidRequestPath) -Description 'Packaged worker test-mode rejection') 'Packaged worker accepted --test-mode.'
        Assert-Equal 2 (Invoke-ContainedExecutable -Executable $workerPath -Arguments @('--live-rehearsal','--root',$actualRuntimeRoot,'--request',$invalidRequestPath,'--repository-root',$actualRuntimeRoot) -Description 'Packaged worker rehearsal-mode rejection') 'Packaged worker accepted --live-rehearsal.'
        Assert-Equal 2 (Invoke-ContainedExecutable -Executable $workerPath -Arguments @('--production','--root',$actualRuntimeRoot,'--request',$invalidRequestPath,'--repository-root',$actualRuntimeRoot) -Description 'Packaged worker repository-root rejection') 'Packaged worker accepted a repository-root activation in place of the application root.'
        $productionBoundaryExit = Invoke-ContainedExecutable -Executable $workerPath -Arguments @('--production','--root',$actualRuntimeRoot,'--request',$invalidRequestPath,'--application-root',$actualRuntimeRoot) -Description 'Packaged worker production invocation recognition'
        Assert-True ($productionBoundaryExit -ne 2) 'Packaged worker did not recognize its exact production invocation shape.'
        $releaseManifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
        Assert-Equal 'Compiled C# WPF' ([string]$releaseManifest.CompiledRuntime.Primary) 'Release manifest primary runtime differs.'
        Assert-Equal 'AVWorkstationToolkit.Worker.exe' ([string]$releaseManifest.CompiledRuntime.WorkerName) 'Release manifest worker identity differs.'
        Assert-Equal ([string]$diagnostic.WorkerSha256) ([string]$releaseManifest.CompiledRuntime.WorkerSha256) 'Release manifest worker hash differs.'
        Assert-Equal 'Retired from shipping' ([string]$releaseManifest.CompiledRuntime.LegacyFallback) 'Release manifest does not record final legacy retirement.'
        $workerSignature = Get-AuthenticodeSignature -LiteralPath $workerPath
        Assert-Equal ([string]$workerSignature.Status) ([string]$releaseManifest.CompiledRuntime.WorkerSignatureStatus) 'Release manifest worker signature state differs.'
        if ($RequireSignature) { Assert-Equal 'Valid' ([string]$workerSignature.Status) 'Packaged compiled worker signature is not valid.' }
        foreach ($relativeNotice in @('notices\THIRD-PARTY-NOTICES.md','notices\PROJECT-LICENSE.txt','notices\DOTNET-LICENSE.txt','notices\DOTNET-THIRD-PARTY-NOTICES.txt')) {
            $runtimeNoticePath = Join-Path ([string]$diagnostic.ApplicationRoot) $relativeNotice
            Assert-True (Test-Path -LiteralPath $runtimeNoticePath -PathType Leaf) "Extracted runtime notice is missing: $relativeNotice"
            Assert-True ((Get-Item -LiteralPath $runtimeNoticePath).Length -gt 500) "Extracted runtime notice is unexpectedly small: $relativeNotice"
        }
        $script:runtimeApplicationRoot = [string]$diagnostic.ApplicationRoot
        foreach ($retiredPath in @('app\AVWorkstationToolkit.xaml','scripts\Start-AVWorkstationToolkit.ps1','scripts\Invoke-AVWorkstationToolkitAction.ps1')) {
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $script:runtimeApplicationRoot $retiredPath))) "Retired runtime file is still packaged: $retiredPath"
        }
        Assert-Equal (Get-FileHash -LiteralPath $downloadedExecutable -Algorithm SHA256).Hash ([string]$releaseManifest.Launcher.Sha256) 'Release launcher hash differs.'
        Assert-Equal ([string]$launcherSignature) ([string]$releaseManifest.Launcher.SignatureStatus) 'Release launcher signature state differs.'
    }

    Invoke-Check 'Launcher removes recognized stale legacy files from an existing versioned runtime' {
        $retiredScript = Join-Path $script:runtimeApplicationRoot 'scripts\Start-AVWorkstationToolkit.ps1'
        New-Item -ItemType Directory -Path (Split-Path -Parent $retiredScript) -Force | Out-Null
        'retired fixture' | Set-Content -LiteralPath $retiredScript -Encoding UTF8
        $cleanupDiagnosticPath = Join-Path $temporaryRoot 'downloaded-diagnostic-cleanup.json'
        $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @('--verify',$cleanupDiagnosticPath,'--data-root',$dataRoot)
        Assert-Equal 0 $exitCode 'Launcher legacy-runtime cleanup verification failed.'
        $cleanupDiagnostic = Get-Content -LiteralPath $cleanupDiagnosticPath -Raw | ConvertFrom-Json
        Assert-Equal 1 ([int]$cleanupDiagnostic.RetiredRuntimeFilesRemoved) 'Launcher did not report the exact retired-file cleanup.'
        Assert-True (-not (Test-Path -LiteralPath $retiredScript)) 'Launcher left a recognized retired runtime file behind.'
    }

    Invoke-Check 'Second launcher verification leaves an unchanged runtime cache untouched' {
        Assert-True (-not [string]::IsNullOrWhiteSpace($script:runtimeApplicationRoot)) 'Initial launcher verification did not expose the runtime directory.'
        $before = @{}
        foreach ($file in @(Get-ChildItem -LiteralPath $script:runtimeApplicationRoot -Recurse -File)) {
            $relative = $file.FullName.Substring($script:runtimeApplicationRoot.Length).TrimStart('\')
            $before[$relative] = [pscustomobject]@{
                Hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
                LastWriteTimeUtc = $file.LastWriteTimeUtc
            }
        }
        Assert-True ($before.Count -ge 10) 'Versioned runtime contains too few files for no-rewrite validation.'
        $secondDiagnosticPath = Join-Path $temporaryRoot 'downloaded-diagnostic-second.json'
        $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @('--verify',$secondDiagnosticPath,'--data-root',$dataRoot)
        Assert-Equal 0 $exitCode 'Second launcher verification failed.'
        foreach ($file in @(Get-ChildItem -LiteralPath $script:runtimeApplicationRoot -Recurse -File)) {
            $relative = $file.FullName.Substring($script:runtimeApplicationRoot.Length).TrimStart('\')
            Assert-True $before.ContainsKey($relative) "Second verification added an unexpected runtime file: $relative"
            Assert-Equal $before[$relative].Hash (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash "Second verification changed runtime content: $relative"
            Assert-Equal $before[$relative].LastWriteTimeUtc $file.LastWriteTimeUtc "Second verification rewrote unchanged runtime content: $relative"
        }
        # The extracted payload contract: every runtime input the compiled product parses must be
        # present, the notices the release is obliged to carry must be present, and the reviewed
        # repository-only manifests that no shipping code parses must be absent.
        foreach ($requiredPayloadFile in @(
            'manifests\managed-applications.json','manifests\external-applications.json',
            'manifests\commercial-av-catalog.json','manifests\software-compatibility.json',
            'manifests\hardware-identities.json','notices\THIRD-PARTY-NOTICES.md',
            'notices\PROJECT-LICENSE.txt','notices\DOTNET-LICENSE.txt','notices\DOTNET-THIRD-PARTY-NOTICES.txt',
            'worker\AVWorkstationToolkit.Worker.exe')) {
            $path = Join-Path $script:runtimeApplicationRoot $requiredPayloadFile
            Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Packaged runtime is missing a required payload file: $requiredPayloadFile"
        }
        foreach ($repositoryOnlyManifest in @('process-launch-policy.json','winget-team-baseline.json')) {
            $path = Join-Path $script:runtimeApplicationRoot (Join-Path 'manifests' $repositoryOnlyManifest)
            Assert-True (-not (Test-Path -LiteralPath $path)) "Packaged runtime embedded a repository-only manifest that no shipping code parses: $repositoryOnlyManifest"
        }
    }

    Invoke-Check 'MSI and executable product identities agree' {
        Assert-Equal 'AV Workstation Toolkit' (Get-MsiProperty -Path $msiPath -Name 'ProductName') 'MSI product name differs.'
        Assert-Equal '{7A3A4978-78F0-5824-B93F-A2C741BF853E}' (Get-MsiProperty -Path $msiPath -Name 'UpgradeCode') 'MSI upgrade family no longer matches the released legacy package family.'
        Assert-True ((Get-MsiProperty -Path $msiPath -Name 'ProductCode') -ne '{8030A656-389C-46B3-8D17-F7F1912C2864}') 'MSI ProductCode did not change for the 1.1.1 major upgrade.'
        Assert-Equal 'AV Workstation Toolkit Project' (Get-MsiProperty -Path $msiPath -Name 'Manufacturer') 'MSI manufacturer differs.'
        Assert-Equal $version (Get-MsiProperty -Path $msiPath -Name 'ProductVersion') 'MSI product version differs.'
        Assert-Equal 'AVWorkstationToolkitProductIcon.ico' (Get-MsiProperty -Path $msiPath -Name 'ARPPRODUCTICON') 'MSI Installed Apps icon identity differs.'
        $versionInfo = (Get-Item -LiteralPath $standalonePath).VersionInfo
        Assert-Equal 'AV Workstation Toolkit' ([string]$versionInfo.FileDescription) 'Launcher file description differs.'
        Assert-Equal 'AV Workstation Toolkit Project' ([string]$versionInfo.CompanyName) 'Launcher project-publisher metadata differs.'
        Assert-Equal 'AVWorkstationToolkit' ([IO.Path]::GetFileNameWithoutExtension([string]$versionInfo.OriginalFilename)) 'Launcher original filename base differs.'
        Assert-Equal 'AVWorkstationToolkit' ([IO.Path]::GetFileNameWithoutExtension([string]$versionInfo.InternalName)) 'Launcher internal-name base differs.'
        Assert-True ([string]$versionInfo.ProductVersion -like "$version*") 'EXE product version differs from MSI/release identity.'
        # The product version people see names the exact candidate; "1.1.3+commit" must not pass for a release candidate.
        Assert-True ([string]$versionInfo.ProductVersion -ceq $releaseName -or ([string]$versionInfo.ProductVersion).StartsWith("$releaseName+",[StringComparison]::Ordinal)) 'EXE product version does not name the exact release.'
        Add-Type -AssemblyName System.Drawing
        # Use a unique probe filename so the Windows Shell icon cache cannot return
        # imagery retained for an earlier build at the stable release path.
        $iconProbePath = Join-Path $temporaryRoot ("icon-probe-{0}.exe" -f [guid]::NewGuid().ToString('N'))
        Copy-Item -LiteralPath $standalonePath -Destination $iconProbePath
        $packagedIcon = [Drawing.Icon]::ExtractAssociatedIcon($iconProbePath)
        try {
            Assert-True ($null -ne $packagedIcon) 'Standalone executable has no extractable Windows application icon.'
            $packagedBitmap = $packagedIcon.ToBitmap()
            try {
                Assert-Equal 32 $packagedBitmap.Width 'Packaged executable icon width differs.'
                Assert-Equal 32 $packagedBitmap.Height 'Packaged executable icon height differs.'
                $signalPixels = 0
                $navyPixels = 0
                $transparentPixels = 0
                for ($x = 0; $x -lt $packagedBitmap.Width; $x++) {
                    for ($y = 0; $y -lt $packagedBitmap.Height; $y++) {
                        $pixel = $packagedBitmap.GetPixel($x,$y)
                        if ($pixel.A -lt 32) { $transparentPixels++ }
                        elseif ($pixel.B -gt 100 -and $pixel.G -gt 45 -and $pixel.B -gt ($pixel.R + 40)) { $signalPixels++ }
                        if ($pixel.A -ge 32 -and $pixel.B -gt 25 -and $pixel.B -gt $pixel.G -and $pixel.G -gt $pixel.R -and $pixel.R -lt 40) { $navyPixels++ }
                    }
                }
                Assert-True ($signalPixels -ge 100 -and $navyPixels -ge 500 -and $transparentPixels -ge 150) 'Packaged executable icon does not contain the canonical cyan signal, navy tile, and transparent boundary.'
            }
            finally {
                $packagedBitmap.Dispose()
            }
        }
        finally {
            if ($null -ne $packagedIcon) { $packagedIcon.Dispose() }
        }
    }

    Expand-Archive -LiteralPath $portablePath -DestinationPath $portableExtract
    $portableLauncher = Join-Path $portableExtract 'AVWorkstationToolkit.exe'
    Invoke-Check 'Portable ZIP contains the same single standalone executable' {
        $portableFiles = @(Get-ChildItem -LiteralPath $portableExtract -Recurse -File)
        Assert-Equal 1 $portableFiles.Count 'Portable ZIP contains unexpected companion files.'
        Assert-True (Test-Path -LiteralPath $portableLauncher -PathType Leaf) 'Portable ZIP is missing AVWorkstationToolkit.exe.'
        Assert-Equal (Get-FileHash -LiteralPath $standalonePath -Algorithm SHA256).Hash (Get-FileHash -LiteralPath $portableLauncher -Algorithm SHA256).Hash 'Portable executable differs from the direct download.'
        Assert-Equal 0 @(Get-ChildItem -LiteralPath $portableExtract -Recurse -File -Filter '*DevHost*').Count 'Portable ZIP contains a worker development host.'
    }

    Invoke-Check 'Packaged production smoke refuses to run without its isolated package QA data root' {
        # Every refused root is disposable and outside the user's profile, so even a regression here could not write there.
        $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @('--production-smoke')
        Assert-Equal 1 $exitCode 'The production smoke ran without a data root, which would be the user profile.'
        Assert-True ($script:LastLauncherStdErr -match 'package QA') "The refusal did not explain the package QA data root: $script:LastLauncherStdErr"
        foreach ($rejectedRoot in @(
            (Join-Path $temporaryRoot ('AVWorkstationToolkit-package-qa-' + $runIdentity)),
            (Join-Path $systemTemporary ('AVWorkstationToolkit-package-qa-' + [guid]::NewGuid().ToString('N').ToUpperInvariant())))) {
            $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @('--production-smoke','--data-root',$rejectedRoot)
            Assert-Equal 1 $exitCode "The production smoke accepted a data root outside its bounds: $rejectedRoot"
            Assert-True ($script:LastLauncherStdErr -match 'package QA data root') "The refusal did not explain the package QA data root: $script:LastLauncherStdErr"
            Assert-True (-not (Test-Path -LiteralPath $rejectedRoot)) "The refused production smoke still created $rejectedRoot."
        }
    }

    if ($SkipDesktopSmoke) {
        $script:Skipped++
        Write-Host 'SKIP  Packaged WPF control and workflow smoke test requires an interactive Windows desktop.' -ForegroundColor Yellow
    }
    else {
        Invoke-Check 'Packaged production compiled WPF smoke opens and reopens cleanly in the isolated data root' {
            $sessionPath = Join-Path $dataRoot 'migration\session.json'
            Assert-True (-not (Test-Path -LiteralPath $sessionPath)) 'The isolated data root already held a migration checklist.'
            $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @('--production-smoke','--data-root',$dataRoot)
            Assert-Equal 0 $exitCode "Packaged production compiled WPF smoke attempt 1 failed. $script:LastLauncherStdErr"
            Assert-True (Test-Path -LiteralPath $sessionPath -PathType Leaf) 'The first production smoke did not save its migration checklist in the isolated data root.'
            $session = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
            Assert-True ([string]$session.documentType -eq 'migration-session' -and [string]$session.source.profileId -eq 'package-qa-smoke') 'The isolated migration checklist is not the one the production smoke saved.'
            $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @('--production-smoke','--data-root',$dataRoot)
            Assert-Equal 0 $exitCode "Packaged production compiled WPF smoke attempt 2 failed. $script:LastLauncherStdErr"
            Assert-True (-not (Test-Path -LiteralPath $sessionPath)) 'The reopened production smoke did not reload and finish its saved migration checklist.'
            $script:migrationEvidence = 'saved-reloaded-finished'
        }
    }

    Invoke-Check 'Standalone launcher repairs a modified runtime cache from embedded content' {
        $diagnosticPath = Join-Path $temporaryRoot 'repair-before.json'
        $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @('--verify',$diagnosticPath,'--data-root',$dataRoot)
        Assert-Equal 0 $exitCode 'Initial runtime preparation failed.'
        $diagnostic = Get-Content -LiteralPath $diagnosticPath -Raw | ConvertFrom-Json
        $cachedCatalog = Join-Path ([string]$diagnostic.ApplicationRoot) 'manifests\managed-applications.json'
        $expectedHash = (Get-FileHash -LiteralPath $cachedCatalog -Algorithm SHA256).Hash
        $cachedWorker = Join-Path ([string]$diagnostic.ApplicationRoot) 'worker\AVWorkstationToolkit.Worker.exe'
        $expectedWorkerHash = (Get-FileHash -LiteralPath $cachedWorker -Algorithm SHA256).Hash
        Add-Content -LiteralPath $cachedCatalog -Value 'tamper test' -Encoding UTF8
        Add-Content -LiteralPath $cachedWorker -Value 'tamper test' -Encoding UTF8
        Assert-True ((Get-FileHash -LiteralPath $cachedCatalog -Algorithm SHA256).Hash -ne $expectedHash) 'Runtime cache tamper setup did not alter the file.'
        Assert-True ((Get-FileHash -LiteralPath $cachedWorker -Algorithm SHA256).Hash -ne $expectedWorkerHash) 'Compiled worker tamper setup did not alter the file.'
        $repairDiagnosticPath = Join-Path $temporaryRoot 'repair-after.json'
        $exitCode = Invoke-PackagedLauncher -Launcher $downloadedExecutable -Arguments @('--verify',$repairDiagnosticPath,'--data-root',$dataRoot)
        Assert-Equal 0 $exitCode 'Launcher did not repair the modified runtime cache.'
        Assert-Equal $expectedHash (Get-FileHash -LiteralPath $cachedCatalog -Algorithm SHA256).Hash 'Repaired runtime cache hash differs.'
        Assert-Equal $expectedWorkerHash (Get-FileHash -LiteralPath $cachedWorker -Algorithm SHA256).Hash 'Repaired compiled worker hash differs.'
    }

    Invoke-Check 'MSI administratively extracts a verifiable standalone AVWorkstationToolkit executable' {
        $arguments = '/a "{0}" /qn TARGETDIR="{1}" /L*v "{2}"' -f $msiPath,$msiExtract,(Join-Path $temporaryRoot 'msi-extract.log')
        $result = Invoke-BoundedProcess -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -RawArguments $arguments -Description 'MSI administrative extraction'
        $exitCode = $result.ExitCode
        Assert-Equal 0 $exitCode 'MSI administrative extraction failed.'
        $extractedLauncher = @(Get-ChildItem -LiteralPath $msiExtract -Recurse -File -Filter 'AVWorkstationToolkit.exe') | Select-Object -First 1
        Assert-True ($null -ne $extractedLauncher) 'MSI did not contain AVWorkstationToolkit.exe.'
        Assert-Equal 0 @(Get-ChildItem -LiteralPath $msiExtract -Recurse -File -Filter '*DevHost*').Count 'MSI contains a worker development host.'
        Assert-Equal (Get-FileHash -LiteralPath $standalonePath -Algorithm SHA256).Hash (Get-FileHash -LiteralPath $extractedLauncher.FullName -Algorithm SHA256).Hash 'MSI-contained launcher differs from the standalone release executable.'
        $extractedSignature = Get-AuthenticodeSignature -LiteralPath $extractedLauncher.FullName
        if ($RequireSignature) {
            Assert-Equal 'Valid' ([string]$extractedSignature.Status) 'MSI-contained launcher signature is not valid.'
            Assert-True ($null -ne $extractedSignature.TimeStamperCertificate) 'MSI-contained launcher is missing its RFC3161 timestamp.'
        }
        else { Assert-True ($extractedSignature.Status -in @('Valid','NotSigned')) "Unexpected MSI-contained launcher signature state: $($extractedSignature.Status)" }
        $diagnosticPath = Join-Path $temporaryRoot 'msi-diagnostic.json'
        $exitCode = Invoke-PackagedLauncher -Launcher $extractedLauncher.FullName -Arguments @('--verify',$diagnosticPath,'--data-root',$dataRoot)
        Assert-Equal 0 $exitCode 'MSI-extracted launcher verification failed.'
    }

    Invoke-Check 'Optional offline bundle is absent or contains only verified catalogued payloads' {
        $manifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
        $bundledIds = @($manifest.BundledExternalPackages)
        $offlineBundlePath = Join-Path $ReleaseRoot ("AV-Workstation-Toolkit-{0}-offline-bundle.zip" -f $releaseName)
        if ($bundledIds.Count -eq 0) {
            Assert-True (-not (Test-Path -LiteralPath $offlineBundlePath)) 'An undeclared offline bundle is present.'
            return
        }

        Assert-True (Test-Path -LiteralPath $offlineBundlePath -PathType Leaf) 'Declared offline bundle is missing.'
        Expand-Archive -LiteralPath $offlineBundlePath -DestinationPath $offlineExtract
        $offlineLauncher = Join-Path $offlineExtract 'AVWorkstationToolkit.exe'
        $offlineIndexPath = Join-Path $offlineExtract 'AVWorkstationToolkit-offline-bundle.json'
        Assert-True (Test-Path -LiteralPath $offlineLauncher -PathType Leaf) 'Offline bundle launcher is missing.'
        Assert-Equal (Get-FileHash -LiteralPath $standalonePath -Algorithm SHA256).Hash (Get-FileHash -LiteralPath $offlineLauncher -Algorithm SHA256).Hash 'Offline launcher differs from the standalone artifact.'
        Assert-True (Test-Path -LiteralPath $offlineIndexPath -PathType Leaf) 'Offline bundle index is missing.'
        $offlineIndex = Get-Content -LiteralPath $offlineIndexPath -Raw | ConvertFrom-Json
        Assert-Equal ($bundledIds -join '|') (@($offlineIndex.Packages.Id) -join '|') 'Offline bundle package IDs differ from release metadata.'
        foreach ($package in @($offlineIndex.Packages)) {
            $relative = ([string]$package.RelativePath).Replace('/',[IO.Path]::DirectorySeparatorChar)
            $payloadPath = [IO.Path]::GetFullPath((Join-Path $offlineExtract $relative))
            $offlinePrefix = [IO.Path]::GetFullPath($offlineExtract).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            Assert-True ($payloadPath.StartsWith($offlinePrefix,[StringComparison]::OrdinalIgnoreCase)) "Offline payload escaped extraction: $($package.Id)"
            Assert-True (Test-Path -LiteralPath $payloadPath -PathType Leaf) "Offline payload is missing: $($package.Id)"
            Assert-Equal ([string]$package.Sha256) (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash "Offline payload hash differs: $($package.Id)"
        }
        if (-not $SkipDesktopSmoke) {
            $exitCode = Invoke-PackagedLauncher -Launcher $offlineLauncher -Arguments @('--smoke-test','--data-root',$offlineDataRoot)
            Assert-Equal 0 $exitCode 'Offline bundle WPF smoke process failed.'
        }
    }

    Invoke-Check 'Packaged launches wrote their runtime and state inside the isolated data root' {
        $extractedWorker = Join-Path $dataRoot (Join-Path (Join-Path 'runtime' $version) 'worker\AVWorkstationToolkit.Worker.exe')
        Assert-True (Test-Path -LiteralPath $extractedWorker -PathType Leaf) 'The packaged runtime was not extracted into the isolated data root.'
        if (-not $SkipDesktopSmoke) {
            foreach ($folder in @('logs\requests','reports')) {
                Assert-True (Test-Path -LiteralPath (Join-Path $dataRoot $folder) -PathType Container) "The packaged app did not create $folder in the isolated data root."
            }
            Assert-Equal 'saved-reloaded-finished' $script:migrationEvidence 'The production smoke did not prove that its migration checklist persisted in the isolated data root.'
        }
    }
}
finally {
    Invoke-Check "The user's AV Workstation Toolkit profile is unchanged" {
        $differences = Compare-TreeSnapshot -Before $profileBefore -After (Get-TreeSnapshot -Root $userProfileRoot)
        if ($differences.Count -gt 0) {
            $note = if ($userToolkitProcesses.Count -gt 0) { " AV Workstation Toolkit was already open (process $($userToolkitProcesses -join ', ')) and may have written these itself." } else { '' }
            throw ('Package QA found {0} change(s) in {1}: {2}.{3}' -f $differences.Count,$userProfileRoot,(($differences | Select-Object -First 8) -join '; '),$note)
        }
    }
    Invoke-Check 'Package QA installed, upgraded, or registered nothing' {
        $differences = Compare-TreeSnapshot -Before $installedBefore -After (Get-InstalledProductState)
        Assert-Equal 0 $differences.Count "Installed AV Workstation Toolkit state changed: $(($differences | Select-Object -First 8) -join '; ')"
    }
    Remove-IsolatedRoot -Path $temporaryRoot
    Remove-IsolatedRoot -Path $dataRoot
}

Write-Host ''
Write-Host ("Package QA summary: {0} passed; {1} skipped; {2} failed" -f $script:Passed,$script:Skipped,$script:Failed) -ForegroundColor Cyan
if ($script:Failed -gt 0) {
    Write-Host ($script:Failures -join "`n") -ForegroundColor Red
    exit 1
}
exit 0
