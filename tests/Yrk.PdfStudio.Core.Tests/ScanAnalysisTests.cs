using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanAnalysisTests
{
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 90)]
    public void Analysis_matches_processing_and_preview_is_bounded(bool blank, int rotation)
    {
        var page = ScanSessionTests.Page(300, 400);
        if (!blank)
            for (var y = 80; y < 320; y += 12)
                for (var x = 50; x < 250; x++)
                    page.Bitmap.Pixels.AsSpan(y * page.Bitmap.Stride + x * 4, 3).Clear();
        var processor = new ScanPageProcessor(new WpfImageCodec());
        var settings = new ScanSettings();
        var analysis = processor.Analyze(page, settings, rotation);
        Assert.Equal(processor.Process(page, settings, rotation).IsBlank, analysis.IsBlank);
        var preview = processor.RenderPreview(page, settings, rotation, 100);
        Assert.InRange(preview.Width, 1, 100);
        Assert.InRange(preview.Height, 1, 100);
    }
}
