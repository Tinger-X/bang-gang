# Grab the running BangGang window into a PNG, so a change to the chat area can
# be looked at without a human at the keyboard.
#
# Needs the app started with BANGGANG_SHOW_IN_CAPTURE=1 -- otherwise the window
# is capture-excluded (WDA_EXCLUDEFROMCAPTURE) and CopyFromScreen returns the
# desktop behind it, which looks exactly like a rendering bug.
#
#   powershell -ExecutionPolicy Bypass -File tools\capture-window.ps1 -Out shot.png
#
# Pure ASCII on purpose: PowerShell 5.1 reads a BOM-less script as ANSI, so a
# non-ASCII byte in here is a syntax error waiting to happen (see CLAUDE.md).
# That is also why the window is matched by CLASS NAME and process id -- its
# caption is set from a Chinese string constant in MainForm.cs.

param(
    [string]$Out = "shot.png",
    [int]$ProcessId = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class W {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(Proc p, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool Proc(IntPtr h, IntPtr l);

    // The app deliberately hides its window title (the class is "W" and the
    // caption is a masked ".^.^"), so Process.MainWindowHandle comes back zero
    // and there is no title to search for. Match on process id instead and take
    // the first VISIBLE top-level window.
    public static IntPtr VisibleWindowOf(uint want) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == want && IsWindowVisible(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

$procs = Get-Process -Name BangGang -ErrorAction SilentlyContinue
if (-not $procs) { throw "BangGang is not running" }
if ($ProcessId -gt 0) { $procs = $procs | Where-Object { $_.Id -eq $ProcessId } }

$hwnd = [W]::VisibleWindowOf([uint32]$procs[0].Id)
if ($hwnd -eq [IntPtr]::Zero) { throw "no visible window (still starting up?)" }

[void][W]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 400

$r = New-Object W+RECT
if (-not [W]::GetWindowRect($hwnd, [ref]$r)) { throw "GetWindowRect failed" }

$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top
if ($w -le 0 -or $h -le 0) { throw "window has no size ($w x $h)" }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
$g.Dispose()

$full = [System.IO.Path]::GetFullPath($Out)
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host ("saved {0} ({1}x{2})" -f $full, $w, $h)
