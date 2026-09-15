# Characterise the difference between two same-sized PNGs: how many pixels differ,
# by how much, and what the differing ones look like. Written to chase down the
# resize staleness (tools\resize-lag.ps1) -- a scaled-down view of two screenshots
# hides a one-shade-of-grey difference completely.
#
# Usage:  powershell -File tools\img-diff.ps1 -A <before.png> -B <after.png> [-Crop x,y,w,h]

param(
    [Parameter(Mandatory)][string]$A,
    [Parameter(Mandatory)][string]$B,
    [string]$Crop = ''
)

. "$PSScriptRoot\_ui.ps1"

$x0 = 0; $y0 = 0; $cw = 0; $ch = 0
if ($Crop -ne '') {
    $p = $Crop.Split(',')
    $x0 = [int]$p[0]; $y0 = [int]$p[1]; $cw = [int]$p[2]; $ch = [int]$p[3]
}

$ba = [System.Drawing.Bitmap]::FromFile((Resolve-Path $A))
$bb = [System.Drawing.Bitmap]::FromFile((Resolve-Path $B))
if ($ba.Width -ne $bb.Width -or $ba.Height -ne $bb.Height) { throw 'size mismatch' }
if ($cw -eq 0) { $cw = $ba.Width - $x0; $ch = $ba.Height - $y0 }

$buckets = @{}
$n = 0
$samples = New-Object System.Collections.Generic.List[string]
for ($x = $x0; $x -lt ($x0 + $cw); $x++) {
    for ($y = $y0; $y -lt ($y0 + $ch); $y++) {
        $p = $ba.GetPixel($x, $y); $q = $bb.GetPixel($x, $y)
        $d = [Math]::Max([Math]::Abs($p.R - $q.R),
             [Math]::Max([Math]::Abs($p.G - $q.G), [Math]::Abs($p.B - $q.B)))
        if ($d -eq 0) { continue }
        $n++
        $k = [string]$d
        if ($buckets.ContainsKey($k)) { $buckets[$k] = $buckets[$k] + 1 } else { $buckets[$k] = 1 }
        if ($samples.Count -lt 12 -and ($x % 17) -eq 0) {
            $samples.Add("at $x,$y  A=R$($p.R) G$($p.G) B$($p.B)   B=R$($q.R) G$($q.G) B$($q.B)   d=$d")
        }
    }
}

Write-Output "crop $x0,$y0 ${cw}x${ch}   differing pixels: $n"
Write-Output 'delta histogram:'
foreach ($k in ($buckets.Keys | Sort-Object { [int]$_ })) {
    Write-Output ("  d=" + $k.PadLeft(3) + "  " + $buckets[$k])
}
Write-Output 'samples:'
foreach ($s in $samples) { Write-Output ("  " + $s) }

# the two crops side by side, so the difference can be looked at instead of guessed at
$ca = New-Object System.Drawing.Bitmap $cw, $ch
$ga = [System.Drawing.Graphics]::FromImage($ca)
$ga.DrawImage($ba, (New-Object System.Drawing.Rectangle 0, 0, $cw, $ch),
              (New-Object System.Drawing.Rectangle $x0, $y0, $cw, $ch), 'Pixel')
$ga.Dispose()
$cb = New-Object System.Drawing.Bitmap $cw, $ch
$gb = [System.Drawing.Graphics]::FromImage($cb)
$gb.DrawImage($bb, (New-Object System.Drawing.Rectangle 0, 0, $cw, $ch),
              (New-Object System.Drawing.Rectangle $x0, $y0, $cw, $ch), 'Pixel')
$gb.Dispose()

$stack = New-Object System.Drawing.Bitmap $cw, ($ch * 2 + 6)
$gs = [System.Drawing.Graphics]::FromImage($stack)
$gs.Clear([System.Drawing.Color]::Magenta)
$gs.DrawImage($ca, 0, 0)
$gs.DrawImage($cb, 0, ($ch + 6))
$gs.Dispose()
$out = Get-ShotPath 'img-diff-stack.png'
$stack.Save($out)
Write-Output ("saved " + $out + "  (" + $stack.Width + "x" + $stack.Height + "  top=A, bottom=B)")
$ca.Dispose(); $cb.Dispose(); $stack.Dispose()

$ba.Dispose(); $bb.Dispose()
