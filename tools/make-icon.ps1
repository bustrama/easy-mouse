# Generates the Easy Mouse app icon from one geometry: assets/icon/icon.svg (master),
# PNGs at several sizes and a multi-size icon.ico (PNG-compressed entries).
# Run from the repo root:  powershell -NoProfile -File tools/make-icon.ps1
# Shapes: rounded-square tile, white mouse body (rounded rectangle, not an ellipse).
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'assets\icon'
New-Item -ItemType Directory -Force $out | Out-Null

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # tile
    $tile = New-RoundRect 0 0 $s $s ($s * 0.225)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), ([System.Drawing.Color]::FromArgb(0x3B, 0x82, 0xF6)), ([System.Drawing.Color]::FromArgb(0x25, 0x63, 0xEB))
    $g.FillPath($grad, $tile)

    # mouse body
    $x0 = $s * 0.32; $y0 = $s * 0.18; $w = $s * 0.36; $h = $s * 0.64
    $body = New-RoundRect $x0 $y0 $w $h ($s * 0.12)
    $g.FillPath([System.Drawing.Brushes]::White, $body)

    # button split (cut lines in the tile colour)
    $stroke = [Math]::Max(1.0, $s * 0.045)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0x2B, 0x6B, 0xEB)), $stroke
    $yl = $y0 + $h * 0.40
    $g.DrawLine($pen, [single]$x0, [single]$yl, [single]($x0 + $w), [single]$yl)
    $g.DrawLine($pen, [single]($s * 0.5), [single]$y0, [single]($s * 0.5), [single]$yl)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256, 512
$pngs = @{}
foreach ($s in $sizes) {
    $bmp = Render $s
    $path = Join-Path $out ("icon-$s.png")
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngs[$s] = [System.IO.File]::ReadAllBytes($path)
}

# multi-size .ico with PNG-compressed entries
$icoSizes = 16, 24, 32, 48, 64, 128, 256
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$icoSizes.Count)
$offset = 6 + 16 * $icoSizes.Count
foreach ($s in $icoSizes) {
    $b = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $bw.Write($b); $bw.Write($b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$s].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$s].Length
}
foreach ($s in $icoSizes) { $bw.Write($pngs[$s]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $out 'icon.ico'), $ms.ToArray())

# SVG master: same geometry in a 512 viewBox
$svg = @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512" role="img" aria-label="Easy Mouse">
  <defs>
    <linearGradient id="g" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#3B82F6"/>
      <stop offset="1" stop-color="#2563EB"/>
    </linearGradient>
  </defs>
  <rect width="512" height="512" rx="115.2" fill="url(#g)"/>
  <rect x="163.84" y="92.16" width="184.32" height="327.68" rx="61.44" fill="#fff"/>
  <path d="M163.84 223.23H348.16M256 92.16V223.23" stroke="#2B6BEB" stroke-width="23" fill="none"/>
</svg>
'@
Set-Content -Path (Join-Path $out 'icon.svg') -Value $svg -Encoding utf8
Write-Output "Wrote $out"
