<#
.SYNOPSIS
    Generates src\Recorder\assets\app.ico (a red record dot) so the exe and tray have an icon
    without checking a binary into the repo.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot  = Split-Path -Parent $PSScriptRoot
$assetsDir = Join-Path $repoRoot 'src\Recorder\assets'
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
$target = Join-Path $assetsDir 'app.ico'

# Render each size as a PNG; a Vista-era .ico may embed PNG payloads directly.
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @{}

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # NOTE: PowerShell's comma operator binds tighter than arithmetic, so every argument
    # below is precomputed into a variable rather than written inline.

    # Dark rounded backdrop so the dot stays visible on light taskbars.
    $pad = [Math]::Max(1, [int]($s * 0.06))
    $backSize = $s - (2 * $pad)
    $backBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 32, 34, 38))
    $g.FillEllipse($backBrush, [float]$pad, [float]$pad, [float]$backSize, [float]$backSize)

    # Record dot.
    $inset = [int]($s * 0.28)
    $dotSize = $s - (2 * $inset)
    $dotBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 232, 62, 62))
    $g.FillEllipse($dotBrush, [float]$inset, [float]$inset, [float]$dotSize, [float]$dotSize)

    $dotBrush.Dispose(); $backBrush.Dispose(); $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$s] = $ms.ToArray()
    $ms.Dispose(); $bmp.Dispose()
}

# ICONDIR + ICONDIRENTRY[] + payloads
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0)              # reserved
$w.Write([uint16]1)              # type = icon
$w.Write([uint16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
foreach ($s in $sizes) {
    $data = $pngs[$s]
    $dim = if ($s -ge 256) { [byte]0 } else { [byte]$s }     # 0 means 256 in an ICONDIRENTRY
    $w.Write($dim)           # width
    $w.Write($dim)           # height
    $w.Write([byte]0)            # palette count
    $w.Write([byte]0)            # reserved
    $w.Write([uint16]1)          # colour planes
    $w.Write([uint16]32)         # bits per pixel
    $w.Write([uint32]$data.Length)
    $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $w.Write($pngs[$s]) }

$w.Flush()
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
$w.Dispose(); $out.Dispose()

Write-Host "Wrote $target ($((Get-Item $target).Length) bytes, $($sizes.Count) sizes)"
