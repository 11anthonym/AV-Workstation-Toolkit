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

function Get-ReleaseFolderState {
    # The size and write time of every file in the release directory except the folder being built. It is cheap enough
    # to take on every build, and any rewrite of an existing release file changes its write time.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ReleaseParent,
        [Parameter(Mandatory)][string]$ExcludedName
    )

    if (-not (Test-Path -LiteralPath $ReleaseParent -PathType Container)) { return ,@() }
    $parent = [IO.Path]::GetFullPath($ReleaseParent).TrimEnd('\')
    $lines = foreach ($item in @(Get-ChildItem -LiteralPath $parent -Force | Where-Object { $_.Name -ne $ExcludedName })) {
        if (-not $item.PSIsContainer) {
            'file|{0}|{1}|{2}' -f $item.Name,$item.Length,$item.LastWriteTimeUtc.Ticks
            continue
        }
        'dir|{0}' -f $item.Name
        foreach ($file in @(Get-ChildItem -LiteralPath $item.FullName -Recurse -Force)) {
            '{0}|{1}|{2}|{3}' -f $(if ($file.PSIsContainer) { 'dir' } else { 'file' }),$file.FullName.Substring($parent.Length + 1),
                $(if ($file.PSIsContainer) { 0 } else { $file.Length }),$file.LastWriteTimeUtc.Ticks
        }
    }
    return ,@($lines | Sort-Object)
}

function Assert-ReleaseFolderReplaceable {
    # A completed release candidate or release is evidence of exactly what was built and checked. Once its folder holds a
    # release manifest from the ReleaseCandidate or Production channel, no build replaces it, from any commit: a
    # candidate that needs changes gets the next rc.N. A folder without a manifest is an unfinished build, and a
    # Development build is a local working copy; either can be rebuilt. The build calls this before it changes a byte.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ReleaseRoot,
        [Parameter(Mandatory)][string]$ReleaseName
    )

    if (-not (Test-Path -LiteralPath $ReleaseRoot -PathType Container)) { return }
    $manifestPath = Join-Path $ReleaseRoot ('AV-Workstation-Toolkit-{0}-release.json' -f $ReleaseName)
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return }
    # An unreadable manifest fails the build here rather than being guessed at.
    $channel = [string](Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).BuildChannel
    if ($channel -ceq 'Development') { return }
    throw "artifacts\release\$ReleaseName already holds a completed $channel build, and completed release candidates and releases are never rebuilt. Use the next rc.N for a new candidate. To rebuild an unpublished release whose QA failed, move its folder aside yourself first."
}