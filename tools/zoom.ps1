# zoom.ps1 -- crop regions out of a PNG and blow them up with nearest-neighbour.
#
# The counterpart to img-diff.ps1: that one tells you *whether* pixels differ,
# this one lets you look at the pixels. Geometry probes report rectangles that
# are already correct while the pixels inside them are wrong (stale resize,
# a square corner poking out of a rounded card), so "what is actually drawn at
# 1180,132" is a question that comes up constantly.
#
# Nearest-neighbour on purpose: it shows the real pixel grid. A smooth zoom
# would invent pixels and hide exactly the one-pixel artifacts this is for.
#
# Usage:
#   tools\zoom.ps1 -File shoots\input-empty.png -Box 264:120:40x28 -Zoom 8
#   tools\zoom.ps1 -File a.png -Box 10:10:20x20,100:10:20x20 -Out b.png
#
# -Box takes X:Y:WxH (or X:Y:W:H), several separated by commas.  Colon and not
# comma between X and Y, because a bare PowerShell array element that contains
# commas would be split by the parser before it ever reaches this script.
# Default zoom 8, default output shoots/_zoom.png.
# Must stay ASCII-only (PS 5.1 reads a BOM-less file as ANSI).

param(
    [Parameter(Mandatory = $true)][string] $File,
    [Parameter(Mandatory = $true)][string[]] $Box,
    [int] $Zoom = 8,
    [string] $Out = '',
    [string[]] $Labels = @()
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($File)) { $File = Join-Path $repo $File }
if (-not (Test-Path $File)) { throw "no such file: $File" }
if ($Out -eq '') { $Out = Join-Path $repo 'shoots\_zoom.png' }
elseif (-not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $repo $Out }

# X:Y:WxH or X:Y:W:H -> int[4]
function Parse-Box([string] $s) {
    $parts = @()
    foreach ($chunk in $s -split ':') {
        foreach ($piece in $chunk -split 'x') { if ($piece -ne '') { $parts += [int]$piece } }
    }
    if ($parts.Count -ne 4) { throw "bad -Box '$s' (want X:Y:WxH or X:Y:W:H)" }
    return $parts
}

$boxes = @()
foreach ($b in $Box) { $boxes += ,(Parse-Box $b) }
$names = @()
foreach ($l in $Labels) { $names += ($l -split ',') }

$lbH = 16
$pad = 6
$src = [System.Drawing.Image]::FromFile($File)

$cellW = ($boxes[0][2] * $Zoom)
$cellH = ($boxes[0][3] * $Zoom)
$sheet = [System.Drawing.Bitmap]::new(($cellW + 2 * $pad) * $boxes.Count, $cellH + 2 * $pad + $lbH)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::FromArgb(255, 0, 255))   # magenta = "outside the crop"
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$font = [System.Drawing.Font]::new('Consolas', 9)

for ($i = 0; $i -lt $boxes.Count; $i++) {
    $b = $boxes[$i]
    $srcRect = [System.Drawing.Rectangle]::new($b[0], $b[1], $b[2], $b[3])
    $dstRect = [System.Drawing.Rectangle]::new(($i * ($cellW + 2 * $pad) + $pad), $pad, ($b[2] * $Zoom), ($b[3] * $Zoom))
    $g.DrawImage($src, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
    $label = if ($i -lt $names.Count -and $names[$i] -ne '') { $names[$i] } else { "$($b[0]),$($b[1]) $($b[2])x$($b[3])" }
    $g.DrawString($label, $font, [System.Drawing.Brushes]::Black, ($i * ($cellW + 2 * $pad) + $pad), ($pad + $cellH + 1))
}

$font.Dispose()
$g.Dispose()
$sheet.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()
$src.Dispose()
Write-Output "saved $Out  ($($boxes.Count) box(es) at ${Zoom}x)"
