# Verifies the three visual fixes in 0.7.21.
#
#   1. Search pill: the EDIT's opaque background used to run to the bottom of the
#      198x32 pill and paint over its 1px border. Measure the gap between the
#      EDIT's bottom edge and the pill's, and prove ink still exists in the
#      border strip below the EDIT.
#   2. Password field eye: it must only appear once the field has content.
#      Probe each candidate EDIT with the field empty, then type and re-probe.
#      The eye lives in the field's right 32px, so that is the crop.
#   3. Conversation delete icon: drawn only for the hovered row. Create a
#      conversation, screenshot its row cold, then hover it and compare.
#
# Geometry used below (see SearchField.cs / SettingsWidgets.cs / ConvListBox.cs):
#   search pill 198x32, EDIT inset 10 from the left, 26 reserved on the right
#   InputField 330x42, EDIT inset PadX=12, eye rect = (Width-32, (H-20)/2, 20, 20)
#   ConvListBox items are 95% of SideW=304 wide, centred, 38 tall, 6 apart
#
# Usage:  powershell -File tools\round7-check.ps1

param([string]$Out = '')

. "$PSScriptRoot\_ui.ps1"

if ($Out -eq '') { $Out = Get-ShotPath 'round7-settings.png' }

# Bounding box of everything differing from the crop's top-left pixel by more
# than $Tol in any channel. Unlike Get-InkBox (absolute luminance < 200) this
# works on both light and dark themes -- the eye is a muted grey on a field fill.
function Get-DiffBox($bmp, [int]$Tol = 10) {
    $ref = $bmp.GetPixel(0, 0)
    $minX = -1; $maxX = -1; $minY = -1; $maxY = -1; $n = 0
    for ($x = 0; $x -lt $bmp.Width; $x++) {
        for ($y = 0; $y -lt $bmp.Height; $y++) {
            $c = $bmp.GetPixel($x, $y)
            $d = [Math]::Max([Math]::Abs($c.R - $ref.R),
                 [Math]::Max([Math]::Abs($c.G - $ref.G), [Math]::Abs($c.B - $ref.B)))
            if ($d -le $Tol) { continue }
            $n++
            if ($minX -lt 0 -or $x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($minY -lt 0 -or $y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
    if ($n -eq 0) { return 'blank' }
    return "$minX..$maxX x $minY..$maxY (n=$n)"
}

function Get-DiffBoxAt([int]$x, [int]$y, [int]$w, [int]$h, [int]$Tol = 10) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()
    $res = Get-DiffBox $bmp $Tol
    $bmp.Dispose()
    return $res
}

Invoke-BBProbe {
    $fail = 0
    $main = Start-BangGang
    $mr = Get-WinRect $main
    $ww = $mr.Right - $mr.Left
    $wh = $mr.Bottom - $mr.Top
    Write-Output ("window " + $mr.Left + "," + $mr.Top + " " + $ww + "x" + $wh)

    # ---------------- 1. search pill bottom border ----------------

    $pill = $null
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -eq 198 -and ($r.Bottom - $r.Top) -eq 32) { $pill = $r }
    }
    if ($null -eq $pill) { throw 'search pill not found' }

    $sEdit = $null
    foreach ($h in Get-WinKids $main) {
        if (-not (Get-WinClass $h).StartsWith('WindowsForms10.Edit')) { continue }
        $r = Get-WinRect $h
        if ($r.Left -le $pill.Left -or $r.Right -gt $pill.Right) { continue }
        if ($r.Top -lt $pill.Top -or $r.Bottom -gt $pill.Bottom) { continue }
        $sEdit = $r
    }
    if ($null -eq $sEdit) { throw 'search EDIT not found' }

    $gap = $pill.Bottom - $sEdit.Bottom
    Write-Output ''
    Write-Output '--- 1. search pill bottom border ---'
    Write-Output ("  pill      : " + $pill.Left + "," + $pill.Top + " 198x32  (rows " + $pill.Top + ".." + ($pill.Bottom - 1) + ")")
    Write-Output ("  EDIT      : " + $sEdit.Left + "," + $sEdit.Top + " " + ($sEdit.Right - $sEdit.Left) + "x" + ($sEdit.Bottom - $sEdit.Top) + "  (last row " + ($sEdit.Bottom - 1) + ")")
    Write-Output ("  clearance : " + $gap + "px of pill left below the EDIT  " + $(if ($gap -ge 3) { 'OK' } else { 'FAIL -- EDIT covers the border' }))
    if ($gap -lt 3) { $fail++ }

    # the border row itself: ink must survive below the EDIT's opaque fill
    $strip = Get-DiffBoxAt ($pill.Left + 30) ($sEdit.Bottom) 100 ($pill.Bottom - $sEdit.Bottom) 6
    Write-Output ("  border strip rows " + $sEdit.Bottom + ".." + ($pill.Bottom - 1) + " : " + $strip + "  " + $(if ($strip -eq 'blank') { 'FAIL -- no border ink' } else { 'OK' }))
    if ($strip -eq 'blank') { $fail++ }
    Save-Shot ($pill.Left - 2) ($pill.Top - 2) 206 40 (Get-ShotPath 'round7-search-pill.png')

    # ---------------- 3. conversation delete icon ----------------

    # items only exist once there is a conversation; the plus button is 28x28 at
    # (SideW-46, 9) inside the 46-tall head strip that starts at y=ChromeH+100.
    $plusX = $mr.Left + 304 - 46 + 14
    $plusY = $mr.Top + 38 + 100 + 9 + 14
    Invoke-MouseClick $plusX $plusY
    Start-Sleep -Milliseconds 900

    # find the list control itself rather than deriving its origin. The sidebar
    # holds two 304-wide children: the 46-tall search/head strip and the list
    # below it, so the height is what tells them apart.
    $list = $null
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -ne 304) { continue }
        if (($r.Bottom - $r.Top) -lt 100) { continue }
        if ($r.Top -le $mr.Top + 38) { continue }
        $list = $r
    }
    if ($null -eq $list) { throw 'conversation list not found' }

    # item 0: 95% of 304 -> 289 wide, centred at x=7, 38 tall, DelRect inset 8/6
    $rowX = $list.Left + 7
    $rowY = $list.Top
    $delX = $rowX + 289 - 6 - 22
    $delY = $rowY + 8
    Write-Output ''
    Write-Output ("  list      : " + $list.Left + "," + $list.Top + " 304x" + ($list.Bottom - $list.Top))
    Write-Output ("  row 0     : " + $rowX + "," + $rowY + " 289x38")

    # park the pointer well away from the list first so no row is hovered
    [void][BB]::SetCursorPos(($mr.Left + $ww - 400), ($mr.Top + $wh - 60))
    Start-Sleep -Milliseconds 700
    $delCold = Get-DiffBoxAt $delX $delY 22 22 12

    # now hover the row (but not the icon) and let the hover repaint land
    [void][BB]::SetCursorPos(($rowX + 60), ($rowY + 19))
    Start-Sleep -Milliseconds 700
    $delHot = Get-DiffBoxAt $delX $delY 22 22 12

    Write-Output ''
    Write-Output '--- 3. conversation delete icon ---'
    Write-Output ("  row 0 delete slot (cold, no hover) : " + $delCold + "  " + $(if ($delCold -eq 'blank') { 'OK' } else { 'FAIL -- icon drawn without hover' }))
    Write-Output ("  row 0 delete slot (row hovered)    : " + $delHot + "  " + $(if ($delHot -eq 'blank') { 'FAIL -- icon missing on hover' } else { 'OK' }))
    if ($delCold -ne 'blank') { $fail++ }
    if ($delHot -eq 'blank') { $fail++ }
    Save-Shot ($rowX - 2) ($rowY - 2) 300 42 (Get-ShotPath 'round7-del-hot.png')

    # park the pointer again so it cannot hover anything while we open settings
    [void][BB]::SetCursorPos(($mr.Left + $ww - 400), ($mr.Top + $wh - 60))
    Start-Sleep -Milliseconds 400

    # ---------------- 2. password field eye ----------------

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
    Start-Sleep -Milliseconds 1200
    Invoke-MouseClick ($cardX + 14 + 90) ($cardY + 96 + 48 * 1 + 21)   # page 1 = model access
    Start-Sleep -Milliseconds 1200

    Write-Output ''
    Write-Output '--- 2. password field eye ---'

    # Every InputField is 330x42 and its EDIT is inset PadX=12 from the left and
    # 12 from the top (Ui.EditBox: (42-18)/2), so the field origin is the EDIT's
    # minus (12,12). The eye sits at (field.Width-32, (42-20)/2) = +298,+11.
    #
    # Only fields FULLY inside the card count. A control scrolled below the
    # card's visible area is still IsWindowVisible, but copying its nominal
    # screen rect picks up whatever is painted there instead -- measuring those
    # is how the first run of this probe reported phantom "eyes".
    $cardR = New-Object BB+RECT
    $cardR.Left = $cardX; $cardR.Top = $cardY; $cardR.Right = $cardX + 880; $cardR.Bottom = $cardY + 640

    $fields = @()
    foreach ($h in Get-WinKids $main) {
        if (-not (Get-WinClass $h).StartsWith('WindowsForms10.Edit')) { continue }
        if (-not [BB]::IsWindowVisible($h)) { continue }
        $r = Get-WinRect $h
        $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
        if ($ht -lt 18 -or $ht -gt 30) { continue }
        if ($w -ne 306 -and $w -ne 278) { continue }
        $fx = $r.Left - 12; $fy = $r.Top - 12
        if ($fx -lt $cardR.Left + 208 -or $fx + 330 -gt $cardR.Right - 20) { continue }
        if ($fy -lt $cardR.Top + 8 -or $fy + 42 -gt $cardR.Bottom - 56) { continue }
        $fields += ,@{ H = $h; R = $r }
    }
    Write-Output ("  candidate edits fully inside the card: " + $fields.Count)

    $eyedBefore = 0; $eyedAfter = 0
    for ($i = 0; $i -lt $fields.Count; $i++) {
        $r = $fields[$i].R
        $ex = $r.Left - 12 + 330 - 32; $ey = $r.Top - 12 + 11

        # clear it first: whatever the saved settings put in there, an empty
        # field must not offer an eye to click.
        Invoke-MouseClick ($r.Left + 20) ($r.Top + [int](($r.Bottom - $r.Top) / 2))
        Start-Sleep -Milliseconds 250
        [void][BB]::SendMessage($fields[$i].H, 0x00B1, [IntPtr]::Zero, [IntPtr](-1))   # EM_SETSEL all
        Start-Sleep -Milliseconds 200
        [void][BB]::SendMessage($fields[$i].H, 0x00AC, [IntPtr]::Zero, [IntPtr]::Zero) # WM_CLEAR
        Start-Sleep -Milliseconds 600
        $empty = Get-DiffBoxAt $ex $ey 22 22 12
        if ($empty -ne 'blank') { $eyedBefore++ }

        [void][BB]::SendText($fields[$i].H, 'sk-test-1234')
        Start-Sleep -Milliseconds 600
        $filled = Get-DiffBoxAt $ex $ey 22 22 12
        if ($filled -ne 'blank') { $eyedAfter++ }

        $nw = (Get-WinRect $fields[$i].H).Right - (Get-WinRect $fields[$i].H).Left
        Write-Output ("  field $i  edit w " + ($r.Right - $r.Left) + " -> $nw   empty: " + $empty + "   filled: " + $filled)
    }

    Write-Output ("  fields showing an eye while EMPTY  : $eyedBefore  " + $(if ($eyedBefore -eq 0) { 'OK' } else { 'FAIL' }))
    Write-Output ("  fields showing an eye when FILLED  : $eyedAfter  " + $(if ($eyedAfter -gt 0) { 'OK' } else { 'FAIL' }))
    if ($eyedBefore -ne 0) { $fail++ }
    if ($eyedAfter -le 0) { $fail++ }

    Save-WindowShot $main $Out

    Write-Output ''
    if ($fail -eq 0) { Write-Output 'PASS: search border clear, delete icon hover-only, eye gated on content.' }
    else { Write-Output "FAIL: $fail check(s) failed." }
}

Write-BBDone 'round7-check'
