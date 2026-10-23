using Yrk.PdfStudio.Core.Pdf.Export;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>Lines, filled boxes, pictures and drawings of a page, read from its objects for exporting.</summary>
internal sealed partial class PdfiumDocument
{
    /// <summary>Lines at most this far from horizontal or vertical (points) count as straight.</summary>
    private const double Straight = 0.6;

    /// <summary>A filled box this thin (points) is a line: Word and Excel draw table borders that way.</summary>
    private const double ThinBox = 2.5;

    private const int MaxGraphicsObjects = 200_000;

    public Task<PdfPageGraphics> GetPageGraphicsAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        return _engine.InvokeAsync(() => ReadGraphics(pageIndex), PdfWorkPriority.Background, cancellationToken);
    }

    private PdfPageGraphics ReadGraphics(int pageIndex)
    {
        var size = PageSizes[pageIndex];
        var page = GetPage(pageIndex);
        var (ax, ay, bx, by, cx, cy) = DisplayTransform(page);
        (double X, double Y) ToDisplay(double x, double y) =>
            ((cx + (x * ax) + (y * bx)) * size.Width, (cy + (x * ay) + (y * by)) * size.Height);

        var reader = new GraphicsReader(ToDisplay);
        var count = PdfiumNative.FPDFPage_CountObjects(page);
        for (var i = 0; i < count && reader.Count < MaxGraphicsObjects; i++)
        {
            reader.Read(PdfiumNative.FPDFPage_GetObject(page, i), Identity, 0);
        }

        return new PdfPageGraphics(size.Width, size.Height, reader.Rules, reader.Graphics);
    }

    /// <summary>
    /// Page space → page-relative display position (rotation included) is affine: taken from three points
    /// mapped with a large device size, which keeps the rounding to whole device units tiny.
    /// </summary>
    private static (double Ax, double Ay, double Bx, double By, double Cx, double Cy) DisplayTransform(IntPtr page)
    {
        const int Device = 1_000_000;
        PdfiumNative.FPDF_PageToDevice(page, 0, 0, Device, Device, 0, 0, 0, out var ox, out var oy);
        PdfiumNative.FPDF_PageToDevice(page, 0, 0, Device, Device, 0, 1000, 0, out var xx, out var xy);
        PdfiumNative.FPDF_PageToDevice(page, 0, 0, Device, Device, 0, 0, 1000, out var yx, out var yy);
        return (
            (xx - ox) / 1000.0 / Device, (xy - oy) / 1000.0 / Device,
            (yx - ox) / 1000.0 / Device, (yy - oy) / 1000.0 / Device,
            (double)ox / Device, (double)oy / Device);
    }

    private sealed class GraphicsReader(Func<double, double, (double X, double Y)> toDisplay)
    {
        public List<PdfRule> Rules { get; } = [];

        public List<PdfGraphic> Graphics { get; } = [];

        public int Count => Rules.Count + Graphics.Count;

        public void Read(IntPtr pageObject, double[] parent, int depth)
        {
            if (pageObject == IntPtr.Zero || depth > MaxFormDepth)
            {
                return;
            }

            var type = PdfiumNative.FPDFPageObj_GetType(pageObject);
            var own = PdfiumNative.FPDFPageObj_GetMatrix(pageObject, out var m) != 0
                ? new double[] { m.A, m.B, m.C, m.D, m.E, m.F }
                : Identity;
            var matrix = Multiply(own, parent);
            (double X, double Y) Map(double x, double y) =>
                toDisplay((x * matrix[0]) + (y * matrix[2]) + matrix[4], (x * matrix[1]) + (y * matrix[3]) + matrix[5]);

            switch (type)
            {
                case PdfiumNative.FPDF_PAGEOBJ_FORM:
                    var children = PdfiumNative.FPDFFormObj_CountObjects(pageObject);
                    for (var i = 0; i < children; i++)
                    {
                        Read(PdfiumNative.FPDFFormObj_GetObject(pageObject, (uint)i), matrix, depth + 1);
                    }

                    break;

                case PdfiumNative.FPDF_PAGEOBJ_IMAGE:
                    // An image fills the unit square of its matrix.
                    AddGraphic(PdfGraphicKind.Image, [Map(0, 0), Map(1, 0), Map(0, 1), Map(1, 1)], null);
                    break;

                case PdfiumNative.FPDF_PAGEOBJ_SHADING:
                    if (PdfiumNative.FPDFPageObj_GetBounds(pageObject, out var l, out var b, out var r, out var t) != 0)
                    {
                        // Bounds are in the parent's space: only the parent's matrix applies.
                        (double X, double Y) MapParent(double x, double y) =>
                            toDisplay((x * parent[0]) + (y * parent[2]) + parent[4], (x * parent[1]) + (y * parent[3]) + parent[5]);
                        AddGraphic(PdfGraphicKind.Drawing, [MapParent(l, b), MapParent(r, b), MapParent(l, t), MapParent(r, t)], null);
                    }

                    break;

                case PdfiumNative.FPDF_PAGEOBJ_PATH:
                    ReadPath(pageObject, Map);
                    break;
            }
        }

        private void ReadPath(IntPtr path, Func<double, double, (double X, double Y)> map)
        {
            if (PdfiumNative.FPDFPath_GetDrawMode(path, out var fillMode, out var strokeFlag) == 0)
            {
                return;
            }

            var stroked = strokeFlag != 0 && Visible(PdfiumNative.FPDFPageObj_GetStrokeColor(path, out var sr, out var sg, out var sb, out var sa), sr, sg, sb, sa);
            var filledColor = fillMode != 0 && PdfiumNative.FPDFPageObj_GetFillColor(path, out var fr, out var fg, out var fb, out var fa) != 0 && fa > 0
                ? new PdfColor((byte)fr, (byte)fg, (byte)fb)
                : (PdfColor?)null;
            var filled = filledColor is not null && !IsWhite(filledColor.Value);
            if (!stroked && !filled)
            {
                return; // A clipping path, or white on white.
            }

            // The path as subpaths of display points; curves only make the drawing's box.
            var subpaths = new List<(List<(double X, double Y)> Points, bool Curved, bool Closed)>();
            var segments = PdfiumNative.FPDFPath_CountSegments(path);
            for (var i = 0; i < segments; i++)
            {
                var segment = PdfiumNative.FPDFPath_GetPathSegment(path, i);
                if (segment == IntPtr.Zero || PdfiumNative.FPDFPathSegment_GetPoint(segment, out var x, out var y) == 0)
                {
                    continue;
                }

                var point = map(x, y);
                var kind = PdfiumNative.FPDFPathSegment_GetType(segment);
                if (kind == PdfiumNative.FPDF_SEGMENT_MOVETO || subpaths.Count == 0)
                {
                    subpaths.Add(([point], false, false));
                }
                else
                {
                    subpaths[^1].Points.Add(point);
                    if (kind == PdfiumNative.FPDF_SEGMENT_BEZIERTO)
                    {
                        subpaths[^1] = subpaths[^1] with { Curved = true };
                    }
                }

                if (PdfiumNative.FPDFPathSegment_GetClose(segment) != 0)
                {
                    subpaths[^1] = subpaths[^1] with { Closed = true };
                }
            }

            var drawing = new List<(double X, double Y)>();
            foreach (var (points, curved, closed) in subpaths)
            {
                if (points.Count < 2)
                {
                    continue;
                }

                if (!curved && Box(points) is { } box)
                {
                    var (left, top, right, bottom) = box;
                    if (filled && Math.Min(right - left, bottom - top) <= ThinBox)
                    {
                        // A thin filled box is a line through its middle.
                        AddRule(right - left >= bottom - top
                            ? (left, (top + bottom) / 2, right, (top + bottom) / 2)
                            : ((left + right) / 2, top, (left + right) / 2, bottom));
                        continue;
                    }

                    if (stroked)
                    {
                        AddRule((left, top, right, top));
                        AddRule((left, bottom, right, bottom));
                        AddRule((left, top, left, bottom));
                        AddRule((right, top, right, bottom));
                    }

                    if (filled)
                    {
                        Graphics.Add(new PdfGraphic(PdfGraphicKind.Drawing, left, top, right - left, bottom - top, filledColor));
                    }

                    continue;
                }

                if (!curved && stroked && !filled)
                {
                    // Separate straight lines: horizontal and vertical ones may be table borders.
                    var all = closed ? [.. points, points[0]] : points;
                    for (var i = 1; i < all.Count; i++)
                    {
                        var (x1, y1) = all[i - 1];
                        var (x2, y2) = all[i];
                        if (Math.Abs(y2 - y1) <= Straight || Math.Abs(x2 - x1) <= Straight)
                        {
                            AddRule((Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2)));
                        }
                        else
                        {
                            drawing.Add(all[i - 1]);
                            drawing.Add(all[i]);
                        }
                    }

                    continue;
                }

                drawing.AddRange(points);
            }

            if (drawing.Count > 0)
            {
                AddGraphic(PdfGraphicKind.Drawing, drawing, null);
            }
        }

        private void AddRule((double X1, double Y1, double X2, double Y2) line)
        {
            var (x1, y1, x2, y2) = line;
            if (Math.Max(x2 - x1, y2 - y1) < 2)
            {
                return; // A dot.
            }

            Rules.Add(Math.Abs(y2 - y1) <= Math.Abs(x2 - x1)
                ? new PdfRule(x1, (y1 + y2) / 2, x2, (y1 + y2) / 2)
                : new PdfRule((x1 + x2) / 2, y1, (x1 + x2) / 2, y2));
        }

        private void AddGraphic(PdfGraphicKind kind, IReadOnlyCollection<(double X, double Y)> points, PdfColor? fill)
        {
            var left = points.Min(p => p.X);
            var top = points.Min(p => p.Y);
            var width = points.Max(p => p.X) - left;
            var height = points.Max(p => p.Y) - top;
            if (width >= 1 || height >= 1)
            {
                Graphics.Add(new PdfGraphic(kind, left, top, width, height, fill));
            }
        }

        /// <summary>The box of four (or five, closed) points forming an upright rectangle; null otherwise.</summary>
        private static (double Left, double Top, double Right, double Bottom)? Box(List<(double X, double Y)> points)
        {
            var corners = points.Count == 5 && Near(points[0], points[4]) ? points.Take(4).ToList() : points;
            if (corners.Count != 4)
            {
                return null;
            }

            for (var i = 0; i < 4; i++)
            {
                var (a, b) = (corners[i], corners[(i + 1) % 4]);
                if (Math.Abs(a.X - b.X) > Straight && Math.Abs(a.Y - b.Y) > Straight)
                {
                    return null;
                }
            }

            return (corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
        }

        private static bool Near((double X, double Y) a, (double X, double Y) b) => Math.Abs(a.X - b.X) <= Straight && Math.Abs(a.Y - b.Y) <= Straight;

        private static bool Visible(int ok, uint r, uint g, uint b, uint a) => ok != 0 && a > 0 && !IsWhite(new PdfColor((byte)r, (byte)g, (byte)b));

        private static bool IsWhite(PdfColor color) => color.R > 245 && color.G > 245 && color.B > 245;
    }
}
