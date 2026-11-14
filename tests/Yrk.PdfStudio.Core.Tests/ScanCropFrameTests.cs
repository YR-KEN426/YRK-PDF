using System.Windows;
using System.Windows.Threading;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanCropFrameTests
{
    private static ScanCropFrame Frame() => new(1000, 1200, new(.1, .1, .6, .5), new(.08, .08, .84, .84));
    [Theory]
    [InlineData(.25)] [InlineData(1)] [InlineData(2)]
    public void Handles_and_drag_use_source_pixels_at_all_zoom_and_pan(double zoom)
    {
        var frame = Frame();
        var view = new ScanViewTransform(); view.Resize(new(1000, 1200), new(500, 400));
        view.ZoomAt(zoom, new(200, 150)); view.Pan(new(-80, -50));
        foreach (var handle in Enum.GetValues<CropHandle>().Skip(2))
        {
            var p = ScanCropFrame.HandlePoint(frame.Box, handle);
            var screen = view.ImageToView(new(p.X, p.Y)); screen.X += 7;
            var mapped = view.ViewToImage(screen);
            Assert.Equal(handle, frame.HitTest(mapped.X, mapped.Y, view.Zoom));
            frame.Begin(p.X, p.Y, handle);
            var end = view.ViewToImage(view.ImageToView(new(p.X + 40, p.Y + 32)));
            frame.Drag(end.X, end.Y, view.Zoom, alt: true);
            var b = frame.Box;
            Assert.Equal(handle is CropHandle.W or CropHandle.NW or CropHandle.SW ? 140 : 100, b.X, 6);
            Assert.Equal(handle is CropHandle.N or CropHandle.NW or CropHandle.NE ? 152 : 120, b.Y, 6);
            Assert.Equal(handle is CropHandle.N or CropHandle.S ? 600 : handle is CropHandle.W or CropHandle.NW or CropHandle.SW ? 560 : 640, b.Width, 6);
            Assert.Equal(handle is CropHandle.W or CropHandle.E ? 600 : handle is CropHandle.N or CropHandle.NW or CropHandle.NE ? 568 : 632, b.Height, 6);
            frame = Frame();
        }
        Assert.Equal(CropHandle.Move, frame.HitTest(300, 300, zoom));
        Assert.Equal(CropHandle.New, frame.HitTest(950, 1100, zoom));
        frame.Begin(300, 300, CropHandle.Move); frame.Drag(330, 350, zoom, alt: true);
        Assert.Equal(new CropBox(130, 170, 600, 600), frame.Box);
        frame.Begin(900, 1000, CropHandle.New); frame.Drag(800, 900, zoom, alt: true);
        Assert.Equal(new CropBox(800, 900, 100, 100), frame.Box);
    }
    [Theory]
    [InlineData(.25, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(.25, true)] [InlineData(1, true)] [InlineData(2, true)]
    public void Snapping_has_eight_screen_pixel_tolerance_and_Alt_disables_it(double zoom, bool alt)
    {
        var frame = Frame();
        frame.Begin(100, 420, CropHandle.W);
        frame.Drag(80 + 6 / zoom, 420, zoom, alt);
        Assert.Equal(alt ? 80 + 6 / zoom : 80, frame.Box.X, 7);
        frame = Frame(); frame.Begin(700, 720, CropHandle.SE);
        frame.Drag(1000 - 6 / zoom, 1200 - 6 / zoom, zoom, alt);
        Assert.Equal(alt ? 1000 - 6 / zoom : 1000, frame.Box.Right, 7);
        Assert.Equal(alt ? 1200 - 6 / zoom : 1200, frame.Box.Bottom, 7);
        frame = Frame(); frame.Begin(100, 420, CropHandle.W);
        frame.Drag(80 + 9 / zoom, 420, zoom);
        Assert.Equal(80 + 9 / zoom, frame.Box.X, 7);
    }
    [Theory]
    [InlineData(CropAspect.Paper, false, .707070707)]
    [InlineData(CropAspect.Original, false, .833333333)]
    [InlineData(CropAspect.Free, true, 1)]
    public void Aspect_ratios_are_preserved_at_bounds(CropAspect aspect, bool shift, double expected)
    {
        foreach (var handle in new[] { CropHandle.NW, CropHandle.NE, CropHandle.SW, CropHandle.SE })
        {
            var frame = Frame(); frame.Aspect = aspect; frame.PaperRatio = 210.0 / 297;
            var p = ScanCropFrame.HandlePoint(frame.Box, handle); frame.Begin(p.X, p.Y, handle);
            frame.Drag(handle is CropHandle.NW or CropHandle.SW ? -500 : 1600,
                handle is CropHandle.NW or CropHandle.NE ? -400 : 2000, 2, alt: true, shift);
            Assert.Equal(expected, frame.Box.Width / frame.Box.Height, 7);
            Assert.InRange(frame.Box.X, 0, 1000); Assert.InRange(frame.Box.Y, 0, 1200);
            Assert.InRange(frame.Box.Right, 0, 1000); Assert.InRange(frame.Box.Bottom, 0, 1200);
        }
        Assert.Equal(297.0 / 210, ScanCropFrame.MatchOrientation(210.0 / 297, 1200, 1000), 7);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Numeric_values_round_trip_in_user_units_and_ignore_invalid_input(bool metric)
    {
        foreach (var pixels in new[] { 0.0, 1, 123.5, 2550 })
        foreach (var dpi in new[] { 75.0, 300, 600 })
        {
            Assert.True(ScanCropFrame.TryPixels(ScanCropFrame.Format(pixels, dpi, metric), dpi, metric, out var actual));
            Assert.InRange(Math.Abs(actual - pixels), 0, dpi * (metric ? .05 / 25.4 : .005) + 1e-6);
        }
        Assert.Equal(metric ? 25.4 : 1, ScanCropFrame.ToUnits(300, 300, metric), 7);
        Assert.False(ScanCropFrame.TryPixels("bad", 300, metric, out _));
        Assert.False(ScanCropFrame.TryPixels("NaN", 300, metric, out _));
        var frame = Frame(); frame.SetBox(new(-10, 1199, 9000, -50));
        Assert.Equal(new CropBox(0, 1197, 1000, 3), frame.Box);
    }
    [Fact]
    public void Draft_initialization_cancel_minimum_and_keyboard_steps()
    {
        var frame = Frame(); var previous = frame.Normalized;
        frame.Step(1, -1, false, false); Assert.Equal(new CropBox(101, 119, 600, 600), frame.Box);
        frame.Step(1, 1, true, true); Assert.Equal(new CropBox(101, 119, 610, 610), frame.Box);
        frame.Begin(101, 119, CropHandle.NW); frame.Drag(1000, 1200, 1, alt: true);
        Assert.True(frame.Box.Width >= 3 && frame.Box.Height >= 3);
        Assert.Equal(previous, frame.Cancel());
        Assert.Equal(new CropBox(0, 0, 1000, 1200), new ScanCropFrame(1000, 1200, null, null).Box);
        Assert.Equal(new CropBox(100, 120, 600, 600), new ScanCropFrame(1000, 1200, null, previous).Box);
    }
    [Theory]
    [InlineData(ScanAdjustmentScope.ThisPage)]
    [InlineData(ScanAdjustmentScope.Selected)]
    [InlineData(ScanAdjustmentScope.AllPages)]
    public void Apply_and_reset_honor_scope_with_one_undo_each(ScanAdjustmentScope scope) => Run(async () =>
    {
        using var session = new ScanSession();
        for (var i = 0; i < 3; i++) session.Add(ScanSessionTests.Page(400, 500));
        var model = new ScanViewModel(session, new FakeScannerService(), Dispatcher.CurrentDispatcher)
        { Settings = new(AutoCrop: false, AutoStraighten: false) };
        await model.LoadPagesAsync(); model.Select(0); model.Select(1, control: true); model.SetAdjustmentScope(scope);
        var draft = new ScanCropFrame(400, 500, null, null);
        draft.Begin(40, 50, CropHandle.New); draft.Drag(320, 400, 1, alt: true);
        await model.CropSelectedAsync(draft.Normalized);
        for (var i = 0; i < 3; i++) Assert.Equal(scope == ScanAdjustmentScope.AllPages || i == 1 || scope == ScanAdjustmentScope.Selected && i == 0,
            model.Pages[i].Page.Crop is not null);
        await model.UndoAsync(); Assert.All(model.Pages, p => Assert.Null(p.Page.Crop)); Assert.False(model.CanUndo);
        await model.RedoAsync();
        // Undo/redo restores the first selected page as Current in the existing editor.
        if (scope == ScanAdjustmentScope.ThisPage) model.Select(1);
        var saved = model.Pages.Select(p => p.Page.Crop).ToArray();
        await model.CropSelectedAsync(null); Assert.All(model.Pages, p => Assert.Null(p.Page.Crop));
        await model.UndoAsync(); Assert.Equal(saved, model.Pages.Select(p => p.Page.Crop));
    });
    [Fact]
    public void Frame_crop_is_saved_with_correct_pdf_dimensions_and_pixels() => Run(async () =>
    {
        using var session = new ScanSession();
        var page = ScanSessionTests.Page(400, 500);
        // Red rectangle exactly within the eventual crop, white outside.
        for (var y = 100; y < 400; y++) for (var x = 80; x < 320; x++)
        { var i = y * page.Bitmap.Stride + x * 4; page.Bitmap.Pixels[i] = page.Bitmap.Pixels[i + 1] = 0; page.Bitmap.Pixels[i + 2] = 255; }
        session.Add(page with { DpiX = 100, DpiY = 100 });
        var model = new ScanViewModel(session, new FakeScannerService(), Dispatcher.CurrentDispatcher)
        { Settings = new(Dpi: 100, AutoCrop: false, AutoStraighten: false, JpegQuality: 95) };
        await model.LoadPagesAsync();
        var draft = new ScanCropFrame(400, 500, null, null);
        draft.Begin(80, 100, CropHandle.New); draft.Drag(320, 400, .25, alt: true);
        await model.CropSelectedAsync(draft.Normalized); model.CommitPages();
        using var engine = new PdfiumEngine();
        var path = Path.Combine(session.DirectoryPath, "frame.pdf");
        await new ScanPdfBuilder(engine, new(new WpfImageCodec())).BuildAsync(session, model.Settings, path);
        await using var pdf = await engine.OpenAsync(path);
        Assert.Equal(240 * .72, pdf.PageSizes[0].Width, 2); Assert.Equal(300 * .72, pdf.PageSizes[0].Height, 2);
        var bitmap = await pdf.RenderPageAsync(0, 240, 300);
        foreach (var (x, y) in new[] { (10, 10), (120, 150), (230, 290) })
        {
            var i = y * bitmap.Stride + x * 4;
            Assert.InRange(bitmap.Pixels[i], 0, 15); Assert.InRange(bitmap.Pixels[i + 2], 240, 255);
        }
    });
    private static void Run(Func<Task> action) => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = action(); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (task.IsCompleted) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); return true;
    });
}
