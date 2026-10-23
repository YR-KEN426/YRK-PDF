using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Cad;

/// <summary>
/// Converts the model space of a DWG or DXF drawing (read with ACadSharp, MIT) to a vector PDF page with
/// one PDF layer per CAD layer, so drawings can be viewed, searched, measured and shared without AutoCAD.
/// </summary>
public static partial class CadToPdf
{
    static CadToPdf() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // ANSI_874 (Thai) and others

    /// <exception cref="PdfException">The file cannot be read or has nothing to draw.</exception>
    public static CadPdfResult Convert(string path, CadConversionOptions? options = null)
    {
        CadDocument document;
        try
        {
            document = Path.GetExtension(path).Equals(".dxf", StringComparison.OrdinalIgnoreCase)
                ? DxfReader.Read(path)
                : DwgReader.Read(path);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException))
        {
            // The reader throws many kinds of exceptions for versions and objects it does not support.
            throw new PdfException("The drawing could not be read: " + ex.Message, ex);
        }

        var drawing = new CadDrawing(document, options ?? new CadConversionOptions());
        drawing.DrawModelSpace();
        drawing.DrawLayouts();
        return drawing.Write(Path.GetFileName(path));
    }

    [GeneratedRegex(@"\\U\+([0-9A-Fa-f]{4})")]
    internal static partial Regex UnicodeEscape();
}

/// <summary>Collects the drawing as paths and texts in world coordinates, then writes the page.</summary>
internal sealed class CadDrawing(CadDocument document, CadConversionOptions options)
{
    private const int MaxBlockDepth = 16;
    private const int MaxHatchLines = 4000;
    private const double DefaultLineWeightMm = 0.25;
    private const double MinLineWeightMm = 0.1;
    private const double MarginMm = 10;
    private const double PointsPerMm = 72 / 25.4;
    private static readonly double[] Scales = [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000, 20000, 50000, 100000];

    private const double LayoutMarginMm = 5;

    private readonly List<Sheet> _sheets = [];
    private readonly Dictionary<string, int> _skipped = new(StringComparer.Ordinal);
    private Sheet _sheet = new("Model", IsLayout: false, UnitMm: 1);

    /// <summary>While drawing a layout viewport: its frame on the paper; shapes are clipped to it.</summary>
    private Clip? _clip;

    /// <summary>While drawing a layout viewport: the layers frozen in that viewport.</summary>
    private HashSet<string>? _frozenLayers;

    private List<Shape> _shapes => _sheet.Shapes;

    private List<PendingText> _texts => _sheet.Texts;

    /// <summary>A rectangle on the paper (paper-space units).</summary>
    private readonly record struct Clip(double X0, double Y0, double X1, double Y1)
    {
        public bool Contains(double x, double y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
    }

    /// <summary>A viewport on a layout: where it sits on the paper and the model scale inside it.</summary>
    /// <param name="ModelUnitsPerPaperUnit">Model units shown per paper unit (1 / viewport zoom).</param>
    private sealed record SheetViewport(Clip Frame, double ModelUnitsPerPaperUnit);

    /// <summary>One PDF page: the model space, or a layout (paper space with its viewports).</summary>
    /// <param name="UnitMm">For layouts: millimetres per paper unit.</param>
    private sealed record Sheet(string Name, bool IsLayout, double UnitMm)
    {
        public List<Shape> Shapes { get; } = [];

        public List<PendingText> Texts { get; } = [];

        public List<SheetViewport> Viewports { get; } = [];

        public double MinX { get; set; } = double.MaxValue;

        public double MinY { get; set; } = double.MaxValue;

        public double MaxX { get; set; } = double.MinValue;

        public double MaxY { get; set; } = double.MinValue;

        public bool IsEmpty => (Shapes.Count == 0 && Texts.Count == 0) || MinX > MaxX;
    }

    private readonly record struct Rgb(byte R, byte G, byte B);

    private readonly record struct Cmd(char Op, double X1, double Y1, double X2 = 0, double Y2 = 0, double X3 = 0, double Y3 = 0);

    /// <summary>Inherited drawing state: block transform, and what ByBlock and layer "0" resolve to.</summary>
    private readonly record struct Context(PdfMatrix Matrix, Layer? Layer, Rgb Color, double WidthMm, LineType? LineType, int Depth);

    private sealed class Shape(Layer layer, Rgb color, double widthMm, double[]? dash)
    {
        public Layer Layer { get; } = layer;

        public Rgb Color { get; } = color;

        public double WidthMm { get; } = widthMm;

        public double[]? Dash { get; } = dash;

        /// <summary>Wide polylines: a width in drawing units that overrides the lineweight.</summary>
        public double DrawingWidth { get; set; }

        public bool Fill { get; set; }

        public List<Cmd> Path { get; } = [];

        /// <summary>Set for model space shown through a layout viewport.</summary>
        public Clip? Clip { get; set; }

        /// <summary>Hatch pattern lines, drawn clipped to <see cref="Path"/>, each with its dash phase.</summary>
        public List<(double X0, double Y0, double X1, double Y1, double Phase)>? PatternLines { get; set; }

        public double[]? PatternDash { get; set; }
    }

    private sealed record PendingText(string Text, double X, double Y, double Height, double Rotation, double WidthFactor,
        double Align, CadTextVertical Vertical, string Font, Rgb Color, Layer Layer);

    private static Context Root => new(PdfMatrix.Identity, null, new Rgb(0, 0, 0), DefaultLineWeightMm, null, 0);

    public void DrawModelSpace()
    {
        _sheet = new Sheet("Model", IsLayout: false, UnitMm: UnitToMm(document.Header.InsUnits));
        _sheets.Add(_sheet);
        foreach (var entity in document.Entities)
        {
            Draw(entity, Root);
        }
    }

    /// <summary>
    /// Each paper space layout (the sheets that are plotted, with title block and viewports) becomes a page
    /// at 1:1 on the paper. Viewports show the model space at their own scale, clipped to their frame,
    /// without the layers frozen in them.
    /// </summary>
    public void DrawLayouts()
    {
        var layouts = document.Layouts
            .Where(l => !string.Equals(l.Name, ACadSharp.Objects.Layout.ModelLayoutName, StringComparison.OrdinalIgnoreCase) && l.AssociatedBlock is not null)
            .OrderBy(l => l.TabOrder)
            .ToList();
        foreach (var layout in layouts)
        {
            var unitMm = layout.PaperUnits == ACadSharp.Objects.PlotPaperUnits.Inches ? 25.4 : 1;
            _sheet = new Sheet(layout.Name, IsLayout: true, unitMm);
            var entities = layout.AssociatedBlock.Entities.ToList();

            // Viewports first, so the title block and notes on the paper are drawn over the model.
            foreach (var viewport in entities.OfType<Viewport>())
            {
                DrawViewport(viewport);
            }

            foreach (var entity in entities.Where(e => e is not Viewport))
            {
                Draw(entity, Root);
            }

            if (!_sheet.IsEmpty)
            {
                _sheets.Add(_sheet);
            }
        }

        _sheet = _sheets[0];
    }

    private void DrawViewport(Viewport viewport)
    {
        if (viewport.RepresentsPaper || viewport.Id == 1 || viewport.Width <= 0 || viewport.Height <= 0 || viewport.ViewHeight <= 0)
        {
            return; // Viewport 1 is the paper itself.
        }

        var direction = Normalize(viewport.ViewDirection);
        if (Math.Abs(direction.Z) < 0.999)
        {
            _skipped["3D viewport"] = _skipped.GetValueOrDefault("3D viewport") + 1;
            return;
        }

        // Model → view (target at the origin, view twist) → paper (view centre on the viewport centre, zoomed).
        var zoom = viewport.Height / viewport.ViewHeight;
        double cos = Math.Cos(viewport.TwistAngle), sin = Math.Sin(viewport.TwistAngle);
        var matrix = new PdfMatrix(1, 0, 0, 1, -viewport.ViewTarget.X, -viewport.ViewTarget.Y)
            .Then(new PdfMatrix(cos, sin, -sin, cos, 0, 0))
            .Then(new PdfMatrix(zoom, 0, 0, zoom, viewport.Center.X - (zoom * viewport.ViewCenter.X), viewport.Center.Y - (zoom * viewport.ViewCenter.Y)));

        var frame = new Clip(
            viewport.Center.X - (viewport.Width / 2),
            viewport.Center.Y - (viewport.Height / 2),
            viewport.Center.X + (viewport.Width / 2),
            viewport.Center.Y + (viewport.Height / 2));
        _sheet.Viewports.Add(new SheetViewport(frame, 1 / zoom));
        Extend(frame.X0, frame.Y0);
        Extend(frame.X1, frame.Y1);

        // The frame is plotted like AutoCAD does, unless its layer is off or set not to plot (often "Defpoints").
        if (viewport.Layer is { } frameLayer && frameLayer.IsOn && frameLayer.PlotFlag
            && !frameLayer.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase))
        {
            var border = new Shape(frameLayer, ResolveColor(viewport.Color, frameLayer, Root), ResolveWidth(viewport.LineWeight, frameLayer, Root), null);
            AddPoints(border, PdfMatrix.Identity, [(frame.X0, frame.Y0), (frame.X1, frame.Y0), (frame.X1, frame.Y1), (frame.X0, frame.Y1)], closed: true);
            Add(border);
        }

        _clip = frame;
        _frozenLayers = viewport.FrozenLayers.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var context = Root with { Matrix = matrix };
            foreach (var entity in document.Entities)
            {
                Draw(entity, context);
            }
        }
        finally
        {
            _clip = null;
            _frozenLayers = null;
        }
    }

    // --- Entities ---------------------------------------------------------------------------

    private void Draw(Entity entity, Context context)
    {
        if (entity.IsInvisible)
        {
            return;
        }

        var layer = entity.Layer is null || (entity.Layer.Name == "0" && context.Layer is not null) ? context.Layer ?? entity.Layer! : entity.Layer;
        if (layer is null || (_frozenLayers is not null && _frozenLayers.Contains(layer.Name)))
        {
            return;
        }

        var color = ResolveColor(entity.Color, layer, context);
        var width = ResolveWidth(entity.LineWeight, layer, context);
        var lineType = ResolveLineType(entity.LineType, layer, context);
        Shape NewShape() => new(layer, color, width, Dash(lineType, entity.LineTypeScale, context.Matrix));

        switch (entity)
        {
            case Line line:
            {
                var shape = NewShape();
                MoveTo(shape, context.Matrix, line.StartPoint.X, line.StartPoint.Y);
                LineTo(shape, context.Matrix, line.EndPoint.X, line.EndPoint.Y);
                Add(shape);
                break;
            }

            case LwPolyline polyline when polyline.Vertices.Count > 0:
            {
                var m = Ocs(polyline.Normal, polyline.Elevation).Then(context.Matrix);
                var shape = NewShape();
                shape.DrawingWidth = polyline.ConstantWidth * Scale(context.Matrix);
                var points = polyline.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList();
                AddBulgePolyline(shape, m, points, polyline.IsClosed);
                Add(shape);
                break;
            }

            case Polyline2D polyline:
            {
                var m = Ocs(polyline.Normal, polyline.Elevation).Then(context.Matrix);
                var shape = NewShape();
                var points = polyline.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList();
                if (points.Count > 0)
                {
                    AddBulgePolyline(shape, m, points, polyline.IsClosed);
                    Add(shape);
                }

                break;
            }

            case Polyline3D polyline:
            {
                var shape = NewShape();
                var points = polyline.Vertices.Select(v => (v.Location.X, v.Location.Y, 0.0)).ToList();
                if (points.Count > 0)
                {
                    AddBulgePolyline(shape, context.Matrix, points, polyline.IsClosed);
                    Add(shape);
                }

                break;
            }

            case Arc arc:
            {
                var m = Ocs(arc.Normal, arc.Center.Z).Then(context.Matrix);
                var sweep = arc.EndAngle - arc.StartAngle;
                while (sweep <= 0)
                {
                    sweep += 2 * Math.PI;
                }

                var shape = NewShape();
                AddEllipseArc(shape, m, arc.Center.X, arc.Center.Y, arc.Radius, 0, 0, arc.Radius, arc.StartAngle, sweep, newFigure: true);
                Add(shape);
                break;
            }

            case Circle circle:
            {
                var m = Ocs(circle.Normal, circle.Center.Z).Then(context.Matrix);
                var shape = NewShape();
                AddEllipseArc(shape, m, circle.Center.X, circle.Center.Y, circle.Radius, 0, 0, circle.Radius, 0, 2 * Math.PI, newFigure: true);
                shape.Path.Add(new Cmd('h', 0, 0));
                Add(shape);
                break;
            }

            case Ellipse ellipse:
            {
                // In world coordinates: the minor axis is normal × major, scaled by the ratio.
                var major = ellipse.MajorAxisEndPoint;
                var minor = Cross(Normalize(ellipse.Normal), major);
                var sweep = ellipse.EndParameter - ellipse.StartParameter;
                while (sweep <= 0)
                {
                    sweep += 2 * Math.PI;
                }

                var shape = NewShape();
                AddEllipseArc(shape, context.Matrix, ellipse.Center.X, ellipse.Center.Y, major.X, major.Y,
                    minor.X * ellipse.RadiusRatio, minor.Y * ellipse.RadiusRatio, ellipse.StartParameter, sweep, newFigure: true);
                Add(shape);
                break;
            }

            case Spline spline:
            {
                var points = SampleSpline(spline.ControlPoints, spline.Knots, spline.Weights, spline.Degree, spline.FitPoints);
                if (points.Count > 1)
                {
                    var shape = NewShape();
                    AddPoints(shape, context.Matrix, points, spline.IsClosed);
                    Add(shape);
                }

                break;
            }

            case Solid solid:
            {
                // Corners are in the order 1, 2, 4, 3 around the outline.
                var m = Ocs(solid.Normal, solid.FirstCorner.Z).Then(context.Matrix);
                var shape = NewShape();
                shape.Fill = true;
                MoveTo(shape, m, solid.FirstCorner.X, solid.FirstCorner.Y);
                LineTo(shape, m, solid.SecondCorner.X, solid.SecondCorner.Y);
                LineTo(shape, m, solid.FourthCorner.X, solid.FourthCorner.Y);
                LineTo(shape, m, solid.ThirdCorner.X, solid.ThirdCorner.Y);
                shape.Path.Add(new Cmd('h', 0, 0));
                Add(shape);
                break;
            }

            case AttributeDefinition:
                break; // Templates: the values come with each insert.

            case AttributeEntity attribute when attribute.Flags.HasFlag(AttributeFlags.Hidden):
                break;

            case TextEntity text:
                AddText(text, layer, color, context);
                break;

            case MText text:
                AddMText(text, layer, color, context);
                break;

            case Insert insert:
                DrawInsert(insert, layer, color, width, lineType, context);
                break;

            case Dimension dimension when dimension.Block is not null:
                DrawBlockEntities(dimension.Block.Entities, new Context(context.Matrix, layer, color, width, lineType, context.Depth + 1));
                break;

            case Hatch hatch:
                AddHatch(hatch, NewShape(), context);
                break;

            case Leader leader:
                AddLeader(leader, NewShape, context);
                break;

            case Point or Viewport or Seqend or Dimension:
                break;

            default:
                var name = entity.GetType().Name;
                _skipped[name] = _skipped.GetValueOrDefault(name) + 1;
                break;
        }
    }

    private void DrawInsert(Insert insert, Layer layer, Rgb color, double width, LineType? lineType, Context context)
    {
        if (insert.Block is not { } block || context.Depth >= MaxBlockDepth)
        {
            return;
        }

        var basePoint = block.BlockEntity?.BasePoint ?? XYZ.Zero;
        var ocs = Ocs(insert.Normal, insert.InsertPoint.Z);
        double cos = Math.Cos(insert.Rotation), sin = Math.Sin(insert.Rotation);
        var rows = Math.Max(1, (int)insert.RowCount);
        var columns = Math.Max(1, (int)insert.ColumnCount);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                // Block → move base point to origin → scale → array offset → rotate → place → OCS → parent.
                var m = new PdfMatrix(1, 0, 0, 1, -basePoint.X, -basePoint.Y)
                    .Then(new PdfMatrix(insert.XScale, 0, 0, insert.YScale, column * insert.ColumnSpacing, row * insert.RowSpacing))
                    .Then(new PdfMatrix(cos, sin, -sin, cos, insert.InsertPoint.X, insert.InsertPoint.Y))
                    .Then(ocs)
                    .Then(context.Matrix);
                DrawBlockEntities(block.Entities, new Context(m, layer, color, width, lineType, context.Depth + 1));
            }
        }

        // Attribute values sit in world coordinates already.
        foreach (var attribute in insert.Attributes)
        {
            Draw(attribute, new Context(context.Matrix, layer, color, width, lineType, context.Depth + 1));
        }
    }

    private void DrawBlockEntities(IEnumerable<Entity> entities, Context context)
    {
        foreach (var entity in entities)
        {
            Draw(entity, context);
        }
    }

    private void AddLeader(Leader leader, Func<Shape> newShape, Context context)
    {
        var vertices = leader.Vertices.ToList();
        if (vertices.Count < 2)
        {
            return;
        }

        var shape = newShape();
        AddPoints(shape, context.Matrix, vertices.Select(v => (v.X, v.Y)).ToList(), closed: false);
        Add(shape);

        if (leader.ArrowHeadEnabled)
        {
            var size = (leader.Style?.ArrowSize ?? 2.5) * (leader.Style?.ScaleFactor is > 0 and var f ? f : 1);
            double dx = vertices[0].X - vertices[1].X, dy = vertices[0].Y - vertices[1].Y;
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length > 0)
            {
                dx /= length;
                dy /= length;
                var arrow = newShape();
                arrow.Fill = true;
                var tip = (vertices[0].X, vertices[0].Y);
                var back = (X: tip.X - (dx * size), Y: tip.Y - (dy * size));
                MoveTo(arrow, context.Matrix, tip.X, tip.Y);
                LineTo(arrow, context.Matrix, back.X - (dy * size / 6), back.Y + (dx * size / 6));
                LineTo(arrow, context.Matrix, back.X + (dy * size / 6), back.Y - (dx * size / 6));
                arrow.Path.Add(new Cmd('h', 0, 0));
                Add(arrow);
            }
        }
    }

    // --- Text -------------------------------------------------------------------------------

    private void AddText(TextEntity text, Layer layer, Rgb color, Context context)
    {
        var value = DecodeText(text.Value);
        if (value.Length == 0 || text.Height <= 0)
        {
            return;
        }

        var horizontal = text.HorizontalAlignment;
        var vertical = text.VerticalAlignment;
        var useAlignment = !(horizontal is TextHorizontalAlignment.Left or TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Fit
                             && vertical == TextVerticalAlignmentType.Baseline);
        var anchor = useAlignment ? text.AlignmentPoint : text.InsertPoint;
        var align = horizontal switch
        {
            TextHorizontalAlignment.Center or TextHorizontalAlignment.Middle => 0.5,
            TextHorizontalAlignment.Right => 1,
            _ => 0,
        };
        var verticalAnchor = horizontal == TextHorizontalAlignment.Middle
            ? CadTextVertical.Middle
            : vertical switch
            {
                TextVerticalAlignmentType.Bottom => CadTextVertical.Bottom,
                TextVerticalAlignmentType.Middle => CadTextVertical.Middle,
                TextVerticalAlignmentType.Top => CadTextVertical.Top,
                _ => CadTextVertical.Baseline,
            };

        var m = Ocs(text.Normal, anchor.Z).Then(context.Matrix);
        PlaceText(value, m, anchor.X, anchor.Y, text.Height, text.Rotation, text.WidthFactor, align, verticalAnchor, text.Style, layer, color);
    }

    private void AddMText(MText text, Layer layer, Rgb color, Context context)
    {
        var lines = DecodeText(text.PlainText).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.All(l => l.Trim().Length == 0) || text.Height <= 0)
        {
            return;
        }

        var (align, row) = text.AttachmentPoint switch
        {
            AttachmentPointType.TopLeft => (0.0, 0),
            AttachmentPointType.TopCenter => (0.5, 0),
            AttachmentPointType.TopRight => (1.0, 0),
            AttachmentPointType.MiddleLeft => (0.0, 1),
            AttachmentPointType.MiddleCenter => (0.5, 1),
            AttachmentPointType.MiddleRight => (1.0, 1),
            AttachmentPointType.BottomLeft => (0.0, 2),
            AttachmentPointType.BottomCenter => (0.5, 2),
            _ => (1.0, 2),
        };

        var height = text.Height;
        var spacing = height * 5 / 3 * (text.LineSpacing > 0 ? text.LineSpacing : 1);
        var total = height + ((lines.Length - 1) * spacing);
        var firstBaseline = row switch
        {
            0 => -height,
            1 => (total / 2) - height,
            _ => (lines.Length - 1) * spacing,
        };

        var rotation = text.Rotation;
        if (rotation == 0 && (text.AlignmentPoint.X != 0 || text.AlignmentPoint.Y != 0))
        {
            rotation = Math.Atan2(text.AlignmentPoint.Y, text.AlignmentPoint.X); // The text's x direction.
        }

        var m = Ocs(text.Normal, text.InsertPoint.Z).Then(context.Matrix);
        double cos = Math.Cos(rotation), sin = Math.Sin(rotation);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            var up = firstBaseline - (i * spacing);
            PlaceText(line, m, text.InsertPoint.X - (sin * up), text.InsertPoint.Y + (cos * up), height, rotation, 1, align, CadTextVertical.Baseline, text.Style, layer, color);
        }
    }

    /// <summary>Transforms a text anchor, height and direction into world coordinates and keeps it.</summary>
    private void PlaceText(string value, PdfMatrix m, double x, double y, double height, double rotation, double widthFactor,
        double align, CadTextVertical vertical, TextStyle? style, Layer layer, Rgb color)
    {
        var anchor = m.Transform(x, y);
        double cos = Math.Cos(rotation), sin = Math.Sin(rotation);
        var along = Linear(m, cos, sin);
        var up = Linear(m, -sin, cos);
        var alongLength = Math.Sqrt((along.X * along.X) + (along.Y * along.Y));
        var upLength = Math.Sqrt((up.X * up.X) + (up.Y * up.Y));
        if (alongLength == 0 || upLength == 0)
        {
            return;
        }

        var worldHeight = height * upLength;
        var factor = (widthFactor > 0 ? widthFactor : 1) * alongLength / upLength;
        var font = style?.Filename is { Length: > 0 } file ? file : "arial.ttf";
        if (_clip is { } clip)
        {
            // Text is written separately and cannot be clipped: keep only what starts inside the viewport.
            if (clip.Contains(anchor.X, anchor.Y))
            {
                _texts.Add(new PendingText(value, anchor.X, anchor.Y, worldHeight, Math.Atan2(along.Y, along.X) * 180 / Math.PI,
                    factor, align, vertical, font, color, layer));
            }

            return;
        }

        _texts.Add(new PendingText(value, anchor.X, anchor.Y, worldHeight, Math.Atan2(along.Y, along.X) * 180 / Math.PI,
            factor, align, vertical, font, color, layer));
        Extend(anchor.X - worldHeight, anchor.Y - worldHeight);
        Extend(anchor.X + worldHeight, anchor.Y + worldHeight);
    }

    /// <summary>AutoCAD control codes: %%c Ø, %%d °, %%p ±, %%% %, %%u/%%o (underline/overline) dropped, \U+XXXX.</summary>
    private static string DecodeText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var decoded = text
            .Replace("%%c", "Ø", StringComparison.OrdinalIgnoreCase)
            .Replace("%%d", "°", StringComparison.OrdinalIgnoreCase)
            .Replace("%%p", "±", StringComparison.OrdinalIgnoreCase)
            .Replace("%%u", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("%%o", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("%%%", "%", StringComparison.Ordinal);
        return CadToPdf.UnicodeEscape().Replace(decoded, match => ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
    }

    // --- Hatches ----------------------------------------------------------------------------

    private void AddHatch(Hatch hatch, Shape shape, Context context)
    {
        var m = Ocs(hatch.Normal, hatch.Elevation).Then(context.Matrix);
        var ocsPoints = new List<(double X, double Y)>();
        foreach (var path in hatch.Paths)
        {
            var start = shape.Path.Count;
            if (path.Edges.Count == 0)
            {
                continue;
            }

            foreach (var edge in path.Edges)
            {
                var newFigure = shape.Path.Count == start;
                switch (edge)
                {
                    case Hatch.BoundaryPath.Polyline polyline:
                    {
                        var vertices = polyline.Vertices.ToList();
                        var bulges = polyline.Bulges.ToList();
                        var points = vertices.Select((v, i) => (v.X, v.Y, polyline.HasBulge && i < bulges.Count ? bulges[i] : 0.0)).ToList();
                        AddBulgePolyline(shape, m, points, polyline.IsClosed || true);
                        ocsPoints.AddRange(points.Select(p => (p.X, p.Y)));
                        break;
                    }

                    case Hatch.BoundaryPath.Line line:
                        if (newFigure)
                        {
                            MoveTo(shape, m, line.Start.X, line.Start.Y);
                        }
                        else
                        {
                            LineTo(shape, m, line.Start.X, line.Start.Y);
                        }

                        LineTo(shape, m, line.End.X, line.End.Y);
                        ocsPoints.Add((line.Start.X, line.Start.Y));
                        ocsPoints.Add((line.End.X, line.End.Y));
                        break;

                    case Hatch.BoundaryPath.Arc arc:
                    {
                        var (startAngle, sweep) = EdgeSweep(arc.StartAngle, arc.EndAngle, arc.CounterClockWise);
                        AddEllipseArc(shape, m, arc.Center.X, arc.Center.Y, arc.Radius, 0, 0, arc.Radius, startAngle, sweep, newFigure);
                        ocsPoints.Add((arc.Center.X - arc.Radius, arc.Center.Y - arc.Radius));
                        ocsPoints.Add((arc.Center.X + arc.Radius, arc.Center.Y + arc.Radius));
                        break;
                    }

                    case Hatch.BoundaryPath.Ellipse ellipse:
                    {
                        var (startAngle, sweep) = EdgeSweep(ellipse.StartAngle, ellipse.EndAngle, ellipse.CounterClockWise);
                        var major = ellipse.MajorAxisEndPoint;
                        AddEllipseArc(shape, m, ellipse.Center.X, ellipse.Center.Y, major.X, major.Y,
                            -major.Y * ellipse.RadiusRatio, major.X * ellipse.RadiusRatio, startAngle, sweep, newFigure);
                        var r = Math.Sqrt((major.X * major.X) + (major.Y * major.Y));
                        ocsPoints.Add((ellipse.Center.X - r, ellipse.Center.Y - r));
                        ocsPoints.Add((ellipse.Center.X + r, ellipse.Center.Y + r));
                        break;
                    }

                    case Hatch.BoundaryPath.Spline spline:
                    {
                        var points = SampleSpline(
                            spline.ControlPoints.Select(p => new XYZ(p.X, p.Y, 0)).ToList(),
                            spline.Knots.ToList(),
                            spline.Weights.ToList(),
                            spline.Degree,
                            spline.FitPoints.Select(p => new XYZ(p.X, p.Y, 0)).ToList());
                        for (var i = 0; i < points.Count; i++)
                        {
                            if (i == 0 && newFigure)
                            {
                                MoveTo(shape, m, points[i].X, points[i].Y);
                            }
                            else
                            {
                                LineTo(shape, m, points[i].X, points[i].Y);
                            }
                        }

                        ocsPoints.AddRange(points);
                        break;
                    }
                }
            }

            if (shape.Path.Count > start && shape.Path[^1].Op != 'h')
            {
                shape.Path.Add(new Cmd('h', 0, 0));
            }
        }

        if (shape.Path.Count == 0)
        {
            return;
        }

        if (hatch.IsSolid || hatch.Pattern is null || hatch.Pattern.Lines.Count == 0)
        {
            shape.Fill = true;
            Add(shape);
            return;
        }

        // A pattern: families of parallel (dashed) lines, clipped to the boundary when written.
        double minX = ocsPoints.Min(p => p.X), maxX = ocsPoints.Max(p => p.X), minY = ocsPoints.Min(p => p.Y), maxY = ocsPoints.Max(p => p.Y);
        (double X, double Y)[] corners = [(minX, minY), (maxX, minY), (minX, maxY), (maxX, maxY)];
        var lines = new List<(double, double, double, double, double)>();
        double[]? dash = null;
        foreach (var family in hatch.Pattern.Lines)
        {
            double dx = Math.Cos(family.Angle), dy = Math.Sin(family.Angle);
            double nx = -dy, ny = dx;
            var spacing = (family.Offset.X * nx) + (family.Offset.Y * ny);
            var shift = (family.Offset.X * dx) + (family.Offset.Y * dy);
            if (Math.Abs(spacing) < 1e-9)
            {
                continue;
            }

            var baseN = (family.BasePoint.X * nx) + (family.BasePoint.Y * ny);
            var projections = corners.Select(c => (c.X * nx) + (c.Y * ny)).ToList();
            var k0 = (int)Math.Floor((projections.Min() - baseN) / spacing);
            var k1 = (int)Math.Ceiling((projections.Max() - baseN) / spacing);
            if (k0 > k1)
            {
                (k0, k1) = (k1, k0);
            }

            var pattern = family.DashLengths.Select(Math.Abs).ToList();
            var patternLength = pattern.Sum();
            if (pattern.Count > 0 && patternLength > 0)
            {
                dash ??= pattern.Select(d => Math.Max(d, patternLength * 0.001)).ToArray();
            }

            for (var k = k0; k <= k1 && lines.Count < MaxHatchLines; k++)
            {
                var ox = family.BasePoint.X + (k * family.Offset.X);
                var oy = family.BasePoint.Y + (k * family.Offset.Y);
                var along = corners.Select(c => ((c.X - ox) * dx) + ((c.Y - oy) * dy)).ToList();
                double t0 = along.Min(), t1 = along.Max();
                var phase = patternLength > 0 ? (((t0 - (k * shift)) % patternLength) + patternLength) % patternLength : 0;
                var a = m.Transform(ox + (t0 * dx), oy + (t0 * dy));
                var b = m.Transform(ox + (t1 * dx), oy + (t1 * dy));
                lines.Add((a.X, a.Y, b.X, b.Y, phase * Scale(m)));
            }
        }

        if (lines.Count == 0)
        {
            return;
        }

        shape.PatternLines = lines;
        shape.PatternDash = dash?.Select(d => d * Scale(m)).ToArray();
        Add(shape);
    }

    /// <summary>Hatch edge angles: clockwise edges store their angles mirrored.</summary>
    private static (double Start, double Sweep) EdgeSweep(double start, double end, bool counterClockwise)
    {
        if (!counterClockwise)
        {
            start = -start;
            end = -end;
        }

        var sweep = end - start;
        if (counterClockwise)
        {
            while (sweep <= 0)
            {
                sweep += 2 * Math.PI;
            }
        }
        else
        {
            while (sweep >= 0)
            {
                sweep -= 2 * Math.PI;
            }
        }

        return (start, sweep);
    }

    // --- Geometry helpers -------------------------------------------------------------------

    private void Add(Shape shape)
    {
        if (shape.Path.Count == 0)
        {
            return;
        }

        if (_clip is { } clip)
        {
            // Model space seen through a viewport: the frame already sets the extent.
            shape.Clip = clip;
            _shapes.Add(shape);
            return;
        }

        foreach (var cmd in shape.Path)
        {
            if (cmd.Op != 'h')
            {
                Extend(cmd.X1, cmd.Y1);
            }

            if (cmd.Op == 'c')
            {
                Extend(cmd.X3, cmd.Y3);
            }
        }

        _shapes.Add(shape);
    }

    private void Extend(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return;
        }

        _sheet.MinX = Math.Min(_sheet.MinX, x);
        _sheet.MinY = Math.Min(_sheet.MinY, y);
        _sheet.MaxX = Math.Max(_sheet.MaxX, x);
        _sheet.MaxY = Math.Max(_sheet.MaxY, y);
    }

    private static void MoveTo(Shape shape, PdfMatrix m, double x, double y)
    {
        var p = m.Transform(x, y);
        shape.Path.Add(new Cmd('m', p.X, p.Y));
    }

    private static void LineTo(Shape shape, PdfMatrix m, double x, double y)
    {
        var p = m.Transform(x, y);
        shape.Path.Add(new Cmd('l', p.X, p.Y));
    }

    private static void AddPoints(Shape shape, PdfMatrix m, IReadOnlyList<(double X, double Y)> points, bool closed)
    {
        MoveTo(shape, m, points[0].X, points[0].Y);
        for (var i = 1; i < points.Count; i++)
        {
            LineTo(shape, m, points[i].X, points[i].Y);
        }

        if (closed)
        {
            shape.Path.Add(new Cmd('h', 0, 0));
        }
    }

    /// <summary>Polyline vertices with bulges (a bulge is tan(¼ of the arc's angle); 0 is a straight segment).</summary>
    private static void AddBulgePolyline(Shape shape, PdfMatrix m, IReadOnlyList<(double X, double Y, double Bulge)> points, bool closed)
    {
        MoveTo(shape, m, points[0].X, points[0].Y);
        var count = closed ? points.Count : points.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            if (Math.Abs(a.Bulge) < 1e-9)
            {
                LineTo(shape, m, b.X, b.Y);
                continue;
            }

            double dx = b.X - a.X, dy = b.Y - a.Y;
            var chord = Math.Sqrt((dx * dx) + (dy * dy));
            if (chord < 1e-12)
            {
                continue;
            }

            // The centre lies on the chord's perpendicular, at chord·(1−b²)/(4b) from its middle.
            var offset = chord * (1 - (a.Bulge * a.Bulge)) / (4 * a.Bulge);
            var cx = ((a.X + b.X) / 2) - (dy / chord * offset);
            var cy = ((a.Y + b.Y) / 2) + (dx / chord * offset);
            var radius = Math.Sqrt(((a.X - cx) * (a.X - cx)) + ((a.Y - cy) * (a.Y - cy)));
            var start = Math.Atan2(a.Y - cy, a.X - cx);
            var sweep = 4 * Math.Atan(a.Bulge);
            AddEllipseArc(shape, m, cx, cy, radius, 0, 0, radius, start, sweep, newFigure: false);
        }

        if (closed)
        {
            shape.Path.Add(new Cmd('h', 0, 0));
        }
    }

    /// <summary>
    /// An elliptical arc P(t) = C + U cos t + V sin t from t = start over sweep (negative = clockwise), as
    /// cubic Béziers of at most 90° each (exact under any affine transform).
    /// </summary>
    private static void AddEllipseArc(Shape shape, PdfMatrix m, double cx, double cy, double ux, double uy, double vx, double vy,
        double start, double sweep, bool newFigure)
    {
        (double X, double Y) P(double t) => (cx + (ux * Math.Cos(t)) + (vx * Math.Sin(t)), cy + (uy * Math.Cos(t)) + (vy * Math.Sin(t)));
        (double X, double Y) D(double t) => ((-ux * Math.Sin(t)) + (vx * Math.Cos(t)), (-uy * Math.Sin(t)) + (vy * Math.Cos(t)));

        var first = P(start);
        if (newFigure)
        {
            MoveTo(shape, m, first.X, first.Y);
        }
        else
        {
            LineTo(shape, m, first.X, first.Y);
        }

        var segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2)));
        var delta = sweep / segments;
        var h = 4.0 / 3 * Math.Tan(delta / 4);
        for (var i = 0; i < segments; i++)
        {
            var a = start + (i * delta);
            var b = a + delta;
            var pa = P(a);
            var pb = P(b);
            var da = D(a);
            var db = D(b);
            var c1 = m.Transform(pa.X + (h * da.X), pa.Y + (h * da.Y));
            var c2 = m.Transform(pb.X - (h * db.X), pb.Y - (h * db.Y));
            var end = m.Transform(pb.X, pb.Y);
            shape.Path.Add(new Cmd('c', c1.X, c1.Y, c2.X, c2.Y, end.X, end.Y));
        }
    }

    /// <summary>Points along a NURBS curve (de Boor), or through its fit points when the definition is incomplete.</summary>
    private static List<(double X, double Y)> SampleSpline(IList<XYZ> controlPoints, IList<double> knots, IList<double> weights, int degree, IList<XYZ> fitPoints)
    {
        var count = controlPoints.Count;
        if (count < 2 || degree < 1 || knots.Count != count + degree + 1)
        {
            var points = (fitPoints.Count >= 2 ? fitPoints : controlPoints).Select(p => (p.X, p.Y)).ToList();
            return points;
        }

        var result = new List<(double X, double Y)>();
        const int steps = 12;
        for (var span = degree; span < count; span++)
        {
            if (knots[span + 1] <= knots[span])
            {
                continue;
            }

            for (var s = 0; s < steps; s++)
            {
                result.Add(DeBoor(span, knots[span] + ((knots[span + 1] - knots[span]) * s / steps)));
            }
        }

        result.Add(DeBoor(count - 1, knots[count]));
        return result;

        (double X, double Y) DeBoor(int k, double u)
        {
            var d = new (double X, double Y, double W)[degree + 1];
            for (var j = 0; j <= degree; j++)
            {
                var index = j + k - degree;
                var w = weights.Count == count && weights[index] > 0 ? weights[index] : 1;
                d[j] = (controlPoints[index].X * w, controlPoints[index].Y * w, w);
            }

            for (var r = 1; r <= degree; r++)
            {
                for (var j = degree; j >= r; j--)
                {
                    var i = j + k - degree;
                    var denominator = knots[i + degree - r + 1] - knots[i];
                    var alpha = denominator == 0 ? 0 : (u - knots[i]) / denominator;
                    d[j] = (((1 - alpha) * d[j - 1].X) + (alpha * d[j].X), ((1 - alpha) * d[j - 1].Y) + (alpha * d[j].Y), ((1 - alpha) * d[j - 1].W) + (alpha * d[j].W));
                }
            }

            var weight = d[degree].W == 0 ? 1 : d[degree].W;
            return (d[degree].X / weight, d[degree].Y / weight);
        }
    }

    /// <summary>The object coordinate system of a 2D entity (AutoCAD's "arbitrary axis" rule), projected to plan view.</summary>
    private static PdfMatrix Ocs(XYZ normal, double elevation)
    {
        var n = Normalize(normal);
        if (Math.Abs(n.X) < 1e-12 && Math.Abs(n.Y) < 1e-12 && n.Z > 0)
        {
            return PdfMatrix.Identity;
        }

        var ax = Math.Abs(n.X) < 1.0 / 64 && Math.Abs(n.Y) < 1.0 / 64 ? Cross(new XYZ(0, 1, 0), n) : Cross(new XYZ(0, 0, 1), n);
        ax = Normalize(ax);
        var ay = Cross(n, ax);
        return new PdfMatrix(ax.X, ax.Y, ay.X, ay.Y, n.X * elevation, n.Y * elevation);
    }

    private static XYZ Normalize(XYZ v)
    {
        var length = Math.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));
        return length == 0 ? new XYZ(0, 0, 1) : new XYZ(v.X / length, v.Y / length, v.Z / length);
    }

    private static XYZ Cross(XYZ a, XYZ b) => new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

    private static (double X, double Y) Linear(PdfMatrix m, double x, double y) => ((m.A * x) + (m.C * y), (m.B * x) + (m.D * y));

    /// <summary>The average scale of a transform (for dash lengths and widths given in drawing units).</summary>
    private static double Scale(PdfMatrix m) => Math.Sqrt(Math.Abs((m.A * m.D) - (m.B * m.C)));

    // --- Styles -----------------------------------------------------------------------------

    private Rgb ResolveColor(Color color, Layer layer, Context context)
    {
        if (options.Monochrome)
        {
            return new Rgb(0, 0, 0);
        }

        var rgb = color.IsByBlock ? context.Color
            : color.IsByLayer ? new Rgb(layer.Color.R, layer.Color.G, layer.Color.B)
            : new Rgb(color.R, color.G, color.B);

        // White (colour 7) and near-white are drawn black, as on paper.
        var luminance = ((0.2126 * rgb.R) + (0.7152 * rgb.G) + (0.0722 * rgb.B)) / 255;
        return luminance > 0.92 ? new Rgb(0, 0, 0) : rgb;
    }

    private static double ResolveWidth(LineWeightType weight, Layer layer, Context context) => weight switch
    {
        LineWeightType.ByBlock => context.WidthMm,
        LineWeightType.ByLayer => layer.LineWeight is LineWeightType.ByLayer or LineWeightType.ByBlock or LineWeightType.Default
            ? DefaultLineWeightMm
            : (short)layer.LineWeight / 100.0,
        LineWeightType.Default => DefaultLineWeightMm,
        _ => (short)weight / 100.0,
    };

    private static LineType? ResolveLineType(LineType? lineType, Layer layer, Context context) =>
        lineType?.Name switch
        {
            null => null,
            var n when n.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) => layer.LineType,
            var n when n.Equals("ByBlock", StringComparison.OrdinalIgnoreCase) => context.LineType,
            _ => lineType,
        };

    /// <summary>A dash pattern in world units (dashes, gaps and dots), or null for continuous lines.</summary>
    private double[]? Dash(LineType? lineType, double entityScale, PdfMatrix m)
    {
        if (lineType is null || lineType.Segments.Count() < 2)
        {
            return null;
        }

        var scale = document.Header.LineTypeScale * (entityScale > 0 ? entityScale : 1) * Scale(m);
        var lengths = lineType.Segments.Select(s => Math.Abs(s.Length) * scale).ToArray();
        var total = lengths.Sum();
        return total <= 0 ? null : lengths.Select(l => Math.Max(l, total * 0.002)).ToArray();
    }

    // --- Output -----------------------------------------------------------------------------

    public CadPdfResult Write(string title)
    {
        var sheets = _sheets.Where(sheet => !sheet.IsEmpty).ToList();
        if (sheets.Count == 0)
        {
            throw new PdfException("The drawing's model space has nothing that can be shown.");
        }

        var pdf = new CosNewDocument();
        var catalog = pdf.Reserve();
        var pagesRef = pdf.Reserve();

        // One PDF layer per CAD layer used (on any page), in name order; frozen or off layers start hidden.
        var used = sheets.SelectMany(sheet => sheet.Shapes.Select(x => x.Layer).Concat(sheet.Texts.Select(t => t.Layer)))
            .Distinct().OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var groups = new Dictionary<Layer, (CosReference Ref, string Name)>();
        var properties = new CosDictionary();
        for (var i = 0; i < used.Count; i++)
        {
            var group = pdf.Add(new CosDictionary().Set("Type", new CosName("OCG")).Set("Name", CosString.FromText(used[i].Name)));
            var name = "L" + i.ToString(CultureInfo.InvariantCulture);
            groups[used[i]] = (group, name);
            properties[name] = group;
        }

        var pageRefs = new List<CosReference>();
        var pageInfos = new List<CadPageInfo>();
        var texts = new List<CadTextItem>();
        foreach (var sheet in sheets)
        {
            var pageIndex = pageRefs.Count;
            double s, margin, scale;
            if (sheet.IsLayout)
            {
                // Layouts are paper: 1:1, a small margin around what is drawn.
                scale = 1;
                s = PointsPerMm * sheet.UnitMm;
                margin = LayoutMarginMm * PointsPerMm;
            }
            else
            {
                // A standard scale that fits the model on A0 (or smaller drawings at 1:1).
                var widthMm = Math.Max(1e-6, (sheet.MaxX - sheet.MinX) * sheet.UnitMm);
                var heightMm = Math.Max(1e-6, (sheet.MaxY - sheet.MinY) * sheet.UnitMm);
                var (longSide, shortSide) = (1189 - (2 * MarginMm), 841 - (2 * MarginMm));
                var (maxWidth, maxHeight) = widthMm >= heightMm ? (longSide, shortSide) : (shortSide, longSide);
                scale = Scales.FirstOrDefault(r => widthMm / r <= maxWidth && heightMm / r <= maxHeight, Math.Max(widthMm / maxWidth, heightMm / maxHeight));
                s = PointsPerMm * sheet.UnitMm / scale; // points per drawing unit
                margin = MarginMm * PointsPerMm;
            }

            var pageWidth = Math.Max(100, ((sheet.MaxX - sheet.MinX) * s) + (2 * margin));
            var pageHeight = Math.Max(100, ((sheet.MaxY - sheet.MinY) * s) + (2 * margin));
            var (minX, minY) = (sheet.MinX, sheet.MinY);
            (double X, double Y) Map(double x, double y) => (((x - minX) * s) + margin, ((y - minY) * s) + margin);

            var content = WriteContent(sheet, groups, Map, s);
            var contents = pdf.Add(CosPageEdit.Stream(Encoding.ASCII.GetBytes(content)));

            // Measuring scales (§12.9): the whole page, then each layout viewport at its model scale.
            var scaleText = ScaleText(scale);
            var viewports = new List<CosObject> { MeasureViewport(sheet.IsLayout ? "Paper" : "Model", 0, 0, pageWidth, pageHeight, scaleText, scale) };
            var modelUnitMm = UnitToMm(document.Header.InsUnits);
            for (var v = 0; v < sheet.Viewports.Count; v++)
            {
                var viewport = sheet.Viewports[v];
                var a = Map(viewport.Frame.X0, viewport.Frame.Y0);
                var b = Map(viewport.Frame.X1, viewport.Frame.Y1);
                var ratio = viewport.ModelUnitsPerPaperUnit * modelUnitMm / sheet.UnitMm;
                viewports.Add(MeasureViewport($"Viewport {v + 1}", a.X, a.Y, b.X, b.Y, ScaleText(ratio), ratio));
            }

            var page = pdf.Add(new CosDictionary()
                .Set("Type", new CosName("Page"))
                .Set("Parent", pagesRef)
                .Set("MediaBox", CosArray.FromNumbers(0, 0, pageWidth, pageHeight))
                .Set("Resources", new CosDictionary().Set("Properties", properties))
                .Set("Contents", contents)
                .Set("VP", new CosArray(viewports)));
            pageRefs.Add(page);
            pageInfos.Add(new CadPageInfo(sheet.Name, pageWidth, pageHeight, scaleText, sheet.IsLayout));

            texts.AddRange(sheet.Texts.Select(t =>
            {
                var p = Map(t.X, t.Y);
                var group = groups[t.Layer].Ref.Id;
                return new CadTextItem(t.Text, p.X, p.Y, t.Height * s, t.Rotation, t.WidthFactor, t.Align, t.Vertical, t.Font,
                    new PdfColor(t.Color.R, t.Color.G, t.Color.B), $"{group.Number} {group.Generation}", pageIndex);
            }));
        }

        pdf.Set(pagesRef, new CosDictionary()
            .Set("Type", new CosName("Pages"))
            .Set("Kids", new CosArray(pageRefs.Cast<CosObject>().ToList()))
            .Set("Count", new CosInteger(pageRefs.Count)));

        var off = used.Where(l => !l.IsOn || l.Flags.HasFlag(LayerFlags.Frozen)).Select(l => (CosObject)groups[l].Ref).ToList();
        var order = used.Select(l => (CosObject)groups[l].Ref).ToList();
        var catalogDictionary = new CosDictionary()
            .Set("Type", new CosName("Catalog"))
            .Set("Pages", pagesRef)
            .Set("PageMode", new CosName(used.Count > 0 ? "UseOC" : "UseNone"))
            .Set("OCProperties", new CosDictionary()
                .Set("OCGs", new CosArray(order))
                .Set("D", new CosDictionary()
                    .Set("Name", CosString.FromText("Layers"))
                    .Set("Order", new CosArray(order))
                    .Set("OFF", new CosArray(off))));
        if (pageRefs.Count > 1)
        {
            catalogDictionary.Set("Outlines", WriteOutline(pdf, pageRefs, pageInfos));
        }

        pdf.Set(catalog, catalogDictionary);
        var info = pdf.Add(new CosDictionary()
            .Set("Title", CosString.FromText(title))
            .Set("Creator", CosString.FromText("YRK PDF (DWG/DXF converter)"))
            .Set("Producer", CosString.FromText("YRK PDF")));

        var warnings = _skipped.OrderByDescending(k => k.Value).Select(k => $"{k.Value} × {k.Key}").ToList();
        return new CadPdfResult(pdf.Build(catalog, info), pageInfos[0].Width, pageInfos[0].Height, texts, pageInfos[0].Scale, warnings)
        {
            Pages = pageInfos,
        };
    }

    /// <summary>Bookmarks named after the pages: "Model", "A1 - Plan", …</summary>
    private static CosReference WriteOutline(CosNewDocument pdf, List<CosReference> pages, List<CadPageInfo> infos)
    {
        var outline = pdf.Reserve();
        var items = pages.Select(_ => pdf.Reserve()).ToList();
        for (var i = 0; i < items.Count; i++)
        {
            var item = new CosDictionary()
                .Set("Title", CosString.FromText(infos[i].Name))
                .Set("Parent", outline)
                .Set("Dest", new CosArray([pages[i], new CosName("Fit")]));
            if (i > 0)
            {
                item.Set("Prev", items[i - 1]);
            }

            if (i < items.Count - 1)
            {
                item.Set("Next", items[i + 1]);
            }

            pdf.Set(items[i], item);
        }

        pdf.Set(outline, new CosDictionary()
            .Set("Type", new CosName("Outlines"))
            .Set("First", items[0])
            .Set("Last", items[^1])
            .Set("Count", new CosInteger(items.Count)));
        return outline;
    }

    /// <summary>A viewport dictionary with a measure: distances inside it read in real millimetres.</summary>
    private static CosDictionary MeasureViewport(string name, double x0, double y0, double x1, double y1, string ratio, double scale)
    {
        var mmPerPoint = scale / PointsPerMm;
        var measure = new CosDictionary()
            .Set("Type", new CosName("Measure"))
            .Set("Subtype", new CosName("RL"))
            .Set("R", CosString.FromText(ratio))
            .Set("X", new CosArray([NumberFormat("mm", mmPerPoint)]))
            .Set("D", new CosArray([NumberFormat("mm", 1)]))
            .Set("A", new CosArray([NumberFormat("sq mm", 1)]));
        return new CosDictionary()
            .Set("Type", new CosName("Viewport"))
            .Set("BBox", CosArray.FromNumbers(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1)))
            .Set("Name", CosString.FromText(name))
            .Set("Measure", measure);
    }

    private static string ScaleText(double scale) =>
        scale >= 1 ? $"1:{scale.ToString("0.##", CultureInfo.InvariantCulture)}" : $"{(1 / scale).ToString("0.##", CultureInfo.InvariantCulture)}:1";

    /// <summary>The page's content stream: shapes grouped by layer, viewport content clipped to its frame.</summary>
    private string WriteContent(Sheet sheet, Dictionary<Layer, (CosReference Ref, string Name)> groups, Func<double, double, (double X, double Y)> map, double s)
    {
        var content = new StringBuilder("1 J 1 j\n");
        Layer? currentLayer = null;
        Clip? currentClip = null;
        string? stroke = null, fill = null, lineWidth = null, dash = null;
        foreach (var shape in sheet.Shapes)
        {
            if (shape.Clip != currentClip)
            {
                // Marked content and saved states must nest: close the layer before changing the clip.
                if (currentLayer is not null)
                {
                    content.Append("EMC\n");
                    currentLayer = null;
                }

                if (currentClip is not null)
                {
                    content.Append("Q\n");
                }

                if (shape.Clip is { } clip)
                {
                    var a = map(clip.X0, clip.Y0);
                    var b = map(clip.X1, clip.Y1);
                    content.Append("q ").Append(F(a.X)).Append(' ').Append(F(a.Y)).Append(' ')
                        .Append(F(b.X - a.X)).Append(' ').Append(F(b.Y - a.Y)).Append(" re W n\n");
                }

                currentClip = shape.Clip;
                stroke = fill = lineWidth = dash = null;
            }

            if (shape.Layer != currentLayer)
            {
                if (currentLayer is not null)
                {
                    content.Append("EMC\n");
                }

                content.Append('/').Append("OC /").Append(groups[shape.Layer].Name).Append(" BDC\n");
                currentLayer = shape.Layer;
            }

            var color = $"{F(shape.Color.R / 255.0)} {F(shape.Color.G / 255.0)} {F(shape.Color.B / 255.0)}";
            var widthPoints = Math.Max(Math.Max(shape.WidthMm, MinLineWeightMm) * PointsPerMm, shape.DrawingWidth * s);
            var width = F(widthPoints);
            if (shape.PatternLines is { } patternLines)
            {
                content.Append("q\n");
                WritePath(content, shape.Path, map);
                content.Append("W* n\n").Append(color).Append(" RG ").Append(width).Append(" w\n");
                var pattern = shape.PatternDash is { } d ? "[" + string.Join(" ", d.Select(v => F(v * s))) + "]" : "[]";
                foreach (var (x0, y0, x1, y1, phase) in patternLines)
                {
                    var a = map(x0, y0);
                    var b = map(x1, y1);
                    content.Append(pattern).Append(' ').Append(F(phase * s)).Append(" d ")
                        .Append(F(a.X)).Append(' ').Append(F(a.Y)).Append(" m ").Append(F(b.X)).Append(' ').Append(F(b.Y)).Append(" l S\n");
                }

                content.Append("Q\n");
                stroke = fill = lineWidth = dash = null; // Q restored the earlier state.
                continue;
            }

            if (shape.Fill)
            {
                if (fill != color)
                {
                    content.Append(color).Append(" rg\n");
                    fill = color;
                }

                WritePath(content, shape.Path, map);
                content.Append("f*\n");
                continue;
            }

            if (stroke != color)
            {
                content.Append(color).Append(" RG\n");
                stroke = color;
            }

            if (lineWidth != width)
            {
                content.Append(width).Append(" w\n");
                lineWidth = width;
            }

            var dashText = shape.Dash is { } pattern2 ? "[" + string.Join(" ", pattern2.Select(v => F(v * s))) + "] 0 d" : "[] 0 d";
            if (dash != dashText)
            {
                content.Append(dashText).Append('\n');
                dash = dashText;
            }

            WritePath(content, shape.Path, map);
            content.Append("S\n");
        }

        if (currentLayer is not null)
        {
            content.Append("EMC\n");
        }

        if (currentClip is not null)
        {
            content.Append("Q\n");
        }

        return content.ToString();
    }

    private static CosDictionary NumberFormat(string unit, double factor) => new CosDictionary()
        .Set("Type", new CosName("NumberFormat"))
        .Set("U", CosString.FromText(unit))
        .Set("C", new CosReal(factor))
        .Set("D", new CosInteger(10));

    private static void WritePath(StringBuilder content, List<Cmd> path, Func<double, double, (double X, double Y)> map)
    {
        foreach (var cmd in path)
        {
            switch (cmd.Op)
            {
                case 'm' or 'l':
                    var p = map(cmd.X1, cmd.Y1);
                    content.Append(F(p.X)).Append(' ').Append(F(p.Y)).Append(cmd.Op == 'm' ? " m\n" : " l\n");
                    break;
                case 'c':
                    var c1 = map(cmd.X1, cmd.Y1);
                    var c2 = map(cmd.X2, cmd.Y2);
                    var e = map(cmd.X3, cmd.Y3);
                    content.Append(F(c1.X)).Append(' ').Append(F(c1.Y)).Append(' ').Append(F(c2.X)).Append(' ').Append(F(c2.Y)).Append(' ')
                        .Append(F(e.X)).Append(' ').Append(F(e.Y)).Append(" c\n");
                    break;
                case 'h':
                    content.Append("h\n");
                    break;
            }
        }
    }

    private static double UnitToMm(UnitsType units) => units switch
    {
        UnitsType.Inches => 25.4,
        UnitsType.Feet => 304.8,
        UnitsType.Yards => 914.4,
        UnitsType.Miles => 1609344,
        UnitsType.Centimeters => 10,
        UnitsType.Decimeters => 100,
        UnitsType.Meters => 1000,
        UnitsType.Kilometers => 1_000_000,
        UnitsType.Microns => 0.001,
        _ => 1, // Millimetres, and unitless drawings (almost always millimetres in practice).
    };

    private static string F(double value) => CosWriter.FormatReal(value);
}
