# End-to-end check of the streaming reply path: a real message goes out to an
# OpenAI-compatible endpoint, the deltas come back as SSE, and the bubble grows
# WHILE the answer is still arriving.
#
# There is no API key on this machine, so the "endpoint" is a stub SSE server
# written on a bare TcpListener. TcpListener rather than HttpListener on purpose:
# HTTP.SYS wants a urlacl reservation for anything but the admin-installed
# prefixes, and this probe must run as a normal user.
#
# What it pins down, in the order the data flows:
#   A. the request the app actually sends    -- model, temperature, max_tokens,
#      the system message, and the reinforcement text riding on the last user turn
#   B. that the reply arrives progressively  -- the bubble is taller mid-stream
#      than the empty placeholder, and taller again once the stream ends
#   C. that it is really painted             -- the strip of screen that was bare
#      chat background mid-stream carries ink by the end (same rect, two snapshots)
#   D. that nothing was lost on the way      -- "llm done chars=N" equals the total
#      the stub sent, to the character
#
# ASCII ONLY (PowerShell 5.1 reads a BOM-less file as ANSI). The settings file
# needs the provider key, which is Chinese, so it is built from code points.
#
# The stub is one connection, one response: it answers the first request and exits.

. "$PSScriptRoot\_ui.ps1"

$settings = Join-Path (Split-Path $script:BBExe -Parent) 'settings.json'
$trace = Join-Path (Split-Path $script:BBExe -Parent) 'ui-trace.log'

# The provider key the app defaults to. Written as code points: a literal here
# would be a non-ASCII byte in a file PowerShell reads as ANSI.
$provider = [string]([char]0x81EA) + [char]0x5B9A + [char]0x4E49

$sysPrompt = 'SYSPROMPT-XYZ'
$reinforce = 'REINFORCE-QQQ'
$modelName = 'bb-stub-model'
$temp = 0.25
$maxTokens = 777

# Eight chunks, 150ms apart: long enough to be several wrapped lines, and slow
# enough that "mid-stream" is a state the probe can actually observe. 150ms sits
# well above the app's 40ms flush timer, so each chunk really is a separate frame.
$chunks = @()
for ($i = 1; $i -le 8; $i++) { $chunks += ('STUB-REPLY-' + $i + ': ' + ('x' * 70) + "`n") }
$replyLen = 0
foreach ($c in $chunks) { $replyLen += $c.Length }

# A free port, picked by binding to 0 and letting go again. Hard-coding one makes
# the probe fail on whatever else happens to be listening that day.
$probe = New-Object System.Net.Sockets.TcpListener -ArgumentList @([System.Net.IPAddress]::Loopback, 0)
$probe.Start()
$port = $probe.LocalEndpoint.Port
$probe.Stop()

$baseUrl = 'http://127.0.0.1:' + $port + '/v1'

$job = Start-Job -ArgumentList $port, $chunks -ScriptBlock {
    param($port, $chunks)

    function Send-Line($stream, [string]$text) {
        $b = [System.Text.Encoding]::UTF8.GetBytes($text)
        $stream.Write($b, 0, $b.Length)
        $stream.Flush()
    }

    $listener = New-Object System.Net.Sockets.TcpListener -ArgumentList @([System.Net.IPAddress]::Loopback, $port)
    $listener.Start()
    $body = ''
    try {
        $client = $listener.AcceptTcpClient()
        $stream = $client.GetStream()

        # Read until the headers are in and Content-Length worth of body has followed.
        # Not "until the stream goes quiet": a TcpClient read blocks until data shows
        # up, so waiting for a quiet moment would just hang.
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
        $all = [System.Text.Encoding]::UTF8.GetString($ms.ToArray())
        $hdr = $all.IndexOf("`r`n`r`n")
        if ($hdr -ge 0) { $body = $all.Substring($hdr + 4) }

        Send-Line $stream ("HTTP/1.1 200 OK`r`nContent-Type: text/event-stream`r`nCache-Control: no-cache`r`nConnection: close`r`n`r`n")
        foreach ($c in $chunks) {
            $frame = @{ id = 'stub'; choices = @(@{ index = 0; delta = @{ content = $c } }) } |
                     ConvertTo-Json -Compress -Depth 8
            Send-Line $stream ('data: ' + $frame + "`r`n`r`n")
            Start-Sleep -Milliseconds 150
        }
        Send-Line $stream "data: [DONE]`r`n`r`n"
        $client.Close()
    } finally {
        $listener.Stop()
    }
    $body
}

# ---- point the app at the stub -------------------------------------------------

$existed = Test-Path $settings
$backup = $null
if ($existed) { $backup = Get-Content $settings -Raw }

$o = @{}
if ($existed) { $o = $backup | ConvertFrom-Json }
$o | Add-Member -NotePropertyName ChatProvider -NotePropertyValue $provider -Force
$o | Add-Member -NotePropertyName ChatProfiles -NotePropertyValue @{
    $provider = @{ url = $baseUrl; key = 'sk-stub'; model = $modelName }
} -Force
$o | Add-Member -NotePropertyName ChatVision -NotePropertyValue $true -Force
$o | Add-Member -NotePropertyName ChatTemperature -NotePropertyValue $temp -Force
$o | Add-Member -NotePropertyName ChatMaxTokens -NotePropertyValue $maxTokens -Force
$o | Add-Member -NotePropertyName ChatSystemPrompt -NotePropertyValue $sysPrompt -Force
$o | Add-Member -NotePropertyName ChatReinforce -NotePropertyValue $reinforce -Force
# Pin the light palette: the ink test below counts DARK pixels, and on a dark chat
# background the bare-background snapshot would come back full of "ink".
$o | Add-Member -NotePropertyName ThemeMode -NotePropertyValue 'light' -Force
$o | ConvertTo-Json -Depth 8 | Set-Content -Path $settings -Encoding utf8

# The input card's text box: the only EDIT in the tree tall enough to be the
# multi-line one (same rule as draft-strip.ps1).
function Get-InputEdit($main) {
    foreach ($h in Get-WinKids $main) {
        if ((Get-ShortClass $h) -ne 'Edit') { continue }
        $r = Get-WinRect $h
        if (($r.Bottom - $r.Top) -gt 40) { return @{ H = $h; R = $r } }
    }
    return $null
}

# The card's two 28x28 buttons below the text box, left to right: attach, then send.
function Get-InputButtons($main, $edit) {
    $er = Get-WinRect $edit
    $btns = @()
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -ne 28 -or ($r.Bottom - $r.Top) -ne 28) { continue }
        if ($r.Top -le $er.Top) { continue }
        $btns += ,$r
    }
    if ($btns.Count -ne 2) { return $null }
    $s = @($btns | Sort-Object Left)
    return @{ Attach = $s[0]; Send = $s[1] }
}

# The chat view: the panel that starts right under the title strip and runs to the
# window's right edge. Same rule as sidebar-check.ps1 -- it cannot be found by name,
# the chat view is a plain Panel and the containers above it all hug the window now.
function Get-ChatPanel($main) {    $mr = Get-WinRect $main
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

# Every control laid out inside the chat view -- i.e. the message bubbles. Printed
# when a sample comes back empty: "no bubble" has several very different causes
# (scrolled out of the view, never created, wrong margin) and the survey tells them
# apart without another round trip.
function Show-ChatKids($main, $chat) {
    $n = 0
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Top -lt $chat.Top -or $r.Left -lt $chat.Left) { continue }
        Write-Output ('    ' + (Get-ShortClass $h).PadRight(14) + $r.Left + ',' + $r.Top + ' ' +
                      ($r.Right - $r.Left) + 'x' + ($r.Bottom - $r.Top) +
                      $(if ([BB]::IsWindowVisible($h)) { '  vis' } else { '  hid' }))
        $n++
    }
    if ($n -eq 0) { Write-Output '    (nothing inside the chat view)' }
}

# The last assistant bubble: the bottom-most control laid out inside the chat view
# whose left edge is the 26px margin ChatView.LayoutRows gives every non-user row.
# Skip the scrollbars -- AutoScroll puts real child HWNDs in there too.
function Get-AsstBubble($main, $chat) {
    $best = $null
    foreach ($h in Get-WinKids $main) {
        if ((Get-ShortClass $h) -like '*SCROLLBAR*') { continue }
        $r = Get-WinRect $h
        if ([Math]::Abs($r.Left - ($chat.Left + 26)) -gt 2) { continue }
        if ($r.Top -lt $chat.Top) { continue }
        if ($r.Bottom -gt $chat.Bottom) { continue }
        if ($null -eq $best -or $r.Top -gt $best.Top) { $best = $r }
    }
    return $best
}

$script:fail = 0
function Check([bool]$ok, [string]$label, [string]$detail) {
    $tag = 'FAIL'
    if ($ok) { $tag = 'ok  ' }
    if (-not $ok) { $script:fail++ }
    Write-Output ('  [' + $tag + '] ' + $label + '  ' + $detail)
}

$b1 = $null
$b2 = $null
$strip = $null
$midInk = $null
$endInk = $null

try {
    Remove-Item $trace -ErrorAction SilentlyContinue

    Invoke-BBProbe {
        $main = Start-BangGang 5
        $mr = Get-WinRect $main

        Write-Output ('stub: ' + $baseUrl + '   reply=' + $replyLen + ' chars in ' + $chunks.Count + ' chunks')
        Write-Output ''

        # A conversation has to exist before anything can be sent.
        $pill = Get-SearchPill $main
        if ($null -eq $pill) { throw 'search pill not found' }
        $pillBtns = Get-PillButtons $main $pill
        if ($null -eq $pillBtns) { throw 'sidebar pill buttons not found' }
        $newBtn = $pillBtns[1]
        Invoke-MouseClick ([int](($newBtn.Left + $newBtn.Right) / 2)) ([int](($newBtn.Top + $newBtn.Bottom) / 2))
        Start-Sleep -Milliseconds 900

        $e0 = Get-InputEdit $main
        if ($null -eq $e0) { throw 'input text box not found' }
        $edit = $e0.H
        $typed = Invoke-TypeKeys $edit 'hello stub'
        Write-Output ("typed back: '" + $typed + "'")

        $ib = Get-InputButtons $main $edit
        if ($null -eq $ib) { throw 'input card buttons not found' }
        Invoke-MouseClick ([int](($ib.Send.Left + $ib.Send.Right) / 2)) ([int](($ib.Send.Top + $ib.Send.Bottom) / 2))

        # ---- B. is it growing while the answer is still coming? ----
        Start-Sleep -Milliseconds 600
        $chat = Get-ChatPanel $main
        if ($null -eq $chat) { throw 'chat panel not found' }
        $script:b1 = Get-AsstBubble $main $chat
        if ($null -eq $script:b1) { throw 'no assistant bubble after sending' }

        # The strip to watch: just under where the bubble ends NOW. It is bare chat
        # background at this instant, and the rest of the reply is going to land in it.
        $sx = $script:b1.Left + 14
        $sy = $script:b1.Bottom + 6
        $sw = 220
        $sh = 20
        if ($sy + $sh -gt $chat.Bottom) { throw 'the watched strip would fall outside the chat view' }
        $script:strip = @{ X = $sx; Y = $sy; W = $sw; H = $sh }
        $bmp = Get-Crop $sx $sy $sw $sh
        $script:midInk = Get-InkBoxNum $bmp
        $bmp.Dispose()

        Start-Sleep -Milliseconds 3000
        $script:b2 = Get-AsstBubble $main $chat
        Save-WindowShot $main (Get-ShotPath 'llm-reply.png')
        if ($null -eq $script:b2) {
            Write-Output '  chat view contents at the end:'
            Show-ChatKids $main $chat
            throw 'the assistant bubble disappeared'
        }
        $bmp = Get-Crop $sx $sy $sw $sh
        $script:endInk = Get-InkBoxNum $bmp
        $bmp.Dispose()
    }
} finally {
    Stop-Job -Job $job -ErrorAction SilentlyContinue
    $sent = Receive-Job -Job $job -ErrorAction SilentlyContinue
    Remove-Job -Job $job -Force -ErrorAction SilentlyContinue

    if ($existed) { Set-Content -Path $settings -Value $backup -Encoding utf8 -NoNewline }
    else { Remove-Item $settings -ErrorAction SilentlyContinue }
    $script:reqBody = ''
    if ($sent -is [array]) { $script:reqBody = [string]$sent[0] }
    elseif ($sent) { $script:reqBody = [string]$sent }
}

Write-Output ''

# ---- A. what the app actually put on the wire ----

Write-Output '--- A. request ---'
if ($script:reqBody.Length -eq 0) {
    Check $false 'stub received a request' 'nothing arrived -- the app never called the endpoint'
} else {
    Check $true 'stub received a request' ($script:reqBody.Length.ToString() + ' bytes of JSON')
    try {
        $j = $script:reqBody | ConvertFrom-Json
        Check ($j.model -eq $modelName) 'model' ('' + $j.model)
        Check ($j.stream -eq $true) 'stream' ('' + $j.stream)
        Check ([Math]::Abs([double]$j.temperature - $temp) -lt 0.001) 'temperature' ('' + $j.temperature)
        Check ([int]$j.max_tokens -eq $maxTokens) 'max_tokens' ('' + $j.max_tokens)

        $msgs = @($j.messages)
        Check ($msgs.Count -eq 2) 'message count' ('' + $msgs.Count)
        if ($msgs.Count -ge 2) {
            Check ($msgs[0].role -eq 'system') 'first message is system' ('' + $msgs[0].role)
            Check ($msgs[0].content -eq $sysPrompt) 'system prompt' ('' + $msgs[0].content)
            Check ($msgs[1].role -eq 'user') 'second message is user' ('' + $msgs[1].role)
            $uc = [string]$msgs[1].content
            Check ($uc.Contains('hello stub')) 'user text present' $uc.Substring(0, [Math]::Min(24, $uc.Length))
            Check ($uc.Contains($reinforce)) 'reinforce rides the last user turn' ('tail=' + $uc.Substring([Math]::Max(0, $uc.Length - 16)))
        }
    } catch {
        Check $false 'request body parses as JSON' $_.Exception.Message
    }
    Write-Output '  ... raw body:'
    Write-Output ('  ' + $script:reqBody)
}

# ---- B/C. progressive display ----

Write-Output ''
Write-Output '--- B. the bubble grows while the answer is still arriving ---'
if ($null -eq $b1 -or $null -eq $b2) {
    Check $false 'bubble sampled twice' 'the probe did not get as far as measuring'
} else {
    $h1 = $b1.Bottom - $b1.Top
    $h2 = $b2.Bottom - $b2.Top
    Write-Output ('  bubble height: mid-stream ' + $h1 + 'px   final ' + $h2 + 'px')
    # 28 is the empty-bubble height (MessageBubble.Rebuild): taller than that at the
    # 600ms mark means text was on screen before the stream had finished.
    Check ($h1 -gt 28) 'text on screen mid-stream' ($h1.ToString() + 'px > 28px empty')
    Check ($h2 -gt $h1 + 20) 'bubble grew after that' ($h1.ToString() + 'px -> ' + $h2.ToString() + 'px')
    Check (($b2.Right - $b2.Left) -gt 400) 'bubble is wide' (($b2.Right - $b2.Left).ToString() + 'px')
}

Write-Output ''
Write-Output '--- C. the strip that was empty mid-stream now carries ink ---'
if ($null -eq $strip -or $null -eq $b2) {
    Check $false 'strip measured' 'the probe did not get as far as measuring'
} else {
    $mn = 0
    if ($null -ne $midInk) { $mn = $midInk.N }
    $en = 0
    if ($null -ne $endInk) { $en = $endInk.N }
    Write-Output ('  watched rect: ' + $strip.X + ',' + $strip.Y + ' ' + $strip.W + 'x' + $strip.H)
    Write-Output ('  ink mid-stream: ' + $mn + ' px      ink at the end: ' + $en + ' px')
    Check ($mn -eq 0) 'bare chat background mid-stream' ($mn.ToString() + ' px of ink')
    Check ($en -gt 40) 'reply text painted in it' ($en.ToString() + ' px of ink')
}

# ---- D. did every character make it ----

Write-Output ''
Write-Output '--- D. nothing lost between the socket and the message ---'
$log = ''
if (Test-Path $trace) { $log = Get-Content $trace -Raw }
$reqLines = @([regex]::Matches($log, 'llm request provider=(\S+) model=(\S+) msgs=(\d+)'))
$doneLines = @([regex]::Matches($log, 'llm done chars=(\d+)'))
Check ($reqLines.Count -ge 1) 'trace logged the request' ($reqLines.Count.ToString() + ' line(s)')
Check ($doneLines.Count -ge 1) 'trace logged a completed stream' ($doneLines.Count.ToString() + ' line(s)')
if ($doneLines.Count -ge 1) {
    $got = [int]$doneLines[0].Groups[1].Value
    Check ($got -eq $replyLen) 'character count matches what was sent' ($got.ToString() + ' vs ' + $replyLen)
}
if ($log -match 'llm error: (.+)') {
    Check $false 'no error on the wire' $Matches[1]
} else {
    Check $true 'no error on the wire' ''
}

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'ALL CHECKS PASSED' } else { Write-Output ('' + $script:fail + ' CHECK(S) FAILED') }

Write-BBDone 'llm-reply'
