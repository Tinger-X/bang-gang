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

    if ($Target -eq 'field') {
        # model-access page: open settings, click rail row 1
        $pill = $null
        foreach ($h in Get-WinKids $main) {
            $r = Get-WinRect $h
            if (($r.Right - $r.Left) -eq 198 -and ($r.Bottom - $r.Top) -eq 32) { $pill = $r }
        }
        if ($null -eq $pill) { throw 'search pill not found' }

        $gear = $null
        foreach ($h in Get-WinKids $main) {
            $r = Get-WinRect $h
            if (($r.Right - $r.Left) -ne 28 -or ($r.Bottom - $r.Top) -ne 28) { continue }
            if ($r.Left -le $pill.Right) { continue }
            if ([Math]::Abs($r.Top - ($pill.Top + 2)) -gt 3) { continue }
            if ($null -eq $gear -or $r.Left -lt $gear.Left) { $gear = $r }
        }
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

    $want = 162
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
