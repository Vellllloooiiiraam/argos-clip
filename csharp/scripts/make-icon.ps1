# ===========================================================================
#  Gera o icone do Argos Clip (um "olho") em src\ArgosClip\app.ico
#  Desenha em multiplas resolucoes (16..256) com o tema do app e monta o .ico.
#  Uso: powershell -ExecutionPolicy Bypass -File scripts\make-icon.ps1
# ===========================================================================
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outIco = Join-Path $PSScriptRoot '..\src\ArgosClip\app.ico'
$outIco = [System.IO.Path]::GetFullPath($outIco)

function New-EyeBitmap([int]$size) {
    $ss = 4                      # supersampling para bordas suaves
    $W  = $size * $ss

    $bmp = New-Object System.Drawing.Bitmap($W, $W, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # --- fundo: quadrado arredondado com o gradiente do app ---
    $r = [single]($W * 0.20); $d = $r * 2
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bg.AddArc(0, 0, $d, $d, 180, 90)
    $bg.AddArc($W - $d, 0, $d, $d, 270, 90)
    $bg.AddArc($W - $d, $W - $d, $d, $d, 0, 90)
    $bg.AddArc(0, $W - $d, $d, $d, 90, 90)
    $bg.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($W, $W)),
        [System.Drawing.Color]::FromArgb(0x7c, 0x5c, 0xff),
        [System.Drawing.Color]::FromArgb(0xb1, 0x4b, 0xf4))
    $g.FillPath($grad, $bg)

    # --- olho: amêndoa branca (dois béziers) ---
    $eye = New-Object System.Drawing.Drawing2D.GraphicsPath
    $eye.AddBezier(
        (New-Object System.Drawing.PointF([single]($W * 0.16), [single]($W * 0.50))),
        (New-Object System.Drawing.PointF([single]($W * 0.34), [single]($W * 0.13))),
        (New-Object System.Drawing.PointF([single]($W * 0.66), [single]($W * 0.13))),
        (New-Object System.Drawing.PointF([single]($W * 0.84), [single]($W * 0.50))))
    $eye.AddBezier(
        (New-Object System.Drawing.PointF([single]($W * 0.84), [single]($W * 0.50))),
        (New-Object System.Drawing.PointF([single]($W * 0.66), [single]($W * 0.87))),
        (New-Object System.Drawing.PointF([single]($W * 0.34), [single]($W * 0.87))),
        (New-Object System.Drawing.PointF([single]($W * 0.16), [single]($W * 0.50))))
    $eye.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)), $eye)

    # --- íris + pupila + brilho ---
    $cx = [single]($W * 0.50); $cy = [single]($W * 0.50)
    $ir = [single]($W * 0.19)
    $irisB = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($W, $W)),
        [System.Drawing.Color]::FromArgb(0x6a, 0x3d, 0xf0),
        [System.Drawing.Color]::FromArgb(0x2a, 0x1b, 0x4d))
    $g.FillEllipse($irisB, $cx - $ir, $cy - $ir, 2 * $ir, 2 * $ir)

    $pr = [single]($W * 0.082)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0x0b, 0x0b, 0x12))),
        $cx - $pr, $cy - $pr, 2 * $pr, 2 * $pr)

    $hr = [single]($W * 0.030)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(235, 255, 255, 255))),
        $cx - $ir * 0.45 - $hr, $cy - $ir * 0.45 - $hr, 2 * $hr, 2 * $hr)

    # --- reduz para o tamanho final (suaviza as bordas) ---
    $out = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.Clear([System.Drawing.Color]::Transparent)
    $g2.DrawImage($bmp, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))

    $g2.Dispose(); $g.Dispose(); $bmp.Dispose()
    return $out
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @{}
foreach ($s in $sizes) {
    $bmp = New-EyeBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$s] = $ms.ToArray()
    $ms.Dispose(); $bmp.Dispose()
}

$fs = [System.IO.File]::Create($outIco)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)                # reserved
$bw.Write([UInt16]1)                # type = icon
$bw.Write([UInt16]$sizes.Count)     # count
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $dim = if ($s -ge 256) { 0 } else { $s }
    $data = $pngs[$s]
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)   # width, height
    $bw.Write([Byte]0);    $bw.Write([Byte]0)      # colors, reserved
    $bw.Write([UInt16]1);  $bw.Write([UInt16]32)   # planes, bpp
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Close(); $fs.Close()

Write-Host "icone gerado: $outIco ($((Get-Item $outIco).Length) bytes)"
