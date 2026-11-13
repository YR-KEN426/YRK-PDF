using Tesseract;
using Yrk.PdfStudio.Core.Pdf.Ocr;

namespace Yrk.PdfStudio.Core.Pdf.Scan;

/// <summary>Returns the clockwise correction; null means no reliable orientation was found.</summary>
public interface IScanOrientationDetector
{
    int? Detect(PdfBitmap bitmap, string tessdataFolder, CancellationToken cancellationToken);
}

public sealed class TesseractScanOrientationDetector : IScanOrientationDetector
{
    public int? Detect(PdfBitmap bitmap, string tessdataFolder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var engine = new TesseractEngine(tessdataFolder, "osd", EngineMode.TesseractOnly);
            using var pix = Pix.LoadFromMemory(TesseractOcrEngine.GrayBmp(bitmap));
            using var page = engine.Process(pix, PageSegMode.OsdOnly);
            page.DetectBestOrientation(out int orientation, out var confidence);
            cancellationToken.ThrowIfCancellationRequested();
            if (confidence < 15) return null;
            return (360 - orientation) % 360;
        }
        catch (Exception ex) when (ex is TesseractException or DllNotFoundException or BadImageFormatException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return null; }
    }
}

/// <summary>Pixel operations stay independent of WPF; only the injected JPEG codec is platform-specific.</summary>
public sealed class ScanPageProcessor(IPdfImageCodec codec, IScanOrientationDetector? orientationDetector = null, string? tessdataFolder = null)
{
    /// <summary>Places cropped card sides at their measured physical size, without scaling to fit.</summary>
    public static ScannedPage ComposeCard(ScannedPage front, NormalizedRect? frontCrop, ScannedPage back,
        NormalizedRect? backCrop, int dpi, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);
        var width = (int)Math.Round(210 / 25.4 * dpi);
        var height = (int)Math.Round(297 / 25.4 * dpi);
        var pixels = new byte[checked(width * height * 4)];
        Array.Fill(pixels, (byte)255);
        void Place(ScannedPage side, NormalizedRect? crop, int half)
        {
            Validate(side);
            var bitmap = Crop(side.Bitmap, crop, ct);
            var w = Math.Max(1, (int)Math.Round(bitmap.Width * dpi / side.DpiX));
            var h = Math.Max(1, (int)Math.Round(bitmap.Height * dpi / side.DpiY));
            if (w > width || h > height / 2) throw new InvalidOperationException("Crop each card side before combining it.");
            var left = (width - w) / 2;
            var top = half * (height / 2) + (height / 2 - h) / 2;
            for (var y = 0; y < h; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (var x = 0; x < w; x++)
                    bitmap.Pixels.AsSpan(Math.Min(bitmap.Height - 1, (int)(y * side.DpiY / dpi)) * bitmap.Stride
                        + Math.Min(bitmap.Width - 1, (int)(x * side.DpiX / dpi)) * 4, 4)
                        .CopyTo(pixels.AsSpan(((top + y) * width + left + x) * 4, 4));
            }
        }
        Place(front, frontCrop, 0);
        Place(back, backCrop, 1);
        return new(new(width, height, width * 4, pixels), dpi, dpi, Adjustments: new(Straighten: false));
    }

    /// <summary>Optional OSD is used only by Auto adjust. This path never downloads data.</summary>
    public int DetectOrientation(ScannedPage page, int rotation = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var folder = tessdataFolder ?? new OcrLanguageData().Folder;
        if (!File.Exists(Path.Combine(folder, "osd.traineddata"))) return 0;
        var small = CreatePreviewPage(page, 1600, ct);
        var image = Adjust(Rotate(small.Bitmap, rotation, ct), new(WhiteBackground: true), ct);
        var correction = (orientationDetector ?? new TesseractScanOrientationDetector()).Detect(image, folder, ct);
        return correction is 0 or 90 or 180 or 270 ? correction.Value : 0;
    }

    public ProcessedScanPage Process(ScannedPage page, ScanSettings settings, int rotation = 0,
        NormalizedRect? crop = null, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var (bitmap, dpiX, dpiY, analysis) = Prepare(page, settings, rotation, ct);
        bitmap = Crop(bitmap, crop ?? analysis.Keep, ct);
        bitmap = Adjust(bitmap, page.Adjustments ?? new(), ct);
        var gray = settings.ColorMode == ScanColorMode.Color ? null : Gray(bitmap, ct);
        byte[] data;
        if (settings.ColorMode == ScanColorMode.BlackAndWhite)
            data = BilevelBmp(gray!, bitmap.Width, bitmap.Height, ct);
        else
        {
            ct.ThrowIfCancellationRequested();
            var pixels = gray is null
                ? new PdfRawPixels(bitmap.Width, bitmap.Height, bitmap.Stride, PdfPixelFormat.Bgra32, bitmap.Pixels)
                : new PdfRawPixels(bitmap.Width, bitmap.Height, bitmap.Width, PdfPixelFormat.Gray8, gray);
            data = codec.EncodeJpeg(pixels, bitmap.Width, bitmap.Height, settings.JpegQuality);
        }
        ct.ThrowIfCancellationRequested();
        return new(bitmap.Width, bitmap.Height, dpiX, dpiY, settings.ColorMode, data, analysis.IsBlank);
    }

    public ScanPageAnalysis Analyze(ScannedPage page, ScanSettings settings, int rotation = 0, CancellationToken ct = default)
        => Prepare(page, settings, rotation, ct).Analysis;

    public PdfBitmap RenderPreview(ScannedPage page, ScanSettings settings, int rotation, int maxSide,
        NormalizedRect? crop = null, bool applyCrop = false, CancellationToken ct = default)
    {
        var prepared = Prepare(CreatePreviewPage(page, maxSide, ct), settings, rotation, ct);
        var bitmap = applyCrop ? Crop(prepared.Bitmap, crop ?? prepared.Analysis.Keep, ct) : prepared.Bitmap;
        bitmap = Adjust(bitmap, page.Adjustments ?? new(), ct);
        if (settings.ColorMode == ScanColorMode.Color) return bitmap;
        var gray = Gray(bitmap, ct);
        var threshold = settings.ColorMode == ScanColorMode.BlackAndWhite ? BilevelThreshold(gray) : -1;
        var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        for (var y = 0; y < bitmap.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < bitmap.Width; x++)
            {
                var i = y * bitmap.Width + x;
                var value = threshold < 0 ? gray[i] : gray[i] > threshold ? (byte)255 : (byte)0;
                pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = value;
                pixels[i * 4 + 3] = 255;
            }
        }
        return new(bitmap.Width, bitmap.Height, bitmap.Width * 4, pixels);
    }

    public static ScannedPage CreatePreviewPage(ScannedPage page, int maxSide, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSide);
        Validate(page);
        var bitmap = Preview(page.Bitmap, ScanCleanup.Dpi, ScanCleanup.Dpi, ct, maxSide);
        return page with { Bitmap = bitmap, DpiX = page.DpiX * bitmap.Width / page.Bitmap.Width,
            DpiY = page.DpiY * bitmap.Height / page.Bitmap.Height };
    }

    private static (PdfBitmap Bitmap, double DpiX, double DpiY, ScanPageAnalysis Analysis) Prepare(
        ScannedPage page, ScanSettings settings, int rotation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Validate(page);
        var adjustments = page.Adjustments ?? new();
        adjustments.Validate();
        ct.ThrowIfCancellationRequested();
        var bitmap = Rotate(page.Bitmap, rotation, ct);
        var quarterTurn = ((rotation % 180) + 180) % 180 != 0;
        var dpiX = quarterTurn ? page.DpiY : page.DpiX;
        var dpiY = quarterTurn ? page.DpiX : page.DpiY;
        // ScanCleanup is calibrated at 100 dpi, not at the scanner's full resolution.
        var checkImage = Preview(bitmap, dpiX, dpiY, ct);
        var edgeCheck = ScanCleanup.Check(checkImage);
        var check = adjustments.WhiteBackground ? ScanCleanup.Check(Adjust(checkImage, new(WhiteBackground: true), ct)) : edgeCheck;
        // Find the physical edge before whitening removes the paper/glass contrast.
        var originalKeep = edgeCheck.Keep;
        var straighten = adjustments.Straighten ?? settings.AutoStraighten;
        if (straighten && check.Straighten != 0)
            bitmap = Straighten(bitmap, check.Straighten, ct);
        var keep = settings.AutoCrop ? originalKeep : null;
        // Deskew leaves white corners around formerly straight black edges. Preserve an inscribed
        // rectangle from the original edge check, expressed in the straightened canvas coordinates.
        if (settings.AutoCrop && straighten && check.Straighten != 0 && originalKeep is { } original)
            keep = StraightenedKeep(original, bitmap.Width, bitmap.Height, check.Straighten);
        return (bitmap, dpiX, dpiY, new(check.IsBlank, keep));
    }

    private static PdfBitmap Adjust(PdfBitmap source, ScanPageAdjustments edits, CancellationToken ct)
    {
        if (edits.Brightness == 0 && edits.Sharpness == 0 && !edits.WhiteBackground) return source;
        var gray = Gray(source, ct);
        var background = 255;
        var black = 0;
        if (edits.WhiteBackground)
        {
            var histogram = new int[256];
            foreach (var value in gray) histogram[value]++;
            int Percentile(int count)
            {
                var sum = 0;
                for (var i = 0; i < 256; i++) { sum += histogram[i]; if (sum >= count) return i; }
                return 255;
            }
            background = Math.Max(1, Percentile(Math.Max(1, (int)(gray.Length * 0.90))));
            black = Math.Min(background - 1, Percentile(Math.Max(1, (int)(gray.Length * 0.005))));
            black = Math.Max(0, Math.Min(black, background - 32));
        }
        var pixels = new byte[checked(source.Width * source.Height * 4)];
        for (var y = 0; y < source.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < source.Width; x++)
            {
                var index = y * source.Width + x;
                double tone = gray[index];
                if (edits.WhiteBackground)
                {
                    var stretched = Math.Clamp((tone - black) * 255 / (background - black), 0, 255);
                    // Preserve/darken faint ink, while allowing paper pixels to reach white.
                    tone = tone < background - 12 ? Math.Min(tone, stretched) : stretched;
                }
                var delta = tone - gray[index] + edits.Brightness * 2;
                for (var channel = 0; channel < 3; channel++)
                {
                    double value = source.Pixels[y * source.Stride + x * 4 + channel] + delta;
                    pixels[index * 4 + channel] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
                }
                pixels[index * 4 + 3] = 255;
            }
        }
        if (edits.Sharpness == 0) return new(source.Width, source.Height, source.Width * 4, pixels);
        var result = (byte[])pixels.Clone();
        // Unsharp mask for positive values, the same local blur blended in for negative ones.
        var amount = edits.Sharpness / 25.0;
        for (var y = 1; y < source.Height - 1; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 1; x < source.Width - 1; x++)
                for (var c = 0; c < 3; c++)
                {
                    var i = (y * source.Width + x) * 4 + c;
                    var sum = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++) sum += pixels[((y + dy) * source.Width + x + dx) * 4 + c];
                    var blend = amount < 0 ? amount / 2 : amount;
                    result[i] = (byte)Math.Clamp((int)Math.Round(pixels[i] + blend * (pixels[i] - sum / 9.0)), 0, 255);
                }
        }
        return new(source.Width, source.Height, source.Width * 4, result);
    }

    internal static void Validate(ScannedPage page)
    {
        var b = page.Bitmap;
        if (b.Width <= 0 || b.Height <= 0 || b.Stride < checked(b.Width * 4)
            || b.Pixels.LongLength < (long)b.Stride * b.Height)
            throw new ArgumentException("Invalid BGRA bitmap.", nameof(page));
        if (!double.IsFinite(page.DpiX) || !double.IsFinite(page.DpiY) || page.DpiX <= 0 || page.DpiY <= 0)
            throw new ArgumentException("Invalid scan resolution.", nameof(page));
    }

    /// <summary>Clockwise quarter turns; crop coordinates refer to the resulting, straightened page.</summary>
    public static PdfBitmap Rotate(PdfBitmap source, int degrees, CancellationToken cancellationToken = default)
    {
        if (degrees % 90 != 0) throw new ArgumentOutOfRangeException(nameof(degrees));
        var turn = ((degrees % 360) + 360) % 360;
        var w = turn is 90 or 270 ? source.Height : source.Width;
        var h = turn is 90 or 270 ? source.Width : source.Height;
        var pixels = new byte[checked(w * h * 4)];
        for (var y = 0; y < source.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < source.Width; x++)
            {
                var (dx, dy) = turn switch
                {
                    90 => (source.Height - 1 - y, x),
                    180 => (source.Width - 1 - x, source.Height - 1 - y),
                    270 => (y, source.Width - 1 - x),
                    _ => (x, y),
                };
                source.Pixels.AsSpan(y * source.Stride + x * 4, 4).CopyTo(pixels.AsSpan((dy * w + dx) * 4, 4));
            }
        }
        return new(w, h, w * 4, pixels);
    }

    private static PdfBitmap Preview(PdfBitmap source, double dpiX, double dpiY, CancellationToken ct, int maxSide = 1600)
    {
        var scale = Math.Min(1, ScanCleanup.Dpi / Math.Max(dpiX, dpiY));
        // Bound work even if a driver sends a bogus DPI value.
        scale = Math.Min(scale, (double)maxSide / Math.Max(source.Width, source.Height));
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var pixels = new byte[checked(w * h * 4)];
        for (var y = 0; y < h; y++)
        {
            ct.ThrowIfCancellationRequested();
            // Span the complete source, including its last row/column. Dividing by
            // scale skips the trailing edge (and thin scanner borders) after rounding.
            var sy = h == 1 ? source.Height / 2 : (int)((long)y * (source.Height - 1) / (h - 1));
            for (var x = 0; x < w; x++)
            {
                var sx = w == 1 ? source.Width / 2 : (int)((long)x * (source.Width - 1) / (w - 1));
                source.Pixels.AsSpan(sy * source.Stride + sx * 4, 4)
                    .CopyTo(pixels.AsSpan((y * w + x) * 4, 4));
            }
        }
        return new(w, h, w * 4, pixels);
    }

    private static PdfBitmap Straighten(PdfBitmap source, double degrees, CancellationToken ct)
    {
        var radians = degrees * Math.PI / 180;
        var (sin, cos) = (Math.Sin(radians), Math.Cos(radians));
        var pixels = new byte[checked(source.Width * source.Height * 4)];
        Array.Fill(pixels, (byte)255);
        double cx = (source.Width - 1) / 2.0, cy = (source.Height - 1) / 2.0;
        for (var y = 0; y < source.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < source.Width; x++)
            {
                // Inverse of a counter-clockwise visual rotation, in top-left image coordinates.
                var sx = (int)Math.Round(cx + (x - cx) * cos - (y - cy) * sin);
                var sy = (int)Math.Round(cy + (x - cx) * sin + (y - cy) * cos);
                if (sx >= 0 && sx < source.Width && sy >= 0 && sy < source.Height)
                    source.Pixels.AsSpan(sy * source.Stride + sx * 4, 4)
                        .CopyTo(pixels.AsSpan((y * source.Width + x) * 4, 4));
            }
        }
        return new(source.Width, source.Height, source.Width * 4, pixels);
    }

    private static PdfBitmap Crop(PdfBitmap source, NormalizedRect? crop, CancellationToken ct)
    {
        if (crop is not { } r) return source;
        if (!double.IsFinite(r.X + r.Y + r.Width + r.Height) || r.X < 0 || r.Y < 0
            || r.Width <= 0 || r.Height <= 0 || r.X + r.Width > 1 || r.Y + r.Height > 1)
            throw new ArgumentOutOfRangeException(nameof(crop));
        var x = Math.Min(source.Width - 1, (int)Math.Round(r.X * source.Width));
        var y = Math.Min(source.Height - 1, (int)Math.Round(r.Y * source.Height));
        var w = Math.Clamp((int)Math.Round(r.Width * source.Width), 1, source.Width - x);
        var h = Math.Clamp((int)Math.Round(r.Height * source.Height), 1, source.Height - y);
        var pixels = new byte[checked(w * h * 4)];
        for (var row = 0; row < h; row++)
        {
            ct.ThrowIfCancellationRequested();
            source.Pixels.AsSpan((y + row) * source.Stride + x * 4, w * 4).CopyTo(pixels.AsSpan(row * w * 4));
        }
        return new(w, h, w * 4, pixels);
    }

    private static NormalizedRect StraightenedKeep(NormalizedRect rect, int width, int height, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var (sin, cos) = (Math.Sin(radians), Math.Cos(radians));
        var w = rect.Width * width;
        var h = rect.Height * height;
        var dx = (rect.X + rect.Width / 2 - 0.5) * width;
        var dy = (rect.Y + rect.Height / 2 - 0.5) * height;
        var cx = width / 2.0 + dx * cos + dy * sin;
        var cy = height / 2.0 - dx * sin + dy * cos;
        var denominator = cos * cos - sin * sin;
        var innerW = Math.Max(1, (w * cos - h * Math.Abs(sin)) / denominator);
        var innerH = Math.Max(1, (h * cos - w * Math.Abs(sin)) / denominator);
        var left = Math.Clamp(cx - innerW / 2, 0, width - 1);
        var top = Math.Clamp(cy - innerH / 2, 0, height - 1);
        return new(left / width, top / height, Math.Min(innerW, width - left) / width, Math.Min(innerH, height - top) / height);
    }

    private static byte[] Gray(PdfBitmap source, CancellationToken ct)
    {
        var gray = new byte[checked(source.Width * source.Height)];
        for (var y = 0; y < source.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < source.Width; x++)
            {
                var i = y * source.Stride + x * 4;
                gray[y * source.Width + x] = (byte)((source.Pixels[i] * 114 + source.Pixels[i + 1] * 587 + source.Pixels[i + 2] * 299) / 1000);
            }
        }
        return gray;
    }

    private static int BilevelThreshold(byte[] gray)
    {
        var histogram = new long[256];
        foreach (var value in gray) histogram[value]++;
        var total = gray.LongLength;
        var sum = histogram.Select((n, i) => n * i).Sum();
        long below = 0, sumBelow = 0;
        double best = -1;
        var threshold = 127;
        for (var t = 0; t < 255; t++)
        {
            below += histogram[t];
            sumBelow += histogram[t] * t;
            if (below == 0 || below == total) continue;
            var difference = (double)sumBelow / below - (double)(sum - sumBelow) / (total - below);
            var score = below * (double)(total - below) * difference * difference;
            if (score > best) (best, threshold) = (score, t);
        }
        return threshold;
    }

    private static byte[] BilevelBmp(byte[] gray, int width, int height, CancellationToken ct)
    {
        var threshold = BilevelThreshold(gray);
        var stride = checked((width + 31) / 32 * 4);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4d42);
        writer.Write(checked(62 + stride * height));
        writer.Write(0);
        writer.Write(62);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height); // BMP rows are bottom-up.
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(0);
        writer.Write(stride * height);
        writer.Write(0); writer.Write(0);
        writer.Write(2); writer.Write(2);
        writer.Write(0x00000000); writer.Write(0x00ffffff);
        var row = new byte[stride];
        for (var y = height - 1; y >= 0; y--)
        {
            ct.ThrowIfCancellationRequested();
            Array.Clear(row);
            for (var x = 0; x < width; x++)
                if (gray[y * width + x] > threshold) row[x / 8] |= (byte)(0x80 >> (x % 8));
            writer.Write(row);
        }
        return stream.ToArray();
    }
}
