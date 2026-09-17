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
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
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

    // Type into a foreign EDIT the way a user does -- one WM_CHAR per character.
    //
    // ASCII ONLY. Do NOT reach for SendText (WM_SETTEXT) here either: it does put
    // the string into the native edit, but it does not drive WinForms' TextChanged,
    // so anything the app hangs off that event (hiding a placeholder, enabling a
    // button) never runs: you get a "typed" screenshot that is really still the
    // empty state, with the placeholder drawn on top of the text.
    //
    // And do not reach for this method for non-ASCII text -- see TypeUnicode below
    // for what happens (measured, not guessed) when you do.
    //
    // Returns the text the control actually reports afterwards, so the caller
    // can assert instead of hoping.
    public static string Type(IntPtr h, string s) {
        foreach (char c in s) PostMessage(h, 0x0102, (IntPtr)c, IntPtr.Zero);
        System.Threading.Thread.Sleep(250);
        return Tx(h);
    }

    // --- Unicode keyboard input ---
    //
    // PostMessage(WM_CHAR) above survives only for ASCII. Measured on this app's
    // input box: posting 0x53D1 lands 0x88C7 in the control, and a Latin-1 0xE9 is
    // swallowed outright, while 'abc' arrives intact. ASCII working is what makes
    // that trap dangerous -- it reads as the APP mangling text.
    //
    // SendInput with KEYEVENTF_UNICODE is the path a real keyboard takes and the
    // only one of the three that round-trips CJK. It goes through the keyboard
    // input queue, so it needs the target focused (the PowerShell wrapper below
    // clicks it first) and the app window in the foreground.
    //
    // INPUT is a tagged union whose alignment differs between architectures, so
    // the offset of the KEYBDINPUT inside it does too; rather than guess, both
    // layouts are declared explicitly and picked by IntPtr.Size. (MOUSEINPUT is
    // the union's largest member and would give the same sizes, but an explicit
    // offset is easier to check against the C header than a padding argument.)
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT64 { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    public struct INPUT32 { [FieldOffset(0)] public uint type; [FieldOffset(4)] public KEYBDINPUT ki; }
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT64[] p, int cb);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT32[] p, int cb);
    public const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;

    public static void TypeUnicode(string s) {
        bool x64 = IntPtr.Size == 8;
        foreach (char c in s) {
            if (x64) {
                var a = new INPUT64[2];
                for (int i = 0; i < 2; i++) {
                    a[i].type = 1;                       // INPUT_KEYBOARD
                    a[i].ki.wVk = 0;
                    a[i].ki.wScan = c;
                    a[i].ki.dwFlags = KEYEVENTF_UNICODE | (i == 1 ? KEYEVENTF_KEYUP : 0);
                }
                SendInput(2, a, 40);
            } else {
                var a = new INPUT32[2];
                for (int i = 0; i < 2; i++) {
                    a[i].type = 1;
                    a[i].ki.wVk = 0;
                    a[i].ki.wScan = c;
                    a[i].ki.dwFlags = KEYEVENTF_UNICODE | (i == 1 ? KEYEVENTF_KEYUP : 0);
                }
                SendInput(2, a, 28);
            }
            System.Threading.Thread.Sleep(20);
        }
        System.Threading.Thread.Sleep(300);
    }

    public const ushort VK_CONTROL = 0x11;

    // Ctrl+<vk> as one keystroke. Both SendKeys entry points are unusable from here:
    //
    //   Send()     throws "SendKeys cannot run inside this application because the
    //              application is not handling Windows messages" -- the PowerShell host
    //              is a console app with no message pump.
    //   SendWait() installs a journal hook and then blocks until the TARGET has
    //              processed the keys. If the target stops pumping -- a modal error
    //              dialog, a stuck message loop -- it blocks FOREVER while spinning a
    //              core: a probe that hangs for minutes, leaves the app running, and
    //              reports nothing at all. That is a very expensive way to find out
    //              that something threw.
    //
    // SendInput goes through the keyboard input queue like a real keyboard and returns
    // immediately. It delivers to the FOCUSED window, so click the target first, and
    // give the app a moment afterwards before asserting on the result.
    public static void Chord(ushort vk) {
        // ctrl down, key down, key up, ctrl up -- order matters, and the modifier must
        // be released last or the target sees a bare key.
        ushort[] keys = { VK_CONTROL, vk, vk, VK_CONTROL };
        bool[] up = { false, false, true, true };
        if (IntPtr.Size == 8) {
            var a = new INPUT64[4];
            for (int i = 0; i < 4; i++) {
                a[i].type = 1;                       // INPUT_KEYBOARD
                a[i].ki.wVk = keys[i];
                a[i].ki.wScan = 0;
                a[i].ki.dwFlags = up[i] ? KEYEVENTF_KEYUP : 0;
            }
            SendInput(4, a, 40);
        } else {
            var a = new INPUT32[4];
            for (int i = 0; i < 4; i++) {
                a[i].type = 1;
                a[i].ki.wVk = keys[i];
                a[i].ki.wScan = 0;
                a[i].ki.dwFlags = up[i] ? KEYEVENTF_KEYUP : 0;
            }
            SendInput(4, a, 28);
        }
    }

    // One wheel notch, at wherever the cursor happens to be. There is no managed way to
    // do this from a console host, and mouse_event is the one that puts a real
    // WM_MOUSEEVENTF_WHEEL on the queue addressed to the focused window with the
    // cursor's screen position in lParam -- which is exactly what this app's
    // IMessageFilter reads. It decides by cursor position, so put the cursor where the
    // scroll should go BEFORE calling this (see Invoke-WheelAt), and make sure the app
    // is foreground or the wheel is delivered to whatever is.
    //
    // Positive = one notch up (away from the user), matching the WinForms delta sign.
    public static void Wheel(int notches) {
        mouse_event(0x0800, 0, 0, unchecked((uint)(notches * 120)), IntPtr.Zero);
        System.Threading.Thread.Sleep(250);
    }

    // One bare key, no modifier. Chord() above is Ctrl+<vk> and Ctrl is not optional
    // there -- so reaching for it to press Esc actually sends Ctrl+Esc, which is the
    // Start menu. That overlay then covers the bottom left of the screen and takes
    // focus: every click after it goes to the Start menu and every pixel read comes
    // back dark. It looks exactly like "the dialog ignored Esc AND the mouse", and the
    // dark scrim the probe was sampling made it look like the dialog never closed.
    //
    // Delivers to the FOCUSED window, like Chord -- click the target first.
    public static void Key(ushort vk) {
        if (IntPtr.Size == 8) {
            var a = new INPUT64[2];
            for (int i = 0; i < 2; i++) {
                a[i].type = 1;                       // INPUT_KEYBOARD
                a[i].ki.wVk = vk;
                a[i].ki.wScan = 0;
                a[i].ki.dwFlags = (i == 1) ? KEYEVENTF_KEYUP : 0;
            }
            SendInput(2, a, 40);
        } else {
            var a = new INPUT32[2];
            for (int i = 0; i < 2; i++) {
                a[i].type = 1;
                a[i].ki.wVk = vk;
                a[i].ki.wScan = 0;
                a[i].ki.dwFlags = (i == 1) ? KEYEVENTF_KEYUP : 0;
            }
            SendInput(2, a, 28);
        }
    }
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

    # ui-rows.json is a snapshot of ONE run. Leaving the previous run's copy on disk means
    # a probe that waits for "two rows to appear" is satisfied instantly by a file
    # describing the last app -- there are no rows yet and it says there are. (The origin
    # in it is no longer part of the problem: Get-UiRows re-anchors that on the window
    # rect, so a stale file can no longer scatter clicks across the desktop. The stale
    # ROW LIST still can, and that is what this removes.)
    Remove-Item $script:BBRows -ErrorAction SilentlyContinue

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

# 'WindowsForms10.STATIC.app.0.141b42a_r6_ad1' -> 'Static'. The mangled name carries a
# per-process hash, so matching on the raw class name is never portable.
function Get-ShortClass($h) {
    $c = Get-WinClass $h
    if ($c.StartsWith('WindowsForms10.')) { $c = $c.Substring(0, $c.IndexOf('.app')) -replace '^WindowsForms10\.', '' }
    return $c
}
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
function Format-InkBox($r) {
    if ($null -eq $r) { return 'no ink' }
    return "$($r.X)..$($r.R) x $($r.Y)..$($r.B) (n=$($r.N), w=" + ($r.R - $r.X + 1) + " h=" + ($r.B - $r.Y + 1) + ")"
}

function Get-InkBox($bmp) { return Format-InkBox (Get-InkBoxNum $bmp) }

# The numeric form: @{ X, Y, R, B, N } over the pixels dark enough to be text, or
# $null when there are none. Get-InkBox only formats this, so a probe that prints
# the box and one that asserts on it can never be looking at different numbers.
#
# $Cut is the luminance the test splits on, and it is worth thinking about rather
# than accepting the default: 200 is tuned for TEXT on a near-white ground. The
# input card's scroll thumb is mixed only 30% toward the muted colour and lands
# near 217, so a strip containing one comes back "no ink" at 200 -- pass 240 for
# that. -Bright flips the test to white-on-dark, which is how the glyphs inside
# the send button's accent-filled circle have to be read.
function Get-InkBoxNum($bmp, [int]$Cut = 200, [switch]$Bright) {
    $minX = -1; $maxX = -1; $minY = -1; $maxY = -1; $n = 0
    for ($x = 0; $x -lt $bmp.Width; $x++) {
        for ($y = 0; $y -lt $bmp.Height; $y++) {
            $c = $bmp.GetPixel($x, $y)
            $lum = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
            if ($Bright) { if ($lum -le $Cut) { continue } }
            elseif ($lum -ge $Cut) { continue }
            $n++
            if ($minX -lt 0 -or $x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($minY -lt 0 -or $y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
    if ($n -eq 0) { return $null }
    return @{ X = $minX; Y = $minY; R = $maxX; B = $maxY; N = $n }
}

# Same, straight off the screen, into a bitmap the caller does not have to own.
function Get-InkBoxNumAt([int]$x, [int]$y, [int]$w, [int]$h, [int]$Cut = 200, [switch]$Bright) {
    $bmp = Get-Crop $x $y $w $h
    $res = Get-InkBoxNum $bmp $Cut -Bright:$Bright
    $bmp.Dispose()
    return $res
}

# The ink box with the threshold at HALF INTENSITY between the crop's own lightest
# and darkest pixel, rather than at a fixed luminance.
#
# Use this whenever the two things being compared are drawn in different colours --
# a placeholder in TextMuted against typed text in TextMain, say. A fixed cut is a
# fixed *coverage* only for one ink colour: with mid grey ink, a pixel 25% covered
# lands at luminance 221 and is excluded, while the same pixel under near-black ink
# lands at 198 and is included. The box then comes out one column wider for the
# darker layer, which reads as a 1px misalignment that is not there. Half intensity
# is the geometric edge under either colour, so the same glyphs give the same box.
function Get-InkBoxHalf($bmp) {
    $lo = 255.0; $hi = 0.0
    for ($x = 0; $x -lt $bmp.Width; $x++) {
        for ($y = 0; $y -lt $bmp.Height; $y++) {
            $c = $bmp.GetPixel($x, $y)
            $lum = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
            if ($lum -lt $lo) { $lo = $lum }
            if ($lum -gt $hi) { $hi = $lum }
        }
    }
    if ($hi - $lo -lt 8) { return $null }      # flat crop: nothing drawn in it

    # Which end is the background decides which way round the test goes: a text crop
    # is mostly background and nothing is drawn flush into a corner, so the median
    # corner luminance is the background's. Dark text (light theme) is "below the
    # cut", light text (dark theme) is "above" -- and the half-intensity cut is the
    # same geometric edge either way.
    $by = 0.0
    # Precomputed because of a PowerShell parsing trap: inside an array literal the
    # comma binds TIGHTER than the minus, so `@($bmp.Width - 1, 0)` is not "width-1
    # and 0", it is $bmp.Width minus the ARRAY (1, 0) -- "Object[] does not contain
    # a method named op_Subtraction".
    $wx = $bmp.Width - 1
    $wy = $bmp.Height - 1
    foreach ($p in @(@(0, 0), @($wx, 0), @(0, $wy), @($wx, $wy))) {
        $c = $bmp.GetPixel($p[0], $p[1])
        $by += 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
    }
    $by = $by / 4
    $cut = [int](($lo + $hi) / 2)
    if ($by -ge $cut) { return Get-InkBoxNum $bmp $cut }
    return Get-InkBoxNum $bmp $cut -Bright
}

# Grab a rectangle of the real screen into a Bitmap the caller owns (and disposes).
function Get-Crop([int]$x, [int]$y, [int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()
    return $bmp
}

function Get-InkBoxAt([int]$x, [int]$y, [int]$w, [int]$h) {
    $bmp = Get-Crop $x $y $w $h
    $res = Get-InkBox $bmp
    $bmp.Dispose()
    return $res
}

function Invoke-MouseClick([int]$x, [int]$y) { [BB]::Click($x, $y) }

# Scroll the wheel at a screen point. $Notches is signed: +1 = one notch up.
#
# The cursor has to be there first, and it has to MOVE there -- a SetCursorPos that
# does not move anything produces no WM_MOUSEMOVE, which for a hover test reads as
# "the app ignored the hover". That is why callers park the cursor somewhere else
# before asking for a hover.
function Invoke-WheelAt([int]$x, [int]$y, [int]$Notches) {
    [void][BB]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 200
    [BB]::Wheel($Notches)
}

# Type a string into a control the way a real user does, CJK included.
#
# This is the one to reach for. The two shortcuts both have a failure mode that
# looks like an app bug rather than a probe bug:
#
#   SendText (WM_SETTEXT)  leaves the native edit holding the string without ever
#                          raising WinForms' TextChanged, so nothing the app hangs
#                          off that event runs -- the placeholder stays up over the
#                          text and a button that should light up stays grey. The
#                          screenshot then shows the EMPTY state, which is exactly
#                          the state a "did typing align?" comparison wants, so it
#                          passes while proving nothing.
#   [BB]::Type (WM_CHAR)   is ASCII-only. Posting 0x53D1 lands 0x88C7 in the
#                          control and 0xE9 is swallowed, while 'abc' arrives
#                          intact -- see the note on BB.Type.
#
# SendInput with KEYEVENTF_UNICODE round-trips all three. It delivers to the
# FOCUSED control, so the control is clicked first; that click also drops focus
# from whatever held it, and it puts the caret where you clicked, which is why
# this is not the right tool for building up multi-line text (click at the end
# of the first line, then type, instead of calling it twice).
#
# Returns the text the control reports afterwards, so callers assert.
function Invoke-TypeKeys($h, [string]$s) {
    $r = Get-WinRect $h
    Invoke-MouseClick ([int](($r.Left + $r.Right) / 2)) ([int](($r.Top + $r.Bottom) / 2))
    Start-Sleep -Milliseconds 250
    [BB]::TypeUnicode($s)
    return Get-WinText $h
}

# The line height the control's own font produces, as a number (the string form
# is Get-TextExtent). This is the same quantity InputPanel divides its box height
# by to decide how many lines fit, so a probe can check the app's arithmetic
# against the font rather than against a number copied out of the source.
function Get-LineHeight($h) {
    $s = Get-TextExtent $h 'Xg'
    if ($s -notmatch 'line height=(\d+)') { return 0 }
    return [int]$Matches[1]
}

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

# The two 28x28 icon buttons on the search pill's row, left to right: the settings
# gear, then new-conversation. Returned by POSITION, never by their x -- that is
# SideW-86 / SideW-46 and moves every time the sidebar is resized.
function Get-PillButtons($main, $pill) {
    $btns = @()
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -ne 28 -or ($r.Bottom - $r.Top) -ne 28) { continue }
        if ([Math]::Abs($r.Top - ($pill.Top + 2)) -gt 3) { continue }
        if ($r.Left -le $pill.Right) { continue }
        $btns += ,$r
    }
    if ($btns.Count -ne 2) { return $null }
    return @($btns | Sort-Object Left)
}

# The settings gear: the leftmost of those two.
function Get-SettingsGear($main, $pill) {
    $b = Get-PillButtons $main $pill
    if ($null -eq $b) { return $null }
    return $b[0]
}

# The conversation list: the tall narrow child flush with the window's left edge.
#
# Since 0.9.0 every launch lands on the welcome page (item 3 -- the last conversation is
# no longer reopened), so the chat view and the input box do not exist until a row is
# clicked. Any probe that needs them has to start here, and before this helper existed
# four of them each carried their own copy of this loop.
function Get-ConvList($main) {
    $mr = Get-WinRect $main
    $found = $null
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Left -ne $mr.Left) { continue }
        if (($r.Right - $r.Left) -ge 320) { continue }
        if (($r.Bottom - $r.Top) -lt 100) { continue }
        if ($r.Top -le $mr.Top + 38) { continue }
        $found = @{ H = $h; R = $r }
    }
    return $found
}

# A list row's pitch, mirrored from ConvListBox: 38px rows with 6px between them.
$script:BBCovRowH = 38
$script:BBCovRowGap = 6

# Opens a conversation by clicking its row in the list (default: the first one), then
# waits for the layout to settle. Returns $false if there is no list at all -- an empty
# list and a list that failed to render are the same picture, so the caller has to
# decide for itself whether that counts as a failure.
function Open-ConvRow($main, [int]$Index = 0) {
    $list = Get-ConvList $main
    if ($null -eq $list) { return $false }
    $rowH = $script:BBCovRowH
    $y = $list.R.Top + $Index * ($rowH + $script:BBCovRowGap) + [int]($rowH / 2)
    $x = [int](($list.R.Left + $list.R.Right) / 2)
    Invoke-MouseClick $x $y
    Start-Sleep -Milliseconds 900
    return $true
}

# The input card's two 28x28 buttons, left to right: attach (+), then send.
# Named by "28x28, below the text box" rather than by x for the same reason as
# above: they are pinned BtnInset inside the card's left/right edges, and the
# card's left edge slides with the sidebar.
function Get-InputButtons($main, $edit) {
    $er = Get-WinRect $edit
    $btns = @()
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -ne 28 -or ($r.Bottom - $r.Top) -ne 28) { continue }
        if ($r.Top -le $er.Top) { continue }
        $btns += ,$r
    }
    if ($btns.Count -ne 2) { return $null }
    $s = @($btns | Sort-Object Left)
    return @{ Attach = $s[0]; Send = $s[1] }
}

# The chrome bar's status message ("sent, waiting for the model...", "settings
# saved"). A real Label (AutoSize Static) parked to the right of the brand block,
# so "the rightmost Static in the top strip" names it without hard-coding 180.
function Get-ChromeStatus($main) {
    $mr = Get-WinRect $main
    $best = [IntPtr]::Zero; $bestX = -1
    foreach ($h in Get-WinKids $main) {
        if ((Get-WinClass $h) -notlike '*STATIC*') { continue }
        $r = Get-WinRect $h
        if ($r.Bottom -gt $mr.Top + 38) { continue }
        if ($r.Left -gt $bestX) { $bestX = $r.Left; $best = $h }
    }
    return $best
}

# The accent-filled WIDGETS inside a screen region, as bands. Returns $null if there
# are none.
#
# This is how a probe asks "is the welcome page up?". The welcome page is SELF-PAINTED
# -- nothing on it has a handle -- so there is no control to look for, and looking for
# one is what produced a round of "title bar not found", a probe bug that reads exactly
# like a broken app. Its logo circle and its button are filled with the accent colour
# and nothing else of that size is, so counting accent bands answers the question
# directly. (welcome-click.ps1 and start-welcome.ps1 both need it.)
#
# Sampling every 2px is plenty for a 92px circle and a 180x40 button, and keeps
# PowerShell's per-pixel interop cost down.
#
# Rows are merged into bands and thin ones are dropped, because "accent-coloured pixels"
# is not the same question as "an accent-filled widget". The page's faux input card is
# outlined in a colour close enough to the accent to match, so a raw row scan reports
# its two 1px borders as extra bands -- and a probe that then takes the bottom band
# clicks the card's outline instead of the button. A widget here is at least 16px tall
# and 60px wide; an outline is neither.
function Get-AccentBands([int]$x0, [int]$y0, [int]$x1, [int]$y1) {
    $bmp = Get-Crop $x0 $y0 ($x1 - $x0) ($y1 - $y0)
    $w = $bmp.Width; $h = $bmp.Height

    # The accent is the most common saturated colour in the area (logo + button are both
    # filled with it and nothing else is). Saturated = the channels disagree.
    $hist = @{}
    for ($yy = 0; $yy -lt $h; $yy += 2) {
        for ($xx = 0; $xx -lt $w; $xx += 2) {
            $c = $bmp.GetPixel($xx, $yy)
            $mx = [Math]::Max($c.R, [Math]::Max($c.G, $c.B))
            $mn = [Math]::Min($c.R, [Math]::Min($c.G, $c.B))
            if ($mx - $mn -lt 40) { continue }
            $k = ($c.R * 65536) + ($c.G * 256) + $c.B
            if ($hist.ContainsKey($k)) { $hist[$k]++ } else { $hist[$k] = 1 }
        }
    }
    if ($hist.Count -eq 0) { $bmp.Dispose(); return $null }
    $accKey = -1; $accN = -1
    foreach ($k in $hist.Keys) { if ($hist[$k] -gt $accN) { $accN = $hist[$k]; $accKey = $k } }
    $ar = [Math]::Floor($accKey / 65536); $ag = [Math]::Floor(($accKey - $ar * 65536) / 256)
    $ab = $accKey - ($ar * 65536) - ($ag * 256)

    $bands = New-Object System.Collections.Generic.List[object]
    $cur = $null
    for ($yy = 0; $yy -lt $h; $yy += 2) {
        $n = 0; $lo = -1; $hi = -1
        for ($xx = 0; $xx -lt $w; $xx += 2) {
            $c = $bmp.GetPixel($xx, $yy)
            if (([Math]::Abs($c.R - $ar) + [Math]::Abs($c.G - $ag) + [Math]::Abs($c.B - $ab)) -gt 30) { continue }
            $n++
            if ($lo -lt 0) { $lo = $xx }
            $hi = $xx
        }
        if ($n -ge 10) {
            if ($null -eq $cur) { $cur = @{ Y0 = $yy; Y1 = $yy; X0 = $lo; X1 = $hi } }
            else {
                $cur.Y1 = $yy
                if ($lo -lt $cur.X0) { $cur.X0 = $lo }
                if ($hi -gt $cur.X1) { $cur.X1 = $hi }
            }
        }
        elseif ($null -ne $cur) { $bands.Add($cur); $cur = $null }
    }
    if ($null -ne $cur) { $bands.Add($cur) }
    $bmp.Dispose()

    $keep = New-Object System.Collections.Generic.List[object]
    foreach ($b in $bands) {
        if (($b.Y1 - $b.Y0) -lt 16) { continue }        # an outline, not a widget
        if (($b.X1 - $b.X0) -lt 60) { continue }        # a 28px icon, not a widget
        $keep.Add($b)
    }
    return @{ Accent = "$ar,$ag,$ab"; Bands = $keep }
}

# The real input card's multiline EDIT, or [IntPtr]::Zero. "Is a conversation open?"
# -- the welcome page only paints a REPLICA of that card, so it brings no EDIT with it.
function Get-InputEditBig($main) {
    foreach ($h in Get-WinKids $main) {
        if ((Get-WinClass $h) -notlike '*EDIT*') { continue }
        $r = Get-WinRect $h
        if (($r.Bottom - $r.Top) -lt 40) { continue }
        return $h
    }
    return [IntPtr]::Zero
}

# Calls out the end of a probe run so it is obvious in the transcript.
function Write-BBDone([string]$Name) {
    Write-Output ''
    Write-Output ('== DONE: ' + $Name + ' -- app closed, nothing left running ==')
    Write-Output ''
}

# ============================================================================
# ui-rows.json -- the chat rows, now that bubbles are not windows any more.
# ============================================================================
#
# 0.9.0 made ChatView paint the bubbles itself (they used to be one child HWND
# each). That is what fixed the sidebar animation, and it also means
# EnumChildWindows can no longer see a bubble. Losing that signal is SILENT:
# "0 rows" reads either as "the app is broken" or as "there are no bubbles",
# and every probe that counted them would start passing on an empty screen.
#
# ChatView writes this file on every layout pass instead. Debug-only and gated
# on BANGGANG_SHOW_IN_CAPTURE, i.e. exactly when a probe can see the window.
#
# It is also better than what it replaced: a file read is a consistent
# snapshot, whereas enumerating children mid-animation can catch rows halfway
# through a move. And it carries frame timings, which nothing could measure
# before -- see tools/sidebar-perf.ps1.

$script:BBRows = Join-Path (Split-Path $PSScriptRoot -Parent) 'build\bin\Debug\net8.0-windows\ui-rows.json'

# The parsed snapshot, or $null if the app has not written one yet.
#
# viewX/viewY on the returned object are computed HERE, as the chat view's screen origin
# right now. The file itself carries no absolute screen coordinate on purpose: the app
# lays out once and the window then keeps moving (CenterScreen on startup, dragging,
# maximize), and nothing re-dumps the file when it does. An absolute origin in there is
# therefore stale by construction, and it goes stale in the worst possible way -- the
# number stays perfectly plausible, so a probe clicks the desktop, reads pixels off
# whatever is there, and reports app bugs. (Measured: the first dump of a run carried the
# pre-CenterScreen origin, 264,79, while the window's rect was already 360,120, and it sat
# there until the first reflow overwrote it.)
#
# So the file carries viewOffX/viewOffY -- how far the view sits inside the top-level
# window, a pure layout quantity that no window move can invalidate -- and this function
# re-anchors it on the window rect of the moment. Doing it here fixes every caller at
# once: Get-UiRowScreen, Get-UiRowPoint, and the probes that read $ui.viewX directly.
function Get-UiRows {
    if (-not (Test-Path $script:BBRows)) { return $null }
    $ui = $null
    try { $ui = (Get-Content $script:BBRows -Raw | ConvertFrom-Json) } catch { return $null }
    if ($null -eq $ui) { return $null }

    # No viewOffX means the file came from a build that still wrote absolute coordinates,
    # which cannot be corrected. Loud, because the alternative is a probe that quietly
    # measures the wrong rectangle.
    if ($null -eq $ui.viewOffX) {
        throw ('ui-rows.json has no viewOffX/viewOffY -- this Debug build predates the ' +
               'window-relative origin, so its coordinates cannot be trusted. Rebuild it.')
    }

    # Reads before Start-BangGang have no window to anchor on, and an unanchored origin
    # is how coordinates silently end up near the desktop's corner.
    $main = $script:BBMain
    if ($null -eq $main -or $main -eq [IntPtr]::Zero) {
        throw 'ui-rows.json was read before Start-BangGang; there is no window to anchor it on'
    }

    $mr = Get-WinRect $main
    $ui | Add-Member -NotePropertyName viewX -NotePropertyValue ($mr.Left + [int]$ui.viewOffX) -Force
    $ui | Add-Member -NotePropertyName viewY -NotePropertyValue ($mr.Top + [int]$ui.viewOffY) -Force
    return $ui
}

# Every row, in document order. Always a real array: a JSON array with one
# element still comes back from ConvertFrom-Json as a scalar-ish object, and
# `$rows.Count` on that is the object's PROPERTY count, not 1.
function Get-UiRowList($ui) {
    if ($null -eq $ui) { return @() }
    return @($ui.rows)
}

# Rows of one role ('user' / 'assistant').
function Get-UiRowsOf($ui, [string]$Role) {
    return @(Get-UiRowList $ui | Where-Object { $_.role -eq $Role })
}

# A row's rectangle in SCREEN coordinates. Every click and every pixel read has
# to go through this -- the JSON is in the chat view's client space, and the view
# is offset by the sidebar. Reading $row.y as a screen y silently clicks the
# wrong thing, which then looks like a broken app.
#
# $ui must come from Get-UiRows, which has already re-anchored viewX/viewY on the
# window's current position -- going through the raw file instead gives coordinates
# that were true whenever the app last laid out, which is not necessarily now.
#
# Returns a hashtable, NOT a BB+RECT: BB+RECT has no Width/Height, and reading
# a missing property in PowerShell yields $null rather than an error (see the
# note on Get-WinRect).
function Get-UiRowScreen($ui, $row) {
    if ($null -eq $ui -or $null -eq $row) { return $null }
    return @{
        L = [int]$ui.viewX + [int]$row.x
        T = [int]$ui.viewY + [int]$row.y
        W = [int]$row.w
        H = [int]$row.h
    }
}

# A point inside a row, at a fraction across and down it. Default is the middle.
function Get-UiRowPoint($ui, $row, [double]$Fx = 0.5, [double]$Fy = 0.5) {
    $r = Get-UiRowScreen $ui $row
    if ($null -eq $r) { return $null }
    return @{
        X = [int]($r.L + $r.W * $Fx)
        Y = [int]($r.T + $r.H * $Fy)
    }
}

# Total ink height of the row stack, measured the way the app lays it out:
# from the first row's top to the last row's bottom. Used where a probe only
# cares "did anything appear / disappear".
function Get-UiRowsInkH($ui) {
    $rows = Get-UiRowList $ui
    if ($rows.Count -eq 0) { return 0 }
    $top = ($rows | ForEach-Object { [int]$_.y } | Measure-Object -Minimum).Minimum
    $bot = ($rows | ForEach-Object { [int]$_.y + [int]$_.h } | Measure-Object -Maximum).Maximum
    return $bot - $top
}
