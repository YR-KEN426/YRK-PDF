using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Forms;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>PDFium's incremental save rewrites every loaded object; the trimmed save keeps only real changes.</summary>
public sealed class SaveTrimTests : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-trim-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task A_filled_form_saves_only_what_changed_and_reads_the_same()
    {
        var source = TestPdfFactory.CreateReportPdf(Path.Combine(_folder.FullName, "report.pdf"));
        var formPath = Path.Combine(_folder.FullName, "form.pdf");
        File.WriteAllBytes(formPath, PdfFormBuilder.AddFields(File.ReadAllBytes(source),
        [
            new(0, new(0.1, 0.30, 0.6, 0.03), PdfNewFieldKind.Text, "Name"),
            new(0, new(0.1, 0.40, 0.6, 0.03), PdfNewFieldKind.Text, "City"),
            new(0, new(0.1, 0.50, 0.025, 0.018), PdfNewFieldKind.CheckBox, "Agree"),
        ]));

        // Fill in one field and tick the box, after rendering (rendering loads fonts PDFium then writes again).
        var savedPath = Path.Combine(_folder.FullName, "saved.pdf");
        await using (var document = await _engine.OpenAsync(formPath))
        {
            var size = document.PageSizes[0];
            await document.RenderPageAsync(0, (int)size.Width, (int)size.Height);
            var fields = await document.GetFormFieldsAsync(0);
            await document.ApplyFormEditsAsync(
            [
                new PdfFormFieldEdit(0, fields.Single(f => f.Name == "Name").AnnotIndex, Text: "สมชาย Somchai"),
                new PdfFormFieldEdit(0, fields.Single(f => f.Name == "Agree").AnnotIndex, IsChecked: true),
            ]);
            await document.SaveAsync(savedPath);
        }

        var original = File.ReadAllBytes(formPath);
        var saved = File.ReadAllBytes(savedPath);
        var trimmed = PdfFiles.TrimIncrementalSave(original, saved);
        Assert.True(trimmed.Length <= saved.Length);
        Assert.Equal(original, trimmed.AsSpan(0, original.Length).ToArray()); // Still an update of the original bytes.

        var trimmedPath = Path.Combine(_folder.FullName, "trimmed.pdf");
        File.WriteAllBytes(trimmedPath, trimmed);
        Assert.Equal(await Describe(savedPath), await Describe(trimmedPath));
    }

    [Fact]
    public void Bytes_that_are_not_an_update_of_the_original_are_left_alone()
    {
        var source = TestPdfFactory.CreateReportPdf(Path.Combine(_folder.FullName, "report.pdf"));
        var bytes = File.ReadAllBytes(source);
        var other = PdfFiles.Compact(bytes);

        Assert.Same(other, PdfFiles.TrimIncrementalSave(bytes, other));
    }

    /// <summary>Field values and the rendered first page, to compare two files.</summary>
    private async Task<string> Describe(string path)
    {
        await using var document = await _engine.OpenAsync(path);
        var fields = await document.GetFormFieldsAsync(0);
        var size = document.PageSizes[0];
        var bitmap = await document.RenderPageAsync(0, (int)size.Width, (int)size.Height);
        var pixels = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bitmap.Pixels));
        return string.Join('\n', fields.Select(f => $"{f.Name}={f.Value}/{f.IsChecked}")) + "\n" + pixels;
    }
}
