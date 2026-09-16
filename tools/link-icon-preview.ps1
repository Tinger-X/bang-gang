# link-icon-preview.ps1 -- design board for the "open the vendor guide" glyph.
#
# The current icon is the textbook external-link mark (a squared box with a gap in
# its top-right corner, plus a 45-degree arrow leaving through it). The complaint is
# that it has no beauty, and the board exists because that is not something you can
# reason your way to: the shape has to be looked at, at the size it is really drawn
# and inside the circle it really sits in.
#
# So every candidate is rendered the way IconButton draws it -- 28px button, a
# Tinted circle in the real theme colours, the glyph in the real accent-derived ink
# -- and then blown up 6x with the stroke width scaled alongside, which is the only
# way a 1.8px stroke can be judged.
#
# Pure offline: does NOT start BangGang.  Must stay ASCII-only (PS 5.1 reads a
# BOM-less file as ANSI).
#
#   powershell -ExecutionPolicy Bypass -File tools\link-icon-preview.ps1
#
# Output: shoots/link-icon-preview.png

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repo 'shoots'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# ---------- palette: the exact numbers Theme/SC produce ----------

function MixC($a, $b, $k) {
    $r = [int][Math]::Round($a.R + ($b.R - $a.R) * $k)
    $g = [int][Math]::Round($a.G + ($b.G - $a.G) * $k)
    $bl = [int][Math]::Round($a.B + ($b.B - $a.B) * $k)
    return [System.Drawing.Color]::FromArgb($r, $g, $bl)
}

$accent = [System.Drawing.Color]::FromArgb(47, 112, 224)
$lp = [System.Drawing.Color]::FromArgb(252, 253, 255)
$ls = [System.Drawing.Color]::FromArgb(246, 248, 251)
$dp = [System.Drawing.Color]::FromArgb(34, 37, 43)
$ds = [System.Drawing.Color]::FromArgb(29, 32, 37)

$lightBg = MixC $lp $ls 0.55
$darkBg  = MixC $dp $ds 0.55

# Tinted skin, resting state (no hover): circle = mix(BackColor, Accent, 0.13),
# ink = mix(Accent, TextMain, 0.28).
$lightCircle = MixC $lightBg $accent 0.13
$darkCircle  = MixC $darkBg  $accent 0.13
$lightInk = MixC $accent ([System.Drawing.Color]::FromArgb(30, 34, 40)) 0.28
$darkInk  = MixC $accent ([System.Drawing.Color]::FromArgb(232, 235, 239)) 0.28

# ---------- drawing helpers (all centred on the origin; s = half the glyph box) ----------

function New-Pen($c, $w) {
    $p = New-Object System.Drawing.Pen($c, $w)
    $p.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $p.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $p.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    return $p
}

# A rounded rectangle whose top-right corner is left open -- the opening the arrow
# leaves through.  gapTop / gapRight are measured from the corner inwards.
#
# Drawn as three arcs plus four lines rather than one GraphicsPath: each piece then
# gets the pen's round caps, so the two ends at the opening come out soft instead of
# being sliced off square, and the tangent joins at the corners stay invisible.
function Draw-OpenBox($g, $pen, $x, $y, $w, $h, $r, $gapTop, $gapRight) {
    $g.DrawLine($pen, ($x + $r), $y, ($x + $w - $gapTop), $y)                    # top edge
    $g.DrawLine($pen, ($x + $w), ($y + $gapRight), ($x + $w), ($y + $h - $r))    # right edge
    $g.DrawArc($pen, ($x + $w - 2 * $r), ($y + $h - 2 * $r), (2 * $r), (2 * $r), 0, 90)
    $g.DrawLine($pen, ($x + $w - $r), ($y + $h), ($x + $r), ($y + $h))           # bottom edge
    $g.DrawArc($pen, $x, ($y + $h - 2 * $r), (2 * $r), (2 * $r), 90, 90)
    $g.DrawLine($pen, $x, ($y + $h - $r), $x, ($y + $r))                         # left edge
    $g.DrawArc($pen, $x, $y, (2 * $r), (2 * $r), 180, 90)
}

# The 45-degree arrow: a shaft plus a head whose two barbs are horizontal and
# vertical, which is what makes it read as "up and to the right" rather than as a
# generic tick.
function Draw-ArrowNE($g, $pen, $tx, $ty, $px, $py, $head) {
    $g.DrawLine($pen, $tx, $ty, $px, $py)
    $g.DrawLine($pen, ($px - $head), $py, $px, $py)
    $g.DrawLine($pen, $px, $py, $px, ($py + $head))
}

# The icon from shoots/open.svg.
#
# The source is a FILLED 1024-unit SVG, but it is uniform in width: the frame wall and
# the arrow shaft are both 90 units thick, and every free end is a semicircle of radius
# 45.  A shape like that is exactly what a round-capped pen of the same width draws, so
# it needs no filled GraphicsPath at all -- which matters, because then it goes through
# Gfx.DrawGlyph like every other glyph and inherits the app's ink, caps and joins.
#
# Every number is in the source's own units, with the origin at the centre of its 1024
# viewBox; $u converts one unit to device pixels.  Read straight off the path data:
#
#   the frame   a rounded square of centreline half-width 355 and corner radius 105,
#               broken between the top edge and the right edge
#   the break   the top edge stops 185 units right of centre, the right edge 185 below
#   the arrow   two arms meeting at the frame's missing corner (355,-355) and stopping
#               55 units short of the centre lines, plus a shaft from that corner to the
#               centre -- so the shaft is the long gesture and the arms are the head
function Draw-OpenSvg($g, $s, $k, $ink, $bg, $tone) {
    $u = $s * $k / 512.0
    $e = 355 * $u
    $r = 105 * $u
    $ring = 185 * $u
    $arm = 55 * $u
    $ne = -$e
    $nring = -$ring
    $narm = -$arm

    # The frame.  tone > 0 mixes it a step back toward the disc so the arrow can carry
    # the full ink -- the same hierarchy the Tinted skin already sets up, applied
    # inside the glyph instead of only between glyph and disc.
    $col = $ink
    if ($tone -gt 0) { $col = MixC $ink $bg $tone }
    $fp = New-Pen $col (90 * $u)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddLine($e, $ring, $e, ($e - $r))
    $p.AddArc(($e - 2 * $r), ($e - 2 * $r), (2 * $r), (2 * $r), 0, 90)
    $p.AddLine(($e - $r), $e, ($ne + $r), $e)
    $p.AddArc($ne, ($e - 2 * $r), (2 * $r), (2 * $r), 90, 90)
    $p.AddLine($ne, ($e - $r), $ne, ($ne + $r))
    $p.AddArc($ne, $ne, (2 * $r), (2 * $r), 180, 90)
    $p.AddLine(($ne + $r), $ne, $nring, $ne)
    $g.DrawPath($fp, $p)
    $p.Dispose()
    $fp.Dispose()

    # The arrow.  Two figures, not one polyline: run as a single figure the path would
    # double back on itself at the corner, and a 180-degree reversal is a degenerate
    # join.  Separate figures simply overlap there instead.
    $ap = New-Pen $ink (90 * $u)
    $q = New-Object System.Drawing.Drawing2D.GraphicsPath
    $q.AddLine(0, 0, $e, $ne)
    $q.StartFigure()
    $q.AddLine($arm, $ne, $e, $ne)
    $q.AddLine($e, $ne, $e, $narm)
    $g.DrawPath($ap, $q)
    $q.Dispose()
    $ap.Dispose()
}

# ---------- candidates ----------
# Each takes ($g, $s, $ink, $bg) and draws centred on the origin.

$cands = @()

# 01 -- the icon as it stands today, for comparison.
$cands += @{ N = '01 current'; F = {
    param($g, $s, $ink, $bg)
    $pen = New-Pen $ink (1.6 * $s / 10.0)
    $g.DrawLine($pen, -0.92 * $s, -0.28 * $s, -0.92 * $s, 0.92 * $s)
    $g.DrawLine($pen, -0.92 * $s, 0.92 * $s, 0.28 * $s, 0.92 * $s)
    $g.DrawLine($pen, 0.28 * $s, 0.92 * $s, 0.28 * $s, 0.24 * $s)
    $g.DrawLine($pen, -0.92 * $s, -0.28 * $s, -0.34 * $s, -0.28 * $s)
    $g.DrawLine($pen, -0.06 * $s, 0.06 * $s, 0.92 * $s, -0.92 * $s)
    $g.DrawLine($pen, 0.28 * $s, -0.92 * $s, 0.92 * $s, -0.92 * $s)
    $g.DrawLine($pen, 0.92 * $s, -0.92 * $s, 0.92 * $s, -0.28 * $s)
    $pen.Dispose()
} }

# 02 -- round 1's winner, kept as the reference the refinements are judged against.
$cands += @{ N = '02 r3.0'; F = {
    param($g, $s, $ink, $bg)
    $pen = New-Pen $ink (1.6 * $s / 10.0)
    Draw-OpenBox $g $pen (-0.92 * $s) (-0.28 * $s) (1.20 * $s) (1.20 * $s) (0.30 * $s) (0.62 * $s) (0.52 * $s)
    Draw-ArrowNE $g $pen (-0.06 * $s) (0.06 * $s) (0.92 * $s) (-0.92 * $s) (0.64 * $s)
    $pen.Dispose()
} }

# The icon from shoots/open.svg at several scales and two colour treatments.
#
#   k     overall scale.  1.0 is the source's own proportions, which leave the ink
#         spanning 800 of the 1024 viewBox -- i.e. it fills 78% of the glyph box.
#   tone  0 = one colour throughout; otherwise the frame is mixed this far toward the
#         disc, leaving the arrow to carry the full ink.
function Make-Svg($k, $tone) {
    return {
        param($g, $s, $ink, $bg)
        Draw-OpenSvg $g $s $k $ink $bg $tone
    }.GetNewClosure()
}

$cands += @{ N = '03 svg k1.00'; F = (Make-Svg 1.00 0) }
$cands += @{ N = '04 svg k1.10'; F = (Make-Svg 1.10 0) }
$cands += @{ N = '05 svg k1.20'; F = (Make-Svg 1.20 0) }
$cands += @{ N = '06 k1.10 tone.45'; F = (Make-Svg 1.10 0.45) }
$cands += @{ N = '07 k1.20 tone.45'; F = (Make-Svg 1.20 0.45) }
$cands += @{ N = '08 k1.20 tone.30'; F = (Make-Svg 1.20 0.30) }

# ---------- board ----------

$cell = 200
$cols = 4
$scale = 6
$btn = 28
$rowH = 300
$bigY = 96          # the 6x render sits well clear of the 1:1 one -- overlapping them
                    # makes the real-size button impossible to look at

$rows = [int][Math]::Ceiling($cands.Count / $cols)
$tw = $cols * $cell
$th = $rows * $rowH

$bmp = New-Object System.Drawing.Bitmap($tw, ($th * 2))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$g.Clear([System.Drawing.Color]::FromArgb(120, 122, 128))

$font = New-Object System.Drawing.Font('Consolas', 9)
$fontBig = New-Object System.Drawing.Font('Consolas', 12)

# One 28px button, drawn the way IconButton does it: a filled circle, then the
# glyph on a 20px box (the 4px inset), everything scaled off $sz.
function Draw-Button($g, $ox, $oy, $sz, $circle, $ink, $fn) {
    $b = New-Object System.Drawing.SolidBrush($circle)
    $g.FillEllipse($b, $ox, $oy, $sz, $sz)
    $b.Dispose()
    $st = $g.Save()
    $g.TranslateTransform(($ox + $sz / 2.0), ($oy + $sz / 2.0))
    $k = $sz / 28.0
    & $fn $g (10.0 * $k) $ink $circle
    $g.Restore($st)
}

for ($half = 0; $half -lt 2; $half++) {
    $back  = if ($half -eq 0) { $lightBg } else { $darkBg }
    $circ  = if ($half -eq 0) { $lightCircle } else { $darkCircle }
    $ink   = if ($half -eq 0) { $lightInk } else { $darkInk }
    $label = if ($half -eq 0) { 'LIGHT   GroupBg ' + $lightBg.R + ',' + $lightBg.G + ',' + $lightBg.B + '   circle ' + $lightCircle.R + ',' + $lightCircle.G + ',' + $lightCircle.B }
                            else { 'DARK    GroupBg ' + $darkBg.R + ',' + $darkBg.G + ',' + $darkBg.B + '   circle ' + $darkCircle.R + ',' + $darkCircle.G + ',' + $darkCircle.B }

    $g.FillRectangle((New-Object System.Drawing.SolidBrush($back)), 0, ($half * $th), $tw, $th)
    $g.DrawString($label, $fontBig, (New-Object System.Drawing.SolidBrush($ink)), 10, ($half * $th + 6))
    $g.DrawLine((New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 200, 204))),
                0, ($half * $th + 30), $tw, ($half * $th + 30))

    # Contact strip: every candidate at the real 28px, side by side, so one crop of
    # this row shows all of them at the size the user will actually see.  The 6x
    # cells below are for shape; this row is the one that decides.
    for ($i = 0; $i -lt $cands.Count; $i++) {
        Draw-Button $g (10 + $i * 48) ($half * $th + 38) $btn $circ $ink $cands[$i].F
    }

    for ($i = 0; $i -lt $cands.Count; $i++) {
        $col = $i % $cols
        $row = [int][Math]::Floor($i / $cols)
        $x = $col * $cell
        $y = $half * $th + 76 + $row * $rowH

        $g.DrawString($cands[$i].N, $font, (New-Object System.Drawing.SolidBrush($ink)), ($x + 12), ($y + 40))
        Draw-Button $g ($x + 12) ($y + 58) $btn $circ $ink $cands[$i].F
        Draw-Button $g ($x + 14) ($y + $bigY) ($btn * $scale) $circ $ink $cands[$i].F
    }
}
$g.Dispose()

$out = Join-Path $outDir 'link-icon-preview.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved $out"
