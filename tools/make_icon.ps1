# Generates assets\app.ico (16-256 px, PNG-compressed), assets\app.png (256 px), assets\app-1024.png (source of the
# macOS .icns, built by macos/build.sh) and assets\social-preview.png.
# The logo is only the pie chart shaped like Pac-Man facing left, on a transparent background (no tile): one circle
# split by colour into an upper-left section, a right half and a lower-left section, mouth on the left.
# Drawn at 4x and downscaled.
#
# The logo colours are NOT set here: they are read from the LOGO_* lines in src\Theme.cs (the single place to edit
# them), so the exe icon, the README logo, the social preview and the in-app logo always match.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Get-ThemeColor([string]$name) {
    $theme = Get-Content (Join-Path $PSScriptRoot '..\src\Theme.cs') -Raw
    $m = [regex]::Match($theme, "\b$name\s*=\s*Hex\(0x([0-9a-fA-F]{6})\)")
    if (-not $m.Success) { throw "Colour $name not found in src\Theme.cs" }
    return [System.Drawing.ColorTranslator]::FromHtml('#' + $m.Groups[1].Value)
}

$LOGO_TOP_LEFT = Get-ThemeColor 'LOGO_TOP_LEFT'
$LOGO_RIGHT = Get-ThemeColor 'LOGO_RIGHT'
$LOGO_BOTTOM_LEFT = Get-ThemeColor 'LOGO_BOTTOM_LEFT'
$ACCENT = Get-ThemeColor 'ACCENT'
$FG = Get-ThemeColor 'FG'
$BG = Get-ThemeColor 'BG'

# Draws the logo pie into a square of side $n at ($x, $y) on graphics $g.
function Draw-Logo($g, [float]$x, [float]$y, [float]$n) {
    $R = 0.47 * $n                                # nearly the full canvas, small margin for anti-aliasing
    $cx = $x + 0.5 * $n; $cy = $y + 0.5 * $n
    $mouth = 30                                   # half-angle of the mouth, degrees
    $under = 6                                    # left sections run under the right half: no seam between colours
    $L = $cx - $R; $T = $cy - $R; $D = 2 * $R
    $g.FillPie((New-Object System.Drawing.SolidBrush $LOGO_TOP_LEFT), $L, $T, $D, $D, 180 + $mouth, 90 - $mouth + $under)
    $g.FillPie((New-Object System.Drawing.SolidBrush $LOGO_BOTTOM_LEFT), $L, $T, $D, $D, 90 - $under, 90 - $mouth + $under)
    $g.FillPie((New-Object System.Drawing.SolidBrush $LOGO_RIGHT), $L, $T, $D, $D, -90, 180)
}
function New-IconBitmap([int]$size) {
    $n = 4 * $size
    $big = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Logo $g 0 0 $n
    $g.Dispose()

    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($big, 0, 0, $size, $size)
    $g.Dispose(); $big.Dispose()
    return $bmp
}
function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

$assets = Join-Path $PSScriptRoot '..\assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    if ($s -eq 256) { $bmp.Save((Join-Path $assets 'app.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $pngs += , (Get-PngBytes $bmp)
    $bmp.Dispose()
}

$big = New-IconBitmap 1024
$big.Save((Join-Path $assets 'app-1024.png'), [System.Drawing.Imaging.ImageFormat]::Png); $big.Dispose()

$out = Join-Path $assets 'app.ico'
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Host "Wrote $out"

# Social preview card (GitHub repo settings > Social preview, 1280x640): logo, name and tagline on the bg colour.
$MUTED = Get-ThemeColor 'MUTED'
$SURFACE = Get-ThemeColor 'SURFACE'
$card = New-Object System.Drawing.Bitmap 1280, 640
$g = [System.Drawing.Graphics]::FromImage($card)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear($BG)
$g.DrawRectangle((New-Object System.Drawing.Pen $SURFACE, 4), 2, 2, 1275, 635)
$logo = New-IconBitmap 288
$g.DrawImage($logo, 96, 176, 288, 288); $logo.Dispose()
$fmt = New-Object System.Drawing.StringFormat; $fmt.Trimming = 'None'
$g.DrawString('Disk Space Analyzer', (New-Object System.Drawing.Font 'Segoe UI', 54, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)), (New-Object System.Drawing.SolidBrush $FG), 440, 214, $fmt)
$g.DrawString('Fast disk space analyzer and cleaner for Windows', (New-Object System.Drawing.Font 'Segoe UI', 30, ([System.Drawing.GraphicsUnit]::Pixel)), (New-Object System.Drawing.SolidBrush $MUTED), 444, 298, $fmt)
$g.DrawString('Free and open source  --  one small .exe, nothing to install', (New-Object System.Drawing.Font 'Segoe UI', 24, ([System.Drawing.GraphicsUnit]::Pixel)), (New-Object System.Drawing.SolidBrush $MUTED), 446, 352, $fmt)
$g.FillRectangle((New-Object System.Drawing.SolidBrush $ACCENT), 446, 410, 120, 6)
$g.Dispose()
$card.Save((Join-Path $assets 'social-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png); $card.Dispose()
Write-Host "Wrote $(Join-Path $assets 'social-preview.png')"
