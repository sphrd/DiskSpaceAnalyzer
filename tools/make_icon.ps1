# Generates assets\app.ico (16-256 px, PNG-compressed) and assets\app.png (256 px) following the
# design language: ACCENT rounded tile (radius 22 %), FG document with a folded corner (ACCENT_LIGHT),
# three BG content lines ending in ACCENT dots with a BG ring. Drawn at 4x and downscaled.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$ACCENT = [System.Drawing.ColorTranslator]::FromHtml('#4da3ff')
$ACCENT_LIGHT = [System.Drawing.ColorTranslator]::FromHtml('#cfe4ff')
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

    $x0 = 0.26 * $n; $x1 = 0.74 * $n; $y0 = 0.17 * $n; $y1 = 0.83 * $n; $fold = 0.14 * $n
    $doc = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF $x0, $y0), (New-Object System.Drawing.PointF ($x1 - $fold), $y0),
        (New-Object System.Drawing.PointF $x1, ($y0 + $fold)), (New-Object System.Drawing.PointF $x1, $y1),
        (New-Object System.Drawing.PointF $x0, $y1))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $FG), $doc)
    $tri = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($x1 - $fold), $y0), (New-Object System.Drawing.PointF ($x1 - $fold), ($y0 + $fold)),
        (New-Object System.Drawing.PointF $x1, ($y0 + $fold)))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $ACCENT_LIGHT), $tri)

    $thick = 0.045 * $n; $dot = 0.035 * $n; $ring = 0.012 * $n
    $pen = New-Object System.Drawing.Pen $BG, $thick
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    foreach ($row in @(@(0.42, 0.58), @(0.55, 0.62), @(0.68, 0.58))) {
        $y = $row[0] * $n; $xe = $row[1] * $n
        $g.DrawLine($pen, 0.34 * $n, $y, $xe, $y)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush $BG), $xe - $dot - $ring, $y - $dot - $ring, 2 * ($dot + $ring), 2 * ($dot + $ring))
        $g.FillEllipse((New-Object System.Drawing.SolidBrush $ACCENT), $xe - $dot, $y - $dot, 2 * $dot, 2 * $dot)
    }
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
