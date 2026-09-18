# Items 2+3: a code block / table whose TOP has scrolled out of the visible band must
# keep its panel / rules in the part still on screen.
#
# Root cause being guarded: the whole-block decors (code panel fill, table header band,
# row separators, vertical rules, quote panel) hang on the block's FIRST line, while
# Markdown.DrawBack culls PER LINE (CullMargin is only 40px). Scroll the first line past
# the band and the decor of the whole block vanishes even though most of the block is
# still visible -- "the code block lost its background", "the table lost its rules".
#
# The fixture is one tall assistant message: intro line, a 70-line code block, a 60-row
# table, outro. Both blocks are taller than the viewport, so the probe can park the
# scroll offset in the MIDDLE of each and ask "is the decor still painted there".
#
# Everything is sampled RELATIVE to colours learned from the screen itself (the
# draft-strip.ps1 discipline: never absolute colours, themes change them):
#
#   learn   -- at offset 0 (the probe scrolls back UP first: ChatView.Load pins the
#              scroll to the BOTTOM, so the top of the conversation is off-screen at
#              open, and a background sample taken there reads pure black and makes
#              every later "differs from background" test trivially true -- that is
#              how an earlier version of this probe passed against the UNFIXED build).
#              Learned at the top: the bubble background (top strip, right of the name)
#              and the code panel colour (majority of a crop parked inside the code
#              block, whose first line is still on screen there, so the panel is
#              painted with AND without the fix).
#   code    -- parked ~500px into the bubble, the band's majority colour must BE the
#              learned panel colour. With the bug the panel is gone and the majority
#              falls back to the bubble background.
#   table   -- parked ~2100px into the bubble, there must be >= 2 columns whose pixels
#              differ from the bubble background at ALL 5 sample rows spread over the
#              band: those are the vertical rules. Presence-at-every-row, NOT colour
#              constancy: a rule is a 1px FillRectangle that can land on a half-pixel
#              boundary and blend 50/50 with the background, and where a horizontal
#              separator crosses it the shade changes again -- so a constancy test
#              rejects real rules, while "differs from the background at every one of
#              5 rows 100px apart" is something a glyph column cannot do (the 5 sample
#              ys cannot all land inside the ~12px text band of their table rows).
#
# Guards, so a green run cannot be a scrolled-to-the-wrong-place / occluded-window
# artefact:
#   - the learned background must not be near-black (pure black = the sample landed
#     off-window or under an occluding window; with bg=black every diff test passes
#     vacuously);
#   - the learned panel colour must differ from the background by a real margin,
#     otherwise the whole code test cannot tell anything and must say so loudly;
#   - text pixels must be present in both measured bands (glyphs ARE painted even
#     with the bug, so a band without ink means the probe is lost, not the app);
#   - near-black pixels must stay rare inside the band (a window covering the app
#     shows up as a black share spike);
#   - the scroll must actually reach each target, checked against ui-rows.json.
#
# Usage:  powershell -File tools\code-table-cull.ps1

. "$PSScriptRoot\_ui.ps1"

$script:fail = 0

# --- the fixture conversation (same backup/restore discipline as sidebar-perf.ps1) ---
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

$script:ConvId = 'codetablecull00000000000000000001'
$script:Tick = [char]96 + [char]96 + [char]96      # three backticks; single-quoted so literal

function New-AssistantText {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('Here is the code and the table you asked for.')
    [void]$sb.Append("`n`n")
    [void]$sb.Append($script:Tick + 'csharp' + "`n")
    for ($i = 1; $i -le 70; $i++) {
        [void]$sb.Append('var v' + $i + ' = "code line ' + $i + ' alpha beta gamma";' + "`n")
    }
    [void]$sb.Append($script:Tick + "`n`n")
    [void]$sb.Append('| col alpha | col beta | col gamma | col delta |' + "`n")
    [void]$sb.Append('|:----------|:---------|:----------|:----------|' + "`n")
    for ($i = 1; $i -le 60; $i++) {
        [void]$sb.Append('| row ' + $i + ' a | row ' + $i + ' b | row ' + $i + ' c | row ' + $i + ' d |' + "`n")
    }
    [void]$sb.Append("`n")
    [void]$sb.Append('Done.')
    return $sb.ToString()
}

function Write-Fixture {
    if (-not (Test-Path $chatsDir)) { [void](New-Item -ItemType Directory -Force $chatsDir) }
    $now = (Get-Date).ToString('o')
    $msgs = @(
        @{ Role = 'user'; When = $now; Text = 'show me the code and the table please'
           Reasoning = ''; ReasoningMs = 0; Warning = ''; Attachments = @() },
        @{ Role = 'assistant'; When = $now; Text = (New-AssistantText)
           Reasoning = ''; ReasoningMs = 0; Warning = ''; Attachments = @() }
    )
    # ChatFile envelope, not a bare Conversation -- see the note in markdown-render.ps1.
    $file = @{
        Version = 1
        Conversation = @{
            Id = $script:ConvId; Title = 'code-table-cull'; CreatedAt = $now; UpdatedAt = $now
            Messages = $msgs
        }
    }
    $json = $file | ConvertTo-Json -Depth 8
    Set-Content -Path (Join-Path $chatsDir ($script:ConvId + '.json')) -Value $json -Encoding utf8
}

# Sum-of-channel-diffs between a Color and an (r,g,b) triple.
function Px-Diff($c, $bg) {
    return ([Math]::Abs([int]$c.R - $bg[0]) + [Math]::Abs([int]$c.G - $bg[1]) + [Math]::Abs([int]$c.B - $bg[2]))
}

# Wheel to an absolute document offset, in EITHER direction. ChatView.Load pins the
# scroll to the bottom, and a down-only wheel can never come back -- the earlier
# version of this probe measured everything at MaxOffset while believing it was
# parked inside the code block. Stops when within 100px of the target (one wheel
# burst is 4 notches x 54px = 216px, so tighter is an oscillation), or when the
# offset stops moving (an end stop).
#
# NOTE: the returned snapshot's rows are the only ones safe to use afterwards.
# row.y is VIEWPORT-relative -- it changes with every scroll -- so a $row captured
# before a Scroll-To describes where the bubble USED to be. Reading pixels at the
# stale rectangle lands off-screen, which reads as pure black, which reads as
# "the app lost its background". Re-fetch the row from the fresh snapshot.
function Scroll-To($ui, [int]$target) {
    $cx = [int]([int]$ui.viewX + [int]$ui.clientW / 2)
    $cy = [int]([int]$ui.viewY + [int]$ui.clientH / 2)
    $last = -1
    for ($i = 0; $i -lt 120; $i++) {
        $ui = Get-UiRows
        $off = [int]$ui.offset
        if ([Math]::Abs($off - $target) -le 100) { break }
        if ($off -eq $last) { break }          # cannot scroll further
        $last = $off
        if ($off -lt $target) { Invoke-WheelAt $cx $cy -4 } else { Invoke-WheelAt $cx $cy 4 }
    }
    Start-Sleep -Milliseconds 400              # let the paint after the last scroll land
    return (Get-UiRows)
}

# The bubble's background colour, learned from the top strip of the assistant bubble.
# The name sits top-LEFT and the right edge is where an occluding window shows up
# first, so the samples live at 55/65/75/85 percent of the width. Returns $null when
# the samples disagree -- better loud than a probe measuring against a colour that
# is not the background at all.
function Get-BubbleBg($ui, $row) {
    $rs = Get-UiRowScreen $ui $row
    # Inside an array literal the comma binds TIGHTER than the minus, so
    # @($rs.W - 16, 14) is "$rs.W minus the array (16,14)" -- compute first.
    $w1 = [int]($rs.W * 0.55)
    $w2 = [int]($rs.W * 0.65)
    $w3 = [int]($rs.W * 0.75)
    $w4 = [int]($rs.W * 0.85)
    $samples = @()
    foreach ($f in @(@($w1, 14), @($w2, 26), @($w3, 14), @($w4, 26))) {
        $bmp = Get-Crop ($rs.L + $f[0]) ($rs.T + $f[1]) 1 1
        $c = $bmp.GetPixel(0, 0)
        $bmp.Dispose()
        $samples += ,@([int]$c.R, [int]$c.G, [int]$c.B)
    }
    foreach ($s in $samples) {
        $agree = 0
        foreach ($o in $samples) {
            $d = [Math]::Abs($s[0] - $o[0]) + [Math]::Abs($s[1] - $o[1]) + [Math]::Abs($s[2] - $o[2])
            if ($d -le 12) { $agree++ }
        }
        if ($agree -ge 3) { return $s }
    }
    return $null
}

# The code panel colour: the majority colour of a crop parked safely inside the code
# block (300..560px into the bubble; the block starts around +90). Only valid at
# scroll offset 0, where the block's first line is still on screen and the panel is
# painted regardless of the bug. Returns $null if the crop would not fit.
function Get-CodePanelColor($ui, $row) {
    $rs = Get-UiRowScreen $ui $row
    $y0 = $rs.T + 300
    $y1 = [Math]::Min($rs.T + 560, [int]$ui.viewY + [int]$ui.clientH - 10)
    if ($y1 - $y0 -lt 100) { return $null }
    $x0 = $rs.L + 30
    $x1 = $rs.L + $rs.W - 30
    $bmp = Get-Crop $x0 $y0 ($x1 - $x0) ($y1 - $y0)
    $hist = @{}
    for ($y = 0; $y -lt $bmp.Height; $y += 2) {
        for ($x = 0; $x -lt $bmp.Width; $x += 2) {
            $c = $bmp.GetPixel($x, $y)
            $k = ([int]$c.R * 65536) + ([int]$c.G * 256) + [int]$c.B
            if ($hist.ContainsKey($k)) { $hist[$k]++ } else { $hist[$k] = 1 }
        }
    }
    $bmp.Dispose()
    $majK = -1; $majN = -1
    foreach ($k in $hist.Keys) { if ($hist[$k] -gt $majN) { $majN = $hist[$k]; $majK = $k } }
    if ($majK -lt 0) { return $null }
    $r = [int][Math]::Floor($majK / 65536)
    $g = [int][Math]::Floor(($majK - $r * 65536) / 256)
    $b = $majK - $r * 65536 - $g * 256
    return @($r, $g, $b)
}

# The rectangle where the bubble is actually on screen, as a crop the caller disposes.
function Get-BandCrop($ui, $row) {
    $rs = Get-UiRowScreen $ui $row
    $x0 = $rs.L + 6
    $x1 = $rs.L + $rs.W - 6
    $y0 = [Math]::Max([int]$ui.viewY + 4, $rs.T + 2)
    $y1 = [Math]::Min([int]$ui.viewY + [int]$ui.clientH - 4, $rs.T + $rs.H - 2)
    if ($y1 - $y0 -lt 100) { return $null }
    return (Get-Crop $x0 $y0 ($x1 - $x0) ($y1 - $y0))
}

# One pass over the band:
#   Maj      majority colour (step-2 histogram)
#   Ratio    share of sampled pixels differing from the bubble background
#   TextPx   sampled pixels differing from BOTH background and majority (glyph ink --
#            painted with and without the bug, so it is the "are we lost" control)
#   Black    share of near-black pixels (an occluding window / off-screen read spikes
#            this; no real surface in either theme is near-black)
#   Cols     columns whose pixels differ from the bubble background at ALL 5 sample
#            rows spread over the band -- the table's vertical rules. Scanned at
#            step 1 in x because a rule is 1px wide and a coarser step could skip it.
#            NOT a constancy test: the rule blends 50/50 with the background when it
#            lands on a half-pixel boundary, and separator crossings shift its shade
#            again, so the colour legitimately varies along the column (40..87 away
#            from the background; the cut is 20).
function Measure-Band($ui, $row, $bg) {
    $bmp = Get-BandCrop $ui $row
    if ($null -eq $bmp) { return $null }
    $hist = @{}
    for ($y = 0; $y -lt $bmp.Height; $y += 2) {
        for ($x = 0; $x -lt $bmp.Width; $x += 2) {
            $c = $bmp.GetPixel($x, $y)
            $k = ([int]$c.R * 65536) + ([int]$c.G * 256) + [int]$c.B
            if ($hist.ContainsKey($k)) { $hist[$k]++ } else { $hist[$k] = 1 }
        }
    }
    $majK = -1; $majN = -1
    foreach ($k in $hist.Keys) { if ($hist[$k] -gt $majN) { $majN = $hist[$k]; $majK = $k } }
    $majR = [int][Math]::Floor($majK / 65536)
    $majG = [int][Math]::Floor(($majK - $majR * 65536) / 256)
    $majB = $majK - $majR * 65536 - $majG * 256
    $maj = @($majR, $majG, $majB)

    $diffBg = 0; $textPx = 0; $black = 0; $total = 0
    for ($y = 0; $y -lt $bmp.Height; $y += 2) {
        for ($x = 0; $x -lt $bmp.Width; $x += 2) {
            $c = $bmp.GetPixel($x, $y)
            $total++
            if (([int]$c.R + [int]$c.G + [int]$c.B) -lt 30) { $black++ }
            $dBg = Px-Diff $c $bg
            if ($dBg -ge 36) { $diffBg++ }
            if ($dBg -ge 36 -and (Px-Diff $c $maj) -ge 36) { $textPx++ }
        }
    }

    $ys = @()
    foreach ($f in @(0.18, 0.34, 0.5, 0.66, 0.82)) { $ys += [int]($bmp.Height * $f) }
    $cols = 0
    for ($x = 2; $x -lt $bmp.Width - 2; $x++) {
        $ok = $true
        for ($i = 0; $i -lt 5; $i++) {
            if ((Px-Diff ($bmp.GetPixel($x, $ys[$i])) $bg) -lt 20) { $ok = $false; break }
        }
        if ($ok) { $cols++ }
    }
    $h = $bmp.Height
    $bmp.Dispose()
    return @{ H = $h; Maj = $maj; Ratio = ($diffBg / [double]$total); TextPx = $textPx
              Black = ($black / [double]$total); Cols = $cols }
}

try {
    Invoke-BBProbe {
        Remove-Item $script:BBRows -ErrorAction SilentlyContinue
        Write-Fixture

        $main = Start-BangGang
        Write-Output '--- opening the fixture conversation ---'
        if (-not (Open-ConvRow $main 0)) { throw 'conversation list not found' }

        $ui = $null
        for ($i = 0; $i -lt 20; $i++) {
            $ui = Get-UiRows
            if ($null -ne $ui -and @(Get-UiRowList $ui).Count -eq 2) { break }
            Start-Sleep -Milliseconds 300
        }
        if ($null -eq $ui) { throw 'no ui-rows.json' }
        $rows = @(Get-UiRowList $ui)
        Write-Output ('  rows rendered ' + $rows.Count)
        if ($rows.Count -ne 2) { throw ('expected 2 rows, got ' + $rows.Count) }
        $row = $rows[1]
        if ($row.role -ne 'assistant') { throw ('row 1 is ' + $row.role + ', expected assistant') }
        Write-Output ('  assistant row: h=' + $row.h + ' (tall enough to scroll inside: ' + $(if ([int]$row.h -gt 2400) { 'yes' } else { 'NO' }) + ')')
        if ([int]$row.h -lt 2400) { throw 'the fixture row is shorter than intended; the scroll targets below would land outside the blocks' }

        # ---- learn the colours at the TOP of the conversation ------------------
        #
        # Load() pins the scroll to the bottom; everything learned there is off-screen
        # black. Scroll back up first. The bubble top strip and the code block's top
        # are both on screen at offset 0.
        [void][BB]::BringWindowToTop($main)
        [void][BB]::SetForegroundWindow($main)
        Write-Output ''
        Write-Output '--- scrolling to the top to learn the colours ---'
        $ui = Scroll-To $ui 0
        Write-Output ('  offset ' + $ui.offset + ' (target 0)')
        if ([int]$ui.offset -gt 100) { throw 'could not scroll back to the top; anything learned now would be off-screen' }
        $row = @(Get-UiRowsOf $ui 'assistant')[0]     # row.y moves with every scroll -- re-fetch
        if ($null -eq $row) { throw 'assistant row missing after the scroll' }

        $bg = Get-BubbleBg $ui $row
        if ($null -eq $bg) {
            # One retry after re-raising the window: a sample that disagrees with
            # itself is usually something sitting on top of the app.
            [void][BB]::BringWindowToTop($main)
            [void][BB]::SetForegroundWindow($main)
            Start-Sleep -Milliseconds 400
            $bg = Get-BubbleBg $ui $row
        }
        if ($null -eq $bg) { throw 'could not learn the bubble background colour (samples disagree -- is something covering the window?)' }
        Write-Output ('  bubble background rgb(' + $bg[0] + ',' + $bg[1] + ',' + $bg[2] + ')')
        if (($bg[0] + $bg[1] + $bg[2]) -lt 30) {
            throw 'the bubble background reads as near-black -- the samples are landing off-window or under another window, and every diff test below would pass vacuously'
        }

        $panel = Get-CodePanelColor $ui $row
        if ($null -eq $panel) { throw 'could not learn the code panel colour (viewport too short?)' }
        Write-Output ('  code panel rgb(' + $panel[0] + ',' + $panel[1] + ',' + $panel[2] + ')')
        $panelDiff = [Math]::Abs($panel[0] - $bg[0]) + [Math]::Abs($panel[1] - $bg[1]) + [Math]::Abs($panel[2] - $bg[2])
        if ($panelDiff -lt 24) {
            throw ('code panel and bubble background are indistinguishable in this theme (diff ' + $panelDiff + '); the code test below could not tell anything')
        }

        $rowDocTop = [int]$row.y + [int]$ui.offset     # offset is ~0 here; row.y is document space

        # ---- code block: park the scroll ~500px into the bubble ----------------
        #
        # Layout of the row (approx): name ~26 + intro ~30 + spacing, so the code
        # block starts around +90 and its first line is culled once the offset is
        # past it by more than CullMargin (40). +500 is safely inside a ~1400px
        # block, with its top ~410px above the viewport.
        Write-Output ''
        Write-Output '--- code block, top scrolled out ---'
        $fail0 = $script:fail
        $ui = Scroll-To $ui ($rowDocTop + 500)
        Write-Output ('  offset ' + $ui.offset + ' (target ' + ($rowDocTop + 500) + ')')
        $row = @(Get-UiRowsOf $ui 'assistant')[0]     # re-fetch: row.y moved with the scroll
        if ([int]$ui.offset -lt $rowDocTop + 400) {
            Write-Output '  FAIL -- could not scroll into the code block; nothing below is measured where it claims to be'
            $script:fail++
        }
        else {
            $m = Measure-Band $ui $row $bg
            if ($null -eq $m) { Write-Output '  FAIL -- the bubble no longer intersects the viewport'; $script:fail++ }
            else {
                Write-Output ('  band h=' + $m.H + '  majority rgb(' + $m.Maj[0] + ',' + $m.Maj[1] + ',' + $m.Maj[2] + ')  vs-bg ratio=' + [Math]::Round($m.Ratio, 3) + '  text px=' + $m.TextPx + '  black share=' + [Math]::Round($m.Black, 4))
                if ($m.Black -gt 0.05) {
                    Write-Output '  FAIL -- a large black share in the band: something is covering the window (or the crop fell off-screen)'
                    $script:fail++
                }
                if ($m.TextPx -lt 300) {
                    Write-Output '  FAIL -- no text ink in the band; the probe is not looking at the code block at all'
                    $script:fail++
                }
                $majVsPanel = [Math]::Abs($m.Maj[0] - $panel[0]) + [Math]::Abs($m.Maj[1] - $panel[1]) + [Math]::Abs($m.Maj[2] - $panel[2])
                if ($majVsPanel -gt 18) {
                    Write-Output ('  FAIL -- the code panel background is GONE in the visible part (band majority is ' + $majVsPanel + ' away from the learned panel colour)')
                    $script:fail++
                }
                elseif ($script:fail -eq $fail0) { Write-Output '  OK   the code panel survives its first line scrolling out' }
            }
        }

        # ---- table: park the scroll ~2100px into the bubble ---------------------
        #
        # The table starts right after the ~1500px of intro+code; +2100 sits ~580px
        # below the table top with ~1200px of table still below, so the whole
        # viewport is inside the table body while its header row (carrying every
        # rule decor) is far above the band.
        Write-Output ''
        Write-Output '--- table, top scrolled out ---'
        $fail0 = $script:fail
        $ui = Scroll-To $ui ($rowDocTop + 2100)
        Write-Output ('  offset ' + $ui.offset + ' (target ' + ($rowDocTop + 2100) + ')')
        $row = @(Get-UiRowsOf $ui 'assistant')[0]     # re-fetch: row.y moved with the scroll
        if ([int]$ui.offset -lt $rowDocTop + 1900) {
            Write-Output '  FAIL -- could not scroll into the table; nothing below is measured where it claims to be'
            $script:fail++
        }
        else {
            $m = Measure-Band $ui $row $bg
            if ($null -eq $m) { Write-Output '  FAIL -- the bubble no longer intersects the viewport'; $script:fail++ }
            else {
                Write-Output ('  band h=' + $m.H + '  text px=' + $m.TextPx + '  black share=' + [Math]::Round($m.Black, 4) + '  constant non-bg columns=' + $m.Cols)
                if ($m.Black -gt 0.05) {
                    Write-Output '  FAIL -- a large black share in the band: something is covering the window (or the crop fell off-screen)'
                    $script:fail++
                }
                if ($m.TextPx -lt 300) {
                    Write-Output '  FAIL -- no cell text in the band; the probe is not looking at the table at all'
                    $script:fail++
                }
                if ($m.Cols -lt 2) {
                    Write-Output '  FAIL -- the table rules are GONE in the visible part (no vertical rule survives the header scrolling out)'
                    $script:fail++
                }
                elseif ($script:fail -eq $fail0) { Write-Output '  OK   the table rules survive the header scrolling out' }
            }
        }

        [void](Save-WindowShot $main (Get-ShotPath 'code-table-cull.png'))

        Write-Output ''
        if ($script:fail -eq 0) { Write-Output 'PASS: block-wide decors survive their first line leaving the visible band.' }
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

Write-BBDone 'code-table-cull'
