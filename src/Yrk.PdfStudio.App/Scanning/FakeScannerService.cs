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
    private static readonly int[] Resolutions = [75, 100, 150, 200, 300, 600];
    public bool SupportsRegion { get; set; } = true;
    public static ScanDevice Device { get; } = new("yrk-fake", "YRK test scanner");
    public ScannerErrorKind? SimulatedError { get; set; }
    public int ErrorAfterPages { get; set; }
    public bool DisconnectOnError { get; set; }
    public int? ErrorHResult { get; set; }
    private bool _disconnected;
    public int PageCount { get; set; } = 3;
    public FakeScanPageStyle PageStyle { get; set; }

    public Task<IReadOnlyList<ScanDevice>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ScanDevice>>(_disconnected ? [] : [Device]);
    }

    public Task<ScanCapabilities> GetCapabilitiesAsync(ScanDevice device, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckDevice(device);
        return Task.FromResult(new ScanCapabilities(Enum.GetValues<ScanSource>(), Resolutions, Enum.GetValues<ScanColorMode>(), new(215.9, 297), new(215.9, 297)));
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
                {
                    _disconnected = DisconnectOnError;
                    throw new ScannerException(error, $"Simulated scanner error: {error}.", hresult: ErrorHResult) { TransferStarted = true };
                }
                var dpi = Resolutions.MinBy(n => Math.Abs((long)n - settings.Dpi));
                var bitmap = CreateTestPage(i, dpi, PageStyle);
                var notices = new List<string>();
                if (dpi != settings.Dpi) notices.Add($"DPI adjusted from {settings.Dpi} to {dpi}.");
                if (settings.Region is { } region)
                {
                    bitmap = ScanRegion.Crop(bitmap, region, cancellationToken);
                    if (!SupportsRegion) notices.Add(ScanRegion.FallbackNotice);
                }
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new(bitmap, dpi, dpi, notices));
            }
            return true;
        }, cancellationToken);
    }

    private static void CheckDevice(ScanDevice device)
    {
        if (device.Id != Device.Id) throw new ScannerException(ScannerErrorKind.NoDevice, "Unknown test scanner.");
    }

    private static PdfBitmap CreateTestPage(int index, int dpi, FakeScanPageStyle style)
    {
        var width = (int)Math.Round(210 / 25.4 * dpi);
        var height = (int)Math.Round(297 / 25.4 * dpi);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            if (style == FakeScanPageStyle.GrayFaintText)
                drawing.DrawRectangle(Brushes.LightGray, null, new Rect(0, 0, width, height));
            if (style == FakeScanPageStyle.CardOnWhiteGlass)
            {
                var card = new Rect(width * 0.25, height * 0.25, 85.6 / 25.4 * dpi, 54 / 25.4 * dpi);
                drawing.DrawRectangle(Brushes.Beige, new Pen(Brushes.LightGray, 1), card);
                drawing.DrawRectangle(Brushes.LightSteelBlue, null, new Rect(card.X + dpi * 0.15, card.Y + dpi * 0.2, dpi * 0.6, dpi * 0.8));
                for (var line = 0; line < 5; line++)
                    drawing.DrawRectangle(Brushes.Gray, null, new Rect(card.X + dpi, card.Y + dpi * (0.25 + line * 0.23), dpi * 1.6, dpi * 0.04));
            }
            else if (index % 3 != 2) // Every third page is blank, including a dark scanner border.
            {
                drawing.PushTransform(new RotateTransform(-1.5, width / 2.0, height / 2.0));
                var typeface = new Typeface(new FontFamily("Tahoma"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                for (var line = 0; line < 24; line++)
                {
                    var text = new FormattedText($"ทดสอบสแกน ภาษาไทย — Scan test page {index + 1}, line {line + 1}",
                        CultureInfo.GetCultureInfo("th-TH"), FlowDirection.LeftToRight, typeface, dpi * 0.14, style == FakeScanPageStyle.GrayFaintText ? Brushes.Gray : Brushes.Black, 1);
                    drawing.DrawText(text, new Point(width * 0.12, height * 0.12 + line * dpi * 0.28));
                }
                drawing.Pop();
            }
            if (style == FakeScanPageStyle.Default)
                drawing.DrawRectangle(null, new Pen(Brushes.Black, Math.Max(4, dpi * 0.08)), new Rect(0, 0, width, height));
        }
        var image = ImageConversion.ToPdfImage(ImageConversion.Render(visual, width, height));
        return new(image.Width, image.Height, image.Width * 4, image.Pixels);
    }
}

public enum FakeScanPageStyle { Default, GrayFaintText, CardOnWhiteGlass }
