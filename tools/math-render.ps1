# math-render.ps1 -- does a formula actually come out as a formula?
#
# The renderer has three input forms (TeX delimiters, bare commands, MathML) feeding one
# layout engine, and every failure mode of it looks the same from the outside: the formula
# shows up as raw text and nobody notices, because raw text is exactly what the build did
# before formulas existed. "It looked fine in the screenshot" is not a check.
#
# So this probe drives the OFFLINE renderer (OfflineRender, BANGGANG_RENDER_MD) rather than
# the window, and asserts on the layout dump. Three reasons:
#
#   * it is the only path that runs with the desktop down. CopyFromScreen returns a flat
#     BSOD frame while the machine's display is broken, and a probe that reports "no ink"
#     then reads exactly like "the renderer draws nothing" -- which is how a whole class of
#     real breakage gets dismissed as a probe problem.
#   * the questions worth asking here are about LAYOUT (is the box big enough, is the bar a
#     real bar, do the text and the formula share a baseline), and those are answered by
#     numbers, not by pixels. Screenshots cannot say "this glyph's cell is 33% taller than
#     the box that claims to contain it" -- that is what the unit bug did, and it looked
#     like "the spacing is a bit tight".
#   * a dump is a consistent snapshot. Nothing here depends on an animation frame, a window
#     position, or which of two message queues got there first.
#
# Each case is its own file so one case cannot shift another's line numbers.
#
# What it asserts:
#   A. every input form reaches the math path: $..$, \(..\), $$..$$, \[..\], a bare \command,
#      and <math>..</math> each produce a MATH run (never a text run of the raw source).
#   B. $100 / $50 stay literal text -- no MATH run on that line at all. This is the guard
#      that keeps two prices in one sentence from being eaten as one formula.
#   C. a display formula row is much taller than a text row, and the fraction bar / radical
#      actually exist as prims (a rule, a polyline). Raw text is never taller than text.
#   D. asc + desc == the font's own pixel height. THIS is the unit check: the layout's
#      vertical model is pixels end to end, and it used to be points while the glyphs were
#      drawn in pixels, so every formula was 4/3 too tall for its box and the parts piled
#      into each other. Nothing else in the dump can see that -- the dump was in the wrong
#      unit too, but Font.Height never was.
#   E. every prim sits inside the box that claims to contain it.
#   F. fraction / radical bars are SOLID dark rows, not two half-lit ones. A 1px rule at a
#      fractional y gets antialiased across two rows at 50% each and reads as a light grey
#      line -- the model is fine, the pixels are not, so this one has to read pixels.
#   G. an inline formula sits ON THE SAME BASELINE as the text beside it (ink bottoms agree).
#
# ASCII only -- PowerShell 5.1 reads a BOM-less file as ANSI and Chinese breaks the parse.
# Backslashes in the fixtures are fine: PowerShell single quotes are literal. Writing these
# fixtures from a bash heredoc instead mangles every backslash, which is how an earlier
# version of this probe managed to test nothing at all.

. "$PSScriptRoot\_ui.ps1"

$script:fail = 0
$script:pad  = 14                       # OfflineRender's margin; the dump's y starts here

$work = Join-Path (Split-Path $PSScriptRoot -Parent) 'shoots\math'
if (-not (Test-Path $work)) { [void](New-Item -ItemType Directory -Path $work -Force) }

# --- fixtures ---------------------------------------------------------------
# One case per file. No Chinese, no quotes -- this file has to stay pure ASCII.
$cases = [ordered]@{
    'text-only'   = 'Plain text with no math in it at all.'
    'inline-usd'  = 'Inline dollar math $x^{2^{2}}$ sits in this line.'
    'inline-paren'= 'Inline paren math \(\frac{a}{b}\) sits in this line.'
    'display-usd' = '$$\frac{a}{b}$$'
    'display-brk' = '\[\int_0^\infty e^{-x^2}\,dx = \frac{\sqrt{\pi}}{2}\]'
    'bare-cmd'    = 'Bare commands \alpha + \beta = \gamma are recognized.'
    'mathml'      = '<math><mfrac><mn>1</mn><mn>2</mn></mfrac></math>'
    'sqrt-index'  = '$$\sqrt[3]{x+1}$$'
    'currency'    = 'The price is $100 and the other one is $50.'
}

$script:dumps = @{}

function Write-Fixture([string]$Name, [string]$Md) {
    # UTF8Encoding($false) -- no BOM. File.ReadAllText strips one, but the fixture should
    # still be byte-for-byte what the renderer would get from a user's file.
    $p = Join-Path $work ($Name + '.md')
    [System.IO.File]::WriteAllText($p, $Md, (New-Object System.Text.UTF8Encoding $false))
    return $p
}

# Run the Debug build in offline-render mode and keep its dump. It returns before the
# single-instance mutex is taken and never creates a window, so this cannot collide with a
# running app. Env vars are cleared afterwards regardless of the outcome.
function Invoke-Render([string]$Md) {
    $png = [System.IO.Path]::ChangeExtension($Md, '.png')
    $env:BANGGANG_RENDER_MD    = $Md
    $env:BANGGANG_RENDER_PNG   = $png
    $env:BANGGANG_RENDER_W     = '760'
    $env:BANGGANG_RENDER_DUMP  = '2'
    try { $out = (& $script:BBExe 2>&1 | Out-String) }
    finally {
        foreach ($n in 'BANGGANG_RENDER_MD','BANGGANG_RENDER_PNG','BANGGANG_RENDER_W','BANGGANG_RENDER_DUMP') {
            Remove-Item ("Env:" + $n) -ErrorAction SilentlyContinue
        }
    }
    return @{ Out = $out; Png = $png }
}

# --- dump parsing -----------------------------------------------------------
# The console's code page mangles the glyph TEXT (a CJK or math symbol comes back as '??'),
# so nothing below ever matches on a glyph's character -- only on its numbers.
function Parse-Dump([string]$Out) {
    $lines = New-Object System.Collections.Generic.List[object]
    $cur = $null
    foreach ($raw in ($Out -split "`n")) {
        $t = $raw.TrimEnd("`r")
        $m = [regex]::Match($t, '^\s*line (\d+): y=(-?[\d.]+) pitch=(-?[\d.]+) lineH=(-?[\d.]+) base=(-?[\d.]+) shift=(-?[\d.]+) indent=(-?[\d.]+) runs=(\d+)')
        if ($m.Success) {
            $cur = @{
                Y = [double]$m.Groups[2].Value; Pitch = [double]$m.Groups[3].Value
                LineH = [double]$m.Groups[4].Value; Base = [double]$m.Groups[5].Value
                Shift = [double]$m.Groups[6].Value
                Runs = New-Object System.Collections.Generic.List[object]
                Prims = New-Object System.Collections.Generic.List[object]
            }
            $lines.Add($cur)
            continue
        }
        if ($null -eq $cur) { continue }

        $m = [regex]::Match($t, '^\s*x=(-?[\d.]+) w=(-?[\d.]+) adv=(-?[\d.]+) (.*)$')
        if ($m.Success) {
            $tail = $m.Groups[4].Value
            $mm = [regex]::Match($tail, '^MATH (-?[\d.]+)x(-?[\d.]+)\+(-?[\d.]+) prims=(\d+)')
            if ($mm.Success) {
                $cur.Runs.Add(@{
                    X = [double]$m.Groups[1].Value; W = [double]$m.Groups[2].Value; Math = $true; Text = ''
                    BoxW = [double]$mm.Groups[1].Value
                    BoxH = [double]$mm.Groups[2].Value
                    BoxD = [double]$mm.Groups[3].Value
                })
            }
            else {
                $cur.Runs.Add(@{
                    X = [double]$m.Groups[1].Value; W = [double]$m.Groups[2].Value; Math = $false
                    Text = $tail.Trim('"')
                })
            }
            continue
        }

        $m = [regex]::Match($t, '^\s*glyph "[^"]*" size=(-?[\d.]+) asc=(-?[\d.]+) desc=(-?[\d.]+) ink=(-?[\d.]+)\.\.(-?[\d.]+) px=(-?[\d.]+) x=(-?[\d.]+) y=(-?[\d.]+)$')
        if ($m.Success) {
            $cur.Prims.Add(@{
                Kind = 'glyph'
                Size = [double]$m.Groups[1].Value
                Asc  = [double]$m.Groups[2].Value; Desc = [double]$m.Groups[3].Value
                Px   = [double]$m.Groups[6].Value
                X    = [double]$m.Groups[7].Value; Y = [double]$m.Groups[8].Value
                Y0 = [double]$m.Groups[8].Value; Y1 = [double]$m.Groups[8].Value
                X1 = [double]$m.Groups[7].Value
            })
            continue
        }
        $m = [regex]::Match($t, '^\s*rule x=(-?[\d.]+) y=(-?[\d.]+) w=(-?[\d.]+) h=(-?[\d.]+)$')
        if ($m.Success) {
            $x = [double]$m.Groups[1].Value; $y = [double]$m.Groups[2].Value
            $w = [double]$m.Groups[3].Value; $h = [double]$m.Groups[4].Value
            $cur.Prims.Add(@{ Kind='rule'; X=$x; X1=($x+$w); Y=$y; Y0=$y; Y1=($y+$h) })
            continue
        }
        $m = [regex]::Match($t, '^\s*poly th=(-?[\d.]+) (.*)$')
        if ($m.Success) {
            $xs = @(); $ys = @()
            foreach ($p in [regex]::Matches($m.Groups[2].Value, '\((-?[\d.]+),(-?[\d.]+)\)')) {
                $xs += [double]$p.Groups[1].Value
                $ys += [double]$p.Groups[2].Value
            }
            $a = ($xs | Measure-Object -Minimum).Minimum
            $b = ($xs | Measure-Object -Maximum).Maximum
            $lo = ($ys | Measure-Object -Minimum).Minimum
            $hi = ($ys | Measure-Object -Maximum).Maximum
            $cur.Prims.Add(@{ Kind='poly'; X=$a; X1=$b; Y=$lo; Y0=$lo; Y1=$hi })
            continue
        }
    }
    return $lines
}

# The one MATH run on a line (a fixture line has at most one), or $null.
function Get-MathRun($line) {
    $found = @($line.Runs | Where-Object { $_.Math })
    if ($found.Count -eq 0) { return $null }
    return $found[0]
}

# All prims of one kind on a line, as PIPELINE OUTPUT -- collect with @(), never with a
# bare call. Returning an array from a function instead is the trap the codebase already has
# a scar for: PowerShell hands a one-element collection back as the element itself, so
# `(Get-Prims $l 'rule').Count` is then the hashtable's KEY count -- >= 1 whether or not a
# rule was found, i.e. a check that cannot fail. Emitting each prim separately and letting
# the caller wrap in @() is the one form whose count is unambiguous.
function Get-Prims($line, [string]$Kind) {
    $r = Get-MathRun $line
    if ($null -eq $r) { return }
    # -eq against $Kind rather than a Where-Object script block: the block would be a child
    # scope and could not see $Kind at all (see the note on Invoke-BBProbe).
    foreach ($p in $line.Prims) { if ($p.Kind -eq $Kind) { Write-Output $p } }
}

# All prims of the first MATH run of a file's first line, plus that line. Fixture lines are
# one line each; a case that wrapped would silently lose prims, so the caller asserts count.
function Get-Case([string]$Name) {
    $lines = @($script:dumps[$Name])
    if ($lines.Count -eq 0) { return $null }
    return $lines[0]
}

function Check([bool]$Ok, [string]$Label, [string]$Detail) {
    if ($Ok) { Write-Output ('  OK   ' + $Label + '  ' + $Detail) }
    else { Write-Output ('  FAIL ' + $Label + '  ' + $Detail); $script:fail++ }
}

# Ink bounding box of a rectangle of a loaded bitmap (the file-based twin of _ui.ps1's
# Get-Crop + Get-InkBoxNum, which read the SCREEN). Returns @{ Y, B } or $null.
function Get-BmpInkBox($bmp, [int]$x, [int]$y, [int]$w, [int]$h) {
    $x = [Math]::Max(0, $x); $y = [Math]::Max(0, $y)
    $w = [Math]::Min($w, $bmp.Width - $x); $h = [Math]::Min($h, $bmp.Height - $y)
    if ($w -le 0 -or $h -le 0) { return $null }
    $sub = $bmp.Clone((New-Object System.Drawing.Rectangle $x, $y, $w, $h), $bmp.PixelFormat)
    $res = Get-InkBoxNum $sub
    $sub.Dispose()
    return $res
}

# --- run --------------------------------------------------------------------
try {
    foreach ($name in $cases.Keys) {
        $md = Write-Fixture $name $cases[$name]
        $r = Invoke-Render $md
        $script:dumps[$name] = @(Parse-Dump $r.Out)
        $script:dumps[$name + '!png'] = $r.Png
        $script:dumps[$name + '!raw'] = $r.Out
    }

    $ref = Get-Case 'text-only'
    if ($null -eq $ref) { throw 'the reference render produced no lines at all' }
    $refH = $ref.LineH
    Write-Output ('reference text line: lineH=' + $refH)
    Write-Output ''

    # ---- A. every input form reaches the math path --------------------------
    Write-Output '--- A: each input form lands in the math path ---'
    foreach ($name in 'inline-usd','inline-paren','display-usd','display-brk','bare-cmd','mathml','sqrt-index') {
        $ln = Get-Case $name
        $r = if ($null -ne $ln) { Get-MathRun $ln } else { $null }
        if ($null -eq $r) {
            $raw = $script:dumps[$name + '!raw']
            Check $false $name 'no MATH run -- it rendered as plain text (see the dump below)'
            Write-Output ('       ' + (($raw -split "`n") | Select-Object -Skip 1 | Select-Object -First 3 | Out-String).Trim())
        }
        else {
            Check $true $name ('MATH ' + [Math]::Round($r.BoxW,1) + 'x' + [Math]::Round($r.BoxH + $r.BoxD,1))
        }
    }

    # ---- B. the currency guard ---------------------------------------------
    Write-Output ''
    Write-Output '--- B: two prices in one sentence are not one formula ---'
    $cur = Get-Case 'currency'
    $curMath = @($cur.Runs | Where-Object { $_.Math })
    $curText = ''
    foreach ($r in $cur.Runs) { if (-not $r.Math) { $curText += $r.Text } }
    Check ($curMath.Count -eq 0) 'currency' ('' + $curMath.Count + ' MATH run(s), expected 0')
    Check ($curText.Contains('$100') -and $curText.Contains('$50')) 'currency text' ('"' + $curText + '"')

    # ---- C. display math is taller, and has the parts it needs --------------
    Write-Output ''
    Write-Output '--- C: a display formula is taller than a text row, and has real parts ---'
    $disp = Get-Case 'display-usd'
    Check ($disp.LineH -gt $refH * 2) 'display height' ('' + [Math]::Round($disp.LineH,1) + 'px vs a text row''s ' + $refH + 'px')
    Check ((@(Get-Prims $disp 'rule')).Count -ge 1) 'fraction bar' ('' + (@(Get-Prims $disp 'rule')).Count + ' rule(s)')

    $brk = Get-Case 'display-brk'
    Check ((@(Get-Prims $brk 'glyph')).Count -ge 10) 'integral glyphs' ('' + (@(Get-Prims $brk 'glyph')).Count + ' glyphs')
    Check ((@(Get-Prims $brk 'rule')).Count -ge 2) 'integral bars' ('' + (@(Get-Prims $brk 'rule')).Count + ' rule(s) for the fraction and the radical')

    $sq = Get-Case 'sqrt-index'
    Check ((@(Get-Prims $sq 'poly')).Count -ge 1) 'radical tick' ('' + (@(Get-Prims $sq 'poly')).Count + ' polyline(s)')
    Check ((@(Get-Prims $sq 'rule')).Count -ge 1) 'radical bar' ('' + (@(Get-Prims $sq 'rule')).Count + ' rule(s)')

    $mm = Get-Case 'mathml'
    $mmlText = ''
    foreach ($r in $mm.Runs) { if (-not $r.Math) { $mmlText += $r.Text } }
    Check ($mmlText -notlike '*<math*') 'MathML not echoed' ('"' + $mmlText + '"')
    Check ((@(Get-Prims $mm 'rule')).Count -ge 1) 'mfrac bar' ('' + (@(Get-Prims $mm 'rule')).Count + ' rule(s)')

    # ---- D. the unit check --------------------------------------------------
    #
    # This is the one that matters. The layout's vertical model is pixels end to end; it used
    # to be POINTS while the fonts were drawn in pixels, so every glyph's real cell was
    # DpiY/72 (1.3333x at 96 DPI) taller than the box that said it contained it -- numerators
    # sitting on fraction bars, matrix rows colliding, the integral's limits inside the
    # integral. Reading the dump could not show it either, because the dump was in the same
    # wrong unit. Font.Height never was: it comes straight from GDI. So compare against it.
    Write-Output ''
    Write-Output '--- D: the layout unit is the font pixel, not the point ---'
    $worst = 0.0; $worstAt = ''
    $nGlyph = 0
    foreach ($name in $cases.Keys) {
        $ln = Get-Case $name
        if ($null -eq $ln) { continue }
        foreach ($g in @(Get-Prims $ln 'glyph')) {
            $nGlyph++
            $d = [Math]::Abs(($g.Asc + $g.Desc) - $g.Px)
            if ($d -gt $worst) { $worst = $d; $worstAt = $name + ' size=' + $g.Size }
        }
    }
    Check ($nGlyph -gt 0) 'glyphs seen' ('' + $nGlyph + ' glyph prim(s) across the cases')
    Check ($worst -le 1.0) 'asc+desc == Font.Height' ('worst off by ' + [Math]::Round($worst,2) + 'px (' + $worstAt + '); 4/3 of this would mean points')

    # ---- E. containment -----------------------------------------------------
    Write-Output ''
    Write-Output '--- E: every prim sits inside the box that claims to contain it ---'
    $esc = @()
    foreach ($name in $cases.Keys) {
        $ln = Get-Case $name
        if ($null -eq $ln) { continue }
        $r = Get-MathRun $ln
        if ($null -eq $r) { continue }
        $baseY = $ln.Y + $ln.Base
        $top = $baseY - $r.BoxH
        $bot = $baseY + $r.BoxD
        foreach ($p in $ln.Prims) {
            if ($p.Y0 -lt $top - 1.5 -or $p.Y1 -gt $bot + 1.5 -or
                $p.X -lt $r.X - 2.0 -or $p.X1 -gt $r.X + $r.BoxW + 2.0) {
                $esc += ($name + ' ' + $p.Kind + ' y ' + [Math]::Round($p.Y0,1) + '..' + [Math]::Round($p.Y1,1) +
                         ' vs box ' + [Math]::Round($top,1) + '..' + [Math]::Round($bot,1))
            }
        }
    }
    Check ($esc.Count -eq 0) 'containment' ('' + $esc.Count + ' prim(s) outside their box')
    foreach ($e in ($esc | Select-Object -First 8)) { Write-Output ('       ' + $e) }

    # ---- F. the bars are solid ---------------------------------------------
    #
    # A 1px rule at a fractional y gets antialiased across two rows at 50% each: the model is
    # right and the pixels say "light grey line". So this one has to look at the PNG. The test
    # is the MINIMUM luminance along the rule's own row -- a solid row has pixels at the ink
    # colour, a 50/50 split has none below ~128 on a light ground. Sampling the minimum rather
    # than the mean is what makes it immune to a glyph overlapping the far end of the bar.
    Write-Output ''
    Write-Output '--- F: fraction and radical bars are solid, not half-lit ---'
    $faint = @(); $nRule = 0; $worstRule = 999.0
    foreach ($name in $cases.Keys) {
        $ln = Get-Case $name
        if ($null -eq $ln) { continue }
        $rules = @(Get-Prims $ln 'rule')
        if ($rules.Count -eq 0) { continue }
        $bmp = New-Object System.Drawing.Bitmap $script:dumps[$name + '!png']
        foreach ($p in $rules) {
            $nRule++
            $yy = [int][Math]::Round([double]$p.Y)
            $x0 = [int][Math]::Max(0, [Math]::Round([double]$p.X) + 1)
            $x1 = [int][Math]::Min($bmp.Width - 1, [Math]::Round([double]$p.X1) - 1)
            if ($yy -lt 0 -or $yy -ge $bmp.Height -or $x1 -lt $x0) { $bmp.Dispose(); continue }
            $min = 255.0
            for ($xx = $x0; $xx -le $x1; $xx++) {
                $c = $bmp.GetPixel($xx, $yy)
                $lum = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
                if ($lum -lt $min) { $min = $lum }
            }
            if ($min -lt $worstRule) { $worstRule = $min }
            if ($min -ge 100.0) {
                $faint += ($name + ' rule at y=' + $yy + ' darkest pixel ' + [Math]::Round($min,1))
            }
        }
        $bmp.Dispose()
    }
    Check ($nRule -gt 0) 'rules sampled' ('' + $nRule + ' bar(s)')
    Check ($faint.Count -eq 0) 'bars are solid' ('darkest pixel over all bars = ' + [Math]::Round($worstRule,1) + ', 100 or more means the bar is split across two rows')
    foreach ($e in ($faint | Select-Object -First 8)) { Write-Output ('       ' + $e) }

    # ---- G. the inline baseline --------------------------------------------
    #
    # The renderer carries its own baseline model so that an inline formula and the prose
    # around it sit on ONE line. If Base and TextShift ever drift apart the formula keeps its
    # box and the row keeps its height -- only the TEXT moves, so every check above would
    # still pass. The ink BOTTOMs are what move, and the prose on either side of the fixture's
    # formula ('Inline dollar math ' / ' sits in this line.') has no descenders, so its ink
    # bottom IS the baseline.
    #
    # The fixture is a nested power on purpose. `$x^2+1$` lifts its line by 2px, which a
    # 2px tolerance calls equal -- the check would pass with TextShift pinned to zero, i.e.
    # it would be measuring nothing. `$x^{2^{2}}$` lifts it by 9px (the dump's shift= field),
    # so "the text is 9px above the formula" cannot be mistaken for "they agree".
    Write-Output ''
    Write-Output '--- G: an inline formula shares the text baseline ---'
    $ln = Get-Case 'inline-usd'
    $mr = Get-MathRun $ln
    $mi = -1
    for ($i = 0; $i -lt $ln.Runs.Count; $i++) { if ($ln.Runs[$i].Math) { $mi = $i; break } }
    # The run right after a formula is a bare space; skip blanks or the crop is 4px of
    # background and comes back "no ink", which reads as a broken renderer.
    $after = $null
    for ($i = $mi + 1; $i -lt $ln.Runs.Count; $i++) {
        if ($ln.Runs[$i].Math) { continue }
        if ($ln.Runs[$i].Text.Trim().Length -eq 0) { continue }
        $after = $ln.Runs[$i]; break
    }
    if ($null -eq $after) { throw 'the inline fixture has no prose after its formula' }
    $bmp = New-Object System.Drawing.Bitmap $script:dumps['inline-usd!png']
    $y0 = [int][Math]::Round($ln.Y) - 2
    $hh = [int][Math]::Ceiling($ln.Pitch) + 6
    $bTxt = Get-BmpInkBox $bmp ([int][Math]::Round($after.X) + 1) $y0 ([int][Math]::Floor($after.W) - 1) $hh
    $bMath = Get-BmpInkBox $bmp ([int][Math]::Round($mr.X) + 1) $y0 ([int][Math]::Floor($mr.BoxW) - 1) $hh
    $bmp.Dispose()
    if ($null -eq $bTxt -or $null -eq $bMath) {
        Check $false 'inline baseline' 'no ink in one of the two regions -- the crop is wrong, not the renderer'
    }
    else {
        $d = [Math]::Abs($bTxt.B - $bMath.B)
        Check ($d -le 2) 'inline baseline' ('prose ink bottom ' + $bTxt.B + ', formula ink bottom ' + $bMath.B +
                                           ', off by ' + $d + 'px (the dump says the line lifts the text ' +
                                           [Math]::Round($ln.Shift,1) + 'px)')
    }

    Write-Output ''
    Write-Output ('renders kept in ' + $work)
    if ($script:fail -eq 0) { Write-Output 'PASS: all three input forms render as formulas, on the text baseline, in a box big enough for them.' }
    else { Write-Output "FAIL: $($script:fail) check(s) failed." }
}
finally {
    Stop-BangGang
}

Write-BBDone 'math-render'
