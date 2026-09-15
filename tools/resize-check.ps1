# Verify the two 0.7.22 changes:
#
#   A. The left sidebar is narrower (SideW 304 -> 256).
#   B. The window is resizable. It is FormBorderStyle.None, so there is no system
#      border to grab -- MainForm.PreFilterMessage intercepts the left-button-down
#      in the queue and runs the resize itself whenever the cursor is within
#      GripPx (6) of the client edge.
#
# Sequence:
#   1. sidebar width == 256, and the search pill + both 28x28 icon buttons still
#      fit inside it with the pill's right edge clear of the gear
#   2. drag the right edge outward   -> width grows, height unchanged
#   3. drag the bottom edge outward  -> height grows, width unchanged
#   4. drag the corner               -> both grow
#   5. drag the right edge far left  -> width clamps at MainForm.MinWindow.Width
#      (one full-size settings card + its margin), and the whole layout (main area /
#      search pill) reflows to the new ClientSize
#   6. at that minimum size, open settings and prove the card stayed at its design
#      size instead of being squeezed by the window
#
# The drag is driven by SetCursorPos + synthetic LEFTDOWN/LEFTUP: the resize is
# fed from Cursor.Position, so moving the real cursor is what drives it.
#
# Usage:  powershell -File tools\resize-check.ps1

. "$PSScriptRoot\_ui.ps1"

function Get-Size($main) {
    $r = Get-WinRect $main
    return @{ W = $r.Right - $r.Left; H = $r.Bottom - $r.Top }
}

Invoke-BBProbe {
    $fail = 0
    $main = Start-BangGang
    $mr = Get-WinRect $main
    $s = Get-Size $main
    Write-Output ("screen   : " + [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width + "x" + [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
    Write-Output ("window   : " + $mr.Left + "," + $mr.Top + " " + $s.W + "x" + $s.H)
    Write-Output ''

    # ---------------- A. sidebar width ----------------

    Write-Output '--- A. sidebar ---'
    $side = $null
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Left -ne $mr.Left) { continue }
        if ($r.Top -ne ($mr.Top + 38)) { continue }
        if (($r.Bottom - $r.Top) -lt 100) { continue }
        $side = $r
    }
    if ($null -eq $side) { throw 'sidebar not found' }
    $sw = $side.Right - $side.Left
    Write-Output ("  sidebar width : $sw  " + $(if ($sw -eq 256) { 'OK' } else { 'FAIL -- expected 256' }))
    if ($sw -ne 256) { $fail++ }

    $pill = Get-SearchPill $main
    if ($null -eq $pill) { throw 'search pill not found' }
    $pw = $pill.Right - $pill.Left
    Write-Output ("  search pill   : " + $pill.Left + "," + $pill.Top + " ${pw}x32  (right edge at " + ($pill.Right - $mr.Left) + " of $sw)")
    if ($pill.Right -gt $side.Right) { Write-Output '  FAIL -- pill runs past the sidebar'; $fail++ }

    $gear = Get-SettingsGear $main $pill
    if ($null -eq $gear) { throw 'settings gear not found' }
    $clear = $gear.Left - $pill.Right
    Write-Output ("  gear          : " + $gear.Left + "," + $gear.Top + " 28x28, " + $clear + "px clear of the pill  " + $(if ($clear -gt 0 -and $gear.Right -le $side.Right) { 'OK' } else { 'FAIL' }))
    if ($clear -le 0 -or $gear.Right -gt $side.Right) { $fail++ }

    $plus = $null
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -ne 28 -or ($r.Bottom - $r.Top) -ne 28) { continue }
        if ($r.Top -ne $gear.Top -or $r.Left -le $gear.Left) { continue }
        $plus = $r
    }
    if ($null -eq $plus) { throw 'new-conversation button not found' }
    Write-Output ("  plus          : " + $plus.Left + "," + $plus.Top + " 28x28, right edge at " + ($plus.Right - $mr.Left) + " of $sw  " + $(if ($plus.Right -le $side.Right) { 'OK' } else { 'FAIL -- past the sidebar' }))
    if ($plus.Right -gt $side.Right) { $fail++ }

    # ---------------- B. resize ----------------

    Write-Output ''
    Write-Output '--- B. window resize ---'
    $midY = $mr.Top + [int]($s.H / 2)

    # The window tracks the CURSOR delta, exactly like a native resize: where the
    # grab started inside the edge does not shift the result, so a cursor moved
    # 260px widens the window by exactly 260px.
    # 1. right edge
    Invoke-Drag ($mr.Right - 2) $midY (($mr.Right - 2) + 260) $midY
    $b = Get-Size $main
    $okW = ($b.W -eq $s.W + 260); $okH = ($b.H -eq $s.H)
    Write-Output ("  right edge  : " + $s.W + "x" + $s.H + " -> " + $b.W + "x" + $b.H + "   " + $(if ($okW -and $okH) { 'OK' } else { 'FAIL -- expected width +260, height unchanged' }))
    if (-not ($okW -and $okH)) { $fail++ }
    $s = $b

    # 2. bottom edge
    $mr = Get-WinRect $main
    $midX = $mr.Left + [int]($s.W / 2)
    Invoke-Drag $midX ($mr.Bottom - 2) $midX (($mr.Bottom - 2) + 120)
    $b = Get-Size $main
    $okH = ($b.H -eq $s.H + 120); $okW = ($b.W -eq $s.W)
    Write-Output ("  bottom edge : " + $s.W + "x" + $s.H + " -> " + $b.W + "x" + $b.H + "   " + $(if ($okH -and $okW) { 'OK' } else { 'FAIL -- expected height +120, width unchanged' }))
    if (-not ($okH -and $okW)) { $fail++ }
    $s = $b

    # 3. top-left corner. Deliberately not the bottom-right: the window already
    #    reaches y=1040 of a 1080px screen, and SetCursorPos CLAMPS to the screen,
    #    so a drag toward the bottom would silently deliver less than asked for.
    $mr = Get-WinRect $main
    Invoke-Drag ($mr.Left + 2) ($mr.Top + 2) (($mr.Left + 2) - 90) (($mr.Top + 2) - 70)
    $b = Get-Size $main
    $mr2 = Get-WinRect $main
    $ok = ($b.W -eq $s.W + 90 -and $b.H -eq $s.H + 70 -and
           $mr2.Left -eq $mr.Left - 90 -and $mr2.Top -eq $mr.Top - 70)
    Write-Output ("  corner      : " + $s.W + "x" + $s.H + " at " + $mr.Left + "," + $mr.Top + " -> " + $b.W + "x" + $b.H + " at " + $mr2.Left + "," + $mr2.Top + "   " + $(if ($ok) { 'OK -- grew +90/+70 and the origin moved -90/-70' } else { 'FAIL -- expected +90/+70 with the origin following' }))
    if (-not $ok) { $fail++ }
    $s = $b
    $mr = $mr2

    # 4. shrink the right edge well past the floor: it must stop at MinWindow.Width
    $floor = Get-MinWindow
    $mr = Get-WinRect $main
    $midY = $mr.Top + [int]($s.H / 2)
    Invoke-Drag ($mr.Right - 2) $midY ($mr.Right - 900) $midY
    $b = Get-Size $main
    $ok = ($b.W -eq $floor.W)
    Write-Output ("  min clamp   : width " + $b.W + "  " + $(if ($ok) { "OK -- stopped at MinWindow.Width " + $floor.W } else { "FAIL -- expected " + $floor.W }))
    if (-not $ok) { $fail++ }
    $s = $b

    # 5. the layout must have followed the new ClientSize
    $main2 = $null
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Left -ne ($mr.Left + 256)) { continue }
        if ($r.Top -ne ($mr.Top + 38)) { continue }
        $main2 = $r
    }
    if ($null -eq $main2) { Write-Output '  reflow      : FAIL -- main area is not at x=256'; $fail++ }
    else {
        $mw = $main2.Right - $main2.Left
        $ok = ($main2.Right -eq $mr.Left + $s.W)
        Write-Output ("  reflow      : main area " + $main2.Left + "," + $main2.Top + " ${mw}x" + ($main2.Bottom - $main2.Top) + "  " + $(if ($ok) { 'OK -- starts at 256, reaches the right edge' } else { 'FAIL' }))
        if (-not $ok) { $fail++ }
    }
    $pill2 = Get-SearchPill $main
    $ok = ($null -ne $pill2) -and (($pill2.Right - $pill2.Left) -eq ($pw))
    Write-Output ("  search pill : " + $(if ($null -eq $pill2) { 'MISSING' } else { ($pill2.Right - $pill2.Left).ToString() + "px wide" }) + "  " + $(if ($ok) { 'OK -- unchanged by the resize' } else { 'FAIL' }))
    if (-not $ok) { $fail++ }

    Save-WindowShot $main (Get-ShotPath 'resize-min.png')

    # ---------------- C. settings at the minimum size ----------------

    Write-Output ''
    Write-Output '--- C. settings overlay at the minimum window size ---'
    $gear2 = Get-SettingsGear $main $pill2
    if ($null -eq $gear2) { throw 'settings gear not found after resize' }
    Invoke-MouseClick ($gear2.Left + 14) ($gear2.Top + 14)
    Start-Sleep -Milliseconds 1600

    # the card is min(880, max(520, W-96)) x min(640, max(380, H-96))
    $cardW = [Math]::Min(880, [Math]::Max(520, $s.W - 96))
    $cardH = [Math]::Min(640, [Math]::Max(380, $s.H - 96))
    $cardX = $mr.Left + [int](($s.W - $cardW) / 2)
    $cardY = $mr.Top + [int](($s.H - $cardH) / 2)
    Write-Output ("  window      : " + $s.W + "x" + $s.H + "  -> card should be ${cardW}x${cardH} at " + $cardX + "," + $cardY)

    # page 1 = model access, the page that actually has input fields on it
    Invoke-MouseClick ($cardX + 14 + 90) ($cardY + 96 + 48 * 1 + 21)
    Start-Sleep -Milliseconds 1500

    # every visible Edit on the page must sit inside the card's right edge
    $worst = 0; $n = 0
    foreach ($h in Get-WinKids $main) {
        if (-not (Get-WinClass $h).StartsWith('WindowsForms10.Edit')) { continue }
        if (-not [BB]::IsWindowVisible($h)) { continue }
        $r = Get-WinRect $h
        if (($r.Bottom - $r.Top) -gt 40) { continue }          # the multi-line chat input
        if ($r.Left -lt $cardX) { continue }                   # the sidebar search box
        $n++
        if ($r.Right -gt $cardX + $cardW) { $worst = [Math]::Max($worst, $r.Right - ($cardX + $cardW)) }
    }
    Write-Output ("  visible edits inside the card : $n ; worst overflow past the card = ${worst}px  " + $(if ($n -gt 0 -and $worst -eq 0) { 'OK' } else { 'FAIL -- no field found, or a field is clipped' }))
    if ($n -le 0 -or $worst -ne 0) { $fail++ }

    Save-WindowShot $main (Get-ShotPath 'resize-min-settings.png')

    Write-Output ''
    if ($fail -eq 0) { Write-Output 'PASS: sidebar narrowed, window resizes from every edge, layout reflows.' }
    else { Write-Output "FAIL: $fail check(s) failed." }
}

Write-BBDone 'resize-check'
