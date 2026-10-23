using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Compression;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>Rotate, delete, move, insert, extract, combine, images to PDF and compression.</summary>
public sealed class PageToolsTests : IDisposable
{
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf"); // 3 A4 pages

    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-pages-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task Rotates_pages_and_saves_rotation()
    {
        var target = PathFor("rotated.pdf");
        await using (var document = await _engine.OpenAsync(SamplePath))
        {
            await document.RotatePagesAsync([0], 1);
            await document.RotatePagesAsync([2], -1);

            Assert.Equal(842, document.PageSizes[0].Width, 0); // landscape now
            Assert.Equal(595, document.PageSizes[1].Width, 0);
            await document.SaveAsync(target);
        }

        await using var reopened = await _engine.OpenAsync(target);
        Assert.Equal(842, reopened.PageSizes[0].Width, 0);
        Assert.Equal(842, reopened.PageSizes[2].Width, 0);
    }

    [Fact]
    public async Task Deleted_pages_come_back_in_place_from_a_copy()
    {
        // What undo does in the app: copy the pages, delete them, then insert copy page i at its old index.
        await using var document = await _engine.OpenAsync(SamplePath);
        int[] deleted = [0, 2];
        var copyPath = PathFor("copy.pdf");
        await document.ExtractPagesAsync(deleted, copyPath);
        await document.DeletePagesAsync(deleted);
        Assert.Equal(1, document.PageCount);

        await using (var copy = await _engine.OpenAsync(copyPath))
        {
            for (var i = 0; i < deleted.Length; i++)
            {
                await document.InsertPagesAsync(copy, deleted[i], [i]);
            }
        }

        Assert.Equal(3, document.PageCount);
        Assert.Contains("Chapter 1", await document.GetPageTextAsync(0));
        Assert.Contains("Chapter 2", await document.GetPageTextAsync(1));
        Assert.Contains("Chapter 3", await document.GetPageTextAsync(2));
    }

    [Fact]
    public async Task Deletes_pages_but_never_the_last_one()
    {
        await using var document = await _engine.OpenAsync(SamplePath);

        await document.DeletePagesAsync([1]);

        Assert.Equal(2, document.PageCount);
        Assert.Contains("Chapter 3", await document.GetPageTextAsync(1));
        await Assert.ThrowsAsync<PdfException>(() => document.DeletePagesAsync([0, 1]));
        Assert.Equal(2, document.PageCount);
    }

    [Fact]
    public async Task Moves_pages()
    {
        await using var document = await _engine.OpenAsync(SamplePath);

        await document.MovePagesAsync([2], 0);

        Assert.Contains("Chapter 3", await document.GetPageTextAsync(0));
        Assert.Contains("Chapter 1", await document.GetPageTextAsync(1));
        Assert.Contains("Chapter 2", await document.GetPageTextAsync(2));
    }

    [Fact]
    public async Task Inserts_pages_from_another_document()
    {
        await using var document = await _engine.OpenAsync(SamplePath);
        await using var form = await _engine.OpenAsync(TestPdfFactory.CreateFormPdf(PathFor("form.pdf")));

        await document.InsertPagesAsync(form, 1);

        Assert.Equal(4, document.PageCount);
        Assert.Contains("Application form", await document.GetPageTextAsync(1));
        Assert.Contains("Chapter 2", await document.GetPageTextAsync(2));
    }

    [Fact]
    public async Task Extracts_pages_in_the_given_order()
    {
        var target = PathFor("extract.pdf");
        await using (var document = await _engine.OpenAsync(SamplePath))
        {
            await document.ExtractPagesAsync([2, 0], target);
            Assert.Equal(3, document.PageCount); // the source is unchanged
        }

        await using var extracted = await _engine.OpenAsync(target);
        Assert.Equal(2, extracted.PageCount);
        Assert.Contains("Chapter 3", await extracted.GetPageTextAsync(0));
        Assert.Contains("Chapter 1", await extracted.GetPageTextAsync(1));
    }

    [Fact]
    public async Task Combines_documents()
    {
        var target = PathFor("combined.pdf");
        await using (var first = await _engine.OpenAsync(SamplePath))
        await using (var second = await _engine.OpenAsync(TestPdfFactory.CreateFormPdf(PathFor("form.pdf"))))
        {
            await _engine.CombineAsync([first, second], target);
        }

        await using var combined = await _engine.OpenAsync(target);
        Assert.Equal(4, combined.PageCount);
        Assert.Contains("Application form", await combined.GetPageTextAsync(3));
    }

    [Fact]
    public async Task Creates_pdf_from_jpeg_and_bitmap_images()
    {
        var target = PathFor("images.pdf");
        var landscape = Gradient(1200, 800);
        var portrait = Gradient(600, 900);

        await _engine.CreateFromImagesAsync(
            [new PdfJpegInput(EncodeJpeg(landscape), 1200, 800), new PdfBitmapInput(ImageConversion.ToPdfImage(portrait))],
            new ImagesToPdfOptions(PdfPageSizeMode.A4),
            target);

        await using var document = await _engine.OpenAsync(target);
        Assert.Equal(2, document.PageCount);
        Assert.Equal(842, document.PageSizes[0].Width, 0); // landscape image → landscape A4
        Assert.Equal(595, document.PageSizes[1].Width, 0);

        // The page centre shows the gradient's middle (mid-grey), not white paper.
        var bitmap = await document.RenderPageAsync(1, 300, 424);
        var centre = ((212 * bitmap.Stride) + (150 * 4));
        Assert.InRange(bitmap.Pixels[centre], 60, 200);
    }

    [Fact]
    public async Task Compresses_large_images()
    {
        // A 3000×4000 lossless image on an A4 page is ~500 dpi; compression brings it to 150 dpi JPEG.
        var source = PathFor("large.pdf");
        var compressed = PathFor("large (compressed).pdf");
        await _engine.CreateFromImagesAsync(
            [new PdfBitmapInput(ImageConversion.ToPdfImage(Gradient(3000, 4000)))],
            new ImagesToPdfOptions(PdfPageSizeMode.A4),
            source);

        var result = await PdfCompressor.CompressAsync(source, null, compressed, new PdfCompressionOptions(150, 75), new WpfImageCodec());

        Assert.Equal(1, result.ImagesRecompressed);
        Assert.True(result.CompressedSize < result.OriginalSize / 2, $"{result.OriginalSize} → {result.CompressedSize}");
        Assert.Equal(new FileInfo(compressed).Length, result.CompressedSize);

        await using var document = await _engine.OpenAsync(compressed);
        var bitmap = await document.RenderPageAsync(0, 300, 424);
        var centre = ((212 * bitmap.Stride) + (150 * 4));
        Assert.InRange(bitmap.Pixels[centre], 60, 200);
    }

    [Fact]
    public async Task Compression_refuses_to_overwrite_the_source()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PdfCompressor.CompressAsync(SamplePath, null, SamplePath, new PdfCompressionOptions(), new WpfImageCodec()));
    }

    [Theory]
    [InlineData("1-3, 5", 10, "0,1,2|4")]
    [InlineData("8-", 10, "7,8,9")]
    [InlineData("-2;4", 10, "0,1|3")]
    [InlineData("3-1", 5, "2,1,0")]
    public void Parses_page_ranges(string text, int pageCount, string expected)
    {
        var ranges = PageRanges.Parse(text, pageCount);

        Assert.Equal(expected, string.Join("|", ranges.Select(r => string.Join(",", r))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("4-12")]
    [InlineData("abc")]
    public void Rejects_invalid_page_ranges(string text)
    {
        Assert.Throws<FormatException>(() => PageRanges.Parse(text, 10));
    }

    [Fact]
    public void Splits_every_n_pages()
    {
        var parts = PageRanges.EveryNPages(7, 3);

        Assert.Equal("0,1,2|3,4,5|6", string.Join("|", parts.Select(r => string.Join(",", r))));
    }

    [Theory]
    [InlineData("สมัครงาน", "ใช้สำหรับสมัครงานเท่านั้น")]
    [InlineData("สมัครงาน ABC", "ใช้สำหรับสมัครงาน ABC เท่านั้น")]
    [InlineData("ใช้สำหรับยื่นธนาคารเท่านั้น", "ใช้สำหรับยื่นธนาคารเท่านั้น")]
    [InlineData("  ", null)]
    public void Formats_certified_copy_purpose(string input, string? expected)
    {
        Assert.Equal(expected, StampRenderer.FormatPurpose(input));
    }

    private static BitmapSource Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                pixels[i] = (byte)(255 * x / width);       // B
                pixels[i + 1] = (byte)(255 * y / height);  // G
                pixels[i + 2] = 128;                       // R
                pixels[i + 3] = 255;                       // A
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodeJpeg(BitmapSource bitmap)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private string PathFor(string name) => Path.Combine(_folder.FullName, name);
}
