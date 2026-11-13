using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit.Abstractions;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanPdfBuilderTests(ITestOutputHelper output) : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("scan-pdf-tests-");
    private readonly ScanPageProcessor _processor = new(new WpfImageCodec());

    public void Dispose() { _engine.Dispose(); _folder.Delete(true); }

    [Fact]
    public async Task Black_and_white_A4_300dpi_is_small_and_renders_with_correct_polarity()
    {
        // Actual Thai/English text from a rendered PDF, rather than a blank page or repeated bars.
        await using var sample = await _engine.OpenAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf"));
        var bitmap = await sample.RenderPageAsync(0, 2480, 3508);
        var processed = _processor.Process(new(bitmap, 300, 300), new(ColorMode: ScanColorMode.BlackAndWhite, AutoCrop: false, AutoStraighten: false));
        var path = Path.Combine(_folder.FullName, "bw.pdf");
        await new ScanPdfBuilder(_engine, _processor).BuildAsync([processed], path);
        var kb = new FileInfo(path).Length / 1024.0;
        using var structural = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var image = Images(structural).Single();
        var filter = image.Elements["/Filter"]!.ToString();
        output.WriteLine($"B&W A4 300 dpi: {kb:F2} KB; filter: {filter}; DecodeParms: {image.Elements["/DecodeParms"]}");
        Assert.True(kb <= 100, $"A4 scan is {kb:F2} KB");
        Assert.Equal(1, image.Elements.GetInteger("/BitsPerComponent"));
        if (filter!.Contains("CCITTFaxDecode", StringComparison.Ordinal))
            Assert.Contains("/K -1", image.Elements["/DecodeParms"]!.ToString(), StringComparison.Ordinal);
        else Assert.Contains("FlateDecode", filter, StringComparison.Ordinal);
        await using var result = await _engine.OpenAsync(path);
        Assert.Equal(1, result.PageCount);
        Assert.Equal(2480 * 72.0 / 300, result.PageSizes[0].Width, 2);
        Assert.Equal(3508 * 72.0 / 300, result.PageSizes[0].Height, 2);
        var rendered = await result.RenderPageAsync(0, 600, 849);
        var reference = await sample.RenderPageAsync(0, 600, 849);
        var wrong = 0;
        for (var i = 0; i < rendered.Pixels.Length; i += 4)
            if ((rendered.Pixels[i] < 128) != (reference.Pixels[i] < 128)) wrong++;
        Assert.True(wrong / (600.0 * 849) < 0.035, "Bilevel image changed polarity, position or content.");
        Assert.True(rendered.Pixels[0] > 240);
        Assert.Contains(rendered.Pixels.Where((_, i) => i % 4 == 0), value => value < 20);
    }

    [Fact]
    public async Task Mixed_JPEG_gray_and_bilevel_pages_open_in_order_at_their_individual_DPI()
    {
        var pages = new List<ProcessedScanPage>();
        foreach (var mode in Enum.GetValues<ScanColorMode>())
        {
            var raw = ColoredPage(120, 160) with { DpiX = 150, DpiY = 300 };
            pages.Add(_processor.Process(raw, new(ColorMode: mode, AutoCrop: false, AutoStraighten: false)));
        }
        var path = Path.Combine(_folder.FullName, "mixed.pdf");
        await new ScanPdfBuilder(_engine, _processor).BuildAsync(pages, path);
        using var structural = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Contains("YRK PDF", structural.Info.Producer, StringComparison.Ordinal);
        Assert.Equal(2, Images(structural).Count(i => i.Elements["/Filter"]!.ToString()!.Contains("DCTDecode", StringComparison.Ordinal)));
        Assert.Single(Images(structural), i => i.Elements.GetInteger("/BitsPerComponent") == 1);
        await using var result = await _engine.OpenAsync(path);
        Assert.Equal(3, result.PageCount);
        for (var p = 0; p < 3; p++)
        {
            Assert.Equal(57.6, result.PageSizes[p].Width, 2);
            Assert.Equal(38.4, result.PageSizes[p].Height, 2);
            var bitmap = await result.RenderPageAsync(p, 120, 160);
            var center = (80 * bitmap.Stride) + 60 * 4;
            Assert.True(bitmap.Pixels[center] < 160); // Content fills the new MediaBox after Y scaling.
            Assert.True(bitmap.Pixels[0] > 240);
            if (p == 0) Assert.True(bitmap.Pixels[center + 2] > bitmap.Pixels[center] + 60);
            else Assert.InRange(Math.Abs(bitmap.Pixels[center + 2] - bitmap.Pixels[center]), 0, 2);
        }
    }

    [Theory]
    [InlineData(90, 4, 2, 12)]
    [InlineData(180, 2, 4, 15)]
    [InlineData(270, 4, 2, 3)]
    public void Rotation_precedes_crop_and_swaps_resolution_on_quarter_turns(int rotation, int width, int height, int firstValue)
    {
        var pixels = new byte[4 * 4 * 4];
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
            {
                var i = (y * 4 + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(y * 4 + x);
                pixels[i + 3] = 255;
            }
        var codec = new CapturingCodec();
        var processor = new ScanPageProcessor(codec);
        var crop = rotation == 180 ? new NormalizedRect(0, 0, 0.5, 1) : new NormalizedRect(0, 0, 1, 0.5);
        var result = processor.Process(new(new(4, 4, 16, pixels), 150, 300), new(AutoCrop: false, AutoStraighten: false), rotation, crop);
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Equal((byte)firstValue, codec.Pixels!.Pixels[0]);
        Assert.Equal(rotation == 180 ? 150 : 300, result.DpiX);
        Assert.Equal(rotation == 180 ? 300 : 150, result.DpiY);
    }

    [Fact]
    public async Task Fake_pages_are_deskewed_cropped_and_blank_page_is_identified()
    {
        using var session = new ScanSession();
        await session.ScanAsync(new FakeScannerService(), FakeScannerService.Device, new(Dpi: 100));
        var raw = session.Read(session.Pages[0]);
        Assert.InRange(Math.Abs(ScanCleanup.Check(raw.Bitmap).Straighten), 1, 2);
        var codec = new CapturingCodec();
        var result = new ScanPageProcessor(codec).Process(raw, new());
        Assert.False(result.IsBlank);
        var straight = codec.Pixels!;
        Assert.InRange(Math.Abs(ScanCleanup.Check(new(straight.Width, straight.Height, straight.Stride, straight.Pixels)).Straighten), 0, 0.25);
        Assert.True(result.Width < raw.Bitmap.Width || result.Height < raw.Bitmap.Height);
        Assert.True(new ScanPageProcessor(codec).Process(session.Read(session.Pages[2]), new()).IsBlank);
    }

    [Fact]
    public async Task Building_from_session_applies_order_rotation_crop_and_updates_blank_status()
    {
        using var session = new ScanSession();
        var first = session.Add(ScanSessionTests.Page(120, 160));
        first.Rotation = 90;
        first.Crop = new(0, 0, 0.5, 1);
        session.Add(ScanSessionTests.Page(100, 200));
        session.Move(0, 1);
        var path = Path.Combine(_folder.FullName, "session.pdf");
        await new ScanPdfBuilder(_engine, _processor).BuildAsync(session, new(ColorMode: ScanColorMode.BlackAndWhite), path);
        await using var document = await _engine.OpenAsync(path);
        Assert.Equal(2, document.PageCount);
        Assert.Equal(100 * 72.0 / 300, document.PageSizes[0].Width, 2);
        Assert.Equal(80 * 72.0 / 300, document.PageSizes[1].Width, 2);
        Assert.All(session.Pages, page => Assert.True(page.IsBlank));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Builder_failure_or_cancellation_deletes_session_without_publishing_a_PDF(bool cancel)
    {
        using var session = new ScanSession();
        var page = session.Add(ScanSessionTests.Page(120, 160));
        page.Crop = new(-1, 0, 1, 1);
        var path = Path.Combine(_folder.FullName, "failure.pdf");
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => new ScanPdfBuilder(_engine, _processor).BuildAsync(session, new(), path, cancellation.Token));
        Assert.False(Directory.Exists(session.DirectoryPath));
        Assert.False(File.Exists(path));
    }

    private static IEnumerable<PdfDictionary> Images(PdfDocument document) => document.Internals.GetAllObjects()
        .OfType<PdfDictionary>().Where(d => d.Elements.GetName("/Subtype") == "/Image");

    private static ScannedPage ColoredPage(int w, int h)
    {
        var page = ScanSessionTests.Page(w, h);
        for (var y = h / 4; y < h * 3 / 4; y++)
            for (var x = w / 4; x < w * 3 / 4; x++)
            {
                var i = y * page.Bitmap.Stride + x * 4;
                page.Bitmap.Pixels[i] = 15;
                page.Bitmap.Pixels[i + 1] = 20;
                page.Bitmap.Pixels[i + 2] = 180;
            }
        return page;
    }

    private sealed class CapturingCodec : IPdfImageCodec
    {
        public PdfRawPixels? Pixels { get; private set; }
        public PdfRawPixels? DecodeJpeg(byte[] data) => throw new NotSupportedException();
        public byte[] EncodeJpeg(PdfRawPixels pixels, int targetWidth, int targetHeight, int quality)
        { Pixels = pixels; return Encoding.ASCII.GetBytes("test"); }
    }
}
