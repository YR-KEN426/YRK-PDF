using System.Windows;
using System.Windows.Threading;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanGlassRegressionTests
{
    internal static ScannedPage WhiteGlass(bool text = true, byte ink = 65)
    {
        const int width = 600, height = 800;
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        void Mark(int x, int y, byte value)
        {
            var i = (y * width + x) * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
        }
        // A white sheet on a white lid, sparse glyphs, dust and thin scanner-edge strips; no paper shadow.
        if (text)
            for (var y = 250; y < 550; y += 25)
            for (var x = 150; x < 450; x += 12)
            for (var dy = 0; dy < 8; dy++)
            for (var dx = 0; dx < 4; dx++) Mark(x + dx, y + dy, ink);
        Mark(20, 30, 0); Mark(570, 700, 0);
        for (var y = 0; y < height / 3; y++) for (var x = 0; x < 3; x++) Mark(x, y, 0);
        return new(new(width, height, width * 4, pixels), 100, 100,
            Adjustments: new(Straighten: false));
    }

    [Theory]
    [InlineData(65)] [InlineData(225)]
    public void White_on_white_finds_ink_without_dust_and_starts_with_three_mm_margin(byte ink)
    {
        var page = WhiteGlass(ink: ink);
        Assert.Null(ScanCleanup.Check(page.Bitmap).Keep);
        var bounds = new ScanPageProcessor(new WpfImageCodec()).DetectCropBounds(page, new(AutoStraighten: false));
        Assert.Null(bounds.Paper);
        Assert.Equal(new NormalizedRect(150.0 / 600, 250.0 / 800, 292.0 / 600, 283.0 / 800), bounds.Content);
        var frame = new ScanCropFrame(600, 800, null, bounds.Paper, bounds.Content, 100, 100);
        var margin = 3 * 100 / 25.4;
        Assert.Equal(150 - margin, frame.Box.X, 6); Assert.Equal(250 - margin, frame.Box.Y, 6);
        Assert.Equal(292 + margin * 2, frame.Box.Width, 6); Assert.Equal(283 + margin * 2, frame.Box.Height, 6);
        var adjusted = page with { Adjustments = new(Brightness: -20, Contrast: 15, Straighten: false) };
        Assert.Equal(bounds.Content, new ScanPageProcessor(new WpfImageCodec()).DetectCropBounds(adjusted, new(AutoStraighten: false)).Content);
    }

    [Fact]
    public void Blank_and_dust_have_no_content_and_previous_crop_is_preserved()
    {
        Assert.Null(ScanCleanup.ContentBounds(WhiteGlass(false).Bitmap));
        var previous = new NormalizedRect(.1, .2, .7, .6);
        var frame = new ScanCropFrame(600, 800, previous, null, new(.25, .3, .5, .4), 100, 100);
        Assert.Equal(previous, frame.Normalized);
    }

    [Fact]
    public void Snap_priority_is_paper_content_image_and_alt_hides_guides()
    {
        ScanCropFrame Frame(NormalizedRect? paper, NormalizedRect? content) =>
            new(1000, 1000, new(.1, .1, .7, .7), paper, content);
        var paper = new NormalizedRect(.005, .005, .99, .99);
        var content = new NormalizedRect(.002, .002, .996, .996);
        foreach (var (p, c, expected) in new (NormalizedRect?, NormalizedRect?, double)[] { (paper, content, 5.0), (null, content, 2.0), (null, null, 0.0) })
        {
            var frame = Frame(p, c);
            frame.Begin(100, 400, CropHandle.W); frame.Drag(1, 400, 1);
            Assert.Equal(expected, frame.Box.X); Assert.Equal(expected, frame.SnapX);
            frame = Frame(p, c); frame.Begin(100, 400, CropHandle.W); frame.Drag(1, 400, 1, alt: true);
            Assert.Equal(1, frame.Box.X); Assert.Null(frame.SnapX); Assert.Null(frame.SnapY);
        }
    }

    [Fact]
    public void Pan_wheel_scrollbar_and_autoscroll_use_the_same_clamped_transform()
    {
        var view = new ScanViewTransform(); view.Resize(new(1000, 1200), new(500, 400)); view.ZoomAt(1, new(0, 0));
        Assert.Equal(new Vector(500, 800), view.ScrollMaximum);
        view.ScrollTo(200, 300); Assert.Equal(new Vector(-200, -300), view.Offset);
        view.Pan(ScanViewTransform.WheelPan(-120, false)); Assert.Equal(new Vector(-200, -348), view.Offset);
        view.Pan(ScanViewTransform.WheelPan(-120, true)); Assert.Equal(new Vector(-248, -348), view.Offset);
        Assert.Equal(new Vector(), ScanViewTransform.AutoScroll(new(250, 200), view.View));
        view.Pan(ScanViewTransform.AutoScroll(new(499, 399), view.View));
        Assert.True(view.Offset.X < -248 && view.Offset.Y < -348);
        view.Pan(new(-9999, -9999)); Assert.Equal(new Vector(-500, -800), view.Offset);
        view.Fit(); Assert.Equal(new Vector(), view.ScrollMaximum);
        Assert.True(ScanViewTransform.IsPanGesture(true, false, true));
        Assert.True(ScanViewTransform.IsPanGesture(false, true, true));
        Assert.False(ScanViewTransform.IsPanGesture(false, false, true));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void General_transfer_error_checks_inventory_and_keeps_scanned_pages(bool disconnect) => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        using var session = new ScanSession();
        var scanner = new FakeScannerService { SimulatedError = ScannerErrorKind.Other, ErrorAfterPages = 1,
            ErrorHResult = unchecked((int)0x80210001), DisconnectOnError = disconnect };
        var model = new ScanViewModel(session, scanner, Dispatcher.CurrentDispatcher) { Settings = new(Dpi: 75) };
        var task = model.ScanAsync(FakeScannerService.Device, () => { });
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (task.IsCompleted) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame);
        var error = Assert.Throws<ScannerException>(() => task.GetAwaiter().GetResult());
        Assert.Equal(disconnect ? ScannerErrorKind.Disconnected : ScannerErrorKind.Other, error.Kind);
        Assert.Equal(unchecked((int)0x80210001), error.HResult);
        Assert.Single(model.Pages); Assert.Single(session.Pages); Assert.NotNull(model.Pages[0].Thumbnail);
        return true;
    });

    [Fact]
    public async Task General_error_before_transfer_does_not_infer_disconnection()
    {
        var error = WiaScannerService.MapError(unchecked((int)0x80210001));
        Assert.Equal(ScannerErrorKind.Other, error.Kind);
        Assert.Same(error, await ScannerFailure.ResolveAsync(new FakeScannerService(), FakeScannerService.Device, error));
    }
}
