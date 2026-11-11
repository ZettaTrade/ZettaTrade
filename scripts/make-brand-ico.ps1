# scripts/make-brand-ico.ps1 - regenerate src/ZettaTrade.App/Assets/ZettaTrade.ico
# from the product mark's geometry (T-162). The mark: a rounded brand tile
# with the stroked Z monogram, drawn in a 64x64 design space and rendered at
# every taskbar-ready size (the 16px frame is the taskbar's own pick). The
# frames ride PNG-compressed ICO entries (legal since Vista; Windows reads
# the right frame). The vector source of truth is
# Assets/zettatrade-mark.svg - the same fractions.
#
# Dev-side, Windows-only (GDI+): a one-off regeneration tool, run by hand
# when the mark changes. The generated binary is committed.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$outPath = Join-Path $PSScriptRoot '..\src\ZettaTrade.App\Assets\ZettaTrade.ico'

function Render-MarkPng {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $k = $Size / 64.0

    # The rounded tile: 4..60 in the 64-space, corner radius 12.
    $tile = New-Object System.Drawing.RectangleF([single](4 * $k), [single](4 * $k), [single](56 * $k), [single](56 * $k))
    $radius = 12 * $k
    $tilePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tilePath.AddArc($tile.X, $tile.Y, 2 * $radius, 2 * $radius, 180, 90)
    $tilePath.AddArc($tile.Right - 2 * $radius, $tile.Y, 2 * $radius, 2 * $radius, 270, 90)
    $tilePath.AddArc($tile.Right - 2 * $radius, $tile.Bottom - 2 * $radius, 2 * $radius, 2 * $radius, 0, 90)
    $tilePath.AddArc($tile.X, $tile.Bottom - 2 * $radius, 2 * $radius, 2 * $radius, 90, 90)
    $tilePath.CloseFigure()
    $tileBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x1E, 0x40, 0xAF))
    $g.FillPath($tileBrush, $tilePath)

    # The Z monogram: the top bar, the diagonal down-left, the bottom bar -
    # one stroked path, round caps and joins, width 6.
    $zPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $zPath.AddLines(@(
        [System.Drawing.PointF]::new([single](20 * $k), [single](20 * $k)),
        [System.Drawing.PointF]::new([single](44 * $k), [single](20 * $k)),
        [System.Drawing.PointF]::new([single](20 * $k), [single](44 * $k)),
        [System.Drawing.PointF]::new([single](44 * $k), [single](44 * $k))
    ))
    $pen = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb(255, 0xF5, 0xF7, 0xFA)), ([single](6 * $k))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, $zPath)

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bitmap.Dispose()
    return $stream.ToArray()
}

$frames = @()
foreach ($size in $sizes) {
    # The cast matters: the function's pipeline unrolls a returned byte[]
    # into boxed elements - re-pack before the writer sees it.
    $png = [byte[]](Render-MarkPng -Size $size)
    $frames += ,@{ Size = $size; Data = $png }
}

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)

# ICONDIR: reserved 0, type 1 (icon), one entry per frame.
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$frames.Count)

# ICONDIRENTRYs first (PNG-compressed frames; 256 encodes as 0).
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $byte = if ($frame['Size'] -ge 256) { 0 } else { $frame['Size'] }
    $writer.Write([byte]$byte)  # width
    $writer.Write([byte]$byte)  # height
    $writer.Write([byte]0)      # palette colors (none)
    $writer.Write([byte]0)      # reserved
    $writer.Write([uint16]1)    # color planes
    $writer.Write([uint16]32)   # bits per pixel
    $writer.Write([uint32]$frame['Data'].Length)
    $writer.Write([uint32]$offset)
    $offset += $frame['Data'].Length
}

foreach ($frame in $frames) {
    $writer.Write($frame['Data'])
}

$writer.Flush()
$directory = Split-Path $outPath
if (-not (Test-Path $directory)) {
    New-Item -ItemType Directory -Path $directory | Out-Null
}
[System.IO.File]::WriteAllBytes($outPath, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

Write-Output "Wrote $outPath ($($frames.Count) frames: $($sizes -join ', '))"
