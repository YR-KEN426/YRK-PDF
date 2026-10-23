using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class PdfEditingTests : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-edit-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task Reads_form_fields()
    {
        await using var document = await _engine.OpenAsync(TestPdfFactory.CreateFormPdf(PathFor("form.pdf")));

        var fields = await document.GetFormFieldsAsync(0);

        Assert.True(document.HasForm);
        var name = Assert.Single(fields, f => f.Name == "name");
        Assert.Equal(PdfFormFieldType.Text, name.Type);
        Assert.Equal("John", name.Value);
        Assert.InRange(name.Bounds.X, 0.12, 0.122);             // 72 / 595
        Assert.InRange(name.Bounds.Y, 0.139, 0.141);            // (842 - 724) / 842
        Assert.Equal(12, name.FontSize);

        var agree = Assert.Single(fields, f => f.Name == "agree");
        Assert.Equal(PdfFormFieldType.CheckBox, agree.Type);
        Assert.False(agree.IsChecked);

        var province = Assert.Single(fields, f => f.Name == "province");
        Assert.Equal(PdfFormFieldType.ComboBox, province.Type);
        Assert.Equal(["Bangkok", "Chiang Mai", "Phuket"], province.Options);
        Assert.Equal(0, province.SelectedIndex);

        Assert.True(Assert.Single(fields, f => f.Name == "id").IsReadOnly);
    }

    [Fact]
    public async Task Saves_form_values_incrementally()
    {
        var source = TestPdfFactory.CreateFormPdf(PathFor("form.pdf"));
        var target = PathFor("filled.pdf");

        await using (var document = await _engine.OpenAsync(source))
        {
            var fields = await document.GetFormFieldsAsync(0);
            int IndexOf(string name) => fields.Single(f => f.Name == name).AnnotIndex;

            await document.ApplyFormEditsAsync(
            [
                new PdfFormFieldEdit(0, IndexOf("name"), Text: "สมชาย ใจดี"),
                new PdfFormFieldEdit(0, IndexOf("agree"), IsChecked: true),
                new PdfFormFieldEdit(0, IndexOf("province"), SelectedIndex: 2),
                new PdfFormFieldEdit(0, IndexOf("id"), Text: "changed"),
            ]);
            await document.SaveAsync(target);
        }

        // Incremental update: the original file is an exact prefix of the saved one.
        var original = await File.ReadAllBytesAsync(source);
        var saved = await File.ReadAllBytesAsync(target);
        Assert.True(saved.Length > original.Length);
        Assert.Equal(original, saved[..original.Length]);

        await using var reopened = await _engine.OpenAsync(target);
        var result = await reopened.GetFormFieldsAsync(0);
        Assert.Equal("สมชาย ใจดี", result.Single(f => f.Name == "name").Value);
        Assert.True(result.Single(f => f.Name == "agree").IsChecked);
        Assert.Equal(2, result.Single(f => f.Name == "province").SelectedIndex);
        Assert.Equal("A-001", result.Single(f => f.Name == "id").Value); // read-only fields are not changed
    }

    [Fact]
    public async Task Adds_transparent_image_to_page()
    {
        var source = TestPdfFactory.CreateFormPdf(PathFor("form.pdf"));
        var target = PathFor("signed.pdf");

        // 40×20 image: transparent everywhere except a solid red block in the middle.
        var image = new PdfImage(40, 20, new byte[40 * 20 * 4]);
        for (var y = 5; y < 15; y++)
        {
            for (var x = 10; x < 30; x++)
            {
                var i = ((y * 40) + x) * 4;
                image.Pixels[i + 2] = 255; // R (BGRA)
                image.Pixels[i + 3] = 255; // A
            }
        }

        await using (var document = await _engine.OpenAsync(source))
        {
            // Lower half of the page, away from the form fields.
            await document.AddImageAsync(0, image, new NormalizedRect(0.25, 0.5, 0.5, 0.25));
            await document.SaveAsync(target);
        }

        await using var reopened = await _engine.OpenAsync(target);
        var bitmap = await reopened.RenderPageAsync(0, 200, 283);

        (byte B, byte G, byte R) Pixel(double nx, double ny)
        {
            var i = ((int)(ny * bitmap.Height) * bitmap.Stride) + ((int)(nx * bitmap.Width) * 4);
            return (bitmap.Pixels[i], bitmap.Pixels[i + 1], bitmap.Pixels[i + 2]);
        }

        Assert.Equal((0, 0, 255), Pixel(0.5, 0.625));     // center of the image: red
        Assert.Equal((255, 255, 255), Pixel(0.27, 0.52)); // transparent corner: page stays white
        Assert.Equal((255, 255, 255), Pixel(0.5, 0.95));  // outside the image
    }

    [Fact]
    public async Task Refuses_to_overwrite_the_open_file()
    {
        var source = TestPdfFactory.CreateFormPdf(PathFor("form.pdf"));
        await using var document = await _engine.OpenAsync(source);

        await Assert.ThrowsAsync<ArgumentException>(() => document.SaveAsync(source));
    }

    private string PathFor(string name) => Path.Combine(_folder.FullName, name);
}
