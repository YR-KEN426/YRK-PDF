namespace Yrk.PdfStudio.Core.Pdf.Scan;

/// <summary>What clean-up a scanned page needs.</summary>
/// <param name="IsBlank">Nothing on the page but paper, specks and show-through from the other side.</param>
/// <param name="Straighten">Degrees to turn the page (positive = counter-clockwise) so its lines are level; 0 when it is level.</param>
/// <param name="Keep">The page without the dark scanner edges around it; null when it has none.</param>
public sealed record ScanPageCheck(bool IsBlank, double Straighten, NormalizedRect? Keep);

/// <summary>
/// Looks at a rendered page (BGRA, about <see cref="Dpi"/> dpi) the way a scanner's "clean up" does: blank pages,
/// tilted pages (the angle where the rows of text line up best) and the black edges a lid leaves.
/// </summary>
public static class ScanCleanup
{
    /// <summary>The resolution pages should be rendered at for <see cref="Check"/>.</summary>
    public const int Dpi = 100;

    private const int InkLevel = 140;          // Darker than this is ink; light show-through is not.
    private const int EdgeLevel = 80;          // Scanner edges are near black.
    private const double Margin = 0.05;        // Edges are left out of the blank and tilt checks.
    private const double BlankInk = 0.0002;    // At most this share of ink (a few specks) on a blank page.
    private const double MaxTilt = 6;          // Larger angles are a page put in sideways, not a tilt.
    private const double MinTilt = 0.2;        // Smaller ones are not worth turning the page for.

    public static ScanPageCheck Check(PdfBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var gray = Gray(bitmap);
        var ink = InkPoints(gray, bitmap.Width, bitmap.Height);
        var area = bitmap.Width * (1 - (2 * Margin)) * bitmap.Height * (1 - (2 * Margin));
        var blank = ink.Count <= area * BlankInk;
        return new ScanPageCheck(blank, blank ? 0 : Straighten(ink), DarkEdges(gray, bitmap.Width, bitmap.Height));
    }

    private static byte[] Gray(PdfBitmap bitmap)
    {
        var gray = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = y * bitmap.Stride;
            for (var x = 0; x < bitmap.Width; x++)
            {
                var i = row + (x * 4);
                gray[(y * bitmap.Width) + x] = (byte)(((bitmap.Pixels[i + 2] * 299) + (bitmap.Pixels[i + 1] * 587) + (bitmap.Pixels[i] * 114)) / 1000);
            }
        }

        return gray;
    }

    /// <summary>Ink inside the margins, without lone specks (an ink pixel needs two inked neighbours).</summary>
    private static List<(int X, int Y)> InkPoints(byte[] gray, int width, int height)
    {
        var points = new List<(int X, int Y)>();
        int x0 = (int)(width * Margin), x1 = width - x0, y0 = (int)(height * Margin), y1 = height - y0;
        bool Ink(int x, int y) => gray[(y * width) + x] < InkLevel;
        for (var y = Math.Max(1, y0); y < Math.Min(height - 1, y1); y++)
        {
            for (var x = Math.Max(1, x0); x < Math.Min(width - 1, x1); x++)
            {
                if (Ink(x, y) && (Ink(x - 1, y) ? 1 : 0) + (Ink(x + 1, y) ? 1 : 0) + (Ink(x, y - 1) ? 1 : 0) + (Ink(x, y + 1) ? 1 : 0) >= 2)
                {
                    points.Add((x, y));
                }
            }
        }

        return points;
    }

    /// <summary>
    /// The tilt from a projection profile: turned by the right angle, the ink falls into sharp rows (text lines,
    /// table rules), which makes the sum of squared row counts largest.
    /// </summary>
    private static double Straighten(List<(int X, int Y)> ink)
    {
        if (ink.Count < 200)
        {
            return 0;
        }

        var step = Math.Max(1, ink.Count / 60000);
        var points = ink.Where((_, i) => i % step == 0).ToArray();
        double Score(double degrees)
        {
            var radians = degrees * Math.PI / 180;
            var (sin, cos) = (Math.Sin(radians), Math.Cos(radians));
            var rows = new Dictionary<int, int>();
            foreach (var (x, y) in points)
            {
                var row = (int)Math.Round((y * cos) + (x * sin));
                rows[row] = rows.GetValueOrDefault(row) + 1;
            }

            return rows.Values.Sum(n => (double)n * n);
        }

        var level = Score(0);
        var (best, bestScore) = (0.0, level);
        for (var a = -MaxTilt; a <= MaxTilt; a += 0.25)
        {
            var score = Score(a);
            if (score > bestScore)
            {
                (best, bestScore) = (a, score);
            }
        }

        var coarse = best;
        for (var a = coarse - 0.25; a <= coarse + 0.25; a += 0.02)
        {
            var score = Score(a);
            if (score > bestScore)
            {
                (best, bestScore) = (a, score);
            }
        }

        // Lines tilted counter-clockwise by t line up at t; the page is turned back by -t. A weak peak is no line at all.
        return Math.Abs(best) < MinTilt || bestScore < level * 1.05 ? 0 : -Math.Round(best, 2);
    }

    /// <summary>Rows and columns from each edge that are mostly near black (at most 15% of the page per side).</summary>
    private static NormalizedRect? DarkEdges(byte[] gray, int width, int height)
    {
        bool DarkRow(int y)
        {
            var dark = 0;
            for (var x = 0; x < width; x++)
            {
                dark += gray[(y * width) + x] < EdgeLevel ? 1 : 0;
            }

            return dark > width / 2;
        }

        bool DarkColumn(int x)
        {
            var dark = 0;
            for (var y = 0; y < height; y++)
            {
                dark += gray[(y * width) + x] < EdgeLevel ? 1 : 0;
            }

            return dark > height / 2;
        }

        int Count(int limit, Func<int, bool> dark)
        {
            var n = 0;
            while (n < limit && dark(n))
            {
                n++;
            }

            return n;
        }

        int maxX = (int)(width * 0.15), maxY = (int)(height * 0.15);
        var top = Count(maxY, DarkRow);
        var bottom = Count(maxY, i => DarkRow(height - 1 - i));
        var left = Count(maxX, DarkColumn);
        var right = Count(maxX, i => DarkColumn(width - 1 - i));
        if (top + bottom + left + right == 0)
        {
            return null;
        }

        // A little more than the dark part, so no grey fringe is left.
        double Extra(int n, int size) => n == 0 ? 0 : (n / (double)size) + 0.003;
        var (l, t, r, b) = (Extra(left, width), Extra(top, height), Extra(right, width), Extra(bottom, height));
        return new NormalizedRect(l, t, 1 - l - r, 1 - t - b);
    }
}
