# Item 2: with a picture open, the wheel zooms it from ANYWHERE in the app.
#
# The zoom algorithm was never the problem -- it was that the wheel never reached it.
# OnMouseWheel only fires when the message lands on the ImageViewer's own HWND, so the
# user had to keep the pointer over the picture to zoom, which is not what "wheel to
# zoom" means to anyone. Now the viewer also installs an IMessageFilter and takes the
# wheel wherever it is; ChatView and InputPanel each stand down while it is open.
#
# The measurement is the picture's own pixel extent, not the zoom factor:
#
#   * the zoom factor is not on screen anywhere readable. The caption's "152%" is drawn
#     from the same ViewScale, in the same OnPaint, as the picture -- so it cannot tell
#     us anything the picture does not, and reading it back would need an OCR-quality
#     ink measurement of a proportional font. The extent is the thing the user sees.
#   * a bounding box also catches "the scale changed but the wrong thing was redrawn".
#
# Four parts:
#   A  clicking the chip opens the viewer (the fixture's colour at the window centre)
#   B  a wheel FAR from the picture -- over the chat area, hundreds of pixels away --
#      grows it, and the opposite wheel shrinks it back to where it started
#   C  while the viewer is open, the chat area behind it does NOT scroll. This is the
#      assertion that fails if the viewer's filter is never reached: the wheel would
#      fall through to ChatView, which is exactly the old behaviour.
#   D  the control group that makes C mean something: close the viewer, wheel at the
#      SAME point, and the chat area does scroll. Without D, "the offset did not
#      change" passes just as well on a point that never had a scrollable list under
#      it at all, or on a build whose scroll is broken.
#
# The fixture has to overflow the view or D measures nothing -- and it did not, at first:
# 46 repetitions gave 102px of travel against a 150px precondition, because item 5's new
# Markdown layer packs the same text into fewer pixels than the old one did. C is asserted
# up front so that the next drift says "the fixture is too short to test scrolling"
# instead of blaming the app. (chat-area.ps1 carries the same note for the same reason.)
#
# ASCII ONLY. The fixture is magenta on purpose -- nothing in either theme is anywhere
# near it, so counting it cannot be fooled by the palette.

. "$PSScriptRoot\_ui.ps1"

$dir = Split-Path $script:BBExe -Parent
$settings = Join-Path $dir 'settings.json'
$chatsDir = Join-Path $dir 'chats'
$legacy = Join-Path $dir 'conversations.json'

$script:fail = 0
function Check([bool]$ok, [string]$label, [string]$detail) {
    $tag = 'FAIL'
    if ($ok) { $tag = 'ok  ' }
    if (-not $ok) { $script:fail++ }
    Write-Output ('  [' + $tag + '] ' + $label + '  ' + $detail)
}

# ---- fixture ---------------------------------------------------------------------
#
# 160x110: small enough that "fit to window" comes out at exactly 100% and the picture
# stays fully on screen after zooming in a few notches, so the measured extent is the
# picture's real drawn size and never the window clipping it.

$fixDir = Join-Path $script:BBShoots 'fixtures'
if (-not (Test-Path $fixDir)) { [void](New-Item -ItemType Directory -Path $fixDir -Force) }
$pngPath = Join-Path $fixDir 'wheel-photo.png'

$bmp = New-Object System.Drawing.Bitmap 160, 110
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(210, 0, 150))
$g.Dispose()
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

# Square to the eye and unmistakable to the pixel counter.
function Test-Magenta($c) { return ($c.R -ge 170 -and $c.G -le 70 -and $c.B -ge 110) }

# The picture's extent on screen, measured along the window's centre row and centre
# column -- the ImageViewer centres the picture on itself, so both lines cross it. A
# full window scan would be a million GetPixel calls per frame; two lines are enough
# because the picture is a rectangle and never rotated or skewed.
#
# Returns W/H = 0 and L/T = -1 when nothing magenta is on screen, so "the viewer is
# closed" is a value rather than an exception.
function Get-MagentaSpan($mr, [int]$cx, [int]$cy) {
    $row = Get-Crop $mr.Left $cy ($mr.Right - $mr.Left) 1
    $col = Get-Crop $cx $mr.Top 1 ($mr.Bottom - $mr.Top)
    $lo = -1; $hi = -1
    for ($x = 0; $x -lt $row.Width; $x++) {
        if (-not (Test-Magenta $row.GetPixel($x, 0))) { continue }
        if ($lo -lt 0) { $lo = $x }
        $hi = $x
    }
    $tlo = -1; $thi = -1
    for ($y = 0; $y -lt $col.Height; $y++) {
        if (-not (Test-Magenta $col.GetPixel(0, $y))) { continue }
        if ($tlo -lt 0) { $tlo = $y }
        $thi = $y
    }
    $row.Dispose(); $col.Dispose()
    $w = 0; if ($lo -ge 0) { $w = $hi - $lo + 1 }
    $h = 0; if ($tlo -ge 0) { $h = $thi - $tlo + 1 }
    $l = -1; if ($lo -ge 0) { $l = $mr.Left + $lo }
    $t = -1; if ($tlo -ge 0) { $t = $mr.Top + $tlo }
    return @{ W = $w; H = $h; L = $l; T = $t }
}

$cid = 'eeeeffff000011112222333344445555'
$stamp = '2026-09-17T11:00:00'

# The chat area has to overflow the view for D to measure a scroll -- by a good margin,
# because a wheel that clamps on the first notch still moves the offset by whatever the
# range happens to be. Short words, same as chat-area.ps1: this fixture only has to be
# tall, not wide.
$sentence = 'ab cd ef gh ij kl mn op qr st uv wx yz '
$longAsst = $sentence * 120

# ---- backups ---------------------------------------------------------------------

$hadSettings = Test-Path $settings
$bakSettings = $null
if ($hadSettings) { $bakSettings = Get-Content $settings -Raw }

$hadLegacy = Test-Path $legacy
$bakLegacy = $null
if ($hadLegacy) { $bakLegacy = Get-Content $legacy -Raw }

$bakChats = @{}
if (Test-Path $chatsDir) {
    foreach ($f in Get-ChildItem $chatsDir -File) { $bakChats[$f.Name] = (Get-Content $f.FullName -Raw) }
}

$script:span0 = $null
$script:spanIn = $null
$script:spanBack = $null
$script:spanClosed = $null
$script:off0 = -1
$script:offIn = -1
$script:offOut = -1
$script:offClosed = -1
$script:offScrolled = -1
$script:rowTop0 = -1
$script:rowTopScrolled = -1
$script:range = 0
$script:px = 0
$script:py = 0

try {
    if (Test-Path $chatsDir) { Remove-Item (Join-Path $chatsDir '*') -Force }
    if (Test-Path $legacy) { Remove-Item $legacy -Force }

    # The dead endpoint is here for the send gate only, same as attachment-zoom.ps1 --
    # nothing is sent in this probe, but the message has to be one the app will show.
    $set = @{
        ThemeMode = 'light'
        ChatProvider = 'custom'
        ChatProfiles = @{ custom = @{ url = 'http://127.0.0.1:9/v1'; key = 'probe'; model = 'probe-model' } }
    }
    Set-Content -Path $settings -Value ($set | ConvertTo-Json -Depth 6) -Encoding utf8

    # Long assistant message first (so there is something to scroll), then the user's
    # message carrying the picture -- last, so it is on screen when the view pins to the
    # bottom and its chip is clickable without any scrolling of the probe's own.
    $conv = @{
        Version = 1
        Conversation = @{
            Id = $cid
            Title = 'zoom-wheel'
            CreatedAt = $stamp
            UpdatedAt = $stamp
            Messages = @(
                @{ Role = 'assistant'; When = $stamp; Text = $longAsst; Attachments = @() },
                @{ Role = 'user'; When = $stamp; Text = ''; Attachments = @(
                    @{ Kind = 'image'; Name = 'wheel-photo.png'; Path = $pngPath; Size = 4096 }
                ) }
            )
        }
    }
    if (-not (Test-Path $chatsDir)) { New-Item -ItemType Directory -Path $chatsDir -Force | Out-Null }
    Set-Content -Path (Join-Path $chatsDir ($cid + '.json')) -Value ($conv | ConvertTo-Json -Depth 8) -Encoding utf8

    Invoke-BBProbe {
        $main = Start-BangGang 5
        $mr = Get-WinRect $main
        $cx = [int](($mr.Left + $mr.Right) / 2)
        $cy = [int](($mr.Top + $mr.Bottom) / 2)

        if (-not (Open-ConvRow $main)) { throw 'conversation list not found; the fixture was not read back' }
        $waited = 0
        while ($waited -lt 6000) {
            if (@(Get-UiRowList (Get-UiRows)).Count -ge 2) { break }
            Start-Sleep -Milliseconds 250
            $waited += 250
        }

        $ui = Get-UiRows
        $script:range = [int]$ui.contentH - [int]$ui.clientH

        # The user's row is the one with the attachment; it is last in the store, so it is
        # last here. Its chip sits at (0, 0) -- the attachment strip IS the top of the row
        # since item 1 moved it out of the bubble, and the fixture sends no text, so item 6
        # leaves the row as the chip row alone: no bubble means no 2 x PadX for the chip to
        # sit inside. The row is CHIP wide, so CHIP/2 in from its left edge is the centre.
        $urows = @(Get-UiRowsOf $ui 'user')
        if ($urows.Count -eq 0) { throw 'no user row; the attachment fixture did not come back' }
        $ur = Get-UiRowScreen $ui $urows[$urows.Count - 1]
        if ($null -eq $ur) { throw 'the user row has no screen rectangle' }
        if ($ur.W -lt 44 -or $ur.H -lt 44) {
            throw ('the user row came back ' + $ur.W + 'x' + $ur.H + '; the chip-only fixture should be 44x44')
        }
        $chipX = $ur.L + 22
        $chipY = $ur.T + 22

        # The point the wheel is sent at: inside the chat view, far from the centre where
        # the picture is drawn. Nothing about the picture is under it.
        $script:px = [int]$ui.viewX + 40
        $script:py = [int]$ui.viewY + 30

        # Both points are derived from the snapshot's absolute origin. Get-UiRows now
        # re-anchors that on the window's own rect, so a snapshot taken while the window
        # was somewhere else can no longer scatter clicks across the desktop -- but a
        # snapshot whose LAYOUT is from somewhere else would still be read as a plausible
        # click point, and the failures below would read as app bugs. Start-BangGang
        # deletes the stale file; this is the belt to that pair of braces, and it costs
        # one comparison to keep the whole probe honest about where it is pointing.
        $inside = { param($x, $y) ($x -gt $mr.Left) -and ($x -lt $mr.Right) -and ($y -gt $mr.Top) -and ($y -lt $mr.Bottom) }
        if ((-not (& $inside $script:px $script:py)) -or (-not (& $inside $chipX $chipY))) {
            throw ('a point derived from ui-rows.json (' + $script:px + ',' + $script:py + ' / ' + $chipX + ',' + $chipY +
                   ') is outside the window ' + $mr.Left + ',' + $mr.Top + ' -- the snapshot is not from this run')
        }
        Write-Output ("window " + $mr.Left + "," + $mr.Top + " " + ($mr.Right - $mr.Left) + "x" + ($mr.Bottom - $mr.Top) +
                      "   centre " + $cx + "," + $cy + "   wheel point " + $script:px + "," + $script:py)
        Write-Output ("user row " + $ur.L + "," + $ur.T + " " + $ur.W + "x" + $ur.H + "   chip " + $chipX + "," + $chipY)

        # ---- A. open the picture ----
        Write-Output ''
        Write-Output '--- A. clicking the chip opens the viewer ---'
        $script:span0 = Get-MagentaSpan $mr $cx $cy
        Check ($script:span0.W -eq 0) 'nothing magenta on screen before the click' 'clean'

        Invoke-MouseClick $chipX $chipY
        Start-Sleep -Milliseconds 800
        $script:spanIn = Get-MagentaSpan $mr $cx $cy
        Write-Output ('  picture on screen: ' + $script:spanIn.W + 'x' + $script:spanIn.H + ' at ' + $script:spanIn.L + ',' + $script:spanIn.T)
        Check (($script:spanIn.W -ge 150) -and ($script:spanIn.H -ge 100)) 'the picture is drawn at the window centre' `
            ($script:spanIn.W.ToString() + 'x' + $script:spanIn.H + ' (fixture is 160x110)')
        Save-WindowShot $main (Get-ShotPath 'zoom-wheel-1-open.png')

        # ---- B. the wheel, from far away, zooms ----
        Write-Output ''
        Write-Output '--- B. a wheel over the chat area zooms it ---'
        $script:off0 = (Get-UiRows).offset
        Invoke-WheelAt $script:px $script:py 3
        Start-Sleep -Milliseconds 500
        $script:offIn = (Get-UiRows).offset
        $s1 = Get-MagentaSpan $mr $cx $cy
        Write-Output ('  +3 notches at ' + $script:px + ',' + $script:py + ' -> ' + $s1.W + 'x' + $s1.H +
                      '   (1.15^3 = 1.52x of ' + $script:spanIn.W + 'x' + $script:spanIn.H + ')')
        Check ($s1.W -gt $script:spanIn.W * 1.3) 'the picture grew' `
            ($script:spanIn.W.ToString() + ' -> ' + $s1.W + 'px wide')
        Check ($s1.H -gt $script:spanIn.H * 1.3) 'in both directions' `
            ($script:spanIn.H.ToString() + ' -> ' + $s1.H + 'px tall')
        # It grew about the centre, not off to one side: the wheel point is nowhere near
        # the picture, so the anchor has to fall back to the picture's own centre. Without
        # that fallback the picture is thrown off screen in the direction of the pointer.
        $c1 = [int](($s1.L + ($s1.L + $s1.W - 1)) / 2)
        Check ([Math]::Abs($c1 - $cx) -le 3) 'it grew about its own centre, not the pointer' `
            ('centre ' + $c1 + ' vs window ' + $cx)
        Save-WindowShot $main (Get-ShotPath 'zoom-wheel-2-zoomed.png')

        # ---- C. the chat area behind it did not scroll ----
        Write-Output ''
        Write-Output '--- C. the chat area behind it stayed put ---'
        Write-Output ('  offset ' + $script:off0 + ' -> ' + $script:offIn + ' while the viewer is open')
        Check ($script:offIn -eq $script:off0) 'the wheel did not fall through to the chat area' `
            ('offset ' + $script:offIn + ', was ' + $script:off0)

        # ...and the other direction brings it back, so "it grew" is not just "any mouse
        # event makes the picture bigger".
        Invoke-WheelAt $script:px $script:py -3
        Start-Sleep -Milliseconds 500
        $script:offOut = (Get-UiRows).offset
        $s2 = Get-MagentaSpan $mr $cx $cy
        Write-Output ('  -3 notches -> ' + $s2.W + 'x' + $s2.H)
        Check ([Math]::Abs($s2.W - $script:spanIn.W) -le 4) 'and the opposite wheel brings it back' `
            ($s2.W.ToString() + ' vs ' + $script:spanIn.W + 'px')
        Check ($script:offOut -eq $script:off0) 'still no scrolling behind it' ('offset ' + $script:offOut)

        # ---- D. close, then the same point DOES scroll: the control group ----
        Write-Output ''
        Write-Output '--- D. the control group: that point scrolls once the viewer is closed ---'
        # Key, not Chord: Chord is Ctrl+<vk>, and Ctrl+Esc opens the Start menu, whose
        # overlay then eats the rest of the run (see the note on BB.Key).
        [BB]::Key(0x1B)
        Start-Sleep -Milliseconds 600
        $script:spanClosed = Get-MagentaSpan $mr $cx $cy
        $script:offClosed = (Get-UiRows).offset
        Check ($script:spanClosed.W -eq 0) 'Esc closed the viewer' 'clean'
        Check ($script:offClosed -eq $script:off0) 'and closing it scrolled nothing' ('offset ' + $script:offClosed)

        $rows0 = @(Get-UiRowList (Get-UiRows))
        if ($rows0.Count -eq 0) { throw 'the chat rows vanished' }
        $script:rowTop0 = [int]$rows0[0].y

        # Precondition, asserted rather than assumed: with nothing to scroll, the offset
        # could not change and this section would report success for a scroll that never
        # happened. Same trap chat-area.ps1 documents.
        Check ($script:range -gt 150) 'the fixture overflows the view enough to scroll' ($script:range.ToString() + 'px of travel')

        Invoke-WheelAt $script:px $script:py 3
        Start-Sleep -Milliseconds 400
        $script:offScrolled = (Get-UiRows).offset
        $rows1 = @(Get-UiRowList (Get-UiRows))
        if ($rows1.Count -gt 0) { $script:rowTopScrolled = [int]$rows1[0].y }
        Write-Output ('  offset ' + $script:off0 + ' -> ' + $script:offScrolled +
                      '   first row y ' + $script:rowTop0 + ' -> ' + $script:rowTopScrolled)
        Check ([Math]::Abs($script:offScrolled - $script:off0) -gt 100) 'the same wheel point does scroll the chat area' `
            (($script:offScrolled - $script:off0).ToString() + 'px of offset, 3 notches = 162px asked for')
        Check ($script:rowTopScrolled -gt $script:rowTop0) 'and the rows moved with it' `
            ($script:rowTop0.ToString() + ' -> ' + $script:rowTopScrolled)
        Save-WindowShot $main (Get-ShotPath 'zoom-wheel-3-closed.png')
    }
} finally {
    if ($hadSettings) { Set-Content -Path $settings -Value $bakSettings -Encoding utf8 -NoNewline }
    else { Remove-Item $settings -ErrorAction SilentlyContinue }
    if ($hadLegacy) { Set-Content -Path $legacy -Value $bakLegacy -Encoding utf8 -NoNewline }
    else { Remove-Item $legacy -ErrorAction SilentlyContinue }
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

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'ALL CHECKS PASSED' } else { Write-Output ('' + $script:fail + ' CHECK(S) FAILED') }

Write-BBDone 'zoom-wheel'
