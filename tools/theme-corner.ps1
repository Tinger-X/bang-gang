# The sidebar collapse/expand button sits in the chat title strip. It is an IconButton: an
# opaque circle painted into a rectangle, so the four corners of that rectangle are nothing
# but the control's BackColor. Restyle() takes BackColor from the panel the button is on --
# and this particular button is a child of _chatUI (ChatBg) while it is drawn on top of the
# SIBLING label _convTitle (PanelBg). Reading Parent.BackColor therefore pastes a square of
# ChatBg onto the PanelBg strip: exactly the reported "the non-rounded part did not follow
# the theme".
#
# It is only visible after a theme switch, because ApplyThemeUi -- the sole caller of the
# restyle sweep -- never runs at startup. So the check is: measure the corners, switch the
# theme through the settings UI the way a user would, measure again.
#
# "Correct" is not a colour constant here: a corner must be indistinguishable from the
# surface 6px to its left, i.e. from the strip itself. In the dark theme ChatBg (23,25,29)
# and PanelBg (34,37,43) differ by 11 and the bug is loud; in the light theme they differ
# by 3 and it is invisible. All three modes are stepped through, and the run must have
# produced at least two distinct strip colours, so that "the clicks missed the control"
# cannot be mistaken for a pass.
#
# Usage:  powershell -File tools\theme-corner.ps1

. "$PSScriptRoot\_ui.ps1"

$script:fail = 0

# 11 units of separation in the dark theme, 3 in the light one: 6 splits them cleanly.
$script:Tol = 6

# Chinese text has to be built from code points -- this file must stay pure ASCII, since
# PowerShell 5.1 reads a BOM-less file as ANSI and a raw CJK byte is a syntax error.
function U { param([int[]]$Codes) return (-join ($Codes | ForEach-Object { [char]$_ })) }

function Get-Px([int]$x, [int]$y) {
    $bmp = New-Object System.Drawing.Bitmap 1, 1
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size 1, 1))
    $g.Dispose()
    $c = $bmp.GetPixel(0, 0)
    $bmp.Dispose()
    return $c
}

function Dist($a, $b) {
    return [Math]::Max([Math]::Abs($a.R - $b.R),
           [Math]::Max([Math]::Abs($a.G - $b.G), [Math]::Abs($a.B - $b.B)))
}

# Get-WinRect hands back a BB+RECT: Left / Top / Right / Bottom and nothing else. There is
# no Width or Height -- and a missing property is not an error in PowerShell, it is $null,
# so "$rect.Width * 0.12" silently evaluates to 0 and every click lands on the top-left
# pixel. Always go through these two.
function RW($r) { return ($r.Right - $r.Left) }
function RH($r) { return ($r.Bottom - $r.Top) }

# Centre of a rect, plus an offset in each direction when a click needs to be off-centre.
# The two halves are computed into locals first: written inline as "@(a / 2 + $dx, b / 2)"
# the comma binds before the division and PowerShell then tries to divide an array.
function Mid($r, [int]$dx = 0, [int]$dy = 0) {
    $x = ($r.Left + $r.Right) / 2 + $dx
    $y = ($r.Top + $r.Bottom) / 2 + $dy
    return @($x, $y)
}

function Show($c) { return ("(" + $c.R + "," + $c.G + "," + $c.B + ")") }

# The collapse/expand button: the only 28x28 control in the chat title strip, i.e. below
# the chrome row and above the end of that 48px strip. Located by shape, never by x -- its
# x follows the sidebar.
function Get-Toggle($main) {
    $mr = Get-WinRect $main
    foreach ($h in Get-WinKids $main) {
        if (-not [BB]::IsWindowVisible($h)) { continue }
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -ne 28 -or ($r.Bottom - $r.Top) -ne 28) { continue }
        if ($r.Top -le ($mr.Top + 38)) { continue }
        if ($r.Top -ge ($mr.Top + 38 + 48)) { continue }
        return $r
    }
    return $null
}

# Corners are sampled at +1 / +26 inside a 28x28 button: the circle is inscribed in
# (1,1)-(26,26), so each of those pixels is ~18px from the centre -- 5px outside the 12.5px
# radius, far enough that no antialiasing reaches it. Nothing but BackColor can be there.
#
# Reports through Write-Host, not Write-Output: the caller collects the return value, and
# anything written to the pipeline would be collected alongside it.
function Test-Corners($main, [string]$Tag) {
    $t = Get-Toggle $main
    if ($null -eq $t) { throw "sidebar toggle not found ($Tag)" }
    # 6px left of the button: still inside the title strip (the button sits at +10 of it),
    # far enough from the circle for the reference to be pure strip.
    $ref = Get-Px ($t.Left - 6) ($t.Top + 14)
    $vals = @()
    $worst = 0
    foreach ($p in @(@(1, 1), @(26, 1), @(1, 26), @(26, 26))) {
        $c = Get-Px ($t.Left + $p[0]) ($t.Top + $p[1])
        $vals += (Show $c)
        $d = Dist $c $ref
        if ($d -gt $worst) { $worst = $d }
    }
    $ok = $worst -le $script:Tol
    Write-Host ("  " + $Tag.PadRight(10) + " strip " + (Show $ref) +
                "  corners " + ($vals -join ' ') + "  max diff $worst  " +
                $(if ($ok) { 'OK -- the corners are the strip' } else { 'FAIL -- square corners keep the previous theme colour' }))
    if (-not $ok) { $script:fail++ }
    return (Show $ref)
}

# Visible controls of an exact size, as rects.
function Find-Sized($main, [int]$w, [int]$h) {
    $out = @()
    foreach ($k in Get-WinKids $main) {
        if (-not [BB]::IsWindowVisible($k)) { continue }
        $r = Get-WinRect $k
        if (($r.Right - $r.Left) -eq $w -and ($r.Bottom - $r.Top) -eq $h) { $out += , $r }
    }
    return $out
}

# The settings rail's nav rows: one column, so they share a left edge. Taking them by shape
# rather than by the card's origin keeps this working if the card is ever resized.
function Find-NavRow($main, [int]$Index) {
    $cands = @()
    foreach ($k in Get-WinKids $main) {
        if (-not [BB]::IsWindowVisible($k)) { continue }
        $r = Get-WinRect $k
        $w = $r.Right - $r.Left
        if (($r.Bottom - $r.Top) -ne 42) { continue }
        if ($w -lt 120 -or $w -gt 220) { continue }
        $cands += , $r
    }
    if ($cands.Count -lt 3) { throw ("expected the rail's nav rows, found " + $cands.Count) }
    $left = ($cands | ForEach-Object { $_.Left } | Measure-Object -Minimum).Minimum
    $rows = @($cands | Where-Object { $_.Left -eq $left } | Sort-Object { $_.Top })
    if ($rows.Count -le $Index) { throw ("nav row $Index missing (" + $rows.Count + " rows)") }
    return $rows[$Index]
}

# The theme segmented control: the only 34px-tall control on the appearance page. Its three
# segments live inside one HWND, so they can only be addressed by x offset -- see the
# fractions at the call site for why they are safe.
function Find-Segmented($main) {
    $cands = @()
    foreach ($k in Get-WinKids $main) {
        if (-not [BB]::IsWindowVisible($k)) { continue }
        $r = Get-WinRect $k
        $w = $r.Right - $r.Left
        if (($r.Bottom - $r.Top) -ne 34) { continue }
        if ($w -lt 120) { continue }
        $cands += , $r
    }
    if ($cands.Count -eq 0) { throw 'theme segmented control not found on the appearance page' }
    foreach ($c in $cands) { Write-Host ("  candidate 34x" + ($c.Right - $c.Left) + " at " + $c.Left + "," + $c.Top) }
    return ($cands | Sort-Object { $_.Top })[0]
}

function Find-ByText($root, $Want) {
    foreach ($k in Get-WinKids $root) {
        if (-not [BB]::IsWindowVisible($k)) { continue }
        if ((Get-WinText $k) -eq $Want) { return $k }
    }
    return $null
}

# The settings card: a fixed-size panel centred in the window (SettingsOverlay.LayoutCard).
# Far larger than anything else inside the dialog, and the only control whose centre is the
# window's centre.
function Find-Card($main) {
    $mr = Get-WinRect $main
    $cx = ($mr.Left + $mr.Right) / 2
    $cy = ($mr.Top + $mr.Bottom) / 2
    $best = $null
    foreach ($k in Get-WinKids $main) {
        if (-not [BB]::IsWindowVisible($k)) { continue }
        $r = Get-WinRect $k
        $w = $r.Right - $r.Left
        $h = $r.Bottom - $r.Top
        if ($w -lt 520 -or $w -gt 900) { continue }
        if ($h -lt 380 -or $h -gt 700) { continue }
        if ([Math]::Abs((($r.Left + $r.Right) / 2) - $cx) -gt 2) { continue }
        if ([Math]::Abs((($r.Top + $r.Bottom) / 2) - $cy) -gt 2) { continue }
        $best = $r
    }
    return $best
}

# The card's own close button: the topmost 28x28 inside the card and in its right half.
# The brand mark is the other 28x28 up there and sits on the left, and the main window's
# chrome buttons are above the card altogether -- taking the window's rectangle here would
# click the app's own close button, which raises the unsaved-changes bar and blocks the
# footer from then on.
function Find-OverlayClose($main, $card) {
    if ($null -eq $card) { return $null }
    $mid = ($card.Left + $card.Right) / 2
    $best = $null
    foreach ($r in (Find-Sized $main 28 28)) {
        if ($r.Left -lt $mid -or $r.Right -gt $card.Right) { continue }
        if ($r.Top -lt $card.Top -or $r.Bottom -gt $card.Bottom) { continue }
        if ($null -eq $best -or $r.Top -lt $best.Top) { $best = $r }
    }
    return $best
}

# What the app has actually persisted. The settings dialog writes this file on save, so it
# is the ground truth for "did the segment click and the save click land?" -- without it a
# missed click is indistinguishable from a theme that happened not to change.
# The serializer writes the property names as declared, so the key is "ThemeMode".
function Get-SavedMode([string]$Path) {
    if ([string]::IsNullOrEmpty($Path)) { return '(no path)' }
    if (-not (Test-Path $Path)) { return '(no settings.json)' }
    $m = [regex]::Match((Get-Content $Path -Raw), '"ThemeMode"\s*:\s*"([a-z]+)"', 'IgnoreCase')
    if ($m.Success) { return $m.Groups[1].Value }
    return '(no ThemeMode)'
}

# Everything the probe body needs has to live in the SCRIPT scope: the block runs in a child
# scope of its own, so a plain "$refs = @()" up here is invisible in there -- and "$refs += x"
# on an undefined name starts from $null, which silently turns the whole thing into string
# concatenation instead of a list.
$script:settingsPath = Join-Path (Split-Path $script:BBExe -Parent) 'settings.json'
$hadSettings = Test-Path $script:settingsPath
$savedSettings = $null
if ($hadSettings) { $savedSettings = [System.IO.File]::ReadAllBytes($script:settingsPath) }

$script:refs = @()
try {
    Invoke-BBProbe {
        $main = Start-BangGang
        $mr = Get-WinRect $main

        # a conversation, so the title strip (and with it the button) exists at all
        $pill = Get-SearchPill $main
        if ($null -eq $pill) { throw 'search pill not found' }
        $gear = Get-SettingsGear $main $pill
        if ($null -eq $gear) { throw 'settings gear not found' }
        Invoke-MouseClick ($gear.Left + 40 + 14) ($gear.Top + 14)
        Start-Sleep -Milliseconds 1200

        Write-Output ''
        Write-Output '--- the button as it is when the app starts ---'
        Write-Output ("  window   : " + $mr.Left + "," + $mr.Top + " " + ($mr.Right - $mr.Left) + "x" + ($mr.Bottom - $mr.Top))
        $script:refs += Test-Corners $main 'startup'
        $t = Get-Toggle $main
        Save-Shot ($t.Left - 30) ($t.Top - 6) 90 40 (Get-ShotPath 'theme-corner-startup.png')

        # ---- drive the theme through the settings UI, like a user would ----

        # Re-resolve everything the dialog needs each time round: the click targets are
        # inside a page that only exists while the dialog is open.
        function Open-AppearancePage($main, $gear) {
            Invoke-MouseClick ($gear.Left + 14) ($gear.Top + 14)   # the gear itself = open settings
            Start-Sleep -Milliseconds 1400
            $nav = Find-NavRow $main 3                              # 4th rail row = the appearance page
            Invoke-MouseClick ($nav.Left + 20) ($nav.Top + 21)
            Start-Sleep -Milliseconds 900
        }

        function Get-DialogParts($main) {
            $card = Find-Card $main
            $saveH = Find-ByText $main (U 0x4FDD, 0x5B58)           # "save"
            if ($null -eq $saveH) { throw 'save button not found' }
            return @{
                Card = $card
                Seg = Find-Segmented $main
                # Find-ByText hands back an HWND (cross-process control text needs WM_GETTEXT,
                # see _ui.ps1); an IntPtr has no Left/Top, so turn it into a rect first.
                Save = Get-WinRect $saveH
                Close = Find-OverlayClose $main $card
            }
        }

        Open-AppearancePage $main $gear
        $d = Get-DialogParts $main
        if ($null -eq $d.Close) { throw 'settings close button not found' }
        Write-Host ("  card     : " + $d.Card.Left + "," + $d.Card.Top + " " + ($d.Card.Right - $d.Card.Left) + "x" + ($d.Card.Bottom - $d.Card.Top))
        Write-Host ("  segmented: " + $d.Seg.Left + "," + $d.Seg.Top + " " + ($d.Seg.Right - $d.Seg.Left) + "x" + ($d.Seg.Bottom - $d.Seg.Top))
        Write-Host ("  save     : " + $d.Save.Left + "," + $d.Save.Top + " " + ($d.Save.Right - $d.Save.Left) + "x" + ($d.Save.Bottom - $d.Save.Top))
        Write-Host ("  close    : " + $d.Close.Left + "," + $d.Close.Top + " " + ($d.Close.Right - $d.Close.Left) + "x" + ($d.Close.Bottom - $d.Close.Top))

        # Segment x offsets, as fractions of the control's width. Each segment is at least
        # 48px wide and the two short labels are the same length, so 0.12 / 0.5 / 0.85 land
        # in segments 0 / 1 / 2 for any text width -- the boundaries sit at
        # (3+w0)/total and (3+2*w0)/total, both of which bracket 0.5 from either side.
        $fracs = @(0.12, 0.5, 0.85)
        $i = 0
        foreach ($f in $fracs) {
            $i++
            Write-Output ''
            Write-Output ("--- theme switch " + $i + " of " + $fracs.Count + " (segment at " + $f + " of the width) ---")
            Invoke-MouseClick ($d.Seg.Left + [int]([double](RW $d.Seg) * $f)) ($d.Seg.Top + [int]((RH $d.Seg) / 2))
            Start-Sleep -Milliseconds 500
            $c = Mid $d.Save
            Invoke-MouseClick $c[0] $c[1]
            Start-Sleep -Milliseconds 1000
            Write-Host ("  saved mode: " + (Get-SavedMode $script:settingsPath))
            $c = Mid $d.Close
            Invoke-MouseClick $c[0] $c[1]
            Start-Sleep -Milliseconds 800

            $script:refs += Test-Corners $main ("switch " + $i)
            $t = Get-Toggle $main
            Save-Shot ($t.Left - 30) ($t.Top - 6) 90 40 (Get-ShotPath ("theme-corner-switch" + $i + ".png"))

            # back into the settings dialog for the next segment
            if ($i -lt $fracs.Count) {
                Open-AppearancePage $main $gear
                $d = Get-DialogParts $main
                if ($null -eq $d.Close) { throw 'settings close button not found' }
            }
        }

        # a pass is only worth anything if the theme actually moved
        Write-Output ''
        $distinct = @($script:refs | Sort-Object -Unique)
        $ok = $distinct.Count -ge 2
        Write-Output ("  strip colours seen: " + ($script:refs -join ' ') + "  (" + $distinct.Count + " distinct)  " +
                      $(if ($ok) { 'OK -- the theme really switched' } else { 'FAIL -- the theme never changed, so nothing was tested' }))
        if (-not $ok) { $script:fail++ }

        Write-Output ''
        if ($script:fail -eq 0) { Write-Output 'PASS: the toggle button corners follow the theme through a live switch.' }
        else { Write-Output ("FAIL: " + $script:fail + " check(s) failed.") }
    }
}
finally {
    # after the app is gone: put settings.json back the way it was
    if ($hadSettings) { [System.IO.File]::WriteAllBytes($script:settingsPath, $savedSettings) }
    elseif (Test-Path $script:settingsPath) { Remove-Item $script:settingsPath -Force }
    Write-Output ("[cleanup] settings.json " + $(if ($hadSettings) { 'restored' } else { 'removed (it did not exist before)' }))
}

Write-BBDone 'theme-corner'
