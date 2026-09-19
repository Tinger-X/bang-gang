# Real end-to-end live dictation (0.9.8+): the fake-server probe (record-stt.ps1) pins the
# wire format, but only the vendor's own service can say whether the protocol, the
# credentials and the audio path actually work together. This one talks to the REAL
# Volcengine bidirectional endpoint with the real API key and plays a human recording,
# then compares what landed in the input box against the reference transcript.
#
#   key   : <repo>/.local/stt/volcengine.txt   ("api key: <key>")
#   audio : <repo>/.local/stt/voice-demo.m4a   (played out the default device, so the
#           system-loopback capture picks it up exactly like a real meeting would)
#   truth : <repo>/.local/stt/voice-gt.txt
#
# Everything else stays local: settings.json is rewritten for the leg and restored in
# finally. The similarity floor is deliberately loose (0.6) -- this is a smoke test for
# "the chain works end to end", not a character-accuracy benchmark.
#
# Usage:  powershell -File tools\record-live.ps1
# This file must stay pure ASCII (PS 5.1 reads it as ANSI otherwise).

param([string]$ResourceId = 'volc.seedasr.sauc.duration', [int]$StopDelayMs = 2000,
      [string]$Tag = 'live')

. "$PSScriptRoot\_ui.ps1"

$script:repo = Split-Path $PSScriptRoot -Parent
$script:dir = Split-Path $script:BBExe -Parent
$script:settings = Join-Path $script:dir 'settings.json'
$script:keyFile = Join-Path $script:repo '.local\stt\volcengine.txt'
$script:audio = Join-Path $script:repo '.local\stt\voice-demo.m4a'
$script:truthFile = Join-Path $script:repo '.local\stt\voice-gt.txt'

$script:fail = 0
function Check([bool]$ok, [string]$label, [string]$detail) {
    $tag = 'FAIL'
    if ($ok) { $tag = 'ok  ' }
    if (-not $ok) { $script:fail++ }
    Write-Output ('  [' + $tag + '] ' + $label + '  ' + $detail)
}

function U { param([int[]]$Codes) return (-join ($Codes | ForEach-Object { [char]$_ })) }

# Core Audio, so the leg can make the playback audible and take the room mic out of the
# picture -- peak levels measured on this box: played speech reached 0.05 of full scale
# while the idle mic sat at 0.16, i.e. the transcription was being fed mostly room noise.
# Both are restored in finally. (Nothing here talks to the vendor; it is the test rig.)
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {
    int NotImpl1();
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice {
    int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                 [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume {
    // vtable order matters: every slot must be declared, in order, or the calls land on
    // the wrong method (a mis-ordered GetMute throws "value not in the expected range").
    int RegisterControlChangeNotify(IntPtr p);
    int UnregisterControlChangeNotify(IntPtr p);
    int GetChannelCount(out int c);
    int SetMasterVolumeLevel(float f, ref Guid g);
    int SetMasterVolumeLevelScalar(float f, ref Guid g);
    int GetMasterVolumeLevel(out float f);
    int GetMasterVolumeLevelScalar(out float f);
    int SetChannelVolumeLevel(int ch, float f, ref Guid g);
    int SetChannelVolumeLevelScalar(int ch, float f, ref Guid g);
    int GetChannelVolumeLevel(int ch, out float f);
    int GetChannelVolumeLevelScalar(int ch, out float f);
    int SetMute(bool b, ref Guid g);
    int GetMute(out bool b);
}

public static class AudioEndpoints {
    private static IAudioEndpointVolume Vol(int flow) {
        var en = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
        IMMDevice dev;
        Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(flow, 1, out dev));
        Guid iid = typeof(IAudioEndpointVolume).GUID;
        object o;
        Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out o));
        return (IAudioEndpointVolume)o;
    }
    public static float GetVolume(int flow) { float v; Vol(flow).GetMasterVolumeLevelScalar(out v); return v; }
    public static void SetVolume(int flow, float v) { Guid g = Guid.Empty; Vol(flow).SetMasterVolumeLevelScalar(v, ref g); }
    public static bool GetMute(int flow) { bool m; Vol(flow).GetMute(out m); return m; }
    public static void SetMute(int flow, bool m) { Guid g = Guid.Empty; Vol(flow).SetMute(m, ref g); }
}
'@

$RENDER = 0
$volBefore = $null
try {
    $volBefore = [AudioEndpoints]::GetVolume($RENDER)
    Write-Output ('render volume before = ' + [Math]::Round($volBefore, 2) + ' -> 0.75 for the leg')
    [AudioEndpoints]::SetVolume($RENDER, 0.75)
} catch {
    Write-Output ('audio setup failed (volume left untouched): ' + $_.Exception.Message)
}

$VOLC  = U 0x706B,0x5C71,0x5F15,0x64CE,0xFF08,0x6D41,0x5F0F,0xFF09   # volc preset name
$DOT   = U 0x25CF
$TICK  = U 0x2713

# ---- the API key lives in .local/ (gitignored); the script never hardcodes it ----
if (-not (Test-Path $script:keyFile)) { throw ('missing ' + $script:keyFile) }
$raw = (Get-Content $script:keyFile -Raw).Trim()
$apiKey = $raw
$ci = $raw.IndexOf(':')
if ($ci -ge 0) { $apiKey = $raw.Substring($ci + 1).Trim() }
if ($apiKey.Length -lt 8) { throw 'no api key in .local/stt/volcengine.txt' }
if (-not (Test-Path $script:audio)) { throw ('missing ' + $script:audio) }
if (-not (Test-Path $script:truthFile)) { throw ('missing ' + $script:truthFile) }
# Read as UTF-8 explicitly: PowerShell 5.1's Get-Content defaults to the ANSI codepage,
# which turns a BOM-less UTF-8 truth file into mojibake and makes the comparison nonsense.
$truth = ([System.IO.File]::ReadAllText($script:truthFile, [System.Text.Encoding]::UTF8) -replace '\s', '')
Write-Output ('key length = ' + $apiKey.Length + ', reference chars = ' + $truth.Length)

# ---- settings for this leg (restored in finally) ----
$hadSettings = Test-Path $script:settings
$bakSettings = $null
if ($hadSettings) { $bakSettings = Get-Content $script:settings -Raw }

$o = @{}
$o['ThemeMode'] = 'light'
$o['RecordMode'] = 'toggle'
$o['Shortcuts'] = @(@{ Action = 'record'; Ctrl = $true; Alt = $false; Shift = $false; Vk = 0x52 })
$o['SttProvider'] = $VOLC
$o['SttProfiles'] = @{ $VOLC = @{
    url = 'wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async'
    key = $apiKey
    model = $ResourceId
} }
($o | ConvertTo-Json -Depth 8) | Set-Content $script:settings -Encoding utf8
Write-Output ('resource id = ' + $ResourceId)

function Get-InputEditBig($main) {
    foreach ($h in Get-WinKids $main) {
        if ((Get-ShortClass $h) -ne 'Edit') { continue }
        $r = Get-WinRect $h
        if (($r.Bottom - $r.Top) -gt 40) { return $h }
    }
    return [IntPtr]::Zero
}

# Longest-common-subsequence length: how much of $b the string $a actually contains,
# insensitive to insertions/deletions (ASR punctuation and ITN differ from the truth file).
function Get-LcsLen([string]$a, [string]$b) {
    if ($a.Length -eq 0 -or $b.Length -eq 0) { return 0 }
    $prev = New-Object int[] ($b.Length + 1)
    $cur = New-Object int[] ($b.Length + 1)
    for ($i = 1; $i -le $a.Length; $i++) {
        for ($j = 1; $j -le $b.Length; $j++) {
            if ($a[$i - 1] -eq $b[$j - 1]) { $cur[$j] = $prev[$j - 1] + 1 }
            else { $cur[$j] = [Math]::Max($prev[$j], $cur[$j - 1]) }
        }
        $tmp = $prev; $prev = $cur; $cur = $tmp
        for ($j = 0; $j -le $b.Length; $j++) { $cur[$j] = 0 }
    }
    return $prev[$b.Length]
}

function Get-MatchRatio([string]$a, [string]$b) {
    if ($a.Length -eq 0 -or $b.Length -eq 0) { return 0.0 }
    return (2.0 * (Get-LcsLen $a $b)) / ($a.Length + $b.Length)
}

# How much of the reference's TAIL made it into the box -- the whole point of this leg.
function Get-ContainRatio([string]$text, [string]$needle) {
    if ($needle.Length -eq 0) { return 0.0 }
    return (1.0 * (Get-LcsLen $text $needle)) / $needle.Length
}

try {
    Invoke-BBProbe {
        $main = Start-BangGang 5
        $status = Get-ChromeStatus $main
        if ($status -eq [IntPtr]::Zero) { throw 'chrome status label not found' }

        # Warm-up can swallow the first chord; resend until the status reacts.
        $baseline = Get-WinText $status
        $s = $baseline
        $reacted = $false
        for ($attempt = 0; $attempt -lt 4 -and -not $reacted; $attempt++) {
            [BB]::Chord(0x52)
            $sw = [Diagnostics.Stopwatch]::StartNew()
            while ($sw.Elapsed.TotalSeconds -lt 10) {
                $s = Get-WinText $status
                if ($s -ne $baseline) { $reacted = $true; break }
                Start-Sleep -Milliseconds 200
            }
        }
        Write-Output ('  status = ''' + $s + '''')
        $started = $reacted -and $s.Contains($DOT)
        Check $started 'recording started against the live endpoint' ('status = ''' + $s + '''')
        if (-not $started) { throw 'recording did not start: ' + $s }

        $edit = Get-InputEditBig $main
        Check ($edit -ne [IntPtr]::Zero) 'a conversation was created on the spot' ''

        # Play the reference recording out the default device: the app hears it through
        # its loopback capture, exactly like a real call.
        Add-Type -AssemblyName PresentationCore
        $player = New-Object System.Windows.Media.MediaPlayer
        $player.Open((New-Object System.Uri($script:audio)))
        $player.Play()
        $t0 = [Diagnostics.Stopwatch]::StartNew()
        while (-not $player.NaturalDuration.HasTimeSpan -and $t0.Elapsed.TotalSeconds -lt 10) {
            Start-Sleep -Milliseconds 100
        }
        $dur = 0.0
        if ($player.NaturalDuration.HasTimeSpan) { $dur = $player.NaturalDuration.TimeSpan.TotalSeconds }
        Write-Output ('  audio duration = ' + [Math]::Round($dur, 1) + 's')
        $t1 = [Diagnostics.Stopwatch]::StartNew()
        while ($t1.Elapsed.TotalSeconds -lt $dur + 3.0) {
            if ($player.Position.TotalSeconds -ge $dur -and $dur -gt 0) { break }
            if ($t1.Elapsed.TotalSeconds -gt 120) { break }
            Start-Sleep -Milliseconds 200
        }
        $player.Stop()
        # How long to keep recording after the audio ends. 0 = the "user hits stop the
        # instant the speech stops" case, which is where an unfinished tail sentence
        # would go missing if the stop path cut the vendor off too early.
        if ($StopDelayMs -gt 0) { Start-Sleep -Milliseconds $StopDelayMs }

        # Interim text should already be on screen while still recording.
        $mid = Get-WinText $edit
        Check ($mid.Length -gt 0) 'text streamed in while still recording' ('box = ''' + $mid + '''')

        [BB]::Chord(0x52)   # toggle off

        $lastText = ''
        $lastStatus = ''
        $sw3 = [Diagnostics.Stopwatch]::StartNew()
        while ($sw3.Elapsed.TotalSeconds -lt 20) {
            $lastText = Get-WinText $edit
            $lastStatus = Get-WinText $status
            if ($lastStatus.StartsWith($TICK)) { break }
            Start-Sleep -Milliseconds 200
        }
        Check $lastStatus.StartsWith($TICK) 'status reports completion' ('status = ''' + $lastStatus + '''')

        $script:heard = ($lastText -replace '\s', '')
        $script:ref = $script:truth
        $script:ratio = Get-MatchRatio $script:heard $script:ref
        # The reference trails off with "..." (the file is a truncated transcript), so the
        # needle is the last few real characters -- short and distinctive on purpose: a long
        # needle is scored down by any homophone the model picks (ASR writes 或者说 for
        # 或者是), which would make this flaky instead of discriminating. The tail sentence
        # only ever arrives in the vendor's terminal response, so a stop path that cuts the
        # stream early loses exactly these characters and nothing else in the file.
        $speech = ($script:ref -replace '[.]+$', '')
        $tailLen = [Math]::Min(3, $speech.Length)
        $script:tailRef = $speech.Substring($speech.Length - $tailLen)
        $script:tailRatio = Get-ContainRatio $script:heard $script:tailRef
        Check ($script:heard.Length -gt 0) 'the live service returned text' ('chars = ' + $script:heard.Length)
        Check ($script:ratio -ge 0.6) 'the transcript matches the reference recording' ('similarity = ' + [Math]::Round($script:ratio, 3))
        Check ($script:tailRatio -ge 0.9) 'the tail sentence survived the stop' ('tail = ''' + $script:tailRef + ''' contained ' + [Math]::Round($script:tailRatio, 2))
        Save-WindowShot $main (Get-ShotPath ('record-live-' + $Tag + '.png'))
    }
} finally {
    try { if ($player) { $player.Close() } } catch {}
    if ($volBefore -ne $null) { try { [AudioEndpoints]::SetVolume($RENDER, $volBefore) } catch {} }
    if ($hadSettings) { Set-Content -Path $script:settings -Value $bakSettings -Encoding utf8 -NoNewline }
    else { Remove-Item $script:settings -ErrorAction SilentlyContinue }
}

Write-Output ''
Write-Output ('--- heard (' + $script:heard.Length + ' chars) ---')
Write-Output $script:heard
Write-Output ('--- reference (' + $script:ref.Length + ' chars) ---')
Write-Output $script:ref
# The console codepage mangles CJK in the redirected log, so the two texts (and the
# score) also go to a UTF-8 side file -- that one is the artifact worth reading.
$report = 'tag = ' + $Tag + ', stop delay = ' + $StopDelayMs + "ms`r`n" +
          'similarity = ' + [Math]::Round($script:ratio, 3) +
          ', tail containment = ' + [Math]::Round($script:tailRatio, 2) + "`r`n" +
          'resource id = ' + $ResourceId + "`r`n`r`n" +
          '--- heard (' + $script:heard.Length + ' chars) ---' + "`r`n" + $script:heard + "`r`n`r`n" +
          '--- reference (' + $script:ref.Length + ' chars) ---' + "`r`n" + $script:ref + "`r`n"
[System.IO.File]::WriteAllText((Get-ShotPath ('record-live-report-' + $Tag + '.txt')), $report, (New-Object Text.UTF8Encoding($false)))
$tracePath = Join-Path $script:dir 'ui-trace.log'
$script:traceTail = @()
if (Test-Path $tracePath) {
    $script:traceTail = @(Get-Content $tracePath | Where-Object { $_ -match 'record|dictation|stt-' } | Select-Object -Last 40)
    foreach ($ln in @($script:traceTail | Select-Object -Last 10)) { Write-Output ('  trace| ' + $ln) }
    # Deterministic end-of-stream check: the vendor answers the last packet with a normal
    # response (type 0x9, negative sequence). If the packet is malformed it answers with an
    # error frame instead, and whatever the tail sentence was dies with it. The live audio
    # may or may not leave words pending at stop time (so the box assertion above can be
    # lucky), but this one is always either there or not.
    $script:terminalOk = $false
    $script:sawError = $false
    foreach ($ln in $script:traceTail) {
        if ($ln -match 'stt-finish: terminal=True') { $script:terminalOk = $true }
        if ($ln -match 'stt-error') { $script:sawError = $true }
        if ($ln -match 'type=0x9 .*TERMINAL') { $script:sawError = $false }
    }
}
Check ($script:terminalOk -and -not $script:sawError) 'the end-of-stream handshake was accepted by the vendor' ('terminal=' + $script:terminalOk + ' error=' + $script:sawError)

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'ALL CHECKS PASSED' } else { Write-Output ('' + $script:fail + ' CHECK(S) FAILED') }

Write-BBDone 'record-live'
