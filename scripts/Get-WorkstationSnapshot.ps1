<#
.SYNOPSIS
    Creates a read-only AV/IT workstation inventory snapshot.

.DESCRIPTION
    Collects hardware, operating system, package, runtime, service, driver,
    network, listener, startup, task, audio, and selected development metadata.

    The script does not install, upgrade, uninstall, enable, disable, start, or
    stop software or services. Its only intended writes are files beneath the
    selected DestinationRoot and an optional ZIP/checksum beside the snapshot.

    Credentials and application payloads are deliberately excluded: no SSH
    private keys, KeePass databases, browser profiles, mRemoteNG/FileZilla
    credentials, project files, command history, or environment-variable dump.
#>

[CmdletBinding()]
param(
    [string]$DestinationRoot,
    [switch]$IncludeDirectorySizes,
    [switch]$IncludeIdentityMetadata,
    [switch]$SkipZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$launchIsElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($launchIsElevated) {
    throw 'AV Workstation Toolkit snapshot collection must run as a standard user. Elevated execution of user-writable repository scripts is intentionally prohibited.'
}

Import-Module (Join-Path $PSScriptRoot 'AVWorkstationToolkit.Core.psd1') -Force

if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
    $DestinationRoot = Join-Path (Get-AVWorkstationToolkitDataRoot) 'snapshots'
}

$snapshotStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$snapshotName = 'Snapshot-{0}-{1}' -f $env:COMPUTERNAME, $snapshotStamp
$resolvedDestination = [IO.Path]::GetFullPath($DestinationRoot)
$snapshotDirectory = Join-Path $resolvedDestination $snapshotName
$zipPath = "$snapshotDirectory.zip"

New-Item -ItemType Directory -Path $resolvedDestination -Force | Out-Null
if ((Test-Path -LiteralPath $snapshotDirectory) -or (Test-Path -LiteralPath $zipPath)) {
    throw "Snapshot target already exists; no file will be overwritten: $snapshotDirectory"
}
New-Item -ItemType Directory -Path $snapshotDirectory | Out-Null
$sections = @('system','applications','packages','services-drivers','network','security','development','diagnostics')
foreach ($section in $sections) {
    New-Item -ItemType Directory -Path (Join-Path $snapshotDirectory $section) -Force | Out-Null
}

$collectionResults = [System.Collections.Generic.List[object]]::new()
$startedAt = Get-Date

function Add-CollectionResult {
    param(
        [string]$Name,
        [string]$RelativePath,
        [string]$Status,
        [int]$Items,
        [string]$Message = ''
    )
    $collectionResults.Add([pscustomobject]@{
        Name = $Name
        RelativePath = $RelativePath
        Status = $Status
        Items = $Items
        Message = $Message
    }) | Out-Null
}

function Invoke-Collection {
    param(
        [string]$Name,
        [string]$RelativePath,
        [ValidateSet('Csv','Json','Text')][string]$Format,
        [scriptblock]$Collector
    )

    $target = Join-Path $snapshotDirectory $RelativePath
    try {
        $data = @(& $Collector)
        switch ($Format) {
            'Csv'  { $data | Export-Csv -LiteralPath $target -NoTypeInformation -Encoding utf8 }
            'Json' { $data | ConvertTo-Json -Depth 10 | Out-File -LiteralPath $target -Encoding utf8 }
            'Text' { $data | Out-File -LiteralPath $target -Encoding utf8 }
        }
        Add-CollectionResult -Name $Name -RelativePath $RelativePath -Status 'Success' -Items $data.Count
        Write-Host ('[OK]   {0} ({1})' -f $Name, $data.Count) -ForegroundColor Green
    }
    catch {
        $errorPath = Join-Path $snapshotDirectory ('diagnostics\{0}.error.txt' -f ($Name -replace '[^A-Za-z0-9._-]','_'))
        $safeError = Protect-AVWorkstationToolkitSensitiveText -Text ($_ | Out-String)
        $safeMessage = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
        $safeError | Out-File -LiteralPath $errorPath -Encoding utf8
        Add-CollectionResult -Name $Name -RelativePath $RelativePath -Status 'Failed' -Items 0 -Message $safeMessage
        Write-Host ('[WARN] {0}: {1}' -f $Name, $safeMessage) -ForegroundColor Yellow
    }
}

function Add-SkippedCollection {
    param([string]$Name, [string]$RelativePath, [string]$Reason)
    $safeName = $Name -replace '[^A-Za-z0-9._-]','_'
    $safeReason = Protect-AVWorkstationToolkitSensitiveText -Text $Reason
    $notePath = Join-Path $snapshotDirectory ("diagnostics\$safeName.skipped.txt")
    $safeReason | Out-File -LiteralPath $notePath -Encoding utf8
    Add-CollectionResult -Name $Name -RelativePath $RelativePath -Status 'Skipped' -Items 0 -Message $safeReason
    Write-Host ('[SKIP] {0}: {1}' -f $Name, $safeReason) -ForegroundColor DarkYellow
}

function Invoke-NativeTextCollection {
    param(
        [string]$Name,
        [string]$RelativePath,
        [string]$Command,
        [string[]]$Arguments = @()
    )
    if ($Command -ieq 'winget') {
        try { $commandPath = Get-AVWorkstationToolkitWingetCommand }
        catch {
            $safeReason = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
            Add-SkippedCollection -Name $Name -RelativePath $RelativePath -Reason $safeReason
            return
        }
    }
    else {
        $resolvedCommand = Get-Command $Command -CommandType Application -All -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $resolvedCommand) {
            Add-SkippedCollection -Name $Name -RelativePath $RelativePath -Reason "$Command is not available"
            return
        }
        $commandPath = $resolvedCommand.Source
    }
    Invoke-Collection -Name $Name -RelativePath $RelativePath -Format Text -Collector {
        & $commandPath @Arguments 2>&1 | ForEach-Object { Protect-AVWorkstationToolkitSensitiveText -Text ([string]$_) }
        if ($LASTEXITCODE -ne 0) {
            throw "$Command exited with code $LASTEXITCODE"
        }
    }
}

function Invoke-WingetExport {
    param([string]$Name, [string]$RelativePath, [switch]$IncludeVersions)

    $target = Join-Path $snapshotDirectory $RelativePath
    $logTarget = Join-Path $snapshotDirectory ('diagnostics\{0}.txt' -f $Name)
    try {
        $wingetPath = Get-AVWorkstationToolkitWingetCommand
        $arguments = @('export','--output',$target,'--disable-interactivity')
        if ($IncludeVersions) { $arguments += '--include-versions' }
        $nativeOutput = & $wingetPath @arguments 2>&1
        $exitCode = $LASTEXITCODE
        $nativeOutput | ForEach-Object { Protect-AVWorkstationToolkitSensitiveText -Text ([string]$_) } | Out-File -LiteralPath $logTarget -Encoding utf8
        if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $target)) {
            throw "winget export exited with code $exitCode"
        }
        $packageCount = 0
        try {
            $wingetJson = Get-Content -LiteralPath $target -Raw | ConvertFrom-Json
            $packageCount = @($wingetJson.Sources.Packages).Count
        }
        catch { }
        Add-CollectionResult -Name $Name -RelativePath $RelativePath -Status 'Success' -Items $packageCount
        Write-Host ('[OK]   {0} ({1})' -f $Name, $packageCount) -ForegroundColor Green
    }
    catch {
        $safeError = Protect-AVWorkstationToolkitSensitiveText -Text ($_ | Out-String)
        $safeMessage = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
        $safeError | Out-File -LiteralPath ($logTarget + '.error.txt') -Encoding utf8
        Add-CollectionResult -Name $Name -RelativePath $RelativePath -Status 'Failed' -Items 0 -Message $safeMessage
        Write-Host ('[WARN] {0}: {1}' -f $Name, $safeMessage) -ForegroundColor Yellow
    }
}

$readme = @"
AV/IT workstation inventory snapshot
Created: $(Get-Date -Format s)
Computer: $env:COMPUTERNAME
User: $env:USERNAME
Collection mode: Standard user

This is an inventory/evidence package, not a restore package. It intentionally
excludes passwords, private keys, KeePass databases, browser profiles, command
history, application configuration payloads, and project files.

The collection is read-only with respect to workstation configuration. The only
intended writes are this report folder and its optional ZIP/checksum.
"@
$readme | Out-File -LiteralPath (Join-Path $snapshotDirectory 'README.txt') -Encoding utf8

Invoke-Collection -Name 'Computer system' -RelativePath 'system\computer-system.json' -Format Json -Collector {
    Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer,Model,Name,Domain,PartOfDomain,TotalPhysicalMemory,SystemType
}
Invoke-Collection -Name 'Operating system' -RelativePath 'system\operating-system.json' -Format Json -Collector {
    Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture,InstallDate,LastBootUpTime
}
Invoke-Collection -Name 'BIOS' -RelativePath 'system\bios.json' -Format Json -Collector {
    Get-CimInstance Win32_BIOS | Select-Object Manufacturer,SMBIOSBIOSVersion,ReleaseDate,SerialNumber
}
Invoke-Collection -Name 'Processor' -RelativePath 'system\processor.json' -Format Json -Collector {
    Get-CimInstance Win32_Processor | Select-Object Name,Manufacturer,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed
}
Invoke-Collection -Name 'Physical memory' -RelativePath 'system\physical-memory.csv' -Format Csv -Collector {
    Get-CimInstance Win32_PhysicalMemory | Select-Object Manufacturer,Capacity,Speed,PartNumber
}
Invoke-Collection -Name 'Disk volumes' -RelativePath 'system\volumes.csv' -Format Csv -Collector {
    Get-CimInstance Win32_LogicalDisk | Select-Object DeviceID,DriveType,VolumeName,FileSystem,Size,FreeSpace
}
Invoke-Collection -Name 'Installed hotfixes' -RelativePath 'system\hotfixes.csv' -Format Csv -Collector {
    Get-HotFix | Select-Object HotFixID,Description,InstalledOn,InstalledBy
}
Add-SkippedCollection -Name 'Enabled Windows features' -RelativePath 'system\windows-features.csv' -Reason 'Not collected: AV Workstation Toolkit prohibits elevated execution'

$uninstallRoots = @(
    @{ Path='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'; Scope='HKLM64' },
    @{ Path='HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'; Scope='HKLM32' },
    @{ Path='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'; Scope='HKCU' }
)
Invoke-Collection -Name 'Win32 applications' -RelativePath 'applications\installed-win32.csv' -Format Csv -Collector {
    foreach ($root in $uninstallRoots) {
        Get-ItemProperty -Path $root.Path -ErrorAction SilentlyContinue |
            Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName } |
            Select-Object @{n='RegistryScope';e={$root.Scope}},DisplayName,DisplayVersion,Publisher,InstallDate,InstallLocation,PSChildName
    }
}
Invoke-Collection -Name 'Installed Appx packages' -RelativePath 'applications\appx-installed.csv' -Format Csv -Collector {
    Get-AppxPackage | Select-Object Name,PackageFullName,Version,Publisher,InstallLocation,@{n='Scope';e={'CurrentUser'}}
}
Add-SkippedCollection -Name 'Provisioned Appx packages' -RelativePath 'applications\appx-provisioned.csv' -Reason 'Not collected: AV Workstation Toolkit prohibits elevated execution'

Invoke-NativeTextCollection -Name 'winget version' -RelativePath 'packages\winget-version.txt' -Command 'winget' -Arguments @('--version')
Invoke-NativeTextCollection -Name 'winget installed list' -RelativePath 'packages\winget-list.txt' -Command 'winget' -Arguments @('list','--disable-interactivity')
Invoke-WingetExport -Name 'winget-export-ids' -RelativePath 'packages\winget-export-ids.json'
Invoke-WingetExport -Name 'winget-export-versions' -RelativePath 'packages\winget-export-versions.json' -IncludeVersions

Invoke-Collection -Name 'Services' -RelativePath 'services-drivers\services.csv' -Format Csv -Collector {
    Get-CimInstance Win32_Service | Select-Object Name,DisplayName,State,StartMode,StartName,@{n='PathName';e={Protect-AVWorkstationToolkitSensitiveText $_.PathName}},ProcessId
}
Invoke-Collection -Name 'System drivers' -RelativePath 'services-drivers\system-drivers.csv' -Format Csv -Collector {
    Get-CimInstance Win32_SystemDriver | Select-Object Name,DisplayName,State,StartMode,ServiceType,PathName
}
Invoke-Collection -Name 'Signed PnP drivers' -RelativePath 'services-drivers\pnp-signed-drivers.csv' -Format Csv -Collector {
    Get-CimInstance Win32_PnPSignedDriver | Select-Object DeviceName,DeviceClass,Manufacturer,DriverProviderName,DriverVersion,DriverDate,InfName,DeviceID
}
Invoke-Collection -Name 'Audio and selected PnP devices' -RelativePath 'services-drivers\pnp-audio-network-usb.csv' -Format Csv -Collector {
    Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.Class -in @('Media','AudioEndpoint','Net','USB') } |
        Select-Object Class,FriendlyName,InstanceId,Status,Manufacturer,Problem
}

Invoke-Collection -Name 'Network adapters' -RelativePath 'network\adapters.csv' -Format Csv -Collector {
    Get-NetAdapter -IncludeHidden | Select-Object Name,InterfaceDescription,Status,MacAddress,LinkSpeed,DriverInformation,Virtual,HardwareInterface,InterfaceIndex
}
Invoke-Collection -Name 'IP configuration' -RelativePath 'network\ip-configuration.json' -Format Json -Collector {
    $connectionProfiles = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue)
    foreach ($adapter in Get-NetAdapter -IncludeHidden) {
        $connectionProfile = $connectionProfiles | Where-Object InterfaceIndex -eq $adapter.InterfaceIndex | Select-Object -First 1
        $networkCategory = if ($connectionProfile) { $connectionProfile.NetworkCategory } else { $null }
        $ipv4Addresses = @(Get-NetIPAddress -InterfaceIndex $adapter.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | ForEach-Object { $_.IPAddress })
        $ipv6Addresses = @(Get-NetIPAddress -InterfaceIndex $adapter.InterfaceIndex -AddressFamily IPv6 -ErrorAction SilentlyContinue | ForEach-Object { $_.IPAddress })
        $ipv4Gateways = @(Get-NetRoute -InterfaceIndex $adapter.InterfaceIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | ForEach-Object { $_.NextHop })
        $dnsServers = @(Get-DnsClientServerAddress -InterfaceIndex $adapter.InterfaceIndex -ErrorAction SilentlyContinue | ForEach-Object { $_.ServerAddresses } | Where-Object { $_ })
        [pscustomobject]@{
            InterfaceAlias = $adapter.Name
            InterfaceIndex = $adapter.InterfaceIndex
            NetworkCategory = $networkCategory
            IPv4Address = $ipv4Addresses
            IPv6Address = $ipv6Addresses
            IPv4DefaultGateway = $ipv4Gateways
            DnsServers = $dnsServers
        }
    }
}
Invoke-Collection -Name 'Network adapter bindings' -RelativePath 'network\adapter-bindings.csv' -Format Csv -Collector {
    Get-NetAdapterBinding -AllBindings | Select-Object Name,DisplayName,ComponentID,Enabled
}
Invoke-Collection -Name 'TCP listeners' -RelativePath 'network\tcp-listeners.csv' -Format Csv -Collector {
    foreach ($listener in Get-NetTCPConnection -State Listen) {
        $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
        $processName = if ($process) { $process.ProcessName } else { $null }
        $processPath = if ($process) { $process.Path } else { $null }
        [pscustomobject]@{
            Protocol='TCP'; LocalAddress=$listener.LocalAddress; LocalPort=$listener.LocalPort
            OwningProcess=$listener.OwningProcess; ProcessName=$processName; ProcessPath=$processPath
        }
    }
}
Invoke-Collection -Name 'UDP endpoints' -RelativePath 'network\udp-endpoints.csv' -Format Csv -Collector {
    foreach ($listener in Get-NetUDPEndpoint) {
        $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
        $processName = if ($process) { $process.ProcessName } else { $null }
        $processPath = if ($process) { $process.Path } else { $null }
        [pscustomobject]@{
            Protocol='UDP'; LocalAddress=$listener.LocalAddress; LocalPort=$listener.LocalPort
            OwningProcess=$listener.OwningProcess; ProcessName=$processName; ProcessPath=$processPath
        }
    }
}

Invoke-Collection -Name 'Firewall profiles' -RelativePath 'security\firewall-profiles.csv' -Format Csv -Collector {
    Get-NetFirewallProfile | Select-Object Name,Enabled,DefaultInboundAction,DefaultOutboundAction,NotifyOnListen,AllowInboundRules
}
Invoke-Collection -Name 'Active scheduled tasks' -RelativePath 'security\scheduled-tasks.csv' -Format Csv -Collector {
    foreach ($task in Get-ScheduledTask | Where-Object State -ne 'Disabled') {
        foreach ($action in $task.Actions) {
            [pscustomobject]@{
                TaskPath=$task.TaskPath; TaskName=$task.TaskName; State=$task.State
                Author=$task.Author
                Execute=if ($action.PSObject.Properties['Execute']) { $action.Execute } else { $null }
                Arguments=if ($action.PSObject.Properties['Arguments']) { Protect-AVWorkstationToolkitSensitiveText $action.Arguments } else { $null }
                WorkingDirectory=if ($action.PSObject.Properties['WorkingDirectory']) { $action.WorkingDirectory } else { $null }
            }
        }
    }
}
Invoke-Collection -Name 'Startup commands' -RelativePath 'security\startup-commands.csv' -Format Csv -Collector {
    Get-CimInstance Win32_StartupCommand | Select-Object Name,@{n='Command';e={Protect-AVWorkstationToolkitSensitiveText $_.Command}},Location,User
}

Invoke-Collection -Name 'Join and management summary' -RelativePath 'security\join-management-summary.json' -Format Json -Collector {
    $summary = [ordered]@{
        DomainJoined = (Get-CimInstance Win32_ComputerSystem).PartOfDomain
        Domain = (Get-CimInstance Win32_ComputerSystem).Domain
        CollectionElevated = $false
        AzureAdJoined = $null
        WorkplaceJoined = $null
        DeviceAuthStatus = $null
        MdmUrlPresent = $null
    }
    $dsregPath = Join-Path $env:SystemRoot 'System32\dsregcmd.exe'
    if (Test-Path -LiteralPath $dsregPath -PathType Leaf) {
        $dsreg = & $dsregPath /status 2>$null | Out-String
        foreach ($key in @('AzureAdJoined','WorkplaceJoined','DeviceAuthStatus')) {
            if ($dsreg -match "(?m)^\s*$key\s*:\s*(.+?)\s*$") { $summary[$key] = $Matches[1] }
        }
        $summary.MdmUrlPresent = [bool]($dsreg -match '(?m)^\s*MdmUrl\s*:\s*https?://')
        if ($IncludeIdentityMetadata) {
            $dsreg | Out-File -LiteralPath (Join-Path $snapshotDirectory 'security\dsregcmd-status.txt') -Encoding utf8
        }
    }
    [pscustomobject]$summary
}

$dspPattern = 'Krisp|Elevoc|Dolby|RealtekAudio|SmartMicrophone|Nahimic|Waves|MaxxAudio|PreSonus|Universal Control'
Invoke-Collection -Name 'Audio DSP indicators' -RelativePath 'applications\audio-dsp-indicators.csv' -Format Csv -Collector {
    $dspRows = [System.Collections.Generic.List[object]]::new()
    foreach ($app in Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $dspPattern }) {
        $dspRows.Add([pscustomobject]@{Source='Appx';Name=$app.Name;Version=$app.Version;Path=$app.InstallLocation}) | Out-Null
    }
    $uninstallPaths = @($uninstallRoots | ForEach-Object { $_.Path })
    foreach ($app in Get-ItemProperty -Path $uninstallPaths -ErrorAction SilentlyContinue | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -match $dspPattern }) {
        $dspRows.Add([pscustomobject]@{Source='Win32';Name=$app.DisplayName;Version=$app.DisplayVersion;Path=$app.InstallLocation}) | Out-Null
    }
    foreach ($service in Get-CimInstance Win32_Service -ErrorAction SilentlyContinue | Where-Object { ($_.Name + ' ' + $_.DisplayName) -match $dspPattern }) {
        $dspRows.Add([pscustomobject]@{Source='Service';Name=$service.DisplayName;Version='';Path=(Protect-AVWorkstationToolkitSensitiveText $service.PathName)}) | Out-Null
    }
    foreach ($driver in Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue | Where-Object { ($_.Name + ' ' + $_.DisplayName) -match $dspPattern }) {
        $dspRows.Add([pscustomobject]@{Source='Driver';Name=$driver.DisplayName;Version='';Path=$driver.PathName}) | Out-Null
    }
    foreach ($device in Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match $dspPattern }) {
        $dspRows.Add([pscustomobject]@{Source='PnPDevice';Name=$device.FriendlyName;Version='';Path=$device.InstanceId}) | Out-Null
    }
    foreach ($startup in Get-CimInstance Win32_StartupCommand -ErrorAction SilentlyContinue | Where-Object { ($_.Name + ' ' + $_.Command) -match $dspPattern }) {
        $dspRows.Add([pscustomobject]@{Source='Startup';Name=$startup.Name;Version='';Path=(Protect-AVWorkstationToolkitSensitiveText $startup.Command)}) | Out-Null
    }
    $dspRows | Sort-Object Source,Name -Unique
}

Invoke-Collection -Name 'PATH entries' -RelativePath 'development\path-entries.txt' -Format Text -Collector {
    $env:Path -split ';' | Where-Object { $_ } | ForEach-Object { $_.Trim() }
}
Invoke-NativeTextCollection -Name '.NET SDKs' -RelativePath 'development\dotnet-sdks.txt' -Command 'dotnet' -Arguments @('--list-sdks')
Invoke-NativeTextCollection -Name '.NET runtimes' -RelativePath 'development\dotnet-runtimes.txt' -Command 'dotnet' -Arguments @('--list-runtimes')
Invoke-NativeTextCollection -Name 'Python launcher inventory' -RelativePath 'development\python-installations.txt' -Command 'py' -Arguments @('--list-paths')
Invoke-NativeTextCollection -Name 'Python packages' -RelativePath 'development\python-packages.txt' -Command 'py' -Arguments @('-m','pip','freeze')
Invoke-NativeTextCollection -Name 'Node.js version' -RelativePath 'development\node-version.txt' -Command 'node' -Arguments @('--version')
Invoke-NativeTextCollection -Name 'Global npm packages' -RelativePath 'development\npm-global.txt' -Command 'npm' -Arguments @('list','--global','--depth=0')
Invoke-NativeTextCollection -Name 'VS Code extensions' -RelativePath 'development\vscode-extensions.txt' -Command 'code' -Arguments @('--list-extensions','--show-versions')
Invoke-Collection -Name 'PowerShell modules' -RelativePath 'development\powershell-modules.csv' -Format Csv -Collector {
    Get-Module -ListAvailable | Select-Object Name,Version,ModuleBase | Sort-Object Name,Version -Unique
}
Invoke-Collection -Name 'Java commands' -RelativePath 'development\java-commands.csv' -Format Csv -Collector {
    Get-Command java -All -ErrorAction SilentlyContinue | Select-Object Name,CommandType,Source,Path,Version
}

Add-SkippedCollection -Name 'Prefetch last-write evidence' -RelativePath 'diagnostics\prefetch-lastwrite.csv' -Reason 'Not collected: AV Workstation Toolkit prohibits elevated execution'

if ($IncludeDirectorySizes) {
    Invoke-Collection -Name 'Top-level directory sizes' -RelativePath 'diagnostics\top-level-directory-sizes.csv' -Format Csv -Collector {
        $sizeRoots = @($env:LOCALAPPDATA,$env:APPDATA,$env:ProgramData,(Join-Path $env:USERPROFILE 'Documents')) |
            Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Sort-Object -Unique
        foreach ($root in $sizeRoots) {
            foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction SilentlyContinue) {
                [int64]$fileCount = 0
                [int64]$byteCount = 0
                Get-ChildItem -LiteralPath $directory.FullName -File -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
                    $fileCount++
                    $byteCount += $_.Length
                }
                [pscustomobject]@{
                    Root=$root; Path=$directory.FullName; Files=$fileCount
                    Bytes=$byteCount; MB=[math]::Round(([double]$byteCount / 1MB),1)
                }
            }
        }
    }
}

$completedAt = Get-Date
$manifest = [ordered]@{
    SchemaVersion = 1
    SnapshotName = $snapshotName
    ComputerName = $env:COMPUTERNAME
    Started = $startedAt.ToString('o')
    Completed = $completedAt.ToString('o')
    DurationSeconds = [math]::Round(($completedAt - $startedAt).TotalSeconds,1)
    Elevated = $false
    IncludeDirectorySizes = [bool]$IncludeDirectorySizes
    IncludeIdentityMetadata = [bool]$IncludeIdentityMetadata
    Collections = @($collectionResults)
}
$manifest | ConvertTo-Json -Depth 8 | Out-File -LiteralPath (Join-Path $snapshotDirectory '00-manifest.json') -Encoding utf8
$collectionResults | Export-Csv -LiteralPath (Join-Path $snapshotDirectory '00-collection-results.csv') -NoTypeInformation -Encoding utf8

if (-not $SkipZip) {
    try {
        Compress-Archive -LiteralPath $snapshotDirectory -DestinationPath $zipPath -CompressionLevel Optimal
        $hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
        @(
            "File: $($hash.Path)",
            "Algorithm: $($hash.Algorithm)",
            "Hash: $($hash.Hash)"
        ) | Out-File -LiteralPath ($zipPath + '.sha256.txt') -Encoding ascii
        Write-Host ('ZIP:  {0}' -f $zipPath) -ForegroundColor Cyan
        Write-Host ('SHA256: {0}' -f $hash.Hash) -ForegroundColor Cyan
    }
    catch {
        $safeError = Protect-AVWorkstationToolkitSensitiveText -Text ($_ | Out-String)
        $safeMessage = Protect-AVWorkstationToolkitSensitiveText -Text $_.Exception.Message
        $safeError | Out-File -LiteralPath (Join-Path $snapshotDirectory 'diagnostics\zip.error.txt') -Encoding utf8
        Write-Host ('[WARN] ZIP creation failed: {0}' -f $safeMessage) -ForegroundColor Yellow
    }
}

Write-Host ('Snapshot folder: {0}' -f $snapshotDirectory) -ForegroundColor Cyan
Write-Host ('Collections: {0} successful, {1} skipped, {2} failed' -f @($collectionResults | Where-Object Status -eq 'Success').Count, @($collectionResults | Where-Object Status -eq 'Skipped').Count, @($collectionResults | Where-Object Status -eq 'Failed').Count) -ForegroundColor Cyan
