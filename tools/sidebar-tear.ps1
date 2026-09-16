# After the sidebar collapses or expands, does the window still show pixels from the
# frames the animation went through?
#
# Why this exists: the input card is SELF-PAINTED inside InputPanel and its left edge
# follows the sidebar (ContentInset). While the animation runs, the children that get
# re-placed every frame only mark the strips they vacate as dirty, so the panel's
# WM_PAINT is clipped to those strips: the card is redrawn inside them and the strips
# that are NOT dirty keep the border it drew one frame earlier. One collapse leaves a
# nested trail of rounded corners down the card's left edge -- and it never goes away,
# because after the animation nothing invalidates those pixels again.
#
# Geometry probes cannot see any of this. sidebar-check.ps1 measures the sidebar width,
# sidebar-anim.ps1 measures the two buttons, and both are correct in every frame; the
# wrong thing is the PIXELS left over from an earlier frame. So measure pixels, with the
# same trick resize-lag.ps1 uses:
#
#   screen  : grab the window as it is
#   repaint : force a full repaint (RedrawWindow RDW_UPDATENOW), grab again
#   control : force another repaint, grab a third time
#
# screen-vs-repaint differing only means something when repaint-vs-repaint is identical;
# otherwise the renderer is simply not deterministic and the comparison proves nothing.
#
# The comparison is restricted to the input band (the bottom PanelH pixels): that is where
# the card is, and it keeps a full-window GetPixel sweep from costing seconds.
#
# Usage:  powershell -File tools\sidebar-tear.ps1

. "$PSScriptRoot\_ui.ps1"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BBT {
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h, IntPtr r, IntPtr hrgn, uint flags);
}
'@

$RDW = 0x0001 -bor 0x0004 -bor 0x0080 -bor 0x0100   # INVALIDATE|ERASE|ALLCHILDREN|UPDATENOW

# The sidebar panel: flush with the window's left edge, starting under the chrome bar,
# shorter than the main area (which now also spans the window and would otherwise match).
#
# A probe that measures pixels is worthless if the click that was supposed to start the
# animation missed -- "no stale pixels left" and "nothing happened" look identical. So the
# width is read before and after every click and asserted, not assumed.
function Get-SidebarRect($main) {
    $mr = Get-WinRect $main
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Left -ne $mr.Left) { continue }
        if ($r.Top -ne ($mr.Top + 38)) { continue }
        if (($r.Bottom - $r.Top) -lt 200) { continue }
        if (($r.Right - $r.Left) -gt 400) { continue }
        return $r
    }
    return $null
}

function Get-SidebarW($main) {
    $r = Get-SidebarRect $main
    if ($null -eq $r) { return 0 }
    return ($r.Right - $r.Left)
}

# The collapse/expand button: the only 28x28 control in the chat title strip, i.e. the 48px
# band under the chrome bar. Its x moves with the sidebar, so it must NOT be located by x --
# an x-based filter is also wide enough to latch onto the sidebar's own icon buttons, and a
# click that lands on one of those reads as "no tear" because nothing animated at all.
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

function Get-ShotBitmap([int]$x, [int]$y, [int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()
    return $bmp
}

# Bounding box of the pixels that differ, in the coordinates of the crop.
#
# X is read every 2px and Y every 4px, and the asymmetry is deliberate: every artefact
# this looks for is a VERTICAL stroke -- the card's rounded left edge, a bubble outline
# at the x its row used to sit at, the scroll bar's thumb down the right-hand column --
# because the animation moves things sideways. X is what has to be sampled tightly, and
# halving the Y samples is what keeps a full-window sweep from taking minutes.
function Compare-Bitmaps($a, $b, [int]$Step = 2, [int]$Tol = 10, [int]$StepY = 4) {
    $minX = -1; $maxX = -1; $minY = -1; $maxY = -1; $n = 0
    for ($x = 0; $x -lt $a.Width; $x += $Step) {
        for ($y = 0; $y -lt $a.Height; $y += $StepY) {
            $p = $a.GetPixel($x, $y); $q = $b.GetPixel($x, $y)
            $d = [Math]::Max([Math]::Abs($p.R - $q.R),
                 [Math]::Max([Math]::Abs($p.G - $q.G), [Math]::Abs($p.B - $q.B)))
            if ($d -le $Tol) { continue }
            $n++
            if ($minX -lt 0 -or $x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($minY -lt 0 -or $y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
    if ($n -eq 0) { return $null }
    return "$minX..$maxX x $minY..$maxY (n=$n)"
}

function Band-Of($bmp, [int]$x, [int]$y, [int]$w, [int]$h) {
    return $bmp.Clone((New-Object System.Drawing.Rectangle $x, $y, $w, $h), $bmp.PixelFormat)
}

# Bumps $script:fail rather than returning a count: Write-Output feeds the success stream,
# so a function that both prints and returns would hand back a string ARRAY.
#
# The three grabs are taken ONCE for the whole window and then cropped per band: grabbing
# is a screen blit plus a settle delay, and doing it per band would triple that for nothing.
function Test-AfterAnim($main, [string]$Tag, $Bands) {
    $r = Get-WinRect $main
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    Write-Output ("  " + $Tag.PadRight(12) + " sidebar width now " + (Get-SidebarW $main))

    $a = Get-ShotBitmap $r.Left $r.Top $w $h
    [void][BBT]::RedrawWindow($main, [IntPtr]::Zero, [IntPtr]::Zero, $RDW)
    Start-Sleep -Milliseconds 600
    $b = Get-ShotBitmap $r.Left $r.Top $w $h
    [void][BBT]::RedrawWindow($main, [IntPtr]::Zero, [IntPtr]::Zero, $RDW)
    Start-Sleep -Milliseconds 600
    $c = Get-ShotBitmap $r.Left $r.Top $w $h

    foreach ($band in $Bands) {
        $ca = Band-Of $a $band.X $band.Y $band.W $band.H
        $cb = Band-Of $b $band.X $band.Y $band.W $band.H
        $cc = Band-Of $c $band.X $band.Y $band.W $band.H
        $ab = Compare-Bitmaps $ca $cb $band.StepX 10 $band.StepY
        $bc = Compare-Bitmaps $cb $cc $band.StepX 10 $band.StepY
        $name = $Tag + '-' + $band.Name

        # Always keep the pair: when this passes but the picture is still wrong, the images
        # are the only way to see which of the two was wrong.
        $ca.Save((Get-ShotPath ("tear-" + $name + "-a.png")))
        $cb.Save((Get-ShotPath ("tear-" + $name + "-b.png")))

        if ($null -ne $bc) {
            Write-Output ("  " + $name.PadRight(20) + " SKIP -- renderer not deterministic, test says nothing")
        }
        elseif ($null -eq $ab) {
            Write-Output ("  " + $name.PadRight(20) + " OK -- no stale pixels left on screen")
        }
        else {
            Write-Output ("  " + $name.PadRight(20) + " FAIL -- screen keeps stale pixels " + $ab)
            $cb.Save((Get-ShotPath ("tear-" + $name + "-repaint.png")))
            $cc.Save((Get-ShotPath ("tear-" + $name + "-repaint2.png")))
            $script:fail++
        }
        $ca.Dispose(); $cb.Dispose(); $cc.Dispose()
    }
    $a.Dispose(); $b.Dispose(); $c.Dispose()
}

Invoke-BBProbe {
    $script:fail = 0
    $main = Start-BangGang
    $mr = Get-WinRect $main

    # A conversation, because the input card only exists once one is open
    $pill = Get-SearchPill $main
    if ($null -eq $pill) { throw 'search pill not found' }
    $pb = Get-PillButtons $main $pill
    if ($null -eq $pb) { throw 'sidebar pill buttons not found' }
    Invoke-MouseClick ($pb[1].Left + 14) ($pb[1].Top + 14)
    Start-Sleep -Milliseconds 1000

    if ($null -eq (Get-SideToggle $main)) { throw 'sidebar toggle not found' }

    # Two bands, because "the sidebar animation left a mark" has two different victims.
    #   chat   -- between the chat title strip (ends at 86 from the top) and the input
    #             panel (the bottom 158px). This is the band the user reported: the
    #             bubbles move sideways with the width every frame, and so does the
    #             scroll bar's thumb down its right edge.
    #   input  -- the self-painted card whose left edge follows the sidebar, which is
    #             where this probe started. Kept side by side with the chat band rather
    #             than instead of it: they are separate controls with separate paint
    #             paths, and a fix for one has no reason to cover the other.
    $winH = $mr.Bottom - $mr.Top
    $winW = $mr.Right - $mr.Left
    $bands = @(
        @{ Name = 'chat';  X = 0; Y = 86;           W = $winW; H = $winH - 86 - 158; StepX = 2; StepY = 4 },
        @{ Name = 'input'; X = 0; Y = $winH - 150;  W = $winW; H = 150;              StepX = 2; StepY = 2 }
    )

    # The toggle's rect does not survive the collapse -- collapsed it sits at the window's
    # left edge -- so look it up again before every click instead of caching it.
    Write-Output '--- collapse ---'
    $w0 = Get-SidebarW $main
    $tr = Get-SideToggle $main
    Invoke-MouseClick ($tr.Left + 14) ($tr.Top + 14)
    Start-Sleep -Milliseconds 900                 # let it settle; the tear is what persists
    $w1 = Get-SidebarW $main
    Write-Output ("  click      : sidebar " + $w0 + " -> " + $w1)
    if ($w1 -ne 0) { Write-Output '  click      : FAIL -- the click did not collapse the sidebar, the pixel test below proves nothing'; $script:fail++ }
    else { Test-AfterAnim $main 'collapse' $bands }

    Write-Output ''
    Write-Output '--- expand ---'
    $tr = Get-SideToggle $main
    Invoke-MouseClick ($tr.Left + 14) ($tr.Top + 14)
    Start-Sleep -Milliseconds 900
    $w2 = Get-SidebarW $main
    Write-Output ("  click      : sidebar " + $w1 + " -> " + $w2)
    if ($w2 -ne 256) { Write-Output '  click      : FAIL -- the click did not expand the sidebar, the pixel test below proves nothing'; $script:fail++ }
    else { Test-AfterAnim $main 'expand' $bands }

    Write-Output ''
    if ($script:fail -eq 0) { Write-Output 'PASS: the input card leaves no trail behind the sidebar animation.' }
    else { Write-Output "FAIL: $($script:fail) state(s) kept pixels from an animation frame." }
}

Write-BBDone 'sidebar-tear'
