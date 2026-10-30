using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanCleanupTests : IDisposable
{
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task Finds_a_tilt_and_straightens_it()
    {
        await using var document = await _engine.OpenAsync(SamplePath);
        Assert.Equal(0, (await CheckAsync(document, 0)).Straighten);

        await document.StraightenPageAsync(0, 3); // As if the page went in turned 3° counter-clockwise.
        var tilted = await CheckAsync(document, 0);
        Assert.InRange(tilted.Straighten, -3.3, -2.7);
        Assert.False(tilted.IsBlank);

        await document.StraightenPageAsync(0, tilted.Straighten);
        Assert.InRange((await CheckAsync(document, 0)).Straighten, -0.2, 0.2);
        Assert.Contains("invoice", await document.GetPageTextAsync(0)); // Still text, only turned.
    }

    [Fact]
    public void A_page_with_only_specks_and_show_through_is_blank()
    {
        var bitmap = Page(400, 560, (x, y) => (x * 7 + y * 13) % 997 == 0 ? (byte)20 : y is > 200 and < 210 ? (byte)200 : (byte)255);
        Assert.True(ScanCleanup.Check(bitmap).IsBlank);

        var written = Page(400, 560, (x, y) => y is > 100 and < 106 && x is > 60 and < 340 ? (byte)0 : (byte)255);
        Assert.False(ScanCleanup.Check(written).IsBlank);
    }

    [Fact]
    public void Finds_the_dark_edges_a_scanner_lid_leaves()
    {
        var bitmap = Page(400, 500, (x, y) => x < 20 || y < 10 ? (byte)15 : (byte)250);

        var keep = ScanCleanup.Check(bitmap).Keep;

        Assert.NotNull(keep);
        Assert.InRange(keep.Value.X, 0.05, 0.06);
        Assert.InRange(keep.Value.Y, 0.02, 0.025);
        Assert.InRange(keep.Value.X + keep.Value.Width, 0.99, 1.0);
        Assert.Null(ScanCleanup.Check(Page(400, 500, (_, _) => 250)).Keep);
    }

    [Fact]
    public async Task Crops_a_page()
    {
        await using var document = await _engine.OpenAsync(SamplePath);
        var before = document.PageSizes[0];

        await document.CropPageAsync(0, new NormalizedRect(0.1, 0.1, 0.8, 0.5));

        Assert.Equal(before.Width * 0.8, document.PageSizes[0].Width, 0);
        Assert.Equal(before.Height * 0.5, document.PageSizes[0].Height, 0);
    }

    private static async Task<ScanPageCheck> CheckAsync(IPdfDocument document, int page)
    {
        var size = document.PageSizes[page];
        var bitmap = await document.RenderPageAsync(page, (int)(size.Width / 72 * ScanCleanup.Dpi), (int)(size.Height / 72 * ScanCleanup.Dpi));
        return ScanCleanup.Check(bitmap);
    }

    private static PdfBitmap Page(int width, int height, Func<int, int, byte> level)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = level(x, y);
                var i = ((y * width) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (v, v, v, 255);
            }
        }

        return new PdfBitmap(width, height, width * 4, pixels);
    }
}
