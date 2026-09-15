# Offline experiment: where do a single-line EDIT and a TextRenderer placeholder
# put their text, inside rectangles of different heights?
#
# The search box's placeholder is a separate control painted with TextRenderer,
# while the typed text is painted by the EDIT itself. They must coincide pixel for
# pixel, and they only do if both are anchored the same way -- so measure which
# shape each one actually follows:
#
#   - does the EDIT keep its text at a fixed offset from the top, or centre it?
#   - which TextFormatFlags reproduce that offset?
#
# No BangGang involved -- pure WinForms harness, fast and repeatable.
#
# Usage:  powershell -File tools\hint-align.ps1

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$text = [string]([char]0x641C + [char]0x7D22 + [char]0x5BF9 + [char]0x8BDD + [char]0x2026)

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
    return "$minX..$maxX x $minY..$maxY"
}

# A DrawToBitmap on an unparented TextBox renders nothing, so put it on a form.
$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.ShowInTaskbar = $false
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point -4000, -4000
$form.Size = New-Object System.Drawing.Size 300, 60

foreach ($pt in @(11.25, 10.5)) {
    $font = New-Object System.Drawing.Font 'Microsoft YaHei UI', $pt
    Write-Output ("=== " + $font.Name + " " + $pt + "pt   Font.Height=" + $font.Height + " ===")

    $tb = New-Object System.Windows.Forms.TextBox
    $tb.BorderStyle = [System.Windows.Forms.BorderStyle]::None
    $tb.AutoSize = $false
    $tb.Font = $font
    $tb.Text = $text
    $tb.Location = New-Object System.Drawing.Point 0, 0
    $form.Controls.Add($tb)
    [void]$form.Handle
    $form.Show()
    $null = $tb.Handle

    Write-Output ("  PreferredHeight = " + $tb.PreferredHeight)
    foreach ($h in @($tb.PreferredHeight, $tb.PreferredHeight + 6)) {
        $tb.Size = New-Object System.Drawing.Size 200, $h
        $bmp = New-Object System.Drawing.Bitmap 200, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::White)
        $g.Dispose()
        $tb.DrawToBitmap($bmp, (New-Object System.Drawing.Rectangle 0, 0, 200, $h))
        Write-Output ("  EDIT   h=" + $h.ToString().PadRight(3) + " : " + (Get-InkBox $bmp))
        $bmp.Dispose()
    }
    $form.Controls.Remove($tb)
    $tb.Dispose()

    foreach ($k in @('Top', 'VerticalCenter')) {
        $bmp = New-Object System.Drawing.Bitmap 200, 40
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::White)
        $flags = [System.Windows.Forms.TextFormatFlags]::$k `
                 -bor [System.Windows.Forms.TextFormatFlags]::Left `
                 -bor [System.Windows.Forms.TextFormatFlags]::NoPrefix `
                 -bor [System.Windows.Forms.TextFormatFlags]::NoPadding
        $rect = New-Object System.Drawing.Rectangle 0, 0, 200, 40
        [System.Windows.Forms.TextRenderer]::DrawText($g, $text, $font, $rect,
            [System.Drawing.Color]::Black, $flags)
        $g.Dispose()
        Write-Output ("  TextR  " + $k.PadRight(15) + " : " + (Get-InkBox $bmp))
        $bmp.Dispose()
    }
    $font.Dispose()
}

$form.Close()
$form.Dispose()
