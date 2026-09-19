# make-icon.ps1 -- build a standards-compliant multi-size .ico from a PNG source.
#
# Why this exists: the .ico files shipped in assets/ (fav-char.ico / fav-text.ico)
# are single-frame 512x512 uncompressed BMP blobs whose ICO directory entry
# declares 0x0 (= 256x256) -- the directory header and the DIB header disagree,
# and there is exactly one frame. Windows picks icon frames by matching the
# directory's declared size, so those files degrade badly at 16/32 px.
#
# This script emits a real multi-resolution icon with directory entries that
# match their frames.
#
# Frame encoding is deliberately mixed, because consumers disagree:
#   * 16..128 px -> BMP (32bpp BGRA + AND mask), the classic DIB frame.
#   * 256 px     -> PNG, the near-universal convention for the largest frame.
#
# Why not PNG everywhere: GDI+ (System.Drawing.Icon, and anything built on it)
# cannot decode PNG-compressed icon frames -- it reads the PNG bytes as raw DIB
# pixels and renders colour noise. The Windows shell reads PNG frames fine, so
# an all-PNG icon looks correct in Explorer yet turns to static in any GDI+
# consumer. BMP frames are decoded correctly by both, so they carry the sizes
# that every consumer actually asks for.
#
# ASCII-only on purpose: PowerShell 5.1 reads a BOM-less script as ANSI, so any
# non-ASCII byte here would be a syntax error.
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1

param(
    [string]$Source = (Join-Path $PSScriptRoot '..\assets\icon-char.png'),
    [string]$Output = (Join-Path $PSScriptRoot '..\assets\app.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngSizes = @(256)   # see the header comment: only the top frame is PNG


# Quality settings shared by every DrawImage in this script. PixelOffsetMode
# must be HighQuality alongside HighQualityBicubic, otherwise the bicubic
# kernel samples half a pixel off and the result is visibly soft.
function Set-HighQuality {
    param([System.Drawing.Graphics]$G)
    $G.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $G.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $G.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $G.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
}

# Scale down one step at a time. A single 939 -> 16 bicubic jump samples far too
# few source pixels and drops detail; halving until we are within 2x of the
# target keeps every destination pixel fed by a real neighbourhood.
function New-IconBitmap {
    param([System.Drawing.Bitmap]$Src, [int]$Size)

    $cur = $Src
    $temp = New-Object System.Collections.ArrayList

    while ($cur.Width -gt $Size * 2) {
        $n = [int]($cur.Width / 2)
        $half = New-Object System.Drawing.Bitmap($n, $n, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($half)
        Set-HighQuality $g
        $g.DrawImage($cur, 0, 0, $n, $n)
        $g.Dispose()
        [void]$temp.Add($half)
        $cur = $half
    }

    $out = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    Set-HighQuality $g2
    $g2.DrawImage($cur, 0, 0, $Size, $Size)
    $g2.Dispose()

    foreach ($t in $temp) { $t.Dispose() }
    return $out
}

# Serialise a bitmap as an ICO "BMP" frame: a BITMAPINFOHEADER followed by a
# bottom-up 32bpp BGRA bitmap and a 1bpp AND mask. biHeight is doubled because
# an icon frame stacks the XOR (colour) image and the AND (mask) image.
function Get-DibFrame {
    param([System.Drawing.Bitmap]$Bmp)

    [int]$w = $Bmp.Width
    [int]$h = $Bmp.Height

    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $locked = $Bmp.LockBits($rect,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $locked.Stride
    $pixels = [byte[]]::new($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    $Bmp.UnlockBits($locked)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    $bw.Write([UInt32]40)              # biSize
    $bw.Write([Int32]$w)               # biWidth
    $bw.Write([Int32]($h * 2))         # biHeight = XOR + AND
    $bw.Write([UInt16]1)               # biPlanes
    $bw.Write([UInt16]32)              # biBitCount
    $bw.Write([UInt32]0)               # biCompression = BI_RGB
    $bw.Write([UInt32]($w * $h * 4))   # biSizeImage
    $bw.Write([Int32]0)                # biXPelsPerMeter
    $bw.Write([Int32]0)                # biYPelsPerMeter
    $bw.Write([UInt32]0)               # biClrUsed
    $bw.Write([UInt32]0)               # biClrImportant

    # XOR image, bottom-up.
    for ($y = $h - 1; $y -ge 0; $y--) {
        $bw.Write($pixels, $y * $stride, $w * 4)
    }

    # AND mask, bottom-up, each row padded to a 4-byte boundary. A set bit means
    # "transparent"; with a 32bpp frame Windows uses the alpha channel instead,
    # but the mask must still be present and consistent.
    [int]$maskStride = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $mask = [byte[]]::new($maskStride * $h)
    for ($y = 0; $y -lt $h; $y++) {
        $srcRow = $y * $stride
        $dstRow = ($h - 1 - $y) * $maskStride
        for ($x = 0; $x -lt $w; $x++) {
            if ($pixels[$srcRow + $x * 4 + 3] -lt 128) {
                $mask[$dstRow + [int]($x / 8)] = $mask[$dstRow + [int]($x / 8)] -bor (0x80 -shr ($x % 8))
            }
        }
    }
    $bw.Write($mask)

    $bw.Flush()
    $out = $ms.ToArray()
    $bw.Dispose()
    $ms.Dispose()
    return , $out
}

if (-not (Test-Path -LiteralPath $Source)) { throw "source image not found: $Source" }

# Icons must be square. Centre-crop the long edge rather than padding, so the
# artwork's own rounded tile keeps filling the frame edge to edge.
$src = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source).Path)
$side = [Math]::Min($src.Width, $src.Height)
$ox = [int](($src.Width - $side) / 2)
$oy = [int](($src.Height - $side) / 2)

$square = New-Object System.Drawing.Bitmap($side, $side, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gs = [System.Drawing.Graphics]::FromImage($square)
Set-HighQuality $gs
$gs.DrawImage($src,
    (New-Object System.Drawing.Rectangle(0, 0, $side, $side)),
    (New-Object System.Drawing.Rectangle($ox, $oy, $side, $side)),
    [System.Drawing.GraphicsUnit]::Pixel)
$gs.Dispose()
$src.Dispose()

$blobs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap -Src $square -Size $s
    if ($pngSizes -contains $s) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $blobs += , $ms.ToArray()
        $ms.Dispose()
    } else {
        $blobs += , (Get-DibFrame -Bmp $bmp)
    }
    $bmp.Dispose()
}
$square.Dispose()

# ICONDIR (6 bytes) + one ICONDIRENTRY (16 bytes) per frame + the PNG payloads.
$outDir = Resolve-Path -LiteralPath (Split-Path -Parent $Output) -ErrorAction SilentlyContinue
if (-not $outDir) { throw "output directory not found: $(Split-Path -Parent $Output)" }

$fs = [System.IO.File]::Create($Output)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)                # reserved
$bw.Write([UInt16]1)                # type: 1 = icon
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }   # 0 encodes 256
    $bw.Write([Byte]$dim)           # width
    $bw.Write([Byte]$dim)           # height
    $bw.Write([Byte]0)              # palette count (0 = truecolour)
    $bw.Write([Byte]0)              # reserved
    $bw.Write([UInt16]1)            # colour planes
    $bw.Write([UInt16]32)           # bits per pixel
    $bw.Write([UInt32]$blobs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $bw.Write($b) }
$bw.Flush()
$bw.Dispose()
$fs.Dispose()

$fi = Get-Item -LiteralPath $Output
Write-Host ("wrote {0}  ({1} bytes, {2} frames)" -f $fi.FullName, $fi.Length, $sizes.Count)
