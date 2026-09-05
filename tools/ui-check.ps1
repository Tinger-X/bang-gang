# Drive the new chat UI: create a conversation, type+send, open settings, screenshot the window.
$ErrorActionPreference = 'Stop'
$exe = 'D:\project\bang-bang\dist\LiveAssistant.exe'
$dist = Split-Path $exe

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class D {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT{ public int L,T,R,B; }
  [DllImport("user32.dll")] public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,UIntPtr e);
}
"@

function Find-Main([int]$targetPid) {
  $script:main=[IntPtr]::Zero
  $cb=[D+EnumProc]{ param($h,$l)
    $p=0; [D]::GetWindowThreadProcessId($h,[ref]$p)|Out-Null
    if($p -eq $targetPid){ $s=New-Object System.Text.StringBuilder 120; [D]::GetWindowText($h,$s,120)|Out-Null
      if($s.ToString().Length -gt 0 -and [D]::IsWindowVisible($h)){ $script:main=$h; return $false } }
    return $true }
  [D]::EnumWindows($cb,[IntPtr]::Zero)|Out-Null
  return $script:main
}
function Click([int]$x,[int]$y){
  [D]::SetCursorPos($x,$y); Start-Sleep -Milliseconds 120
  [D]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 80
  [D]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 250
}
function TypeText([string]$s){
  foreach($ch in $s.ToCharArray()){
    $vk=[int][char]($ch.ToString().ToUpper())
    if($ch -ge 'a' -and $ch -le 'z'){
      [D]::keybd_event(0x10,0,0,[UIntPtr]::Zero); [D]::keybd_event($vk,0,0,[UIntPtr]::Zero)
      Start-Sleep -Milliseconds 18
      [D]::keybd_event($vk,0,2,[UIntPtr]::Zero); [D]::keybd_event(0x10,0,2,[UIntPtr]::Zero)
    } elseif($ch -ge 'A' -and $ch -le 'Z'){
      [D]::keybd_event($vk,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 12; [D]::keybd_event($vk,0,2,[UIntPtr]::Zero)
    } elseif($ch -eq ' '){ [D]::keybd_event(0x20,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 12; [D]::keybd_event(0x20,0,2,[UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 24
  }
}

Get-Process LiveAssistant -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
$proc=Start-Process -FilePath $exe -WorkingDirectory $dist -PassThru
Start-Sleep -Milliseconds 2200
$hwnd=Find-Main $proc.Id
if($hwnd -eq [IntPtr]::Zero){ Write-Output 'no main window'; exit 1 }
$r=New-Object D+RECT
[D]::GetWindowRect($hwnd,[ref]$r)|Out-Null
$L=$r.L; $T=$r.T; $W=$r.R-$r.L
Write-Output ("window at {0},{1} {2}x{3}" -f $L,$T,$W,($r.B-$r.T))

# 1) click "+" new conversation (sidebar header + button, centered ~ L+182,T+200)
Click ($L+182) ($T+200)
Start-Sleep -Milliseconds 600

# 2) click input area and type + Enter (send)
Click ($L+600) ($T+700)
TypeText "hello"
[D]::keybd_event(0x0D,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 30; [D]::keybd_event(0x0D,0,2,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 1500

# 3) open settings (gear in sidebar header ~ L+270,T+200) then Esc to close
Click ($L+270) ($T+200)
Start-Sleep -Milliseconds 900
[D]::keybd_event(0x1B,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 40; [D]::keybd_event(0x1B,0,2,[UIntPtr]::Zero) # Esc
Start-Sleep -Milliseconds 400

# 4) screenshot main window to file
$bmp = New-Object System.Drawing.Bitmap($W,($r.B-$r.T))
$g=[System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($L,$T,0,0,$bmp.Size)
$out=Join-Path $dist 'ui-check.png'
$bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ("saved ui-check.png {0}x{1}" -f $W,($r.B-$r.T))
if(Test-Path (Join-Path $dist 'crash.log')){ Write-Output 'CRASHLOG EXISTS'; Get-Content (Join-Path $dist 'crash.log') -Tail 5 }
