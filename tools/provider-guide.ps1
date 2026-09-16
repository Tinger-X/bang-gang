# The model-access page's provider row: a dropdown narrowed on the right, with an
# "open the vendor's official guide" icon button sitting where the width went.
#
# What is checked, and why each one needs to be here:
#
#   A  geometry. The dropdown keeps the row's LEFT edge and gives up width on the
#      right; the button ends flush with the row's right edge -- and that edge is
#      the SAME x every input field on the page ends at. The composite exists only
#      to make that true, so it is the one thing worth asserting: change FieldW for
#      one widget and not the other and the whole column goes ragged.
#   B  reachability. WindowFromPoint at the button's centre names the button, so
#      nothing (the dropdown, the card, the page) is stacked over it.
#   C  the disabled look, as a PAIR. With the custom preset selected the preset has no guide,
#      so the icon must be measurably lighter and hovering it must repaint nothing;
#      with OpenAI selected the same button must be darker and must repaint on
#      hover. Each half alone is worthless -- "hover did nothing" passes on a dead
#      button, and "hover repainted it" passes on a button that is clickable when
#      it should not be. Together they pin the state.
#   D  the click. With the custom preset it must NOT log an open; with OpenAI it must log
#      "open provider guide ok=True url=https://...". The negative half is only
#      meaningful next to the positive half, which is why both run.
#
# D launches a real browser tab, so it is opt-in:
#   powershell -File tools\provider-guide.ps1            (A/B/C, plus the negative half of D)
#   powershell -File tools\provider-guide.ps1 -Launch     (adds the positive half of D)
#   powershell -File tools\provider-guide.ps1 -Dump       (prints the raw geometry)
#   powershell -File tools\provider-guide.ps1 -Shot       (saves a crop of each state to shoots/)
#
# -Shot exists because the numbers in C cannot tell you whether the button LOOKS right:
# "darkest=85" is satisfied by any number of shapes. The two crops are the only way to
# see the circle the tinted skin draws, and having them come out of the same run that
# measured them keeps the two readings on the same pixels.

param(
    [switch]$Launch,
    [switch]$Dump,
    [switch]$Shot
)

. "$PSScriptRoot\_ui.ps1"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PG {
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
}
'@

$script:fail = 0
$script:mr = $null

# BB+RECT carries Left/Top/Right/Bottom only. Reading .Width off it gives $null and
# a silently-zero calculation, so every size in here goes through these two.
function RW($r) { return ($r.Right - $r.Left) }
function RH($r) { return ($r.Bottom - $r.Top) }

function Get-Pt([int]$x, [int]$y) {
    $p = New-Object PG+POINT
    $p.X = $x
    $p.Y = $y
    return $p
}

function Get-TraceLog { return (Join-Path (Split-Path $script:BBExe -Parent) 'ui-trace.log') }

# The trace lines that say a guide was opened -- the only observable the app offers
# for "the button did something", since the result is a browser tab in another process.
function Get-OpenLines {
    $log = Get-TraceLog
    if (-not (Test-Path $log)) { return @() }
    return @(Select-String -Path $log -Pattern 'open provider guide' -ErrorAction SilentlyContinue)
}

# The provider row's composite: a 28x28 icon button whose parent is one field wide.
#
# Found structurally rather than by coordinate -- the row moves with the card -- and
# the "sibling wider than 200" test is what separates it from every other 28x28 button
# in the window (the chrome buttons' parent is the whole form, the nav rail's are
# 180 wide). No eye button can be mistaken for it: InputField paints its eye rather
# than parenting a control.
function Get-Pickers($main, [int]$cardX) {
    $out = @()
    foreach ($h in Get-WinKids $main) {
        if (-not [BB]::IsWindowVisible($h)) { continue }
        $r = Get-WinRect $h
        if ((RW $r) -ne 28 -or (RH $r) -ne 28) { continue }
        if ($r.Left -lt $cardX -or $r.Right -gt ($cardX + 880)) { continue }

        $p = [BB]::GetParent($h)
        if ($p -eq [IntPtr]::Zero -or $p -eq $main) { continue }
        $pr = Get-WinRect $p
        $pw = RW $pr
        if ($pw -lt 280 -or $pw -gt 380) { continue }

        $dd = [IntPtr]::Zero
        foreach ($k in Get-WinKids $p) {
            if ($k -eq $h) { continue }
            if (-not [BB]::IsWindowVisible($k)) { continue }
            if ((RW (Get-WinRect $k)) -gt 200) { $dd = $k; break }
        }
        if ($dd -eq [IntPtr]::Zero) { continue }
        $out += @{ Panel = $p; Btn = $h; Dd = $dd }
    }
    return $out
}

# The panel's own colour, read from the crop's corner (outside the halo circle in
# either state), plus the glyph's darkest pixel and how many pixels are visibly
# darker than the panel. "Darker ink" and "there is a halo" are two different
# quantities and the two states differ in both -- measuring one of them only would
# let a change in the other go unnoticed.
function Measure-Icon($r) {
    $bmp = Get-Crop $r.Left $r.Top (RW $r) (RH $r)
    $bg = 0.299 * $bmp.GetPixel(1, 1).R + 0.587 * $bmp.GetPixel(1, 1).G + 0.114 * $bmp.GetPixel(1, 1).B
    $min = 255.0
    $n = 0
    for ($x = 0; $x -lt $bmp.Width; $x++) {
        for ($y = 0; $y -lt $bmp.Height; $y++) {
            $c = $bmp.GetPixel($x, $y)
            $l = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
            if ($l -lt $min) { $min = $l }
            if ($l -le ($bg - 6)) { $n++ }
        }
    }
    $bmp.Dispose()
    return @{ Bg = [int]$bg; Min = [int]$min; N = $n }
}

# A crop of the button with a little margin, so the card's own colour around the halo
# circle is in the picture -- the circle is what the skin added, and a shot cropped to
# the button's exact 28x28 would cut it off at the corners.
#
# The pointer is parked away first: this is called right after Get-HoverChange, which
# leaves the cursor sitting ON the button, and a shot of the hover state would show the
# wrong idle colours -- the thing being judged here is the resting look.
function Save-IconShot($r, [string]$Name) {
    if (-not $Shot) { return }
    [void][BB]::SetCursorPos(($script:mr.Left + 30), ($script:mr.Top + 300))
    Start-Sleep -Milliseconds 400
    $m = 6
    Save-Shot ($r.Left - $m) ($r.Top - $m) ((RW $r) + $m * 2) ((RH $r) + $m * 2) (Get-ShotPath $Name)
}

# Park the pointer somewhere neutral, shoot the button, move onto the button, shoot
# again. Returns the number of differing pixels -- 0 for a button that ignores hover.
function Get-HoverChange($r) {
    [void][BB]::SetCursorPos(($script:mr.Left + 30), ($script:mr.Top + 300))
    Start-Sleep -Milliseconds 400
    $a = Get-Crop $r.Left $r.Top (RW $r) (RH $r)
    [void][BB]::SetCursorPos(([int](($r.Left + $r.Right) / 2)), ([int](($r.Top + $r.Bottom) / 2)))
    Start-Sleep -Milliseconds 500
    $b = Get-Crop $r.Left $r.Top (RW $r) (RH $r)

    $n = 0
    for ($x = 0; $x -lt $a.Width; $x++) {
        for ($y = 0; $y -lt $a.Height; $y++) {
            $p = $a.GetPixel($x, $y); $q = $b.GetPixel($x, $y)
            if ([Math]::Abs($p.R - $q.R) -gt 4 -or [Math]::Abs($p.G - $q.G) -gt 4 -or
                [Math]::Abs($p.B - $q.B) -gt 4) { $n++ }
        }
    }
    $a.Dispose(); $b.Dispose()
    return $n
}

# Open the dropdown and click item $idx.
#
# The list is laid out from the source's own numbers (Pad 6, a 5px inset, 30px rows)
# rather than from a screenshot: there is no text to search for, the rows are drawn.
function Select-Provider($picker, [int]$idx) {
    $dd = Get-WinRect $picker.Dd
    Invoke-MouseClick ([int](($dd.Left + $dd.Right) / 2)) ([int](($dd.Top + $dd.Bottom) / 2))
    Start-Sleep -Milliseconds 600

    $pop = [IntPtr]::Zero
    foreach ($h in Get-WinKids $script:BBMain) {
        if (-not [BB]::IsWindowVisible($h)) { continue }
        if ((RW (Get-WinRect $h)) -eq ((RW $dd) + 12)) { $pop = $h; break }
    }
    if ($pop -eq [IntPtr]::Zero) { throw 'dropdown popup not found after clicking the dropdown' }

    $pr = Get-WinRect $pop
    Invoke-MouseClick ($pr.Left + 40) ($pr.Top + 26 + 30 * $idx)
    Start-Sleep -Milliseconds 700
}

Invoke-BBProbe {
    $main = Start-BangGang
    $script:BBMain = $main
    $script:mr = Get-WinRect $main
    $ww = RW $script:mr
    $wh = RH $script:mr
    Write-Output ("window " + $script:mr.Left + "," + $script:mr.Top + " ${ww}x${wh}")

    $pill = Get-SearchPill $main
    if ($null -eq $pill) { throw 'search pill not found' }
    $gear = Get-SettingsGear $main $pill
    if ($null -eq $gear) { throw 'settings gear not found' }

    $cardX = $script:mr.Left + [int](($ww - 880) / 2)
    $cardY = $script:mr.Top + [int](($wh - 640) / 2)

    Invoke-MouseClick ($gear.Left + 14) ($gear.Top + 14)
    Start-Sleep -Milliseconds 1100
    # rail page 1 = model access (see SettingsOverlay's AddPage order)
    Invoke-MouseClick ($cardX + 14 + 90) ($cardY + 96 + 48 * 1 + 21)
    Start-Sleep -Milliseconds 1100

    # Put the row in a known state instead of trusting whatever settings.json holds:
    # the "dim" assertions below are about the custom preset, and a saved OpenAI selection would
    # turn every one of them into a false failure.
    $pickers = @(Get-Pickers $main $cardX)
    if ($pickers.Count -lt 1) { throw 'no provider picker found on the model-access page' }
    Select-Provider $pickers[0] 0        # 0 = the custom preset

    $pickers = @(Get-Pickers $main $cardX)
    Write-Output ''
    Write-Output ("--- found " + $pickers.Count + " picker(s) (one per card) ---")
    if ($pickers.Count -ne 2) {
        Write-Output ("  FAIL -- expected 2 (chat + speech), found " + $pickers.Count)
        $script:fail++
    }

    # ---------------- A. geometry ----------------
    Write-Output ''
    Write-Output '--- A. dropdown narrowed, button flush with the field edge ---'
    $fieldW = 0
    foreach ($pk in $pickers) {
        $pr = Get-WinRect $pk.Panel
        $dr = Get-WinRect $pk.Dd
        $br = Get-WinRect $pk.Btn
        $fieldW = RW $pr
        $d = ("panel " + $fieldW + "  dd " + (RW $dr) + "  btn " + $br.Left + ".." + $br.Right)
        $ok = ($dr.Left -eq $pr.Left) -and ($br.Right -eq $pr.Right) -and
              ($br.Left -eq ($dr.Right + 12))
        if ($ok) { Write-Output ("  OK   " + $d) }
        else {
            Write-Output ("  FAIL " + $d + "  (want dd.Left=" + $pr.Left + ", btn.Left=dd.Right+12, btn.Right=" + $pr.Right + ")")
            $script:fail++
        }
        if (-not $Dump) { continue }
        Write-Output ("       panel " + $pr.Left + "," + $pr.Top + "  dd " + $dr.Left + "," + $dr.Top + "  btn " + $br.Left + "," + $br.Top)
    }

    # Every control on the page that is one field wide must END at the same x --
    # that is the whole reason the two pieces are one composite.
    # Count the controls as well as the distinct edges: "one distinct edge" is also
    # true of a scan that found one control, or none at all.
    $rights = @{}
    $n = 0
    foreach ($h in Get-WinKids $main) {
        if (-not [BB]::IsWindowVisible($h)) { continue }
        $r = Get-WinRect $h
        if ((RW $r) -ne $fieldW) { continue }
        if ($r.Left -lt $cardX -or $r.Right -gt ($cardX + 880)) { continue }
        $rights[$r.Right] = 1
        $n++
    }
    $keys = @($rights.Keys | Sort-Object)
    $exp = 0
    foreach ($pk in $pickers) { $exp = (Get-WinRect $pk.Btn).Right }
    if ($n -lt 4) {
        Write-Output ("  FAIL only " + $n + " field-wide control(s) found on the page -- the scan is not seeing the row")
        $script:fail++
    }
    elseif ($keys.Count -eq 1 -and $keys[0] -eq $exp) {
        Write-Output ("  OK   all " + $n + " field-wide control(s) share the edge at x=" + $exp)
    }
    else {
        Write-Output ("  FAIL " + $n + " field-wide control(s) end at " + ($keys -join ',') + "  (button ends at " + $exp + ")")
        $script:fail++
    }

    # ---------------- B. reachability ----------------
    Write-Output ''
    Write-Output '--- B. the button is on top at its own centre ---'
    $btn = $pickers[0].Btn
    $br = Get-WinRect $btn
    $hit = [PG]::WindowFromPoint((Get-Pt ([int](($br.Left + $br.Right) / 2)) ([int](($br.Top + $br.Bottom) / 2))))
    if ($hit -eq $btn) { Write-Output '  OK   WindowFromPoint names the link button' }
    else {
        Write-Output ("  FAIL covered: WindowFromPoint gave 0x" + $hit.ToString('X') + " not 0x" + $btn.ToString('X'))
        $script:fail++
    }

    # ---------------- C. disabled look, as a pair ----------------
    Write-Output ''
    Write-Output '--- C. ink and hover: the "custom" preset (no guide) vs OpenAI (has one) ---'

    $dim = Measure-Icon $br
    $dimHover = Get-HoverChange $br
    Save-IconShot $br 'guide-btn-custom.png'
    Write-Output ("  custom : bg=" + $dim.Bg + "  darkest=" + $dim.Min + "  inked px=" + $dim.N + "  hover-diff=" + $dimHover)

    $before = (Get-OpenLines).Count
    Invoke-MouseClick ([int](($br.Left + $br.Right) / 2)) ([int](($br.Top + $br.Bottom) / 2))
    Start-Sleep -Milliseconds 900
    $after = (Get-OpenLines).Count
    if ($after -eq $before) { Write-Output '  OK   clicking it logged nothing (no guide to open)' }
    else {
        Write-Output ("  FAIL clicking the disabled button opened " + ($after - $before) + " guide(s)")
        $script:fail++
    }
    if ($dimHover -eq 0) { Write-Output '  OK   hovering repainted nothing' }
    else {
        Write-Output ("  FAIL hovering a disabled button repainted " + $dimHover + " px")
        $script:fail++
    }

    Select-Provider $pickers[0] 1        # 1 = OpenAI, which carries a guide URL
    $pickers = @(Get-Pickers $main $cardX)
    if ($pickers.Count -lt 1) { throw 'picker vanished after switching provider' }
    $btn = $pickers[0].Btn
    $br = Get-WinRect $btn

    $lit = Measure-Icon $br
    $litHover = Get-HoverChange $br
    Save-IconShot $br 'guide-btn-openai.png'
    Write-Output ("  openai : bg=" + $lit.Bg + "  darkest=" + $lit.Min + "  inked px=" + $lit.N + "  hover-diff=" + $litHover)

    if ($lit.Min -le ($dim.Min - 15)) { Write-Output ("  OK   enabled ink is darker (" + $dim.Min + " -> " + $lit.Min + ")") }
    else {
        Write-Output ("  FAIL enabled ink is not darker: " + $dim.Min + " -> " + $lit.Min)
        $script:fail++
    }
    if ($litHover -gt 0) { Write-Output '  OK   hovering repaints it' }
    else {
        Write-Output '  FAIL hovering an enabled button repainted nothing -- the click may never land'
        $script:fail++
    }

    # ---------------- D. the click ----------------
    Write-Output ''
    Write-Output '--- D. the click opens the vendor guide ---'
    if (-not $Launch) {
        Write-Output '  SKIP positive half (-Launch not given: it opens a real browser tab)'
    }
    else {
        $before = (Get-OpenLines).Count
        Invoke-MouseClick ([int](($br.Left + $br.Right) / 2)) ([int](($br.Top + $br.Bottom) / 2))
        Start-Sleep -Milliseconds 1500
        $lines = Get-OpenLines
        $new = @($lines | Select-Object -Last ($lines.Count - $before))
        if ($new.Count -eq 0) {
            Write-Output '  FAIL no "open provider guide" line was logged'
            $script:fail++
        }
        else {
            $last = $new[$new.Count - 1].Line
            Write-Output ('       ' + $last)
            if ($last -match 'ok=True url=https://') { Write-Output '  OK   opened an https guide' }
            else {
                Write-Output '  FAIL the line does not report ok=True with an https url'
                $script:fail++
            }
        }
    }
}

Write-Output ''
Write-Output ("== failures: " + $script:fail + " ==")
Write-BBDone 'provider-guide'
