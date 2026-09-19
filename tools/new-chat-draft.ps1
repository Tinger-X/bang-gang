# A new conversation must start with an EMPTY input box.
#
# The draft in the input box belongs to the conversation it was typed into. Started a new
# one -- from the sidebar "+", from the welcome page, or right after deleting the open
# conversation -- and the old draft came along, so a single Enter sent the previous
# conversation's half-written question into the new one. Nothing on screen looks wrong
# while that happens, which is exactly why this needs a probe.
#
# Three legs, and every one of them is paired with the positive signal that makes it mean
# something:
#
#   A  type a draft          -> the box really holds it (CONTROL: without this, "the box
#                               is empty after a new conversation" passes on a probe that
#                               never managed to type anything at all)
#   B  click "+"             -> empty
#   C  delete the open one   -> the NEXT conversation opened is empty as well (the draft
#                               went with the conversation it was typed into, it does not
#                               just wait around for a new one to be created), and one
#                               started from the welcome page after that is empty too
#   D  a draft that is an ATTACHMENT (pasted file) -> gone too
#
# B and D go through the sidebar "+", C through the list's delete slot and the welcome
# page's own button -- three different entry points into NewConversation /
# DeleteConversation, and C3 is the only leg that covers DeleteConversation's own clear:
# with that one removed every other check here still passes.
#
# The attachment strip is found as a WINDOW, not by pixels: DraftStrip is a real child
# control whose whole job is to be visible only while something is attached, so its
# visibility answers the question directly.
#
# ASCII ONLY (PS 5.1 reads a BOM-less .ps1 as ANSI; CJK literals are syntax errors).
#
# Usage:  powershell -File tools\new-chat-draft.ps1 [-Shot]

param([switch]$Shot)

. "$PSScriptRoot\_ui.ps1"

$script:fail = @()

function Check([string]$What, [bool]$Ok, [string]$Detail) {
    if ($Ok) { Write-Output ('  OK   ' + $What + ' -- ' + $Detail) }
    else { Write-Output ('  FAIL ' + $What + ' -- ' + $Detail); $script:fail += $What }
}

# The body area beside the sidebar, in screen coords -- where the welcome page paints.
# The sidebar's own width is whatever it currently is, never assumed (see SideW).
function Get-WelcomeArea($main) {
    $mr = Get-WinRect $main
    $mw = $mr.Right - $mr.Left
    $sw = 0
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if (($r.Right - $r.Left) -ge $mw) { continue }
        if ([Math]::Abs($r.Left - $mr.Left) -gt 1) { continue }
        if ([Math]::Abs($r.Top - ($mr.Top + 38)) -gt 1) { continue }
        if (($r.Right - $r.Left) -lt $sw) { continue }
        $sw = $r.Right - $r.Left
    }
    if ($sw -le 0) { throw 'sidebar not found' }
    return @{ X0 = $mr.Left + $sw; Y0 = $mr.Top + 38; X1 = $mr.Right - 8; Y1 = $mr.Bottom - 4 }
}

# The welcome page's "new conversation" button, or $null if the page is not up. The page
# is self-painted -- nothing on it has a handle -- so the button is found by COLOUR: it
# and the logo are the only two accent-filled widgets, and the button is the bottom band.
# (Same reading as welcome-click.ps1; see Get-AccentBands for why it is done this way.)
function Get-WelcomeButton($main) {
    $a = Get-WelcomeArea $main
    $b = Get-AccentBands $a.X0 $a.Y0 $a.X1 $a.Y1
    if ($null -eq $b -or $b.Bands.Count -lt 2) { return $null }
    $band = $b.Bands[$b.Bands.Count - 1]
    return @{ X = [int](($band.X0 + $band.X1) / 2) + $a.X0; Y = [int](($band.Y0 + $band.Y1) / 2) + $a.Y0 }
}

# The attachment strip, or [IntPtr]::Zero. DraftStrip sits inside the card with the SAME
# left edge and the same width as the text box and ends exactly 2px above it (both are
# laid out from card.Left + CardPadX), and it is hidden outright when the draft is empty.
function Get-AttachStrip($main, $edit) {
    $er = Get-WinRect $edit
    $ew = $er.Right - $er.Left
    foreach ($h in Get-WinKids $main) {
        if (-not [BB]::IsWindowVisible($h)) { continue }
        $r = Get-WinRect $h
        $w = $r.Right - $r.Left
        if ($w -lt 100) { continue }
        if ([Math]::Abs($r.Left - $er.Left) -gt 1) { continue }
        if ([Math]::Abs($w - $ew) -gt 2) { continue }
        $ht = $r.Bottom - $r.Top
        if ($ht -lt 20 -or $ht -gt 60) { continue }
        if ($r.Bottom -gt $er.Top - 1 -or $r.Bottom -lt $er.Top - 8) { continue }
        return $h
    }
    return [IntPtr]::Zero
}

# Drop a file list on the clipboard and let the app's own Ctrl+V path pick it up -- the
# same route a user takes, through the same TryPasteClipboard code. (Copied from
# draft-strip.ps1 rather than shared: _ui.ps1 is for geometry helpers, and the two probes
# use it for different things.)
function Send-FilesPaste($edit, [string[]]$Paths) {
    $col = New-Object System.Collections.Specialized.StringCollection
    foreach ($p in $Paths) { [void]$col.Add($p) }
    $ok = $false
    for ($i = 0; $i -lt 5 -and -not $ok; $i++) {
        try { [System.Windows.Forms.Clipboard]::SetFileDropList($col); $ok = $true }
        catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ok) { throw 'could not put the file list on the clipboard' }
    Start-Sleep -Milliseconds 250

    $r = Get-WinRect $edit
    Invoke-MouseClick ([int](($r.Left + $r.Right) / 2)) ([int](($r.Top + $r.Bottom) / 2))
    Start-Sleep -Milliseconds 300
    # Ctrl+V through SendInput (see BB.Chord for why neither SendKeys entry point works
    # from a console host). Deliver to the focused window, hence the click above.
    [BB]::Chord(0x56)
    Start-Sleep -Milliseconds 700
}

# The sidebar's two 28x28 head buttons, left to right: settings, then new conversation.
function Get-NewConvButton($main) {
    $pill = Get-SearchPill $main
    if ($null -eq $pill) { throw 'search pill not found' }
    $btns = Get-PillButtons $main $pill
    if ($null -eq $btns) { throw 'sidebar head buttons not found' }
    $b = $btns[1]
    return @{ X = [int](($b.Left + $b.Right) / 2); Y = [int](($b.Top + $b.Bottom) / 2) }
}

# ---- fixtures. Under shoots\ (gitignored), never in the system temp. ----
$fixDir = Join-Path $script:BBShoots 'fixtures'
if (-not (Test-Path $fixDir)) { [void](New-Item -ItemType Directory -Path $fixDir -Force) }
$txtPath = Join-Path $fixDir 'new-chat-draft.txt'
Set-Content -Path $txtPath -Value 'hello from new-chat-draft.ps1' -Encoding Ascii

# ---- the store is moved aside, not hoped to be empty ----
#
# Leg C DELETES a conversation, and the row it deletes is row 0 -- the most recently
# updated one, i.e. one of the user's own if this build has ever been used. chats\,
# conversations.json and settings.json are all backed up and put back in the finally,
# including when the body throws, which is when a half-restored store is most likely.
$dir = Split-Path $script:BBExe -Parent
$settings = Join-Path $dir 'settings.json'
$chatsDir = Join-Path $dir 'chats'
$legacy = Join-Path $dir 'conversations.json'

$hadSettings = Test-Path $settings
$bakSettings = $null
if ($hadSettings) { $bakSettings = Get-Content $settings -Raw }

# A leftover single-file store matters here too: with chats\ empty the app imports it on
# startup and its conversations come back into the list leg C deletes from.
$hadLegacy = Test-Path $legacy
$bakLegacy = $null
if ($hadLegacy) { $bakLegacy = Get-Content $legacy -Raw }

$bakChats = @{}
if (Test-Path $chatsDir) {
    foreach ($f in Get-ChildItem $chatsDir -File) { $bakChats[$f.Name] = (Get-Content $f.FullName -Raw) }
    Remove-Item (Join-Path $chatsDir '*') -Force
}
if (Test-Path $legacy) { Remove-Item $legacy -Force }

try {
    Invoke-BBProbe {
        $main = Start-BangGang 5
        $mr = Get-WinRect $main
        Write-Output ("window " + $mr.Left + "," + $mr.Top + " " + ($mr.Right - $mr.Left) + "x" + ($mr.Bottom - $mr.Top))

        # ---- 0. the welcome page, and the button we start every leg from ----
        Write-Output ''
        Write-Output '--- 0. start ---'
        $btn = Get-WelcomeButton $main
        Check 'app comes up on the welcome page' ($null -ne $btn) 'its logo and its button are both painted'
        if ($null -eq $btn) { throw 'the welcome page is not up; cannot continue' }

        # ---- A. the control: a draft really does land in the input box ----
        Write-Output ''
        Write-Output '--- A. type a draft (the control for every check below) ---'
        Invoke-MouseClick $btn.X $btn.Y
        Start-Sleep -Milliseconds 1200

        $edit = Get-InputEditBig $main
        Check 'starting a conversation brings up the input box' ($edit -ne [IntPtr]::Zero) 'a multiline EDIT exists'
        if ($edit -eq [IntPtr]::Zero) { throw 'no input box; cannot continue' }

        $typed = Invoke-TypeKeys $edit 'draft-one'
        Check 'the typed draft is in the input box' ($typed -eq 'draft-one') ('box reads "' + $typed + '"')

        # ---- B. the sidebar "+" ----
        Write-Output ''
        Write-Output '--- B. the sidebar + starts a new conversation ---'
        $plus = Get-NewConvButton $main
        Invoke-MouseClick $plus.X $plus.Y
        Start-Sleep -Milliseconds 1200
        $t = Get-WinText $edit
        Check 'the new conversation has an empty input box' ($t -eq '') ('box reads "' + $t + '"')
        Check 'and it is still a live conversation' ([BB]::IsWindowVisible($edit)) 'the input box is on screen'

        # ---- C. delete the open conversation ----
        Write-Output ''
        Write-Output '--- C. delete the open conversation ---'
        # The two "typed again" checks below ask only that the typing LANDED. Requiring the
        # box to read exactly the new word would make them fail as a knock-on effect of a
        # neighbouring check having left text behind -- two red lines for one bug, and the
        # second one points at the typing helper instead of at the app.
        $typedC = Invoke-TypeKeys $edit 'draft-two'
        Check 'draft typed again' ($typedC -like '*draft-two') ('box reads "' + $typedC + '"')

        # row 0's delete slot: 95% of the list width, centred, 38 tall, DelRect inset 8/6
        # (ConvListBox.ItemRect / DelRect). Clicking it does not require hovering first --
        # the hit test is the rectangle, the hover only decides whether it is DRAWN.
        $list = Get-ConvList $main
        if ($null -eq $list) { throw 'conversation list not found' }
        $lw = $list.R.Right - $list.R.Left
        $rowW = [int][Math]::Round($lw * 0.95)
        $rowX = $list.R.Left + [int](($lw - $rowW) / 2)
        Invoke-MouseClick ($rowX + $rowW - 6 - 22 + 11) ($list.R.Top + 8 + 11)
        Start-Sleep -Milliseconds 1000

        $btnC = Get-WelcomeButton $main
        Check 'deleting the open conversation lands on the welcome page' ($null -ne $btnC) 'logo and button are painted again'
        Check 'the input box went with it' (-not [BB]::IsWindowVisible($edit)) 'the EDIT is hidden'
        if ($null -eq $btnC) { throw 'the welcome page did not come back; cannot continue' }

        # C3: the other conversation left in the list. This is the leg that pins the draft
        # to the conversation it was typed into -- without DeleteConversation's own clear
        # the deleted conversation's draft is still sitting in the (hidden) box, and it
        # reappears here, in a conversation that never saw it.
        if (-not (Open-ConvRow $main 0)) { throw 'the second conversation disappeared from the list' }
        $t2 = Get-WinText $edit
        Check 'a draft does not survive into the next conversation opened' ($t2 -eq '') ('box reads "' + $t2 + '"')

        # C5: and a conversation started afterwards from the welcome page is empty too.
        $typedC2 = Invoke-TypeKeys $edit 'draft-four'
        Check 'draft typed once more' ($typedC2 -like '*draft-four') ('box reads "' + $typedC2 + '"')
        $list2 = Get-ConvList $main
        $lw2 = $list2.R.Right - $list2.R.Left
        $rowW2 = [int][Math]::Round($lw2 * 0.95)
        $rowX2 = $list2.R.Left + [int](($lw2 - $rowW2) / 2)
        Invoke-MouseClick ($rowX2 + $rowW2 - 6 - 22 + 11) ($list2.R.Top + 8 + 11)
        Start-Sleep -Milliseconds 1000
        $btnC2 = Get-WelcomeButton $main
        if ($null -eq $btnC2) { throw 'the welcome page did not come back after the second delete' }
        Invoke-MouseClick $btnC2.X $btnC2.Y
        Start-Sleep -Milliseconds 1200
        $t3 = Get-WinText $edit
        Check 'the conversation started after a delete has an empty input box' ($t3 -eq '') ('box reads "' + $t3 + '"')

        # ---- D. an ATTACHMENT draft is part of the draft too ----
        Write-Output ''
        Write-Output '--- D. a pasted attachment is dropped as well ---'
        Check 'no attachment strip to begin with' ((Get-AttachStrip $main $edit) -eq [IntPtr]::Zero) 'nothing attached yet'

        $typedD = Invoke-TypeKeys $edit 'draft-three'
        Send-FilesPaste $edit @($txtPath)
        Check 'pasting a file brings the attachment strip up' ((Get-AttachStrip $main $edit) -ne [IntPtr]::Zero) 'the strip is visible'
        Check 'and the typed draft is still there' ((Get-WinText $edit) -like '*draft-three') ('box reads "' + (Get-WinText $edit) + '"')

        $plus2 = Get-NewConvButton $main
        Invoke-MouseClick $plus2.X $plus2.Y
        Start-Sleep -Milliseconds 1200
        $t3 = Get-WinText $edit
        Check 'the new conversation has an empty input box' ($t3 -eq '') ('box reads "' + $t3 + '"')
        Check 'and no attachments came along' ((Get-AttachStrip $main $edit) -eq [IntPtr]::Zero) 'the strip is hidden'

        if ($Shot) { [void](Save-WindowShot $main (Get-ShotPath 'new-chat-draft.png')) }

        Write-Output ''
        if ($script:fail.Count -eq 0) {
            Write-Output 'PASS: a new conversation starts empty; an unsent draft does not follow it.'
        }
        else { Write-Output ('FAIL: ' + $script:fail.Count + ' check(s) failed: ' + ($script:fail -join '; ')) }
    }
} finally {
    if ($hadSettings) { Set-Content -Path $settings -Value $bakSettings -Encoding utf8 -NoNewline }
    else { Remove-Item $settings -ErrorAction SilentlyContinue }
    if ($hadLegacy) { Set-Content -Path $legacy -Value $bakLegacy -Encoding utf8 -NoNewline }
    else { Remove-Item $legacy -ErrorAction SilentlyContinue }
    # Only the files this probe may have created are removed; everything that was there
    # before is rewritten from the backup.
    if (Test-Path $chatsDir) {
        foreach ($f in Get-ChildItem $chatsDir -File) {
            if (-not $bakChats.ContainsKey($f.Name)) { Remove-Item $f.FullName -Force }
        }
    }
    foreach ($n in $bakChats.Keys) {
        if (-not (Test-Path $chatsDir)) { New-Item -ItemType Directory -Force $chatsDir | Out-Null }
        Set-Content -Path (Join-Path $chatsDir $n) -Value $bakChats[$n] -Encoding utf8 -NoNewline
    }
}

Write-BBDone 'new-chat-draft'
