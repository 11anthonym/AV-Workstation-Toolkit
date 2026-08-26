Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-AVWorkstationToolkitVendorBridgePath {
    [CmdletBinding()]
    param()

    $path = [Environment]::GetEnvironmentVariable('AVWORKSTATIONTOOLKIT_LAUNCHER_PATH','Process')
    if ([string]::IsNullOrWhiteSpace($path) -or -not [IO.Path]::IsPathRooted($path)) { return '' }
    $path = [IO.Path]::GetFullPath($path)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or [IO.Path]::GetExtension($path) -ne '.exe') { return '' }
    return $path
}

function Invoke-AVWorkstationToolkitVendorBridge {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Request,
        [scriptblock]$PumpEvents,
        [ValidateRange(5,3600)][int]$TimeoutSeconds = 1800
    )

    $bridgePath = Get-AVWorkstationToolkitVendorBridgePath
    if ([string]::IsNullOrWhiteSpace($bridgePath)) {
        throw 'Vendor downloads require the packaged AV Workstation Toolkit executable. Build AV Workstation Toolkit and run the packaged application.'
    }
    $json = $Request | ConvertTo-Json -Depth 8 -Compress
    if ($json.Length -gt 65536) { throw 'Vendor bridge request exceeds the 64 KiB safety limit.' }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $bridgePath
    $startInfo.Arguments = '--vendor-bridge'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $utf8NoBom = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardOutputEncoding = $utf8NoBom
    $startInfo.StandardErrorEncoding = $utf8NoBom
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw 'Windows did not start the AV Workstation Toolkit vendor bridge.' }
        $process.StandardInput.Write($json)
        $process.StandardInput.Close()
        $json = $null
        if ($Request.PSObject.Properties.Name -contains 'Password') { $Request.Password = '' }
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while (-not $process.WaitForExit(100)) {
            if ([DateTime]::UtcNow -ge $deadline) {
                try { $process.Kill() } catch { }
                throw 'The vendor operation timed out.'
            }
            if ($null -ne $PumpEvents) { & $PumpEvents }
        }
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        if ($stdout.Length -gt 1048576 -or $stderr.Length -gt 1048576) { throw 'Vendor bridge output exceeds the 1 MiB safety limit.' }
        try { $response = $stdout | ConvertFrom-Json -ErrorAction Stop }
        catch { throw 'Vendor bridge returned malformed JSON.' }
        if ($null -eq $response -or $response.PSObject.Properties.Name -notcontains 'Success' -or $response.PSObject.Properties.Name -notcontains 'Message') {
            throw 'Vendor bridge response is missing required fields.'
        }
        if (-not [bool]$response.Success -or $process.ExitCode -ne 0) {
            $message = [string]$response.Message
            if ([string]::IsNullOrWhiteSpace($message)) { $message = 'The vendor operation failed.' }
            throw $message
        }
        return $response
    }
    finally {
        if ($Request.PSObject.Properties.Name -contains 'Password') { $Request.Password = '' }
        $process.Dispose()
    }
}

Export-ModuleMember -Function @('Get-AVWorkstationToolkitVendorBridgePath','Invoke-AVWorkstationToolkitVendorBridge')
