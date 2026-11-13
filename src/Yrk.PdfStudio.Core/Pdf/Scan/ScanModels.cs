namespace Yrk.PdfStudio.Core.Pdf.Scan;

public enum ScanColorMode { Color, Grayscale, BlackAndWhite }
public enum ScanSource { Flatbed, Feeder, Duplex }

public sealed record ScanDevice(string Id, string Name);
public sealed record ScanPageAnalysis(bool IsBlank, NormalizedRect? Keep);
public sealed record ScanCapabilities(IReadOnlyList<ScanSource> Sources, IReadOnlyList<int> Dpi, IReadOnlyList<ScanColorMode> ColorModes);

public sealed record ScanSettings(
    int Dpi = 300,
    ScanColorMode ColorMode = ScanColorMode.Color,
    ScanSource Source = ScanSource.Flatbed,
    int JpegQuality = 80,
    bool AutoStraighten = true,
    bool AutoCrop = true)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Dpi);
        if (JpegQuality is < 50 or > 95) throw new ArgumentOutOfRangeException(nameof(JpegQuality));
        if (!Enum.IsDefined(ColorMode)) throw new ArgumentOutOfRangeException(nameof(ColorMode));
        if (!Enum.IsDefined(Source)) throw new ArgumentOutOfRangeException(nameof(Source));
    }
}

/// <summary>Opaque BGRA pixels at the device's actual, independently measured X/Y resolution.</summary>
public sealed record ScannedPage(PdfBitmap Bitmap, double DpiX, double DpiY, IReadOnlyList<string>? Notices = null,
    ScanPageAdjustments? Adjustments = null);

/// <summary>Immutable so background previews and output use one consistent set of page edits.</summary>
public sealed record ScanPageAdjustments(int Brightness = 0, int Sharpness = 0, bool WhiteBackground = false, bool? Straighten = null)
{
    public void Validate()
    {
        if (Brightness is < -50 or > 50) throw new ArgumentOutOfRangeException(nameof(Brightness));
        if (Sharpness is < -50 or > 50) throw new ArgumentOutOfRangeException(nameof(Sharpness));
    }
}

/// <summary>JPEG bytes, or a packed 1-bit BMP for PDFsharp's bilevel/CCITT importer.</summary>
public sealed record ProcessedScanPage(int Width, int Height, double DpiX, double DpiY, ScanColorMode ColorMode, byte[] Data, bool IsBlank);
