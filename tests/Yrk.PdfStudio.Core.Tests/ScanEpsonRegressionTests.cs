using System.Security.Cryptography;
using System.Net.Http;
using System.Windows.Threading;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Ocr;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanEpsonRegressionTests
{
    private static readonly HttpClient Client = new();
    private static OcrLanguageData Data(ScanSession session)
    {
        var folder = Directory.CreateDirectory(Path.Combine(session.DirectoryPath, "osd")).FullName;
        byte[] payload = [1, 2, 3];
        File.WriteAllBytes(Path.Combine(folder, "osd.traineddata"), payload);
        return new(folder, Client, new Dictionary<string, (string, long)>
            { ["osd"] = (Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length) });
    }

    [Theory]
    [InlineData(ScanAdjustmentScope.ThisPage)]
    [InlineData(ScanAdjustmentScope.Selected)]
    [InlineData(ScanAdjustmentScope.AllPages)]
    public void Enabling_rotates_only_scope_preserves_crop_and_supports_undo_without_rotating_twice(ScanAdjustmentScope scope) => Run(async () =>
    {
        using var session = new ScanSession();
        var data = Data(session);
        for (var i = 0; i < 3; i++) session.Add(ScanSessionTests.Page(100, 160));
        var detector = new Detector();
        var model = new ScanViewModel(session, new FakeScannerService(), Dispatcher.CurrentDispatcher)
        { Settings = new(AutoCrop: false, AutoStraighten: false), Processor = new(new WpfImageCodec(), detector, data.Folder) };
        await model.LoadPagesAsync();
        model.Select(0);
        if (scope == ScanAdjustmentScope.Selected) model.Select(1, control: true);
        model.SetAdjustmentScope(scope);
        var crop = new NormalizedRect(.1, .2, .3, .4);
        model.Pages[0].Page.Crop = crop;
        await model.SetAutoOrientationAsync(true, data, () => throw new Exception("Must not download"));
        var count = scope == ScanAdjustmentScope.AllPages ? 3 : scope == ScanAdjustmentScope.Selected ? 2 : 1;
        Assert.Equal(count, detector.Calls);
        Assert.Equal(count, model.Pages.Count(p => p.Page.Rotation == 180));
        Assert.Equal(.6, model.Pages[0].Page.Crop!.Value.X, 10);
        Assert.Equal(.4, model.Pages[0].Page.Crop!.Value.Y, 10);
        Assert.Equal(.3, model.Pages[0].Page.Crop!.Value.Width, 10);
        Assert.Equal(.4, model.Pages[0].Page.Crop!.Value.Height, 10);
        Assert.Contains("180", model.AdjustmentMessage);
        await model.SetAutoOrientationAsync(false, data, () => Task.FromResult(false));
        await model.SetAutoOrientationAsync(true, data, () => Task.FromResult(false));
        Assert.Equal(count, detector.Calls);
        await model.UndoAsync();
        Assert.All(model.Pages, p => Assert.Equal(0, p.Page.Rotation));
        Assert.Equal(crop, model.Pages[0].Page.Crop);
        await model.RedoAsync();
        Assert.Equal(count, model.Pages.Count(p => p.Page.Rotation == 180));
    });

    [Theory]
    [InlineData(ScanSource.Flatbed, 1)]
    [InlineData(ScanSource.Flatbed, 3)]
    [InlineData(ScanSource.Feeder, 3)]
    [InlineData(ScanSource.Duplex, 4)]
    public void Every_received_scan_page_rotates_and_batch_is_undoable(ScanSource source, int count) => Run(async () =>
    {
        using var session = new ScanSession();
        var data = Data(session);
        var detector = new Detector();
        var scanner = new FakeScannerService { PageCount = count };
        var model = new ScanViewModel(session, scanner, Dispatcher.CurrentDispatcher)
        { Settings = new(Dpi: 75, Source: source, AutoCrop: false, AutoStraighten: false),
            Processor = new(new WpfImageCodec(), detector, data.Folder) };
        await model.SetAutoOrientationAsync(true, data, () => Task.FromResult(false));
        await model.ScanAsync(FakeScannerService.Device, () => { });
        Assert.Equal(count, detector.Calls);
        Assert.Equal(count, model.Pages.Count);
        Assert.All(model.Pages, p => { Assert.Equal(180, p.Page.Rotation); Assert.NotNull(p.Thumbnail); });
        await model.AutoCropAsync();
        Assert.Equal(count, detector.Calls);
        await model.UndoAsync(); // auto adjust
        await model.UndoAsync(); // scan batch
        Assert.Empty(model.Pages);
        await model.RedoAsync();
        Assert.All(model.Pages, p => Assert.Equal(180, p.Page.Rotation));
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Missing_or_oversized_card_crop_keeps_session_and_can_be_fixed(bool back, bool oversized) => Run(async () =>
    {
        using var session = new ScanSession();
        var original = session.Add(ScanSessionTests.Page(20, 30));
        var scanner = new FakeScannerService { PageCount = 1 }; // Document, not a detectable card.
        var model = new ScanViewModel(session, scanner, Dispatcher.CurrentDispatcher) { Settings = new(Dpi: 75) };
        await model.ScanCardAsync(FakeScannerService.Device);
        await model.ScanCardAsync(FakeScannerService.Device);
        var front = model.CardFront!;
        var rear = model.CardBack!;
        var valid = new NormalizedRect(.2, .2, .4, .15);
        front.Page.Crop = rear.Page.Crop = valid;
        var invalid = back ? rear : front;
        invalid.Page.Crop = oversized ? new(0, 0, 1, 1) : null;
        await model.ScanCardAsync(FakeScannerService.Device);
        Assert.Same(front, model.CardFront); Assert.Same(rear, model.CardBack);
        Assert.Same(invalid, model.TemporaryPreview);
        Assert.Equal(original, Assert.Single(session.Pages));
        Assert.Empty(model.Pages);
        Assert.Contains(back ? "Back" : "Front", model.AdjustmentMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(front.Page.FilePath)); Assert.True(File.Exists(rear.Page.FilePath));
        invalid.Page.Crop = valid;
        await model.ScanCardAsync(FakeScannerService.Device);
        Assert.Single(model.Pages); Assert.Equal(2, session.Pages.Count);
        Assert.Null(model.CardFront); Assert.Null(model.CardBack);
    });

    [Fact]
    public void Osd_uses_original_resolution_without_preview_whitening()
    {
        using var session = new ScanSession();
        var data = Data(session);
        var detector = new Detector();
        var page = ScanSessionTests.Page(2480, 3508) with { DpiX = 300, DpiY = 300 };
        page.Bitmap.Pixels[0] = 211;
        new ScanPageProcessor(new WpfImageCodec(), detector, data.Folder).DetectOrientation(page);
        Assert.Equal(3508, detector.Bitmap!.Height);
        Assert.Equal(211, detector.Bitmap.Pixels[0]);
    }

    private sealed class Detector : IScanOrientationDetector
    {
        public int Calls { get; private set; }
        public PdfBitmap? Bitmap { get; private set; }
        public int? Detect(PdfBitmap bitmap, string tessdataFolder, CancellationToken cancellationToken)
        { Calls++; Bitmap = bitmap; return 180; }
    }

    [Fact]
    public void Missing_or_invalid_osd_is_logged_without_throwing()
    {
        using var session = new ScanSession();
        var messages = new List<string>();
        var page = ScanSessionTests.Page(100, 160);
        var processor = new ScanPageProcessor(new WpfImageCodec(), tessdataFolder: session.DirectoryPath, log: messages.Add);
        Assert.Equal(0, processor.DetectOrientation(page));
        Assert.Contains(messages, m => m.Contains("missing", StringComparison.OrdinalIgnoreCase));
        messages.Clear();
        File.WriteAllBytes(Path.Combine(session.DirectoryPath, "osd.traineddata"), [1, 2, 3]);
        Assert.Equal(0, processor.DetectOrientation(page));
        Assert.Contains(messages, m => m.Contains("Exception", StringComparison.Ordinal));
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
