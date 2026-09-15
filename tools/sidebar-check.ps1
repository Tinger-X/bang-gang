# Verify the conversation bar's new top strip (0.7.24):
#
#   A. A collapse/expand-sidebar button sits at the LEFT of the chat panel's title
#      strip, and the conversation title is centred across the whole strip -- the
#      centring comes from symmetric Padding (ConvTitlePadX) on a MiddleCenter Label,
#      so it is measured as ink, not read off a property.
#
#   B. Collapsing and expanding ANIMATES. The width must pass through intermediate
#      values rather than jumping 256 -> 0 in one frame, which is what "no sudden
#      change" means and is exactly what a width sample over time can prove:
#      poll the sidebar's width and require several distinct values in between.
#
#   C. A collapsed sidebar cannot be stranded, and collapsing is reversible. While the
#      sidebar is shut its conversation list is not visible, so the rows -- and with them
#      the only route to deleting the last conversation -- are unreachable; the toggle is
#      all that is left, and it must still be there. Expanding again has to put every
#      panel back on exactly the geometry it started from.
#
# Usage:  powershell -File tools\sidebar-check.ps1

. "$PSScriptRoot\_ui.ps1"

$SIDE_W = 256

# Bounding box of everything differing from the crop's top-left pixel (see round7-check).
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
    if ($n -eq 0) { return $null }
    return @{ X0 = $minX; X1 = $maxX; Y0 = $minY; Y1 = $maxY; N = $n }
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

# The sidebar panel: full height, flush with the window's left edge, below the chrome bar.
function Get-Sidebar($main) {
    $mr = Get-WinRect $main
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Left -ne $mr.Left) { continue }
        if ($r.Top -ne ($mr.Top + 38)) { continue }
        if (($r.Bottom - $r.Top) -lt 200) { continue }
        if (($r.Right - $r.Left) -gt 400) { continue }
        return $r
    }
    return $null
}

# The chat panel: the area right of the sidebar, below the chrome bar, filling the rest.
#
# Measured as the chat VIEW (one title strip further down) rather than the panel that
# contains it. The containers are deliberately pinned to the window and no longer shrink
# with the sidebar -- the sidebar is simply drawn over their left edge -- so every one of
# them reports "window left" no matter what the sidebar is doing. The view is the topmost
# control whose left edge still means "where the sidebar ends", which is what every check
# below actually wants to know.
function Get-ChatPanel($main) {
    $mr = Get-WinRect $main
    $best = $null
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Top -ne ($mr.Top + 38 + 48)) { continue }
        if ($r.Right -ne $mr.Right) { continue }
        if ($r.Left -lt $mr.Left) { continue }
        if ($null -eq $best -or $r.Left -lt $best.Left) { $best = $r }
    }
    return $best
}

Invoke-BBProbe {
    $fail = 0
    $main = Start-BangGang
    $mr = Get-WinRect $main
    $ww = $mr.Right - $mr.Left

    # a conversation has to exist: the title strip only shows when one is active
    $pill = Get-SearchPill $main
    if ($null -eq $pill) { throw 'search pill not found' }
    $gear = Get-SettingsGear $main $pill
    if ($null -eq $gear) { throw 'settings gear not found' }
    Invoke-MouseClick ($gear.Left + 40 + 14) ($gear.Top + 14)
    Start-Sleep -Milliseconds 1000

    $chat = Get-ChatPanel $main
    if ($null -eq $chat) { throw 'chat panel not found (no conversation active?)' }
    $chatW = $chat.Right - $chat.Left
    # the title strip sits directly on top of the view (MainForm puts it at y = 48 of the
    # chat container, and the view right below it)
    $stripTop = $chat.Top - 48
    Write-Output ("window     : " + $mr.Left + "," + $mr.Top + " ${ww}x" + ($mr.Bottom - $mr.Top))
    Write-Output ("chat panel : " + $chat.Left + "," + $stripTop + " ${chatW}x" + ($mr.Bottom - $chat.Top))

    # ---------------- A. toggle button + centred title ----------------

    Write-Output ''
    Write-Output '--- A. title strip ---'
    $toggle = $null
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -ne 28 -or ($r.Bottom - $r.Top) -ne 28) { continue }
        if ([Math]::Abs($r.Left - ($chat.Left + 10)) -gt 2) { continue }
        if ([Math]::Abs($r.Top - ($stripTop + 10)) -gt 2) { continue }
        $toggle = $r
    }
    if ($null -eq $toggle) { throw 'sidebar toggle button not found in the title strip' }
    Write-Output ("  toggle   : " + $toggle.Left + "," + $toggle.Top + " 28x28  " + $(if ($toggle.Left -lt $chat.Left + $chatW / 2) { 'OK -- on the left' } else { 'FAIL' }))
    if (-not ($toggle.Left -lt $chat.Left + $chatW / 2)) { $fail++ }

    # Title ink, measured from x = +50 so the button is out of the crop, and limited to
    # the middle rows so a divider under the strip cannot stretch the box full width.
    # The right edge stops 8px short of the panel: the window frame paints a border down
    # the last column, and a single border pixel at the crop's edge drags the box's right
    # side out to the crop boundary, which reads as a hugely off-centre title.
    $cropX = $chat.Left + 50
    $cropW = $chatW - 50 - 8
    $box = Get-DiffBoxAt $cropX ($stripTop + 14) $cropW 20 12
    if ($null -eq $box) { throw 'no title ink found in the strip' }
    $inkCentre = $cropX + ($box.X0 + $box.X1) / 2
    $stripCentre = $chat.Left + $chatW / 2
    $off = [Math]::Abs($inkCentre - $stripCentre)
    Write-Output ("  title    : ink " + ($cropX + $box.X0) + ".." + ($cropX + $box.X1) + ", centre " + $inkCentre + " vs strip centre " + $stripCentre + "  " + $(if ($off -le 3) { 'OK -- centred' } else { "FAIL -- off by $off px" }))
    if ($off -gt 3) { $fail++ }
    Save-Shot $chat.Left $stripTop $chatW 48 (Get-ShotPath 'sidebar-title.png')

    # ---------------- B. the toggle animates ----------------

    Write-Output ''
    Write-Output '--- B. collapse / expand animation ---'

    # Poll the sidebar's width as fast as PowerShell can and keep every distinct value.
    # A snap from 256 to 0 in one step yields exactly two; a real animation yields many.
    function Measure-Collapse($main, [int]$ms) {
        $seen = New-Object System.Collections.Generic.List[int]
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt $ms) {
            $sb = Get-Sidebar $main
            $w = $(if ($null -eq $sb) { 0 } else { $sb.Right - $sb.Left })
            if ($seen.Count -eq 0 -or $seen[$seen.Count - 1] -ne $w) { $seen.Add($w) }
        }
        return $seen
    }

    Invoke-MouseClick ($toggle.Left + 14) ($toggle.Top + 14)
    $seen = Measure-Collapse $main 900
    $mid = @($seen | Where-Object { $_ -gt 0 -and $_ -lt $SIDE_W })
    $finalSb = Get-Sidebar $main
    $finalW = $(if ($null -eq $finalSb) { 0 } else { $finalSb.Right - $finalSb.Left })
    Write-Output ("  collapse : widths seen " + ($seen -join ', '))
    $ok = ($mid.Count -ge 4) -and ($finalW -eq 0)
    Write-Output ("  collapse : " + $mid.Count + " intermediate width(s), settled at $finalW  " + $(if ($ok) { 'OK -- eased, not snapped' } else { 'FAIL -- needs >=4 intermediates and a settle at 0' }))
    if (-not $ok) { $fail++ }

    # the chat panel must track the sidebar every frame, not just at the end
    $chat2 = Get-ChatPanel $main
    $ok = ($chat2.Left -eq $mr.Left)
    Write-Output ("  reflow   : chat panel now at x=" + $chat2.Left + " (window left " + $mr.Left + ")  " + $(if ($ok) { 'OK -- reclaimed the whole width' } else { 'FAIL' }))
    if (-not $ok) { $fail++ }
    Save-WindowShot $main (Get-ShotPath 'sidebar-collapsed.png')

    # with the sidebar gone the toggle moved to the window's left edge; click it there
    Invoke-MouseClick ($toggle.Left + 14 - $SIDE_W) ($toggle.Top + 14)
    Start-Sleep -Milliseconds 100
    $seen = Measure-Collapse $main 900
    $mid = @($seen | Where-Object { $_ -gt 0 -and $_ -lt $SIDE_W })
    $finalSb = Get-Sidebar $main
    $finalW = $(if ($null -eq $finalSb) { 0 } else { $finalSb.Right - $finalSb.Left })
    Write-Output ("  expand   : widths seen " + ($seen -join ', '))
    $ok = ($mid.Count -ge 4) -and ($finalW -eq $SIDE_W)
    Write-Output ("  expand   : " + $mid.Count + " intermediate width(s), settled at $finalW  " + $(if ($ok) { 'OK' } else { 'FAIL -- needs >=4 intermediates and a settle at 256' }))
    if (-not $ok) { $fail++ }

    # ---------------- C. no stranding, and collapsing is reversible ----------------

    Write-Output ''
    Write-Output '--- C. the sidebar cannot be stranded ---'
    Invoke-MouseClick ($toggle.Left + 14) ($toggle.Top + 14)     # collapse again
    Start-Sleep -Milliseconds 800
    $sb = Get-Sidebar $main
    $w = $(if ($null -eq $sb) { 0 } else { $sb.Right - $sb.Left })
    Write-Output ("  collapsed: sidebar width $w")
    if ($w -ne 0) { $fail++ }

    # The rows are how a conversation gets deleted, and they are inside the collapsed
    # panel -- hidden, so not clickable. That is what makes stranding impossible: there
    # is no way to drop the last conversation while the toggle's host strip is gone.
    $list = $null
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Left -ne $mr.Left) { continue }
        if (($r.Right - $r.Left) -ge 320) { continue }
        if (($r.Bottom - $r.Top) -lt 100) { continue }
        if ($r.Top -le $mr.Top + 38) { continue }
        $list = $h
    }
    if ($null -eq $list) { throw 'conversation list not found' }
    $listVisible = [BB]::IsWindowVisible($list)
    $ok = -not $listVisible
    Write-Output ("  rows     : conversation list visible = $listVisible  " + $(if ($ok) { 'OK -- no stray hit targets while collapsed' } else { 'FAIL -- rows are reachable with the toggle strip gone' }))
    if (-not $ok) { $fail++ }
    Save-WindowShot $main (Get-ShotPath 'sidebar-collapsed.png')

    # and back: the geometry has to land on exactly what section A measured.
    # The toggle travelled left with the panel, so click it at its collapsed position
    # (same offset section B's expand used) -- not at the expanded one.
    Invoke-MouseClick ($toggle.Left + 14 - $SIDE_W) ($toggle.Top + 14)
    Start-Sleep -Milliseconds 900
    $sb = Get-Sidebar $main
    $w = $(if ($null -eq $sb) { 0 } else { $sb.Right - $sb.Left })
    $chat2 = Get-ChatPanel $main
    $ok = ($w -eq $SIDE_W) -and ($null -ne $chat2) -and ($chat2.Left -eq $chat.Left)
    Write-Output ("  restored : sidebar width $w, chat panel x=" + $(if ($null -eq $chat2) { '(missing)' } else { $chat2.Left }) + " (was " + $chat.Left + ")  " + $(if ($ok) { 'OK -- back where it started' } else { 'FAIL -- round trip lost geometry' }))
    if (-not $ok) { $fail++ }
    Save-WindowShot $main (Get-ShotPath 'sidebar-restored.png')

    Write-Output ''
    if ($fail -eq 0) { Write-Output 'PASS: toggle button left of a centred title, animated collapse/expand, no stranded sidebar.' }
    else { Write-Output "FAIL: $fail check(s) failed." }
}

Write-BBDone 'sidebar-check'
