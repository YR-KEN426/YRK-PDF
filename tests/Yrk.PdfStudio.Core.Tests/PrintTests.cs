using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Printing;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class PrintTests : IDisposable
{
    private const double A4Width = 595.28;
    private const double A4Height = 841.89;
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");
    private static readonly PrintRect A4Printable = new(12, 12, A4Width - 24, A4Height - 24);

    private readonly PdfiumEngine _engine = new();

    public void Dispose() => _engine.Dispose();

    [Fact]
    public void Fit_fills_the_printable_area_and_centres_the_page()
    {
        var placement = PrintLayout.Place(300, 400, A4Width, A4Height, A4Printable, PrintSizing.Fit);

        Assert.False(placement.Rotated);
        Assert.Equal(Math.Min(A4Printable.Width / 300, A4Printable.Height / 400), placement.Scale, 6);
        Assert.Equal(A4Width / 2, placement.Target.X + (placement.Target.Width / 2), 6);
        Assert.Equal(A4Height / 2, placement.Target.Y + (placement.Target.Height / 2), 6);
    }

    [Fact]
    public void A_landscape_drawing_is_turned_on_portrait_paper()
    {
        // An A3 landscape drawing on A4 portrait paper: turned, then shrunk to fit.
        var placement = PrintLayout.Place(1190.55, 841.89, A4Width, A4Height, A4Printable, PrintSizing.ShrinkOversized);

        Assert.True(placement.Rotated);
        Assert.Equal(841.89 * placement.Scale, placement.Target.Width, 6);
        Assert.True(placement.Scale < 1);
        Assert.True(placement.Target.Height <= A4Printable.Height + 1e-6);
    }

    [Fact]
    public void Actual_size_keeps_the_scale_even_when_it_does_not_fit()
    {
        var placement = PrintLayout.Place(1190.55, 841.89, A4Width, A4Height, A4Printable, PrintSizing.ActualSize, autoRotate: false);

        Assert.False(placement.Rotated);
        Assert.Equal(1, placement.Scale);
        Assert.True(placement.Target.X < 0); // Wider than the paper: centred, both sides cut off.
    }

    [Fact]
    public void Small_pages_are_not_enlarged_by_shrink_oversized_but_are_by_custom()
    {
        Assert.Equal(1, PrintLayout.Place(200, 200, A4Width, A4Height, A4Printable, PrintSizing.ShrinkOversized).Scale);
        Assert.Equal(1.5, PrintLayout.Place(200, 200, A4Width, A4Height, A4Printable, PrintSizing.Custom, 1.5).Scale);
    }

    [Fact]
    public void Page_ranges_list_the_pages_in_order()
    {
        Assert.Equal([0, 1, 2], PrintLayout.Pages("", 3));
        Assert.Equal([0, 2, 3, 4], PrintLayout.Pages("1, 3-", 5));
        Assert.Throws<FormatException>(() => PrintLayout.Pages("7", 5));
    }

    [Fact]
    public async Task Print_rendering_can_leave_out_comments_and_colour()
    {
        await using var document = await _engine.OpenAsync(SamplePath);
        await document.AddAnnotationAsync(0, new PdfShapeSpec(PdfAnnotationKind.Square, new NormalizedRect(0.1, 0.1, 0.5, 0.5), 8, new PdfColor(220, 30, 30)));
        var size = document.PageSizes[0];
        int width = (int)size.Width, height = (int)size.Height;

        var withComments = await document.RenderPageForPrintAsync(0, width, height, grayscale: false, comments: true);
        var withoutComments = await document.RenderPageForPrintAsync(0, width, height, grayscale: false, comments: false);
        var grey = await document.RenderPageForPrintAsync(0, width, height, grayscale: true, comments: true);

        Assert.True(CountReddish(withComments) > 100);
        Assert.Equal(0, CountReddish(withoutComments));
        Assert.Equal(0, CountReddish(grey));
        Assert.True(CountDark(grey) > 100); // The square is still printed, in grey.
    }

    private static int CountReddish(PdfBitmap bitmap) => Count(bitmap, (b, g, r) => r > 150 && g < 100 && b < 100);

    private static int CountDark(PdfBitmap bitmap) => Count(bitmap, (b, g, r) => r < 200 && Math.Abs(r - g) < 8 && Math.Abs(g - b) < 8);

    private static int Count(PdfBitmap bitmap, Func<byte, byte, byte, bool> test)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var o = (y * bitmap.Stride) + (x * 4);
                if (test(bitmap.Pixels[o], bitmap.Pixels[o + 1], bitmap.Pixels[o + 2]))
                {
                    count++;
                }
            }
        }

        return count;
    }
}
