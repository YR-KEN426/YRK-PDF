using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit.Abstractions;
using Yrk.PdfStudio.App.Imaging;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SignaturePhotoVerificationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("gradient", false)]
    [InlineData("gradient", true)]
    [InlineData("fold", false)]
    [InlineData("fold", true)]
    [InlineData("specks", true)]
    [InlineData("edge", false)]
    [InlineData("edge", true)]
    public void Synthetic_photos_keep_the_known_ink_and_trim_to_its_bounds(string kind, bool blue) => Sta.Run(() =>
    {
        var (source, mask) = MakePhoto(kind, blue);
        var clean = Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.CleanPhoto(source, false));
        Measure(source, mask, clean);
        var trim = Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.CleanPhoto(source));
        var indices = Enumerable.Range(0, mask.Length).Where(p => mask[p]).ToArray();
        int width = source.PixelWidth, height = source.PixelHeight;
        int left = Math.Max(0, indices.Min(p => p % width) - 6);
        int right = Math.Min(width - 1, indices.Max(p => p % width) + 6);
        int top = Math.Max(0, indices.Min(p => p / width) - 6);
        int bottom = Math.Min(height - 1, indices.Max(p => p / width) + 6);
        Assert.Equal(right - left + 1, trim.PixelWidth);
        Assert.Equal(bottom - top + 1, trim.PixelHeight);
        var bytes = Pixels(trim);
        // The detached upper vowel is just two pixels and must survive component cleanup/trim.
        Assert.Equal(255, bytes[((142 - top) * trim.PixelWidth + 250 - left) * 4 + 3]);
        if (blue)
        {
            var ink = Enumerable.Range(0, bytes.Length / 4).Where(p => bytes[p * 4 + 3] > 200).ToArray();
            Assert.All(ink, p => Assert.True(bytes[p * 4] > 2 * bytes[p * 4 + 2]
                && bytes[p * 4] > 2 * bytes[p * 4 + 1], "Blue ink lost its hue"));
        }
        Assert.True(clean.IsFrozen);
        return true;
    });

    [Fact]
    public void Transparent_PNG_preserves_original_alpha_and_ignores_hidden_RGB() => Sta.Run(() =>
    {
        var (source, mask) = MakePhoto("shadow", true);
        var bytes = Pixels(source);
        for (var p = 0; p < mask.Length; p++)
        {
            bytes[p * 4 + 3] = mask[p] ? (byte)(48 + p % 208) : (byte)0;
            if (!mask[p])
            {
                // Arbitrary RGB in fully transparent pixels must not become paper or ink.
                bytes[p * 4] = (byte)(p % 256);
                bytes[p * 4 + 1] = 0;
                bytes[p * 4 + 2] = 255;
            }
        }
        source = Bitmap(source.PixelWidth, source.PixelHeight, bytes);
        var clean = Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.CleanPhoto(source, false));
        var result = Pixels(clean);
        var retained = 0;
        for (var p = 0; p < mask.Length; p++)
        {
            Assert.True(result[p * 4 + 3] <= bytes[p * 4 + 3]);
            if (mask[p] && result[p * 4 + 3] >= bytes[p * 4 + 3] * .95) retained++;
            if (!mask[p]) Assert.Equal(0, result[p * 4 + 3]);
        }
        var rate = 100.0 * retained / mask.Count(v => v);
        output.WriteLine($"Transparent PNG ink retained relative to original alpha: {rate:F3}%");
        Assert.True(rate >= 95);
        return true;
    });

    [Fact]
    public void Paper_with_partial_alpha_multiplies_ink_opacity_instead_of_replacing_it() => Sta.Run(() =>
    {
        var (source, mask) = MakePhoto("shadow", true);
        var bytes = Pixels(source);
        for (var p = 0; p < mask.Length; p++) bytes[p * 4 + 3] = (byte)(64 + p % 192);
        source = Bitmap(source.PixelWidth, source.PixelHeight, bytes);
        var result = Pixels(Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.CleanPhoto(source, false)));
        Assert.All(Enumerable.Range(0, mask.Length).Where(p => mask[p]), p => Assert.Equal(bytes[p * 4 + 3], result[p * 4 + 3]));
        Assert.All(Enumerable.Range(0, mask.Length).Where(p => !mask[p]), p => Assert.Equal(0, result[p * 4 + 3]));
        output.WriteLine("Partially transparent PNG: paper transparent 100%; original ink alpha retained 100%");
        return true;
    });

    [Fact]
    public void Very_small_images_skip_closing_but_keep_the_signature() => Sta.Run(() =>
    {
        const int width = 12, height = 10;
        var bytes = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        for (var x = 2; x <= 9; x++)
            for (var c = 0; c < 3; c++) bytes[(5 * width + x) * 4 + c] = 20;
        var result = Pixels(Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.CleanPhoto(Bitmap(width, height, bytes), false)));
        Assert.Equal(8, Enumerable.Range(0, width * height).Count(p => result[p * 4 + 3] == 255));
        Assert.Equal(width * height - 8, Enumerable.Range(0, width * height).Count(p => result[p * 4 + 3] == 0));
        return true;
    });

    [Theory]
    [InlineData("blank")]
    [InlineData("transparent")]
    [InlineData("dense")]
    [InlineData("speck-only")]
    public void Unusable_images_return_null(string kind) => Sta.Run(() =>
    {
        const int size = 200;
        var bytes = Enumerable.Repeat((byte)255, size * size * 4).ToArray();
        for (var p = 0; p < size * size; p++)
        {
            if (kind == "transparent") bytes[p * 4 + 3] = 0;
            if (kind == "dense" && p % 4 != 0 || kind == "speck-only" && p == 10050)
                for (var c = 0; c < 3; c++) bytes[p * 4 + c] = 10;
        }
        Assert.Null(SignatureImaging.CleanPhoto(Bitmap(size, size, bytes), false));
        return true;
    });

    [Theory]
    [InlineData(200, 10000)]
    [InlineData(10000, 200)]
    [InlineData(2000, 2000)]
    public void Image_file_decoding_is_bounded_before_cleanup(int width, int height) => Sta.Run(() =>
    {
        var bytes = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        // A thin line along the long axis, with margins large enough to verify trimming too.
        var radius = Math.Min(Math.Min(width, height) / 20, Math.Max(width, height) / 250);
        for (var y = height / 10; y < height * 9 / 10; y++)
        for (var x = width / 10; x < width * 9 / 10; x++)
        {
            if (width < height ? Math.Abs(x - width / 2) > radius : Math.Abs(y - height / 2) > radius) continue;
            for (var c = 0; c < 3; c++) bytes[(y * width + x) * 4 + c] = 20;
        }
        var path = Path.Combine(Path.GetTempPath(), $"signature-bounded-{Guid.NewGuid():N}.png");
        try
        {
            SavePng(Bitmap(width, height, bytes), path);
            var decoded = SignatureImaging.DecodePhoto(path);
            Assert.InRange(decoded.PixelWidth, 1, 1600);
            Assert.InRange(decoded.PixelHeight, 1, 1600);
            Assert.True((long)decoded.PixelWidth * decoded.PixelHeight <= 2_000_000);
            var bitmap = Assert.IsType<BitmapImage>(decoded);
            Assert.True(width >= height ? bitmap.DecodePixelWidth > 0 : bitmap.DecodePixelHeight > 0,
                "The decoder itself must be bounded, before any pixel copy/cleanup");
            var cleaned = Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.FromImageFile(path));
            Assert.True(cleaned.PixelWidth <= decoded.PixelWidth && cleaned.PixelHeight <= decoded.PixelHeight);
            output.WriteLine($"Decode {width}x{height} -> {decoded.PixelWidth}x{decoded.PixelHeight}; trimmed {cleaned.PixelWidth}x{cleaned.PixelHeight}");
        }
        finally { File.Delete(path); }
        return true;
    });

    [Fact]
    public void BMP_without_EXIF_metadata_still_imports() => Sta.Run(() =>
    {
        var (image, _) = MakePhoto("shadow", false);
        var path = Path.Combine(Path.GetTempPath(), $"signature-bmp-{Guid.NewGuid():N}.bmp");
        try
        {
            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(path)) encoder.Save(stream);
            Assert.NotNull(SignatureImaging.FromImageFile(path));
        }
        finally { File.Delete(path); }
        return true;
    });

    [Fact]
    public void Legacy_RemovePaper_matches_the_original_formula_for_every_pixel() => Sta.Run(() =>
    {
        const int width = 81, height = 57;
        var bytes = new byte[width * height * 4];
        new Random(517).NextBytes(bytes);
        // Include all greys at several alpha values, and transparent borders to exercise Trim.
        for (var p = 0; p < width * height; p++)
        {
            if (p < 1024)
            {
                bytes[p * 4] = bytes[p * 4 + 1] = bytes[p * 4 + 2] = (byte)(p % 256);
                bytes[p * 4 + 3] = (byte)(p / 256 * 85);
            }
            if (p % width < 9 || p % width >= width - 9 || p / width < 9 || p / width >= height - 9)
                bytes[p * 4 + 3] = 0;
        }
        var image = Bitmap(width, height, bytes);
        var expected = (byte[])bytes.Clone();
        for (var i = 0; i < expected.Length; i += 4)
        {
            var luminance = .114 * expected[i] + .587 * expected[i + 1] + .299 * expected[i + 2];
            if (luminance >= 225) expected[i + 3] = 0;
            else if (luminance > 170) expected[i + 3] = (byte)(expected[i + 3] * (225 - luminance) / (225 - 170));
        }
        var reference = ImageConversion.Trim(Bitmap(width, height, expected));
        var actual = SignatureImaging.RemovePaper(image);
        Assert.Equal(reference.PixelWidth, actual.PixelWidth);
        Assert.Equal(reference.PixelHeight, actual.PixelHeight);
        Assert.Equal(Pixels(reference), Pixels(actual));
        output.WriteLine($"Legacy RemovePaper: {actual.PixelWidth * actual.PixelHeight} pixels identical");
        return true;
    });

    private void Measure(BitmapSource source, bool[] mask, BitmapSource cleaned)
    {
        var bytes = Pixels(cleaned);
        int width = source.PixelWidth, height = source.PixelHeight;
        var rates = new List<double>();
        foreach (var (name, left, right) in new[] { ("bright", 0, width / 2), ("shadow", width / 2, width),
                     ("bright edge 10px", width / 2 - 10, width / 2), ("shadow edge 10px", width / 2, width / 2 + 10) })
        {
            var paper = Enumerable.Range(0, mask.Length).Where(p => !mask[p] && p % width >= left && p % width < right).ToArray();
            var rate = 100.0 * paper.Count(p => bytes[p * 4 + 3] < 32) / paper.Length;
            output.WriteLine($"Paper transparent ({name}): {rate:F3}%");
            rates.Add(rate);
        }
        var inkRate = 100.0 * Enumerable.Range(0, mask.Length).Count(p => mask[p] && bytes[p * 4 + 3] > 200) / mask.Count(v => v);
        var band = 0;
        for (var y = 0; y < height; y++)
        {
            var run = 0;
            for (var x = width / 2 - 10; x < width / 2 + 10; x++)
            {
                var p = y * width + x;
                run = !mask[p] && bytes[p * 4 + 3] >= 32 ? run + 1 : 0;
                band = Math.Max(band, run);
            }
        }
        output.WriteLine($"Opaque ink retained: {inkRate:F3}%; residual shadow band width: {band}px");
        Assert.All(rates, rate => Assert.True(rate >= 99));
        Assert.True(inkRate >= 95);
        Assert.Equal(0, band);
    }

    private static (BitmapSource Image, bool[] Mask) MakePhoto(string kind, bool blue)
    {
        const int width = 600, height = 360;
        var bytes = new byte[width * height * 4];
        var mask = new bool[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var paper = kind switch
            {
                "gradient" => 155 + x * 80 / width + y * 10 / height,
                "fold" => 235 - (int)(25 * Math.Max(0, 1 - Math.Abs(y - 205) / 14.0)),
                _ => x < width / 2 ? 235 : 160,
            };
            var ink = x >= (kind == "edge" ? 0 : 120) && x <= (kind == "edge" ? width - 1 : 480)
                && Math.Abs(y - (210 + (int)(20 * Math.Sin(x / 25.0)))) <= 2;
            ink |= x >= 249 && x <= 252 && y >= 155 && y <= 180;
            ink |= x == 250 && y is 142 or 143;
            if (kind == "edge") ink |= x is >= 70 and <= 73 || x is >= 525 and <= 528;
            var p = y * width + x;
            mask[p] = ink;
            var speck = kind == "specks" && x % 83 == 17 && y % 71 == 13 && (y < 80 || y > 280);
            bytes[p * 4] = (byte)(ink ? (blue ? 120 : 25) * paper / 235 : speck ? 15 : paper);
            bytes[p * 4 + 1] = (byte)(ink ? (blue ? 38 : 25) * paper / 235 : speck ? 15 : paper);
            bytes[p * 4 + 2] = (byte)(ink ? (blue ? 20 : 25) * paper / 235 : speck ? 15 : paper);
            bytes[p * 4 + 3] = 255;
        }
        return (Bitmap(width, height, bytes), mask);
    }

    private static BitmapSource Bitmap(int width, int height, byte[] bytes)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        image.Freeze();
        return image;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var bytes = new byte[source.PixelWidth * source.PixelHeight * 4];
        ImageConversion.ToStraightBgra(source).CopyPixels(bytes, source.PixelWidth * 4, 0);
        return bytes;
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
