# The streaming reply path against the REAL endpoint, not a stub.
#
# tools/llm-reply.ps1 proves the app handles the SSE shape it was written for, using a
# stub it also wrote -- so it can only ever confirm the app's own assumptions. This one
# talks to the endpoint the user actually configured, and it exists because the two
# assumptions that turned out to be wrong were both about the real server:
#
#   1. every frame carries delta.reasoning_content with content NULL, and the answer
#      only starts after the thinking is done (the stub used to send plain content
#      frames, which is why a probe that passed still left the user staring at an
#      empty bubble for six seconds);
#   2. max_tokens is shared between the thinking and the answer, so a small budget
#      ends the run with finish_reason "length" and an EMPTY answer -- a perfectly
#      normal-looking stream that no amount of reading the deltas can distinguish
#      from "the model was just brief".
#
# Where the profile comes from: the release settings file, i.e. the configuration the
# user pointed at in the first place. It holds a real API key, so it is READ and
# copied -- never printed, and never written into this script, which is committed.
#
#   powershell -File tools\llm-live.ps1
#   powershell -File tools\llm-live.ps1 -Profile <path\to\settings.json>
#
# Two rounds, because the two things being checked need opposite budgets:
#   A  a normal budget (whatever the profile says): the thinking streams, the answer
#      follows it, and there is NO truncation notice at the end
#   B  a deliberately tiny budget: the server really does answer finish_reason
#      "length", and the app has to say so on screen
#
# Needs a Debug build and a reachable endpoint. Takes a couple of minutes.

param(
    [string]$Profile = ''
)

. "$PSScriptRoot\_ui.ps1"

$debugSettings = Join-Path (Split-Path $script:BBExe -Parent) 'settings.json'
$trace = Join-Path (Split-Path $script:BBExe -Parent) 'ui-trace.log'

if ($Profile -eq '') { $Profile = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\Release\settings.json' }
if (-not (Test-Path $Profile)) { throw "no profile to copy the endpoint from: $Profile" }

# The provider key in the app's settings is Chinese, written as code points: a literal
# here would be a non-ASCII byte in a file PowerShell reads as ANSI.
$provider = [string]([char]0x81EA) + [char]0x5B9A + [char]0x4E49    # "custom"

$src = Get-Content $Profile -Raw | ConvertFrom-Json
$key = ''
if ($src.ChatProfiles.PSObject.Properties.Name -contains $provider) {
    $key = [string]$src.ChatProfiles.$provider.key
}
if ($key.Length -eq 0) {
    # Fall back to whatever provider the file names, so a renamed profile still works.
    $first = @($src.ChatProfiles.PSObject.Properties)[0]
    $provider = $first.Name
    $key = [string]$first.Value.key
}
$url = [string]$src.ChatProfiles.$provider.url
$model = [string]$src.ChatProfiles.$provider.model
if ($url.Length -eq 0 -or $model.Length -eq 0 -or $key.Length -eq 0) {
    throw "the profile has no usable url / model / key (provider=$provider)"
}

# The budget of a normal round: the profile's own, which is what the user runs with.
$normalTokens = 2048
if ($null -ne $src.ChatMaxTokens) { $normalTokens = [int]$src.ChatMaxTokens }

# The budget for round B. Small enough that the thinking alone exhausts it -- checked
# against this very endpoint before it was written down: 64 tokens in, 64 out, all of
# them reasoning_tokens, finish_reason "length", content "".
$tinyTokens = 64

$question = 'Reply in Chinese. Explain in two sentences what a binary search is.'

Write-Output ('endpoint : ' + $url + '   model=' + $model + '   key=***' + $key.Substring([Math]::Max(0, $key.Length - 4)))

# ---- helpers ---------------------------------------------------------------

# Poll a condition instead of sleeping a fixed amount: a real model takes as long as it
# takes, and every fixed sleep in here would be either a flaky failure on a slow day or
# wasted minutes on a fast one.
function Wait-For([scriptblock]$Test, [int]$TimeoutMs, [string]$What) {
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        if (& $Test) { return $true }
        Start-Sleep -Milliseconds 250
    }
    Write-Output ('  (timed out after ' + [int]($TimeoutMs / 1000) + 's waiting for ' + $What + ')')
    return $false
}

function Get-TraceText {
    if (-not (Test-Path $script:trace)) { return '' }
    return (Get-Content $script:trace -Raw -ErrorAction SilentlyContinue)
}

# How many streams have completed so far -- the app's own "this reply is over" marker.
function Get-DoneCount {
    $t = Get-TraceText
    if ($t.Length -eq 0) { return 0 }
    return @([regex]::Matches($t, 'llm done chars=(\d+) reason=(\d+) finish=(\S*)')).Count
}

# The last "llm done" line, parsed. Null until the first stream has finished.
function Get-LastDone {
    $t = Get-TraceText
    $m = @([regex]::Matches($t, 'llm done chars=(\d+) reason=(\d+) finish=(\S*)'))
    if ($m.Count -eq 0) { return $null }
    $last = $m[$m.Count - 1]
    return @{ Chars = [int]$last.Groups[1].Value
              Reason = [int]$last.Groups[2].Value
              Finish = [string]$last.Groups[3].Value }
}

# The chat view: the panel starting under the title strip and running to the window's
# right edge (same rule as llm-reply.ps1). Returns the handle AND the rect.
function Get-ChatPanel($main) {
    $mr = Get-WinRect $main
    $best = $null
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Top -ne ($mr.Top + 38 + 48)) { continue }
        if ($r.Right -ne $mr.Right) { continue }
        if ($r.Left -lt $mr.Left) { continue }
        if ($null -eq $best -or $r.Left -lt $best.R.Left) { $best = @{ H = $h; R = $r } }
    }
    return $best
}

# The bottom-most bubble of THIS chat view, by parent rather than by coordinate: a
# bubble taller than what is left of the view starts above the panel's top edge, and a
# geometric window would drop exactly the case worth looking at.
function Get-AsstBubble($main, $chat) {
    $best = $null
    foreach ($h in Get-WinKids $main) {
        if ((Get-ShortClass $h) -like '*SCROLLBAR*') { continue }
        if ([BB]::GetParent($h) -ne $chat.H) { continue }
        $r = Get-WinRect $h
        if ([Math]::Abs($r.Left - ($chat.R.Left + 26)) -gt 2) { continue }
        if ($r.Bottom -le $chat.R.Top) { continue }
        if ($null -eq $best -or $r.Top -gt $best.Top) { $best = $r }
    }
    return $best
}

function Get-BubbleHeight($main, $chat) {
    $b = Get-AsstBubble $main $chat
    if ($null -eq $b) { return 0 }
    return ($b.Bottom - $b.Top)
}

function Get-InputEdit($main) {
    foreach ($h in Get-WinKids $main) {
        if ((Get-ShortClass $h) -ne 'Edit') { continue }
        $r = Get-WinRect $h
        if (($r.Bottom - $r.Top) -gt 40) { return @{ H = $h; R = $r } }
    }
    return $null
}

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

# Start a fresh conversation and send one message. Returns the chat panel.
function Send-One($main, [string]$text) {
    $pill = Get-SearchPill $main
    if ($null -eq $pill) { throw 'search pill not found' }
    $pillBtns = Get-PillButtons $main $pill
    if ($null -eq $pillBtns) { throw 'sidebar pill buttons not found' }
    $newBtn = $pillBtns[1]
    Invoke-MouseClick ([int](($newBtn.Left + $newBtn.Right) / 2)) ([int](($newBtn.Top + $newBtn.Bottom) / 2))
    Start-Sleep -Milliseconds 900

    $e = Get-InputEdit $main
    if ($null -eq $e) { throw 'input text box not found' }
    [void](Invoke-TypeKeys $e.H $text)

    $ib = Get-InputButtons $main $e.H
    if ($null -eq $ib) { throw 'input card buttons not found' }
    Invoke-MouseClick ([int](($ib.Send.Left + $ib.Send.Right) / 2)) ([int](($ib.Send.Top + $ib.Send.Bottom) / 2))

    Start-Sleep -Milliseconds 400
    $chat = Get-ChatPanel $main
    if ($null -eq $chat) { throw 'chat panel not found' }
    return $chat
}

$script:fail = 0
function Check([bool]$ok, [string]$label, [string]$detail) {
    $tag = 'FAIL'
    if ($ok) { $tag = 'ok  ' }
    if (-not $ok) { $script:fail++ }
    Write-Output ('  [' + $tag + '] ' + $label + '  ' + $detail)
}

function Write-Settings([int]$maxTokens) {
    $o = @{}
    if (Test-Path $script:debugSettings) { $o = (Get-Content $script:debugSettings -Raw) | ConvertFrom-Json }
    $o | Add-Member -NotePropertyName ChatProvider -NotePropertyValue $provider -Force
    $o | Add-Member -NotePropertyName ChatProfiles -NotePropertyValue @{
        $provider = @{ url = $url; key = $key; model = $model }
    } -Force
    $o | Add-Member -NotePropertyName ChatVision -NotePropertyValue $true -Force
    $o | Add-Member -NotePropertyName ChatTemperature -NotePropertyValue 0.7 -Force
    $o | Add-Member -NotePropertyName ChatMaxTokens -NotePropertyValue $maxTokens -Force
    $o | Add-Member -NotePropertyName ChatSystemPrompt -NotePropertyValue '' -Force
    $o | Add-Member -NotePropertyName ChatReinforce -NotePropertyValue '' -Force
    # Light palette: the amber/ink pixel tests below assume a pale chat background.
    $o | Add-Member -NotePropertyName ThemeMode -NotePropertyValue 'light' -Force
    $o | ConvertTo-Json -Depth 8 | Set-Content -Path $script:debugSettings -Encoding utf8
}

$script:debugSettings = $debugSettings
$script:trace = $trace

$existed = Test-Path $debugSettings
$backup = $null
if ($existed) { $backup = Get-Content $debugSettings -Raw }

# What each round measured, for the report after the app is gone.
$r1 = @{}
$r2 = @{}

try {
    # ---------------- A. a normal budget ----------------

    Write-Settings $normalTokens
    Remove-Item $trace -ErrorAction SilentlyContinue

    Invoke-BBProbe {
        $main = Start-BangGang 5
        $chat = Send-One $main $question
        $script:r1.Chat = $chat

        # Sample the bubble for as long as the stream is running, and stop the moment
        # it finishes. Taking the series rather than two fixed samples is what makes
        # this deterministic: a real model answers in its own time, and "sample at
        # 2.5s" is a race the probe would sometimes lose -- the first version of this
        # did lose it, and reported a failure that was entirely its own arithmetic.
        # Everything recorded here is by construction mid-stream (the loop breaks on
        # the first finished stream), so the assertion afterwards is simply: was the
        # bubble ever taller than the empty placeholder while the model was still
        # talking? Before the fix it sat at exactly 28px from send to answer.
        $series = @()
        $shot = $false
        $deadline = (Get-Date).AddSeconds(240)
        while ((Get-Date) -lt $deadline) {
            if ((Get-DoneCount) -ge 1) { break }
            $h = Get-BubbleHeight $main $chat
            $series += , $h
            if ($h -gt 28 -and -not $shot) {
                # A picture of the thinking phase itself: the bubble is short enough
                # here that its top -- the thinking header, under the "provider name"
                # -- is still on screen.
                Save-WindowShot $main (Get-ShotPath 'llm-live-thinking.png')
                $shot = $true
            }
            Start-Sleep -Milliseconds 200
        }
        $script:r1.Series = $series
        $script:r1.Ended = ($series.Count -gt 0) -and ((Get-DoneCount) -ge 1)
        if ($script:r1.Ended) {
            $script:r1.Done = Get-LastDone
            $script:r1.HMid = 0
            if ($series.Count -gt 0) { $script:r1.HMid = ($series | Measure-Object -Maximum).Maximum }
            $script:r1.HFinal = Get-BubbleHeight $main $chat
            Save-WindowShot $main (Get-ShotPath 'llm-live-answer.png')
            $b = Get-AsstBubble $main $chat
            $bmp = Get-Crop $b.Left $b.Top ($b.Right - $b.Left) ($b.Bottom - $b.Top)
            $amber = 0
            for ($x = 0; $x -lt $bmp.Width; $x++) {
                for ($y = 0; $y -lt $bmp.Height; $y++) {
                    $c = $bmp.GetPixel($x, $y)
                    if (($c.R - $c.B) -gt 60 -and $c.R -gt 120 -and $c.B -lt 170) { $amber++ }
                }
            }
            $bmp.Dispose()
            $script:r1.Amber = $amber
        }
    }

    # ---------------- B. a budget the thinking alone exhausts ----------------

    Write-Settings $tinyTokens
    Remove-Item $trace -ErrorAction SilentlyContinue

    Invoke-BBProbe {
        $main = Start-BangGang 5
        $chat = Send-One $main $question
        $script:r2.Chat = $chat

        $ended = Wait-For { (Get-DoneCount) -ge 1 } 240000 'the truncated stream to finish'
        $script:r2.Ended = $ended
        if ($ended) {
            $script:r2.Done = Get-LastDone
            $script:r2.HFinal = Get-BubbleHeight $main $chat
            Save-WindowShot $main (Get-ShotPath 'llm-live-truncated.png')
            $b = Get-AsstBubble $main $chat
            $bmp = Get-Crop $b.Left $b.Top ($b.Right - $b.Left) ($b.Bottom - $b.Top)
            $amber = 0
            for ($x = 0; $x -lt $bmp.Width; $x++) {
                for ($y = 0; $y -lt $bmp.Height; $y++) {
                    $c = $bmp.GetPixel($x, $y)
                    if (($c.R - $c.B) -gt 60 -and $c.R -gt 120 -and $c.B -lt 170) { $amber++ }
                }
            }
            $bmp.Dispose()
            $script:r2.Amber = $amber
        }
    }
} finally {
    if ($existed) { Set-Content -Path $debugSettings -Value $backup -Encoding utf8 -NoNewline }
    else { Remove-Item $debugSettings -ErrorAction SilentlyContinue }
}

# ---------------- report ----------------

Write-Output ''
Write-Output ('--- A. a normal budget (' + $normalTokens + ' tokens) ---')
if (-not $r1.Ended) {
    Check $false 'the stream finished' 'no "llm done" line appeared within 4 minutes'
} else {
    $hMid = $r1.HMid
    $n = @($r1.Series).Count
    $distinct = @($r1.Series | Sort-Object -Unique).Count
    Write-Output ('  sampled ' + $n + ' frame(s) while the stream ran; tallest ' + $hMid + 'px, ' + $distinct + ' distinct heights, final ' + $r1.HFinal + 'px')
    # The user's first complaint, in one line. Every sample in the series was taken
    # while the stream was still running, so "taller than the 28px placeholder" means
    # there was something on screen BEFORE the reply finished. That is what streaming
    # means here, and it is precisely what did not happen before: with the reasoning
    # frames dropped, the bubble sat at 28px for the whole thinking phase.
    Check ($hMid -gt 28) 'something was on screen while the model was still talking' ($hMid.ToString() + 'px > 28px empty')
    # And it arrived in pieces. Two heights would be "empty, then everything"; a whole
    # reply landing in one frame is not streaming either, however tall it is.
    Check ($distinct -gt 2) 'the bubble took intermediate heights on the way' ($distinct.ToString() + ' distinct heights')
    $d = $r1.Done
    Write-Output ('  trace: chars=' + $d.Chars + ' reason=' + $d.Reason + ' finish=' + $d.Finish)
    Check ($d.Reason -gt 0) 'the thinking was received' ($d.Reason.ToString() + ' chars')
    Check ($d.Chars -gt 0) 'the answer was received' ($d.Chars.ToString() + ' chars')
    Check ($d.Finish -ne 'length') 'the reply was not truncated' ('finish=' + $d.Finish)
    Check ($r1.Amber -eq 0) 'no truncation notice on screen' ($r1.Amber.ToString() + ' amber px')
}

Write-Output ''
Write-Output ('--- B. a budget the thinking alone exhausts (' + $tinyTokens + ' tokens) ---')
if ($r2.Ended) {
    $d = $r2.Done
    Write-Output ('  trace: chars=' + $d.Chars + ' reason=' + $d.Reason + ' finish=' + $d.Finish + '   bubble=' + $r2.HFinal + 'px')
    Check ($d.Finish -eq 'length') 'the server reported truncation' ('finish=' + $d.Finish)
    Check ($d.Chars -eq 0) 'the budget went entirely to the thinking' ($d.Chars.ToString() + ' chars of answer')
    # The one the user could see. Amber ink (R-B ~ 164) and not the box's own 14% tint
    # (R-B ~ 23): this counts the words of the notice, not just a rectangle drawn.
    Check ($r2.Amber -gt 60) 'the truncation notice is on screen' ($r2.Amber.ToString() + ' amber px')
} else {
    Check $false 'the truncated stream finished' 'no "llm done" line appeared within 4 minutes'
}

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'ALL CHECKS PASSED' } else { Write-Output ('' + $script:fail + ' CHECK(S) FAILED') }

Write-BBDone 'llm-live'
