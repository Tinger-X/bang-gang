# Verify the unsaved-changes guard covers quitting the whole app (0.7.21).
#
# Settings is an in-window overlay, and the close button's rect is punched out of
# its Region so the real button still receives the click. That means the app could
# be quit straight from the settings screen -- silently discarding unsaved edits.
# The close now flows through MainForm.OnFormClosing, which asks the overlay first.
#
# Sequence:
#   A. no settings open          -> close button quits immediately
#   B. settings open + dirty edit-> close button must NOT quit, confirm bar appears
#                                   with "放弃并退出"; "继续编辑" keeps the app alive
#                                   and hides the bar; close again -> bar returns;
#                                   "放弃并退出" finally quits
#
# The confirm bar is found by control TEXT, not by arithmetic: its title label and
# its buttons are real HWNDs, so WM_GETTEXT across the process boundary names them.
#
# Layout reference (SettingsOverlay.LayoutCard):
#   confirm card 400x180 centred in the window
#   title  (24, 30, 352, 26)          quit button (200, 118, 116, 34)
#
# Usage:  powershell -File tools\quit-guard.ps1
#
# ASCII ONLY (CLAUDE.md hard constraint): PS 5.1 reads a BOM-less .ps1 as ANSI,
# so a literal Chinese label here is a parse error. The labels we have to match
# are built from code points instead.

. "$PSScriptRoot\_ui.ps1"

# Build a string from code points, e.g.  U 0x7EE7,0x7EED -> the two-char label.
function U { param([int[]]$Codes) return (-join ($Codes | ForEach-Object { [char]$_ })) }

$CONFIRM_TITLE = U 0x653E,0x5F03,0x672A,0x4FDD,0x5B58,0x7684,0x4FEE,0x6539,0xFF1F  # discard unsaved changes?
$STAY_LABEL    = U 0x7EE7,0x7EED,0x7F16,0x8F91                                    # keep editing
$QUIT_LABEL    = U 0x653E,0x5F03,0x5E76,0x9000,0x51FA                              # discard and quit

function Get-Procs { return @(Get-Process BangGang -ErrorAction SilentlyContinue).Count }

# HWND of the first visible child whose text is exactly $Want.
function Find-ByText($root, [string]$Want) {
    foreach ($h in Get-WinKids $root) {
        if (-not [BB]::IsWindowVisible($h)) { continue }
        if ((Get-WinText $h) -eq $Want) { return $h }
    }
    return [IntPtr]::Zero
}

function Get-ConfirmState($main) {
    $title = Find-ByText $main $CONFIRM_TITLE
    if ($title -eq [IntPtr]::Zero) { return $null }
    return @{ Title = (Get-WinRect $title); Button = (Find-ByText $main $QUIT_LABEL) }
}

Invoke-BBProbe {
    $fail = 0
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

    # ---------------- A. nothing to guard ----------------

    Write-Output '--- A. close button with settings closed ---'
    Invoke-MouseClick $closeX $closeY
    Start-Sleep -Milliseconds 1600
    $a = Get-Procs
    Write-Output ("  processes after close = $a  " + $(if ($a -eq 0) { 'OK' } else { 'FAIL -- should quit immediately' }))
    if ($a -ne 0) { $fail++ }

    # ---------------- B. dirty settings, then close ----------------

    $main = Start-BangGang
    $mr = Get-WinRect $main
    $ww = $mr.Right - $mr.Left

    Invoke-MouseClick ($gear.Left + 14) ($gear.Top + 14)
    Start-Sleep -Milliseconds 1400

    # page 1 = model access; type into the first field to make the page dirty
    $cardX = $mr.Left + [int](($ww - 880) / 2)
    $cardY = $mr.Top + [int](($wh - 640) / 2)
    Invoke-MouseClick ($cardX + 14 + 90) ($cardY + 96 + 48 + 21)
    Start-Sleep -Milliseconds 1200

    $edit = [IntPtr]::Zero
    foreach ($h in Get-WinKids $main) {
        if (-not (Get-WinClass $h).StartsWith('WindowsForms10.Edit')) { continue }
        if (-not [BB]::IsWindowVisible($h)) { continue }
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -ne 306) { continue }
        if ($r.Left -lt $cardX + 208) { continue }
        $edit = $h; break
    }
    if ($edit -eq [IntPtr]::Zero) { throw 'no model-access field found to dirty' }

    $er = Get-WinRect $edit
    Invoke-MouseClick ($er.Left + 20) ($er.Top + 12)
    Start-Sleep -Milliseconds 300
    [void][BB]::SendText($edit, 'dirty-edit')
    Start-Sleep -Milliseconds 800
    Write-Output ''
    Write-Output '--- B. settings open with an unsaved edit ---'
    Write-Output ("  processes before close = " + (Get-Procs) + "  (page dirtied)")

    # B1: the close button must be refused and raise the confirm bar
    Invoke-MouseClick $closeX $closeY
    Start-Sleep -Milliseconds 1300
    $b1 = Get-Procs
    $st = Get-ConfirmState $main
    Write-Output ("  after close button : processes = $b1  " + $(if ($b1 -eq 1) { 'OK' } else { 'FAIL -- quit without asking' }))
    if ($b1 -ne 1) { $fail++ }
    if ($null -eq $st) {
        Write-Output '  confirm bar        : NOT FOUND  FAIL'
        $fail++
    }
    else {
        $t = $st.Title
        $out = ($t.Left.ToString() + ',' + $t.Top.ToString())
        Write-Output ("  confirm bar        : title at $out")
        $bt = $st.Button
        if ($bt -eq [IntPtr]::Zero) {
            Write-Output '  quit button        : MISSING (label not "discard and quit"?)  FAIL'
            $fail++
        }
        else {
            $br = Get-WinRect $bt
            Write-Output ('  quit button        : ' + $br.Left + ',' + $br.Top + ' ' + ($br.Right - $br.Left) + 'x' + ($br.Bottom - $br.Top) + '  OK')
        }
    }
    Save-WindowShot $main (Get-ShotPath 'quit-guard-confirm.png')

    # B2: "keep editing" must keep the app alive and hide the bar
    $stay = Find-ByText $main $STAY_LABEL
    if ($stay -eq [IntPtr]::Zero) { throw 'stay button not found' }
    $sr = Get-WinRect $stay
    Invoke-MouseClick ($sr.Left + [int](($sr.Right - $sr.Left) / 2)) ($sr.Top + [int](($sr.Bottom - $sr.Top) / 2))
    Start-Sleep -Milliseconds 1000
    $b2 = Get-Procs
    $gone = (Find-ByText $main $CONFIRM_TITLE) -eq [IntPtr]::Zero
    Write-Output ("  after 'keep editing': processes = $b2  " + $(if ($b2 -eq 1) { 'OK' } else { 'FAIL -- app died' }) + ", confirm dismissed = $gone  " + $(if ($gone) { 'OK' } else { 'FAIL -- bar still up' }))
    if ($b2 -ne 1) { $fail++ }
    if (-not $gone) { $fail++ }

    # B3: close again, then take the quit option
    Invoke-MouseClick $closeX $closeY
    Start-Sleep -Milliseconds 1300
    $quit = Find-ByText $main $QUIT_LABEL
    if ($quit -eq [IntPtr]::Zero) { throw 'quit button not found on the second ask' }
    $qr = Get-WinRect $quit
    Invoke-MouseClick ($qr.Left + [int](($qr.Right - $qr.Left) / 2)) ($qr.Top + [int](($qr.Bottom - $qr.Top) / 2))
    Start-Sleep -Milliseconds 1800
    $b3 = Get-Procs
    Write-Output ("  after 'discard+quit': processes = $b3  " + $(if ($b3 -eq 0) { 'OK' } else { 'FAIL -- app still running' }))
    if ($b3 -ne 0) { $fail++ }

    Write-Output ''
    if ($fail -eq 0) { Write-Output 'PASS: quitting with dirty settings asks first, and obeys the answer.' }
    else { Write-Output "FAIL: $fail check(s) failed." }
}

Write-BBDone 'quit-guard'
