# The chat area's own scroll bar, the horizontal bar that is NOT there any more, and the
# bubble width rule (<= 90% of the chat area, user right / assistant left).
#
# All three of these are invisible to a probe that only enumerates controls and reads
# rects, which is why this one is written the way it is:
#
#   * the horizontal bar along the bottom was never "a control you can point at". The old
#     code set AutoScrollMinSize.Width = clientW - 26 while the vertical bar ate ~17px of
#     that client width, so the number was ALWAYS wider than the real client area and the
#     native horizontal bar was always up. Two independent proofs are taken here: no
#     *SCROLLBAR* class anywhere under the main window, AND the chat view's client rect
#     equals its window rect -- a native bar costs 17px of client height, so "client ==
#     window" is the direct evidence rather than a proxy for it.
#   * the flicker while the sidebar animates had the same root cause: the horizontal bar
#     appearing and vanishing changed the client height, which fed back into the vertical
#     bar, on every frame of the animation. The leftover-pixel half of that lives in
#     tools/sidebar-tear.ps1; the half that belongs here is the client rect simply not
#     moving.
#   * the bubble width. It used to be a hard 560px cap. Now it is <= 90% of the chat area,
#     pushed in by the chat view, and the bubbles sit right / left.
#
# The thumb is 6px wide of a colour mixed 26% toward the text colour. On the light theme's
# white chat background that lands at luminance ~197, which a 200 cutoff only just catches
# -- so the scans below cut at 240 (see the note on Get-InkBoxNum).
#
# settings.json and the whole chats\ directory are backed up and put back: the
# conversation this seeds is written straight into chats\ (typing a 2500-character
# paragraph into the input box would be slower and far less exact), and chats\ is the
# user's own history if they ever ran this build.
#
# Usage:  powershell -File tools\chat-area.ps1

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

function Lum($c) { return 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B }

# ---------------- window lookups ----------------

# The chat view: the control that starts 48px below the chrome bar and runs to the window's
# right edge. Same one persist-check.ps1 uses -- _mainArea / _chatUI span the whole window
# so they cannot be told apart by their left edge any more, and the chat view is the
# leftmost of the candidates that still ends at the window's right edge.
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

# The bubbles: the chat view's own children. Bubbles are real child HWNDs (that is a
# standing invariant -- several probes count them), so this is an enumeration and not a
# pixel scan.
function Get-Bubbles($chat) {
    $rows = @()
    foreach ($h in Get-WinKids $chat.H) {
        if ((Get-WinClass $h) -like '*SCROLLBAR*') { continue }
        $r = Get-WinRect $h
        $rows += ,@{ Left = $r.Left; Top = $r.Top; Right = $r.Right; Bottom = $r.Bottom
                     W = ($r.Right - $r.Left); H = ($r.Bottom - $r.Top) }
    }
    return @($rows | Sort-Object { $_.Top })
}

# The collapse/expand button: the only 28x28 control in the chat title strip. Its x moves
# with the sidebar, so it must not be found by x.
function Get-SideToggle($main) {
    $mr = Get-WinRect $main
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -ne 28 -or ($r.Bottom - $r.Top) -ne 28) { continue }
        if ($r.Top -le ($mr.Top + 38)) { continue }
        if ($r.Top -ge ($mr.Top + 38 + 48)) { continue }
        return $r
    }
    return $null
}

# How many native scroll bars exist anywhere under the main window. EnumChildWindows walks
# the whole tree, so this covers the chat view, the conversation list, the settings overlay
# and anything else at once.
function Count-NativeBars($main) {
    $n = 0
    foreach ($h in Get-WinKids $main) {
        if ((Get-WinClass $h) -like '*SCROLLBAR*') { $n++ }
    }
    return $n
}

# The self-drawn thumb, as an ink box inside a 1px column down the bar's centre line.
# $X is the bar's centre (the bar spans [W-9, W-4]), so nothing else can be in the column.
#
# The control group -- the same column scan on the far side of the chat view -- is what
# makes this mean something: ink found on the right proves a bar is drawn only if the same
# scan finds none on the left.
function Get-ThumbBox($chat) {
    $top = $chat.R.Top + 4
    $h = ($chat.R.Bottom - $chat.R.Top) - 8
    return Get-InkBoxNumAt ($chat.R.Right - 6) $top 1 $h 240
}

function Get-LeftColumnInk($chat) {
    $top = $chat.R.Top + 4
    $h = ($chat.R.Bottom - $chat.R.Top) - 8
    return Get-InkBoxNumAt ($chat.R.Left + 12) $top 1 $h 240
}

# The width a bubble may not exceed, mirrored from ChatView.LayoutRows. Note [Math]::Floor
# and not [int]: PowerShell's [int] cast ROUNDS (banker's rounding), so [int](944 * 0.9)
# is 850 where the C# (int) cast gives 849 -- a 1px disagreement that would sit right on
# the assertion's edge.
function Get-CapW([int]$chatW) {
    $cap = [int][Math]::Floor($chatW * 0.9)
    if (($chatW - 52) -lt $cap) { $cap = $chatW - 52 }
    if ($cap -lt 180) { $cap = 180 }
    return $cap
}

# ---------------- fixtures ----------------

$cid = 'aaaabbbbccccddddeeeeffff00001111'
$stamp = '2026-09-16T10:00:00'
# Deliberately short words. Greedy wrapping breaks BEFORE the word that does not fit, so the
# widest physical line falls short of the cap by up to one word -- and the F check below
# asks the bubble to come within a word of the cap. With ordinary English words at this
# machine's font scaling that one word is ~100px and the tolerance would have to be sloppy;
# with two-letter words the leftover is a third of that, and the check stays tight enough
# that a bubble stuck at its old width (289px short) cannot sneak past it.
$sentence = 'ab cd ef gh ij kl mn op qr st uv wx yz '
# Long enough to wrap many times over (so the wrap has to fill the cap) and, together,
# taller than the chat area -- otherwise there is nothing to scroll and the thumb checks
# would be measuring a bar that is not drawn. Kept modest because the machine this runs
# on scales the fonts up: at 150% each of these is several hundred pixels tall already.
$longUser = $sentence * 15
$longAsst = $sentence * 22
$shortMsg = 'hi there'

# ---------------- backups ----------------

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

try {
    # The seeded conversation is the only one, so "which conversation is on screen" needs no
    # guessing. conversations.json is moved out of the way as well: with chats\ holding one
    # file ChatStore never looks at it, but leaving a 0.8.1 store sitting there while this
    # probe wipes chats\ is how a user's history gets eaten by a probe that forgot a branch.
    if (Test-Path $chatsDir) { Remove-Item (Join-Path $chatsDir '*') -Force }
    if (Test-Path $legacy) { Remove-Item $legacy -Force }

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
            Title = 'chat-area'
            CreatedAt = $stamp
            UpdatedAt = $stamp
            Messages = @(
                @{ Role = 'user';      When = $stamp; Text = $longUser; Attachments = @() },
                @{ Role = 'assistant'; When = $stamp; Text = $longAsst; Attachments = @() },
                @{ Role = 'user';      When = $stamp; Text = $shortMsg; Attachments = @() }
            )
        }
    }
    if (-not (Test-Path $chatsDir)) { New-Item -ItemType Directory -Force $chatsDir | Out-Null }
    Set-Content -Path (Join-Path $chatsDir ($cid + '.json')) -Value ($conv | ConvertTo-Json -Depth 8) -Encoding utf8

    Invoke-BBProbe {
        $main = Start-BangGang 5
        $chat = Get-ChatPanel $main
        if ($null -eq $chat) { throw 'chat panel not found' }
        $cw = $chat.R.Right - $chat.R.Left

        # ---- A. no native scroll bar, and the client area proves it ----
        Write-Output '--- A. the chat area scrolls itself ---'
        $bars = Count-NativeBars $main
        Check ($bars -eq 0) 'no native scroll bar anywhere under the main window' ($bars.ToString() + ' found')

        $wr = Get-WinRect $chat.H
        $cr = Get-ClientRect $chat.H
        $ww = $wr.Right - $wr.Left
        $wh = $wr.Bottom - $wr.Top
        $cwid = $cr.Right - $cr.Left
        $chgt = $cr.Bottom - $cr.Top
        Check (($cwid -eq $ww) -and ($chgt -eq $wh)) 'client area == window area (a native bar would cost 17px)' `
            (($cwid.ToString() + 'x' + $chgt) + ' vs ' + ($ww.ToString() + 'x' + $wh))

        # ---- B. the thumb is drawn, and only where it should be ----
        Write-Output ''
        Write-Output '--- B. a 6px thumb on the right edge, nothing on the left ---'
        $thumb0 = Get-ThumbBox $chat
        if ($null -eq $thumb0) {
            Check $false 'thumb ink found in the right-hand column' 'no ink at all'
        } else {
            $th0 = $thumb0.B - $thumb0.Y + 1
            Check ($th0 -gt 28) 'thumb ink found in the right-hand column' ($th0.ToString() + 'px tall')
        }
        $leftInk = Get-LeftColumnInk $chat
        Check ($null -eq $leftInk) 'the same scan on the left finds nothing (control group)' `
            $(if ($null -eq $leftInk) { 'clean' } else { '' + $leftInk.N + ' ink px' })
        Save-WindowShot $main (Get-ShotPath 'chat-area-1-bottom.png')

        # ---- C. it starts pinned to the bottom ----
        Write-Output ''
        Write-Output '--- C. a restored conversation comes up pinned to the bottom ---'
        $b0 = Get-Bubbles $chat
        Check ($b0.Count -eq 3) 'three bubbles on screen' ((@($b0).Count).ToString())
        if (@($b0).Count -ge 3) {
            $gap = $chat.R.Bottom - $b0[2].Bottom
            Check ([Math]::Abs($gap - 26) -le 40) 'the last bubble sits at the bottom' ($gap.ToString() + 'px above the bottom edge')
        }

        # ---- D. the wheel scrolls it, and the rows move with it ----
        Write-Output ''
        Write-Output '--- D. the wheel scrolls the view ---'
        $mx = [int](($chat.R.Left + $chat.R.Right) / 2)
        $my = [int](($chat.R.Top + $chat.R.Bottom) / 2)
        Invoke-WheelAt $mx $my 5
        Start-Sleep -Milliseconds 300
        $thumb1 = Get-ThumbBox $chat
        $b1 = Get-Bubbles $chat
        if (($null -eq $thumb0) -or ($null -eq $thumb1)) {
            Check $false 'the thumb moves up as the content scrolls down' 'thumb missing in one of the two frames'
        } else {
            $up = $thumb0.Y - $thumb1.Y
            Check ($up -gt 10) 'the thumb moves up as the content scrolls down' ($up.ToString() + 'px up')
        }
        if ((@($b0).Count -ge 1) -and (@($b1).Count -ge 1)) {
            $moved = $b1[0].Top - $b0[0].Top
            Check ($moved -gt 100) 'the bubbles move with the offset' ($moved.ToString() + 'px down')
        }
        Save-WindowShot $main (Get-ShotPath 'chat-area-2-scrolled.png')

        # ---- E. hovering the thumb darkens it ----
        #
        # Compared against the SAME pixel one moment earlier, never against an absolute
        # luminance: the resting thumb is a mix of the chat background and the text colour,
        # so "how dark is it" depends on the theme, while "did it get darker when the
        # pointer arrived" does not.
        Write-Output ''
        Write-Output '--- E. the thumb darkens under the pointer ---'
        $barX = $chat.R.Right - 6
        if ($null -eq $thumb1) {
            Check $false 'hovering the thumb darkens it' 'no thumb to hover'
        } else {
            $ty = $chat.R.Top + 4 + [int](($thumb1.Y + $thumb1.B) / 2)
            # park the cursor somewhere else first: a SetCursorPos that moves nothing
            # produces no WM_MOUSEMOVE, and "the app ignored the hover" would be this
            # probe's own doing
            [void][BB]::SetCursorPos(($mx), ($my))
            Start-Sleep -Milliseconds 300
            $c1 = Get-Crop $barX $ty 1 1
            $cold = Lum $c1.GetPixel(0, 0)
            $c1.Dispose()
            [void][BB]::SetCursorPos(($barX), ($ty))
            Start-Sleep -Milliseconds 400
            $c2 = Get-Crop $barX $ty 1 1
            $hot = Lum $c2.GetPixel(0, 0)
            $c2.Dispose()
            # Both samples are checked against black as well: a crop taken off the window
            # comes back black, and black differs from a dark theme's thumb by enough to
            # make a "it got darker" assertion pass on a sample that is not on screen.
            $onscreen = ($cold -gt 30) -and ($hot -gt 30)
            Check ($onscreen -and ($hot -lt $cold - 6)) 'hovering the thumb darkens it' `
                ($cold.ToString('0.0') + ' -> ' + $hot.ToString('0.0') + ' luminance')
        }

        # ---- F. the width cap and the two alignments ----
        Write-Output ''
        Write-Output '--- F. bubble width tracks the chat area, capped at 90% ---'
        $cap = Get-CapW $cw
        Write-Output ('  chat area ' + $cw + 'px wide, so a bubble may be ' + $cap + 'px')
        foreach ($r in @($b0)) { Write-Output ('  bubble  x=' + $r.Left + ' w=' + $r.W + ' h=' + $r.H) }

        # The two long messages wrap, so their bubble width is the cap minus whatever the
        # last word of the longest line did not fill; the short one is a single line and is
        # told apart by height rather than by position in the list.
        $longW = 0
        $shortW = 99999
        foreach ($r in @($b0)) {
            if ($r.H -lt 100) { if ($r.W -lt $shortW) { $shortW = $r.W } }
            elseif ($r.W -gt $longW) { $longW = $r.W }
        }
        Check ($longW -le $cap + 2) 'a wrapped bubble is not wider than the cap' ($longW.ToString() + ' vs ' + $cap)
        # 80 and not 40: greedy wrapping breaks before the word that would not fit, so the
        # widest physical line always ends one word short of the cap. The fixture's words
        # are two letters (~30px here) precisely so this stays tight -- it has to stay well
        # under the 289px a bubble WOULD be short by if it were stuck at its old width.
        Check ($longW -ge $cap - 80) 'and it really does fill the cap' `
            ((($cap - $longW)).ToString() + 'px short of it')
        Check ($shortW -lt $cap - 100) 'a short message stays its own width' ($shortW.ToString() + 'px')

        $maxRight = 0
        foreach ($r in @($b0)) { if ($r.Right -gt $maxRight) { $maxRight = $r.Right } }
        Check ($maxRight -le $chat.R.Right - 12) 'no bubble runs under the thumb column' `
            ($maxRight.ToString() + ' vs right edge ' + $chat.R.Right)

        $u0 = $b0[0]
        $a1 = $b0[1]
        Check ([Math]::Abs($u0.Right - ($chat.R.Right - 26)) -le 1) 'the user bubble hugs the right edge' `
            ('right ' + $u0.Right + ' vs ' + ($chat.R.Right - 26))
        Check ([Math]::Abs($a1.Left - ($chat.R.Left + 26)) -le 1) 'the assistant bubble hugs the left edge' `
            ('left ' + $a1.Left + ' vs ' + ($chat.R.Left + 26))

        # ---- G. collapsing the sidebar hands the bubbles the new width ----
        #
        # The window does not change size here -- only the room the chat area has inside it.
        # That makes this the cleanest evidence that the width follows the chat area rather
        # than the window: a bubble that stayed at its old width is one that was sized once.
        Write-Output ''
        Write-Output '--- G. the bubbles grow when the sidebar collapses ---'
        $tr = Get-SideToggle $main
        if ($null -eq $tr) { throw 'sidebar toggle not found' }
        Invoke-MouseClick ($tr.Left + 14) ($tr.Top + 14)
        Start-Sleep -Milliseconds 1200
        $chat2 = Get-ChatPanel $main
        if ($null -eq $chat2) { throw 'chat panel not found after collapse' }
        $cw2 = $chat2.R.Right - $chat2.R.Left
        $b2 = Get-Bubbles $chat2
        $long2 = 0
        foreach ($r in @($b2)) { if ($r.W -gt $long2) { $long2 = $r.W } }
        Check ($cw2 -gt $cw + 200) 'the chat area really did get wider' ($cw.ToString() + ' -> ' + $cw2.ToString())
        Check ($long2 -gt $longW + 150) 'and the long bubble grew with it' ($longW.ToString() + ' -> ' + $long2.ToString())
        Check ($long2 -le (Get-CapW $cw2) + 2) 'still under 90% of the wider area' `
            ($long2.ToString() + ' vs ' + (Get-CapW $cw2))
        Save-WindowShot $main (Get-ShotPath 'chat-area-3-collapsed.png')
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

Write-BBDone 'chat-area'
