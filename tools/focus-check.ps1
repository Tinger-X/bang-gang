# Focused checks: provider dropdown, slim scrollbar, shortcuts page with 录音方式.
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
public static class FX {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,UIntPtr e);
  [DllImport("user32.dll", EntryPoint="mouse_event")] public static extern void mouse_event_wheel(uint f,uint dx,uint dy,int d,UIntPtr e);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x,int y,int cx,int cy,uint f);
  public struct RECT{ public int L,T,R,B; }
}
"@
function Pump([int]$ms){ $n=[int]($ms/25); for($i=0;$i -lt $n;$i++){ [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 25 } }
$script:hwnd=[IntPtr]::Zero
function Raise-App(){ if($script:hwnd -ne [IntPtr]::Zero){ [void][FX]::BringWindowToTop($script:hwnd); [void][FX]::SetForegroundWindow($script:hwnd); [void][FX]::SetWindowPos($script:hwnd,[IntPtr]::Zero,0,0,0,0,0x0003); Pump 350 } }
function Click([int]$x,[int]$y){ Raise-App; [void][FX]::SetCursorPos($x,$y); Pump 180; [FX]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Pump 90; [FX]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Pump 420 }
function Hover([int]$x,[int]$y){ Raise-App; [void][FX]::SetCursorPos($x,$y); Pump 300 }
function Wheel([int]$down,[int]$x,[int]$y){ Raise-App; [void][FX]::SetCursorPos($x,$y); Pump 150
  for($i=0;$i -lt $down;$i++){ [FX]::mouse_event_wheel(0x0800,0,0,-120,[UIntPtr]::Zero); Pump 90 } }
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
$cb=[FX+EnumProc]{ param($h,$l) $pp=0; [FX]::GetWindowThreadProcessId($h,[ref]$pp)|Out-Null
  if($pp -eq $proc.Id){ $t=New-Object System.Text.StringBuilder 120; [FX]::GetWindowText($h,$t,120)|Out-Null
    if($t.ToString().Length -gt 0 -and [FX]::IsWindowVisible($h)){ $script:m=$h; return $false } }
  return $true }
[FX]::EnumWindows($cb,[IntPtr]::Zero)|Out-Null
$script:hwnd=$script:m
if($script:hwnd -eq [IntPtr]::Zero){ Write-Output 'no window'; exit 1 }
$r=New-Object FX+RECT; [void][FX]::GetWindowRect($script:hwnd,[ref]$r)
$script:L=$r.L; $script:T=$r.T; $script:W=$r.R-$r.L; $script:H=$r.B-$r.T

$cw=880; $ch=640
$cardX=$script:L+[int](($script:W-$cw)/2); $cardY=$script:T+[int](($script:H-$ch)/2)
$navY0=$cardY+96+21

# 1) shortcuts page: renamed row + 录音方式 (2nd card, 1st row -> segmented "按下")
Click ($script:L+232) ($script:T+203)
Pump 800
Shot 'fx-1-shortcuts'
Click ($cardX+712) ($cardY+422)          # 录音方式 = 按下
Pump 400
Shot 'fx-2-recmode-toggle'
Click ($cardX+795) ($cardY+610)          # save
Pump 1000
Shot 'fx-3-recmode-saved'
$sj = Join-Path $dist 'settings.json'
if(Test-Path $sj){ Write-Output ("saved RecordMode line: " + ((Get-Content $sj -Raw) -split "`n" | Select-String -Pattern 'RecordMode' | ForEach-Object { $_.Line.Trim() })) }

# 1b) opacity regression: 100% -> 70% -> save -> back to 100% must re-enable 保存
Click ($cardX+104) ($navY0+96)           # appearance page
Pump 500
Click ($cardX+512+95) ($cardY+370)       # slider ~70%
Pump 400
$btn70 = (New-Object System.Drawing.Bitmap(1,1))
$bmp=New-Object System.Drawing.Bitmap($script:W,$script:H)
$g=[System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($script:L,$script:T,0,0,$bmp.Size); $g.Dispose()
$c=$bmp.GetPixel($cardX+795-$script:L,$cardY+610-$script:T); Write-Output ("save button @70%: #{0:X2}{1:X2}{2:X2}" -f $c.R,$c.G,$c.B)
$bmp.Dispose()
Click ($cardX+795) ($cardY+610)          # save 70%
Pump 1000
Click ($cardX+512+245) ($cardY+370)      # slider back to 100%
Pump 500
$bmp=New-Object System.Drawing.Bitmap($script:W,$script:H)
$g=[System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($script:L,$script:T,0,0,$bmp.Size); $g.Dispose()
$c=$bmp.GetPixel($cardX+795-$script:L,$cardY+610-$script:T); Write-Output ("save button back@100%: #{0:X2}{1:X2}{2:X2}  (accent #2F70E0 = clickable)" -f $c.R,$c.G,$c.B)
$bmp.Save((Join-Path $shotDir 'fx-0-opacity.png'),[System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
if(Test-Path $sj){ Write-Output ("saved Opacity line: " + ((Get-Content $sj -Raw) -split "`n" | Select-String -Pattern 'Opacity' | ForEach-Object { $_.Line.Trim() })) }

# 2) models page: dropdown closed / open / choosing a provider
Click ($cardX+104) ($navY0+48)
Pump 500
Shot 'fx-4-models'
Click ($cardX+700) ($cardY+178)          # open the chat provider dropdown
Pump 500
Shot 'fx-5-dropdown-open'
Click ($cardX+700) ($cardY+178+4+5+30+15) # choose 2nd item (通义千问)
Pump 500
Shot 'fx-6-preset-chosen'

# 3) scrollbar: scroll the models page down
Wheel 4 ($cardX+400) ($cardY+400)
Pump 400
Shot 'fx-7-scrolled'

# 4) appearance page: accent swatch clipping check
Click ($cardX+104) ($navY0+96)
Pump 500
Shot 'fx-8-appearance'

if(Test-Path (Join-Path $dist 'crash.log')){ Write-Output 'CRASHLOG:'; Get-Content (Join-Path $dist 'crash.log') -Tail 10 } else { Write-Output 'no crash.log' }
Get-Process BangGang -ErrorAction SilentlyContinue | Stop-Process -Force
Pump 400
Remove-Item (Join-Path $dist 'settings.json') -ErrorAction SilentlyContinue
