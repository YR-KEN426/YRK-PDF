using Yrk.PdfStudio.Core.Pdf.Measure;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>Snap points for measuring, read from the page's path objects.</summary>
internal sealed partial class PdfiumDocument
{
    private const int MaxSnapPoints = 400_000;
    private const int MaxFormDepth = 8;

    public Task<IReadOnlyList<PdfSnapPoint>> GetSnapPointsAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        return _engine.InvokeAsync(() => ReadSnapPoints(pageIndex), PdfWorkPriority.Background, cancellationToken);
    }

    private IReadOnlyList<PdfSnapPoint> ReadSnapPoints(int pageIndex)
    {
        var page = GetPage(pageIndex);
        var (ax, ay, bx, by, cx, cy) = DisplayTransform(page);

        var seen = new HashSet<(long, long)>();
        var points = new List<PdfSnapPoint>();
        void Add(double x, double y, PdfSnapKind kind)
        {
            var u = cx + (x * ax) + (y * bx);
            var v = cy + (x * ay) + (y * by);
            if (u is < 0 or > 1 || v is < 0 or > 1 || points.Count >= MaxSnapPoints)
            {
                return;
            }

            // The same corner is usually shared by several lines: keep it once.
            if (seen.Add(((long)Math.Round(u * 1e5), (long)Math.Round(v * 1e5))))
            {
                points.Add(new PdfSnapPoint(u, v, kind));
            }
        }

        var count = PdfiumNative.FPDFPage_CountObjects(page);
        for (var i = 0; i < count && points.Count < MaxSnapPoints; i++)
        {
            Collect(PdfiumNative.FPDFPage_GetObject(page, i), Identity, 0, Add);
        }

        return points;
    }

    private static readonly double[] Identity = [1, 0, 0, 1, 0, 0];

    /// <summary>Adds the snap points of one object; <paramref name="parent"/> maps its space to page space.</summary>
    private static void Collect(IntPtr pageObject, double[] parent, int depth, Action<double, double, PdfSnapKind> add)
    {
        if (pageObject == IntPtr.Zero || depth > MaxFormDepth)
        {
            return;
        }

        var type = PdfiumNative.FPDFPageObj_GetType(pageObject);
        if (type is not (PdfiumNative.FPDF_PAGEOBJ_PATH or PdfiumNative.FPDF_PAGEOBJ_FORM))
        {
            return;
        }

        var own = PdfiumNative.FPDFPageObj_GetMatrix(pageObject, out var m) != 0
            ? new double[] { m.A, m.B, m.C, m.D, m.E, m.F }
            : Identity;
        var matrix = Multiply(own, parent);

        if (type == PdfiumNative.FPDF_PAGEOBJ_FORM)
        {
            var children = PdfiumNative.FPDFFormObj_CountObjects(pageObject);
            for (var i = 0; i < children; i++)
            {
                Collect(PdfiumNative.FPDFFormObj_GetObject(pageObject, (uint)i), matrix, depth + 1, add);
            }

            return;
        }

        (double X, double Y) Map(float x, float y) => ((x * matrix[0]) + (y * matrix[2]) + matrix[4], (x * matrix[1]) + (y * matrix[3]) + matrix[5]);

        var segments = PdfiumNative.FPDFPath_CountSegments(pageObject);
        (double X, double Y) current = default, start = default;
        var bezier = 0;
        for (var i = 0; i < segments; i++)
        {
            var segment = PdfiumNative.FPDFPath_GetPathSegment(pageObject, i);
            if (segment == IntPtr.Zero || PdfiumNative.FPDFPathSegment_GetPoint(segment, out var sx, out var sy) == 0)
            {
                continue;
            }

            var point = Map(sx, sy);
            switch (PdfiumNative.FPDFPathSegment_GetType(segment))
            {
                case PdfiumNative.FPDF_SEGMENT_MOVETO:
                    start = point;
                    bezier = 0;
                    add(point.X, point.Y, PdfSnapKind.End);
                    break;
                case PdfiumNative.FPDF_SEGMENT_LINETO:
                    bezier = 0;
                    add(point.X, point.Y, PdfSnapKind.End);
                    add((current.X + point.X) / 2, (current.Y + point.Y) / 2, PdfSnapKind.Middle);
                    break;
                case PdfiumNative.FPDF_SEGMENT_BEZIERTO:
                    // Two control points, then the end point: only the end lies on the curve.
                    if (++bezier % 3 == 0)
                    {
                        add(point.X, point.Y, PdfSnapKind.End);
                    }

                    break;
            }

            current = point;
            if (PdfiumNative.FPDFPathSegment_GetClose(segment) != 0 && current != start)
            {
                add((current.X + start.X) / 2, (current.Y + start.Y) / 2, PdfSnapKind.Middle);
                current = start;
            }
        }
    }

    /// <summary>PDF matrices act on row vectors: applying <paramref name="first"/> then <paramref name="then"/>.</summary>
    private static double[] Multiply(double[] first, double[] then) =>
    [
        (first[0] * then[0]) + (first[1] * then[2]),
        (first[0] * then[1]) + (first[1] * then[3]),
        (first[2] * then[0]) + (first[3] * then[2]),
        (first[2] * then[1]) + (first[3] * then[3]),
        (first[4] * then[0]) + (first[5] * then[2]) + then[4],
        (first[4] * then[1]) + (first[5] * then[3]) + then[5],
    ];
}
