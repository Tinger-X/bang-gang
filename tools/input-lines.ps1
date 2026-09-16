# How many lines does the chat input box actually PAINT, and does the custom scrollbar
# show up at the right moment?
#
# The card sizes the text box as a multiple of one line's height and derives "lines the
# user can see" by dividing that height back down. Both halves are arithmetic; what the
# user sees is an EDIT that paints only the lines that fit WHOLE and scrolls the rest.
# So the two can disagree -- and did: the box was 3 * Font.Height = 66 while the EDIT
# lays lines out on a pitch of tmHeight = 23, so it painted 2 of the 3 lines it had room
# for, scrolled the text up, and still believed 3 lines were visible (hence: no bar).
# That is the reported "only two lines, and the bottom row is left empty".
#
# This probe types 1..5 lines and, for each, counts the ink bands the EDIT actually puts
# on screen (from a screen grab, so it is the real paint) next to the line count and the
# scrollbar's presence. tools/edit-lines.ps1 is the same question asked of a bare WinForms
# EDIT with no app in the loop; this one is the end-to-end version.
#
# Usage:  powershell -File tools\input-lines.ps1 [-Shot] [-Dump]
#   -Shot also saves the window after each step, so a failing band count can be looked at.
#   -Dump lists every Edit in the tree with its rect, visibility and text.
#
# Watch out when editing the typed strings: \r AND \n each break the line in a multiline
# EDIT, so "`r`n" between two chunks types TWO breaks -- five chunks came out as 9 lines
# (EM_GETLINECOUNT is what gave that away). One \n per break.

param([switch]$Shot, [switch]$Dump)

. "$PSScriptRoot\_ui.ps1"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ILE {
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
}
'@

$EM_GETLINECOUNT        = 0x00BA
$EM_GETFIRSTVISIBLELINE = 0x00CE

$script:fail = 0

# Rows of the crop that carry ink, grouped into bands -- i.e. the lines the user can see.
# Background is the crop's top-left pixel: nothing is drawn flush into that corner (the
# first line's cap starts ~9px down), and the caret sits at the END of the text.
function Get-BandCount([int]$x, [int]$y, [int]$w, [int]$h) {
    $bmp = Get-Crop $x $y $w $h
    $bg = $bmp.GetPixel(0, 0)
    $bands = New-Object System.Collections.Generic.List[string]
    $inBand = $false
    $start = -1
    for ($yy = 0; $yy -lt $h; $yy++) {
        $ink = $false
        for ($xx = 0; $xx -lt $w; $xx++) {
            $c = $bmp.GetPixel($xx, $yy)
            $d = [Math]::Abs($c.R - $bg.R) + [Math]::Abs($c.G - $bg.G) + [Math]::Abs($c.B - $bg.B)
            if ($d -gt 40) { $ink = $true; break }
        }
        if ($ink -and -not $inBand) { $inBand = $true; $start = $yy }
        elseif (-not $ink -and $inBand) { $inBand = $false; $bands.Add("$start.." + ($yy - 1)) }
    }
    if ($inBand) { $bands.Add("$start.." + ($h - 1)) }
    $bmp.Dispose()
    return @{ N = $bands.Count; List = ($bands -join ' | ') }
}

# The custom scrollbar is painted by the panel in the card's right padding, just outside
# the EDIT: x in [edit.Right + 4, edit.Right + 10). Nothing else is drawn there, so
# "pixels that are not the strip's most common colour" is the bar.
function Get-BarPixels([int]$x, [int]$y, [int]$w, [int]$h) {
    $bmp = Get-Crop $x $y $w $h
    $hist = @{}
    for ($yy = 0; $yy -lt $h; $yy++) {
        for ($xx = 0; $xx -lt $w; $xx++) {
            $c = $bmp.GetPixel($xx, $yy)
            $k = ($c.R * 65536) + ($c.G * 256) + $c.B
            if ($hist.ContainsKey($k)) { $hist[$k]++ } else { $hist[$k] = 1 }
        }
    }
    $bgKey = -1; $bgN = -1
    foreach ($k in $hist.Keys) { if ($hist[$k] -gt $bgN) { $bgN = $hist[$k]; $bgKey = $k } }
    $br = [Math]::Floor($bgKey / 65536); $bgn = $bgKey - ($br * 65536)
    $bgG = [Math]::Floor($bgn / 256); $bgB = $bgn - ($bgG * 256)
    $n = 0
    for ($yy = 0; $yy -lt $h; $yy++) {
        for ($xx = 0; $xx -lt $w; $xx++) {
            $c = $bmp.GetPixel($xx, $yy)
            $d = [Math]::Abs($c.R - $br) + [Math]::Abs($c.G - $bgG) + [Math]::Abs($c.B - $bgB)
            if ($d -gt 24) { $n++ }
        }
    }
    $bmp.Dispose()
    return $n
}

Invoke-BBProbe {
    $main = Start-BangGang

    # a conversation, so the chat input card exists at all
    $pill = Get-SearchPill $main
    if ($null -eq $pill) { throw 'search pill not found' }
    $pb = Get-PillButtons $main $pill
    if ($null -eq $pb) { throw 'pill buttons not found' }
    Invoke-MouseClick ($pb[1].Left + 14) ($pb[1].Top + 14)
    Start-Sleep -Milliseconds 1500

    # the multiline EDIT: the only Edit taller than a single line
    $edit = [IntPtr]::Zero
    foreach ($h in Get-WinKids $main) {
        if ((Get-WinClass $h) -notlike '*EDIT*') { continue }
        $r = Get-WinRect $h
        if (($r.Bottom - $r.Top) -lt 40) { continue }
        $edit = $h
        break
    }
    if ($edit -eq [IntPtr]::Zero) { throw 'input text box not found' }

    $er = Get-WinRect $edit
    $ew = $er.Right - $er.Left
    $eh = $er.Bottom - $er.Top
    Write-Output ("input box " + $er.Left + "," + $er.Top + " ${ew}x${eh}   font height " + (Get-LineHeight $edit))
    Write-Output ''

    # One click to focus it and put the caret in; the caret goes to the END as we type,
    # so every later measurement has its last line carrying text (a caret parked on an
    # empty line would be ink of its own and read as another band).
    #
    # Do NOT "helpfully" send EM_SETSEL here to park the caret. EM_SETSEL is 0x00B1;
    # 0x000B is WM_SETREDRAW, and wParam = 0 turns the control's painting OFF -- the box
    # then reports the text (WM_GETTEXT still answers) while the screen stays blank, which
    # reads exactly like "the app is not painting the text it has".
    Invoke-MouseClick ([int]($er.Left + 40)) ([int]($er.Top + 12))
    Start-Sleep -Milliseconds 300

    $chunks = @('aaaa', "`nbbbb", "`ncccc", "`ndddd", "`neeee")
    $n = 0
    Write-Output '  lines   EDIT says   bands painted   first visible   bar px   verdict'
    foreach ($c in $chunks) {
        $n++
        [BB]::TypeUnicode($c)
        Start-Sleep -Milliseconds 450

        $er = Get-WinRect $edit          # re-read: the box can move with the card
        $ew = $er.Right - $er.Left
        $eh = $er.Bottom - $er.Top
        $total = [int][ILE]::SendMessage($edit, $EM_GETLINECOUNT, [IntPtr]::Zero, [IntPtr]::Zero)
        $first = [int][ILE]::SendMessage($edit, $EM_GETFIRSTVISIBLELINE, [IntPtr]::Zero, [IntPtr]::Zero)
        $band = Get-BandCount $er.Left $er.Top $ew $eh
        $bar = Get-BarPixels ($er.Right + 4) $er.Top 6 $eh

        $wantBands = [Math]::Min($n, 3)
        $wantFirst = [Math]::Max(0, $n - 3)
        $wantBar = $n -gt 3
        $ok = ($band.N -eq $wantBands) -and ($first -eq $wantFirst) -and ($total -eq $n) -and
              (($bar -gt 12) -eq $wantBar)
        if (-not $ok) { $script:fail++ }

        $verdict = 'OK'
        if (-not $ok) {
            $why = @()
            if ($band.N -ne $wantBands) { $why += ("painted " + $band.N + " of " + $wantBands + " line(s)") }
            if ($first -ne $wantFirst) { $why += ("first visible " + $first + ", want " + $wantFirst) }
            if ($total -ne $n) { $why += ("EDIT counts " + $total + " lines, typed " + $n) }
            if ((($bar -gt 12)) -ne $wantBar) {
                if ($wantBar) { $why += 'no scrollbar' } else { $why += 'scrollbar showing early' }
            }
            $verdict = 'FAIL -- ' + ($why -join '; ')
            # The crop's own colours, so "no ink in an 884x69 box that says it has text"
            # can be told apart from "the probe grabbed the wrong rectangle".
            $bmp = Get-Crop $er.Left $er.Top $ew $eh
            $hist = @{}
            for ($yy = 0; $yy -lt [Math]::Min($eh, 20); $yy++) {
                for ($xx = 0; $xx -lt [Math]::Min($ew, 40); $xx++) {
                    $c = $bmp.GetPixel($xx, $yy)
                    $k = "$($c.R),$($c.G),$($c.B)"
                    if ($hist.ContainsKey($k)) { $hist[$k]++ } else { $hist[$k] = 1 }
                }
            }
            $bmp.Dispose()
            $top = ($hist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 4 |
                    ForEach-Object { $_.Key + " x" + $_.Value }) -join '  '
            Write-Output ("        top-left 40x20 colours: " + $top)
            Write-Output ("        edit rect " + $er.Left + "," + $er.Top + " ${ew}x${eh}   text = " +
                          (Get-WinText $edit).Replace("`n", '\n'))
        }
        if ($Shot) { [void](Save-WindowShot $main (Get-ShotPath ("input-lines-" + $n + ".png"))) }
        if ($Dump) { Write-WinTree $main }
        Write-Output ("  " + ([string]$n).PadRight(7) + " " + ([string]$total).PadRight(10) + " " +
                      (([string]$band.N) + "  [" + $band.List + "]").PadRight(24) + " " +
                      ([string]$first).PadRight(14) + " " + ([string]$bar).PadRight(8) + $verdict)
    }

    Write-Output ''
    if ($script:fail -eq 0) {
        Write-Output 'PASS: under three lines they all show; past three the newest three show and the bar appears.'
    }
    else { Write-Output "FAIL: $script:fail line count(s) wrong." }
}

Write-BBDone 'input-lines'
