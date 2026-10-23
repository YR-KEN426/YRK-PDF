using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Ocr;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>
/// Needs the Tesseract language files (tha, eng) in %LOCALAPPDATA%\YRK\PdfStudio\tessdata, which the app
/// downloads on first use. Without them these tests pass without checking anything.
/// </summary>
public sealed class OcrTests : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-ocr-");
    private readonly OcrLanguageData _data = new();

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    private bool HasData => _data.IsInstalled("tha") && _data.IsInstalled("eng");

    [Fact]
    public async Task Makes_a_scanned_page_searchable()
    {
        if (!HasData)
        {
            return;
        }

        var scan = await CreateScanAsync(["Invoice number 12345", "Total amount 9,870 Baht", "สัญญาเช่าบ้าน กรุงเทพมหานคร"], quarterTurns: 0);
        await using var document = await _engine.OpenAsync(scan);
        Assert.Equal(string.Empty, (await document.GetPageTextAsync(0)).Trim());

        var summary = await PdfOcr.RecognizeAsync(document, File.ReadAllBytes(scan), new OcrOptions(["tha", "eng"]), _data);
        Assert.Equal(1, summary.PagesRecognised);
        Assert.True(summary.Words >= 6, $"Only {summary.Words} words were recognised.");

        var output = Path.Combine(_folder.FullName, "searchable.pdf");
        File.WriteAllBytes(output, summary.Output);
        await using var searchable = await _engine.OpenAsync(output);
        var text = await searchable.GetPageTextAsync(0);
        Assert.Contains("Invoice", text, StringComparison.Ordinal);
        Assert.Contains("12345", text, StringComparison.Ordinal);
        Assert.Contains("สัญญาเช่าบ้าน", text, StringComparison.Ordinal);
        Assert.Contains("กรุงเทพมหานคร", text, StringComparison.Ordinal);

        // The word is found where it is printed: near the top-left of the page.
        var match = Assert.Single(await searchable.FindTextAsync(0, "Invoice", PdfSearchOptions.None));
        var box = match.Rects[0];
        Assert.InRange(box.X, 0.05, 0.3);
        Assert.InRange(box.Y, 0.03, 0.2);
    }

    [Fact]
    public async Task Places_text_correctly_on_rotated_pages()
    {
        if (!HasData)
        {
            return;
        }

        var scan = await CreateScanAsync(["Invoice number 12345"], quarterTurns: 1);
        await using var document = await _engine.OpenAsync(scan);
        var summary = await PdfOcr.RecognizeAsync(document, File.ReadAllBytes(scan), new OcrOptions(["eng"]), _data);

        var output = Path.Combine(_folder.FullName, "rotated-searchable.pdf");
        File.WriteAllBytes(output, summary.Output);
        await using var searchable = await _engine.OpenAsync(output);
        var match = Assert.Single(await searchable.FindTextAsync(0, "Invoice", PdfSearchOptions.None));
        Assert.InRange(match.Rects[0].X, 0.05, 0.3);
        Assert.InRange(match.Rects[0].Y, 0.03, 0.2);
    }

    [Fact]
    public async Task Skips_pages_that_already_have_text()
    {
        if (!HasData)
        {
            return;
        }

        var sample = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");
        await using var document = await _engine.OpenAsync(sample);
        var summary = await PdfOcr.RecognizeAsync(document, File.ReadAllBytes(sample), new OcrOptions(["eng"]), _data);

        Assert.Equal(0, summary.PagesRecognised);
        Assert.Equal(File.ReadAllBytes(sample), summary.Output);
    }

    /// <summary>A one-page "scan": the lines drawn as a 300 dpi image, then optionally rotated with /Rotate.</summary>
    private async Task<string> CreateScanAsync(IReadOnlyList<string> lines, int quarterTurns)
    {
        // For a rotated page the picture is stored turned the other way, as scanners do for landscape
        // pages, and /Rotate turns it upright for display.
        var image = RunSta(() => RenderLines(lines, quarterTurns));
        var path = Path.Combine(_folder.FullName, $"scan-{quarterTurns}.pdf");
        await _engine.CreateFromImagesAsync([new PdfBitmapInput(image)], new ImagesToPdfOptions(PdfPageSizeMode.A4, 0, 300), path);
        if (quarterTurns == 0)
        {
            return path;
        }

        var rotated = Path.Combine(_folder.FullName, $"scan-{quarterTurns}-rotated.pdf");
        await using (var document = await _engine.OpenAsync(path))
        {
            await document.RotatePagesAsync([0], quarterTurns);
            await document.SaveAsync(rotated);
        }

        return rotated;
    }

    /// <summary>Black text on white, A4 at 300 dpi; turned counter-clockwise by <paramref name="counterTurns"/> quarter turns.</summary>
    private static PdfImage RenderLines(IReadOnlyList<string> lines, int counterTurns = 0)
    {
        const int width = 2480, height = 3508;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            if (counterTurns % 2 == 1)
            {
                // Draw upright in a landscape frame, then turn it into the portrait bitmap.
                context.PushTransform(new MatrixTransform(0, -1, 1, 0, 0, height));
            }

            var y = 300.0;
            foreach (var line in lines)
            {
                var text = new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Leelawadee UI"), 60, Brushes.Black, 1.0);
                context.DrawText(text, new Point(300, y));
                y += 120;
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return new PdfImage(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    private static T RunSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return error is null ? result : throw new InvalidOperationException("Rendering failed.", error);
    }
}
