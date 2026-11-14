using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Scan;
using Xunit.Abstractions;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanOrientationRealTests(ITestOutputHelper output)
{
    internal static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "tessdata");

    [InstalledOsdTheory]
    [InlineData(false, 0)]
    [InlineData(false, 90)]
    [InlineData(false, 180)]
    [InlineData(false, 270)]
    [InlineData(true, 0)]
    [InlineData(true, 90)]
    [InlineData(true, 180)]
    [InlineData(true, 270)]
    public void Real_osd_detects_clockwise_correction_for_rendered_text(bool thai, int rotation)
    {
        Sta.Run(() =>
        {
            var source = TextPage(thai);
            var processor = new ScanPageProcessor(new WpfImageCodec(), tessdataFolder: DataFolder, log: output.WriteLine);
            // Independent WPF rotation checks the processor's clockwise correction, including quarter turns.
            var image = BitmapSource.Create(source.Bitmap.Width, source.Bitmap.Height, 96, 96,
                PixelFormats.Bgra32, null, source.Bitmap.Pixels, source.Bitmap.Stride);
            var rotated = new TransformedBitmap(image, new RotateTransform(rotation));
            var pixels = new byte[rotated.PixelWidth * rotated.PixelHeight * 4];
            rotated.CopyPixels(pixels, rotated.PixelWidth * 4, 0);
            var page = new ScannedPage(new(rotated.PixelWidth, rotated.PixelHeight, rotated.PixelWidth * 4, pixels), 300, 300);
            Assert.Equal((360 - rotation) % 360, processor.DetectOrientation(page));
            Assert.Equal(0, processor.DetectOrientation(page, (360 - rotation) % 360));
            return true;
        });
    }

    internal static ScannedPage TextPage(bool thai)
    {
        const int width = 2480, height = 3508;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var font = new Typeface(thai ? "Leelawadee UI" : "Arial");
            for (var line = 0; line < 36; line++)
            {
                var text = thai
                    ? "เอกสารทดสอบภาษาไทย การสแกนและหมุนหน้ากระดาษให้ถูกต้อง"
                    : "The quick brown fox jumps over the lazy dog. Scanned document testing.";
                if (thai && line % 5 == 0) text = "Thai and English document orientation testing, page number 12345.";
                var formatted = new FormattedText(text, CultureInfo.GetCultureInfo(thai ? "th-TH" : "en-US"),
                    FlowDirection.LeftToRight, font, 45, Brushes.Black, 1);
                dc.DrawText(formatted, new Point(140, 160 + line * 82));
            }
        }
        var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        render.Render(visual);
        var pixels = new byte[width * height * 4];
        render.CopyPixels(pixels, width * 4, 0);
        return new(new(width, height, width * 4, pixels), 300, 300);
    }
}

public sealed class InstalledOsdTheoryAttribute : TheoryAttribute
{
    public InstalledOsdTheoryAttribute()
    {
        if (!File.Exists(Path.Combine(ScanOrientationRealTests.DataFolder, "osd.traineddata")))
            Skip = "osd.traineddata is not installed.";
    }
}
