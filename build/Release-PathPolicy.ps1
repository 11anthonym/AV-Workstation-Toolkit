Set-StrictMode -Version Latest

function Test-FullyQualifiedWindowsPath {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.IndexOf([char]0) -ge 0) {
        return $false
    }

    # Extended/device namespaces are intentionally outside the release input contract.
    if ($Path.StartsWith('\\?\', [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith('\\.\', [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    try {
        $root = [IO.Path]::GetPathRoot($Path)
        [void][IO.Path]::GetFullPath($Path)
    }
    catch {
        return $false
    }

    $driveQualified = $root -match '^[A-Za-z]:[\\/]$'
    $uncQualified = $root -match '^\\\\[^\\/:*?"<>|]+\\[^\\/:*?"<>|]+\\?$'
    if (-not $driveQualified -and -not $uncQualified) {
        return $false
    }

    $invalidNameCharacters = [IO.Path]::GetInvalidFileNameChars()
    $remainder = $Path.Substring($root.Length)
    foreach ($segment in @($remainder -split '[\\/]')) {
        if ($segment.Length -gt 0 -and $segment.IndexOfAny($invalidNameCharacters) -ge 0) {
            return $false
        }
    }

    return $true
}
