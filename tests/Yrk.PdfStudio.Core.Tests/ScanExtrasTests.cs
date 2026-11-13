using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanExtrasTests
{
    private static readonly int[] FullArea = [0, 0, 2480, 3508];
    private static readonly int[] SelectedArea = [620, 1754, 1240, 877];
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fake_scans_only_requested_region_and_reports_software_fallback(bool supported)
    {
        var scanner = new FakeScannerService { PageCount = 1, SupportsRegion = supported, PageStyle = FakeScanPageStyle.CardOnWhiteGlass };
        ScannedPage? whole = null, cropped = null;
        var settings = new ScanSettings(Dpi: 75);
        await scanner.ScanAsync(FakeScannerService.Device, settings, new ScanSessionTests.Sink<ScannedPage>(p => whole = p));
        var region = new NormalizedRect(.2, .25, .5, .4);
        await scanner.ScanAsync(FakeScannerService.Device, settings with { Region = region }, new ScanSessionTests.Sink<ScannedPage>(p => cropped = p));
        var expected = ScanRegion.Crop(whole!.Bitmap, region);
        Assert.Equal(expected.Width, cropped!.Bitmap.Width);
        Assert.Equal(expected.Height, cropped.Bitmap.Height);
        Assert.Equal(expected.Pixels, cropped.Bitmap.Pixels);
        Assert.Equal(75, cropped.DpiX);
        Assert.Equal(!supported, cropped.Notices!.Contains(ScanRegion.FallbackNotice));
    }

    [Theory]
    [InlineData(0, 0, 1, 1, 2480, 3508, 0, 0, 2480, 3508)]
    [InlineData(.25, .5, .5, .25, 2480, 3508, 620, 1754, 1240, 877)]
    [InlineData(.9999, .9999, .0001, .0001, 10, 20, 9, 19, 1, 1)]
    public void Wia_area_uses_actual_device_pixel_maxima_and_clamps_tiny_regions(double x, double y, double w, double h,
        int maxW, int maxH, int expectedX, int expectedY, int expectedW, int expectedH)
        => Assert.Equal((expectedX, expectedY, expectedW, expectedH), WiaScannerService.CalculateArea(new(x, y, w, h), maxW, maxH));

    [Fact]
    public void Invalid_region_is_rejected_before_scanning()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ScanSettings(Region: new(.8, 0, .5, 1)).Validate());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wia_region_sets_pixel_properties_or_restores_full_glass_after_driver_rejection(bool reject)
    {
        var item = new AreaItem(reject);
        var fallback = WiaScannerService.ConfigureArea(item, new(.25, .5, .5, .25));
        Assert.Equal(reject, fallback);
        var values = Enumerable.Range(1, 4).Select(i => item.Properties[i].Value).ToArray();
        Assert.Equal(reject ? FullArea : SelectedArea, values);
        // A subsequent preview must not inherit the last selected scan area.
        Assert.False(WiaScannerService.ConfigureArea(item, null));
        Assert.Equal(0, item.Properties[1].Value);
        Assert.Equal(2480, item.Properties[3].Value);
    }

    [Fact]
    public void Wia_missing_region_properties_requests_software_fallback()
        => Assert.True(WiaScannerService.ConfigureArea(new AreaItem(false, missing: true), new(0, 0, .5, .5)));

    public sealed class AreaItem(bool reject, bool missing = false)
    {
        public WiaErrorMappingTests.AutomationItems<AreaProperty> Properties { get; } = new(missing ? [] :
            [new(6149, 0, 2480, reject), new(6150, 0, 3508), new(6151, 2480, 2480), new(6152, 3508, 3508)]);
    }

    public sealed class AreaProperty(int id, int initial, int maximum, bool reject = false)
    {
        private int _value = initial;
        public int PropertyID => id;
        public int SubType { get; } = 1;
        public int SubTypeMax => maximum;
        public int Value
        {
            get => _value;
            set
            {
                if (reject && value > 0) throw new NotSupportedException("Driver rejected scan position.");
                _value = value;
            }
        }
    }

    [Fact]
    public void Glass_preview_is_75_dpi_full_glass_outside_document_and_deleted_when_finished() => Run(async () =>
    {
        using var session = new ScanSession();
        var scanner = new FakeScannerService { PageCount = 1 };
        var model = new ScanViewModel(session, scanner, Dispatcher.CurrentDispatcher)
        { Settings = new(Dpi: 200, Region: new(.2, .2, .5, .5)) };
        await model.PreviewGlassAsync(FakeScannerService.Device);
        var preview = model.GlassPreview!.Page;
        Assert.Empty(session.Pages); Assert.Empty(model.Pages);
        Assert.Null(model.Settings.Region);
        Assert.Equal(75, preview.DpiX);
        Assert.Equal((int)Math.Round(210 / 25.4 * 75), session.Read(preview).Bitmap.Width);
        var path = preview.FilePath;
        Assert.True(File.Exists(path));
        model.Settings = model.Settings with { Region = new(0, 0, .5, .5), AutoCrop = false, AutoStraighten = false };
        model.ClearGlassPreview();
        Assert.False(File.Exists(path));
        await model.ScanAsync(FakeScannerService.Device, () => { });
        Assert.Single(model.Pages);
        Assert.Equal((int)Math.Round(Math.Round(210 / 25.4 * 200) * .5), session.Read(session.Pages[0]).Bitmap.Width);
    });

    [Fact]
    public void Two_card_sides_become_one_A4_page_at_real_size_and_source_files_are_removed() => Run(async () =>
    {
        using var session = new ScanSession();
        var scanner = new FakeScannerService { PageCount = 1, PageStyle = FakeScanPageStyle.CardOnWhiteGlass };
        var model = new ScanViewModel(session, scanner, Dispatcher.CurrentDispatcher);
        model.ApplyPreset(ScanPreset.TwoSidedCard);
        model.Settings = model.Settings with { Dpi = 100 };
        await model.ScanCardAsync(FakeScannerService.Device);
        var front = model.CardFront!.Page;
        Assert.NotNull(front.Crop);
        Assert.Empty(model.Pages); Assert.Empty(session.Pages);
        await model.ScanCardAsync(FakeScannerService.Device);
        var back = model.CardBack!.Page;
        Assert.NotNull(back.Crop);
        await model.ScanCardAsync(FakeScannerService.Device);
        Assert.False(File.Exists(front.FilePath)); Assert.False(File.Exists(back.FilePath));
        Assert.Null(model.CardFront); Assert.Null(model.CardBack);
        var page = Assert.Single(session.Pages);
        Assert.Single(model.Pages);
        var image = session.Read(page);
        Assert.Equal((int)Math.Round(210 / 25.4 * 100), image.Bitmap.Width);
        Assert.Equal((int)Math.Round(297 / 25.4 * 100), image.Bitmap.Height);
        Assert.Equal(100, image.DpiX);
        // The two beige cards occupy about 85.6 mm horizontally, with a white gap.
        var row = image.Bitmap.Height / 4;
        var colored = Enumerable.Range(0, image.Bitmap.Width).Count(x => image.Bitmap.Pixels[row * image.Bitmap.Stride + x * 4] < 250);
        Assert.InRange(colored * 25.4 / image.DpiX, 84, 88);
        Assert.Equal(255, image.Bitmap.Pixels[(image.Bitmap.Height / 2) * image.Bitmap.Stride]);
        var processed = new ScanPageProcessor(new WpfImageCodec()).Process(image, model.Settings, crop: page.Crop);
        Assert.Equal(image.Bitmap.Width, processed.Width);
        Assert.Equal(image.Bitmap.Height, processed.Height);
    });

    [Fact]
    public void Cancelling_card_removes_both_inputs_but_keeps_document_pages() => Run(async () =>
    {
        using var session = new ScanSession();
        session.Add(ScanSessionTests.Page(20, 30));
        var scanner = new FakeScannerService { PageCount = 1, PageStyle = FakeScanPageStyle.CardOnWhiteGlass };
        var model = new ScanViewModel(session, scanner, Dispatcher.CurrentDispatcher) { Settings = new(Dpi: 75) };
        await model.ScanCardAsync(FakeScannerService.Device);
        await model.ScanCardAsync(FakeScannerService.Device);
        var paths = new[] { model.CardFront!.Page.FilePath, model.CardBack!.Page.FilePath };
        model.CancelCard();
        Assert.All(paths, path => Assert.False(File.Exists(path)));
        Assert.Single(session.Pages);
    });

    [Theory]
    [InlineData(ScannerErrorKind.Cancelled)]
    [InlineData(ScannerErrorKind.DeviceBusy)]
    public void Failed_card_back_removes_both_side_sources_and_preserves_prior_document(ScannerErrorKind error) => Run(async () =>
    {
        using var session = new ScanSession();
        var original = session.Add(ScanSessionTests.Page(20, 30));
        var scanner = new FakeScannerService { PageCount = 1, PageStyle = FakeScanPageStyle.CardOnWhiteGlass };
        var model = new ScanViewModel(session, scanner, Dispatcher.CurrentDispatcher) { Settings = new(Dpi: 75) };
        await model.ScanCardAsync(FakeScannerService.Device);
        scanner.PageCount = 2; scanner.ErrorAfterPages = 1; scanner.SimulatedError = error;
        await Assert.ThrowsAsync<ScannerException>(() => model.ScanCardAsync(FakeScannerService.Device));
        Assert.Null(model.CardFront); Assert.Null(model.CardBack);
        Assert.Equal(original, Assert.Single(session.Pages));
        Assert.Equal(original.FilePath, Assert.Single(Directory.GetFiles(session.DirectoryPath, "*.bgra")));
    });

    [Fact]
    public void Disposing_session_removes_preview_and_unfinished_card_sources() => Run(async () =>
    {
        var session = new ScanSession();
        var folder = session.DirectoryPath;
        try
        {
            var scanner = new FakeScannerService { PageCount = 1, PageStyle = FakeScanPageStyle.CardOnWhiteGlass };
            var model = new ScanViewModel(session, scanner, Dispatcher.CurrentDispatcher) { Settings = new(Dpi: 75) };
            await model.PreviewGlassAsync(FakeScannerService.Device);
            Assert.NotEmpty(Directory.GetFiles(folder, "*.bgra"));
            await model.ScanCardAsync(FakeScannerService.Device);
            Assert.Single(Directory.GetFiles(folder, "*.bgra"));
        }
        finally { session.Dispose(); }
        Assert.False(Directory.Exists(folder));
    });

    [Theory]
    [InlineData(1, 1, "scan-001.jpg")]
    [InlineData(9, 23, "scan-009.jpg")]
    [InlineData(1000, 1000, "scan-1000.jpg")]
    [InlineData(1, 1000, "scan-0001.jpg")]
    public void Image_names_are_numbered_with_at_least_three_digits(int page, int count, string expected)
        => Assert.Equal(expected, Path.GetFileName(MainViewModel.ScanImagePath("scan.jpg", page, count, ScanOutputFormat.Jpeg)));

    private static Func<object?[], object?> ImageAnswer(ScanOutputFormat format, Action<ScanSession>? observe = null) => args =>
    {
        var session = (ScanSession)args[0]!;
        var raw = ScanSessionTests.Page(80, 120);
        for (var i = 0; i < raw.Bitmap.Pixels.Length; i += 4)
            raw.Bitmap.Pixels[i] = raw.Bitmap.Pixels[i + 1] = raw.Bitmap.Pixels[i + 2] = 100;
        var first = session.Add(raw);
        first.Rotation = 90; first.Crop = new(.1, .1, .5, .5); first.Brightness = 20;
        session.Add(ScanSessionTests.Page(80, 120));
        observe?.Invoke(session);
        return new ScanRequest(session, new(AutoCrop: false, AutoStraighten: false), true, ["tha"], format);
    };

    [Theory]
    [InlineData(ScanOutputFormat.Jpeg)]
    [InlineData(ScanOutputFormat.Png)]
    public async Task Image_export_applies_edits_numbers_pages_skips_OCR_and_cleans_temp(ScanOutputFormat format)
    {
        await using var app = new AppTestHarness();
        string? folder = null;
        app.Dialogs.Answer("AskScan", ImageAnswer(format, s => folder = s.DirectoryPath));
        var chosen = app.PathFor("scan.image");
        app.Dialogs.Answer("PickSaveFile", chosen);
        await app.Model.ScanToPdfCommand.ExecuteAsync(null);
        Assert.Empty(app.Dialogs.Named("ShowError"));
        var files = Enumerable.Range(1, 2).Select(n => MainViewModel.ScanImagePath(chosen, n, 2, format)).ToArray();
        Assert.All(files, path => Assert.True(File.Exists(path)));
        using var stream = File.OpenRead(files[0]);
        var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(60, image.PixelWidth); Assert.Equal(40, image.PixelHeight);
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        bgra.CopyPixels(pixels, image.PixelWidth * 4, 0);
        Assert.InRange((int)pixels[0], 138, 142); // Brightness +20 is applied before JPEG/PNG encoding.
        Assert.False(app.Model.HasDocument);
        Assert.Empty(app.Dialogs.Named("AskOcr"));
        Assert.False(Directory.Exists(folder));
        Assert.Empty(Directory.GetFiles(app.Folder, "*.yrk-writing"));
    }

    [Fact]
    public async Task Declined_image_replacement_does_not_write_any_pages_under_rejected_name()
    {
        await using var app = new AppTestHarness();
        var chosen = app.PathFor("scan.png");
        var existing = app.PathFor("scan-002.png");
        byte[] original = [7, 8, 9];
        File.WriteAllBytes(existing, original);
        app.Dialogs.Answer("AskScan", ImageAnswer(ScanOutputFormat.Png));
        app.Dialogs.Answer("PickSaveFile", chosen, app.PathFor("other.png"));
        app.Dialogs.Answer("Confirm", false);
        await app.Model.ScanToPdfCommand.ExecuteAsync(null);
        Assert.Equal(original, File.ReadAllBytes(existing));
        Assert.False(File.Exists(app.PathFor("scan-001.png")));
        Assert.True(File.Exists(app.PathFor("other-001.png")));
        Assert.True(File.Exists(app.PathFor("other-002.png")));
        Assert.Single(app.Dialogs.Named("Confirm"));
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Fact]
    public async Task Confirmed_image_replacement_succeeds_and_cleans_staging_files()
    {
        await using var app = new AppTestHarness();
        var output = app.PathFor("scan-001.png");
        File.WriteAllBytes(output, [1, 2, 3]);
        app.Dialogs.Answer("AskScan", ImageAnswer(ScanOutputFormat.Png));
        app.Dialogs.Answer("PickSaveFile", app.PathFor("scan.png"));
        app.Dialogs.Answer("Confirm", true);
        await app.Model.ScanToPdfCommand.ExecuteAsync(null);
        using var stream = File.OpenRead(output);
        Assert.Single(BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames);
        Assert.Single(app.Dialogs.Named("Confirm"));
        Assert.Empty(app.Dialogs.Named("ShowError"));
        Assert.Empty(Directory.GetFiles(app.Folder, "*.yrk-writing"));
    }

    [Fact]
    public async Task Image_output_rejects_junction_parent_and_does_not_touch_its_target()
    {
        await using var app = new AppTestHarness();
        var target = Directory.CreateDirectory(app.PathFor("target")).FullName;
        var link = app.PathFor("link");
        var sentinel = Path.Combine(target, "scan-001.png");
        File.WriteAllText(sentinel, "keep");
        ScanSessionTests.MakeJunction(link, target);
        try
        {
            app.Dialogs.Answer("AskScan", ImageAnswer(ScanOutputFormat.Png));
            app.Dialogs.Answer("PickSaveFile", Path.Combine(link, "scan.png"));
            app.Dialogs.Answer("Confirm", true);
            await app.Model.ScanToPdfCommand.ExecuteAsync(null);
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Assert.Single(Directory.GetFiles(target));
            Assert.Empty(app.Dialogs.Named("Confirm"));
            Assert.Single(app.Dialogs.Named("ShowError"));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Image_destination_created_or_changed_after_approval_is_never_overwritten(bool existed)
    {
        await using var app = new AppTestHarness();
        var output = app.PathFor("scan-001.png");
        if (existed) File.WriteAllBytes(output, [1]);
        app.Dialogs.Answer("AskScan", ImageAnswer(ScanOutputFormat.Png));
        app.Dialogs.Answer("PickSaveFile", app.PathFor("scan.png"));
        if (existed) app.Dialogs.Answer("Confirm", true);
        byte[] competing = [5, 6, 7];
        app.Model.CommitEditsRequested += (_, _) => File.WriteAllBytes(output, competing);
        await app.Model.ScanToPdfCommand.ExecuteAsync(null);
        Assert.Equal(competing, File.ReadAllBytes(output));
        var recovery = Assert.Single(Directory.GetFiles(app.Folder, "*.yrk-writing"));
        using var stream = File.OpenRead(recovery);
        Assert.Single(BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames);
        Assert.Single(app.Dialogs.Named("ShowError"));
    }

    private static void Run(Func<Task> action) => Sta.Run(() =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var task = action();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (task.IsCompleted) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
        return true;
    });
}
