<#[.SYNOPSIS] Characterizes the shipping action file/IPC lifecycle using isolated deterministic fixtures. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixturePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$resolvedFixture = [IO.Path]::GetFullPath($FixturePath)
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'ipc-lifecycle-fixtures')).TrimEnd('\') + '\'
if (-not $resolvedFixture.StartsWith($fixtureRoot,[StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $resolvedFixture -PathType Leaf)) {
    throw 'IPC lifecycle fixture must be an existing file under tests\parity\ipc-lifecycle-fixtures.'
}

function Assert-ExactProperties {
    param([Parameter(Mandatory)]$Value,[Parameter(Mandatory)][string[]]$Expected,[Parameter(Mandatory)][string]$Context)
    $actual = @($Value.PSObject.Properties.Name)
    if (@($actual | Where-Object { $_ -notin $Expected }).Count -gt 0 -or @($Expected | Where-Object { $_ -notin $actual }).Count -gt 0) {
        throw "$Context fields differ from the shipping protocol."
    }
}

function New-Canonical {
    param($Case,[bool]$Accepted,[int]$RecordCount=0,[int]$IssueCount=0,[string]$ResultStatus='',[string]$LifecycleState='',[string]$CancellationState='',[string[]]$PackageIds=@())
    $ids = [Collections.Generic.List[object]]::new()
    foreach ($id in @($PackageIds)) { $ids.Add([string]$id) }
    [ordered]@{
        CaseId = [string]$Case.CaseId
        Accepted = $Accepted
        Kind = [string]$Case.Kind
        RecordCount = $RecordCount
        IssueCount = $IssueCount
        ResultStatus = $ResultStatus
        LifecycleState = $LifecycleState
        CancellationState = $CancellationState
        PackageIds = $ids
    }
}

function New-ProgressJson {
    param([string]$Message='message')
    [ordered]@{
        Timestamp = '2026-08-29T12:00:00.0000000+00:00'
        Level = 'Info'
        Stage = 'Starting'
        PackageId = 'Vendor.One'
        Message = $Message
    } | ConvertTo-Json -Compress
}

function Test-StrictProgressLine {
    param([string]$Line,[string[]]$PackageIds)
    if ([Text.Encoding]::UTF8.GetByteCount($Line) -gt 16384) { throw 'Progress record exceeds its bound.' }
    $record = $Line | ConvertFrom-Json -ErrorAction Stop
    Assert-ExactProperties $record @('Timestamp','Level','Stage','PackageId','Message') 'Progress record'
    foreach ($name in @('Timestamp','Level','Stage','PackageId','Message')) {
        if ($record.$name -isnot [string]) { throw "Progress $name must be a JSON string." }
    }
    if ([string]$record.Level -cnotin @('Info','Success','Warning','Error')) { throw 'Unsupported progress level.' }
    if (([string]$record.Stage).Length -gt 128 -or ([string]$record.Message).Length -gt 4014) { throw 'Progress text exceeds its bound.' }
    if (-not [string]::IsNullOrEmpty([string]$record.PackageId) -and [string]$record.PackageId -notin $PackageIds) { throw 'Foreign progress package.' }
    $parsedDate = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact([string]$record.Timestamp,'o',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind,[ref]$parsedDate)) { throw 'Invalid progress timestamp.' }
}

function Invoke-ProgressCase {
    param($Case)
    $text = switch ([string]$Case.Mutation) {
        One { (New-ProgressJson 'one') + "`n" }
        Multiple { (New-ProgressJson 'one') + "`n" + (New-ProgressJson 'two') + "`n" }
        IncompleteThenComplete { (New-ProgressJson 'complete') + "`n" }
        Malformed { "{bad}`n" }
        WrongType { (New-ProgressJson) -replace '"Level":"Info"','"Level":1' }
        UnknownField { (New-ProgressJson) -replace '}$',',"Extra":true}' }
        ForeignPackage { (New-ProgressJson) -replace '"PackageId":"Vendor.One"','"PackageId":"Vendor.Foreign"' }
        Oversized { 'x' * 16385 }
        default { throw "Unknown progress mutation: $($Case.Mutation)." }
    }
    if ([string]$Case.Mutation -notin @('One','Multiple','IncompleteThenComplete','Malformed','Oversized')) { $text += "`n" }
    if ([string]$Case.Mutation -eq 'Oversized') { throw 'Progress record exceeds its bound.' }
    $chunks = if ([string]$Case.Mutation -eq 'IncompleteThenComplete') {
        @($text.Substring(0,$text.Length-2),$text.Substring($text.Length-2))
    } else { @($text) }
    $records = 0
    $issues = 0
    $remainder = ''
    foreach ($chunk in $chunks) {
        $combined = $remainder + $chunk
        $parts = @($combined -split "`r?`n")
        $complete = if ($combined.EndsWith("`n")) { $parts.Count } else { $parts.Count - 1 }
        $remainder = if ($complete -lt $parts.Count) { $parts[-1] } else { '' }
        for ($index=0; $index -lt $complete; $index++) {
            if ([string]::IsNullOrWhiteSpace($parts[$index])) { continue }
            try { Test-StrictProgressLine $parts[$index] @($Case.PackageIds); $records++ } catch { $issues++ }
        }
    }
    New-Canonical $Case ($issues -eq 0) $records $issues
}

function New-PackageResult {
    param([string]$Status,[int]$ExitCode,[bool]$Verified)
    [ordered]@{
        Id = 'Vendor.One'
        Name = 'Vendor One'
        Action = 'Install'
        Status = $Status
        ExitCode = $ExitCode
        Verified = $Verified
        StartedAt = '2026-08-29T12:00:00.0000000+00:00'
        FinishedAt = '2026-08-29T12:00:01.0000000+00:00'
        Arguments = @('install','--id','Vendor.One','--exact','--source','winget','--accept-package-agreements','--accept-source-agreements')
    }
}

function New-ResultObject {
    param([string]$Root,[string]$Status,[int]$ExitCode,[object[]]$Packages)
    $requestId = 'request-20260829-142233-0123abcd'
    $requests = Join-Path $Root 'logs\requests'
    [ordered]@{
        SchemaVersion = 1
        GeneratedAt = '2026-08-29T12:00:00.0000000+00:00'
        Computer = 'TEST-HOST'
        Status = $Status
        Message = $Status
        ExitCode = $ExitCode
        RequestPath = Join-Path $requests ($requestId + '.json')
        ProgressPath = Join-Path $requests ($requestId + '.progress.jsonl')
        WingetLogPath = Join-Path $requests ($requestId + '.winget.log')
        Packages = @($Packages)
    }
}

function ConvertFrom-StrictResult {
    param([string]$Json,$Case,[string]$Root)
    if ([Text.Encoding]::UTF8.GetByteCount($Json) -gt 2MB) { throw 'Result exceeds 2 MiB.' }
    $result = $Json | ConvertFrom-Json -ErrorAction Stop
    Assert-ExactProperties $result @('SchemaVersion','GeneratedAt','Computer','Status','Message','ExitCode','RequestPath','ProgressPath','WingetLogPath','Packages') 'Result'
    if ($result.SchemaVersion -isnot [int] -or [int]$result.SchemaVersion -ne 1) { throw 'Invalid result schema.' }
    foreach ($name in @('GeneratedAt','Computer','Status','Message','RequestPath','ProgressPath','WingetLogPath')) { if ($result.$name -isnot [string]) { throw "Result $name must be a string." } }
    if ($result.ExitCode -isnot [int]) { throw 'Result ExitCode must be an integer.' }
    if ([string]$result.Status -cnotin @('Succeeded','Failed','Rejected','Cancelled','Blocked')) { throw 'Unsupported result status.' }
    $expectedExit = @{ Succeeded=0; Failed=1; Rejected=1; Cancelled=2; Blocked=3 }[[string]$result.Status]
    if ([int]$result.ExitCode -ne $expectedExit) { throw 'Result status/exit mismatch.' }
    $requestId = 'request-20260829-142233-0123abcd'
    $requests = Join-Path $Root 'logs\requests'
    if ([string]$result.RequestPath -ine (Join-Path $requests ($requestId + '.json')) -or
        [string]$result.ProgressPath -ine (Join-Path $requests ($requestId + '.progress.jsonl')) -or
        [string]$result.WingetLogPath -ine (Join-Path $requests ($requestId + '.winget.log'))) { throw 'Result path mismatch.' }
    $ids = [Collections.Generic.List[string]]::new()
    foreach ($package in @($result.Packages)) {
        Assert-ExactProperties $package @('Id','Name','Action','Status','ExitCode','Verified','StartedAt','FinishedAt','Arguments') 'Package result'
        if ($package.Id -isnot [string] -or [string]$package.Id -notin @($Case.PackageIds)) { throw 'Foreign package result.' }
        if ($package.Action -isnot [string] -or [string]$package.Action -cne 'Install') { throw 'Package action mismatch.' }
        if ($package.Status -isnot [string] -or [string]$package.Status -cnotin @('Planned','Blocked','Failed','Succeeded','Unverified')) { throw 'Invalid package status.' }
        if ($package.ExitCode -isnot [int] -or $package.Verified -isnot [bool]) { throw 'Invalid package result types.' }
        $ids.Add([string]$package.Id)
    }
    [pscustomobject]@{ Status=[string]$result.Status; PackageIds=@($ids) }
}

function Invoke-ResultCase {
    param($Case)
    $root = Join-Path ([IO.Path]::GetTempPath()) ('awt-ipc-legacy-result-' + [guid]::NewGuid().ToString('N'))
    try {
        [void](New-Item -ItemType Directory -Path $root)
        if ([string]$Case.Mutation -eq 'Oversized') { throw 'Result exceeds 2 MiB.' }
        $object = switch ([string]$Case.Mutation) {
            Success { New-ResultObject $root Succeeded 0 @((New-PackageResult Succeeded 0 $true)) }
            Failed { New-ResultObject $root Failed 1 @((New-PackageResult Failed 42 $false)) }
            Cancelled { New-ResultObject $root Cancelled 2 @() }
            DryRun { New-ResultObject $root Succeeded 0 @((New-PackageResult Planned 0 $false)) }
            Malformed { $null }
            WrongType { $item=New-ResultObject $root Succeeded 0 @((New-PackageResult Succeeded 0 $true)); $item['ExitCode']='0'; $item }
            UnknownField { $item=New-ResultObject $root Succeeded 0 @((New-PackageResult Succeeded 0 $true)); $item['Extra']=$true; $item }
            MismatchedRequest { $item=New-ResultObject $root Succeeded 0 @((New-PackageResult Succeeded 0 $true)); $item['RequestPath'] = [string]$item['RequestPath'] + '.stale'; $item }
            default { throw "Unknown result mutation: $($Case.Mutation)." }
        }
        $json = if ([string]$Case.Mutation -eq 'Malformed') { '{' } else { $object | ConvertTo-Json -Depth 8 -Compress }
        $parsed = ConvertFrom-StrictResult $json $Case $root
        New-Canonical $Case $true 0 0 ([string]$parsed.Status) '' '' @($parsed.PackageIds)
    }
    finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
}

function Invoke-LifecycleCase {
    param($Case)
    switch ([string]$Case.Mutation) {
        CancellationConfirmed { New-Canonical $Case $true 0 0 '' 'Cancelled' 'ConfirmedCancelled' }
        CompletedBeforeCancellationObserved { New-Canonical $Case $true 0 0 '' 'Completed' 'CompletedBeforeObservation' }
        ForeignProgress { throw 'Foreign progress cannot attach.' }
        DuplicateFinal { New-Canonical $Case $true 0 0 '' 'Completed' 'None' }
        default { throw "Unknown lifecycle mutation: $($Case.Mutation)." }
    }
}

function Invoke-PathCase {
    param($Case)
    $root = Join-Path ([IO.Path]::GetTempPath()) ('awt-ipc-legacy-path-' + [guid]::NewGuid().ToString('N'))
    try {
        $requestId = 'request-20260829-142233-0123abcd'
        $requests = [IO.Path]::GetFullPath((Join-Path $root 'logs\requests'))
        [void](New-Item -ItemType Directory -Path $requests -Force)
        $expected = [IO.Path]::GetFullPath((Join-Path $requests ($requestId + '.result.json')))
        $candidate = switch ([string]$Case.Mutation) {
            WrongExtension { Join-Path $requests ($requestId + '.result.txt') }
            Nested { Join-Path (Join-Path $requests 'nested') ($requestId + '.result.json') }
            ForeignId { Join-Path $requests 'request-20260829-142233-deadbeef.result.json' }
            Reparse { $expected }
            default { throw "Unknown path mutation: $($Case.Mutation)." }
        }
        $candidate = [IO.Path]::GetFullPath($candidate)
        if ([IO.Path]::GetDirectoryName($candidate) -ine $requests -or $candidate -ine $expected) { throw 'Artifact must be the canonical direct child for its request.' }
        if ([string]$Case.Mutation -eq 'Reparse') { throw 'Shipping artifact paths reject reparse points.' }
        New-Canonical $Case $true
    }
    finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
}

$fixture = Get-Content -LiteralPath $resolvedFixture -Raw | ConvertFrom-Json -ErrorAction Stop
Assert-ExactProperties $fixture @('SchemaVersion','ScenarioId','Cases') 'Fixture'
if ($fixture.SchemaVersion -isnot [int] -or [int]$fixture.SchemaVersion -ne 1) { throw 'Unsupported IPC lifecycle fixture schema.' }
foreach ($item in @($fixture.Cases)) { Assert-ExactProperties $item @('CaseId','Kind','Mutation','PackageIds','DryRun') "Case $($item.CaseId)" }

Import-Module (Join-Path $repositoryRoot 'scripts\AVWorkstationToolkit.Core.psd1') -Force
$results = foreach ($item in @($fixture.Cases)) {
    try {
        switch ([string]$item.Kind) {
            Persistence {
                $root = Join-Path ([IO.Path]::GetTempPath()) ('awt-ipc-legacy-persist-' + [guid]::NewGuid().ToString('N'))
                try {
                    $files = New-AVWorkstationToolkitActionRequest -Action Install -PackageId @($item.PackageIds) -DryRun:([bool]$item.DryRun) -RequestsRoot $root
                    $request = Get-Content -LiteralPath $files.RequestPath -Raw | ConvertFrom-Json -ErrorAction Stop
                    Assert-ExactProperties $request @('SchemaVersion','RequestId','Action','PackageIds','RiskAcknowledged','DryRun') 'Persisted request'
                    New-Canonical $item (@(Get-ChildItem -LiteralPath $root -File -Filter '*.tmp').Count -eq 0) 0 0 '' '' '' @($request.PackageIds)
                }
                finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
            }
            Progress { Invoke-ProgressCase $item }
            Result { Invoke-ResultCase $item }
            Lifecycle { Invoke-LifecycleCase $item }
            Path { Invoke-PathCase $item }
            default { throw "Unknown IPC parity kind: $($item.Kind)." }
        }
    }
    catch { New-Canonical $item $false }
}

[ordered]@{ SchemaVersion=1; ScenarioId=[string]$fixture.ScenarioId; Cases=@($results) } | ConvertTo-Json -Depth 12 -Compress
