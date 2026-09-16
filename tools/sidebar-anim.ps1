# During the sidebar collapse/expand animation the chat view is resized on every frame.
# The two icon buttons in the input card's bottom row sit at opposite ends of that card,
# so they are pinned to two DIFFERENT things and need two different assertions:
#
#   send      -- pinned to the CARD's right edge, which is the window's right edge minus a
#                fixed margin. It must not move on screen at all, in any frame. Exact.
#   attach    -- pinned to the CARD's left edge, and the card's left edge follows the
#                sidebar (InputPanel.ContentInset). It is SUPPOSED to travel, so asserting
#                a fixed position would fail on correct behaviour.
#
# This measures two things per frame, because they can disagree:
#
#   geometry : GetWindowRect on the two buttons. Constant = pinned.
#   pixels   : the icon ink inside a strip along the bottom-right, grabbed with
#              CopyFromScreen. This is what the user sees, and it is the only thing that
#              can show FLICKER -- a frame where a button is in the right place but its
#              ink is not on screen. Geometry can never see that.
#
# Plus the premise the fix rests on: the input panel itself must not move either. Moving a
# parent makes Windows blit its whole subtree at once, and WinForms only re-applies each
# descendant's own bounds afterwards -- so a parent that moves drags its children with it
# for a moment. The panel's rect is therefore asserted to be identical in every frame, not
# just its children's.
#
# Interleaved over both directions of the animation, driven by a real click on the toggle.
#
# Usage:  powershell -File tools\sidebar-anim.ps1 [-Trace]
#   -Trace prints every sampled frame, which is how the checks below were calibrated.

param([switch]$Trace)

. "$PSScriptRoot\_ui.ps1"

$script:trace = $Trace

# -ReferencedAssemblies is required: PS 5.1's default compiler reference set does not
# include System.Drawing, so "using System.Drawing.Imaging" fails to compile without it.
Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class BBA {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    // One frame. Returns { aLeft, aTop, bLeft, bTop, inkMinX, inkMaxX, inkMinY, inkMaxY,
    //                      inkCount, cLeft, cTop, cRight, cBottom, dLeft, dTop, dRight, dBottom,
    //                      hInkMinX, hInkMaxX, hInkMinY, hInkMaxY, hInkCount,
    //                      hLeft, hRight, dLeft2, dRight2 }
    // where a/b are the two icon buttons, c is the input panel they hang off, d is the
    // sidebar, and h* is the hint text in the middle of the same row.
    //
    // The sidebar's rect is read here, in the same call, rather than by a separate helper:
    // it is the reference the buttons are laid out against, and reading it one message later
    // samples a different instant of a running animation -- the two then disagree by a whole
    // animation step and every frame looks like a layout violation.
    //
    // The crops below take a few ms each, so the rects at the top are OLDER than the pixels
    // by roughly one animation step. That is why the sidebar is read a SECOND time after the
    // crops (d2): pairing a pixel with the rect read before it shows a fixed lag on every
    // frame, which is easy to mistake for the app moving things in the wrong order.
    //
    // h* rect (hLeft/hRight) is returned alongside the ink so the two can be compared per
    // frame: the ink is what the user sees, the rect is what the layout asked for, and a
    // disagreement between them is a repaint that did not keep up.
    //
    // "ink" = a pixel differing from the strip's top-left pixel (the panel background) by more
    // than tol on any channel. In the light theme the icon buttons' own circle is ~24 units
    // off the panel colour while the glyph is far darker, so tol=15 catches the whole button
    // -- a bigger, steadier target than the thin glyph strokes.
    //
    // The hint text gets its own crop at a FIXED place on the window, chosen to be wide
    // enough for the text at both ends of the animation and to stop short of both buttons.
    // Cropping its own rect instead would read the rect and the pixels at two different
    // instants: a label that moves 11px between those two calls lands the crop 11px off and
    // banks whatever is over there -- which reads as a second copy of the text.
    public static int[] Frame(IntPtr a, IntPtr b, IntPtr c, IntPtr side, IntPtr hint,
                              int x, int y, int w, int h,
                              int hx, int hy, int hw, int hh, int tol) {
        RECT ra, rb, rc, rd, rh, rd2;
        GetWindowRect(a, out ra); GetWindowRect(b, out rb);
        GetWindowRect(c, out rc); GetWindowRect(side, out rd);
        GetWindowRect(hint, out rh);
        int minX = -1, maxX = -1, minY = -1, maxY = -1, n = 0;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
            var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try {
                byte[] buf = new byte[d.Stride * h];
                Marshal.Copy(d.Scan0, buf, 0, buf.Length);
                int r0 = buf[2], g0 = buf[1], b0 = buf[0];
                for (int yy = 0; yy < h; yy++) {
                    int row = yy * d.Stride;
                    for (int xx = 0; xx < w; xx++) {
                        int i = row + xx * 4;
                        if (Math.Abs(buf[i + 2] - r0) <= tol &&
                            Math.Abs(buf[i + 1] - g0) <= tol &&
                            Math.Abs(buf[i] - b0) <= tol) continue;
                        n++;
                        if (minX < 0 || xx < minX) minX = xx;
                        if (xx > maxX) maxX = xx;
                        if (minY < 0 || yy < minY) minY = yy;
                        if (yy > maxY) maxY = yy;
                    }
                }
            } finally { bmp.UnlockBits(d); }
        }

        int hMinX = -1, hMaxX = -1, hMinY = -1, hMaxY = -1, hn = 0;
        // ...and the ink's intensity-weighted centroid. The bbox above is a threshold
        // crossing, so a glyph whose leftmost column sits right on the tolerance can move
        // the bbox by a pixel without anything moving on screen (ClearType spreads a stem
        // over neighbouring subpixels). The centroid is a continuous average of the same
        // pixels, so it moves only when the text does -- which is what "smooth" means.
        double hSum = 0, hSumX = 0;
        bool[] colInk = new bool[hw];
        using (var bmp = new Bitmap(hw, hh, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(hx, hy, 0, 0, new Size(hw, hh));
            var d = bmp.LockBits(new Rectangle(0, 0, hw, hh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try {
                byte[] buf = new byte[d.Stride * hh];
                Marshal.Copy(d.Scan0, buf, 0, buf.Length);
                int r0 = buf[2], g0 = buf[1], b0 = buf[0];
                for (int yy = 0; yy < hh; yy++) {
                    int row = yy * d.Stride;
                    for (int xx = 0; xx < hw; xx++) {
                        int i = row + xx * 4;
                        int dr = Math.Abs(buf[i + 2] - r0), dg = Math.Abs(buf[i + 1] - g0),
                            db = Math.Abs(buf[i] - b0);
                        int wk = Math.Max(dr, Math.Max(dg, db));
                        if (wk > tol) {
                            hn++;
                            if (hMinX < 0 || xx < hMinX) hMinX = xx;
                            if (xx > hMaxX) hMaxX = xx;
                            if (hMinY < 0 || yy < hMinY) hMinY = yy;
                            if (yy > hMaxY) hMaxY = yy;
                            hSum += wk; hSumX += (double)wk * xx;
                            colInk[xx] = true;
                        }
                    }
                }
            } finally { bmp.UnlockBits(d); }
        }
        // scaled by 1000 so the caller can keep working in ints; whoever reads it divides.
        int hCen = hSum > 0 ? (int)Math.Round(hSumX * 1000.0 / hSum) : -1;
        int hCols = 0;
        for (int i = 0; i < hw; i++) if (colInk[i]) hCols++;

        GetWindowRect(side, out rd2);   // again, after the crops -- see the header

        return new int[] { ra.Left, ra.Top, rb.Left, rb.Top, minX, maxX, minY, maxY, n,
                           rc.Left, rc.Top, rc.Right, rc.Bottom, rd.Left, rd.Top, rd.Right, rd.Bottom,
                           hMinX, hMaxX, hMinY, hMaxY, hn,
                           rh.Left, rh.Right, rd2.Left, rd2.Right, hCen, hCols };
    }
}
'@

$script:fail = 0

# Baselines captured from the idle run (see Measure-Run), not written down here. It is pure
# card geometry, so restyling the input panel moves it -- and a hardcoded value reports that
# as a regression when nothing regressed.
$script:baseOffA = $null   # send: window right edge minus its left edge

# The sidebar, found by shape rather than by identity: flush with the window's left edge,
# starting under the chrome bar, running to the window's bottom, and far too narrow to be the
# main area (which now also covers the full window and would otherwise match). Returns the
# HWND; its width is read per-frame inside BBA.Frame, never here.
function Get-Sidebar($main) {
    $mr = Get-WinRect $main
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Left -ne $mr.Left) { continue }
        if ($r.Top -ne ($mr.Top + 38)) { continue }
        if ($r.Bottom -ne $mr.Bottom) { continue }
        if (($r.Right - $r.Left) -gt 400) { continue }
        return $h
    }
    return [IntPtr]::Zero
}

# The collapse/expand button: the only 28x28 control sitting in the chat title strip, i.e.
# below the chrome row and above the end of that 48px strip. Its x moves with the sidebar,
# which is exactly why it must not be located by x.
function Get-SideToggle($main) {
    $mr = Get-WinRect $main
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -ne 28 -or ($r.Bottom - $r.Top) -ne 28) { continue }
        if ($r.Top -le ($mr.Top + 38)) { continue }
        if ($r.Top -ge ($mr.Top + 38 + 48)) { continue }
        return $r
    }
    return $null
}

function Measure-Run($main, [string]$Tag) {
    $rows = New-Object System.Collections.Generic.List[object]
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 800) {
        $f = [BBA]::Frame($script:sendH, $script:attachH, $script:inputH, $script:sidebarH, $script:hintH,
                          $script:sx, $script:sy, $script:swid, $script:shgt,
                          $script:hx, $script:hy, $script:hwid, $script:hhgt, 15)
        # $f is 26 long: 0..8 buttons+ink, 9..12 the input panel, 13..16 the sidebar before
        # the crops, 17..21 the ink inside the fixed hint strip, 22..23 the hint's own rect,
        # 24..25 the sidebar again after the crops.
        $side = $f[15] - $f[13]
        $rows.Add([pscustomobject]@{
            T     = $sw.ElapsedMilliseconds
            Side  = $side
            Side2 = $f[25] - $f[24]
            OffA  = $script:mrRight - $f[0]
            AttL  = $f[2] - $script:mrLeft
            Panel = (($f[9] - $script:mrLeft).ToString() + "," + ($f[10] - $script:mrTop).ToString() + " " +
                     ($f[11] - $f[9]).ToString() + "x" + ($f[12] - $f[10]).ToString())
            Ink   = if ($f[8] -eq 0) { 'none' } else { "$($f[4])..$($f[5])" }
            N     = $f[8]
            # where the hint's glyphs are, in window coordinates, and how wide the ink is --
            # two copies of the text on screen at once would widen it, which a rect
            # measurement could never see
            HintX = if ($f[21] -eq 0) { -1 } else { ($script:hx - $script:mrLeft) + $f[17] }
            HintR = if ($f[21] -eq 0) { -1 } else { ($script:hx - $script:mrLeft) + $f[18] }
            HintN = $f[21]
            # ...and where the layout put the label itself, which is the same quantity
            # measured without the paint path in between
            BoxL  = $f[22] - $script:mrLeft
            BoxR  = $f[23] - $script:mrLeft
            # the glyph centroid, in window coordinates and in thousandths of a pixel
            Cen   = if ($f[26] -lt 0) { -1 } else { ($script:hx - $script:mrLeft) * 1000 + $f[26] }
            # how many x columns carry ink: fewer than the bbox is wide means the ink is not
            # one contiguous block -- which is what a partly-erased old copy looks like
            Cols  = $f[27]
        })
    }

    Write-Output ''
    Write-Output ("--- " + $Tag + " ---")
    Write-Output ("  samples: " + $rows.Count + " over " + $sw.ElapsedMilliseconds + " ms")

    $sides = @($rows | ForEach-Object { $_.Side } | Sort-Object -Unique)
    Write-Output ("  sidebar width seen : " + (($sides | Select-Object -First 12) -join ', ') + $(if ($sides.Count -gt 12) { ' ...' }) + "  (" + $sides.Count + " distinct)")

    # The first call is the idle one -- nothing is animating, so it is the definition
    # of "correct" for the rest of the run. Read the expected offsets off it.
    if ($null -eq $script:baseOffA) {
        $script:baseOffA = @($rows | ForEach-Object { $_.OffA } | Select-Object -First 1)[0]
        Write-Output ("  baseline           : send sits " + $script:baseOffA +
                      " px from the window's right edge (taken from this idle run, not hardcoded)")
    }

    # The two buttons are pinned to two different things, so they need two different checks.
    #
    #   send -- pinned to the CARD's right edge, which is the window's right edge minus a
    #           fixed margin. It must not move at all, in any frame. This is what the 0.7.27
    #           flicker was about, and it is exact.
    $off = @($rows | ForEach-Object { $_.OffA } | Sort-Object -Unique)
    $ok = ($off.Count -eq 1) -and ($off[0] -eq $script:baseOffA)
    Write-Output ("  send      : window-right minus its left = " + ($off -join ', ') + "  (want " + $script:baseOffA + ")   " + $(if ($ok) { 'OK -- pinned' } else { 'FAIL -- the button moved on screen' }))
    if (-not $ok) { $script:fail++ }

    #   attach -- pinned to the CARD's left edge, which follows the sidebar, so it is
    #           SUPPOSED to travel and a fixed-offset assertion would fail on correct
    #           behaviour (see the header).
    #
    # An exact offset relative to the sidebar is not measurable from out here either: the app
    # moves both in one ApplyLayout pass, but reading two foreign windows costs two kernel
    # calls, so a sample can land between them and see a torn frame -- sidebar already moved,
    # button not yet. That reads as attLeft lagging side by exactly one animation step.
    #
    # What is both meaningful and tear-proof is the SIGN. The attach button must always travel
    # the same way the sidebar does, never against it; a torn frame can only ever show lag,
    # never opposite-signed motion. Jitter -- the 0.7.27 symptom -- is exactly a reversal.
    $rev = New-Object System.Collections.Generic.List[object]
    for ($i = 1; $i -lt $rows.Count; $i++) {
        $ds = $rows[$i].Side - $rows[$i - 1].Side
        $dc = $rows[$i].AttL - $rows[$i - 1].AttL
        if ($ds -ne 0 -and $dc -ne 0 -and [Math]::Sign($ds) -ne [Math]::Sign($dc)) {
            $rev.Add($rows[$i])
        }
    }
    Write-Output ("  attach    : frames moving AGAINST the sidebar = " + $rev.Count + "   " +
                  $(if ($rev.Count -eq 0) { 'OK -- travels with it, no reversal' } else { 'FAIL -- it bounced back mid-animation' }))
    if ($rev.Count -gt 0) {
        $script:fail++
        foreach ($r in ($rev | Select-Object -First 8)) {
            Write-Output ("    t=" + $r.T + "ms  side=" + $r.Side + "  attLeft=" + $r.AttL)
        }
    }

    # the premise: the panel these two hang off must be absolutely still
    $panels = @($rows | ForEach-Object { $_.Panel } | Sort-Object -Unique)
    $pok = ($panels.Count -eq 1)
    Write-Output ("  input panel        : " + ($panels -join ' | ') + "   " + $(if ($pok) { 'OK -- never moved' } else { 'FAIL -- the panel moved and dragged its children' }))
    if (-not $pok) { $script:fail++ }

    $inks = @($rows | ForEach-Object { $_.Ink } | Where-Object { $_ -ne 'none' } | Sort-Object -Unique)
    $none = @($rows | Where-Object { $_.Ink -eq 'none' }).Count
    Write-Output ("  icon ink x-range   : " + ($inks -join ', ') + "   (" + $inks.Count + " distinct)")
    if ($inks.Count -gt 1) { $script:fail++ }
    Write-Output ("  frames with no ink : " + $none + "   " + $(if ($none -eq 0) { 'OK' } else { 'FAIL -- the icons blinked out' }))
    if ($none -ne 0) { $script:fail++ }

    $ns = @($rows | ForEach-Object { $_.N } | Sort-Object)
    $med = $ns[[int]($ns.Count / 2)]
    $thin = @($rows | Where-Object { $_.N -lt $med * 0.8 })
    Write-Output ("  ink pixels/frame   : min " + $ns[0] + ", median " + $med + ", max " + $ns[$ns.Count - 1] + "   (frames below 80% of median: " + $thin.Count + ")")
    if ($thin.Count -gt 0) {
        $script:fail++
        foreach ($r in ($thin | Select-Object -First 8)) {
            Write-Output ("    t=" + $r.T + "ms  side=" + $r.Side + "  ink=" + $r.Ink + "  n=" + $r.N)
        }
    }

    # ---- the hint text in the middle of the row ----
    #
    # It is centred on the card, so it is SUPPOSED to travel with the card -- at half the
    # speed of the card's left edge. Two things must not happen, and they are different
    # failures:
    #
    #   shake : a frame where it moves the other way. This is what the user reported as
    #           "左右抖动，不是平滑过渡", and the cause was that the label was RESIZED on
    #           every frame of the animation (its width followed the card), so its centred
    #           content had to be rebuilt and repainted each tick -- while the window move
    #           is a separate path with its own queue. Frames then alternate between "text
    #           re-centred for the new width" and "text still where the old width put it".
    #   smear : one frame drawing it in two places (ink wider than the text).
    #
    # Measured off the CENTROID of the glyph ink, not off its bounding box. The bbox is a
    # threshold crossing, so a glyph whose leftmost column sits right on the tolerance can
    # move the bbox by a pixel with nothing moving on screen; it reports reversals on a
    # correct build (and did, until this was switched). The centroid is a continuous average
    # of the same pixels, so it moves only when the text does.
    #
    # And it is compared against ITSELF over time, never against the sidebar width: the
    # pixel stream is a genuine time series (each screen grab is atomic), whereas ANY
    # pairing of a pixel with a window rect is torn. The app sets the sidebar's bounds and
    # the hint's bounds at two different moments of the same layout pass, so a sample can
    # legitimately catch the sidebar already moved and the label not yet -- that reads as a
    # whole animation step of disagreement and looks exactly like a layout violation.
    $hrows = @($rows | Where-Object { $_.HintX -ge 0 })
    if ($hrows.Count -lt $rows.Count) {
        Write-Output ("  hint      : " + ($rows.Count - $hrows.Count) + " frame(s) with no hint ink   FAIL -- the text blinked out")
        $script:fail++
    }
    else {
        # A frame that paints the glyphs twice would widen the ink box; the text itself is
        # one fixed string, so its width is a constant of this run whatever the animation does.
        $hw = @($hrows | ForEach-Object { $_.HintR - $_.HintX } | Sort-Object -Unique)
        $hok = ($hw.Count -eq 1)
        Write-Output ("  hint      : glyph ink width = " + ($hw -join ', ') + "   " + $(if ($hok) { 'OK -- drawn once, same width every frame' } else { 'FAIL -- the text is being painted more than once' }))
        if (-not $hok) {
            $script:fail++
            foreach ($r in (@($hrows | Where-Object { ($_.HintR - $_.HintX) -ne $hw[0] }) | Select-Object -First 8)) {
                $span = $r.HintR - $r.HintX + 1
                $gap = if ($r.Cols -lt $span) { 'GAP -- ' + ($span - $r.Cols) + ' empty column(s) inside the ink box' } else { 'solid' }
                Write-Output ("    t=" + $r.T + "ms  side=" + $r.Side + "  hintX=" + $r.HintX + "  w=" + ($r.HintR - $r.HintX) +
                              "  cols=" + $r.Cols + "  " + $gap)
            }
        }
        $hx = @($hrows | ForEach-Object { $_.HintX } | Sort-Object -Unique)
        Write-Output ("  hint      : x-range " + ($hx[0]) + ".." + ($hx[$hx.Count - 1]) + " over " + $hx.Count + " distinct position(s)")

        # The centroid is a continuous measure of where the glyphs are, so it is the one that
        # answers "does it move smoothly". The text travels one way and one way only: a sample
        # that steps back is a text that visibly shook, whatever the reason. The 30/1000 px
        # floor is far below anything visible and far above the rounding in the measure itself.
        $cen = @($hrows | ForEach-Object { $_.Cen })
        $dir = [Math]::Sign($cen[$cen.Count - 1] - $cen[0])
        $back = New-Object System.Collections.Generic.List[object]
        if ($dir -ne 0) {
            for ($i = 1; $i -lt $cen.Count; $i++) {
                if ([Math]::Sign($cen[$i] - $cen[$i - 1]) -eq -$dir -and
                    [Math]::Abs($cen[$i] - $cen[$i - 1]) -gt 30) { $back.Add($hrows[$i]) }
            }
        }
        Write-Output ("  hint      : frames stepping BACKWARD = " + $back.Count + "   " +
                      $(if ($back.Count -eq 0) { 'OK -- moves one way the whole way, no shake' } else { 'FAIL -- it shook' }))
        if ($back.Count -gt 0) {
            $script:fail++
            foreach ($r in ($back | Select-Object -First 8)) {
                Write-Output ("    t=" + $r.T + "ms  side=" + $r.Side + "  centroid=" +
                              ([Math]::Round($r.Cen / 1000.0, 2)) + "  hintX=" + $r.HintX)
            }
        }

        # Every distinct screen position it stopped at, and the step between them: a jump much
        # larger than its neighbours would be the text skipping, which the sign test alone
        # cannot see (a skip goes the right way).
        $steps = @()
        for ($i = 1; $i -lt $cen.Count; $i++) { $steps += [Math]::Round(($cen[$i] - $cen[$i - 1]) / 1000.0, 2) }
        Write-Output ("  hint      : per-frame centroid step min/max = " +
                      ($steps | Measure-Object -Minimum).Minimum + " / " + ($steps | Measure-Object -Maximum).Maximum +
                      " px   (frames: " + $cen.Count + ")")

        if ($script:trace) {
            Write-Output ("    t     side/2  boxL..boxR   inkX  boxCentre/inkCentre/centroid")
            foreach ($r in $hrows) {
                $bc = ($r.BoxL + $r.BoxR) / 2
                $ic = $r.HintX + ($r.HintR - $r.HintX) / 2
                Write-Output ("    " + ([string]$r.T).PadRight(5) + " " +
                              (([string]$r.Side) + "/" + ([string]$r.Side2)).PadRight(9) + " " +
                              (([string]$r.BoxL) + ".." + ([string]$r.BoxR)).PadRight(11) + " " +
                              ([string]$r.HintX).PadRight(6) + " " + $bc + " / " + $ic + " / " +
                              ([string]([Math]::Round($r.Cen / 1000.0, 2))))
            }
        }
    }

    # only interesting if something moved -- print the frames where it did
    $bad = @($rows | Where-Object { $_.OffA -ne $script:baseOffA })
    foreach ($r in ($bad | Select-Object -First 10)) {
        Write-Output ("    t=" + $r.T + "ms  side=" + $r.Side + "  attLeft=" + $r.AttL +
                      "  sendOff=" + $r.OffA + "  ink=" + $r.Ink)
    }
}

Invoke-BBProbe {
    $main = Start-BangGang
    $mr = Get-WinRect $main
    $script:mrLeft = $mr.Left
    $script:mrTop = $mr.Top
    $script:mrRight = $mr.Right

    $pill = Get-SearchPill $main
    if ($null -eq $pill) { throw 'search pill not found' }
    $gear = Get-SettingsGear $main $pill
    if ($null -eq $gear) { throw 'settings gear not found' }

    # a conversation, so the chat view and its input panel are up
    Invoke-MouseClick ($gear.Left + 40 + 14) ($gear.Top + 14)
    Start-Sleep -Milliseconds 1500

    # the two bottom-row icon buttons: 28x28 in the band above the window's bottom edge
    $toggle = Get-SideToggle $main
    $btns = @()
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        $w = $r.Right - $r.Left
        if ($w -eq 28 -and ($r.Bottom - $r.Top) -eq 28 -and $r.Top -ge ($mr.Bottom - 50)) { $btns += ,@($h, $r) }
    }
    if ($null -eq $toggle) { throw 'sidebar toggle not found' }
    if ($btns.Count -ne 2) { throw ("expected 2 bottom-right icon buttons, found " + $btns.Count) }
    $sorted = @($btns | Sort-Object { $_[1].Left })
    $script:attachH = $sorted[0][0]; $script:sendH = $sorted[1][0]
    $script:inputH = [BB]::GetParent($script:sendH)
    $script:sidebarH = Get-Sidebar $main
    if ($script:sidebarH -eq [IntPtr]::Zero) { throw 'sidebar not found' }

    # the hint text in the middle of the same row, measured through a FIXED strip of the
    # window instead of through the label's own rect (see BBA.Frame). The strip's left edge
    # must clear the attach button at its furthest -- i.e. with the sidebar open, which is
    # where that button sits furthest right -- and its right edge must clear the send button,
    # which never moves. Both are asserted rather than assumed: the strip is only a valid
    # measurement of the hint while nothing else is inside it.
    $script:hintH = [IntPtr]::Zero
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Top -lt ($mr.Bottom - 50)) { continue }
        if ((Get-ShortClass $h) -ne 'Static') { continue }
        if (($r.Right - $r.Left) -le 100) { continue }
        $script:hintH = $h
    }
    if ($script:hintH -eq [IntPtr]::Zero) { throw 'bottom-row hint label not found' }
    $hr = Get-WinRect $script:hintH

    $script:hx = $mr.Left + 320                 # clears the attach button at its rightmost
    $script:hy = $hr.Top
    $script:hhgt = $hr.Bottom - $hr.Top
    $script:hwid = 680                          # ...and stops well short of the send button
    # attach is furthest right while the sidebar is OPEN, which is the state it was just
    # measured in; collapsing only ever moves it left, towards the window edge.
    $attRightMax = $sorted[0][1].Right - $mr.Left
    Write-Output ("hint strip " + $script:hx + "," + $script:hy + " " + $script:hwid + "x" + $script:hhgt +
                  "  (attach reaches " + $attRightMax + ", send starts " + ($sorted[1][1].Left - $mr.Left) + ")")
    if (($script:hx - $mr.Left) -le $attRightMax) { throw 'hint strip overlaps the attach button' }
    if (($script:hx + $script:hwid) -gt $sorted[1][1].Left) { throw 'hint strip overlaps the send button' }

    # the strip: from 260px left of the window's right edge up to 3px short of it (the
    # window frame paints its border down the last columns), sitting inside the window --
    # run it past the bottom edge and it captures the desktop instead of the app.
    $script:sx = $mr.Right - 260
    $script:sy = $sorted[1][1].Top - 4
    $script:swid = 257
    $script:shgt = ($mr.Bottom - 3) - $script:sy

    Write-Output ("window " + $mr.Left + "," + $mr.Top + " " + ($mr.Right - $mr.Left) + "x" + ($mr.Bottom - $mr.Top))
    Write-Output ("toggle " + $toggle.Left + "," + $toggle.Top + "   send " + $sorted[1][1].Left + "," + $sorted[1][1].Top + "   attach " + $sorted[0][1].Left + "," + $sorted[0][1].Top)
    Write-Output ("strip  " + $script:sx + "," + $script:sy + " " + $script:swid + "x" + $script:shgt)

    # a quiet baseline first: nothing is animating, so this is what "correct" looks like
    Measure-Run $main 'idle (no animation)'

    Invoke-MouseClick ($toggle.Left + 14) ($toggle.Top + 14)
    Measure-Run $main 'collapse'

    Start-Sleep -Milliseconds 300
    $toggle2 = Get-SideToggle $main
    if ($null -eq $toggle2) { throw 'sidebar toggle not found after collapsing' }
    Invoke-MouseClick ($toggle2.Left + 14) ($toggle2.Top + 14)
    Measure-Run $main 'expand'

    Write-Output ''
    if ($script:fail -eq 0) { Write-Output 'PASS: the bottom-right icons hold their position and never blink during the animation.' }
    else { Write-Output "FAIL: $script:fail check(s) failed." }
}

Write-BBDone 'sidebar-anim'
