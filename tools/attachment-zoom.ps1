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
#      plus a pixel count over the chip (a cropped or blank thumbnail fails both)
#   B. clicking it opens the viewer -- and the scrim is TRANSLUCENT, which takes three
#      checks to say (see the note further down; two of them can be satisfied by an
#      opaque scrim, and the third is the one that cannot)
#   C. Esc closes it, and so does a click on the scrim outside the picture
#   D. attachments are 44px thumbnails on ONE row, and only the image is clickable:
#      clicking the file chip must not open anything, and the image chip right next to
#      it must -- the pair is what rules out "clicks just do not work in that corner"
#
# D is why there are two conversations below rather than one message with two sends:
# a single bubble per conversation means nothing can scroll, and both heights are read
# off the same "right-aligned row, empty chat" geometry, so the comparison is clean.
#
# The settings fixture carries a provider pointing at 127.0.0.1:9 (discard). Not for a
# reply -- the port refuses instantly -- but because the send gate refuses outright when
# no model is configured, and a probe that cannot send cannot measure a SENT message.
#
# ASCII ONLY (PowerShell 5.1 reads a BOM-less file as ANSI). The fixture colour is
# magenta deliberately: nothing in either theme is anywhere near it, so counting it
# cannot be fooled by the palette -- unlike "count the dark pixels", which a dark
# theme would satisfy with the background alone.

. "$PSScriptRoot\_ui.ps1"

$settings = Join-Path (Split-Path $script:BBExe -Parent) 'settings.json'

# The chat view's own thumbnail geometry, mirrored from DraftStrip: a chip is 44px square
# (an image chip is exactly ChipH wide, a file chip ChipNatW), chips sit ChipGap apart,
# and MessageBubble lays the first one at (PadX, PadY) = (14, 11) inside the bubble.
# Hard-coded here on purpose -- if any of these move, the click point lands on the wrong
# pixel and A/D fail, which is the whole point of not deriving them from the app.
$CHIP = 44
$PADX = 14
$PADY = 11
$GAP = 8
$FILEW = 118

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

# Reads a pixel back through a 1x1 grab. Not BB::PxAt: that one answers black for a
# point outside the window, and a black answer reads as "very different from the
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

function Lum($c) { return 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B }

# "Not a black hole." The scrim is (12,14,18) at alpha 150, so what comes back through it
# is a washed-out version of the UI underneath -- well above 40 in every channel, while an
# opaque scrim of the same colour would answer (12,14,18) and fail this.
function Test-NotBlack($c) { return ([Math]::Max($c.R, [Math]::Max($c.G, $c.B)) -ge 40) }

function Lum($c) { return 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B }

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

# The settings file is replaced so the app runs against a dead endpoint: this probe is
# about the bubble, and a real provider would turn every send into a network call to a
# key this machine may or may not have. Backed up and restored -- it holds the user's.
$existed = Test-Path $settings
$backup = $null
if ($existed) { $backup = Get-Content $settings -Raw }

$script:hImg = 0
$script:hBoth = 0
$script:inBubble = 0
$script:onClick = $null
$script:bgBefore = $null
$script:bgScrim = $null
$script:chipScrim = $null
$script:centerPx = $null
$script:afterEscPx = $null
$script:afterOutPx = $null
$script:midBefore = $null
$script:midAfterFile = $null
$script:midAfterImg = $null

try {
    # url + model, so the send gate lets the message through. No key: a local server
    # needs none and LlmConfig.Problem does not ask for one.
    Set-Content -Path $settings -Encoding utf8 `
        -Value '{"ThemeMode":"light","ChatProvider":"custom","ChatProfiles":{"custom":{"url":"http://127.0.0.1:9/v1","key":"probe","model":"probe-model"}}}'

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

        # MessageBubble lays the first chip at (PadX, PadY) = (14, 11), and a user bubble
        # has no "help" header above it, so this lands on the chip's centre.
        $ix = $ub.Left + $PADX + [int]($CHIP / 2)
        $iy = $ub.Top + $PADY + [int]($CHIP / 2)
        $script:onClick = Get-Px $ix $iy

        # The pair the scrim checks are built on: the chip, and the chat background 24px
        # to its left (the user bubble is right-aligned and one chip wide, so that strip
        # is empty chat). Both are outside the picture the viewer draws at the centre.
        $bgX = $ub.Left - 24
        $script:bgBefore = Get-Px $bgX $iy

        # ---- B. click it open ----
        Invoke-MouseClick $ix $iy
        Start-Sleep -Milliseconds 700

        $script:bgScrim = Get-Px $bgX $iy
        $script:chipScrim = Get-Px $ix $iy
        $script:centerPx = Get-Px ([int](($mr.Left + $mr.Right) / 2)) ([int](($mr.Top + $mr.Bottom) / 2))
        Save-WindowShot $main (Get-ShotPath 'attachment-zoom.png')

        # ---- C1. Esc ----
        # Key, not Chord: Chord is Ctrl+<vk>, and Ctrl+Esc opens the Start menu, whose
        # overlay then eats the rest of the run (see the note on BB.Key).
        [BB]::Key(0x1B)            # VK_ESCAPE
        Start-Sleep -Milliseconds 500
        $script:afterEscPx = Get-Px $bgX $iy

        # ---- C2. a click on the scrim, well outside the picture ----
        # Park the pointer first. The last click was on that exact pixel, and two clicks
        # at one spot inside the double-click interval arrive as a DoubleClick -- WinForms
        # raises Click for the first only, so the viewer would never reopen and the next
        # assertion would fail for a reason that has nothing to do with the app.
        [void][BB]::SetCursorPos(($bgX), ($iy))
        Start-Sleep -Milliseconds 700
        Invoke-MouseClick $ix $iy
        Start-Sleep -Milliseconds 600
        Invoke-MouseClick ($bgX - 4) $iy
        Start-Sleep -Milliseconds 600
        $script:afterOutPx = Get-Px $bgX $iy

        # ---- D. image + file in one message ----
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

        # The window centre is plain chat background here -- the picture is only ever
        # drawn there BY THE VIEWER, so "is it magenta" is a clean "did it open".
        $midX = [int](($mr.Left + $mr.Right) / 2)
        $midY = [int](($mr.Top + $mr.Bottom) / 2)
        $chipY = $ub2.Top + $PADY + [int]($CHIP / 2)
        $imgX = $ub2.Left + $PADX + [int]($CHIP / 2)
        $fileX = $ub2.Left + $PADX + $CHIP + $GAP + [int]($FILEW / 2)

        $script:midBefore = Get-Px $midX $midY
        Invoke-MouseClick $fileX $chipY        # the file chip: nothing may happen
        Start-Sleep -Milliseconds 700
        $script:midAfterFile = Get-Px $midX $midY
        Invoke-MouseClick $imgX $chipY         # the image chip 82px away: must open
        Start-Sleep -Milliseconds 700
        $script:midAfterImg = Get-Px $midX $midY
        [BB]::Key(0x1B)
        Start-Sleep -Milliseconds 400
    }
} finally {
    if ($existed) { Set-Content -Path $settings -Value $backup -Encoding utf8 -NoNewline }
    else { Remove-Item $settings -ErrorAction SilentlyContinue }
}

Write-Output ''

Write-Output '--- A. the image is painted inside the sent bubble ---'
Write-Output ('  bubble height ' + $script:hImg + 'px   (one 44px chip row = ' + ($PADY + $CHIP + 12 + $PADY) + 'px)')
Check ($script:hImg -ge 70 -and $script:hImg -le 92) 'bubble is one chip row tall' ($script:hImg.ToString() + 'px')
# 44x44 = 1936 pixels; the rounded corners and their antialiasing take the rest.
Check ($script:inBubble -gt 1500) 'fixture colour fills the chip' ($script:inBubble.ToString() + ' px of 1936')
$clickOk = $false
if ($null -ne $script:onClick) { $clickOk = Test-Magenta $script:onClick }
Check $clickOk 'the chip sits where the layout says it does' ('pixel at the chip centre = ' + $script:onClick.R + ',' + $script:onClick.G + ',' + $script:onClick.B)

Write-Output ''
Write-Output '--- B. clicking it opens the viewer, behind a translucent scrim ---'
Write-Output ('  background: closed ' + $script:bgBefore.R + ',' + $script:bgBefore.G + ',' + $script:bgBefore.B +
              '   open ' + $script:bgScrim.R + ',' + $script:bgScrim.G + ',' + $script:bgScrim.B +
              '   chip under the scrim ' + $script:chipScrim.R + ',' + $script:chipScrim.G + ',' + $script:chipScrim.B)
Check ((Lum $script:bgBefore) -gt 150) 'the sample point starts on the light UI' ((Lum $script:bgBefore).ToString('0.0') + ' luminance')
Check (Test-NotBlack $script:bgScrim) 'the scrim is not black -- the UI shows through' ($script:bgScrim.R.ToString() + ',' + $script:bgScrim.G + ',' + $script:bgScrim.B)
Check (((Lum $script:bgBefore) - (Lum $script:bgScrim)) -ge 60) 'and it is clearly darker than it was' `
    ((Lum $script:bgBefore).ToString('0.0') + ' -> ' + (Lum $script:bgScrim).ToString('0.0'))
# The one an opaque scrim cannot pass: it would paint every pixel underneath it the same
# colour, so the chip and the background beside it would come back equal. Keeping them
# apart is what "translucent" means and nothing else here measures it.
Check ([Math]::Abs((Lum $script:chipScrim) - (Lum $script:bgScrim)) -ge 30) `
    'two points that differed before still differ under it' `
    ((Lum $script:chipScrim).ToString('0.0') + ' vs ' + (Lum $script:bgScrim).ToString('0.0'))
Check (Test-Magenta $script:centerPx) 'the picture is redrawn at the window centre' ($script:centerPx.R.ToString() + ',' + $script:centerPx.G + ',' + $script:centerPx.B)

Write-Output ''
Write-Output '--- C. closing it ---'
Check ((Lum $script:afterEscPx) -gt 150) 'Esc closes' ((Lum $script:afterEscPx).ToString('0.0') + ' luminance')
Check ((Lum $script:afterOutPx) -gt 150) 'a click on the scrim closes' ((Lum $script:afterOutPx).ToString('0.0') + ' luminance')

Write-Output ''
Write-Output '--- D. one row of chips, and only the image is clickable ---'
$d = $script:hBoth - $script:hImg
Write-Output ('  bubble height: image alone ' + $script:hImg + 'px   image + file ' + $script:hBoth + 'px   delta ' + $d + 'px')
Check ([Math]::Abs($d) -le 3) 'the file chip shares the row instead of adding one' `
    ('delta ' + $d + 'px; a per-file row would have made it 38')
Check (-not (Test-Magenta $script:midBefore)) 'the window centre starts on plain chat background' `
    ($script:midBefore.R.ToString() + ',' + $script:midBefore.G + ',' + $script:midBefore.B)
Check (-not (Test-Magenta $script:midAfterFile)) 'clicking the FILE chip opens nothing' `
    ($script:midAfterFile.R.ToString() + ',' + $script:midAfterFile.G + ',' + $script:midAfterFile.B)
Check (Test-Magenta $script:midAfterImg) 'and the image chip beside it does open' `
    ($script:midAfterImg.R.ToString() + ',' + $script:midAfterImg.G + ',' + $script:midAfterImg.B)

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'ALL CHECKS PASSED' } else { Write-Output ('' + $script:fail + ' CHECK(S) FAILED') }

Write-BBDone 'attachment-zoom'
