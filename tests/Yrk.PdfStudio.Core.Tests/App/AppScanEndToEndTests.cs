using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppScanEndToEndTests
{
    [Fact]
    public void Temporary_and_composed_pages_capture_their_acquisition_mode() => Run(async () =>
    {
        using var session = new ScanSession();
        var model = new ScanViewModel(session, new MarkerScanner { Count = 1 }, Dispatcher.CurrentDispatcher)
        { Settings = new(Dpi: 100, ColorMode: ScanColorMode.BlackAndWhite, AutoStraighten: false, AutoCrop: false) };
        await model.PreviewGlassAsync(FakeScannerService.Device);
        Assert.Equal(ScanColorMode.BlackAndWhite, model.GlassPreview!.Page.ColorMode);
        model.Settings = model.Settings with { ColorMode = ScanColorMode.Color };
        Assert.Equal(ScanColorMode.BlackAndWhite, session.Read(model.GlassPreview.Page).ColorMode);
        await model.ScanCardAsync(FakeScannerService.Device);
        Assert.Null(model.GlassPreview);
        Assert.Equal(ScanColorMode.Color, model.CardFront!.Page.ColorMode);
        model.Settings = model.Settings with { ColorMode = ScanColorMode.Grayscale };
        await model.ScanCardAsync(FakeScannerService.Device);
        Assert.Equal(ScanColorMode.Grayscale, model.CardBack!.Page.ColorMode);
        model.Settings = model.Settings with { ColorMode = ScanColorMode.BlackAndWhite };
        await model.ScanCardAsync(FakeScannerService.Device);
        Assert.Equal(ScanColorMode.BlackAndWhite, Assert.Single(session.Pages).ColorMode);
        Assert.Equal(ScanColorMode.BlackAndWhite, session.Read(session.Pages[0]).ColorMode);
    });

    [Theory]
    [InlineData(ScanAdjustmentScope.ThisPage, false)]
    [InlineData(ScanAdjustmentScope.Selected, false)]
    [InlineData(ScanAdjustmentScope.AllPages, false)]
    [InlineData(ScanAdjustmentScope.ThisPage, true)]
    [InlineData(ScanAdjustmentScope.Selected, true)]
    [InlineData(ScanAdjustmentScope.AllPages, true)]
    public void Upside_down_scan_retains_edits_in_pdf_and_image_exports(ScanAdjustmentScope scope, bool straighten) => Run(async () =>
    {
        using var session = new ScanSession();
        using var engine = new PdfiumEngine();
        var scanner = new MarkerScanner();
        var model = new ScanViewModel(session, scanner, Dispatcher.CurrentDispatcher)
        { Settings = new(Dpi: 100, AutoStraighten: straighten, AutoCrop: true, JpegQuality: 95) };
        await model.ScanAsync(FakeScannerService.Device, () => { });
        Assert.InRange(Math.Abs(ScanCleanup.Check(session.Read(session.Pages[0]).Bitmap).Straighten), 1, 2);
        var first = model.Pages[0];
        model.Select(0);
        if (scope == ScanAdjustmentScope.Selected) model.Select(1, control: true);
        await model.RotateSelectedAsync(90);
        await model.UndoAsync();
        Assert.Equal(0, first.Page.Rotation);
        await model.RedoAsync();
        Assert.Equal(90, first.Page.Rotation);
        await model.RotateSelectedAsync(90);
        var crop = new NormalizedRect(.1, .1, .7, .7);
        await model.CropSelectedAsync(crop);
        await model.UndoAsync();
        Assert.Null(first.Page.Crop);
        await model.RedoAsync();
        Assert.Equal(crop, first.Page.Crop);
        Assert.Equal(180, first.Page.Rotation);
        model.SetAdjustmentScope(scope);
        var edits = new ScanPageAdjustments(Brightness: 12, Contrast: 20, WhiteBackground: true, Straighten: straighten);
        model.Adjust(_ => edits);
        await model.UndoAsync();
        Assert.Equal(0, first.Page.Brightness);
        await model.RedoAsync();
        for (var i = 0; i < 3; i++)
            Assert.Equal(scope == ScanAdjustmentScope.AllPages || i == 0 || scope == ScanAdjustmentScope.Selected && i == 1,
                model.Pages[i].Page.WhiteBackground);
        var preview = await model.PreviewAsync(first);
        var previewPixels = new byte[preview.PixelWidth * preview.PixelHeight * 4];
        preview.CopyPixels(previewPixels, preview.PixelWidth * 4, 0);
        AssertEdited(new(preview.PixelWidth, preview.PixelHeight, preview.PixelWidth * 4, previewPixels));
        // Reorder a different page ahead of the edited one, then move the edited page back to page 1.
        model.Move(2, 0);
        model.Move(model.Pages.IndexOf(first), 0);
        await model.UndoAsync();
        await model.RedoAsync();
        model.CommitPages();
        Assert.Same(first.Page, session.Pages[0]);
        Assert.Equal(model.Pages.Select(p => p.Page), session.Pages);

        var path = Path.Combine(session.DirectoryPath, "edited.pdf");
        await new ScanPdfBuilder(engine, new(new WpfImageCodec())).BuildAsync(session, model.Settings, path);
        await using var pdf = await engine.OpenAsync(path);
        Assert.Equal(280 * .72, pdf.PageSizes[0].Width, 2);
        Assert.Equal(350 * .72, pdf.PageSizes[0].Height, 2);
        var rendered = await pdf.RenderPageAsync(0, 280, 350);
        AssertEdited(rendered);
        foreach (var format in new[] { ScanOutputFormat.Jpeg, ScanOutputFormat.Png })
        {
            var bytes = MainViewModel.RenderScanImage(session, first.Page, model.Settings, format);
            var bitmap = Decode(bytes);
            AssertEdited(bitmap);
            // A render with identical geometry but no tone edits proves the saved adjustments changed pixels.
            first.Page.Adjustments = new(Straighten: straighten);
            var original = Decode(MainViewModel.RenderScanImage(session, first.Page, model.Settings, format));
            first.Page.Adjustments = edits;
            Assert.True(bitmap.Pixels[300 * bitmap.Stride + 240 * 4] > original.Pixels[300 * original.Stride + 240 * 4] + 30);
        }
        // Manual crop wins over analysis.Keep; automatic crop still runs when no crop is supplied.
        var processor = new ScanPageProcessor(new WpfImageCodec());
        var raw = session.Read(first.Page);
        var manual = processor.Process(raw, model.Settings, 180, crop);
        var automatic = processor.Process(raw, model.Settings, 180);
        Assert.Equal((280, 350), (manual.Width, manual.Height));
        Assert.True(automatic.Width > manual.Width && automatic.Height > manual.Height);
        if (straighten)
        {
            var deskewed = processor.RenderPreview(raw, model.Settings, 180, int.MaxValue, crop, true);
            var uncorrected = processor.RenderPreview(raw with { Adjustments = edits with { Straighten = false } },
                model.Settings, 180, int.MaxValue, crop, true);
            Assert.False(deskewed.Pixels.SequenceEqual(uncorrected.Pixels), "Non-zero skew must be corrected after the 180-degree turn.");
        }
    });

    [Theory]
    [InlineData(ScanColorMode.BlackAndWhite)]
    [InlineData(ScanColorMode.Grayscale)]
    public void Mixed_scan_modes_survive_preset_change_save_quality_and_exports(ScanColorMode mode) => Run(async () =>
    {
        using var session = new ScanSession();
        using var engine = new PdfiumEngine();
        var scanner = new MarkerScanner { Count = 2, RealText = true };
        var model = new ScanViewModel(session, scanner, Dispatcher.CurrentDispatcher)
        { Settings = new(Dpi: 100, AutoStraighten: false, AutoCrop: false) };
        model.ApplyPreset(mode == ScanColorMode.BlackAndWhite ? ScanPreset.BlackAndWhiteDocument : ScanPreset.Receipt);
        await model.ScanAsync(FakeScannerService.Device, () => { });
        var sizes = model.Pages.Select(p => p.EncodedBytes).ToArray();
        model.ApplyPreset(ScanPreset.ColorDocument);
        await model.RefreshAllAsync();
        Assert.Equal(sizes, model.Pages.Select(p => p.EncodedBytes));
        scanner.Count = 1;
        await model.ScanAsync(FakeScannerService.Device, () => { });
        model.Settings = model.Settings with { JpegQuality = 55 };
        await model.RefreshAllAsync();
        model.CommitPages();
        Assert.Equal(new[] { mode, mode, ScanColorMode.Color }, session.Pages.Select(p => p.ColorMode!.Value));
        Assert.Equal(new[] { 300.0, 300, 200 }, session.Pages.Select(p => p.DpiX));
        var processor = new ScanPageProcessor(new WpfImageCodec());
        for (var i = 0; i < 3; i++)
        {
            var page = session.Pages[i];
            var processed = processor.Process(session.Read(page), model.Settings);
            Assert.Equal(i < 2 ? mode : ScanColorMode.Color, processed.ColorMode);
            if (processed.ColorMode == ScanColorMode.BlackAndWhite)
            {
                using var estimate = new PdfDocument();
                estimate.Options.EnableCcittCompressionForBilevelImages = true;
                var target = estimate.AddPage();
                using var input = new MemoryStream(processed.Data);
                using var image = PdfSharp.Drawing.XImage.FromStream(input);
                using (var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(target)) graphics.DrawImage(image, 0, 0);
                using var output = new MemoryStream();
                estimate.Save(output, false);
                Assert.Equal(output.Length, model.Pages[i].EncodedBytes);
            }
            else
            {
                Assert.Equal(processed.Data.LongLength, model.Pages[i].EncodedBytes);
                // Scan-time quality was 80; encoding follows the save-time value of 55.
                Assert.NotEqual(processor.Process(session.Read(page), model.Settings with { JpegQuality = 95 }).Data.Length, processed.Data.Length);
            }
            foreach (var format in new[] { ScanOutputFormat.Png, ScanOutputFormat.Jpeg })
            {
                var bitmap = Decode(MainViewModel.RenderScanImage(session, page, model.Settings, format));
                var pixel = 400 * bitmap.Stride + 320 * 4; // Red marker on the upside-down input.
                if (i == 2) Assert.True(bitmap.Pixels[pixel + 2] > bitmap.Pixels[pixel] + 80);
                else Assert.InRange(Math.Abs(bitmap.Pixels[pixel + 2] - bitmap.Pixels[pixel]), 0, 2);
                if (mode == ScanColorMode.BlackAndWhite && i < 2 && format == ScanOutputFormat.Png)
                    Assert.All(bitmap.Pixels.Where((_, index) => index % 4 != 3), value => Assert.True(value is 0 or 255));
            }
        }
        Assert.Equal(model.Pages.Sum(p => p.EncodedBytes), model.EstimatedBytes);
        var path = Path.Combine(session.DirectoryPath, "mixed.pdf");
        await new ScanPdfBuilder(engine, processor).BuildAsync(session, model.Settings, path);
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var images = pdf.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(d => d.Elements.GetName("/Subtype") == "/Image").ToArray();
        Assert.Equal(3, images.Length);
        Assert.Single(images, image => image.Elements.GetName("/ColorSpace") == "/DeviceRGB"
            && image.Elements["/Filter"]!.ToString()!.Contains("DCTDecode"));
        if (mode == ScanColorMode.BlackAndWhite)
            Assert.Equal(2, images.Count(image => image.Elements.GetInteger("/BitsPerComponent") == 1
                && image.Elements["/Filter"]!.ToString()!.Contains("CCITTFaxDecode")));
        else
            Assert.Equal(2, images.Count(image => image.Elements.GetInteger("/BitsPerComponent") == 8
                && image.Elements.GetName("/ColorSpace") == "/DeviceGray"
                && image.Elements["/Filter"]!.ToString()!.Contains("DCTDecode")));
    });

    private static void AssertEdited(PdfBitmap bitmap)
    {
        Assert.Equal((280, 350), (bitmap.Width, bitmap.Height));
        // The upright original's top-left marker returns to the cropped top-left after two turns.
        var marker = 50 * bitmap.Stride + 40 * 4;
        Assert.True(bitmap.Pixels[marker + 2] > bitmap.Pixels[marker] + 80, "Top-left red marker lost its orientation.");
        Assert.True(bitmap.Pixels[300 * bitmap.Stride + 240 * 4] > 245, "Paper background was not whitened.");
        var tone = 130 * bitmap.Stride + 140 * 4;
        Assert.InRange(bitmap.Pixels[tone], 88, 96); // 80 + brightness 24, contrast factor 1.5 gives 92; allow JPEG rounding.
    }

    private static PdfBitmap Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var source = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return new(source.PixelWidth, source.PixelHeight, source.PixelWidth * 4, pixels);
    }

    private sealed class MarkerScanner : IScannerService
    {
        public int Count { get; set; } = 3;
        public bool RealText { get; set; }
        public Task<IReadOnlyList<ScanDevice>> ListDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScanDevice>>([FakeScannerService.Device]);
        public Task<ScanCapabilities> GetCapabilitiesAsync(ScanDevice device, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScanCapabilities([ScanSource.Flatbed], [100, 200, 300], Enum.GetValues<ScanColorMode>()));
        public Task ScanAsync(ScanDevice device, ScanSettings settings, IProgress<ScannedPage> progress, CancellationToken cancellationToken = default)
        {
            if (RealText)
                return new FakeScannerService { PageCount = Count }.ScanAsync(device, settings, new Sink(page =>
                {
                    for (var y = 380; y < 420; y++)
                        for (var x = 300; x < 340; x++)
                        {
                            var i = y * page.Bitmap.Stride + x * 4;
                            page.Bitmap.Pixels[i] = page.Bitmap.Pixels[i + 1] = 20;
                            page.Bitmap.Pixels[i + 2] = 190;
                        }
                    progress.Report(page);
                }), cancellationToken);
            for (var n = 0; n < Count; n++)
            {
                var pixels = new byte[400 * 500 * 4];
                for (var y = 0; y < 500; y++)
                    for (var x = 0; x < 400; x++)
                    {
                        var i = (y * 400 + x) * 4;
                        var value = (byte)(x < 8 || x >= 392 || y < 8 || y >= 492 ? 0 : 211);
                        if (x is >= 110 and < 300 && y is >= 110 and < 340 && (y - 110 - (int)(x * .0262)) % 18 is >= 0 and < 3) value = 20;
                        if (x is >= 150 and < 210 && y is >= 150 and < 210) value = 80;
                        if (x is >= 340 and < 360 && y >= 200 + n * 20 && y < 210 + n * 20) value = 0;
                        pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                        if (x is >= 60 and < 110 && y is >= 70 and < 130)
                        { pixels[i] = 20; pixels[i + 1] = 20; pixels[i + 2] = 190; }
                        pixels[i + 3] = 255;
                    }
                // Physical page was loaded upside down. The logical upright marker above starts at top-left.
                progress.Report(new(ScanPageProcessor.Rotate(new(400, 500, 1600, pixels), 180, cancellationToken), settings.Dpi, settings.Dpi));
            }
            return Task.CompletedTask;
        }
        private sealed class Sink(Action<ScannedPage> receive) : IProgress<ScannedPage>
        {
            public void Report(ScannedPage page) => receive(page);
        }
    }

    private static void Run(Func<Task> action) => Sta.Run(() =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var task = action();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (task.IsCompleted) { timer.Stop(); frame.Continue = false; } };
        timer.Start();
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        return true;
    });
}
