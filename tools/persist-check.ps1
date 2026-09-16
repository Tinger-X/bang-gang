# Does a conversation survive a restart, and does the store on disk keep its shape?
#
# Conversations used to live only in memory: closing the app threw the lot away. They are
# now one JSON file per conversation under <exe dir>\chats\, named after the conversation's
# id. This probe kills the app and starts it again -- in-process checks (call Save, call
# Load, compare) would pass on a build where nothing ever gets written, which is exactly
# the bug that was reported the first time.
#
# What it pins down:
#   A. the 0.8.1 single-file store (conversations.json) is imported once and then removed.
#      This path runs exactly once per install, so nothing else can regress it unnoticed --
#      and getting it wrong silently eats the history the user already had.
#   B. a sent conversation is a file of its own, NAMED AFTER THE CONVERSATION'S ID -- the
#      id the app put in settings.json is checked against the file name, which is the only
#      way to tell "named after the id" from "named after something that happens to fit".
#      The file holds both messages while the app is still running.
#   C. after a restart it is BACK ON SCREEN -- the title strip shows it and the chat view
#      holds the same bubbles at the same sizes it had before the kill (a restore that
#      drops message text would come back with shorter bubbles)
#   D. empty conversations leave NO file: click + twice, and the next launch must show one
#      conversation again, not three. This is the rule that keeps the list from filling up
#      with "new chat" rows the user can never get rid of.
#   E. deleting a conversation deletes its file -- and its attachments stay (see the note
#      by the attachments check; the rule is "no orphan files, but never delete a picture
#      that might still be referenced")
#   F. the fallback lands somewhere sane: with everything deleted the relaunch comes back
#      to the welcome page rather than to a conversation that no longer exists
#
# The list itself is self-drawn (no HWND per row), so "how many conversations are showing"
# is read as the ink bounding box of the list area: one row is one band, and three rows are
# visibly taller. Anything that reads a row count out of a control name would be reading a
# control that does not exist.
#
# settings.json, conversations.json and the WHOLE chats\ directory are backed up and put
# back -- the last two are the user's own chat history if they ever ran this build.

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

# ---------------- the store on disk ----------------

# Every conversation file, by name. Temporary files (x.json.tmp) are excluded the same way
# the app excludes them: by the extension being exactly .json.
#
# Callers must wrap this in @(): with one conversation in the directory PowerShell hands
# back the bare string instead of a one-element array, and $files[0] then quietly becomes
# the first CHARACTER of the file name -- which reads as "the file is named 5".
function List-ChatFiles {
    if (-not (Test-Path $chatsDir)) { return @() }
    $out = @()
    foreach ($f in Get-ChildItem $chatsDir -File) {
        if ([IO.Path]::GetExtension($f.Name) -ne '.json') { continue }
        $out += ,$f.Name
    }
    return @($out)
}

function Read-ChatFile([string]$name) {
    try { return (Get-Content (Join-Path $chatsDir $name) -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { return $null }
}

function Read-Settings {
    try { return (Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { return $null }
}

# ---------------- the window ----------------

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

# The conversation list: full width of the sidebar, below the 46-tall head strip.
function Get-ConvList($main) {
    $mr = Get-WinRect $main
    $found = $null
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Left -ne $mr.Left) { continue }
        if (($r.Right - $r.Left) -ge 320) { continue }
        if (($r.Bottom - $r.Top) -lt 100) { continue }
        if ($r.Top -le $mr.Top + 38) { continue }
        $found = @{ H = $h; R = $r }
    }
    return $found
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
$titleLegacy = 'persist-legacy-msg'
$legacyId = 'abcdef0123456789abcdef0123456789'

# ---------------- backups ----------------

$hadSettings = Test-Path $settings
$bakSettings = $null
if ($hadSettings) { $bakSettings = Get-Content $settings -Raw }

$hadLegacy = Test-Path $legacy
$bakLegacy = $null
if ($hadLegacy) { $bakLegacy = Get-Content $legacy -Raw }

$bakChats = @{}
foreach ($n in @(List-ChatFiles)) { $bakChats[$n] = (Get-Content (Join-Path $chatsDir $n) -Raw) }

$script:rows1 = @()
$script:rows2 = @()
$script:ink1 = 0
$script:ink3 = 0
$script:ink4 = 0
$script:importTitle = $false
$script:importFile = 0
$script:importMsgs = 0
$script:importGone = $false
$script:importName = ''
$script:oneFile = ''
$script:fileCount = 0
$script:fileList = ''
$script:fileMsgs = 0
$script:fileTitle = ''
$script:nameIsId = $false
$script:foundTitle = $false
$script:foundAgain = $false
$script:backOnWelcome = $false
$script:filesAfterEmpty = -1
$script:filesAfterDelete = -1
$script:activeCleared = $false

try {
    # A provider is required for round 2 below to be able to send at all: since 0.8.5 the
    # send gate refuses outright when no model is configured, and a send that never
    # happens writes no file -- which would read as "persistence is broken". The endpoint
    # is a dead port; the reply lands as an error bubble, which is fine here because both
    # sides of the restart see the same two messages.
    Set-Content -Path $settings -Encoding utf8 `
        -Value '{"ThemeMode":"light","ChatProvider":"custom","ChatProfiles":{"custom":{"url":"http://127.0.0.1:9/v1","key":"probe","model":"probe-model"}}}'
    if (Test-Path $chatsDir) { Remove-Item (Join-Path $chatsDir '*') -Force }

    Invoke-BBProbe {
        # ---- round 1: the old single-file store is imported ----
        #
        # Written by hand in the 0.8.1 shape: {Version, ActiveId, Conversations:[...]}.
        $stamp = '2026-09-16T10:00:00'
        $old = @{
            Version = 1
            ActiveId = $legacyId
            Conversations = @(@{
                Id = $legacyId
                Title = $titleLegacy
                CreatedAt = $stamp
                UpdatedAt = $stamp
                Messages = @(
                    @{ Role = 'user'; When = $stamp; Text = $titleLegacy; Attachments = @() },
                    @{ Role = 'assistant'; When = $stamp; Text = 'imported reply'; Attachments = @() }
                )
            })
        }
        Set-Content -Path $legacy -Value ($old | ConvertTo-Json -Depth 8) -Encoding utf8

        $main = Start-BangGang 5
        $script:importTitle = ($null -ne (Find-Text $main $titleLegacy))
        $files = @(List-ChatFiles)
        $script:importFile = $files.Count
        if ($files.Count -eq 1) {
            $j = Read-ChatFile $files[0]
            if ($null -ne $j) {
                $script:importMsgs = @($j.Conversation.Messages).Count
                $script:importName = $files[0]
            }
        }
        Save-WindowShot $main (Get-ShotPath 'persist-1-import.png')
        Stop-BangGang
        Start-Sleep -Milliseconds 600
        $script:importGone = -not (Test-Path $legacy)

        # start the rest of the run from an empty history
        if (Test-Path $chatsDir) { Remove-Item (Join-Path $chatsDir '*') -Force }

        # ---- round 2: send a message ----
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

        # Poll for the round to FINISH; do not sleep a fixed 1200ms.
        #
        # With the provider above the reply is a real HTTP attempt, and against that dead
        # port it fails after ~2.1s (read off the trace log: 18:35:25.835 request ->
        # 18:35:27.951 error). The conversation is written twice -- once at send time with
        # the user's message alone, once when the round ends with the reply appended -- so
        # a fixed 1200ms lands in between: the file holds one message and the chat view
        # holds an empty placeholder bubble, which is indistinguishable from "persistence
        # dropped the reply". Wait for the file to say two, not for the clock to say enough.
        $waited = 0
        while ($waited -lt 20000) {
            $seen = @(List-ChatFiles)
            if ($seen.Count -eq 1) {
                $j = Read-ChatFile $seen[0]
                if ($null -ne $j -and @($j.Conversation.Messages).Count -ge 2) { break }
            }
            Start-Sleep -Milliseconds 250
            $waited += 250
        }
        Write-Output ('  round finished after ' + $waited + 'ms')

        $chat = Get-ChatPanel $main
        if ($null -eq $chat) { throw 'chat panel not found' }
        $script:rows1 = Get-Rows $chat
        $script:ink1 = List-InkHeight $main
        Save-WindowShot $main (Get-ShotPath 'persist-2-before.png')

        $files = @(List-ChatFiles)
        $script:fileCount = $files.Count
        $script:fileList = ($files -join ' ')
        $script:oneFile = ''
        if ($files.Count -eq 1) {
            $script:oneFile = $files[0]
            $j = Read-ChatFile $files[0]
            if ($null -ne $j) {
                $script:fileMsgs = @($j.Conversation.Messages).Count
                $script:fileTitle = '' + $j.Conversation.Title
            }
        }
        $set = Read-Settings
        $script:nameIsId = ($files.Count -eq 1 -and $null -ne $set -and ('' + $set.ActiveChatId) -eq [IO.Path]::GetFileNameWithoutExtension($files[0]))

        # ---- round 3: restart, is it back? ----
        Stop-BangGang
        Start-Sleep -Milliseconds 600
        $main = Start-BangGang 5
        $script:foundTitle = ($null -ne (Find-Text $main $title))
        $chat2 = Get-ChatPanel $main
        if ($null -eq $chat2) { throw 'chat panel not found after restart' }
        $script:rows2 = Get-Rows $chat2
        Save-WindowShot $main (Get-ShotPath 'persist-3-after.png')

        # ---- round 4: two empty conversations ----
        $pill = Get-SearchPill $main
        $btns = Get-PillButtons $main $pill
        foreach ($i in 1..2) {
            Invoke-MouseClick ([int](($btns[1].Left + $btns[1].Right) / 2)) ([int](($btns[1].Top + $btns[1].Bottom) / 2))
            Start-Sleep -Milliseconds 700
        }
        $script:ink3 = List-InkHeight $main
        # the list is on the two empty ones now: go back to the real one so the delete
        # below hits a conversation that has something in it
        $row = Get-ConvList $main
        if ($null -ne $row) {
            $lw = $row.R.Right - $row.R.Left
            Invoke-MouseClick ($row.R.Left + [int]($lw * 0.5)) ($row.R.Top + 38 * 2 + 6 * 2 + 19)
            Start-Sleep -Milliseconds 700
        }
        Save-WindowShot $main (Get-ShotPath 'persist-4-empty.png')

        Stop-BangGang
        Start-Sleep -Milliseconds 600
        $main = Start-BangGang 5
        $script:ink4 = List-InkHeight $main
        $script:foundAgain = ($null -ne (Find-Text $main $title))
        $script:filesAfterEmpty = @(List-ChatFiles).Count

        # ---- round 5: deleting the conversation takes its file with it ----
        $row = Get-ConvList $main
        if ($null -eq $row) { throw 'conversation list not found' }
        $lw = $row.R.Right - $row.R.Left
        $rowW = [int]($lw * 0.95)
        $rowX = $row.R.Left + [int](($lw - $rowW) / 2)
        # item 0: 38 tall, its delete slot 22 wide ending 6px inside the right edge
        Invoke-MouseClick ($rowX + $rowW - 6 - 11) ($row.R.Top + 8 + 11)
        Start-Sleep -Milliseconds 900
        $script:filesAfterDelete = @(List-ChatFiles).Count
        $set = Read-Settings
        $script:activeCleared = ($null -ne $set -and ('' + $set.ActiveChatId).Length -eq 0)
        Save-WindowShot $main (Get-ShotPath 'persist-5-deleted.png')

        # ---- round 6: nothing left, so nothing comes back ----
        Stop-BangGang
        Start-Sleep -Milliseconds 600
        $main = Start-BangGang 5
        $script:backOnWelcome = ($null -eq (Find-Text $main $title))
        Save-WindowShot $main (Get-ShotPath 'persist-6-welcome.png')
    }
} finally {
    if ($hadSettings) { Set-Content -Path $settings -Value $bakSettings -Encoding utf8 -NoNewline }
    else { Remove-Item $settings -ErrorAction SilentlyContinue }
    if ($hadLegacy) { Set-Content -Path $legacy -Value $bakLegacy -Encoding utf8 -NoNewline }
    else { Remove-Item $legacy -ErrorAction SilentlyContinue }
    # put the chats back exactly as they were: only the files this probe may have created
    # are removed, and every file that was there before is rewritten from the backup.
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

# ---- A. the old single-file store is imported ----
#
# Note every count below was captured DURING the run, not here: by the time these checks
# print, the finally block has already put the user's chats\ directory back, so reading
# the directory now would be reading the restore and not the run.
Write-Output '--- A. the 0.8.1 single-file store is imported ---'
Check ($script:importFile -eq 1) 'the old conversation became one file' ($script:importFile.ToString() + ' file(s) now')
Check $script:importTitle 'it is the one shown on screen' ('label "' + $titleLegacy + '"')
Check ($script:importMsgs -eq 2) 'both messages came across' ($script:importMsgs.ToString())
Check $script:importGone 'conversations.json is gone once imported' 'moved, not copied'

# ---- B. one file per conversation, named after its id ----
Write-Output ''
Write-Output '--- B. the conversation is written out, one file per conversation ---'
Check ($script:oneFile.Length -gt 0) 'exactly one conversation file' ('chats\ ' + $script:fileList)
Check $script:nameIsId 'the file is named after the conversation id' $script:oneFile
Check ($script:fileMsgs -eq 2) 'both messages stored' ($script:fileMsgs.ToString())
Check ($script:fileTitle -eq $title) 'title stored' $script:fileTitle

# ---- C. back on screen after a restart ----
Write-Output ''
Write-Output '--- C. it comes back after a restart ---'
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

# ---- D. empty conversations leave no file ----
Write-Output ''
Write-Output '--- D. empty conversations are not kept ---'
Write-Output ('  list ink height: 1 conversation ' + $script:ink1 + 'px   3 conversations (live) ' + $script:ink3 + 'px   after restart ' + $script:ink4 + 'px')
Check ($script:ink1 -gt 0) 'the list has a row to measure' ($script:ink1.ToString() + 'px')
Check ($script:ink3 -gt $script:ink1 + 10) 'three rows really are taller than one' ($script:ink3.ToString() + 'px vs ' + $script:ink1 + 'px')
Check ([Math]::Abs($script:ink4 - $script:ink1) -le 4) 'back to one row after the restart' ($script:ink4.ToString() + 'px vs ' + $script:ink1 + 'px')
Check ($script:filesAfterEmpty -eq 1) 'still one file in chats\' ($script:filesAfterEmpty.ToString())

# ---- E. deleting a conversation deletes its file ----
Write-Output ''
Write-Output '--- E. deleting a conversation deletes its file ---'
Check ($script:filesAfterDelete -eq 0) 'its file is gone' ($script:filesAfterDelete.ToString() + ' file(s) left')
Check $script:activeCleared 'settings no longer points at it' 'ActiveChatId emptied'
# The attachments under images\ are deliberately NOT swept up: an attachment is a path,
# not an owned file, so the same picture can be referenced by several messages. Deleting
# is irreversible, and an orphan costs a few KB -- so nothing is deleted on a guess.

# ---- F. where the relaunch lands ----
Write-Output ''
Write-Output '--- F. with nothing left it comes back to the welcome page ---'
Check $script:backOnWelcome 'not stuck on a conversation that is gone' 'title label is gone'

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'ALL CHECKS PASSED' } else { Write-Output ('' + $script:fail + ' CHECK(S) FAILED') }

Write-BBDone 'persist-check'
