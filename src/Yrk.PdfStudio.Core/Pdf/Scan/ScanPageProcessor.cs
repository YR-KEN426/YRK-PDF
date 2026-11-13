namespace Yrk.PdfStudio.Core.Pdf.Scan;

/// <summary>Pixel operations stay independent of WPF; only the injected JPEG codec is platform-specific.</summary>
public sealed class ScanPageProcessor(IPdfImageCodec codec)
{
    public ProcessedScanPage Process(ScannedPage page, ScanSettings settings, int rotation = 0,
        NormalizedRect? crop = null, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var (bitmap, dpiX, dpiY, analysis) = Prepare(page, settings, rotation, ct);
        bitmap = Crop(bitmap, crop ?? analysis.Keep, ct);
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

    public PdfBitmap RenderPreview(ScannedPage page, ScanSettings settings, int rotation, int maxSide, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSide);
        var bitmap = Prepare(page, settings, rotation, ct).Bitmap;
        return Preview(bitmap, ScanCleanup.Dpi, ScanCleanup.Dpi, ct, maxSide);
    }

    private static (PdfBitmap Bitmap, double DpiX, double DpiY, ScanPageAnalysis Analysis) Prepare(
        ScannedPage page, ScanSettings settings, int rotation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Validate(page);
        ct.ThrowIfCancellationRequested();
        var bitmap = Rotate(page.Bitmap, rotation, ct);
        var quarterTurn = ((rotation % 180) + 180) % 180 != 0;
        var dpiX = quarterTurn ? page.DpiY : page.DpiX;
        var dpiY = quarterTurn ? page.DpiX : page.DpiY;
        // ScanCleanup is calibrated at 100 dpi, not at the scanner's full resolution.
        var check = ScanCleanup.Check(Preview(bitmap, dpiX, dpiY, ct));
        if (settings.AutoStraighten && check.Straighten != 0)
            bitmap = Straighten(bitmap, check.Straighten, ct);
        // Recheck edges after straightening; the previous rectangle was in the old coordinates.
        var keep = settings.AutoCrop
            ? ScanCleanup.Check(Preview(bitmap, dpiX, dpiY, ct)).Keep : null;
        // Deskew leaves white corners around formerly straight black edges. Preserve an inscribed
        // rectangle from the original edge check, expressed in the straightened canvas coordinates.
        if (settings.AutoCrop && settings.AutoStraighten && check.Straighten != 0 && check.Keep is { } originalKeep)
            keep = StraightenedKeep(originalKeep, bitmap.Width, bitmap.Height, check.Straighten);
        return (bitmap, dpiX, dpiY, new(check.IsBlank, keep));
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

    private static byte[] BilevelBmp(byte[] gray, int width, int height, CancellationToken ct)
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
