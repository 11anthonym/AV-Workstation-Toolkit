<#
.SYNOPSIS
    Writes the GitHub release page for a completed release folder.
.DESCRIPTION
    The page is docs\releases\<release>.md, written for people installing the app, with its relative links made
    absolute at the release tag, followed by the SHA-256 checksums and one Source line read from the release
    manifest: the tagged commit, the embedded .NET runtime, and whether and by whom the files are code-signed.
    The unsigned publication procedure and the tagged signing workflow both publish exactly this text.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseRoot,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$RepositoryUrl = 'https://github.com/11anthonym/AV-Workstation-Toolkit'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedRelease = [IO.Path]::GetFullPath($ReleaseRoot)
if (-not (Test-Path -LiteralPath $resolvedRelease -PathType Container)) { throw 'ReleaseRoot must identify a completed release folder.' }
$releaseName = Split-Path -Leaf $resolvedRelease
if ($releaseName -notmatch '^\d+\.\d+\.\d+(?:-rc\.\d+)?$') { throw "Release folder name '$releaseName' is not a release or release candidate." }
if ($Tag -notmatch '^v?\d+\.\d+\.\d+(?:-rc\.\d+)?$' -or $Tag.TrimStart('v') -ne $releaseName) { throw "Tag '$Tag' does not name release $releaseName." }
if ($RepositoryUrl -notmatch '^https://github\.com/[A-Za-z0-9-]+/[A-Za-z0-9._-]+$') { throw 'RepositoryUrl must be a GitHub repository URL.' }

$notesPath = Join-Path $repositoryRoot "docs\releases\$releaseName.md"
if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) { throw "Release notes docs\releases\$releaseName.md are missing." }
$manifest = Get-Content -LiteralPath (Join-Path $resolvedRelease "AV-Workstation-Toolkit-$releaseName-release.json") -Raw | ConvertFrom-Json
if ([string]$manifest.ReleaseName -ne $releaseName -or [string]$manifest.CommitSha -notmatch '^[a-f0-9]{40}$' -or [bool]$manifest.SourceDirty) {
    throw 'The release manifest does not describe a clean build of this release.'
}
$checksumLines = @(Get-Content -LiteralPath (Join-Path $resolvedRelease "AV-Workstation-Toolkit-$releaseName-SHA256SUMS.txt") |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($checksumLines.Count -ne 7) { throw 'The checksum list must cover exactly the seven other standard assets.' }

# Notes link to repository files relative to docs\releases; a release page needs them absolute at the tag.
$notes = (Get-Content -LiteralPath $notesPath -Raw -Encoding UTF8) -replace "`r`n","`n"
$notes = $notes -replace '\A# [^\n]*\n+',''
$notes = [regex]::Replace($notes,'\]\((?<target>[^)#\s]+)(?<anchor>#[^)\s]*)?\)',{
    param($match)
    $target = $match.Groups['target'].Value
    if ($target -match '^(?:[a-z][a-z0-9+.-]*:)') { return $match.Value }
    $segments = New-Object System.Collections.Generic.List[string]
    foreach ($segment in @('docs','releases') + ($target -split '/')) {
        if ($segment -eq '..') {
            if ($segments.Count -eq 0) { throw "Release-note link '$target' leaves the repository." }
            $segments.RemoveAt($segments.Count - 1)
        } elseif ($segment -ne '.' -and $segment -ne '') { $segments.Add($segment) }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot ($segments -join '\')))) { throw "Release-note link '$target' does not resolve." }
    return "]($RepositoryUrl/blob/$Tag/$($segments -join '/')$($match.Groups['anchor'].Value))"
})

$builder = New-Object System.Text.StringBuilder
[void]$builder.Append($notes.TrimEnd()).Append("`n`n## SHA-256 checksums`n`n| File | SHA-256 |`n|---|---|`n")
foreach ($line in $checksumLines) {
    $parts = $line -split '\s+\*?',2
    if ($parts.Count -ne 2 -or $parts[0] -notmatch '^[A-Fa-f0-9]{64}$') { throw "Checksum line is malformed: $line" }
    [void]$builder.Append("| ``$($parts[1].Trim())`` | ``$($parts[0].ToUpperInvariant())`` |`n")
}
[void]$builder.Append("`nThe checksum list itself is ``AV-Workstation-Toolkit-$releaseName-SHA256SUMS.txt``.`n")

$runtime = ([string]$manifest.TargetRuntime -split '/')[-1]
if ($runtime -notmatch '^\d+\.\d+\.\d+$') { throw 'The release manifest does not record the embedded .NET runtime.' }
$signing = if ([bool]$manifest.Signed) {
    $signer = [regex]::Match([string]$manifest.Launcher.SignerSubject,'(?:^|,\s*)CN=(?<name>[^,]+)').Groups['name'].Value
    if ([string]::IsNullOrWhiteSpace($signer)) { throw 'The signed release manifest does not record its signer.' }
    "Code-signed by $signer."
} else { 'Not code-signed.' }
$commit = [string]$manifest.CommitSha
[void]$builder.Append("`n## Source`n`nBuilt from [``$($commit.Substring(0,7))``]($RepositoryUrl/commit/$commit) (tag ``$Tag``) with the self-contained .NET $runtime runtime. $signing See the [Code signing policy]($RepositoryUrl/blob/$Tag/docs/Code-Signing-Policy.md).`n")

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
[IO.File]::WriteAllText($resolvedOutput,$builder.ToString(),(New-Object System.Text.UTF8Encoding($false)))
Write-Output "RELEASE_NOTES_OK release=$releaseName tag=$Tag path=$resolvedOutput"
