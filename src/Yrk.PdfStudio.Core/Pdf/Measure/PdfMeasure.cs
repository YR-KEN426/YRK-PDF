using System.Globalization;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Measure;

/// <summary>
/// A drawing scale stored in the PDF (§12.9, a /VP viewport with a /Measure dictionary), as written by CAD
/// programs and by our DWG converter.
/// </summary>
/// <param name="Ratio">As written in the file, for example "1:25" or "1 in = 10 ft".</param>
/// <param name="Unit">The real-world unit, for example "mm" or "ft"; empty when the file does not say (AutoCAD leaves it out).</param>
/// <param name="UnitsPerPoint">Real-world units per PDF point on the page.</param>
/// <param name="Area">Where the scale applies on the page as displayed; null for the whole page.</param>
public sealed record PdfPageScale(string Ratio, string Unit, double UnitsPerPoint, NormalizedRect? Area)
{
    /// <summary>The scale that applies at a point (the smallest viewport containing it).</summary>
    public static PdfPageScale? At(IReadOnlyList<PdfPageScale> scales, NormalizedPoint point) =>
        scales.Where(s => s.Area is not { } a || (point.X >= a.X && point.X <= a.X + a.Width && point.Y >= a.Y && point.Y <= a.Y + a.Height))
            .OrderBy(s => s.Area is { } a ? a.Width * a.Height : double.MaxValue)
            .FirstOrDefault();
}

public static class PdfScaleReader
{
    /// <summary>The scales of each page (an empty list for pages without one).</summary>
    public static IReadOnlyList<IReadOnlyList<PdfPageScale>> Read(byte[] pdf)
    {
        CosDocument document;
        try
        {
            document = CosDocument.Load(pdf);
        }
        catch (PdfException)
        {
            return [];
        }

        if (document.IsEncrypted)
        {
            return [];
        }

        var result = new List<IReadOnlyList<PdfPageScale>>();
        foreach (var page in document.GetPages())
        {
            var scales = new List<PdfPageScale>();
            if (document.Resolve(page.Dictionary["VP"]) is CosArray viewports)
            {
                foreach (var viewport in viewports.Items.Select(document.Resolve).OfType<CosDictionary>())
                {
                    if (Read(document, page, viewport) is { } scale)
                    {
                        scales.Add(scale);
                    }
                }
            }

            result.Add(scales);
        }

        return result;
    }

    private static PdfPageScale? Read(CosDocument document, CosPage page, CosDictionary viewport)
    {
        if (document.Resolve(viewport["Measure"]) is not CosDictionary measure
            || (document.Resolve(measure["Subtype"]) as CosName)?.Value is { } subtype && subtype != "RL"
            || document.Resolve(measure["X"]) is not CosArray { Count: > 0 } formats)
        {
            return null;
        }

        // Only the first number format matters for a value (later ones split it into smaller units).
        var first = document.Resolve(formats[0]) as CosDictionary;
        var factor = PdfMatrix.Number(document.Resolve(first?["C"]) ?? new CosReal(0));
        if (factor <= 0 || !double.IsFinite(factor))
        {
            return null;
        }

        var unit = (document.Resolve(first?["U"]) as CosString)?.ToText().Trim() ?? string.Empty;
        var ratio = (document.Resolve(measure["R"]) as CosString)?.ToText().Trim() ?? string.Empty;

        NormalizedRect? area = null;
        if (document.ToRect(viewport["BBox"]) is { } box)
        {
            var corners = new[] { FromUserSpace(page, box.X0, box.Y0), FromUserSpace(page, box.X1, box.Y1) };
            double x0 = corners.Min(c => c.X), y0 = corners.Min(c => c.Y), x1 = corners.Max(c => c.X), y1 = corners.Max(c => c.Y);
            if (!(x0 <= 0.001 && y0 <= 0.001 && x1 >= 0.999 && y1 >= 0.999))
            {
                area = new NormalizedRect(x0, y0, x1 - x0, y1 - y0);
            }
        }

        return new PdfPageScale(ratio, unit, factor, area);
    }

    /// <summary>The inverse of <see cref="CosPage.ToUserSpace"/>: a user-space point on the page as displayed (0..1).</summary>
    private static (double X, double Y) FromUserSpace(CosPage page, double x, double y)
    {
        var (x0, y0, x1, y1) = page.CropBox;
        double w = x1 - x0, h = y1 - y0;
        return page.Rotate switch
        {
            90 => ((y - y0) / h, (x - x0) / w),
            180 => ((x1 - x) / w, (y - y0) / h),
            270 => ((y1 - y) / h, (x1 - x) / w),
            _ => ((x - x0) / w, (y1 - y) / h),
        };
    }
}

/// <summary>Lengths and areas on a page, converted to real-world units.</summary>
public static class Measurement
{
    /// <summary>Real-world units the app offers, with their size in millimetres.</summary>
    public static IReadOnlyList<(string Unit, double Millimetres)> Units { get; } =
    [
        ("mm", 1),
        ("cm", 10),
        ("m", 1000),
        ("in", 25.4),
        ("ft", 304.8),
        ("pt", 25.4 / 72), // Typographic points, as used for paper and type sizes.
    ];

    /// <summary>Millimetres per unit for a unit name from a PDF or from the list; null when unknown.</summary>
    public static double? Millimetres(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "mm" or "millimeter" or "millimeters" or "millimetre" or "millimetres" => 1,
        "cm" or "centimeter" or "centimeters" => 10,
        "m" or "meter" or "meters" or "metre" or "metres" => 1000,
        "km" => 1_000_000,
        "in" or "inch" or "inches" or "\"" => 25.4,
        "ft" or "foot" or "feet" or "'" => 304.8,
        "yd" or "yard" or "yards" => 914.4,
        "pt" or "point" or "points" => 25.4 / 72,
        _ => null,
    };

    /// <summary>Length of a polyline given in PDF points.</summary>
    public static double Length(IReadOnlyList<(double X, double Y)> points, bool closed = false)
    {
        var length = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            length += Distance(points[i - 1], points[i]);
        }

        if (closed && points.Count > 2)
        {
            length += Distance(points[^1], points[0]);
        }

        return length;
    }

    /// <summary>Area of a simple polygon in square points (shoelace formula; the sign is dropped).</summary>
    public static double Area(IReadOnlyList<(double X, double Y)> points)
    {
        var sum = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return Math.Abs(sum) / 2;
    }

    /// <summary>A length with a sensible number of decimals for its size, in the current culture.</summary>
    public static string Format(double value, string unit, bool squared = false)
    {
        var magnitude = Math.Abs(value);
        var decimals = magnitude >= 1000 ? 0 : magnitude >= 100 ? 1 : magnitude >= 1 ? 2 : 3;
        return value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture) + " " + unit + (squared ? "²" : string.Empty);
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
}
