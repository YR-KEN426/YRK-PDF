using System.Text;
using System.Security.Cryptography;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Export;
using Yrk.PdfStudio.Core.Pdf.Security;
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

    private string CreatePlacementPage(string name, int rotation = 0, string content = "")
    {
        var path = PathFor(name);
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Rotate {rotation} /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        using var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.Latin1.GetBytes(text));
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(output.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = output.Position;
        Write("xref\n0 5\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write($"{offset:D10} 00000 n \n");
        }

        // A fixed first ID is also used by the deterministic legacy encryption fixture below.
        Write($"trailer\n<< /Size 5 /Root 1 0 R /ID [<0123456789ABCDEF0123456789ABCDEF><0123456789ABCDEF0123456789ABCDEF>] >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, output.ToArray());
        return path;
    }

    private static PdfImage RedImage(byte alpha = 255) => new(2, 2,
        [0, 0, 255, alpha, 0, 0, 255, alpha, 0, 0, 255, alpha, 0, 0, 255, alpha]);

    private static PdfVectorArt RedVector(byte alpha = 255) => new(2, 1,
    [
        new PdfVectorPath([PdfPathCommand.Move(0, 0), PdfPathCommand.Line(2, 0),
            PdfPathCommand.Line(2, 1), PdfPathCommand.Line(0, 1), PdfPathCommand.Closing], new PdfColor(255, 0, 0, alpha)),
    ]);

    [Theory]
    [InlineData("image", "D0CE4C081CF9B6BE9B183A1B7C3C09A37F28F11932A015BF740A834B91FF247F")]
    [InlineData("vector", "41A1182ECB2C26852858A1E3CE1E05894B9E3AE0CCA3ED652EB12D7B0518FA7A")]
    [InlineData("pdf", "01DF74B2952F02253ACB5695B4E4A20B0889102E7F3A961D27CE40282470775C")]
    public async Task Default_placement_preserves_every_legacy_byte(string kind, string legacyHash)
    {
        var source = CreatePlacementPage("blank.pdf");
        EncryptForByteComparison(source);
        var pdf = File.ReadAllBytes(CreatePlacementPage("art.pdf", content: "1 0 0 rg 0 0 400 300 re f"));
        var bounds = new NormalizedRect(0.25, 0.3, 0.4, 0.2);
        byte[]? omitted = null;
        // Hashes captured with the pre-style implementation. No bytes are normalized or excluded.
        foreach (var mode in new[] { "omitted", "null", "default" })
        {
            await using (var document = await _engine.OpenAsync(source))
            {
                if (mode == "omitted")
                {
                    if (kind == "image") await document.AddImageAsync(0, RedImage(180), bounds, CancellationToken.None);
                    else if (kind == "vector") await document.AddVectorAsync(0, RedVector(180), bounds, CancellationToken.None);
                    else await document.AddPdfPageAsync(0, pdf, new NormalizedRect(0, 0, 1, 1), bounds, CancellationToken.None);
                }
                else
                {
                    var style = mode == "null" ? null : new PlacementStyle();
                    if (kind == "image") await document.AddImageAsync(0, RedImage(180), bounds, style: style);
                    else if (kind == "vector") await document.AddVectorAsync(0, RedVector(180), bounds, style: style);
                    else await document.AddPdfPageAsync(0, pdf, new NormalizedRect(0, 0, 1, 1), bounds, style: style);
                }

                await document.SaveAsync(PathFor($"{mode}.pdf"));
            }

            var bytes = File.ReadAllBytes(PathFor($"{mode}.pdf"));
            Assert.Equal(legacyHash, Convert.ToHexString(SHA256.HashData(bytes)));
            if (omitted is null) omitted = bytes;
            else Assert.Equal(omitted, bytes);
        }
    }

    [Theory]
    [InlineData("image", 0, 90)]
    [InlineData("image", 0, 45)]
    [InlineData("image", 90, 30)]
    [InlineData("image", 180, 30)]
    [InlineData("image", 270, 30)]
    [InlineData("vector", 0, 90)]
    [InlineData("vector", 0, 45)]
    [InlineData("vector", 90, 30)]
    [InlineData("vector", 180, 30)]
    [InlineData("vector", 270, 30)]
    [InlineData("pdf", 0, 45)]
    [InlineData("pdf", 90, 30)]
    [InlineData("pdf", 180, 30)]
    [InlineData("pdf", 270, 30)]
    public async Task Rotated_placement_has_expected_display_bounds(string kind, int pageRotation, double rotation)
    {
        var source = CreatePlacementPage("blank.pdf", pageRotation);
        var pdf = File.ReadAllBytes(CreatePlacementPage("art.pdf", content: "1 0 0 rg 0 0 400 300 re f"));
        var bounds = new NormalizedRect(0.2, 0.3, 0.4, 0.2);
        var style = new PlacementStyle(rotation);
        var target = PathFor("rotated.pdf");
        await using (var document = await _engine.OpenAsync(source))
        {
            if (kind == "image") await document.AddImageAsync(0, RedImage(), bounds, style: style);
            else if (kind == "vector") await document.AddVectorAsync(0, RedVector(), bounds, style: style);
            else await document.AddPdfPageAsync(0, pdf, new NormalizedRect(0, 0, 1, 1), bounds, style: style);
            CheckBounds(await document.GetPageGraphicsAsync(0));
            await document.SaveAsync(target);
        }

        await using var reopened = await _engine.OpenAsync(target);
        CheckBounds(await reopened.GetPageGraphicsAsync(0));

        void CheckBounds(PdfPageGraphics graphics)
        {
            var actual = Assert.Single(graphics.Graphics);
            var radians = rotation * Math.PI / 180;
            var width = bounds.Width * graphics.Width;
            var height = bounds.Height * graphics.Height;
            var expectedWidth = Math.Abs(width * Math.Cos(radians)) + Math.Abs(height * Math.Sin(radians));
            var expectedHeight = Math.Abs(width * Math.Sin(radians)) + Math.Abs(height * Math.Cos(radians));
            Assert.InRange(actual.Width, expectedWidth - 0.02, expectedWidth + 0.02);
            Assert.InRange(actual.Height, expectedHeight - 0.02, expectedHeight + 0.02);
            var centreX = (bounds.X + bounds.Width / 2) * graphics.Width;
            var centreY = (bounds.Y + bounds.Height / 2) * graphics.Height;
            Assert.InRange(actual.X, centreX - expectedWidth / 2 - 0.02, centreX - expectedWidth / 2 + 0.02);
            Assert.InRange(actual.Y, centreY - expectedHeight / 2 - 0.02, centreY - expectedHeight / 2 + 0.02);
        }
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(90, 90)]
    [InlineData(180, 90)]
    [InlineData(270, 90)]
    [InlineData(0, 30)]
    [InlineData(90, 30)]
    [InlineData(180, 30)]
    [InlineData(270, 30)]
    public async Task Positive_rotation_is_clockwise_on_display(int pageRotation, double rotation)
    {
        var source = CreatePlacementPage("blank.pdf", pageRotation);
        // Distinct halves detect the rotation's sign, which a bounding box alone cannot do.
        var image = new PdfImage(2, 1, [0, 0, 255, 255, 255, 0, 0, 255]);
        var art = new PdfVectorArt(2, 1,
        [
            new PdfVectorPath([PdfPathCommand.Move(0, 0), PdfPathCommand.Line(1, 0),
                PdfPathCommand.Line(1, 1), PdfPathCommand.Line(0, 1), PdfPathCommand.Closing], new PdfColor(255, 0, 0)),
            new PdfVectorPath([PdfPathCommand.Move(1, 0), PdfPathCommand.Line(2, 0),
                PdfPathCommand.Line(2, 1), PdfPathCommand.Line(1, 1), PdfPathCommand.Closing], new PdfColor(0, 0, 255)),
        ]);
        var pdf = File.ReadAllBytes(CreatePlacementPage("art.pdf", content: "1 0 0 rg 0 0 200 300 re f 0 0 1 rg 200 0 200 300 re f"));
        foreach (var kind in new[] { "image", "vector", "pdf" })
        {
            var target = PathFor($"{kind}.pdf");
            await using (var document = await _engine.OpenAsync(source))
            {
                var bounds = new NormalizedRect(0.25, 0.25, 0.5, 0.5);
                var style = new PlacementStyle(rotation);
                if (kind == "image") await document.AddImageAsync(0, image, bounds, style: style);
                else if (kind == "vector") await document.AddVectorAsync(0, art, bounds, style: style);
                else await document.AddPdfPageAsync(0, pdf, new NormalizedRect(0, 0, 1, 1), bounds, style: style);
                var size = document.PageSizes[0];
                CheckDirection(await document.RenderPageAsync(0, (int)size.Width, (int)size.Height));
                await document.SaveAsync(target);
            }

            await using var reopened = await _engine.OpenAsync(target);
            var savedSize = reopened.PageSizes[0];
            CheckDirection(await reopened.RenderPageAsync(0, (int)savedSize.Width, (int)savedSize.Height));
        }

        void CheckDirection(PdfBitmap bitmap)
        {
            var radians = rotation * Math.PI / 180;
            var dx = bitmap.Width * 0.125 * Math.Cos(radians);
            var dy = bitmap.Width * 0.125 * Math.Sin(radians);
            AssertPixel(bitmap, (int)(bitmap.Width / 2 - dx), (int)(bitmap.Height / 2 - dy), 255, 0, 0);
            AssertPixel(bitmap, (int)(bitmap.Width / 2 + dx), (int)(bitmap.Height / 2 + dy), 0, 0, 255);
        }
    }

    [Theory]
    [InlineData("image", 255, 0.5, 127)]
    [InlineData("image", 128, 0.5, 191)]
    [InlineData("image", 255, 0, 255)]
    [InlineData("vector", 255, 0.5, 127)]
    [InlineData("vector", 128, 0.5, 191)]
    [InlineData("vector", 255, 0, 255)]
    public async Task Placement_opacity_blends_with_white(string kind, byte alpha, double opacity, int pale)
    {
        var image = RedImage(alpha);
        var before = (byte[])image.Pixels.Clone();
        var target = PathFor("opacity.pdf");
        await using (var document = await _engine.OpenAsync(CreatePlacementPage("blank.pdf")))
        {
            var bounds = new NormalizedRect(0.25, 0.25, 0.5, 0.5);
            var style = new PlacementStyle(30, opacity);
            if (kind == "image") await document.AddImageAsync(0, image, bounds, style: style);
            else await document.AddVectorAsync(0, RedVector(alpha), bounds, style: style);
            AssertPixel(await document.RenderPageAsync(0, 400, 300), 200, 150, 255, pale, pale);
            await document.SaveAsync(target);
        }

        Assert.Equal(before, image.Pixels);
        await using var reopened = await _engine.OpenAsync(target);
        AssertPixel(await reopened.RenderPageAsync(0, 400, 300), 200, 150, 255, pale, pale);
    }

    [Fact]
    public async Task Pdf_page_opacity_is_explicitly_unsupported_without_modifying_the_page()
    {
        var source = CreatePlacementPage("blank.pdf");
        var pdf = File.ReadAllBytes(CreatePlacementPage("art.pdf", content: "1 0 0 rg 0 0 400 300 re f"));
        await using var document = await _engine.OpenAsync(source);
        var before = await document.RenderPageAsync(0, 400, 300);
        await Assert.ThrowsAsync<NotSupportedException>(() => document.AddPdfPageAsync(0, pdf,
            new NormalizedRect(0, 0, 1, 1), new NormalizedRect(0.25, 0.25, 0.5, 0.5), style: new PlacementStyle(Opacity: 0.5)));
        Assert.Empty((await document.GetPageGraphicsAsync(0)).Graphics);
        Assert.Equal(before.Pixels, (await document.RenderPageAsync(0, 400, 300)).Pixels);
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(double.NegativeInfinity, 1)]
    [InlineData(0, double.NaN)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(0, -0.1)]
    [InlineData(0, 1.1)]
    public async Task Rejects_invalid_styles_before_changing_the_page(double rotation, double opacity)
    {
        var source = CreatePlacementPage("blank.pdf");
        await using var document = await _engine.OpenAsync(source);
        var bounds = new NormalizedRect(0.25, 0.25, 0.5, 0.5);
        var style = new PlacementStyle(rotation, opacity);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.AddImageAsync(0, RedImage(), bounds, style: style));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.AddVectorAsync(0, RedVector(), bounds, style: style));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.AddPdfPageAsync(0, File.ReadAllBytes(source), bounds, bounds, style: style));
        Assert.Empty((await document.GetPageGraphicsAsync(0)).Graphics);
    }

    private static void AssertPixel(PdfBitmap bitmap, int x, int y, int r, int g, int b)
    {
        var offset = y * bitmap.Stride + x * 4;
        Assert.InRange((int)bitmap.Pixels[offset], Math.Max(0, b - 2), Math.Min(255, b + 2));
        Assert.InRange((int)bitmap.Pixels[offset + 1], Math.Max(0, g - 2), Math.Min(255, g + 2));
        Assert.InRange((int)bitmap.Pixels[offset + 2], Math.Max(0, r - 2), Math.Min(255, r + 2));
    }

    private static void EncryptForByteComparison(string path)
    {
        // PDFium preserves both IDs on incremental saves of encrypted PDFs. Revision 2 uses
        // deterministic RC4, so full files can be compared byte for byte without masking IDs/IVs.
        // Synthetic test data only; this is deliberately not production encryption guidance.
        var padding = Convert.FromHexString("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");
        var id = Convert.FromHexString("0123456789ABCDEF0123456789ABCDEF");
        var owner = CosCrypt.Rc4(MD5.HashData(padding)[..5], padding);
        var key = MD5.HashData([.. padding, .. owner, 0xFC, 0xFF, 0xFF, 0xFF, .. id])[..5];
        var encrypt = new CosDictionary().Set("Filter", new CosName("Standard"))
            .Set("V", new CosInteger(1)).Set("R", new CosInteger(2)).Set("P", new CosInteger(-4))
            .Set("O", new CosString(owner, isHex: true))
            .Set("U", new CosString(CosCrypt.Rc4(key, padding), isHex: true));
        var document = CosDocument.Load(File.ReadAllBytes(path));
        var crypt = CosCrypt.Open(encrypt, document.Trailer, "")!;
        File.WriteAllBytes(path, CosCompactWriter.Write(document, crypt));
    }
}
