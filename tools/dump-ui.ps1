# Dump every child HWND of the main window with its real geometry.
#
# WinForms controls are real HWNDs, so EnumChildWindows + GetWindowRect gives
# ground truth -- more reliable than eyeballing a screenshot, and it catches
# sizes WinForms silently rewrote (a single-line TextBox forced back to
# PreferredHeight, for instance).
#
# Usage:  powershell -File tools\dump-ui.ps1

. "$PSScriptRoot\_ui.ps1"

Invoke-BBProbe {
    $main = Start-BangGang
    Write-WinTree $main
}

Write-BBDone 'dump-ui'
