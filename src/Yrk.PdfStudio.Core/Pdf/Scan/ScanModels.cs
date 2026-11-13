namespace Yrk.PdfStudio.Core.Pdf.Scan;

public enum ScanColorMode { Color, Grayscale, BlackAndWhite }
public enum ScanSource { Flatbed, Feeder, Duplex }

public sealed record ScanDevice(string Id, string Name);
public sealed record ScanPageAnalysis(bool IsBlank, NormalizedRect? Keep);
public sealed record ScanCapabilities(IReadOnlyList<ScanSource> Sources, IReadOnlyList<int> Dpi, IReadOnlyList<ScanColorMode> ColorModes,
    ScanExtent? Bed = null, ScanExtent? Feeder = null)
{
    public ScanExtent? Extent(ScanSource source) => source == ScanSource.Flatbed ? Bed : Feeder;
}

public sealed record ScanSettings(
    int Dpi = 300,
    ScanColorMode ColorMode = ScanColorMode.Color,
    ScanSource Source = ScanSource.Flatbed,
    int JpegQuality = 80,
    bool AutoStraighten = true,
    bool AutoCrop = true,
    NormalizedRect? Region = null)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Dpi);
        if (JpegQuality is < 50 or > 95) throw new ArgumentOutOfRangeException(nameof(JpegQuality));
        if (!Enum.IsDefined(ColorMode)) throw new ArgumentOutOfRangeException(nameof(ColorMode));
        if (!Enum.IsDefined(Source)) throw new ArgumentOutOfRangeException(nameof(Source));
        if (Region is { } r) ScanRegion.Validate(r);
    }
}

/// <summary>Device-independent normalized glass coordinates, converted at the actual scan resolution.</summary>
public static class ScanRegion
{
    public const string FallbackNotice = "The scanner cannot scan the selected area. A full scan was cropped instead.";

    internal static void Validate(NormalizedRect r)
    {
        if (!double.IsFinite(r.X + r.Y + r.Width + r.Height) || r.X < 0 || r.Y < 0
            || r.Width <= 0 || r.Height <= 0 || r.X + r.Width > 1 || r.Y + r.Height > 1)
            throw new ArgumentOutOfRangeException(nameof(r));
    }

    public static (int X, int Y, int Width, int Height) Pixels(NormalizedRect r, int maxWidth, int maxHeight)
    {
        Validate(r);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        var x = Math.Clamp((int)Math.Round(r.X * maxWidth), 0, maxWidth - 1);
        var y = Math.Clamp((int)Math.Round(r.Y * maxHeight), 0, maxHeight - 1);
        return (x, y, Math.Clamp((int)Math.Round(r.Width * maxWidth), 1, maxWidth - x),
            Math.Clamp((int)Math.Round(r.Height * maxHeight), 1, maxHeight - y));
    }

    public static PdfBitmap Crop(PdfBitmap bitmap, NormalizedRect region, CancellationToken ct = default)
    {
        var (x, y, w, h) = Pixels(region, bitmap.Width, bitmap.Height);
        var pixels = new byte[checked(w * h * 4)];
        for (var row = 0; row < h; row++)
        {
            ct.ThrowIfCancellationRequested();
            bitmap.Pixels.AsSpan((y + row) * bitmap.Stride + x * 4, w * 4).CopyTo(pixels.AsSpan(row * w * 4));
        }
        return new(w, h, w * 4, pixels);
    }
}

public enum ScanOutputFormat { Pdf, Jpeg, Png }

/// <summary>Opaque BGRA pixels at the device's actual, independently measured X/Y resolution.</summary>
public sealed record ScannedPage(PdfBitmap Bitmap, double DpiX, double DpiY, IReadOnlyList<string>? Notices = null,
    ScanPageAdjustments? Adjustments = null);

/// <summary>Immutable so background previews and output use one consistent set of page edits.</summary>
public sealed record ScanPageAdjustments(int Brightness = 0, int Sharpness = 0, bool WhiteBackground = false, bool? Straighten = null, int Contrast = 0)
{
    public void Validate()
    {
        if (Brightness is < -50 or > 50) throw new ArgumentOutOfRangeException(nameof(Brightness));
        if (Contrast is < -50 or > 50) throw new ArgumentOutOfRangeException(nameof(Contrast));
        if (Sharpness is < -50 or > 50) throw new ArgumentOutOfRangeException(nameof(Sharpness));
    }
}

/// <summary>JPEG bytes, or a packed 1-bit BMP for PDFsharp's bilevel/CCITT importer.</summary>
public sealed record ProcessedScanPage(int Width, int Height, double DpiX, double DpiY, ScanColorMode ColorMode, byte[] Data, bool IsBlank);
