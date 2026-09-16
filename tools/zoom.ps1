# Blow up a region of an already-saved shot so the pixels can be looked at directly.
#
# Written for the case where a probe passes but the picture is wrong: geometry probes
# measure rects, and a stale-pixel artefact (a corner drawn at an old offset, a border
# left behind by a control that moved) is invisible to them. Cropping the region out of
# the PNG and scaling it up with nearest-neighbour keeps the pixels honest -- no
# smoothing to hide a 1px seam -- and it works on a shot from a run that is already
# over, which is the only way to look at a frame you did not know you would need.
#
# Usage:  powershell -File tools\zoom.ps1 -Path shoots\x.png -X 20 -Y 655 -W 300 -H 30 -F 4

param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [Parameter(Mandatory = $true)][int]$W,
    [Parameter(Mandatory = $true)][int]$H,
    [int]$F = 4,
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if ($Out -eq '') {
    $dir = Join-Path (Split-Path $PSScriptRoot -Parent) 'shoots'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $Out = Join-Path $dir 'zoom.png'
}

$src = [System.Drawing.Image]::FromFile((Resolve-Path $Path))
if ($X + $W -gt $src.Width -or $Y + $H -gt $src.Height) {
    throw ("region " + $X + "," + $Y + " " + $W + "x" + $H + " runs off the " + $src.Width + "x" + $src.Height + " image")
}

$b = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($b)
$g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $W, $H),
                  (New-Object System.Drawing.Rectangle $X, $Y, $W, $H), [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()

$big = New-Object System.Drawing.Bitmap ($W * $F), ($H * $F)
$g2 = [System.Drawing.Graphics]::FromImage($big)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$g2.DrawImage($b, 0, 0, $W * $F, $H * $F)
$g2.Dispose()

$big.Save($Out)
$big.Dispose(); $b.Dispose(); $src.Dispose()
Write-Output ("saved " + $Out + "  (" + $W + "x" + $H + " at +" + $X + "+" + $Y + " scaled x" + $F + ")")
