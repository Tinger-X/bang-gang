# Item 1 (round 2): WHY does the window crawl behind the cursor on a fast drag?
#
# drag-perf.ps1 paces its drag with a screen capture per step (~50-70ms/step), which gives
# the app all the time in the world -- it can never reproduce "mouse is already at B, the
# window is still crawling from A to B". This probe removes the per-step capture and drags
# as fast as SetCursorPos can go, then keeps sampling the window position for 800ms AFTER
# the cursor stops: any backlog shows up directly as the window continuing to move on its
# own. That post-stop crawl is the user's exact complaint, turned into a number.
#
# Everything is logical (GetWindowRect), no pixels: legs are comparable even for the
# WDA-excluded Release build, and the run needs no screen access.
#
# Legs:
#   A  Debug + opacity 1.00   baseline (no layering, no affinity)
#   B  Debug + opacity 0.87   layered window (the user's own setting)
#   C  Release + opacity 1.00 + WDA_EXCLUDEFROMCAPTURE (the user's build)
#
# Usage:  powershell -File tools\drag-lag2.ps1

. "$PSScriptRoot\_ui.ps1"

$script:fail = 0

$repoRoot = Split-Path $PSScriptRoot -Parent
$exeDbg = Join-Path $repoRoot 'build\bin\Debug\net8.0-windows\BangGang.exe'
$exeRel = Join-Path $repoRoot 'build\bin\Release\net8.0-windows\BangGang.exe'

$MEF_DOWN = [uint32]0x0002
$MEF_UP   = [uint32]0x0004

# --- settings.json Opacity swap (same helpers as drag-perf.ps1) -------------------------

function Push-Opacity([string]$exe, [double]$op) {
    $path = Join-Path (Split-Path $exe -Parent) 'settings.json'
    $bk = @{ Path = $path; Had = (Test-Path $path); Raw = $null }
    if ($bk.Had) { $bk.Raw = Get-Content $path -Raw }
    $cfg = $null
    if ($bk.Had) { try { $cfg = $bk.Raw | ConvertFrom-Json } catch { $cfg = $null } }
    if ($null -ne $cfg) {
        if ($cfg.PSObject.Properties['Opacity']) { $cfg.Opacity = $op }
        else { $cfg | Add-Member -NotePropertyName Opacity -NotePropertyValue $op }
        $json = ($cfg | ConvertTo-Json -Depth 8 -Compress)
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

function Get-P95($vals) {
    if ($vals.Count -eq 0) { return -1 }
    $s = @($vals | Sort-Object)
    return $s[[int][Math]::Floor(0.95 * ($s.Count - 1))]
}

# Notepad leg needs to place the window deterministically (it may open maximized or
# remembered off somewhere). BB has no MoveWindow, so this probe carries its own import.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BBX {
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
}
'@

# --- notepad leg: the machine's own baseline --------------------------------------------
#
# Same sweep against an ordinary window. If notepad also lands at ~30-60 updates/s the
# floor is the machine / the injection rate, not our window; if it does several hundred,
# the modal move loop is cheap for normal windows and something about OURS is slow.

function Measure-Notepad {
    $script:legRet = $null
    $p = Start-Process notepad.exe -PassThru
    try {
        $main = [IntPtr]::Zero
        for ($i = 0; $i -lt 40 -and $main -eq [IntPtr]::Zero; $i++) {
            Start-Sleep -Milliseconds 250
            $main = [BB]::FindTop([uint32]$p.Id)
        }
        if ($main -eq [IntPtr]::Zero) { Write-Output '  FAIL -- notepad window not found'; $script:fail++; return }
        [void][BB]::ShowWindow($main, 9)        # SW_RESTORE (in case it opens maximized)
        [void][BBX]::MoveWindow($main, 360, 120, 900, 600, $true)
        [void][BB]::SetForegroundWindow($main)
        Start-Sleep -Milliseconds 600

        $mr = Get-WinRect $main
        $grabX = [int](($mr.Left + $mr.Right) / 2)
        $grabY = $mr.Top + 12                   # title bar
        Write-Output ('  window ' + $mr.Left + ',' + $mr.Top + ' ' + ($mr.Right - $mr.Left) + 'x' + ($mr.Bottom - $mr.Top))

        [void][BB]::SetCursorPos($grabX, $grabY)
        Start-Sleep -Milliseconds 250
        [BB]::mouse_event($MEF_DOWN, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 300

        $wx0 = (Get-WinRect $main).Left

        $sweep = New-Object System.Collections.ArrayList
        $t0 = [System.Diagnostics.Stopwatch]::StartNew()
        for ($off = 3; $off -le 600; $off += 3) {
            $cx = $grabX + $off
            [void][BB]::SetCursorPos($cx, $grabY)
            [void]$sweep.Add(@{ T = $t0.ElapsedMilliseconds; CX = $cx; WX = (Get-WinRect $main).Left })
        }
        $sweepMs = $t0.ElapsedMilliseconds

        $cxEnd = $grabX + 600
        $post = New-Object System.Collections.ArrayList
        while ($t0.ElapsedMilliseconds - $sweepMs -lt 800) {
            [void]$post.Add(@{ T = $t0.ElapsedMilliseconds; WX = (Get-WinRect $main).Left })
            Start-Sleep -Milliseconds 2
        }
        [BB]::mouse_event($MEF_UP, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 300

        $wxEnd = (Get-WinRect $main).Left
        Write-Output ('  travel: ' + ($wxEnd - $wx0) + 'px in ' + $sweepMs + 'ms (' + $sweep.Count + ' steps)')
        if ($wxEnd - $wx0 -lt 500) {
            Write-Output '  FAIL -- window did not follow; grab missed the title bar (maximised?)'
            $script:fail++
            return
        }
        $script:legRet = @{ Wx0 = $wx0; GrabX = $grabX; Sweep = $sweep; Post = $post; SweepMs = $sweepMs }
    }
    finally {
        try { if (!$p.HasExited) { $p.Kill() } } catch { }
    }
}

#
# --- one BangGang leg -------------------------------------------------------------------
#
# Sweep +600px in 3px cursor steps with no pacing, sampling (t, cursorX, windowLeft) per
# step; then keep sampling windowLeft for 800ms with the cursor parked at the end. Returns
# NOTHING on the pipeline -- results come back through $script:legRet (Write-Output inside
# a function lands in its return value; mixing logs and data on one channel corrupts both).

$script:legRet = $null

function Measure-Leg([string]$exe, [double]$op) {
    $script:legRet = $null
    $script:BBExe = $exe
    $bk = Push-Opacity $exe $op
    try {
        $main = Start-BangGang
        $mr = Get-WinRect $main
        $grabX = [int](($mr.Left + $mr.Right) / 2)
        $grabY = $mr.Top + 19
        Write-Output ('  window ' + $mr.Left + ',' + $mr.Top + ' ' + ($mr.Right - $mr.Left) + 'x' + ($mr.Bottom - $mr.Top))

        # Pin-down prelude: the app reads Cursor.Position when it DEQUEUES the button-down.
        [void][BB]::SetCursorPos($grabX, $grabY)
        Start-Sleep -Milliseconds 250
        [BB]::mouse_event($MEF_DOWN, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 300

        $wx0 = (Get-WinRect $main).Left

        $sweep = New-Object System.Collections.ArrayList
        $t0 = [System.Diagnostics.Stopwatch]::StartNew()
        for ($off = 3; $off -le 600; $off += 3) {
            $cx = $grabX + $off
            [void][BB]::SetCursorPos($cx, $grabY)
            [void]$sweep.Add(@{ T = $t0.ElapsedMilliseconds; CX = $cx; WX = (Get-WinRect $main).Left })
        }
        $sweepMs = $t0.ElapsedMilliseconds

        # Cursor parked at the end; watch the window finish (or crawl) for 800ms.
        $cxEnd = $grabX + 600
        $post = New-Object System.Collections.ArrayList
        while ($t0.ElapsedMilliseconds - $sweepMs -lt 800) {
            [void]$post.Add(@{ T = $t0.ElapsedMilliseconds; WX = (Get-WinRect $main).Left })
            Start-Sleep -Milliseconds 2
        }
        [BB]::mouse_event($MEF_UP, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 300

        # Sanity: the drag actually happened.
        $wxEnd = (Get-WinRect $main).Left
        Write-Output ('  travel: ' + ($wxEnd - $wx0) + 'px in ' + $sweepMs + 'ms (' + $sweep.Count + ' steps)')
        if ($wxEnd - $wx0 -lt 500) {
            Write-Output '  FAIL -- window did not follow; the grab missed the chrome bar'
            $script:fail++
            return
        }

        $script:legRet = @{ Wx0 = $wx0; GrabX = $grabX; Sweep = $sweep; Post = $post; SweepMs = $sweepMs }
    }
    finally {
        Stop-BangGang | Out-Null
        Pop-Opacity $bk
    }
}

function Show-Leg([string]$tag, $leg) {
    $lags = @()
    foreach ($s in $leg.Sweep) { $lags += [Math]::Abs(($s.CX - $leg.GrabX) - ($s.WX - $leg.Wx0)) }
    $distinct = @($leg.Sweep | ForEach-Object { $_.WX } | Sort-Object -Unique).Count
    $durS = [Math]::Max(1, $leg.SweepMs) / 1000.0
    $mean = ($lags | Measure-Object -Average).Average
    $max = ($lags | Measure-Object -Maximum).Maximum
    Write-Output ('  sweep: updates/s ' + [Math]::Round($distinct / $durS, 1) +
                  '  lag mean ' + [Math]::Round($mean, 1) + 'px  p95 ' + (Get-P95 $lags) + 'px  max ' + $max + 'px')

    # Post-stop crawl: how far from the parked position is the window, and when does it
    # settle within 2px? "Settles instantly" = no backlog; anything else IS the complaint.
    $expected = $leg.Wx0 + 600
    $stopT = $leg.SweepMs
    $settleMs = -1
    $worstPost = 0
    foreach ($p in $leg.Post) {
        $d = [Math]::Abs($p.WX - $expected)
        if ($d -gt $worstPost) { $worstPost = $d }
        if ($d -le 2 -and $settleMs -lt 0) { $settleMs = $p.T - $stopT }
        elseif ($d -gt 2) { $settleMs = -1 }   # fell out of tolerance again: still crawling
    }
    if ($settleMs -lt 0) { $settleMs = 800 }   # never settled inside the window
    $firstLag = [Math]::Abs($leg.Post[0].WX - $expected)
    Write-Output ('  post-stop: lag at stop ' + $firstLag + 'px  worst ' + $worstPost +
                  'px  settles after ' + $settleMs + 'ms')

    # Shape of the crawl, one line per ~25ms of the post-stop phase.
    $line = '  tail: '
    $lastT = -1000
    foreach ($p in $leg.Post) {
        if ($p.T - $lastT -ge 25) {
            $line += ($p.T - $stopT).ToString() + 'ms:' + ($p.WX - $leg.Wx0).ToString() + '  '
            $lastT = $p.T
        }
    }
    Write-Output $line
}

# --- run --------------------------------------------------------------------------------

$legs = @(
    @{ Tag = 'A'; Desc = 'A: Debug, opacity 1.00 (baseline)';    Exe = $exeDbg; Op = 1.0  },
    @{ Tag = 'B'; Desc = 'B: Debug, opacity 0.87 (layered)';     Exe = $exeDbg; Op = 0.87 },
    @{ Tag = 'C'; Desc = 'C: Release, opacity 1.00 (+WDA)';      Exe = $exeRel; Op = 1.0  }
)

Invoke-BBProbe {
    foreach ($leg in $legs) {
        Write-Output ('--- leg ' + $leg.Desc + ' ---')
        if (-not (Test-Path $leg.Exe)) {
            Write-Output ('  FAIL -- exe missing: ' + $leg.Exe)
            $script:fail++
            continue
        }
        Measure-Leg $leg.Exe $leg.Op
        if ($null -ne $script:legRet) { Show-Leg $leg.Tag $script:legRet }
        Write-Output ''
    }
    Write-Output '--- leg D: notepad (machine baseline) ---'
    Measure-Notepad
    if ($null -ne $script:legRet) { Show-Leg 'D' $script:legRet }
    Write-Output ''
}

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'OK: three legs measured; compare settle times.' }
else { Write-Output "FAIL: $($script:fail) leg(s) void -- see above." }

Write-BBDone 'drag-lag2'
