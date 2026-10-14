# Draws the app icon (multi-size .ico) and, if asked, a preview sheet of the mark at every size.
param([string]$IcoOut = (Join-Path $PSScriptRoot '..\src\Wattkin\Assets\Wattkin.ico'), [string]$PreviewOut = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Wattkin mark: a W made of two Vs. V1 neutral, V2 amber ("V for volts"), on a dark rounded tile.
$tile = [System.Drawing.Color]::FromArgb(255, 21, 24, 29)
$neutral = [System.Drawing.Color]::FromArgb(255, 240, 242, 245)
$amber = [System.Drawing.Color]::FromArgb(255, 255, 184, 0)

function New-Mark([int]$s, [bool]$withTile) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    if ($withTile) {
        $r = [single]($s * 0.23)
        $w = [single]$s
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
        $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
        $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
        $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
        $path.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush $tile), $path)
    }
    # Stroke width scales with size, with a floor so 16 px stays crisp.
    $stroke = [single][Math]::Max(1.6, $s * 0.105)
    $top = [single]($s * 0.28); $bottom = [single]($s * 0.72)
    $x0 = [single]($s * 0.17); $x1 = [single]($s * 0.335); $x2 = [single]($s * 0.5); $x3 = [single]($s * 0.665); $x4 = [single]($s * 0.83)
    $pen1 = New-Object System.Drawing.Pen $neutral, $stroke
    $pen2 = New-Object System.Drawing.Pen $amber, $stroke
    foreach ($p in $pen1, $pen2) { $p.StartCap = 'Round'; $p.EndCap = 'Round'; $p.LineJoin = 'Round' }
    $g.DrawLines($pen1, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF $x0, $top), (New-Object System.Drawing.PointF $x1, $bottom), (New-Object System.Drawing.PointF $x2, $top)))
    $g.DrawLines($pen2, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF $x2, $top), (New-Object System.Drawing.PointF $x3, $bottom), (New-Object System.Drawing.PointF $x4, $top)))
    $g.Dispose()
    return $bmp
}

# Multi-size .ico (PNG-compressed entries).
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) { $b = New-Mark $s $true; $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); , @($s, $ms.ToArray()) }
$fs = [System.IO.File]::Create($IcoOut)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]; $data = $img[1]; $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset); $offset += $data.Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img[1]) }
$bw.Close()
if (-not $PreviewOut) { return }

# Preview sheet: sizes on light and dark backgrounds, plus the wordmark.
$sheet = New-Object System.Drawing.Bitmap 760, 300
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 243, 243, 243))), 0, 0, 380, 300)
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 32, 32))), 380, 0, 380, 300)
foreach ($half in 0, 380) {
    $x = $half + 20
    foreach ($s in 16, 24, 32, 48, 96) { $m = New-Mark $s $true; $g.DrawImage($m, $x, 30 + (96 - $s)); $m.Dispose(); $x += $s + 18 }
    $big = New-Mark 120 $false
    $g.DrawImage($big, $half + 10, 160)
    $big.Dispose()
    $font = New-Object System.Drawing.Font 'Segoe UI Variable Display', 34, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $color = if ($half -eq 0) { [System.Drawing.Color]::FromArgb(255, 26, 26, 26) } else { [System.Drawing.Color]::White }
    $g.DrawString('wattkin', $font, (New-Object System.Drawing.SolidBrush $color), $half + 135, 198)
}
$g.Dispose()
$sheet.Save($PreviewOut, [System.Drawing.Imaging.ImageFormat]::Png)
