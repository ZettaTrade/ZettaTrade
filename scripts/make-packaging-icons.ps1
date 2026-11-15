# scripts/make-packaging-icons.ps1 - regenerate the packaging icon assets
# (T-257): scripts/assets/zettatrade.icns (the macOS bundle icon) and
# scripts/assets/zettatrade-256.png (the Linux .desktop icon for the
# AppImage AppDir and the .deb hicolor entry). The mark: a rounded brand
# tile with the stroked Z monogram, drawn in a 64x64 design space - the
# same fractions as scripts/make-brand-ico.ps1 (the committed
# ZettaTrade.ico), so every platform icon shows the identical mark.
#
# The .icns is the modern PNG-frame container: one 'icns' magic + a
# big-endian total length, then one entry per frame (a 4-byte type, a
# big-endian entry length, PNG data). The pinned frame set covers the
# dock and Finder sizes that matter for a desktop app: ic11 (32), ic07
# (128), ic08 (256) and ic13 (512). The container is plain bytes, so it
# can be generated on any OS - the macOS legs only copy it into
# Contents/Resources; no iconutil round trip is needed anywhere.
#
# Dev-side, Windows-only (GDI+), a one-off regeneration tool run by hand
# when the mark changes. The generated binaries are committed.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-packaging-icons.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assetsDir = Join-Path $PSScriptRoot 'assets'
$pngPath = Join-Path $assetsDir 'zettatrade-256.png'
$icnsPath = Join-Path $assetsDir 'zettatrade.icns'

# The frame set: the icns type tag and the PNG pixel size it carries.
$icnsFrames = @(
    @{ Type = 'ic11'; Size = 32 },
    @{ Type = 'ic07'; Size = 128 },
    @{ Type = 'ic08'; Size = 256 },
    @{ Type = 'ic13'; Size = 512 }
)

function Write-BEUInt32 {
    # BinaryWriter is little-endian; the icns lengths are big-endian.
    param([System.IO.BinaryWriter]$Writer, [uint32]$Value)
    $bytes = [BitConverter]::GetBytes([uint32]$Value)
    [Array]::Reverse($bytes)
    $Writer.Write($bytes)
}

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

if (-not (Test-Path $assetsDir)) {
    New-Item -ItemType Directory -Path $assetsDir | Out-Null
}

# ---------------------------------------------------------------- the PNG
# The cast matters: the function's pipeline unrolls a returned byte[]
# into boxed elements - re-pack before the writer sees it.
$png256 = [byte[]](Render-MarkPng -Size 256)
[System.IO.File]::WriteAllBytes($pngPath, $png256)

# --------------------------------------------------------------- the icns
$frames = @()
foreach ($frame in $icnsFrames) {
    $frames += ,@{ Type = $frame['Type']; Data = [byte[]](Render-MarkPng -Size $frame['Size']) }
}

$totalLength = 8
foreach ($frame in $frames) {
    $totalLength += 8 + $frame['Data'].Length
}

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)

# The container: 'icns' magic + the big-endian total file length.
$writer.Write([byte[]][char[]]'icns')
Write-BEUInt32 $writer ([uint32]$totalLength)

# One entry per frame: the type tag, the big-endian entry length (the
# tag + the length word + the PNG data), the data.
foreach ($frame in $frames) {
    $writer.Write([byte[]][char[]]$frame['Type'])
    Write-BEUInt32 $writer ([uint32](8 + $frame['Data'].Length))
    $writer.Write($frame['Data'])
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($icnsPath, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

# ------------------------------------------------------------ self-check
# Re-read both files and assert the container facts the packaging
# scripts and the tests pin: the icns magic, the total length and the
# per-entry types; the PNG signature and the 256x256 IHDR.
$icnsBytes = [System.IO.File]::ReadAllBytes($icnsPath)
$icnsMagic = [System.Text.Encoding]::ASCII.GetString($icnsBytes, 0, 4)
if ($icnsMagic -ne 'icns') { throw "The icns magic is wrong: $icnsMagic." }
$icnsTotal = [int](([uint32]$icnsBytes[4] -shl 24) -bor ([uint32]$icnsBytes[5] -shl 16) -bor ([uint32]$icnsBytes[6] -shl 8) -bor [uint32]$icnsBytes[7])
if ($icnsTotal -ne $icnsBytes.Length) { throw "The icns total length $icnsTotal does not match the file length $($icnsBytes.Length)." }
$offset = 8
$foundTypes = @()
while ($offset -lt $icnsBytes.Length) {
    $type = [System.Text.Encoding]::ASCII.GetString($icnsBytes, $offset, 4)
    $entryLength = [int](([uint32]$icnsBytes[$offset + 4] -shl 24) -bor ([uint32]$icnsBytes[$offset + 5] -shl 16) -bor ([uint32]$icnsBytes[$offset + 6] -shl 8) -bor [uint32]$icnsBytes[$offset + 7])
    if ($entryLength -lt 8) { throw "The icns entry $type carries an impossible length: $entryLength." }
    $foundTypes += $type
    $offset += $entryLength
}
$expectedTypes = @($icnsFrames | ForEach-Object { $_['Type'] })
foreach ($type in $expectedTypes) {
    if ($foundTypes -notcontains $type) { throw "The icns is missing the $type frame." }
}

$pngBytes = [System.IO.File]::ReadAllBytes($pngPath)
$pngHeader = [System.Text.Encoding]::ASCII.GetString($pngBytes, 1, 3)
if ($pngHeader -ne 'PNG') { throw "The PNG signature is wrong." }
$ihdrWidth = ([int]$pngBytes[16] -shl 24) -bor ([int]$pngBytes[17] -shl 16) -bor ([int]$pngBytes[18] -shl 8) -bor [int]$pngBytes[19]
$ihdrHeight = ([int]$pngBytes[20] -shl 24) -bor ([int]$pngBytes[21] -shl 16) -bor ([int]$pngBytes[22] -shl 8) -bor [int]$pngBytes[23]
if ($ihdrWidth -ne 256 -or $ihdrHeight -ne 256) { throw "The PNG is ${ihdrWidth}x${ihdrHeight}, not 256x256." }

Write-Output "Wrote $pngPath ($($pngBytes.Length) bytes, 256x256)."
Write-Output "Wrote $icnsPath ($($icnsBytes.Length) bytes; frames: $($foundTypes -join ', '))."
exit 0
