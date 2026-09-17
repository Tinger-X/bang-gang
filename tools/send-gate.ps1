# With no model configured, clicking send must not send -- it must say why, at the top.
#
# The old behaviour was silence: the button was clickable, the click did nothing, and the
# text the user had typed stayed where it was with no explanation. So the checks are all
# about what DID NOT happen (no bubble, no file on disk) plus one about what DID (the
# chrome status strip). "Nothing happened" is exactly the symptom being fixed, which is
# why every one of those assertions is paired with the control group in phase B: a send
# button that is simply broken passes phase A perfectly.
#
# Two launches, because the provider lives in settings.json and that is read at startup:
#   A  settings.json is {"ThemeMode":"light"} only  -> the click must be refused
#   B  the same file with a filled-in profile       -> the same click must send
# The endpoint in B points at 127.0.0.1:9 (discard), which refuses instantly; the reply
# then lands as an error bubble. That is fine -- B only asks whether the message went out,
# and the user's own message is written to chats\ before the request is even attempted.
#
# The status text is compared as "changed, and starts with the endpoint complaint", never
# as an exact match: the rest of the sentence ("open Settings -> ...") is prose that will
# be reworded, and pinning it here would turn a copy edit into a probe failure. The few
# characters that ARE pinned are spelled as code points -- this file has to stay pure
# ASCII, see CLAUDE.md.
#
# Usage:  powershell -File tools\send-gate.ps1

. "$PSScriptRoot\_ui.ps1"

$script:dir = Split-Path $script:BBExe -Parent
$script:settings = Join-Path $script:dir 'settings.json'
$script:chatsDir = Join-Path $script:dir 'chats'
$script:legacy = Join-Path $script:dir 'conversations.json'

$script:fail = 0
function Check([bool]$ok, [string]$label, [string]$detail) {
    $tag = 'FAIL'
    if ($ok) { $tag = 'ok  ' }
    if (-not $ok) { $script:fail++ }
    Write-Output ('  [' + $tag + '] ' + $label + '  ' + $detail)
}

function U { param([int[]]$Codes) return (-join ($Codes | ForEach-Object { [char]$_ })) }

# "not yet filled in the endpoint" -- the head of LlmConfig.Problem's first sentence.
$NEED_URL = U 0x5C1A,0x672A,0x586B,0x5199,0x63A5,0x53E3

# ---------------- window lookups ----------------

# The chat view: 48px below the chrome bar, running to the window's right edge. Same
# lookup as chat-area.ps1 -- _mainArea / _chatUI span the whole window now, so the left
# edge cannot name them.
function Get-ChatView($main) {
    $mr = Get-WinRect $main
    $best = $null
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Top -ne ($mr.Top + 38 + 48)) { continue }
        if ($r.Right -ne $mr.Right) { continue }
        if ($null -eq $best -or $r.Left -lt $best.R.Left) { $best = @{ H = $h; R = $r } }
    }
    return $best
}

# How many bubbles the chat area is showing.
#
# 0.9.0 note: bubbles are painted by ChatView now, not one child HWND each, so the
# enumeration this used to do returns 0 -- and "0 bubbles" reads exactly like "the
# message was never rendered", which would make every assertion below pass or fail for
# the wrong reason. It reads the row snapshot ChatView writes instead; see the
# ui-rows.json block in _ui.ps1.
function Count-Bubbles($chat) {
    if ($null -eq $chat) { return -1 }
    return @(Get-UiRowList (Get-UiRows)).Count
}

# The conversation's own EDIT. Found in the bottom band and taken as the LAST match, the
# same way input-check.ps1 does it: the sidebar's search field is an EDIT too, and it is
# up at the top, while the input card is always the bottom-most thing in the window.
function Get-InputEdit($main) {
    $mr = Get-WinRect $main
    $bandTop = $mr.Bottom - 220
    $edit = [IntPtr]::Zero
    foreach ($h in Get-WinKids $main) {
        $r = Get-WinRect $h
        if ($r.Bottom -le $bandTop) { continue }
        if ((Get-WinClass $h) -like '*Edit*') { $edit = $h }
    }
    return $edit
}

function Get-JsonFiles {
    if (-not (Test-Path $script:chatsDir)) { return @() }
    return @(Get-ChildItem $script:chatsDir -Filter *.json -File -ErrorAction SilentlyContinue)
}

# ---------------- fixtures ----------------

# Only the theme: no url, no model, no profile. This is what "the user never opened the
# settings page" actually looks like on disk.
$SETTINGS_BARE = '{"ThemeMode":"light"}'

# The control group's settings. url/model are enough for LlmConfig.Problem to clear --
# it does not check the key, and shouldn't: a local server needs none.
$SETTINGS_OK = '{"ThemeMode":"light","ChatProvider":"custom","ChatProfiles":{"custom":{"url":"http://127.0.0.1:9/v1","key":"probe","model":"probe-model"}}}'

$TYPED = 'hello there'

# ---------------- backups ----------------

$hadSettings = Test-Path $script:settings
$bakSettings = $null
if ($hadSettings) { $bakSettings = Get-Content $script:settings -Raw }

$hadLegacy = Test-Path $script:legacy
$bakLegacy = $null
if ($hadLegacy) { $bakLegacy = Get-Content $script:legacy -Raw }

$bakChats = @{}
if (Test-Path $script:chatsDir) {
    foreach ($f in Get-ChildItem $script:chatsDir -File) { $bakChats[$f.Name] = (Get-Content $f.FullName -Raw) }
}

# Empties chats\ without removing the directory: both phases want to start from "this
# build has never been run".
function Reset-Chats {
    if (Test-Path $script:chatsDir) { Remove-Item (Join-Path $script:chatsDir '*') -Force }
    else { [void](New-Item -ItemType Directory -Force $script:chatsDir) }
    if (Test-Path $script:legacy) { Remove-Item $script:legacy -Force }
}

try {
    # ================= A. nothing configured -> the click is refused =================
    Reset-Chats
    Set-Content -Path $script:settings -Value $SETTINGS_BARE -Encoding utf8

    Write-Output '--- A. no model configured: the send must be refused ---'
    Invoke-BBProbe {
        $main = Start-BangGang 5

        # The welcome page is up (no conversations), so the chat view and the input box do
        # not exist yet -- their parent is hidden and WinForms never creates the handles.
        # The sidebar's new-conversation button is a real HWND and is the way in.
        $pill = Get-SearchPill $main
        if ($null -eq $pill) { throw 'search pill not found' }
        $pbtns = Get-PillButtons $main $pill
        if ($null -eq $pbtns) { throw 'sidebar buttons not found' }
        $plus = $pbtns[1]
        Invoke-MouseClick ([int](($plus.Left + $plus.Right) / 2)) ([int](($plus.Top + $plus.Bottom) / 2))
        Start-Sleep -Milliseconds 900

        $edit = Get-InputEdit $main
        if ($edit -eq [IntPtr]::Zero) { throw 'the input box never appeared after starting a conversation' }
        $ibtns = Get-InputButtons $main $edit
        if ($null -eq $ibtns) { throw 'attach / send buttons not found' }
        $status = Get-ChromeStatus $main
        if ($status -eq [IntPtr]::Zero) { throw 'chrome status label not found' }

        # The status strip must be read as "it changed", so where it started matters.
        # Park the pointer far from everything first: a stray hover does not write the
        # strip, but it does make the Send click below a hover-then-click pair.
        [void][BB]::SetCursorPos(([int]($plus.Left + 14)), ([int]($plus.Top + 14)))
        Start-Sleep -Milliseconds 300
        $pre = Get-WinText $status
        Write-Output ('  status before the click = ''' + $pre + '''')

        $got = [BB]::Type($edit, $TYPED)
        Start-Sleep -Milliseconds 300
        Invoke-MouseClick ([int](($ibtns.Send.Left + 14))) ([int]($ibtns.Send.Top + 14))
        Start-Sleep -Milliseconds 900

        $after = Get-WinText $status
        Write-Output ('  status after  the click = ''' + $after + '''')

        Check ($after -ne $pre) 'the top strip said something at all' ('was ''' + $pre + '''')
        Check ($after.StartsWith($NEED_URL)) 'and it is the missing-endpoint message' $after

        $n = Count-Bubbles (Get-ChatView $main)
        Check ($n -eq 0) 'no bubble was added' ($n.ToString() + ' bubble(s)')

        $files = Get-JsonFiles
        Check (@($files).Count -eq 0) 'nothing was written to chats\' ((@($files).Count).ToString() + ' file(s)')

        # The typed text has to survive: the guard sits BEFORE the input is flushed, and
        # eating the user's paragraph would be worse than the silence this replaces.
        $kept = Get-WinText $edit
        Check ($kept -eq $TYPED) 'the typed text is still in the input box' ('''' + $kept + '''')

        Save-WindowShot $main (Get-ShotPath 'send-gate-A-refused.png')
    }

    # ================= B. control group: configured -> the same click sends ==========
    Reset-Chats
    Set-Content -Path $script:settings -Value $SETTINGS_OK -Encoding utf8

    Write-Output ''
    Write-Output '--- B. control group: with a provider filled in, the same click sends ---'
    Invoke-BBProbe {
        $main = Start-BangGang 5
        $pill = Get-SearchPill $main
        if ($null -eq $pill) { throw 'search pill not found' }
        $pbtns = Get-PillButtons $main $pill
        if ($null -eq $pbtns) { throw 'sidebar buttons not found' }
        $plus = $pbtns[1]
        Invoke-MouseClick ([int](($plus.Left + $plus.Right) / 2)) ([int](($plus.Top + $plus.Bottom) / 2))
        Start-Sleep -Milliseconds 900

        $edit = Get-InputEdit $main
        if ($edit -eq [IntPtr]::Zero) { throw 'the input box never appeared' }
        $ibtns = Get-InputButtons $main $edit
        if ($null -eq $ibtns) { throw 'attach / send buttons not found' }

        [void][BB]::SetCursorPos(([int]($plus.Left + 14)), ([int]($plus.Top + 14)))
        Start-Sleep -Milliseconds 300
        $chat = Get-ChatView $main
        $n0 = Count-Bubbles $chat

        $got = [BB]::Type($edit, $TYPED)
        Start-Sleep -Milliseconds 300
        Invoke-MouseClick ([int](($ibtns.Send.Left + 14))) ([int]($ibtns.Send.Top + 14))
        Start-Sleep -Milliseconds 1200

        $n1 = Count-Bubbles (Get-ChatView $main)
        # +1 at least, not exactly +1: the endpoint is a dead port, so a second bubble (the
        # reply's error) shows up right behind the user's. The point here is only that the
        # click went through at all.
        Check ($n1 -ge ($n0 + 1)) 'a bubble appeared' ($n0.ToString() + ' -> ' + $n1.ToString())

        $files = Get-JsonFiles
        Check (@($files).Count -ge 1) 'and the conversation was written to chats\' ((@($files).Count).ToString() + ' file(s)')
        if (@($files).Count -ge 1) {
            Write-Output ('  file: ' + $files[0].Name)
            # The file is the real evidence that this is the message and not just some
            # bubble: the text typed above has to be inside it.
            $body = Get-Content $files[0].FullName -Raw
            Check ($body -like ('*' + $TYPED + '*')) 'and the typed text is in it' $files[0].Name
        }

        $left = Get-WinText $edit
        Check ($left -ne $TYPED) 'the input box was cleared by the send' ('''' + $left + '''')

        Save-WindowShot $main (Get-ShotPath 'send-gate-B-sent.png')
    }
} finally {
    if ($hadSettings) { Set-Content -Path $script:settings -Value $bakSettings -Encoding utf8 -NoNewline }
    else { Remove-Item $script:settings -ErrorAction SilentlyContinue }
    if ($hadLegacy) { Set-Content -Path $script:legacy -Value $bakLegacy -Encoding utf8 -NoNewline }
    else { Remove-Item $script:legacy -ErrorAction SilentlyContinue }
    if (Test-Path $script:chatsDir) {
        foreach ($f in Get-ChildItem $script:chatsDir -File) {
            if (-not $bakChats.ContainsKey($f.Name)) { Remove-Item $f.FullName -Force }
        }
    }
    foreach ($n in $bakChats.Keys) {
        if (-not (Test-Path $script:chatsDir)) { [void](New-Item -ItemType Directory -Force $script:chatsDir) }
        Set-Content -Path (Join-Path $script:chatsDir $n) -Value $bakChats[$n] -Encoding utf8 -NoNewline
    }
}

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'ALL CHECKS PASSED' } else { Write-Output ('' + $script:fail + ' CHECK(S) FAILED') }

Write-BBDone 'send-gate'
