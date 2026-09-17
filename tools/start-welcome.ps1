# Item 3: a relaunch lands on the WELCOME page, even with history in the store.
#
# Before 0.9.0 RestoreConversations() ended with ActivateConversation(last), so every
# launch reopened whatever was open when the app was closed. The user asked for the
# welcome page instead.
#
# welcome-click.ps1 already asserts "the app starts on the welcome page" -- but it WIPES
# chips\ first, so it only ever proves that an app with nothing to open shows nothing.
# That is a different claim, and it would pass unchanged on the broken build. This one
# seeds a conversation AND leaves ActiveChatId pointing at it in settings.json, i.e. it
# hands the app every reason to reopen and requires it not to.
#
# Three parts, and the third is what makes the first two mean anything:
#   A  the welcome page is up and there is no input box (no conversation is open)
#   B  the seeded conversation IS in the list -- "no conversation open" and "the store
#      never loaded" are the same picture on the right-hand side of the window, and B
#      is the only thing that tells them apart
#   C  clicking that row really does open it. Without this, a list that is painted but
#      dead -- or a window that never pumped a message -- passes A and B.
#
# chats\, conversations.json and settings.json go back in the finally. All three are the
# user's own data if they ever ran this build.

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

$cid = 'ddddeeeeffff00001111222233334444'
$stamp = '2026-09-17T10:00:00'

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

$script:bandsStart = ''
$script:bandsAfter = ''
$script:editStart = [IntPtr]::Zero
$script:editAfter = [IntPtr]::Zero
$script:listInk = 0
$script:listW = 0
$script:listR = $null

try {
    # The seeded conversation is the only one, so "which row is it" needs no guessing.
    if (Test-Path $chatsDir) { Remove-Item (Join-Path $chatsDir '*') -Force }
    if (Test-Path $legacy) { Remove-Item $legacy -Force }

    # ActiveChatId is set ON PURPOSE: it is the record of "this is the conversation that
    # was open", which is exactly the thing the app must now decline to act on.
    $set = @{
        ThemeMode = 'light'
        ActiveChatId = $cid
        ChatProvider = 'custom'
        ChatProfiles = @{ custom = @{ url = 'http://127.0.0.1:9/v1'; key = 'probe'; model = 'probe-model' } }
    }
    Set-Content -Path $settings -Value ($set | ConvertTo-Json -Depth 6) -Encoding utf8

    $conv = @{
        Version = 1
        Conversation = @{
            Id = $cid
            Title = 'start-welcome'
            CreatedAt = $stamp
            UpdatedAt = $stamp
            Messages = @(
                @{ Role = 'user';      When = $stamp; Text = 'first one';  Attachments = @() },
                @{ Role = 'assistant'; When = $stamp; Text = 'second one'; Attachments = @() }
            )
        }
    }
    if (-not (Test-Path $chatsDir)) { New-Item -ItemType Directory -Force $chatsDir | Out-Null }
    Set-Content -Path (Join-Path $chatsDir ($cid + '.json')) -Value ($conv | ConvertTo-Json -Depth 8) -Encoding utf8

    Invoke-BBProbe {
        $main = Start-BangGang 5
        $mr = Get-WinRect $main

        # The conversation list is the anchor for both the region to scan and the row to
        # click, and it must be found before anything else is asked.
        $list = Get-ConvList $main
        if ($null -eq $list) { throw 'conversation list not found' }

        # The body: everything right of the list. The welcome page is centred in it, and
        # the sidebar has no accent-filled widget of its own, so a slice of overlap would
        # not matter -- but the region is asserted wide enough below so that a bogus
        # 20px-wide region fails loudly instead of scanning nothing and reporting "no
        # accent bands", which reads as "the welcome page is broken".
        $wx0 = $list.R.Right + 8
        $wx1 = $mr.Right - 8
        $wy0 = $mr.Top + 38
        $wy1 = $mr.Bottom - 4
        $script:listR = $list.R
        $script:listW = $wx1 - $wx0
        if ($script:listW -lt 400) { throw ('the scan region is only ' + $script:listW + 'px wide; the list lookup is wrong') }

        Write-Output ("window " + $mr.Left + "," + $mr.Top + " " + ($mr.Right - $mr.Left) + "x" + ($mr.Bottom - $mr.Top) +
                      "   list " + $list.R.Left + "," + $list.R.Top + " " + ($list.R.Right - $list.R.Left) + "x" + ($list.R.Bottom - $list.R.Top) +
                      "   body scan " + $wx0 + "," + $wy0 + " .. " + $wx1 + "," + $wy1)

        # ---- A. the welcome page, not a conversation ----
        Write-Output ''
        Write-Output '--- A. a relaunch lands on the welcome page ---'
        $script:editStart = Get-InputEditBig $main
        $a = Get-AccentBands $wx0 $wy0 $wx1 $wy1
        $script:bandsStart = ''
        if ($null -ne $a) { $script:bandsStart = (($a.Bands | ForEach-Object { [int](($_.Y0 + $_.Y1) / 2) }) -join ',') }
        $bandN = 0
        if ($null -ne $a) { $bandN = $a.Bands.Count }
        Write-Output ('  accent ' + $(if ($null -eq $a) { 'none' } else { $a.Accent }) +
                      '   widgets span y ' + $script:bandsStart + '   input box ' + $script:editStart)
        # Two: the logo and the button. One would also be true of a page that painted half
        # of itself, and the button is the one that matters.
        Check ($bandN -ge 2) 'the welcome page is up (its logo and its button)' ($bandN.ToString() + ' accent widgets')
        Check ($script:editStart -eq [IntPtr]::Zero) 'no input box: no conversation was reopened' 'none'

        # ---- B. the history is there, it just is not open ----
        Write-Output ''
        Write-Output '--- B. the seeded conversation is still in the list ---'
        # Row 0's own strip: 38px of row plus the 6px gap. An empty list paints nothing at
        # all (ConvListBox has no "no conversations" placeholder), so ink here is the row
        # and only the row. Absolute dark pixels are fine in THIS spot -- the list's ground
        # is PanelBg and the row's title is near-black -- unlike the bubble-strip probes,
        # where the fixture's own colour would satisfy a darkness count.
        $script:listInk = 0
        $ib = Get-InkBoxNumAt ($list.R.Left + 6) ($list.R.Top + 2) (($list.R.Right - $list.R.Left) - 12) 46 200
        if ($null -ne $ib) { $script:listInk = $ib.N }
        Write-Output ('  ink in row 0''s strip: ' + $script:listInk + 'px')
        Check ($script:listInk -gt 40) 'the stored conversation is listed' ($script:listInk.ToString() + ' ink px')

        # ---- C. the control group: that row really opens it ----
        Write-Output ''
        Write-Output '--- C. clicking the row opens the conversation (control group) ---'
        if (-not (Open-ConvRow $main 0)) { throw 'the list vanished before the click' }
        # The chat view's own rows are the second signal: the input box appearing alone
        # would be true of a conversation that opened empty.
        $rows = 0
        $waited = 0
        while ($waited -lt 6000) {
            $rows = @(Get-UiRowList (Get-UiRows)).Count
            if ($rows -ge 2) { break }
            Start-Sleep -Milliseconds 250
            $waited += 250
        }
        $script:editAfter = Get-InputEditBig $main
        $b = Get-AccentBands $wx0 $wy0 $wx1 $wy1
        $script:bandsAfter = ''
        $bandN2 = 0
        if ($null -ne $b) {
            $bandN2 = $b.Bands.Count
            $script:bandsAfter = (($b.Bands | ForEach-Object { [int](($_.Y0 + $_.Y1) / 2) }) -join ',')
        }
        Write-Output ('  input box ' + $script:editAfter + '   welcome widgets ' + $bandN2 + ' (' + $script:bandsAfter + ')   chat rows ' + $rows)
        Check ($script:editAfter -ne [IntPtr]::Zero) 'the input box appeared' 'a conversation is open'
        Check ($bandN2 -eq 0) 'and the welcome page is gone' ($bandN2.ToString() + ' accent widgets left')
        Check ($rows -ge 2) 'both stored messages are on screen' ($rows.ToString() + ' rows')
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

Write-BBDone 'start-welcome'
