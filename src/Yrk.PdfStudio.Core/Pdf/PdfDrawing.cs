using System.Globalization;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf;

/// <summary>A straight line or a filled box on a page, in points from the top-left corner.</summary>
/// <param name="Fill">Grey level 0 (black) to 1 (white) of a filled box; null draws a line from (X1, Y1) to (X2, Y2).</param>
public sealed record PdfStroke(double X1, double Y1, double X2, double Y2, double Thickness = 0.6, double? Fill = null);

/// <summary>One new page and what is drawn on it.</summary>
public sealed record PdfPageDrawing(double Width, double Height, IReadOnlyList<PdfStroke> Strokes);

/// <summary>Makes a new PDF with plain pages of lines and boxes (forms such as invoices; text is added afterwards).</summary>
public static class PdfDrawing
{
    public static byte[] Create(IReadOnlyList<PdfPageDrawing> pages, string? title = null)
    {
        if (pages.Count == 0)
        {
            throw new ArgumentException("A PDF needs at least one page.", nameof(pages));
        }

        var pdf = new CosNewDocument();
        var root = pdf.Reserve();
        var tree = pdf.Reserve();
        var kids = new List<CosObject>();
        foreach (var page in pages)
        {
            var content = new StringBuilder();
            foreach (var stroke in page.Strokes)
            {
                string N(double value) => CosWriter.FormatReal(value);
                if (stroke.Fill is { } gray)
                {
                    content.Append(CultureInfo.InvariantCulture, $"{N(gray)} g {N(stroke.X1)} {N(page.Height - stroke.Y2)} {N(stroke.X2 - stroke.X1)} {N(stroke.Y2 - stroke.Y1)} re f\n");
                }
                else
                {
                    content.Append(CultureInfo.InvariantCulture, $"0 G {N(stroke.Thickness)} w {N(stroke.X1)} {N(page.Height - stroke.Y1)} m {N(stroke.X2)} {N(page.Height - stroke.Y2)} l S\n");
                }
            }

            var stream = pdf.Add(new CosStream(new CosDictionary(), Encoding.ASCII.GetBytes(content.ToString())));
            kids.Add(pdf.Add(new CosDictionary()
                .Set("Type", new CosName("Page"))
                .Set("Parent", tree)
                .Set("MediaBox", new CosArray([new CosInteger(0), new CosInteger(0), new CosReal(page.Width), new CosReal(page.Height)]))
                .Set("Resources", new CosDictionary())
                .Set("Contents", stream)));
        }

        pdf.Set(tree, new CosDictionary()
            .Set("Type", new CosName("Pages"))
            .Set("Kids", new CosArray(kids))
            .Set("Count", new CosInteger(kids.Count)));
        pdf.Set(root, new CosDictionary().Set("Type", new CosName("Catalog")).Set("Pages", tree));
        var info = new CosDictionary().Set("Producer", CosString.FromText("YRK PDF")).Set("CreationDate", CosString.FromDate(DateTimeOffset.Now));
        if (title is { Length: > 0 })
        {
            info["Title"] = CosString.FromText(title);
        }

        return pdf.Build(root, pdf.Add(info));
    }
}
