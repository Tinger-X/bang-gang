# Does a conversation survive a restart?
#
# Conversations used to live only in memory: closing the app threw the lot away. The
# requirement is now that they are written to disk and picked up again next launch, so
# this probe kills the app and starts it again -- in-process checks (call Save, call
# Load, compare) would pass on a build where nothing ever gets written, which is
# exactly the bug that was reported.
#
# What it pins down:
#   A. a sent conversation is on disk, with both messages, while the app is still running
#   B. after a restart it is BACK ON SCREEN -- the title strip shows it and the chat view
#      holds the same bubbles at the same sizes it had before the kill (a restore that
#      drops message text would come back with shorter bubbles)
#   C. empty conversations are NOT written: click + twice, and the next launch must show
#      one conversation again, not three. This is the rule that keeps the list from
#      filling up with "new chat" rows the user can never get rid of.
#   D. the fallback lands somewhere sane: after those two empty ones, the relaunch comes
#      back to the last conversation that actually HAS something in it, not to the
#      welcome page and not to an empty one
#
# The list itself is self-drawn (no HWND per row), so "how many conversations are
# showing" is read as the ink bounding box of the list area: one row is one band, and
# three rows are visibly taller. Anything that reads a row count out of a control name
# would be reading a control that does not exist.
#
# The settings file and conversations.json are both backed up and restored -- the
# second one is the user's own chat history if they ever ran the debug build.

. "$PSScriptRoot\_ui.ps1"

$dir = Split-Path $script:BBExe -Parent
$settings = Join-Path $dir 'settings.json'
$convFile = Join-Path $dir 'conversations.json'

$script:fail = 0
function Check([bool]$ok, [string]$label, [string]$detail) {
    $tag = 'FAIL'
    if ($ok) { $tag = 'ok  ' }
    if (-not $ok) { $script:fail++ }
    Write-Output ('  [' + $tag + '] ' + $label + '  ' + $detail)
}

# What is actually in the store, read from the file rather than from the app.
function Read-Store {
    if (-not (Test-Path $convFile)) { return $null }
    try { return (Get-Content $convFile -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null }
}

# The chat view, as both handle and rect: the rect says which control it is, the handle
# is what the rows have to be enumerated from (see Get-Rows).
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

function Get-InputEdit($main) {
    foreach ($h in Get-WinKids $main) {
        if ((Get-ShortClass $h) -ne 'Edit') { continue }
        $r = Get-WinRect $h
        if (($r.Bottom - $r.Top) -gt 40) { return @{ H = $h; R = $r } }
    }
    return $null
}

# The bubbles: the chat view's OWN children, by rect. Reduced to "how big is it" and
# ordered top-down, which is what the before/after comparison needs -- a dropped or
# truncated message comes back as a shorter bubble.
#
# Enumerated from the chat view rather than from the main window: the input card's text
# box and its two 28x28 buttons also sit below the title strip and pass any filter that
# only looks at "is it in the body area", which is how a first cut of this counted the
# send button as a message. They are siblings of the chat view, not children of it.
function Get-Rows($chat) {
    $rows = @()
    foreach ($h in Get-WinKids $chat.H) {
        if ((Get-ShortClass $h) -like '*SCROLLBAR*') { continue }
        $r = Get-WinRect $h
        $rows += ,@{ Top = $r.Top; S = ('' + ($r.Right - $r.Left) + 'x' + ($r.Bottom - $r.Top)) }
    }
    $out = @()
    foreach ($x in @($rows | Sort-Object Top)) { $out += ,$x.S }
    return @($out)
}

# The conversation title is a real Label, so its text can be read straight back. That
# is the cheapest solid proof that the UI came up on the restored conversation rather
# than on the welcome page -- and it needs no OCR of the self-drawn list.
function Find-Text($main, [string]$want) {
    foreach ($h in Get-WinKids $main) {
        if ((Get-WinText $h) -eq $want) { return $h }
    }
    return $null
}

# Ink bounding box of the conversation list area (self-drawn rows), as a height.
#
# Get-InkBoxNumAt, not Get-InkBoxAt: the latter is the FORMATTING helper and hands back
# "12..44 x 3..20 (n=311, w=33 h=18)" as a string. Reading .B / .Y off a string gives
# $null without a word from PowerShell, and $null - $null + 1 is 1 -- so the box came
# back as a flat 1px for every sample and all three list checks passed on nothing.
function List-InkHeight($main) {
    $mr = Get-WinRect $main
    $r = Get-InkBoxNumAt ($mr.Left + 10) ($mr.Top + 170) 200 260
    if ($null -eq $r) { return 0 }
    return ($r.B - $r.Y + 1)
}

$title = 'persist-check-msg'

# ---- backups ---------------------------------------------------------------------

$hadSettings = Test-Path $settings
$bakSettings = $null
if ($hadSettings) { $bakSettings = Get-Content $settings -Raw }

$hadConv = Test-Path $convFile
$bakConv = $null
if ($hadConv) { $bakConv = Get-Content $convFile -Raw }

$script:rows1 = @()
$script:rows2 = @()
$script:ink1 = 0
$script:ink3 = 0
$script:ink4 = 0
$script:store1 = $null
$script:store2 = $null
$script:store4 = $null

try {
    Set-Content -Path $settings -Value '{"ThemeMode":"light"}' -Encoding utf8
    if (Test-Path $convFile) { Remove-Item $convFile -Force }

    Invoke-BBProbe {
        # ---- round 1: send a message ----
        $main = Start-BangGang 5
        $pill = Get-SearchPill $main
        $btns = Get-PillButtons $main $pill
        Invoke-MouseClick ([int](($btns[1].Left + $btns[1].Right) / 2)) ([int](($btns[1].Top + $btns[1].Bottom) / 2))
        Start-Sleep -Milliseconds 900

        $box = Get-InputEdit $main
        if ($null -eq $box) { throw 'input text box not found' }
        [void](Invoke-TypeKeys $box.H $title)
        $ib = Get-InputButtons $main $box.H
        Invoke-MouseClick ([int](($ib.Send.Left + $ib.Send.Right) / 2)) ([int](($ib.Send.Top + $ib.Send.Bottom) / 2))
        Start-Sleep -Milliseconds 1200

        $chat = Get-ChatPanel $main
        if ($null -eq $chat) { throw 'chat panel not found' }
        $script:rows1 = Get-Rows $chat
        $script:ink1 = List-InkHeight $main
        Save-WindowShot $main (Get-ShotPath 'persist-1-before.png')
        $script:store1 = Read-Store

        # ---- round 2: restart, is it back? ----
        Stop-BangGang
        Start-Sleep -Milliseconds 600
        $main = Start-BangGang 5
        $script:store2 = Read-Store
        $found = Find-Text $main $title
        $chat2 = Get-ChatPanel $main
        if ($null -eq $chat2) { throw 'chat panel not found after restart' }
        $script:rows2 = Get-Rows $chat2
        Save-WindowShot $main (Get-ShotPath 'persist-2-after.png')

        # ---- round 3: two empty conversations, then restart again ----
        $pill = Get-SearchPill $main
        $btns = Get-PillButtons $main $pill
        foreach ($i in 1..2) {
            Invoke-MouseClick ([int](($btns[1].Left + $btns[1].Right) / 2)) ([int](($btns[1].Top + $btns[1].Bottom) / 2))
            Start-Sleep -Milliseconds 700
        }
        $script:ink3 = List-InkHeight $main
        Save-WindowShot $main (Get-ShotPath 'persist-3-empty.png')

        Stop-BangGang
        Start-Sleep -Milliseconds 600
        $main = Start-BangGang 5
        $script:ink4 = List-InkHeight $main
        $script:store4 = Read-Store
        $back = Find-Text $main $title
        Save-WindowShot $main (Get-ShotPath 'persist-4-empty-after.png')

        $script:foundTitle = ($null -ne $found)
        $script:foundAgain = ($null -ne $back)
    }
} finally {
    if ($hadSettings) { Set-Content -Path $settings -Value $bakSettings -Encoding utf8 -NoNewline }
    else { Remove-Item $settings -ErrorAction SilentlyContinue }
    if ($hadConv) { Set-Content -Path $convFile -Value $bakConv -Encoding utf8 -NoNewline }
    else { Remove-Item $convFile -ErrorAction SilentlyContinue }
}

Write-Output ''

# ---- A. on disk while it is running ----
Write-Output '--- A. the conversation is written out ---'
$s1 = $script:store1
Check ($null -ne $s1) 'store file exists and parses' 'conversations.json'
if ($null -ne $s1) {
    $cs = @($s1.Conversations)
    Check ($cs.Count -eq 1) 'one conversation stored' ($cs.Count.ToString())
    if ($cs.Count -ge 1) {
        $ms = @($cs[0].Messages)
        Check ($cs[0].Title -eq $title) 'title stored' ('' + $cs[0].Title)
        Check ($ms.Count -eq 2) 'both messages stored' ($ms.Count.ToString())
        if ($ms.Count -ge 2) {
            Check ($ms[0].Role -eq 'user' -and $ms[0].Text -eq $title) 'user message stored verbatim' ('' + $ms[0].Text)
            Check ($ms[1].Role -eq 'assistant' -and ([string]$ms[1].Text).Length -gt 0) 'assistant message stored' ('' + ([string]$ms[1].Text).Length + ' chars')
        }
    }
}

# ---- B. back on screen after a restart ----
Write-Output ''
Write-Output '--- B. it comes back after a restart ---'
Check $script:foundTitle 'the title strip shows it again' ('label "' + $title + '"')
$r1 = @($script:rows1)
$r2 = @($script:rows2)
Write-Output ('  bubbles before: ' + ($r1 -join '  '))
Write-Output ('  bubbles after : ' + ($r2 -join '  '))
Check ($r1.Count -ge 2) 'two bubbles before the restart' ($r1.Count.ToString())
Check ($r2.Count -eq $r1.Count) 'same number of bubbles after' ($r2.Count.ToString() + ' vs ' + $r1.Count)
# Same sizes means the same text was re-measured into them: a dropped or truncated
# message comes back as a shorter bubble, which is the failure this is aimed at.
Check (($r2 -join ',') -eq ($r1 -join ',')) 'and the same sizes' 'identical rects'

# ---- C. empty conversations stay out of the file ----
Write-Output ''
Write-Output '--- C. empty conversations are not kept ---'
Write-Output ('  list ink height: 1 conversation ' + $script:ink1 + 'px   3 conversations (live) ' + $script:ink3 + 'px   after restart ' + $script:ink4 + 'px')
Check ($script:ink1 -gt 0) 'the list has a row to measure' ($script:ink1.ToString() + 'px')
Check ($script:ink3 -gt $script:ink1 + 10) 'three rows really are taller than one' ($script:ink3.ToString() + 'px vs ' + $script:ink1 + 'px')
Check ([Math]::Abs($script:ink4 - $script:ink1) -le 4) 'back to one row after the restart' ($script:ink4.ToString() + 'px vs ' + $script:ink1 + 'px')
if ($null -ne $script:store4) {
    Check ((@($script:store4.Conversations)).Count -eq 1) 'still one conversation in the file' ((@($script:store4.Conversations)).Count.ToString())
} else {
    Check $false 'store still readable' 'conversations.json missing'
}

# ---- D. where the relaunch lands ----
Write-Output ''
Write-Output '--- D. it lands on the last real conversation ---'
Check $script:foundAgain 'not dumped back on the welcome page' ('label "' + $title + '"')

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'ALL CHECKS PASSED' } else { Write-Output ('' + $script:fail + ' CHECK(S) FAILED') }

Write-BBDone 'persist-check'
