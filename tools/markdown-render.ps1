# Renders the full-feature markdown sample and measures what came out.
#
# 0.9.0 rewrote the markdown renderer (the old one had no tables, quotes, links, nested
# lists, rules or syntax-highlighted code blocks) and moved the bubbles from "one child
# HWND each" to "ChatView paints them itself". Those two changes are exactly the kind that
# compile fine while rendering nothing, so this probe renders a document that exercises
# EVERY block and inline feature and then looks at the pixels.
#
# The document lives in tools/fixtures/markdown-sample.md -- it is UTF-8 with real Chinese
# in it, which a .ps1 in this repo cannot be (see the ASCII rule in CLAUDE.md). The probe
# reads the file and wraps it into a conversation under chats\.
#
# What it asserts, and why each one can actually fail:
#
#   A  the app starts on the welcome page, and no rows exist yet. This is the item-3
#      behaviour AND the calibration for the next step: without it, "read ui-rows.json and
#      found 2 rows" could be reading a snapshot from a previous run of this probe.
#   B  clicking the conversation in the sidebar opens it: 2 rows, user on top.
#   C  the assistant bubble is tall enough to hold the whole document. A renderer that
#      silently dropped the code blocks and the table would still produce a bubble here,
#      just a short one.
#   D  the table's row count is honoured. Two fixtures differ ONLY in how many body rows
#      the table has; the bubble has to grow by that many rows. This is the honest version
#      of "the table has as many rows as the source" -- counting grid rules in pixels would
#      be measuring one specific drawing of a table, this measures the layout contract.
#   E  there are backgrounded BLOCKS (>= 4). A code block, a quote and a table header are
#      the only things that fill a full-width band behind their text. Measured as: a
#      scanline is a "band row" when most of its pixels differ from the bubble's own
#      background. Text lines never qualify (ink is a minority of the row), so this cannot
#      be satisfied by "there is some text here".
#
# Screenshots land in shoots\markdown-*.png for the eye -- geometry that passes can still
# be ugly, and "ugly" is half of what this item was about.
#
# Usage:  powershell -File tools\markdown-render.ps1

. "$PSScriptRoot\_ui.ps1"

$script:fail = 0

# --- the fixture document -----------------------------------------------------
$samplePath = Join-Path $PSScriptRoot 'fixtures\markdown-sample.md'
if (-not (Test-Path $samplePath)) { throw "fixture missing: $samplePath" }
$script:Sample = Get-Content $samplePath -Raw -Encoding UTF8

# A much shorter document with the same block types in it. The band scan below reads
# pixels, so it needs the whole bubble to fit inside the viewport; the full sample is
# ~1100px tall and the viewport is ~560.
$panelsPath = Join-Path $PSScriptRoot 'fixtures\markdown-panels.md'
if (-not (Test-Path $panelsPath)) { throw "fixture missing: $panelsPath" }
$script:Panels = Get-Content $panelsPath -Raw -Encoding UTF8

# The same document with the table's body grown by $Extra rows. Everything else is
# byte-identical, so any height difference is the table and nothing else.
#
# The marker is spelled in code points because this file has to be pure ASCII (CLAUDE.md)
# while the fixture is real UTF-8. It has to match the marker row EXACTLY -- a missing
# character turns this into "the fixture is not what I think it is", which is worth a
# throw rather than a silent "the table did not grow".
function Add-TableRows([string]$doc, [int]$Extra) {
    $marker = '| gamma | bool | ' + [char]0x53F3 + [char]0x8FB9 + [char]0x8FD9 + [char]0x4E00 + [char]0x5217 + ' |'
    $at = $doc.IndexOf($marker)
    if ($at -lt 0) { throw 'table marker row not found in the fixture' }
    if ($Extra -le 0) { return $doc }
    # Inserted directly BELOW the marker row, inside the table. Appending at the end of the
    # document instead is the trap this had: those lines become a lazy continuation of the
    # trailing paragraph, the table never grows, and the height still changes a little --
    # so the check "passes" while measuring the wrong thing entirely.
    $ins = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt $Extra; $i++) {
        [void]$ins.Append("`n| extra" + $i + " | int | filler row |")
    }
    $cut = $at + $marker.Length
    return $doc.Substring(0, $cut) + $ins.ToString() + $doc.Substring($cut)
}

# --- reading the rendered bubble -----------------------------------------------
#
# Counts the distinct backgrounded BLOCKS in a bubble: a code panel, a quote panel, a table
# header. Returns (bubble background, distinct blocks, a readable detail string).
#
# The background is taken as the MODAL colour of the scanned area rather than a hard-coded
# palette value -- it is whatever the current theme paints, and a probe that hard-codes it
# would silently compare against the wrong colour after a theme switch and report "no
# blocks" for a perfectly good render.
#
# What counts as a block is "a flat colour that is CLOSE to the bubble background". All of
# the panels are literally the background mixed slightly toward the text (see Markdown's
# Palette), so their distance from it is small -- and text ink, at the other end of the
# range, is excluded by the upper bound. This is the one test that survives all three of
# the shapes the blocks actually take, and each of them broke a more obvious one first:
#
#   * A code block spans the content width; a quote panel is as wide as its text; a table
#     header is as wide as its columns. "Most of the scanline" therefore only ever sees the
#     code block, and a three column table is ~155px inside an 810px bubble.
#   * Counting "non-background rows" instead merges the code block with the quote under it,
#     because the two panels are only a couple of pixels apart.
#   * The quote's left bar and the 帮帮 avatar are both accent-coloured, so anything keyed on
#     "a blue vertical line" has to out-guess the header.
#
# So: the longest run of one flat near-background colour >= 20px wide marks a scanline, the
# marked scanlines are grouped into vertical blocks >= 6px tall, and the distinct colours
# among those blocks are what gets counted. Glyph antialiasing produces thousands of
# near-background colours, but never a 20px wide run of any single one.
function Get-BubbleBands([int]$L, [int]$T, [int]$W, [int]$H, $Ui) {
    # Inside the rounded corners (r=12) and inside the bubble's horizontal padding, so the
    # scan never touches the corner, the border stroke or the text margin.
    $x0 = $L + 20; $x1 = $L + $W - 20
    $y0 = $T + 16; $y1 = $T + $H - 16

    # Clip to the chat view's client area. A bubble taller than the viewport has a negative
    # screen y here, and Get-Crop reads the screen -- outside the window that is not the
    # bubble, it is whatever else is on the desktop, which reports as a wall of bands or as
    # none, depending on the wallpaper.
    $vx0 = [int]$Ui.viewX; $vy0 = [int]$Ui.viewY
    $vx1 = $vx0 + [int]$Ui.clientW; $vy1 = $vy0 + [int]$Ui.clientH
    $clipped = $false
    if ($x0 -lt $vx0) { $x0 = $vx0; $clipped = $true }
    if ($x1 -gt $vx1) { $x1 = $vx1; $clipped = $true }
    if ($y0 -lt $vy0) { $y0 = $vy0; $clipped = $true }
    if ($y1 -gt $vy1) { $y1 = $vy1; $clipped = $true }
    if (($x1 - $x0) -lt 60 -or ($y1 - $y0) -lt 20) { return $null }

    $bmp = Get-Crop $x0 $y0 ($x1 - $x0) ($y1 - $y0)
    $w = $bmp.Width; $h = $bmp.Height
    $step = 3
    $minRun = 20      # a block's fill is at least this wide, in a row that holds it
    $minBlockH = 6    # ...and the block is at least this tall
    $near = 6         # "close to the background": at least this far from it...
    $far = 70         # ...and no further, which is what keeps text ink out

    $hist = @{}
    for ($yy = 0; $yy -lt $h; $yy += $step) {
        for ($xx = 0; $xx -lt $w; $xx += 6) {
            $c = $bmp.GetPixel($xx, $yy)
            $k = ($c.R * 65536) + ($c.G * 256) + $c.B
            if ($hist.ContainsKey($k)) { $hist[$k]++ } else { $hist[$k] = 1 }
        }
    }
    if ($hist.Count -eq 0) { $bmp.Dispose(); return $null }
    $bgKey = -1; $bgN = -1
    foreach ($k in $hist.Keys) { if ($hist[$k] -gt $bgN) { $bgN = $hist[$k]; $bgKey = $k } }
    $br = [Math]::Floor($bgKey / 65536)
    $bg = [Math]::Floor(($bgKey - $br * 65536) / 256)
    $bb = $bgKey - ($br * 65536) - ($bg * 256)

    # Pass 1: the dominant near-background fill of each scanline, or -1.
    $line = @{}
    for ($yy = 0; $yy -lt $h; $yy += $step) {
        $best = 0; $bestK = -1; $run = 0; $prev = -1
        for ($xx = 0; $xx -lt $w; $xx += 6) {
            $c = $bmp.GetPixel($xx, $yy)
            $k = ($c.R * 65536) + ($c.G * 256) + $c.B
            $r = [Math]::Floor($k / 65536)
            $g = [Math]::Floor(($k - $r * 65536) / 256)
            $b = $k - ($r * 65536) - ($g * 256)
            $d = [Math]::Abs($r - $br) + [Math]::Abs($g - $bg) + [Math]::Abs($b - $bb)
            if ($d -ge $near -and $d -le $far) {
                if ($k -eq $prev) { $run += 6 } else { $run = 6; $prev = $k }
                if ($run -gt $best) { $best = $run; $bestK = $k }
            }
            else { $run = 0; $prev = -1 }
        }
        if ($best -ge $minRun) { $line[$yy] = $bestK } else { $line[$yy] = -1 }
    }

    # Pass 2: vertical blocks of one colour, and the distinct fills among them. One block of
    # quote panel split in two by a line of text is the same fill counted once -- the set is
    # keyed by colour, not by block.
    $fills = @{}
    $detail = @()
    $curK = -1; $curTop = 0; $curH = 0
    for ($yy = 0; $yy -le $h; $yy += $step) {
        $k = -1
        if ($yy -lt $h) { $k = $line[$yy] }
        if ($k -ne $curK) {
            if ($curK -ne -1 -and $curH -ge $minBlockH) {
                $fills[$curK] = 1
                $detail += ($y0 + $curTop)
            }
            $curK = $k; $curTop = $yy; $curH = 0
        }
        else { $curH += $step }
    }
    $bmp.Dispose()
    return @{ Bg = "$br,$bg,$bb"; Bands = $fills.Count; At = ($detail -join ','); Clipped = $clipped; Scanned = ($y1 - $y0) }
}

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
# The single-file store is imported on startup when chats\ is empty, which would leave two
# conversations in the list and put the fixture on the second row instead of the first.
if (Test-Path $legacy) { Remove-Item $legacy -Force }

$script:ConvId = 'markdowntest0000000000000000000001'

function Write-Fixture([string]$doc, [string]$Title) {
    if (-not (Test-Path $chatsDir)) { [void](New-Item -ItemType Directory -Force $chatsDir) }
    $now = (Get-Date).ToString('o')
    # The file is a ChatFile envelope, not a bare Conversation: ChatStore.Save writes
    # { Version, Conversation }, and a conversation written at the top level deserializes
    # to a ChatFile whose Conversation is null -- the app silently shows an empty list, so
    # the probe would report "the click did nothing" for what is really a bad fixture.
    $file = @{
        Version = 1
        Conversation = @{
            Id        = $script:ConvId
            Title     = $Title
            CreatedAt = $now
            UpdatedAt = $now
            Messages  = @(
                @{
                    Role = 'user'; When = $now
                    Text = 'markdown ' + [char]0x6E32 + [char]0x67D3 + ' test'
                    Reasoning = ''; ReasoningMs = 0; Warning = ''; Attachments = @()
                },
                @{
                    Role = 'assistant'; When = $now
                    Text = $doc
                    Reasoning = ''; ReasoningMs = 0; Warning = ''; Attachments = @()
                }
            )
        }
    }
    $json = $file | ConvertTo-Json -Depth 8
    Set-Content -Path (Join-Path $chatsDir ($script:ConvId + '.json')) -Value $json -Encoding utf8
}

# Starts the app and locates the first row of the conversation list. Deliberately does NOT
# click yet: the caller reads ui-rows.json in between, and "0 rows on the welcome page" is
# what makes the later "2 rows" mean something.
function Start-Fixture {
    $main = Start-BangGang
    $mr = Get-WinRect $main
    $list = $null
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Left -ne $mr.Left) { continue }
        if (($r.Right - $r.Left) -ge 320) { continue }
        if (($r.Bottom - $r.Top) -lt 100) { continue }
        if ($r.Top -le $mr.Top + 38) { continue }
        if ($null -eq $list) { $list = $r }
    }
    return @{ Main = $main; Rect = $mr; List = $list }
}

# Clicks the first row of the list: middle of its width, middle of its 38px height. The
# delete button only appears on hover and only at the far right, so the middle is safe.
function Open-Row($app) {
    $list = $app.List
    $lx = [int]($list.Left + ($list.Right - $list.Left) / 2)
    $ly = $list.Top + 19
    Invoke-MouseClick $lx $ly
    Start-Sleep -Milliseconds 900
    return @{ X = $lx; Y = $ly }
}

try {
    Invoke-BBProbe {
        # ---------- calibration: the app comes up on the welcome page --------------
        Remove-Item $script:BBRows -ErrorAction SilentlyContinue
        Write-Fixture $script:Panels 'markdown-panels'
        $app = Start-Fixture
        $main = $app.Main; $mr = $app.Rect
        $mw = $mr.Right - $mr.Left; $mh = $mr.Bottom - $mr.Top

        Write-Output ("window " + $mr.Left + "," + $mr.Top + " ${mw}x${mh}")
        Write-Output ''
        Write-Output '--- A. starts on the welcome page with the history intact ---'
        if ($null -eq $app.List) {
            Write-Output '  FAIL -- no conversation list; the history did not come back'
            $script:fail++
            throw 'cannot continue without the conversation list'
        }
        # The signal for "is a conversation open" is read off ui-rows.json rather than off
        # the control tree, because that is the only thing that answers it now -- the
        # bubbles stopped being windows in 0.9.0 and the title bar is a child of a panel
        # that has never been shown (so it does not even have a handle).
        $before = @(Get-UiRowList (Get-UiRows)).Count
        if ($before -ne 0) {
            Write-Output ("  FAIL -- " + $before + ' rows are already rendered; a conversation opened by itself')
            $script:fail++
        }
        else { Write-Output '  OK   the list has a row, and no conversation is open' }

        # ---------- B. opening it gives two rows ----------------------------------
        Write-Output ''
        Write-Output '--- B. opening the conversation renders both messages ---'
        $click = Open-Row $app
        Write-Output ('  clicked the first list row at ' + $click.X + ',' + $click.Y)
        $ui = Get-UiRows
        $rows = @(Get-UiRowList $ui)
        if ($rows.Count -ne 2) {
            Write-Output ("  FAIL -- " + $rows.Count + ' rows, expected 2 (user + assistant)')
            $script:fail++
            throw 'cannot continue without two rows'
        }
        if ($rows[0].role -ne 'user' -or $rows[1].role -ne 'assistant') {
            $roleStr = ($rows | ForEach-Object { $_.role }) -join ','
            Write-Output ('  FAIL -- roles are ' + $roleStr + ', expected user,assistant')
            $script:fail++
        }
        else { Write-Output '  OK   user row then assistant row' }

        $asst = $rows[1]
        $asstR = Get-UiRowScreen $ui $asst
        $userR = Get-UiRowScreen $ui $rows[0]
        Write-Output ("  rows  user " + $userR.W + 'x' + $userR.H + '   assistant ' + $asstR.W + 'x' + $asstR.H)
        [void](Save-WindowShot $main (Get-ShotPath 'markdown-open.png'))

        # ---------- C. the whole document fits ------------------------------------
        Write-Output ''
        Write-Output '--- C. the assistant bubble is tall enough for the whole document ---'
        # A doc with every block in it is ~1100px, far taller than the viewport -- which is
        # fine here, because this check reads the height the LAYOUT reports, not pixels. A
        # renderer that silently dropped the code blocks and the table would still produce a
        # bubble, just a short one.
        Remove-Item $script:BBRows -ErrorAction SilentlyContinue
        Write-Fixture $script:Sample 'markdown-full'
        $appC = Start-Fixture
        Open-Row $appC | Out-Null
        $uiC = Get-UiRows
        $rowsC = @(Get-UiRowsOf $uiC 'assistant')
        $asstR = $null
        if ($rowsC.Count -ne 1) {
            Write-Output ("  FAIL -- " + $rowsC.Count + ' assistant rows in the full fixture, expected 1')
            $script:fail++
        }
        else {
            $asstR = Get-UiRowScreen $uiC $rowsC[0]
            Write-Output ("  full document renders " + $asstR.W + 'x' + $asstR.H)
            if ($asstR.H -lt 400) {
                Write-Output ("  FAIL -- the bubble is only " + $asstR.H + 'px; blocks were dropped')
                $script:fail++
            }
            else { Write-Output ("  OK   " + $asstR.H + 'px tall') }
            # Shots of the whole document, a screenful at a time. The geometry checks above
            # say the blocks were laid out; only the eye can say whether they are readable,
            # and "好看" is half of what this item was about.
            $vcx = [int]$uiC.viewX + [int]([int]$uiC.clientW / 2)
            $vcy = [int]$uiC.viewY + [int]([int]$uiC.clientH / 2)
            Invoke-WheelAt $vcx $vcy 40
            Start-Sleep -Milliseconds 400
            [void](Save-WindowShot $appC.Main (Get-ShotPath 'markdown-full-1.png'))
            Invoke-WheelAt $vcx $vcy -10
            Start-Sleep -Milliseconds 400
            [void](Save-WindowShot $appC.Main (Get-ShotPath 'markdown-full-2.png'))
            Invoke-WheelAt $vcx $vcy -10
            Start-Sleep -Milliseconds 400
            [void](Save-WindowShot $appC.Main (Get-ShotPath 'markdown-full-3.png'))
        }

        # ---------- E. distinct backgrounded blocks -------------------------------
        #
        # Measured on the COMPACT fixture, which is the whole point of it: this check reads
        # pixels, so the whole bubble has to be inside the viewport. Scanning a 1100px bubble
        # in a 564px viewport would be reading the desktop (or, after the clamp below, an
        # arbitrary 500px slice of the document).
        Write-Output ''
        Write-Output '--- E. backgrounded blocks (code / quote / table header) ---'
        Remove-Item $script:BBRows -ErrorAction SilentlyContinue
        Write-Fixture $script:Panels 'markdown-panels'
        $appE = Start-Fixture
        Open-Row $appE | Out-Null
        $uiE = Get-UiRows
        $rowsE = @(Get-UiRowsOf $uiE 'assistant')
        if ($rowsE.Count -ne 1) {
            Write-Output ("  FAIL -- " + $rowsE.Count + ' assistant rows in the panel fixture, expected 1')
            $script:fail++
        }
        else {
            $eR = Get-UiRowScreen $uiE $rowsE[0]
            $bands = Get-BubbleBands $eR.L $eR.T $eR.W $eR.H $uiE
            if ($null -eq $bands) {
                Write-Output '  FAIL -- the bubble is too small to scan'
                $script:fail++
            }
            elseif ($bands.Bands -lt 3) {
                Write-Output ("  FAIL -- found " + $bands.Bands + ' backgrounded blocks, expected >= 3 ' +
                              '(a code block, a quote panel, a table header); at y=' + $bands.At)
                $script:fail++
            }
            else {
                Write-Output ("  OK   " + $bands.Bands + ' backgrounded blocks at y=' + $bands.At +
                              ' (bg ' + $bands.Bg + ', scanned ' + $bands.Scanned + 'px' +
                              $(if ($bands.Clipped) { ', CLIPPED to the viewport' } else { '' }) + ')')
            }
            [void](Save-WindowShot $appE.Main (Get-ShotPath 'markdown-panels.png'))
        }

        # ---------- D. the table grows with its row count -------------------------
        #
        # Two runs, three and six extra rows, against the baseline height from C. The
        # tolerance on the pitch alone would not be enough: a document that grew by a
        # paragraph-sized amount for some unrelated reason could land inside any range wide
        # enough to hold a real table row. Requiring the six-row delta to be twice the
        # three-row one is what makes this a measurement of "each row costs a row".
        Write-Output ''
        Write-Output '--- D. table row count drives the layout ---'
        if ($null -eq $asstR) {
            Write-Output '  SKIP -- no baseline height from C'
        }
        else {
            $hs = @{}
            foreach ($extra in @(3, 6)) {
                Remove-Item $script:BBRows -ErrorAction SilentlyContinue
                Write-Fixture (Add-TableRows $script:Sample $extra) ('markdown-' + $extra)
                $appD = Start-Fixture
                Open-Row $appD | Out-Null
                $uiD = Get-UiRows
                $rowsD = @(Get-UiRowsOf $uiD 'assistant')
                if ($rowsD.Count -ne 1) {
                    Write-Output ("  FAIL -- " + $rowsD.Count + ' assistant rows with ' + $extra + ' extra table rows')
                    $script:fail++
                    $hs[$extra] = $null
                }
                else {
                    $hs[$extra] = (Get-UiRowScreen $uiD $rowsD[0]).H
                    [void](Save-WindowShot $appD.Main (Get-ShotPath ('markdown-table' + $extra + '.png')))
                }
            }
            if ($null -ne $hs[3] -and $null -ne $hs[6]) {
                $d3 = $hs[3] - $asstR.H
                $d6 = $hs[6] - $asstR.H
                $pitch = $d6 / 6
                Write-Output ("  baseline " + $asstR.H + 'px, +3 rows ' + $hs[3] + ' (+' + $d3 +
                              '), +6 rows ' + $hs[6] + ' (+' + $d6 + ') = ' + [Math]::Round($pitch, 1) + 'px/row')
                if ($pitch -lt 20 -or $pitch -gt 45) {
                    Write-Output ('  FAIL -- a table row is ' + [Math]::Round($pitch, 1) +
                                  'px, which is not a line plus cell padding')
                    $script:fail++
                }
                elseif ([Math]::Abs($d6 - 2 * $d3) -gt [Math]::Max(4, 0.15 * $d6)) {
                    Write-Output ('  FAIL -- six rows added ' + $d6 + 'px but three added ' + $d3 +
                                  'px; the growth is not one row per row')
                    $script:fail++
                }
                else { Write-Output ('  OK   ' + $d6 + 'px for 6 rows, ' + $d3 + 'px for 3 -- linear') }
            }
        }

        Write-Output ''
        if ($script:fail -eq 0) { Write-Output 'PASS: the markdown sample renders every block, with backgrounded panels and a real table.' }
        else { Write-Output "FAIL: $script:fail check(s) failed." }
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

Write-BBDone 'markdown-render'
