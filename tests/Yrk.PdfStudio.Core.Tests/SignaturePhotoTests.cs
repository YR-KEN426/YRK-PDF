using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit.Abstractions;
using Yrk.PdfStudio.App.Imaging;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SignaturePhotoTests(ITestOutputHelper output)
{
    [Fact]
    public void Sharp_shadow_preserves_crossing_ink_and_removes_paper_at_the_boundary() => Sta.Run(() =>
    {
        var photo = Photo("shadow");
        var cleaned = Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.CleanPhoto(photo.Image, trim: false));
        SaveShadowExample(photo.Image, cleaned);
        Measure(photo, cleaned);
        return true;
    });

    [Theory]
    [InlineData("gradient", true, false)]
    [InlineData("gradient", false, false)]
    [InlineData("fold", true, false)]
    [InlineData("fold", false, true)]
    [InlineData("shadow", false, true)]
    public void Uneven_paper_folds_and_scattered_specks_leave_blue_or_black_ink(string background, bool blue, bool noise) => Sta.Run(() =>
    {
        var photo = Photo(background, blue, noise);
        var cleaned = Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.CleanPhoto(photo.Image, trim: false));
        Measure(photo, cleaned);
        // An isolated speck far from the writing must disappear.
        if (noise) Assert.Equal(0, Pixels(cleaned)[(8 * 600 + 9) * 4 + 3]);
        AssertTrimAndColor(photo, blue);
        return true;
    });

    [Fact]
    public void Ink_touching_the_image_edge_is_retained_across_a_sharp_shadow() => Sta.Run(() =>
    {
        var photo = Photo("shadow", edge: true);
        Measure(photo, Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.CleanPhoto(photo.Image, trim: false)));
        var trimmed = Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.CleanPhoto(photo.Image));
        Assert.Equal(482, trimmed.PixelWidth); // x=0 through x=475, plus the six-pixel right margin.
        Assert.True(Pixels(trimmed).Where((_, i) => i % (trimmed.PixelWidth * 4) == 3).Any(a => a > 200));
        return true;
    });

    [Fact]
    public void Transparent_png_ignores_hidden_rgb_and_keeps_original_ink_alpha() => Sta.Run(() =>
    {
        var photo = Photo("shadow");
        var bytes = Pixels(photo.Image);
        for (var p = 0; p < photo.Ink.Length; p++)
        {
            if (photo.Ink[p]) bytes[p * 4 + 3] = (byte)(32 + (p % 4) * 64);
            else
            {
                bytes[p * 4] = (byte)(p % 256);
                bytes[p * 4 + 1] = 0;
                bytes[p * 4 + 2] = 0;
                bytes[p * 4 + 3] = 0;
            }
        }
        photo = photo with { Image = Bitmap(600, 360, bytes) };
        var cleaned = Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.CleanPhoto(photo.Image, trim: false));
        Measure(photo, cleaned);
        var result = Pixels(cleaned);
        for (var p = 0; p < photo.Ink.Length; p++) Assert.Equal(bytes[p * 4 + 3], result[p * 4 + 3]);
        return true;
    });

    [Fact]
    public void Partly_transparent_paper_multiplies_original_alpha_by_the_soft_ink_mask() => Sta.Run(() =>
    {
        var bytes = Pixels(Photo("plain").Image);
        // A faint edge near substantial ink: luminance 170/235 lies between the two ratio thresholds.
        const int p = 203 * 600 + 250;
        bytes[p * 4] = bytes[p * 4 + 1] = bytes[p * 4 + 2] = 170;
        bytes[p * 4 + 3] = 128;
        var cleaned = Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.CleanPhoto(Bitmap(600, 360, bytes), trim: false));
        var expected = (byte)Math.Round(128 * Math.Clamp((0.82 - 170.0 / 235) / 0.22, 0, 1));
        Assert.Equal(expected, Pixels(cleaned)[p * 4 + 3]);
        Assert.InRange(expected, (byte)1, (byte)127);
        return true;
    });

    [Fact]
    public void Very_small_images_skip_closing_and_keep_their_ink() => Sta.Run(() =>
    {
        var bytes = SolidPixels(12, 12, 235);
        for (var y = 5; y <= 6; y++)
        for (var x = 3; x <= 8; x++)
        {
            var i = (y * 12 + x) * 4;
            bytes[i] = bytes[i + 1] = bytes[i + 2] = 20;
        }
        var cleaned = Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.CleanPhoto(Bitmap(12, 12, bytes), trim: false));
        var result = Pixels(cleaned);
        Assert.Equal(12, Enumerable.Range(0, 144).Count(p => result[p * 4 + 3] == 255));
        Assert.Equal(132, Enumerable.Range(0, 144).Count(p => result[p * 4 + 3] == 0));
        return true;
    });

    [Fact]
    public void Empty_specks_only_and_excessive_ink_are_rejected() => Sta.Run(() =>
    {
        Assert.Null(SignatureImaging.CleanPhoto(Bitmap(200, 200, SolidPixels(200, 200, 235))));
        Assert.Null(SignatureImaging.CleanPhoto(Bitmap(200, 200, new byte[200 * 200 * 4])));
        Assert.Null(SignatureImaging.CleanPhoto(Bitmap(200, 200, SolidPixels(200, 200, 20))));
        var specks = SolidPixels(200, 200, 235);
        for (var p = 0; p < 40_000; p += 999)
            specks[p * 4] = specks[p * 4 + 1] = specks[p * 4 + 2] = 20;
        Assert.Null(SignatureImaging.CleanPhoto(Bitmap(200, 200, specks)));
        var excessive = SolidPixels(200, 200, 235);
        for (var p = 0; p < 40_000; p++)
            if (p % 4 != 0) excessive[p * 4] = excessive[p * 4 + 1] = excessive[p * 4 + 2] = 20;
        Assert.Null(SignatureImaging.CleanPhoto(Bitmap(200, 200, excessive)));
        return true;
    });

    [Theory]
    [InlineData(200, 10000, 32, 1600)]
    [InlineData(10000, 200, 1600, 32)]
    [InlineData(2000, 2000, 1414, 1414)]
    public void File_decode_bounds_both_sides_and_pixel_count_before_photo_cleanup(int width, int height, int expectedWidth, int expectedHeight) => Sta.Run(() =>
    {
        var bytes = SolidPixels(width, height, 235);
        // The wide source stroke still has a measurable interior after the decoder scales it down.
        for (var y = height / 3; y < height / 3 + Math.Max(8, height / 200); y++)
        for (var x = width / 4; x < width * 3 / 4; x++)
        {
            var i = (y * width + x) * 4;
            bytes[i] = bytes[i + 1] = bytes[i + 2] = 20;
        }
        var path = Path.Combine(Path.GetTempPath(), $"signature-photo-{Guid.NewGuid():N}.png");
        try
        {
            SavePng(Bitmap(width, height, bytes), path);
            var decoded = SignatureImaging.DecodePhoto(path);
            Assert.Equal(expectedWidth, decoded.PixelWidth);
            Assert.Equal(expectedHeight, decoded.PixelHeight);
            Assert.True((long)decoded.PixelWidth * decoded.PixelHeight <= 2_000_000);
            var imported = Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.FromImageFile(path));
            Assert.InRange(imported.PixelWidth, 1, expectedWidth);
            Assert.InRange(imported.PixelHeight, 1, expectedHeight);
            output.WriteLine($"Decode {width}x{height} -> {decoded.PixelWidth}x{decoded.PixelHeight}; trimmed {imported.PixelWidth}x{imported.PixelHeight}");
            // Guard the allocation order too: output dimensions alone would also pass a full decode + resize.
            var source = File.ReadAllText(Path.Combine(SourceRoot(), "src", "Yrk.PdfStudio.App", "Imaging", "SignatureImaging.cs"));
            var decode = source[source.IndexOf("internal static BitmapSource DecodePhoto", StringComparison.Ordinal)..source.IndexOf("internal static BitmapSource? CleanPhoto", StringComparison.Ordinal)];
            Assert.Contains("BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand", decode);
            Assert.Contains("bitmap.DecodePixelWidth =", decode);
            Assert.Contains("bitmap.DecodePixelHeight =", decode);
            Assert.True(decode.IndexOf("bitmap.DecodePixelHeight =", StringComparison.Ordinal) < decode.IndexOf("bitmap.EndInit()", StringComparison.Ordinal));
            Assert.DoesNotContain("CopyPixels", decode);
            Assert.DoesNotContain("ScaleTransform", decode);
        }
        finally { File.Delete(path); }
        return true;
    });

    [Theory]
    [InlineData(1, 80, 40)]
    [InlineData(2, 80, 40)]
    [InlineData(3, 80, 40)]
    [InlineData(4, 80, 40)]
    [InlineData(5, 40, 80)]
    [InlineData(6, 40, 80)]
    [InlineData(7, 40, 80)]
    [InlineData(8, 40, 80)]
    public void Jpeg_exif_orientation_is_applied_before_cleanup(int orientation, int expectedWidth, int expectedHeight) => Sta.Run(() =>
    {
        var bytes = SolidPixels(80, 40, 235);
        for (var y = 8; y < 10; y++)
        for (var x = 6; x < 16; x++)
        {
            var i = (y * 80 + x) * 4;
            bytes[i] = bytes[i + 1] = bytes[i + 2] = 20;
        }
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
        var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(Bitmap(80, 40, bytes), null, metadata, null));
        var path = Path.Combine(Path.GetTempPath(), $"signature-exif-{Guid.NewGuid():N}.jpg");
        try
        {
            using (var file = File.Create(path)) encoder.Save(file);
            var decoded = SignatureImaging.DecodePhoto(path);
            Assert.Equal(expectedWidth, decoded.PixelWidth);
            Assert.Equal(expectedHeight, decoded.PixelHeight);
            var expectedPosition = orientation switch
            {
                2 => (69, 9), 3 => (69, 30), 4 => (10, 30), 5 => (9, 10),
                6 => (30, 10), 7 => (30, 69), 8 => (9, 69), _ => (10, 9),
            };
            Assert.InRange(Pixels(decoded)[(expectedPosition.Item2 * expectedWidth + expectedPosition.Item1) * 4], (byte)0, (byte)40);
            Assert.NotNull(SignatureImaging.FromImageFile(path));
        }
        finally { File.Delete(path); }
        return true;
    });

    [Fact]
    public void Rendered_pdf_RemovePaper_matches_the_original_formula_pixel_for_pixel() => Sta.Run(() =>
    {
        const int width = 113, height = 79;
        var bytes = new byte[width * height * 4];
        new Random(5317).NextBytes(bytes);
        var original = Bitmap(width, height, bytes);
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var luminance = (0.114 * bytes[i]) + (0.587 * bytes[i + 1]) + (0.299 * bytes[i + 2]);
            if (luminance >= 225) bytes[i + 3] = 0;
            else if (luminance > 170) bytes[i + 3] = (byte)(bytes[i + 3] * (225 - luminance) / (225 - 170));
        }
        var expected = ImageConversion.Trim(Bitmap(width, height, bytes));
        var actual = SignatureImaging.RemovePaper(original);
        Assert.Equal(expected.PixelWidth, actual.PixelWidth);
        Assert.Equal(expected.PixelHeight, actual.PixelHeight);
        Assert.Equal(Pixels(expected), Pixels(actual));
        return true;
    });

    private static void AssertTrimAndColor(Synthetic photo, bool blue)
    {
        var trimmed = Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.CleanPhoto(photo.Image));
        var ink = Enumerable.Range(0, photo.Ink.Length).Where(p => photo.Ink[p]).ToArray();
        int left = ink.Min(p => p % 600), right = ink.Max(p => p % 600);
        int top = ink.Min(p => p / 600), bottom = ink.Max(p => p / 600);
        // Nearby specks may survive intentionally; allow at most the proximity radius at each edge.
        Assert.InRange(trimmed.PixelWidth, right - left + 1 + 12, right - left + 1 + 42);
        Assert.InRange(trimmed.PixelHeight, bottom - top + 1 + 12, bottom - top + 1 + 42);
        var untrimmed = Pixels(Assert.IsAssignableFrom<BitmapSource>(SignatureImaging.CleanPhoto(photo.Image, trim: false)));
        Assert.Equal(255, untrimmed[(166 * 600 + 201) * 4 + 3]);
        var result = Pixels(trimmed);
        Assert.Equal(2, Enumerable.Range(0, trimmed.PixelWidth * trimmed.PixelHeight)
            .Count(p => result[p * 4 + 3] == 255 && p / trimmed.PixelWidth is 6 or 7));
        foreach (var p in ink)
        {
            var i = p * 4;
            if (blue) Assert.True(untrimmed[i] > untrimmed[i + 1] * 2 && untrimmed[i] > untrimmed[i + 2] * 3);
            else Assert.True(untrimmed[i] == untrimmed[i + 1] && untrimmed[i] == untrimmed[i + 2]);
        }
    }

    private static byte[] SolidPixels(int width, int height, byte value)
    {
        var bytes = new byte[width * height * 4];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = bytes[i + 1] = bytes[i + 2] = value;
            bytes[i + 3] = 255;
        }
        return bytes;
    }

    private static string SourceRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "YrkPdfStudio.slnx"))) return folder.FullName;
        throw new DirectoryNotFoundException("Cannot locate source root.");
    }

    private void Measure(Synthetic photo, BitmapSource cleaned)
    {
        Assert.Equal(photo.Image.PixelWidth, cleaned.PixelWidth);
        Assert.Equal(photo.Image.PixelHeight, cleaned.PixelHeight);
        var bytes = Pixels(cleaned);
        var original = Pixels(photo.Image);
        int width = cleaned.PixelWidth, height = cleaned.PixelHeight;
        var ink = Enumerable.Range(0, photo.Ink.Length).Where(p => photo.Ink[p]).ToArray();
        var retained = ink.Count(p => bytes[p * 4 + 3] >= original[p * 4 + 3] * 0.79) * 100.0 / ink.Length;
        var opaque = ink.Count(p => bytes[p * 4 + 3] > 200) * 100.0 / ink.Length;
        var paperRates = new List<double>();
        foreach (var (name, start, end) in new[]
        {
            ("bright", 0, width / 2), ("shadow", width / 2, width),
            ("boundary bright 10px", width / 2 - 10, width / 2),
            ("boundary shadow 10px", width / 2, width / 2 + 10),
        })
        {
            var paper = Enumerable.Range(0, photo.Ink.Length)
                .Where(p => !photo.Ink[p] && original[p * 4 + 3] > 0 && p % width >= start && p % width < end).ToArray();
            var rate = paper.Length == 0 ? 100 : paper.Count(p => bytes[p * 4 + 3] < 32) * 100.0 / paper.Length;
            output.WriteLine($"Paper transparent ({name}): {rate:F3}% ({paper.Length} pixels)");
            paperRates.Add(rate);
        }
        // Longest horizontal run of non-paper alpha within 10px of either side of the shadow edge.
        var bandWidth = 0;
        for (var y = 0; y < height; y++)
        {
            var run = 0;
            for (var x = width / 2 - 10; x < width / 2 + 10; x++)
            {
                var p = y * width + x;
                run = !photo.Ink[p] && bytes[p * 4 + 3] >= 32 ? run + 1 : 0;
                bandWidth = Math.Max(bandWidth, run);
            }
        }
        output.WriteLine($"Ink retained relative to original alpha: {retained:F3}%; opaque ink: {opaque:F3}%; residual shadow band width: {bandWidth}px");
        Assert.All(paperRates, rate => Assert.True(rate >= 99, $"Paper transparency {rate:F3}% < 99%"));
        Assert.True(retained >= 95, $"Ink retention {retained:F3}% < 95%");
        if (ink.All(p => original[p * 4 + 3] == 255)) Assert.True(opaque >= 95, $"Opaque ink {opaque:F3}% < 95%");
        Assert.Equal(0, bandWidth);
    }

    private sealed record Synthetic(BitmapSource Image, bool[] Ink);

    private static Synthetic Photo(string background, bool blue = true, bool noise = false, bool edge = false)
    {
        const int width = 600, height = 360;
        var pixels = new byte[width * height * 4];
        var ink = new bool[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var paper = background switch
            {
                "shadow" => x < width / 2 ? 235 : 160,
                "gradient" => 155 + x * 85 / width + y * 10 / height,
                "fold" => 235 - (int)(22 * Math.Max(0, 1 - Math.Abs(y - 130) / 12.0)),
                _ => 235,
            };
            var wave = 210 + (int)(22 * Math.Sin(x / 25.0));
            var isInk = x >= (edge ? 0 : 125) && x <= 475 && Math.Abs(y - wave) <= 2;
            isInk |= x >= 200 && x <= 203 && y >= 175 && y <= 195;
            // A detached two-pixel upper vowel, too small to survive the size threshold alone.
            isInk |= x == 201 && y is 166 or 167;
            var p = y * width + x;
            ink[p] = isInk;
            var i = p * 4;
            var speck = noise && x % 73 == 9 && y % 61 == 8 && (y < 150 || y > 260);
            pixels[i] = (byte)(isInk ? (blue ? 120 : 25) * paper / 235 : speck ? 25 : paper);
            pixels[i + 1] = (byte)(isInk ? (blue ? 38 : 25) * paper / 235 : speck ? 25 : paper);
            pixels[i + 2] = (byte)(isInk ? (blue ? 20 : 25) * paper / 235 : speck ? 25 : paper);
            pixels[i + 3] = 255;
        }
        return new Synthetic(Bitmap(width, height, pixels), ink);
    }

    private static BitmapSource Bitmap(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
        ImageConversion.ToStraightBgra(image).CopyPixels(bytes, image.PixelWidth * 4, 0);
        return bytes;
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void SaveShadowExample(BitmapSource before, BitmapSource after)
    {
        var directory = Environment.GetEnvironmentVariable("YRK_SIG5B_OUT");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        SavePng(before, Path.Combine(directory, "shadow-before.png"));
        var bytes = Pixels(after);
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var alpha = bytes[i + 3] / 255.0;
            bytes[i] = (byte)Math.Round(bytes[i] * alpha + 190 * (1 - alpha));
            bytes[i + 1] = (byte)Math.Round(bytes[i + 1] * alpha + 240 * (1 - alpha));
            bytes[i + 2] = (byte)Math.Round(bytes[i + 2] * alpha + 200 * (1 - alpha));
            bytes[i + 3] = 255;
        }
        SavePng(Bitmap(after.PixelWidth, after.PixelHeight, bytes), Path.Combine(directory, "shadow-after.png"));
    }
}
