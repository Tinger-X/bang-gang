# publish-artifacts.ps1 -- upload the built installers to the shared R2 bucket.
#
# Why this is a separate step from tools\build-installer.ps1: the website serves
# downloads from R2 (key <app>/<variant>.exe), never as a static asset. That is
# exactly what lets /download count. Building installers and publishing them are
# therefore two different concerns, and only this one needs wrangler and network
# access -- the app's build script stays offline and untouched.
#
# Idempotent: re-running overwrites the same keys, so the site's version label
# and its download files always move together.
#
# Single source of truth: the app name, extension, filename prefix and the list
# of variants are read out of web/functions/_lib/site.js by asking node to import
# it. Nothing here is a second copy, so the keys written here cannot drift from
# the keys the site reads back.
#
# The filename put into Content-Disposition is what the browser saves AND what
# /api/stats parses the version out of, so it must keep the shape
# <prefix><version>[-<variant id>].<ext>.
#
# ASCII-only on purpose: PowerShell 5.1 reads a BOM-less script as ANSI, so any
# non-ASCII byte here would be a syntax error.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File web\tools\publish-artifacts.ps1
#   powershell -ExecutionPolicy Bypass -File web\tools\publish-artifacts.ps1 -DryRun

param(
    [string]$Version,
    [string]$InstallerDir,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # web\tools -> web -> repo root
$siteJs = Join-Path $root 'web\functions\_lib\site.js'
if (-not $InstallerDir) { $InstallerDir = Join-Path $root 'dist\installer' }

if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'node not found on PATH' }
if (-not (Get-Command wrangler -ErrorAction SilentlyContinue)) { throw 'wrangler not found on PATH (npm install -g wrangler)' }
if (-not (Test-Path -LiteralPath $siteJs)) { throw "site config not found: $siteJs" }

# ---- read the site config (single source of truth) -------------------------
$siteUri = 'file:///' + ($siteJs -replace '\\', '/')
$json = & node --input-type=module -e "const m = await import(process.argv[1]); console.log(JSON.stringify({app:m.SITE.app, ext:m.SITE.ext, contentType:m.SITE.contentType, namePrefix:m.SITE.namePrefix, ids:m.SITE.variants.map(v=>v.id)}))" $siteUri
if ($LASTEXITCODE -ne 0) { throw "failed to read $siteJs" }
$site = $json | ConvertFrom-Json
Write-Host ("site: app={0} ext={1} variants={2}" -f $site.app, $site.ext, ($site.ids -join ', '))

# ---- read the version ------------------------------------------------------
if (-not $Version) {
    $csproj = Join-Path $root 'src\BangGang\BangGang.csproj'
    $m = Select-String -LiteralPath $csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
    if (-not $m) { throw "no <Version> in $csproj" }
    $Version = $m.Matches[0].Groups[1].Value.Trim()
}
Write-Host ("version: {0}" -f $Version)

if (-not (Test-Path -LiteralPath $InstallerDir)) { throw "installer directory not found: $InstallerDir" }

# ---- upload each variant ---------------------------------------------------
$uploaded = New-Object System.Collections.ArrayList

foreach ($id in $site.ids) {
    $fileName = '{0}{1}-{2}.{3}' -f $site.namePrefix, $Version, $id, $site.ext
    $localPath = Join-Path $InstallerDir $fileName
    if (-not (Test-Path -LiteralPath $localPath)) {
        throw "missing installer: $localPath`n  Build it first: powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1"
    }

    $key = '{0}/{1}.{2}' -f $site.app, $id, $site.ext
    $disposition = 'attachment; filename="{0}"' -f $fileName
    $sizeMb = [math]::Round((Get-Item -LiteralPath $localPath).Length / 1MB, 2)

    Write-Host ''
    Write-Host ("-> r2://softwares/{0}   ({1} MB)" -f $key, $sizeMb)
    Write-Host ("   from  {0}" -f $localPath)
    Write-Host ("   as    {0}" -f $fileName)

    if ($DryRun) {
        Write-Host '   [dry run] skipped'
    } else {
        # Not $args: that is an automatic variable and assigning to it is asking
        # for trouble. Build the argv array and splat it.
        $wranglerArgs = @(
            'r2', 'object', 'put', "softwares/$key",
            '--file', $localPath,
            '--remote',
            '--content-type', $site.contentType,
            '--content-disposition', $disposition
        )
        & wrangler @wranglerArgs
        if ($LASTEXITCODE -ne 0) { throw "wrangler r2 object put failed for $key (exit $LASTEXITCODE)" }
    }

    $uploaded.Add([pscustomobject]@{
        Variant = $id
        Key     = $key
        File    = $fileName
        Sha256  = (Get-FileHash -LiteralPath $localPath -Algorithm SHA256).Hash
    }) | Out-Null
}

# ---- summary ---------------------------------------------------------------
Write-Host ''
Write-Host '=== published ==='
foreach ($u in $uploaded) {
    Write-Host ("{0,-18} {1}" -f $u.Variant, $u.File)
    Write-Host ("{0,-18} sha256 {1}" -f '', $u.Sha256)
}

# ---- integrity manifest ----------------------------------------------------
#
# The app verifies a downloaded installer against this before running it, and
# /api/stats serves it back. It gets a file of its own rather than riding along
# in the R2 object metadata because:
#   * the one metadata field this script can set (Content-Disposition) is the
#     downloaded file's name -- abusing it to carry a hash would show up in the
#     user's Save dialog;
#   * the bundled wrangler (4.124) has no --custom-metadata flag, and depending
#     on an undocumented one is a trap for whoever upgrades it next.
#
# Written without a BOM: the Worker JSON.parses this, and a leading BOM would
# make that throw (PS 5.1's -Encoding utf8 always writes one).
if (-not $DryRun) {
    $manifest = [ordered]@{}
    foreach ($u in $uploaded) { $manifest[$u.Variant] = $u.Sha256 }
    $manifestJson = ($manifest | ConvertTo-Json -Compress)

    $manifestFile = Join-Path $env:TEMP ('bg-sha256-' + $Version + '.json')
    [System.IO.File]::WriteAllText($manifestFile, $manifestJson, (New-Object System.Text.UTF8Encoding $false))
    try {
        $manifestKey = '{0}/sha256.json' -f $site.app
        Write-Host ''
        Write-Host ("-> r2://softwares/{0}" -f $manifestKey)
        Write-Host ("   {0}" -f $manifestJson)
        & wrangler r2 object put "softwares/$manifestKey" --file $manifestFile --remote --content-type application/json
        if ($LASTEXITCODE -ne 0) { throw "wrangler r2 object put failed for $manifestKey (exit $LASTEXITCODE)" }
    }
    finally { Remove-Item -LiteralPath $manifestFile -Force -ErrorAction SilentlyContinue }
} else {
    Write-Host ''
    Write-Host ("[dry run] would write the sha256 manifest: {0}" -f (($uploaded | ForEach-Object { $_.Variant + '=' + $_.Sha256 }) -join ' '))
}

if ($DryRun) {
    Write-Host ''
    Write-Host 'DRY RUN: nothing was uploaded.'
} else {
    Write-Host ''
    Write-Host 'The site picks these up immediately: /api/stats reads the version and'
    Write-Host 'filename from the R2 object metadata, and /download streams the payload.'
}
