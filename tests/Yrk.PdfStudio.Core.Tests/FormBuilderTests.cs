using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Forms;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class FormBuilderTests : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-forms-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    private static readonly NormalizedRect NameBox = new(0.1, 0.40, 0.4, 0.03);

    private static IReadOnlyList<PdfNewField> AllKinds() =>
    [
        new(0, NameBox, PdfNewFieldKind.Text, "ชื่อ-นามสกุล", Required: true),
        new(0, new(0.1, 0.45, 0.6, 0.08), PdfNewFieldKind.MultilineText, "Address"),
        new(0, new(0.1, 0.55, 0.025, 0.018), PdfNewFieldKind.CheckBox, "Agree"),
        new(0, new(0.1, 0.60, 0.3, 0.03), PdfNewFieldKind.Dropdown, "จังหวัด", ["กรุงเทพมหานคร", "เชียงใหม่", "ภูเก็ต"]),
        new(0, new(0.5, 0.70, 0.35, 0.07), PdfNewFieldKind.Signature, "Signature"),
    ];

    private string Build()
    {
        var source = TestPdfFactory.CreateReportPdf(Path.Combine(_folder.FullName, "report.pdf"));
        var output = Path.Combine(_folder.FullName, "form.pdf");
        File.WriteAllBytes(output, PdfFormBuilder.AddFields(File.ReadAllBytes(source), AllKinds()));
        return output;
    }

    [Fact]
    public async Task Added_fields_are_real_form_fields_of_the_right_kind()
    {
        var path = Build();
        await using var document = await _engine.OpenAsync(path);

        Assert.True(document.HasForm);
        var fields = await document.GetFormFieldsAsync(0);
        Assert.Equal(PdfFormFieldType.Text, fields.Single(f => f.Name == "ชื่อ-นามสกุล").Type);
        Assert.Equal(PdfFormFieldType.Text, fields.Single(f => f.Name == "Address").Type);
        Assert.Equal(PdfFormFieldType.CheckBox, fields.Single(f => f.Name == "Agree").Type);
        var province = fields.Single(f => f.Name == "จังหวัด");
        Assert.Equal(PdfFormFieldType.ComboBox, province.Type);
        Assert.Equal(["กรุงเทพมหานคร", "เชียงใหม่", "ภูเก็ต"], province.Options);
        Assert.Equal(PdfFormFieldType.Signature, fields.Single(f => f.Name == "Signature").Type);

        // Where it was put (relative to the page, top-left origin).
        var name = fields.Single(f => f.Name == "ชื่อ-นามสกุล").Bounds;
        Assert.Equal(NameBox.X, name.X, 2);
        Assert.Equal(NameBox.Y, name.Y, 2);
        Assert.Equal(NameBox.Width, name.Width, 2);
    }

    [Fact]
    public async Task Added_as_an_update_that_keeps_the_original_bytes()
    {
        var source = TestPdfFactory.CreateReportPdf(Path.Combine(_folder.FullName, "report.pdf"));
        var original = File.ReadAllBytes(source);
        var result = PdfFormBuilder.AddFields(original, AllKinds());

        Assert.Equal(original, result[..original.Length]); // So digital signatures already in the file stay valid.
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_new_fields_can_be_filled_in_Thai_and_show_on_the_page()
    {
        var path = Build();
        var filled = Path.Combine(_folder.FullName, "filled.pdf");
        await using (var document = await _engine.OpenAsync(path))
        {
            var fields = await document.GetFormFieldsAsync(0);
            int IndexOf(string name) => fields.Single(f => f.Name == name).AnnotIndex;
            await document.ApplyFormEditsAsync(
            [
                new PdfFormFieldEdit(0, IndexOf("ชื่อ-นามสกุล"), Text: "สมชาย ใจดี"),
                new PdfFormFieldEdit(0, IndexOf("Agree"), IsChecked: true),
                new PdfFormFieldEdit(0, IndexOf("จังหวัด"), SelectedIndex: 1),
            ]);
            await document.SaveAsync(filled);
        }

        await using var reopened = await _engine.OpenAsync(filled);
        var result = await reopened.GetFormFieldsAsync(0);
        Assert.Equal("สมชาย ใจดี", result.Single(f => f.Name == "ชื่อ-นามสกุล").Value);
        Assert.True(result.Single(f => f.Name == "Agree").IsChecked);
        Assert.Equal(1, result.Single(f => f.Name == "จังหวัด").SelectedIndex);

        // The typed text is drawn inside the box (dark pixels beyond the thin frame).
        var size = reopened.PageSizes[0];
        var bitmap = await reopened.RenderPageAsync(0, (int)(size.Width * 2), (int)(size.Height * 2));
        var dark = 0;
        for (var y = (int)((NameBox.Y + 0.004) * bitmap.Height); y < (int)((NameBox.Y + NameBox.Height - 0.004) * bitmap.Height); y++)
        {
            for (var x = (int)((NameBox.X + 0.01) * bitmap.Width); x < (int)((NameBox.X + 0.2) * bitmap.Width); x++)
            {
                var i = (y * bitmap.Stride) + (x * 4);
                dark += bitmap.Pixels[i] < 100 && bitmap.Pixels[i + 1] < 100 && bitmap.Pixels[i + 2] < 100 ? 1 : 0;
            }
        }

        Assert.True(dark > 50, $"The Thai text is not drawn in the field ({dark} dark pixels).");
    }

    [Fact]
    public async Task Saved_fields_can_be_renamed_changed_and_deleted()
    {
        var path = Build();
        var original = File.ReadAllBytes(path);
        var changed = PdfFormBuilder.ChangeFields(original,
        [
            new PdfFieldChange("Address", Delete: true),
            new PdfFieldChange("Signature", Delete: true),
            new PdfFieldChange("ชื่อ-นามสกุล", NewName: "Full name", Required: false),
            new PdfFieldChange("จังหวัด", Options: ["ขอนแก่น", "ภูเก็ต"]),
        ]);
        Assert.Equal(original, changed[..original.Length]); // Still an incremental update.

        var output = Path.Combine(_folder.FullName, "changed.pdf");
        File.WriteAllBytes(output, changed);
        await using var document = await _engine.OpenAsync(output);
        var fields = await document.GetFormFieldsAsync(0);
        Assert.Equal(["Agree", "Full name", "จังหวัด"], fields.Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["ขอนแก่น", "ภูเก็ต"], fields.Single(f => f.Name == "จังหวัด").Options);

        // The deleted ones are gone from the form too, not only from the page.
        var cos = Pdf.Cos.CosDocument.Load(changed);
        var form = (Pdf.Cos.CosDictionary)cos.Resolve(cos.Catalog["AcroForm"])!;
        Assert.Equal(3, ((Pdf.Cos.CosArray)cos.Resolve(form["Fields"])!).Count);

        Assert.Throws<PdfException>(() => PdfFormBuilder.ChangeFields(original, [new PdfFieldChange("No such field", Delete: true)]));
    }

    [Fact]
    public async Task A_name_already_used_gets_a_number()
    {
        var path = Build();
        var again = PdfFormBuilder.AddFields(File.ReadAllBytes(path), [new(0, new(0.1, 0.8, 0.3, 0.03), PdfNewFieldKind.Text, "Address")]);
        var output = Path.Combine(_folder.FullName, "again.pdf");
        File.WriteAllBytes(output, again);

        await using var document = await _engine.OpenAsync(output);
        var fields = await document.GetFormFieldsAsync(0);
        Assert.Contains(fields, f => f.Name == "Address 2");
        Assert.Equal(6, fields.Count);
    }
}
