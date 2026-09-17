# Item 4: is collapsing / expanding the sidebar with a full conversation actually fast now?
#
# The complaint was "the animation stutters and the UI tears when the chat area has content
# in it, and it is the RESIZE that does it". The cause was architectural: every bubble was a
# real child HWND, so every one of the ~15 animation frames moved N child windows (not part
# of the parent's double buffer -> tearing) and re-wrapped every message (-> stutter).
#
# 0.9.0 made ChatView the only painter and split layout in two:
#
#   ReflowRows()  re-measures every bubble -- expensive, and now runs ONCE, at the end
#   PlaceRows()   only repositions rows   -- microseconds, and this is what each frame runs
#
# This probe is the only quantitative evidence for that claim. It opens a conversation heavy
# enough to matter, runs a collapse and an expand, and reads the frame timings ChatView
# writes into ui-rows.json (animMs / animFrames / animPaintMs / animPaintMaxMs / animLayoutMs
# / animLayoutMaxMs, plus the non-animation idle* pair as the control group).
#
# Why each assertion can fail:
#
#   * the sidebar width is read before and after each click. "The animation was fast" and
#     "the animation never happened" look identical in the timings -- animMs is the wall
#     clock of the LAST animation, and a stale one from startup reads just as small.
#   * animLayoutMs is the per-frame LAYOUT cost. This is the number the fix targets: if a
#     frame went back to re-wrapping every message it would be milliseconds, not the tens of
#     microseconds a position pass costs. A probe that only looked at paint time would miss
#     that regression entirely.
#   * animPaintMaxMs is the WORST frame, not the average. An animation that is smooth except
#     for one 300ms hitch stutters for the user and has a fine average.
#   * the idle* numbers are printed alongside as the control group: the one-off re-wrap and
#     re-render after the animation (and the initial load) are paid there. Without them,
#     "the animation is fast" could just mean "this machine is fast" -- and, worse, a run
#     where the expensive work had silently moved INTO the animation would look identical.
#
# The fixture is 20 messages with a paragraph, a code block and a table each, written into
# chats\ and removed afterwards, along with settings.json and conversations.json.
#
# Usage:  powershell -File tools\sidebar-perf.ps1

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
# An empty chats\ makes the app import the single-file store on startup, which would add a
# second conversation to the list and put the fixture on the wrong row.
if (Test-Path $legacy) { Remove-Item $legacy -Force }

$script:ConvId = 'sidebarperf00000000000000000000001'
$script:Tick = [char]96 + [char]96 + [char]96      # three backticks; single-quoted so literal

function New-AssistantText([int]$n) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('### section ' + $n)
    [void]$sb.Append("`n`n")
    [void]$sb.Append('A paragraph long enough to wrap several times inside the bubble, so ')
    [void]$sb.Append('that every row in this conversation is a real measurement and not a ')
    [void]$sb.Append('single-line stamp that would render in no time at all.')
    [void]$sb.Append("`n`n")
    [void]$sb.Append($script:Tick + 'csharp' + "`n")
    [void]$sb.Append('public static int Add' + $n + '(int a, int b)' + "`n")
    [void]$sb.Append('{' + "`n")
    [void]$sb.Append('    var s = "row ' + $n + '";' + "`n")
    [void]$sb.Append('    return a + b;' + "`n")
    [void]$sb.Append('}' + "`n")
    [void]$sb.Append($script:Tick + "`n`n")
    [void]$sb.Append('| key | value | note |' + "`n")
    [void]$sb.Append('|:----|:-----:|-----:|' + "`n")
    [void]$sb.Append('| alpha' + $n + ' | 1 | first |' + "`n")
    [void]$sb.Append('| beta' + $n + ' | 2 | second |' + "`n")
    [void]$sb.Append("`n")
    [void]$sb.Append('Closing line.')
    return $sb.ToString()
}

function New-Body([string]$role, [string]$text) {
    return @{
        Role = $role; When = (Get-Date).ToString('o'); Text = $text
        Reasoning = ''; ReasoningMs = 0; Warning = ''; Attachments = @()
    }
}

function Write-Fixture {
    if (-not (Test-Path $chatsDir)) { [void](New-Item -ItemType Directory -Force $chatsDir) }
    $now = (Get-Date).ToString('o')
    $msgs = @()
    for ($i = 1; $i -le 10; $i++) {
        $msgs += New-Body 'user' ('question ' + $i + ' about the thing that was asked')
        $msgs += New-Body 'assistant' (New-AssistantText $i)
    }
    # ChatFile envelope, not a bare Conversation -- see the note in markdown-render.ps1.
    $file = @{
        Version = 1
        Conversation = @{
            Id = $script:ConvId; Title = 'sidebar-perf'; CreatedAt = $now; UpdatedAt = $now
            Messages = $msgs
        }
    }
    $json = $file | ConvertTo-Json -Depth 8
    Set-Content -Path (Join-Path $chatsDir ($script:ConvId + '.json')) -Value $json -Encoding utf8
}

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

function Get-SidebarW($main) {
    $mr = Get-WinRect $main
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Left -ne $mr.Left) { continue }
        if ($r.Top -ne ($mr.Top + 38)) { continue }
        if (($r.Bottom - $r.Top) -lt 200) { continue }
        if (($r.Right - $r.Left) -gt 400) { continue }
        return ($r.Right - $r.Left)
    }
    return 0
}

# One collapse or expand: click, wait for the animation to settle, report the timings.
#
# The wait is 1200ms rather than the ~270ms the animation needs, because the readings are
# only final once the settle re-wrap has Dumped again. Reading early would compare a run
# against a half-finished one, and the numbers would differ per run for no real reason.
function Invoke-Anim($main, [string]$Tag) {
    $tr = Get-SideToggle $main
    if ($null -eq $tr) { Write-Output ("  " + $Tag + ' FAIL -- the toggle button is not there'); $script:fail++; return $null }
    Remove-Item $script:BBRows -ErrorAction SilentlyContinue
    Invoke-MouseClick ($tr.Left + 14) ($tr.Top + 14)
    Start-Sleep -Milliseconds 1200
    $ui = Get-UiRows
    if ($null -eq $ui) { Write-Output ("  " + $Tag + ' FAIL -- no ui-rows.json'); $script:fail++; return $null }
    return $ui
}

try {
    Invoke-BBProbe {
        Remove-Item $script:BBRows -ErrorAction SilentlyContinue
        Write-Fixture

        $main = Start-BangGang
        $mr = Get-WinRect $main
        Write-Output ('window ' + $mr.Left + ',' + $mr.Top + ' ' + ($mr.Right - $mr.Left) + 'x' + ($mr.Bottom - $mr.Top))
        Write-Output ''

        # Open the conversation: the list is the tall narrow child flush with the window's
        # left edge. Same lookup the other probes use.
        $list = $null
        foreach ($h in Get-WinKids $main) {
            $r = Get-WinRect $h
            if ($r.Left -ne $mr.Left) { continue }
            if (($r.Right - $r.Left) -ge 320) { continue }
            if (($r.Bottom - $r.Top) -lt 100) { continue }
            if ($r.Top -le $mr.Top + 38) { continue }
            if ($null -eq $list) { $list = $r }
        }
        if ($null -eq $list) { throw 'conversation list not found' }

        Write-Output '--- opening the 20-message conversation ---'
        Invoke-MouseClick ([int]($list.Left + ($list.Right - $list.Left) / 2)) ($list.Top + 19)
        Start-Sleep -Milliseconds 1500
        $ui = Get-UiRows
        $rows = @(Get-UiRowList $ui)
        Write-Output ('  rows rendered ' + $rows.Count)
        if ($rows.Count -ne 20) {
            Write-Output ('  FAIL -- ' + $rows.Count + ' rows, expected 20; the timings below would be for a lighter screen than intended')
            $script:fail++
        }
        else { Write-Output '  OK   20 rows, heavy enough to be worth measuring' }

        # ---- collapse --------------------------------------------------------
        Write-Output ''
        Write-Output '--- collapse ---'
        $w0 = Get-SidebarW $main
        $uiC = Invoke-Anim $main 'collapse'
        $w1 = Get-SidebarW $main
        Write-Output ('  sidebar ' + $w0 + ' -> ' + $w1)
        if ($w0 -ne 256 -or $w1 -ne 0) {
            Write-Output '  FAIL -- the click did not collapse the sidebar; the timings below are from some other animation'
            $script:fail++
            $uiC = $null
        }

        # ---- expand ----------------------------------------------------------
        Write-Output ''
        Write-Output '--- expand ---'
        $uiE = Invoke-Anim $main 'expand'
        $w2 = Get-SidebarW $main
        Write-Output ('  sidebar ' + $w1 + ' -> ' + $w2)
        if ($w2 -ne 256) {
            Write-Output '  FAIL -- the click did not expand the sidebar; the timings below are from some other animation'
            $script:fail++
            $uiE = $null
        }

        # ---- the numbers -----------------------------------------------------
        #
        # Everything printed here is spelled as ONE parenthesised expression. `Write-Output 'a'
        # + $b` parses as three separate arguments -- the string, the literal '+', and the
        # value -- and Write-Output happily prints them on nine separate lines.
        Write-Output ''
        Write-Output '--- frame timings ---'
        Write-Output ('  collapse  animMs=' + $uiC.animMs + '  frames=' + $uiC.animFrames +
                      '  paint avg/max=' + $uiC.animPaintMs + '/' + $uiC.animPaintMaxMs +
                      '  layout avg/max=' + $uiC.animLayoutMs + '/' + $uiC.animLayoutMaxMs)
        if ($null -ne $uiE) {
            Write-Output ('  expand    animMs=' + $uiE.animMs + '  frames=' + $uiE.animFrames +
                          '  paint avg/max=' + $uiE.animPaintMs + '/' + $uiE.animPaintMaxMs +
                          '  layout avg/max=' + $uiE.animLayoutMs + '/' + $uiE.animLayoutMaxMs)
            Write-Output '  control   idle, whole run, includes the one-off re-wrap at settle:'
            Write-Output ('            paint avg/max=' + $uiE.idlePaintMs + '/' + $uiE.idlePaintMaxMs +
                          '  layout avg=' + $uiE.idleLayoutMs + '  frames=' + $uiE.idleFrames)
        }
        [void](Save-WindowShot $main (Get-ShotPath 'sidebar-perf.png'))

        if ($null -eq $uiC -or $null -eq $uiE) {
            Write-Output ''
            Write-Output 'FAIL: the animation could not be measured.'
            return
        }

        Write-Output ''
        Write-Output '--- checks ---'

        # Frames actually happened. A run where the animation never started also reports a
        # small animMs, and that is the shape a silently broken probe takes.
        $minFrames = 10
        foreach ($pair in @(@('collapse', $uiC), @('expand', $uiE))) {
            $tag = $pair[0]; $u = $pair[1]
            if ($u.animFrames -lt $minFrames) {
                Write-Output ('  FAIL -- ' + $tag + ' painted ' + $u.animFrames + ' frames, expected at least ' + $minFrames)
                $script:fail++
            }
        }

        # Per-frame LAYOUT. This is the number the fix targets: a frame that re-wraps every
        # message is milliseconds; a frame that only repositions rows is microseconds.
        $maxLayout = [Math]::Max([double]$uiC.animLayoutMs, [double]$uiE.animLayoutMs)
        $maxLayoutPeak = [Math]::Max([double]$uiC.animLayoutMaxMs, [double]$uiE.animLayoutMaxMs)
        Write-Output ('  per-frame layout avg worst ' + $maxLayout + 'ms, worst single ' + $maxLayoutPeak + 'ms')
        if ($maxLayout -gt 2.0 -or $maxLayoutPeak -gt 8.0) {
            Write-Output '  FAIL -- a frame is re-wrapping the messages instead of only repositioning rows'
            $script:fail++
        }
        else { Write-Output '  OK   frames only reposition rows' }

        # The WORST frame, not the average: one 200ms hitch is a visible stutter and barely
        # moves an average over ~15 frames.
        $maxPaintPeak = [Math]::Max([double]$uiC.animPaintMaxMs, [double]$uiE.animPaintMaxMs)
        Write-Output ('  worst single frame paint ' + $maxPaintPeak + 'ms')
        if ($maxPaintPeak -gt 40.0) {
            Write-Output '  FAIL -- one animation frame took long enough to be a visible hitch'
            $script:fail++
        }
        else { Write-Output '  OK   no frame is a hitch' }

        # Whole-animation wall clock. The tick interval is 18ms but a WinForms Timer cannot
        # resolve finer than the system tick (~15.6ms), so a tick really lands every ~31ms and
        # 14 frames come to ~430ms. That is the floor, and it is what this run measures. The
        # ceiling is therefore about twice the floor: past that, WM_TIMER is coalescing, which
        # is what "the animation stutters" looks like from inside the message loop.
        $maxAnim = [Math]::Max([double]$uiC.animMs, [double]$uiE.animMs)
        Write-Output ('  animation wall clock, worst ' + [Math]::Round($maxAnim, 1) + 'ms')
        if ($maxAnim -gt 900.0) {
            Write-Output '  FAIL -- the animation took far longer than its 14 ticks; the loop was too busy to keep up'
            $script:fail++
        }
        else { Write-Output '  OK   the animation finishes in about its nominal time' }

        Write-Output ''
        if ($script:fail -eq 0) { Write-Output 'PASS: the sidebar animation repositions rows, it does not re-wrap them.' }
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

Write-BBDone 'sidebar-perf'
