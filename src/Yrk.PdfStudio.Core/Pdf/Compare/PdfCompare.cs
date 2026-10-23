namespace Yrk.PdfStudio.Core.Pdf.Compare;

/// <param name="Overlay">Both versions in one picture: unchanged ink grey, ink only in the first version red,
/// ink only in the second version blue.</param>
/// <param name="Regions">Areas with changes, largest first (pixels of the overlay).</param>
/// <param name="RemovedPixels">Ink pixels only in the first version.</param>
/// <param name="AddedPixels">Ink pixels only in the second version.</param>
public sealed record PdfDiffResult(PdfBitmap Overlay, IReadOnlyList<PdfPixelRegion> Regions, int RemovedPixels, int AddedPixels)
{
    public bool HasChanges => Regions.Count > 0;
}

/// <summary>
/// Visual comparison of two renderings of the same page size. A pixel counts as changed only when the other
/// version has no ink within <see cref="Tolerance"/> pixels, so anti-aliasing and one-pixel shifts between
/// exports are not reported.
/// </summary>
public static class PdfCompare
{
    public const int Tolerance = 1;
    private const int InkThreshold = 60;   // darkness (0–255) above which a pixel counts as ink
    private const int Cell = 8;            // pixels per cell when grouping changes into regions
    private const int RegionGap = 2;       // cells between changes that still count as one region
    private const int MinRegionPixels = 6; // smaller specks are ignored
    private const int MaxRegions = 300;

    private static readonly (byte B, byte G, byte R) Removed = (40, 40, 215);
    private static readonly (byte B, byte G, byte R) Added = (230, 110, 20);

    public static PdfDiffResult Diff(PdfBitmap first, PdfBitmap second)
    {
        if (first.Width != second.Width || first.Height != second.Height)
        {
            throw new ArgumentException("Both renderings must have the same size.", nameof(second));
        }

        int width = first.Width, height = first.Height;
        var inkFirst = Ink(first);
        var inkSecond = Ink(second);
        var nearFirst = Dilate(inkFirst, width, height);
        var nearSecond = Dilate(inkSecond, width, height);

        var overlay = new byte[width * height * 4];
        var changed = new bool[width * height];
        int removed = 0, added = 0;
        for (var i = 0; i < width * height; i++)
        {
            var o = i * 4;
            var a = inkFirst[i];
            var b = inkSecond[i];
            if (a > 0 && !nearSecond[i])
            {
                Paint(overlay, o, Removed, a);
                changed[i] = true;
                removed++;
            }
            else if (b > 0 && !nearFirst[i])
            {
                Paint(overlay, o, Added, b);
                changed[i] = true;
                added++;
            }
            else
            {
                // Unchanged: a light grey version of the darker of the two.
                var grey = (byte)(255 - (Math.Max(a, b) * 0.55));
                overlay[o] = overlay[o + 1] = overlay[o + 2] = grey;
                overlay[o + 3] = 255;
            }
        }

        return new PdfDiffResult(new PdfBitmap(width, height, width * 4, overlay), Regions(changed, width, height), removed, added);
    }

    /// <summary>Darkness per pixel (0 = paper), from BGRA.</summary>
    private static byte[] Ink(PdfBitmap bitmap)
    {
        var ink = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = bitmap.Pixels.AsSpan(y * bitmap.Stride, bitmap.Width * 4);
            for (var x = 0; x < bitmap.Width; x++)
            {
                var luminance = ((row[(x * 4) + 2] * 299) + (row[(x * 4) + 1] * 587) + (row[x * 4] * 114)) / 1000;
                var darkness = 255 - luminance;
                ink[(y * bitmap.Width) + x] = darkness >= InkThreshold ? (byte)darkness : (byte)0;
            }
        }

        return ink;
    }

    /// <summary>True where there is ink within <see cref="Tolerance"/> pixels.</summary>
    private static bool[] Dilate(byte[] ink, int width, int height)
    {
        var horizontal = new bool[ink.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var d = -Tolerance; d <= Tolerance; d++)
                {
                    var xx = x + d;
                    if (xx >= 0 && xx < width && ink[(y * width) + xx] > 0)
                    {
                        horizontal[(y * width) + x] = true;
                        break;
                    }
                }
            }
        }

        var result = new bool[ink.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var d = -Tolerance; d <= Tolerance; d++)
                {
                    var yy = y + d;
                    if (yy >= 0 && yy < height && horizontal[(yy * width) + x])
                    {
                        result[(y * width) + x] = true;
                        break;
                    }
                }
            }
        }

        return result;
    }

    private static void Paint(byte[] overlay, int offset, (byte B, byte G, byte R) color, byte strength)
    {
        // Full colour for solid ink, lighter for anti-aliased edges.
        var t = Math.Clamp(strength / 160.0, 0.35, 1);
        overlay[offset] = (byte)(255 - ((255 - color.B) * t));
        overlay[offset + 1] = (byte)(255 - ((255 - color.G) * t));
        overlay[offset + 2] = (byte)(255 - ((255 - color.R) * t));
        overlay[offset + 3] = 255;
    }

    /// <summary>Groups changed pixels into rectangles: cells with changes, joined when close together.</summary>
    private static List<PdfPixelRegion> Regions(bool[] changed, int width, int height)
    {
        int columns = (width + Cell - 1) / Cell, rows = (height + Cell - 1) / Cell;
        var counts = new int[columns * rows];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (changed[(y * width) + x])
                {
                    counts[((y / Cell) * columns) + (x / Cell)]++;
                }
            }
        }

        var seen = new bool[counts.Length];
        var regions = new List<(PdfPixelRegion Region, int Pixels)>();
        var stack = new Stack<int>();
        for (var start = 0; start < counts.Length; start++)
        {
            if (counts[start] == 0 || seen[start])
            {
                continue;
            }

            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, pixels = 0;
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                var cell = stack.Pop();
                int cx = cell % columns, cy = cell / columns;
                pixels += counts[cell];
                minX = Math.Min(minX, cx);
                maxX = Math.Max(maxX, cx);
                minY = Math.Min(minY, cy);
                maxY = Math.Max(maxY, cy);
                for (var dy = -RegionGap; dy <= RegionGap; dy++)
                {
                    for (var dx = -RegionGap; dx <= RegionGap; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= columns || ny >= rows)
                        {
                            continue;
                        }

                        var next = (ny * columns) + nx;
                        if (counts[next] > 0 && !seen[next])
                        {
                            seen[next] = true;
                            stack.Push(next);
                        }
                    }
                }
            }

            if (pixels >= MinRegionPixels)
            {
                var x0 = minX * Cell;
                var y0 = minY * Cell;
                regions.Add((new PdfPixelRegion(x0, y0, Math.Min(width, (maxX + 1) * Cell) - x0, Math.Min(height, (maxY + 1) * Cell) - y0), pixels));
            }
        }

        return regions.OrderByDescending(r => r.Pixels).Take(MaxRegions).Select(r => r.Region).ToList();
    }
}
