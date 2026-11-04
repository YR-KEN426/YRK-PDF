using System.Text;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Redaction;

namespace Yrk.PdfStudio.Core.Tests;

// Every input is synthesized here; no user documents or external fixtures.
public sealed class RedactionSecurityTests : IDisposable
{
    private const string Secret = "REDACTION-PRIVATE-73921";
    private static readonly NormalizedRect Area = new(0.4, 0.4, 0.2, 0.2);
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-redaction-security-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(true);
    }

    [Theory]
    [InlineData("20 40 70 20 re f", true)]
    [InlineData("10 w 20 50 m 100 50 l S", true)] // zero-height centreline
    [InlineData("10 w 2 J 20 50 m 46 50 l S", true)] // only square cap hits
    [InlineData("10 w 1 J 20 50 m 46 50 l S", true)] // only round cap hits
    [InlineData("8 w 0 j 12 M 20 20 m 45 55 l 20 60 l S", true)]
    [InlineData("8 w 2 j 20 20 m 55 55 l 20 80 l S", true)]
    [InlineData("20 40 70 20 re W f", true)] // path clips AND paints
    [InlineData("10 w 20 50 m 100 50 l W S", true)]
    [InlineData("20 40 70 20 re W n", false)] // clipping alone paints nothing
    [InlineData("52 45 10 10 re f", false)] // fully contained path is removed
    [InlineData("1 w 5 5 m 10 5 l S", false)]
    [InlineData("0 w 20 50 m 100 50 l S", true)] // device hairline
    [InlineData("q 2 0 0 3 0 0 cm 6 w 10 17 m 50 17 l S Q", true)]
    [InlineData("20 40 70 20 re 1 0 0 rg f", true)] // state interleaved with a path
    [InlineData("BX /UnknownOperator customPaint EX", true)]
    public async Task Painted_paths_use_safe_fallback(string content, bool raster)
    {
        var fixture = new Fixture(content);
        var result = await Redact(fixture.Build());
        Assert.Equal(raster, result.ImagePages.Contains(0));
        if (raster || content.StartsWith("52", StringComparison.Ordinal))
        {
            Assert.DoesNotContain(content, DecodedObjects(result.Output), StringComparison.Ordinal);
        }

        await AssertBlack(result.Output);
    }

    [Fact]
    public async Task Shading_and_unhandled_graphics_state_rasterize()
    {
        var fixture = new Fixture("/Shade sh /Unknown gs");
        fixture.Resources["Shading"] = new CosDictionary().Set("Shade", new CosDictionary()
            .Set("ShadingType", new CosInteger(2)).Set("ColorSpace", new CosName("DeviceRGB"))
            .Set("Coords", CosArray.FromNumbers(0, 0, 120, 100))
            .Set("Extend", new CosArray([new CosBoolean(true), new CosBoolean(true)]))
            .Set("Function", new CosDictionary().Set("FunctionType", new CosInteger(2))
                .Set("Domain", CosArray.FromNumbers(0, 1)).Set("C0", CosArray.FromNumbers(1, 0, 0))
                .Set("C1", CosArray.FromNumbers(0, 0, 1)).Set("N", new CosInteger(1))));
        fixture.Resources["ExtGState"] = new CosDictionary().Set("Unknown", new CosDictionary().Set("SMask", new CosName("None")));
        var result = await Redact(fixture.Build());
        Assert.Equal([0], result.ImagePages);
        Assert.DoesNotContain("/Shade sh", DecodedObjects(result.Output), StringComparison.Ordinal);
        await AssertBlack(result.Output);
    }

    [Fact]
    public async Task External_line_width_is_included()
    {
        var fixture = new Fixture("/Wide gs 20 35 m 100 35 l S");
        fixture.Resources["ExtGState"] = new CosDictionary().Set("Wide", new CosDictionary().Set("LW", new CosInteger(20)));
        Assert.Equal([0], (await Redact(fixture.Build())).ImagePages);
    }

    [Fact]
    public async Task Image_verification_rejects_a_surviving_original_even_after_recompression()
    {
        var pixels = Encoding.ASCII.GetBytes("PRIVATE-IMAGE-12345");
        var fixture = new Fixture("q 120 0 0 100 0 0 cm /Im Do Q");
        fixture.Resources["XObject"] = new CosDictionary().Set("Im", fixture.AddImage(pixels));
        var source = CosDocument.Load(fixture.Build());
        var output = CosDocument.Load(fixture.Build());
        var xobjects = (CosDictionary)output.Resolve(((CosDictionary)output.Resolve(output.GetPages()[0].Resources)!)["XObject"])!;
        var reference = (CosReference)xobjects["Im"]!;
        var stream = (CosStream)output.Resolve(reference)!;
        var dictionary = stream.Dictionary.Clone();
        dictionary["Filter"] = null;
        output.Replace(reference.Id, new CosStream(dictionary, pixels));
        Assert.Throws<PdfException>(() => PdfRedactor.VerifyImages(source, output, [0], [0]));
    }

    [Fact]
    public async Task Render_verification_rejects_an_area_that_is_not_black()
    {
        await using var document = await Open(new Fixture("1 0 0 rg 0 0 120 100 re f").Build());
        await Assert.ThrowsAsync<PdfException>(() => PdfRedactor.VerifyRenderedAreasAsync(document, 0, [Area], CancellationToken.None));
    }

    [Fact]
    public async Task Real_jpeg_codec_passes_the_final_render_check()
    {
        var fixture = new Fixture("q 120 0 0 100 0 0 cm /Im Do Q");
        fixture.Resources["XObject"] = new CosDictionary().Set("Im", fixture.AddImage(Encoding.ASCII.GetBytes("PRIVATE-IMAGE-12345")));
        var result = await PdfRedactor.RedactAsync(fixture.Build(), [new PdfRedactionArea(0, Area)], _engine, new WpfImageCodec());
        Assert.Equal([0], result.ImagePages);
        await AssertBlack(result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inherited_image_is_removed_unless_an_unmarked_page_paints_it(bool shared)
    {
        var pixels = Encoding.ASCII.GetBytes("PRIVATE-IMAGE-12345"); // 3 x 2 RGB
        var fixture = new Fixture("q 120 0 0 100 0 0 cm /Im Do Q", shared ? "q 120 0 0 100 0 0 cm /Im Do Q" : "q Q");
        var image = fixture.AddImage(pixels);
        fixture.Resources["XObject"] = new CosDictionary().Set("Im", image);
        var input = fixture.Build();
        var result = await Redact(input);
        Assert.Equal([0], result.ImagePages);
        var decoded = DecodedObjects(result.Output);
        Assert.Equal(shared, decoded.Contains(Encoding.ASCII.GetString(pixels), StringComparison.Ordinal));
        var output = CosDocument.Load(result.Output);
        AssertNoInheritedAttributes(output);
        await AssertBlack(result.Output);
        await using var before = await Open(input);
        await using var after = await Open(result.Output);
        Assert.Equal((await before.RenderPageAsync(1, 240, 200)).Pixels, (await after.RenderPageAsync(1, 240, 200)).Pixels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Nested_inheritance_keeps_boxes_rotation_and_render(int rotation)
    {
        var fixture = new Fixture("0 0 1 rg 25 35 40 20 re f", "0 1 0 rg 25 35 40 20 re f");
        fixture.RootPages["MediaBox"] = CosArray.FromNumbers(10, 20, 150, 140);
        fixture.RootPages["CropBox"] = CosArray.FromNumbers(15, 25, 145, 135);
        fixture.RootPages["Rotate"] = new CosInteger(90);
        fixture.InnerPages["CropBox"] = CosArray.FromNumbers(20, 30, 140, 130);
        fixture.InnerPages["Rotate"] = new CosInteger(rotation);
        fixture.Pages[1]["MediaBox"] = CosArray.FromNumbers(12, 22, 148, 138);
        fixture.Pages[1]["CropBox"] = CosArray.FromNumbers(25, 35, 135, 125);
        fixture.Pages[1]["Rotate"] = new CosInteger(270);
        var input = fixture.Build();
        var result = await Redact(input);
        var beforeCos = CosDocument.Load(input).GetPages();
        var output = CosDocument.Load(result.Output);
        var afterCos = output.GetPages();
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(beforeCos[i].CropBox, afterCos[i].CropBox);
            Assert.Equal(beforeCos[i].Rotate, afterCos[i].Rotate);
        }

        AssertNoInheritedAttributes(output);
        await using var before = await Open(input);
        await using var after = await Open(result.Output);
        Assert.Equal(before.PageSizes, after.PageSizes);
        Assert.Equal((await before.RenderPageAsync(1, 240, 200)).Pixels, (await after.RenderPageAsync(1, 240, 200)).Pixels);
    }

    [Theory]
    [InlineData("Thumb")]
    [InlineData("PieceInfo")]
    [InlineData("PageMetadata")]
    [InlineData("CatalogMetadata")]
    [InlineData("Info")]
    [InlineData("Bookmark")]
    [InlineData("Structure")]
    [InlineData("EmbeddedFiles")]
    [InlineData("AF")]
    [InlineData("FileAttachment")]
    [InlineData("XFA")]
    [InlineData("Annotation")]
    [InlineData("LinkedAnnotation")]
    [InlineData("AliasedAttachment")]
    [InlineData("Actions")]
    public async Task Hidden_data_is_gone_from_decoded_objects_and_the_entire_file(string channel)
    {
        var fixture = new Fixture("q Q", "q Q");
        var payload = channel is "PageMetadata" or "CatalogMetadata"
            ? $"<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/' dc:description='{Secret}'/></rdf:RDF></x:xmpmeta>"
            : Secret;
        var streamDictionary = channel is "PageMetadata" or "CatalogMetadata"
            ? new CosDictionary().Set("Type", new CosName("Metadata")).Set("Subtype", new CosName("XML"))
            : new CosDictionary().Set("Type", new CosName("EmbeddedFile"));
        var secretStream = fixture.Document.Add(CosPageEdit.Stream(Encoding.UTF8.GetBytes(payload), streamDictionary));
        var embeddedFile = fixture.Document.Add(new CosDictionary().Set("Type", new CosName("Filespec"))
            .Set("F", CosString.FromText("attachment.txt")).Set("EF", new CosDictionary().Set("F", secretStream)));
        var secretDictionary = fixture.Document.Add(new CosDictionary().Set("Secret", CosString.FromText(Secret)));
        switch (channel)
        {
            case "Thumb":
                fixture.Pages[0]["Thumb"] = fixture.AddImage(Encoding.ASCII.GetBytes(Secret.PadRight(24, 'x')[..24]));
                fixture.Pages[1]["Thumb"] = secretStream;
                break;
            case "PieceInfo":
                fixture.Pages[0]["PieceInfo"] = secretDictionary;
                fixture.Pages[1]["PieceInfo"] = secretDictionary;
                break;
            case "PageMetadata":
                fixture.Pages[0]["Metadata"] = secretStream;
                fixture.Pages[1]["Metadata"] = secretStream;
                break;
            case "CatalogMetadata": fixture.Catalog["Metadata"] = secretStream; break;
            case "Info": fixture.Info = secretDictionary; break;
            case "Bookmark":
                fixture.Catalog["Outlines"] = fixture.Document.Add(new CosDictionary().Set("First",
                    fixture.Document.Add(new CosDictionary().Set("Title", CosString.FromText(Secret)).Set("Dest", new CosArray([fixture.PageRefs[0], new CosName("Fit")])))));
                break;
            case "Structure":
                fixture.Catalog["StructTreeRoot"] = secretDictionary;
                fixture.Catalog["MarkInfo"] = secretDictionary;
                break;
            case "EmbeddedFiles":
                fixture.Catalog["Names"] = fixture.Document.Add(new CosDictionary().Set("EmbeddedFiles", new CosDictionary()
                    .Set("Names", new CosArray([CosString.FromText("attachment.txt"), embeddedFile]))));
                break;
            case "AF":
                fixture.Catalog["AF"] = new CosArray([embeddedFile]);
                fixture.Pages[1]["AF"] = new CosArray([embeddedFile]);
                break;
            case "FileAttachment":
                fixture.Pages[1]["Annots"] = new CosArray([fixture.Document.Add(new CosDictionary()
                    .Set("Subtype", new CosName("FileAttachment")).Set("Rect", CosArray.FromNumbers(0, 0, 10, 10))
                    .Set("FS", embeddedFile))]);
                break;
            case "XFA": fixture.Catalog["AcroForm"] = new CosDictionary().Set("XFA", secretStream); break;
            case "Annotation":
                fixture.Pages[1]["Annots"] = new CosArray([fixture.Document.Add(new CosDictionary().Set("Subtype", new CosName("Text"))
                    .Set("Rect", CosArray.FromNumbers(0, 0, 10, 10)).Set("Contents", CosString.FromText(Secret))
                    .Set("RC", CosString.FromText(Secret)).Set("AP", new CosDictionary().Set("N", secretStream)))]);
                break;
            case "LinkedAnnotation":
                var popup = fixture.Document.Add(new CosDictionary().Set("Subtype", new CosName("Popup"))
                    .Set("Contents", CosString.FromText(Secret)).Set("AP", new CosDictionary().Set("N", secretStream)));
                fixture.Pages[1]["Annots"] = new CosArray([fixture.Document.Add(new CosDictionary().Set("Subtype", new CosName("Text"))
                    .Set("Popup", popup).Set("Rect", CosArray.FromNumbers(0, 0, 10, 10)))]);
                break;
            case "AliasedAttachment":
                var filespec = fixture.Document.Add(new CosDictionary().Set("Type", new CosName("Filespec"))
                    .Set("EF", new CosDictionary().Set("F", secretStream)).Set("UF", CosString.FromText(Secret)));
                fixture.Catalog["AF"] = new CosArray([filespec]);
                fixture.Pages[1]["AlternateFileReference"] = filespec;
                break;
            case "Actions":
                var action = fixture.Document.Add(new CosDictionary().Set("Type", new CosName("Action"))
                    .Set("S", new CosName("JavaScript")).Set("JS", secretStream));
                fixture.Catalog["OpenAction"] = action;
                fixture.Catalog["Names"] = new CosDictionary().Set("JavaScript", new CosArray([action]));
                fixture.Pages[1]["AA"] = new CosDictionary().Set("O", action);
                fixture.Pages[1]["A"] = action;
                fixture.Pages[1]["AlternateActionReference"] = action;
                break;
        }

        // Also seed a completely orphaned object and an earlier revision carrying the secret.
        var original = fixture.Build();
        var incremental = new CosIncrementalUpdate(CosDocument.Load(original));
        incremental.Add(CosString.FromText(Secret));
        var result = await Redact(incremental.Build().Bytes);
        Assert.DoesNotContain(Secret, Encoding.Latin1.GetString(result.Output), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, DecodedObjects(result.Output), StringComparison.Ordinal);
        Assert.Null(CosDocument.Load(result.Output).Trailer["Info"]);
        Assert.Null(CosDocument.Load(result.Output).Trailer["Prev"]);
        Assert.Equal(1, Encoding.Latin1.GetString(result.Output).Split("%%EOF").Length - 1);
        await AssertBlack(result.Output);
    }

    [Fact]
    public async Task Removed_widget_clears_values_on_all_shared_ancestors()
    {
        var fixture = new Fixture("q Q", "q Q");
        var parent = fixture.Document.Reserve();
        var grandparent = fixture.Document.Reserve();
        var widget1 = fixture.Document.Add(new CosDictionary().Set("Subtype", new CosName("Widget"))
            .Set("Parent", parent).Set("Rect", CosArray.FromNumbers(50, 45, 65, 55)).Set("P", fixture.PageRefs[0]));
        var widget2 = fixture.Document.Add(new CosDictionary().Set("Subtype", new CosName("Widget"))
            .Set("Parent", parent).Set("Rect", CosArray.FromNumbers(5, 5, 20, 15)).Set("P", fixture.PageRefs[1]));
        fixture.Document.Set(parent, new CosDictionary().Set("T", CosString.FromText("shared")).Set("FT", new CosName("Tx"))
            .Set("Parent", grandparent).Set("Kids", new CosArray([widget1, widget2]))
            .Set("V", CosString.FromText(Secret)).Set("DV", CosString.FromText(Secret)));
        fixture.Document.Set(grandparent, new CosDictionary().Set("T", CosString.FromText("root"))
            .Set("Kids", new CosArray([parent])).Set("V", CosString.FromText(Secret)).Set("DV", CosString.FromText(Secret)));
        fixture.Catalog["AcroForm"] = new CosDictionary().Set("Fields", new CosArray([grandparent]));
        fixture.Pages[0]["Annots"] = new CosArray([widget1]);
        fixture.Pages[1]["Annots"] = new CosArray([widget2]);
        var result = await Redact(fixture.Build());
        var output = CosDocument.Load(result.Output);
        Assert.DoesNotContain(Secret, DecodedObjects(result.Output), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, Encoding.Latin1.GetString(result.Output), StringComparison.Ordinal);
        Assert.Empty(((CosArray)output.Resolve(output.GetPages()[0].Dictionary["Annots"])!).Items);
        Assert.Single(((CosArray)output.Resolve(output.GetPages()[1].Dictionary["Annots"])!).Items);
        var form = (CosDictionary)output.Resolve(output.Catalog["AcroForm"])!;
        Assert.Single(((CosArray)output.Resolve(form["Fields"])!).Items);
    }

    private Task<PdfRedactionResult> Redact(byte[] pdf) => PdfRedactor.RedactAsync(pdf, [new PdfRedactionArea(0, Area)], _engine);

    private async Task<IPdfDocument> Open(byte[] bytes)
    {
        var path = Path.Combine(_folder.FullName, Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, bytes);
        return await _engine.OpenAsync(path);
    }

    private async Task AssertBlack(byte[] bytes)
    {
        await using var document = await Open(bytes);
        var bitmap = await document.RenderPageAsync(0, 240, 200);
        for (var y = 85; y < 115; y++)
        for (var x = 101; x < 139; x++)
        {
            var p = y * bitmap.Stride + x * 4;
            Assert.True(bitmap.Pixels[p] < 30 && bitmap.Pixels[p + 1] < 30 && bitmap.Pixels[p + 2] < 30);
        }
    }

    private static void AssertNoInheritedAttributes(CosDocument document)
    {
        foreach (var id in document.ObjectIds)
        {
            if (document.GetObject(id) is CosDictionary dictionary && (dictionary["Type"] as CosName)?.Value == "Pages")
            {
                foreach (var key in new[] { "Resources", "MediaBox", "CropBox", "Rotate" }) Assert.Null(dictionary[key]);
            }
        }

        foreach (var page in document.GetPages())
        {
            foreach (var key in new[] { "Resources", "MediaBox", "CropBox", "Rotate" }) Assert.NotNull(page.Dictionary[key]);
        }
    }

    private static string DecodedObjects(byte[] bytes)
    {
        var document = CosDocument.Load(bytes);
        var text = new StringBuilder();
        var seen = new HashSet<CosObject>(ReferenceEqualityComparer.Instance);
        void Read(CosObject? value)
        {
            value = document.Resolve(value);
            if (value is null || !seen.Add(value)) return;
            switch (value)
            {
                case CosString s: text.AppendLine(s.ToText()); break;
                case CosArray a: foreach (var item in a.Items) Read(item); break;
                case CosStream s:
                    text.AppendLine(Encoding.Latin1.GetString(CosFilters.Decode(s, document.Resolve)));
                    Read(s.Dictionary);
                    break;
                case CosDictionary d: foreach (var item in d.Entries.Values) Read(item); break;
            }
        }

        foreach (var id in document.ObjectIds) Read(document.GetObject(id));
        return text.ToString();
    }

    private sealed class Fixture
    {
        public CosNewDocument Document { get; } = new();
        public CosDictionary Catalog { get; } = new CosDictionary().Set("Type", new CosName("Catalog"));
        public CosDictionary RootPages { get; } = new CosDictionary().Set("Type", new CosName("Pages"));
        public CosDictionary InnerPages { get; } = new CosDictionary().Set("Type", new CosName("Pages"));
        public CosDictionary Resources { get; } = new();
        public List<CosDictionary> Pages { get; } = [];
        public List<CosReference> PageRefs { get; } = [];
        public CosReference? Info { get; set; }
        private readonly CosReference _root;

        public Fixture(params string[] contents)
        {
            _root = Document.Add(Catalog);
            var rootPages = Document.Add(RootPages);
            var innerPages = Document.Add(InnerPages);
            Catalog["Pages"] = rootPages;
            RootPages["Kids"] = new CosArray([innerPages]);
            RootPages["Count"] = new CosInteger(contents.Length);
            RootPages["MediaBox"] = CosArray.FromNumbers(0, 0, 120, 100);
            RootPages["Resources"] = Document.Add(Resources);
            InnerPages["Parent"] = rootPages;
            InnerPages["Count"] = new CosInteger(contents.Length);
            foreach (var content in contents)
            {
                var page = new CosDictionary().Set("Type", new CosName("Page")).Set("Parent", innerPages)
                    .Set("Contents", Document.Add(CosPageEdit.Stream(Encoding.ASCII.GetBytes(content))));
                Pages.Add(page);
                PageRefs.Add(Document.Add(page));
            }

            InnerPages["Kids"] = new CosArray(PageRefs);
        }

        public CosReference AddImage(byte[] pixels) => Document.Add(CosPageEdit.Stream(pixels,
            new CosDictionary().Set("Type", new CosName("XObject")).Set("Subtype", new CosName("Image"))
                .Set("Width", new CosInteger(pixels.Length / 6)).Set("Height", new CosInteger(2))
                .Set("ColorSpace", new CosName("DeviceRGB")).Set("BitsPerComponent", new CosInteger(8))));

        public byte[] Build() => Document.Build(_root, Info);
    }
}
