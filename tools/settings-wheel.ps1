# Item 3 (0.9.6): with a scrollable conversation open behind it, the settings overlay
# must take the wheel for its own page -- before the fix the wheel went to the chat
# underneath. WM_MOUSEWHEEL is delivered to the FOCUSED window, not the one under the
# cursor, and this app's IMessageFilters re-route it by cursor position; ChatView's
# filter saw the cursor over the (covered) chat area and ate every notch.
#
# The fix: SettingsOverlay routes the wheel to the current page's ScrollArea by cursor
# position (SettingsOverlay.PreFilterMessage), ChatView / InputPanel yield while
# SettingsOverlay.AnyOpen, and the routing itself yields while a dropdown popup is open
# (the popup owns the wheel for its own list).
#
# Phases:
#   A  control -- settings CLOSED, wheel UP over the chat: the chat offset must DROP.
#      (The chat loads pinned to the bottom -- offset is already at max -- so only the
#      up direction has room to move.) Proves the fixture chat is scrollable and the
#      wheel signal can reach it; without this, "the chat did not scroll" in phases
#      B-D would be vacuous.
#   B  settings open on the model-access page (the page with a scrollbar), wheel down
#      over the page body: a page input field must move UP and the chat offset must NOT
#      move.
#   C  wheel back up over the page body: the field returns to its rest position, chat
#      offset still unchanged.
#   D  provider dropdown open, wheel down over the popup: the page must NOT scroll (the
#      routing yields to the popup) and the chat must NOT scroll (ChatView still yields).
#      The page is at its top here (phase C left it there), so a broken yield would show
#      up as the field moving up -- the assertion is not vacuous.
#
# The page's scroll position is measured through a real child HWND of the page (the
# first single-line Edit): ScrollArea scrolls by moving its content panel, so the
# children's screen rects move with it, and GetWindowRect sees that even while the
# child is clipped. The chat's scroll position is ui-rows.json's offset field.
#
# Fixture: one conversation with 3 long assistant replies (enough for a chat
# scrollbar), written into chats\ and removed afterwards; settings.json /
# conversations.json and the whole chats\ directory are backed up and restored.
#
# Usage:  powershell -File tools\settings-wheel.ps1

. "$PSScriptRoot\_ui.ps1"

$script:fail = 0

# --- the fixture conversation --------------------------------------------------
$dir = Split-Path $script:BBExe -Parent
$settings = Join-Path $dir 'settings.json'
$chatsDir = Join-Path $dir 'chats'
$legacy = Join-Path $dir 'conversations.json'

$hadSettings = Test-Path $settings
$bakSettings = $null
if ($hadSettings) { $bakSettings = Get-Content $settings -Raw }
$hadLegacy = Test-Path $legacy
$bakLegacy = $null
if ($hadLegacy) { $bakLegacy = Get-Content $legacy -Raw }
$bakChats = @{}
if (Test-Path $chatsDir) {
    foreach ($f in Get-ChildItem $chatsDir -File) { $bakChats[$f.Name] = (Get-Content $f.FullName -Raw) }
    Remove-Item (Join-Path $chatsDir '*') -Force
}
# An empty chats\ makes the app import the single-file store on startup, which would add
# a second conversation to the list and put the fixture on the wrong row.
if (Test-Path $legacy) { Remove-Item $legacy -Force }

$script:ConvId = 'settingswheel0000000000000000001'

function New-Body([string]$role, [string]$text) {
    return @{
        Role = $role; When = (Get-Date).ToString('o'); Text = $text
        Reasoning = ''; ReasoningMs = 0; Warning = ''; Attachments = @()
    }
}

function Write-Fixture {
    if (-not (Test-Path $chatsDir)) { [void](New-Item -ItemType Directory -Force $chatsDir) }
    $now = (Get-Date).ToString('o')
    $para = 'A paragraph long enough to wrap several times inside the bubble, so that ' +
            'the conversation grows a real scrollbar: the wheel assertions below only ' +
            'mean something while both the chat and the settings page can scroll. '
    $msgs = @()
    for ($i = 1; $i -le 3; $i++) {
        $msgs += New-Body 'user' ('question ' + $i + ' about the thing that was asked')
        $long = New-Object System.Text.StringBuilder
        for ($p = 1; $p -le 10; $p++) { [void]$long.Append($para).Append("`n`n") }
        $msgs += New-Body 'assistant' ($long.ToString())
    }
    # ChatFile envelope, not a bare Conversation -- see the note in markdown-render.ps1.
    $file = @{
        Version = 1
        Conversation = @{
            Id = $script:ConvId; Title = 'settings-wheel'; CreatedAt = $now; UpdatedAt = $now
            Messages = $msgs
        }
    }
    $json = $file | ConvertTo-Json -Depth 8
    Set-Content -Path (Join-Path $chatsDir ($script:ConvId + '.json')) -Value $json -Encoding utf8
}

# --- helpers -------------------------------------------------------------------

# BB+RECT has no Width/Height members -- reading a missing property yields $null, not
# an error, and $null arithmetic silently collapses to 0.
function RW($r) { return ($r.Right - $r.Left) }
function RH($r) { return ($r.Bottom - $r.Top) }

# The first (topmost) single-line Edit inside the settings card's page area. Same
# filter as open-settings.ps1 -Dump: class WindowsForms10.Edit, height <= 40, and
# right of the rail (the sidebar's own search box must not qualify).
function Get-PageEdit([int]$cardX) {
    $best = $null
    foreach ($h in Get-WinKids $script:BBMain) {
        if (-not (Get-WinClass $h).StartsWith('WindowsForms10.Edit')) { continue }
        if (-not [BB]::IsWindowVisible($h)) { continue }
        $r = Get-WinRect $h
        if ((RH $r) -gt 40) { continue }
        if ((RW $r) -lt 100) { continue }
        if ($r.Left -lt ($cardX + 210)) { continue }
        if ($null -eq $best -or $r.Top -lt (Get-WinRect $best).Top) { $best = $h }
    }
    return $best
}

# The provider dropdowns of the model-access page, topmost first. Same structural
# lookup as provider-guide.ps1's Get-Pickers: a 28x28 guide button whose parent panel
# is 280-380 wide and also holds the wide dropdown control.
function Get-Pickers([int]$cardX) {
    $out = @()
    foreach ($h in Get-WinKids $script:BBMain) {
        if (-not [BB]::IsWindowVisible($h)) { continue }
        $r = Get-WinRect $h
        if ((RW $r) -ne 28 -or (RH $r) -ne 28) { continue }
        if ($r.Left -lt $cardX -or $r.Right -gt ($cardX + 880)) { continue }

        $p = [BB]::GetParent($h)
        if ($p -eq [IntPtr]::Zero -or $p -eq $script:BBMain) { continue }
        $pw = RW (Get-WinRect $p)
        if ($pw -lt 280 -or $pw -gt 380) { continue }

        $dd = [IntPtr]::Zero
        foreach ($k in Get-WinKids $p) {
            if ($k -eq $h) { continue }
            if (-not [BB]::IsWindowVisible($k)) { continue }
            if ((RW (Get-WinRect $k)) -gt 200) { $dd = $k; break }
        }
        if ($dd -eq [IntPtr]::Zero) { continue }
        $out += @{ Btn = $h; Dd = $dd; Top = $r.Top }
    }
    return @($out | Sort-Object { $_.Top })
}

# The chat offset from ui-rows.json, or -1 when the file is not (yet) there.
function Get-ChatOffset {
    $ui = Get-UiRows
    if ($null -eq $ui) { return -1 }
    return [int]$ui.offset
}

# --- run ------------------------------------------------------------------------

try {
    Invoke-BBProbe {
        Remove-Item $script:BBRows -ErrorAction SilentlyContinue
        Write-Fixture

        $main = Start-BangGang
        $script:BBMain = $main
        $mr = Get-WinRect $main
        $ww = RW $mr
        $wh = RH $mr
        Write-Output ('window ' + $mr.Left + ',' + $mr.Top + ' ' + $ww + 'x' + $wh)

        # Open the fixture conversation: the list is the tall narrow child flush with
        # the window's left edge (same lookup the other probes use).
        $list = $null
        foreach ($h in Get-WinKids $main) {
            $r = Get-WinRect $h
            if ($r.Left -ne $mr.Left) { continue }
            if ((RW $r) -ge 320) { continue }
            if ((RH $r) -lt 100) { continue }
            if ($r.Top -le $mr.Top + 38) { continue }
            if ($null -eq $list) { $list = $r }
        }
        if ($null -eq $list) { throw 'conversation list not found' }
        Invoke-MouseClick ([int]($list.Left + (RW $list) / 2)) ($list.Top + 19)

        # Wait for the rows file to show the loaded conversation, not a fixed sleep.
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $ui = $null
        while ($sw.ElapsedMilliseconds -lt 10000) {
            Start-Sleep -Milliseconds 300
            $ui = Get-UiRows
            if ($null -ne $ui -and @(Get-UiRowList $ui).Count -eq 6) { break }
        }
        if ($null -eq $ui) { Write-Output '  FAIL -- no ui-rows.json after opening the conversation'; $script:fail++; return }
        $rows = @(Get-UiRowList $ui)
        if ($rows.Count -ne 6) {
            Write-Output ('  FAIL -- ' + $rows.Count + ' rows rendered, expected 6')
            $script:fail++; return
        }
        if ([int]$ui.contentH -le [int]$ui.clientH) {
            Write-Output ('  FAIL -- content ' + $ui.contentH + 'px fits the view ' + $ui.clientH + 'px; the chat is not scrollable and phase A would prove nothing')
            $script:fail++; return
        }
        Write-Output ('  conversation open: 6 rows, content ' + $ui.contentH + 'px in a ' + $ui.clientH + 'px view (scrollable)')

        $ccx = [int]($ui.viewX + [int]$ui.clientW / 2)
        $ccy = [int]($ui.viewY + [int]$ui.clientH / 2)

        # ---- A. control: the wheel CAN scroll the chat --------------------------
        # The chat loads pinned to the bottom (offset == max), so wheel UP to make room.
        Write-Output ''
        Write-Output '--- A. settings closed, wheel up over the chat (control) ---'
        $off0 = Get-ChatOffset
        Invoke-WheelAt $ccx $ccy 4
        $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
        $offA = $off0
        while ($sw2.ElapsedMilliseconds -lt 5000) {
            Start-Sleep -Milliseconds 200
            $offA = Get-ChatOffset
            if ($offA -ge 0 -and $offA -ne $off0) { break }
        }
        Write-Output ('  chat offset ' + $off0 + ' -> ' + $offA)
        if ($offA -gt ($off0 - 100)) {
            Write-Output '  FAIL -- the chat barely scrolled; either it cannot scroll or the wheel never reaches it, and phases B-D would prove nothing'
            $script:fail++; return
        }

        # ---- open the settings overlay on the model-access page -----------------
        Write-Output ''
        Write-Output '--- open settings, page 1 (model access) ---'
        $pill = Get-SearchPill $main
        if ($null -eq $pill) { throw 'search pill not found' }
        $gear = Get-SettingsGear $main $pill
        if ($null -eq $gear) { throw 'settings gear not found' }

        $cardX = $mr.Left + [int](($ww - 880) / 2)
        $cardY = $mr.Top + [int](($wh - 640) / 2)

        Invoke-MouseClick ($gear.Left + 14) ($gear.Top + 14)
        Start-Sleep -Milliseconds 1100
        Invoke-MouseClick ($cardX + 14 + 90) ($cardY + 96 + 48 * 1 + 21)
        Start-Sleep -Milliseconds 1100

        $edit = Get-PageEdit $cardX
        if ($null -eq $edit) { Write-Output '  FAIL -- no input field found on the model-access page'; $script:fail++; return }
        $editTop0 = (Get-WinRect $edit).Top
        Write-Output ('  tracking the page''s first input at top=' + $editTop0)

        # The wheel point: inside the page body, over the rows' label column (no child
        # HWND there), away from the page's own scrollbar at the body's right edge.
        $wx = $cardX + 450
        $wy = $cardY + 350

        # ---- B. wheel over the settings page -------------------------------------
        Write-Output ''
        Write-Output '--- B. wheel down over the settings page ---'
        $offB0 = Get-ChatOffset
        Invoke-WheelAt $wx $wy -3
        Start-Sleep -Milliseconds 700
        $editTopB = (Get-WinRect $edit).Top
        $offB1 = Get-ChatOffset
        Write-Output ('  page input top ' + $editTop0 + ' -> ' + $editTopB + ', chat offset ' + $offB0 + ' -> ' + $offB1)
        if ($editTopB -ge ($editTop0 - 40)) {
            Write-Output '  FAIL -- the page did not scroll (the pre-0.9.6 bug, or the nav click missed the page)'
            $script:fail++
        }
        if ($offB1 -ne $offB0) {
            Write-Output '  FAIL -- the chat scrolled behind the overlay: ChatView did not yield'
            $script:fail++
        }

        # ---- C. wheel back up -----------------------------------------------------
        Write-Output ''
        Write-Output '--- C. wheel back up over the settings page ---'
        Invoke-WheelAt $wx $wy 3
        Start-Sleep -Milliseconds 700
        $editTopC = (Get-WinRect $edit).Top
        $offC1 = Get-ChatOffset
        Write-Output ('  page input top -> ' + $editTopC + ' (rest ' + $editTop0 + '), chat offset -> ' + $offC1)
        if ([Math]::Abs($editTopC - $editTop0) -gt 4) {
            Write-Output '  FAIL -- wheeling back up did not restore the page; the routing only works in one direction?'
            $script:fail++
        }
        if ($offC1 -ne $offB0) {
            Write-Output '  FAIL -- the chat scrolled while wheeling over the overlay'
            $script:fail++
        }

        # ---- D. dropdown popup owns the wheel -------------------------------------
        Write-Output ''
        Write-Output '--- D. wheel over the open provider dropdown ---'
        $pickers = @(Get-Pickers $cardX)
        if ($pickers.Count -eq 0) {
            Write-Output '  FAIL -- provider dropdown not found on the page'
            $script:fail++
        }
        else {
            $dd = Get-WinRect $pickers[0].Dd
            Invoke-MouseClick ([int](($dd.Left + $dd.Right) / 2)) ([int](($dd.Top + $dd.Bottom) / 2))
            Start-Sleep -Milliseconds 700

            $pop = [IntPtr]::Zero
            foreach ($h in Get-WinKids $main) {
                if (-not [BB]::IsWindowVisible($h)) { continue }
                if ((RW (Get-WinRect $h)) -eq ((RW $dd) + 12)) { $pop = $h; break }
            }
            if ($pop -eq [IntPtr]::Zero) {
                Write-Output '  FAIL -- dropdown popup did not open'
                $script:fail++
            }
            else {
                $pr = Get-WinRect $pop
                Invoke-WheelAt ([int](($pr.Left + $pr.Right) / 2)) ([int](($pr.Top + $pr.Bottom) / 2)) -3
                Start-Sleep -Milliseconds 700
                $editTopD = (Get-WinRect $edit).Top
                $offD1 = Get-ChatOffset
                Write-Output ('  page input top -> ' + $editTopD + ' (rest ' + $editTop0 + '), chat offset -> ' + $offD1)
                if ([Math]::Abs($editTopD - $editTop0) -gt 2) {
                    Write-Output '  FAIL -- the page scrolled under the open popup; the routing did not yield to it'
                    $script:fail++
                }
                if ($offD1 -ne $offB0) {
                    Write-Output '  FAIL -- the chat scrolled while wheeling over the popup'
                    $script:fail++
                }
                # close the popup: click the card's title area (the close button sits
                # at the top-RIGHT, the top centre is inert)
                Invoke-MouseClick ($cardX + 440) ($cardY + 24)
                Start-Sleep -Milliseconds 500
            }
        }

        Write-Output ''
        if ($script:fail -eq 0) { Write-Output 'PASS: with settings open the wheel scrolls the settings page; the chat behind never moves; an open dropdown owns the wheel.' }
        else { Write-Output "FAIL: $($script:fail) check(s) failed." }
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

Write-BBDone 'settings-wheel'
