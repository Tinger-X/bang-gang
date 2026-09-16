# The attachment strip in the DARK theme.
#
# draft-strip.ps1 runs whatever theme the app happens to be set to (light by default),
# so it cannot see the dark half of the strip's palette at all -- and that half is
# exactly the kind of thing that is only wrong after a theme switch. This runs the same
# paste-and-hover sequence with settings.json pointed at the dark palette.
#
# It saves settings.json first and puts it back in a finally: the file is the app's real
# configuration and losing it would lose the user's provider settings.
#
# Everything measured here is measured the same way the light probe measures it, off
# rects read back from the running window -- see the notes on each helper.
#
# ASCII ONLY (PS 5.1 reads a BOM-less .ps1 as ANSI; CJK literals are syntax errors).

. "$PSScriptRoot\_ui.ps1"

$script:fail = @()
$script:snap = $null
$script:snapOX = 0
$script:snapOY = 0

function Check([string]$What, [bool]$Ok, [string]$Detail) {
    if ($Ok) { Write-Output ("OK   " + $What + " -- " + $Detail) }
    else { Write-Output ("FAIL " + $What + " -- " + $Detail); $script:fail += $What }
}

function Take-Snapshot($main) {
    if ($null -ne $script:snap) { $script:snap.Dispose() }
    $mr = Get-WinRect $main
    $script:snap = Get-Crop $mr.Left $mr.Top ($mr.Right - $mr.Left) ($mr.Bottom - $mr.Top)
    $script:snapOX = $mr.Left
    $script:snapOY = $mr.Top
}

function PxAt($bmp, [int]$x, [int]$y) {
    $px = $x - $script:snapOX
    $py = $y - $script:snapOY
    if ($px -lt 0 -or $py -lt 0 -or $px -ge $bmp.Width -or $py -ge $bmp.Height) {
        return [System.Drawing.Color]::Black
    }
    return $bmp.GetPixel($px, $py)
}

function Get-Px([int]$x, [int]$y) { return PxAt $script:snap $x $y }

function Lum($c) { return 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B }

# "Is this pixel NOT the card's own fill?" -- the reference is sampled, never assumed:
# a hard-coded white would call the whole dark card a chip. The tolerance is wider than
# the light probe's because the dark palette's steps are smaller, but it is still far
# below the gap the chip's own fill is supposed to have (see step C).
function Differs($c, $bg) {
    return ([Math]::Abs($c.R - $bg.R) -gt 6 -or [Math]::Abs($c.G - $bg.G) -gt 6 `
         -or [Math]::Abs($c.B - $bg.B) -gt 6)
}

function Chan-Diff($a, $b) {
    return [Math]::Max([Math]::Abs($a.R - $b.R), [Math]::Max([Math]::Abs($a.G - $b.G), [Math]::Abs($a.B - $b.B)))
}

function Get-InputEdit($main) {
    foreach ($h in Get-WinKids $main) {
        if ((Get-ShortClass $h) -ne 'Edit') { continue }
        $r = Get-WinRect $h
        if (($r.Bottom - $r.Top) -gt 40) { return @{ H = $h; R = $r } }
    }
    return $null
}

function Send-FilesPaste($edit, [string[]]$Paths) {
    $col = New-Object System.Collections.Specialized.StringCollection
    foreach ($p in $Paths) { [void]$col.Add($p) }
    $ok = $false
    for ($i = 0; $i -lt 5 -and -not $ok; $i++) {
        try { [System.Windows.Forms.Clipboard]::SetFileDropList($col); $ok = $true }
        catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ok) { throw 'could not put the file list on the clipboard' }
    Start-Sleep -Milliseconds 250
    $r = Get-WinRect $edit
    Invoke-MouseClick ([int](($r.Left + $r.Right) / 2)) ([int](($r.Top + $r.Bottom) / 2))
    Start-Sleep -Milliseconds 300
    [BB]::Chord(0x56)
    Start-Sleep -Milliseconds 700
}

# The chip row: walk up from just above the text box until a whole row is card fill
# again. Several columns, because the chips are narrow and the gaps between them are
# card fill -- a single column can land in a gap and report "no band at all".
function Get-BandAbove([int[]]$Cols, [int]$Bottom, $bg, [int]$MaxUp = 200) {
    $top = -1
    for ($y = $Bottom; $y -gt $Bottom - $MaxUp; $y--) {
        $hit = $false
        foreach ($x in $Cols) { if (Differs (Get-Px $x $y) $bg) { $hit = $true; break } }
        if ($hit) { $top = $y } else { break }
    }
    if ($top -lt 0) { return $null }
    return @{ Top = $top; Bottom = $Bottom; H = $Bottom - $top + 1 }
}

function Get-BandCols($r) { return @(($r.Left + 12), ($r.Left + 34), ($r.Left + 60), ($r.Left + 200)) }

# Each chip is one contiguous run of off-card pixels, separated from the next by the
# layout gap. Anything under 30px wide is an edge artefact, not a chip.
function Get-ChipRuns([int]$x0, [int]$x1, [int]$y, $bg) {
    $runs = @()
    $start = -1
    for ($x = $x0; $x -le $x1; $x++) {
        $on = Differs (Get-Px $x $y) $bg
        if ($on -and $start -lt 0) { $start = $x }
        elseif (-not $on -and $start -ge 0) {
            if (($x - $start) -ge 30) { $runs += , @{ L = $start; R = $x - 1 } }
            $start = -1
        }
    }
    if ($start -ge 0 -and ($x1 - $start) -ge 30) { $runs += , @{ L = $start; R = $x1 } }
    return $runs
}

# The topmost row of the chip that owns column $x. Sample at the chip's own centre:
# a photo chip is 44px across, so a column 44px to the right of its left edge lands in
# the gap between two chips and the walk starts on card fill.
function Get-ChipTop([int]$x, [int]$yMid, $bg) {
    for ($y = $yMid; $y -gt $yMid - 60; $y--) {
        if (-not (Differs (Get-Px $x $y) $bg)) { return $y + 1 }
    }
    return -1
}

# Pixels that are BRIGHT now and were not bright in $Other. In the dark theme the delete
# badge is a light disc on a dark card, so "bright" is the signal -- but the photo chip
# underneath can be bright too, and only a difference against the un-hovered frame
# separates "the button appeared" from "there was already a light patch there".
function Get-NewBright($Other, [int]$Cut, [int]$x0, [int]$y0, [int]$x1, [int]$y1) {
    $minX = -1; $maxX = -1; $minY = -1; $maxY = -1; $n = 0
    for ($x = $x0; $x -le $x1; $x++) {
        for ($y = $y0; $y -le $y1; $y++) {
            if ((Lum (Get-Px $x $y)) -le $Cut) { continue }
            if ((Lum (PxAt $Other $x $y)) -gt $Cut) { continue }
            $n++
            if ($minX -lt 0 -or $x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($minY -lt 0 -or $y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
    if ($n -eq 0) { return $null }
    return @{ X = $minX; Y = $minY; R = $maxX; B = $maxY; N = $n
              CX = [int](($minX + $maxX) / 2); CY = [int](($minY + $maxY) / 2) }
}

$fixDir = Join-Path $script:BBShoots 'fixtures'
$pngPath = Join-Path $fixDir 'pic.png'
$txtPath = Join-Path $fixDir 'notes.txt'
if (-not (Test-Path $pngPath) -or -not (Test-Path $txtPath)) {
    throw 'run tools\draft-strip.ps1 first: it writes the fixtures this one pastes'
}

$settings = Join-Path (Split-Path $script:BBExe -Parent) 'settings.json'
# Usually there is no settings.json at all next to the debug exe (the app then runs on its
# defaults), so "restore" has to be able to mean "make it not exist again".
$existed = Test-Path $settings
$backup = $null
if ($existed) { $backup = Get-Content $settings -Raw }

try {
    # Flip the theme in the app's own config, then let the app load it. Writing the JSON
    # rather than clicking through the settings overlay keeps this probe about the strip.
    # Only ThemeMode goes in: everything else falls back to the class defaults, which is
    # exactly what the app would have used with no file there at all.
    $o = @{}
    if ($existed) { $o = $backup | ConvertFrom-Json }
    $o | Add-Member -NotePropertyName ThemeMode -NotePropertyValue 'dark' -Force
    $o | ConvertTo-Json -Depth 8 | Set-Content -Path $settings -Encoding utf8

    Invoke-BBProbe {
        $main = Start-BangGang 5
        $mr = Get-WinRect $main

        $pill = Get-SearchPill $main
        $pillBtns = Get-PillButtons $main $pill
        if ($null -eq $pillBtns) { throw 'sidebar pill buttons not found' }
        $newBtn = $pillBtns[1]
        Invoke-MouseClick ([int](($newBtn.Left + $newBtn.Right) / 2)) ([int](($newBtn.Top + $newBtn.Bottom) / 2))
        Start-Sleep -Milliseconds 900

        $e0 = Get-InputEdit $main
        if ($null -eq $e0) { throw 'input text box not found' }
        $edit = $e0.H

        Send-FilesPaste $edit @($pngPath, $txtPath)
        $mr = Get-WinRect $main
        [void][BB]::SetCursorPos(($mr.Right - 8), ($mr.Bottom - 4))
        Start-Sleep -Milliseconds 400
        Take-Snapshot $main
        Save-WindowShot $main (Get-ShotPath 'draft-dark-1.png')

        $r1 = Get-WinRect $edit

        # Card fill, read off the text box's own top-left interior: a few px in from the
        # corner is above the first line of text and inside no chip. Read it AFTER the
        # snapshot -- Get-Px reads the snapshot, and before one exists every coordinate
        # comes back Black, which makes every pixel "differ from the card" and turns the
        # band walk into a 200px crawl up the whole window.
        $bg = Get-Px ($r1.Left + 12) ($r1.Top + 3)

        # ---- A. the dark card draws the same chip row ----
        $band = Get-BandAbove (Get-BandCols $r1) ($r1.Top - 3) $bg
        $bandOk = ($null -ne $band) -and ($band.H -ge 40) -and ($band.H -le 62)
        Check 'the dark card draws a chip row above the text box' $bandOk `
              ($(if ($null -eq $band) { 'no band found' } else { 'band ' + $band.H + 'px tall, top y=' + $band.Top }))
        if ($null -eq $band) { throw 'no strip in the dark theme: nothing left to measure' }

        $rowMid = $band.Top + [int]($band.H / 2)
        # @() around the call: a function that returns a one-element array hands back the
        # element itself, and a hashtable's .Count is its KEY count (2), not "one run" --
        # which reads as two chips when there was really one.
        $runs = @(Get-ChipRuns $r1.Left ($r1.Right - 1) $rowMid $bg)
        Check 'both chips are there in the dark theme' ($runs.Count -eq 2) `
              ($runs.Count.ToString() + ' chip(s)')
        if ($runs.Count -lt 2) { throw 'the dark strip is not the strip: stopping here' }

        $chip0 = $runs[0]      # the image: a 44px square tile, name and all
        $chip1 = $runs[1]      # the file: icon + two lines of text
        $w0 = $chip0.R - $chip0.L + 1
        $w1 = $chip1.R - $chip1.L + 1
        Check 'the image chip is a square tile, not a name chip' ($w0 -ge 40 -and $w0 -le 50) `
              ('image chip is ' + $w0 + 'px wide')
        Check 'the file chip is the halved width' ($w1 -ge 110 -and $w1 -le 126) `
              ('file chip is ' + $w1 + 'px wide')

        # ---- B. the file chip is VISIBLE against the card ----
        # This is the dark palette's trap: the light theme draws the chip in AsstBubble,
        # but in dark mode AsstBubble (40,44,50) sits 3-4 levels off the card's own fill
        # (43,47,54) -- a chip you cannot see. Sample the chip's own fill, below the icon
        # and below the second text line, where nothing else is drawn.
        #
        # The row comes from the band (its top and bottom ARE the chip's own edges); a run
        # only carries L/R, and a missing key reads as $null, which quietly puts the sample
        # off-window where every pixel comes back Black -- 54 levels off any dark card,
        # i.e. this check passing for the worst possible reason.
        $fill = Get-Px ($chip1.L + 20) ($band.Bottom - 3)
        $gap = Chan-Diff $fill $bg
        # The two Lum() guards are not decoration: an off-window sample comes back Black,
        # and Black is 54 levels off any dark card -- enough to satisfy the gap on its own.
        Check 'the file chip stands out from the card in the dark theme' `
              (($gap -ge 10) -and ((Lum $fill) -gt 5) -and ((Lum $bg) -gt 5)) `
              ('chip fill ' + $fill.R + ',' + $fill.G + ',' + $fill.B + ' vs card ' `
               + $bg.R + ',' + $bg.G + ',' + $bg.B + ' = ' + $gap + ' levels')

        $chipTop = Get-ChipTop ([int](($chip0.L + $chip0.R) / 2)) $rowMid $bg
        Write-Output ("chip 0: x " + $chip0.L + ".." + $chip0.R + ", top y=" + $chipTop + ", mid y=" + $rowMid)

        # ---- C. hover the image chip: the badge must still read as a button ----
        $script:cold = $script:snap.Clone()
        [void][BB]::SetCursorPos([int](($chip0.L + $chip0.R) / 2), ($chipTop + 20))
        Start-Sleep -Milliseconds 500
        Take-Snapshot $main
        Save-WindowShot $main (Get-ShotPath 'draft-dark-2-hover.png')

        # 150 is the midpoint between the badge's light disc (219) and the photo chip
        # under it (the fixture's orange is ~125), so the two cannot be confused.
        $hot = Get-NewBright $script:cold 150 ($chip0.L + 2) ($chipTop - 14) ($chip0.R + 16) ($chipTop + 10)
        Check 'hovering a chip floats the badge in the dark theme' ($null -ne $hot -and $hot.N -ge 100) `
              ($(if ($null -eq $hot) { 'nothing new appeared' } else { $hot.N.ToString() + ' new bright pixels' }))

        if ($null -ne $hot) {
            $dx = [Math]::Abs($hot.CX - $chip0.R)
            Check 'the badge sits at the chip RIGHT edge in the dark theme' ($dx -le 5) `
                  ('badge centre x=' + $hot.CX + ', chip right=' + $chip0.R + ' (off by ' + $dx + ')')
            Check 'the badge straddles the chip TOP edge in the dark theme' `
                  ($chipTop -gt 0 -and [Math]::Abs($hot.CY - $chipTop) -le 6) `
                  ('badge centre y=' + $hot.CY + ', chip top=' + $chipTop)
            Check 'the badge pokes ABOVE the chip in the dark theme' `
                  ($chipTop -gt 0 -and $hot.Y -lt $chipTop) `
                  ('badge top y=' + $hot.Y + ' vs chip top=' + $chipTop)
        }
    }

    if ($script:fail.Count -gt 0) {
        Write-Output ''
        Write-Output ('FAILED: ' + ($script:fail -join '; '))
        throw ($script:fail.Count.ToString() + ' check(s) failed')
    }
    Write-Output ''
    Write-Output 'PASS'
}
finally {
    # Put the user's own configuration back, whatever happened above -- including
    # "there was no file here before", which is the normal case.
    if ($existed) { Set-Content -Path $settings -Value $backup -Encoding utf8 -NoNewline }
    else { Remove-Item -Path $settings -Force -ErrorAction SilentlyContinue }
}

Write-BBDone 'draft-strip-dark'
