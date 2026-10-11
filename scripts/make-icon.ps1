# Generates NcmForge's application icon.
#
# Written as a script rather than committed as an opaque binary so the mark can be changed without
# a design tool: run this, and both the .ico and the README preview are rebuilt.
#
# The mark is a gradient rounded square carrying an eighth note — recognisable at 16 px, and the
# two colours are the ones the app itself uses for accents.
#
# Constructors use ::new() rather than New-Object: the latter parses its parenthesised argument
# list in a way that mangles arithmetic expressions such as "$size - 2 * $pad".

param(
    [string] $IcoPath = (Join-Path $PSScriptRoot '..\src\NcmForge.App\Assets\ncmforge.ico'),
    [string] $WindowIconPath = (Join-Path $PSScriptRoot '..\src\NcmForge.App\Assets\ncmforge.png'),
    [string] $PreviewPath = (Join-Path $PSScriptRoot '..\docs\icon-256.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Sizes Windows asks for: the shell, Alt-Tab, the taskbar, and the large tile views.
$sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-IconBitmap([int] $size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        # Everything is laid out on a 256-unit grid then scaled, so one set of numbers draws all sizes.
        [double] $k = $size / 256.0

        # --- rounded square -------------------------------------------------
        [double] $pad = 10 * $k
        [double] $radius = 54 * $k
        [double] $diameter = $radius * 2
        [double] $inner = $size - (2 * $pad)
        $rect = [System.Drawing.RectangleF]::new($pad, $pad, $inner, $inner)

        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $path.AddArc($rect.X, $rect.Y, $diameter, $diameter, 180, 90)
        $path.AddArc($rect.Right - $diameter, $rect.Y, $diameter, $diameter, 270, 90)
        $path.AddArc($rect.Right - $diameter, $rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
        $path.AddArc($rect.X, $rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
        $path.CloseFigure()

        $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            $rect,
            [System.Drawing.Color]::FromArgb(255, 47, 111, 237),
            [System.Drawing.Color]::FromArgb(255, 123, 76, 224),
            45.0)
        $graphics.FillPath($gradient, $path)

        $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)

        # --- sound waves behind the note ------------------------------------
        # Two arcs on the left read as "audio" without needing to survive at 16 px on their own.
        $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(96, 255, 255, 255), [float](13 * $k))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        foreach ($r in @(34, 58)) {
            [double] $d = $r * 2 * $k
            $graphics.DrawArc($pen, [float]((128 * $k) - ($r * $k)), [float]((140 * $k) - ($r * $k)), [float]$d, [float]$d, 132, 96)
        }

        # --- eighth note ----------------------------------------------------
        $graphics.FillRectangle($white, [float](150 * $k), [float](62 * $k), [float](15 * $k), [float](118 * $k))

        # Flag, swept to the right from the top of the stem.
        $flag = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $flag.AddBezier([float](165 * $k), [float](62 * $k), [float](216 * $k), [float](84 * $k), [float](214 * $k), [float](126 * $k), [float](172 * $k), [float](148 * $k))
        $flag.AddLine([float](172 * $k), [float](148 * $k), [float](172 * $k), [float](122 * $k))
        $flag.AddBezier([float](172 * $k), [float](122 * $k), [float](202 * $k), [float](104 * $k), [float](200 * $k), [float](84 * $k), [float](165 * $k), [float](76 * $k))
        $flag.CloseFigure()
        $graphics.FillPath($white, $flag)

        # Note head: an ellipse, rotated so it leans with the stem.
        $state = $graphics.Save()
        $graphics.TranslateTransform([float](126 * $k), [float](182 * $k))
        $graphics.RotateTransform(-22)
        $graphics.FillEllipse($white, [float](-34 * $k), [float](-24 * $k), [float](68 * $k), [float](48 * $k))
        $graphics.Restore($state)

        $white.Dispose()
        $pen.Dispose()
        $gradient.Dispose()
        $path.Dispose()
        $flag.Dispose()
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function Get-PngBytes([System.Drawing.Bitmap] $bitmap) {
    $stream = [System.IO.MemoryStream]::new()
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)

        # The leading comma matters: without it PowerShell enumerates the array on the way out and
        # the caller receives thousands of separate bytes instead of one byte[].
        return , $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

# --- build every size ------------------------------------------------------
$images = @()
foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    $images += [pscustomobject]@{ Size = $size; Bytes = (Get-PngBytes $bitmap) }
    $bitmap.Dispose()
}

# --- assemble the .ico -----------------------------------------------------
# The directory entries are written first, so the header size has to be known before the payloads
# are appended and each entry's offset computed.
$offset = 6 + (16 * $images.Count)

$ico = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($ico)

$writer.Write([uint16]0)                 # reserved
$writer.Write([uint16]1)                 # type: icon
$writer.Write([uint16]$images.Count)

foreach ($image in $images) {
    # 256 is encoded as 0 in the single-byte width/height fields.
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)               # palette entries
    $writer.Write([byte]0)               # reserved
    $writer.Write([uint16]1)             # colour planes
    $writer.Write([uint16]32)            # bits per pixel
    $writer.Write([uint32]$image.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $image.Bytes.Length
}

foreach ($image in $images) {
    $writer.Write([byte[]] $image.Bytes)
}

$writer.Flush()

$icoFull = [System.IO.Path]::GetFullPath($IcoPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $icoFull) | Out-Null
[System.IO.File]::WriteAllBytes($icoFull, $ico.ToArray())

$writer.Dispose()
$ico.Dispose()

# --- window icon + README preview ------------------------------------------
# The .ico carries the executable's icon on Windows; Avalonia wants a plain bitmap for the window
# and taskbar, and the README wants a preview.
$previewFull = [System.IO.Path]::GetFullPath($PreviewPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $previewFull) | Out-Null
$preview = New-IconBitmap 256
$preview.Save($previewFull, [System.Drawing.Imaging.ImageFormat]::Png)

$windowIconFull = [System.IO.Path]::GetFullPath($WindowIconPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $windowIconFull) | Out-Null
$preview.Save($windowIconFull, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

Write-Host ("ico     : {0} ({1:N0} bytes, {2} sizes)" -f $icoFull, (Get-Item $icoFull).Length, $images.Count)
Write-Host ("window  : {0}" -f $windowIconFull)
Write-Host ("preview : {0}" -f $previewFull)
