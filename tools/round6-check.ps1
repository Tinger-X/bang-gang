# Round-6 UI verification: antialiased corners, sidebar conversation list, dropdown/input metrics.
#
# NOTE: screenshots require the Debug build's BANGGANG_SHOW_IN_CAPTURE=1 switch.
param(
    [string]$OutDir = 'D:\project\bang-bang\dist\ui-preview'
)
$ErrorActionPreference = 'Stop'
$dist = $OutDir
$shotDir = 'D:\project\bang-bang\dist\shots'
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Remove-Item (Join-Path $dist 'settings.json') -ErrorAction SilentlyContinue

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class R6 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT{ public int L,T,R,B; }
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,int dx,int dy,int d,UIntPtr e);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
"@

$script:L=0; $script:T=0; $script:W=1200; $script:H=800; $script:hwnd=[IntPtr]::Zero

function Find-Main([int]$targetPid) {
  $script:main=[IntPtr]::Zero
  $cb=[R6+EnumProc]{ param($h,$l)
    $p=0; [R6]::GetWindowThreadProcessId($h,[ref]$p)|Out-Null
    if($p -eq $targetPid){ $s=New-Object System.Text.StringBuilder 120; [R6]::GetWindowText($h,$s,120)|Out-Null
      if($s.ToString().Length -gt 0 -and [R6]::IsWindowVisible($h)){ $script:main=$h; return $false } }
    return $true }
  [R6]::EnumWindows($cb,[IntPtr]::Zero)|Out-Null
  return $script:main
}

function Raise-App(){
  if($script:hwnd -eq [IntPtr]::Zero){ return }
  [void][R6]::BringWindowToTop($script:hwnd)
  [void][R6]::SetForegroundWindow($script:hwnd)
  [void][R6]::SetWindowPos($script:hwnd,[IntPtr]::Zero,0,0,0,0,0x0003)
  Start-Sleep -Milliseconds 320
}
function Click([int]$x,[int]$y){
  Raise-App
  [void][R6]::SetCursorPos($x,$y); Start-Sleep -Milliseconds 120
  [R6]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 80
  [R6]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 260
}
function Hover([int]$x,[int]$y){ [void][R6]::SetCursorPos($x,$y); Start-Sleep -Milliseconds 350 }
function Shot([string]$name){
  Raise-App
  $bmp = New-Object System.Drawing.Bitmap($script:W,$script:H)
  $g=[System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($script:L,$script:T,0,0,$bmp.Size)
  $bmp.Save((Join-Path $shotDir ($name+'.png')),[System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Output "shot -> $name"
}

# is the settings card on screen? sample a pixel just inside its right edge
function CardOpen(){
  $bmp = New-Object System.Drawing.Bitmap($script:W,$script:H)
  $g=[System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($script:L,$script:T,0,0,$bmp.Size); $g.Dispose()
  $c=$bmp.GetPixel(1035,400)
  $bmp.Dispose()
  return ($c.R -eq 252 -and $c.G -eq 253 -and $c.B -eq 255)
}

Get-Process BangGang -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$env:BANGGANG_SHOW_IN_CAPTURE='1'
$proc=Start-Process (Join-Path $dist 'BangGang.exe') -WorkingDirectory $dist -PassThru
Remove-Item Env:\BANGGANG_SHOW_IN_CAPTURE
Start-Sleep -Milliseconds 2600
$h=Find-Main $proc.Id
if($h -eq [IntPtr]::Zero){ Write-Output 'no main window'; exit 1 }
$script:hwnd=$h
$r=New-Object R6+RECT
[void][R6]::GetWindowRect($h,[ref]$r)
$script:L=$r.L; $script:T=$r.T; $script:W=$r.R-$r.L; $script:H=$r.B-$r.T
Write-Output ("window at {0},{1} {2}x{3}" -f $script:L,$script:T,$script:W,$script:H)

# ---------- 1) sidebar conversation list ----------
# the "+" (new conversation) button sits at window (272,203); repeat to fill the list
for($i=0;$i -lt 16;$i++){ Click ($script:L+272) ($script:T+203); Start-Sleep -Milliseconds 90 }
Hover ($script:L+700) ($script:T+400)                 # park the cursor away from the list
Shot 'r6-1-convlist'
Hover ($script:L+150) ($script:T+257)                 # hover the first row
Shot 'r6-2-convlist-hover'
Hover ($script:L+150) ($script:T+345)                 # hover the third row
Shot 'r6-3-convlist-hover3'
Hover ($script:L+288) ($script:T+345)                 # hover its delete icon (right side of the row)
Shot 'r6-4-convlist-del'

# ---------- 2) settings: dropdown/input metrics + card corners ----------
$cw=880; $ch=640
$cardX=$script:L+[int](($script:W-$cw)/2); $cardY=$script:T+[int](($script:H-$ch)/2)
$navY0=$cardY+96+21
Click ($script:L+232) ($script:T+203)                 # gear
Start-Sleep -Milliseconds 700
if(CardOpen){ Write-Output 'PASS: settings card is on screen' } else { Write-Output 'FAIL: settings card did not open' }
Click ($cardX+104) ($navY0+48)                        # models page
Start-Sleep -Milliseconds 500
Hover ($cardX+430) ($cardY+340)
Shot 'r6-5-models'
Click ($cardX+104) ($navY0+96)                        # appearance page
Start-Sleep -Milliseconds 450
Click ($cardX+556+30*2) ($cardY+232)                  # pick a different preset -> page becomes dirty
Start-Sleep -Milliseconds 400
Hover ($cardX+430) ($cardY+340)
Shot 'r6-6-appearance'
Click ($cardX+$cw-30) ($cardY+26)                     # close -> confirm card
Start-Sleep -Milliseconds 600
Hover ($cardX+430) ($cardY+560)
Shot 'r6-7-confirm'

Get-Process BangGang -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
Remove-Item (Join-Path $dist 'settings.json') -ErrorAction SilentlyContinue
if(Test-Path (Join-Path $dist 'crash.log')){ Write-Output 'CRASHLOG EXISTS'; Get-Content (Join-Path $dist 'crash.log') -Tail 8 }
Write-Output 'round6-check done'
