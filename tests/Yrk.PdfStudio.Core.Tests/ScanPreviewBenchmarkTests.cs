using System.Diagnostics;
using System.Windows.Threading;
using Xunit.Abstractions;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanPreviewBenchmarkTests(ITestOutputHelper output)
{
    private sealed class BenchmarkFactAttribute : FactAttribute
    {
        public BenchmarkFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("YRK_SCAN_BENCH") != "1") Skip = "Set YRK_SCAN_BENCH=1 to measure preview updates.";
        }
    }

    [BenchmarkFact]
    public void Slider_update_cost() => Sta.Run(() =>
    {
        const int width = 2480, height = 3508;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = (byte)(180 + x % 70);
            pixels[i + 1] = (byte)(170 + y % 80);
            pixels[i + 2] = (byte)(160 + (x + y) % 90);
            pixels[i + 3] = 255;
        }
        using var session = new ScanSession();
        var entry = new ScanPageEntry(session.Add(new(new(width, height, width * 4, pixels), 300, 300)));
        var model = new ScanViewModel(session, new FakeScannerService(), Dispatcher.CurrentDispatcher)
        { Settings = new(Dpi: 300) };
        model.PreviewAsync(entry).GetAwaiter().GetResult(); // Warm the source cache and JIT.
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
        {
            entry.Page.Adjustments = new(Brightness: 10 + i, Contrast: 15 + i, WhiteBackground: true, Sharpness: 20);
            model.PreviewAsync(entry).GetAwaiter().GetResult();
        }
        watch.Stop();
        output.WriteLine($"2480x3508 colour, warm cache, brightness/contrast/white/sharpness: {watch.Elapsed.TotalMilliseconds / 5:F2} ms/update (5 updates)");
        return true;
    });
}
