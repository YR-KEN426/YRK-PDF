using System.Reflection;
using System.Text;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Ocr;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class OcrScanPagesTests
{
    [Theory]
    [InlineData("() Tj", false)]
    [InlineData("[120] TJ", false)]
    [InlineData("[() 120 <>] TJ", false)]
    [InlineData("() '", false)]
    [InlineData("0 0 () \"", false)]
    [InlineData("[(old) 120] TJ", true)]
    [InlineData("<0061> Tj", true)]
    public async Task Empty_text_and_numeric_adjustments_do_not_count_as_an_ocr_layer(string show, bool hidden)
    {
        await using var app = new AppTestHarness();
        var bytes = ScanPdfFixture.Build("BT /F1 12 Tf 3 Tr " + show + " ET");
        File.WriteAllBytes(app.PathFor("empty.pdf"), bytes);
        await using var document = await app.Engine.OpenAsync(app.PathFor("empty.pdf"));
        var page = Assert.Single(await OcrScanPages.FindAsync(document, bytes));
        Assert.Equal(hidden, page.HasOcrLayer);
        Assert.Equal(hidden, page.OcrLayerOnly);
    }

    [Theory]
    [InlineData("BT /F1 12 Tf 3 Tr (old hidden words) Tj ET", false, true)]
    [InlineData("BT /F1 12 Tf 3 Tr (old hidden words) Tj 0.0 Tr (Visible) Tj ET", false, false)]
    [InlineData("BT /F1 12 Tf 3 Tr (old hidden words) Tj ET /GS1 gs", true, false)]
    public async Task Ocr_presence_is_separate_from_safe_removal(string content, bool extFont, bool safe)
    {
        await using var app = new AppTestHarness();
        var bytes = ScanPdfFixture.Build(content, extGStateFont: extFont);
        File.WriteAllBytes(app.PathFor("flags.pdf"), bytes);
        await using var document = await app.Engine.OpenAsync(app.PathFor("flags.pdf"));
        var page = Assert.Single(await OcrScanPages.FindAsync(document, bytes));
        Assert.True(page.HasOcrLayer);
        Assert.Equal(safe, page.OcrLayerOnly);
    }

    [Theory]
    [InlineData("", true, false)]
    [InlineData("BT /F1 12 Tf (short) Tj ET", true, false)]
    [InlineData("BT /F1 12 Tf (This is a page with genuine visible text) Tj ET", false, false)]
    [InlineData("BT 3 Tr /F1 12 Tf (This is an invisible OCR text layer) Tj ET", true, true)]
    [InlineData("BT 3 Tr /F1 12 Tf (Hidden text layer with enough characters) Tj ET q BT 0 Tr /F1 12 Tf (Visible text) Tj ET Q", false, true)]
    [InlineData("q BT 3 Tr /F1 12 Tf (Hidden text layer with enough characters) Tj ET Q BT /F1 12 Tf (Visible text) Tj ET", false, true)]
    [InlineData("BT 3 Tr /F1 12 Tf [(Hidden) 10 ( text layer with enough characters)] TJ ET", true, true)]
    [InlineData("BT 3 Tr /F1 12 Tf (Hidden text layer with enough characters) Tj ET Unknown", false, true)]
    [InlineData("BT 3 Tr /F1 12 Tf (Hidden text layer with enough characters) Tj ET 42", false, true)]
    [InlineData("BT 3 Tr /F1 12 Tf (Hidden text layer with enough characters) Tj ET ) q Q", false, true)]
    public async Task Detects_scans_and_distinguishes_hidden_mixed_and_unknown_content(string content, bool scan, bool hidden)
    {
        await using var app = new AppTestHarness();
        var path = app.PathFor("page.pdf");
        File.WriteAllBytes(path, ScanPdfFixture.Build(content));
        await using var document = await app.Engine.OpenAsync(path);
        var page = Assert.Single(await OcrScanPages.FindAsync(document, File.ReadAllBytes(path)));
        Assert.Equal(scan, page.IsScan);
        Assert.Equal(hidden, page.HasOcrLayer);
    }

    [Fact]
    public async Task Detects_the_layer_written_by_AddTextLayer_and_nested_form_text()
    {
        await using var app = new AppTestHarness();
        foreach (var nested in new[] { false, true })
        {
            var bytes = nested ? ScanPdfFixture.Build("BT 3 Tr /F1 12 Tf (Hidden text inside the form object) Tj ET", form: true)
                : PdfOcr.AddTextLayer(ScanPdfFixture.Build(""), [new OcrPageResult(0,
                    [new OcrWord("Existing invisible OCR words", new(.1, .1, .7, .05), 90, true)])]);
            var path = app.PathFor(nested + ".pdf");
            File.WriteAllBytes(path, bytes);
            await using var doc = await app.Engine.OpenAsync(path);
            var page = Assert.Single(await OcrScanPages.FindAsync(doc, bytes));
            Assert.True(page.IsScan);
            Assert.True(page.HasOcrLayer);
        }
    }

    [Fact]
    public async Task Rendering_mode_persists_across_separate_page_streams()
    {
        await using var app = new AppTestHarness();
        var cos = CosDocument.Load(ScanPdfFixture.Build("BT 3 Tr /F1 12 Tf (Existing hidden text layer words) Tj ET"));
        var page = cos.GetPages()[0];
        var update = new CosIncrementalUpdate(cos);
        var extra = update.Add(new CosStream(new CosDictionary(), Encoding.ASCII.GetBytes("BT /F1 12 Tf (More hidden words) Tj ET")));
        var dict = page.Dictionary.Clone();
        dict["Contents"] = new CosArray([page.Dictionary["Contents"]!, extra]);
        update.Replace(page.Id, dict);
        var bytes = update.Build().Bytes;
        var file = app.PathFor("streams.pdf");
        File.WriteAllBytes(file, bytes);
        await using var document = await app.Engine.OpenAsync(file);
        var found = Assert.Single(await OcrScanPages.FindAsync(document, bytes));
        Assert.True(found.IsScan);
        Assert.True(found.HasOcrLayer);
    }

    [Fact]
    public async Task OnlyPages_limits_inspection_and_empty_selection_does_not_initialize_tesseract()
    {
        var visited = new List<int>();
        var document = DispatchProxy.Create<IPdfDocument, SignatureSecurityTests.TestProxy>();
        ((SignatureSecurityTests.TestProxy)(object)document).Handle = (method, args) => method.Name switch
        {
            "get_PageCount" => 4,
            nameof(IPdfDocument.GetPageTextAsync) => Text((int)args![0]!),
            _ => throw new InvalidOperationException("Unexpected native OCR operation: " + method.Name),
        };
        Task<string> Text(int page)
        {
            visited.Add(page);
            return Task.FromResult("This page contains enough real text already.");
        }
        var data = new OcrLanguageData(Path.Combine(PrivateTempDirectory.Path, "absent-models"));
        Assert.Empty(await PdfOcr.RecognizePagesAsync(document, new(["eng"], OnlyPages: [1, 3]), data));
        Assert.Equal(new[] { 1, 3 }, visited);
        visited.Clear();
        Assert.Empty(await PdfOcr.RecognizePagesAsync(document, new(["eng"], OcrPageSelection.AllPages, OnlyPages: []), data));
        Assert.Empty(visited);
        Assert.Empty(await PdfOcr.RecognizePagesAsync(document, new(["eng"]), data));
        Assert.Equal(new[] { 0, 1, 2, 3 }, visited); // Default selection remains unchanged.
    }
}

internal static class ScanPdfFixture
{
    public static byte[] Build(string content, bool form = false, int pages = 1, bool extGStateFont = false)
    {
        var pdf = new CosNewDocument();
        var root = pdf.Reserve();
        var tree = pdf.Reserve();
        var font = pdf.Add(new CosDictionary().Set("Type", new CosName("Font")).Set("Subtype", new CosName("Type1"))
            .Set("BaseFont", new CosName("Helvetica")));
        var resources = new CosDictionary().Set("Font", new CosDictionary().Set("F1", font));
        if (extGStateFont)
            resources.Set("ExtGState", new CosDictionary().Set("GS1", new CosDictionary()
                .Set("Font", new CosArray([font, new CosInteger(12)]))));
        var stream = pdf.Add(new CosStream(form
            ? new CosDictionary().Set("Type", new CosName("XObject")).Set("Subtype", new CosName("Form"))
                .Set("BBox", CosArray.FromNumbers(0, 0, 300, 400)).Set("Resources", resources)
            : new CosDictionary(), Encoding.ASCII.GetBytes(content)));
        if (form)
        {
            resources = new CosDictionary().Set("XObject", new CosDictionary().Set("Nested", stream));
            stream = pdf.Add(new CosStream(new CosDictionary(), Encoding.ASCII.GetBytes("/Nested Do")));
        }
        var kids = new List<CosObject>();
        for (var i = 0; i < pages; i++)
            kids.Add(pdf.Add(new CosDictionary().Set("Type", new CosName("Page")).Set("Parent", tree)
                .Set("MediaBox", CosArray.FromNumbers(0, 0, 300, 400)).Set("Resources", resources).Set("Contents", stream)));
        pdf.Set(tree, new CosDictionary().Set("Type", new CosName("Pages")).Set("Kids", new CosArray(kids)).Set("Count", new CosInteger(pages)));
        pdf.Set(root, new CosDictionary().Set("Type", new CosName("Catalog")).Set("Pages", tree));
        return pdf.Build(root);
    }
}
