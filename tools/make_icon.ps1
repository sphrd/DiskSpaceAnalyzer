# Generates assets\app.ico (16-256 px, PNG-compressed) and assets\app.png (256 px) following the
# design language: ACCENT rounded tile (radius 22 %) with a pie chart shaped like Pac-Man facing left: FG half disc on the
# right, two smaller sections on the left (ACCENT_LIGHT above, ACCENT_MID below). Drawn at 4x and downscaled.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$ACCENT = [System.Drawing.ColorTranslator]::FromHtml('#4da3ff')
$ACCENT_LIGHT = [System.Drawing.ColorTranslator]::FromHtml('#cfe4ff')
$ACCENT_MID = [System.Drawing.ColorTranslator]::FromHtml('#a6cfff')   # icon-only shade between accent-light and accent-active (user request)
$FG = [System.Drawing.ColorTranslator]::FromHtml('#ffffff')
$BG = [System.Drawing.ColorTranslator]::FromHtml('#222831')

function New-RoundPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $n = 4 * $size
    $big = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillPath((New-Object System.Drawing.SolidBrush $ACCENT), (New-RoundPath 0 0 $n $n (0.22 * $n)))

    # Pie chart, Pac-Man facing left: one circle (single centre and radius) split only by colour into an FG
    # right half, an ACCENT_LIGHT upper-left section and an ACCENT_MID lower-left section, with the mouth on the left.
    # The left sections are drawn first and run a few degrees under the half disc, so the shared edges blend
    # colour into colour (no seam of tile colour between the slices).
    $cx = 0.5 * $n; $cy = 0.5 * $n
    $R = 0.32 * $n
    $mouth = 30                                   # half-angle of the mouth, degrees
    $under = 6                                    # degrees each section runs underneath the half disc
    $box = @(($cx - $R), ($cy - $R), (2 * $R), (2 * $R))
    $g.FillPie((New-Object System.Drawing.SolidBrush $ACCENT_LIGHT), $box[0], $box[1], $box[2], $box[3], 180 + $mouth, 90 - $mouth + $under)
    $g.FillPie((New-Object System.Drawing.SolidBrush $ACCENT_MID), $box[0], $box[1], $box[2], $box[3], 90 - $under, 90 - $mouth + $under)
    $g.FillPie((New-Object System.Drawing.SolidBrush $FG), $box[0], $box[1], $box[2], $box[3], -90, 180)
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
