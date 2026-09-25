<#
.SYNOPSIS
    Executes one validated frontend install or update request.

.DESCRIPTION
    This worker accepts only a request file beneath the resolved AV Workstation Toolkit data
    folder. Packaged runs use the current user's LocalAppData directory.
    Every package ID is reloaded from canonical managed-applications.json and revalidated against current winget
    state before any change. It writes structured progress and result files for
    the desktop frontend, then verifies every completed package.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RequestPath,
    [string]$DataRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$launchIsElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($launchIsElevated) {
    throw 'AV Workstation Toolkit change workers must run as a standard user. Allow individual installers to request elevation through Windows when required.'
}

Import-Module (Join-Path $PSScriptRoot 'AVWorkstationToolkit.Core.psd1') -Force
$resolvedDataRoot = Get-AVWorkstationToolkitDataRoot -Path $DataRoot
$logsRoot = [IO.Path]::GetFullPath((Join-Path $resolvedDataRoot 'logs'))
$requestsRoot = [IO.Path]::GetFullPath((Join-Path $logsRoot 'requests'))
$requestFullPath = [IO.Path]::GetFullPath($RequestPath)
$requestDirectory = [IO.Path]::GetDirectoryName($requestFullPath)
$requestName = [IO.Path]::GetFileNameWithoutExtension($requestFullPath)
$progressPath = Join-Path $requestDirectory ($requestName + '.progress.jsonl')
$resultPath = Join-Path $requestDirectory ($requestName + '.result.json')
$runLogPath = Join-Path $requestDirectory ($requestName + '.winget.log')
$cancelPath = Join-Path $requestDirectory ($requestName + '.cancel')

if (-not $requestDirectory.Equals($requestsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Request files must be direct children of the resolved AV Workstation Toolkit logs\requests directory.'
}
if ([IO.Path]::GetExtension($requestFullPath) -ine '.json' -or $requestName -notmatch '^request-\d{8}-\d{6}-[a-f0-9]{8}$') {
    throw 'Request filename does not match the required AV Workstation Toolkit request format.'
}
if (-not (Test-Path -LiteralPath $requestFullPath -PathType Leaf)) {
    throw "Request file was not found: $requestFullPath"
}
foreach ($path in @($logsRoot,$requestsRoot,$requestFullPath)) {
    $item = Get-Item -LiteralPath $path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Request path contains an unsupported reparse point: $path"
    }
}
$requestFile = Get-Item -LiteralPath $requestFullPath
if ($requestFile.Length -gt 65536) {
    throw 'Request file exceeds the 64 KiB safety limit.'
}

function Write-ActionEvent {
    param(
        [ValidateSet('Info','Success','Warning','Error')][string]$Level,
        [string]$Stage,
        [string]$Message,
        [string]$PackageId = ''
    )

    $safeMessage = Protect-AVWorkstationToolkitSensitiveText -Text $Message
    if ($safeMessage.Length -gt 4000) { $safeMessage = $safeMessage.Substring(0,4000) + '...[truncated]' }
    $actionEvent = [ordered]@{
        Timestamp = (Get-Date).ToString('o')
        Level = $Level
        Stage = $Stage
        PackageId = $PackageId
        Message = $safeMessage
    }
    $line = $actionEvent | ConvertTo-Json -Compress
    Add-Content -LiteralPath $progressPath -Value $line -Encoding UTF8
    $packageLabel = if ([string]::IsNullOrWhiteSpace($PackageId)) { '' } else { " [$PackageId]" }
    Write-Output ('[{0}] {1,-7} {2}{3}: {4}' -f (Get-Date -Format 'HH:mm:ss'),$Level,$Stage,$packageLabel,$safeMessage)
}

function Write-ActionResult {
    param(
        [string]$Status,
        [string]$Message,
        [object[]]$Packages,
        [int]$ExitCode
    )

    $report = [ordered]@{
        SchemaVersion = 2
        GeneratedAt = (Get-Date).ToString('o')
        Computer = $env:COMPUTERNAME
        Status = $Status
        Message = $Message
        ExitCode = $ExitCode
        ManagedCatalogRevision = 0
        RequestPath = $requestFullPath
        ProgressPath = $progressPath
        WingetLogPath = $runLogPath
        Packages = @($Packages)
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding UTF8
}

$packageResults = [System.Collections.Generic.List[object]]::new()
$finalExitCode = 1
$blockReason = ''

try {
    $requestText = Get-Content -LiteralPath $requestFullPath -Raw
    $request = $requestText | ConvertFrom-Json
    $propertyNames = @($request.PSObject.Properties.Name)
    $requiredProperties = @('SchemaVersion','RequestId','Action','PackageIds','RiskAcknowledged','DryRun','ManagedCatalogRevision')
    $unknownProperties = @($propertyNames | Where-Object { $_ -notin $requiredProperties })
    $missingProperties = @($requiredProperties | Where-Object { $_ -notin $propertyNames })
    if ($unknownProperties.Count -gt 0) { throw ('Request contains unsupported properties: {0}' -f ($unknownProperties -join ', ')) }
    if ($missingProperties.Count -gt 0) { throw ('Request is missing required properties: {0}' -f ($missingProperties -join ', ')) }
    if ($request.SchemaVersion -isnot [int] -or [int]$request.SchemaVersion -ne 2) { throw 'Request SchemaVersion must be the integer 2.' }
    if ($request.RequestId -isnot [string] -or [string]$request.RequestId -cne $requestName) { throw 'RequestId must exactly match the request filename.' }
    if ($request.Action -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$request.Action)) { throw 'Request Action must be a string.' }
    if ($request.RiskAcknowledged -isnot [bool]) { throw 'Request RiskAcknowledged must be a Boolean.' }
    if ($request.DryRun -isnot [bool]) { throw 'Request DryRun must be a Boolean.' }
    if ($request.ManagedCatalogRevision -isnot [int] -or [int]$request.ManagedCatalogRevision -ne 0) { throw 'Source-checkout requests require ManagedCatalogRevision 0.' }

    $rawIds = @($request.PackageIds)
    if ($rawIds.Count -gt 100) { throw 'Request contains too many package IDs.' }
    foreach ($rawId in $rawIds) {
        if ($rawId -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$rawId)) {
            throw 'Every request PackageIds entry must be a non-empty string.'
        }
    }

    $action = if ([string]$request.Action -ieq 'Install') { 'Install' } elseif ([string]$request.Action -ieq 'Update') { 'Update' } else { [string]$request.Action }
    $ids = @($rawIds | ForEach-Object { [string]$_ })
    $riskAcknowledged = [bool]$request.RiskAcknowledged
    $dryRun = [bool]$request.DryRun

    if ($action -notin @('Install','Update')) {
        throw "Invalid action '$action'."
    }

    Write-ActionEvent -Level Info -Stage Preflight -Message ("Preparing {0} request for {1} package(s)." -f $action.ToLowerInvariant(),$ids.Count)
    $plan = Get-AVWorkstationToolkitPlan
    $selected = @(Assert-AVWorkstationToolkitRequest -Action $action -PackageId $ids -Plan $plan -RiskAcknowledged:$riskAcknowledged)

    $packageIndex = 0
    foreach ($package in $selected) {
        if (Test-Path -LiteralPath $cancelPath) {
            Write-ActionEvent -Level Warning -Stage Cancelled -Message 'Stopped before starting the next package.'
            break
        }

        # The first package uses the just-created live plan. Before every later
        # package, rebuild state so a newly pending reboot or external package
        # change cannot be ignored by a long request.
        if (-not $dryRun -and $packageIndex -gt 0) {
            try {
                $freshPlan = Get-AVWorkstationToolkitPlan
                $package = @(Assert-AVWorkstationToolkitRequest -Action $action -PackageId $package.Id -Plan $freshPlan -RiskAcknowledged:$riskAcknowledged)[0]
            }
            catch {
                $blockReason = 'Stopped before the next package: ' + $_.Exception.Message
                $packageResults.Add([pscustomobject]@{
                    Id = $package.Id
                    Name = $package.Name
                    Action = $action
                    Status = 'Blocked'
                    ExitCode = 3
                    Verified = $false
                    StartedAt = $null
                    FinishedAt = (Get-Date).ToString('o')
                    Arguments = @()
                }) | Out-Null
                Write-ActionEvent -Level Warning -Stage Blocked -PackageId $package.Id -Message $blockReason
                break
            }
        }

        $arguments = @(Get-AVWorkstationToolkitWingetArguments -Action $action -Package $package)
        $startedAt = Get-Date
        Write-ActionEvent -Level Info -Stage Starting -PackageId $package.Id -Message ("{0}: {1}" -f $action,$package.Name)

        if ($dryRun) {
            $packageResults.Add([pscustomobject]@{
                Id = $package.Id
                Name = $package.Name
                Action = $action
                Status = 'Planned'
                ExitCode = 0
                Verified = $false
                StartedAt = $startedAt.ToString('o')
                FinishedAt = (Get-Date).ToString('o')
                Arguments = @($arguments)
            }) | Out-Null
            Write-ActionEvent -Level Success -Stage Planned -PackageId $package.Id -Message 'Dry run validated; no change executed.'
            $packageIndex++
            continue
        }

        "=== $action $($package.Id) $($startedAt.ToString('o')) ===" | Add-Content -LiteralPath $runLogPath -Encoding UTF8
        $wingetPath = Get-AVWorkstationToolkitWingetCommand
        $lastWingetLine = ''
        $duplicateLineCount = 0
        & $wingetPath @arguments 2>&1 | ForEach-Object {
            $line = Protect-AVWorkstationToolkitSensitiveText -Text ([string]$_)
            $transientProgress = $line -match '^\s*(?:[/|\\-]|\d{1,3}%|[\p{So}#=.\-\s]+\s*\d{1,3}%)\s*$'
            if ([string]::IsNullOrWhiteSpace($line) -or $transientProgress) {
                # Drop spinner/progress redraws before they reach JSONL or logs.
            }
            elseif ($line -eq $lastWingetLine) {
                $duplicateLineCount++
            }
            else {
                if ($duplicateLineCount -gt 0) {
                    $summaryLine = "Previous winget line repeated $duplicateLineCount additional time(s)."
                    Add-Content -LiteralPath $runLogPath -Value $summaryLine -Encoding UTF8
                    Write-ActionEvent -Level Info -Stage Winget -PackageId $package.Id -Message $summaryLine
                }
                $duplicateLineCount = 0
                Add-Content -LiteralPath $runLogPath -Value $line -Encoding UTF8
                Write-ActionEvent -Level Info -Stage Winget -PackageId $package.Id -Message $line
                $lastWingetLine = $line
            }
        }
        if ($duplicateLineCount -gt 0) {
            $summaryLine = "Previous winget line repeated $duplicateLineCount additional time(s)."
            Add-Content -LiteralPath $runLogPath -Value $summaryLine -Encoding UTF8
            Write-ActionEvent -Level Info -Stage Winget -PackageId $package.Id -Message $summaryLine
        }
        $nativeExitCode = $LASTEXITCODE

        $verified = $false
        if ($nativeExitCode -eq 0) {
            $verified = if ($action -eq 'Install') {
                Test-AVWorkstationToolkitInstalled -Id $package.Id
            }
            else {
                Test-AVWorkstationToolkitCurrent -Id $package.Id
            }
        }

        $status = if ($nativeExitCode -ne 0) { 'Failed' } elseif ($verified) { 'Succeeded' } else { 'Unverified' }
        $packageResults.Add([pscustomobject]@{
            Id = $package.Id
            Name = $package.Name
            Action = $action
            Status = $status
            ExitCode = $nativeExitCode
            Verified = $verified
            StartedAt = $startedAt.ToString('o')
            FinishedAt = (Get-Date).ToString('o')
            Arguments = @($arguments)
        }) | Out-Null

        if ($status -eq 'Succeeded') {
            Write-ActionEvent -Level Success -Stage Verified -PackageId $package.Id -Message ("Verified {0}." -f $action.ToLowerInvariant())
        }
        elseif ($status -eq 'Unverified') {
            Write-ActionEvent -Level Error -Stage Verification -PackageId $package.Id -Message 'winget returned success, but post-action verification failed.'
        }
        else {
            Write-ActionEvent -Level Error -Stage Failed -PackageId $package.Id -Message ("winget exited with code {0}." -f $nativeExitCode)
        }
        $packageIndex++
    }

    $failed = @($packageResults | Where-Object Status -in @('Failed','Unverified'))
    $blocked = @($packageResults | Where-Object Status -eq 'Blocked')
    $cancelled = Test-Path -LiteralPath $cancelPath
    if ($failed.Count -gt 0) {
        $finalExitCode = 1
        Write-ActionResult -Status Failed -Message ("{0} package(s) failed or could not be verified." -f $failed.Count) -Packages @($packageResults) -ExitCode $finalExitCode
    }
    elseif ($blocked.Count -gt 0) {
        $finalExitCode = 3
        Write-ActionResult -Status Blocked -Message $blockReason -Packages @($packageResults) -ExitCode $finalExitCode
    }
    elseif ($cancelled) {
        $finalExitCode = 2
        Write-ActionResult -Status Cancelled -Message 'Stopped after the current package.' -Packages @($packageResults) -ExitCode $finalExitCode
    }
    else {
        $finalExitCode = 0
        $message = if ($dryRun) { 'Dry run completed.' } else { 'All requested packages completed and were verified.' }
        Write-ActionResult -Status Succeeded -Message $message -Packages @($packageResults) -ExitCode $finalExitCode
        Write-ActionEvent -Level Success -Stage Complete -Message $message
    }
}
catch {
    $message = $_.Exception.Message
    try { Write-ActionEvent -Level Error -Stage Rejected -Message $message } catch { }
    try { Write-ActionResult -Status Rejected -Message $message -Packages @($packageResults) -ExitCode 1 } catch { }
    Write-Error $message
    $finalExitCode = 1
}

exit $finalExitCode
