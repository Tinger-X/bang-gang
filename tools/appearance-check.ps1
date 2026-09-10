# Appearance verification: draggable popup, dark mode, window border toggle (local UI review).
# Uses a Debug build because the Release build is never capturable.
param(
    [string]$Config = 'Debug',
    [string]$OutDir = 'D:\project\bang-bang\dist\ui-preview'
)
$ErrorActionPreference = 'Stop'
$proj = 'D:\project\bang-bang\src\BangGang\BangGang.csproj'
$dist = $OutDir
$shotDir = 'D:\project\bang-bang\dist\shots'
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
Remove-Item (Join-Path $dist 'settings.json') -ErrorAction SilentlyContinue
dotnet publish $proj -c $Config -o $dist --nologo | Out-Null
$exe = Join-Path $dist 'BangGang.exe'
if (-not (Test-Path $exe)) { Write-Output 'publish failed'; exit 1 }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class AP {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,UIntPtr e);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x,int y,int cx,int cy,uint f);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  public struct RECT{ public int L,T,R,B; }
}
"@
function Pump([int]$ms){ $n=[int]($ms/25); for($i=0;$i -lt $n;$i++){ [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 25 } }
$script:hwnd=[IntPtr]::Zero
function Raise-App(){
  if($script:hwnd -eq [IntPtr]::Zero){ return }
  [void][AP]::BringWindowToTop($script:hwnd); [void][AP]::SetForegroundWindow($script:hwnd)
  [void][AP]::SetWindowPos($script:hwnd,[IntPtr]::Zero,0,0,0,0,0x0003)
  Pump 400
}
function Click([int]$x,[int]$y){
  Raise-App; [void][AP]::SetCursorPos($x,$y); Pump 180
  [AP]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Pump 90
  [AP]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Pump 420
}
function Drag([int]$x1,[int]$y1,[int]$x2,[int]$y2){
  Raise-App; [void][AP]::SetCursorPos($x1,$y1); Pump 220
  [AP]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Pump 140
  for($i=1;$i -le 14;$i++){
    [void][AP]::SetCursorPos([int]($x1+($x2-$x1)*$i/14), [int]($y1+($y2-$y1)*$i/14)); Pump 45
  }
  [AP]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Pump 450
}
$script:L=0; $script:T=0; $script:W=0; $script:H=0
function Shot([string]$name){
  Raise-App
  $bmp=New-Object System.Drawing.Bitmap($script:W,$script:H)
  $g=[System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($script:L,$script:T,0,0,$bmp.Size); $g.Dispose()
  $bmp.Save((Join-Path $shotDir ($name+'.png')),[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  Write-Output "shot -> $name"
}

Get-Process BangGang -ErrorAction SilentlyContinue | Stop-Process -Force
Pump 800
$env:BANGGANG_SHOW_IN_CAPTURE='1'
$proc=Start-Process -FilePath $exe -WorkingDirectory $dist -PassThru
Remove-Item Env:\BANGGANG_SHOW_IN_CAPTURE
Pump 2800
$script:m=[IntPtr]::Zero
$cb=[AP+EnumProc]{ param($h,$l) $pp=0; [AP]::GetWindowThreadProcessId($h,[ref]$pp)|Out-Null
  if($pp -eq $proc.Id){ $t=New-Object System.Text.StringBuilder 120; [AP]::GetWindowText($h,$t,120)|Out-Null
    if($t.ToString().Length -gt 0 -and [AP]::IsWindowVisible($h)){ $script:m=$h; return $false } }
  return $true }
[AP]::EnumWindows($cb,[IntPtr]::Zero)|Out-Null
$script:hwnd=$script:m
if($script:hwnd -eq [IntPtr]::Zero){ Write-Output 'no main window'; exit 1 }
$r=New-Object AP+RECT; [void][AP]::GetWindowRect($script:hwnd,[ref]$r)
$script:L=$r.L; $script:T=$r.T; $script:W=$r.R-$r.L; $script:H=$r.B-$r.T
Write-Output ("window {0},{1} {2}x{3}" -f $script:L,$script:T,$script:W,$script:H)

# card geometry (880x640 centered), then the popup is dragged by (-240,+80)
$cw=880; $ch=640
$cx=[int](($script:W-$cw)/2); $cy=[int](($script:H-$ch)/2)
$cardX=$script:L+$cx; $cardY=$script:T+$cy

Click ($script:L+232) ($script:T+203)          # gear -> open settings
Pump 800
Shot 'appearance-0-open'

# 浮窗现在固定居中、不可拖动：试着点/拖菜单栏空白处也应保持原位
Drag ($cardX+100) ($cardY+560) ($cardX+180) ($cardY+620)
Shot 'appearance-1-drag-attempt'

Click ($cardX+104) ($cardY+96+96+21)           # nav: 界面外观
Pump 500
Shot 'appearance-2-page'

Click ($cardX+652) ($cardY+178)                # segmented: 暗色
Pump 450
Shot 'appearance-3-dark-chosen'

Click ($cardX+795) ($cardY+610)                # save
Pump 1000
Shot 'appearance-4-dark-saved'

Click ($cardX+788) ($cardY+422)                # toggle 窗口边框 off
Pump 450
Click ($cardX+795) ($cardY+610)                # save
Pump 1000
Shot 'appearance-5-border-off'

Click ($cardX+850) ($cardY+26)                 # close popup
Pump 800
Shot 'appearance-6-closed'

if(Test-Path (Join-Path $dist 'crash.log')){ Write-Output 'CRASHLOG:'; Get-Content (Join-Path $dist 'crash.log') -Tail 10 } else { Write-Output 'no crash.log' }
Get-Process BangGang -ErrorAction SilentlyContinue | Stop-Process -Force
Pump 400
Remove-Item (Join-Path $dist 'settings.json') -ErrorAction SilentlyContinue
