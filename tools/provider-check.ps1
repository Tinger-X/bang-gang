# Verify that switching provider changes the number/titles of the parameter inputs.
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
public static class PV {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x,int y,int cx,int cy,uint f);
  public struct RECT{ public int L,T,R,B; }
}
"@
function Pump([int]$ms){ $n=[int]($ms/25); for($i=0;$i -lt $n;$i++){ [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 25 } }
$script:hwnd=[IntPtr]::Zero
function Raise-App(){ if($script:hwnd -ne [IntPtr]::Zero){ [void][PV]::BringWindowToTop($script:hwnd); [void][PV]::SetForegroundWindow($script:hwnd); [void][PV]::SetWindowPos($script:hwnd,[IntPtr]::Zero,0,0,0,0,0x0003); Pump 350 } }
function Click([int]$x,[int]$y){ Raise-App; [void][PV]::SetCursorPos($x,$y); Pump 180
  [PV]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Pump 90; [PV]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Pump 420 }
# picking a list item must not re-activate the window: that would close the list
function ClickSoft([int]$x,[int]$y){ [void][PV]::SetCursorPos($x,$y); Pump 180
  [PV]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Pump 90; [PV]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Pump 420 }
$script:L=0; $script:T=0; $script:W=0; $script:H=0
function Shot([string]$name){ Raise-App
  $bmp=New-Object System.Drawing.Bitmap($script:W,$script:H)
  $g=[System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($script:L,$script:T,0,0,$bmp.Size); $g.Dispose()
  $bmp.Save((Join-Path $shotDir ($name+'.png')),[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  Write-Output "shot -> $name" }

Get-Process BangGang -ErrorAction SilentlyContinue | Stop-Process -Force
Pump 800
$env:BANGGANG_SHOW_IN_CAPTURE='1'
$proc=Start-Process -FilePath $exe -WorkingDirectory $dist -PassThru
Remove-Item Env:\BANGGANG_SHOW_IN_CAPTURE
Pump 2800
$script:m=[IntPtr]::Zero
$cb=[PV+EnumProc]{ param($h,$l) $pp=0; [PV]::GetWindowThreadProcessId($h,[ref]$pp)|Out-Null
  if($pp -eq $proc.Id){ $t=New-Object System.Text.StringBuilder 120; [PV]::GetWindowText($h,$t,120)|Out-Null
    if($t.ToString().Length -gt 0 -and [PV]::IsWindowVisible($h)){ $script:m=$h; return $false } }
  return $true }
[PV]::EnumWindows($cb,[IntPtr]::Zero)|Out-Null
$script:hwnd=$script:m
if($script:hwnd -eq [IntPtr]::Zero){ Write-Output 'no window'; exit 1 }
$r=New-Object PV+RECT; [void][PV]::GetWindowRect($script:hwnd,[ref]$r)
$script:L=$r.L; $script:T=$r.T; $script:W=$r.R-$r.L; $script:H=$r.B-$script:T

$cw=880; $ch=640
$cardX=$script:L+[int](($script:W-$cw)/2); $cardY=$script:T+[int](($script:H-$ch)/2)
$navY0=$cardY+96+21
$fieldX=$cardX+647                      # centre of the dropdown / input column
$chatDropY=$cardY+178                   # provider dropdown of the chat card
$itemY0=$cardY+195+28                   # first list item centre (list opens below the field)

Click ($script:L+232) ($script:T+203)
Pump 800
Click ($cardX+104) ($navY0+48)
Pump 500
Shot 'pv-1-custom'

Click $fieldX $chatDropY                # open the provider list
Pump 500
Shot 'pv-2-list-open'
[PV]::keybd_event(0x1B,0,0,[UIntPtr]::Zero); Pump 60     # Esc closes the list
[PV]::keybd_event(0x1B,0,2,[UIntPtr]::Zero); Pump 300
Click $fieldX $chatDropY                # open again, then pick
Pump 400
ClickSoft $fieldX ($itemY0+30*7)        # item 8 = Ollama (local) -> 2 fields
Pump 700
Shot 'pv-3-ollama'

Click $fieldX $chatDropY
Pump 400
ClickSoft $fieldX ($itemY0+30*5)        # item 6 = Volcengine Ark -> 3 fields, 3rd title differs
Pump 700
Shot 'pv-4-ark'

Click ($cardX+795) ($cardY+610)         # save so settings.json records both profiles
Pump 1000
$sj = Join-Path $dist 'settings.json'
if(Test-Path $sj){
  $txt = Get-Content $sj -Raw
  foreach($key in @('"ChatProvider"','"ChatProfiles"','"Ollama','"火山方舟"','"model"')){
    $hit = ($txt -split "`n" | Select-String -SimpleMatch -Pattern $key | Select-Object -First 1)
    if($hit){ Write-Output ("  {0} -> {1}" -f $key, $hit.Line.Trim()) }
  }
}
if(Test-Path (Join-Path $dist 'crash.log')){ Write-Output 'CRASH:'; Get-Content (Join-Path $dist 'crash.log') -Tail 10 } else { Write-Output 'no crash.log' }
Get-Process BangGang -ErrorAction SilentlyContinue | Stop-Process -Force
Pump 400
Remove-Item $sj -ErrorAction SilentlyContinue
