# During the sidebar collapse/expand animation the chat panel is resized on every frame.
# The two icon buttons at the bottom-right of the chat view (paperclip + send) hang off
# the input panel's RIGHT edge, and the input panel's right edge is the window's right
# edge -- so whatever the sidebar does, those two should not move on screen at all.
#
# This measures two things per frame, because they can disagree:
#
#   geometry : GetWindowRect on the two buttons, reported as their distance from the
#              window's right edge. Constant = pinned.
#   pixels   : the icon ink inside a strip along the bottom-right, grabbed with
#              CopyFromScreen. This is what the user sees, and it is the only thing that
#              can show FLICKER -- a frame where the buttons are in the right place but
#              their ink is not on screen. Geometry can never see that.
#
# Plus the premise the fix rests on: the input panel itself must not move either. Moving a
# parent makes Windows blit its whole subtree at once, and WinForms only re-applies each
# descendant's own bounds afterwards -- so a parent that moves drags the pinned buttons
# with it for a moment. The panel's rect is therefore asserted to be identical in every
# frame, not just its children's.
#
# Interleaved over both directions of the animation, driven by a real click on the toggle.
#
# Usage:  powershell -File tools\sidebar-anim.ps1

. "$PSScriptRoot\_ui.ps1"

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
    //                      inkCount, cLeft, cTop, cRight, cBottom } where a/b are the two
    // icon buttons and c is the input panel they hang off.
    //
    // "ink" = a pixel differing from the strip's top-left pixel (the panel background) by more
    // than tol on any channel. In the light theme the icon buttons' own circle is ~24 units
    // off the panel colour while the glyph is far darker, so tol=15 catches the whole button
    // -- a bigger, steadier target than the thin glyph strokes.
    public static int[] Frame(IntPtr a, IntPtr b, IntPtr c, int x, int y, int w, int h, int tol) {
        RECT ra, rb, rc;
        GetWindowRect(a, out ra); GetWindowRect(b, out rb); GetWindowRect(c, out rc);
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
        return new int[] { ra.Left, ra.Top, rb.Left, rb.Top, minX, maxX, minY, maxY, n,
                           rc.Left, rc.Top, rc.Right, rc.Bottom };
    }
}
'@

$script:fail = 0

# The sidebar's own width. This is the one number that is SUPPOSED to change -- it is the
# witness that the animation actually ran, so that "nothing moved" cannot be confused with
# "nothing happened". Found by shape rather than by identity: flush with the window's left
# edge, starting under the chrome bar, running to the window's bottom, and far too narrow
# to be the main area (which now also covers the full window and would otherwise match).
function Get-SidebarW($main) {
    $mr = Get-WinRect $main
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Left -ne $mr.Left) { continue }
        if ($r.Top -ne ($mr.Top + 38)) { continue }
        if ($r.Bottom -ne $mr.Bottom) { continue }
        if (($r.Right - $r.Left) -gt 400) { continue }
        return ($r.Right - $r.Left)
    }
    return -1
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
        $f = [BBA]::Frame($script:sendH, $script:attachH, $script:inputH,
                          $script:sx, $script:sy, $script:swid, $script:shgt, 15)
        $rows.Add([pscustomobject]@{
            T     = $sw.ElapsedMilliseconds
            Side  = Get-SidebarW $main
            OffA  = $script:mrRight - $f[0]
            OffB  = $script:mrRight - $f[2]
            Panel = (($f[9] - $script:mrLeft).ToString() + "," + ($f[10] - $script:mrTop).ToString() + " " +
                     ($f[11] - $f[9]).ToString() + "x" + ($f[12] - $f[10]).ToString())
            Ink   = if ($f[8] -eq 0) { 'none' } else { "$($f[4])..$($f[5])" }
            N     = $f[8]
        })
    }

    Write-Output ''
    Write-Output ("--- " + $Tag + " ---")
    Write-Output ("  samples: " + $rows.Count + " over " + $sw.ElapsedMilliseconds + " ms")

    $sides = @($rows | ForEach-Object { $_.Side } | Sort-Object -Unique)
    Write-Output ("  sidebar width seen : " + (($sides | Select-Object -First 12) -join ', ') + $(if ($sides.Count -gt 12) { ' ...' }) + "  (" + $sides.Count + " distinct)")

    # the send button sits at Width-40 of the input panel, the paperclip at Width-78
    foreach ($pair in @(@('send', 'OffA', 40), @('paperclip', 'OffB', 78))) {
        $name = $pair[0]; $prop = $pair[1]; $want = $pair[2]
        $off = @($rows | ForEach-Object { $_.$prop } | Sort-Object -Unique)
        $ok = ($off.Count -eq 1) -and ($off[0] -eq $want)
        Write-Output ("  " + $name.PadRight(10) + ": window-right minus left = " + ($off -join ', ') + "  (want " + $want + ")   " + $(if ($ok) { 'OK -- pinned' } else { 'FAIL -- the button moved on screen' }))
        if (-not $ok) { $script:fail++ }
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

    # only interesting if something moved -- print the frames where it did
    $bad = @($rows | Where-Object { $_.OffA -ne 40 -or $_.OffB -ne 78 })
    foreach ($r in ($bad | Select-Object -First 10)) {
        Write-Output ("    t=" + $r.T + "ms  side=" + $r.Side + "  sendOff=" + $r.OffA + "  clipOff=" + $r.OffB + "  ink=" + $r.Ink)
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

    # the strip: from 260px left of the window's right edge up to 3px short of it (the
    # window frame paints its border down the last columns), sitting inside the window --
    # run it past the bottom edge and it captures the desktop instead of the app.
    $script:sx = $mr.Right - 260
    $script:sy = $sorted[1][1].Top - 4
    $script:swid = 257
    $script:shgt = ($mr.Bottom - 3) - $script:sy

    Write-Output ("window " + $mr.Left + "," + $mr.Top + " " + ($mr.Right - $mr.Left) + "x" + ($mr.Bottom - $mr.Top))
    Write-Output ("toggle " + $toggle.Left + "," + $toggle.Top + "   send " + $sorted[1][1].Left + "," + $sorted[1][1].Top + "   clip " + $sorted[0][1].Left + "," + $sorted[0][1].Top)
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
