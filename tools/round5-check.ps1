# Round-5 UI verification for the settings popup (local review only).
#
# Covers: live shortcut capture, narrower segmented controls, provider switch without
# flicker, dropdown scroll + close-on-outside-click, confirm dialog (no shadow),
# preset colour highlight (sliding background, hidden for custom colours).
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
public static class R5 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT{ public int L,T,R,B; }
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,int dx,int dy,int d,UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
"@

$script:L=0; $script:T=0; $script:W=1200; $script:H=800; $script:hwnd=[IntPtr]::Zero

function Find-Main([int]$targetPid) {
  $script:main=[IntPtr]::Zero
  $cb=[R5+EnumProc]{ param($h,$l)
    $p=0; [R5]::GetWindowThreadProcessId($h,[ref]$p)|Out-Null
    if($p -eq $targetPid){ $s=New-Object System.Text.StringBuilder 120; [R5]::GetWindowText($h,$s,120)|Out-Null
      if($s.ToString().Length -gt 0 -and [R5]::IsWindowVisible($h)){ $script:main=$h; return $false } }
    return $true }
  [R5]::EnumWindows($cb,[IntPtr]::Zero)|Out-Null
  return $script:main
}

function Raise-App(){
  if($script:hwnd -eq [IntPtr]::Zero){ return }
  [void][R5]::BringWindowToTop($script:hwnd)
  [void][R5]::SetForegroundWindow($script:hwnd)
  [void][R5]::SetWindowPos($script:hwnd,[IntPtr]::Zero,0,0,0,0,0x0003)
  Start-Sleep -Milliseconds 400
}
function Click([int]$x,[int]$y){
  Raise-App
  [void][R5]::SetCursorPos($x,$y); Start-Sleep -Milliseconds 150
  [R5]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 90
  [R5]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 320
}
# click without re-activating the app (a popup list closes when the window loses focus)
function ClickSoft([int]$x,[int]$y){
  [void][R5]::SetCursorPos($x,$y); Start-Sleep -Milliseconds 200
  [R5]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 90
  [R5]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 320
}
function Wheel([int]$delta,[int]$times){
  for($i=0;$i -lt $times;$i++){ [R5]::mouse_event(0x0800,0,0,$delta,[UIntPtr]::Zero); Start-Sleep -Milliseconds 120 }
  Start-Sleep -Milliseconds 250
}
function Shot([string]$name){
  Raise-App
  $bmp = New-Object System.Drawing.Bitmap($script:W,$script:H)
  $g=[System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($script:L,$script:T,0,0,$bmp.Size)
  $bmp.Save((Join-Path $shotDir ($name+'.png')),[System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Output "shot -> $name"
}
# grab the window immediately (no re-activation) - used to catch mid-repaint states
function ShotFast([string]$name){
  $bmp = New-Object System.Drawing.Bitmap($script:W,$script:H)
  $g=[System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($script:L,$script:T,0,0,$bmp.Size)
  $bmp.Save((Join-Path $shotDir ($name+'.png')),[System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Output "shot -> $name"
}

function Launch([string]$exe,[string]$work){
  $env:BANGGANG_SHOW_IN_CAPTURE='1'
  $p=Start-Process $exe -WorkingDirectory $work -PassThru
  Remove-Item Env:\BANGGANG_SHOW_IN_CAPTURE
  Start-Sleep -Milliseconds 2600
  $h=Find-Main $p.Id
  if($h -eq [IntPtr]::Zero){ Write-Output 'no main window'; exit 1 }
  $script:hwnd=$h
  $r=New-Object R5+RECT
  [void][R5]::GetWindowRect($h,[ref]$r)
  $script:L=$r.L; $script:T=$r.T; $script:W=$r.R-$r.L; $script:H=$r.B-$r.T
  Write-Output ("window at {0},{1} {2}x{3}" -f $script:L,$script:T,$script:W,$script:H)
  return $p
}

$exe = Join-Path $dist 'BangGang.exe'
$proc = Launch $exe $dist

# popup geometry: 880x640 card centred in the window
$cw=880; $ch=640
$cardX=$script:L+[int](($script:W-$cw)/2); $cardY=$script:T+[int](($script:H-$ch)/2)
$navY0=$cardY+96+21                 # centre of the first nav item
$fieldX=$cardX+647                  # centre of the dropdown / input column
$rowY1=$cardY+178                   # centre of the first row's right-hand widget
$saveX=$cardX+795; $saveY=$cardY+610
$closeX=$cardX+$cw-30; $closeY=$cardY+26
$gearX=$script:L+232; $gearY=$script:T+203

# ---------- 1) shortcut capture: live display while held, commit on release ----------
Click $gearX $gearY
Start-Sleep -Milliseconds 700
Click ($cardX+694) $rowY1                       # focus the first key box
Start-Sleep -Milliseconds 350
Shot 'r5-1-key-idle'                            # expect the "press a combo" hint

[R5]::keybd_event(0x11,0,0,[UIntPtr]::Zero)      # hold Ctrl
Start-Sleep -Milliseconds 300
Shot 'r5-2-key-ctrl'                            # expect a Ctrl keycap while held

[R5]::keybd_event(0x4B,0,0,[UIntPtr]::Zero)      # hold K as well
Start-Sleep -Milliseconds 300
Shot 'r5-3-key-ctrlk'                           # expect Ctrl + K while both are held

[R5]::keybd_event(0x4B,0,2,[UIntPtr]::Zero)      # release K
Start-Sleep -Milliseconds 250
[R5]::keybd_event(0x11,0,2,[UIntPtr]::Zero)      # release Ctrl -> commit
Start-Sleep -Milliseconds 450
Shot 'r5-4-key-committed'                       # expect the committed Ctrl+K keycaps
Click $saveX $saveY                             # persist, so the new combo lands in settings.json
Start-Sleep -Milliseconds 800
if(Test-Path (Join-Path $dist 'settings.json')){
  $js = Get-Content (Join-Path $dist 'settings.json') -Raw | ConvertFrom-Json
  $sc = $js.Shortcuts | Where-Object { $_.Action -eq 'hide' } | Select-Object -First 1
  Write-Host ("committed shortcut hide = Ctrl:{0} Alt:{1} Shift:{2} Vk:{3}" -f $sc.Ctrl,$sc.Alt,$sc.Shift,$sc.Vk)
  # Ctrl+K: committed only after every key was released, with the modifiers held when K went down
  if($sc.Ctrl -and -not $sc.Alt -and -not $sc.Shift -and $sc.Vk -eq 75){
    Write-Output 'PASS: shortcut committed on release (Ctrl+K)'
  } else { Write-Output 'FAIL: shortcut not committed as Ctrl+K' }
} else { Write-Output 'FAIL: settings.json missing after save' }

# ---------- 2) appearance page: theme segmented control + preset colour dots ----------
Click ($cardX+104) ($navY0+96)
Start-Sleep -Milliseconds 500
Shot 'r5-5-appearance-default'                  # expect the highlight on dot 1, narrow segments

Click ($cardX+556+30*2) ($cardY+232)             # pick preset dot 3 (teal)
Start-Sleep -Milliseconds 700
Shot 'r5-6-dot-teal'                            # expect the highlight moved to teal only

# ---------- 3) provider dropdown: placement, wheel handling, close-on-any-click ----------
$traceFile = Join-Path $dist 'ui-trace.log'
function Trace-Lines([int]$skip = 0){ @(Get-Content $traceFile -Encoding UTF8 | Select-Object -Skip $skip) }
function PageColumn(){
  # fingerprint of the page column left of the popup: used to prove the wheel does
  # not scroll the page behind the open list
  $bmp = New-Object System.Drawing.Bitmap($script:W,$script:H)
  $g=[System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($script:L,$script:T,0,0,$bmp.Size); $g.Dispose()
  $sum=0
  for($y=300;$y -lt 700;$y+=4){ for($x=210;$x -lt 560;$x+=4){ $c=$bmp.GetPixel($x,$y); $sum=($sum*31 + $c.R*7 + $c.G*3 + $c.B) % 2147483647 } }
  $bmp.Dispose()
  return $sum
}
$item0 = $cardY + 223                            # centre of the first list row (list opens under the field)
function Provider([string]$tag){
  Click $saveX $saveY
  Start-Sleep -Milliseconds 800
  $p = (Get-Content (Join-Path $dist 'settings.json') -Raw | ConvertFrom-Json).ChatProvider
  $codes = ([int[]][char[]]$p) -join ','
  Write-Host ("{0}: ChatProvider codes = {1}" -f $tag, $codes)
  return $p
}

Click ($cardX+104) ($navY0+48)                  # models page
Start-Sleep -Milliseconds 500
$t0 = (Trace-Lines).Count
Click $fieldX $rowY1                            # open the provider list
Start-Sleep -Milliseconds 450
Shot 'r5-7-dd-open'
$openLine = (Trace-Lines $t0 | Select-String -Pattern 'dropdown open' | Select-Object -Last 1).Line
Write-Host ("open trace: {0}" -f $openLine)
if($openLine -match 'popup=\((\d+),(\d+),(\d+)x(\d+)\)'){
  $px=[int]$Matches[1]; $py=[int]$Matches[2]
  $fieldBottom = $rowY1 + 21                     # field is 42 tall, centred on the row
  if($py -ge $fieldBottom -and $py -le $fieldBottom + 12 -and [Math]::Abs($px - ($fieldX - 165)) -le 10){
    Write-Output 'PASS: popup is placed under the provider field'
  } else { Write-Output ("FAIL: popup at {0},{1} is not under the field (field bottom {2}, left {3})" -f $px,$py,$fieldBottom,($fieldX-165)) }
}

$before = PageColumn
[void][R5]::SetCursorPos($fieldX, ($item0+150))  # put the cursor over the list
Start-Sleep -Milliseconds 150
Wheel (-120) 4                                   # wheel over the list: the list must consume it
Shot 'r5-8-dd-scrolled'
$after = PageColumn
$wheelLines = (Trace-Lines $t0 | Select-String -Pattern 'list wheel').Count
Write-Host ("wheel events received by the list: {0}" -f $wheelLines)
if($wheelLines -ge 4){ Write-Output 'PASS: wheel events reach the popup list' } else { Write-Output 'FAIL: wheel did not reach the popup list' }
if($before -eq $after){ Write-Output 'PASS: page behind did not scroll while scrolling the list' } else { Write-Output 'FAIL: wheel scrolled the page behind the list' }

ClickSoft $fieldX ($item0+30*2)                  # click slot 3 of the list
ShotFast 'r5-12-switch-instant'                  # grabbed right after the click: rows must already be there
Start-Sleep -Milliseconds 700
Shot 'r5-13-switch-settled'
$pPicked = Provider 'after clicking list slot 3'
$pickedCodes = ([int[]][char[]]$pPicked) -join ','
if($pickedCodes -ne '33258,23450,20041'){      # not the default "custom" provider any more
  Write-Output ("PASS: list item click selected a provider (codes {0})" -f $pickedCodes)
} else { Write-Output 'FAIL: list item click did not change the provider' }

# clicking anywhere else must close the list without changing the provider
$t1 = (Trace-Lines).Count
Click $fieldX $rowY1
Start-Sleep -Milliseconds 450
ClickSoft ($script:L+100) ($script:T+400)        # app sidebar, outside the card and the list
Start-Sleep -Milliseconds 400
ShotFast 'r5-9-dd-closed'                        # expect no popup list left on screen
$pAfterOutside = Provider 'after outside click'
$closeLine = (Trace-Lines $t1 | Select-String -Pattern 'filter click').Line
Write-Host ("outside click trace: {0}" -f ($closeLine -join ' | '))
if($pAfterOutside -eq $pPicked){ Write-Output 'PASS: outside click closed the list without changing the provider' }
else { Write-Output 'FAIL: outside click leaked into the list' }

# ---------- 4) unsaved changes -> confirm card (no shadow) ----------
Click ($cardX+104) ($navY0+96)                   # appearance page (still dirty from the dot)
Start-Sleep -Milliseconds 450
Click $closeX $closeY
Start-Sleep -Milliseconds 600
Shot 'r5-10-confirm'                            # expect a border-only card, radius 10

Click ($cardX+(($cw-400)/2)+24+(400-48-232)/2+52) ($cardY+(($ch-180)/2)+118+17)   # stay editing
Start-Sleep -Milliseconds 400
Get-Process BangGang -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

# ---------- 5) custom accent colour must clear the preset highlight ----------
$custom = [System.Drawing.Color]::FromArgb(255,30,136,229).ToArgb()   # not one of the presets
$json = @{ Accent=$custom; ThemeMode='light'; WindowBorder=$true; Opacity=1.0 } |
        ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Join-Path $dist 'settings.json'), $json, [System.Text.UTF8Encoding]::new($false))

$proc2 = Launch $exe $dist
$cardX=$script:L+[int](($script:W-$cw)/2); $cardY=$script:T+[int](($script:H-$ch)/2)
$navY0=$cardY+96+21
$gearX=$script:L+232; $gearY=$script:T+203
Click $gearX $gearY
Start-Sleep -Milliseconds 700
Click ($cardX+104) ($navY0+96)
Start-Sleep -Milliseconds 500
Shot 'r5-11-custom-accent'                      # expect no highlight ring on any preset dot

Get-Process BangGang -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item (Join-Path $dist 'settings.json') -ErrorAction SilentlyContinue
if(Test-Path (Join-Path $dist 'crash.log')){ Write-Output 'CRASHLOG EXISTS'; Get-Content (Join-Path $dist 'crash.log') -Tail 10 }
Write-Output 'round5-check done'
