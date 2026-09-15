# Screenshot the main window (Debug build, BANGGANG_SHOW_IN_CAPTURE=1).
#
# Release builds carry WDA_EXCLUDEFROMCAPTURE and come back as bare desktop,
# so this always launches the Debug exe.
#
# Usage:  powershell -File tools\shot.ps1 [-Out <name or path>]

param(
    [string]$Out = ''
)

. "$PSScriptRoot\_ui.ps1"

if ($Out -eq '') { $Out = Get-ShotPath 'main.png' }

Invoke-BBProbe {
    $main = Start-BangGang
    Save-WindowShot $main $Out
}

Write-BBDone 'shot'
