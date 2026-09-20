# build-installer.ps1 -- publish BangGang and pack it into per-user Windows
# installers at dist\installer\.
#
# Two flavours come out of the same installer\BangGang.iss:
#
#   with-runtime     self-contained publish. Ships the .NET 8 desktop runtime,
#                    so the target machine needs nothing preinstalled. ~49 MB.
#   without-runtime  framework-dependent publish. ~2 MB, but the target machine
#                    must already have the .NET 8 Desktop Runtime -- the
#                    installer checks for it and says so before doing anything.
#
# Both share one AppId, so they upgrade over each other in place.
#
# ASCII-only on purpose: PowerShell 5.1 reads a BOM-less script as ANSI, so any
# non-ASCII byte here would be a syntax error. All Chinese lives in the .iss.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
#   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -Flavor SelfContained
#   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -FetchToolchain

[CmdletBinding()]
param(
    # Which package(s) to produce.
    [ValidateSet('Both', 'SelfContained', 'FrameworkDependent')]
    [string]$Flavor = 'Both',

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
$outDir = Join-Path $root 'dist\installer'

# Publish directory names are duplicated in installer\BangGang.iss (it derives
# them from whether SelfContained is defined rather than taking a path over /D,
# because a Windows path in a /D value needs quoting to survive ISPP's
# expression parser). Rename one, rename the other.
$flavors = [ordered]@{
    'SelfContained'      = @{ Dir = 'selfcontained';      Suffix = 'with-runtime' }
    'FrameworkDependent' = @{ Dir = 'frameworkdependent'; Suffix = 'without-runtime' }
}

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

# ---- 3. build each flavour ------------------------------------------------
$iscc = Get-Iscc -Explicit $IsccPath -Root $root -Version $InnoVersion
Write-Host "using ISCC: $iscc"

$wanted = if ($Flavor -eq 'Both') { @($flavors.Keys) } else { @($Flavor) }
$built = New-Object System.Collections.ArrayList

foreach ($name in $wanted) {
    $f = $flavors[$name]
    $selfContained = ($name -eq 'SelfContained')
    $publishDir = Join-Path $root ("build\publish\" + $f.Dir)

    Write-Host ''
    Write-Host "=== $name -> $publishDir ==="

    if (-not $SkipPublish) {
        # Wipe first: dotnet publish leaves files from an earlier publish in
        # place, and stale ones would be packaged into the installer.
        if (Test-Path -LiteralPath $publishDir) {
            Remove-Item -LiteralPath $publishDir -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

        $publishArgs = @(
            'publish', $csproj,
            '-c', 'Release',
            '-r', 'win-x64',
            '--self-contained', $(if ($selfContained) { 'true' } else { 'false' }),
            '-o', $publishDir
        )
        if ($selfContained) {
            # Self-contained is the only flavour that needs anything from NuGet
            # (the win-x64 runtime packs). This machine has no package sources
            # configured, so name one explicitly; framework-dependent needs no
            # packages at all and is left to resolve offline.
            $publishArgs += '-p:RestoreSources=https://api.nuget.org/v3/index.json'
        }

        Write-Host ("dotnet publish (self-contained={0}) ..." -f $selfContained)
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    }
    else {
        Write-Host 'skipping publish (-SkipPublish)'
    }

    $exe = Join-Path $publishDir 'BangGang.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "publish produced no BangGang.exe in $publishDir -- run without -SkipPublish"
    }

    # The .iss branches on whether SelfContained is *defined*, not on its value:
    # ISPP's #if does not treat an integer 0 as false, so /DSelfContained=0 makes
    # both flavours compile to the same package. Pass the define for the
    # self-contained build only, and leave it undefined otherwise.
    $isccArgs = @("/DMyAppVersion=$version")
    if ($selfContained) { $isccArgs += '/DSelfContained=1' }
    $isccArgs += $iss
    & $iscc @isccArgs
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

    $setup = Join-Path $outDir "BangGang-Setup-$version-$($f.Suffix).exe"
    if (-not (Test-Path -LiteralPath $setup)) { throw "expected installer not found: $setup" }
    [void]$built.Add($setup)
}

# ---- 4. report ------------------------------------------------------------
Write-Host ''
foreach ($setup in $built) {
    $fi = Get-Item -LiteralPath $setup
    $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    Write-Host ("installer : {0}" -f $fi.FullName)
    Write-Host ("            {0:N1} MB   sha256 {1}" -f ($fi.Length / 1MB), $hash)
}
