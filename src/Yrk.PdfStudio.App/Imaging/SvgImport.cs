using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>
/// Reads the shapes of a simple SVG (a signature or logo exported from Illustrator, Inkscape or a
/// signing app) as vector artwork: path, rect, circle, ellipse, line, polyline and polygon, with fill,
/// stroke, opacity and transforms on shapes and groups. Text, gradients, masks and images are skipped.
/// </summary>
internal static partial class SvgImport
{
    /// <exception cref="FormatException">The file is not an SVG or has no shapes we can draw.</exception>
    public static PdfVectorArt Load(string path)
    {
        XDocument document;
        try
        {
            // No DTDs or external entities: the file is untrusted input.
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("The SVG file could not be read: " + ex.Message, ex);
        }

        if (document.Root is not { } root || root.Name.LocalName != "svg")
        {
            throw new FormatException("The file is not an SVG drawing.");
        }

        var shapes = new List<(Geometry Geometry, Color Color)>();
        Walk(root, Matrix.Identity, new Style(Colors.Black, 1, null, 1, 1, false), shapes);
        return VectorArtBuilder.Build(shapes, evenOdd: false)
            ?? throw new FormatException("The SVG has no shapes that can be used as a signature.");
    }

    private sealed record Style(Color? Fill, double FillOpacity, Color? Stroke, double StrokeWidth, double Opacity, bool EvenOdd);

    private static void Walk(XElement element, Matrix parent, Style inherited, List<(Geometry, Color)> shapes)
    {
        var name = element.Name.LocalName;
        if (name is "defs" or "clipPath" or "mask" or "symbol" or "title" or "desc" or "metadata" or "style" or "text")
        {
            return;
        }

        var matrix = Transform(element.Attribute("transform")?.Value) * parent;
        var style = ReadStyle(element, inherited);
        var geometry = name switch
        {
            "path" => PathData(element.Attribute("d")?.Value, style.EvenOdd),
            "rect" => new RectangleGeometry(new Rect(N(element, "x"), N(element, "y"), N(element, "width"), N(element, "height")), N(element, "rx"), N(element, "ry")),
            "circle" => new EllipseGeometry(new Point(N(element, "cx"), N(element, "cy")), N(element, "r"), N(element, "r")),
            "ellipse" => new EllipseGeometry(new Point(N(element, "cx"), N(element, "cy")), N(element, "rx"), N(element, "ry")),
            "line" => Lines([new Point(N(element, "x1"), N(element, "y1")), new Point(N(element, "x2"), N(element, "y2"))], closed: false),
            "polyline" => Lines(Points(element.Attribute("points")?.Value), closed: false),
            "polygon" => Lines(Points(element.Attribute("points")?.Value), closed: true),
            _ => null,
        };

        if (geometry is not null && !geometry.IsEmpty())
        {
            var transform = new MatrixTransform(matrix);
            if (style.Fill is { } fill && name is not ("line" or "polyline"))
            {
                var filled = geometry.Clone();
                filled.Transform = transform;
                shapes.Add((filled, WithAlpha(fill, style.FillOpacity * style.Opacity)));
            }

            if (style.Stroke is { } stroke && style.StrokeWidth > 0)
            {
                // A stroke becomes the filled outline of the line, at its width after the transform.
                var outline = geometry.GetWidenedPathGeometry(new Pen(Brushes.Black, style.StrokeWidth) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
                outline.Transform = transform;
                shapes.Add((outline, WithAlpha(stroke, style.Opacity)));
            }
        }

        foreach (var child in element.Elements())
        {
            Walk(child, matrix, style, shapes);
        }
    }

    private static Style ReadStyle(XElement element, Style inherited)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in element.Attributes())
        {
            values[attribute.Name.LocalName] = attribute.Value;
        }

        // style="fill:#000;stroke:none" wins over the attributes.
        foreach (var declaration in (element.Attribute("style")?.Value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon > 0)
            {
                values[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
            }
        }

        var fill = values.TryGetValue("fill", out var f) ? ParseColor(f, inherited.Fill) : inherited.Fill;
        var stroke = values.TryGetValue("stroke", out var s) ? ParseColor(s, inherited.Stroke) : inherited.Stroke;
        return new Style(
            fill,
            values.TryGetValue("fill-opacity", out var fo) ? Number(fo, 1) : inherited.FillOpacity,
            stroke,
            values.TryGetValue("stroke-width", out var sw) ? Number(sw, 1) : inherited.StrokeWidth,
            inherited.Opacity * (values.TryGetValue("opacity", out var o) ? Number(o, 1) : 1),
            values.TryGetValue("fill-rule", out var rule) ? rule == "evenodd" : inherited.EvenOdd);
    }

    private static Color? ParseColor(string value, Color? inherited)
    {
        value = value.Trim();
        if (value is "none" or "transparent")
        {
            return null;
        }

        if (value is "inherit")
        {
            return inherited;
        }

        if (value is "currentColor" || value.StartsWith("url(", StringComparison.Ordinal))
        {
            return Colors.Black; // Gradients and patterns are drawn in black.
        }

        if (value.Length == 4 && value[0] == '#')
        {
            value = "#" + new string([value[1], value[1], value[2], value[2], value[3], value[3]]);
        }

        if (RgbFunction().Match(value) is { Success: true } rgb)
        {
            return Color.FromRgb(Channel(rgb.Groups[1].Value), Channel(rgb.Groups[2].Value), Channel(rgb.Groups[3].Value));
        }

        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch (FormatException)
        {
            return Colors.Black;
        }

        static byte Channel(string v) => v.EndsWith('%')
            ? (byte)Math.Clamp(Number(v.TrimEnd('%'), 0) * 2.55, 0, 255)
            : (byte)Math.Clamp(Number(v, 0), 0, 255);
    }

    private static Color WithAlpha(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Clamp(color.A * opacity, 0, 255), color.R, color.G, color.B);

    private static Geometry? PathData(string? data, bool evenOdd)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        try
        {
            // WPF path markup is SVG path data with an optional fill rule in front.
            return Geometry.Parse((evenOdd ? "F0 " : "F1 ") + data);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Geometry? Lines(IReadOnlyList<Point> points, bool closed)
    {
        if (points.Count < 2)
        {
            return null;
        }

        var figure = new PathFigure(points[0], [new PolyLineSegment(points.Skip(1), isStroked: true)], closed);
        return new PathGeometry([figure]) { FillRule = FillRule.Nonzero };
    }

    private static List<Point> Points(string? text)
    {
        var numbers = NumberList(text);
        var points = new List<Point>();
        for (var i = 0; i + 1 < numbers.Count; i += 2)
        {
            points.Add(new Point(numbers[i], numbers[i + 1]));
        }

        return points;
    }

    private static Matrix Transform(string? text)
    {
        var result = Matrix.Identity;
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        // Applied right to left: the last function in the list acts on the shape first.
        var functions = TransformFunction().Matches(text).Select(m => (Name: m.Groups[1].Value, Args: NumberList(m.Groups[2].Value))).ToList();
        foreach (var (name, a) in functions.AsEnumerable().Reverse())
        {
            var m = Matrix.Identity;
            switch (name)
            {
                case "matrix" when a.Count == 6:
                    m = new Matrix(a[0], a[1], a[2], a[3], a[4], a[5]);
                    break;
                case "translate" when a.Count >= 1:
                    m.Translate(a[0], a.Count > 1 ? a[1] : 0);
                    break;
                case "scale" when a.Count >= 1:
                    m.Scale(a[0], a.Count > 1 ? a[1] : a[0]);
                    break;
                case "rotate" when a.Count >= 1:
                    if (a.Count >= 3)
                    {
                        m.RotateAt(a[0], a[1], a[2]);
                    }
                    else
                    {
                        m.Rotate(a[0]);
                    }

                    break;
                case "skewX" when a.Count == 1:
                    m.Skew(a[0], 0);
                    break;
                case "skewY" when a.Count == 1:
                    m.Skew(0, a[0]);
                    break;
            }

            result *= m;
        }

        return result;
    }

    private static List<double> NumberList(string? text) =>
        NumberPattern().Matches(text ?? string.Empty).Select(m => double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToList();

    private static double N(XElement element, string name) => Number(element.Attribute(name)?.Value, 0);

    private static double Number(string? text, double fallback) =>
        text is not null && NumberPattern().Match(text) is { Success: true } m
            ? double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture)
            : fallback;

    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)")]
    private static partial Regex TransformFunction();

    [GeneratedRegex(@"^rgb\(\s*([\d.]+%?)\s*,\s*([\d.]+%?)\s*,\s*([\d.]+%?)\s*\)$")]
    private static partial Regex RgbFunction();
}
