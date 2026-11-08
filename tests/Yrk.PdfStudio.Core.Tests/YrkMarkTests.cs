using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Signatures;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class YrkMarkTests : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-marks-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    private string PathFor(string name) => Path.Combine(_folder.FullName, name);

    private static byte[] Blank(int rotation = 0)
    {
        var document = CosDocument.Load(SignatureFormFillTests.BlankDocument());
        var update = new CosIncrementalUpdate(document);
        var page = document.GetPages()[0];
        update.Replace(page.Id, page.Dictionary.Clone().Set("Rotate", new CosInteger(rotation)));
        return update.Build().Bytes;
    }

    private static PdfImage Image => new(2, 1, [0, 0, 255, 200, 255, 0, 0, 255]);

    private static PdfVectorArt Vector => new(2, 1,
    [
        new PdfVectorPath([PdfPathCommand.Move(0, 0), PdfPathCommand.Line(1, 0),
            PdfPathCommand.Line(1, 1), PdfPathCommand.Closing], new PdfColor(255, 0, 0)),
        new PdfVectorPath([PdfPathCommand.Move(1, 0), PdfPathCommand.Line(2, 0),
            PdfPathCommand.Line(2, 1), PdfPathCommand.Closing], new PdfColor(0, 0, 255)),
    ]);

    [Theory]
    [InlineData("image", 0)]
    [InlineData("image", 90)]
    [InlineData("stamp", 0)]
    [InlineData("stamp", 90)]
    [InlineData("signature-image", 0)]
    [InlineData("signature-image", 90)]
    [InlineData("vector", 0)]
    [InlineData("vector", 90)]
    [InlineData("pdf", 0)]
    [InlineData("pdf", 90)]
    public async Task Placement_marks_round_trip_without_changing_any_pixels(string artwork, int rotation)
    {
        var kind = artwork switch
        {
            "image" => YrkMarkKind.Image,
            "stamp" => YrkMarkKind.Stamp,
            _ => YrkMarkKind.Signature,
        };
        var mark = new YrkMark(kind, Guid.NewGuid());
        var source = PathFor("source.pdf");
        File.WriteAllBytes(source, Blank(rotation));
        var art = PdfTextWriter.AddText(Blank(), TextBlocks());
        PdfBitmap? baseline = null;
        foreach (var marked in new[] { false, true })
        {
            var target = PathFor($"{marked}.pdf");
            await using (var document = await _engine.OpenAsync(source))
            {
                var style = new PlacementStyle(27, artwork == "pdf" ? 1 : 0.6, marked ? mark : null);
                var bounds = new NormalizedRect(0.2, 0.3, 0.5, 0.25);
                if (artwork == "vector") await document.AddVectorAsync(0, Vector, bounds, style: style);
                else if (artwork == "pdf") await document.AddPdfPageAsync(0, art, new(0, 0, 1, 1), bounds, style: style);
                else await document.AddImageAsync(0, Image, bounds, style: style);
                await document.SaveAsync(target);
            }

            var objects = await ReadObjectsAsync(File.ReadAllBytes(target));
            Assert.Equal(artwork == "vector" ? 2 : 1, objects.Count);
            if (marked) Assert.All(objects, obj => AssertMark(Assert.Single(obj.Marks), mark));
            else Assert.All(objects, obj => Assert.Empty(obj.Marks));
            await using var reloaded = await _engine.OpenAsync(target);
            var bitmap = await reloaded.RenderPageAsync(0, 600, 600);
            if (baseline is null) baseline = bitmap;
            else Assert.Equal(baseline.Pixels, bitmap.Pixels);
        }
    }

    // The text includes stacked Thai glyphs: both text and outline objects must carry the mark.
    private static IReadOnlyList<PdfTextBlock> TextBlocks() => Sta.Run(() => TextShaper.ToBlocks(
        [(0, new NormalizedPoint(0.15, 0.3), new PdfColor(20, 40, 160),
            TextShaper.Shape("Hello สำเนาถูกต้อง น้ำใจ", "Leelawadee UI", false, false, 18))]));

    [Theory]
    [InlineData(YrkMarkKind.Text, 0, false)]
    [InlineData(YrkMarkKind.Text, 90, true)]
    [InlineData(YrkMarkKind.StampText, 0, true)]
    [InlineData(YrkMarkKind.StampText, 90, false)]
    public async Task Inline_text_dictionary_is_readable_by_PDFium_after_save_and_reload(
        YrkMarkKind kind, int rotation, bool onLayer)
    {
        var source = Blank(rotation);
        string? layerId = null;
        if (onLayer)
        {
            var document = CosDocument.Load(source);
            var update = new CosIncrementalUpdate(document);
            var layer = update.Add(new CosDictionary().Set("Type", new CosName("OCG")).Set("Name", CosString.FromText("Test")));
            layerId = $"{layer.Id.Number} {layer.Id.Generation}";
            update.Replace(((CosReference)document.Trailer["Root"]!).Id, document.Catalog.Clone().Set("OCProperties", new CosDictionary()
                .Set("OCGs", new CosArray([layer]))
                .Set("D", new CosDictionary().Set("Order", new CosArray([layer])))));
            source = update.Build().Bytes;
        }

        var mark = new YrkMark(kind, Guid.NewGuid());
        var blocks = TextBlocks().Select(b => b with { LayerId = layerId, Opacity = 0.6 }).ToList();
        PdfBitmap? baseline = null;
        foreach (var marked in new[] { false, true })
        {
            var bytes = PdfTextWriter.AddText(source, blocks.Select(b => b with { Mark = marked ? mark : null }).ToList());
            if (marked)
            {
                // Assert the implementation really uses an inline property dictionary, not a resource name.
                var written = CosDocument.Load(bytes);
                var content = new CosPageEdit(written, written.GetPages()[0]).ReadContent();
                Assert.Contains($"/YRK <</Kind({kind})/Group({mark.Group})/V(1)>> BDC", Encoding.Latin1.GetString(content));
            }
            var path = PathFor($"text-{marked}.pdf");
            File.WriteAllBytes(path, bytes);
            // Read the raw writer output, then exercise PDFium's own serializer and reload again.
            CheckObjects(await ReadObjectsAsync(bytes));
            var saved = PathFor($"resaved-{marked}.pdf");
            await using (var document = await _engine.OpenAsync(path)) await document.SaveAsync(saved);
            CheckObjects(await ReadObjectsAsync(File.ReadAllBytes(saved)));
            await using var reloaded = await _engine.OpenAsync(saved);
            var bitmap = await reloaded.RenderPageAsync(0, 600, 600);
            Assert.Contains("Hello", await reloaded.GetPageTextAsync(0));
            if (baseline is null) baseline = bitmap;
            else Assert.Equal(baseline.Pixels, bitmap.Pixels);

            void CheckObjects(IReadOnlyList<ObjectMarks> objects)
            {
                Assert.Contains(objects, obj => obj.Type == 1); // FPDF_PAGEOBJ_TEXT
                Assert.All(objects, obj =>
                {
                    var yrk = obj.Marks.Where(m => m.Name == "YRK").ToList();
                    if (marked) AssertMark(Assert.Single(yrk), mark);
                    else Assert.Empty(yrk);
                    Assert.Equal(onLayer, obj.Marks.Any(m => m.Name == "OC"));
                });
            }
        }
    }

    [Fact]
    public async Task Stamp_frame_and_words_share_one_group_after_save_and_reload()
    {
        var group = Guid.NewGuid();
        var frame = new YrkMark(YrkMarkKind.Stamp, group);
        var words = new YrkMark(YrkMarkKind.StampText, group);
        var source = PathFor("stamp-source.pdf");
        var saved = PathFor("stamp-frame.pdf");
        File.WriteAllBytes(source, Blank());
        await using (var document = await _engine.OpenAsync(source))
        {
            await document.AddImageAsync(0, Image, new(0.1, 0.1, 0.6, 0.4), style: new(Mark: frame));
            await document.SaveAsync(saved);
        }
        var bytes = PdfTextWriter.AddText(File.ReadAllBytes(saved), TextBlocks().Select(b => b with { Mark = words }).ToList());
        var objects = await ReadObjectsAsync(bytes);
        AssertMark(Assert.Single(Assert.Single(objects, obj => obj.Type == 3).Marks), frame);
        Assert.All(objects.Where(obj => obj.Type != 3), obj => AssertMark(Assert.Single(obj.Marks), words));
    }

    [Fact]
    public async Task Marked_image_incremental_save_preserves_the_original_signed_revision()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=YRK mark test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var signer = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var options = new PdfVerifyOptions(CheckRevocation: false) { CustomTrustRoots = new X509Certificate2Collection(signer) };
        var signed = await PdfSigner.SignAsync(Blank(), new(signer));
        Assert.Equal(PdfSignatureStatus.Valid, Assert.Single(PdfSignatureVerifier.Verify(signed, options)).Status);
        var source = PathFor("signed.pdf");
        File.WriteAllBytes(source, signed);
        var mark = new YrkMark(YrkMarkKind.Image, Guid.NewGuid());
        var target = PathFor("signed-with-image.pdf");
        await using (var document = await _engine.OpenAsync(source))
        {
            await document.AddImageAsync(0, Image, new(0.2, 0.3, 0.4, 0.2), style: new(Mark: mark));
            await document.SaveAsync(target);
        }
        var bytes = File.ReadAllBytes(target);
        Assert.Equal(signed, bytes[..signed.Length]);
        var result = Assert.Single(PdfSignatureVerifier.Verify(bytes, options));
        Assert.True(result.IsIntact);
        Assert.Equal(PdfSignerIdentity.Trusted, result.Identity);
        // Page content is a later edit, exactly as in the existing incremental-update tests.
        Assert.True(result.ModifiedAfterSigning);
        Assert.Equal(PdfSignatureStatus.Modified, result.Status);
        Assert.Equal(signed, PdfSignatureVerifier.GetSignedRevision(bytes, result));
        AssertMark(Assert.Single(Assert.Single(await ReadObjectsAsync(bytes), obj => obj.Type == 3).Marks), mark);
    }

    internal sealed record MarkValues(string? Name, string? Kind, string? Group, string? Version);
    internal sealed record ObjectMarks(int Type, IReadOnlyList<MarkValues> Marks);

    internal static void AssertMark(MarkValues actual, YrkMark expected)
    {
        Assert.Equal("YRK", actual.Name);
        Assert.Equal(expected.Kind.ToString(), actual.Kind);
        Assert.Equal(expected.Group, actual.Group);
        Assert.True(Guid.TryParseExact(actual.Group, "N", out _));
        Assert.Equal("1", actual.Version);
    }

    internal static Task<IReadOnlyList<ObjectMarks>> ReadObjectsAsync(byte[] bytes) => PdfiumWorker.Shared.InvokeAsync<IReadOnlyList<ObjectMarks>>(() =>
    {
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var document = PdfiumNative.FPDF_LoadMemDocument64(pinned.AddrOfPinnedObject(), (nuint)bytes.Length, null);
            Assert.NotEqual(IntPtr.Zero, document);
            try
            {
                var page = PdfiumNative.FPDF_LoadPage(document, 0);
                Assert.NotEqual(IntPtr.Zero, page);
                try
                {
                    var objects = new List<ObjectMarks>();
                    for (var index = 0; index < PdfiumNative.FPDFPage_CountObjects(page); index++)
                    {
                        var obj = PdfiumNative.FPDFPage_GetObject(page, index);
                        var marks = new List<MarkValues>();
                        var count = PdfiumNative.FPDFPageObj_CountMarks(obj);
                        Assert.True(count >= 0);
                        for (uint i = 0; i < count; i++)
                        {
                            var mark = PdfiumNative.FPDFPageObj_GetMark(obj, i);
                            Assert.NotEqual(IntPtr.Zero, mark);
                            marks.Add(new(PdfiumMarks.GetName(mark), PdfiumMarks.GetString(mark, "Kind"),
                                PdfiumMarks.GetString(mark, "Group"), PdfiumMarks.GetString(mark, "V")));
                        }
                        objects.Add(new(PdfiumNative.FPDFPageObj_GetType(obj), marks));
                    }
                    return objects;
                }
                finally { PdfiumNative.FPDF_ClosePage(page); }
            }
            finally { PdfiumNative.FPDF_CloseDocument(document); }
        }
        finally { pinned.Free(); }
    }, PdfWorkPriority.Interactive, CancellationToken.None);
}
