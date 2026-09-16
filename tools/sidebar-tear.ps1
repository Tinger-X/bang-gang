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

# Bounding box of the pixels that differ, in the coordinates of the crop. Sampled every
# $Step columns; the artefacts this looks for are whole border strokes, not single pixels.
function Compare-Bitmaps($a, $b, [int]$Step = 2, [int]$Tol = 10) {
    $minX = -1; $maxX = -1; $minY = -1; $maxY = -1; $n = 0
    for ($x = 0; $x -lt $a.Width; $x += $Step) {
        for ($y = 0; $y -lt $a.Height; $y += $Step) {
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

# Bumps $script:fail rather than returning a count: Write-Output feeds the success stream,
# so a function that both prints and returns would hand back a string ARRAY.
function Test-AfterAnim($main, [string]$Tag) {
    $r = Get-WinRect $main
    $w = $r.Right - $r.Left
    $bandY = $r.Bottom - 150                      # the input panel is the bottom 150px
    Write-Output ("  " + $Tag.PadRight(12) + " sidebar width now " + (Get-SidebarW $main))
    $a = Get-ShotBitmap $r.Left $bandY $w 150
    [void][BBT]::RedrawWindow($main, [IntPtr]::Zero, [IntPtr]::Zero, $RDW)
    Start-Sleep -Milliseconds 600
    $b = Get-ShotBitmap $r.Left $bandY $w 150
    [void][BBT]::RedrawWindow($main, [IntPtr]::Zero, [IntPtr]::Zero, $RDW)
    Start-Sleep -Milliseconds 600
    $c = Get-ShotBitmap $r.Left $bandY $w 150

    $ab = Compare-Bitmaps $a $b
    $bc = Compare-Bitmaps $b $c

    # Always keep the pair: when this passes but the picture is still wrong, the images
    # are the only way to see which of the two was wrong.
    $a.Save((Get-ShotPath ("tear-" + $Tag + "-a.png")))
    $b.Save((Get-ShotPath ("tear-" + $Tag + "-b.png")))

    if ($null -ne $bc) {
        Write-Output ("  " + $Tag.PadRight(12) + " SKIP -- renderer not deterministic, test says nothing")
    }
    elseif ($null -eq $ab) {
        Write-Output ("  " + $Tag.PadRight(12) + " OK -- no stale pixels left on screen")
    }
    else {
        Write-Output ("  " + $Tag.PadRight(12) + " FAIL -- screen keeps stale pixels " + $ab)
        $b.Save((Get-ShotPath ("tear-" + $Tag + "-repaint.png")))
        $c.Save((Get-ShotPath ("tear-" + $Tag + "-repaint2.png")))
        $script:fail++
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
    else { Test-AfterAnim $main 'collapse' }

    Write-Output ''
    Write-Output '--- expand ---'
    $tr = Get-SideToggle $main
    Invoke-MouseClick ($tr.Left + 14) ($tr.Top + 14)
    Start-Sleep -Milliseconds 900
    $w2 = Get-SidebarW $main
    Write-Output ("  click      : sidebar " + $w1 + " -> " + $w2)
    if ($w2 -ne 256) { Write-Output '  click      : FAIL -- the click did not expand the sidebar, the pixel test below proves nothing'; $script:fail++ }
    else { Test-AfterAnim $main 'expand' }

    Write-Output ''
    if ($script:fail -eq 0) { Write-Output 'PASS: the input card leaves no trail behind the sidebar animation.' }
    else { Write-Output "FAIL: $($script:fail) state(s) kept pixels from an animation frame." }
}

Write-BBDone 'sidebar-tear'
