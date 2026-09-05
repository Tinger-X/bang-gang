# End-to-end test of LiveAssistant (real synthesized keyboard/mouse input).
# ASCII-only source (avoid PS5.1 ANSI misread of UTF-8 Chinese literals).
$ErrorActionPreference = 'Stop'
$exe = 'D:\project\bang-bang\dist\LiveAssistant.exe'
$dist = Split-Path $exe

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class K {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtra);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtra);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

function Find-VisibleWindow([int]$pidTarget) {
  $script:found = [IntPtr]::Zero
  $cb = [K+EnumProc]{ param($h,$l)
    $p=0; [K]::GetWindowThreadProcessId($h,[ref]$p) | Out-Null
    if ($p -eq $pidTarget) { if ([K]::IsWindowVisible($h)) { $script:found = $h; return $false } }
    return $true
  }
  [K]::EnumWindows($cb,[IntPtr]::Zero) | Out-Null
  return $script:found
}
# Tap (quick press-release) of an Alt+key combo
function Send-HotKey([byte]$vk) {
  [K]::keybd_event(0x12,0,0,[UIntPtr]::Zero)
  [K]::keybd_event($vk,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 70
  [K]::keybd_event($vk,0,2,[UIntPtr]::Zero)
  [K]::keybd_event(0x12,0,2,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 500
}
# Hold (press and keep) of Alt+key — used for press-and-hold recording
function Key-Down([byte]$vk){ [K]::keybd_event($vk,0,0,[UIntPtr]::Zero) }
function Key-Up([byte]$vk){ [K]::keybd_event($vk,0,2,[UIntPtr]::Zero) }

# 0) launch
Get-Process LiveAssistant -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
$proc = Start-Process -FilePath $exe -WorkingDirectory $dist -PassThru
$hwnd = [IntPtr]::Zero
for ($i=0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 100; $hwnd = Find-VisibleWindow $proc.Id; if ($hwnd -ne [IntPtr]::Zero) { break } }
if ($hwnd -eq [IntPtr]::Zero) { Write-Output 'FAIL start: no visible window'; exit 1 }
Write-Output 'PASS start: app window found'
$r = New-Object K+RECT
[K]::GetWindowRect($hwnd,[ref]$r) | Out-Null
Write-Output ("size: {0}x{1}" -f ($r.Right-$r.Left),($r.Bottom-$r.Top))

# 1) Alt+X hide/show
Send-HotKey 0x58
$hidden = -not [K]::IsWindowVisible($hwnd)
Write-Output ("Alt+X hide -> hidden={0} {1}" -f $hidden,$(if($hidden){'PASS'}else{'FAIL'}))
Send-HotKey 0x58
$shown = [K]::IsWindowVisible($hwnd)
Write-Output ("Alt+X show -> visible={0} {1}" -f $shown,$(if($shown){'PASS'}else{'FAIL'}))

# 2) Alt+C region screenshot
[System.Windows.Forms.Clipboard]::Clear()
Send-HotKey 0x43
Start-Sleep -Milliseconds 900
$x1=300;$y1=200;$x2=640;$y2=400
[K]::SetCursorPos($x1,$y1); Start-Sleep -Milliseconds 150
[K]::mouse_event(0x2,0,0,0,[UIntPtr]::Zero)
for($i=0;$i -le 8;$i++){ [K]::SetCursorPos($x1+($x2-$x1)*$i/8, $y1+($y2-$y1)*$i/8); Start-Sleep -Milliseconds 35 }
[K]::mouse_event(0x4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 800
$has = [System.Windows.Forms.Clipboard]::ContainsImage()
$sizeStr=''
if($has){ $im=[System.Windows.Forms.Clipboard]::GetImage(); $sizeStr='{0}x{1}' -f $im.Width,$im.Height }
Write-Output ("Alt+C shot -> hasImage={0} size={1} {2}" -f $has,$sizeStr,$(if($has){'PASS'}else{'FAIL'}))

# 3) Alt+V press-and-hold recording (play a tone during the hold)
$tone = Join-Path $dist 'test-tone.wav'
$sr=16000; $sec=2; $n=$sr*$sec; $amp=0.6
$buf = New-Object byte[] (44 + $n*2)
function W16($o,$v){ $buf[$o]=$v -band 0xFF; $buf[$o+1]=($v -shr 8) -band 0xFF }
function W32($o,$v){ for($j=0;$j -lt 4;$j++){ $buf[$o+$j]=($v -shr (8*$j)) -band 0xFF } }
[Text.Encoding]::ASCII.GetBytes('RIFF').CopyTo($buf,0); W32 4 (36+$n*2)
[Text.Encoding]::ASCII.GetBytes('WAVE').CopyTo($buf,8)
[Text.Encoding]::ASCII.GetBytes('fmt ').CopyTo($buf,12); W32 16 16
W16 20 1; W16 22 1; W32 24 $sr; W32 28 ($sr*2); W16 32 2; W16 34 16
[Text.Encoding]::ASCII.GetBytes('data').CopyTo($buf,36); W32 40 ($n*2)
for($i=0;$i -lt $n;$i++){ $v=[int]([Math]::Sin(2*[Math]::PI*660*$i/$sr)*$amp*32767); $buf[44+$i*2]=$v -band 0xFF; $buf[44+$i*2+1]=($v -shr 8) -band 0xFF }
[IO.File]::WriteAllBytes($tone,$buf)

$player = New-Object System.Media.SoundPlayer($tone)
$player.PlayLooping()
Start-Sleep -Milliseconds 250
Key-Down 0x12      # Alt down
Key-Down 0x56      # V down  -> recording starts
Start-Sleep -Milliseconds 2400
Key-Up 0x56        # release -> recording stops & saves
Key-Up 0x12
Start-Sleep -Milliseconds 1200
$player.Stop()

$recDir = Join-Path $dist 'recordings'
$wav = Get-ChildItem $recDir -Filter '*.wav' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $wav) { Write-Output 'FAIL record: no wav'; exit 1 }
$b2 = [IO.File]::ReadAllBytes($wav.FullName)
$ch=[BitConverter]::ToUInt16($b2,22); $rate=[BitConverter]::ToUInt32($b2,24); $dlen=[BitConverter]::ToInt32($b2,40)
$peak=0.0; $nz=0
for($i=44; $i+1 -lt $b2.Length; $i+=2){ $a=[Math]::Abs([BitConverter]::ToInt16($b2,$i)/32768.0); if($a -gt $peak){$peak=$a}; if($a -gt 0.01){$nz++} }
Write-Output ("record(hold): name={0} ch={1} rate={2} dataLen={3} peak={4:F3} loudSamples={5}" -f $wav.Name,$ch,$rate,$dlen,$peak,$nz)
$ok = ($dlen -gt 1000) -and ($peak -gt 0.05) -and ($ch -ge 2)
Write-Output ("Alt+V hold record -> {0}" -f $(if($ok){'PASS'}else{'FAIL'}))

# 4) Click the custom close button -> app must exit
[K]::GetWindowRect($hwnd,[ref]$r) | Out-Null
$W = $r.Right - $r.Left
$cx = $r.Left + $W - 23   # ActualCloseRect center X (right margin 8 + 15)
$cy = $r.Top + 23         # top 8 + 15
[K]::SetCursorPos($cx,$cy); Start-Sleep -Milliseconds 200
[K]::mouse_event(0x2,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 120
[K]::mouse_event(0x4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 1200
$exited = (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) -eq $null
Write-Output ("click close -> exited={0} {1}" -f $exited,$(if($exited){'PASS'}else{'FAIL'}))

Write-Output 'DONE'
