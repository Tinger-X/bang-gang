# Record gating (0.9.8): recording now exists to be transcribed. With no STT interface
# configured, pressing the record hotkey must NOT record -- the chrome status strip must
# say what is missing instead. The old behaviour (silently writing a .wav file) is the
# symptom being fixed, so phase A is all about what does NOT happen, and phase B is the
# control group: a fully-filled profile pointing at a dead port must get PAST the gate
# and fail at connect time instead (a different status message).
#
# [BB]::Chord only sends Ctrl+<key>, so the record shortcut is rebound to Ctrl+R in the
# Debug settings.json, and RecordMode is set to "toggle" (hold mode polls GetAsyncKeyState
# and the chord's key is already up by the time WM_HOTKEY arrives). Both legs restore the
# user's settings.json in finally, the same way hotkey-in-settings.ps1 does.
#
# Chinese literals are spelled as code points: this file must stay pure ASCII.
#
# Usage:  powershell -File tools\record-gate.ps1

. "$PSScriptRoot\_ui.ps1"

$script:dir = Split-Path $script:BBExe -Parent
$script:settings = Join-Path $script:dir 'settings.json'

$script:fail = 0
function Check([bool]$ok, [string]$label, [string]$detail) {
    $tag = 'FAIL'
    if ($ok) { $tag = 'ok  ' }
    if (-not $ok) { $script:fail++ }
    Write-Output ('  [' + $tag + '] ' + $label + '  ' + $detail)
}

function U { param([int[]]$Codes) return (-join ($Codes | ForEach-Object { [char]$_ })) }

# "not yet filled in" -- the head of every SttConfig.Problem sentence.
$NEED = U 0x5C1A,0x672A,0x586B,0x5199
# the cross mark that starts "could not start transcribing"
$CROSS = U 0x2717
# the volcengine preset name (kept for the profile key below)
$VOLC = U 0x706B,0x5C71,0x5F15,0x64CE,0xFF08,0x6D41,0x5F0F,0xFF09

$hadSettings = Test-Path $script:settings
$bakSettings = $null
if ($hadSettings) { $bakSettings = Get-Content $script:settings -Raw }

# The conversation's multiline EDIT. Same rule as send-gate.ps1: the sidebar search box
# is an EDIT too, so only a TALL one counts, and the welcome page has none at all.
function Get-InputEditBig2($main) {
    foreach ($h in Get-WinKids $main) {
        if ((Get-ShortClass $h) -ne 'Edit') { continue }
        $r = Get-WinRect $h
        if (($r.Bottom - $r.Top) -gt 40) { return $h }
    }
    return [IntPtr]::Zero
}

function Write-GateSettings([bool]$configured) {
    $o = @{}
    $o['ThemeMode'] = 'light'
    $o['RecordMode'] = 'toggle'
    $o['Shortcuts'] = @(@{ Action = 'record'; Ctrl = $true; Alt = $false; Shift = $false; Vk = 0x52 })
    $o['SttProvider'] = $VOLC
    if ($configured) {
        # A dead port: the gate must pass, the CONNECT must fail. Discard (port 9)
        # refuses instantly on loopback, so this leg stays fast.
        $o['SttProfiles'] = @{ $VOLC = @{
            url = 'ws://127.0.0.1:9/api/v3/sauc/bigmodel'
            appid = 'probe-app'
            key = 'probe-token'
            model = 'probe-resource'
        } }
    }
    ($o | ConvertTo-Json -Depth 8) | Set-Content $script:settings -Encoding utf8
}

try {
    # ============ A. nothing configured -> refused at the gate ============
    Write-GateSettings $false
    Write-Output '--- A. no STT profile: the hotkey must not record ---'
    Invoke-BBProbe {
        $main = Start-BangGang 5
        $status = Get-ChromeStatus $main
        if ($status -eq [IntPtr]::Zero) { throw 'chrome status label not found' }

        $pre = Get-WinText $status
        [BB]::Chord(0x52)
        Start-Sleep -Milliseconds 1200

        $after = Get-WinText $status
        Write-Output ('  status after chord = ''' + $after + '''')
        Check ($after -ne $pre) 'the status strip reacted at all' ('was ''' + $pre + '''')
        Check ($after.StartsWith($NEED)) 'and it says the interface is not configured' $after

        Start-Sleep -Milliseconds 400
        $edit = Get-InputEditBig2 $main
        Check ($edit -eq [IntPtr]::Zero) 'no conversation was created (still on the welcome page)' ''

        # And the record hotkey must stay inert on a second press too (toggle mode would
        # otherwise "stop" a recording that never started).
        [BB]::Chord(0x52)
        Start-Sleep -Milliseconds 900
        $edit = Get-InputEditBig2 $main
        Check ($edit -eq [IntPtr]::Zero) 'second press is equally inert' ''
        Save-WindowShot $main (Get-ShotPath 'record-gate-A-refused.png')
    }

    # ============ B. control group: configured -> passes the gate ============
    Write-GateSettings $true
    Write-Output ''
    Write-Output '--- B. control group: a filled profile passes the gate ---'
    Invoke-BBProbe {
        $main = Start-BangGang 5
        $status = Get-ChromeStatus $main
        if ($status -eq [IntPtr]::Zero) { throw 'chrome status label not found' }

        $pre = Get-WinText $status
        [BB]::Chord(0x52)
        Start-Sleep -Milliseconds 2500

        $after = Get-WinText $status
        Write-Output ('  status after chord = ''' + $after + '''')
        Check ($after -ne $pre) 'the status strip reacted' ('was ''' + $pre + '''')
        # The gate let it through, the connection to the dead port failed. A broken gate
        # (or a broken hotkey) would instead show the A-phase "not configured" message.
        Check ($after.StartsWith($CROSS)) 'and it got as far as the connect attempt' $after
        Save-WindowShot $main (Get-ShotPath 'record-gate-B-connect.png')
    }
} finally {
    if ($hadSettings) { Set-Content -Path $script:settings -Value $bakSettings -Encoding utf8 -NoNewline }
    else { Remove-Item $script:settings -ErrorAction SilentlyContinue }
}

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'ALL CHECKS PASSED' } else { Write-Output ('' + $script:fail + ' CHECK(S) FAILED') }

Write-BBDone 'record-gate'
