# prepare-demo-shots.ps1 -- turn raw screenshot pairs into the site's demo images.
#
# The demo section on the landing page is a draggable divider between two frames
# of the SAME screen: "<id>-visible" (what the machine's user sees, BangGang
# present) and "<id>-capture" (what screen capture receives, BangGang absent).
# The two are stacked on top of each other and revealed by a moving clip, so they
# MUST end up at identical pixel dimensions -- a one-pixel difference shows up as
# a wandering seam along the whole divider. That is the one thing this script
# exists to guarantee: both frames of a pair go through the exact same resize.
#
# It also keeps the page light: a full-screen PNG is easily several MB, and JPEG
# at this width and quality is an order of magnitude smaller with no visible loss
# on UI text at 1600px.
#
# Raw shots go in shoots/demo/ (shoots/ is gitignored), named:
#     <id>-visible.png   and   <id>-capture.png
# Any id works; script.js just looks for the same pair under web/assets/demo/.
# Filenames can be .png / .jpg / .jpeg / .bmp.
#
# ASCII-only on purpose: PowerShell 5.1 reads a BOM-less script as ANSI, so any
# non-ASCII byte here would be a syntax error.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File web\tools\prepare-demo-shots.ps1
#   powershell -ExecutionPolicy Bypass -File web\tools\prepare-demo-shots.ps1 -Width 1920 -Quality 92

param(
    [string]$Source,
    [string]$OutputDir,
    [int]$Width = 1600,
    [int]$Quality = 90
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # web\tools -> web -> repo root
if (-not $Source) { $Source = Join-Path $root 'shoots\demo' }
if (-not $OutputDir) { $OutputDir = Join-Path $root 'web\assets\demo' }

if (-not (Test-Path -LiteralPath $Source)) {
    throw "raw screenshots not found: $Source`n  Put the pairs there first (see the header of this script)."
}
if (-not (Test-Path -LiteralPath $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }

$exts = @('.png', '.jpg', '.jpeg', '.bmp')


function Set-HighQuality {
    param([System.Drawing.Graphics]$G)
    $G.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $G.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $G.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $G.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
}

# Load without holding a lock on the source file.
function Import-Image {
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $ms = New-Object System.IO.MemoryStream($bytes, $false)
    $img = [System.Drawing.Image]::FromStream($ms)
    return @{ Image = $img; Stream = $ms }
}

# Scale down one step at a time (halving until within 2x of the target), so every
# destination pixel is fed by a real neighbourhood instead of a sparse sample.
# PixelOffsetMode must be HighQuality alongside HighQualityBicubic, otherwise the
# bicubic kernel samples half a pixel off and the seam lands differently on the
# two frames.
function New-ScaledBitmap {
    param([System.Drawing.Bitmap]$Src, [int]$TargetW, [int]$TargetH)

    $cur = $Src
    $temps = New-Object System.Collections.ArrayList

    while ($cur.Width -gt $TargetW * 2) {
        $w = [Math]::Max($TargetW, [int]($cur.Width / 2))
        $h = [Math]::Max($TargetH, [int]($cur.Height / 2))
        $next = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $g = [System.Drawing.Graphics]::FromImage($next)
        Set-HighQuality $g
        $g.DrawImage($cur, 0, 0, $w, $h)
        $g.Dispose()
        if ($cur -ne $Src) { $temps.Add($cur) | Out-Null }
        $cur = $next
    }

    # Format24bppRgb from the start: JPEG has no alpha channel, and saving a
    # 32bpp source straight to JPEG can fringe partially transparent edges black.
    $dst = New-Object System.Drawing.Bitmap($TargetW, $TargetH, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g2 = [System.Drawing.Graphics]::FromImage($dst)
    Set-HighQuality $g2
    $g2.Clear([System.Drawing.Color]::White)
    $g2.DrawImage($cur, (New-Object System.Drawing.Rectangle(0, 0, $TargetW, $TargetH)))
    $g2.Dispose()
    if ($cur -ne $Src) { $temps.Add($cur) | Out-Null }
    foreach ($t in $temps) { $t.Dispose() }

    return $dst
}

$jpegCodec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
    Where-Object { $_.MimeType -eq 'image/jpeg' } | Select-Object -First 1
if (-not $jpegCodec) { throw 'no JPEG encoder available' }
$encParams = New-Object System.Drawing.Imaging.EncoderParameters(1)
$encParams.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
    [System.Drawing.Imaging.Encoder]::Quality, [int64]$Quality)

# ---- find pairs ------------------------------------------------------------
$visibles = @()
foreach ($e in $exts) { $visibles += @(Get-ChildItem -LiteralPath $Source -Filter "*-visible$e" -File -ErrorAction SilentlyContinue) }
$visibles = $visibles | Sort-Object Name -Unique

if (-not $visibles.Count) {
    throw "no '*-visible' images in $Source`n  Expected pairs like chat-visible.png / chat-capture.png."
}

$done = 0
foreach ($v in $visibles) {
    $id = $v.Name -replace '-visible\.[^.]+$', ''

    $capture = $null
    foreach ($e in $exts) {
        $candidate = Join-Path $Source ("$id-capture$e")
        if (Test-Path -LiteralPath $candidate) { $capture = Get-Item -LiteralPath $candidate; break }
    }
    if (-not $capture) {
        Write-Warning "no '-capture' counterpart for $($v.Name) -- skipped"
        continue
    }

    $a = Import-Image -Path $v.FullName
    $b = Import-Image -Path $capture.FullName
    try {
        $sw = $a.Image.Width; $sh = $a.Image.Height
        if ($b.Image.Width -ne $sw -or $b.Image.Height -ne $sh) {
            throw ("size mismatch for '{0}': visible is {1}x{2}, capture is {3}x{4}.`n" -f $id, $sw, $sh, $b.Image.Width, $b.Image.Height) +
                  "  Both frames must be captured at the same resolution, or the divider will not line up."
        }

        $targetW = [Math]::Min($Width, $sw)
        $targetH = [int][Math]::Round($sh * $targetW / $sw)

        $pair = @(
            @{ Key = 'visible'; Img = $a.Image },
            @{ Key = 'capture'; Img = $b.Image }
        )
        $outSizes = @()
        foreach ($p in $pair) {
            $srcBmp = New-Object System.Drawing.Bitmap($p.Img)
            $scaled = New-ScaledBitmap -Src $srcBmp -TargetW $targetW -TargetH $targetH
            $srcBmp.Dispose()
            try {
                $outPath = Join-Path $OutputDir ("$id-{0}.jpg" -f $p.Key)
                $scaled.Save($outPath, $jpegCodec, $encParams)
                $outSizes += (Get-Item -LiteralPath $outPath).Length
            } finally {
                $scaled.Dispose()
            }
        }

        $kb = [math]::Round(($outSizes | Measure-Object -Sum).Sum / 1KB)
        Write-Host ("  {0,-14} {1}x{2} -> {3}x{4}  ({5} KB total)" -f $id, $sw, $sh, $targetW, $targetH, $kb)
        $done++
    } finally {
        $a.Image.Dispose(); $a.Stream.Dispose()
        $b.Image.Dispose(); $b.Stream.Dispose()
    }
}

Write-Host ''
if ($done) {
    Write-Host "OK -> $OutputDir  ($done pair(s))"
    Write-Host 'Now bump the ?v= query in index.html for script.js/styles.css if you changed those,'
    Write-Host 'and redeploy: wrangler pages deploy --project-name=banggang'
} else {
    Write-Host 'Nothing produced.'
}
