# The settings overlay paints the whole window: the card in the middle, and around it a
# SNAPSHOT of the main UI (SettingsOverlay.CaptureBackdrop walks the main window's other
# children, DrawToBitmap each into one bitmap). So with the settings open the user is not
# looking at the live main UI at all -- they are looking at a picture of it. Resize the
# window and the picture has to be re-taken; if it is re-taken at the wrong moment it keeps
# showing the main UI of a size the window no longer has, and the user reports "the old UI
# is still there, it does not update".
#
# That "wrong moment" is a real ordering hazard rather than a missing refresh:
# MainForm.ApplyLayout gives the overlay its new bounds BEFORE it re-lays out the controls
# the snapshot is made of, and the capture runs from the overlay's own OnResize -- i.e.
# synchronously, in the middle of that same method, while the rest of the layout is still
# on the previous size.
#
# The check does not compare against any colour constant. It photographs the window twice
# at the SAME size -- once with the settings open, once with them closed -- and requires
# everything outside the settings card to agree. A first round runs at the original size as
# a baseline, because the two shots are not bit-identical for reasons that have nothing to
# do with this bug (the chrome bar's status line is rewritten on open/close, and a native
# EDIT contributes its text but not its background to a WM_PRINT snapshot). Baseline noise
# is subtracted; a stale snapshot is orders of magnitude larger than it.
#
# Usage:  powershell -File tools\settings-resize.ps1

. "$PSScriptRoot\_ui.ps1"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BBX {
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h, IntPtr r, IntPtr hrgn, uint flags);
}
'@

# INVALIDATE|ERASE|ALLCHILDREN|UPDATENOW, same set tools/resize-lag.ps1 uses.
$script:RDW = 0x0001 -bor 0x0004 -bor 0x0080 -bor 0x0100

# -ReferencedAssemblies is required: PS 5.1's default compiler reference set does not
# include System.Drawing (same as tools/sidebar-anim.ps1).
Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class BBC {
    // Compares two same-size window shots pixel by pixel. Returns { n, minX, maxX, minY, maxY }.
    //
    // Two regions are left out of the comparison, both because they legitimately differ
    // between the two shots:
    //   skipTop -- everything above it. The chrome bar's status line is rewritten when the
    //              settings are opened and closed ("..."), and it is not the subject here.
    //   the rectangle (ex,ey,ew,eh) -- the settings card itself, which is one thing in shot
    //              A and the main UI in shot B. Passed in already grown by a margin.
    public static int[] Diff(Bitmap a, Bitmap b, int ex, int ey, int ew, int eh, int skipTop, int tol) {
        int w = a.Width, h = a.Height;
        var da = a.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int n = 0, minX = -1, maxX = -1, minY = -1, maxY = -1;
        try {
            byte[] ba = new byte[da.Stride * h], bb = new byte[db.Stride * h];
            Marshal.Copy(da.Scan0, ba, 0, ba.Length);
            Marshal.Copy(db.Scan0, bb, 0, bb.Length);
            int y0 = skipTop < 0 ? 0 : skipTop;
            for (int y = y0; y < h; y++) {
                bool rowExcluded = (y >= ey && y < ey + eh);
                int ra = y * da.Stride, rb = y * db.Stride;
                for (int x = 0; x < w; x++) {
                    if (rowExcluded && x >= ex && x < ex + ew) continue;
                    int i = x * 4;
                    if (Math.Abs(ba[ra + i]     - bb[rb + i])     <= tol &&
                        Math.Abs(ba[ra + i + 1] - bb[rb + i + 1]) <= tol &&
                        Math.Abs(ba[ra + i + 2] - bb[rb + i + 2]) <= tol) continue;
                    n++;
                    if (minX < 0 || x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (minY < 0 || y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        } finally { a.UnlockBits(da); b.UnlockBits(db); }
        return new int[] { n, minX, maxX, minY, maxY };
    }
}
'@

$script:fail = 0

# The settings card as the app lays it out: min(880, max(520, W-96)) x min(640, max(380, H-96)),
# centred in the client area (= the window rect, the form has no non-client border).
function Get-Card($main) {
    $mr = Get-WinRect $main
    $w = $mr.Right - $mr.Left
    $h = $mr.Bottom - $mr.Top
    $cw = [Math]::Min(880, [Math]::Max(520, $w - 96))
    $ch = [Math]::Min(640, [Math]::Max(380, $h - 96))
    return @{ X = $mr.Left + [int](($w - $cw) / 2); Y = $mr.Top + [int](($h - $ch) / 2); W = $cw; H = $ch }
}

# Checked against the real control tree rather than trusted: if the card ever stops being
# where this formula says, every exclusion below would be wrong and the probe would report
# nonsense. The card is by far the largest thing inside the overlay and it is centred.
function Test-CardShape($main, $card) {
    $mr = Get-WinRect $main
    foreach ($h in Get-WinKids $main) {
        if (-not [BB]::IsWindowVisible($h)) { continue }
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -lt ($card.W - 2) -or ($r.Right - $r.Left) -gt ($card.W + 2)) { continue }
        if (($r.Bottom - $r.Top) -lt ($card.H - 2) -or ($r.Bottom - $r.Top) -gt ($card.H + 2)) { continue }
        if ([Math]::Abs($r.Left - $card.X) -gt 2) { continue }
        if ([Math]::Abs($r.Top - $card.Y) -gt 2) { continue }
        return $true
    }
    return $false
}

# The card's own close button: the topmost 28x28 inside the card's right half. Taking the
# main window's chrome close button by mistake raises the unsaved-changes bar and then
# blocks the footer for the rest of the run.
function Get-CardClose($main, $card) {
    $mid = $card.X + $card.W / 2
    $best = $null
    foreach ($h in Get-WinKids $main) {
        if (-not [BB]::IsWindowVisible($h)) { continue }
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -ne 28 -or ($r.Bottom - $r.Top) -ne 28) { continue }
        if ($r.Left -lt $mid -or $r.Right -gt ($card.X + $card.W)) { continue }
        if ($r.Top -lt $card.Y -or $r.Bottom -gt ($card.Y + $card.H)) { continue }
        if ($null -eq $best -or $r.Top -lt $best.Top) { $best = $r }
    }
    return $best
}

function Get-WindowBitmap($main) {
    $r = Get-WinRect $main
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()
    return $bmp
}

# Off the window entirely: hovering a conversation row or the input box changes the main UI,
# and the two shots must be taken with the mouse in the same, uninteresting place. The app
# reads hover from the real cursor, so "no cursor over the window" is the only parked state
# that means the same thing in both shots.
function Park-Cursor($main) {
    $mr = Get-WinRect $main
    $x = $mr.Left - 24
    if ($x -lt 0) { $x = $mr.Right + 24 }
    [void][BB]::SetCursorPos($x, $mr.Top + 20)
    Start-Sleep -Milliseconds 300
}

# One round: open the settings, optionally resize the window while they are open, shoot,
# close the settings, shoot again, compare everything outside the card.
function Measure-Round($main, [string]$Tag, [int]$DragRight, [int]$DragDown) {
    $pill = Get-SearchPill $main
    $gear = Get-SettingsGear $main $pill
    if ($null -eq $gear) { throw 'settings gear not found' }
    Invoke-MouseClick ($gear.Left + 14) ($gear.Top + 14)
    Start-Sleep -Milliseconds 1500

    $before = Get-WinRect $main
    $bw = $before.Right - $before.Left
    $bh = $before.Bottom - $before.Top

    if ($DragRight -ne 0) {
        $mr = Get-WinRect $main
        $midY = $mr.Top + [int](($mr.Bottom - $mr.Top) / 2)
        Invoke-Drag ($mr.Right - 2) $midY (($mr.Right - 2) + $DragRight) $midY
    }
    if ($DragDown -ne 0) {
        $mr = Get-WinRect $main
        $midX = $mr.Left + [int](($mr.Right - $mr.Left) / 2)
        Invoke-Drag $midX ($mr.Bottom - 2) $midX (($mr.Bottom - 2) + $DragDown)
    }
    Start-Sleep -Milliseconds 500

    $after = Get-WinRect $main
    $aw = $after.Right - $after.Left
    $ah = $after.Bottom - $after.Top

    Park-Cursor $main
    $card = Get-Card $main
    # while the settings are still open -- the card is only there to be found
    $okShape = Test-CardShape $main $card
    $bmpOpen = Get-WindowBitmap $main
    Save-Shot $after.Left $after.Top $aw $ah (Get-ShotPath ("settings-resize-" + $Tag + "-open.png"))

    $close = Get-CardClose $main $card
    if ($null -eq $close) { throw "settings card close button not found ($Tag)" }
    Invoke-MouseClick ($close.Left + 14) ($close.Top + 14)
    Start-Sleep -Milliseconds 1200
    Park-Cursor $main

    # Shot B has to be the LIVE main UI, not the pixels the overlay left behind: force a
    # synchronous full repaint of the main window first. Without this a not-yet-repainted
    # area would still hold the backdrop, and "the screen matches the backdrop" would be
    # read as agreement when it is really the same stale image twice.
    [void][BBX]::RedrawWindow($main, [IntPtr]::Zero, [IntPtr]::Zero, $script:RDW)
    Start-Sleep -Milliseconds 700
    $bmpLive = Get-WindowBitmap $main
    Save-Shot $after.Left $after.Top $aw $ah (Get-ShotPath ("settings-resize-" + $Tag + "-live.png"))

    # sanity: the settings really did close (otherwise shot B is the same overlay and the
    # comparison would be vacuous)
    $stillOpen = $null -ne (Get-CardClose $main (Get-Card $main))
    if ($stillOpen) { throw "the settings overlay did not close ($Tag)" }

    $m = 4   # the card is opaque with no shadow; 4px covers its 1px border and any AA
    $d = [BBC]::Diff($bmpOpen, $bmpLive, $card.X - $after.Left - $m, $card.Y - $after.Top - $m,
                     $card.W + $m * 2, $card.H + $m * 2, 38, 8)
    $bmpOpen.Dispose()
    $bmpLive.Dispose()

    # Write-Host, not Write-Output: the caller assigns this function's return value, and
    # anything on the pipeline would be collected alongside it (the hashtable ends up mixed
    # into an array of strings, and every "$r.N" after that reads as $null).
    Write-Host ''
    Write-Host ("--- " + $Tag + " ---")
    Write-Host ("  window        : " + $bw + "x" + $bh + " -> " + $aw + "x" + $ah + $(if ($DragRight -ne 0 -or $DragDown -ne 0) { "   (dragged " + $DragRight + "," + $DragDown + ")" }))
    Write-Host ("  card          : " + $card.X + "," + $card.Y + " " + $card.W + "x" + $card.H + "   " + $(if ($okShape) { 'OK -- matches a real control' } else { 'FAIL -- no such control; the exclusion below is wrong' }))
    if (-not $okShape) { $script:fail++ }
    $box = $(if ($d[0] -eq 0) { '(none)' } else { $d[1].ToString() + ".." + $d[2] + " x " + $d[3].ToString() + ".." + $d[4] })
    Write-Host ("  backdrop vs live UI, outside the card : " + $d[0] + " px differ   " + $box)

    return @{ N = $d[0]; W = $aw; H = $ah; Box = $box }
}

Invoke-BBProbe {
    $main = Start-BangGang
    $mr = Get-WinRect $main

    # a conversation, so the thing behind the overlay is the full chat UI rather than the
    # welcome page -- the input panel and the chat view are what go stale
    $pill = Get-SearchPill $main
    $gear = Get-SettingsGear $main $pill
    Invoke-MouseClick ($gear.Left + 40 + 14) ($gear.Top + 14)
    Start-Sleep -Milliseconds 1500

    Write-Output ("window " + $mr.Left + "," + $mr.Top + " " + ($mr.Right - $mr.Left) + "x" + ($mr.Bottom - $mr.Top))

    $base = Measure-Round $main 'baseline' 0 0
    $wide = Measure-Round $main 'wider' 260 0
    $narrow = Measure-Round $main 'narrower' -300 0

    # A resize that silently did nothing would leave the snapshot correct and the probe
    # green -- the one way this check could pass while the bug is still there.
    if ($wide.W -eq $base.W) { Write-Output '  FAIL -- the widening drag changed nothing, so nothing was tested'; $script:fail++ }
    if ($narrow.W -eq $wide.W) { Write-Output '  FAIL -- the narrowing drag changed nothing, so nothing was tested'; $script:fail++ }

    Write-Output ''
    Write-Output ("  baseline (no resize) : " + $base.N + " px")
    foreach ($r in @(@('wider', $wide), @('narrower', $narrow))) {
        $tag = $r[0]; $res = $r[1]
        $ok = ($res.N -le $base.N + 500)
        Write-Output ("  " + $tag.PadRight(20) + ": " + $res.N + " px vs baseline   " + $(if ($ok) { 'OK -- the backdrop is the live UI' } else { 'FAIL -- the overlay still shows the previous window size' }))
        if (-not $ok) { $script:fail++ }
    }

    Write-Output ''
    if ($script:fail -eq 0) { Write-Output 'PASS: with the settings open, resizing the window keeps the backdrop equal to the live UI.' }
    else { Write-Output ("FAIL: " + $script:fail + " check(s) failed.") }
}

Write-BBDone 'settings-resize'
