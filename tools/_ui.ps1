# Shared helpers for the BangGang UI probe scripts.
#
# Dot-source me at the top of a script:
#     . "$PSScriptRoot\_ui.ps1"
#
# ASCII ONLY. PowerShell 5.1 reads a BOM-less file as ANSI, so any non-ASCII
# byte in here is a syntax error waiting to happen.
#
# The scripts that use this all launch the Debug build with
# BANGGANG_SHOW_IN_CAPTURE=1, because Release carries WDA_EXCLUDEFROMCAPTURE
# and is invisible to CopyFromScreen. They also all kill the app on the way
# out -- never leave a probe instance running.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class BB {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("gdi32.dll", CharSet=CharSet.Unicode)] public static extern int GetObject(IntPtr h, int n, ref LOGFONT lf);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll", CharSet=CharSet.Unicode)] public static extern bool GetTextExtentPoint32(IntPtr hdc, string s, int n, out SIZE sz);
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct LOGFONT {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision,
                    lfClipPrecision, lfQuality, lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string lfFaceName;
    }
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, int w, StringBuilder l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, string l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    // The main window is a WS_EX_TOOLWINDOW, so Process.MainWindowHandle is 0
    // for it -- walk the top level by PID and take the wide visible one.
    public static IntPtr FindTop(uint want) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == want && IsWindowVisible(h)) {
                RECT r; GetWindowRect(h, out r);
                if (r.Right - r.Left > 400) { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static List<IntPtr> Kids(IntPtr root) {
        var o = new List<IntPtr>();
        EnumChildWindows(root, delegate(IntPtr h, IntPtr l) { o.Add(h); return true; }, IntPtr.Zero);
        return o;
    }

    public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }

    // WM_GETFONT -> the HFONT the control is actually rendered with. This is how
    // you tell whether a placeholder and the EDIT it sits in really share a font,
    // instead of trusting that both were assigned the same one in C#.
    public static string FontOf(IntPtr h) {
        IntPtr hf = SendMessage(h, 0x0031, IntPtr.Zero, IntPtr.Zero);
        if (hf == IntPtr.Zero) return "(no font)";
        var lf = new LOGFONT();
        if (GetObject(hf, Marshal.SizeOf(typeof(LOGFONT)), ref lf) == 0) return "(GetObject failed)";
        return lf.lfFaceName + "  height=" + lf.lfHeight + "  width=" + lf.lfWidth + "  weight=" + lf.lfWeight;
    }

    // Line height the control's own font produces, measured through a memory DC.
    // If this exceeds the control's client height, the glyphs cannot fit and the
    // descenders get cut -- no guessing required.
    public static string TextExtent(IntPtr h, string s) {
        IntPtr hf = SendMessage(h, 0x0031, IntPtr.Zero, IntPtr.Zero);
        if (hf == IntPtr.Zero) return "(no font)";
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr old = SelectObject(dc, hf);
        SIZE sz;
        bool ok = GetTextExtentPoint32(dc, s, s.Length, out sz);
        SelectObject(dc, old);
        DeleteDC(dc);
        if (!ok) return "(GetTextExtent failed)";
        return "line height=" + sz.cy + "  text width=" + sz.cx;
    }

    // GetWindowText cannot read another process's control -- it only returns the
    // cached title, which is empty for a foreign child HWND. WM_GETTEXT via
    // SendMessage does cross the process boundary, so use that for control text.
    public static string Tx(IntPtr h) {
        var s = new StringBuilder(512);
        SendMessage(h, 0x000D, 512, s);
        return s.ToString();
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }

    // The exe path is resolved by the caller; keep it out of here.
    public static IntPtr SendText(IntPtr h, string s) { return SendMessage(h, 0x000C, IntPtr.Zero, s); }
}
'@

# Build output lives under <repo>\build\ (see Directory.Build.props), not in src\BangGang\bin.
$script:BBExe = Join-Path (Split-Path $PSScriptRoot -Parent) 'build\bin\Debug\net8.0-windows\BangGang.exe'
$script:BBShoots = Join-Path (Split-Path $PSScriptRoot -Parent) 'shoots'
$script:BBPid = 0
$script:BBMain = [IntPtr]::Zero

# Screenshots land in <repo>\shoots\ (gitignored), not in the system temp dir.
# Scripts resolve their -Out default through this *after* dot-sourcing, since
# param defaults are evaluated before this file is loaded.
function Get-ShotPath([string]$Name) {
    if (-not (Test-Path $script:BBShoots)) { [void](New-Item -ItemType Directory -Path $script:BBShoots -Force) }
    return (Join-Path $script:BBShoots $Name)
}

function Start-BangGang {
    [CmdletBinding()]
    param([int]$WaitSeconds = 4)

    Get-Process BangGang -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400

    if (-not (Test-Path $script:BBExe)) { throw "debug build missing: $script:BBExe" }
    $env:BANGGANG_SHOW_IN_CAPTURE = '1'      # only the Debug build honours this
    $p = Start-Process -FilePath $script:BBExe -PassThru
    Start-Sleep -Seconds $WaitSeconds

    $main = [BB]::FindTop([uint32]$p.Id)
    if ($main -eq [IntPtr]::Zero) { throw 'main window not found' }

    $script:BBPid = $p.Id
    $script:BBMain = $main
    [void][BB]::ShowWindow($main, 5)
    [void][BB]::BringWindowToTop($main)
    [void][BB]::SetForegroundWindow($main)
    Start-Sleep -Milliseconds 500
    return $main
}

function Stop-BangGang {
    $n = 0
    foreach ($proc in @(Get-Process BangGang -ErrorAction SilentlyContinue)) {
        try { $proc.Kill(); $n++ } catch { }
    }
    if ($n -gt 0) { Start-Sleep -Milliseconds 300 }
    Write-Output ("[cleanup] stopped " + $n + " BangGang process(es)")
}

# Runs $Body, then always kills the app -- even if $Body throws.
function Invoke-BBProbe {
    param([Parameter(Mandatory)][scriptblock]$Body)
    try { & $Body }
    finally { Stop-BangGang }
}

function Get-WinRect($h) { $r = New-Object BB+RECT; [void][BB]::GetWindowRect($h, [ref]$r); return $r }
function Get-ClientRect($h) { $r = New-Object BB+RECT; [void][BB]::GetClientRect($h, [ref]$r); return $r }
function Get-WinKids($h) { return [BB]::Kids($h) }
function Get-WinClass($h) { return [BB]::Cls($h) }
function Get-WinText($h) { return [BB]::Tx($h) }
function Get-WinFont($h) { return [BB]::FontOf($h) }
function Get-TextExtent($h, [string]$s) { return [BB]::TextExtent($h, $s) }

function Write-WinTree($root) {
    $mr = Get-WinRect $root
    Write-Output ("window " + $mr.Left + "," + $mr.Top + " " + ($mr.Right - $mr.Left) + "x" + ($mr.Bottom - $mr.Top))
    foreach ($h in Get-WinKids $root) {
        $r = Get-WinRect $h
        $c = Get-WinClass $h
        if ($c.StartsWith('WindowsForms10.')) { $c = $c.Substring(0, $c.IndexOf('.app')) -replace '^WindowsForms10\.', '' }
        Write-Output ("  " + $c.PadRight(14) + $r.Left + "," + $r.Top + " " + ($r.Right - $r.Left) + "x" + ($r.Bottom - $r.Top) + " " + $(if ([BB]::IsWindowVisible($h)) { 'vis' } else { 'hid' }) + "  '" + (Get-WinText $h) + "'")
    }
}

function Save-WindowShot($h, [string]$Path) {
    $r = Get-WinRect $h
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $ht))
    $g.Dispose()
    $bmp.Save($Path)
    $bmp.Dispose()
    Write-Output ("saved " + $Path + "  (" + $w + "x" + $ht + ")")
}

function Save-Shot([int]$x, [int]$y, [int]$w, [int]$h, [string]$Path) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()
    $bmp.Save($Path)
    $bmp.Dispose()
    Write-Output ("saved " + $Path + "  (" + $w + "x" + $h + ")")
}

# Text ink bounding box, luminance < 200 counts as ink.
function Get-InkBox($bmp) {
    $minX = -1; $maxX = -1; $minY = -1; $maxY = -1; $n = 0
    for ($x = 0; $x -lt $bmp.Width; $x++) {
        for ($y = 0; $y -lt $bmp.Height; $y++) {
            $c = $bmp.GetPixel($x, $y)
            if ((0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B) -lt 200) {
                $n++
                if ($minX -lt 0 -or $x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($minY -lt 0 -or $y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($n -eq 0) { return 'no ink' }
    return "$minX..$maxX x $minY..$maxY (n=$n, w=" + ($maxX - $minX + 1) + " h=" + ($maxY - $minY + 1) + ")"
}

function Get-InkBoxAt([int]$x, [int]$y, [int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()
    $res = Get-InkBox $bmp
    $bmp.Dispose()
    return $res
}

function Invoke-MouseClick([int]$x, [int]$y) { [BB]::Click($x, $y) }

# Drag from (x0,y0) to (x1,y1) in screen coords, in $Steps hops so the app sees a real
# MouseMove stream rather than one teleport. The window's resize tracks Cursor.Position,
# so moving the real cursor is what drives it.
#
# Watch out: SetCursorPos CLAMPS to the screen. A drag whose endpoint is off-screen
# silently delivers less movement than asked for, which then looks like a bug in the app.
function Invoke-Drag([int]$x0, [int]$y0, [int]$x1, [int]$y1, [int]$Steps = 8) {
    $MEF_DOWN = [uint32]0x0002
    $MEF_UP   = [uint32]0x0004
    [void][BB]::SetCursorPos($x0, $y0)
    Start-Sleep -Milliseconds 200
    [BB]::mouse_event($MEF_DOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 150

    # Re-assert the grab point before stepping. The app reads Cursor.Position when it
    # DEQUEUES the button-down, not when the button went down; if the UI thread was busy
    # (the 3s guard timer does affinity + theme work) it dequeues late, sees the cursor
    # already at the first step, and its edge hit test lands outside the grip band --
    # so nothing happens at all. Holding still here gives it a second chance.
    [void][BB]::SetCursorPos($x0, $y0)
    Start-Sleep -Milliseconds 300

    for ($i = 1; $i -le $Steps; $i++) {
        [void][BB]::SetCursorPos(($x0 + [int](($x1 - $x0) * $i / $Steps)),
                                 ($y0 + [int](($y1 - $y0) * $i / $Steps)))
        Start-Sleep -Milliseconds 70
    }
    Start-Sleep -Milliseconds 200
    [BB]::mouse_event($MEF_UP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 350
}

# The main window's minimum size: one full-size settings card plus its margin, pulled
# back if the screen cannot fit that (see MainForm.ComputeMinWindow).
function Get-MinWindow {
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    return @{
        W = [Math]::Min(880 + 96, [Math]::Max(480, $wa.Width - 40))
        H = [Math]::Min(640 + 96, [Math]::Max(360, $wa.Height - 40))
    }
}

# The two 28x28 buttons on the chrome bar's row: the close button, and the
# maximize/restore button immediately to its left.
function Get-ChromeButtons($main) {
    $mr = Get-WinRect $main
    $btns = @()
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -ne 28 -or ($r.Bottom - $r.Top) -ne 28) { continue }
        if ([Math]::Abs($r.Top - ($mr.Top + 5)) -gt 1) { continue }
        if ($r.Left -lt ($mr.Left + 400)) { continue }   # the sidebar's gear/plus sit elsewhere
        $btns += ,$r
    }
    if ($btns.Count -ne 2) { return $null }
    $sorted = @($btns | Sort-Object Left)
    return @{ Max = $sorted[0]; Close = $sorted[1] }
}

# The sidebar's search pill. Found by POSITION, never by width: SearchField sits at
# x=10 with a 32px pill height, whereas its width is SideW-106 and moves every time
# the sidebar is resized. Probes used to look for "the 198x32 control", which
# silently stopped matching the moment SideW changed.
function Get-SearchPill($main) {
    $mr = Get-WinRect $main
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if (($r.Bottom - $r.Top) -ne 32) { continue }
        if ([Math]::Abs($r.Left - ($mr.Left + 10)) -gt 1) { continue }
        if ($r.Top -le $mr.Top + 38) { continue }      # the chrome bar is not the sidebar
        return $r
    }
    return $null
}

# The settings gear: the leftmost 28x28 child on the pill's row. Its x is SideW-86,
# so "leftmost of the two 28x28 buttons on that row" is the position-free way to say it.
function Get-SettingsGear($main, $pill) {
    $gear = $null
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -ne 28 -or ($r.Bottom - $r.Top) -ne 28) { continue }
        if ([Math]::Abs($r.Top - ($pill.Top + 2)) -gt 3) { continue }
        if ($r.Left -le $pill.Right) { continue }
        if ($null -eq $gear -or $r.Left -lt $gear.Left) { $gear = $r }
    }
    return $gear
}

# Calls out the end of a probe run so it is obvious in the transcript.
function Write-BBDone([string]$Name) {
    Write-Output ''
    Write-Output ('== DONE: ' + $Name + ' -- app closed, nothing left running ==')
    Write-Output ''
}
