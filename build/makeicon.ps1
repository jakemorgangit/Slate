# Draws the app mark and packs it into a multi-resolution .ico.
# The mark matches the in-app brand: a violet rounded square with a half-filled square.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\Slate\appicon.ico'))

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function New-Mark([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square, violet gradient, with a little breathing room at small sizes.
    $pad = [Math]::Max(1, [int]($size * 0.06))
    $side = $size - ($pad * 2)
    $radius = [int]($side * 0.24)

    $rect = New-Object System.Drawing.Rectangle $pad, $pad, $side, $side
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 124, 92, 255),
        [System.Drawing.Color]::FromArgb(255, 143, 116, 255),
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $g.FillPath($brush, $path)

    # The half-filled square: an outlined box with its left column solid.
    $inset = [int]($size * 0.28)
    $boxX = $inset
    $boxY = $inset
    $boxW = $size - ($inset * 2)
    $boxH = $boxW
    $stroke = [Math]::Max(1.0, $size * 0.075)

    $white = [System.Drawing.Color]::White
    $pen = New-Object System.Drawing.Pen $white, $stroke
    $pen.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Inset
    $g.DrawRectangle($pen, $boxX, $boxY, $boxW, $boxH)

    $fill = New-Object System.Drawing.SolidBrush $white
    $half = [int]([Math]::Round($boxW / 2.0))
    $g.FillRectangle($fill, $boxX, $boxY, $half, $boxH)

    $g.Dispose(); $brush.Dispose(); $pen.Dispose(); $fill.Dispose(); $path.Dispose()
    return $bmp
}

$sizes = @(256, 128, 64, 48, 32, 24, 16)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Mark $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $ms.Dispose(); $bmp.Dispose()
}

# ICO container: header, one directory entry per image, then the PNG payloads.
$icoStream = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $icoStream
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type: icon
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))   # 0 means 256
    $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([Byte]0)               # palette count
    $bw.Write([Byte]0)               # reserved
    $bw.Write([UInt16]1)             # colour planes
    $bw.Write([UInt16]32)            # bits per pixel
    $bw.Write([UInt32]$pngs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $bw.Write($png) }

$bw.Flush()
[System.IO.File]::WriteAllBytes($Out, $icoStream.ToArray())
$bw.Dispose(); $icoStream.Dispose()

"wrote $Out ($([Math]::Round((Get-Item $Out).Length / 1KB, 1)) KB, $($sizes.Count) sizes)"
