# Measure the sidebar collapse/expand cost against the REAL conversation history.
#
# sidebar-perf.ps1 uses a synthetic fixture: 20 short messages. The reported symptom is
# "when the conversation gets long, collapsing the sidebar stutters", and the real history
# has a different shape -- few messages, but one reply of 14k characters. Shape matters,
# because the two candidate costs scale with different things:
#
#   * per MESSAGE  : SetMaxInner -> Rebuild -> Markdown.Measure for every row
#   * per CHARACTER: the wrap pass and its per-word TextRenderer.MeasureText calls
#
# So this probe runs the real files. It copies ONE conversation from dist\Release\chats
# into the DEBUG build's chats\ folder (the probe drives the Debug exe, and Debug/Release
# each keep their own history), names the file after the conversation id -- the app expects
# filename == id -- opens it, and measures.
#
# The Release originals are only ever READ. The Debug chats\ folder and settings.json are
# backed up and restored in the finally block.
#
# Nothing here prints conversation content. Only counts and timings -- this is the user's
# own chat history.
#
# Usage:  powershell -File tools\sidebar-perf-real.ps1 [-Pick 0]
#           -Pick is an index into the real conversations sorted by total characters,
#           descending. 0 = the heaviest.

param([int]$Pick = 0)

. "$PSScriptRoot\_ui.ps1"

$script:fail = 0

$repo = Split-Path $PSScriptRoot -Parent
$debugDir = Split-Path $script:BBExe -Parent
$releaseChats = Join-Path $repo 'dist\Release\chats'
$debugChats = Join-Path $debugDir 'chats'
$settings = Join-Path $debugDir 'settings.json'
$legacy = Join-Path $debugDir 'conversations.json'

# ---- pick a real conversation, by character volume ---------------------------------
if (-not (Test-Path $releaseChats)) { Write-Output ("no release history at " + $releaseChats); exit 1 }

$cands = @()
foreach ($f in Get-ChildItem $releaseChats -Filter *.json -File) {
    $j = $null
    try { $j = Get-Content -Raw $f.FullName | ConvertFrom-Json } catch { continue }
    if ($null -eq $j.Conversation) { continue }
    $chars = 0
    foreach ($m in $j.Conversation.Messages) {
        if ($m.Text) { $chars += $m.Text.Length }
        if ($m.Reasoning) { $chars += $m.Reasoning.Length }
    }
    $cands += [pscustomobject]@{
        Path = $f.FullName; Name = $f.Name; Chars = $chars; Msgs = $j.Conversation.Messages.Count
    }
}
if ($cands.Count -eq 0) { Write-Output 'no real conversations found'; exit 1 }
$cands = @($cands | Sort-Object -Property Chars -Descending)
if ($Pick -ge $cands.Count) { $Pick = $cands.Count - 1 }
$conv = $cands[$Pick]

Write-Output ('real conversations, heaviest first:')
foreach ($c in $cands) {
    Write-Output ('  ' + $c.Name.Substring(0, 8) + '  msgs=' + $c.Msgs + '  chars=' + $c.Chars)
}
Write-Output ''
Write-Output ('--- measuring pick ' + $Pick + ': msgs=' + $conv.Msgs + ' chars=' + $conv.Chars + ' ---')

# ---- back up the debug build's own state -------------------------------------------
$hadSettings = Test-Path $settings
$bakSettings = $null
if ($hadSettings) { $bakSettings = Get-Content $settings -Raw }
$hadLegacy = Test-Path $legacy
$bakLegacy = $null
if ($hadLegacy) { $bakLegacy = Get-Content $legacy -Raw }
$bakChats = @{}
if (Test-Path $debugChats) {
    foreach ($f in Get-ChildItem $debugChats -File) { $bakChats[$f.Name] = (Get-Content $f.FullName -Raw) }
    Remove-Item (Join-Path $debugChats '*') -Force
} else {
    [void](New-Item -ItemType Directory -Force $debugChats)
}
# An empty chats\ makes the app import conversations.json on startup, which would put an
# extra row above the one we want to click.
if (Test-Path $legacy) { Remove-Item $legacy -Force }

# The id must equal the filename, and the id inside the file must match, or the app will
# rewrite/rename it and the list row we click may not be this conversation.
$id = [System.IO.Path]::GetFileNameWithoutExtension($conv.Name)

# Invoke-BBProbe runs the body in a child scope; plain variables from here are readable but
# an assignment inside would silently create a local. Hoist everything the body touches.
$script:convPath = $conv.Path
$script:convMsgs = [int]$conv.Msgs
$script:convChars = [int]$conv.Chars
$script:convId = $id
$script:debugChats = $debugChats
$script:pick = $Pick

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

function Invoke-Anim($main, [string]$Tag) {
    $tr = Get-SideToggle $main
    if ($null -eq $tr) { Write-Output ('  ' + $Tag + ' FAIL -- no toggle button'); $script:fail++; return $null }
    Remove-Item $script:BBRows -ErrorAction SilentlyContinue
    Invoke-MouseClick ($tr.Left + 14) ($tr.Top + 14)
    Start-Sleep -Milliseconds 1500
    $ui = Get-UiRows
    if ($null -eq $ui) { Write-Output ('  ' + $Tag + ' FAIL -- no ui-rows.json'); $script:fail++; return $null }
    return $ui
}

try {
    Invoke-BBProbe {
        Copy-Item $script:convPath (Join-Path $script:debugChats ($script:convId + '.json')) -Force
        Remove-Item $script:BBRows -ErrorAction SilentlyContinue

        $main = Start-BangGang
        $mr = Get-WinRect $main
        Write-Output ('window ' + $mr.Left + ',' + $mr.Top + ' ' + ($mr.Right - $mr.Left) + 'x' + ($mr.Bottom - $mr.Top))

        $list = $null
        if (-not (Open-ConvRow $main 0)) { throw 'conversation list not found' }        # Open-ConvRow waits 900ms, which is enough for a short conversation. A 14k-char
        # reply takes longer to measure and paint the first time, so wait for the row count
        # to arrive rather than guessing a duration.
        for ($t = 0; $t -lt 40; $t++) {
            Start-Sleep -Milliseconds 250
            $ui = Get-UiRows
            if ($null -ne $ui) {
                if (@(Get-UiRowList $ui).Count -ge $script:convMsgs) { break }
            }
        }
        $ui = Get-UiRows
        if ($null -eq $ui) { throw 'no ui-rows.json after opening the conversation' }
        $rows = @(Get-UiRowList $ui)
        Write-Output ('  rows rendered ' + $rows.Count + ' (conversation has ' + $script:convMsgs + ' messages)')
        if ($rows.Count -ne $script:convMsgs) {
            Write-Output '  FAIL -- row count does not match the message count; the opened conversation is not the one measured'
            $script:fail++
        }

        Write-Output ''
        Write-Output '--- collapse ---'
        $w0 = Get-SidebarW $main
        $uiC = Invoke-Anim $main 'collapse'
        $w1 = Get-SidebarW $main
        Write-Output ('  sidebar ' + $w0 + ' -> ' + $w1)
        if ($w0 -ne 256 -or $w1 -ne 0) { Write-Output '  FAIL -- click did not collapse'; $script:fail++; $uiC = $null }

        Write-Output ''
        Write-Output '--- expand ---'
        $uiE = Invoke-Anim $main 'expand'
        $w2 = Get-SidebarW $main
        Write-Output ('  sidebar ' + $w1 + ' -> ' + $w2)
        if ($w2 -ne 256) { Write-Output '  FAIL -- click did not expand'; $script:fail++; $uiE = $null }

        Write-Output ''
        Write-Output '--- frame timings ---'
        Write-Output ('  collapse  animMs=' + $uiC.animMs + '  frames=' + $uiC.animFrames +
                      '  paint avg/max=' + $uiC.animPaintMs + '/' + $uiC.animPaintMaxMs +
                      '  layout avg/max=' + $uiC.animLayoutMs + '/' + $uiC.animLayoutMaxMs)
        Write-Output ('  expand    animMs=' + $uiE.animMs + '  frames=' + $uiE.animFrames +
                      '  paint avg/max=' + $uiE.animPaintMs + '/' + $uiE.animPaintMaxMs +
                      '  layout avg/max=' + $uiE.animLayoutMs + '/' + $uiE.animLayoutMaxMs)
        Write-Output '  control   idle bucket -- this is where the settle re-wrap lands:'
        Write-Output ('            layout avg=' + $uiE.idleLayoutMs + 'ms' +
                      '  paint avg/max=' + $uiE.idlePaintMs + '/' + $uiE.idlePaintMaxMs +
                      '  frames=' + $uiE.idleFrames)
        [void](Save-WindowShot $main (Get-ShotPath ('sidebar-perf-real-' + $script:pick + '.png')))

        # The settle re-wrap is O(messages) and lands in the idle bucket. The animation
        # frames themselves are a separate budget, reported above.
        Write-Output ''
        Write-Output '--- checks ---'
        $maxAnim = [Math]::Max([double]$uiC.animMs, [double]$uiE.animMs)
        Write-Output ('  animation wall clock, worst ' + [Math]::Round($maxAnim, 1) + 'ms')
        Write-Output ('  worst idle paint frame ' + $uiE.idlePaintMaxMs + 'ms')
        Write-Output ('  settle re-wrap, average ' + $uiE.idleLayoutMs + 'ms per layout call over ' +
                      $script:convMsgs + ' messages / ' + $script:convChars + ' chars')

        # The verdict this probe exists for. The animation frames are each well under a
        # frame budget; what the user feels is the single re-wrap at settle, and it is the
        # thing that grows with the conversation. 100ms of blocked message loop is the point
        # where a click visibly stops responding.
        Write-Output ''
        if ($script:fail -eq 0 -and [double]$uiE.idleLayoutMs -lt 100) {
            Write-Output 'PASS: this conversation settles inside the animation budget.'
        } elseif ($script:fail -eq 0) {
            Write-Output ('MEASURED: no assertion broke, but the settle re-wrap blocks the UI for ' +
                          $uiE.idleLayoutMs + 'ms on this conversation. That is the hitch.')
        } else {
            Write-Output ('FAIL: ' + $script:fail + ' check(s) broke -- the timing above is not a trustworthy measurement.')
        }
    }
} finally {
    if ($hadSettings) { Set-Content -Path $settings -Value $bakSettings -Encoding utf8 -NoNewline }
    else { Remove-Item $settings -ErrorAction SilentlyContinue }
    if ($hadLegacy) { Set-Content -Path $legacy -Value $bakLegacy -Encoding utf8 -NoNewline }
    else { Remove-Item $legacy -ErrorAction SilentlyContinue }
    if (Test-Path $debugChats) {
        foreach ($f in Get-ChildItem $debugChats -File) {
            if (-not $bakChats.ContainsKey($f.Name)) { Remove-Item $f.FullName -Force }
        }
    }
    foreach ($n in $bakChats.Keys) {
        Set-Content -Path (Join-Path $debugChats $n) -Value $bakChats[$n] -Encoding utf8 -NoNewline
    }
}

Write-BBDone 'sidebar-perf-real'
