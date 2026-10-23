using System.Text;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class VectorArtTests : IDisposable
{
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-vector-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task Vector_art_is_drawn_as_paths_in_the_target_rectangle()
    {
        // A red square filling the left half of its 200 × 100 box.
        var art = new PdfVectorArt(200, 100,
        [
            new PdfVectorPath(
                [PdfPathCommand.Move(0, 0), PdfPathCommand.Line(100, 0), PdfPathCommand.Line(100, 100), PdfPathCommand.Line(0, 100), PdfPathCommand.Closing],
                new PdfColor(220, 20, 20)),
        ]);
        var path = Path.Combine(_folder.FullName, "vector.pdf");
        await using (var document = await _engine.OpenAsync(SamplePath))
        {
            await document.AddVectorAsync(0, art, new NormalizedRect(0.2, 0.2, 0.4, 0.1));
            await document.SaveAsync(path);
        }

        await using var saved = await _engine.OpenAsync(path);
        var size = saved.PageSizes[0];
        int width = (int)size.Width, height = (int)size.Height;
        var bitmap = await saved.RenderPageAsync(0, width, height);
        Assert.True(IsRed(bitmap, (int)(width * 0.3), (int)(height * 0.25)));  // left half of the box: red
        Assert.False(IsRed(bitmap, (int)(width * 0.5), (int)(height * 0.25))); // right half: not
    }

    [Fact]
    public async Task A_pdf_page_is_placed_as_vector_content()
    {
        var path = Path.Combine(_folder.FullName, "placed.pdf");
        var original = TestPdfFactory.CreateFormPdf(Path.Combine(_folder.FullName, "form.pdf"));
        var signature = await File.ReadAllBytesAsync(SamplePath);
        await using (var document = await _engine.OpenAsync(original))
        {
            // Only the middle of the sample page: the rest must be clipped away, not drawn around the target.
            await document.AddPdfPageAsync(0, signature, new NormalizedRect(0.1, 0.1, 0.8, 0.4), new NormalizedRect(0.5, 0.5, 0.4, 0.4));
            await document.SaveAsync(path);
        }

        await using var saved = await _engine.OpenAsync(path);
        await using var before = await _engine.OpenAsync(original);
        var size = saved.PageSizes[0];
        int width = (int)size.Width, height = (int)size.Height;
        var bitmap = await saved.RenderPageAsync(0, width, height);
        var unchanged = await before.RenderPageAsync(0, width, height);
        var outside = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var inTarget = x >= width * 0.5 - 1 && x <= width * 0.9 + 1 && y >= height * 0.5 - 1 && y <= height * 0.9 + 1;
                var o = (y * bitmap.Stride) + (x * 4);
                if (!inTarget && Math.Abs(bitmap.Pixels[o] - unchanged.Pixels[o]) > 40)
                {
                    outside++;
                }
            }
        }

        Assert.Equal(0, outside);
        var ink = 0;
        for (var y = (int)(height * 0.5); y < (int)(height * 0.9); y++)
        {
            for (var x = (int)(width * 0.5); x < (int)(width * 0.9); x++)
            {
                if (bitmap.Pixels[(y * bitmap.Stride) + (x * 4)] < 128)
                {
                    ink++;
                }
            }
        }

        Assert.True(ink > 50, $"Expected the placed page's text in the target rectangle, found {ink} dark pixels.");
        Assert.Contains("/Form", Encoding.Latin1.GetString(File.ReadAllBytes(path)));
    }

    private static bool IsRed(PdfBitmap bitmap, int x, int y)
    {
        var o = (y * bitmap.Stride) + (x * 4);
        return bitmap.Pixels[o + 2] > 180 && bitmap.Pixels[o + 1] < 80 && bitmap.Pixels[o] < 80;
    }
}
