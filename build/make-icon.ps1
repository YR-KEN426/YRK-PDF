# Makes the YRK PDF icon from build\icon-source.png (1024 px, transparent background): a multi-size .ico for the
# program (DIB frames, PNG for 256 px) and the 128 px PNG for the website.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File build\make-icon.ps1
param(
    [string]$Output = (Join-Path $PSScriptRoot "..\src\Yrk.PdfStudio.App\Assets\app.ico"),
    [string]$WebsiteIcon = (Join-Path $PSScriptRoot "..\docs\release-repo\site-src\img\icon.png")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$sourcePath = Join-Path $PSScriptRoot "icon-source.png"
$source = [Windows.Media.Imaging.BitmapDecoder]::Create((New-Object Uri (Resolve-Path $sourcePath).Path), 'PreservePixelFormat', 'OnLoad').Frames[0]

$bandPixel = New-Object byte[] 4
(New-Object Windows.Media.Imaging.FormatConvertedBitmap (New-Object Windows.Media.Imaging.CroppedBitmap $source, (New-Object Windows.Int32Rect ([int]($source.PixelWidth * 0.2)), ([int]($source.PixelHeight * 0.86)), 1, 1)), ([Windows.Media.PixelFormats]::Bgra32), $null, 0).CopyPixels($bandPixel, 4, 0)
$bandBrush = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromRgb($bandPixel[2], $bandPixel[1], $bandPixel[0]))

function Render([int]$size) {
    $visual = New-Object Windows.Media.DrawingVisual
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [Windows.Media.BitmapScalingMode]::HighQuality)
    $dc = $visual.RenderOpen()
    $dc.DrawImage($source, (New-Object Windows.Rect 0, 0, $size, $size))
    # Below 32 px the word "PDF" is only a smudge, so the band is left plain (its own colour over the text).
    if ($size -lt 32) {
        $dc.DrawRectangle($bandBrush, $null, (New-Object Windows.Rect ($size * 0.26), ($size * 0.78), ($size * 0.48), ($size * 0.16)))
    }
    $dc.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}

function Png($bitmap) {
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    return , $stream.ToArray()
}

function Frame([int]$size) {
    $bitmap = Render $size
    if ($size -ge 256) { return , (Png $bitmap) }

    # Smaller sizes as classic 32-bit DIB frames (BITMAPINFOHEADER, bottom-up rows, then a 1-bit AND mask),
    # which every Windows icon reader understands.
    $straight = New-Object Windows.Media.Imaging.FormatConvertedBitmap $bitmap, ([Windows.Media.PixelFormats]::Bgra32), $null, 0
    $pixels = New-Object byte[] ($size * $size * 4)
    $straight.CopyPixels($pixels, $size * 4, 0)
    $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
    $dib = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter $dib
    $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]0); $bw.Write([uint32]($size * $size * 4 + $maskRow * $size)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $size * 4, $size * 4) }
    $bw.Write((New-Object byte[] ($maskRow * $size)))
    $bw.Flush()
    return , $dib.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = $sizes | ForEach-Object { , (Frame $_) }
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
[IO.File]::WriteAllBytes($Output, $out.ToArray())
"Wrote $Output ($($out.Length) bytes)"

[IO.File]::WriteAllBytes($WebsiteIcon, (Png (Render 128)))
"Wrote $WebsiteIcon"
