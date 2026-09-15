# Report the REAL font of a TextBox and its placeholder sibling, and whether the
# glyphs actually fit inside the EDIT's box.
#
# Two things it settles:
#   1. WM_GETFONT on both HWNDs -- if the placeholder was assigned a different
#      HFONT than the EDIT, the ink boxes differ in size, not just in AA weight.
#   2. A padded crop around the EDIT after typing a descender-heavy string
#      ("gjy"): if the ink's bottom row lands exactly on the EDIT's bottom edge,
#      the text is being clipped, not just sitting low.
#
# The crop is 10px taller than the EDIT on each side, so the EDIT occupies rows
# [pad, pad + editHeight). Ink touching row pad+editHeight-1 is clipped.
#
# Usage:  powershell -File tools\text-fit.ps1 [-Target search|field]

param(
    [ValidateSet('search', 'field')]
    [string]$Target = 'field',
    [string]$Probe = 'gjy'
)

. "$PSScriptRoot\_ui.ps1"

Invoke-BBProbe {
    $main = Start-BangGang
    $mr = Get-WinRect $main
    $ww = $mr.Right - $mr.Left
    $wh = $mr.Bottom - $mr.Top

    if ($Target -eq 'field') {
        $pill = Get-SearchPill $main
        if ($null -eq $pill) { throw 'search pill not found' }
        $gear = Get-SettingsGear $main $pill
        if ($null -eq $gear) { throw 'settings gear not found' }

        $cardX = $mr.Left + [int](($ww - 880) / 2)
        $cardY = $mr.Top + [int](($wh - 640) / 2)
        Invoke-MouseClick ($gear.Left + 14) ($gear.Top + 14)
        Start-Sleep -Milliseconds 1100
        Invoke-MouseClick ($cardX + 14 + 90) ($cardY + 96 + 48 * 1 + 21)
        Start-Sleep -Milliseconds 1100
    }
    else {
        Invoke-MouseClick ($mr.Left + $ww - 300) ($mr.Top + $wh - 100)
        Start-Sleep -Milliseconds 600
        $pill = Get-SearchPill $main
        if ($null -eq $pill) { throw 'search pill not found' }
    }

    # search EDIT = pill width - TextPadX(10) - 26 reserved for the clear button.
    # Derived, not hard-coded: the pill is SideW-106 and that changes with the sidebar.
    $want = ($pill.Right - $pill.Left) - 36
    if ($Target -eq 'field') { $want = 306 }

    $edit = [IntPtr]::Zero
    foreach ($h in Get-WinKids $main) {
        if (-not (Get-WinClass $h).StartsWith('WindowsForms10.Edit')) { continue }
        if (-not [BB]::IsWindowVisible($h)) { continue }
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -eq $want) { $edit = $h; break }
    }
    if ($edit -eq [IntPtr]::Zero) { throw "no Edit of width $want on the $Target target" }

    $er = Get-WinRect $edit
    $ec = Get-ClientRect $edit
    $ew = $er.Right - $er.Left
    $eh = $er.Bottom - $er.Top

    Write-Output "=== $Target : EDIT $($er.Left),$($er.Top) ${ew}x${eh} ==="
    Write-Output ("  client rect   : " + $ec.Right + "x" + $ec.Bottom)
    Write-Output ("  EDIT font     : " + (Get-WinFont $edit))
    Write-Output ("  font extent   : " + (Get-TextExtent $edit $Probe) + "   (needs to be <= " + $ec.Bottom + "px tall to fit)")

    # the placeholder is the sibling sharing the EDIT's exact rect
    foreach ($h in Get-WinKids $main) {
        if ($h -eq $edit) { continue }
        $r = Get-WinRect $h
        if ($r.Left -ne $er.Left -or $r.Top -ne $er.Top -or
            ($r.Right - $r.Left) -ne $ew -or ($r.Bottom - $r.Top) -ne $eh) { continue }
        Write-Output ("  placeholder   : " + (Get-WinClass $h) + "  " + $(if ([BB]::IsWindowVisible($h)) { 'visible' } else { 'hidden' }))
        Write-Output ("  placeholder fnt: " + (Get-WinFont $h))
    }

    # padded crop: 10px of slack above and below the EDIT
    $pad = 10
    $cx = $er.Left - 4
    $cy = $er.Top - $pad
    $cw = 130
    $ch = $eh + $pad * 2

    Write-Output ("  crop rows: 0.." + ($pad - 1) + " above EDIT, " + $pad + ".." + ($pad + $eh - 1) + " = EDIT, " + ($pad + $eh) + ".." + ($ch - 1) + " below")

    [void][BB]::SendText($edit, $Probe)
    Start-Sleep -Milliseconds 700
    $boxTyped = Get-InkBoxAt $cx $cy $cw $ch
    Write-Output ("  typed '$Probe' ink : " + $boxTyped)
    Save-Shot $cx $cy $cw $ch (Get-ShotPath "fit-$Target-typed.png")

    # select-all paints the EDIT's own client rect, which shows the real box.
    # The EDIT only paints a selection when it has focus, so click into it first --
    # EM_SETSEL alone is silent while the caret lives elsewhere.
    Invoke-MouseClick ($er.Left + 20) ($er.Top + [int]($eh / 2))
    Start-Sleep -Milliseconds 400
    [void][BB]::SendMessage($edit, 0x00B1, [IntPtr]::Zero, [IntPtr](-1))
    Start-Sleep -Milliseconds 700
    Write-Output ("  selected ink  : " + (Get-InkBoxAt $cx $cy $cw $ch))
    Save-Shot $cx $cy $cw $ch (Get-ShotPath "fit-$Target-selected.png")

    Write-Output ''
    Write-Output ("  EDIT bottom edge = row " + ($pad + $eh - 1) + "; ink reaching that row means clipping")
}

Write-BBDone 'text-fit'
