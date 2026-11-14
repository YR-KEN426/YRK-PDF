using System.Windows;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Scan;
using System.Windows.Threading;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanPreviewTests
{
    [Fact]
    public void Zoom_about_cursor_keeps_image_point_fixed()
    {
        var view = Create();
        view.ZoomAt(1, new(500, 400));
        var cursor = new Point(330, 210);
        var point = view.ViewToImage(cursor);
        view.ZoomAt(2, cursor);
        AssertPoint(cursor, view.ImageToView(point));
    }

    [Theory]
    [InlineData(.25)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void Crop_coordinates_round_trip_at_every_zoom(double zoom)
    {
        var view = Create();
        view.ZoomAt(zoom, new(500, 400));
        foreach (var corner in new[] { new Point(.1, .2), new Point(.75, .9) })
            AssertPoint(corner, view.ViewToNormalized(view.NormalizedToView(corner)));
    }

    [Fact]
    public void Fit_pan_and_zoom_are_bounded()
    {
        var view = Create();
        Assert.Equal(800.0 / 3508, view.FitScale, 8);
        view.ZoomAt(.001, new(0, 0));
        Assert.Equal(.05, view.Zoom);
        view.Pan(new(1e6, -1e6));
        Assert.Equal((1000 - 2480 * .05) / 2, view.Offset.X, 8);
        Assert.Equal((800 - 3508 * .05) / 2, view.Offset.Y, 8);
        view.ZoomAt(100, new(500, 400));
        Assert.Equal(8, view.Zoom);
        view.Pan(new(1e6, -1e6));
        Assert.Equal(0, view.Offset.X);
        Assert.Equal(800 - 3508 * 8, view.Offset.Y);
        view.Fit();
        Assert.True(view.IsFit);
    }

    [Fact]
    public void Fit_render_uses_display_size_times_monitor_DPI()
    {
        var view = Create();
        var normal = ScanPreviewSizing.Plan(view, 1);
        var highDpi = ScanPreviewSizing.Plan(view, 2);
        Assert.False(normal.IsRegion);
        Assert.Equal(800, normal.MaxSide);
        Assert.Equal(1600, highDpi.MaxSide);
        Assert.True(highDpi.MaxSide > normal.MaxSide);
    }

    [Fact]
    public void Zoom_selects_only_visible_region_and_caps_pixel_count()
    {
        var view = Create();
        view.ZoomAt(2, new(500, 400));
        var plan = ScanPreviewSizing.Plan(view, 2);
        Assert.True(plan.IsRegion);
        Assert.Equal(1000 / (2480 * 2.0) + 4.0 / 2480, plan.Region.Width, 8);
        Assert.Equal(800 / (3508 * 2.0) + 4.0 / 3508, plan.Region.Height, 8);
        var capped = ScanPreviewSizing.Plan(view, 100);
        var ratio = view.Image.Width * capped.Region.Width / (view.Image.Height * capped.Region.Height);
        Assert.True(capped.MaxSide * capped.MaxSide * Math.Min(ratio, 1 / ratio) <= ScanPreviewSizing.PixelCap + 12000);
    }

    [Fact]
    public async Task Requests_coalesce_for_60ms_and_latest_wins()
    {
        var clock = new VirtualClock();
        var renders = new List<(int Value, bool Sharp)>();
        var value = 1;
        using var queue = new ScanPreviewQueue((sharp, _) => { renders.Add((value, sharp)); return Task.CompletedTask; }, _ => { }, clock.Delay);
        queue.Request(adjustment: true);
        clock.Advance(30);
        value = 2; queue.Request(adjustment: true);
        clock.Advance(29);
        Assert.Empty(renders);
        clock.Advance(1);
        Assert.Equal((2, false), Assert.Single(renders));
        clock.Advance(169);
        Assert.Single(renders);
        clock.Advance(1);
        await queue.Completion;
        Assert.Equal((2, true), renders[1]);
    }

    [Fact]
    public async Task Stale_work_is_cancelled_and_never_runs_concurrently()
    {
        var clock = new VirtualClock();
        var gate = new TaskCompletionSource();
        CancellationToken first = default;
        var count = 0;
        using var queue = new ScanPreviewQueue(async (_, ct) =>
        {
            count++;
            if (count == 1) { first = ct; await gate.Task; ct.ThrowIfCancellationRequested(); }
        }, _ => { }, clock.Delay);
        queue.Request(endGesture: true);
        queue.Request(endGesture: true);
        queue.Request(endGesture: true);
        Assert.True(first.IsCancellationRequested);
        Assert.Equal(1, count);
        gate.SetResult();
        await queue.Completion;
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Busy_only_appears_after_300ms_and_clears_on_completion()
    {
        var clock = new VirtualClock();
        var gate = new TaskCompletionSource();
        var busy = false;
        using var queue = new ScanPreviewQueue((_, _) => gate.Task, value => busy = value, clock.Delay);
        queue.Request(endGesture: true);
        clock.Advance(299); Assert.False(busy);
        clock.Advance(1); Assert.True(busy);
        gate.SetResult(); await queue.Completion;
        Assert.False(busy);
    }

    [Fact]
    public async Task Ending_gesture_requests_sharp_immediately_without_proxy()
    {
        var clock = new VirtualClock();
        var calls = new List<bool>();
        using var queue = new ScanPreviewQueue((sharp, _) => { calls.Add(sharp); return Task.CompletedTask; }, _ => { }, clock.Delay);
        queue.Request(adjustment: true);
        queue.Request(endGesture: true);
        await queue.Completion;
        Assert.True(Assert.Single(calls));
        clock.Advance(1000);
        Assert.Single(calls);
    }

    [Fact]
    public void Sharp_render_reads_original_pixels_and_caches_full_page_and_tile() => Sta.Run(() =>
    {
        using var session = new ScanSession();
        var page = ScanSessionTests.Page(1800, 2400) with { Adjustments = new(Straighten: false) };
        var entry = new ScanPageEntry(session.Add(page));
        var model = new ScanViewModel(session, new FakeScannerService(), Dispatcher.CurrentDispatcher)
        { Settings = new(AutoStraighten: false, AutoCrop: false) };
        var proxy = model.PreviewForViewAsync(entry, true).GetAwaiter().GetResult();
        Assert.Equal(1000, proxy.Image.PixelHeight);
        var view = new ScanViewTransform();
        view.Resize(proxy.ImageSize, new(900, 1200));
        var fullPlan = ScanPreviewSizing.Plan(view, 2);
        var full = model.PreviewForViewAsync(entry, true, fullPlan).GetAwaiter().GetResult();
        Assert.Equal(2400, full.Image.PixelHeight);
        Assert.Same(full, model.PreviewForViewAsync(entry, true, fullPlan).GetAwaiter().GetResult());
        view.ZoomAt(2, new(450, 600));
        var tilePlan = ScanPreviewSizing.Plan(view, 1);
        var tile = model.PreviewForViewAsync(entry, true, tilePlan).GetAwaiter().GetResult();
        Assert.True(tilePlan.IsRegion);
        Assert.True(tile.Image.PixelHeight < page.Bitmap.Height);
        Assert.Equal(tilePlan.Region, tile.Region);
        Assert.Same(tile, model.PreviewForViewAsync(entry, true, tilePlan).GetAwaiter().GetResult());
        Assert.Same(full, model.PreviewForViewAsync(entry, true, fullPlan).GetAwaiter().GetResult());
        entry.Page.Brightness = 20;
        Assert.NotSame(tile, model.PreviewForViewAsync(entry, true, tilePlan).GetAwaiter().GetResult());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => model.PreviewForViewAsync(entry, true, tilePlan, cancelled.Token).GetAwaiter().GetResult());
        return true;
    });

    [Fact]
    public void Tile_whitening_uses_full_page_tone_reference()
    {
        var pixels = new byte[100 * 100 * 4];
        for (var i = 0; i < 100 * 100; i++)
        {
            var tone = i % 100 < 30 ? (byte)180 : (byte)230;
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = tone;
            pixels[i * 4 + 3] = 255;
        }
        var source = new PdfBitmap(100, 100, 400, pixels);
        var edits = new ScanPageAdjustments(WhiteBackground: true, Brightness: 8, Contrast: 10);
        var full = ScanPageProcessor.RenderPreparedPreview(source, edits, ScanColorMode.Color);
        var region = new NormalizedRect(.05, .1, .1, .5);
        var tile = ScanPageProcessor.RenderPreparedPreview(ScanRegion.Crop(source, region), edits,
            ScanColorMode.Color, toneReference: source);
        Assert.Equal(ScanRegion.Crop(full, region).Pixels, tile.Pixels);
    }

    private static ScanViewTransform Create()
    {
        var view = new ScanViewTransform();
        view.Resize(new(2480, 3508), new(1000, 800));
        return view;
    }
    private static void AssertPoint(Point expected, Point actual)
    { Assert.Equal(expected.X, actual.X, 8); Assert.Equal(expected.Y, actual.Y, 8); }

    private sealed class VirtualClock
    {
        private long _now;
        private readonly List<(long Due, TaskCompletionSource Source, CancellationTokenRegistration Registration)> _pending = [];
        public Task Delay(TimeSpan duration, CancellationToken ct)
        {
            var source = new TaskCompletionSource();
            var registration = ct.Register(() => source.TrySetCanceled(ct));
            _pending.Add((_now + (long)duration.TotalMilliseconds, source, registration));
            return source.Task;
        }
        public void Advance(int milliseconds)
        {
            _now += milliseconds;
            foreach (var item in _pending.Where(p => p.Due <= _now).ToArray())
            {
                _pending.Remove(item);
                item.Registration.Dispose();
                item.Source.TrySetResult();
            }
        }
    }
}
