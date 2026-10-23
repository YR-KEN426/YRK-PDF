using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>
/// Vector signatures: pen strokes (with pressure) and typed names become filled outlines, which stay sharp
/// at any zoom and are written into the PDF as paths. Also converts between the artwork, WPF geometry,
/// a compact text form for saving, and preview bitmaps.
/// </summary>
internal static class VectorArtBuilder
{
    private const double Padding = 2;
    private const double TypedFontSize = 72;
    private const double PreviewScale = 3;
    private const double FlattenTolerance = 0.05;

    /// <summary>The outline of every stroke as InkCanvas draws it (pressure changes the width).</summary>
    public static PdfVectorArt? FromStrokes(StrokeCollection strokes)
    {
        if (strokes.Count == 0)
        {
            return null;
        }

        var shapes = strokes.Select(s => (Geometry: s.GetGeometry(s.DrawingAttributes), s.DrawingAttributes.Color)).ToList();
        return Build(shapes, evenOdd: false);
    }

    /// <summary>A typed name in a handwriting font, as letter outlines (no font has to be embedded).</summary>
    public static PdfVectorArt? FromText(string text, FontFamily font, Color color)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            TypedFontSize,
            Brushes.Black,
            1.0);
        var geometry = formatted.BuildGeometry(new Point(0, 0));
        return geometry.IsEmpty() ? null : Build([(geometry, color)], evenOdd: false);
    }

    /// <summary>Filled shapes from geometries, moved so the artwork starts at (padding, padding).</summary>
    public static PdfVectorArt? Build(IReadOnlyList<(Geometry Geometry, Color Color)> shapes, bool evenOdd)
    {
        var bounds = Rect.Empty;
        foreach (var (geometry, _) in shapes)
        {
            bounds.Union(geometry.Bounds);
        }

        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return null;
        }

        var offset = new Vector(Padding - bounds.X, Padding - bounds.Y);
        var paths = new List<PdfVectorPath>();
        foreach (var (geometry, color) in shapes)
        {
            var path = PathGeometry.CreateFromGeometry(geometry);
            var commands = ToCommands(path, offset);
            if (commands.Count > 0)
            {
                paths.Add(new PdfVectorPath(commands, new PdfColor(color.R, color.G, color.B, color.A), evenOdd || path.FillRule == FillRule.EvenOdd));
            }
        }

        return paths.Count == 0 ? null : new PdfVectorArt(bounds.Width + (2 * Padding), bounds.Height + (2 * Padding), paths);
    }

    private static List<PdfPathCommand> ToCommands(PathGeometry path, Vector offset)
    {
        // Arcs have no PDF equivalent: flatten geometries that contain them (this also applies the transform).
        if (path.Figures.Any(f => f.Segments.Any(s => s is ArcSegment)))
        {
            path = path.GetFlattenedPathGeometry(FlattenTolerance, ToleranceType.Absolute);
        }

        var matrix = path.Transform?.Value ?? Matrix.Identity;
        Point T(Point point) => matrix.Transform(point) + offset;
        var commands = new List<PdfPathCommand>();
        foreach (var figure in path.Figures)
        {
            var current = T(figure.StartPoint);
            commands.Add(PdfPathCommand.Move(current.X, current.Y));
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        current = Add(commands, T(line.Point));
                        break;
                    case PolyLineSegment poly:
                        foreach (var point in poly.Points)
                        {
                            current = Add(commands, T(point));
                        }

                        break;
                    case BezierSegment bezier:
                        current = AddCubic(commands, T(bezier.Point1), T(bezier.Point2), T(bezier.Point3));
                        break;
                    case PolyBezierSegment polyBezier:
                        for (var i = 0; i + 2 < polyBezier.Points.Count; i += 3)
                        {
                            current = AddCubic(commands, T(polyBezier.Points[i]), T(polyBezier.Points[i + 1]), T(polyBezier.Points[i + 2]));
                        }

                        break;
                    case QuadraticBezierSegment quadratic:
                        current = AddQuadratic(commands, current, T(quadratic.Point1), T(quadratic.Point2));
                        break;
                    case PolyQuadraticBezierSegment polyQuadratic:
                        for (var i = 0; i + 1 < polyQuadratic.Points.Count; i += 2)
                        {
                            current = AddQuadratic(commands, current, T(polyQuadratic.Points[i]), T(polyQuadratic.Points[i + 1]));
                        }

                        break;
                }
            }

            if (figure.IsClosed || figure.IsFilled)
            {
                commands.Add(PdfPathCommand.Closing);
            }
        }

        return commands;
    }

    private static Point Add(List<PdfPathCommand> commands, Point point)
    {
        commands.Add(PdfPathCommand.Line(point.X, point.Y));
        return point;
    }

    private static Point AddCubic(List<PdfPathCommand> commands, Point c1, Point c2, Point end)
    {
        commands.Add(PdfPathCommand.Cubic(c1.X, c1.Y, c2.X, c2.Y, end.X, end.Y));
        return end;
    }

    private static Point AddQuadratic(List<PdfPathCommand> commands, Point start, Point control, Point end)
    {
        var c1 = start + ((control - start) * (2.0 / 3));
        var c2 = end + ((control - end) * (2.0 / 3));
        return AddCubic(commands, c1, c2, end);
    }

    // --- WPF geometry and previews ---------------------------------------------------------

    public static Geometry ToGeometry(PdfVectorPath path)
    {
        var geometry = new StreamGeometry { FillRule = path.EvenOdd ? FillRule.EvenOdd : FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            var open = false;
            foreach (var command in path.Commands)
            {
                switch (command.Op)
                {
                    case PdfPathOp.MoveTo:
                        context.BeginFigure(new Point(command.X1, command.Y1), isFilled: true, isClosed: false);
                        open = true;
                        break;
                    case PdfPathOp.LineTo when open:
                        context.LineTo(new Point(command.X1, command.Y1), isStroked: false, isSmoothJoin: false);
                        break;
                    case PdfPathOp.CubicTo when open:
                        context.BezierTo(new Point(command.X1, command.Y1), new Point(command.X2, command.Y2), new Point(command.X3, command.Y3), isStroked: false, isSmoothJoin: false);
                        break;
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>A transparent bitmap of the artwork, for the overlay and the saved-signature list.</summary>
    public static BitmapSource RenderPreview(PdfVectorArt art)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(PreviewScale, PreviewScale));
            foreach (var path in art.Paths)
            {
                var brush = new SolidColorBrush(Color.FromArgb(path.Color.A, path.Color.R, path.Color.G, path.Color.B));
                brush.Freeze();
                context.DrawGeometry(brush, null, ToGeometry(path));
            }
        }

        return ImageConversion.Render(
            visual,
            Math.Max(1, (int)Math.Ceiling(art.Width * PreviewScale)),
            Math.Max(1, (int)Math.Ceiling(art.Height * PreviewScale)));
    }

    // --- Saving ----------------------------------------------------------------------------

    /// <summary>"w h|#RRGGBBAA n|M x y L x y C … Z|…": compact and culture-independent.</summary>
    public static string Serialize(PdfVectorArt art)
    {
        var text = new StringBuilder();
        text.Append(F(art.Width)).Append(' ').Append(F(art.Height));
        foreach (var path in art.Paths)
        {
            text.Append('|').Append(CultureInfo.InvariantCulture, $"#{path.Color.R:X2}{path.Color.G:X2}{path.Color.B:X2}{path.Color.A:X2}")
                .Append(path.EvenOdd ? " e" : " n").Append('|');
            foreach (var c in path.Commands)
            {
                text.Append(c.Op switch
                {
                    PdfPathOp.MoveTo => $"M{F(c.X1)} {F(c.Y1)} ",
                    PdfPathOp.LineTo => $"L{F(c.X1)} {F(c.Y1)} ",
                    PdfPathOp.CubicTo => $"C{F(c.X1)} {F(c.Y1)} {F(c.X2)} {F(c.Y2)} {F(c.X3)} {F(c.Y3)} ",
                    _ => "Z ",
                });
            }
        }

        return text.ToString();

        static string F(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);
    }

    /// <exception cref="FormatException">The text is not a saved artwork.</exception>
    public static PdfVectorArt Deserialize(string text)
    {
        var parts = text.Split('|');
        var size = parts[0].Split(' ');
        var paths = new List<PdfVectorPath>();
        for (var i = 1; i + 1 < parts.Length; i += 2)
        {
            var style = parts[i].Split(' ');
            var hex = style[0].TrimStart('#');
            var color = new PdfColor(
                byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            var commands = new List<PdfPathCommand>();
            var op = (char?)null;
            var numbers = new List<double>();
            foreach (var token in parts[i + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                // A command letter is glued to its first number ("M12.5"); the other numbers follow as tokens.
                var number = token;
                if (char.IsLetter(token[0]))
                {
                    Emit(commands, op, numbers);
                    op = token[0];
                    numbers.Clear();
                    number = token[1..];
                }

                if (number.Length > 0)
                {
                    numbers.Add(P(number));
                }
            }

            Emit(commands, op, numbers);
            paths.Add(new PdfVectorPath(commands, color, style.Length > 1 && style[1] == "e"));
        }

        return new PdfVectorArt(P(size[0]), P(size[1]), paths);

        static double P(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        static void Emit(List<PdfPathCommand> commands, char? op, List<double> n)
        {
            switch (op)
            {
                case 'M' when n.Count >= 2:
                    commands.Add(PdfPathCommand.Move(n[0], n[1]));
                    break;
                case 'L' when n.Count >= 2:
                    commands.Add(PdfPathCommand.Line(n[0], n[1]));
                    break;
                case 'C' when n.Count >= 6:
                    commands.Add(PdfPathCommand.Cubic(n[0], n[1], n[2], n[3], n[4], n[5]));
                    break;
                case 'Z':
                    commands.Add(PdfPathCommand.Closing);
                    break;
                case null:
                    break;
                default:
                    throw new FormatException("The saved drawing is damaged.");
            }
        }
    }
}
