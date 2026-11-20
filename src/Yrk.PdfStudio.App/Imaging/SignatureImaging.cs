using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>Handwriting fonts for typed signatures, and imported photos or scans as transparent bitmaps.</summary>
internal static class SignatureImaging
{
    private const int MaxImportSide = 1600;
    private const int MaxImportPixels = 2_000_000;

    /// <summary>Handwriting-style fonts offered for typed signatures, if installed.</summary>
    private static readonly string[] TypedFontCandidates =
    [
        "Charm", "Mali", "Sriracha", "Itim",                  // Thai (Google Fonts, OFL)
        "Dancing Script", "Great Vibes",                      // Latin (Google Fonts, OFL)
        "Ink Free", "Segoe Script", "Segoe Print",            // Shipped with Windows
        "Lucida Handwriting", "Freestyle Script", "Brush Script MT",
    ];

    public static IReadOnlyList<FontFamily> AvailableTypedFonts()
    {
        var installed = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fonts = TypedFontCandidates.Where(installed.Contains).Select(name => new FontFamily(name)).ToList();
        fonts.Add(new FontFamily("Leelawadee UI")); // Always present on Windows 10/11; covers Thai.
        return fonts;
    }

    /// <summary>
    /// Loads a bounded photo and removes its locally estimated paper, independently of the PDF path.
    /// </summary>
    public static BitmapSource? FromImageFile(string path) => CleanPhoto(DecodePhoto(path));

    internal static BitmapSource DecodePhoto(string path)
    {
        using var stream = File.OpenRead(path);
        // OnDemand + DelayCreation reads the header/metadata without materializing the full raster.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight;
        var scale = Math.Min(1, Math.Min((double)MaxImportSide / Math.Max(width, height),
            Math.Sqrt((double)MaxImportPixels / ((long)width * height))));
        var orientation = 1;
        if (frame.Metadata is BitmapMetadata metadata)
        {
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                if (metadata.GetQuery(query) is ushort value)
                {
                    orientation = value;
                    break;
                }
            }
        }

        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (scale < 1)
        {
            if (width >= height) bitmap.DecodePixelWidth = Math.Max(1, (int)(width * scale));
            else bitmap.DecodePixelHeight = Math.Max(1, (int)(height * scale));
        }
        bitmap.EndInit();
        bitmap.Freeze();

        var transform = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, 0, 0),
            3 => new Matrix(-1, 0, 0, -1, 0, 0),
            4 => new Matrix(1, 0, 0, -1, 0, 0),
            5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, 0, 0),
            7 => new Matrix(0, -1, -1, 0, 0, 0),
            8 => new Matrix(0, -1, 1, 0, 0, 0),
            _ => Matrix.Identity,
        };
        if (transform.IsIdentity) return bitmap;
        var oriented = new TransformedBitmap(bitmap, new MatrixTransform(transform));
        oriented.Freeze();
        return oriented;
    }

    /// <summary>Returns null if no usable ink remains. Tests can measure the untrimmed alpha mask.</summary>
    internal static BitmapSource? CleanPhoto(BitmapSource image, bool trim = true)
    {
        var bgra = ImageConversion.ToStraightBgra(image);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[checked(width * height * 4)];
        bgra.CopyPixels(pixels, width * 4, 0);
        var light = new float[width * height];
        for (var p = 0; p < light.Length; p++)
        {
            var i = p * 4;
            // Missing pixels have no observed RGB; use a neutral white paper estimate there.
            light[p] = pixels[i + 3] == 0 ? 255 :
                (float)((0.114 * pixels[i]) + (0.587 * pixels[i + 1]) + (0.299 * pixels[i + 2]));
        }

        float[] paper;
        if (Math.Min(width, height) < 16)
        {
            paper = new float[light.Length];
            Array.Fill(paper, 255);
        }
        else
        {
            var radius = Math.Max(1, (int)Math.Round(Math.Max(width, height) * 0.01));
            // Full-resolution rectangular grayscale closing: dilation, then erosion; no resampling.
            paper = Extremum2D(Extremum2D(light, width, height, radius, true), width, height, radius, false);
        }

        var inkCount = 0;
        for (var p = 0; p < light.Length; p++)
        {
            var i = p * 4;
            if (pixels[i + 3] == 0) continue;
            var background = Math.Max(paper[p], 16);
            var ratio = light[p] / background;
            var opacity = Math.Clamp((0.82 - ratio) / (0.82 - 0.60), 0, 1);
            pixels[i + 3] = (byte)Math.Round(pixels[i + 3] * opacity);
            for (var c = 0; c < 3; c++)
                pixels[i + c] = (byte)Math.Clamp(Math.Round(pixels[i + c] * 255.0 / background), 0, 255);
            if (pixels[i + 3] > 10) inkCount++;
        }

        if (inkCount == 0 || inkCount > light.Length * 0.60) return null;
        RemovePhotoSpecks(pixels, width, height);
        if (!Enumerable.Range(0, light.Length).Any(p => pixels[p * 4 + 3] > 10)) return null;
        var cleaned = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        cleaned.Freeze();
        return trim ? ImageConversion.Trim(cleaned) : cleaned;
    }

    private static float[] Extremum2D(float[] source, int width, int height, int radius, bool maximum)
    {
        var horizontal = new float[source.Length];
        var result = new float[source.Length];
        var length = Math.Max(width, height) + 2 * radius;
        var line = new float[length];
        var prefix = new float[length];
        var suffix = new float[length];
        for (var y = 0; y < height; y++)
            FilterLine(source, horizontal, y * width, 1, width, radius, maximum, line, prefix, suffix);
        for (var x = 0; x < width; x++)
            FilterLine(horizontal, result, x, width, height, radius, maximum, line, prefix, suffix);
        return result;
    }

    // van Herk/Gil-Werman block prefix/suffix extrema: constant work per pixel, independent of kernel size.
    private static void FilterLine(float[] source, float[] target, int start, int step, int count,
        int radius, bool maximum, float[] line, float[] prefix, float[] suffix)
    {
        int kernel = 2 * radius + 1, length = count + 2 * radius;
        for (var i = 0; i < length; i++)
            line[i] = source[start + Math.Clamp(i - radius, 0, count - 1) * step];
        for (var i = 0; i < length; i++)
            prefix[i] = i % kernel == 0 ? line[i] : Extreme(prefix[i - 1], line[i], maximum);
        for (var i = length - 1; i >= 0; i--)
            suffix[i] = i == length - 1 || (i + 1) % kernel == 0 ? line[i] : Extreme(suffix[i + 1], line[i], maximum);
        for (var i = 0; i < count; i++)
            target[start + i * step] = Extreme(suffix[i], prefix[i + kernel - 1], maximum);
    }

    private static float Extreme(float a, float b, bool maximum) => maximum ? Math.Max(a, b) : Math.Min(a, b);

    private static void RemovePhotoSpecks(byte[] pixels, int width, int height)
    {
        var labels = new int[width * height];
        var queue = new int[labels.Length];
        var sizes = new List<int>();
        var minimum = Math.Max(2, (int)Math.Ceiling(labels.Length * 0.00003));
        for (var p = 0; p < labels.Length; p++)
        {
            if (labels[p] != 0 || pixels[p * 4 + 3] <= 10) continue;
            var label = sizes.Count + 1;
            int head = 0, tail = 1;
            queue[0] = p;
            labels[p] = label;
            while (head < tail)
            {
                var current = queue[head++];
                int x = current % width, y = current / width;
                for (var ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
                for (var nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                {
                    var next = ny * width + nx;
                    if (labels[next] != 0 || pixels[next * 4 + 3] <= 10) continue;
                    labels[next] = label;
                    queue[tail++] = next;
                }
            }
            sizes.Add(tail);
        }

        // Dilate only substantial ink to protect nearby detached vowels/accents, without chaining specks.
        var substantial = new float[labels.Length];
        for (var p = 0; p < labels.Length; p++)
            if (labels[p] != 0 && sizes[labels[p] - 1] >= minimum) substantial[p] = 1;
        var nearInk = Extremum2D(substantial, width, height, Math.Max(2, (int)Math.Ceiling(Math.Max(width, height) * 0.025)), true);
        var keep = new bool[sizes.Count];
        for (var p = 0; p < labels.Length; p++)
            if (labels[p] != 0 && (sizes[labels[p] - 1] >= minimum || nearInk[p] > 0)) keep[labels[p] - 1] = true;
        for (var p = 0; p < labels.Length; p++)
            if (labels[p] != 0 && !keep[labels[p] - 1]) pixels[p * 4 + 3] = 0;
    }

    /// <summary>Makes white paper transparent and trims it (photos, scans, rendered PDF signatures).</summary>
    public static BitmapSource RemovePaper(BitmapSource image)
    {
        var bgra = ImageConversion.ToStraightBgra(image);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);

        const double paper = 225, inkLimit = 170;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var luminance = (0.114 * pixels[i]) + (0.587 * pixels[i + 1]) + (0.299 * pixels[i + 2]);
            if (luminance >= paper)
            {
                pixels[i + 3] = 0;
            }
            else if (luminance > inkLimit)
            {
                pixels[i + 3] = (byte)(pixels[i + 3] * (paper - luminance) / (paper - inkLimit));
            }
        }

        var cleaned = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        cleaned.Freeze();
        return ImageConversion.Trim(cleaned);
    }
}
