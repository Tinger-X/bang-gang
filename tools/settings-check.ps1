# Drive the settings popup and capture screenshots (local UI review only).
#
# NOTE: the Release build can never be captured (WDA_EXCLUDEFROMCAPTURE with no runtime
# backdoor). UI screenshots therefore use a *Debug* build, whose temporary
# BANGGANG_SHOW_IN_CAPTURE=1 switch makes the window capturable.
param(
    [string]$Config = 'Debug',
    [string]$OutDir = 'D:\project\bang-bang\dist\ui-preview'
)
$ErrorActionPreference = 'Stop'
$proj = 'D:\project\bang-bang\src\BangGang\BangGang.csproj'
$dist = $OutDir
$shotDir = 'D:\project\bang-bang\dist\shots'
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Remove-Item (Join-Path $dist 'settings.json') -ErrorAction SilentlyContinue   # clean baseline
Write-Output ("publishing {0} build -> {1}" -f $Config, $dist)
dotnet publish $proj -c $Config -o $dist --nologo | Out-Null
$exe = Join-Path $dist 'BangGang.exe'
if (-not (Test-Path $exe)) { Write-Output "publish failed: $exe"; exit 1 }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SD {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT{ public int L,T,R,B; }
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
"@

function Find-Main([int]$targetPid) {
  $script:main=[IntPtr]::Zero
  $cb=[SD+EnumProc]{ param($h,$l)
    $p=0; [SD]::GetWindowThreadProcessId($h,[ref]$p)|Out-Null
    if($p -eq $targetPid){ $s=New-Object System.Text.StringBuilder 120; [SD]::GetWindowText($h,$s,120)|Out-Null
      if($s.ToString().Length -gt 0 -and [SD]::IsWindowVisible($h)){ $script:main=$h; return $false } }
    return $true }
  [SD]::EnumWindows($cb,[IntPtr]::Zero)|Out-Null
  return $script:main
}

function Click([int]$x,[int]$y){
  Raise-App                       # 先确保应用在最前，否则点击会落到别的窗口上
  [void][SD]::SetCursorPos($x,$y); Start-Sleep -Milliseconds 150
  [SD]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 90
  [SD]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 320
}
function Hover([int]$x,[int]$y){ Raise-App; [void][SD]::SetCursorPos($x,$y); Start-Sleep -Milliseconds 300 }
function Combo([int]$vk,[bool]$ctrl,[bool]$alt){
  if($ctrl){ [SD]::keybd_event(0x11,0,0,[UIntPtr]::Zero) }
  if($alt){ [SD]::keybd_event(0x12,0,0,[UIntPtr]::Zero) }
  Start-Sleep -Milliseconds 60
  [SD]::keybd_event([byte]$vk,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 80
  [SD]::keybd_event([byte]$vk,0,2,[UIntPtr]::Zero)
  if($alt){ [SD]::keybd_event(0x12,0,2,[UIntPtr]::Zero) }
  if($ctrl){ [SD]::keybd_event(0x11,0,2,[UIntPtr]::Zero) }
  Start-Sleep -Milliseconds 220
}

$script:L=0; $script:T=0; $script:W=0; $script:H=0; $script:hwnd=[IntPtr]::Zero

# Raise the app to the very front: another (also topmost) window may be sitting
# above it, in which case clicks and grabs would hit that window instead.
function Raise-App(){
  if($script:hwnd -eq [IntPtr]::Zero){ return }
  [void][SD]::BringWindowToTop($script:hwnd)
  [void][SD]::SetForegroundWindow($script:hwnd)
  [void][SD]::SetWindowPos($script:hwnd,[IntPtr]::Zero,0,0,0,0,0x0003)   # HWND_TOPMOST, keep position/size
  Start-Sleep -Milliseconds 450
}
function Shot([string]$name){
  Raise-App
  $bmp = New-Object System.Drawing.Bitmap($script:W,$script:H)
  $g=[System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($script:L,$script:T,0,0,$bmp.Size)
  $out=Join-Path $shotDir ($name + '.png')
  $bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Output ("shot -> {0}" -f $name)
}

Get-Process BangGang -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
$env:BANGGANG_SHOW_IN_CAPTURE='1'
$proc=Start-Process -FilePath $exe -WorkingDirectory $dist -PassThru
Remove-Item Env:\BANGGANG_SHOW_IN_CAPTURE
Start-Sleep -Milliseconds 2400
$hwnd=Find-Main $proc.Id
if($hwnd -eq [IntPtr]::Zero){ Write-Output 'no main window'; exit 1 }
$script:hwnd=$hwnd
$r=New-Object SD+RECT
[void][SD]::GetWindowRect($hwnd,[ref]$r)
$script:L=$r.L; $script:T=$r.T; $script:W=$r.R-$r.L; $script:H=$r.B-$r.T
Write-Output ("window at {0},{1} {2}x{3}" -f $script:L,$script:T,$script:W,$script:H)

# popup geometry: 880x640 centered inside the window, all coordinates card-relative
$cw=880; $ch=640
$cx=[int](($script:W-$cw)/2); $cy=[int](($script:H-$ch)/2)
$cardX=$script:L+$cx; $cardY=$script:T+$cy
$closeX=$cardX+$cw-30; $closeY=$cardY+26
$neutralX=$cardX+430; $neutralY=$cardY+340
$navY0=$cardY+96+21            # first nav item centre
$saveX=$cardX+795; $saveY=$cardY+610
$rowY1=$cardY+178              # centre of the first row's right-hand widget
$purpleDotX=$cardX+529

# 0) make sure the app really is the front window: another window may be sitting
#    above it, in which case the clicks below would hit that window instead.
[void][SD]::ShowWindow($hwnd,9)
Raise-App

# 1) open settings via the gear button, hover the close button
Click ($script:L+232) ($script:T+203)
Start-Sleep -Milliseconds 800
Hover $closeX $closeY
Shot 'settings-1-shortcuts'

# 2) models page (second nav item)
Hover $neutralX $neutralY
Click ($cardX+104) ($navY0+48)
Start-Sleep -Milliseconds 450
Hover $neutralX $neutralY
Shot 'settings-2-models'

# 3) appearance page (third nav item)
Click ($cardX+104) ($navY0+96)
Start-Sleep -Milliseconds 450
Hover $neutralX $neutralY
Shot 'settings-3-appearance'

# 4) pick the purple accent preset -> page becomes dirty (save enabled)
Click $purpleDotX $rowY1
Start-Sleep -Milliseconds 450
Hover $neutralX $neutralY
Shot 'settings-4-dirty'

# 5) save
Click $saveX $saveY
Start-Sleep -Milliseconds 800
Hover $neutralX $neutralY
Shot 'settings-5-saved'
if(Test-Path (Join-Path $dist 'settings.json')){ Write-Output 'settings.json written:'; (Get-Content (Join-Path $dist 'settings.json') -Raw) -split "`n" | Select-String -Pattern 'Accent|ChatBg' }

# 6) shortcuts page: capture a new combo in the first key box (Ctrl+Alt+K)
Click ($cardX+104) $navY0
Start-Sleep -Milliseconds 400
Click ($cardX+694) $rowY1
Start-Sleep -Milliseconds 300
Combo 0x4B $true $true
Start-Sleep -Milliseconds 300
Hover $neutralX $neutralY
Shot 'settings-6-newcombo'

# 7) close with unsaved changes -> confirmation card
Click $closeX $closeY
Start-Sleep -Milliseconds 500
Shot 'settings-7-confirm'

# 8) stay editing
Click ($cardX+(($cw-420)/2)+12+(420-24-232)/2+52) ($cardY+(($ch-180)/2)+100+17)
Start-Sleep -Milliseconds 400
Shot 'settings-8-stay'

# 9) close again, then discard
Click $closeX $closeY
Start-Sleep -Milliseconds 500
Click ($cardX+(($cw-420)/2)+12+(420-24-232)/2+104+12+58) ($cardY+(($ch-180)/2)+100+17)
Start-Sleep -Milliseconds 700
Shot 'settings-9-closed'

if(Test-Path (Join-Path $dist 'crash.log')){ Write-Output 'CRASHLOG EXISTS'; Get-Content (Join-Path $dist 'crash.log') -Tail 8 }
Get-Process BangGang -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item (Join-Path $dist 'settings.json') -ErrorAction SilentlyContinue   # drop test-written settings
Write-Output 'done'
