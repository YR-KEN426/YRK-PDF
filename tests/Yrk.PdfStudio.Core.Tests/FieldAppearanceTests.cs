using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Forms;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>Filled-in fields drawn by the app: Thai text in the chosen font and size, and pictures in image fields.</summary>
public sealed class FieldAppearanceTests : IDisposable
{
    private static readonly NormalizedRect NameBox = new(0.1, 0.30, 0.6, 0.03);
    private static readonly NormalizedRect PictureBox = new(0.1, 0.50, 0.3, 0.2);

    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-looks-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task Text_and_pictures_are_drawn_into_the_saved_fields()
    {
        var source = TestPdfFactory.CreateReportPdf(Path.Combine(_folder.FullName, "report.pdf"));
        var form = PdfFormBuilder.AddFields(File.ReadAllBytes(source),
        [
            new(0, NameBox, PdfNewFieldKind.Text, "Name"),
            new(0, PictureBox, PdfNewFieldKind.Text, "Picture"),
        ]);
        var formPath = Path.Combine(_folder.FullName, "form.pdf");
        File.WriteAllBytes(formPath, form);

        int nameIndex, pictureIndex;
        await using (var document = await _engine.OpenAsync(formPath))
        {
            var fields = await document.GetFormFieldsAsync(0);
            nameIndex = fields.Single(f => f.Name == "Name").AnnotIndex;
            pictureIndex = fields.Single(f => f.Name == "Picture").AnnotIndex;
        }

        // Thai with marks below the line (ญ ฐ ุ ู), shaped as the app does, at 11 pt.
        var text = Sta.Run(() => TextShaper.ToShapedTexts([TextShaper.Shape("ญาดา ฐานะสุขภูมิ", "Leelawadee UI", bold: false, italic: false, 11)])[0]);
        var red = new byte[20 * 10 * 4];
        for (var i = 0; i < red.Length; i += 4)
        {
            red[i + 2] = 255; // BGRA
            red[i + 3] = 255;
        }

        var saved = PdfFieldAppearance.Apply(form,
        [
            new PdfFieldLook(0, nameIndex, 11, text),
            new PdfFieldLook(0, pictureIndex, 0, Image: new PdfImage(20, 10, red)),
        ]);
        var savedPath = Path.Combine(_folder.FullName, "saved.pdf");
        File.WriteAllBytes(savedPath, saved);

        await using var result = await _engine.OpenAsync(savedPath);
        var size = result.PageSizes[0];
        var bitmap = await result.RenderPageAsync(0, (int)size.Width, (int)size.Height);

        // The name is drawn (dark pixels in its box) and the field now asks for 11 pt.
        Assert.True(CountPixels(bitmap, NameBox, (r, g, b) => r < 100 && g < 100 && b < 100) > 20, "No text was drawn in the name field.");
        Assert.Equal(11, (await result.GetFormFieldsAsync(0)).Single(f => f.Name == "Name").FontSize, 1);

        // The picture fills the middle of its box, keeping its 2:1 shape.
        Assert.True(CountPixels(bitmap, PictureBox, (r, g, b) => r > 200 && g < 60 && b < 60) > 200, "The picture is not in its field.");
    }

    [Theory]
    [InlineData("/Helv 12 Tf 0 g", 9.5, "/Helv 9.5 Tf 0 g")]
    [InlineData("0 g /TH 0 Tf", 11, "0 g /TH 11 Tf")]
    [InlineData("0 g", 10, "/Helv 10 Tf 0 g")]
    public void The_default_appearance_gets_the_chosen_size(string da, double size, string expected) =>
        Assert.Equal(expected, PdfFieldAppearance.WithFontSize(da, size));

    [Theory]
    [InlineData("/Helv 12 Tf 0 g", "/Helv 12 Tf 1 0 0 rg")]
    [InlineData("/TH 0 Tf 0.2 0.4 0.6 rg", "/TH 0 Tf 1 0 0 rg")]
    [InlineData("0 0 0 1 k /Helv 9 Tf", "/Helv 9 Tf 1 0 0 rg")]
    public void The_default_appearance_gets_the_chosen_colour(string da, string expected) =>
        Assert.Equal(expected, PdfFieldAppearance.WithColor(da, new PdfColor(255, 0, 0)));

    private static int CountPixels(PdfBitmap bitmap, NormalizedRect box, Func<byte, byte, byte, bool> match)
    {
        var count = 0;
        var x0 = (int)(box.X * bitmap.Width);
        var y0 = (int)(box.Y * bitmap.Height);
        var x1 = (int)((box.X + box.Width) * bitmap.Width);
        var y1 = (int)((box.Y + box.Height) * bitmap.Height);
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = (y * bitmap.Stride) + (x * 4);
                if (match(bitmap.Pixels[i + 2], bitmap.Pixels[i + 1], bitmap.Pixels[i]))
                {
                    count++;
                }
            }
        }

        return count;
    }
}
