# The placeholder animation in the assistant bubble while the model is thinking about
# its first character.
#
# The complaint this exists for: between pressing send and the first token arriving,
# the assistant bubble held nothing but the "帮帮" name and the box closed right under
# it -- a 40px half-bubble that reads as "the reply is broken", for as long as the
# model takes to warm up. The fix reserves one full body line and pulses three dots on
# it, so the bubble is whole from the first frame and the animation says "working".
#
# Like llm-reply.ps1 there is no key on this machine, so the "endpoint" is a bare
# TcpListener (HTTP.SYS wants a urlacl reservation for anything but the admin-installed
# prefixes). Unlike llm-reply.ps1 the STUB IS THE POINT here: it accepts, sends headers,
# then says NOTHING for six seconds. That silence is the state under test -- the old
# probe's stub always had a token 150ms in, which is faster than any measurement could
# be taken in.
#
# What it pins down:
#   A. the bubble is whole while waiting  -- the row is a full body line tall, not the
#      40px stub, and nothing but the dots is painted on that line
#   B. the animation is actually running  -- two reads of that line ~300ms apart differ
#   C. and it is the ANIMATION, not noise  -- after the first character lands, two reads
#      of the same screen strip come back byte-identical. Without C, B is satisfied by
#      any two different-looking screens, including a repaint storm.
#   D. it stops at the FIRST CHARACTER    -- the silence continues for another four
#      seconds after the single token, so C is measured mid-stream, and the row is the
#      same height either side of the token (the reserved line IS a body line, which is
#      what makes the first character land without the bubble jumping)
#
# ASCII ONLY (PowerShell 5.1 reads a BOM-less file as ANSI). The settings file needs the
# provider key, which is Chinese, so it is built from code points.
#
# The stub is one connection, one response: it answers the first request and exits.

. "$PSScriptRoot\_ui.ps1"

$settings = Join-Path (Split-Path $script:BBExe -Parent) 'settings.json'
$trace = Join-Path (Split-Path $script:BBExe -Parent) 'ui-trace.log'
$chatsDir = Join-Path (Split-Path $script:BBExe -Parent) 'chats'

# The provider key the app defaults to. Code points: a literal here would be a
# non-ASCII byte in a file PowerShell reads as ANSI.
$provider = [string]([char]0x81EA) + [char]0x5B9A + [char]0x4E49

$token = 'ONE-TOKEN-ARRIVED-AND-THEN-QUIET'

# Six seconds of silence, one chunk, then four more seconds of silence. Both numbers
# are chosen against what the probe has to do inside them: ~1s to read the row and
# take two band samples while waiting, and ~2s after the token to take two more.
# Shorter and the second pair would race the end of the stream; longer and the probe
# just sits there.
$hold1 = 6000
$hold2 = 4000

$probe = New-Object System.Net.Sockets.TcpListener -ArgumentList @([System.Net.IPAddress]::Loopback, 0)
$probe.Start()
$port = $probe.LocalEndpoint.Port
$probe.Stop()

$baseUrl = 'http://127.0.0.1:' + $port + '/v1'

$job = Start-Job -ArgumentList $port, $hold1, $hold2, $token -ScriptBlock {
    param($port, $hold1, $hold2, $token)

    function Send-Line($stream, [string]$text) {
        $b = [System.Text.Encoding]::UTF8.GetBytes($text)
        $stream.Write($b, 0, $b.Length)
        $stream.Flush()
    }

    $listener = New-Object System.Net.Sockets.TcpListener -ArgumentList @([System.Net.IPAddress]::Loopback, $port)
    $listener.Start()
    try {
        $client = $listener.AcceptTcpClient()
        $stream = $client.GetStream()

        # Read until the headers are in and Content-Length worth of body has followed.
        # Not "until the stream goes quiet": a TcpClient read blocks until data shows up.
        $ms = New-Object System.IO.MemoryStream
        $tmp = New-Object byte[] 8192
        while ($true) {
            $n = $stream.Read($tmp, 0, $tmp.Length)
            if ($n -le 0) { break }
            $ms.Write($tmp, 0, $n)
            $txt = [System.Text.Encoding]::UTF8.GetString($ms.ToArray())
            $hdr = $txt.IndexOf("`r`n`r`n")
            if ($hdr -ge 0) {
                $len = 0
                if ($txt -match 'Content-Length:\s*(\d+)') { $len = [int]$Matches[1] }
                if ($ms.Length -ge ($hdr + 4 + $len)) { break }
            }
        }

        # Headers first: the app must consider the stream OPEN and sit in it. If it did
        # not, the silence below would be indistinguishable from a connection that never
        # came up, and the probe could not tell "waiting" from "broken".
        Send-Line $stream ("HTTP/1.1 200 OK`r`nContent-Type: text/event-stream`r`nCache-Control: no-cache`r`nConnection: close`r`n`r`n")
        Start-Sleep -Milliseconds $hold1

        $frame = @{ id = 'stub'; choices = @(@{ index = 0; delta = @{ content = $token } }) } |
                 ConvertTo-Json -Compress -Depth 8
        Send-Line $stream ('data: ' + $frame + "`r`n`r`n")
        Start-Sleep -Milliseconds $hold2

        $last = @{ id = 'stub'
                   choices = @(@{ index = 0; delta = @{}; finish_reason = 'stop' }) } |
                ConvertTo-Json -Compress -Depth 8
        Send-Line $stream ('data: ' + $last + "`r`n`r`n")
        Send-Line $stream "data: [DONE]`r`n`r`n"
        $client.Close()
    } finally {
        $listener.Stop()
    }
}

# ---- point the app at the stub -------------------------------------------------

$existed = Test-Path $settings
$backup = $null
if ($existed) { $backup = Get-Content $settings -Raw }

$hadChats = Test-Path $chatsDir
$bakChats = @{}
if ($hadChats) {
    foreach ($f in Get-ChildItem $chatsDir -File) { $bakChats[$f.Name] = (Get-Content $f.FullName -Raw) }
}

# Light palette pinned: the ink test counts DARK pixels, and on a dark chat background
# a bare row would come back full of "ink" -- section A would then pass on a bubble that
# is not there.
Set-Content -Path $settings -Encoding utf8 -NoNewline -Value (@{
    ChatProvider = $provider
    ChatProfiles = @{ $provider = @{ url = $baseUrl; key = 'sk-stub'; model = 'bb-stub-model' } }
    ThemeMode = 'light'
} | ConvertTo-Json -Depth 8)

$script:fail = 0
function Check([bool]$ok, [string]$label, [string]$detail) {
    $tag = 'FAIL'
    if ($ok) { $tag = 'ok  ' }
    if (-not $ok) { $script:fail++ }
    Write-Output ('  [' + $tag + '] ' + $label + '  ' + $detail)
}

# The geometry the animation is drawn in. Spelled out here rather than asked of the app:
# a probe that reads the app's own arithmetic agrees with the app even when both are
# wrong, and the whole point of A is that 67 is not 40.
$PADY = 11      # MessageBubble.PadY
$PITCH = 27     # one body line, Markdown.BodyLinePitch()
$ROW_MIN = 58   # 67 expected; the old half-bubble was 40
$ROW_MAX = 92

# Total darkness of a screen strip: sum of (255-R)+(255-G)+(255-B) over every pixel.
#
# Used only where two DIFFERENT strips are compared within one frame (is the left strip as
# bare as the reference strip). It is the wrong tool for "did this strip change": the
# animation is a rotation of brightness among three identically sized and coloured dots,
# so every frame has the same three brightnesses in it and the SUM is invariant. It was
# tried that way first and came back 118746 then 118746 for a strip that was animating
# perfectly -- a metric that cannot see the thing it was written to see.
function Get-BandSum([int]$x, [int]$y, [int]$w, [int]$h) {
    $bmp = Get-Crop $x $y $w $h
    $sum = 0
    for ($j = 0; $j -lt $h; $j++) {
        for ($i = 0; $i -lt $w; $i++) {
            $c = $bmp.GetPixel($i, $j)
            $sum += (255 - $c.R) + (255 - $c.G) + (255 - $c.B)
        }
    }
    $bmp.Dispose()
    return $sum
}

# How many pixels of two same-sized crops differ, which is the question "did this strip
# change" actually asked. A still frame gives exactly 0 -- not "a small number".
function Get-PixelDiff($a, $b) {
    $n = 0
    for ($x = 0; $x -lt $a.Width; $x++) {
        for ($y = 0; $y -lt $a.Height; $y++) {
            $ca = $a.GetPixel($x, $y)
            $cb = $b.GetPixel($x, $y)
            $d = [Math]::Abs($ca.R - $cb.R) + [Math]::Abs($ca.G - $cb.G) + [Math]::Abs($ca.B - $cb.B)
            if ($d -gt 12) { $n++ }
        }
    }
    return $n
}

# The last assistant row, straight off the snapshot ChatView writes (bubbles stopped
# being child windows in 0.9.0, so there is nothing left to enumerate).
function Get-AsstRow {
    $ui = Get-UiRows
    $rows = @(Get-UiRowsOf $ui 'assistant')
    if ($rows.Count -eq 0) { return $null }
    return Get-UiRowScreen $ui $rows[$rows.Count - 1]
}

# The line the dots are drawn on: the LAST body line inside the bubble -- one pitch up
# from the bottom edge, minus the bottom padding. Three strips on it:
#
#   Mid   the middle of the row, where DrawWait centres the three dots
#   Left  the left edge of the content area, where the first character of the answer lands
#   Ref   the far right of the content area -- bare bubble background, both before and
#         after the token (the answer is one short line and cannot reach it)
#
# Ref exists because "is this strip bare" has no absolute answer: how much darkness a bare
# strip has depends on the bubble's own colour, which is a theme away from being different.
# Reading the three together makes it a comparison against a strip of the SAME bubble on
# the SAME line that is known to be empty, which is what the question actually is.
function Get-WaitBands($r) {
    $y = $r.T + $r.H - $PADY - $PITCH
    return @{
        Y     = $y
        H     = $PITCH
        MidX  = $r.L + [int]($r.W / 2) - 48
        MidW  = 96
        LeftX = $r.L + 14
        RefX  = $r.L + $r.W - 14 - 170
        W     = 170
    }
}

function Get-WaitSums($b) {
    return @{
        Mid  = Get-BandSum $b.MidX $b.Y $b.MidW $b.H
        Left = Get-BandSum $b.LeftX $b.Y $b.W $b.H
        Ref  = Get-BandSum $b.RefX $b.Y $b.W $b.H
    }
}

$script:r0 = $null
$script:h0 = 0
$script:h1 = 0
$script:sameOrigin = $false
$script:rect0 = ''
$script:rect1 = ''
$script:midA = $null
$script:midB = $null
$script:midC = $null
$script:midD = $null
$script:waitA = $null
$script:waitB = $null
$script:afterA = $null
$script:afterB = $null
$script:waitedMs = -1

try {
    Remove-Item $trace -ErrorAction SilentlyContinue

    Invoke-BBProbe {
        $main = Start-BangGang 5

        Write-Output ('stub: ' + $baseUrl + '   silence ' + $hold1 + 'ms, one chunk, silence ' + $hold2 + 'ms')
        Write-Output ''

        # A conversation has to exist before anything can be sent.
        $pill = Get-SearchPill $main
        if ($null -eq $pill) { throw 'search pill not found' }
        $pillBtns = Get-PillButtons $main $pill
        if ($null -eq $pillBtns) { throw 'sidebar pill buttons not found' }
        $newBtn = $pillBtns[1]
        Invoke-MouseClick ([int](($newBtn.Left + $newBtn.Right) / 2)) ([int](($newBtn.Top + $newBtn.Bottom) / 2))
        Start-Sleep -Milliseconds 900

        # Get-InputEditBig returns a HANDLE (Zero when it finds nothing), not a hashtable --
        # so there is no .H on it, and reading one yields $null without an error.
        $edit = Get-InputEditBig $main
        if ($edit -eq [IntPtr]::Zero) { throw 'input text box not found' }
        [void](Invoke-TypeKeys $edit 'wait for it')

        $ib = Get-InputButtons $main $edit
        if ($null -eq $ib) { throw 'input card buttons not found' }
        # A stopwatch, not Environment.TickCount64: that property does not exist in
        # Windows PowerShell 5.1's .NET Framework, where reading it yields $null --
        # and "$null - $null" is 0, so every elapsed time would read as 0ms.
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-MouseClick ([int](($ib.Send.Left + $ib.Send.Right) / 2)) ([int](($ib.Send.Top + $ib.Send.Bottom) / 2))

        # ---- A. the bubble is whole, and only the dots are on it ----
        #
        # 800ms in: the request is on the wire (the stub has sent its headers) and no
        # content has arrived -- the stub will not speak for another five seconds.
        Start-Sleep -Milliseconds 800
        $script:r0 = Get-AsstRow
        if ($null -eq $script:r0) { throw 'no assistant row 800ms after sending' }
        $script:h0 = $script:r0.H
        $script:bands = Get-WaitBands $script:r0

        $script:waitA = Get-WaitSums $script:bands
        $script:midA = Get-Crop $script:bands.MidX $script:bands.Y $script:bands.MidW $script:bands.H
        Save-WindowShot $main (Get-ShotPath 'wait-anim-waiting.png')

        # ---- B. those dots move ----
        Start-Sleep -Milliseconds 300
        $script:waitB = Get-WaitSums $script:bands
        $script:midB = Get-Crop $script:bands.MidX $script:bands.Y $script:bands.MidW $script:bands.H

        # ---- D (first half). the first character shows up, eventually ----
        #
        # Polled rather than slept through: the stub's silence is 6s of wall clock, but
        # how long the app takes to get the request out is not part of the measurement,
        # and a fixed sleep would either be slack or be a race.
        #
        # The arrival test is "the left strip stopped looking like the reference strip":
        # while waiting, both are bare bubble background; once the answer starts, only
        # the left one has glyphs on it. Comparing against the bubble's own background
        # rather than against a fixed darkness is what makes this survive a theme change.
        $tries = 0
        $sums = Get-WaitSums $script:bands
        while ($tries -lt 30 -and (($sums.Left - $sums.Ref) -lt 20000)) {
            Start-Sleep -Milliseconds 300
            $sums = Get-WaitSums $script:bands
            $tries++
        }
        $script:waitedMs = [int]$clock.ElapsedMilliseconds
        Save-WindowShot $main (Get-ShotPath 'wait-anim-first-token.png')

        # ---- C. the animation stopped, and stopped at the first character ----
        #
        # The stream is still open (the stub holds for $hold2 more), so nothing else is
        # going to change these pixels. Two reads that come back identical are the
        # control for B: B said "two reads differ", and without C that would also be
        # satisfied by a repaint storm, a scrolling view, or a blinking caret.
        $r1 = Get-AsstRow
        if ($null -eq $r1) { throw 'the assistant row went away' }
        $script:h1 = $r1.H
        $b1 = Get-WaitBands $r1
        $script:afterA = Get-WaitSums $b1
        $script:midC = Get-Crop $b1.MidX $b1.Y $b1.MidW $b1.H
        # Where the row sits, not how wide it is. The width DOES change here and is meant
        # to: the empty bubble is full width (see MessageBubble.Rebuild -- the dots are a
        # narration, like the reasoning block), and the moment there is real text the box
        # hugs it. What must not move is the corner it is anchored at -- the assistant
        # bubble grows from its top-left, and a row that jumped there would drag everything
        # below it.
        $script:sameOrigin = ($r1.T -eq $script:r0.T) -and ($r1.L -eq $script:r0.L)
        $script:rect0 = ('' + $script:r0.L + ',' + $script:r0.T + ' ' + $script:r0.W + 'x' + $script:r0.H)
        $script:rect1 = ('' + $r1.L + ',' + $r1.T + ' ' + $r1.W + 'x' + $r1.H)
        Start-Sleep -Milliseconds 300
        $r2 = Get-AsstRow
        if ($null -eq $r2) { throw 'the assistant row went away between the two reads' }
        $b2 = Get-WaitBands $r2
        $script:afterB = Get-WaitSums $b2
        $script:midD = Get-Crop $b2.MidX $b2.Y $b2.MidW $b2.H
        $script:sameOrigin = $script:sameOrigin -and ($r2.T -eq $r1.T) -and ($r2.L -eq $r1.L)
    }
} finally {
    Stop-Job -Job $job -ErrorAction SilentlyContinue
    Remove-Job -Job $job -Force -ErrorAction SilentlyContinue

    if ($existed) { Set-Content -Path $settings -Value $backup -Encoding utf8 -NoNewline }
    else { Remove-Item $settings -ErrorAction SilentlyContinue }

    # put chats\ back exactly as it was: this probe sends a message, so the app writes a
    # conversation file that was not there before.
    if ($hadChats) {
        foreach ($f in Get-ChildItem $chatsDir -File) {
            if (-not $bakChats.ContainsKey($f.Name)) { Remove-Item $f.FullName -Force }
        }
        foreach ($n in $bakChats.Keys) { Set-Content -Path (Join-Path $chatsDir $n) -Value $bakChats[$n] -Encoding utf8 -NoNewline }
    }
}

Write-Output ''

# ---- A ----

Write-Output '--- A. the bubble is whole while the model is silent ---'
if ($null -eq $script:r0) {
    Check $false 'assistant row sampled while waiting' 'the probe did not get as far as measuring'
} else {
    Write-Output ('  row while waiting: ' + $script:r0.L + ',' + $script:r0.T + ' ' + $script:r0.W + 'x' + $script:r0.H)
    Write-Output ('  dots + one body line = ' + ($PADY + 18 + $PITCH + $PADY) + 'px; the half-bubble was ' + (11 + 18 + 11) + 'px')
    Check ($script:h0 -ge $ROW_MIN -and $script:h0 -le $ROW_MAX) 'the row is a full body line tall' `
        ($script:h0.ToString() + 'px, wanted ' + $ROW_MIN + '..' + $ROW_MAX)
    # The row is as wide as ever: the bubble must not have shrunk to hug the dots. A
    # narrow box "full of animation" would still be the half-bubble complaint.
    Check ($script:r0.W -gt 400) 'and still as wide as the chat area' ($script:r0.W.ToString() + 'px')
    # Nothing but the dots on that line: the left strip is where the answer will start,
    # and it reads the same as the reference strip, which is bare bubble background by
    # construction. This is what makes the arrival detector below mean something -- if
    # the left strip already had glyphs on it, "it changed" would be unmeasurable.
    $bare = [Math]::Abs($script:waitA.Left - $script:waitA.Ref)
    Write-Output ('  left strip ' + $script:waitA.Left + ' vs bare reference ' + $script:waitA.Ref + '  (delta ' + $bare + ')')
    Check ($bare -lt 4000) 'the line is bare apart from the dots' ('delta ' + $bare + ' against a strip known to be empty')
}

# ---- B ----

Write-Output ''
Write-Output '--- B. the dots are animating ---'
if ($null -eq $script:midA -or $null -eq $script:midB) {
    Check $false 'two frames of the dot strip' 'the probe did not get as far as measuring'
} else {
    $d = Get-PixelDiff $script:midA $script:midB
    Write-Output ('  dot strip darkness: ' + $script:waitA.Mid + ' then ' + $script:waitB.Mid + '  (invariant, see Get-BandSum)')
    Write-Output ('  dot strip pixels that changed: ' + $d + ' of ' + ($script:midA.Width * $script:midA.Height))
    # Every tick moves all three dots one step along the rotation, so ~3 dots' worth of
    # pixels (about 110, plus their antialiased rims) change between any two frames.
    # A still frame -- the bug this section exists for -- gives exactly 0.
    Check ($d -gt 40) 'the dot strip changed between two frames' ($d.ToString() + ' px, a still frame gives 0')
    Check ($script:waitA.Mid -gt 0) 'there is something painted on that line' ('darkness ' + $script:waitA.Mid)
}

# ---- D (second half) ----

Write-Output ''
Write-Output '--- D. it stops at the first character ---'
Write-Output ('  first character landed ' + $script:waitedMs + 'ms after send (stub held ' + $hold1 + 'ms)')
Check (($script:afterA.Left - $script:afterA.Ref) -ge 20000) 'the answer started on that line' `
    ('left strip ' + $script:afterA.Left + ' vs bare reference ' + $script:afterA.Ref)
Check ($script:waitedMs -ge ($hold1 - 600) -and $script:waitedMs -le ($hold1 + 3000)) `
    'and it landed while the stub was still holding' `
    ($script:waitedMs.ToString() + 'ms, expected around ' + $hold1 + 'ms')

# ---- C ----

Write-Output ''
Write-Output '--- C. and once it started, nothing on that line moves ---'
if ($null -eq $script:midC -or $null -eq $script:midD) {
    Check $false 'two frames after the token' 'the probe did not get as far as measuring'
} else {
    $d2 = Get-PixelDiff $script:midC $script:midD
    Write-Output ('  dot strip pixels that changed after the token: ' + $d2)
    Check ($d2 -eq 0) 'two frames are identical' `
        ($d2.ToString() + ' px changed -- B measured the animation, not render noise')
}
Check ($script:sameOrigin) 'the row kept its top-left corner' `
    ($script:rect0 + '  ->  ' + $script:rect1)
# The reserved line IS a body line, which is why the first character can land without
# the bubble jumping. If BodyLinePitch() and the wrap pass ever disagree by a pixel,
# this is where it shows up -- and on screen it shows up as the bubble twitching at
# exactly the moment the user is watching for the answer to start.
Check ([Math]::Abs($script:h1 - $script:h0) -le 2) 'no jump when the first character lands' `
    ($script:h0.ToString() + 'px -> ' + $script:h1.ToString() + 'px')

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'ALL CHECKS PASSED' } else { Write-Output ('' + $script:fail + ' CHECK(S) FAILED') }

Write-BBDone 'wait-anim'
