namespace Yrk.PdfStudio.Core.Pdf.Printing;

/// <summary>How a page is sized on the paper.</summary>
public enum PrintSizing
{
    /// <summary>Enlarge or shrink to fill the printable area.</summary>
    Fit,

    /// <summary>100 %: keeps a drawing's scale. Parts outside the paper are cut off.</summary>
    ActualSize,

    /// <summary>100 %, but pages larger than the printable area are shrunk to fit.</summary>
    ShrinkOversized,

    /// <summary>A chosen percentage.</summary>
    Custom,
}

/// <summary>A rectangle in points (1/72 inch), origin top left.</summary>
public readonly record struct PrintRect(double X, double Y, double Width, double Height);

/// <param name="Rotated">The page is turned 90° clockwise to match the paper's orientation.</param>
/// <param name="Target">Where the (possibly turned) page lands on the paper, in points; may extend past the paper.</param>
/// <param name="Scale">Page points per paper point (1 = actual size).</param>
public sealed record PrintPlacement(bool Rotated, PrintRect Target, double Scale);

/// <summary>Places a page on a sheet of paper: rotation to match the paper, sizing, and centring.</summary>
public static class PrintLayout
{
    /// <param name="pageWidth">Page width as displayed (its /Rotate applied), in points.</param>
    /// <param name="pageHeight">Page height as displayed, in points.</param>
    /// <param name="paperWidth">Paper width in points, in the orientation it is printed.</param>
    /// <param name="paperHeight">Paper height in points.</param>
    /// <param name="printable">The area the printer can print on, in paper points.</param>
    /// <param name="sizing">How to size the page.</param>
    /// <param name="customScale">Used with <see cref="PrintSizing.Custom"/>: 1 = 100 %.</param>
    /// <param name="autoRotate">Turn landscape pages on portrait paper (and the reverse) so they fill it.</param>
    public static PrintPlacement Place(
        double pageWidth,
        double pageHeight,
        double paperWidth,
        double paperHeight,
        PrintRect printable,
        PrintSizing sizing,
        double customScale = 1,
        bool autoRotate = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(paperWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(paperHeight);
        if (printable.Width <= 0 || printable.Height <= 0)
        {
            printable = new PrintRect(0, 0, paperWidth, paperHeight);
        }

        // Only turn clearly landscape pages (or portrait ones on landscape paper); squares stay as they are.
        var pageLandscape = pageWidth > pageHeight * 1.01;
        var pagePortrait = pageHeight > pageWidth * 1.01;
        var paperLandscape = printable.Width > printable.Height;
        var rotated = autoRotate && ((pageLandscape && !paperLandscape) || (pagePortrait && paperLandscape));
        var (width, height) = rotated ? (pageHeight, pageWidth) : (pageWidth, pageHeight);

        var fit = Math.Min(printable.Width / width, printable.Height / height);
        var scale = sizing switch
        {
            PrintSizing.Fit => fit,
            PrintSizing.ActualSize => 1,
            PrintSizing.ShrinkOversized => Math.Min(1, fit),
            _ => customScale > 0 ? customScale : 1,
        };

        // Centred in the printable area.
        var w = width * scale;
        var h = height * scale;
        var x = printable.X + ((printable.Width - w) / 2);
        var y = printable.Y + ((printable.Height - h) / 2);
        return new PrintPlacement(rotated, new PrintRect(x, y, w, h), scale);
    }

    /// <summary>The page indices (0-based) to print for a range text such as "1-3, 5"; an empty text means all pages.</summary>
    /// <exception cref="FormatException">The text is not a valid page range.</exception>
    public static IReadOnlyList<int> Pages(string? ranges, int pageCount) =>
        string.IsNullOrWhiteSpace(ranges)
            ? Enumerable.Range(0, pageCount).ToList()
            : PageRanges.Parse(ranges, pageCount).SelectMany(r => r).ToList();
}
