# build-installer.ps1 -- publish BangGang self-contained and pack it into a
# per-user Windows installer at dist\installer\.
#
# Pipeline: read the version out of BangGang.csproj -> dotnet publish
# (self-contained win-x64, so the target machine needs no .NET install) ->
# compile installer\BangGang.iss with Inno Setup's command-line compiler.
#
# The Inno Setup toolchain is expected as a portable directory rather than a
# system install (see -FetchToolchain). ISCC.exe is searched in .local\innosetup
# first, then in the usual system install locations.
#
# ASCII-only on purpose: PowerShell 5.1 reads a BOM-less script as ANSI, so any
# non-ASCII byte here would be a syntax error. All Chinese lives in the .iss.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
#   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -FetchToolchain
#   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -SkipPublish

[CmdletBinding()]
param(
    # Download the Inno Setup compiler into .local\innosetup if it is missing.
    [switch]$FetchToolchain,

    # Explicit path to ISCC.exe; overrides the search order.
    [string]$IsccPath,

    # Reuse the existing publish output instead of running dotnet publish.
    [switch]$SkipPublish,

    # Version of the Tools.InnoSetup package to fetch with -FetchToolchain.
    [string]$InnoVersion = '6.7.3'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'src\BangGang\BangGang.csproj'
$mainForm = Join-Path $root 'src\BangGang\App\MainForm.cs'
$iss = Join-Path $root 'installer\BangGang.iss'
$publishDir = Join-Path $root 'build\bin\Release\net8.0-windows\win-x64\publish'
$outDir = Join-Path $root 'dist\installer'

function Find-Iscc {
    param([string]$Explicit, [string]$Root)

    $candidates = New-Object System.Collections.ArrayList
    if ($Explicit) { [void]$candidates.Add($Explicit) }
    [void]$candidates.Add((Join-Path $Root '.local\innosetup\tools\ISCC.exe'))
    [void]$candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))
    if (${env:ProgramFiles(x86)}) {
        [void]$candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'))
    }
    [void]$candidates.Add((Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))

    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    return $null
}

function Get-Iscc {
    param([string]$Explicit, [string]$Root, [string]$Version)

    $found = Find-Iscc -Explicit $Explicit -Root $Root
    if ($found) { return $found }

    if (-not $Script:FetchToolchain) {
        throw @"
Inno Setup compiler (ISCC.exe) not found.

Either install Inno Setup 6, or fetch the portable compiler into .local\innosetup:

  powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -FetchToolchain

(that downloads the Tools.InnoSetup NuGet package -- the official binaries,
repackaged -- and unpacks it; nothing is installed system-wide)
"@
    }

    Write-Host "fetching Inno Setup $Version into .local\innosetup ..."
    $dlDir = Join-Path $Root '.local\dl'
    $dest = Join-Path $Root '.local\innosetup'
    New-Item -ItemType Directory -Force -Path $dlDir | Out-Null
    $nupkg = Join-Path $dlDir "tools.innosetup.$Version.nupkg"
    $url = "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$Version/tools.innosetup.$Version.nupkg"
    Invoke-WebRequest -Uri $url -OutFile $nupkg -UseBasicParsing

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $dest) { Remove-Item -Recurse -Force -LiteralPath $dest }
    [System.IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $dest)

    $found = Find-Iscc -Explicit $Explicit -Root $Root
    if (-not $found) { throw "fetched the package but ISCC.exe is still not where expected: $dest" }
    return $found
}

# ---- 1. version -----------------------------------------------------------
# BangGang.csproj <Version> is the source of truth; MainForm.AppVersion is the
# string the UI shows. They are edited by hand in two places (see CLAUDE.md), so
# a mismatch means the shipped installer would be labelled with a version the
# app does not report -- fail loudly instead.
[xml]$proj = Get-Content -LiteralPath $csproj
$version = $proj.Project.PropertyGroup |
    ForEach-Object { $_.Version } |
    Where-Object { $_ } |
    Select-Object -First 1
if (-not $version) { throw "no <Version> found in $csproj" }

$m = Select-String -LiteralPath $mainForm -Pattern 'AppVersion\s*=\s*"v([0-9][0-9.]*)"'
if (-not $m) { throw "could not read AppVersion out of $mainForm" }
$appVersion = $m.Matches[0].Groups[1].Value

if ($appVersion -ne $version) {
    throw @"
version mismatch -- refusing to build a mislabelled installer:
  BangGang.csproj  <Version>    = $version
  MainForm.cs      AppVersion   = v$appVersion
Bump both (CLAUDE.md "release checklist", step 1).
"@
}
Write-Host "version $version (csproj and MainForm agree)"

# ---- 2. the .iss must carry a UTF-8 BOM -----------------------------------
# Inno 6 falls back to ANSI when the BOM is missing, which turns every Chinese
# string in the script into mojibake -- silently, at compile time.
$head = [System.IO.File]::ReadAllBytes($iss)
if ($head.Length -lt 3 -or $head[0] -ne 0xEF -or $head[1] -ne 0xBB -or $head[2] -ne 0xBF) {
    throw "$iss is missing its UTF-8 BOM; re-save it as UTF-8 with BOM."
}

# ---- 3. publish -----------------------------------------------------------
if (-not $SkipPublish) {
    Write-Host 'dotnet publish (self-contained win-x64) ...'
    & dotnet publish $csproj -c Release -r win-x64 --self-contained true `
        -p:RestoreSources=https://api.nuget.org/v3/index.json
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}
else {
    Write-Host 'skipping publish (-SkipPublish)'
}

if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'BangGang.exe'))) {
    throw "publish output not found at $publishDir -- run without -SkipPublish"
}

# ---- 4. compile the installer --------------------------------------------
$iscc = Get-Iscc -Explicit $IsccPath -Root $root -Version $InnoVersion
Write-Host "using ISCC: $iscc"

& $iscc "/DMyAppVersion=$version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setup = Join-Path $outDir "BangGang-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "expected installer not found: $setup" }

$fi = Get-Item -LiteralPath $setup
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
Write-Host ''
Write-Host ("installer : {0}" -f $fi.FullName)
Write-Host ("size      : {0:N1} MB" -f ($fi.Length / 1MB))
Write-Host ("sha256    : {0}" -f $hash)
