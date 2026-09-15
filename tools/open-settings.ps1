# Open the settings overlay, select a rail page, screenshot it, and (optionally)
# dump the geometry of every single-line Edit on that page.
#
# Geometry it relies on (see SettingsOverlay.cs):
#   card is 880x640, centred in the window; inset 3
#   rail is 208 wide; nav rows start at y=96, step 48, x=14, 180x42
#
# Usage:  powershell -File tools\open-settings.ps1 [-Page 1] [-Dump] [-Out <name or path>]
#   -Page 0 = shortcuts, 1 = model access, 2 = appearance

param(
    [int]$Page = 1,
    [switch]$Dump,
    [string]$Out = ''
)

. "$PSScriptRoot\_ui.ps1"

if ($Out -eq '') { $Out = Get-ShotPath ("settings-page$Page.png") }

Invoke-BBProbe {
    $main = Start-BangGang
    $mr = Get-WinRect $main
    $ww = $mr.Right - $mr.Left
    $wh = $mr.Bottom - $mr.Top

    # search pill and settings gear are located structurally -- see _ui.ps1
    $pill = Get-SearchPill $main
    if ($null -eq $pill) { throw 'search pill not found' }
    $gear = Get-SettingsGear $main $pill
    if ($null -eq $gear) { throw 'settings gear not found' }

    $cardX = $mr.Left + [int](($ww - 880) / 2)
    $cardY = $mr.Top + [int](($wh - 640) / 2)
    Write-Output ("window " + $mr.Left + "," + $mr.Top + " " + $ww + "x" + $wh + "   card " + $cardX + "," + $cardY)

    Invoke-MouseClick ($gear.Left + 14) ($gear.Top + 14)
    Start-Sleep -Milliseconds 1100

    Invoke-MouseClick ($cardX + 14 + 90) ($cardY + 96 + 48 * $Page + 21)
    Start-Sleep -Milliseconds 1100

    Save-WindowShot $main $Out

    if ($Dump) {
        $cardY2 = $cardY + 100
        Write-Output '--- single-line Edit boxes on this page ---'
        foreach ($h in Get-WinKids $main) {
            if (-not (Get-WinClass $h).StartsWith('WindowsForms10.Edit')) { continue }
            if (-not [BB]::IsWindowVisible($h)) { continue }
            $r = Get-WinRect $h
            $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
            if ($ht -gt 40) { continue }               # skip the multi-line chat input
            if ($r.Left -lt $pill.Left) { continue }   # skip the sidebar search box
            Write-Output ("  Edit " + $r.Left + "," + $r.Top + " " + $w + "x" + $ht + "  ink: " + (Get-InkBoxAt ($r.Left - 4) $r.Top $w $ht))
        }
    }
}

Write-BBDone 'open-settings'
