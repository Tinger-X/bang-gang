# Item 5: is dragging the window by the chrome bar actually laggy, and WHY?
#
# The complaint: with a large (non-maximised) window and a conversation on screen, dragging
# the window by the app's top bar feels sticky / laggy. Two suspects differ between the
# user's daily build and a plain one:
#
#   * Opacity 0.87 (the user's setting) -> WinForms turns the window into a LAYERED window
#     (WS_EX_LAYERED + LWA_ALPHA), and DWM composes those on a slower path whose cost grows
#     with window AREA -- "large window" is half of the repro.
#   * WDA_EXCLUDEFROMCAPTURE (always on in Release) -- same family of DWM special-casing.
#
# This probe drags the window with synthetic input and measures TWO different lags, because
# they have different owners:
#
#   * LOGICAL lag: cursor position vs GetWindowRect(). The modal HTCAPTION move loop updates
#     the window position as it dequeues mouse moves, so this lag is the MESSAGE LOOP
#     falling behind -- stalls inside the app itself.
#   * VISIBLE lag: cursor position vs where the window's left edge actually IS on screen,
#     found by capturing a thin strip across the chrome bar and locating the window's edge
#     column in it. This is the DWM / presentation lag -- what the user's eye actually
#     judges.
#
# The strip measures the edge against whatever is BEHIND the window, so the probe puts its
# own backdrop there: a fullscreen borderless plain-white form shown before the app starts
# (the app is topmost, it always stays above). Without it the "desktop" behind the window
# is wallpaper + icons + the very terminal the probe runs in, and each of those has
# poisoned this measurement at least once: a white desktop made the chrome invisible
# (0.9.5), an accent-blue one made the border invisible (first 0.9.6 rerun), an uncovered
# icon posed as the edge (216px bogus "lag"), and a line of blue terminal text failed the
# margin-uniformity guard. The marker itself is still learned per leg (whichever edge
# column -- border or chrome interior -- sits farther from the backdrop), so the probe
# keeps working if the backdrop colour ever changes.
#
# Three legs isolate the suspects:
#
#   A  Debug + opacity 1.00   baseline (no layering, no affinity)
#   B  Debug + opacity 0.87   + layered window        -> opacity cost = B - A
#   C  Release + opacity 1.00 + WDA_EXCLUDEFROMCAPTURE -> affinity cost = C - A
#
# Leg C cannot be measured visually -- excluding the window from capture is the whole point
# of WDA, the strip shows the desktop behind it -- so C contributes logical lag only.
#
# The drag is horizontal-only so the captured strip (fixed screen band across the chrome
# bar) keeps the window's edge inside it for the whole sweep.
#
# No PASS/FAIL verdict: this is a diagnosis instrument. It prints the numbers and a
# computed DIAGNOSIS line; the fix follows from which delta is large.
#
# Usage:  powershell -File tools\drag-perf.ps1

. "$PSScriptRoot\_ui.ps1"

$script:fail = 0

$repoRoot = Split-Path $PSScriptRoot -Parent
$exeDbg = Join-Path $repoRoot 'build\bin\Debug\net8.0-windows\BangGang.exe'
$exeRel = Join-Path $repoRoot 'build\bin\Release\net8.0-windows\BangGang.exe'

$MEF_DOWN = [uint32]0x0002
$MEF_UP   = [uint32]0x0004

# The big-window legs resize the app's window before dragging (the user's real config is
# opacity 0.50 + a much larger window than the 1200x800 startup size; DWM's layered cost
# grows with AREA, so area and layering must be crossed). BB has no MoveWindow.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BBX {
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
}
'@

# --- settings.json: set Opacity for one leg, restore afterwards ------------------------

function Push-Opacity([string]$exe, [double]$op) {
    $path = Join-Path (Split-Path $exe -Parent) 'settings.json'
    $bk = @{ Path = $path; Had = (Test-Path $path); Raw = $null }
    if ($bk.Had) { $bk.Raw = Get-Content $path -Raw }
    $cfg = $null
    if ($bk.Had) { try { $cfg = $bk.Raw | ConvertFrom-Json } catch { $cfg = $null } }
    if ($null -ne $cfg) {
        if ($cfg.PSObject.Properties['Opacity']) { $cfg.Opacity = $op }
        else { $cfg | Add-Member -NotePropertyName Opacity -NotePropertyValue $op }
        $json = $cfg | ConvertTo-Json -Depth 8 -Compress
    }
    else {
        $json = (@{ Opacity = $op } | ConvertTo-Json -Compress)
    }
    Set-Content -Path $path -Value $json -Encoding utf8
    return $bk
}

function Pop-Opacity($bk) {
    if ($null -eq $bk) { return }
    if ($bk.Had) { Set-Content -Path $bk.Path -Value $bk.Raw -Encoding utf8 -NoNewline }
    else { Remove-Item $bk.Path -ErrorAction SilentlyContinue }
}

# --- the known-solid backdrop ------------------------------------------------------------
#
# A fullscreen borderless white form behind the app under test (the app is topmost, so it
# always stays above). See the header for why the measurement cannot trust the real
# desktop. Painted once via DoEvents at Show time; DWM keeps the redirected surface, so
# the regions the moving window uncovers recomposite white without any further pumping.
# The probe thread never runs a message loop, and none is needed.

$script:backdrop = $null

function Show-Backdrop {
    $f = New-Object System.Windows.Forms.Form
    $f.FormBorderStyle = 'None'
    $f.StartPosition = 'Manual'
    $f.Location = New-Object System.Drawing.Point 0, 0
    $f.Size = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Size
    $f.BackColor = [System.Drawing.Color]::White
    $f.ShowInTaskbar = $false
    $f.TopMost = $false
    $f.Show()
    [System.Windows.Forms.Application]::DoEvents()
    $f.Refresh()
    [System.Windows.Forms.Application]::DoEvents()
    $script:backdrop = $f
}

function Hide-Backdrop {
    if ($null -ne $script:backdrop) {
        try { $script:backdrop.Close() } catch {}
        try { $script:backdrop.Dispose() } catch {}
        $script:backdrop = $null
    }
}

# --- pixel helpers ---------------------------------------------------------------------

# Max per-channel distance between two colours.
function Get-ChannelDist($a, $b) {
    return [Math]::Max([Math]::Abs([int]$a.R - [int]$b.R),
            [Math]::Max([Math]::Abs([int]$a.G - [int]$b.G),
                        [Math]::Abs([int]$a.B - [int]$b.B)))
}

# Leftmost x (STRIP-relative) where $run consecutive pixels all sit WITHIN $tol of $marker
# on every channel. -1 = not found. The run length does the anti-icon work: a desktop icon
# uncovered by the moving window can accidentally match the marker for a pixel or two
# (white glyph strokes vs near-white chrome), but never for a long solid run -- the real
# edge is followed by hundreds of marker pixels. $marker is learned per leg (see the
# calibration in Measure-Leg), already blended by the leg's opacity -- exactly what the
# drag frames will show.
function Find-Edge($bmp, $marker, [int]$tol, [int]$run) {
    $w = $bmp.Width
    $y = [int]($bmp.Height / 2)
    for ($x = 0; $x -le $w - $run; $x++) {
        $ok = $true
        for ($k = 0; $k -lt $run; $k++) {
            if ((Get-ChannelDist $bmp.GetPixel($x + $k, $y) $marker) -gt $tol) { $ok = $false; break }
        }
        if ($ok) { return $x }
    }
    return -1
}

# --- one leg ---------------------------------------------------------------------------
#
# Leaves the result in $script:legRet: @{ Samples; GrabX; Wx0; Edge0 }, or $null when the
# leg voided itself. NOTHING is returned through the pipeline: every Write-Output inside a
# function lands in its return value, so "return the data AND log progress" must not share
# one channel -- a captured array of log strings + hashtable reads back as garbage.
# The drag path is +300px right, then back to the start, in 8px cursor steps.

$script:legRet = $null

function Measure-Leg([string]$tag, [string]$exe, [double]$op, [bool]$capturable, [int]$bigW = 0, [int]$bigH = 0) {
    $script:legRet = $null
    $script:BBExe = $exe
    $bk = Push-Opacity $exe $op
    $samples = @()
    try {
        $main = Start-BangGang
        if ($bigW -gt 0) {
            [void][BBX]::MoveWindow($main, 100, 30, $bigW, $bigH, $true)
            Start-Sleep -Milliseconds 700      # let the resize layout settle
        }
        $mr = Get-WinRect $main
        $grabX = [int](($mr.Left + $mr.Right) / 2)
        $grabY = $mr.Top + 19
        Write-Output ('  window ' + $mr.Left + ',' + $mr.Top + ' ' + ($mr.Right - $mr.Left) + 'x' + ($mr.Bottom - $mr.Top))

        # Park the cursor on the grab point first (same pin-down prelude as Invoke-Drag).
        [void][BB]::SetCursorPos($grabX, $grabY)
        Start-Sleep -Milliseconds 250

        $stripX = $mr.Left - 60
        $stripW = 440          # covers the +300px sweep plus margins on both sides
        $stripH = 6
        $stripY = $grabY - 3

        # Calibration: the strip starts exactly 60px left of the window, so strip-relative
        # x=60 IS the window's left edge at rest. Learn the backdrop colour at x=10 (the
        # window only ever moves RIGHT, so x<60 stays uncovered backdrop for the whole
        # leg) and pick as the edge marker whichever of the two columns at the edge -- the
        # 1px accent border (x=60) or the chrome interior (x=68) -- sits FARTHER from the
        # backdrop. The border is only 1-2px wide, so matching it takes a run of 2; the
        # chrome is a solid expanse, so matching it demands a run of 8, which no stray
        # pixel can fake. At opacity < 1 both are already the blended values, i.e.
        # exactly what the drag frames will show. Demand the winner beat the backdrop by
        # > 48 (Find-Edge matches within 24, so this keeps the backdrop itself 2x tol
        # away from posing as the marker), and demand the marker resolve back to
        # x=60 +- 2 -- that single assertion proves the whole learn-and-find round-trip
        # works on THIS backdrop at THIS opacity.
        $bmp = New-Object System.Drawing.Bitmap $stripW, $stripH
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($stripX, $stripY, 0, 0, (New-Object System.Drawing.Size $stripW, $stripH))
        $g.Dispose()
        $midY = [int]($stripH / 2)
        $deskC = $bmp.GetPixel(10, $midY)
        $candA = $bmp.GetPixel(60, $midY)
        $candB = $bmp.GetPixel(68, $midY)
        $dA = Get-ChannelDist $candA $deskC
        $dB = Get-ChannelDist $candB $deskC
        if ($dA -ge $dB) { $marker = $candA; $md = $dA; $run = 2 }
        else { $marker = $candB; $md = $dB; $run = 8 }
        $edge0 = Find-Edge $bmp $marker 24 $run
        if ($capturable) {
            $uniform = $true
            foreach ($px in 20, 30, 40, 50) {
                $p = $bmp.GetPixel($px, $midY)
                if ((Get-ChannelDist $p $deskC) -gt 16) { $uniform = $false }
            }
            $bmp.Dispose()
            if (-not $uniform) {
                Write-Output ('  FAIL -- the strip''s left margin is not uniform backdrop (' +
                    'the backdrop form did not cover it?); the learned backdrop colour is ' +
                    'unreliable, this leg says nothing')
                $script:fail++
                return
            }
            if ($md -le 48) {
                Write-Output ('  FAIL -- at this opacity neither edge column stands out from ' +
                    'the backdrop (border ' + $dA + ' levels, chrome ' + $dB + ', need > 48); ' +
                    'no usable edge marker, this leg says nothing')
                $script:fail++
                return
            }
            if ($edge0 -lt 0 -or [Math]::Abs($edge0 - 60) -gt 2) {
                Write-Output ('  FAIL -- at rest the strip shows the edge at strip+' +
                    $(if ($edge0 -lt 0) { 'nowhere' } else { $edge0 }) +
                    ', expected 60; calibration broken, this leg says nothing')
                $script:fail++
                return
            }
        }
        else {
            $bmp.Dispose()
        }

        # Grab the chrome bar. Same pin-down prelude as Invoke-Drag: the app reads
        # Cursor.Position when it DEQUEUES the button-down, so hold still first.
        [BB]::mouse_event($MEF_DOWN, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 150
        [void][BB]::SetCursorPos($grabX, $grabY)
        Start-Sleep -Milliseconds 300

        $wx0 = (Get-WinRect $main).Left
        $edgeRest = $mr.Left
        $path = @()
        for ($d = 8; $d -le 300; $d += 8) { $path += $d }
        for ($d = 292; $d -ge 0; $d -= 8) { $path += $d }

        $t0 = [System.Diagnostics.Stopwatch]::StartNew()
        foreach ($off in $path) {
            $cx = $grabX + $off
            [void][BB]::SetCursorPos($cx, $grabY)
            # Pacing: without the per-step CopyFromScreen (non-capturable legs) this loop
            # runs at ~1ms/step, and the app's deliberate 5ms SetWindowPos throttle
            # (protects 1000Hz gaming mice, see MainForm.ApplyWindowDrag) then rate-limits
            # the window mid-sweep -- the samples measure the throttle, not the drag.
            # 8ms is 125Hz, a real mouse rate at which the WDA drag measures pixel-perfect
            # (tools/drag-wda-debug.ps1 pass "mouse-125hz").
            if (-not $capturable) { Start-Sleep -Milliseconds 8 }
            $t = $t0.ElapsedMilliseconds
            $wx = (Get-WinRect $main).Left
            $ex = -1
            if ($capturable) {
                $bmp = New-Object System.Drawing.Bitmap $stripW, $stripH
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($stripX, $stripY, 0, 0, (New-Object System.Drawing.Size $stripW, $stripH))
                $g.Dispose()
                $edge = Find-Edge $bmp $marker 24 $run
                $bmp.Dispose()
                if ($edge -ge 0) { $ex = $stripX + $edge }
            }
            $samples += @{ T = $t; CX = $cx; WX = $wx; EX = $ex }
        }
        [BB]::mouse_event($MEF_UP, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 300

        # Sanity: the drag actually happened. A leg where the window never moved looks
        # perfectly smooth in every lag statistic -- the classic quiet-green trap.
        $wxEnd = (Get-WinRect $main).Left
        $maxShift = 0
        foreach ($s in $samples) { $maxShift = [Math]::Max($maxShift, [Math]::Abs($s.WX - $wx0)) }
        Write-Output ('  drag travel: logical max ' + $maxShift + 'px, ended at ' + $wxEnd + ' (start ' + $wx0 + ')')
        if ($maxShift -lt 250) {
            Write-Output '  FAIL -- the window barely moved; the grab missed the chrome bar and every number below is void'
            $script:fail++
            return
        }

        # stash the anchors the stats need
        $script:legRet = @{ Samples = $samples; GrabX = $grabX; Wx0 = $wx0; Edge0 = $edgeRest }
    }
    finally {
        Stop-BangGang | Out-Null
        Pop-Opacity $bk
    }
}

# --- statistics ------------------------------------------------------------------------

function Get-P95($vals) {
    if ($vals.Count -eq 0) { return -1 }
    $s = @($vals | Sort-Object)
    return $s[[int][Math]::Floor(0.95 * ($s.Count - 1))]
}

$script:legStats = $null

function Show-LegStats([string]$tag, $leg) {
    $samples = $leg.Samples
    $logLags = @()
    $visLags = @()
    foreach ($s in $samples) {
        $logLags += [Math]::Abs(($s.CX - $leg.GrabX) - ($s.WX - $leg.Wx0))
        if ($s.EX -ge 0) { $visLags += [Math]::Abs(($s.CX - $leg.GrabX) - ($s.EX - $leg.Edge0)) }
    }
    $durS = [Math]::Max(1, $samples[$samples.Count - 1].T) / 1000.0
    $distinctWx = @($samples | ForEach-Object { $_.WX } | Sort-Object -Unique).Count
    $logMean = ($logLags | Measure-Object -Average).Average
    Write-Output ('  logical  mean|lag| ' + [Math]::Round($logMean, 1) + 'px  p95 ' + (Get-P95 $logLags) +
                  'px  max ' + ($logLags | Measure-Object -Maximum).Maximum + 'px  updates/s ' +
                  [Math]::Round($distinctWx / $durS, 1))
    if ($visLags.Count -gt 10) {
        $visMean = ($visLags | Measure-Object -Average).Average
        Write-Output ('  visible  mean|lag| ' + [Math]::Round($visMean, 1) + 'px  p95 ' + (Get-P95 $visLags) +
                      'px  max ' + ($visLags | Measure-Object -Maximum).Maximum + 'px  (' + $visLags.Count + ' edge reads)')
    }
    else {
        Write-Output ('  visible  not measurable (' + $visLags.Count + ' edge reads -- window excluded from capture)')
    }
    # Same pipeline-pollution rule as Measure-Leg: the verdict lines above must not ride
    # home inside the return value, so the stats go out via $script:legStats.
    $script:legStats = @{
        LogP95 = [double](Get-P95 $logLags)
        VisP95 = [double]($(if ($visLags.Count -gt 10) { Get-P95 $visLags } else { -1 }))
    }
}

# --- run -------------------------------------------------------------------------------

$legs = @(
    @{ Tag = 'A'; Desc = 'A: Debug, opacity 1.00 (baseline)';        Exe = $exeDbg; Op = 1.0;  Cap = $true;  W = 0;    H = 0;   Wda = $false },
    @{ Tag = 'B'; Desc = 'B: Debug, opacity 0.87 (layered)';         Exe = $exeDbg; Op = 0.87; Cap = $true;  W = 0;    H = 0;   Wda = $false },
    @{ Tag = 'C'; Desc = 'C: Release, opacity 1.00 (+WDA)';          Exe = $exeRel; Op = 1.0;  Cap = $false; W = 0;    H = 0;   Wda = $false },
    @{ Tag = 'D'; Desc = 'D: Debug, opacity 1.00, 1700x980 (area)';  Exe = $exeDbg; Op = 1.0;  Cap = $true;  W = 1700; H = 980; Wda = $false },
    @{ Tag = 'E'; Desc = 'E: Debug, opacity 0.50, 1700x980 (user)';  Exe = $exeDbg; Op = 0.5;  Cap = $true;  W = 1700; H = 980; Wda = $false },
    @{ Tag = 'F'; Desc = 'F: Debug, opacity 1.00, +WDA (isolate)';   Exe = $exeDbg; Op = 1.0;  Cap = $false; W = 0;    H = 0;   Wda = $true },
    @{ Tag = 'G'; Desc = 'G: Release, opacity 1.00, +WDA (repeat)';  Exe = $exeRel; Op = 1.0;  Cap = $false; W = 0;    H = 0;   Wda = $false }
)

$stats = @{}
Invoke-BBProbe {
    Show-Backdrop
    try {
        foreach ($leg in $legs) {
            Write-Output ('--- leg ' + $leg.Desc + ' ---')
            if (-not (Test-Path $leg.Exe)) {
                Write-Output ('  FAIL -- exe missing: ' + $leg.Exe)
                $script:fail++
                continue
            }
            $script:BBNoCaptureEnv = [bool]$leg.Wda
            try { Measure-Leg $leg.Tag $leg.Exe $leg.Op $leg.Cap $leg.W $leg.H }
            finally { $script:BBNoCaptureEnv = $false }   # do not leak the WDA leg into later legs
            if ($null -ne $script:legRet) {
                Show-LegStats $leg.Tag $script:legRet
                $stats[$leg.Tag] = $script:legStats
            }
            Write-Output ''
        }
    }
    finally { Hide-Backdrop }
}

if ($stats.ContainsKey('A') -and $stats.ContainsKey('B')) {
    $dOpacityVis = $stats['B'].VisP95 - $stats['A'].VisP95
    $dOpacityLog = $stats['B'].LogP95 - $stats['A'].LogP95
    Write-Output ('delta B-A (opacity): visible p95 ' + $(if ($dOpacityVis -ge 0) { '+' } else { '' }) + [Math]::Round($dOpacityVis, 0) +
                  'px, logical p95 ' + $(if ($dOpacityLog -ge 0) { '+' } else { '' }) + [Math]::Round($dOpacityLog, 0) + 'px')
    if ($dOpacityVis -gt 40) {
        Write-Output 'DIAGNOSIS: the layered window (opacity < 1) is the main cost. Indicated fix: go temporarily opaque while the drag modal loop runs (WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE).'
    }
    elseif ($dOpacityLog -gt 40) {
        Write-Output 'DIAGNOSIS: the message loop falls behind under opacity < 1, not the composition. Indicated fix: look for per-move work inside the app, not DWM.'
    }
    else {
        Write-Output 'DIAGNOSIS: opacity makes no measurable difference at this window size; the cause is elsewhere (or below this instruments floor).'
    }
}
if ($stats.ContainsKey('A') -and $stats.ContainsKey('C')) {
    $dWdaLog = $stats['C'].LogP95 - $stats['A'].LogP95
    Write-Output ('delta C-A (WDA, release build): logical p95 ' + $(if ($dWdaLog -ge 0) { '+' } else { '' }) + [Math]::Round($dWdaLog, 0) + 'px (visible not measurable -- excluded from capture)')
}

if ($stats.ContainsKey('D') -and $stats.ContainsKey('E')) {
    $dBigVis = $stats['E'].VisP95 - $stats['D'].VisP95
    $dBigLog = $stats['E'].LogP95 - $stats['D'].LogP95
    Write-Output ('delta E-D (opacity 0.50 at 1700x980): visible p95 ' + $(if ($dBigVis -ge 0) { '+' } else { '' }) + [Math]::Round($dBigVis, 0) +
                  'px, logical p95 ' + $(if ($dBigLog -ge 0) { '+' } else { '' }) + [Math]::Round($dBigLog, 0) + 'px')
    if ($dBigVis -gt 40) {
        Write-Output 'DIAGNOSIS: layering costs grow with area -- at the user size/opacity the visible edge trails. The fix must NOT touch opacity (0.9.6 reverted that on user request); candidates: shrink the layered surface (per-monitor?) or accept and report.'
    }
}
Write-Output ''
if ($script:fail -eq 0) { Write-Output 'OK: all legs measured (read the deltas above).' }
else { Write-Output "FAIL: $($script:fail) check(s) failed -- the measurement itself is untrustworthy." }

Write-BBDone 'drag-perf'
