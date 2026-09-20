# run-capture-enabled.ps1 -- launch the Debug build for demo screenshots.
#
# The website's demo section needs PAIRS of screenshots of the same screen:
#
#   visible  = what the person at the machine sees (BangGang window present)
#   capture  = what screen capture receives  (the very same desktop, no BangGang)
#
# Normally the window is excluded from capture in every build, so "capture" is
# just an ordinary screenshot. The "visible" frame is the one that needs help:
# CaptureGuard.Disabled is true only in a Debug build with
# BANGGANG_SHOW_IN_CAPTURE=1 (see src/BangGang/Capture/CaptureProtector.cs), and
# Release has no runtime switch for it at all. This script does that env var.
#
# It is a switchboard, not a screenshot tool -- take the screenshots yourself
# with whatever capture tool you like:
#
#   run-capture-enabled.ps1                  -> visible frame (window IS captured)
#   run-capture-enabled.ps1 -Protected       -> capture frame (window is NOT)
#
# Two things that will otherwise waste your afternoon:
#
#   * The app is single-instance. Launching a second copy only brings the running
#     one to the front, and it keeps the capture setting it was STARTED with --
#     so you would take two identical screenshots and blame the app. This script
#     refuses to start while another instance is alive; pass -Force to close it.
#   * The window is CenterScreen at 1200x800 (MainForm.cs), and its position is
#     never persisted, so it lands in exactly the same place on every launch.
#     Keep the wallpaper, the background window, and the clock the same between
#     the two frames and the pair will line up pixel for pixel.
#
# ASCII-only on purpose: PowerShell 5.1 reads a BOM-less script as ANSI, so any
# non-ASCII byte here would be a syntax error.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\run-capture-enabled.ps1
#   powershell -ExecutionPolicy Bypass -File tools\run-capture-enabled.ps1 -Build
#   powershell -ExecutionPolicy Bypass -File tools\run-capture-enabled.ps1 -Protected -Force

param(
    [switch]$Protected,
    [switch]$Build,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'build\bin\Debug\net8.0-windows\BangGang.exe'

if ($Build) {
    Write-Host 'building Debug...'
    & dotnet build (Join-Path $root 'src\BangGang\BangGang.csproj') -c Debug
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
}

if (-not (Test-Path -LiteralPath $exe)) {
    throw "Debug build not found: $exe`n  Build it: dotnet build src\BangGang\BangGang.csproj -c Debug   (or pass -Build)"
}

# ---- refuse to stack onto a running instance -------------------------------
$running = @(Get-Process -Name 'BangGang' -ErrorAction SilentlyContinue)
if ($running.Count) {
    if (-not $Force) {
        throw @"
BangGang is already running (pid $($running[0].Id)).

The app is single-instance: starting it again only brings the existing window to
the front, and that instance keeps whatever capture setting it was STARTED with.
Closing it with the window's own close button is the clean way; or re-run with
-Force to send a WM_CLOSE (not a kill) and wait for it to exit.
"@
    }
    Write-Host "closing the running instance (pid $($running[0].Id))..."
    & taskkill /IM BangGang.exe | Out-Null
    for ($i = 0; $i -lt 50; $i++) {
        if (-not (Get-Process -Name 'BangGang' -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 100
    }
    if (Get-Process -Name 'BangGang' -ErrorAction SilentlyContinue) {
        throw 'the running instance did not exit (it may be showing a dialog) -- close it by hand'
    }
}

# ---- launch -----------------------------------------------------------------
if ($Protected) {
    # Launch with the variable explicitly absent. It is cleared rather than merely
    # not-set, because it would otherwise be inherited from this shell if you had
    # exported it earlier in the session.
    Remove-Item Env:\BANGGANG_SHOW_IN_CAPTURE -ErrorAction SilentlyContinue
    Write-Host ''
    Write-Host 'MODE: capture frame  (BANGGANG_SHOW_IN_CAPTURE unset)'
    Write-Host '  The window is EXCLUDED from capture. A screenshot now gives you the'
    Write-Host '  "what the audience sees" half of the pair.'
} else {
    $env:BANGGANG_SHOW_IN_CAPTURE = '1'
    Write-Host ''
    Write-Host 'MODE: visible frame  (BANGGANG_SHOW_IN_CAPTURE=1)'
    Write-Host '  This is a Debug-only local escape hatch: capture now SHOWS the window.'
    Write-Host '  A screenshot now gives you the "what you see" half of the pair.'
    Write-Host '  Release builds ignore this variable entirely.'
}
Write-Host ''
Write-Host "launching $exe"
Start-Process -FilePath $exe

Write-Host 'done. The window starts hidden -- press Alt+X to show it.'
