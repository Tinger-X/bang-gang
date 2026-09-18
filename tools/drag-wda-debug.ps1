# One-off diagnostic for the WDA drag failure shape (see drag-perf.ps1 legs C/F/G).
# Drags the Debug+WDA window with 8px steps and prints, per step:
#   cursor offset | window offset | capture still ours
# Pass 1 runs flat-out (drag-perf's shape), pass 2 with 25ms pacing.
# The question: does the drag DIE (capture lost) or LAG (alive but behind)?

. "$PSScriptRoot\_ui.ps1"

$script:fail = 0
$repoRoot = Split-Path $PSScriptRoot -Parent
$exeDbg = Join-Path $repoRoot 'build\bin\Debug\net8.0-windows\BangGang.exe'

$MEF_DOWN = [uint32]0x0002
$MEF_UP   = [uint32]0x0004

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BBC {
    [DllImport("user32.dll")] public static extern IntPtr GetCapture();
}
'@

function Run-Pass([string]$tag, [int]$paceMs) {
    Write-Output ('--- pass ' + $tag + ' (pace ' + $paceMs + 'ms) ---')
    $main = Start-BangGang
    try {
        $mr = Get-WinRect $main
        $grabX = [int](($mr.Left + $mr.Right) / 2)
        $grabY = $mr.Top + 19
        [void][BB]::SetCursorPos($grabX, $grabY)
        Start-Sleep -Milliseconds 250
        [BB]::mouse_event($MEF_DOWN, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 300

        $wx0 = (Get-WinRect $main).Left
        $cap0 = [BBC]::GetCapture()
        Write-Output ('  capture after grab: ' + $cap0 + ' (form ' + $main + ')')

        $path = @()
        for ($d = 8; $d -le 300; $d += 8) { $path += $d }
        for ($d = 292; $d -ge 0; $d -= 8) { $path += $d }

        $line = ''
        $i = 0
        foreach ($off in $path) {
            [void][BB]::SetCursorPos($grabX + $off, $grabY)
            if ($paceMs -gt 0) { Start-Sleep -Milliseconds $paceMs }
            $wx = (Get-WinRect $main).Left
            $cap = [BBC]::GetCapture()
            $mark = $(if ($cap -eq $main) { '.' } else { '!' })
            if (($i % 10) -eq 9 -or $mark -eq '!') {
                $line += (' ' + $off + '->' + ($wx - $wx0) + $mark)
            }
            $i++
        }
        Write-Output ('  every 10th step (cursorOff->windowOff, ! = capture lost):' + $line)
        [BB]::mouse_event($MEF_UP, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 400
        $wxEnd = (Get-WinRect $main).Left
        $capEnd = [BBC]::GetCapture()
        Write-Output ('  end: windowOff ' + ($wxEnd - $wx0) + ' (expect ~0), capture ' + $capEnd)
        $log = Join-Path (Split-Path $exeDbg -Parent) 'ui-trace.log'
        Get-Content $log -Tail 6 | ForEach-Object { Write-Output ('  trace| ' + $_) }
    }
    finally {
        Stop-BangGang | Out-Null
    }
}

$script:BBExe = $exeDbg
Invoke-BBProbe {
    $script:BBNoCaptureEnv = $true      # WDA on
    try {
        Run-Pass 'fast' 0
        Run-Pass 'mouse-125hz' 8
        Run-Pass 'paced' 25
    }
    finally { $script:BBNoCaptureEnv = $false }
}

Write-Output ''
Write-Output '== DONE: drag-wda-debug =='
