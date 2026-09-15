# Does the window repaint at its NEW size after being resized, or does it keep showing
# the layout from before the resize?
#
# Why this exists: WinForms registers each control's window class without
# CS_HREDRAW/CS_VREDRAW unless the control sets ControlStyles.ResizeRedraw (it is off by
# default). Without those class styles Windows repaints only the thin strip a resize
# newly exposes, so a control that draws its content positioned against its own Width --
# the welcome page centres everything on Width/Height -- keeps the pixels painted for the
# OLD size. Geometry probes (resize-check.ps1) cannot see this: the control's rect is
# already correct, only its pixels are stale. So measure pixels.
#
# Method, twice per state and content-independent:
#
#   settle  : screenshot the window as it is
#   repaint : force a full repaint with RedrawWindow(RDW_UPDATENOW), screenshot again
#   control : force ANOTHER repaint, screenshot once more
#
# screen-vs-repaint differing is only evidence when repaint-vs-repaint is identical --
# otherwise the renderer is simply not deterministic and the comparison means nothing.
#
# Usage:  powershell -File tools\resize-lag.ps1

. "$PSScriptRoot\_ui.ps1"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BBX {
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h, IntPtr r, IntPtr hrgn, uint flags);
}
'@

$RDW = 0x0001 -bor 0x0004 -bor 0x0080 -bor 0x0100   # INVALIDATE|ERASE|ALLCHILDREN|UPDATENOW

function Get-ShotBitmap([int]$x, [int]$y, [int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()
    return $bmp
}

# Bounding box of the pixels that differ between two same-sized bitmaps. $Step subsamples:
# a full-window GetPixel sweep in PowerShell is far too slow to do three times per state.
function Compare-Bitmaps($a, $b, [int]$Step = 3, [int]$Tol = 10) {
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

# Shrink the right edge, then grow it back past where it started. Both directions matter:
# growing exposes a strip Windows paints for free, shrinking exposes nothing at all and
# can only come out right if the control repaints itself.
function Invoke-ResizeCycle($main) {
    $r = Get-WinRect $main
    $y = $r.Top + 400
    Invoke-Drag ($r.Right - 2) $y ($r.Right - 202) $y
    Start-Sleep -Milliseconds 400
    Invoke-Drag ($r.Right - 202) $y ($r.Right + 198) $y
    Start-Sleep -Milliseconds 400
}

# Bumps $script:fail rather than returning a count: Write-Output feeds the success
# stream, so a function that both prints and returns would hand back a string ARRAY.
function Test-AfterResize($main, [string]$Tag) {
    $r = Get-WinRect $main
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    Write-Output ("  " + $Tag.PadRight(13) + " window ${w}x${h}")

    $a = Get-ShotBitmap $r.Left $r.Top $w $h
    [void][BBX]::RedrawWindow($main, [IntPtr]::Zero, [IntPtr]::Zero, $RDW)
    Start-Sleep -Milliseconds 700
    $b = Get-ShotBitmap $r.Left $r.Top $w $h
    [void][BBX]::RedrawWindow($main, [IntPtr]::Zero, [IntPtr]::Zero, $RDW)
    Start-Sleep -Milliseconds 700
    $c = Get-ShotBitmap $r.Left $r.Top $w $h

    $ab = Compare-Bitmaps $a $b
    $bc = Compare-Bitmaps $b $c
    $a.Dispose()

    if ($null -ne $bc) {
        Write-Output ("  " + $Tag.PadRight(13) + " SKIP -- renderer not deterministic, test says nothing")
    }
    elseif ($null -eq $ab) {
        Write-Output ("  " + $Tag.PadRight(13) + " OK -- screen matches a fresh repaint")
    }
    else {
        Write-Output ("  " + $Tag.PadRight(13) + " FAIL -- stale region " + $ab)
        $b.Save((Get-ShotPath ("resizelag-" + $Tag + "-stale.png")))
        $script:fail++
    }
    $b.Dispose(); $c.Dispose()
}

Invoke-BBProbe {
    $script:fail = 0
    $main = Start-BangGang

    # ---------------- state 1: the welcome page ----------------

    Write-Output '--- welcome page ---'
    Invoke-ResizeCycle $main
    Test-AfterResize $main 'welcome'

    # ---------------- state 2: a conversation is open ----------------

    Write-Output ''
    Write-Output '--- conversation open (chat view + input panel) ---'
    $pill = Get-SearchPill $main
    if ($null -eq $pill) { throw 'search pill not found' }
    $gear = Get-SettingsGear $main $pill
    if ($null -eq $gear) { throw 'settings gear not found' }
    Invoke-MouseClick ($gear.Left + 40 + 14) ($gear.Top + 14)     # the "new conversation" plus
    Start-Sleep -Milliseconds 1200
    Invoke-ResizeCycle $main
    Test-AfterResize $main 'chat'

    # ---------------- state 3: the settings overlay is open ----------------

    Write-Output ''
    Write-Output '--- settings overlay open ---'
    $pill = Get-SearchPill $main
    $gear = Get-SettingsGear $main $pill
    Invoke-MouseClick ($gear.Left + 14) ($gear.Top + 14)
    Start-Sleep -Milliseconds 1400
    Invoke-ResizeCycle $main
    Test-AfterResize $main 'settings'

    Write-Output ''
    if ($script:fail -eq 0) { Write-Output 'PASS: every state repaints at the new size after a resize.' }
    else { Write-Output "FAIL: $script:fail state(s) kept their pre-resize pixels." }
}

Write-BBDone 'resize-lag'
