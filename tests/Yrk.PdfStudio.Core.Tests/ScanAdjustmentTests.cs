using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Ocr;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanAdjustmentTests
{
    [Fact]
    public async Task Orientation_download_failure_can_retry_with_session_consent_without_prompting_again()
    {
        using var session = new ScanSession();
        using var handler = new ModelHandler { FailFirst = true };
        using var client = new HttpClient(handler);
        var data = new OcrLanguageData(Path.Combine(session.DirectoryPath, "osd"), client,
            new Dictionary<string, (string, long)>
            { ["osd"] = (Convert.ToHexStringLower(SHA256.HashData(handler.Payload)), handler.Payload.Length) });
        var model = new Yrk.PdfStudio.App.ViewModels.ScanViewModel(session, new FakeScannerService(),
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var prompts = 0;
        Task<bool> Consent() { prompts++; return Task.FromResult(true); }
        await Assert.ThrowsAsync<HttpRequestException>(() => model.SetAutoOrientationAsync(true, data, Consent));
        Assert.False(model.AutoOrientationEnabled);
        Assert.False(data.IsInstalled("osd"));
        await model.SetAutoOrientationAsync(false, data, Consent);
        await model.SetAutoOrientationAsync(true, data, Consent);
        Assert.True(model.AutoOrientationEnabled);
        Assert.True(data.IsInstalled("osd"));
        Assert.Equal(1, prompts);
        Assert.Equal(2, handler.Calls);
    }

    private static readonly ScanSettings Plain = new(AutoStraighten: false, AutoCrop: false);

    [Theory]
    [InlineData(25, 200, 80)]
    [InlineData(-25, 200, 80)]
    [InlineData(0, 200, 80)]
    public void Contrast_adjusts_around_middle_gray_in_preview_and_output(int contrast, byte light, byte dark)
    {
        var page = GrayText(light, dark) with { Adjustments = new(Contrast: contrast) };
        var codec = new CaptureCodec();
        var processor = new ScanPageProcessor(codec);
        var preview = processor.RenderPreview(page, Plain, 0, 100);
        var bright = preview.Pixels[0];
        var dim = preview.Pixels[40 * preview.Stride + 40 * 4];
        if (contrast > 0) { Assert.True(bright > light); Assert.True(dim < dark); }
        else if (contrast < 0) { Assert.True(bright < light); Assert.True(dim > dark); }
        else { Assert.Equal(light, bright); Assert.Equal(dark, dim); }
        processor.Process(page, Plain);
        Assert.Equal(bright, codec.Last!.Pixels[0]);
        Assert.Equal(dim, codec.Last.Pixels[40 * 400 + 40 * 4]);
        Assert.Equal(light, page.Bitmap.Pixels[0]);
    }

    [Fact]
    public void Brightness_precedes_contrast_and_middle_gray_stays_fixed()
    {
        var processor = new ScanPageProcessor(new CaptureCodec());
        var page = GrayText(108, 108) with { Adjustments = new(Brightness: 10, Contrast: 50) };
        Assert.Equal(128, processor.RenderPreview(page, Plain, 0, 100).Pixels[0]);
    }

    [Fact]
    public void Contrast_is_stored_in_session_and_validated()
    {
        using var session = new ScanSession();
        var page = session.Add(GrayText(200, 80));
        page.Contrast = 25;
        Assert.Equal(25, session.Read(page).Adjustments!.Contrast);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Contrast = 51);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Contrast = -51);
    }

    [Theory]
    [InlineData(211, 128)]
    [InlineData(211, 185)]
    [InlineData(225, 200)]
    public void Whitening_makes_gray_paper_white_and_faint_text_darker_in_preview_and_output(byte paper, byte ink)
    {
        var page = GrayText(paper, ink) with { Adjustments = new(WhiteBackground: true) };
        var codec = new CaptureCodec();
        var processor = new ScanPageProcessor(codec);
        var preview = processor.RenderPreview(page, Plain, 0, 100);
        Assert.InRange(preview.Pixels[0], 245, 255);
        Assert.InRange(preview.Pixels[40 * preview.Stride + 40 * 4], 0, 100);
        processor.Process(page, Plain);
        Assert.InRange(codec.Last!.Pixels[0], 245, 255);
        Assert.InRange(codec.Last.Pixels[40 * 400 + 40 * 4], 0, 100);
        Assert.Equal(paper, page.Bitmap.Pixels[0]); // Source is preserved for undo.
    }

    [Fact]
    public async Task Fake_faint_gray_A4_at_300dpi_whitens_and_encodes_below_one_MB_at_JPEG80()
    {
        using var session = new ScanSession();
        await session.ScanAsync(new FakeScannerService { PageCount = 1, PageStyle = FakeScanPageStyle.GrayFaintText },
            FakeScannerService.Device, Plain);
        var item = Assert.Single(session.Pages);
        item.WhiteBackground = true;
        item.Sharpness = 20;
        var source = session.Read(item);
        Assert.Equal(2480, source.Bitmap.Width);
        Assert.Equal(3508, source.Bitmap.Height);
        var codec = new WpfImageCodec();
        var processor = new ScanPageProcessor(codec);
        var result = processor.Process(source, Plain with { ColorMode = ScanColorMode.Grayscale });
        Assert.InRange(result.Data.Length, 1, 1_000_000);
        var decoded = Assert.IsType<PdfRawPixels>(codec.DecodeJpeg(result.Data));
        Assert.InRange(decoded.Pixels[0], 245, 255);
        var preview = processor.RenderPreview(source, Plain, 0, 900);
        Assert.Equal(900, Math.Max(preview.Width, preview.Height));
        Assert.InRange(preview.Pixels[0], 245, 255);
        Assert.Contains(preview.Pixels.Where((_, i) => i % 4 == 0), value => value < 100);
    }

    [Fact]
    public async Task Card_on_white_flatbed_is_cropped_to_physical_card_before_whitening()
    {
        using var session = new ScanSession();
        await session.ScanAsync(new FakeScannerService { PageCount = 1, PageStyle = FakeScanPageStyle.CardOnWhiteGlass },
            FakeScannerService.Device, Plain with { Dpi = 100 });
        var item = Assert.Single(session.Pages);
        item.WhiteBackground = true;
        var processor = new ScanPageProcessor(new WpfImageCodec());
        var settings = Plain with { AutoCrop = true };
        var crop = Assert.IsType<NormalizedRect>(processor.Analyze(session.Read(item), settings).Keep);
        Assert.InRange(crop.X, 0.24, 0.26);
        Assert.InRange(crop.Y, 0.24, 0.26);
        var processed = processor.Process(session.Read(item), settings);
        Assert.InRange(processed.Width / 100.0 * 25.4, 84, 88);
        Assert.InRange(processed.Height / 100.0 * 25.4, 52, 57);
        var preview = processor.RenderPreview(session.Read(item), settings, 0, 1400, applyCrop: true);
        Assert.Equal(processed.Width, preview.Width);
        Assert.Equal(processed.Height, preview.Height);
        var captured = new CaptureCodec();
        new ScanPageProcessor(captured).Process(session.Read(item), settings with { ColorMode = ScanColorMode.Grayscale });
        // Estimate the paper tone inside the crop, rather than the much larger surrounding white glass.
        var sample = captured.Last!.Pixels[20 * processed.Width + processed.Width - 20];
        Assert.InRange(sample, 245, 255);
    }

    [Fact]
    public void Paper_on_white_glass_uses_surface_difference_and_does_not_crop_text_alone()
    {
        var page = ScanSessionTests.Page(400, 500);
        for (var y = 50; y < 450; y++)
            for (var x = 40; x < 360; x++)
                page.Bitmap.Pixels.AsSpan(y * page.Bitmap.Stride + x * 4, 3).Fill(239);
        var check = ScanCleanup.Check(page.Bitmap);
        var keep = Assert.IsType<NormalizedRect>(check.Keep);
        Assert.InRange(keep.X, 0.09, 0.11);
        Assert.InRange(keep.Width, 0.79, 0.82);
        var text = ScanSessionTests.Page(400, 500);
        for (var y = 100; y < 400; y += 20)
            for (var x = 80; x < 320; x++)
                text.Bitmap.Pixels.AsSpan(y * text.Bitmap.Stride + x * 4, 3).Fill(50);
        Assert.Null(ScanCleanup.Check(text.Bitmap).Keep);
        Assert.Null(ScanCleanup.Check(ScanSessionTests.Page(400, 500).Bitmap).Keep);
    }

    [Theory]
    [InlineData(-50, -50)]
    [InlineData(50, 50)]
    [InlineData(15, 30)]
    public void Nondefault_brightness_and_sharpness_change_preview_and_processing(int brightness, int sharpness)
    {
        var original = GrayText(210, 180);
        var page = original with { Adjustments = new(brightness, sharpness, Straighten: false) };
        var codec = new CaptureCodec();
        var processor = new ScanPageProcessor(codec);
        var preview = processor.RenderPreview(page, Plain, 0, 100);
        processor.Process(page, Plain);
        Assert.Equal(preview.Pixels, codec.Last!.Pixels);
        Assert.NotEqual(original.Bitmap.Pixels, preview.Pixels);
    }

    [Fact]
    public void Preview_never_encodes_full_image_and_sharpness_alone_changes_edges()
    {
        var page = GrayText(210, 180) with { Adjustments = new(Sharpness: 50) };
        var codec = new CaptureCodec();
        var processor = new ScanPageProcessor(codec);
        var image = processor.RenderPreview(page, Plain, 0, 100);
        Assert.Null(codec.Last);
        Assert.True(image.Pixels[40 * image.Stride + 30 * 4] < 180);
        var small = processor.RenderPreview(page, Plain, 90, 25);
        Assert.Equal(25, Math.Max(small.Width, small.Height));
        Assert.Null(codec.Last);
    }

    [Fact]
    public void Grayscale_and_bilevel_previews_show_the_output_color_mode_without_jpeg_encoding()
    {
        var codec = new CaptureCodec();
        var processor = new ScanPageProcessor(codec);
        var page = GrayText(211, 185) with { Adjustments = new(WhiteBackground: true, Sharpness: 20) };
        var gray = processor.RenderPreview(page, Plain with { ColorMode = ScanColorMode.Grayscale }, 0, 100);
        processor.Process(page, Plain with { ColorMode = ScanColorMode.Grayscale });
        Assert.Equal(gray.Pixels.Where((_, i) => i % 4 == 0), codec.Last!.Pixels);
        var bilevel = processor.RenderPreview(page, Plain with { ColorMode = ScanColorMode.BlackAndWhite }, 0, 100);
        Assert.All(bilevel.Pixels.Where((_, i) => i % 4 == 0), value => Assert.True(value is 0 or 255));
        Assert.Equal(0, bilevel.Pixels[40 * bilevel.Stride + 40 * 4]);
        Assert.Equal(255, bilevel.Pixels[0]);
    }

    [Fact]
    public void Session_retains_independent_edits_and_rejects_values_outside_ranges()
    {
        using var session = new ScanSession();
        var first = session.Add(GrayText(211, 128));
        var second = session.Add(GrayText(211, 128));
        first.Brightness = -50; first.Sharpness = 50; first.WhiteBackground = true; first.Straighten = false;
        Assert.Equal(first.Adjustments, session.Read(first).Adjustments);
        Assert.Equal(new ScanPageAdjustments(), second.Adjustments);
        Assert.Throws<ArgumentOutOfRangeException>(() => first.Brightness = 51);
        Assert.Throws<ArgumentOutOfRangeException>(() => first.Sharpness = -51);
        Assert.Equal(80, new ScanSettings().JpegQuality);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanSettings(JpegQuality: 49).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanSettings(JpegQuality: 96).Validate());
    }

    [Fact]
    public async Task Straighten_is_a_per_page_override_in_both_preview_and_real_processing()
    {
        using var session = new ScanSession();
        await session.ScanAsync(new FakeScannerService { PageCount = 1, PageStyle = FakeScanPageStyle.GrayFaintText },
            FakeScannerService.Device, Plain with { Dpi = 100 });
        var item = Assert.Single(session.Pages);
        item.WhiteBackground = true;
        item.Straighten = false;
        var codec = new CaptureCodec();
        var processor = new ScanPageProcessor(codec);
        var tilted = processor.RenderPreview(session.Read(item), Plain, 0, 1400);
        Assert.InRange(Math.Abs(ScanCleanup.Check(tilted).Straighten), 1, 2);
        item.Straighten = true;
        var straight = processor.RenderPreview(session.Read(item), Plain, 0, 1400);
        Assert.InRange(Math.Abs(ScanCleanup.Check(straight).Straighten), 0, 0.25);
        processor.Process(session.Read(item), Plain);
        Assert.Equal(straight.Pixels, codec.Last!.Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OSD_only_calls_detector_when_optional_file_is_present(bool installed)
    {
        var folder = Directory.CreateTempSubdirectory("scan-osd-");
        try
        {
            if (installed) File.WriteAllBytes(Path.Combine(folder.FullName, "osd.traineddata"), [1]);
            var detector = new OrientationDetector();
            var processor = new ScanPageProcessor(new WpfImageCodec(), detector, folder.FullName);
            Assert.Equal(installed ? 90 : 0, processor.DetectOrientation(GrayText(211, 128)));
            Assert.Equal(installed ? 1 : 0, detector.Calls);
            Assert.Equal(installed ? 1 : 0, folder.GetFiles().Length);
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public async Task OSD_is_pinned_hidden_and_download_requires_consent_and_verification()
    {
        Assert.DoesNotContain(OcrLanguageData.Languages, l => l.Code == "osd");
        var pinned = OcrLanguageData.AllVerificationData["osd"];
        Assert.Equal("9cf5d576fcc47564f11265841e5ca839001e7e6f38ff7f7aacf46d15a96b00ff", pinned.Sha256);
        Assert.Equal(10562727, pinned.Size);
        var folder = Directory.CreateTempSubdirectory("scan-osd-download-");
        try
        {
            using var handler = new ModelHandler();
            using var client = new HttpClient(handler);
            var table = new Dictionary<string, (string Sha256, long Size)>
            { ["osd"] = (Convert.ToHexStringLower(SHA256.HashData(handler.Payload)), handler.Payload.Length) };
            var data = new OcrLanguageData(folder.FullName, client, table);
            Assert.False(await data.EnsureOrientationDataAsync(() => Task.FromResult(false)));
            Assert.Equal(0, handler.Calls);
            Assert.True(await data.EnsureOrientationDataAsync(() => Task.FromResult(true)));
            Assert.Equal(1, handler.Calls);
            Assert.Equal("https://github.com/tesseract-ocr/tessdata_fast/raw/87416418657359cb625c412a48b6e1d6d41c29bd/osd.traineddata", handler.Uri);
            Assert.Equal(handler.Payload, File.ReadAllBytes(Path.Combine(folder.FullName, "osd.traineddata")));
            Assert.True(await data.EnsureOrientationDataAsync(() => throw new InvalidOperationException("Already installed")));
            File.Delete(Path.Combine(folder.FullName, "osd.traineddata"));
            var bad = new OcrLanguageData(folder.FullName, client, new Dictionary<string, (string, long)>
            { ["osd"] = (new string('0', 64), handler.Payload.Length) });
            await Assert.ThrowsAsync<InvalidDataException>(() => bad.EnsureOrientationDataAsync(() => Task.FromResult(true)));
            Assert.Empty(folder.GetFiles());
        }
        finally { folder.Delete(true); }
    }

    private static ScannedPage GrayText(byte paper, byte ink)
    {
        var page = ScanSessionTests.Page(100, 100);
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
                page.Bitmap.Pixels.AsSpan(y * page.Bitmap.Stride + x * 4, 3).Fill(x is >= 30 and < 70 && y is >= 30 and < 70 ? ink : paper);
        return page;
    }

    private sealed class CaptureCodec : IPdfImageCodec
    {
        public PdfRawPixels? Last { get; private set; }
        public PdfRawPixels? DecodeJpeg(byte[] data) => throw new NotSupportedException();
        public byte[] EncodeJpeg(PdfRawPixels pixels, int targetWidth, int targetHeight, int quality)
        { Last = pixels; return [1]; }
    }

    private sealed class OrientationDetector : IScanOrientationDetector
    {
        public int Calls { get; private set; }
        public int? Detect(PdfBitmap bitmap, string tessdataFolder, CancellationToken cancellationToken)
        { Calls++; return 90; }
    }

    private sealed class ModelHandler : HttpMessageHandler
    {
        public bool FailFirst { get; init; }
        public byte[] Payload { get; } = [1, 3, 7];
        public int Calls { get; private set; }
        public string? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Uri = request.RequestUri!.AbsoluteUri;
            if (FailFirst && Calls == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        }
    }
}

