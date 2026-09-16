# Attachment draft strip: does an added file show a chip row above the text box,
# does hovering that chip float a delete button in its TOP-RIGHT corner, does
# clicking it remove the attachment, and -- the regression this design exists to
# avoid -- does the text box keep its three lines while all that happens.
#
# ASCII ONLY (PS 5.1 reads a BOM-less .ps1 as ANSI; CJK literals are syntax errors).
#
# Two things make the pixel work here trustworthy rather than brittle:
#
#   * Every colour test is RELATIVE to the card fill, sampled from the 2px gap right
#     above the text box. The app ships two themes and the chip fill sits only ~15
#     levels off the card fill, so a hard-coded RGB would pass in one theme and fail
#     in the other.
#   * "Did the delete button appear?" is a DIFFERENCE between two snapshots of the
#     same window (mouse away vs mouse over the chip), never an absolute dark-pixel
#     count. The chip's own file-name text is near-black too, so an absolute count
#     would report the text as the button and pass while the button was missing.
#
# The strip's geometry is DERIVED from the text box: both are laid out from the same
# `card.Left + CardPadX` with the same width, and the strip ends 2px above the EDIT.
# Nothing here copies a constant out of InputPanel.cs.

. "$PSScriptRoot\_ui.ps1"

$script:fail = @()
$script:snap = $null
$script:snapOX = 0
$script:snapOY = 0
$script:cold = $null

function Check([string]$What, [bool]$Ok, [string]$Detail) {
    if ($Ok) { Write-Output ("OK   " + $What + " -- " + $Detail) }
    else { Write-Output ("FAIL " + $What + " -- " + $Detail); $script:fail += $What }
}

# One screen grab of the whole window, reused by every pixel test in a step. Reading
# pixel-by-pixel straight off the screen costs a fresh CopyFromScreen per pixel.
function Take-Snapshot($main) {
    if ($null -ne $script:snap) { $script:snap.Dispose() }
    $mr = Get-WinRect $main
    $script:snap = Get-Crop $mr.Left $mr.Top ($mr.Right - $mr.Left) ($mr.Bottom - $mr.Top)
    $script:snapOX = $mr.Left
    $script:snapOY = $mr.Top
}

function PxAt($bmp, [int]$x, [int]$y) {
    $px = $x - $script:snapOX
    $py = $y - $script:snapOY
    if ($px -lt 0 -or $py -lt 0 -or $px -ge $bmp.Width -or $py -ge $bmp.Height) {
        return [System.Drawing.Color]::White
    }
    return $bmp.GetPixel($px, $py)
}

function Get-Px([int]$x, [int]$y) { return PxAt $script:snap $x $y }

function Lum($c) { return 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B }

# "Is this pixel NOT the card's own fill?" -- the reference is sampled, not assumed.
function Differs($c, $bg) {
    return ([Math]::Abs($c.R - $bg.R) -gt 4 -or [Math]::Abs($c.G - $bg.G) -gt 4 -or [Math]::Abs($c.B - $bg.B) -gt 4)
}

function IsDark($c, [double]$Cut) { return (Lum $c) -lt $Cut }

# The input box: the only EDIT taller than 40px in the window (the sidebar's search
# pill is 32px, and it is not a multiline EDIT anyway).
function Get-InputEdit($main) {
    foreach ($h in Get-WinKids $main) {
        if ((Get-ShortClass $h) -ne 'Edit') { continue }
        $r = Get-WinRect $h
        if (($r.Bottom - $r.Top) -gt 40) { return @{ H = $h; R = $r } }
    }
    return $null
}

# Walk a column up from $Bottom and report the contiguous run of non-background rows.
function Get-BandAbove([int]$x, [int]$Bottom, $bg, [int]$MaxUp = 200) {
    $top = -1
    for ($y = $Bottom; $y -gt $Bottom - $MaxUp; $y--) {
        if (Differs (Get-Px $x $y) $bg) { $top = $y } else { break }
    }
    if ($top -lt 0) { return $null }
    return @{ Top = $top; Bottom = $Bottom; H = $Bottom - $top + 1 }
}

# Count the chips along a row through their middle: each chip is one contiguous run
# of non-background pixels, separated from the next by the layout gap.
function Get-ChipRuns([int]$x0, [int]$x1, [int]$y, $bg) {
    $runs = @()
    $start = -1
    for ($x = $x0; $x -le $x1; $x++) {
        $on = Differs (Get-Px $x $y) $bg
        if ($on -and $start -lt 0) { $start = $x }
        elseif (-not $on -and $start -ge 0) {
            if (($x - $start) -ge 40) { $runs += , @{ L = $start; R = $x - 1 } }
            $start = -1
        }
    }
    if ($start -ge 0 -and ($x1 - $start) -ge 40) { $runs += , @{ L = $start; R = $x1 } }
    return $runs
}

# The topmost row of a chip, read off its own fill at a column that clears the icon
# (the icon square is 32px wide, inset 6px from the chip's left edge).
function Get-ChipTop([int]$x, [int]$yMid, $bg) {
    for ($y = $yMid; $y -gt $yMid - 60; $y--) {
        if (-not (Differs (Get-Px $x $y) $bg)) { return $y + 1 }
    }
    return -1
}

# Pixels that are dark NOW and were not dark in $Other. This is what isolates the
# hover button from the chip's own text -- see the header note.
function Get-NewDark($Other, $bg, [int]$x0, [int]$y0, [int]$x1, [int]$y1) {
    $cut = (Lum $bg) - 60
    $minX = -1; $maxX = -1; $minY = -1; $maxY = -1; $n = 0
    for ($x = $x0; $x -le $x1; $x++) {
        for ($y = $y0; $y -le $y1; $y++) {
            if (-not (IsDark (Get-Px $x $y) $cut)) { continue }
            if (IsDark (PxAt $Other $x $y) $cut) { continue }   # was already dark: text
            $n++
            if ($minX -lt 0 -or $x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($minY -lt 0 -or $y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
    if ($n -eq 0) { return $null }
    return @{ X = $minX; Y = $minY; R = $maxX; B = $maxY; N = $n
              CX = [int](($minX + $maxX) / 2); CY = [int](($minY + $maxY) / 2) }
}

# Pixels inside a rect that are clearly NOT the given fill -- i.e. drawn ink (text).
function Count-Ink($R, $fill, [int]$Tol) {
    $n = 0
    for ($x = $R.Left; $x -le $R.Right; $x++) {
        for ($y = $R.Top; $y -le $R.Bottom; $y++) {
            $c = Get-Px $x $y
            if ([Math]::Abs($c.R - $fill.R) -gt $Tol -or [Math]::Abs($c.G - $fill.G) -gt $Tol `
                -or [Math]::Abs($c.B - $fill.B) -gt $Tol) { $n++ }
        }
    }
    return $n
}

# Rectangle's third and fourth arguments are WIDTH and HEIGHT, not the opposite corner.
# Handing it two corners builds a box of (x1, y1) pixels -- for a scan region that is a
# ~1274x772 rect, a million GetPixel calls, and minutes of crawling that looks exactly
# like a hang. Always build scan rects through this.
function Rect-Of([int]$x0, [int]$y0, [int]$x1, [int]$y1) {
    return (New-Object System.Drawing.Rectangle $x0, $y0, ($x1 - $x0), ($y1 - $y0))
}

# Drop a file list on the clipboard and let the app's own Ctrl+V path pick it up --
# the same route a user takes, through the same TryPasteClipboard code.
function Send-FilesPaste($edit, [string[]]$Paths) {
    $col = New-Object System.Collections.Specialized.StringCollection
    foreach ($p in $Paths) { [void]$col.Add($p) }
    $ok = $false
    for ($i = 0; $i -lt 5 -and -not $ok; $i++) {
        try { [System.Windows.Forms.Clipboard]::SetFileDropList($col); $ok = $true }
        catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ok) { throw 'could not put the file list on the clipboard' }
    Start-Sleep -Milliseconds 250

    $r = Get-WinRect $edit
    Invoke-MouseClick ([int](($r.Left + $r.Right) / 2)) ([int](($r.Top + $r.Bottom) / 2))
    Start-Sleep -Milliseconds 300
    # Ctrl+V through SendInput (see BB.Chord for why neither SendKeys entry point works
    # from a console host). Deliver to the focused window, hence the click above.
    [BB]::Chord(0x56)
    Start-Sleep -Milliseconds 700
}

# ---- fixtures. Under shoots\ (gitignored), never in the system temp. ----
$fixDir = Join-Path $script:BBShoots 'fixtures'
if (-not (Test-Path $fixDir)) { [void](New-Item -ItemType Directory -Path $fixDir -Force) }

$pngPath = Join-Path $fixDir 'pic.png'
$bmp = New-Object System.Drawing.Bitmap 160, 110
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(220, 90, 60))
$g.FillEllipse([System.Drawing.Brushes]::Gold, 30, 15, 100, 80)
$g.Dispose()
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$txtPath = Join-Path $fixDir 'notes.txt'
Set-Content -Path $txtPath -Value 'hello from draft-strip.ps1' -Encoding Ascii
$cppPath = Join-Path $fixDir 'widget.cpp'
Set-Content -Path $cppPath -Value 'int main() { return 0; }' -Encoding Ascii
$badPath = Join-Path $fixDir 'bad.zip'
[System.IO.File]::WriteAllBytes($badPath, [byte[]](1..64))

Invoke-BBProbe {
    $main = Start-BangGang 5
    $mr = Get-WinRect $main

    # ---- start a conversation from the sidebar's "+" so the input panel exists ----
    $pill = Get-SearchPill $main
    $pillBtns = Get-PillButtons $main $pill
    if ($null -eq $pillBtns) { throw 'sidebar pill buttons not found' }
    $newBtn = $pillBtns[1]
    Invoke-MouseClick ([int](($newBtn.Left + $newBtn.Right) / 2)) ([int](($newBtn.Top + $newBtn.Bottom) / 2))
    Start-Sleep -Milliseconds 900

    $e0 = Get-InputEdit $main
    if ($null -eq $e0) { throw 'input text box not found after starting a conversation' }
    $edit = $e0.H
    $r0 = $e0.R
    $h0 = $r0.Bottom - $r0.Top
    Write-Output ("input box: " + $r0.Left + "," + $r0.Top + "  " + ($r0.Right - $r0.Left) + "x" + $h0)

    # Park the mouse on the window's bottom-right so nothing is accidentally hovered.
    [void][BB]::SetCursorPos(($mr.Right - 8), ($mr.Bottom - 4))
    Start-Sleep -Milliseconds 400

    Take-Snapshot $main

    # The card's own fill, sampled from the 2px gap between the strip and the EDIT.
    $bg = Get-Px ($r0.Left + 44) ($r0.Top - 1)

    # ---- A. with nothing attached there must be no band above the box ----
    $band0 = Get-BandAbove ($r0.Left + 44) ($r0.Top - 3) $bg
    Check 'no attachment strip before anything is added' ($null -eq $band0) `
          ($(if ($null -eq $band0) { 'nothing but card fill above the box' } else { 'band of ' + $band0.H + 'px found' }))
    Save-WindowShot $main (Get-ShotPath 'draft-0-empty.png')

    # ---- B. paste one image, one .txt and one .cpp ----
    Send-FilesPaste $edit @($pngPath, $txtPath, $cppPath)
    $mr = Get-WinRect $main
    [void][BB]::SetCursorPos(($mr.Right - 8), ($mr.Bottom - 4))
    Start-Sleep -Milliseconds 400
    Take-Snapshot $main
    Save-WindowShot $main (Get-ShotPath 'draft-1-three.png')

    $r1 = Get-WinRect $edit
    $h1 = $r1.Bottom - $r1.Top
    Check 'text box keeps its height when attachments appear' ($h1 -eq $h0) `
          ('was ' + $h0 + 'px, now ' + $h1 + 'px')
    # The whole point of growing the panel instead of enlarging the card: the text
    # area keeps its three lines and the strip is paid for ABOVE the card. If the
    # panel had stayed 150px the box would have been squeezed to one line, and the
    # check above would say so -- this one pins down which way the room came from.
    Check 'the room for the strip came from above, not out of the text box' `
          ($r1.Top -lt $r0.Top) `
          ('text box top moved ' + ($r0.Top - $r1.Top) + 'px up (was y=' + $r0.Top + ', now y=' + $r1.Top + ')')

    $band1 = Get-BandAbove ($r1.Left + 44) ($r1.Top - 3) $bg
    $bandOk = ($null -ne $band1) -and ($band1.H -ge 45) -and ($band1.H -le 62)
    Check 'a chip row appeared directly above the text box' $bandOk `
          ($(if ($null -eq $band1) { 'no band found' } else { 'band ' + $band1.H + 'px tall, top y=' + $band1.Top }))
    if ($null -eq $band1) { throw 'no strip: the rest of the probe has nothing to measure' }

    $rowMid = $band1.Top + [int]($band1.H / 2)
    $runs = Get-ChipRuns $r1.Left ($r1.Right - 1) $rowMid $bg
    Check 'three chips drawn, one per attachment' ($runs.Count -eq 3) ($runs.Count.ToString() + ' run(s)')
    if ($runs.Count -lt 1) { throw 'no chips: cannot test the delete button' }

    $chip0 = $runs[0]
    $chipTop = Get-ChipTop ($chip0.L + 44) $rowMid $bg
    Write-Output ("chip 0: x " + $chip0.L + ".." + $chip0.R + ", top y=" + $chipTop + ", mid y=" + $rowMid)

    # ---- C. hover the first chip: a delete button must float at its TOP-RIGHT ----
    # $script:cold is the same window with nothing hovered; every "did the button
    # appear?" test below is a difference against it.
    $script:cold = $script:snap.Clone()

    $rx0 = $chip0.L + 2
    $ry0 = $band1.Top - 16
    $rx1 = $chip0.R + 16
    $ry1 = $band1.Bottom

    [void][BB]::SetCursorPos([int](($chip0.L + $chip0.R) / 2), ($chipTop + 20))
    Start-Sleep -Milliseconds 500
    Take-Snapshot $main
    Save-WindowShot $main (Get-ShotPath 'draft-2-hover.png')

    $hot = Get-NewDark $script:cold $bg $rx0 $ry0 $rx1 $ry1
    Check 'hovering a chip floats a delete button' ($null -ne $hot -and $hot.N -ge 100) `
          ($(if ($null -eq $hot) { 'nothing new appeared' } else { $hot.N.ToString() + ' new dark pixels' }))

    if ($null -ne $hot) {
        $dx = [Math]::Abs($hot.CX - $chip0.R)
        Check 'the delete button sits at the chip RIGHT edge' ($dx -le 5) `
              ('button centre x=' + $hot.CX + ', chip right=' + $chip0.R + ' (off by ' + $dx + ')')
        Check 'the delete button straddles the chip TOP edge' `
              ($chipTop -gt 0 -and [Math]::Abs($hot.CY - $chipTop) -le 5) `
              ('button centre y=' + $hot.CY + ', chip top=' + $chipTop)
        Check 'the button is drawn ABOVE the chip, not inside it' `
              ($chipTop -gt 0 -and $hot.Y -lt $chipTop) `
              ('button top y=' + $hot.Y + ' vs chip top=' + $chipTop)
        Check 'the button is a round badge, roughly one chip-icon across' `
              (($hot.R - $hot.X) -ge 14 -and ($hot.B - $hot.Y) -ge 14 -and ($hot.R - $hot.X) -le 26) `
              (($hot.R - $hot.X + 1).ToString() + 'x' + ($hot.B - $hot.Y + 1).ToString() + 'px')
    }

    # ---- D. leaving the strip takes the button away again ----
    $er = Get-WinRect $edit
    [void][BB]::SetCursorPos(($er.Left + 60), [int](($er.Top + $er.Bottom) / 2))
    Start-Sleep -Milliseconds 500
    Take-Snapshot $main
    $left = Get-NewDark $script:cold $bg $rx0 $ry0 $rx1 $ry1
    Check 'the delete button disappears when the mouse leaves' ($null -eq $left) `
          ($(if ($null -eq $left) { 'the chip row is back to its cold state' } else { $left.N.ToString() + ' pixels left behind' }))

    # ---- E. clicking that button removes exactly one attachment ----
    [void][BB]::SetCursorPos([int](($chip0.L + $chip0.R) / 2), ($chipTop + 20))
    Start-Sleep -Milliseconds 500
    Take-Snapshot $main
    $hot2 = Get-NewDark $script:cold $bg $rx0 $ry0 $rx1 $ry1
    if ($null -eq $hot2) { throw 'the delete button did not come back for the click test' }

    Invoke-MouseClick $hot2.CX $hot2.CY
    Start-Sleep -Milliseconds 700
    $mr = Get-WinRect $main
    [void][BB]::SetCursorPos(($mr.Right - 8), ($mr.Bottom - 4))
    Start-Sleep -Milliseconds 400
    Take-Snapshot $main
    Save-WindowShot $main (Get-ShotPath 'draft-3-removed.png')

    $r2 = Get-WinRect $edit
    $band2 = Get-BandAbove ($r2.Left + 44) ($r2.Top - 3) $bg
    if ($null -eq $band2) { throw 'the whole strip vanished: expected two chips left' }
    $rowMid2 = $band2.Top + [int]($band2.H / 2)
    $runs2 = Get-ChipRuns $r2.Left ($r2.Right - 1) $rowMid2 $bg
    Check 'clicking the delete button removes that chip' ($runs2.Count -eq 2) ($runs2.Count.ToString() + ' chip(s) left')
    Check 'the text box is untouched by the removal' (($r2.Bottom - $r2.Top) -eq $h0) `
          (($r2.Bottom - $r2.Top).ToString() + 'px vs ' + $h0 + 'px')
    Check 'the strip keeps its full height with attachments left' ($band2.H -eq $band1.H) `
          ($band2.H.ToString() + 'px vs ' + $band1.H + 'px')
    if ($runs2.Count -ge 1) {
        Check 'the remaining chips did not slide left' ([Math]::Abs($runs2[0].L - $chip0.L) -le 2) `
              ('first chip left=' + $runs2[0].L + ' vs ' + $chip0.L)
    }

    # ---- F. an unsupported file is refused, with a reason on the chrome bar ----
    Send-FilesPaste $edit @($badPath)
    Start-Sleep -Milliseconds 400
    $er2 = Get-WinRect $edit
    $band3 = Get-BandAbove ($er2.Left + 44) ($er2.Top - 3) $bg
    $rowMid3 = $band3.Top + [int]($band3.H / 2)
    $runs3 = Get-ChipRuns $er2.Left ($er2.Right - 1) $rowMid3 $bg
    Check 'a .zip is not added to the strip' ($runs3.Count -eq $runs2.Count) `
          ($runs2.Count.ToString() + ' -> ' + $runs3.Count.ToString())
    Check 'the refused file did not shrink the text box either' (($er2.Bottom - $er2.Top) -eq $h0) `
          (($er2.Bottom - $er2.Top).ToString() + 'px vs ' + $h0 + 'px')

    $status = Get-ChromeStatus $main
    $txt = ''
    if ($status -ne [IntPtr]::Zero) { $txt = Get-WinText $status }
    Check 'the refusal names the rejected file on the chrome bar' ($txt -like '*bad.zip*') `
          ($(if ($txt.Length -eq 0) { 'chrome status is empty' } else { 'status: ' + $txt }))
    Save-WindowShot $main (Get-ShotPath 'draft-4-rejected.png')

    # ---- G. a long name is ellipsized, not drawn past the edge of its chip ----
    # Ellipsize() binary-searches the longest prefix that fits. Its failure mode is
    # silent: one character too many just runs a glyph off the end of the chip, and
    # nobody notices until a user attaches a file with a longer name than the fixtures.
    # It also fails BACKWARDS -- over-truncating an ASCII name to "widget.c" because the
    # per-character width was estimated from a full-width CJK glyph -- so the check is
    # two-sided: ink must be there at all, and it must stop before the chip's edge.
    $longPath = Join-Path $fixDir ('a-very-long-attachment-name-' + ('x' * 40) + '.txt')
    Set-Content -Path $longPath -Value 'long name fixture' -Encoding Ascii

    Send-FilesPaste $edit @($longPath)
    $mr = Get-WinRect $main
    [void][BB]::SetCursorPos(($mr.Right - 8), ($mr.Bottom - 4))
    Start-Sleep -Milliseconds 500
    Take-Snapshot $main

    $er3 = Get-WinRect $edit
    $band4 = Get-BandAbove ($er3.Left + 44) ($er3.Top - 3) $bg
    $rowMid4 = $band4.Top + [int]($band4.H / 2)
    $runs4 = Get-ChipRuns $er3.Left ($er3.Right - 1) $rowMid4 $bg
    Check 'the long-named file landed in its own chip' ($runs4.Count -eq 3) `
          ($runs4.Count.ToString() + ' chip(s)')

    if ($runs4.Count -lt 3) { throw 'the long name chip is missing: cannot test ellipsizing' }

    $lc = $runs4[2]
    # The chip's own fill, sampled above the icon (the icon starts 6px in, 6px down),
    # so the two ink tests below measure against the chip and not the card.
    $fill = Get-Px ($lc.L + 20) ($band4.Top + 3)
    # Stay 8px clear of the chip's rounded corners: at the very corner the fill itself
    # curves away, and those few background pixels would read as ink.
    $y0 = $band4.Top + 8
    $y1 = $band4.Bottom - 8

    # NOTE: build scan rects with Rect-Of. Writing the four numbers space-separated into
    # New-Object binds them as four positional parameters and dies at parse time.
    $there = Count-Ink (Rect-Of ($lc.L + 50) $y0 ($lc.L + 140) $y1) $fill 40
    Check 'the long name is drawn at all' ($there -ge 30) ($there.ToString() + ' ink pixels across the name')

    $over = Count-Ink (Rect-Of ($lc.R - 7) $y0 ($lc.R - 2) $y1) $fill 40
    Check 'the long name stops before the chip edge' ($over -eq 0) `
          ($(if ($over -eq 0) { 'nothing drawn in the 6px strip inside the right edge' }
             else { $over.ToString() + ' ink pixels ran into the chip edge' }))

    Save-WindowShot $main (Get-ShotPath 'draft-5-longname.png')

    if ($script:fail.Count -gt 0) {
        Write-Output ''
        Write-Output ('FAILED: ' + ($script:fail -join '; '))
        throw ($script:fail.Count.ToString() + ' check(s) failed')
    }
    Write-Output ''
    Write-Output 'PASS'
}

Write-BBDone 'draft-strip'
