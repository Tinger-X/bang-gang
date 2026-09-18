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
#     found by capturing a thin strip across the chrome bar and locating the chrome colour
#     in it. This is the DWM / presentation lag -- what the user's eye actually judges.
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

# --- pixel helpers ---------------------------------------------------------------------

function Read-Pixel([int]$x, [int]$y) {
    $bmp = New-Object System.Drawing.Bitmap 1, 1
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size 1, 1))
    $g.Dispose()
    $c = $bmp.GetPixel(0, 0)
    $bmp.Dispose()
    return $c
}

# Leftmost x (STRIP-relative) where 4 consecutive pixels sit within $tol of $c0 on every
# channel. -1 = not found. The run requirement keeps a stray same-coloured desktop icon
# pixel from posing as the edge.
function Find-Edge($bmp, $c0, [int]$tol) {
    $w = $bmp.Width
    $y = [int]($bmp.Height / 2)
    $run = 0
    for ($x = 0; $x -lt $w; $x++) {
        $c = $bmp.GetPixel($x, $y)
        if ([Math]::Abs([int]$c.R - [int]$c0.R) -le $tol -and
            [Math]::Abs([int]$c.G - [int]$c0.G) -le $tol -and
            [Math]::Abs([int]$c.B - [int]$c0.B) -le $tol) {
            $run++
            if ($run -ge 4) { return $x - 3 }
        }
        else { $run = 0 }
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

function Measure-Leg([string]$tag, [string]$exe, [double]$op, [bool]$capturable) {
    $script:legRet = $null
    $script:BBExe = $exe
    $bk = Push-Opacity $exe $op
    $samples = @()
    try {
        $main = Start-BangGang
        $mr = Get-WinRect $main
        $grabX = [int](($mr.Left + $mr.Right) / 2)
        $grabY = $mr.Top + 19
        Write-Output ('  window ' + $mr.Left + ',' + $mr.Top + ' ' + ($mr.Right - $mr.Left) + 'x' + ($mr.Bottom - $mr.Top))

        # Learn the chrome colour AT the grab point. For the layered leg this is the
        # blended value (0.87 x chrome + 0.13 x desktop) -- exactly what the drag frames
        # will show, which is why the learn must happen per leg, after the app is up.
        [void][BB]::SetCursorPos($grabX, $grabY)
        Start-Sleep -Milliseconds 250
        $c0 = Read-Pixel $grabX $grabY

        $stripX = $mr.Left - 60
        $stripW = 440          # covers the +300px sweep plus margins on both sides
        $stripH = 6
        $stripY = $grabY - 3

        # Calibration: at rest, the detected edge must be the window's real left edge.
        $bmp = New-Object System.Drawing.Bitmap $stripW, $stripH
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($stripX, $stripY, 0, 0, (New-Object System.Drawing.Size $stripW, $stripH))
        $g.Dispose()
        $edge0 = Find-Edge $bmp $c0 16
        $bmp.Dispose()
        if ($capturable) {
            if ($edge0 -lt 0 -or [Math]::Abs(($stripX + $edge0) - $mr.Left) -gt 3) {
                Write-Output ('  FAIL -- at rest the strip shows the edge at ' +
                    $(if ($edge0 -lt 0) { 'nowhere' } else { $stripX + $edge0 }) +
                    ', the window is at ' + $mr.Left + '; calibration broken, this leg says nothing')
                $script:fail++
                return
            }
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
            $t = $t0.ElapsedMilliseconds
            $wx = (Get-WinRect $main).Left
            $bmp = New-Object System.Drawing.Bitmap $stripW, $stripH
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($stripX, $stripY, 0, 0, (New-Object System.Drawing.Size $stripW, $stripH))
            $g.Dispose()
            $edge = Find-Edge $bmp $c0 16
            $bmp.Dispose()
            $ex = -1
            if ($edge -ge 0) { $ex = $stripX + $edge }
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
    @{ Tag = 'A'; Desc = 'A: Debug, opacity 1.00 (baseline)';    Exe = $exeDbg; Op = 1.0;  Cap = $true },
    @{ Tag = 'B'; Desc = 'B: Debug, opacity 0.87 (layered)';     Exe = $exeDbg; Op = 0.87; Cap = $true },
    @{ Tag = 'C'; Desc = 'C: Release, opacity 1.00 (+WDA)';      Exe = $exeRel; Op = 1.0;  Cap = $false }
)

$stats = @{}
Invoke-BBProbe {
    foreach ($leg in $legs) {
        Write-Output ('--- leg ' + $leg.Desc + ' ---')
        if (-not (Test-Path $leg.Exe)) {
            Write-Output ('  FAIL -- exe missing: ' + $leg.Exe)
            $script:fail++
            continue
        }
        Measure-Leg $leg.Tag $leg.Exe $leg.Op $leg.Cap
        if ($null -ne $script:legRet) {
            Show-LegStats $leg.Tag $script:legRet
            $stats[$leg.Tag] = $script:legStats
        }
        Write-Output ''
    }
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

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'OK: all three legs measured (read the deltas above).' }
else { Write-Output "FAIL: $($script:fail) check(s) failed -- the measurement itself is untrustworthy." }

Write-BBDone 'drag-perf'
