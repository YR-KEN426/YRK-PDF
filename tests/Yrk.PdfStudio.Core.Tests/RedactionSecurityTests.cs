using System.Text;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Redaction;
using Yrk.PdfStudio.Core.Pdf.Text;

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
    public async Task Inherited_image_is_removed_from_redacted_page_but_unmarked_resources_are_preserved(bool shared)
    {
        var pixels = Encoding.ASCII.GetBytes("PRIVATE-IMAGE-12345"); // 3 x 2 RGB
        var fixture = new Fixture("q 120 0 0 100 0 0 cm /Im Do Q", shared ? "q 120 0 0 100 0 0 cm /Im Do Q" : "q Q");
        var image = fixture.AddImage(pixels);
        fixture.Resources["XObject"] = new CosDictionary().Set("Im", image);
        var input = fixture.Build();
        var result = await Redact(input);
        Assert.Equal([0], result.ImagePages);
        var decoded = DecodedObjects(result.Output);
        // Untouched page resources are retained even if this simple page does not execute Do.
        Assert.Contains(Encoding.ASCII.GetString(pixels), decoded, StringComparison.Ordinal);
        var output = CosDocument.Load(result.Output);
        var redactedResources = (CosDictionary)output.Resolve(output.GetPages()[0].Resources)!;
        var redactedXobjects = (CosDictionary)output.Resolve(redactedResources["XObject"])!;
        Assert.Null(redactedXobjects["Im"]);
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
                fixture.Pages[0]["Annots"] = new CosArray([fixture.Document.Add(new CosDictionary().Set("Subtype", new CosName("Text"))
                    .Set("Rect", CosArray.FromNumbers(50, 45, 65, 55)).Set("Contents", CosString.FromText(Secret))
                    .Set("RC", CosString.FromText(Secret)).Set("AP", new CosDictionary().Set("N", secretStream)))]);
                break;
            case "LinkedAnnotation":
                var popup = fixture.Document.Add(new CosDictionary().Set("Subtype", new CosName("Popup"))
                    .Set("Contents", CosString.FromText(Secret)).Set("AP", new CosDictionary().Set("N", secretStream)));
                fixture.Pages[0]["Annots"] = new CosArray([fixture.Document.Add(new CosDictionary().Set("Subtype", new CosName("Text"))
                    .Set("Popup", popup).Set("Rect", CosArray.FromNumbers(50, 45, 65, 55)))]);
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
    public async Task Annotations_outside_the_areas_keep_appearances_values_comments_and_links()
    {
        var fixture = new Fixture("q Q", "q Q");
        var appearance = fixture.Document.Add(CosPageEdit.Stream(Encoding.ASCII.GetBytes("0 0 10 10 re S"),
            new CosDictionary().Set("Type", new CosName("XObject")).Set("Subtype", new CosName("Form"))
                .Set("BBox", CosArray.FromNumbers(0, 0, 10, 10))));
        var widget = fixture.Document.Add(new CosDictionary().Set("Type", new CosName("Annot"))
            .Set("Subtype", new CosName("Widget")).Set("FT", new CosName("Tx")).Set("T", CosString.FromText("field"))
            .Set("Rect", CosArray.FromNumbers(5, 5, 20, 15)).Set("P", fixture.PageRefs[1])
            .Set("AP", new CosDictionary().Set("N", appearance))
            .Set("V", CosString.FromText("filled value")).Set("DV", CosString.FromText("default value")));
        var comment = fixture.Document.Add(new CosDictionary().Set("Subtype", new CosName("Text"))
            .Set("Rect", CosArray.FromNumbers(5, 5, 20, 15)).Set("Contents", CosString.FromText("keep comment"))
            .Set("RC", CosString.FromText("<p>keep comment</p>")).Set("AP", new CosDictionary().Set("N", appearance)));
        var link = fixture.Document.Add(new CosDictionary().Set("Type", new CosName("Annot"))
            .Set("Subtype", new CosName("Link")).Set("Rect", CosArray.FromNumbers(25, 5, 45, 15))
            .Set("A", new CosDictionary().Set("Type", new CosName("Action")).Set("S", new CosName("URI"))
                .Set("URI", CosString.FromText("https://example.com/keep"))));
        var removed = fixture.Document.Add(new CosDictionary().Set("Subtype", new CosName("Text"))
            .Set("Rect", CosArray.FromNumbers(50, 45, 65, 55)).Set("Contents", CosString.FromText(Secret))
            .Set("RC", CosString.FromText(Secret)));
        fixture.Pages[0]["Annots"] = new CosArray([comment, link, removed]);
        fixture.Pages[1]["Annots"] = new CosArray([widget, comment, link]);
        fixture.Catalog["AcroForm"] = new CosDictionary().Set("Fields", new CosArray([widget]));

        var result = await Redact(fixture.Build());
        var output = CosDocument.Load(result.Output);
        var marked = Assert.IsType<CosArray>(output.Resolve(output.GetPages()[0].Dictionary["Annots"]));
        Assert.Equal(2, marked.Count);
        var unmarked = Assert.IsType<CosArray>(output.Resolve(output.GetPages()[1].Dictionary["Annots"]));
        Assert.Equal(3, unmarked.Count);
        var keptWidget = Assert.IsType<CosDictionary>(output.Resolve(unmarked[0]));
        var ap = Assert.IsType<CosDictionary>(output.Resolve(keptWidget["AP"]));
        Assert.Equal("0 0 10 10 re S", Encoding.ASCII.GetString(CosFilters.Decode(Assert.IsType<CosStream>(output.Resolve(ap["N"])), output.Resolve)));
        Assert.Equal("filled value", Assert.IsType<CosString>(keptWidget["V"]).ToText());
        Assert.Equal("default value", Assert.IsType<CosString>(keptWidget["DV"]).ToText());
        var keptComment = Assert.IsType<CosDictionary>(output.Resolve(marked[0]));
        Assert.Equal("keep comment", Assert.IsType<CosString>(keptComment["Contents"]).ToText());
        Assert.Equal("<p>keep comment</p>", Assert.IsType<CosString>(keptComment["RC"]).ToText());
        Assert.NotNull(keptComment["AP"]);
        var keptLink = Assert.IsType<CosDictionary>(output.Resolve(marked[1]));
        var action = Assert.IsType<CosDictionary>(output.Resolve(keptLink["A"]));
        Assert.Equal("URI", Assert.IsType<CosName>(action["S"]).Value);
        Assert.Equal("https://example.com/keep", Assert.IsType<CosString>(action["URI"]).ToText());
        Assert.DoesNotContain(Secret, DecodedObjects(result.Output), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, Encoding.Latin1.GetString(result.Output), StringComparison.Ordinal);
        await AssertBlack(result.Output);
    }

    [Theory]
    [InlineData("JavaScript")]
    [InlineData("Launch")]
    [InlineData("ImportData")]
    [InlineData("SubmitForm")]
    [InlineData("GoToE")]
    [InlineData("GoToR")]
    [InlineData("Rendition")]
    [InlineData("Sound")]
    [InlineData("Movie")]
    [InlineData("RichMedia")]
    public async Task Forbidden_actions_are_removed_from_entries_aliases_and_next_chains(string kind)
    {
        var fixture = new Fixture("q Q", "q Q");
        var script = fixture.Document.Add(CosPageEdit.Stream(Encoding.ASCII.GetBytes(Secret)));
        // Both /S and the action itself are indirect, and the action has no optional /Type.
        var forbidden = fixture.Document.Add(new CosDictionary().Set("S", fixture.Document.Add(new CosName(kind)))
            .Set("JS", script).Set("Payload", CosString.FromText(Secret)));
        var direct = new CosDictionary().Set("Type", new CosName("Action")).Set("S", new CosName(kind))
            .Set("JS", script).Set("Payload", CosString.FromText(Secret));
        CosDictionary Allowed(string name) => new CosDictionary().Set("Type", new CosName("Action"))
            .Set("S", new CosName(name)).Set("D", new CosArray([fixture.PageRefs[1], new CosName("Fit")]));
        var uri = Allowed("URI").Set("URI", CosString.FromText("https://example.com/keep"));
        var goTo = fixture.Document.Add(Allowed("GoTo").Set("Next", forbidden));
        var chain = fixture.Document.Add(uri.Set("Next", fixture.Document.Add(new CosArray([forbidden, goTo, direct]))));
        uri["Cycle"] = chain; // Shared and cyclic graphs must still be cleaned.
        fixture.Catalog["OpenAction"] = forbidden;
        fixture.Catalog["AA"] = new CosDictionary().Set("WC", forbidden);
        fixture.Pages[0]["AA"] = new CosDictionary().Set("O", forbidden);
        fixture.Pages[1]["AA"] = new CosDictionary().Set("O", direct);
        fixture.Pages[1]["A"] = chain;
        fixture.Pages[1]["PA"] = forbidden;
        fixture.Pages[1]["AlternateActionReference"] = forbidden;
        var widget = fixture.Document.Add(new CosDictionary().Set("Subtype", new CosName("Widget"))
            .Set("Rect", CosArray.FromNumbers(5, 5, 20, 15)).Set("AA", new CosDictionary().Set("U", forbidden))
            .Set("A", direct));
        fixture.Pages[1]["Annots"] = new CosArray([widget]);
        fixture.Catalog["AcroForm"] = new CosDictionary().Set("Fields", new CosArray([widget]));

        var result = await Redact(fixture.Build());
        var output = CosDocument.Load(result.Output);
        Assert.Null(output.Catalog["OpenAction"]);
        Assert.Null(output.Catalog["AA"]);
        Assert.Null(output.GetPages()[0].Dictionary["AA"]);
        var page = output.GetPages()[1].Dictionary;
        Assert.Null(page["AA"]);
        Assert.Null(page["PA"]);
        Assert.Null(page["AlternateActionReference"]);
        var kept = Assert.IsType<CosDictionary>(output.Resolve(page["A"]));
        Assert.Equal("URI", Assert.IsType<CosName>(kept["S"]).Value);
        Assert.Equal("https://example.com/keep", Assert.IsType<CosString>(kept["URI"]).ToText());
        var next = Assert.IsType<CosArray>(output.Resolve(kept["Next"]));
        Assert.Single(next.Items);
        var keptGoTo = Assert.IsType<CosDictionary>(output.Resolve(next[0]));
        Assert.Equal("GoTo", Assert.IsType<CosName>(keptGoTo["S"]).Value);
        Assert.NotNull(keptGoTo["D"]);
        Assert.Null(keptGoTo["Next"]);
        var annotations = Assert.IsType<CosArray>(output.Resolve(page["Annots"]));
        var keptWidget = Assert.IsType<CosDictionary>(output.Resolve(Assert.Single(annotations.Items)));
        Assert.Null(keptWidget["AA"]);
        Assert.Null(keptWidget["A"]);
        Assert.DoesNotContain(Secret, DecodedObjects(result.Output), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, Encoding.Latin1.GetString(result.Output), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("URI")]
    [InlineData("GoTo")]
    [InlineData("Named")]
    [InlineData("Hide")]
    [InlineData("ResetForm")]
    [InlineData("SetOCGState")]
    [InlineData("Thread")]
    [InlineData("Trans")]
    public async Task Allowed_actions_keep_their_entries_and_open_action_but_not_aa(string kind)
    {
        var fixture = new Fixture("q Q", "q Q");
        var action = fixture.Document.Add(new CosDictionary().Set("Type", new CosName("Action"))
            .Set("S", fixture.Document.Add(new CosName(kind))).Set("Keep", CosString.FromText("allowed payload")));
        fixture.Catalog["OpenAction"] = action;
        fixture.Catalog["AA"] = new CosDictionary().Set("WC", action);
        fixture.Pages[1]["A"] = action;
        fixture.Pages[1]["PA"] = action;
        fixture.Pages[1]["AA"] = new CosDictionary().Set("O", action);
        var result = await Redact(fixture.Build());
        var output = CosDocument.Load(result.Output);
        foreach (var value in new[] { output.Catalog["OpenAction"], output.GetPages()[1].Dictionary["A"], output.GetPages()[1].Dictionary["PA"] })
        {
            var kept = Assert.IsType<CosDictionary>(output.Resolve(value));
            Assert.Equal("Action", Assert.IsType<CosName>(kept["Type"]).Value);
            Assert.Equal(kind, Assert.IsType<CosName>(output.Resolve(kept["S"])).Value);
            Assert.Equal("allowed payload", Assert.IsType<CosString>(kept["Keep"]).ToText());
        }
        Assert.Null(output.Catalog["AA"]);
        Assert.Null(output.GetPages()[1].Dictionary["AA"]);
    }

    [Fact]
    public async Task Open_action_destination_array_is_preserved()
    {
        var fixture = new Fixture("q Q", "q Q");
        fixture.Catalog["OpenAction"] = new CosArray([fixture.PageRefs[1], new CosName("Fit")]);
        var output = CosDocument.Load((await Redact(fixture.Build())).Output);
        var destination = Assert.IsType<CosArray>(output.Catalog["OpenAction"]);
        Assert.Equal(2, destination.Count);
        Assert.Same(output.GetPages()[1].Dictionary, output.Resolve(destination[0]));
        Assert.Equal("Fit", Assert.IsType<CosName>(destination[1]).Value);
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

    [Fact]
    public async Task Removed_annotations_detach_replies_and_separately_listed_popups()
    {
        var fixture = new Fixture("q Q", "q Q");
        CosDictionary Comment(string text, bool inside = false) => new CosDictionary()
            .Set("Type", new CosName("Annot")).Set("Subtype", new CosName("Text"))
            .Set("Rect", inside ? CosArray.FromNumbers(50, 45, 65, 55) : CosArray.FromNumbers(5, 5, 20, 15))
            .Set("Contents", CosString.FromText(text));
        var removedDictionary = Comment(Secret, true).Set("RC", CosString.FromText(Secret))
            .Set("AP", new CosDictionary().Set("N", fixture.Document.Add(CosPageEdit.Stream(Encoding.ASCII.GetBytes(Secret)))));
        var removed = fixture.Document.Add(removedDictionary);
        var popup = fixture.Document.Add(Comment(Secret).Set("Subtype", new CosName("Popup"))
            .Set("Parent", removed).Set("Popup", removed));
        removedDictionary["Popup"] = popup;
        var reply = fixture.Document.Add(Comment("surviving reply").Set("IRT", removed).Set("Popup", popup));
        var unrelated = fixture.Document.Add(Comment("unrelated comment"));
        var unrelatedReply = fixture.Document.Add(Comment("unrelated reply").Set("IRT", unrelated));
        fixture.Pages[0]["Annots"] = new CosArray([removed, popup, unrelated]);
        fixture.Pages[1]["Annots"] = new CosArray([reply, unrelatedReply]);
        // A nonstandard alias must not retain the removed annotation's payload either.
        fixture.Catalog["AnnotationAlias"] = removed;
        var result = await Redact(fixture.Build());
        Assert.DoesNotContain(Secret, DecodedObjects(result.Output), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, Encoding.Latin1.GetString(result.Output), StringComparison.Ordinal);
        var output = CosDocument.Load(result.Output);
        Assert.Single(((CosArray)output.Resolve(output.GetPages()[0].Dictionary["Annots"])!).Items);
        var replies = ((CosArray)output.Resolve(output.GetPages()[1].Dictionary["Annots"])!).Items
            .Select(v => (CosDictionary)output.Resolve(v)!).ToList();
        var kept = Assert.Single(replies, d => ((CosString)d["Contents"]!).ToText() == "surviving reply");
        Assert.Null(kept["IRT"]);
        Assert.Null(kept["Popup"]);
        Assert.NotNull(Assert.Single(replies, d => ((CosString)d["Contents"]!).ToText() == "unrelated reply")["IRT"]);
        Assert.Empty(((CosDictionary)output.Resolve(output.Catalog["AnnotationAlias"])!).Entries);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Nested_named_properties_are_cleaned_in_isolated_form_resources(bool shared, bool inherited)
    {
        var fixture = shared ? new Fixture("/Outer Do", "/Outer Do") : new Fixture("/Outer Do");
        var properties = fixture.Document.Add(new CosDictionary()
            .Set("Secret", fixture.Document.Add(new CosDictionary().Set("Alt", CosString.FromText(Secret))
                .Set("ActualText", CosString.FromText(Secret)).Set("E", CosString.FromText(Secret))))
            .Set("Keep", new CosDictionary().Set("ActualText", CosString.FromText("unrelated replacement"))));
        CosDictionary Header(CosDictionary resources) => new CosDictionary().Set("Type", new CosName("XObject"))
            .Set("Subtype", new CosName("Form")).Set("BBox", CosArray.FromNumbers(0, 0, 120, 100)).Set("Resources", resources);
        var innerHeader = Header(new CosDictionary().Set("Properties", properties));
        if (inherited) innerHeader["Resources"] = null;
        var inner = fixture.Document.Add(CosPageEdit.Stream(Encoding.ASCII.GetBytes(
            "/Span /Secret BDC 50 45 10 10 re f EMC /Span /Keep BDC 5 5 10 10 re f EMC"),
            innerHeader));
        var outer = fixture.Document.Add(CosPageEdit.Stream("/Inner Do"u8.ToArray(),
            Header(new CosDictionary().Set("XObject", new CosDictionary().Set("Inner", inner))
                .Set("Properties", inherited ? properties : new CosDictionary()))));
        fixture.Resources["XObject"] = new CosDictionary().Set("Outer", outer);
        var input = fixture.Build();
        var result = await Redact(input);
        Assert.Empty(result.ImagePages);
        var output = CosDocument.Load(result.Output);
        CosDictionary InnerProperties(int page)
        {
            var res = (CosDictionary)output.Resolve(output.GetPages()[page].Resources)!;
            foreach (var name in new[] { "Outer", "Inner" })
            {
                var xs = (CosDictionary)output.Resolve(res["XObject"])!;
                res = output.Resolve(((CosStream)output.Resolve(xs[name])!).Dictionary["Resources"]) as CosDictionary ?? res;
            }
            return (CosDictionary)output.Resolve(res["Properties"])!;
        }
        var cleaned = InnerProperties(0);
        var secret = (CosDictionary)output.Resolve(cleaned["Secret"])!;
        foreach (var key in new[] { "ActualText", "Alt", "E" }) Assert.Null(secret[key]);
        Assert.Equal("unrelated replacement", ((CosString)((CosDictionary)output.Resolve(cleaned["Keep"])!)["ActualText"]!).ToText());
        if (shared)
        {
            Assert.Equal(Secret, ((CosString)((CosDictionary)output.Resolve(InnerProperties(1)["Secret"])!)["Alt"]!).ToText());
            await using var before = await Open(input);
            await using var after = await Open(result.Output);
            Assert.Equal((await before.RenderPageAsync(1, 240, 200)).Pixels, (await after.RenderPageAsync(1, 240, 200)).Pixels);
        }
        else Assert.DoesNotContain(Secret, DecodedObjects(result.Output), StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_text_edit_keeps_named_and_inline_replacement_text()
    {
        var fixture = new Fixture("/Span /Keep BDC BT /F1 10 Tf 5 10 Td (hello) Tj ET EMC "
            + "/Span << /ActualText (inline replacement) >> BDC BT /F1 10 Tf 50 50 Td (edit) Tj ET EMC");
        fixture.Resources["Font"] = new CosDictionary().Set("F1", fixture.Document.Add(new CosDictionary()
            .Set("Type", new CosName("Font")).Set("Subtype", new CosName("Type1")).Set("BaseFont", new CosName("Helvetica"))
            .Set("FirstChar", new CosInteger(32)).Set("LastChar", new CosInteger(126))
            .Set("Widths", new CosArray(Enumerable.Range(32, 95).Select(_ => new CosInteger(600))))));
        fixture.Resources["Properties"] = new CosDictionary().Set("Keep", new CosDictionary()
            .Set("ActualText", CosString.FromText("unrelated actual text")).Set("Alt", CosString.FromText("unrelated alt"))
            .Set("E", CosString.FromText("unrelated expansion")));
        var output = CosDocument.Load(PdfTextEditor.Apply(fixture.Build(), [new PdfTextEdit(0, Area, null)]));
        var resources = (CosDictionary)output.Resolve(output.GetPages()[0].Resources)!;
        var property = (CosDictionary)((CosDictionary)output.Resolve(resources["Properties"])!)["Keep"]!;
        Assert.Equal("unrelated actual text", ((CosString)property["ActualText"]!).ToText());
        Assert.Equal("unrelated alt", ((CosString)property["Alt"]!).ToText());
        Assert.Equal("unrelated expansion", ((CosString)property["E"]!).ToText());
        Assert.Contains("inline replacement", Encoding.Latin1.GetString(new CosPageEdit(output, output.GetPages()[0]).ReadContent()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Type3_glyph_using_page_image_resources_keeps_its_render(bool rasterOtherPage, bool glyphOnMarkedPage)
    {
        var fixture = new Fixture(glyphOnMarkedPage ? "BT /Glyph 10 Tf 5 5 Td (A) Tj ET" : rasterOtherPage ? "q 120 0 0 100 0 0 cm /Im Do Q" : "q Q",
            "BT /Glyph 30 Tf 5 5 Td (A) Tj ET");
        fixture.Resources["XObject"] = new CosDictionary().Set("Im", fixture.AddImage(Encoding.ASCII.GetBytes("PRIVATE-IMAGE-12345")));
        var glyph = fixture.Document.Add(CosPageEdit.Stream("1000 0 d0 q 1000 0 0 1000 0 0 cm /Im Do Q"u8.ToArray()));
        fixture.Resources["Font"] = new CosDictionary().Set("Glyph", fixture.Document.Add(new CosDictionary()
            .Set("Type", new CosName("Font")).Set("Subtype", new CosName("Type3"))
            .Set("FontBBox", CosArray.FromNumbers(0, 0, 1000, 1000)).Set("FontMatrix", CosArray.FromNumbers(.001, 0, 0, .001, 0, 0))
            .Set("CharProcs", new CosDictionary().Set("A", glyph)).Set("FirstChar", new CosInteger(65)).Set("LastChar", new CosInteger(65))
            .Set("Widths", CosArray.FromNumbers(1000)).Set("Encoding", new CosDictionary().Set("Differences", new CosArray([new CosInteger(65), new CosName("A")])))));
        var input = fixture.Build();
        var result = await Redact(input);
        await using var before = await Open(input);
        await using var after = await Open(result.Output);
        var bitmap = await before.RenderPageAsync(1, 240, 200);
        Assert.Contains(bitmap.Pixels, p => p < 200);
        Assert.Equal(bitmap.Pixels, (await after.RenderPageAsync(1, 240, 200)).Pixels);
        if (glyphOnMarkedPage)
        {
            Assert.Empty(result.ImagePages);
            var original = await before.RenderPageAsync(0, 240, 200);
            var redacted = await after.RenderPageAsync(0, 240, 200);
            // The glyph at the bottom-left is outside the redaction area.
            for (var y = 160; y < 195; y++)
                Assert.Equal(original.Pixels.AsSpan(y * original.Stride, 80 * 4).ToArray(),
                    redacted.Pixels.AsSpan(y * redacted.Stride, 80 * 4).ToArray());
        }
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
