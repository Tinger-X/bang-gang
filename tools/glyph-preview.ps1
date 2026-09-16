# glyph-preview.ps1 -- offline glyph design board.
#
# Draws candidate icon glyphs (paperclip / send / plus) at 4x zoom on a light
# and a dark swatch, so a glyph can be judged before it is wired into the app.
# Pure offline: does NOT start BangGang.  Must stay ASCII-only (PS 5.1 reads a
# BOM-less file as ANSI).
#
#   powershell -ExecutionPolicy Bypass -File tools\glyph-preview.ps1
#
# Output: shoots/glyph-preview.png

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repo 'shoots'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$cell = 150         # one preview cell, in px (glyph box == cell, 1:1)
$cols = 8

# ---------- glyph painters: each draws into a $box x $box box at 0,0 ----------

function New-RoundPen($c, $w) {
    $p = New-Object System.Drawing.Pen($c, $w)
    $p.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $p.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $p.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    return $p
}

# Paperclip: one wire, three 180-degree turns, long legs -- drawn in a rotated
# frame so every turn is a plain semicircle.
#
#   leg A (outer left)  free end low, up  -> top turn (rOut)  -> leg B (outer right)
#   leg B down -> bottom turn (rBot) -> leg C (inner left) up -> top turn (rIn)
#   -> leg D (inner right) down -> free end
#
# The two top turns nest: the inner one sits inside the outer one, which is what
# makes the shape read as a paperclip rather than a zigzag.
function Draw-Clip($g, $box, $ink, $lw, $rOut, $rBot, $rIn) {
    $s = $box / 2.0
    $g.TranslateTransform($s, $s)
    $g.RotateTransform(45)
    $g.ScaleTransform($s / 10.6, $s / 10.6)
    $pen = New-RoundPen $ink ($lw * 9.5 / $s)

    $xA = 0.0
    $xB = $xA + 2 * $rOut
    $xC = $xB - 2 * $rBot
    $xD = $xC + 2 * $rIn
    $yTopO = -4.2              # centre-line of the outer top turn
    $yBot  = 4.2               # centre-line of the bottom turn
    $yTopI = $yTopO + $rOut - $rIn   # inner top turn, nested under the outer one

    $g.DrawLine($pen, $xA, 9.0, $xA, $yTopO)                 # leg A
    $g.DrawArc($pen, $xA, $yTopO - $rOut, 2 * $rOut, 2 * $rOut, 180, 180)
    $g.DrawLine($pen, $xB, $yTopO, $xB, $yBot)               # leg B
    $g.DrawArc($pen, $xC, $yBot, 2 * $rBot, 2 * $rBot, 0, 180)
    $g.DrawLine($pen, $xC, $yBot, $xC, $yTopI)               # leg C
    $g.DrawArc($pen, $xC, $yTopI - $rIn, 2 * $rIn, 2 * $rIn, 180, 180)
    $g.DrawLine($pen, $xD, $yTopI, $xD, 5.0)                 # leg D

    $pen.Dispose()
    $g.ResetTransform()
}

# Attach as a plus (the Doubao shape).
function Draw-PlusIcon($g, $box, $ink, $lw) {
    $c = $box / 2.0; $r = $box * 0.30
    $pen = New-RoundPen $ink $lw
    $g.DrawLine($pen, $c, $c - $r, $c, $c + $r)
    $g.DrawLine($pen, $c - $r, $c, $c + $r, $c)
    $pen.Dispose()
}

# Send: filled disc + white arrow pointing up.
function Draw-SendDisc($g, $box, $ink, $lw, $body, $arrow) {
    $c = $box / 2.0; $r = $box * 0.46
    $b = New-Object System.Drawing.SolidBrush($body)
    $g.FillEllipse($b, $c - $r, $c - $r, 2 * $r, 2 * $r)
    $b.Dispose()
    Draw-ArrowUp $g $box $arrow ($lw * 0.85) 0.30
}

# Send: filled disc + white paper plane.
function Draw-SendPlane($g, $box, $ink, $lw, $body, $arrow) {
    $c = $box / 2.0; $r = $box * 0.46
    $b = New-Object System.Drawing.SolidBrush($body)
    $g.FillEllipse($b, $c - $r, $c - $r, 2 * $r, 2 * $r)
    $b.Dispose()
    $s = $box * 0.30
    $pts = @(
        (New-Object System.Drawing.PointF(($c), ($c - $s * 0.95))),
        (New-Object System.Drawing.PointF(($c + $s * 0.92), ($c + $s * 0.80))),
        (New-Object System.Drawing.PointF(($c), ($c + $s * 0.26))),
        (New-Object System.Drawing.PointF(($c - $s * 0.92), ($c + $s * 0.80)))
    )
    $b2 = New-Object System.Drawing.SolidBrush($arrow)
    $g.FillPolygon($b2, $pts)
    $b2.Dispose()
}

function Draw-ArrowUp($g, $box, $ink, $lw, $k) {
    $c = $box / 2.0; $s = $box * $k
    $pen = New-RoundPen $ink $lw
    $g.DrawLine($pen, $c, $c + $s, $c, $c - $s)                       # stem
    $g.DrawLine($pen, $c - $s * 0.72, $c - $s * 0.26, $c, $c - $s)    # head
    $g.DrawLine($pen, $c + $s * 0.72, $c - $s * 0.26, $c, $c - $s)
    $pen.Dispose()
}

# Send: outline disc + arrow (no fill) -- quieter rest state.
function Draw-SendRing($g, $box, $ink, $lw) {
    $c = $box / 2.0; $r = $box * 0.44
    $pen = New-RoundPen $ink $lw
    $g.DrawEllipse($pen, $c - $r, $c - $r, 2 * $r, 2 * $r)
    $pen.Dispose()
    Draw-ArrowUp $g $box $ink ($lw * 0.85) 0.28
}

# ---------- board ----------

$cells = @(
    @{ N = 'clip-A 5/3/2';  F = { param($g,$b,$ink,$lw) Draw-Clip $g $b $ink $lw 5.0 3.0 2.0 } }
    @{ N = 'clip-B 5/3.5/2.5'; F = { param($g,$b,$ink,$lw) Draw-Clip $g $b $ink $lw 5.0 3.5 2.5 } }
    @{ N = 'clip-C 5.5/3/2.5'; F = { param($g,$b,$ink,$lw) Draw-Clip $g $b $ink $lw 5.5 3.0 2.5 } }
    @{ N = 'clip-D 4.6/3/2.4'; F = { param($g,$b,$ink,$lw) Draw-Clip $g $b $ink $lw 4.6 3.0 2.4 } }
    @{ N = 'clip-E 6/4/3';  F = { param($g,$b,$ink,$lw) Draw-Clip $g $b $ink $lw 6.0 4.0 3.0 } }
    @{ N = 'clip-plus';     F = { param($g,$b,$ink,$lw) Draw-PlusIcon $g $b $ink $lw } }
    @{ N = 'send-ring';     F = { param($g,$b,$ink,$lw) Draw-SendRing $g $b $ink $lw } }
    @{ N = 'send-disc';     F = { param($g,$b,$ink,$lw) Draw-SendDisc $g $b $ink $lw $ink ([System.Drawing.Color]::White) } }
    @{ N = 'send-arrow';    F = { param($g,$b,$ink,$lw) Draw-ArrowUp $g $b $ink $lw 0.34 } }
    @{ N = 'send-off';      F = { param($g,$b,$ink,$lw) Draw-SendDisc $g $b $ink $lw ([System.Drawing.Color]::FromArgb(120,128,138)) ([System.Drawing.Color]::White) } }
    @{ N = 'send-plane';    F = { param($g,$b,$ink,$lw) Draw-SendPlane $g $b $ink $lw $ink ([System.Drawing.Color]::White) } }
    @{ N = 'clip-B dark';   F = { param($g,$b,$ink,$lw) Draw-Clip $g $b $ink $lw 5.0 3.5 2.5 } }
)

$rows = 2
$light = [System.Drawing.Color]::FromArgb(252, 253, 255)
$dark  = [System.Drawing.Color]::FromArgb(43, 47, 54)
$inkL  = [System.Drawing.Color]::FromArgb(80, 88, 98)
$inkD  = [System.Drawing.Color]::FromArgb(180, 188, 198)
$accent = [System.Drawing.Color]::FromArgb(47, 112, 224)

$W = $cols * $cell
$H = $rows * $cell
$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.Clear($light)

for ($i = 0; $i -lt $cells.Count; $i++) {
    $col = $i % $cols
    $row = [int][Math]::Floor($i / $cols)
    $x = $col * $cell
    $y = $row * $cell
    $back = if ($row -eq 0) { $light } else { $dark }
    $ink  = if ($row -eq 0) { $inkL } else { $inkD }

    $g.FillRectangle((New-Object System.Drawing.SolidBrush($back)), $x, $y, $cell, $cell)
    $g.DrawRectangle((New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(220, 225, 232))), $x, $y, $cell - 1, $cell - 1)

    # glyphs are authored for a 28px button; scale the stroke with the cell so
    # the preview shows the real weight.
    $lw = 1.8 * $cell / 28.0
    $st = $g.Save()
    $g.TranslateTransform($x, $y)
    & $cells[$i].F $g $cell $ink $lw
    $g.Restore($st)

    $f = New-Object System.Drawing.Font('Consolas', 10)
    $g.DrawString($cells[$i].N, $f, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(120, 128, 138))), $x + 4, $y + 4)
    $f.Dispose()
}
$g.Dispose()

$out = Join-Path $outDir 'glyph-preview.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved $out"
