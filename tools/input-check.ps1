# input-check.ps1 -- inspect the message input area.
#
# Opens a conversation (the welcome page has no input area at all -- the input
# panel is a child of _chatUI, which is hidden until a conversation exists),
# then dumps the geometry of everything in the bottom band and saves two crops:
# the empty state and the "has text" state.
#
# Usage:  powershell -File tools\input-check.ps1
#
# Output: shoots/input-empty.png, shoots/input-typed.png

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_ui.ps1"

# The welcome page's "new conversation" button is a solid accent-coloured pill;
# it is drawn, not an HWND, so find it by colour. The logo circle is the same
# colour, hence the "below the middle" restriction.
function Find-AccentButton($bmp) {
    $accent = [System.Drawing.Color]::FromArgb(47, 112, 224)
    $minX = -1; $maxX = -1; $minY = -1; $maxY = -1; $n = 0
    for ($x = 0; $x -lt $bmp.Width; $x += 2) {
        for ($y = [int]($bmp.Height * 0.55); $y -lt $bmp.Height; $y += 2) {
            $c = $bmp.GetPixel($x, $y)
            if ([Math]::Abs($c.R - $accent.R) -le 6 -and [Math]::Abs($c.G - $accent.G) -le 6 -and [Math]::Abs($c.B - $accent.B) -le 6) {
                $n++
                if ($minX -lt 0 -or $x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($minY -lt 0 -or $y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($n -lt 50) { return $null }
    return @{ X = [int](($minX + $maxX) / 2); Y = [int](($minY + $maxY) / 2); W = $maxX - $minX; H = $maxY - $minY }
}

Invoke-BBProbe {
    $main = Start-BangGang
    $mr = Get-WinRect $main
    $w = $mr.Right - $mr.Left; $h = $mr.Bottom - $mr.Top

    $shot = Get-ShotPath 'welcome.png'
    Save-WindowShot $main $shot
    $bmp = [System.Drawing.Image]::FromFile($shot)
    $btn = Find-AccentButton $bmp
    $bmp.Dispose()
    if ($null -eq $btn) { throw 'welcome: new-conversation button not found' }
    Write-Output ("new-conversation button: " + $btn.W + "x" + $btn.H + " at +" + $btn.X + "+" + $btn.Y)

    Invoke-MouseClick ($mr.Left + $btn.X) ($mr.Top + $btn.Y)
    Start-Sleep -Milliseconds 900

    # The input panel is pinned to the bottom 150px of the client area
    # (MainForm.ApplyLayout). The window is borderless, so client == window rect.
    $bandTop = $mr.Top + $h - 150
    Write-Output ''
    Write-Output '--- controls in the bottom 150px band ---'
    foreach ($child in Get-WinKids $main) {
        $r = Get-WinRect $child
        if ($r.Bottom -le $bandTop) { continue }
        $c = Get-WinClass $child
        if ($c.StartsWith('WindowsForms10.')) { $c = $c.Substring(0, $c.IndexOf('.app')) -replace '^WindowsForms10\.', '' }
        Write-Output ('  ' + $c.PadRight(14) + ' win ' + $r.Left + ',' + $r.Top + ' ' + ($r.Right - $r.Left) + 'x' + ($r.Bottom - $r.Top) +
                      '   rel ' + ($r.Left - $mr.Left) + ',' + ($r.Top - $bandTop) + '   ' +
                      $(if ([BB]::IsWindowVisible($child)) { 'vis' } else { 'hid' }))
    }

    Save-Shot $mr.Left $bandTop $w 150 (Get-ShotPath 'input-empty.png')

    # Put some text in the box so the send button's enabled look is exercised.
    # Type it (WM_CHAR) rather than SendText (WM_SETTEXT) -- see BB.Type's note:
    # WM_SETTEXT leaves the native edit holding the string without ever raising
    # WinForms' TextChanged, so the placeholder stays up and the "typed" shot is
    # really the empty state.  Assert the round-trip instead of trusting it.
    $edit = [IntPtr]::Zero
    foreach ($child in Get-WinKids $main) {
        $r = Get-WinRect $child
        if ($r.Bottom -le $bandTop) { continue }
        if ((Get-WinClass $child) -like '*Edit*') { $edit = $child }
    }
    if ($edit -eq [IntPtr]::Zero) { throw 'no EDIT found in the input band' }

    $got = [BB]::Type($edit, 'hello')
    Write-Output ''
    Write-Output ("EDIT text after typing = '" + $got + "'")
    if ($got -ne 'hello') { throw "EDIT did not take the text (got '$got')" }

    # The placeholder must be gone now, otherwise the screenshot shows it drawn
    # over the typed text.  Match on the *native* class name: a WinForms Label's
    # GetClassName is "WindowsForms10.STATIC.app.0.xxxx", never plain "Static".
    foreach ($child in Get-WinKids $main) {
        $r = Get-WinRect $child
        if ($r.Bottom -le $bandTop) { continue }
        if ((Get-WinClass $child) -like '*STATIC*') {
            Write-Output ("Static '" + [BB]::Tx($child) + "'  rel " + ($r.Left - $mr.Left) + ',' + ($r.Top - $bandTop) +
                          "  visible=" + [BB]::IsWindowVisible($child))
        }
    }
    Save-Shot $mr.Left $bandTop $w 150 (Get-ShotPath 'input-typed.png')
}

Write-BBDone 'input-check'
