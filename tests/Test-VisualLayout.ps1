<#
.SYNOPSIS
    Validates deterministic AVWorkstationToolkit viewports and classifies unusable captures.

.DESCRIPTION
    Uses the source launcher's inert mock plan. It does not query providers,
    store credentials, download software, or execute package actions.
#>

[CmdletBinding()]
param([string]$OutputRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'artifacts\visual-qa'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd('\') + '\'
if (-not $OutputRoot.StartsWith($artifactsRoot,[StringComparison]::OrdinalIgnoreCase)) {
    throw "Visual QA output must stay beneath the repository artifacts directory: $OutputRoot"
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

Add-Type -AssemblyName PresentationCore
$powershellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$frontendPath = Join-Path $repositoryRoot 'scripts\Start-AVWorkstationToolkit.ps1'
$viewports = @(
    @{ Width=1040; Height=760; QuickView='Missing' },
    @{ Width=1280; Height=860; QuickView='Updates' },
    @{ Width=1440; Height=900; QuickView='All' },
    @{ Width=1920; Height=1080; QuickView='All' }
)
$meaningfulCaptures = 0
$unavailableCaptures = 0

foreach ($viewport in $viewports) {
    $width = [int]$viewport.Width
    $height = [int]$viewport.Height
    $quickView = [string]$viewport.QuickView
    $previewPath = Join-Path $OutputRoot ("AV-Workstation-Toolkit-{0}-{1}x{2}.png" -f $quickView,$width,$height)
    $dataRoot = Join-Path $OutputRoot ("data-{0}x{1}" -f $width,$height)
    $output = (& $powershellPath -NoProfile -ExecutionPolicy RemoteSigned -STA -File $frontendPath `
        -SmokeTest -RenderPreviewPath $previewPath -RenderWidth $width -RenderHeight $height -RenderQuickView $quickView -DataRoot $dataRoot 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Visual render ${width}x${height} failed: $output" }
    if ($output -notmatch 'SMOKE_OK' -or $output -notmatch 'UI_BEHAVIOR_OK' -or $output -notmatch 'UI_SELECTION_FLOW_OK' -or
        $output -notmatch ("LAYOUT_OK viewport=.* quickView={0}" -f $quickView) -or $output -notmatch 'PREVIEW_(?:OK|UNAVAILABLE)') {
        throw "Visual render ${width}x${height} did not report complete layout evidence: $output"
    }
    if (-not (Test-Path -LiteralPath $previewPath -PathType Leaf) -or (Get-Item -LiteralPath $previewPath).Length -le 0) {
        throw "Visual render ${width}x${height} did not produce an inspectable fallback frame."
    }

    $stream = [IO.File]::OpenRead($previewPath)
    try {
        $decoder = [Windows.Media.Imaging.PngBitmapDecoder]::new(
            $stream,
            [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $frame = $decoder.Frames[0]
        $exactDimensions = $frame.PixelWidth -eq $width -and $frame.PixelHeight -eq $height
        if (-not $exactDimensions -and $output -notmatch 'PREVIEW_UNAVAILABLE') {
            throw "Visual render dimensions differ without an unavailable-capture diagnostic. Expected ${width}x${height}; found $($frame.PixelWidth)x$($frame.PixelHeight)."
        }
        $meaningful = $false
        $samples = 0
        $nonBlack = 0
        $themePixels = 0
        $colors = [Collections.Generic.HashSet[int]]::new()
        if ($exactDimensions) {
            $bitmap = [Windows.Media.Imaging.FormatConvertedBitmap]::new(
                $frame,[Windows.Media.PixelFormats]::Bgra32,$null,0)
            $stride = $width * 4
            $pixels = New-Object byte[] ($stride * $height)
            $bitmap.CopyPixels($pixels,$stride,0)
            for ($y = 0; $y -lt $height; $y += 24) {
                for ($x = 0; $x -lt $width; $x += 24) {
                    $offset = ($y * $stride) + ($x * 4)
                    $blue = [int]$pixels[$offset]
                    $green = [int]$pixels[$offset + 1]
                    $red = [int]$pixels[$offset + 2]
                    $alpha = [int]$pixels[$offset + 3]
                    $samples++
                    if ($alpha -gt 0 -and ($red -gt 4 -or $green -gt 4 -or $blue -gt 4)) { $nonBlack++ }
                    if (($red -eq 10 -and $green -eq 15 -and $blue -eq 28) -or
                        ($red -eq 14 -and $green -eq 23 -and $blue -eq 40) -or
                        ($red -eq 13 -and $green -eq 21 -and $blue -eq 36)) { $themePixels++ }
                    [void]$colors.Add(($red -shl 16) -bor ($green -shl 8) -bor $blue)
                }
            }
            $meaningful = $samples -gt 0 -and ($nonBlack / $samples) -ge 0.80 -and
                ($themePixels / $samples) -ge 0.35 -and $colors.Count -ge 16
        }
    }
    finally { $stream.Dispose() }

    if ($meaningful) {
        $meaningfulCaptures++
        Write-Output ("VISUAL_LAYOUT_OK viewport={0}x{1} quickView={2} screenshot=meaningful file={3}" -f $width,$height,$quickView,$previewPath)
    }
    elseif ($output -match 'PREVIEW_UNAVAILABLE') {
        $unavailableCaptures++
        Write-Output ("VISUAL_LAYOUT_OK viewport={0}x{1} quickView={2} screenshot=unavailable file={3}" -f $width,$height,$quickView,$previewPath)
    }
    else {
        throw "Visual render ${width}x${height} was reported available but resembles a black, foreign, or empty frame (nonBlack=$nonBlack/$samples, theme=$themePixels/$samples, colors=$($colors.Count))."
    }
}

Write-Output ("VISUAL_SUMMARY layouts={0} screenshots={1} unavailable={2}" -f $viewports.Count,$meaningfulCaptures,$unavailableCaptures)
