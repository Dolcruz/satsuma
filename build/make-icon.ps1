# Renders the Satsuma app icon (multi-size .ico + 512px .png) with GDI+.
# Usage: pwsh build/make-icon.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\Satsuma\Assets'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-SatsumaBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 256.0

    # Soft shadow
    $shadow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shadow.AddEllipse([single](34*$s), [single](70*$s), [single](192*$s), [single](178*$s))
    $sb = New-Object System.Drawing.Drawing2D.PathGradientBrush($shadow)
    $sb.CenterColor = [System.Drawing.Color]::FromArgb(70, 120, 40, 0)
    $sb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 120, 40, 0))
    $g.FillPath($sb, $shadow)

    # Fruit body (slightly flattened sphere)
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    $body.AddEllipse([single](28*$s), [single](56*$s), [single](200*$s), [single](184*$s))
    $bb = New-Object System.Drawing.Drawing2D.PathGradientBrush($body)
    $bb.CenterPoint = New-Object System.Drawing.PointF([single](100*$s), [single](110*$s))
    $bb.CenterColor = [System.Drawing.Color]::FromArgb(255, 255, 196, 92)
    $bb.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 236, 92, 8))
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend(3)
    $blend.Colors = @(
        [System.Drawing.Color]::FromArgb(255, 226, 84, 6),
        [System.Drawing.Color]::FromArgb(255, 255, 132, 24),
        [System.Drawing.Color]::FromArgb(255, 255, 200, 98))
    $blend.Positions = @([single]0, [single]0.55, [single]1)
    $bb.InterpolationColors = $blend
    $g.FillPath($bb, $body)

    # Specular highlight
    $hl = New-Object System.Drawing.Drawing2D.GraphicsPath
    $hl.AddEllipse([single](62*$s), [single](84*$s), [single](70*$s), [single](44*$s))
    $hb = New-Object System.Drawing.Drawing2D.PathGradientBrush($hl)
    $hb.CenterColor = [System.Drawing.Color]::FromArgb(170, 255, 255, 255)
    $hb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillPath($hb, $hl)

    # Stem
    $stemPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 92, 64, 24), [single]([Math]::Max(1.5, 9*$s)))
    $stemPen.StartCap = 'Round'; $stemPen.EndCap = 'Round'
    $g.DrawLine($stemPen, [single](128*$s), [single](66*$s), [single](134*$s), [single](44*$s))

    # Leaf
    $leaf = New-Object System.Drawing.Drawing2D.GraphicsPath
    $leaf.AddBezier(
        [single](134*$s), [single](48*$s),
        [single](150*$s), [single](8*$s),
        [single](206*$s), [single](10*$s),
        [single](226*$s), [single](26*$s))
    $leaf.AddBezier(
        [single](226*$s), [single](26*$s),
        [single](204*$s), [single](64*$s),
        [single](160*$s), [single](70*$s),
        [single](134*$s), [single](48*$s))
    $lb = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF([single](134*$s), [single](60*$s))),
        (New-Object System.Drawing.PointF([single](226*$s), [single](16*$s))),
        [System.Drawing.Color]::FromArgb(255, 46, 140, 52),
        [System.Drawing.Color]::FromArgb(255, 120, 200, 80))
    $g.FillPath($lb, $leaf)

    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = foreach ($sz in $sizes) { ,(Get-PngBytes (New-SatsumaBitmap $sz)) }

# ICO container with PNG payloads
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'Satsuma.ico'), $ico.ToArray())

$big = New-SatsumaBitmap 512
$big.Save((Join-Path $OutDir 'Satsuma.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Icon written to $OutDir"
