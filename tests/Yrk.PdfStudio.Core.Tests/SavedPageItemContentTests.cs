using System.Text;
using Xunit.Abstractions;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SavedPageItemContentTests(ITestOutputHelper output) : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-item-content-");
    public void Dispose() { _engine.Dispose(); _folder.Delete(true); }
    private string PathFor(string name) => Path.Combine(_folder.FullName, name);

    // All three neighbours and the edited occurrence deliberately share ONE content stream.
    // Otherwise PDFium can leave an untouched stream alone and conceal regeneration loss.
    private static byte[] AddNeighbours(byte[] bytes)
    {
        var doc = CosDocument.Load(bytes);
        var update = new CosIncrementalUpdate(doc);
        var edit = new CosPageEdit(doc, doc.GetPages()[0]);
        var function = new CosDictionary().Set("FunctionType", new CosInteger(2))
            .Set("Domain", CosArray.FromNumbers(0, 1)).Set("C0", CosArray.FromNumbers(0, 1, 0))
            .Set("C1", CosArray.FromNumbers(0, 1, 1)).Set("N", new CosInteger(1));
        var shading = new CosDictionary().Set("ShadingType", new CosInteger(2))
            .Set("ColorSpace", new CosName("DeviceRGB")).Set("Coords", CosArray.FromNumbers(160, 280, 240, 280))
            .Set("Function", function).Set("Extend", new CosArray([new CosBoolean(true), new CosBoolean(true)]));
        var name = edit.AddResource("Shading", "Neighbour", update.Add(shading));
        var neighbours = $"""
            q 80 0 0 80 40 280 cm
            BI /W 1 /H 1 /BPC 8 /CS /RGB /F /AHx ID
            0000ff>
            EI
            Q
            q 160 280 80 80 re W n /{name} sh Q
            /Span << /MCID 7 /ActualText (NEIGHBOUR) >> BDC
            q 1 0 1 rg 280 280 80 80 re f Q
            EMC
            """;
        edit.ReplaceContent([.. Encoding.Latin1.GetBytes(neighbours + "\n"), .. edit.ReadContent()]);
        edit.Commit(update);
        return update.Build().Bytes;
    }

    private async Task<byte[]> Fixture(bool legacy)
    {
        var b = new CosNewDocument();
        var root = b.Reserve(); var pages = b.Reserve();
        var page = b.Add(new CosDictionary().Set("Type", new CosName("Page")).Set("Parent", pages)
            .Set("MediaBox", CosArray.FromNumbers(0, 0, 400, 400)).Set("Resources", new CosDictionary()));
        b.Set(pages, new CosDictionary().Set("Type", new CosName("Pages")).Set("Kids", new CosArray([page])).Set("Count", new CosInteger(1)));
        b.Set(root, new CosDictionary().Set("Type", new CosName("Catalog")).Set("Pages", pages));
        var bytes = b.Build(root);
        var mark = new YrkMark(YrkMarkKind.Image, Guid.NewGuid());
        if (legacy)
        {
            // The pre-group format, with a YRK mark directly on an image occurrence.
            var doc = CosDocument.Load(bytes);
            var update = new CosIncrementalUpdate(doc);
            var edit = new CosPageEdit(doc, doc.GetPages()[0]);
            var image = update.Add(CosPageEdit.Stream([255, 0, 0], new CosDictionary()
                .Set("Type", new CosName("XObject")).Set("Subtype", new CosName("Image"))
                .Set("Width", new CosInteger(1)).Set("Height", new CosInteger(1))
                .Set("ColorSpace", new CosName("DeviceRGB")).Set("BitsPerComponent", new CosInteger(8))));
            var name = edit.AddResource("XObject", "Legacy", image);
            edit.ReplaceContent(Encoding.Latin1.GetBytes($"q 80 0 0 80 40 60 cm /YRK <</Kind(Image)/Group({mark.Group})/V(1)>> BDC /{name} Do EMC Q\n"));
            edit.Commit(update);
            bytes = update.Build().Bytes;
        }
        else
        {
            await using var d = await _engine.OpenBytesAsync(bytes, PathFor("seed.pdf"));
            await d.AddImageAsync(0, new(1, 1, [0, 0, 255, 255]), new(.1, .65, .2, .2), style: new(Mark: mark));
            bytes = await d.SaveDecryptedAsync();
        }
        return AddNeighbours(bytes);
    }

    [Theory]
    [InlineData(false, "move")] [InlineData(false, "opacity")] [InlineData(false, "delete")]
    [InlineData(true, "move")] [InlineData(true, "opacity")] [InlineData(true, "delete")]
    public async Task Unsafe_page_refuses_item_edits_before_mutation_and_preserves_all_content(bool legacy, string operation)
    {
        var bytes = await Fixture(legacy);
        AssertSyntax(bytes, requireInline: true);
        await using var d = await _engine.OpenBytesAsync(bytes, PathFor("source.pdf"));
        var before = await d.RenderPageAsync(0, 400, 400);
        // Prove that each fixture really paints its intended region before comparing it.
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(before, 80, 80)); // inline blue
        Assert.Equal(255, Pixel(before, 200, 80)[1]); // shading green channel
        Assert.InRange(Pixel(before, 200, 80)[0], 100, 160); // gradient blue channel
        Assert.Equal(new byte[] { 255, 0, 255, 255 }, Pixel(before, 320, 80)); // marked magenta
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(before, 80, 300)); // YRK red
        var item = Assert.Single(await d.GetPageItemsAsync(0), i => i.HasYrkMark);
        Assert.Contains("cannot be regenerated safely", item.EditError);
        var error = await Assert.ThrowsAsync<PdfException>(Edit);
        Assert.Equal(item.EditError, error.Message);
        output.WriteLine($"{operation}: refused before mutation: {error.Message}");
        Assert.Equal(item, Assert.Single(await d.GetPageItemsAsync(0), i => i.HasYrkMark));
        Assert.Equal(before.Pixels, (await d.RenderPageAsync(0, 400, 400)).Pixels);
        CheckNeighbours(before, await d.RenderPageAsync(0, 400, 400), "in memory");
        var path = PathFor("edited.pdf");
        await d.SaveAsync(path);
        await using var reopened = await _engine.OpenAsync(path);
        var after = await reopened.RenderPageAsync(0, 400, 400);
        CheckNeighbours(before, after, "saved/reopened");
        Assert.Equal(before.Pixels, after.Pixels);
        AssertSyntax(File.ReadAllBytes(path), requireInline: true);
        Assert.Equal(ReadContent(bytes), ReadContent(File.ReadAllBytes(path)));
        Assert.Single(await reopened.GetPageItemsAsync(0), i => i.HasYrkMark);

        Task Edit() => operation switch
        {
            "move" => d.MovePageItemAsync(item.Reference, new(.4, 0)),
            "opacity" => d.SetPageItemOpacityAsync(item.Reference, .5),
            "delete" => d.RemovePageItemAsync(item.Reference),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private void CheckNeighbours(PdfBitmap before, PdfBitmap after, string stage)
    {
        var differences = new[] { 40, 160, 280 }.Select(x0 =>
            Enumerable.Range(40, 80).Sum(y => Enumerable.Range(x0, 80)
                .Count(x => !Pixel(before, x, y).SequenceEqual(Pixel(after, x, y))))).ToArray();
        var message = $"{stage}: changed pixels / 6400: inline={differences[0]}, shading={differences[1]}, marked={differences[2]}";
        output.WriteLine(message);
        Assert.True(differences.All(n => n == 0), message);
    }

    private static byte[] Pixel(PdfBitmap b, int x, int y) => b.Pixels.AsSpan(y * b.Stride + x * 4, 4).ToArray();

    private static byte[] ReadContent(byte[] bytes)
    {
        var doc = CosDocument.Load(bytes);
        return new CosPageEdit(doc, doc.GetPages()[0]).ReadContent();
    }

    private static void AssertSyntax(byte[] bytes, bool requireInline)
    {
        var doc = CosDocument.Load(bytes);
        var ops = ContentTokenizer.Parse(new CosPageEdit(doc, doc.GetPages()[0]).ReadContent());
        if (requireInline) Assert.Contains(ops, o => o.Operator == "BI");
        Assert.Contains(ops, o => o.Operator == "sh");
        Assert.Contains(ops, o => o.Operator == "BDC" && o.Operands.LastOrDefault() is CosDictionary dict
            && (dict["MCID"] as CosInteger)?.Value == 7 && (dict["ActualText"] as CosString)?.ToText() == "NEIGHBOUR");
        Assert.Contains(ops, o => o.Operator == "EMC");
    }
}
