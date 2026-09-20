# make-logo.ps1 -- generate the website logo PNGs from the app icon source.
#
# The site needs square PNGs at a few fixed sizes. Rendering them from
# assets/icon-char.png (the same 939px source tools\make-icon.ps1 uses) keeps the
# website and the installed app showing the same artwork, and means the logo is
# regenerated rather than hand-edited -- same rule as the .ico.
#
# Outputs (all into web\assets\):
#   logo.png             512x512  header / hero / download card
#   apple-touch-icon.png 180x180  iOS home-screen bookmark
#   favicon-32.png        32x32   browser tab fallback (favicon.svg is primary)
#
# Downscaling steps down by halves, for the same reason make-icon.ps1 does: a
# single 939 -> 32 bicubic jump samples too few source pixels and loses detail.
#
# ASCII-only on purpose: PowerShell 5.1 reads a BOM-less script as ANSI, so any
# non-ASCII byte here would be a syntax error.
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools\make-logo.ps1

param(
    [string]$Source = (Join-Path $PSScriptRoot '..\assets\icon-char.png'),
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\web\assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$targets = @(
    @{ Name = 'logo.png';             Size = 512 },
    @{ Name = 'apple-touch-icon.png'; Size = 180 },
    @{ Name = 'favicon-32.png';       Size = 32  }
)


function Set-HighQuality {
    param([System.Drawing.Graphics]$G)
    $G.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $G.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $G.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $G.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
}

# Scale down one step at a time (halving until within 2x of the target), so every
# destination pixel is fed by a real neighbourhood instead of a sparse sample.
function New-LogoBitmap {
    param([System.Drawing.Bitmap]$Src, [int]$Size)

    $cur = $Src
    $temps = New-Object System.Collections.ArrayList

    while ($cur.Width -gt $Size * 2) {
        $w = [Math]::Max($Size, [int]($cur.Width / 2))
        $h = [Math]::Max($Size, [int]($cur.Height / 2))
        $next = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($next)
        Set-HighQuality $g
        $g.DrawImage($cur, 0, 0, $w, $h)
        $g.Dispose()
        if ($cur -ne $Src) { $temps.Add($cur) | Out-Null }
        $cur = $next
    }

    $dst = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($dst)
    Set-HighQuality $g2
    $g2.DrawImage($cur, (New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)))
    $g2.Dispose()
    if ($cur -ne $Src) { $temps.Add($cur) | Out-Null }
    foreach ($t in $temps) { $t.Dispose() }

    return $dst
}


if (-not (Test-Path -LiteralPath $Source)) { throw "Source image not found: $Source" }
if (-not (Test-Path -LiteralPath $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }

# Read through a MemoryStream: Image.FromFile would keep the source locked for
# the rest of the session.
$bytes = [System.IO.File]::ReadAllBytes($Source)
$ms = New-Object System.IO.MemoryStream($bytes, $false)
$src = [System.Drawing.Image]::FromStream($ms)
$srcBmp = New-Object System.Drawing.Bitmap($src)
$src.Dispose()
$ms.Dispose()

try {
    foreach ($t in $targets) {
        $bmp = New-LogoBitmap -Src $srcBmp -Size $t.Size
        try {
            $path = Join-Path $OutputDir $t.Name
            $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Host ("  {0,-24} {1}x{1}  {2:N0} bytes" -f $t.Name, $t.Size, (Get-Item -LiteralPath $path).Length)
        } finally {
            $bmp.Dispose()
        }
    }
} finally {
    $srcBmp.Dispose()
}

Write-Host "OK -> $OutputDir"
