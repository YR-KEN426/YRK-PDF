using System.Globalization;
using System.IO;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>
/// What to stamp on every page: a watermark across the middle, and header and footer text in six places.
/// Texts may hold {page}, {pages}, {date} and {file}.
/// </summary>
public sealed record PageMarkSettings
{
    public string Watermark { get; init; } = string.Empty;

    /// <summary>Points; 0 fits the text to the page.</summary>
    public double WatermarkSize { get; init; }

    public PdfColor WatermarkColor { get; init; } = new(128, 128, 128);

    public double WatermarkOpacity { get; init; } = 0.3;

    /// <summary>Degrees, counter-clockwise.</summary>
    public double WatermarkAngle { get; init; } = 45;

    public bool WatermarkBehind { get; init; } = true;

    public string HeaderLeft { get; init; } = string.Empty;

    public string HeaderCenter { get; init; } = string.Empty;

    public string HeaderRight { get; init; } = string.Empty;

    public string FooterLeft { get; init; } = string.Empty;

    public string FooterCenter { get; init; } = string.Empty;

    public string FooterRight { get; init; } = string.Empty;

    /// <summary>The font of the watermark and of header and footer text.</summary>
    public string FontFamily { get; init; } = PageMarks.Font;

    public double TextSize { get; init; } = 10;

    public PdfColor TextColor { get; init; } = new(0, 0, 0);

    /// <summary>Distance of header and footer from the page edges, in millimetres.</summary>
    public double MarginMm { get; init; } = 12;

    /// <summary>The number printed on the first page.</summary>
    public int FirstNumber { get; init; } = 1;

    public bool SkipFirstPage { get; init; }

    public bool IsEmpty => Watermark.Trim().Length == 0
        && new[] { HeaderLeft, HeaderCenter, HeaderRight, FooterLeft, FooterCenter, FooterRight }.All(t => t.Trim().Length == 0);
}

/// <summary>One piece of text on a page, in points from the page's top-left as displayed.</summary>
internal sealed record PageMark(string Text, TextShaper.ShapedLine Line, double Left, double Top, double Angle, PdfColor Color, double Opacity, bool Behind, double FontSize, bool Bold);

internal static class PageMarks
{
    public const string Font = "Leelawadee UI";
    private const double PointsPerMm = 72 / 25.4;
    private const double FitShare = 0.8;
    private const double MaxWatermarkSize = 200;

    /// <summary>Where every mark goes on one page (also used for the preview).</summary>
    /// <param name="pageIndex">0-based.</param>
    public static IReadOnlyList<PageMark> Layout(PageMarkSettings settings, int pageIndex, int pageCount, double width, double height, string fileName, DateTime today)
    {
        var marks = new List<PageMark>();
        if (settings.SkipFirstPage && pageIndex == 0)
        {
            return marks;
        }

        string Fill(string text) => text
            .Replace("{page}", (pageIndex + settings.FirstNumber).ToString(CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{pages}", (pageCount + settings.FirstNumber - 1).ToString(CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", today.ToString("d", CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{file}", Path.GetFileNameWithoutExtension(fileName), StringComparison.OrdinalIgnoreCase);

        var watermark = Fill(settings.Watermark).Trim();
        if (watermark.Length > 0)
        {
            var angle = settings.WatermarkAngle * Math.PI / 180;
            double cos = Math.Abs(Math.Cos(angle)), sin = Math.Abs(Math.Sin(angle));
            var size = settings.WatermarkSize;
            if (size <= 0)
            {
                // Fit the turned text inside the page: its bounding box may use 80 % of each side.
                var unit = TextShaper.Shape(watermark, settings.FontFamily, bold: true, italic: false, 100);
                var scale = Math.Min(
                    FitShare * width / ((unit.Width * cos) + (unit.Height * sin)),
                    FitShare * height / ((unit.Width * sin) + (unit.Height * cos)));
                size = Math.Min(MaxWatermarkSize, 100 * scale);
            }

            var line = TextShaper.Shape(watermark, settings.FontFamily, bold: true, italic: false, size);

            // Centre the turned box on the page: the box's middle, turned counter-clockwise, lands on the page's middle.
            var a = settings.WatermarkAngle * Math.PI / 180;
            double cx = line.Width / 2, cy = line.Height / 2;
            var dx = (cx * Math.Cos(a)) + (cy * Math.Sin(a));
            var dy = (-cx * Math.Sin(a)) + (cy * Math.Cos(a));
            marks.Add(new PageMark(watermark, line, (width / 2) - dx, (height / 2) - dy, settings.WatermarkAngle, settings.WatermarkColor, settings.WatermarkOpacity, settings.WatermarkBehind, size, Bold: true));
        }

        var margin = settings.MarginMm * PointsPerMm;
        void Add(string text, bool top, int column)
        {
            text = Fill(text).Trim();
            if (text.Length == 0)
            {
                return;
            }

            var line = TextShaper.Shape(text, settings.FontFamily, bold: false, italic: false, settings.TextSize);
            var left = column switch
            {
                0 => margin,
                1 => (width - line.Width) / 2,
                _ => width - margin - line.Width,
            };
            var y = top ? margin : height - margin - line.Height;
            marks.Add(new PageMark(text, line, left, y, 0, settings.TextColor, 1, false, settings.TextSize, Bold: false));
        }

        Add(settings.HeaderLeft, true, 0);
        Add(settings.HeaderCenter, true, 1);
        Add(settings.HeaderRight, true, 2);
        Add(settings.FooterLeft, false, 0);
        Add(settings.FooterCenter, false, 1);
        Add(settings.FooterRight, false, 2);
        return marks;
    }

    /// <summary>The marks of the chosen pages as PDF text blocks (fonts embedded once for all pages).</summary>
    public static IReadOnlyList<PdfTextBlock> ToBlocks(PageMarkSettings settings, IReadOnlyList<PdfPageSize> pageSizes, IReadOnlyList<int> pages, string fileName, DateTime today)
    {
        try
        {
            return Build(settings);
        }
        catch (InvalidOperationException) when (settings.FontFamily != Font)
        {
            return Build(settings with { FontFamily = Font }); // The chosen font does not allow embedding.
        }

        IReadOnlyList<PdfTextBlock> Build(PageMarkSettings settings)
        {
            var placed = new List<(int PageIndex, PageMark Mark, double Width, double Height)>();
            foreach (var index in pages)
            {
                var size = pageSizes[index];
                foreach (var mark in Layout(settings, index, pageSizes.Count, size.Width, size.Height, fileName, today))
                {
                    placed.Add((index, mark, size.Width, size.Height));
                }
            }

            var blocks = TextShaper.ToBlocks(placed.Select(p => (p.PageIndex, new NormalizedPoint(p.Mark.Left / p.Width, p.Mark.Top / p.Height), p.Mark.Color, p.Mark.Line)).ToList());
            return blocks.Select((block, i) => block with { Rotation = placed[i].Mark.Angle, Opacity = placed[i].Mark.Opacity, Behind = placed[i].Mark.Behind }).ToList();
        }
    }
}
