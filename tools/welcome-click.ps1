# Clicking a blank part of the welcome page must NOT start a conversation.
#
# The welcome page used to end OnMouseUp with an unconditional StartRequested, so ANY
# left click anywhere on it -- including on empty space, where a user might be clicking to
# give the window focus or just to dismiss something -- created a new conversation. The
# page is self-painted (no child controls), so the click is what says which widget was hit.
#
# Two clicks, and the second is what makes the first mean anything:
#   A  blank space                     -> no conversation (the fix)
#   B  the "new conversation" button   -> conversation, title = the new-conversation text
# B has to be a widget ON the welcome page: if the welcome page were covered by a sibling
# panel, both clicks would land on that sibling, A would "pass" for the wrong reason and
# only B would notice.
#
# Neither click may be hard-coded: the page centres everything on its own width and the
# chips' widths come from MeasureString. The button is found by COLOUR instead -- it and
# the logo are the only two accent-filled shapes on the page, the logo is the topmost band
# of accent pixels and the button is the bottom one, so the scan finds the button without
# knowing anything about the layout.
#
# Usage:  powershell -File tools\welcome-click.ps1 [-Shot]

param([switch]$Shot)

. "$PSScriptRoot\_ui.ps1"

$script:fail = 0

# The accent-filled WIDGETS of the welcome area, as bands. Returns $null if there are none.
#
# Sampling every 2px is plenty for a 92px circle and a 180x40 button, and keeps PowerShell's
# per-pixel interop cost down.
#
# Rows are merged into bands and thin ones are dropped, because "accent-coloured pixels"
# is not the same question as "an accent-filled widget". The page's faux input card is
# outlined in a colour close enough to the accent to match, so a raw row scan reports its
# two 1px borders as extra bands -- and a probe that then takes the bottom band clicks the
# card's outline instead of the button. A widget here is at least 16px tall and 60px wide;
# an outline is neither.
function Get-AccentBands([int]$x0, [int]$y0, [int]$x1, [int]$y1) {
    $bmp = Get-Crop $x0 $y0 ($x1 - $x0) ($y1 - $y0)
    $w = $bmp.Width; $h = $bmp.Height

    # The accent is the most common saturated colour in the area (logo + button are both
    # filled with it and nothing else is). Saturated = the channels disagree.
    $hist = @{}
    for ($yy = 0; $yy -lt $h; $yy += 2) {
        for ($xx = 0; $xx -lt $w; $xx += 2) {
            $c = $bmp.GetPixel($xx, $yy)
            $mx = [Math]::Max($c.R, [Math]::Max($c.G, $c.B))
            $mn = [Math]::Min($c.R, [Math]::Min($c.G, $c.B))
            if ($mx - $mn -lt 40) { continue }
            $k = ($c.R * 65536) + ($c.G * 256) + $c.B
            if ($hist.ContainsKey($k)) { $hist[$k]++ } else { $hist[$k] = 1 }
        }
    }
    if ($hist.Count -eq 0) { $bmp.Dispose(); return $null }
    $accKey = -1; $accN = -1
    foreach ($k in $hist.Keys) { if ($hist[$k] -gt $accN) { $accN = $hist[$k]; $accKey = $k } }
    $ar = [Math]::Floor($accKey / 65536); $ag = [Math]::Floor(($accKey - $ar * 65536) / 256)
    $ab = $accKey - ($ar * 65536) - ($ag * 256)

    $bands = New-Object System.Collections.Generic.List[object]
    $cur = $null
    for ($yy = 0; $yy -lt $h; $yy += 2) {
        $n = 0; $lo = -1; $hi = -1
        for ($xx = 0; $xx -lt $w; $xx += 2) {
            $c = $bmp.GetPixel($xx, $yy)
            if (([Math]::Abs($c.R - $ar) + [Math]::Abs($c.G - $ag) + [Math]::Abs($c.B - $ab)) -gt 30) { continue }
            $n++
            if ($lo -lt 0) { $lo = $xx }
            $hi = $xx
        }
        if ($n -ge 10) {
            if ($null -eq $cur) { $cur = @{ Y0 = $yy; Y1 = $yy; X0 = $lo; X1 = $hi } }
            else {
                $cur.Y1 = $yy
                if ($lo -lt $cur.X0) { $cur.X0 = $lo }
                if ($hi -gt $cur.X1) { $cur.X1 = $hi }
            }
        }
        elseif ($null -ne $cur) { $bands.Add($cur); $cur = $null }
    }
    if ($null -ne $cur) { $bands.Add($cur) }
    $bmp.Dispose()

    $keep = New-Object System.Collections.Generic.List[object]
    foreach ($b in $bands) {
        if (($b.Y1 - $b.Y0) -lt 16) { continue }        # an outline, not a widget
        if (($b.X1 - $b.X0) -lt 60) { continue }        # a 28px icon, not a widget
        $keep.Add($b)
    }
    return @{ Accent = "$ar,$ag,$ab"; Bands = $keep }
}
# The store is moved aside rather than hoped to be empty.
#
# This probe's whole premise is that the app comes up on the WELCOME page, and that needs
# no conversation to open -- but any probe that sends a message leaves one behind in chats\
# (llm-reply and attachment-zoom both do), so what this measures depends on what ran before
# it. A probe whose verdict depends on run order is a probe that lies, and the lie reads as
# "the welcome page is broken". chats\, conversations.json and settings.json are the user's
# own data if they ever ran this build, so all three go back in the finally -- including
# when the body throws, which is exactly when a half-restored store is most likely.
$dir = Split-Path $script:BBExe -Parent
$settings = Join-Path $dir 'settings.json'
$chatsDir = Join-Path $dir 'chats'
$legacy = Join-Path $dir 'conversations.json'

$hadSettings = Test-Path $settings
$bakSettings = $null
if ($hadSettings) { $bakSettings = Get-Content $settings -Raw }

# A leftover single-file store matters here more than in the other probes: with chats\
# empty the app imports it on startup, which starts a conversation and lands us right
# back on "the welcome page is not showing".
$hadLegacy = Test-Path $legacy
$bakLegacy = $null
if ($hadLegacy) { $bakLegacy = Get-Content $legacy -Raw }

$bakChats = @{}
if (Test-Path $chatsDir) {
    foreach ($f in Get-ChildItem $chatsDir -File) { $bakChats[$f.Name] = (Get-Content $f.FullName -Raw) }
    Remove-Item (Join-Path $chatsDir '*') -Force
}
if (Test-Path $legacy) { Remove-Item $legacy -Force }

try {
    Invoke-BBProbe {
        $main = Start-BangGang
        $mr = Get-WinRect $main
        $mw = $mr.Right - $mr.Left; $mh = $mr.Bottom - $mr.Top

        # The sidebar's own width is whatever it currently is -- never assume SideW.
        $sw = 0
        foreach ($h in Get-WinKids $main) {
            $r = Get-WinRect $h
            if (($r.Right - $r.Left) -ge $mw) { continue }
            if ([Math]::Abs($r.Left - $mr.Left) -gt 1 -or [Math]::Abs($r.Top - ($mr.Top + 38)) -gt 1) { continue }
            if (($r.Right - $r.Left) -lt $sw) { continue }
            $sw = $r.Right - $r.Left
        }
        if ($sw -le 0) { throw 'sidebar not found' }

        # The scan covers the whole body beside the sidebar -- including the welcome page's own
        # faux input card, whose outline is accent-coloured. Get-AccentBands drops it by size.
        $wx0 = $mr.Left + $sw
        $wx1 = $mr.Right - 8
        $wy0 = $mr.Top + 38
        $wy1 = $mr.Bottom - 4

        # "Is the welcome page up?" is read off the pixels rather than off a control: the page
        # is self-painted, and nothing of it has a handle in this state (_convTitle and the
        # chat view are children of a panel that has never been shown, so they are not even
        # enumerable). Its logo circle and its button are filled with the accent colour and
        # nothing else of that size is, so counting accent bands answers the question directly.
        # Looking for a control instead cost a round of "title bar not found", which is exactly
        # the kind of probe bug that then reads as "the app is broken".
        $welcomeBands = {
            $b = Get-AccentBands $wx0 $wy0 $wx1 $wy1
            if ($null -eq $b) { return '' }
            return (($b.Bands | ForEach-Object { ($_.Y0 + $_.Y1) / 2 }) -join ',')
        }

        # ...and "did a conversation start?" is read off the control tree, where the signal is
        # unambiguous: the real input card brings a multiline EDIT with it, and the welcome
        # page -- which only paints a replica of that card -- does not.
        $inputEdit = {
            foreach ($h in Get-WinKids $main) {
                if ((Get-WinClass $h) -notlike '*EDIT*') { continue }
                if (((Get-WinRect $h).Bottom - (Get-WinRect $h).Top) -lt 40) { continue }
                return $h
            }
            return [IntPtr]::Zero
        }

        Write-Output ("window " + $mr.Left + "," + $mr.Top + " ${mw}x${mh}   sidebar $sw   welcome " +
                      $wx0 + "," + $wy0 + " .. " + $wx1 + "," + $wy1)
        Write-Output ''
        Write-Output '--- 0. the app starts on the welcome page ---'
        $a0 = Get-AccentBands $wx0 $wy0 $wx1 $wy1
        $bands0 = & $welcomeBands
        if ($null -eq $a0 -or $a0.Bands.Count -lt 2) {
            Write-Output '  FAIL -- the welcome page is not showing (need its logo and its button)'
            $script:fail++
            throw 'cannot continue without the welcome page'
        }
        if ((& $inputEdit) -ne [IntPtr]::Zero) {
            Write-Output '  FAIL -- an input box already exists; a conversation is active at startup'
            $script:fail++
        }
        Write-Output ("  OK   accent " + $a0.Accent + ", widgets span y " + $bands0 + "; no input box yet")

        # The button is the BOTTOM accent band (the logo is the top one) and the widest, so the
        # click lands on solid fill rather than on the label inside it.
        $btn = $a0.Bands[$a0.Bands.Count - 1]
        $btnX = [int](($btn.X0 + $btn.X1) / 2) + $wx0
        $btnY = [int](($btn.Y0 + $btn.Y1) / 2) + $wy0

        # ---- A. a blank click ----
        # The button's own row (so it is certainly in the visible part of the page) but hard
        # against the left edge, well clear of the centred logo / chips / button.
        $blankX = $wx0 + 30
        Write-Output ''
        Write-Output ('--- A. click blank space at ' + $blankX + ',' + $btnY + ' ---')
        Invoke-MouseClick $blankX $btnY
        Start-Sleep -Milliseconds 800
        $bands1 = & $welcomeBands
        $edit1 = & $inputEdit
        if ($bands1 -ne $bands0 -or $edit1 -ne [IntPtr]::Zero) {
            Write-Output ("  FAIL -- a conversation started under the blank click: welcome spans " +
                          $bands0 + " -> " + $bands1 + ", input box " + $edit1)
            if ($Shot) { [void](Save-WindowShot $main (Get-ShotPath 'welcome-blank-click.png')) }
            $script:fail++
        }
        else { Write-Output '  OK   still the same welcome page, no conversation started' }

        # ---- B. the new-conversation button, the positive control ----
        Write-Output ''
        Write-Output ('--- B. click the button at ' + $btnX + ',' + $btnY + ' ---')
        Invoke-MouseClick $btnX $btnY
        Start-Sleep -Milliseconds 1200
        $edit2 = & $inputEdit
        $bands2 = & $welcomeBands
        if ($edit2 -eq [IntPtr]::Zero) {
            Write-Output '  FAIL -- no conversation started; the click never reached the button'
            if ($Shot) { [void](Save-WindowShot $main (Get-ShotPath 'welcome-button-click.png')) }
            $script:fail++
        }
        elseif ($bands2 -ne '') {
            Write-Output ('  FAIL -- an input box exists but the welcome page is still up, spans ' + $bands2)
            $script:fail++
        }
        else {
            # Only now does the chat view exist, so its title bar can be looked up.
            $newConv = [string][char]0x65B0 + [char]0x5BF9 + [char]0x8BDD
            $title = [IntPtr]::Zero
            foreach ($h in Get-WinKids $main) {
                if ((Get-WinClass $h) -notlike '*STATIC*') { continue }
                if ([Math]::Abs((Get-WinRect $h).Top - ($mr.Top + 38)) -gt 2) { continue }
                if (((Get-WinRect $h).Bottom - (Get-WinRect $h).Top) -ne 48) { continue }
                $title = $h
                break
            }
            $er = Get-WinRect $edit2
            Write-Output ("  OK   conversation started, input box " + ($er.Right - $er.Left) + "x" + ($er.Bottom - $er.Top))
            if ($title -eq [IntPtr]::Zero) {
                Write-Output '  FAIL -- welcome page gone but no conversation title bar appeared'
                $script:fail++
            }
            elseif (-not [BB]::IsWindowVisible($title) -or (Get-WinText $title) -ne $newConv) {
                Write-Output ('  FAIL -- title bar is ' + (Get-WinText $title) + ', expected the new-conversation title')
                $script:fail++
            }
            else { Write-Output ('  OK   title reads "' + (Get-WinText $title) + '"') }
        }

        if ($Shot) { [void](Save-WindowShot $main (Get-ShotPath 'welcome-after.png')) }

        Write-Output ''
        if ($script:fail -eq 0) {
            Write-Output 'PASS: blank clicks leave the welcome page alone; its own buttons still start a conversation.'
        }
        else { Write-Output "FAIL: $script:fail check(s) failed." }
    }
} finally {
    if ($hadSettings) { Set-Content -Path $settings -Value $bakSettings -Encoding utf8 -NoNewline }
    else { Remove-Item $settings -ErrorAction SilentlyContinue }
    if ($hadLegacy) { Set-Content -Path $legacy -Value $bakLegacy -Encoding utf8 -NoNewline }
    else { Remove-Item $legacy -ErrorAction SilentlyContinue }
    # Only the files this probe may have created are removed, and everything that was there
    # before is rewritten from the backup.
    if (Test-Path $chatsDir) {
        foreach ($f in Get-ChildItem $chatsDir -File) {
            if (-not $bakChats.ContainsKey($f.Name)) { Remove-Item $f.FullName -Force }
        }
    }
    foreach ($n in $bakChats.Keys) {
        if (-not (Test-Path $chatsDir)) { New-Item -ItemType Directory -Force $chatsDir | Out-Null }
        Set-Content -Path (Join-Path $chatsDir $n) -Value $bakChats[$n] -Encoding utf8 -NoNewline
    }
}

Write-BBDone 'welcome-click'
