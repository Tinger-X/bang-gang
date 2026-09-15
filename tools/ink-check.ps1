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
# Usage:  powershell -File tools\ink-check.ps1 [-Target search|field] [-Text "..."]

param(
    [ValidateSet('search', 'field')]
    [string]$Target = 'search',
    [string]$Text = ''
)

. "$PSScriptRoot\_ui.ps1"

# ASCII-only file, so the CJK defaults are built from code points.
# "search conversation..." -- matches SearchField's placeholder; the ellipsis
# glyph is the interesting part, it is what TextRenderer and the EDIT disagree on.
if ($Text -eq '') {
    $Text = -join ([char]0x641C, [char]0x7D22, [char]0x5BF9, [char]0x8BDD, [char]0x2026)
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

    $cx = $er.Left - 4
    $cy = $er.Top
    $cw = 130
    $ch = $er.Bottom - $er.Top

    Write-Output ("placeholder : " + (Get-InkBoxAt $cx $cy $cw $ch))
    Save-Shot $cx $cy $cw $ch (Get-ShotPath "ink-$Target-empty.png")

    [void][BB]::SendText($edit, $Text)
    Start-Sleep -Milliseconds 800

    Write-Output ("  text now: '" + (Get-WinText $edit) + "'  (len=" + (Get-WinText $edit).Length + ")")
    Write-Output ("typed       : " + (Get-InkBoxAt $cx $cy $cw $ch))
    Save-Shot $cx $cy $cw $ch (Get-ShotPath "ink-$Target-typed.png")

    Write-Output '  (both crops share the same origin; equal leftmost columns = aligned)'
}

Write-BBDone 'ink-check'
