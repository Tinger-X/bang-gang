# Offline experiment: what is a multiline EDIT's real line pitch, and does GDI+'s
# Font.Height agree with it?
#
# The input card sizes the text box as MaxLines * Font.Height and derives the visible
# line count as boxH / Font.Height. If the EDIT lays text out on a different pitch than
# Font.Height reports, both numbers are wrong in the same direction: the box is shorter
# than three lines, and the code still believes three fit -- so the scrollbar stays hidden
# while the text is already scrolling. That is exactly the reported symptom, so measure
# the two numbers against each other instead of assuming they agree.
#
# No BangGang involved -- pure WinForms harness, fast and repeatable.
#
# Usage:  powershell -File tools\edit-lines.ps1

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ELE {
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TEXTMETRICW {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading;
        public int tmAveCharWidth, tmMaxCharWidth, tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll", EntryPoint = "GetTextMetricsW")]
    public static extern bool GetTextMetricsW(IntPtr dc, out TEXTMETRICW tm);
    [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int i);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
}
'@

$EM_GETLINECOUNT      = 0x00BA
$EM_GETFIRSTVISIBLELINE = 0x00CE
$EM_POSFROMCHAR       = 0x00D6

# y of the line box containing character $index, in the EDIT's client coordinates.
function Get-CharY($h, [int]$index) {
    $r = [ELE]::SendMessage($h, $EM_POSFROMCHAR, [IntPtr]$index, [IntPtr]::Zero)
    return [int](($r.ToInt64() -shr 16) -band 0xFFFF)
}

function Get-LineCount($h) { return [int][ELE]::SendMessage($h, $EM_GETLINECOUNT, [IntPtr]::Zero, [IntPtr]::Zero) }
function Get-FirstVis($h) { return [int][ELE]::SendMessage($h, $EM_GETFIRSTVISIBLELINE, [IntPtr]::Zero, [IntPtr]::Zero) }

$form = New-Object System.Windows.Forms.Form
$form.ClientSize = New-Object System.Drawing.Size 400, 400
$form.ShowInTaskbar = $false

$box = New-Object System.Windows.Forms.TextBox
$box.Multiline = $true
$box.BorderStyle = [System.Windows.Forms.BorderStyle]::None
$box.ScrollBars = [System.Windows.Forms.ScrollBars]::None
$box.WordWrap = $true
$box.AcceptsReturn = $true
$box.Font = New-Object System.Drawing.Font 'Microsoft YaHei UI', 12.5
$box.Location = New-Object System.Drawing.Point 10, 10
$box.Size = New-Object System.Drawing.Size 300, 200
$form.Controls.Add($box)
$form.Show()
[System.Windows.Forms.Application]::DoEvents()

Write-Output ("Font.Height (GDI+)      : " + $box.Font.Height)
Write-Output ("Font.Size / SizeInPoints: " + $box.Font.Size + " / " + $box.Font.SizeInPoints)

# Five hard lines, no wrapping (width is wide enough).
$lines = @('aaaa', 'bbbb', 'cccc', 'dddd', 'eeee')
$box.Text = [string]::Join("`r`n", $lines)
[System.Windows.Forms.Application]::DoEvents()
$h = $box.Handle

Write-Output ("EM_GETLINECOUNT         : " + (Get-LineCount $h))
$ys = @()
for ($i = 0; $i -lt 5; $i++) { $ys += (Get-CharY $h ($i * 6)) }
Write-Output ("line tops (char y)      : " + ($ys -join ', '))
$pitch = @()
for ($i = 1; $i -lt $ys.Count; $i++) { $pitch += ($ys[$i] - $ys[$i - 1]) }
Write-Output ("pitch between lines     : " + ($pitch -join ', '))
Write-Output ("first line top          : " + $ys[0])

# Measure how many lines actually have ink inside the client area, from a render.
#
# This, not Font.Height, is the ground truth: the EDIT lays lines out on its own pitch
# and will not paint a line that does not fit **whole** inside the formatting rectangle,
# so a box that is a hair short of N lines shows N-1 of them and silently keeps the rest
# of the space blank. Counting bands catches that; dividing heights does not.
function Count-Bands([int]$boxH) {
    $box.Size = New-Object System.Drawing.Size 300, $boxH
    $box.Text = [string]::Join("`r`n", $lines)
    $box.SelectionStart = $box.Text.Length
    [System.Windows.Forms.Application]::DoEvents()
    $bmp = New-Object System.Drawing.Bitmap 300, $boxH
    $box.DrawToBitmap($bmp, (New-Object System.Drawing.Rectangle 0, 0, 300, $boxH))
    $bands = New-Object System.Collections.Generic.List[string]
    $inBand = $false
    $start = -1
    for ($y = 0; $y -lt $boxH; $y++) {
        $ink = $false
        for ($x = 0; $x -lt 60; $x++) {
            $c = $bmp.GetPixel($x, $y)
            if ((0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B) -lt 160) { $ink = $true; break }
        }
        if ($ink -and -not $inBand) { $inBand = $true; $start = $y }
        elseif (-not $ink -and $inBand) { $inBand = $false; $bands.Add("$start.." + ($y - 1)) }
    }
    if ($inBand) { $bands.Add("$start.." + ($boxH - 1)) }
    $bmp.Dispose()
    return ($bands -join ' | ') + "   (count " + $bands.Count + ")"
}

# The pitch the EDIT actually lays lines out on = tmHeight + tmExternalLeading of the DC
# carrying its font. GDI+'s Font.Height is only the first half of that.
$dc = [ELE]::CreateCompatibleDC([IntPtr]::Zero)
$hFont = $box.Font.ToHfont()
$old = [ELE]::SelectObject($dc, $hFont)
$tm = New-Object ELE+TEXTMETRICW
[void][ELE]::GetTextMetricsW($dc, [ref]$tm)
[void][ELE]::SelectObject($dc, $old)
[void][ELE]::DeleteObject($hFont)
[void][ELE]::DeleteDC($dc)
$pitch = $tm.tmHeight + $tm.tmExternalLeading
Write-Output ("tmHeight/tmExternalLeading: " + $tm.tmHeight + " / " + $tm.tmExternalLeading + "  -> pitch " + $pitch)
Write-Output ''

# What the card did before 0.7.32: boxH = 3 * Font.Height, then it believed 3 lines fit.
$boxH = 3 * $box.Font.Height
Write-Output ("box height = 3 * Font.Height : " + $boxH + "   code would say visible: " + [Math]::Floor($boxH / $box.Font.Height))
Write-Output ("  ink bands on screen       : " + (Count-Bands $boxH))

Write-Output ''
Write-Output ("box height = 3 * pitch : " + (3 * $pitch) + "   code would say visible: " + [Math]::Floor((3 * $pitch) / $pitch))
Write-Output ("  ink bands on screen       : " + (Count-Bands (3 * $pitch)))

Write-Output ''
Write-Output 'how many lines each candidate height really shows:'
foreach ($n in @(2, 3, 4)) {
    Write-Output ("  " + $n + " * pitch = " + ($n * $pitch) + "  ->  " + (Count-Bands ($n * $pitch)))
}

$form.Close()
$form.Dispose()
