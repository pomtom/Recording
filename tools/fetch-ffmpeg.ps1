<#
.SYNOPSIS
    One-time fetch of ffmpeg.exe into src\Recorder\assets\ so it can be embedded in the published exe.

.DESCRIPTION
    Downloads the "essentials" static build from gyan.dev, extracts ONLY bin\ffmpeg.exe, and
    writes it to src\Recorder\assets\ffmpeg.exe. The asset is gitignored; run this once after
    cloning. Recorder.csproj embeds whatever it finds there.

.NOTES
    Skips the download when the asset already exists unless -Force is passed.
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$assetsDir = Join-Path $repoRoot 'src\Recorder\assets'
$target    = Join-Path $assetsDir 'ffmpeg.exe'
$gzTarget  = Join-Path $assetsDir 'ffmpeg.exe.gz'

if ((Test-Path $gzTarget) -and -not $Force) {
    Write-Host "ffmpeg.exe.gz already present at $gzTarget"
    Write-Host ("Size: {0:N1} MB" -f ((Get-Item $gzTarget).Length / 1MB))
    Write-Host "Pass -Force to re-download."
    return
}

New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

$url  = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("ffmpeg-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temp | Out-Null
$zip = Join-Path $temp 'ffmpeg.zip'

try {
    Write-Host "Downloading $url ..."
    $progressPreferenceOld = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'   # order-of-magnitude faster for large files
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $ProgressPreference = $progressPreferenceOld

    Write-Host "Extracting ffmpeg.exe ..."
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -like '*/bin/ffmpeg.exe' } | Select-Object -First 1
        if (-not $entry) { throw "ffmpeg.exe not found inside the archive." }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
    finally {
        $archive.Dispose()
    }

    # Gzip it for embedding. The single-file bundle is published *uncompressed* so that it stays
    # memory-mapped rather than being decompressed into RAM at every launch; compressing just this
    # one payload ourselves reclaims most of the file size without that cost. The provisioner
    # inflates it once, streaming, on first run.
    Write-Host "Compressing for embedding ..."
    $inStream  = [System.IO.File]::OpenRead($target)
    $outStream = [System.IO.File]::Create($gzTarget)
    $gzip      = New-Object System.IO.Compression.GZipStream($outStream, [System.IO.Compression.CompressionLevel]::Optimal)
    try   { $inStream.CopyTo($gzip) }
    finally { $gzip.Dispose(); $outStream.Dispose(); $inStream.Dispose() }

    Remove-Item $target -Force

    $hash = (Get-FileHash $gzTarget -Algorithm SHA256).Hash
    Write-Host ""
    Write-Host ("Wrote $gzTarget ({0:N1} MB compressed)" -f ((Get-Item $gzTarget).Length / 1MB))
    Write-Host "SHA-256: $hash"
}
finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}
