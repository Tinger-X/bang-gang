# Attachments inside a SENT message: does the bubble show them, and does clicking an
# image open it?
#
# The draft strip (draft-strip.ps1) only covers the row above the input box. Everything
# past Send is a different control (MessageBubble) on a different path, and nothing else
# watches it. It matters twice over: the user asked for attachments to be visible in the
# conversation, and an image that cannot be opened is the whole reason ImageViewer exists.
#
# What it pins down:
#   A. the image is really painted -- not "a bubble appeared" but "the fixture's colour is
#      on screen at the spot the layout says it should be", plus a pixel count over the
#      chip (a cropped or blank thumbnail fails both), plus the item-1 pair: the chip sits
#      on chat background and the bubble starts BELOW it, i.e. the attachments are outside
#      the bubble rather than inside it
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
# 0.9.0 note: the rows come from ui-rows.json now, not from the control tree -- the bubbles
# are painted by ChatView rather than each being a child HWND. See the ui-rows.json block
# in _ui.ps1, and Get-UserRect below for what that replaced here.
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
# (an image chip is exactly ChipH wide, a file chip ChipNatW), chips sit ChipGap apart.
#
# 0.9.0 moved the attachments OUT of the bubble (item 1): LayoutChips() now starts at
# y = 0 -- the top of the whole message -- and the bubble's rounded rect only begins below
# them, at ChipH + AttachGap = 52. So a chip's own y inside the row is 0, where it used to
# be PadY. $ROWH is what the row should come to: the attachment strip, the gap, then the
# bubble's own padding top and bottom around one line of body text.
#
# Hard-coded on purpose -- if any of these move, the click point lands on the wrong pixel
# and A/D fail, which is the whole point of not deriving them from the app.
$CHIP = 44
$PADX = 14
$CHIPY = 0
$ATTACHGAP = 8
$BODYPAD = 11
$GAP = 8
$FILEW = 118
$TEXTH = 27                      # one markdown line of body text: 20px pitch + 7px space-before
$ROWH = $CHIP + $ATTACHGAP + $BODYPAD + $TEXTH + $BODYPAD

# Sent alongside the image. It is not decoration: a user message with ONLY attachments is
# now drawn as the chip row alone -- no bubble at all (item 6) -- so a fixture that sends
# no text would have nothing to hold the "the attachment sits OUTSIDE the bubble" pair in
# section A2 up against. Section E covers the text-less case on its own.
$BODYTEXT = 'attachment probe note'

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

# The user bubble's own fill: Theme.UserBubble = (219,233,255), against a chat background
# of plain white. The red channel alone separates them (219 vs 255), which is what makes
# this usable as "the bubble is / is not here" at a single pixel.
function Test-BubbleBlue($c) { return ($c.R -le 240 -and $c.B -ge 245 -and $c.B -gt $c.R) }

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

# The user's row, from the snapshot ChatView writes.
#
# This used to walk the control tree: the user's bubble is the one whose left edge sits
# far from the chat view's margin, since the assistant's is pinned AT it. There is no
# tree to walk now -- 0.9.0 made ChatView paint the bubbles itself, and a silent 0 here
# would read as "nothing was sent", which is the failure this probe is looking for. The
# snapshot names the role outright, which is strictly better than inferring it from an x.
function Get-UserRect {
    $ui = Get-UiRows
    $rows = @(Get-UiRowsOf $ui 'user')
    if ($rows.Count -eq 0) { return $null }
    $s = Get-UiRowScreen $ui $rows[0]
    if ($null -eq $s) { return $null }
    return @{ L = $s.L; T = $s.T; W = $s.W; H = $s.H; R = ($s.L + $s.W); B = ($s.T + $s.H) }
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

# Same route for body text -- the clipboard, so no per-character key timing to get wrong.
function Invoke-PasteText($main, $box, [string]$text) {
    Invoke-MouseClick ([int](($box.R.Left + $box.R.Right) / 2)) ([int]($box.R.Top + 40))
    Start-Sleep -Milliseconds 200
    Set-Clipboard -Value $text
    Start-Sleep -Milliseconds 300
    [BB]::Chord(0x56)          # VK_V
    Start-Sleep -Milliseconds 400
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
$script:insideAtChip = $null
$script:insideAtFoot = $null
$script:bgBefore = $null
$script:bgScrim = $null
$script:chipScrim = $null
$script:centerPx = $null
$script:afterEscPx = $null
$script:afterOutPx = $null
$script:midBefore = $null
$script:midAfterFile = $null
$script:midAfterImg = $null
$script:hOnly = 0
$script:onlyChipPx = $null
$script:onlyLeftPx = $null

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
        Invoke-PasteText $main $box $BODYTEXT
        Invoke-Send $main $box

        $ub = Get-UserRect
        if ($null -eq $ub) { throw 'no user row after sending the image' }

        $script:hImg = $ub.B - $ub.T
        $b = New-Object System.Drawing.Bitmap $ub.W, $ub.H
        $gg = [System.Drawing.Graphics]::FromImage($b)
        $gg.CopyFromScreen($ub.L, $ub.T, 0, 0, (New-Object System.Drawing.Size $ub.W, $ub.H))
        $gg.Dispose()
        $script:inBubble = Count-Magenta $b
        $b.Dispose()

        # LayoutChips puts the first chip at (PadX, 0) -- y = 0 because the attachment strip
        # IS the top of the row now (see the constants at the top of this file).
        $ix = $ub.L + $PADX + [int]($CHIP / 2)
        $iy = $ub.T + $CHIPY + [int]($CHIP / 2)
        $script:onClick = Get-Px $ix $iy

        # ---- A2. the chip row is OUTSIDE the bubble, above it ----
        # Item 1 of the round: the bubble used to contain the chips, so this same pixel
        # used to be bubble blue. Both points are on the same vertical line, 4px in from
        # the row's left edge so they miss the chip (which starts at PadX = 14) -- one at
        # the chip's own height, one down at the bottom of the row.
        $script:insideAtChip = Get-Px ($ub.L + 4) $iy
        $script:insideAtFoot = Get-Px ($ub.L + 4) ($ub.B - 8)

        # The pair the scrim checks are built on: the chip, and the chat background 24px
        # to its left (the user bubble is right-aligned and one chip wide, so that strip
        # is empty chat). Both are outside the picture the viewer draws at the centre.
        $bgX = $ub.L - 24
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
        Invoke-PasteText $main $box2 $BODYTEXT
        # Evidence for the case where D comes back flat: if the second paste never
        # landed, the bubble is short because the DRAFT was short, and this shot says
        # which of the two it was without another run.
        Save-WindowShot $main (Get-ShotPath 'attachment-zoom-draft.png')
        Invoke-Send $main $box2

        $ub2 = Get-UserRect
        if ($null -eq $ub2) { throw 'no user row after sending image + file' }
        $script:hBoth = $ub2.B - $ub2.T

        # The window centre is plain chat background here -- the picture is only ever
        # drawn there BY THE VIEWER, so "is it magenta" is a clean "did it open".
        $midX = [int](($mr.Left + $mr.Right) / 2)
        $midY = [int](($mr.Top + $mr.Bottom) / 2)
        $chipY = $ub2.T + $CHIPY + [int]($CHIP / 2)
        $imgX = $ub2.L + $PADX + [int]($CHIP / 2)
        $fileX = $ub2.L + $PADX + $CHIP + $GAP + [int]($FILEW / 2)

        $script:midBefore = Get-Px $midX $midY
        Invoke-MouseClick $fileX $chipY        # the file chip: nothing may happen
        Start-Sleep -Milliseconds 700
        $script:midAfterFile = Get-Px $midX $midY
        Invoke-MouseClick $imgX $chipY         # the image chip 82px away: must open
        Start-Sleep -Milliseconds 700
        $script:midAfterImg = Get-Px $midX $midY
        [BB]::Key(0x1B)
        Start-Sleep -Milliseconds 400

        # ---- E. an image and NO text: the chip row alone, with no empty bubble ----
        # Item 6. The bubble used to be drawn regardless, so this message came out as a
        # 74px row -- a 44px chip with a hollow 30px bubble hanging under it. The row
        # should now be the chip and nothing else. Note the chip also moves: the bubble's
        # 2 x PadX went with the bubble, so it starts at the row's own left edge.
        Invoke-NewConversation $main
        $box3 = Get-InputEdit $main
        if ($null -eq $box3) { throw 'input text box not found (3rd conversation)' }
        Invoke-PasteFile $main $box3 $pngPath
        Invoke-Send $main $box3

        $ub3 = Get-UserRect
        if ($null -eq $ub3) { throw 'no user row after sending an image with no text' }
        $script:hOnly = $ub3.B - $ub3.T
        $script:onlyChipPx = Get-Px ($ub3.L + [int]($CHIP / 2)) ($ub3.T + [int]($CHIP / 2))
        $script:onlyLeftPx = Get-Px ($ub3.L - 8) ($ub3.T + [int]($CHIP / 2))
    }
} finally {
    if ($existed) { Set-Content -Path $settings -Value $backup -Encoding utf8 -NoNewline }
    else { Remove-Item $settings -ErrorAction SilentlyContinue }
}

Write-Output ''

Write-Output '--- A. the image is painted above the sent bubble ---'
Write-Output ('  row height ' + $script:hImg + 'px   (attachment strip + gap + bubble body = ' + $ROWH + 'px)')
Check ([Math]::Abs($script:hImg - $ROWH) -le 6) 'the row is one chip row plus the bubble' `
    ($script:hImg.ToString() + 'px vs ' + $ROWH + 'px; the chip alone would be ' + $CHIP + 'px')
# 44x44 = 1936 pixels; the rounded corners and their antialiasing take the rest.
Check ($script:inBubble -gt 1500) 'fixture colour fills the chip' ($script:inBubble.ToString() + ' px of 1936')
$clickOk = $false
if ($null -ne $script:onClick) { $clickOk = Test-Magenta $script:onClick }
Check $clickOk 'the chip sits where the layout says it does' ('pixel at the chip centre = ' + $script:onClick.R + ',' + $script:onClick.G + ',' + $script:onClick.B)

# Item 1: the attachments are laid out ABOVE the bubble, not inside it. Two points on the
# same column, 4px in from the row's left edge so they clear the chip (which starts at
# PadX = 14): one level with the chip, one down in the bubble's own body. Only the second
# may be blue. Both are asserted, because "the chip is on white" alone would also be true
# of a build that simply stopped painting the bubble at all.
Write-Output ('  same column, at the chip ' + $script:insideAtChip.R + ',' + $script:insideAtChip.G + ',' + $script:insideAtChip.B +
              '   at the bubble body ' + $script:insideAtFoot.R + ',' + $script:insideAtFoot.G + ',' + $script:insideAtFoot.B)
Check (-not (Test-BubbleBlue $script:insideAtChip)) 'at the chip''s height it is chat background, not bubble' `
    ($script:insideAtChip.R.ToString() + ',' + $script:insideAtChip.G + ',' + $script:insideAtChip.B)
Check (Test-BubbleBlue $script:insideAtFoot) 'and the bubble body below them still is a bubble' `
    ($script:insideAtFoot.R.ToString() + ',' + $script:insideAtFoot.G + ',' + $script:insideAtFoot.B)

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
Write-Output ('  row height: image alone ' + $script:hImg + 'px   image + file ' + $script:hBoth + 'px   delta ' + $d + 'px')
Check ([Math]::Abs($d) -le 3) 'the file chip shares the row instead of adding one' `
    ('delta ' + $d + 'px; a per-file row would have made it 38')
Check (-not (Test-Magenta $script:midBefore)) 'the window centre starts on plain chat background' `
    ($script:midBefore.R.ToString() + ',' + $script:midBefore.G + ',' + $script:midBefore.B)
Check (-not (Test-Magenta $script:midAfterFile)) 'clicking the FILE chip opens nothing' `
    ($script:midAfterFile.R.ToString() + ',' + $script:midAfterFile.G + ',' + $script:midAfterFile.B)
Check (Test-Magenta $script:midAfterImg) 'and the image chip beside it does open' `
    ($script:midAfterImg.R.ToString() + ',' + $script:midAfterImg.G + ',' + $script:midAfterImg.B)

Write-Output ''
Write-Output '--- E. an image with no text draws no bubble at all ---'
Write-Output ('  row height: image + text ' + $script:hImg + 'px   image alone ' + $script:hOnly + 'px   (the chip is ' + $CHIP + 'px)')
Check ([Math]::Abs($script:hOnly - $CHIP) -le 2) 'the row is the chip row and nothing under it' `
    ($script:hOnly.ToString() + 'px; an empty bubble would have made it ' + ($CHIP + $ATTACHGAP + $BODYPAD + $BODYPAD) + 'px or more')
# The control for the line above: if the app stopped rendering body text, $hImg would
# collapse onto $hOnly and that assertion would pass on a build with no text in it.
Check (($script:hImg - $script:hOnly) -ge 20) 'and the same send WITH text is visibly taller' `
    ('delta ' + ($script:hImg - $script:hOnly) + 'px')
# The chip moved out to the row's own left edge: the 2 x PadX it used to sit inside went
# with the bubble. Sampling at CHIP/2 in from the row's left edge only lands on the picture
# if the chip really starts at x = 0.
Check (Test-Magenta $script:onlyChipPx) 'the chip is flush with the row''s left edge' `
    ($script:onlyChipPx.R.ToString() + ',' + $script:onlyChipPx.G + ',' + $script:onlyChipPx.B)
Check (-not (Test-Magenta $script:onlyLeftPx)) 'and there is nothing painted to its left' `
    ($script:onlyLeftPx.R.ToString() + ',' + $script:onlyLeftPx.G + ',' + $script:onlyLeftPx.B)

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'ALL CHECKS PASSED' } else { Write-Output ('' + $script:fail + ' CHECK(S) FAILED') }

Write-BBDone 'attachment-zoom'
