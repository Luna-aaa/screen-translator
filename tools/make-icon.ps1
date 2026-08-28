<#
    Generates src/ScreenTranslator/Resources/app.ico (multi-size, PNG-compressed).
    Re-run this after changing the artwork; the .ico is a build input, not hand-edited.

    Artwork: rounded gradient square + white crop-marquee corner brackets.
    Corner brackets stay legible at 16x16 in the tray, where a glyph would turn to mush.
#>
[CmdletBinding()]
param(
    [string]$OutPath = (Join-Path $PSScriptRoot '..\src\ScreenTranslator\Resources\app.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-Artwork([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # --- rounded background -------------------------------------------------
    $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $d = ($s * 0.24) * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 99, 91, 255)
    $c2 = [System.Drawing.Color]::FromArgb(255, 37, 99, 235)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 55.0)
    $g.FillPath($brush, $path)

    # --- crop marquee -------------------------------------------------------
    $w = [Math]::Max(1.4, $s * 0.085)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $w)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $lo = $s * 0.26
    $hi = $s - $lo
    $len = $s * 0.20

    $g.DrawLine($pen, $lo, $lo, $lo + $len, $lo)
    $g.DrawLine($pen, $lo, $lo, $lo, $lo + $len)
    $g.DrawLine($pen, $hi, $lo, $hi - $len, $lo)
    $g.DrawLine($pen, $hi, $lo, $hi, $lo + $len)
    $g.DrawLine($pen, $lo, $hi, $lo + $len, $hi)
    $g.DrawLine($pen, $lo, $hi, $lo, $hi - $len)
    $g.DrawLine($pen, $hi, $hi, $hi - $len, $hi)
    $g.DrawLine($pen, $hi, $hi, $hi, $hi - $len)

    $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Artwork $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ Size = $s; Data = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)                # reserved
$bw.Write([UInt16]1)                # type: icon
$bw.Write([UInt16]$frames.Count)

$offset = 6 + (16 * $frames.Count)
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }   # 0 means 256 in ICO
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)              # palette entries
    $bw.Write([byte]0)              # reserved
    $bw.Write([UInt16]1)            # color planes
    $bw.Write([UInt16]32)           # bits per pixel
    $bw.Write([UInt32]$f.Data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $bw.Write($f.Data) }
$bw.Flush()

$resolved = [System.IO.Path]::GetFullPath($OutPath)
$dir = [System.IO.Path]::GetDirectoryName($resolved)
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[System.IO.File]::WriteAllBytes($resolved, $out.ToArray())
$bw.Dispose(); $out.Dispose()

Write-Output "wrote $resolved ($((Get-Item $resolved).Length) bytes, $($frames.Count) sizes)"
