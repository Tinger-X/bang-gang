# End-to-end live dictation (0.9.8): recording now transcribes through the vendor's
# streaming WebSocket and the text lands in the input box. There are no real credentials
# on this machine, so the "vendor" is a fake WebSocket server in a Start-Job (same move as
# llm-reply.ps1's fake SSE, one protocol layer lower). Two legs, one per wire format:
#
#   xfyun: URL-signed handshake (signa = base64(HMAC-SHA1(APISecret, md5hex(appid+ts)))),
#          audio as JSON text frames (status 0/1/2, base64 PCM, format
#          "audio/L16;rate=16000"), results as JSON: cn.st.rt[].ws[].cw[].w, type "1" =
#          interim / "0" = final, ls=true = last.
#   volc:  new-console auth headers (X-Api-Key / X-Api-Resource-Id / X-Api-Connect-Id,
#          no App ID or Access Token), then bidirectional-streaming binary frames
#          [0x11, type<<4|flags, ser<<4|comp, 0, (int32 seq), BE32 size, payload];
#          type 0x1 = full request JSON (must carry "bigmodel"), type 0x2 = gzip'd audio.
#          The server acks the full request FIRST (that is what the bidirectional flow
#          is: one response per packet), then answers audio with type 0x9 gzip'd JSON
#          carrying result.utterances[].definite, and flags its terminal response 0x3.
#          A rejected key comes back as a type 0xF error frame (code + size + UTF-8
#          message), which the client must surface at connect time.
#
# Both legs assert the SAME user-visible story: hotkey -> status shows the recording dot,
# a conversation is created on the spot (we start on the welcome page), the interim
# sentence appears in the input box while still recording, and after the second hotkey
# press the box holds exactly the finalized sentence and the status says done. The server
# log pins the wire format (credentials arrived, audio frames flowed, full request sane),
# so a leg cannot pass by the app merely "not crashing".
#
# The fake server sends its interim result 1.5s before the final one, so the probe's
# 100ms poll has a wide window to catch the interim text -- no fixed-sleep guessing on
# the probe side (the wait is the server's, and the server knows exactly what it sent).
#
# [BB]::Chord only sends Ctrl+<key>: record is rebound to Ctrl+R and RecordMode is
# "toggle". settings.json is restored in finally. This file must stay pure ASCII.
#
# Usage:  powershell -File tools\record-stt.ps1

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

$DOT   = U 0x25CF          # the recording dot in the chrome status
$TICK  = U 0x2713          # "transcription done"
$CROSS = U 0x2717          # "could not start"
$VOLC  = U 0x706B,0x5C71,0x5F15,0x64CE,0xFF08,0x6D41,0x5F0F,0xFF09
$XFY   = U 0x8BAF,0x98DE,0xFF08,0x5B9E,0x65F6,0x8F6C,0x5199,0xFF09
$VOLCERR = U 0x706B,0x5C71,0x5F15,0x64CE,0x8FD4,0x56DE,0x9519,0x8BEF   # 'volc returned an error'

# ---------------------------------------------------------------------------
# The fake vendor server. Runs in a Start-Job: everything it needs arrives via
# param(), everything it has to say is written to $LogPath at the end.
# ---------------------------------------------------------------------------
$server = {
    param([int]$Port, [string]$Flavor, [string]$LogPath, [string]$Interim, [string]$Final)

    # Log lines go straight to the file: Stop-Job kills the job WITHOUT running finally,
    # so a "collect lines, write at the end" design loses everything exactly when the
    # probe needs it most (the hang cases). Timestamps tell real-time flow from a
    # stop-time burst -- a frame flood processed in milliseconds looks exactly like
    # "frames flowed" without them.
    function L([string]$s) { Add-Content -Path $LogPath -Value ((Get-Date -Format 'HH:mm:ss.fff') + ' ' + $s) -Encoding ascii }

    $listener = $null
    $script:stream = $null
    $script:pending = New-Object byte[] 0
    $script:volcSeq = 0

    function Read-Exact([int]$n) {
        $out = New-Object byte[] $n
        $off = 0
        if ($script:pending.Length -gt 0) {
            $take = [Math]::Min($n, $script:pending.Length)
            [Array]::Copy($script:pending, 0, $out, 0, $take)
            $rem = $script:pending.Length - $take
            if ($rem -gt 0) {
                $np = New-Object byte[] $rem
                [Array]::Copy($script:pending, $take, $np, 0, $rem)
                $script:pending = $np
            } else { $script:pending = New-Object byte[] 0 }
            $off = $take
        }
        while ($off -lt $n) {
            $r = $script:stream.Read($out, $off, $n - $off)
            if ($r -le 0) { throw 'eof' }
            $off += $r
        }
        return ,$out
    }

    # One client->server frame (RFC 6455; ClientWebSocket always masks).
    # Result goes out through $script:frameOp / $script:framePayload.
    function Read-Frame {
        $h = Read-Exact 2
        $script:frameOp = $h[0] -band 0x0F
        $masked = ($h[1] -band 0x80) -ne 0
        [long]$len = $h[1] -band 0x7F
        if ($len -eq 126) { $e = Read-Exact 2; $len = ([long]$e[0] -shl 8) -bor $e[1] }
        elseif ($len -eq 127) {
            $e = Read-Exact 8
            $len = 0
            foreach ($x in $e) { $len = ($len -shl 8) -bor $x }
        }
        $mask = New-Object byte[] 0
        if ($masked) { $mask = Read-Exact 4 }
        $pl = New-Object byte[] 0
        if ($len -gt 0) { $pl = Read-Exact ([int]$len) }
        if ($masked) { for ($i = 0; $i -lt $pl.Length; $i++) { $pl[$i] = $pl[$i] -bxor $mask[$i -band 3] } }
        $script:framePayload = $pl
    }

    # One server->client frame (never masked). Payloads here are small.
    function Send-Frame([byte]$op, [byte[]]$payload) {
        $ms = New-Object IO.MemoryStream
        $ms.WriteByte([byte](0x80 -bor $op))
        if ($payload.Length -lt 126) {
            $ms.WriteByte([byte]$payload.Length)
        } else {
            $ms.WriteByte(126)
            $ms.WriteByte([byte]($payload.Length -shr 8))
            $ms.WriteByte([byte]($payload.Length -band 0xFF))
        }
        $ms.Write($payload, 0, $payload.Length)
        $b = $ms.ToArray()
        $script:stream.Write($b, 0, $b.Length)
    }

    function Send-Text([string]$json) { Send-Frame 0x1 ([Text.Encoding]::UTF8.GetBytes($json)) }

    function Gzip-B([byte[]]$data) {
        $ms = New-Object IO.MemoryStream
        $gz = New-Object IO.Compression.GZipStream($ms, [IO.Compression.CompressionMode]::Compress)
        $gz.Write($data, 0, $data.Length)
        $gz.Close()
        return ,$ms.ToArray()
    }
    function Gunzip-B([byte[]]$data) {
        $src = New-Object IO.MemoryStream(,$data)
        $gz = New-Object IO.Compression.GZipStream($src, [IO.Compression.CompressionMode]::Decompress)
        $ms = New-Object IO.MemoryStream
        $gz.CopyTo($ms)
        $gz.Close()
        return ,$ms.ToArray()
    }

    # A volc server->client result frame: type 0x9, JSON + gzip. Full server responses
    # put an int32 sequence between the header and the payload size (flags 0x1 = positive
    # sequence), which is exactly what the client's parser skips. Still a WebSocket
    # message -- writing the raw volc frame straight to the stream makes the client
    # parse "0x11 0x90 ..." as a WS header and the connection dies on the spot.
    function Send-VolcFrame([byte]$flags, [int]$seq, [byte[]]$pay) {
        $ms = New-Object IO.MemoryStream
        $ms.WriteByte(0x11)
        $ms.WriteByte([byte](0x90 -bor $flags))   # type 0x9 | flags
        $ms.WriteByte(0x11)                       # JSON + gzip
        $ms.WriteByte(0)
        $ms.WriteByte([byte](($seq -shr 24) -band 0xFF))
        $ms.WriteByte([byte](($seq -shr 16) -band 0xFF))
        $ms.WriteByte([byte](($seq -shr 8) -band 0xFF))
        $ms.WriteByte([byte]($seq -band 0xFF))
        $ms.WriteByte([byte](($pay.Length -shr 24) -band 0xFF))
        $ms.WriteByte([byte](($pay.Length -shr 16) -band 0xFF))
        $ms.WriteByte([byte](($pay.Length -shr 8) -band 0xFF))
        $ms.WriteByte([byte]($pay.Length -band 0xFF))
        $ms.Write($pay, 0, $pay.Length)
        Send-Frame 0x2 ($ms.ToArray())
    }

    function Send-VolcResult([string]$json) {
        $script:volcSeq++
        Send-VolcFrame 0x1 $script:volcSeq (Gzip-B ([Text.Encoding]::UTF8.GetBytes($json)))
    }

    # The volc last-packet marker: type 0x9, flags 0x3 (last packet with a negative
    # sequence), empty payload. This is how the terminal response is flagged.
    function Send-VolcLast {
        $script:volcSeq++
        Send-VolcFrame 0x3 (0 - $script:volcSeq) (New-Object byte[] 0)
    }

    # Error frame: header + code(uint32) + message size(uint32) + UTF-8 message,
    # no JSON and no gzip. A bad API key comes back this way right after the full
    # client request, which is what lets the client fail fast at connect time.
    function Send-VolcError([int]$code, [string]$msg) {
        $mb = [Text.Encoding]::UTF8.GetBytes($msg)
        $ms = New-Object IO.MemoryStream
        $ms.WriteByte(0x11)
        $ms.WriteByte(0xF0)          # type 0xF, no flags
        $ms.WriteByte(0x10)          # JSON serialization, uncompressed
        $ms.WriteByte(0)
        $ms.WriteByte([byte](($code -shr 24) -band 0xFF))
        $ms.WriteByte([byte](($code -shr 16) -band 0xFF))
        $ms.WriteByte([byte](($code -shr 8) -band 0xFF))
        $ms.WriteByte([byte]($code -band 0xFF))
        $ms.WriteByte([byte](($mb.Length -shr 24) -band 0xFF))
        $ms.WriteByte([byte](($mb.Length -shr 16) -band 0xFF))
        $ms.WriteByte([byte](($mb.Length -shr 8) -band 0xFF))
        $ms.WriteByte([byte]($mb.Length -band 0xFF))
        $ms.Write($mb, 0, $mb.Length)
        Send-Frame 0x2 ($ms.ToArray())
    }

    try {
        $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $Port)
        $listener.Start()
        L ('listening on ' + $Port)
        $client = $listener.AcceptTcpClient()
        $script:stream = $client.GetStream()
        $script:stream.ReadTimeout = 20000

        # ---- HTTP handshake (the client may already have pipelined a frame) ----
        $buf = New-Object byte[] 4096
        $head = ''
        while ($true) {
            $n = $script:stream.Read($buf, 0, $buf.Length)
            if ($n -le 0) { throw 'eof during handshake' }
            $all = New-Object byte[] ($script:pending.Length + $n)
            [Array]::Copy($script:pending, 0, $all, 0, $script:pending.Length)
            [Array]::Copy($buf, 0, $all, $script:pending.Length, $n)
            $txt = [Text.Encoding]::ASCII.GetString($all)
            $idx = $txt.IndexOf("`r`n`r`n")
            if ($idx -ge 0) {
                $head = $txt.Substring(0, $idx)
                if ($idx + 4 -lt $all.Length) { $script:pending = $all[($idx + 4)..($all.Length - 1)] }
                else { $script:pending = New-Object byte[] 0 }
                break
            }
            $script:pending = $all
        }

        $lines = $head -split "`r`n"
        $reqLine = $lines[0]
        $hdrs = @{}
        for ($i = 1; $i -lt $lines.Length; $i++) {
            $ci = $lines[$i].IndexOf(':')
            if ($ci -gt 0) { $hdrs[$lines[$i].Substring(0, $ci).Trim().ToLower()] = $lines[$i].Substring($ci + 1).Trim() }
        }
        $wsKey = ''
        if ($hdrs.Contains('sec-websocket-key')) { $wsKey = $hdrs['sec-websocket-key'] }
        if ($wsKey.Length -eq 0) { throw 'no Sec-WebSocket-Key' }

        # ---- credential checks per flavor ----
        if ($Flavor -eq 'xfyun') {
            $target = ($reqLine -split ' ')[1]
            $q = @{}
            $qi = $target.IndexOf('?')
            if ($qi -ge 0) {
                foreach ($pair in $target.Substring($qi + 1).Split('&')) {
                    $kv = $pair.Split('=')
                    if ($kv.Length -eq 2) { $q[$kv[0]] = [Uri]::UnescapeDataString($kv[1]) }
                }
            }
            $appid = ''
            if ($q.Contains('appid')) { $appid = $q['appid'] }
            $ts = ''
            if ($q.Contains('ts')) { $ts = $q['ts'] }
            $signa = ''
            if ($q.Contains('signa')) { $signa = $q['signa'] }
            L ('appid-ok=' + ($appid -eq 'probe-app'))
            # Recompute the documented signature and compare.
            $md5 = [Security.Cryptography.MD5]::Create()
            $h = $md5.ComputeHash([Text.Encoding]::UTF8.GetBytes($appid + $ts))
            $hex = New-Object Text.StringBuilder
            foreach ($x in $h) { [void]$hex.Append($x.ToString('x2')) }
            $hmac = New-Object Security.Cryptography.HMACSHA1(,[Text.Encoding]::UTF8.GetBytes('probe-secret'))
            $expect = [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($hex.ToString())))
            L ('signa-ok=' + ($signa -eq $expect))
        } else {
            # New-console auth: an API Key plus a resource id, no App ID / Access Token.
            $tok = ''
            if ($hdrs.Contains('x-api-key')) { $tok = $hdrs['x-api-key'] }
            $res = ''
            if ($hdrs.Contains('x-api-resource-id')) { $res = $hdrs['x-api-resource-id'] }
            $cid = ''
            if ($hdrs.Contains('x-api-connect-id')) { $cid = $hdrs['x-api-connect-id'] }
            L ('apikey-ok=' + ($tok -eq 'probe-key'))
            L ('resource=' + $res)
            L ('connect-id-ok=' + ($cid.Length -gt 10))
            L ('legacy-headers-absent=' + (-not $hdrs.Contains('x-api-access-key') -and -not $hdrs.Contains('x-api-app-key')))
        }

        $accept = [Convert]::ToBase64String(
            [Security.Cryptography.SHA1]::Create().ComputeHash(
                [Text.Encoding]::ASCII.GetBytes($wsKey + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11')))
        $resp = "HTTP/1.1 101 Switching Protocols`r`nUpgrade: websocket`r`nConnection: Upgrade`r`nSec-WebSocket-Accept: $accept`r`n`r`n"
        $rb = [Text.Encoding]::ASCII.GetBytes($resp)
        $script:stream.Write($rb, 0, $rb.Length)
        L 'handshake done'

        if ($Flavor -eq 'xfyun') {
            Send-Text '{"action":"started","code":"0","desc":"success","sid":"probe"}'
        }

        # Result payloads. Interim goes out at the 2nd audio frame, the final one
        # 1.5s later, so the probe can see both.
        if ($Flavor -eq 'xfyun') {
            $jsonI = '{"action":"result","code":"0","data":{"ls":false,"cn":{"st":{"type":"1","rt":[{"ws":[{"cw":[{"w":"' + $Interim + '"}]}]}]}}},"desc":"ok","sid":"probe"}'
            $jsonF = '{"action":"result","code":"0","data":{"ls":true,"cn":{"st":{"type":"0","rt":[{"ws":[{"cw":[{"w":"' + $Final + '"}]}]}]}}},"desc":"ok","sid":"probe"}'
        } else {
            $jsonI = '{"result":{"text":"' + $Interim + '","utterances":[{"text":"' + $Interim + '","definite":false}]}}'
            $jsonF = '{"result":{"text":"' + $Final + '","utterances":[{"text":"' + $Final + '","definite":true}]}}'
        }

        $audio = 0
        $framesSeen = 0
        $sentInterim = $false
        $sentFinal = $false
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $interimAt = [TimeSpan]::Zero

        while ($true) {
            try { Read-Frame } catch { L ('read-end: ' + $_.Exception.Message); break }
            if ($script:frameOp -eq 0x8) { L 'ws-close'; break }
            $framesSeen++
            if ($framesSeen -le 8 -or ($framesSeen % 50) -eq 0) {
                $m0 = $script:framePayload
                $hexLen = [Math]::Min(12, $m0.Length)
                $hex = ''
                if ($hexLen -gt 0) { $hex = [BitConverter]::ToString($m0[0..($hexLen - 1)]) }
                L ('frame #' + $framesSeen + ' op=' + $script:frameOp + ' len=' + $m0.Length + ' ' + $hex)
            }

            if ($Flavor -ne 'xfyun') {
                if ($script:frameOp -ne 0x2) { continue }
                $m = $script:framePayload
                if ($m.Length -lt 8) { continue }
                $type = $m[1] -shr 4
                $flags = $m[1] -band 0x0F
                $comp = $m[2] -band 0x0F
                $off = ($m[0] -band 0x0F) * 4
                if ($flags -eq 1 -or $flags -eq 3) { $off += 4 }
                if ($off + 4 -gt $m.Length) { continue }
                # PS 5.1 truncates [byte] -shl n back to byte (25 -shl 8 == 0), so
                # every shift here needs an explicit [int] cast -- without it size
                # collapses to just the last byte and every frame parses as empty.
                $size = ([int]$m[$off] -shl 24) -bor ([int]$m[$off + 1] -shl 16) -bor ([int]$m[$off + 2] -shl 8) -bor $m[$off + 3]
                $off += 4
                $pl = New-Object byte[] 0
                if ($size -gt 0 -and $off + $size -le $m.Length) {
                    $pl = New-Object byte[] $size
                    [Array]::Copy($m, $off, $pl, 0, $size)
                    if ($comp -eq 1) { $pl = Gunzip-B $pl }
                }
                if ($type -eq 0x1) {
                    $j = [Text.Encoding]::UTF8.GetString($pl)
                    L ('fullreq-bigmodel=' + $j.Contains('bigmodel'))
                    L ('fullreq-rate=' + $j.Contains('16000'))
                    L ('fullreq-pcm=' + ($j.Contains('"format":"pcm"') -and $j.Contains('"codec":"raw"')))
                    L ('fullreq-nonstream=' + $j.Contains('enable_nonstream'))
                    # Bidirectional streaming acks the full request BEFORE any audio.
                    # The ack has to be a real full server response (with its sequence),
                    # otherwise the client sits out its whole connect-time wait.
                    if ($Flavor -eq 'volcbad') {
                        Send-VolcError 45000001 'invalid api key'
                        L 'sent-error'
                    } else {
                        Send-VolcResult '{"result":{"text":""}}'
                        L 'sent-first-response'
                    }
                } elseif ($type -eq 0x2) {
                    if ($pl.Length -gt 0) { $audio++ } else { L 'client-empty-last' }
                }
            } else {
                if ($script:frameOp -ne 0x1) { continue }
                $o = [Text.Encoding]::UTF8.GetString($script:framePayload) | ConvertFrom-Json
                $st = [int]$o.data.status
                if ($st -eq 0) { L ('fmt-ok=' + ($o.data.format -eq 'audio/L16;rate=16000')) }
                if ($st -le 1) { $audio++ } else { L 'client-status2' }
            }

            if (-not $sentInterim -and $audio -ge 2) {
                if ($Flavor -ne 'xfyun') { Send-VolcResult $jsonI } else { Send-Text $jsonI }
                $sentInterim = $true
                $interimAt = $sw.Elapsed
                L 'sent-interim'
            }
            if ($sentInterim -and -not $sentFinal -and ($sw.Elapsed - $interimAt).TotalMilliseconds -gt 1500) {
                if ($Flavor -ne 'xfyun') { Send-VolcResult $jsonF; Send-VolcLast } else { Send-Text $jsonF }
                $sentFinal = $true
                L 'sent-final'
            }
        }
        L ('audio-frames=' + $audio)
    } catch {
        L ('server-error: ' + $_.Exception.Message)
    } finally {
        try { if ($script:stream -ne $null) { $script:stream.Close() } } catch {}
        try { if ($listener -ne $null) { $listener.Stop() } } catch {}
        L 'server-exit'
    }
}

# ---------------------------------------------------------------------------
# Probe side
# ---------------------------------------------------------------------------
$hadSettings = Test-Path $script:settings
$bakSettings = $null
if ($hadSettings) { $bakSettings = Get-Content $script:settings -Raw }

function Get-InputEditBig2($main) {
    foreach ($h in Get-WinKids $main) {
        if ((Get-ShortClass $h) -ne 'Edit') { continue }
        $r = Get-WinRect $h
        if (($r.Bottom - $r.Top) -gt 40) { return $h }
    }
    return [IntPtr]::Zero
}

function Write-SttSettings([string]$preset, [hashtable]$profile) {
    $o = @{}
    $o['ThemeMode'] = 'light'
    $o['RecordMode'] = 'toggle'
    $o['Shortcuts'] = @(@{ Action = 'record'; Ctrl = $true; Alt = $false; Shift = $false; Vk = 0x52 })
    $o['SttProvider'] = $preset
    $o['SttProfiles'] = @{ $preset = $profile }
    ($o | ConvertTo-Json -Depth 8) | Set-Content $script:settings -Encoding utf8
}

function Get-FreePort {
    $tl = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $tl.Start()
    $p = ($tl.LocalEndpoint).Port
    $tl.Stop()
    return $p
}

function Run-Leg {
    param([string]$Flavor, [string]$Preset, [string]$Path, [hashtable]$Extra, [string]$Interim, [string]$Final,
          [switch]$BadKey)

    Write-Output ('--- leg ' + $Flavor + ' ---')
    $port = Get-FreePort
    $profile = @{ url = ('ws://127.0.0.1:' + $port + $Path); key = 'probe-key' }
    if ($Flavor -eq 'xfyun') { $profile['appid'] = 'probe-app' }
    foreach ($k in $Extra.Keys) { $profile[$k] = $Extra[$k] }
    Write-SttSettings $Preset $profile

    $logPath = Get-ShotPath ('record-stt-server-' + $Flavor + '.log')
    if (Test-Path $logPath) { Remove-Item $logPath -Force }
    $job = Start-Job -ScriptBlock $server -ArgumentList $port, $Flavor, $logPath, $Interim, $Final
    Start-Sleep -Milliseconds 600
    if ($job.State -ne 'Running' -and $job.State -ne 'Blocked') {
        Write-Output ('  server job died at startup: ' + $job.State)
        Write-Output (Receive-Job $job | Out-String)
        Check $false 'server job is listening' $job.State
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        return
    }

    try {
        try {
            Invoke-BBProbe {
                $main = Start-BangGang 5
                $status = Get-ChromeStatus $main
                if ($status -eq [IntPtr]::Zero) { throw 'chrome status label not found' }

                # The chord can land before the app finishes warming up; a lost chord
                # leaves the status strip untouched, so resend until it reacts.
                $baseline = Get-WinText $status
                $s = $baseline
                $reacted = $false
                for ($attempt = 0; $attempt -lt 4 -and -not $reacted; $attempt++) {
                    [BB]::Chord(0x52)
                    $sw = [Diagnostics.Stopwatch]::StartNew()
                    while ($sw.Elapsed.TotalSeconds -lt 12) {
                        $s = Get-WinText $status
                        if ($s -ne $baseline) { $reacted = $true; break }
                        Start-Sleep -Milliseconds 200
                    }
                }
                Write-Output ('  status = ''' + $s + '''')
                if ($BadKey) {
                    # The bidirectional flow acks before any audio, so a rejected key shows
                    # up as an error frame at connect time -- the status strip has to say so
                    # right there, instead of "connected but never a word".
                    $sawErr = $false
                    $lastStatus = $s
                    $swErr = [Diagnostics.Stopwatch]::StartNew()
                    while ($swErr.Elapsed.TotalSeconds -lt 10) {
                        $lastStatus = Get-WinText $status
                        if ($lastStatus.Contains($VOLCERR) -and $lastStatus.Contains('45000001')) { $sawErr = $true; break }
                        Start-Sleep -Milliseconds 150
                    }
                    Check $sawErr 'a rejected API key fails at connect with the vendor message' ('status = ''' + $lastStatus + '''')
                    Check $lastStatus.StartsWith($CROSS) 'the failure carries the could-not-start marker' ('status = ''' + $lastStatus + '''')
                    $edit = Get-InputEditBig2 $main
                    Check ($edit -eq [IntPtr]::Zero) 'no conversation was created for a failed connect' ''
                    Save-WindowShot $main (Get-ShotPath ('record-stt-' + $Flavor + '.png'))
                    return
                }

                $started = $reacted -and $s.Contains($DOT)
                $errMsg = ''
                if ($reacted -and -not $started) { $errMsg = $s }
                Check $started ('recording started (' + $Flavor + ')') $errMsg
                if (-not $started) { throw 'recording did not start: ' + $errMsg }

                $edit = Get-InputEditBig2 $main
                Check ($edit -ne [IntPtr]::Zero) 'a conversation was created on the spot' ''

                # The interim sentence must be visible while still recording.
                $saw = $false
                $lastText = ''
                $sw2 = [Diagnostics.Stopwatch]::StartNew()
                while ($sw2.Elapsed.TotalSeconds -lt 10) {
                    $lastText = Get-WinText $edit
                    if ($lastText.Contains($Interim)) { $saw = $true; break }
                    Start-Sleep -Milliseconds 100
                }
                Check $saw 'interim text streamed into the input box' ('box = ''' + $lastText + '''')

                # The finalized sentence must also land WHILE STILL RECORDING:
                # the server waits 1.5s between interim and final, so stopping the
                # moment the interim appears races that gap and the final never comes.
                $sawFinal = $false
                $sw2b = [Diagnostics.Stopwatch]::StartNew()
                while ($sw2b.Elapsed.TotalSeconds -lt 10) {
                    $lastText = Get-WinText $edit
                    if ($lastText -eq $Final) { $sawFinal = $true; break }
                    Start-Sleep -Milliseconds 100
                }
                Check $sawFinal 'finalized sentence landed while still recording' ('box = ''' + $lastText + '''')

                [BB]::Chord(0x52)   # toggle off

                $okText = $false
                $okStatus = $false
                $lastText = ''
                $lastStatus = ''
                $sw3 = [Diagnostics.Stopwatch]::StartNew()
                while ($sw3.Elapsed.TotalSeconds -lt 12) {
                    $lastText = Get-WinText $edit
                    $lastStatus = Get-WinText $status
                    $okText = ($lastText -eq $Final)
                    $okStatus = $lastStatus.StartsWith($TICK)
                    if ($okText -and $okStatus) { break }
                    Start-Sleep -Milliseconds 150
                }
                Check $okText 'after stop the box holds exactly the finalized sentence' ('box = ''' + $lastText + '''')
                Check $okStatus 'status reports completion' ('status = ''' + $lastStatus + '''')
                Save-WindowShot $main (Get-ShotPath ('record-stt-' + $Flavor + '.png'))
            }
        } catch {
            Check $false ('probe run (' + $Flavor + ')') $_.Exception.Message
        }
    } finally {
        [void](Wait-Job $job -Timeout 10)
        Stop-Job $job -ErrorAction SilentlyContinue
        $jobOut = Receive-Job $job 2>$null
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        if ($jobOut) { Write-Output ('  job-output| ' + ($jobOut | Out-String).Trim()) }
    }

    # The app's own view of what happened (Debug-only Trace -> ui-trace.log).
    $tracePath = Join-Path $script:dir 'ui-trace.log'
    if (Test-Path $tracePath) {
        $tail = @(Get-Content $tracePath | Where-Object { $_ -match 'record|dictation' } | Select-Object -Last 12)
        foreach ($ln in $tail) { Write-Output ('  trace| ' + $ln) }
    }

    # ---- what the fake server saw on the wire ----
    if (Test-Path $logPath) { $lines = @(Get-Content $logPath) } else { $lines = @() }
    foreach ($ln in $lines) { Write-Output ('  server| ' + $ln) }
    # Lines are timestamp-prefixed ('HH:mm:ss.fff marker'), so match by Contains.
    function Has([string]$marker) { foreach ($ln in $lines) { if ($ln.Contains($marker)) { return $true } }; return $false }

    Check (Has 'handshake done') 'server completed the WebSocket handshake' ''
    if ($Flavor -eq 'xfyun') {
        Check (Has 'appid-ok=True') 'appid arrived on the handshake query' ''
        Check (Has 'signa-ok=True') 'signa matches the documented HMAC-SHA1 recipe' ''
        Check (Has 'fmt-ok=True') 'first frame declares audio/L16;rate=16000' ''
    } else {
        Check (Has 'apikey-ok=True') 'X-Api-Key arrived on the handshake' ''
        Check (Has 'connect-id-ok=True') 'X-Api-Connect-Id arrived on the handshake' ''
        Check (Has 'resource=volc.bigasr.sauc.duration') 'X-Api-Resource-Id arrived on the handshake' ''
        Check (Has 'legacy-headers-absent=True') 'no App ID / Access Token headers (new-console auth)' ''
        Check (Has 'fullreq-bigmodel=True') 'full request declares the bigmodel' ''
        Check (Has 'fullreq-rate=True') 'full request declares 16kHz' ''
        Check (Has 'fullreq-pcm=True') 'full request declares pcm / raw audio' ''
        Check (Has 'fullreq-nonstream=True') 'full request asks for the non-stream second pass (definite sentences)' ''
    }
    if ($BadKey) {
        Check (Has 'sent-error') 'server answered the full request with an error frame' ''
    } else {
        if ($Flavor -ne 'xfyun') {
            # The bidirectional handshake ack: one response before any audio.
            Check (Has 'sent-first-response') 'server acked the full request before any audio' ''
        }
        Check (Has 'sent-interim') 'server sent the interim result' ''
        Check (Has 'sent-final') 'server sent the final result' ''
        $frames = 0
        foreach ($ln in $lines) { if ($ln -match 'audio-frames=(\d+)') { $frames = [int]$Matches[1] } }
        Check ($frames -ge 3) 'audio frames actually flowed' ('frames = ' + $frames)
    }
    Write-Output ''
}

try {
    Run-Leg 'xfyun' $XFY '/ast/communicate/v1' @{ secret2 = 'probe-secret' } 'XF-PART' 'XF-FINAL'
    Run-Leg 'volc' $VOLC '/api/v3/sauc/bigmodel_async' @{ model = 'volc.bigasr.sauc.duration' } 'VC-PART' 'VC-FINAL'
    # Control leg: the server answers with an error frame -- the UI must say why,
    # right there, and leave no empty conversation behind.
    Run-Leg 'volcbad' $VOLC '/api/v3/sauc/bigmodel_async' @{ model = 'volc.bigasr.sauc.duration' } '' '' -BadKey
} finally {
    if ($hadSettings) { Set-Content -Path $script:settings -Value $bakSettings -Encoding utf8 -NoNewline }
    else { Remove-Item $script:settings -ErrorAction SilentlyContinue }
}

Write-Output ''
if ($script:fail -eq 0) { Write-Output 'ALL CHECKS PASSED' } else { Write-Output ('' + $script:fail + ' CHECK(S) FAILED') }

Write-BBDone 'record-stt'
