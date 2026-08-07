<#
.SYNOPSIS
    Publishes Pomtom Recorder as a single portable executable.

.DESCRIPTION
    Produces publish\Recorder.exe: self-contained (no .NET install needed on the target machine),
    single-file, with ffmpeg embedded. Copy that one file anywhere and run it.

.PARAMETER SkipFetch
    Skip the ffmpeg download check. Only useful when you know assets\ffmpeg.exe is already present.

.EXAMPLE
    .\build.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipFetch,
    [switch]$OpenOutput
)

$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$project = Join-Path $root 'src\Recorder\Recorder.csproj'
$outDir  = Join-Path $root 'publish'
$assets  = Join-Path $root 'src\Recorder\assets'

# ffmpeg is embedded into the exe, so it has to exist before the build.
if (-not $SkipFetch) {
    if (-not (Test-Path (Join-Path $assets 'ffmpeg.exe.gz'))) {
        Write-Host "ffmpeg.exe.gz not found; fetching it..." -ForegroundColor Yellow
        & (Join-Path $root 'tools\fetch-ffmpeg.ps1')
    }
    if (-not (Test-Path (Join-Path $assets 'app.ico'))) {
        Write-Host "app.ico not found; generating it..." -ForegroundColor Yellow
        & (Join-Path $root 'tools\make-icon.ps1')
    }
}

if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }

Write-Host "`nPublishing..." -ForegroundColor Cyan
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none `
    -o $outDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$exe = Join-Path $outDir 'Recorder.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe to exist after publish." }

# A single-file publish should leave exactly one file behind; flag anything else so a stray
# native dll that failed to embed does not silently break portability.
$extras = Get-ChildItem $outDir -File | Where-Object { $_.Name -ne 'Recorder.exe' }

Write-Host ""
Write-Host ("Built {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green
if ($extras) {
    Write-Host "Additional files alongside the exe:" -ForegroundColor Yellow
    $extras | ForEach-Object { "  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB) }
}

if ($OpenOutput) { Start-Process explorer.exe $outDir }
