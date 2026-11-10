using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Security;
using Yrk.PdfStudio.Core.Pdf.Signatures;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SavedPageItemTests : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-items-");
    private string PathFor(string name) => Path.Combine(_folder.FullName, name);
    public void Dispose() { _engine.Dispose(); _folder.Delete(true); }

    private static PdfImage Red(byte alpha = 255) => new(2, 2, Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 0, 0, 255, alpha }).ToArray());
    private static PdfVectorPath Rect(double x, double y, double w, double h) => new(
        [PdfPathCommand.Move(x, y), PdfPathCommand.Line(x + w, y), PdfPathCommand.Line(x + w, y + h), PdfPathCommand.Line(x, y + h), PdfPathCommand.Closing], new(255, 0, 0));
    private static PdfVectorArt Cross => new(100, 100, [Rect(0, 40, 100, 20), Rect(40, 0, 20, 100)]);

    private static byte[] Blank(int rotation = 0)
    {
        var b = new CosNewDocument();
        var root = b.Reserve(); var pages = b.Reserve();
        var page = b.Add(new CosDictionary().Set("Type", new CosName("Page")).Set("Parent", pages)
            .Set("MediaBox", CosArray.FromNumbers(0, 0, 400, 400)).Set("Rotate", new CosInteger(rotation))
            .Set("Resources", new CosDictionary()));
        b.Set(pages, new CosDictionary().Set("Type", new CosName("Pages")).Set("Kids", new CosArray([page])).Set("Count", new CosInteger(1)));
        b.Set(root, new CosDictionary().Set("Type", new CosName("Catalog")).Set("Pages", pages));
        return b.Build(root);
    }

    private async Task<IPdfDocument> Open(byte[] bytes, string? password = null) =>
        await _engine.OpenBytesAsync(bytes, PathFor(Guid.NewGuid() + ".pdf"), password);
    private static async Task<PdfPageItem> Item(IPdfDocument d, string? group = null) =>
        Assert.Single(await d.GetPageItemsAsync(0), i => group is null || i.Group == group);
    private static int Green(PdfBitmap b, int x, int y) => b.Pixels[y * b.Stride + x * 4 + 1];
    private static async Task<PdfBitmap> Render(IPdfDocument d) => await d.RenderPageAsync(0, 400, 400);
    private static string[] ImageHashes(byte[] pdf, string? password = null)
    {
        var d = CosDocument.Load(pdf, password);
        return d.ObjectIds.Select(d.GetObject).OfType<CosStream>()
            .Where(s => (s.Dictionary["Subtype"] as CosName)?.Value == "Image")
            .Select(s => Convert.ToHexString(SHA256.HashData(s.Data.Span))).Order().ToArray();
    }

    [Fact]
    public async Task Crossing_pixels_match_single_line_pixels_before_and_after_saving()
    {
        await using var d = await Open(Blank());
        await d.AddVectorAsync(0, Cross, new(.1, .1, .25, .25), style: new(Opacity: .5,
            Mark: new(YrkMarkKind.Signature, Guid.NewGuid())));
        Check(await Render(d));
        var path = PathFor("cross.pdf"); await d.SaveAsync(path);
        await using var reopened = await _engine.OpenAsync(path);
        Check(await Render(reopened));
        static void Check(PdfBitmap bitmap)
        {
            Assert.InRange(Green(bitmap, 60, 90), 126, 129);
            Assert.InRange(Math.Abs(Green(bitmap, 60, 90) - Green(bitmap, 90, 90)), 0, 2);
        }
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)] [InlineData(false, 90)] [InlineData(true, 90)]
    [InlineData(false, 180)] [InlineData(true, 270)]
    public async Task New_groups_move_rotate_fade_restore_and_delete_after_reopen(bool vector, int pageRotation)
    {
        var mark = new YrkMark(vector ? YrkMarkKind.Signature : YrkMarkKind.Image, Guid.NewGuid());
        await using var d = await Open(Blank(pageRotation));
        if (vector) await d.AddVectorAsync(0, Cross, new(.1, .1, .25, .25), style: new(Opacity: .5, Mark: mark));
        else await d.AddImageAsync(0, Red(), new(.1, .1, .25, .25), style: new(Opacity: .5, Mark: mark));
        var initial = await Item(d);
        Assert.Equal(PdfItemOpacityMode.Group, initial.OpacityMode);
        Assert.Null(initial.EditError);
        Assert.InRange(Green(await Render(d), 60, 90), 126, 129);
        if (vector) Assert.InRange(Green(await Render(d), 90, 90), 126, 129); // crossing = single line
        var before = await d.SaveDecryptedAsync();
        await d.SetPageItemOpacityAsync((await Item(d)).Reference, 0);
        Assert.Equal(255, Green(await Render(d), 60, 90));
        await d.SetPageItemOpacityAsync((await Item(d)).Reference, 1);
        Assert.InRange(Green(await Render(d), 60, 90), 0, 1);
        await d.SetPageItemOpacityAsync((await Item(d)).Reference, .25);
        await d.MovePageItemAsync((await Item(d)).Reference, new(.4, .3));
        await d.RotatePageItemAsync((await Item(d)).Reference, 90);
        var target = PathFor("edited.pdf"); await d.SaveAsync(target);
        await using var reopened = await _engine.OpenAsync(target);
        var item = await Item(reopened);
        Assert.Equal(mark.Group, item.Group); Assert.Equal(mark.Kind, item.Kind);
        Assert.InRange(item.Bounds.X, .499, .501); Assert.InRange(item.Bounds.Y, .399, .401);
        Assert.InRange(item.Opacity, .249, .253);
        Assert.InRange(Green(await Render(reopened), 220, 210), 190, 193);
        Assert.Equal(ImageHashes(before), ImageHashes(File.ReadAllBytes(target)));
        await reopened.RemovePageItemAsync(item.Reference);
        var deleted = PathFor("deleted.pdf"); await reopened.SaveAsync(deleted);
        await using var final = await _engine.OpenAsync(deleted);
        Assert.Empty(await final.GetPageItemsAsync(0)); Assert.Equal(255, Green(await Render(final), 220, 210));
    }

    [Fact]
    public async Task Stamp_words_remain_in_one_group_and_searchable_through_every_edit()
    {
        var group = Guid.NewGuid();
        await using var frame = await Open(Blank());
        await frame.AddImageAsync(0, Red(), new(.1, .1, .3, .2), style: new(Opacity: .5, Mark: new(YrkMarkKind.Stamp, group)));
        var blocks = Sta.Run(() => TextShaper.ToBlocks([(0, new NormalizedPoint(.15, .2), new PdfColor(0, 0, 0),
            TextShaper.Shape("StampText สำเนา", "Leelawadee UI", false, false, 14))]))
            .Select(b => b with { Mark = new(YrkMarkKind.StampText, group), Opacity = .5 }).ToArray();
        var file = PdfTextWriter.AddText(await frame.SaveDecryptedAsync(), blocks);
        await using var d = await Open(file);
        var one = await Item(d);
        Assert.Equal(YrkMarkKind.Stamp, one.Kind); Assert.Equal(PdfItemOpacityMode.Group, one.OpacityMode);
        Assert.Contains("StampText", await d.GetPageTextAsync(0));
        Assert.NotEmpty(await d.FindTextAsync(0, "StampText", PdfSearchOptions.None));
        // The black words must really render, rather than only surviving as invisible searchable text.
        var bm = await Render(d);
        Assert.Contains(Enumerable.Range(72, 28), y => Enumerable.Range(60, 90).Any(x => bm.Pixels[y * bm.Stride + x * 4 + 2] < 200));
        Assert.InRange(Enumerable.Range(0, bm.Height).SelectMany(y => Enumerable.Range(0, bm.Width)
            .Select(x => (int)bm.Pixels[y * bm.Stride + x * 4 + 2])).Min(), 126, 129);
        await d.MovePageItemAsync(one.Reference, new(.2, .3));
        await d.RotatePageItemAsync((await Item(d)).Reference, 35);
        await d.SetPageItemOpacityAsync((await Item(d)).Reference, .75);
        var path = PathFor("stamp.pdf"); await d.SaveAsync(path);
        await using var reopened = await _engine.OpenAsync(path);
        Assert.Contains("StampText", await reopened.GetPageTextAsync(0));
        Assert.NotEmpty(await reopened.FindTextAsync(0, "StampText", PdfSearchOptions.None));
        Assert.Equal(group.ToString("N"), (await Item(reopened)).Group);
        var edited = await Render(reopened);
        Assert.InRange(Enumerable.Range(0, edited.Height).SelectMany(y => Enumerable.Range(0, edited.Width)
            .Select(x => (int)edited.Pixels[y * edited.Stride + x * 4 + 2])).Min(), 62, 67);
        await reopened.RemovePageItemAsync((await Item(reopened)).Reference);
        Assert.DoesNotContain("StampText", await reopened.GetPageTextAsync(0));
    }

    [Fact]
    public async Task Attaching_separated_stamp_words_clones_shared_forms_and_resources()
    {
        var stamp = new YrkMark(YrkMarkKind.Stamp, Guid.NewGuid());
        await using var frames = await Open(Blank());
        await frames.AddImageAsync(0, Red(), new(.1, .1, .3, .2), style: new(Mark: stamp));
        await frames.AddImageAsync(0, Red(), new(.7, .7, .2, .2), style: new(Mark: new(YrkMarkKind.Image, Guid.NewGuid())));
        var cos = CosDocument.Load(await frames.SaveDecryptedAsync());
        var update = new CosIncrementalUpdate(cos);
        // Both pages invoke the very same stamp form, image, mask and alpha resources.
        var second = update.Add(cos.GetPages()[0].Dictionary.Clone());
        var pagesRef = Assert.IsType<CosReference>(cos.Catalog["Pages"]);
        var pages = Assert.IsType<CosDictionary>(cos.Resolve(pagesRef)).Clone();
        pages["Kids"] = new CosArray([new CosReference(cos.GetPages()[0].Id), second]);
        pages["Count"] = new CosInteger(2);
        update.Replace(pagesRef.Id, pages);
        var shared = update.Build().Bytes;
        var original = CosDocument.Load(shared);
        await using var before = await Open(shared);
        var otherPage = await before.RenderPageAsync(1, 400, 400);
        var blocks = Sta.Run(() => TextShaper.ToBlocks([(0, new NormalizedPoint(.15, .15), new PdfColor(0, 0, 0),
            TextShaper.Shape("SHARED-STAMP", "Leelawadee UI", false, false, 12))]))
            .Select(b => b with { Mark = new(YrkMarkKind.StampText, Guid.ParseExact(stamp.Group, "N")) }).ToArray();
        var result = PdfTextWriter.AddText(shared, blocks);
        var written = CosDocument.Load(result);
        foreach (var id in original.ObjectIds.Where(id => id != original.GetPages()[0].Id))
            Assert.Equal(Serialize(original.GetObject(id)), Serialize(written.GetObject(id)));
        await using var after = await Open(result);
        Assert.All(await after.GetPageItemsAsync(0), i => Assert.Null(i.EditError));
        Assert.Contains("SHARED-STAMP", await after.GetPageTextAsync(0));
        Assert.DoesNotContain("SHARED-STAMP", await after.GetPageTextAsync(1));
        Assert.Equal(otherPage.Pixels, (await after.RenderPageAsync(1, 400, 400)).Pixels);
        await after.SetPageItemOpacityAsync((await Item(after, stamp.Group)).Reference, .3);
        await after.MovePageItemAsync((await Item(after, stamp.Group)).Reference, new(.1, .1));
        await after.RemovePageItemAsync((await Item(after, stamp.Group)).Reference);
        var path = PathFor("shared-stamp.pdf"); await after.SaveAsync(path);
        await using var reopened = await _engine.OpenAsync(path);
        Assert.Equal(otherPage.Pixels, (await reopened.RenderPageAsync(1, 400, 400)).Pixels);
        Assert.Equal(ImageHashes(shared), ImageHashes(File.ReadAllBytes(path)));

        static byte[] Serialize(CosObject? value)
        {
            using var output = new MemoryStream(); new CosWriter(output).Write(value!); return output.ToArray();
        }
    }

    [Fact]
    public async Task New_stamp_words_do_not_absorb_existing_separated_members()
    {
        var group = Guid.NewGuid();
        await using var frames = await Open(Blank());
        await frames.AddImageAsync(0, Red(), new(.1, .1, .3, .2), style: new(Mark: new(YrkMarkKind.Stamp, group)));
        await frames.AddImageAsync(0, Red(), new(.7, .7, .2, .2), style: new(Mark: new(YrkMarkKind.Image, Guid.NewGuid())));
        var blocks = Sta.Run(() => TextShaper.ToBlocks([(0, new NormalizedPoint(.15, .15), new PdfColor(0, 0, 0),
            TextShaper.Shape("UNSAFE-STAMP", "Leelawadee UI", false, false, 12))]))
            .Select(b => b with { Mark = new(YrkMarkKind.StampText, group) }).ToArray();
        var original = CosDocument.Load(await frames.SaveDecryptedAsync());
        var update = new CosIncrementalUpdate(original);
        // Deliberately bypass grouping to reproduce a pre-existing unsupported stream.
        PdfTextWriter.AddText(original, update, blocks);
        var unsafeFile = update.Build().Bytes;
        await using var document = await Open(unsafeFile);
        Assert.NotNull((await Item(document, group.ToString("N"))).EditError);
        Assert.Throws<PdfException>(() => PdfTextWriter.AddText(unsafeFile, blocks));
    }

    // Reproduces 1.2.0's real PDFium image insertion: BGRA alpha is multiplied BEFORE SetBitmap.
    // It deliberately does not call the production placement path, which now constructs a Form.
    private static Task<byte[]> LegacyImage(YrkMark mark) => PdfiumWorker.Shared.InvokeAsync(() =>
    {
        var d = PdfiumNative.FPDF_CreateNewDocument();
        var p = PdfiumNative.FPDFPage_New(d, 0, 400, 400);
        try
        {
            var pixels = Red().Pixels;
            for (var n = 3; n < pixels.Length; n += 4) pixels[n] = (byte)Math.Round(pixels[n] * .5, MidpointRounding.AwayFromZero);
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var bitmap = PdfiumNative.FPDFBitmap_CreateEx(2, 2, PdfiumNative.FPDFBitmap_BGRA, pin.AddrOfPinnedObject(), 8);
                try
                {
                    var image = PdfiumNative.FPDFPageObj_NewImageObj(d);
                    Assert.NotEqual(0, PdfiumNative.FPDFImageObj_SetBitmap(IntPtr.Zero, 0, image, bitmap));
                    Assert.NotEqual(0, PdfiumNative.FPDFImageObj_SetMatrix(image, 100, 0, 0, 80, 40, 280));
                    PdfiumMarks.Add(d, image, mark); PdfiumNative.FPDFPage_InsertObject(p, image);
                    Assert.NotEqual(0, PdfiumNative.FPDFPage_GenerateContent(p));
                    return PdfiumIo.WriteToBytes(d, PdfiumNative.FPDF_NO_INCREMENTAL);
                }
                finally { PdfiumNative.FPDFBitmap_Destroy(bitmap); }
            }
            finally { pin.Free(); }
        }
        finally { PdfiumNative.FPDF_ClosePage(p); PdfiumNative.FPDF_CloseDocument(d); }
    }, PdfWorkPriority.Interactive, CancellationToken.None);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Legacy_baked_alpha_is_a_fade_only_baseline_and_keeps_password(bool encrypted)
    {
        var mark = new YrkMark(YrkMarkKind.Image, Guid.NewGuid());
        var bytes = await LegacyImage(mark);
        const string password = "test-open";
        if (encrypted) bytes = PdfSecurity.Protect(bytes, new(password, "test-owner", PdfPermissions.All));
        await using var d = await Open(bytes, encrypted ? password : null);
        var old = await Item(d);
        Assert.Equal(PdfItemOpacityMode.FadeOnly, old.OpacityMode); Assert.Equal(1, old.Opacity);
        Assert.InRange(Green(await Render(d), 60, 90), 126, 128);
        await d.SetPageItemOpacityAsync(old.Reference, .5);
        Assert.InRange(Green(await Render(d), 60, 90), 190, 192);
        await d.SetPageItemOpacityAsync((await Item(d)).Reference, 1);
        Assert.InRange(Green(await Render(d), 60, 90), 126, 128); // Cannot recover the original opaque pixels.
        await d.SetPageItemOpacityAsync((await Item(d)).Reference, .5);
        await d.MovePageItemAsync((await Item(d)).Reference, new(.4, .3));
        await d.RotatePageItemAsync((await Item(d)).Reference, 90);
        var path = PathFor("legacy.pdf"); await d.SaveAsync(path);
        if (encrypted) await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => _engine.OpenAsync(path));
        await using var reopened = await _engine.OpenAsync(path, encrypted ? password : null);
        Assert.Equal(PdfItemOpacityMode.FadeOnly, (await Item(reopened)).OpacityMode);
        Assert.InRange((await Item(reopened)).Bounds.X, .524, .526);
        Assert.InRange(Green(await Render(reopened), 230, 200), 190, 192);
        Assert.Equal(ImageHashes(bytes, encrypted ? password : null), ImageHashes(File.ReadAllBytes(path), encrypted ? password : null));
        await reopened.RemovePageItemAsync((await Item(reopened)).Reference);
        Assert.Empty(await reopened.GetPageItemsAsync(0));
    }

    [Fact]
    public async Task Shared_image_and_mask_streams_and_other_occurrences_are_unchanged()
    {
        var mark = new YrkMark(YrkMarkKind.Image, Guid.NewGuid());
        var bytes = await LegacyImage(mark);
        var cos = CosDocument.Load(bytes); var update = new CosIncrementalUpdate(cos);
        var edit = new CosPageEdit(cos, cos.GetPages()[0]);
        var images = (CosDictionary)cos.Resolve(edit.Resources["XObject"])!;
        var image = images.Entries.First().Key;
        edit.Append($"q 100 0 0 80 240 80 cm /{image} Do Q"); edit.Commit(update);
        // A second page uses the original page's resource dictionary and image/mask references.
        var second = update.Add(cos.GetPages()[0].Dictionary.Clone());
        var pagesRef = Assert.IsType<CosReference>(cos.Catalog["Pages"]);
        var pages = Assert.IsType<CosDictionary>(cos.Resolve(pagesRef)).Clone();
        var kids = Assert.IsType<CosArray>(cos.Resolve(pages["Kids"]));
        pages["Kids"] = new CosArray([.. kids.Items, second]); pages["Count"] = new CosInteger(2);
        update.Replace(pagesRef.Id, pages);
        bytes = update.Build().Bytes;
        await using var d = await Open(bytes);
        var before = await Render(d);
        var otherPage = await d.RenderPageAsync(1, 400, 400);
        var tagged = (await d.GetPageItemsAsync(0)).Single(i => i.HasYrkMark);
        await d.SetPageItemOpacityAsync(tagged.Reference, .25);
        await d.MovePageItemAsync((await d.GetPageItemsAsync(0)).Single(i => i.HasYrkMark).Reference, new(.1, .1));
        var after = await Render(d);
        Assert.Equal(otherPage.Pixels, (await d.RenderPageAsync(1, 400, 400)).Pixels);
        for (var y = 200; y < 340; y++)
            for (var x = 220; x < 360; x++) Assert.Equal(Green(before, x, y), Green(after, x, y));
        var path = PathFor("shared.pdf"); await d.SaveAsync(path);
        Assert.Equal(ImageHashes(bytes), ImageHashes(File.ReadAllBytes(path)));
        await using var saved = await _engine.OpenAsync(path);
        Assert.Equal(otherPage.Pixels, (await saved.RenderPageAsync(1, 400, 400)).Pixels);
        var untagged = (await d.GetPageItemsAsync(0)).Single(i => !i.HasYrkMark);
        Assert.Equal(PdfItemOpacityMode.DeleteOnly, untagged.OpacityMode);
        await Assert.ThrowsAsync<PdfException>(() => d.SetPageItemOpacityAsync(untagged.Reference, .5));
        await d.RemovePageItemAsync(untagged.Reference);
        Assert.Equal(255, Green(await Render(d), 280, 280));
        Assert.Single(await d.GetPageItemsAsync(0));
        var deletedPath = PathFor("shared-deleted.pdf"); await d.SaveAsync(deletedPath);
        await using var deleted = await _engine.OpenAsync(deletedPath);
        Assert.Single(await deleted.GetPageItemsAsync(0));
        Assert.Equal(255, Green(await Render(deleted), 280, 280));
        Assert.InRange(Green(await Render(deleted), 100, 110), 222, 224);
        Assert.Equal(otherPage.Pixels, (await deleted.RenderPageAsync(1, 400, 400)).Pixels);
    }

    [Fact]
    public async Task Each_group_has_independent_opacity_and_stale_references_are_rejected()
    {
        await using var d = await Open(Blank());
        var a = new YrkMark(YrkMarkKind.Image, Guid.NewGuid()); var b = new YrkMark(YrkMarkKind.Image, Guid.NewGuid());
        await d.AddImageAsync(0, Red(), new(.1, .1, .2, .2), style: new(Opacity: .5, Mark: a));
        await d.AddImageAsync(0, Red(), new(.6, .6, .2, .2), style: new(Opacity: .5, Mark: b));
        var first = await Item(d, a.Group);
        await d.SetPageItemOpacityAsync(first.Reference, 1);
        Assert.InRange(Green(await Render(d), 280, 280), 126, 129);
        await Assert.ThrowsAsync<PdfException>(() => d.RemovePageItemAsync(first.Reference));
        await d.SetPageItemOpacityAsync((await Item(d, a.Group)).Reference, .5);
        var path = PathFor("private.pdf"); await d.SaveAsync(path);
        var cos = CosDocument.Load(File.ReadAllBytes(path));
        var edit = new CosPageEdit(cos, cos.GetPages()[0]);
        var gs = (CosDictionary)cos.Resolve(edit.Resources["ExtGState"])!;
        var references = PlacedContent.Read(cos, edit).Where(u => u.Grouped).Select(u =>
        {
            var name = (CosName)ContentTokenizer.Parse(u.Snapshot).Last(o => o.Operator == "gs").Operands[0];
            return Assert.IsType<CosReference>(gs[name.Value]).Id;
        }).ToArray();
        Assert.Equal(2, references.Distinct().Count());
        await using var reopened = await _engine.OpenAsync(path);
        await reopened.RemovePageItemAsync((await Item(reopened, a.Group)).Reference);
        Assert.InRange(Green(await Render(reopened), 280, 280), 126, 129);
    }

    [Fact]
    public async Task Signed_documents_are_explicitly_rejected_before_mutation()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Saved item test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var signer = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var bytes = await LegacyImage(new(YrkMarkKind.Image, Guid.NewGuid()));
        var signed = await PdfSigner.SignAsync(bytes, new(signer));
        await using var d = await Open(signed);
        var before = await Render(d);
        var item = await Item(d);
        var error = await Assert.ThrowsAsync<PdfException>(() => d.RemovePageItemAsync(item.Reference));
        Assert.Contains("digital signatures", error.Message);
        Assert.Equal(before.Pixels, (await Render(d)).Pixels);
        await d.MovePageItemAsync(item.Reference, new(.1, .1), options: new(AcknowledgeDigitalSignatures: true));
        var path = PathFor("signed-edited.pdf"); await d.SaveAsync(path);
        var output = File.ReadAllBytes(path);
        Assert.Equal(signed, output[..signed.Length]);
        var signature = Assert.Single(PdfSignatureVerifier.Verify(output, new(CheckRevocation: false)));
        Assert.True(signature.IsIntact); Assert.True(signature.ModifiedAfterSigning);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unsafe_legacy_layouts_are_reported_and_rejected_without_changing_pixels(bool clipped)
    {
        var bytes = await LegacyImage(new(YrkMarkKind.Image, Guid.NewGuid()));
        var cos = CosDocument.Load(bytes); var update = new CosIncrementalUpdate(cos);
        var edit = new CosPageEdit(cos, cos.GetPages()[0]);
        var old = Encoding.Latin1.GetString(edit.ReadContent());
        edit.ReplaceContent(Encoding.Latin1.GetBytes(clipped ? "q 40 280 50 80 re W n\n" + old + "\nQ"
            : old + "\nq 0 0 1 rg 200 200 10 10 re f Q\n" + old));
        edit.Commit(update);
        await using var d = await Open(update.Build().Bytes);
        var before = await Render(d); var item = await Item(d);
        Assert.Contains(clipped ? "Clipped" : "separated", item.EditError);
        await Assert.ThrowsAsync<PdfException>(() => d.MovePageItemAsync(item.Reference, new(.2, .2)));
        await Assert.ThrowsAsync<PdfException>(() => d.SetPageItemOpacityAsync(item.Reference, .5));
        await Assert.ThrowsAsync<PdfException>(() => d.RemovePageItemAsync(item.Reference));
        Assert.Equal(before.Pixels, (await Render(d)).Pixels);
    }

    [Fact]
    public async Task Legacy_stamp_text_and_background_text_survive_migration_and_edits()
    {
        var group = Guid.NewGuid();
        var bytes = await LegacyImage(new(YrkMarkKind.Stamp, group));
        var cos = CosDocument.Load(bytes); var update = new CosIncrementalUpdate(cos);
        var edit = new CosPageEdit(cos, cos.GetPages()[0]);
        var font = edit.AddResource("Font", "TestFont", update.Add(new CosDictionary()
            .Set("Type", new CosName("Font")).Set("Subtype", new CosName("Type1")).Set("BaseFont", new CosName("Helvetica"))));
        var gs = edit.AddResource("ExtGState", "OldAlpha", update.Add(PlacedGroupBuilder.Alpha(.5)));
        edit.Append($"q /{gs} gs /YRK <</Kind(StampText)/Group({group:N})/V(1)>> BDC BT /{font} 14 Tf 1 0 0 1 50 300 Tm (LegacyStampText) Tj ET EMC Q\n"
            + $"q BT /{font} 14 Tf 1 0 0 1 220 80 Tm (BackgroundText) Tj ET Q\n");
        edit.Commit(update);
        await using var d = await Open(update.Build().Bytes);
        Assert.Contains("LegacyStampText", await d.GetPageTextAsync(0));
        Assert.Equal(PdfItemOpacityMode.FadeOnly, (await Item(d)).OpacityMode);
        await d.SetPageItemOpacityAsync((await Item(d)).Reference, .5);
        await d.MovePageItemAsync((await Item(d)).Reference, new(.1, .1));
        await d.RotatePageItemAsync((await Item(d)).Reference, 25);
        var path = PathFor("legacy-stamp.pdf"); await d.SaveAsync(path);
        await using var reopened = await _engine.OpenAsync(path);
        Assert.Contains("LegacyStampText", await reopened.GetPageTextAsync(0));
        Assert.Contains("BackgroundText", await reopened.GetPageTextAsync(0));
        await reopened.RemovePageItemAsync((await Item(reopened)).Reference);
        Assert.DoesNotContain("LegacyStampText", await reopened.GetPageTextAsync(0));
        Assert.Contains("BackgroundText", await reopened.GetPageTextAsync(0));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task New_protected_groups_keep_both_passwords_and_searchable_text(bool text)
    {
        var protectedBytes = PdfSecurity.Protect(Blank(), new("open-test", "owner-test", PdfPermissions.All));
        await using var d = await Open(protectedBytes, "open-test");
        var mark = new YrkMark(text ? YrkMarkKind.Text : YrkMarkKind.Image, Guid.NewGuid());
        if (text)
        {
            var blocks = Sta.Run(() => TextShaper.ToBlocks([(0, new NormalizedPoint(.1, .2), new PdfColor(0, 0, 0),
                TextShaper.Shape("ProtectedText", "Leelawadee UI", false, false, 14))])).Select(b => b with { Mark = mark }).ToArray();
            var path = PathFor("text-source.pdf"); await d.SaveAsync(path);
            await using var placed = await Open(PdfTextWriter.AddText(File.ReadAllBytes(path), blocks, d.OpenPassword), d.OpenPassword);
            await Check(placed);
        }
        else
        {
            await d.AddImageAsync(0, Red(128), new(.1, .1, .25, .2), style: new(Opacity: .5, Mark: mark));
            await Check(d);
        }
        async Task Check(IPdfDocument placed)
        {
            Assert.Equal(PdfItemOpacityMode.Group, (await Item(placed)).OpacityMode);
            await placed.MovePageItemAsync((await Item(placed)).Reference, new(.2, .3));
            await placed.RotatePageItemAsync((await Item(placed)).Reference, 45);
            await placed.SetPageItemOpacityAsync((await Item(placed)).Reference, .5);
            var path = PathFor("new-protected.pdf"); await placed.SaveAsync(path);
            await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => _engine.OpenAsync(path));
            await using var reopened = await _engine.OpenAsync(path, "open-test");
            await using var owner = await _engine.OpenAsync(path, "owner-test");
            Assert.True(reopened.IsEncrypted); Assert.Equal(PdfPermissions.All, owner.Permissions);
            if (text) Assert.NotEmpty(await reopened.FindTextAsync(0, "ProtectedText", PdfSearchOptions.None));
            else Assert.InRange(Green(await Render(reopened), 170, 200), 190, 193);
        }
    }

    [Fact]
    public async Task References_cannot_edit_another_document()
    {
        var bytes = await LegacyImage(new(YrkMarkKind.Image, Guid.NewGuid()));
        await using var a = await Open(bytes); await using var b = await Open(bytes);
        var reference = (await Item(a)).Reference;
        await Assert.ThrowsAsync<PdfException>(() => b.RemovePageItemAsync(reference));
        Assert.Single(await b.GetPageItemsAsync(0));
    }

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(270)]
    public async Task Rotation_of_an_asymmetric_image_is_clockwise_about_its_centre(int pageRotation)
    {
        await using var d = await Open(Blank(pageRotation));
        var image = new PdfImage(2, 1, [0, 0, 255, 255, 255, 0, 0, 255]);
        await d.AddImageAsync(0, image, new(.1, .1, .25, .2), style: new(Mark: new(YrkMarkKind.Image, Guid.NewGuid())));
        await d.MovePageItemAsync((await Item(d)).Reference, new(.4, .3));
        await d.RotatePageItemAsync((await Item(d)).Reference, 90);
        var path = PathFor("asymmetric.pdf"); await d.SaveAsync(path);
        await using var reopened = await _engine.OpenAsync(path);
        var bitmap = await Render(reopened);
        var top = (170 * bitmap.Stride) + 250 * 4;
        var bottom = (230 * bitmap.Stride) + 250 * 4;
        Assert.InRange(bitmap.Pixels[top + 2], 250, 255); Assert.InRange(bitmap.Pixels[top], 0, 5);
        Assert.InRange(bitmap.Pixels[bottom], 250, 255); Assert.InRange(bitmap.Pixels[bottom + 2], 0, 5);
        var item = await Item(reopened);
        Assert.InRange(item.Bounds.X, .524, .526); Assert.InRange(item.Bounds.Y, .374, .376);
        Assert.InRange(item.Bounds.Width, .199, .201); Assert.InRange(item.Bounds.Height, .249, .251);
    }

    [Fact]
    public async Task Legacy_vector_keeps_its_baked_per_path_alpha_and_crossing_when_faded()
    {
        var bytes = await PdfiumWorker.Shared.InvokeAsync(() =>
        {
            var doc = PdfiumNative.FPDF_CreateNewDocument(); var page = PdfiumNative.FPDFPage_New(doc, 0, 400, 400);
            try
            {
                var mark = new YrkMark(YrkMarkKind.Signature, Guid.NewGuid());
                foreach (var shape in Cross.Paths)
                {
                    var path = PdfiumNative.FPDFPageObj_CreateNewPath(0, 0);
                    foreach (var c in shape.Commands)
                    {
                        var x = (float)(c.X1 / 100); var y = (float)(1 - c.Y1 / 100);
                        if (c.Op == PdfPathOp.MoveTo) PdfiumNative.FPDFPath_MoveTo(path, x, y);
                        else if (c.Op == PdfPathOp.LineTo) PdfiumNative.FPDFPath_LineTo(path, x, y);
                        else if (c.Op == PdfPathOp.Close) PdfiumNative.FPDFPath_Close(path);
                    }
                    PdfiumNative.FPDFPageObj_SetFillColor(path, 255, 0, 0, 128);
                    PdfiumNative.FPDFPath_SetDrawMode(path, PdfiumNative.FPDF_FILLMODE_WINDING, 0);
                    PdfiumNative.FPDFPageObj_Transform(path, 100, 0, 0, 100, 40, 260);
                    PdfiumMarks.Add(doc, path, mark); PdfiumNative.FPDFPage_InsertObject(page, path);
                }
                Assert.NotEqual(0, PdfiumNative.FPDFPage_GenerateContent(page));
                return PdfiumIo.WriteToBytes(doc, PdfiumNative.FPDF_NO_INCREMENTAL);
            }
            finally { PdfiumNative.FPDF_ClosePage(page); PdfiumNative.FPDF_CloseDocument(doc); }
        }, PdfWorkPriority.Interactive, CancellationToken.None);
        await using var d = await Open(bytes);
        Assert.Equal(PdfItemOpacityMode.FadeOnly, (await Item(d)).OpacityMode);
        Assert.InRange(Green(await Render(d), 60, 90), 126, 128);
        Assert.InRange(Green(await Render(d), 90, 90), 62, 64);
        await d.SetPageItemOpacityAsync((await Item(d)).Reference, .5);
        Assert.InRange(Green(await Render(d), 60, 90), 190, 192);
        Assert.InRange(Green(await Render(d), 90, 90), 158, 160);
        await d.MovePageItemAsync((await Item(d)).Reference, new(.4, .3));
        await d.RotatePageItemAsync((await Item(d)).Reference, 90);
        var path = PathFor("legacy-vector.pdf"); await d.SaveAsync(path);
        await using var reopened = await _engine.OpenAsync(path);
        Assert.InRange(Green(await Render(reopened), 220, 210), 190, 192);
        Assert.InRange(Green(await Render(reopened), 250, 210), 158, 160);
        await reopened.RemovePageItemAsync((await Item(reopened)).Reference);
        Assert.Empty(await reopened.GetPageItemsAsync(0));
    }

    [Fact]
    public async Task Images_nested_inside_a_compound_foreign_form_are_listed_but_refuse_edits()
    {
        var bytes = await LegacyImage(new(YrkMarkKind.Image, Guid.NewGuid()));
        var cos = CosDocument.Load(bytes); var update = new CosIncrementalUpdate(cos);
        var edit = new CosPageEdit(cos, cos.GetPages()[0]);
        var image = ((CosDictionary)cos.Resolve(edit.Resources["XObject"])!).Entries.First().Key;
        var form = update.Add(CosPageEdit.Stream(Encoding.Latin1.GetBytes($"q 100 0 0 80 40 280 cm /{image} Do Q\nq 100 0 0 80 240 80 cm /{image} Do Q"),
            new CosDictionary().Set("Type", new CosName("XObject")).Set("Subtype", new CosName("Form"))
                .Set("BBox", CosArray.FromNumbers(0, 0, 400, 400)).Set("Resources", edit.Resources.Clone())));
        var name = edit.AddResource("XObject", "Foreign", form);
        edit.ReplaceContent(Encoding.Latin1.GetBytes($"q /{name} Do Q")); edit.Commit(update);
        await using var d = await Open(update.Build().Bytes);
        var items = await d.GetPageItemsAsync(0); Assert.Equal(2, items.Count);
        var before = await Render(d);
        foreach (var item in items)
        {
            Assert.Equal(PdfItemOpacityMode.DeleteOnly, item.OpacityMode);
            Assert.Contains("foreign Form", item.EditError);
            await Assert.ThrowsAsync<PdfException>(() => d.RemovePageItemAsync(item.Reference));
        }
        Assert.Equal(before.Pixels, (await Render(d)).Pixels);
    }
}
