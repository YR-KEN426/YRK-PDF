using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanAnalysisTests
{
    [Theory]
    [InlineData(160, 0)]
    [InlineData(160, 90)]
    [InlineData(1400, 0)]
    [InlineData(1400, 90)]
    public async Task Preview_preserves_full_300_dpi_fake_page_and_corner_marks(int maxSide, int rotation)
    {
        using var session = new ScanSession();
        var settings = new ScanSettings(Dpi: 300, AutoStraighten: false, AutoCrop: false);
        await session.ScanAsync(new FakeScannerService { PageCount = 1 }, FakeScannerService.Device, settings);
        var page = session.Read(Assert.Single(session.Pages));
        Assert.Equal(300, page.DpiX);
        Assert.Equal(2480, page.Bitmap.Width);
        Assert.Equal(3508, page.Bitmap.Height);

        // Distinct BGRA marks as thick as the fake scanner's half-width edge stroke.
        byte[][] colors = [[0, 0, 255, 255], [0, 255, 0, 255], [255, 0, 0, 255], [255, 0, 255, 255]];
        var bitmap = page.Bitmap;
        const int markSize = 12;
        for (var corner = 0; corner < colors.Length; corner++)
        {
            var left = corner % 2 == 0 ? 0 : bitmap.Width - markSize;
            var top = corner < 2 ? 0 : bitmap.Height - markSize;
            for (var y = top; y < top + markSize; y++)
                for (var x = left; x < left + markSize; x++)
                    colors[corner].CopyTo(bitmap.Pixels, y * bitmap.Stride + x * 4);
        }

        var preview = new ScanPageProcessor(new WpfImageCodec()).RenderPreview(page, settings, rotation, maxSide);
        var width = rotation == 0 ? bitmap.Width : bitmap.Height;
        var height = rotation == 0 ? bitmap.Height : bitmap.Width;
        Assert.Equal(maxSide, Math.Max(preview.Width, preview.Height));
        // Allow only the half-pixel rounding error in the shorter dimension.
        Assert.InRange(Math.Abs(preview.Width - preview.Height * (double)width / height), 0, 0.5);
        Assert.Equal(preview.Width * 4, preview.Stride);
        Assert.Equal(preview.Stride * preview.Height, preview.Pixels.Length);
        int[] cornerOrder = rotation == 0 ? [0, 1, 2, 3] : [2, 0, 3, 1];
        for (var corner = 0; corner < cornerOrder.Length; corner++)
        {
            var x = corner % 2 == 0 ? 0 : preview.Width - 1;
            var y = corner < 2 ? 0 : preview.Height - 1;
            Assert.Equal(colors[cornerOrder[corner]], preview.Pixels.AsSpan(y * preview.Stride + x * 4, 4).ToArray());
        }
    }

    [Theory]
    [InlineData(160, 0)]
    [InlineData(160, 90)]
    [InlineData(1400, 0)]
    [InlineData(1400, 90)]
    public async Task Full_fake_300dpi_page_preview_preserves_aspect_and_all_four_corners(int maxSide, int rotation)
    {
        using var session = new ScanSession();
        var settings = new ScanSettings(Dpi: 300, AutoStraighten: false, AutoCrop: false);
        await session.ScanAsync(new FakeScannerService { PageCount = 1 }, FakeScannerService.Device, settings);
        var page = session.Read(session.Pages[0]);
        Assert.Equal(300, page.DpiX);
        Assert.Equal(2480, page.Bitmap.Width);
        Assert.Equal(3508, page.Bitmap.Height);
        // Distinct BGRA marks, wide enough to survive thumbnail reduction.
        byte[][] colors = [[0, 0, 255, 255], [0, 255, 0, 255], [255, 0, 0, 255], [255, 0, 255, 255]];
        for (var corner = 0; corner < 4; corner++)
        {
            var left = corner % 2 == 0 ? 0 : page.Bitmap.Width - 200;
            var top = corner < 2 ? 0 : page.Bitmap.Height - 200;
            for (var y = top; y < top + 200; y++)
                for (var x = left; x < left + 200; x++)
                    colors[corner].CopyTo(page.Bitmap.Pixels, y * page.Bitmap.Stride + x * 4);
        }
        var preview = new ScanPageProcessor(new WpfImageCodec()).RenderPreview(page, settings, rotation, maxSide);
        var width = rotation == 0 ? page.Bitmap.Width : page.Bitmap.Height;
        var height = rotation == 0 ? page.Bitmap.Height : page.Bitmap.Width;
        Assert.Equal(maxSide, Math.Max(preview.Width, preview.Height));
        Assert.InRange(Math.Abs(preview.Width - preview.Height * (double)width / height), 0, 1);
        // A clockwise rotation maps TL/TR/BL/BR to BL/TL/BR/TR in the source.
        int[] order = rotation == 0 ? [0, 1, 2, 3] : [2, 0, 3, 1];
        for (var corner = 0; corner < 4; corner++)
        {
            var x = corner % 2 == 0 ? 1 : preview.Width - 2;
            var y = corner < 2 ? 1 : preview.Height - 2;
            Assert.Equal(colors[order[corner]], preview.Pixels.AsSpan(y * preview.Stride + x * 4, 4).ToArray());
        }
    }

    [Theory]
    [InlineData(160)]
    [InlineData(1400)]
    public async Task Fake_page_preview_keeps_thin_right_and_bottom_scanner_borders(int maxSide)
    {
        using var session = new ScanSession();
        var settings = new ScanSettings(Dpi: 300, AutoStraighten: false, AutoCrop: false);
        await session.ScanAsync(new FakeScannerService(), FakeScannerService.Device, settings);
        // The third fake page is blank apart from the thin scanner border.
        var page = session.Read(session.Pages[2]);
        var preview = new ScanPageProcessor(new WpfImageCodec()).RenderPreview(page, settings, 0, maxSide);
        Assert.True(preview.Pixels[(preview.Height / 2) * preview.Stride + (preview.Width - 1) * 4] < 250,
            "The right border disappeared during reduction.");
        Assert.True(preview.Pixels[(preview.Height - 1) * preview.Stride + (preview.Width / 2) * 4] < 250,
            "The bottom border disappeared during reduction.");
    }

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
