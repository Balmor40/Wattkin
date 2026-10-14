# Writes the README logo (mark + "wattkin" wordmark) as SVG for light and dark backgrounds.
# The wordmark is converted to outlines, so it looks the same on machines without Segoe UI.
param([string]$OutDir = (Join-Path $PSScriptRoot '..\assets\branding'))
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$inv = [Globalization.CultureInfo]::InvariantCulture
function F([double]$v) { $v.ToString('0.##', $inv) }

# Wordmark outline at 64 px, origin at the top-left of the em box.
$family = New-Object System.Drawing.FontFamily 'Segoe UI Variable Display'
$style = [int][System.Drawing.FontStyle]::Bold
$em = 64.0
$word = New-Object System.Drawing.Drawing2D.GraphicsPath
$word.AddString('wattkin', $family, $style, [single]$em, (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)

# The "w" gives the x-height and baseline the mark sits on.
$probe = New-Object System.Drawing.Drawing2D.GraphicsPath
$probe.AddString('w', $family, $style, [single]$em, (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)
$xTop = $probe.GetBounds().Top
$baseline = $probe.GetBounds().Bottom

# Mark: same geometry as the app's BrandMark, one x-height tall, a little taller for presence.
$h = ($baseline - $xTop) * 1.18
$w = $h * 1.45
$stroke = $h * 0.17
$inset = $stroke / 2
$markTop = $baseline - $h
$x0 = $inset; $x2 = $w / 2; $x4 = $w - $inset; $x1 = ($x0 + $x2) / 2; $x3 = ($x2 + $x4) / 2
$top = $markTop + $inset; $bottom = $baseline - $inset

# Place the wordmark after the mark.
$gap = $h * 0.32
$b = $word.GetBounds()
$dx = $w + $gap - $b.Left
$d = New-Object System.Text.StringBuilder
$points = $word.PathPoints; $types = $word.PathTypes
for ($i = 0; $i -lt $points.Length; $i++) {
    $t = $types[$i] -band 0x07
    $p = $points[$i]
    if ($t -eq 0) { [void]$d.Append("M$(F ($p.X + $dx)) $(F $p.Y)") }
    elseif ($t -eq 1) { [void]$d.Append("L$(F ($p.X + $dx)) $(F $p.Y)") }
    else {
        $p2 = $points[$i + 1]; $p3 = $points[$i + 2]
        [void]$d.Append("C$(F ($p.X + $dx)) $(F $p.Y) $(F ($p2.X + $dx)) $(F $p2.Y) $(F ($p3.X + $dx)) $(F $p3.Y)")
        $i += 2
        $t = $types[$i]
    }
    if (($types[$i] -band 0x80) -ne 0) { [void]$d.Append('Z') }
}

$minY = [Math]::Min($markTop, $b.Top) - 4
$maxY = [Math]::Max($baseline, $b.Bottom) + 4
$width = $w + $gap + $b.Width + 4
$height = $maxY - $minY

New-Item -ItemType Directory -Force $OutDir | Out-Null
foreach ($variant in @(@{ Name = 'light'; Text = '#1A1A1A' }, @{ Name = 'dark'; Text = '#F0F2F5' })) {
    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 $(F $minY) $(F $width) $(F $height)" width="$(F ($width * 2))" height="$(F ($height * 2))" role="img" aria-label="Wattkin">
  <g fill="none" stroke-width="$(F $stroke)" stroke-linecap="round" stroke-linejoin="round">
    <polyline points="$(F $x0),$(F $top) $(F $x1),$(F $bottom) $(F $x2),$(F $top)" stroke="$($variant.Text)"/>
    <polyline points="$(F $x2),$(F $top) $(F $x3),$(F $bottom) $(F $x4),$(F $top)" stroke="#FFB800"/>
  </g>
  <path d="$d" fill="$($variant.Text)"/>
</svg>
"@
    [IO.File]::WriteAllText((Join-Path $OutDir "wattkin-logo-$($variant.Name).svg"), $svg, (New-Object Text.UTF8Encoding $false))
}
"ok"
