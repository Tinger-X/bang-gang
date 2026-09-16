# The chat input panel's bottom row (hint text + attach/plus + send) lives INSIDE the panel,
# and the panel's TextBox is Dock = Fill. WinForms decides z-order by add order -- later
# added goes further back -- so adding the TextBox before those three makes the TextBox
# cover them. They are then invisible AND unclickable in normal use, and reappear only
# through the settings overlay's backdrop, which is composited with DrawToBitmap/WM_PRINT:
# a native EDIT's WM_PRINTCLIENT paints its text but not its background, so whatever is
# stacked behind it shows through. That is the "why are there two garbled icons in the
# bottom-right when settings is open" report.
#
# Two assertions:
#   A  at the centre of each bottom-row control, WindowFromPoint names that control --
#      i.e. nothing (in particular not the input Edit) is covering it
#   B  the window area outside the settings card is pixel-identical with the overlay open
#      and closed -- the backdrop must not reveal anything the live UI keeps hidden
#   C  hovering one of the buttons repaints it -- being on top is not enough, the mouse
#      message has to actually reach it
#
# Usage:  powershell -File tools\settings-over-chat.ps1

. "$PSScriptRoot\_ui.ps1"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SCO {
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
}
'@

$script:fail = 0

function Get-ShortClass($h) {
    $c = Get-WinClass $h
    if ($c.StartsWith('WindowsForms10.')) { $c = $c.Substring(0, $c.IndexOf('.app')) -replace '^WindowsForms10\.', '' }
    return $c
}

function Get-Desc($h) {
    $r = Get-WinRect $h
    return ((Get-ShortClass $h) + " " + $r.Left + "," + $r.Top + " " +
            ($r.Right - $r.Left) + "x" + ($r.Bottom - $r.Top))
}

# Every differing pixel between two same-sized bitmaps over the given box.
function Compare-Box($a, $b, [int]$x0, [int]$y0, [int]$x1, [int]$y1) {
    $n = 0; $first = ''
    for ($x = $x0; $x -lt $x1; $x++) {
        for ($y = $y0; $y -lt $y1; $y++) {
            $p = $a.GetPixel($x, $y); $q = $b.GetPixel($x, $y)
            $d = [Math]::Max([Math]::Abs($p.R - $q.R),
                 [Math]::Max([Math]::Abs($p.G - $q.G), [Math]::Abs($p.B - $q.B)))
            if ($d -le 4) { continue }
            $n++
            if ($first -eq '') { $first = "first at $x,$y (d=$d)" }
        }
    }
    if ($n -eq 0) { return $null }
    return "$n pixel(s), $first"
}

Invoke-BBProbe {
    $main = Start-BangGang
    $mr = Get-WinRect $main
    $ww = $mr.Right - $mr.Left
    $wh = $mr.Bottom - $mr.Top
    Write-Output ("window " + $mr.Left + "," + $mr.Top + " ${ww}x${wh}")

    $pill = Get-SearchPill $main
    if ($null -eq $pill) { throw 'search pill not found' }
    $gear = Get-SettingsGear $main $pill
    if ($null -eq $gear) { throw 'settings gear not found' }

    # open a conversation so the chat input panel exists
    Invoke-MouseClick ($gear.Left + 40 + 14) ($gear.Top + 14)
    Start-Sleep -Milliseconds 1500

    # ---- the bottom row: two 28x28 icon buttons and the wide hint label ----
    $band = $mr.Bottom - 50
    $row = @()
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Top -lt $band) { continue }
        $c = Get-ShortClass $h
        $w = $r.Right - $r.Left
        if ($c -eq 'Window.8' -and $w -eq 28) { $row += $h }
        elseif ($c -eq 'Static' -and $w -gt 100) { $row += $h }
    }

    Write-Output ''
    Write-Output '--- A. bottom-row controls reachable? ---'
    if ($row.Count -ne 3) {
        Write-Output ("  FAIL -- expected 3 bottom-row controls (hint + 2 buttons), found " + $row.Count)
        $script:fail++
    }
    foreach ($h in $row) {
        $r = Get-WinRect $h
        $pt = New-Object SCO+POINT
        $pt.X = [int](($r.Left + $r.Right) / 2)
        $pt.Y = [int](($r.Top + $r.Bottom) / 2)
        $hit = [SCO]::WindowFromPoint($pt)
        $d = Get-Desc $h
        if ($hit -eq $h) { Write-Output ("  OK   " + $d.PadRight(34) + " top at its centre") }
        else {
            Write-Output ("  FAIL " + $d.PadRight(34) + " covered by " + (Get-Desc $hit))
            $script:fail++
        }
    }

    # ---- C. does a button actually receive the mouse? Hovering must repaint it. ----
    Write-Output ''
    Write-Output '--- C. hover reaches the button ---'
    $btn = $null
    foreach ($h in $row) {
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -eq 28) { $btn = $h; break }
    }
    if ($null -eq $btn) {
        Write-Output '  FAIL -- no 28x28 button found in the bottom row'
        $script:fail++
    }
    else {
        $r = Get-WinRect $btn
        $bw = $r.Right - $r.Left; $bh = $r.Bottom - $r.Top
        [void][BB]::SetCursorPos(($mr.Left + 400), ($mr.Top + 620))   # a neutral spot first
        Start-Sleep -Milliseconds 400
        $off = New-Object System.Drawing.Bitmap $bw, $bh
        $og = [System.Drawing.Graphics]::FromImage($off)
        $og.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $bw, $bh))
        $og.Dispose()
        [void][BB]::SetCursorPos([int](($r.Left + $r.Right) / 2), [int](($r.Top + $r.Bottom) / 2))
        Start-Sleep -Milliseconds 400
        $on = New-Object System.Drawing.Bitmap $bw, $bh
        $ng = [System.Drawing.Graphics]::FromImage($on)
        $ng.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $bw, $bh))
        $ng.Dispose()
        $d = Compare-Box $off $on 0 0 $bw $bh
        if ($null -eq $d) {
            Write-Output ("  FAIL the button did not repaint on hover -- the mouse is not reaching it")
            $script:fail++
        }
        else { Write-Output ("  OK   repainted on hover (" + $d + ")") }
        $off.Dispose(); $on.Dispose()
    }

    # ---- live frame, then the same frame with the settings overlay open ----
    # Park the cursor somewhere neutral so neither capture is mid-hover.
    [void][BB]::SetCursorPos(($mr.Left + 400), ($mr.Top + 620))
    Start-Sleep -Milliseconds 300

    $live = New-Object System.Drawing.Bitmap $ww, $wh
    $lg = [System.Drawing.Graphics]::FromImage($live)
    $lg.CopyFromScreen($mr.Left, $mr.Top, 0, 0, (New-Object System.Drawing.Size $ww, $wh))
    $lg.Dispose()

    Invoke-MouseClick ($gear.Left + 14) ($gear.Top + 14)
    Start-Sleep -Milliseconds 1500

    $with = New-Object System.Drawing.Bitmap $ww, $wh
    $wg = [System.Drawing.Graphics]::FromImage($with)
    $wg.CopyFromScreen($mr.Left, $mr.Top, 0, 0, (New-Object System.Drawing.Size $ww, $wh))
    $wg.Dispose()

    # compare only OUTSIDE the settings card, so the card itself is not part of the test
    $cardX = [int](($ww - 880) / 2); $cardY = [int](($wh - 640) / 2)
    $x0 = $cardX + 880 + 6; $x1 = $ww - 2
    $y0 = $cardY + 640 + 6; $y1 = $wh - 2
    Write-Output ''
    Write-Output ('--- B. backdrop outside the card matches the live frame ---')
    Write-Output ("  box " + $x0 + "," + $y0 + " .. " + $x1 + "," + $y1)
    $diff = Compare-Box $live $with $x0 $y0 $x1 $y1
    if ($null -eq $diff) { Write-Output '  OK   identical' }
    else {
        Write-Output ("  FAIL -- " + $diff)
        $with.Save((Get-ShotPath 'sc-backdrop-mismatch.png'))
        $script:fail++
    }
    $live.Dispose(); $with.Dispose()

    Write-Output ''
    if ($script:fail -eq 0) { Write-Output 'PASS: input bottom row is on top and the settings backdrop hides nothing.' }
    else { Write-Output "FAIL: $script:fail check(s) failed." }
}

Write-BBDone 'settings-over-chat'
