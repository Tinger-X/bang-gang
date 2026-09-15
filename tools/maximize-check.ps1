# Verify the maximize/restore button and the settings-aware window floor.
#
#   G. A maximize/restore button sits immediately left of the app's close button.
#      MainForm.ToggleMaximize deliberately does NOT use WindowState.Maximized: the
#      window is FormBorderStyle.None, so the OS would stretch it over the taskbar
#      too, and MaximizedBounds is undocumented for borderless forms. Instead it saves
#      Bounds, fills Screen.WorkingArea, and puts the saved rectangle back on the
#      second click. The glyph flips Maximize <-> Restore.
#
#   H. MainForm.MinWindow is now "one full-size settings card + its margin", so the
#      settings overlay is never squeezed: at the floor the card is still 880x640 and
#      sits whole inside the window.
#
# And the seam between them. SettingsOverlay punches the chrome buttons out of its
# Region (AppChromeHoles) so they keep working while settings is open. That hole used
# to be the close button only, and a second button changes the geometry, so the last
# section maximises and restores THROUGH the overlay, at the minimum window size --
# where the maximize button is closest to the card and the hole is easiest to lose.
#
# Usage:  powershell -File tools\maximize-check.ps1

. "$PSScriptRoot\_ui.ps1"

$CARD_W = 880
$CARD_H = 640
$MARGIN = 96

Invoke-BBProbe {
    $fail = 0
    $main = Start-BangGang
    $mr0 = Get-WinRect $main
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $floor = Get-MinWindow

    $w0 = $mr0.Right - $mr0.Left; $h0 = $mr0.Bottom - $mr0.Top
    Write-Output ("screen work area : " + $wa.Left + "," + $wa.Top + " " + $wa.Width + "x" + $wa.Height)
    Write-Output ("window           : " + $mr0.Left + "," + $mr0.Top + " ${w0}x${h0}")
    Write-Output ("window floor     : " + $floor.W + "x" + $floor.H + "  (card ${CARD_W}x${CARD_H} + margin $MARGIN)")
    Write-Output ''

    # ---------------- A. the button exists, left of close ----------------

    Write-Output '--- A. chrome buttons ---'
    $b = Get-ChromeButtons $main
    if ($null -eq $b) { throw 'expected two 28x28 buttons on the chrome bar, found a different number' }
    $gap = $b.Close.Left - $b.Max.Right
    $inset = $mr0.Right - $b.Close.Right
    Write-Output ("  maximize : " + $b.Max.Left + "," + $b.Max.Top + " 28x28")
    Write-Output ("  close    : " + $b.Close.Left + "," + $b.Close.Top + " 28x28")
    $ok = ($b.Max.Left -lt $b.Close.Left) -and ($b.Max.Top -eq $b.Close.Top) -and
          ($gap -ge 4) -and ($inset -eq 12)
    Write-Output ("  layout   : maximize is left of close, ${gap}px apart, close inset ${inset}px  " + $(if ($ok) { 'OK' } else { 'FAIL' }))
    if (-not $ok) { $fail++ }

    # ---------------- B. maximize / restore ----------------

    Write-Output ''
    Write-Output '--- B. maximize / restore ---'
    Invoke-MouseClick ($b.Max.Left + 14) ($b.Max.Top + 14)
    Start-Sleep -Milliseconds 1000
    $mr = Get-WinRect $main
    $mw = $mr.Right - $mr.Left; $mh = $mr.Bottom - $mr.Top
    $ok = ($mr.Left -eq $wa.Left) -and ($mr.Top -eq $wa.Top) -and ($mw -eq $wa.Width) -and ($mh -eq $wa.Height)
    Write-Output ("  click    : ${w0}x${h0} at " + $mr0.Left + "," + $mr0.Top + " -> ${mw}x${mh} at " + $mr.Left + "," + $mr.Top + "  " + $(if ($ok) { 'OK -- filled the working area, taskbar left alone' } else { 'FAIL -- expected exactly the working area' }))
    if (-not $ok) { $fail++ }
    Save-WindowShot $main (Get-ShotPath 'maximized.png')

    # the button moved with the window, so re-find it
    $b = Get-ChromeButtons $main
    if ($null -eq $b) { throw 'chrome buttons not found after maximizing' }
    Invoke-MouseClick ($b.Max.Left + 14) ($b.Max.Top + 14)
    Start-Sleep -Milliseconds 1000
    $mr = Get-WinRect $main
    $rw = $mr.Right - $mr.Left; $rh = $mr.Bottom - $mr.Top
    $ok = ($mr.Left -eq $mr0.Left) -and ($mr.Top -eq $mr0.Top) -and ($rw -eq $w0) -and ($rh -eq $h0)
    Write-Output ("  again    : ${mw}x${mh} -> ${rw}x${rh} at " + $mr.Left + "," + $mr.Top + "  " + $(if ($ok) { 'OK -- back to the exact rectangle it came from' } else { 'FAIL -- expected ' + $w0 + 'x' + $h0 + ' at ' + $mr0.Left + ',' + $mr0.Top }))
    if (-not $ok) { $fail++ }

    # ---------------- C. the floor keeps a whole settings card ----------------

    Write-Output ''
    Write-Output '--- C. minimum size still shows the whole settings card ---'
    $mr = Get-WinRect $main
    $midY = $mr.Top + [int](($mr.Bottom - $mr.Top) / 2)
    Invoke-Drag ($mr.Right - 2) $midY ($mr.Right - 900) $midY
    $mr = Get-WinRect $main
    $midX = $mr.Left + [int](($mr.Right - $mr.Left) / 2)
    Invoke-Drag $midX ($mr.Bottom - 2) $midX ($mr.Bottom - 900)
    $mr = Get-WinRect $main
    $w = $mr.Right - $mr.Left; $h = $mr.Bottom - $mr.Top
    $ok = ($w -eq $floor.W) -and ($h -eq $floor.H)
    Write-Output ("  clamped  : ${w}x${h}  " + $(if ($ok) { "OK -- stopped at the floor " + $floor.W + "x" + $floor.H } else { "FAIL -- expected " + $floor.W + "x" + $floor.H }))
    if (-not $ok) { $fail++ }

    $pill = Get-SearchPill $main
    if ($null -eq $pill) { throw 'search pill not found after resizing' }
    $gear = Get-SettingsGear $main $pill
    if ($null -eq $gear) { throw 'settings gear not found after resizing' }
    Invoke-MouseClick ($gear.Left + 14) ($gear.Top + 14)
    Start-Sleep -Milliseconds 1600

    # the card is a real HWND -- look for the child that is exactly the design size
    $card = $null
    foreach ($hh in Get-WinKids $main) {
        $r = Get-WinRect $hh
        if (($r.Right - $r.Left) -eq $CARD_W -and ($r.Bottom - $r.Top) -eq $CARD_H) { $card = $r; break }
    }
    if ($null -eq $card) {
        Write-Output "  card     : FAIL -- no ${CARD_W}x${CARD_H} child; the card shrank with the window"
        $fail++
    }
    else {
        $lm = $card.Left - $mr.Left; $rm = $mr.Right - $card.Right
        $tm = $card.Top - $mr.Top;   $bm = $mr.Bottom - $card.Bottom
        $ok = ($lm -ge 0) -and ($rm -ge 0) -and ($tm -ge 0) -and ($bm -ge 0)
        Write-Output ("  card     : " + $card.Left + "," + $card.Top + " ${CARD_W}x${CARD_H}, margins L${lm} R${rm} T${tm} B${bm}  " + $(if ($ok) { 'OK -- whole card inside the window at the floor' } else { 'FAIL -- the card hangs outside the window' }))
        if (-not $ok) { $fail++ }
    }

    # ---------------- D. both buttons still work through the settings overlay ----------------

    Write-Output ''
    Write-Output '--- D. maximize works with settings open (the Region punch-hole) ---'
    $b = Get-ChromeButtons $main
    if ($null -eq $b) { throw 'chrome buttons not found with settings open' }
    Invoke-MouseClick ($b.Max.Left + 14) ($b.Max.Top + 14)
    Start-Sleep -Milliseconds 1200
    $mr = Get-WinRect $main
    $w = $mr.Right - $mr.Left; $h = $mr.Bottom - $mr.Top
    $ok = ($w -eq $wa.Width) -and ($h -eq $wa.Height)
    Write-Output ("  maximize : ${w}x${h}  " + $(if ($ok) { 'OK -- the click reached the real button through the hole' } else { 'FAIL -- the overlay swallowed it' }))
    if (-not $ok) { $fail++ }
    Save-WindowShot $main (Get-ShotPath 'maximized-settings.png')

    $b = Get-ChromeButtons $main
    Invoke-MouseClick ($b.Max.Left + 14) ($b.Max.Top + 14)
    Start-Sleep -Milliseconds 1200
    $mr = Get-WinRect $main
    $w = $mr.Right - $mr.Left; $h = $mr.Bottom - $mr.Top
    $ok = ($w -eq $floor.W) -and ($h -eq $floor.H)
    Write-Output ("  restore  : ${w}x${h}  " + $(if ($ok) { 'OK -- back to the floor' } else { 'FAIL -- expected ' + $floor.W + 'x' + $floor.H }))
    if (-not $ok) { $fail++ }

    # the close button shares the same mechanism; if the hole grew a second rectangle
    # and got that wrong, this is where it shows up
    $b = Get-ChromeButtons $main
    Invoke-MouseClick ($b.Close.Left + 14) ($b.Close.Top + 14)
    Start-Sleep -Milliseconds 1500
    $alive = @(Get-Process BangGang -ErrorAction SilentlyContinue).Count
    $ok = ($alive -eq 0)
    Write-Output ("  close    : process count $alive  " + $(if ($ok) { 'OK -- settings open, close button still exits' } else { 'FAIL -- the app is still running' }))
    if (-not $ok) { $fail++ }

    Write-Output ''
    if ($fail -eq 0) { Write-Output 'PASS: maximize/restore button works, and the window floor keeps the settings card whole.' }
    else { Write-Output "FAIL: $fail check(s) failed." }
}

Write-BBDone 'maximize-check'
