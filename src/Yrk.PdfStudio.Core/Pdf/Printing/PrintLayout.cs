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

/// <summary>How many pages go on one side of a sheet.</summary>
public enum PagesPerSheet
{
    /// <summary>Two pages side by side on each side of the sheet, in the order that makes a booklet when the printed
    /// sheets are folded in the middle (printed on both sides, flipped on the short edge).</summary>
    Booklet = 0,
    One = 1,
    Two = 2,
    Four = 4,
    Six = 6,
    Nine = 9,
}

/// <summary>A part of a sheet and the page printed on it (null: left blank, as at the end of a booklet).</summary>
public sealed record SheetCell(int? PageIndex, PrintRect Area);

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

    /// <summary>Two and six pages a sheet (and booklets) fit best with the paper turned against the pages.</summary>
    public static bool TurnsPaper(PagesPerSheet layout) => layout is PagesPerSheet.Two or PagesPerSheet.Six or PagesPerSheet.Booklet;

    /// <summary>
    /// The sides of sheets to print, in order, each with its pages and where they go: <paramref name="pages"/>
    /// in rows across the printable area, or (booklet) in folding order, two to a side.
    /// </summary>
    /// <param name="gap">Space between pages, in points (none at a booklet's fold).</param>
    public static IReadOnlyList<IReadOnlyList<SheetCell>> Sheets(IReadOnlyList<int> pages, PagesPerSheet layout, PrintRect printable, double gap = 8)
    {
        var sides = new List<IReadOnlyList<SheetCell>>();
        if (layout == PagesPerSheet.Booklet)
        {
            var half = printable.Width / 2;
            var order = BookletOrder(pages.Count);
            for (var i = 0; i < order.Count; i += 2)
            {
                sides.Add(
                [
                    new SheetCell(order[i] is { } left ? pages[left] : null, printable with { Width = half }),
                    new SheetCell(order[i + 1] is { } right ? pages[right] : null, printable with { X = printable.X + half, Width = half }),
                ]);
            }

            return sides;
        }

        var (columns, rows) = Grid(layout, printable);
        var width = (printable.Width - (gap * (columns - 1))) / columns;
        var height = (printable.Height - (gap * (rows - 1))) / rows;
        var perSheet = columns * rows;
        for (var first = 0; first < pages.Count; first += perSheet)
        {
            var cells = new List<SheetCell>();
            for (var i = 0; i < perSheet && first + i < pages.Count; i++)
            {
                var (row, column) = Math.DivRem(i, columns);
                cells.Add(new SheetCell(
                    pages[first + i],
                    new PrintRect(printable.X + (column * (width + gap)), printable.Y + (row * (height + gap)), width, height)));
            }

            sides.Add(cells);
        }

        return sides;
    }

    /// <summary>Columns and rows of pages: more columns on landscape paper.</summary>
    public static (int Columns, int Rows) Grid(PagesPerSheet layout, PrintRect area)
    {
        var wide = area.Width >= area.Height;
        return layout switch
        {
            PagesPerSheet.Two => wide ? (2, 1) : (1, 2),
            PagesPerSheet.Four => (2, 2),
            PagesPerSheet.Six => wide ? (3, 2) : (2, 3),
            PagesPerSheet.Nine => (3, 3),
            PagesPerSheet.Booklet => (2, 1),
            _ => (1, 1),
        };
    }

    /// <summary>
    /// Booklet order for <paramref name="count"/> pages: positions (null for blank pages that fill the last sheet), two
    /// per side, front then back of each sheet. 8 pages: 8 1 | 2 7 | 6 3 | 4 5 (1-based).
    /// </summary>
    public static IReadOnlyList<int?> BookletOrder(int count)
    {
        var padded = (count + 3) / 4 * 4;
        var order = new List<int?>(padded);
        for (var sheet = 0; sheet < padded / 4; sheet++)
        {
            foreach (var position in new[] { padded - 1 - (2 * sheet), 2 * sheet, (2 * sheet) + 1, padded - 2 - (2 * sheet) })
            {
                order.Add(position < count ? position : null);
            }
        }

        return order;
    }

    /// <summary>The page indices (0-based) to print for a range text such as "1-3, 5"; an empty text means all pages.</summary>
    /// <exception cref="FormatException">The text is not a valid page range.</exception>
    public static IReadOnlyList<int> Pages(string? ranges, int pageCount) =>
        string.IsNullOrWhiteSpace(ranges)
            ? Enumerable.Range(0, pageCount).ToList()
            : PageRanges.Parse(ranges, pageCount).SelectMany(r => r).ToList();
}
