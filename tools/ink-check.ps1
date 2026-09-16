# Measure where a TextBox's ink starts, empty (placeholder) vs filled (typed).
#
# The point of the exercise: the placeholder is a separate child control drawn
# with TextRenderer, while typed text is drawn by the EDIT itself, which has its
# own built-in left padding. If those two origins differ the text jumps sideways
# the moment you type. Ui.PinEditTextLeft() zeroes the EDIT margin to match.
#
# Reports the ink bounding box of both states from the exact same crop origin,
# so the leftmost column is directly comparable.
#
# The 'message' target is the same question about the conversation input box, and
# it ASSERTS instead of reporting, because there the two layers are not merely
# misaligned by a pixel or two -- they used to carry different font sizes (the
# placeholder 12pt, the EDIT 12.5pt), so typing visibly shrank the text.
#
# All three targets answer it the same way: the same string is drawn by both layers
# over the same crop origin and the two ink boxes are compared as a whole. Equal
# boxes means equal origin AND equal size, which is the requirement stated as one
# measurement -- and it is a stronger statement than comparing fonts would be,
# because it is about the rasterised pixels rather than about intent.
#
# The boxes are measured at half intensity (Get-InkBoxHalf), not at a fixed cut: the
# two layers draw in different colours, and a fixed cut would report the darker one
# as a pixel wider on a thin stem's antialiased edge.
#
# Both shots are taken with the box UNFOCUSED. A focused EDIT paints a caret at the
# insertion point -- a line-height bar starting above the glyph ink -- which would
# blow up the bounding box and make the two states incomparable.
#
# Usage:  powershell -File tools\ink-check.ps1 [-Target search|field|message] [-Text "..."]

param(
    [ValidateSet('search', 'field', 'message')]
    [string]$Target = 'search',
    [string]$Text = ''
)

. "$PSScriptRoot\_ui.ps1"

$script:fail = 0

# ASCII-only file, so the CJK defaults are built from code points.
# "search conversation..." -- matches SearchField's placeholder; the ellipsis
# glyph is the interesting part, it is what TextRenderer and the EDIT disagree on.
if ($Text -eq '') {
    if ($Target -eq 'message') {
        # The input box's placeholder verbatim, ellipsis included: retyping exactly
        # what the placeholder draws means the right and bottom edges of the ink are
        # comparable too, not just the origin.
        $Text = -join ([char]0x53D1, [char]0x6D88, [char]0x606F, [char]0x7ED9, [char]0x5E2E, [char]0x5E2E, [char]0x2026)
    }
    elseif ($Target -eq 'field') {
        # Same reasoning, for the settings field this probe looks at: the "url" row
        # of the chat block, whose example text is Providers.Chat[0]'s placeholder.
        # Nothing can read that string back off the placeholder -- it is a HintText,
        # a drawn Control with no window text -- so it is spelled out here. If the
        # preset's example changes, this fails loudly instead of comparing two
        # different strings and reporting a size difference that is not there.
        $Text = 'https://api.example.com/v1'
    }
    else {
        $Text = -join ([char]0x641C, [char]0x7D22, [char]0x5BF9, [char]0x8BDD, [char]0x2026)
    }
}

# The placeholder layer sitting over an EDIT. It is a HintText, i.e. a bare Control
# under UserPaint, so it has no native class to match on -- but nothing else in the
# window sits on the text box's exact rectangle, and the placeholder does by
# construction (both layers are laid out from the same TextBounds()).
function Get-Placeholder($main, $edit) {
    $er = Get-WinRect $edit
    foreach ($h in Get-WinKids $main) {
        if ($h -eq $edit) { continue }
        if (-not [BB]::IsWindowVisible($h)) { continue }
        $r = Get-WinRect $h
        if ($r.Left -ne $er.Left -or $r.Top -ne $er.Top) { continue }
        if (($r.Right - $r.Left) -ne ($er.Right - $er.Left)) { continue }
        return $h
    }
    return [IntPtr]::Zero
}

Invoke-BBProbe {
    $main = Start-BangGang
    $mr = Get-WinRect $main
    $ww = $mr.Right - $mr.Left
    $wh = $mr.Bottom - $mr.Top

    # Both branches need the pill: the field branch to reach the gear beside it,
    # the search branch to derive the EDIT's width from it.
    $pill = Get-SearchPill $main
    if ($null -eq $pill) { throw 'search pill not found' }

    if ($Target -eq 'message') {
        # A conversation has to exist before the input card does: _chatUI -- and the
        # InputPanel inside it -- stays hidden until one is opened. Clicked via the
        # sidebar's new-conversation button rather than the welcome page's accent
        # pill, because that pill is painted, not an HWND (input-check.ps1 finds it
        # by colour; there is no reason to pay for that scan twice).
        $pb = Get-PillButtons $main $pill
        if ($null -eq $pb) { throw 'sidebar pill buttons not found' }
        Invoke-MouseClick ($pb[1].Left + 14) ($pb[1].Top + 14)
        Start-Sleep -Milliseconds 1000

        # Drop focus from the text box, into the search field. A focused EDIT paints
        # a caret -- a line-height bar at the insertion point that starts ABOVE the
        # glyph ink -- so the bounding box would grow upward and the two states could
        # never compare equal. WM_CHAR is delivered straight to the edit control's
        # window proc, so BB.Type still types with the box unfocused.
        #
        # Clicking the search field rather than "somewhere in the chat area": a
        # WinForms Panel is not selectable, so clicking one hands focus back to the
        # container's first selectable child -- which can be this very text box.
        Invoke-MouseClick ($pill.Left + 30) ($pill.Top + 16)
        Start-Sleep -Milliseconds 500

        $bandTop = $mr.Top + $wh - 150
        $edit = [IntPtr]::Zero
        foreach ($h in Get-WinKids $main) {
            $r = Get-WinRect $h
            if ($r.Bottom -le $bandTop) { continue }
            if ((Get-WinClass $h) -like '*Edit*') { $edit = $h }
        }
        if ($edit -eq [IntPtr]::Zero) { throw 'no EDIT in the input band' }
        $er = Get-WinRect $edit

        # The placeholder layer: see Get-Placeholder.
        $ph = Get-Placeholder $main $edit
        if ($ph -eq [IntPtr]::Zero) { throw 'placeholder layer not found over the input box' }

        Write-Output ("EDIT      : " + $er.Left + "," + $er.Top + " " + ($er.Right - $er.Left) + "x" + ($er.Bottom - $er.Top))
        Write-Output ("EDIT font : " + (Get-WinFont $edit))
        # No matching WM_GETFONT for the placeholder: WinForms only pushes a font
        # onto the HWNDs of control classes it wraps itself, and a bare Control
        # under UserPaint comes back "(no font)". That is not a problem, because
        # the two ink boxes below are a stronger statement than font equality --
        # they compare the same string rasterised by both layers, so they catch a
        # different size and a different position with the same measurement.

        # Crop 4px left of the box so the first ink column is directly comparable.
        # 180px wide is enough for the whole placeholder including its trailing
        # ellipsis, which is what lets R and B be compared as well as X and Y.
        $cx = $er.Left - 4
        $cy = $er.Top
        $cw = 180
        $ch = $er.Bottom - $er.Top

        $bmp = Get-Crop $cx $cy $cw $ch
        $a = Get-InkBoxHalf $bmp
        $bmp.Dispose()
        Save-Shot $cx $cy $cw $ch (Get-ShotPath "ink-$Target-empty.png")
        Write-Output ("placeholder: " + (Format-InkBox $a))

        $got = Invoke-TypeKeys $edit $Text
        if ($got -ne $Text) {
            $script:fail++
            Write-Output ("  FAIL: EDIT holds '" + $got + "', expected '" + $Text + "'")
        }
        # Focus went into the box to type, and a focused EDIT paints a caret at the
        # insertion point -- a line-height bar that would stretch the ink box. The
        # placeholder is gone by now, so nothing covers it: put focus back on the
        # search field before measuring.
        Invoke-MouseClick ($pill.Left + 30) ($pill.Top + 16)
        Start-Sleep -Milliseconds 400
        # If the placeholder were still up, the "typed" crop would be a picture of
        # the placeholder -- identical to the first one, and the comparison below
        # would pass while proving nothing.
        if ([BB]::IsWindowVisible($ph)) {
            $script:fail++
            Write-Output '  FAIL: placeholder layer is still visible over the typed text'
        }

        $bmp = Get-Crop $cx $cy $cw $ch
        $b = Get-InkBoxHalf $bmp
        $bmp.Dispose()
        Save-Shot $cx $cy $cw $ch (Get-ShotPath "ink-$Target-typed.png")
        Write-Output ("typed     : " + (Format-InkBox $b))

        if ($null -eq $a -or $null -eq $b) {
            $script:fail++
            Write-Output '  FAIL: one of the two states has no ink at all'
        }
        else {
            Write-Output ("  placeholder box (" + $a.X + "," + $a.Y + ")..(" + $a.R + "," + $a.B + ")   typed box (" + $b.X + "," + $b.Y + ")..(" + $b.R + "," + $b.B + ")")
            # Same string, same rectangle, two different drawing layers. Equal
            # boxes means equal origin AND equal size -- which is the requirement,
            # "the typed text and the placeholder must match in position and size",
            # stated as one measurement instead of two.
            if ($a.X -ne $b.X -or $a.Y -ne $b.Y) {
                $script:fail++
                Write-Output '  FAIL: typed text does not start where the placeholder starts'
            }
            if ($a.R -ne $b.R -or $a.B -ne $b.B) {
                $script:fail++
                Write-Output '  FAIL: typed text does not end where the placeholder ends (different size or letter spacing)'
            }
        }

        if ($script:fail -gt 0) { throw "$($script:fail) check(s) failed" }
        return
    }

    if ($Target -eq 'field') {
        # model-access page: open settings, click rail row 1
        $gear = Get-SettingsGear $main $pill
        if ($null -eq $gear) { throw 'settings gear not found' }

        $cardX = $mr.Left + [int](($ww - 880) / 2)
        $cardY = $mr.Top + [int](($wh - 640) / 2)

        Invoke-MouseClick ($gear.Left + 14) ($gear.Top + 14)
        Start-Sleep -Milliseconds 1100
        Invoke-MouseClick ($cardX + 14 + 90) ($cardY + 96 + 48 * 1 + 21)
        Start-Sleep -Milliseconds 1100
    }
    else {
        # drop focus from the search box so the placeholder is actually showing
        Invoke-MouseClick ($mr.Left + $ww - 300) ($mr.Top + $wh - 100)
        Start-Sleep -Milliseconds 600
    }

    # search EDIT = pill width - TextPadX(10) - 26 reserved for the clear button.
    # Derived, not hard-coded: the pill is SideW-106 and that changes with the sidebar.
    $want = ($pill.Right - $pill.Left) - 36
    if ($Target -eq 'field') { $want = 306 }

    $edit = [IntPtr]::Zero
    foreach ($h in Get-WinKids $main) {
        if (-not (Get-WinClass $h).StartsWith('WindowsForms10.Edit')) { continue }
        if (-not [BB]::IsWindowVisible($h)) { continue }
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -eq $want) { $edit = $h; break }
    }
    if ($edit -eq [IntPtr]::Zero) { throw "no Edit of width $want on the $Target target" }

    $er = Get-WinRect $edit
    Write-Output ("target $Target : Edit " + $er.Left + "," + $er.Top + " " + ($er.Right - $er.Left) + "x" + ($er.Bottom - $er.Top))
    Write-Output ("  text now: '" + (Get-WinText $edit) + "'")

    # The placeholder layer: see Get-Placeholder.
    $ph = Get-Placeholder $main $edit
    if ($ph -eq [IntPtr]::Zero) { throw "placeholder layer not found over the $Target box" }

    # Crop 4px left of the box so the first ink column is directly comparable, and no
    # wider than the box itself. The upper bound matters for the search field: as soon
    # as it holds text it grows a round clear button at the right end of the pill, and
    # its "x" would land in the crop and read as "the typed text got wider".
    $cx = $er.Left - 4
    $cy = $er.Top
    $cw = [Math]::Min(130, ($er.Right - $er.Left) + 4)
    $ch = $er.Bottom - $er.Top

    $bmp = Get-Crop $cx $cy $cw $ch
    $a = Get-InkBoxHalf $bmp
    $bmp.Dispose()
    Save-Shot $cx $cy $cw $ch (Get-ShotPath "ink-$Target-empty.png")
    Write-Output ("placeholder: " + (Format-InkBox $a))

    # WM_SETTEXT rather than SendInput here, for two reasons. The box has to stay
    # UNFOCUSED (see the header) -- SendInput goes through the keyboard queue, so it
    # needs the caret, and with the settings overlay up there is no click target that
    # is safe by construction to hand focus to afterwards. And WM_SETTEXT is not
    # limited to ASCII the way WM_CHAR is.
    #
    # Its catch is that it never raises WinForms' TextChanged, so the placeholder
    # layer would stay up and the "typed" crop would be a second picture of the
    # placeholder -- a comparison that passes while proving nothing. Two WM_CHARs
    # close that hole: a space, then a backspace. They reach the edit's window proc
    # without focus, they raise TextChanged twice, and they leave the text as $Text.
    [void][BB]::SendText($edit, $Text)
    Start-Sleep -Milliseconds 300
    [void][BB]::Type($edit, ' ')
    [void][BB]::Type($edit, [string][char]8)
    Start-Sleep -Milliseconds 300

    $got = Get-WinText $edit
    Write-Output ("  text now: '" + $got + "'  (len=" + $got.Length + ")")
    if ($got -ne $Text) {
        $script:fail++
        Write-Output ("  FAIL: EDIT holds '" + $got + "', expected '" + $Text + "'")
    }
    # If the placeholder were still up, the "typed" crop would be a picture of the
    # placeholder -- identical to the first one, and the comparison below would pass
    # while proving nothing.
    if ([BB]::IsWindowVisible($ph)) {
        $script:fail++
        Write-Output '  FAIL: placeholder layer is still visible over the typed text'
    }

    $bmp = Get-Crop $cx $cy $cw $ch
    $b = Get-InkBoxHalf $bmp
    $bmp.Dispose()
    Save-Shot $cx $cy $cw $ch (Get-ShotPath "ink-$Target-typed.png")
    Write-Output ("typed      : " + (Format-InkBox $b))

    if ($null -eq $a -or $null -eq $b) {
        $script:fail++
        Write-Output '  FAIL: one of the two states has no ink at all'
    }
    else {
        Write-Output ("  placeholder box (" + $a.X + "," + $a.Y + ")..(" + $a.R + "," + $a.B + ")   typed box (" + $b.X + "," + $b.Y + ")..(" + $b.R + "," + $b.B + ")")
        # Same string, same rectangle, two different drawing layers. Equal boxes
        # means equal origin AND equal size -- which is the requirement, "the typed
        # text and the placeholder must match in position and size", stated as one
        # measurement instead of two.
        if ($a.X -ne $b.X -or $a.Y -ne $b.Y) {
            $script:fail++
            Write-Output '  FAIL: typed text does not start where the placeholder starts'
        }
        if ($a.R -ne $b.R -or $a.B -ne $b.B) {
            $script:fail++
            Write-Output '  FAIL: typed text does not end where the placeholder ends (different size or letter spacing)'
        }
    }

    if ($script:fail -gt 0) { throw "$($script:fail) check(s) failed" }
}

Write-BBDone 'ink-check'
