# Proves the anti-capture requirement with an A/B test:
#
#   behind the app we place a solid magenta window (NOT topmost, so the app's
#   topmost window sits above it). We then grab the screen:
#
#     Debug build + BANGGANG_SHOW_IN_CAPTURE=1  (protection off, positive control)
#        -> magenta is hidden by the app, app colours are captured
#     Release build (protection on, debug switch ignored)
#        -> magenta shows through, ZERO app pixels, and NOT black
#
# Any pixels of the app in the Release grab means the protection failed; a black
# rectangle means WDA_MONITOR-style blacking instead of full transparency.
param(
    [string]$Release = 'D:\project\bang-bang\dist\BangGang.exe',
    [string]$DebugExe = 'D:\project\bang-bang\dist\ui-preview\BangGang.exe',
    [int]$Magenta = 1000, [int]$MagX = 400, [int]$MagY = 180, [int]$MagH = 700
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class AB {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(IntPtr h, out uint a);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  public struct RECT{ public int L,T,R,B; }
}
"@
$probeScript = Join-Path $env:TEMP 'banggang-magenta-backdrop.ps1'
@"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
`$f = New-Object System.Windows.Forms.Form
`$f.FormBorderStyle = 'None'
`$f.StartPosition = 'Manual'
`$f.Bounds = New-Object System.Drawing.Rectangle($MagX,$MagY,$Magenta,$MagH)
`$f.BackColor = [System.Drawing.Color]::Magenta
`$f.TopMost = `$false
`$t = New-Object System.Windows.Forms.Timer
`$t.Interval = 60000
`$t.Add_Tick({ `$f.Close() })
`$t.Start()
[System.Windows.Forms.Application]::Run(`$f)
"@ | Set-Content -Path $probeScript -Encoding UTF8

function Grab([int]$x,[int]$y,[int]$w,[int]$h){
  $b=New-Object System.Drawing.Bitmap($w,$h)
  $g=[System.Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($x,$y,0,0,$b.Size); $g.Dispose(); return $b
}
function Count($bmp,$w,$h){
  $mag=0;$accent=0;$black=0;$n=0
  for($y=0;$y -lt $h;$y+=2){ for($x=0;$x -lt $w;$x+=2){
    $n++; $c=$bmp.GetPixel($x,$y)
    if($c.R -gt 240 -and $c.G -lt 20 -and $c.B -gt 240){ $mag++ }
    if([math]::Abs($c.R-47) -le 6 -and [math]::Abs($c.G-112) -le 6 -and [math]::Abs($c.B-224) -le 6){ $accent++ }
    if($c.R -lt 12 -and $c.G -lt 12 -and $c.B -lt 12){ $black++ }
  }}
  return [pscustomobject]@{ Total=$n; Magenta=$mag; Accent=$accent; Black=$black }
}

$backdrop = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') `
    -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',$probeScript -PassThru
Start-Sleep -Milliseconds 2500

function Run-Case([string]$name,[string]$exe,[bool]$useSwitch){
  Get-Process BangGang -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 1200
  if(-not (Test-Path $exe)){ Write-Host "$name : missing $exe"; return $null }
  if($useSwitch){ $env:BANGGANG_SHOW_IN_CAPTURE='1' }
  $p=Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
  Remove-Item Env:\BANGGANG_SHOW_IN_CAPTURE -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 3000
  $script:m=[IntPtr]::Zero
  $cb=[AB+EnumProc]{ param($h,$l) $pp=0; [AB]::GetWindowThreadProcessId($h,[ref]$pp)|Out-Null
    if($pp -eq $p.Id){ $t=New-Object System.Text.StringBuilder 120; [AB]::GetWindowText($h,$t,120)|Out-Null
      if($t.ToString().Length -gt 0 -and [AB]::IsWindowVisible($h)){ $script:m=$h; return $false } }
    return $true }
  [AB]::EnumWindows($cb,[IntPtr]::Zero)|Out-Null
  if($script:m -eq [IntPtr]::Zero){ Write-Host "$name : no window"; return $null }
  $aff=0; [void][AB]::GetWindowDisplayAffinity($script:m,[ref]$aff)
  # 打开设置浮窗（同时会挂上浅色遮罩），保证“浮窗 + 遮罩”也在被验证的范围里
  $wr=New-Object AB+RECT; [void][AB]::GetWindowRect($script:m,[ref]$wr)
  [void][AB]::SetCursorPos($wr.L+232, $wr.T+203); Start-Sleep -Milliseconds 200
  [AB]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 90
  [AB]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 900
  [void][AB]::SetCursorPos(20,1060); Start-Sleep -Milliseconds 400
  $w=$Magenta; $h=$MagH
  $bmp = Grab $MagX $MagY $w $h
  $st = Count $bmp $w $h
  $bmp.Save((Join-Path 'D:\project\bang-bang\dist\shots' ("capture-ab-{0}.png" -f $name)),[System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Get-Process BangGang -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 1200
  $st | Add-Member -NotePropertyName Affinity -NotePropertyValue $aff
  Write-Host ("{0,-10} affinity=0x{1:X}  samples={2}  magenta(backdrop showing through)={3}  app-accent-blue={4}  near-black={5}" -f `
      $name,$aff,$st.Total,$st.Magenta,$st.Accent,$st.Black)
  return $st
}

Write-Output '=== capture A/B (magenta backdrop placed behind the app) ==='
$dbg = Run-Case 'debug'   $DebugExe $true
$rel = Run-Case 'release' $Release  $false
try { Stop-Process -Id $backdrop.Id -Force -ErrorAction SilentlyContinue } catch { }

Write-Output ''
if($dbg -and $dbg.Magenta -lt ($dbg.Total*0.05) -and $dbg.Accent -gt 200){
  Write-Output 'PASS  positive control: protection off -> the app covers the backdrop and is captured'
} else { Write-Output 'FAIL  positive control: the app was not captured even with protection off (test setup problem)' }
if($rel -and $rel.Accent -eq 0){
  Write-Output 'PASS  release: not a single app pixel in the grab (fully excluded from capture)'
} else { Write-Output 'FAIL  release: app pixels found in the grab' }
if($rel -and $dbg -and $rel.Magenta -gt ($dbg.Magenta + $rel.Total*0.2)){
  Write-Output 'PASS  the desktop behind shows through the window area (fully transparent, not a black box)'
} else { Write-Output 'FAIL  the window area was not transparent' }
if($rel -and $rel.Black -lt ($rel.Total*0.02)){
  Write-Output 'PASS  no blacked-out region (near-black pixels below 2%)'
} else { Write-Output 'FAIL  looks like a blacked-out region' }
