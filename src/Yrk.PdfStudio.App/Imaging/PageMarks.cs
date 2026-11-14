using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
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

    public bool WatermarkBehind { get; init; }

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

        var watermark = Fill(settings.Watermark);
        var texts = watermark.Split(["\r\n", "\n", "\r"], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (texts.Length > 0)
        {
            var angle = settings.WatermarkAngle * Math.PI / 180;
            double cos = Math.Abs(Math.Cos(angle)), sin = Math.Abs(Math.Sin(angle));
            var size = settings.WatermarkSize;
            if (size <= 0)
            {
                double Fit(string[] candidate)
                {
                    var units = candidate.Select(text => TextShaper.Shape(text, settings.FontFamily, bold: true, italic: false, 100)).ToArray();
                    double w = units.Max(line => line.Width), h = units.Sum(line => line.Height);
                    var scale = Math.Min(FitShare * width / ((w * cos) + (h * sin)), FitShare * height / ((w * sin) + (h * cos)));
                    return Math.Min(MaxWatermarkSize, 100 * scale);
                }

                size = Fit(texts);
                if (texts.Length == 1 && watermark.IndexOfAny(['\r', '\n']) < 0 && size < MaxWatermarkSize)
                {
                    var singleSize = size;
                    var bestTexts = texts;
                    foreach (var candidate in BalancedWatermarkLines(texts[0], settings.FontFamily))
                    {
                        var candidateSize = Fit(candidate);
                        // Candidates arrive in line-count order; ties retain fewer lines.
                        if (candidateSize > size)
                        {
                            bestTexts = candidate;
                            size = candidateSize;
                        }
                    }

                    // Wrapping must improve on the single line by at least ten percent.
                    if (size >= singleSize * 1.1)
                    {
                        texts = bestTexts;
                    }
                    else
                    {
                        size = singleSize;
                    }
                }
            }

            var lines = texts.Select(text => TextShaper.Shape(text, settings.FontFamily, bold: true, italic: false, size)).ToArray();
            var totalHeight = lines.Sum(line => line.Height);
            var y = 0.0;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Rotate each centred line's top-left around the middle of the whole stack.
                double vx = -line.Width / 2, vy = y - (totalHeight / 2);
                var left = (width / 2) + (vx * Math.Cos(angle)) + (vy * Math.Sin(angle));
                var top = (height / 2) - (vx * Math.Sin(angle)) + (vy * Math.Cos(angle));
                marks.Add(new PageMark(texts[i], line, left, top, settings.WatermarkAngle, settings.WatermarkColor, settings.WatermarkOpacity, settings.WatermarkBehind, size, Bold: true));
                y += line.Height;
            }
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

    /// <summary>For each line count, minimise the widest line using actual shaped widths.</summary>
    private static IEnumerable<string[]> BalancedWatermarkLines(string text, string fontFamily)
    {
        var words = Regex.Matches(text, @"\S+");
        var count = words.Count;
        var maxLines = Math.Min(4, count);
        if (maxLines < 2)
        {
            yield break;
        }

        string Segment(int start, int end) => text[words[start].Index..(words[end - 1].Index + words[end - 1].Length)];
        var widths = new Dictionary<(int Start, int End), double>();
        double Width(int start, int end)
        {
            if (!widths.TryGetValue((start, end), out var value))
            {
                value = TextShaper.Shape(Segment(start, end), fontFamily, bold: true, italic: false, 100).Width;
                widths[(start, end)] = value;
            }

            return value;
        }

        var widest = new double[maxLines + 1, count + 1];
        var cuts = new int[maxLines + 1, count + 1];
        for (var end = 1; end <= count; end++)
        {
            widest[1, end] = Width(0, end);
        }

        for (var n = 2; n <= maxLines; n++)
        {
            for (var end = n; end <= count; end++)
            {
                widest[n, end] = double.PositiveInfinity;
                for (var cut = n - 1; cut < end; cut++)
                {
                    var candidate = Math.Max(widest[n - 1, cut], Width(cut, end));
                    if (candidate < widest[n, end])
                    {
                        widest[n, end] = candidate;
                        cuts[n, end] = cut;
                    }
                }
            }

            var lines = new string[n];
            var last = count;
            for (var row = n; row > 0; row--)
            {
                var first = row == 1 ? 0 : cuts[row, last];
                lines[row - 1] = Segment(first, last);
                last = first;
            }

            yield return lines;
        }
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
