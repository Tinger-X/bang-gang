# Attachments inside a SENT message: does the bubble show them, and does clicking an
# image open it?
#
# The draft strip (draft-strip.ps1) only covers the row above the input box. Everything
# past Send is a different control (MessageBubble) on a different path, and nothing else
# watches it. It matters twice over: the user asked for attachments to be visible in the
# conversation, and an image that cannot be opened is the whole reason ImageViewer exists.
#
# What it pins down:
#   A. the image is really painted inside the bubble -- not "a bubble appeared" but
#      "the fixture's colour is on screen at the spot the layout says it should be",
#      plus a pixel count over the bubble's rect (a cropped or blank image fails both)
#   B. clicking it opens the viewer -- the window goes dark everywhere (the scrim), and
#      the picture is redrawn at the window's centre
#   C. Esc closes it, and so does a click on the scrim outside the picture
#   D. a FILE attachment takes its own row in the bubble: the same message with and
#      without one differs in height by exactly the row it occupies (34) + the gap (4)
#
# D is why there are two conversations below rather than one message with two sends:
# a single bubble per conversation means nothing can scroll, and the height is read off
# the same "right-aligned row, empty chat" geometry both times, so the difference is
# the one thing that changed.
#
# ASCII ONLY (PowerShell 5.1 reads a BOM-less file as ANSI). The fixture colour is
# magenta deliberately: nothing in either theme is anywhere near it, so counting it
# cannot be fooled by the palette -- unlike "count the dark pixels", which a dark
# theme would satisfy with the background alone.

. "$PSScriptRoot\_ui.ps1"

$settings = Join-Path (Split-Path $script:BBExe -Parent) 'settings.json'

# ---- fixtures, under shoots\ (gitignored), never the system temp -----------------

$fixDir = Join-Path $script:BBShoots 'fixtures'
if (-not (Test-Path $fixDir)) { [void](New-Item -ItemType Directory -Path $fixDir -Force) }

$pngPath = Join-Path $fixDir 'zoom-photo.png'
$txtPath = Join-Path $fixDir 'zoom-note.txt'

$bmp = New-Object System.Drawing.Bitmap 160, 110
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(210, 0, 150))
$g.Dispose()
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Set-Content -Path $txtPath -Value 'zoom fixture note' -Encoding Ascii

$ImgW = 160
$ImgH = 110

# ---- helpers ---------------------------------------------------------------------

$script:fail = 0
function Check([bool]$ok, [string]$label, [string]$detail) {
    $tag = 'FAIL'
    if ($ok) { $tag = 'ok  ' }
    if (-not $ok) { $script:fail++ }
    Write-Output ('  [' + $tag + '] ' + $label + '  ' + $detail)
}

# One screen pixel, read back through a 1x1 grab. Not BB::PxAt: that one answers black
# for a point outside the window, and a black answer reads as "very different from the
# background" -- an out-of-window sample point would pass a difference test.
function Get-Px([int]$x, [int]$y) {
    $b = Get-Crop $x $y 1 1
    $c = $b.GetPixel(0, 0)
    $b.Dispose()
    return $c
}

function Test-Magenta($c) {
    return ($c.R -ge 170 -and $c.G -le 70 -and $c.B -ge 110)
}

function Count-Magenta($b) {
    $n = 0
    for ($y = 0; $y -lt $b.Height; $y++) {
        for ($x = 0; $x -lt $b.Width; $x++) {
            $c = $b.GetPixel($x, $y)
            if ($c.R -ge 170 -and $c.G -le 70 -and $c.B -ge 110) { $n++ }
        }
    }
    return $n
}

function Test-Dark($c) { return ($c.R -lt 70 -and $c.G -lt 70 -and $c.B -lt 70) }

function Get-InputEdit($main) {
    foreach ($h in Get-WinKids $main) {
        if ((Get-ShortClass $h) -ne 'Edit') { continue }
        $r = Get-WinRect $h
        if (($r.Bottom - $r.Top) -gt 40) { return @{ H = $h; R = $r } }
    }
    return $null
}

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

# The user's row: ChatView.LayoutRows right-aligns it, so its left edge sits far from
# the chat view's left margin -- while an assistant row is pinned AT that margin. That
# is the only thing distinguishing them; there is no name to look up.
function Get-UserBubble($main, $chat) {
    $best = $null
    foreach ($h in Get-WinKids $main) {
        if ((Get-ShortClass $h) -like '*SCROLLBAR*') { continue }
        $r = Get-WinRect $h
        if ($r.Top -lt $chat.Top) { continue }
        if ($r.Left -lt ($chat.Left + 100)) { continue }
        if ($r.Right -gt ($chat.Right + 2)) { continue }
        if ($null -eq $best -or $r.Top -lt $best.Top) { $best = $r }
    }
    return $best
}

# Paste whatever the clipboard holds into the input box, the way a user does. The
# clipboard is the only way in that does not need a modal file dialog: the attach
# button opens a native OpenFileDialog, and driving one of those from a probe is its
# own adventure.
function Invoke-PasteFile($main, $box, [string]$path) {
    Invoke-MouseClick ([int](($box.R.Left + $box.R.Right) / 2)) ([int]($box.R.Top + 40))
    Start-Sleep -Milliseconds 200
    Set-Clipboard -Path $path
    Start-Sleep -Milliseconds 300
    [BB]::Chord(0x56)          # VK_V
    Start-Sleep -Milliseconds 600
}

function Invoke-NewConversation($main) {
    $pill = Get-SearchPill $main
    if ($null -eq $pill) { throw 'search pill not found' }
    $b = Get-PillButtons $main $pill
    if ($null -eq $b) { throw 'pill buttons not found' }
    Invoke-MouseClick ([int](($b[1].Left + $b[1].Right) / 2)) ([int](($b[1].Top + $b[1].Bottom) / 2))
    Start-Sleep -Milliseconds 900
}

function Invoke-Send($main, $box) {
    $ib = Get-InputButtons $main $box.H
    if ($null -eq $ib) { throw 'send button not found' }
    Invoke-MouseClick ([int](($ib.Send.Left + $ib.Send.Right) / 2)) ([int](($ib.Send.Top + $ib.Send.Bottom) / 2))
    Start-Sleep -Milliseconds 1000
}

# The settings file is replaced so the app runs with NO endpoint: this probe is about
# the bubble, and a real provider would turn every send into a network call to a key
# this machine may or may not have. Backed up and restored -- it holds the user's.
$existed = Test-Path $settings
$backup = $null
if ($existed) { $backup = Get-Content $settings -Raw }

$script:hImg = 0
$script:hBoth = 0
$script:inBubble = 0
$script:onClick = $null
$script:beforePx = $null
$script:scrimPx = $null
$script:centerPx = $null
$script:afterEscPx = $null
$script:afterOutPx = $null

try {
    Set-Content -Path $settings -Value '{"ThemeMode":"light"}' -Encoding utf8

    Invoke-BBProbe {
        $main = Start-BangGang 5
        $mr = Get-WinRect $main

        # ---- A. one image, sent ----
        Invoke-NewConversation $main
        $box = Get-InputEdit $main
        if ($null -eq $box) { throw 'input text box not found' }

        Invoke-PasteFile $main $box $pngPath
        Invoke-Send $main $box

        $chat = Get-ChatPanel $main
        if ($null -eq $chat) { throw 'chat panel not found' }
        $ub = Get-UserBubble $main $chat
        if ($null -eq $ub) { throw 'no user bubble after sending the image' }

        $script:hImg = $ub.Bottom - $ub.Top
        $b = New-Object System.Drawing.Bitmap ($ub.Right - $ub.Left), ($ub.Bottom - $ub.Top)
        $gg = [System.Drawing.Graphics]::FromImage($b)
        $gg.CopyFromScreen($ub.Left, $ub.Top, 0, 0, (New-Object System.Drawing.Size ($ub.Right - $ub.Left), ($ub.Bottom - $ub.Top)))
        $gg.Dispose()
        $script:inBubble = Count-Magenta $b
        $b.Dispose()

        # MessageBubble places the first image at (PadX, PadY) = (14, 11); a user bubble
        # has no "help" header above it. Same numbers the paint code uses.
        $ix = $ub.Left + 14 + [int]($ImgW / 2)
        $iy = $ub.Top + 11 + [int]($ImgH / 2)
        $script:onClick = Get-Px $ix $iy

        # ---- B. click it open ----
        $script:beforePx = Get-Px ($mr.Left + 60) ($mr.Top + 300)
        Invoke-MouseClick $ix $iy
        Start-Sleep -Milliseconds 600

        $script:scrimPx = Get-Px ($mr.Left + 60) ($mr.Top + 300)
        $script:centerPx = Get-Px ([int](($mr.Left + $mr.Right) / 2)) ([int](($mr.Top + $mr.Bottom) / 2))
        Save-WindowShot $main (Get-ShotPath 'attachment-zoom.png')

        # ---- C1. Esc ----
        # Key, not Chord: Chord is Ctrl+<vk>, and Ctrl+Esc opens the Start menu, whose
        # overlay then eats the rest of the run (see the note on BB.Key).
        [BB]::Key(0x1B)            # VK_ESCAPE
        Start-Sleep -Milliseconds 500
        $script:afterEscPx = Get-Px ($mr.Left + 60) ($mr.Top + 300)

        # ---- C2. a click on the scrim, well outside the picture ----
        Invoke-MouseClick $ix $iy
        Start-Sleep -Milliseconds 500
        Invoke-MouseClick ($mr.Left + 30) ($mr.Top + 300)
        Start-Sleep -Milliseconds 500
        $script:afterOutPx = Get-Px ($mr.Left + 60) ($mr.Top + 300)

        # ---- D. the same message, plus a file ----
        Invoke-NewConversation $main
        $box2 = Get-InputEdit $main
        if ($null -eq $box2) { throw 'input text box not found (2nd conversation)' }
        Invoke-PasteFile $main $box2 $pngPath
        Invoke-PasteFile $main $box2 $txtPath
        # Evidence for the case where D comes back flat: if the second paste never
        # landed, the bubble is short because the DRAFT was short, and this shot says
        # which of the two it was without another run.
        Save-WindowShot $main (Get-ShotPath 'attachment-zoom-draft.png')
        Invoke-Send $main $box2

        $chat2 = Get-ChatPanel $main
        if ($null -eq $chat2) { throw 'chat panel not found (2nd conversation)' }
        $ub2 = Get-UserBubble $main $chat2
        if ($null -eq $ub2) { throw 'no user bubble after sending image + file' }
        $script:hBoth = $ub2.Bottom - $ub2.Top
    }
} finally {
    if ($existed) { Set-Content -Path $settings -Value $backup -Encoding utf8 -NoNewline }
    else { Remove-Item $settings -ErrorAction SilentlyContinue }
}

Write-Output ''

Write-Output '--- A. the image is painted inside the sent bubble ---'
Write-Output ('  bubble height ' + $script:hImg + 'px   (expected ' + (11 + $ImgH + 11) + 'px + one text line)')
Check ($script:hImg -gt ($ImgH + 20)) 'bubble is at least as tall as the image' ($script:hImg.ToString() + 'px')
# 160x110 = 17600 pixels; the rounded corners and their antialiasing take a few hundred.
Check ($script:inBubble -gt 15000) 'fixture colour fills the bubble area' ($script:inBubble.ToString() + ' px of 17600')
$clickOk = $false
if ($null -ne $script:onClick) { $clickOk = Test-Magenta $script:onClick }
Check $clickOk 'image sits where the layout says it does' ('pixel at the image centre = ' + $script:onClick.R + ',' + $script:onClick.G + ',' + $script:onClick.B)

Write-Output ''
Write-Output '--- B. clicking it opens the viewer ---'
Write-Output ('  sidebar pixel: closed ' + $script:beforePx.R + ',' + $script:beforePx.G + ',' + $script:beforePx.B +
              '   open ' + $script:scrimPx.R + ',' + $script:scrimPx.G + ',' + $script:scrimPx.B)
Check (-not (Test-Dark $script:beforePx)) 'the sample point starts on the light UI' ($script:beforePx.R.ToString() + ',' + $script:beforePx.G + ',' + $script:beforePx.B)
Check (Test-Dark $script:scrimPx) 'the scrim covers it once open' ($script:scrimPx.R.ToString() + ',' + $script:scrimPx.G + ',' + $script:scrimPx.B)
Check (Test-Magenta $script:centerPx) 'the picture is redrawn at the window centre' ($script:centerPx.R.ToString() + ',' + $script:centerPx.G + ',' + $script:centerPx.B)

Write-Output ''
Write-Output '--- C. closing it ---'
Check (-not (Test-Dark $script:afterEscPx)) 'Esc closes' ($script:afterEscPx.R.ToString() + ',' + $script:afterEscPx.G + ',' + $script:afterEscPx.B)
Check (-not (Test-Dark $script:afterOutPx)) 'a click on the scrim closes' ($script:afterOutPx.R.ToString() + ',' + $script:afterOutPx.G + ',' + $script:afterOutPx.B)

Write-Output ''
Write-Output '--- D. a file attachment takes its own row ---'
$d = $script:hBoth - $script:hImg
Write-Output ('  bubble height: image alone ' + $script:hImg + 'px   image + file ' + $script:hBoth + 'px   delta ' + $d + 'px')
Check ($d -gt 30 -and $d -lt 46) 'file row is inside the bubble' ('delta ' + $d + 'px, expected 34 row + 4 gap = 38')

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'ALL CHECKS PASSED' } else { Write-Output ('' + $script:fail + ' CHECK(S) FAILED') }

Write-BBDone 'attachment-zoom'
