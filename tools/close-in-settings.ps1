# Verify the app's own close button still quits while the settings overlay is open.
#
# The overlay covers the whole window -- that is exactly what makes the main UI
# unclickable while settings is open -- so it used to swallow clicks on the close
# button too, and the user had to dismiss settings first. It now punches a hole in
# its own Region for that one button.
#
# Sequence: open settings -> click a spot behind the overlay (must NOT quit)
#           -> click the close button (must quit).
#
# Usage:  powershell -File tools\close-in-settings.ps1

. "$PSScriptRoot\_ui.ps1"

Invoke-BBProbe {
    $main = Start-BangGang
    $mr = Get-WinRect $main
    $ww = $mr.Right - $mr.Left
    $wh = $mr.Bottom - $mr.Top

    # the close button sits at (Width - 40, 5) inside the 38px chrome bar
    $closeX = $mr.Left + $ww - 40 + 14
    $closeY = $mr.Top + 5 + 14

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

    $closeHwnd = [IntPtr]::Zero
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Left -ne ($mr.Right - 40) -or $r.Top -ne ($mr.Top + 5)) { continue }
        if (($r.Right - $r.Left) -ne 28) { continue }
        $closeHwnd = $h
    }
    Write-Output ("close button hwnd   : " + $closeHwnd + "   (zero means it is not where we think it is)")
    Write-Output ("close button target : " + $closeX + "," + $closeY)
    Write-Output ("settings gear       : " + ($gear.Left + 14) + "," + ($gear.Top + 14))

    Invoke-MouseClick ($gear.Left + 14) ($gear.Top + 14)
    Start-Sleep -Milliseconds 1400

    Save-WindowShot $main (Get-ShotPath 'close-in-settings-open.png')
    $open = @(Get-Process BangGang -ErrorAction SilentlyContinue).Count
    Write-Output ("settings opened, processes = " + $open + "   (must be 1)")

    # a spot behind the overlay: the click must be eaten, not reach the sidebar
    Invoke-MouseClick ($mr.Left + 40) ($mr.Top + $wh - 60)
    Start-Sleep -Milliseconds 900
    $covered = @(Get-Process BangGang -ErrorAction SilentlyContinue).Count
    Write-Output ("click on covered sidebar, processes = " + $covered + "   (must be 1)")

    # the real close button: must quit
    Invoke-MouseClick $closeX $closeY
    Start-Sleep -Milliseconds 1600
    $after = @(Get-Process BangGang -ErrorAction SilentlyContinue).Count
    Write-Output ("click on close button, processes = " + $after + "   (must be 0)")

    if ($covered -ne 1) { throw 'the overlay stopped blocking clicks on the covered UI' }
    if ($after -ne 0) { throw 'the close button did not quit while settings was open' }
    Write-Output ''
    Write-Output 'PASS: settings is open, covered UI is inert, close button quits.'
}

Write-BBDone 'close-in-settings'
