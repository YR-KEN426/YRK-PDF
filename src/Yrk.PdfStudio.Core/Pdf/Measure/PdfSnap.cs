namespace Yrk.PdfStudio.Core.Pdf.Measure;

public enum PdfSnapKind
{
    /// <summary>The end of a line or curve (corners, line ends).</summary>
    End,

    /// <summary>The middle of a straight line.</summary>
    Middle,
}

/// <summary>A point to snap to, relative to the page as displayed (0..1).</summary>
public readonly record struct PdfSnapPoint(double X, double Y, PdfSnapKind Kind);

/// <summary>
/// Finds the nearest snap point quickly: points are kept in a grid of square cells (in page points), so
/// a query only looks at the cells around the cursor even on drawings with hundreds of thousands of lines.
/// </summary>
public sealed class PdfSnapIndex
{
    private const double CellSize = 8;

    private readonly double _width;
    private readonly double _height;
    private readonly Dictionary<(int, int), List<PdfSnapPoint>> _cells = [];

    /// <param name="width">Page width as displayed, in points.</param>
    /// <param name="height">Page height as displayed, in points.</param>
    public PdfSnapIndex(IEnumerable<PdfSnapPoint> points, double width, double height)
    {
        _width = width;
        _height = height;
        foreach (var point in points)
        {
            var key = Cell(point.X * width, point.Y * height);
            if (!_cells.TryGetValue(key, out var list))
            {
                _cells[key] = list = [];
            }

            list.Add(point);
        }
    }

    public int Count => _cells.Values.Sum(c => c.Count);

    /// <summary>The nearest point within <paramref name="radius"/> points of (x, y) (page-relative); ends win over middles at about the same distance.</summary>
    public PdfSnapPoint? Nearest(double x, double y, double radius)
    {
        double px = x * _width, py = y * _height;
        var (cx0, cy0) = Cell(px - radius, py - radius);
        var (cx1, cy1) = Cell(px + radius, py + radius);
        PdfSnapPoint? best = null;
        var bestScore = double.MaxValue;
        for (var cx = cx0; cx <= cx1; cx++)
        {
            for (var cy = cy0; cy <= cy1; cy++)
            {
                if (!_cells.TryGetValue((cx, cy), out var list))
                {
                    continue;
                }

                foreach (var point in list)
                {
                    var dx = (point.X * _width) - px;
                    var dy = (point.Y * _height) - py;
                    var distance = Math.Sqrt((dx * dx) + (dy * dy));
                    if (distance > radius)
                    {
                        continue;
                    }

                    var score = point.Kind == PdfSnapKind.End ? distance : distance + (radius * 0.25);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = point;
                    }
                }
            }
        }

        return best;
    }

    private static (int, int) Cell(double x, double y) => ((int)Math.Floor(x / CellSize), (int)Math.Floor(y / CellSize));
}
