using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class PageMarksTests : IDisposable
{
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");
    private static readonly DateTime Today = new(2026, 10, 19);
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-marks-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public void Page_numbers_and_placeholders_are_filled_in()
    {
        var settings = new PageMarkSettings { FooterCenter = "หน้า {page} จาก {pages}", HeaderRight = "{file}", FirstNumber = 5 };
        var marks = Sta.Run(() => PageMarks.Layout(settings, 1, 3, 595, 842, @"C:\docs\Contract.pdf", Today));

        Assert.Contains(marks, m => m.Text == "หน้า 6 จาก 7");
        var file = Assert.Single(marks, m => m.Text == "Contract");
        Assert.True(file.Left + file.Line.Width <= 595 - (12 * 72 / 25.4) + 0.01); // Right-aligned inside the margin.
        Assert.True(file.Top < 50);                                                // Header at the top.
    }

    [Fact]
    public void A_fitted_watermark_stays_inside_the_page_and_is_centred()
    {
        var settings = new PageMarkSettings { Watermark = "CONFIDENTIAL", WatermarkAngle = 45 };
        var mark = Assert.Single(Sta.Run(() => PageMarks.Layout(settings, 0, 1, 595, 842, "a.pdf", Today)));

        var a = Math.PI / 4;
        double w = mark.Line.Width, h = mark.Line.Height;
        var centreX = mark.Left + (w / 2 * Math.Cos(a)) + (h / 2 * Math.Sin(a));
        var centreY = mark.Top - (w / 2 * Math.Sin(a)) + (h / 2 * Math.Cos(a));
        Assert.Equal(595 / 2.0, centreX, 3);
        Assert.Equal(842 / 2.0, centreY, 3);
        Assert.True((w * Math.Cos(a)) + (h * Math.Sin(a)) <= 595 * 0.8 + 0.01);
    }

    [Fact]
    public async Task Marks_are_written_as_text_with_a_see_through_watermark()
    {
        var settings = new PageMarkSettings
        {
            Watermark = "DRAFT",
            WatermarkAngle = 0,
            WatermarkColor = new PdfColor(220, 0, 0),
            WatermarkOpacity = 0.25,
            FooterCenter = "Page {page} of {pages}",
            SkipFirstPage = true,
        };
        var input = await File.ReadAllBytesAsync(SamplePath);
        IReadOnlyList<PdfPageSize> sizes;
        await using (var original = await _engine.OpenAsync(SamplePath))
        {
            sizes = original.PageSizes;
        }

        var blocks = Sta.Run(() => PageMarks.ToBlocks(settings, sizes, [0, 1, 2], "sample.pdf", Today));
        var path = Path.Combine(_folder.FullName, "marked.pdf");
        await File.WriteAllBytesAsync(path, PdfTextWriter.AddText(input, blocks));

        await using var document = await _engine.OpenAsync(path);
        Assert.DoesNotContain("Page 1 of 3", await document.GetPageTextAsync(0)); // First page skipped.
        var second = await document.GetPageTextAsync(1);
        Assert.Contains("Page 2 of 3", second);
        Assert.Contains("DRAFT", second);

        // The watermark is light red, not solid: 25 % red over white paper.
        var size = document.PageSizes[1];
        var bitmap = await document.RenderPageAsync(1, (int)size.Width, (int)size.Height);
        var solid = 0;
        var light = 0;
        for (var i = 0; i < bitmap.Pixels.Length; i += 4)
        {
            byte b = bitmap.Pixels[i], g = bitmap.Pixels[i + 1], r = bitmap.Pixels[i + 2];
            if (r > 200 && g < 60 && b < 60)
            {
                solid++;
            }
            else if (r > 230 && g is > 150 and < 225 && b is > 150 and < 225)
            {
                light++;
            }
        }

        Assert.Equal(0, solid);
        Assert.True(light > 200, $"Expected see-through red pixels, found {light}.");
    }
}
