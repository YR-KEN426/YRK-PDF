using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.App.Scanning;

/// <summary>A deterministic scanner for development and tests; no dialogs or hardware required.</summary>
public sealed class FakeScannerService : IScannerService
{
    private static readonly int[] Resolutions = [100, 150, 200, 300, 600];
    public static ScanDevice Device { get; } = new("yrk-fake", "YRK test scanner");
    public ScannerErrorKind? SimulatedError { get; set; }
    public int ErrorAfterPages { get; set; }
    public int PageCount { get; set; } = 3;

    public Task<IReadOnlyList<ScanDevice>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ScanDevice>>([Device]);
    }

    public Task<ScanCapabilities> GetCapabilitiesAsync(ScanDevice device, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckDevice(device);
        return Task.FromResult(new ScanCapabilities(Enum.GetValues<ScanSource>(), Resolutions, Enum.GetValues<ScanColorMode>()));
    }

    public Task ScanAsync(ScanDevice device, ScanSettings settings, IProgress<ScannedPage> progress, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        return ScannerSta.RunAsync(() =>
        {
            CheckDevice(device);
            for (var i = 0; i < PageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SimulatedError is { } error && i >= ErrorAfterPages)
                    throw new ScannerException(error, $"Simulated scanner error: {error}.");
                var dpi = Resolutions.MinBy(n => Math.Abs((long)n - settings.Dpi));
                var bitmap = CreateTestPage(i, dpi);
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new(bitmap, dpi, dpi, dpi == settings.Dpi ? [] : [$"DPI adjusted from {settings.Dpi} to {dpi}."]));
            }
            return true;
        }, cancellationToken);
    }

    private static void CheckDevice(ScanDevice device)
    {
        if (device.Id != Device.Id) throw new ScannerException(ScannerErrorKind.NoDevice, "Unknown test scanner.");
    }

    private static PdfBitmap CreateTestPage(int index, int dpi)
    {
        var width = (int)Math.Round(210 / 25.4 * dpi);
        var height = (int)Math.Round(297 / 25.4 * dpi);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            if (index % 3 != 2) // Every third page is blank, including a dark scanner border.
            {
                drawing.PushTransform(new RotateTransform(-1.5, width / 2.0, height / 2.0));
                var typeface = new Typeface(new FontFamily("Tahoma"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                for (var line = 0; line < 24; line++)
                {
                    var text = new FormattedText($"ทดสอบสแกน ภาษาไทย — Scan test page {index + 1}, line {line + 1}",
                        CultureInfo.GetCultureInfo("th-TH"), FlowDirection.LeftToRight, typeface, dpi * 0.14, Brushes.Black, 1);
                    drawing.DrawText(text, new Point(width * 0.12, height * 0.12 + line * dpi * 0.28));
                }
                drawing.Pop();
            }
            drawing.DrawRectangle(null, new Pen(Brushes.Black, Math.Max(4, dpi * 0.08)), new Rect(0, 0, width, height));
        }
        var image = ImageConversion.ToPdfImage(ImageConversion.Render(visual, width, height));
        return new(image.Width, image.Height, image.Width * 4, image.Pixels);
    }
}
