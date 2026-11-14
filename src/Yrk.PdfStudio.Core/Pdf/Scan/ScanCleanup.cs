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
        return new ScanPageCheck(blank, blank ? 0 : Straighten(ink),
            DarkEdges(gray, bitmap.Width, bitmap.Height) ?? ObjectOnGlass(gray, bitmap.Width, bitmap.Height));
    }

    /// <summary>Finds a filled rectangular object against the flatbed's border colour, including a white lid.
    /// Sparse text alone is deliberately insufficient evidence of a paper edge.</summary>
    private static NormalizedRect? ObjectOnGlass(byte[] gray, int width, int height)
    {
        if (width < 20 || height < 20) return null;
        var histogram = new int[256];
        for (var x = 0; x < width; x++) { histogram[gray[x]]++; histogram[gray[(height - 1) * width + x]]++; }
        for (var y = 0; y < height; y++) { histogram[gray[y * width]]++; histogram[gray[y * width + width - 1]]++; }
        var sum = 0;
        var background = 255;
        for (var i = 0; i < 256; i++)
        {
            sum += histogram[i];
            if (sum >= width + height) { background = i; break; }
        }
        bool Object(int x, int y) => Math.Abs(gray[y * width + x] - background) >= 6;
        var rows = new int[height];
        var columns = new int[width];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (Object(x, y)) { rows[y]++; columns[x]++; }
        var left = Array.FindIndex(columns, n => n >= Math.Max(5, height * 0.03));
        var right = Array.FindLastIndex(columns, n => n >= Math.Max(5, height * 0.03));
        var top = Array.FindIndex(rows, n => n >= Math.Max(5, width * 0.03));
        var bottom = Array.FindLastIndex(rows, n => n >= Math.Max(5, width * 0.03));
        if (left < 0 || top < 0 || right - left < width * 0.1 || bottom - top < height * 0.08
            || left < 2 || top < 2 || right >= width - 2 || bottom >= height - 2) return null;
        long filled = 0;
        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++) if (Object(x, y)) filled++;
        if (filled < (right - left + 1L) * (bottom - top + 1) * 0.65) return null;
        // All four boundaries must have substantial support. Do not crop a text block or a photograph's isolated marks.
        if (rows[top] < (right - left) * 0.65 || rows[bottom] < (right - left) * 0.65
            || columns[left] < (bottom - top) * 0.65 || columns[right] < (bottom - top) * 0.65) return null;
        left--; top--; right++; bottom++;
        return new((double)left / width, (double)top / height, (double)(right - left + 1) / width, (double)(bottom - top + 1) / height);
    }

    /// <summary>Ink bounds on an adjusted image, excluding the outer 1% and isolated dust.</summary>
    public static NormalizedRect? ContentBounds(PdfBitmap bitmap)
    {
        var gray = Gray(bitmap);
        var width = bitmap.Width; var height = bitmap.Height;
        var edgeX = Math.Max(1, width / 100); var edgeY = Math.Max(1, height / 100);
        var histogram = new long[256];
        for (var y = edgeY; y < height - edgeY; y++)
            for (var x = edgeX; x < width - edgeX; x++) histogram[gray[y * width + x]]++;
        var total = histogram.Sum();
        if (total == 0) return null;
        var sum = histogram.Select((n, i) => n * i).Sum();
        long count = 0, weighted = 0; double best = -1; var threshold = 0;
        var background = Array.FindIndex(histogram, n => n > 0);
        for (var i = 0; i < 256; i++)
        {
            count += histogram[i]; weighted += histogram[i] * i;
            if (count < total * .9) background = i + 1;
            if (count == 0 || count == total) continue;
            var difference = (double)weighted / count - (double)(sum - weighted) / (total - count);
            var score = (double)count * (total - count) * difference * difference;
            if (score > best) { best = score; threshold = i; }
        }
        threshold = Math.Min(threshold, background - 12);
        var visited = new bool[gray.Length]; var component = new List<int>();
        var left = width; var right = -1; var top = height; var bottom = -1;
        for (var y = edgeY; y < height - edgeY; y++)
        for (var x = edgeX; x < width - edgeX; x++)
        {
            var start = y * width + x;
            if (visited[start] || gray[start] > threshold) continue;
            component.Clear(); component.Add(start); visited[start] = true;
            var l = x; var r = x; var t = y; var b = y;
            for (var n = 0; n < component.Count; n++)
            {
                var px = component[n] % width; var py = component[n] / width;
                l = Math.Min(l, px); r = Math.Max(r, px); t = Math.Min(t, py); b = Math.Max(b, py);
                Visit(px - 1, py); Visit(px + 1, py); Visit(px, py - 1); Visit(px, py + 1);
            }
            if (component.Count < 6 || r == l || b == t) continue;
            left = Math.Min(left, l); right = Math.Max(right, r); top = Math.Min(top, t); bottom = Math.Max(bottom, b);
        }
        return right < left ? null : new((double)left / width, (double)top / height,
            (double)(right - left + 1) / width, (double)(bottom - top + 1) / height);

        void Visit(int x, int y)
        {
            if (x < edgeX || x >= width - edgeX || y < edgeY || y >= height - edgeY) return;
            var next = y * width + x;
            if (visited[next] || gray[next] > threshold) return;
            visited[next] = true; component.Add(next);
        }
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
