# Hotkey gating while the settings overlay is open (0.9.7):
#   the show/hide hotkey must STILL work with settings open (hide, then show again),
#   while shot / record stay blocked (that half is unchanged code, not re-asserted here).
#
# [BB]::Chord only sends Ctrl+<key>, so the probe rewrites the hide binding in the
# Debug settings.json to Ctrl+X first (backed up and restored in finally, like
# persist-check does). RegisterHotKey is global, so the chord reaches the app no
# matter which window has focus -- including when the settings overlay holds it.
. "$PSScriptRoot\_ui.ps1"

$settingsPath = Join-Path (Split-Path $script:BBExe -Parent) 'settings.json'
$backup = $null

Invoke-BBProbe {
    if (Test-Path $settingsPath) {
        $script:backup = Get-Content $settingsPath -Raw
    }

    # Rebind hide to Ctrl+X (Vk 0x58), keep the rest of the file untouched.
    $s = Get-Content $settingsPath -Raw | ConvertFrom-Json
    foreach ($sc in $s.Shortcuts) {
        if ($sc.Action -eq 'hide') {
            $sc.Ctrl = $true
            $sc.Alt = $false
            $sc.Shift = $false
            $sc.Vk = 0x58
        }
    }
    ($s | ConvertTo-Json -Depth 8) | Set-Content $settingsPath -Encoding utf8

    try {
        $main = Start-BangGang
        if (-not [BB]::IsWindowVisible($main)) { throw 'main window should start visible' }

        # Baseline: chord toggles with settings closed.
        [BB]::Chord(0x58)
        Start-Sleep -Milliseconds 700
        if ([BB]::IsWindowVisible($main)) { throw 'hide hotkey did not hide the window (settings closed)' }
        Write-Output '[ok  ] hide hotkey hides the window (settings closed)'

        [BB]::Chord(0x58)
        Start-Sleep -Milliseconds 700
        if (-not [BB]::IsWindowVisible($main)) { throw 'hide hotkey did not show the window again' }
        Write-Output '[ok  ] hide hotkey shows the window again'

        # Open settings.
        $pill = Get-SearchPill $main
        $gear = Get-SettingsGear $main $pill
        Invoke-MouseClick ($gear.Left + 14) ($gear.Top + 14)
        Start-Sleep -Milliseconds 1100
        if (-not [BB]::IsWindowVisible($main)) { throw 'window vanished when opening settings' }

        # The point of the probe: same chord must STILL toggle with settings open.
        [BB]::Chord(0x58)
        Start-Sleep -Milliseconds 700
        if ([BB]::IsWindowVisible($main)) { throw 'hide hotkey did not hide the window while settings open' }
        Write-Output '[ok  ] hide hotkey hides the window while settings open'

        [BB]::Chord(0x58)
        Start-Sleep -Milliseconds 700
        if (-not [BB]::IsWindowVisible($main)) { throw 'window did not come back with settings open' }
        Write-Output '[ok  ] window comes back, settings still open'
    }
    finally {
        if ($null -ne $script:backup) {
            $script:backup | Set-Content $settingsPath -Encoding utf8
        }
    }
}

Write-BBDone 'hotkey-in-settings'
