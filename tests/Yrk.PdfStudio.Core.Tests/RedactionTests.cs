using System.Text;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Redaction;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class RedactionTests : IDisposable
{
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");

    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-redact-test-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task Removes_the_word_and_keeps_the_rest_in_place()
    {
        NormalizedRect area;
        NormalizedRect before;
        await using (var document = await _engine.OpenAsync(SamplePath))
        {
            area = Union((await document.FindTextAsync(0, "sample document", PdfSearchOptions.None))[0].Rects);
            before = Union((await document.FindTextAsync(0, "appears", PdfSearchOptions.None))[0].Rects);
        }

        var result = await PdfRedactor.RedactAsync(File.ReadAllBytes(SamplePath), [new PdfRedactionArea(0, area)], _engine);

        Assert.Empty(result.ImagePages);
        await using var redacted = await OpenAsync(result.Output);
        var text = await redacted.GetPageTextAsync(0);
        Assert.DoesNotContain("sample document", text, StringComparison.Ordinal);
        Assert.Contains("This", text, StringComparison.Ordinal);
        Assert.Contains("appears", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, (await redacted.GetTextInAreaAsync(0, area)).Trim());

        // Text after the removed words did not move.
        var after = Union((await redacted.FindTextAsync(0, "appears", PdfSearchOptions.None))[0].Rects);
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);

        // One clean revision: nothing of the old content is left in the file.
        var cos = CosDocument.Load(result.Output);
        Assert.Null(cos.Trailer["Prev"]);
        Assert.Equal(1, Encoding.Latin1.GetString(result.Output).Split("%%EOF").Length - 1);
    }

    [Fact]
    public async Task Removes_Thai_text()
    {
        NormalizedRect area;
        await using (var document = await _engine.OpenAsync(SamplePath))
        {
            var text = await document.GetPageTextAsync(0);
            Assert.Contains("ภาษาไทย", text, StringComparison.Ordinal);
            area = Union((await document.FindTextAsync(0, "ภาษาไทย", PdfSearchOptions.None))[0].Rects);
        }

        var result = await PdfRedactor.RedactAsync(File.ReadAllBytes(SamplePath), [new PdfRedactionArea(0, area)], _engine);

        // The page has "ภาษาไทย" twice; only the first one was marked.
        await using var redacted = await OpenAsync(result.Output);
        Assert.Equal(string.Empty, (await redacted.GetTextInAreaAsync(0, area)).Trim());
        var remaining = await redacted.GetPageTextAsync(0);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(remaining, "ภาษาไทย"));
        Assert.Contains("invoice", remaining, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turns_a_page_into_an_image_when_an_image_is_under_the_area()
    {
        var images = Path.Combine(_folder.FullName, "image.pdf");
        var pixels = new byte[200 * 100 * 4];
        Array.Fill(pixels, (byte)200);
        await _engine.CreateFromImagesAsync([new PdfBitmapInput(new PdfImage(200, 100, pixels))], new ImagesToPdfOptions(), images);

        var area = new NormalizedRect(0.3, 0.3, 0.2, 0.1);
        var result = await PdfRedactor.RedactAsync(File.ReadAllBytes(images), [new PdfRedactionArea(0, area)], _engine);

        Assert.Equal([0], result.ImagePages);
        await using var redacted = await OpenAsync(result.Output);
        var bitmap = await redacted.RenderPageAsync(0, 400, 566);
        var centre = ((int)((area.Y + (area.Height / 2)) * bitmap.Height) * bitmap.Stride) + ((int)((area.X + (area.Width / 2)) * bitmap.Width) * 4);
        Assert.True(bitmap.Pixels[centre] < 30 && bitmap.Pixels[centre + 1] < 30, "The area is not black.");
    }

    [Fact]
    public async Task Removes_form_fields_and_their_values()
    {
        var form = TestPdfFactory.CreateFormPdf(Path.Combine(_folder.FullName, "form.pdf"));
        NormalizedRect fieldBounds;
        await using (var document = await _engine.OpenAsync(form))
        {
            fieldBounds = (await document.GetFormFieldsAsync(0)).Single(f => f.Name == "name").Bounds;
        }

        var result = await PdfRedactor.RedactAsync(File.ReadAllBytes(form), [new PdfRedactionArea(0, fieldBounds)], _engine);

        await using var redacted = await OpenAsync(result.Output);
        var fields = await redacted.GetFormFieldsAsync(0);
        Assert.DoesNotContain(fields, f => f.Name == "name");
        Assert.Contains(fields, f => f.Name == "province");
        Assert.DoesNotContain("(John)", Encoding.Latin1.GetString(result.Output), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Works_on_rotated_pages(int quarterTurns)
    {
        var rotated = Path.Combine(_folder.FullName, $"rotated-{quarterTurns}.pdf");
        NormalizedRect area;
        await using (var document = await _engine.OpenAsync(SamplePath))
        {
            await document.RotatePagesAsync([0], quarterTurns);
            await document.SaveAsync(rotated);
        }

        await using (var document = await _engine.OpenAsync(rotated))
        {
            area = Union((await document.FindTextAsync(0, "invoice", PdfSearchOptions.None))[0].Rects);
        }

        var result = await PdfRedactor.RedactAsync(File.ReadAllBytes(rotated), [new PdfRedactionArea(0, area)], _engine);

        Assert.Empty(result.ImagePages);
        await using var redacted = await OpenAsync(result.Output);
        Assert.Equal(string.Empty, (await redacted.GetTextInAreaAsync(0, area)).Trim());
        Assert.Contains("appears", await redacted.GetPageTextAsync(0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removes_an_ocr_text_layer_with_the_scan()
    {
        // A scan with an invisible text layer: the image forces the page to become an image, which drops the layer.
        var images = Path.Combine(_folder.FullName, "scan.pdf");
        var pixels = new byte[100 * 100 * 4];
        Array.Fill(pixels, (byte)255);
        await _engine.CreateFromImagesAsync([new PdfBitmapInput(new PdfImage(100, 100, pixels))], new ImagesToPdfOptions(), images);
        var layered = Pdf.Ocr.PdfOcr.AddTextLayer(File.ReadAllBytes(images), [new Pdf.Ocr.OcrPageResult(0, [new Pdf.Ocr.OcrWord("Secret", new NormalizedRect(0.2, 0.2, 0.3, 0.05), 90, false)])]);

        var result = await PdfRedactor.RedactAsync(layered, [new PdfRedactionArea(0, new NormalizedRect(0.15, 0.15, 0.4, 0.15))], _engine);

        await using var redacted = await OpenAsync(result.Output);
        Assert.DoesNotContain("Secret", await redacted.GetPageTextAsync(0), StringComparison.Ordinal);
    }

    private async Task<IPdfDocument> OpenAsync(byte[] bytes)
    {
        var path = Path.Combine(_folder.FullName, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, bytes);
        return await _engine.OpenAsync(path);
    }

    private static NormalizedRect Union(IReadOnlyList<NormalizedRect> rects)
    {
        var x0 = rects.Min(r => r.X);
        var y0 = rects.Min(r => r.Y);
        var x1 = rects.Max(r => r.X + r.Width);
        var y1 = rects.Max(r => r.Y + r.Height);
        return new NormalizedRect(x0, y0, x1 - x0, y1 - y0);
    }
}
