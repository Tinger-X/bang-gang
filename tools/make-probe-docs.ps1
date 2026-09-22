# Zip the OOXML probe sources into real .docx / .xlsx / .pptx files.
#
# Pure ASCII on purpose: PowerShell 5.1 reads a BOM-less script as ANSI, so any
# Chinese literal in here becomes a syntax error (see CLAUDE.md). The XML that
# carries Chinese lives in probe-data\src\ as real UTF-8 files instead.
#
#   powershell -ExecutionPolicy Bypass -File tools\make-probe-docs.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$src  = Join-Path $root 'probe-data\src'
$out  = Join-Path $root 'probe-data'

Add-Type -AssemblyName System.IO.Compression.FileSystem

$map = @{
    'docx' = 'report.docx'
    'xlsx' = 'sheet.xlsx'
    'pptx' = 'deck.pptx'
}

foreach ($k in $map.Keys) {
    $dir = Join-Path $src $k
    if (-not (Test-Path $dir)) { throw "missing source dir: $dir" }
    $file = Join-Path $out $map[$k]
    if (Test-Path $file) { Remove-Item $file -Force }

    # Build the entries by hand rather than ZipFile::CreateFromDirectory: that
    # API writes BACKSLASH separators on .NET Framework, while real OOXML
    # packages always use forward slashes. (The readers tolerate both, but the
    # fixtures should look like files that actually exist in the wild.)
    $zip = [System.IO.Compression.ZipFile]::Open($file, 'Create')
    try {
        $baseLen = $dir.TrimEnd('\').Length + 1
        foreach ($f in Get-ChildItem $dir -Recurse -File) {
            $rel = $f.FullName.Substring($baseLen).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $f.FullName, $rel,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $zip.Dispose() }

    $len = (Get-Item $file).Length
    Write-Host ("{0}  {1} bytes" -f $map[$k], $len)
}

# The date serials used in sheet.xlsx, printed so the expected values are
# checkable against what the reader prints.
Write-Host ""
Write-Host ("B2 serial 45366 -> {0}" -f [DateTime]::FromOADate(45366).ToString('yyyy-MM-dd'))
Write-Host ("B3 serial 45400 -> {0}" -f [DateTime]::FromOADate(45400).ToString('yyyy-MM-dd'))
