# input-check.ps1 -- inspect the message input area, and assert the things about it
# that are easy to break and hard to see.
#
# Opens a conversation (the welcome page has no input area at all -- the input
# panel is a child of _chatUI, which is hidden until a conversation exists), dumps
# the geometry of everything in the bottom band, and then walks the card through
# four states, saving a shot of each:
#
#   empty     shoots/input-empty.png     -- the hint must not erase the card border
#   typed     shoots/input-typed.png     -- the send button lights up
#   overflow  shoots/input-overflow.png  -- wraps past 3 lines: box stops growing,
#                                           view follows the caret, thumb appears
#   sending   (no shot)                  -- the send button becomes "pause" while a
#                                           reply is in flight, and clicking it there
#                                           cancels that reply
#
# Every check below is an assertion, not a printout. The ones worth spelling out:
#
#   border  the hint Label is opaque and its rect used to run all the way to the
#           card's lower edge, painting the bottom border flat across the middle of
#           the card. Measured against the border's own grey, calibrated from a
#           column nothing overlaps -- see Test-BottomBorder.
#   lines   EM_GETLINECOUNT / EM_GETFIRSTVISIBLELINE, i.e. what the EDIT itself
#           thinks, rather than inferring from the box height.
#   thumb   read out of the 14px of card padding to the right of the text box,
#           which is the only place it is ever drawn.
#   pause   the click has to land inside the model-reply window, and the app only
#           writes the "paused" status when it actually cancelled something -- so
#           the chrome status label is a precise witness that StopRequested fired.
#   glyph   the send button has three looks and only the last two are told apart by
#           pixels: a flat 8px stop square and a 13px up-arrow, so height separates
#           them. Only the busy one is hard to catch, see step 4.
#
# Usage:  powershell -File tools\input-check.ps1

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_ui.ps1"

$script:fail = 0

# EM_GETLINECOUNT / EM_GETFIRSTVISIBLELINE. There is no absolute
# "set first visible line" message, but there is one to read it back -- and that
# read is the only honest source for "the box is showing lines 2..4 of 4".
$EM_GETLINECOUNT = 0x00BA
$EM_GETFIRSTVISIBLELINE = 0x00CE

function Get-LineCount($edit) { return [int]([BB]::SendMessage($edit, 0x00BA, [IntPtr]::Zero, [IntPtr]::Zero)) }
function Get-FirstVisible($edit) { return [int]([BB]::SendMessage($edit, 0x00CE, [IntPtr]::Zero, [IntPtr]::Zero)) }

# How much accent fill is in a rectangle. The send button's circle is 28px across,
# so a lit one is worth ~600 of these and an unlit (neutral grey) one is worth 0.
function Get-AccentCount([int]$x, [int]$y, [int]$w, [int]$h) {
    $bmp = Get-Crop $x $y $w $h
    $n = 0
    for ($i = 0; $i -lt $bmp.Width; $i++) {
        for ($j = 0; $j -lt $bmp.Height; $j++) {
            $c = $bmp.GetPixel($i, $j)
            # Theme.Accent = 47,112,224. Tolerant enough for antialiased edges to
            # count as "not accent" without the core fill ever being missed.
            if ([Math]::Abs($c.R - 47) -le 14 -and [Math]::Abs($c.G - 112) -le 14 -and [Math]::Abs($c.B - 224) -le 14) { $n++ }
        }
    }
    $bmp.Dispose()
    return $n
}

# The glyph inside the send button, read as WHITE ink on the accent-filled circle.
#
# The crop has to be smaller than the circle it sits in, or its corners -- which are
# outside the circle and therefore show the card's white background -- read as ink
# and stretch the box to the whole crop. A 28px button means radius 14, so the
# largest centred square inside it is 14*sqrt(2) = 19.8px; 16px leaves real margin.
# The two glyphs differ in height and in nothing else worth measuring: the up-arrow
# is a ~13px shaft with round caps, the stop square is a flat 8px.
function Get-SendGlyph($btn) {
    $bmp = Get-Crop ($btn.Left + 6) ($btn.Top + 6) 16 16
    $res = Get-InkBoxNum $bmp 200 -Bright
    $bmp.Dispose()
    return $res
}

# Is the card's bottom border still there, across the span the text box occupies?
#
# The hint Label is opaque, so if its rect reaches the card's lower edge it erases
# the stroke for that whole span -- the middle of the card. Rather than hard-code
# the border's colour (it is not the same in both themes, and antialiasing moves
# it), the grey is calibrated from a column nothing overlaps: 10px left of the text
# box, still inside the card, clear of the attach button which starts 9px in. Every
# column across the text box must then be at least half as dark as that, somewhere
# within 2px of the same row.
#
# The scan has to run all the way down to the card's lower edge. An earlier version
# stopped 44px under the text box, which is above the border -- the calibration
# column then held nothing but background, "half as dark as the background" was
# true of everything, and the check passed no matter what the hint was doing.
function Test-BottomBorder([int]$xCal, [int]$x0, [int]$x1, [int]$yFrom, [int]$yTo) {
    $h = $yTo - $yFrom + 1
    $cal = Get-Crop $xCal $yFrom 1 $h
    $gBest = 255.0; $row = 0
    for ($j = 0; $j -lt $h; $j++) {
        $c = $cal.GetPixel(0, $j)
        $lum = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
        if ($j -eq 0 -or $lum -lt $gBest) { $gBest = $lum; $row = $j }
    }
    $cal.Dispose()

    $w = $x1 - $x0 + 1
    $strip = Get-Crop $x0 ($yFrom + $row - 2) $w 5
    $bad = 0; $worst = 0.0
    for ($i = 0; $i -lt $strip.Width; $i++) {
        $best = 255.0
        for ($j = 0; $j -lt $strip.Height; $j++) {
            $c = $strip.GetPixel($i, $j)
            $lum = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
            if ($lum -lt $best) { $best = $lum }
        }
        if ($best -gt (($gBest + 255) / 2)) { $bad++ }
        if ($best -gt $worst) { $worst = $best }
    }
    $strip.Dispose()
    return @{ Row = $yFrom + $row; Grey = [int]$gBest; Bad = $bad; W = $w; Worst = [int]$worst }
}

# How many lines of text are on screen inside the box, counted from the picture:
# runs of rows that contain ink, with a run broken by three empty rows. Sampled
# every third column over the first 360px -- the text is long enough to reach that
# far on every line, and reading all 884 columns x 66 rows one GetPixel at a time
# costs seconds.
function Get-VisibleLines([int]$x, [int]$y, [int]$w, [int]$h) {
    $bmp = Get-Crop $x $y $w $h
    $bands = 0; $gap = 99
    for ($j = 0; $j -lt $h; $j++) {
        $ink = $false
        for ($i = 0; $i -lt $w; $i += 3) {
            $c = $bmp.GetPixel($i, $j)
            if ((0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B) -lt 200) { $ink = $true; break }
        }
        if ($ink) { if ($gap -ge 3) { $bands++ }; $gap = 0 } else { $gap++ }
    }
    $bmp.Dispose()
    return $bands
}

# The welcome page's "new conversation" button is a solid accent-coloured pill;
# it is drawn, not an HWND, so find it by colour. The logo circle is the same
# colour, hence the "below the middle" restriction.
function Find-AccentButton($bmp) {
    $accent = [System.Drawing.Color]::FromArgb(47, 112, 224)
    $minX = -1; $maxX = -1; $minY = -1; $maxY = -1; $n = 0
    for ($x = 0; $x -lt $bmp.Width; $x += 2) {
        for ($y = [int]($bmp.Height * 0.55); $y -lt $bmp.Height; $y += 2) {
            $c = $bmp.GetPixel($x, $y)
            if ([Math]::Abs($c.R - $accent.R) -le 6 -and [Math]::Abs($c.G - $accent.G) -le 6 -and [Math]::Abs($c.B - $accent.B) -le 6) {
                $n++
                if ($minX -lt 0 -or $x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($minY -lt 0 -or $y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($n -lt 50) { return $null }
    return @{ X = [int](($minX + $maxX) / 2); Y = [int](($minY + $maxY) / 2); W = $maxX - $minX; H = $maxY - $minY }
}

Invoke-BBProbe {
    $main = Start-BangGang
    $mr = Get-WinRect $main
    $w = $mr.Right - $mr.Left; $h = $mr.Bottom - $mr.Top

    $shot = Get-ShotPath 'welcome.png'
    Save-WindowShot $main $shot
    $bmp = [System.Drawing.Image]::FromFile($shot)
    $btn = Find-AccentButton $bmp
    $bmp.Dispose()
    if ($null -eq $btn) { throw 'welcome: new-conversation button not found' }
    Write-Output ("new-conversation button: " + $btn.W + "x" + $btn.H + " at +" + $btn.X + "+" + $btn.Y)

    Invoke-MouseClick ($mr.Left + $btn.X) ($mr.Top + $btn.Y)
    Start-Sleep -Milliseconds 900

    # The input panel is pinned to the bottom 150px of the client area
    # (MainForm.ApplyLayout). The window is borderless, so client == window rect.
    $bandTop = $mr.Top + $h - 150
    Write-Output ''
    Write-Output '--- controls in the bottom 150px band ---'
    foreach ($child in Get-WinKids $main) {
        $r = Get-WinRect $child
        if ($r.Bottom -le $bandTop) { continue }
        $c = Get-WinClass $child
        if ($c.StartsWith('WindowsForms10.')) { $c = $c.Substring(0, $c.IndexOf('.app')) -replace '^WindowsForms10\.', '' }
        Write-Output ('  ' + $c.PadRight(14) + ' win ' + $r.Left + ',' + $r.Top + ' ' + ($r.Right - $r.Left) + 'x' + ($r.Bottom - $r.Top) +
                      '   rel ' + ($r.Left - $mr.Left) + ',' + ($r.Top - $bandTop) + '   ' +
                      $(if ([BB]::IsWindowVisible($child)) { 'vis' } else { 'hid' }))
    }

    $edit = [IntPtr]::Zero
    foreach ($child in Get-WinKids $main) {
        $r = Get-WinRect $child
        if ($r.Bottom -le $bandTop) { continue }
        if ((Get-WinClass $child) -like '*Edit*') { $edit = $child }
    }
    if ($edit -eq [IntPtr]::Zero) { throw 'no EDIT found in the input band' }
    $er = Get-WinRect $edit

    $btns = Get-InputButtons $main $edit
    if ($null -eq $btns) { throw 'attach / send buttons not found' }

    Write-Output ''
    Write-Output ('EDIT  ' + $er.Left + ',' + $er.Top + ' ' + ($er.Right - $er.Left) + 'x' + ($er.Bottom - $er.Top) +
                  '   font ' + (Get-WinFont $edit) + '   ' + (Get-TextExtent $edit 'Xg'))
    Write-Output ('attach ' + $btns.Attach.Left + ',' + $btns.Attach.Top + '    send ' + $btns.Send.Left + ',' + $btns.Send.Top)

    # ============ 1. empty ============
    Write-Output ''
    Write-Output '--- empty ---'
    Save-Shot $mr.Left $bandTop $w 150 (Get-ShotPath 'input-empty.png')

    $acc = Get-AccentCount $btns.Send.Left $btns.Send.Top 28 28
    Write-Output ("send button accent pixels: " + $acc + "  (0 = the disabled grey state)")
    if ($acc -ne 0) {
        $script:fail++
        Write-Output '  FAIL: the send button is lit while the box is empty and no reply is in flight'
    }

    # The card's bottom border, scanned from just under the text box down to the
    # lower edge of the card. The scan starts 4px under the text box so the hint's
    # own glyphs (which sit ~20px higher, centred in the row) can never be mistaken
    # for the border; it stops 5px short of the bottom of the band so the window's
    # own edging, which is darker than any card border, stays out of the calibration.
    $bd = Test-BottomBorder ($er.Left - 10) $er.Left $er.Right ($er.Bottom + 4) ($bandTop + 145)
    Write-Output ("card border row " + $bd.Row + " (rel " + ($bd.Row - $bandTop) + ") grey " + $bd.Grey +
                  " ; erased at " + $bd.Bad + " of " + $bd.W + " columns (worst " + $bd.Worst + ")")
    if ($bd.Grey -gt 245) {
        $script:fail++
        Write-Output '  FAIL: the calibration column found no border to compare against'
    }
    if ($bd.Bad -gt 0) {
        $script:fail++
        Write-Output '  FAIL: the card''s bottom border is missing under the hint text'
    }

    # The thumb lives in the card padding to the right of the text box, so it never
    # overlaps a glyph -- and its appearance can therefore never change the line
    # count that decides whether it appears at all. The strip stops 3px short of the
    # card's own right border, which is a full-height 1px line the same shade as ink
    # and would otherwise read as "a thumb" in every state.
    $barX = $er.Right + 1
    $barW = 10
    $barH = $er.Bottom - $er.Top
    $bar = Get-InkBoxNumAt $barX $er.Top $barW $barH 240
    Write-Output ('scroll thumb strip ' + $barX + ',' + $er.Top + ' ' + $barW + 'x' + $barH + ': ' + (Format-InkBox $bar))
    if ($null -ne $bar) {
        $script:fail++
        Write-Output '  FAIL: a scroll thumb is showing on a one-line (empty) box'
    }

    # ============ 2. typed ============
    Write-Output ''
    Write-Output '--- typed ---'
    # Type it (WM_CHAR) rather than SendText (WM_SETTEXT) -- see BB.Type's note:
    # WM_SETTEXT leaves the native edit holding the string without ever raising
    # WinForms' TextChanged, so the placeholder stays up and the "typed" shot is
    # really the empty state.  Assert the round-trip instead of trusting it.
    $got = [BB]::Type($edit, 'hello')
    Write-Output ("EDIT text after typing = '" + $got + "'")
    if ($got -ne 'hello') { throw "EDIT did not take the text (got '$got')" }

    # The placeholder must be gone now, otherwise the screenshot shows it drawn
    # over the typed text.  Match on the *native* class name: a WinForms Label's
    # GetClassName is "WindowsForms10.STATIC.app.0.xxxx", never plain "Static".
    foreach ($child in Get-WinKids $main) {
        $r = Get-WinRect $child
        if ($r.Bottom -le $bandTop) { continue }
        if ((Get-WinClass $child) -like '*STATIC*') {
            Write-Output ("Static '" + [BB]::Tx($child) + "'  rel " + ($r.Left - $mr.Left) + ',' + ($r.Top - $bandTop) +
                          "  visible=" + [BB]::IsWindowVisible($child))
        }
    }
    Save-Shot $mr.Left $bandTop $w 150 (Get-ShotPath 'input-typed.png')

    $acc = Get-AccentCount $btns.Send.Left $btns.Send.Top 28 28
    $glyph = Get-SendGlyph $btns.Send
    Write-Output ("send button accent pixels: " + $acc + "   glyph " + (Format-InkBox $glyph))
    if ($acc -lt 300) {
        $script:fail++
        Write-Output '  FAIL: the send button did not light up with text in the box'
    }
    # Up-arrow: a 5.6px shaft with round caps, so ~13px of white ink. Stop square:
    # a flat 8px block. Two pixels of daylight on either side of the threshold.
    if ($null -eq $glyph -or ($glyph.B - $glyph.Y + 1) -lt 11) {
        $script:fail++
        Write-Output '  FAIL: the send button is not showing the send (up-arrow) glyph'
    }

    $bar = Get-InkBoxNumAt $barX $er.Top $barW $barH 240
    if ($null -ne $bar) {
        $script:fail++
        Write-Output ('  FAIL: a scroll thumb is showing on a one-line box: ' + (Format-InkBox $bar))
    }

    # ============ 3. overflow: more lines than the box can show ============
    Write-Output ''
    Write-Output '--- overflow ---'
    # A long ASCII sentence, left to wrap on its own. That is how the cap is reached
    # in use -- and it is the honest way to test it, because "one line" here means a
    # VISUAL line: that is what EM_GETLINECOUNT counts and what the thumb is
    # proportional to. Explicit CRs would test the same counter with the wrapping
    # turned off. (ASCII because WM_CHAR is; see BB.Type.)
    $long = ('the quick brown fox jumps over the lazy dog ' * 10).Trim()
    $got = [BB]::Type($edit, $long)
    Start-Sleep -Milliseconds 400
    $lines = Get-LineCount $edit
    $first = Get-FirstVisible $edit
    Write-Output ("EDIT lines = " + $lines + "   first visible = " + $first + "   (typed " + $long.Length + " chars)")

    # The cap is a fact about the BOX, not about the text: EM_GETLINECOUNT goes up
    # forever, so what has to hold is that the box stayed three lines tall and that
    # at most three lines of text are on screen.
    $er2 = Get-WinRect $edit
    $boxH = $er2.Bottom - $er2.Top
    $vis = Get-VisibleLines ($er.Left + 6) $er.Top 360 $boxH
    Write-Output ("EDIT box " + $boxH + "px tall (was " + ($er.Bottom - $er.Top) + ")   lines = " + $lines +
                  "   first visible = " + $first + "   text lines on screen = " + $vis)
    if ($boxH -ne ($er.Bottom - $er.Top)) {
        $script:fail++
        Write-Output '  FAIL: the text box grew instead of scrolling'
    }
    if ($lines -le 3) {
        $script:fail++
        Write-Output ("  FAIL: the text did not overflow at all (" + $lines + " lines)")
    }
    # Counted off the pixels, because that is what "shows at most 3 lines" means --
    # EM_GETLINECOUNT counts the text and would happily say 40.
    if ($vis -gt 3) {
        $script:fail++
        Write-Output ("  FAIL: " + $vis + " lines of text are on screen in a 3-line box")
    }
    if ($first -le 0) {
        $script:fail++
        Write-Output '  FAIL: the box did not scroll; the caret is at the end of the text'
    }

    $bar = Get-InkBoxNumAt $barX $er.Top $barW $barH 240
    Write-Output ('scroll thumb strip: ' + (Format-InkBox $bar))
    if ($null -eq $bar) {
        $script:fail++
        Write-Output '  FAIL: no scroll thumb on a box holding more lines than it can show'
    }
    else {
        Write-Output ('  thumb spans y ' + $bar.Y + '..' + $bar.B + ' of ' + $barH + '  (x ' + $bar.X + '..' + $bar.R + ' of ' + $barW + ')')
        # Proportional, not full height: the thumb's share of the track is the share
        # of the text the box is showing. _barVis is the app's own "lines that fit"
        # (3 here), so this is really "is the thumb drawn from the same numbers the
        # box height came from".
        $thumbH = $bar.B - $bar.Y + 1
        $wantH = [int]($barH * 3 / $lines)
        if ($thumbH -ge $barH) {
            $script:fail++
            Write-Output '  FAIL: the thumb fills the whole track; it is not proportional to the text'
        }
        elseif ([Math]::Abs($thumbH - $wantH) -gt 3) {
            $script:fail++
            Write-Output ("  FAIL: the thumb is " + $thumbH + "px, expected about " + $wantH + "px (3 of " + $lines + " lines)")
        }
    }
    Save-Shot $mr.Left $bandTop $w 150 (Get-ShotPath 'input-overflow.png')

    # ============ 4. send, then pause ============
    Write-Output ''
    Write-Output '--- sending / pause ---'
    $status = Get-ChromeStatus $main
    if ($status -eq [IntPtr]::Zero) { throw 'chrome status label not found' }

    # The status label is what makes both halves of this phase readable: it is
    # written by the same handler that sets Busy, so "the label changed" means the
    # click landed, which a glyph comparison alone can never tell you.
    $pre = Get-WinText $status
    Invoke-MouseClick ($btns.Send.Left + 14) ($btns.Send.Top + 14)

    # Sample the button in a loop and keep the first frame that shows the stop
    # square. The busy state only exists for the 450ms reply window, and a single
    # crop taken "right after the click" is a race against the app's repaint: the
    # click is delivered through the input queue, so the very next thing this
    # script does can easily still be looking at the pre-click frame. Losing that
    # race looks exactly like a button that never changed, which is why the loop
    # reports how many frames it took.
    $landed = $false
    $busy = $null
    $shots = 0
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 400) {
        if (-not $landed -and (Get-WinText $status) -ne $pre) { $landed = $true }
        $shot = Get-Crop ($btns.Send.Left + 6) ($btns.Send.Top + 6) 16 16
        $r = Get-InkBoxNum $shot 200 -Bright
        $shot.Dispose()
        $shots++
        # Only the square can be shorter than 10px. The disabled (grey) button
        # cannot be mistaken for it either: its bright region is the whole crop,
        # because there the glyph is the dark thing and the card shows through.
        if ($null -ne $r -and ($r.B - $r.Y + 1) -le 10) { $busy = $r; break }
    }
    $ms = $sw.ElapsedMilliseconds

    # The status label is written by the same handler that sets Busy, so reading it
    # back is how we know the click reached the app at all -- a bare glyph check
    # cannot tell "the button did not change" from "the click was never delivered".
    Write-Output ("click landed (status changed): " + $landed + "   sampled " + $shots +
                  " frames in " + $ms + "ms")
    if (-not $landed) {
        $script:fail++
        Write-Output '  FAIL: the send click never reached the app; the status label never changed'
    }
    Write-Output ('send glyph while busy: ' + (Format-InkBox $busy))
    if ($null -eq $busy) {
        $script:fail++
        Write-Output '  FAIL: the send button kept its up-arrow while a reply was in flight; expected the stop square'
    }

    # ---- pause: a second click inside that same window cancels the reply ----
    #
    # A separate send, because this half is on the other side of the same clock:
    # nothing slow may sit between the two clicks, or the second one lands after
    # the reply has been delivered and the app has nothing left to cancel. The
    # sampling loop above would spend the whole window.
    #
    # The box was emptied by the send, so there has to be text again first --
    # otherwise the first click hits a button that is neither busy nor holding a
    # draft, the app ignores it, and the second click becomes the send.
    #
    # And the previous reply has to be *over* before that text goes in. This used to
    # be a flat 600ms sleep, on the assumption that the endpoint fails fast; it does
    # not. The fixture points at a closed port, and a connect to it takes 2.1s on this
    # machine -- so 600ms in, the first reply was still in flight, the "send" click
    # landed as a PAUSE, and the click that was supposed to pause became the send.
    # The two failures that produced ("status is not the paused message", "the button
    # is still lit") read exactly like a broken pause button. Poll for the state
    # instead of guessing the clock: with the box empty and no reply running, the
    # button is grey, so accent pixels going to zero is "Busy cleared".
    $cleared = $false
    $swWait = [System.Diagnostics.Stopwatch]::StartNew()
    while ($swWait.ElapsedMilliseconds -lt 15000) {
        if ((Get-AccentCount $btns.Send.Left $btns.Send.Top 28 28) -eq 0) { $cleared = $true; break }
        Start-Sleep -Milliseconds 100
    }
    Write-Output ("previous reply finished after " + $swWait.ElapsedMilliseconds + "ms (button grey again: " + $cleared + ")")
    if (-not $cleared) { throw 'the first reply never finished; nothing left to pause' }

    $got = [BB]::Type($edit, 'again')
    if ($got -ne 'again') { throw "EDIT did not take the text (got '$got')" }
    Start-Sleep -Milliseconds 200

    # The second click has to land on a different part of the button, and that is
    # not a detail of the probe. Two clicks on the same pixel inside the system
    # double-click time are delivered as ONE double-click: the second becomes
    # WM_LBUTTONDBLCLK, and WinForms raises DoubleClick there instead of a second
    # Click -- so the app never hears the pause at all and the button just sits
    # there looking broken. A real pause click is a separate, deliberate click, so
    # the honest model of one is a click somewhere else on the same button (the
    # default double-click distance is 4px; these two are 12px apart, and both are
    # well inside the 28px circle).
    $sendA = $btns.Send.Left + 8
    $sendB = $btns.Send.Left + 20
    $sendY = $btns.Send.Top + 14

    Invoke-MouseClick $sendA $sendY
    Start-Sleep -Milliseconds 120
    Invoke-MouseClick $sendB $sendY
    Start-Sleep -Milliseconds 250

    # "paused" is written only by the branch that actually cancelled a pending
    # reply, so reading it back proves the second click was understood as a stop
    # and not as a second send.
    $want = -join ([char]0x5DF2, [char]0x6682, [char]0x505C, [char]0x672C, [char]0x6B21, [char]0x56DE, [char]0x590D)
    $seen = Get-WinText $status
    Write-Output ("status label after the pause click = '" + $seen + "'")
    if ($seen -ne $want) {
        $script:fail++
        Write-Output "  FAIL: chrome status is not the 'paused' message"
    }

    # And back to the first state: Busy cleared and the box emptied by the send, so
    # the button is neither lit nor clickable again.
    $acc = Get-AccentCount $btns.Send.Left $btns.Send.Top 28 28
    Write-Output ("send button accent pixels after the pause: " + $acc + "  (0 = back to the grey state)")
    if ($acc -ne 0) {
        $script:fail++
        Write-Output '  FAIL: the send button is still lit after the reply was cancelled with an empty box'
    }

    if ($script:fail -gt 0) { throw "$($script:fail) check(s) failed" }
}

Write-BBDone 'input-check'
