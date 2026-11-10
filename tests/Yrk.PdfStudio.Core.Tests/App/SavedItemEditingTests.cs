using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Security;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

public sealed partial class SavedItemEditingTests
{
    private static IPdfDocument Document(AppTestHarness app) => (IPdfDocument)typeof(MainViewModel)
        .GetField("_document", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app.Model)!;
    private static PlacedItemViewModel Frame(AppTestHarness app) => Assert.Single(app.Model.Pages[0].Items, i => i.IsSaved);
    private static Task<PdfBitmap> Render(AppTestHarness app) => Document(app).RenderPageAsync(0, 400, 400);
    private static Task<IReadOnlyList<PdfPageItem>> Items(AppTestHarness app) => Document(app).GetPageItemsAsync(0);
    private static Task EditFinished(AppTestHarness app) => app.Model.SavedItemEditTask;

    private static async Task Select(AppTestHarness app, PdfPageItem? item = null)
    {
        item ??= (await Items(app))[0];
        Assert.True(await app.Model.TrySelectSavedItemAsync(app.Model.Pages[0],
            item.Bounds.X + item.Bounds.Width / 2, item.Bounds.Y + item.Bounds.Height / 2));
    }

    private static byte[] Blank(int rotation = 0)
    {
        var b = new CosNewDocument();
        var root = b.Reserve(); var pages = b.Reserve(); var page = b.Reserve();
        b.Set(page, new CosDictionary().Set("Type", new CosName("Page")).Set("Parent", pages)
            .Set("MediaBox", new CosArray([new CosInteger(0), new CosInteger(0), new CosInteger(400), new CosInteger(400)]))
            .Set("Rotate", new CosInteger(rotation)).Set("Resources", new CosDictionary()));
        b.Set(pages, new CosDictionary().Set("Type", new CosName("Pages")).Set("Kids", new CosArray([page])).Set("Count", new CosInteger(1)));
        b.Set(root, new CosDictionary().Set("Type", new CosName("Catalog")).Set("Pages", pages));
        return b.Build(root);
    }

    private static PdfImage Red() => new(2, 2, Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 0, 0, 255, 255 }).ToArray());
    private static BitmapSource Image()
    {
        var image = Red();
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, image.Pixels, 8);
        bitmap.Freeze(); return bitmap;
    }

    private static async Task<string> PlacedSource(AppTestHarness app, string kind = "picture", int rotation = 0)
    {
        var blank = app.PathFor("blank.pdf"); File.WriteAllBytes(blank, Blank(rotation));
        Assert.True(await app.OpenAsync(blank));
        var stamp = kind == "stamp" ? Sta.Run(() => StampRenderer.RenderCertifiedCopy(new CertifiedCopyOptions("SAVED-STAMP", false, null))) : null;
        var item = PlacedItemViewModel.CreateImage(0, kind == "stamp" ? PlacedItemKind.Stamp : kind == "signature" ? PlacedItemKind.Signature : PlacedItemKind.Picture,
            400, 400, stamp?.Image ?? Image(), kind == "signature" ? SignatureArt.FromImage(Image()) : null, stamp);
        item.Bounds = new(.1, .1, .25, .15);
        Attach(app, item);
        var other = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Picture, 400, 400, Image());
        other.Bounds = new(.75, .75, .15, .15); Attach(app, other);
        var source = app.PathFor("placed.pdf"); Assert.True(await app.SaveToAsync(source), string.Join("; ", app.Dialogs.Named("ShowError").Select(c => c.Arguments[0])));
        return source;
    }

    private static void Attach(AppTestHarness app, PlacedItemViewModel item) => typeof(MainViewModel)
        .GetMethod("AttachItem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app.Model, [item]);

    [Theory]
    [InlineData("picture-after", false)] [InlineData("picture-before", false)]
    [InlineData("signature-after", false)] [InlineData("signature-before", false)]
    [InlineData("two-stamps", false)]
    [InlineData("picture-after", true)] [InlineData("picture-before", true)]
    [InlineData("signature-after", true)] [InlineData("signature-before", true)]
    [InlineData("two-stamps", true)]
    public async Task One_save_keeps_every_stamp_and_its_searchable_words_editable_with_other_placements(string layout, bool encrypted)
    {
        await using var app = new AppTestHarness();
        await OpenBlank(app);
        var placements = MakePlacements();
        foreach (var item in placements) Attach(app, item);
        var source = app.PathFor("all-at-once.pdf");
        // SaveToAsync exercises CommitOverlaysAsync(keepText: true), then ApplyPlacedText.
        Assert.True(await app.SaveToAsync(source), Errors(app));
        Assert.True(await app.OpenAsync(source));
        var initial = await Items(app);
        Assert.Equal(placements.Length, initial.Count);
        Assert.All(initial, i => { Assert.Null(i.EditError); Assert.Equal(PdfItemOpacityMode.Group, i.OpacityMode); });
        var baseline = await Render(app);

        // Independent visual oracle: the previously working route saves each placement immediately.
        // The one-save result must have exactly the same frame, words, alpha and stacking order.
        await using (var reference = new AppTestHarness())
        {
            await OpenBlank(reference);
            foreach (var item in MakePlacements())
            {
                Attach(reference, item);
                Assert.True(await reference.SaveToAsync(reference.PathFor("sequential.pdf")), Errors(reference));
            }
            Assert.Equal((await Render(reference)).Pixels, baseline.Pixels);
        }

        foreach (var stamp in placements.Where(i => i.HasStampText))
        {
            var group = stamp.MarkGroup.ToString("N");
            var label = stamp == placements.First(i => i.HasStampText) ? "FIRST-STAMP" : "SECOND-STAMP";
            var original = Assert.Single(initial, i => i.Group == group);
            Assert.Equal(YrkMarkKind.Stamp, original.Kind);
            var wordBox = await WordBounds(label);
            var before = await Render(app);
            var others = (await Items(app)).Where(i => i.Group != group).ToArray();
            await Select(app, (await Items(app)).Single(i => i.Group == group));
            var frame = Frame(app);
            Assert.True(frame.CanTransform); Assert.True(frame.CanAdjustOpacity);
            app.Model.BeginItemDrag(frame); app.Model.MoveItem(frame, 0, 140); app.Model.EndItemDrag(frame);
            await EditFinished(app);
            var moved = (await Items(app)).Single(i => i.Group == group);
            Assert.InRange(moved.Bounds.Y - original.Bounds.Y, .349, .351);
            var movedWords = await WordBounds(label);
            Assert.InRange(movedWords.Y - wordBox.Y, .349, .351);
            AssertStampPixels(baseline, original.Bounds, await Render(app), rotate: false, opacity: 1);

            app.Model.RotateItem90(Frame(app)); await EditFinished(app);
            var rotated = (await Items(app)).Single(i => i.Group == group);
            Assert.InRange(Math.Abs(rotated.Bounds.Width - moved.Bounds.Height), 0, .002);
            var rotatedWords = await WordBounds(label);
            Assert.InRange(Math.Abs(rotatedWords.Width - wordBox.Height), 0, .002);
            Assert.InRange(Math.Abs(rotatedWords.Height - wordBox.Width), 0, .002);
            AssertStampPixels(baseline, original.Bounds, await Render(app), rotate: true, opacity: 1);

            app.Model.ChangeItemOpacity(Frame(app), .4); await EditFinished(app);
            AssertStampPixels(baseline, original.Bounds, await Render(app), rotate: true, opacity: .4);
            var target = app.PathFor(label + "-edited.pdf");
            Assert.True(await app.SaveToAsync(target), Errors(app)); Assert.True(await app.OpenAsync(target));
            var updated = (await Items(app)).Single(i => i.Group == group);
            Assert.Null(updated.EditError); Assert.InRange(updated.Opacity, .399, .401);
            Assert.Equal(rotated.Bounds, updated.Bounds);
            Assert.NotEmpty(await Document(app).FindTextAsync(0, label, PdfSearchOptions.None));
            AssertStampPixels(baseline, original.Bounds, await Render(app), rotate: true, opacity: .4);
            await AssertOthersUnchanged(others, before, removed: false);

            await Select(app, updated); app.Model.DeleteSelectedCommand.Execute(null); await EditFinished(app);
            var deleted = app.PathFor(label + "-deleted.pdf");
            Assert.True(await app.SaveToAsync(deleted), Errors(app)); Assert.True(await app.OpenAsync(deleted));
            Assert.DoesNotContain(await Items(app), i => i.Group == group);
            Assert.Empty(await Document(app).FindTextAsync(0, label, PdfSearchOptions.None));
            Assert.DoesNotContain(label, await Document(app).GetPageTextAsync(0));
            AssertWhite(await Render(app), original.Bounds); AssertWhite(await Render(app), rotated.Bounds);
            await AssertOthersUnchanged(others, before, removed: true);
        }
        Assert.Single(await Items(app)); Assert.Empty(app.Dialogs.Named("ShowError"));

        async Task OpenBlank(AppTestHarness target)
        {
            var path = target.PathFor("blank.pdf");
            File.WriteAllBytes(path, encrypted ? PdfSecurity.Protect(Blank(), new("user", "owner", PdfPermissions.All)) : Blank());
            if (encrypted)
            {
                target.Dialogs.Answer("PromptPassword", Enumerable.Repeat<object?>("user", 16).ToArray());
                target.Dialogs.Answer("ConfirmPassword", Enumerable.Repeat<object?>("user", 16).ToArray());
            }
            Assert.True(await target.OpenAsync(path));
        }

        PlacedItemViewModel[] MakePlacements()
        {
            PlacedItemViewModel Stamp(string label, double x)
            {
                var art = Sta.Run(() => StampRenderer.RenderCertifiedCopy(new CertifiedCopyOptions(label, false, null)));
                var item = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Stamp, 400, 400, art.Image, stamp: art);
                item.Bounds = new(x, .1, .35, .18);
                Assert.True(item.HasStampText);
                return item;
            }
            var first = Stamp("FIRST-STAMP", .1);
            var signature = layout.StartsWith("signature", StringComparison.Ordinal);
            var other = PlacedItemViewModel.CreateImage(0, signature ? PlacedItemKind.Signature : PlacedItemKind.Picture,
                400, 400, Image(), signature ? SignatureArt.FromImage(Image()) : null);
            other.Bounds = new(.75, .8, .15, .15);
            return layout == "two-stamps" ? [first, other, Stamp("SECOND-STAMP", .55)]
                : layout.EndsWith("before", StringComparison.Ordinal) ? [other, first] : [first, other];
        }

        async Task<NormalizedRect> WordBounds(string label)
        {
            var rects = Assert.Single(await Document(app).FindTextAsync(0, label, PdfSearchOptions.None)).Rects;
            Assert.NotEmpty(rects);
            var x = rects.Min(r => r.X); var y = rects.Min(r => r.Y);
            return new(x, y, rects.Max(r => r.X + r.Width) - x, rects.Max(r => r.Y + r.Height) - y);
        }

        async Task AssertOthersUnchanged(PdfPageItem[] others, PdfBitmap before, bool removed)
        {
            var current = await Items(app); var after = await Render(app);
            Assert.Equal(others.Length + (removed ? 0 : 1), current.Count);
            foreach (var other in others)
            {
                var unchanged = Assert.Single(current, i => i.Group == other.Group);
                Assert.Equal(other.Bounds, unchanged.Bounds); Assert.Equal(other.Opacity, unchanged.Opacity);
                Assert.Null(unchanged.EditError);
                Assert.Equal(Crop(before, other.Bounds), Crop(after, other.Bounds));
            }
        }
        static string Errors(AppTestHarness target) => string.Join("; ", target.Dialogs.Named("ShowError").Select(c => c.Arguments[0]));
    }

    [Theory]
    [InlineData("picture", false)] [InlineData("picture", true)]
    [InlineData("signature", false)] [InlineData("signature", true)]
    [InlineData("stamp", false)] [InlineData("stamp", true)]
    public async Task One_save_preserves_overlapping_stamp_pixels_and_placement_order(string otherKind, bool otherFirst)
    {
        PdfBitmap? expected = null;
        foreach (var oneSave in new[] { false, true })
        {
            await using var app = new AppTestHarness();
            var blank = app.PathFor("blank.pdf"); File.WriteAllBytes(blank, Blank());
            Assert.True(await app.OpenAsync(blank));
            var art = Sta.Run(() => StampRenderer.RenderCertifiedCopy(new CertifiedCopyOptions("OVERLAP", false, null)));
            var stamp = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Stamp, 400, 400, art.Image, stamp: art);
            stamp.Bounds = new(.1, .1, .5, .25); stamp.Rotation = 27; stamp.Opacity = .6;
            var other = PlacedItemViewModel.CreateImage(0,
                otherKind == "stamp" ? PlacedItemKind.Stamp : otherKind == "signature" ? PlacedItemKind.Signature : PlacedItemKind.Picture,
                400, 400, otherKind == "stamp" ? art.Image : Image(),
                otherKind == "signature" ? SignatureArt.FromImage(Image()) : null, otherKind == "stamp" ? art : null);
            other.Bounds = new(.25, .22, .22, .15); other.Opacity = .7;
            foreach (var item in otherFirst ? new[] { other, stamp } : new[] { stamp, other })
            {
                Attach(app, item);
                if (!oneSave) Assert.True(await app.SaveToAsync(app.PathFor("sequential.pdf")));
            }
            if (oneSave) Assert.True(await app.SaveToAsync(app.PathFor("one-save.pdf")));
            var actual = await Render(app);
            if (expected is null) expected = actual;
            else Assert.Equal(expected.Pixels, actual.Pixels);
            Assert.Equal(2, (await Items(app)).Count);
            Assert.All(await Items(app), i => Assert.Null(i.EditError));
            Assert.NotEmpty(await Document(app).FindTextAsync(0, "OVERLAP", PdfSearchOptions.None));
            Assert.Empty(app.Dialogs.Named("ShowError"));
        }
    }

    private static byte[] Crop(PdfBitmap bitmap, NormalizedRect bounds)
    {
        var x = Math.Max(0, (int)(bounds.X * bitmap.Width) - 2);
        var y = Math.Max(0, (int)(bounds.Y * bitmap.Height) - 2);
        var right = Math.Min(bitmap.Width, (int)Math.Ceiling((bounds.X + bounds.Width) * bitmap.Width) + 2);
        var bottom = Math.Min(bitmap.Height, (int)Math.Ceiling((bounds.Y + bounds.Height) * bitmap.Height) + 2);
        return Enumerable.Range(y, bottom - y).SelectMany(row => bitmap.Pixels.AsSpan(row * bitmap.Stride + x * 4, (right - x) * 4).ToArray()).ToArray();
    }

    private static void AssertWhite(PdfBitmap bitmap, NormalizedRect bounds) => Assert.True(Crop(bitmap, bounds).All(value => value == 255), "Stamp frame or words remained painted after deletion.");

    private static void AssertStampPixels(PdfBitmap baseline, NormalizedRect bounds, PdfBitmap after, bool rotate, double opacity)
    {
        var cx = (bounds.X + bounds.Width / 2) * 400;
        var cy = (bounds.Y + bounds.Height / 2) * 400;
        var samples = 0; var matches = 0;
        for (var y = (int)(bounds.Y * 400) + 1; y < (bounds.Y + bounds.Height) * 400 - 1; y++)
        for (var x = (int)(bounds.X * 400) + 1; x < (bounds.X + bounds.Width) * 400 - 1; x++)
        {
            var offset = y * baseline.Stride + x * 4;
            if (baseline.Pixels[offset + 2] > 100) continue;
            var rx = rotate ? (int)Math.Round(cx - (y + .5 - cy) - .5) : x;
            var ry = rotate ? (int)Math.Round(cy + (x + .5 - cx) + 140 - .5) : y + 140;
            samples++;
            if (Enumerable.Range(0, 3).All(channel => Math.Abs(after.Pixels[ry * after.Stride + rx * 4 + channel]
                - (baseline.Pixels[offset + channel] * opacity + 255 * (1 - opacity))) < 35)) matches++;
        }
        Assert.True(samples > 30, $"Only {samples} stamp pixels checked.");
        Assert.True(matches >= samples * .90, $"Stamp pixels: {matches}/{samples} (rotation={rotate}, opacity={opacity}).");
    }

    [Theory]
    [InlineData("picture", 0)] [InlineData("stamp", 0)] [InlineData("signature", 0)]
    [InlineData("picture", 90)] [InlineData("stamp", 270)] [InlineData("signature", 180)]
    public async Task Saved_placements_reuse_controls_and_survive_all_edits_save_and_reopen(string kind, int rotation)
    {
        await using var app = new AppTestHarness();
        var source = await PlacedSource(app, kind, rotation);
        Assert.True(await app.OpenAsync(source));
        var original = (await Items(app))[0]; var other = (await Items(app))[1];
        var baseline = await Render(app);
        Assert.False(await app.Model.TrySelectSavedItemAsync(app.Model.Pages[0], .6, .6));
        Assert.Empty(app.Model.Pages[0].Items);
        await Select(app, original);
        var frame = Frame(app);
        Assert.True(frame.IsSelected); Assert.True(frame.CanTransform); Assert.True(frame.ShowRotationHandle);
        Assert.True(frame.CanAdjustOpacity); Assert.False(frame.ShowResizeHandle); Assert.Null(frame.Image);
        Assert.False(app.Model.IsDirty);
        app.Model.BeginItemDrag(frame); app.Model.MoveItem(frame, 80, 100); app.Model.EndItemDrag(frame);
        await EditFinished(app);
        Assert.True(app.Model.IsDirty);
        var moved = (await Items(app))[0];
        Assert.InRange(moved.Bounds.X - original.Bounds.X, .199, .201);
        Assert.InRange(moved.Bounds.Y - original.Bounds.Y, .249, .251);
        Assert.NotEqual(original.Reference, moved.Reference);
        app.Model.RotateItem90(Frame(app)); await EditFinished(app);
        var rotated = (await Items(app))[0];
        Assert.InRange(Math.Abs(rotated.Bounds.Width - moved.Bounds.Height), 0, .002);
        app.Model.ChangeItemOpacity(Frame(app), .4); await EditFinished(app);
        Assert.InRange((await Items(app))[0].Opacity, .399, .401);
        var target = app.PathFor("edited.pdf"); Assert.True(await app.SaveToAsync(target));
        Assert.True(await app.OpenAsync(target));
        var updated = (await Items(app))[0];
        Assert.Equal(rotated.Bounds, updated.Bounds); Assert.InRange(updated.Opacity, .399, .401);
        Assert.Equal(other.Bounds, (await Items(app))[1].Bounds);
        Assert.Equal(other.Opacity, (await Items(app))[1].Opacity);
        var after = await Render(app);
        // The other item's pixels are preserved, not merely its metadata.
        for (var y = 305; y < 355; y++)
            Assert.Equal(baseline.Pixels.AsSpan(y * baseline.Stride + 305 * 4, 50 * 4).ToArray(), after.Pixels.AsSpan(y * after.Stride + 305 * 4, 50 * 4).ToArray());
        if (kind == "stamp") Assert.Contains("SAVED-STAMP", await Document(app).GetPageTextAsync(0));
        await Select(app, updated); app.Model.DeleteSelectedCommand.Execute(null); await EditFinished(app);
        Assert.DoesNotContain(app.Model.Pages[0].Items, i => i.IsSaved);
        Assert.Single(await Items(app));
        var deleted = app.PathFor("deleted.pdf"); Assert.True(await app.SaveToAsync(deleted));
        await using var reopened = await app.Engine.OpenAsync(deleted);
        Assert.Single(await reopened.GetPageItemsAsync(0));
        if (kind == "stamp") Assert.DoesNotContain("SAVED-STAMP", await reopened.GetPageTextAsync(0));
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Theory]
    [InlineData("move")] [InlineData("rotate")] [InlineData("opacity")] [InlineData("delete")]
    public async Task Every_saved_item_edit_has_one_snapshot_undo_and_redo(string operation)
    {
        await using var app = new AppTestHarness(); await PlacedSource(app);
        var before = (await Render(app)).Pixels;
        await Select(app); var frame = Frame(app);
        switch (operation)
        {
            case "move": app.Model.BeginItemDrag(frame); app.Model.MoveItem(frame, 30, 40); app.Model.MoveItem(frame, 10, 5); app.Model.EndItemDrag(frame); break;
            case "rotate": app.Model.BeginItemDrag(frame); app.Model.RotateItem(frame, 31, true); app.Model.RotateItem(frame, 48, true); app.Model.EndItemDrag(frame); break;
            case "opacity": app.Model.BeginItemDrag(frame); frame.Opacity = .8; frame.Opacity = .3; app.Model.EndItemDrag(frame); break;
            case "delete": app.Model.DeleteSelectedCommand.Execute(null); break;
        }
        await EditFinished(app); var after = (await Render(app)).Pixels;
        Assert.False(before.SequenceEqual(after));
        app.Model.UndoCommand.Execute(null); await Idle(app);
        Assert.Equal(before, (await Render(app)).Pixels);
        Assert.False(app.Model.UndoCommand.CanExecute(null));
        Assert.DoesNotContain(app.Model.Pages[0].Items, i => i.IsSaved);
        app.Model.RedoCommand.Execute(null); await Idle(app);
        Assert.Equal(after, (await Render(app)).Pixels);
        Assert.False(app.Model.RedoCommand.CanExecute(null));
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    private static async Task Idle(AppTestHarness app)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (app.Model.IsBusy) await Task.Delay(5, timeout.Token);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Legacy_items_show_the_note_and_each_selection_can_only_fade_its_current_baseline(bool encrypted)
    {
        await using var app = new AppTestHarness();
        var bytes = await LegacyImageAsync();
        if (encrypted) bytes = PdfSecurity.Protect(bytes, new("user", "owner", PdfPermissions.All));
        var path = app.PathFor("legacy.pdf"); File.WriteAllBytes(path, bytes);
        if (encrypted) app.Dialogs.Answer("PromptPassword", "user");
        Assert.True(await app.OpenAsync(path));
        if (encrypted) app.Dialogs.Answer("ConfirmPassword", "user", "user");
        await Select(app); var frame = Frame(app);
        Assert.Equal(PdfItemOpacityMode.FadeOnly, frame.SavedItem!.OpacityMode);
        Assert.Equal(1, frame.Opacity);
        Assert.Equal(Loc.T("This item has its opacity saved already; it can only be made fainter."), frame.OpacityNote);
        app.Model.ChangeItemOpacity(frame, 1.5); await EditFinished(app);
        Assert.False(app.Model.IsDirty); Assert.Equal(1, Frame(app).Opacity);
        app.Model.ChangeItemOpacity(frame, .5); await EditFinished(app);
        Assert.Equal(1, Frame(app).Opacity); // Current artwork is the next selection's 100% baseline.
        var firstFade = await Render(app);
        app.Model.ChangeItemOpacity(Frame(app), 2); await EditFinished(app);
        Assert.Equal(firstFade.Pixels, (await Render(app)).Pixels);
        app.Model.ChangeItemOpacity(Frame(app), .5); await EditFinished(app);
        Assert.InRange((await Items(app))[0].Opacity, .249, .253);
        app.Model.UndoCommand.Execute(null); await Idle(app);
        Assert.Equal(firstFade.Pixels, (await Render(app)).Pixels);
        app.Model.RedoCommand.Execute(null); await Idle(app);
        var target = app.PathFor("legacy-edited.pdf"); Assert.True(await app.SaveToAsync(target));
        await using var reopened = await app.Engine.OpenAsync(target, encrypted ? "user" : null);
        Assert.InRange((await reopened.GetPageItemsAsync(0))[0].Opacity, .249, .253);
        if (encrypted)
        {
            Assert.Equal(2, app.Dialogs.Named("ConfirmPassword").Length);
            await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => app.Engine.OpenAsync(target));
            await using var owner = await app.Engine.OpenAsync(target, "owner");
            Assert.Single(await owner.GetPageItemsAsync(0));
        }
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    // Same 1.2.0 fixture as SavedPageItemTests: baked alpha and a native image with the old mark.
    private static Task<byte[]> LegacyImageAsync() => PdfiumWorker.Shared.InvokeAsync(() =>
    {
        var d = PdfiumNative.FPDF_CreateNewDocument(); var p = PdfiumNative.FPDFPage_New(d, 0, 400, 400);
        try
        {
            var pixels = Red().Pixels;
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 128;
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var bitmap = PdfiumNative.FPDFBitmap_CreateEx(2, 2, PdfiumNative.FPDFBitmap_BGRA, pin.AddrOfPinnedObject(), 8);
                try
                {
                    var image = PdfiumNative.FPDFPageObj_NewImageObj(d);
                    Assert.NotEqual(0, PdfiumNative.FPDFImageObj_SetBitmap(IntPtr.Zero, 0, image, bitmap));
                    Assert.NotEqual(0, PdfiumNative.FPDFImageObj_SetMatrix(image, 100, 0, 0, 80, 40, 280));
                    PdfiumMarks.Add(d, image, new(YrkMarkKind.Image, Guid.NewGuid()));
                    PdfiumNative.FPDFPage_InsertObject(p, image); Assert.NotEqual(0, PdfiumNative.FPDFPage_GenerateContent(p));
                    return PdfiumIo.WriteToBytes(d, PdfiumNative.FPDF_NO_INCREMENTAL);
                }
                finally { PdfiumNative.FPDFBitmap_Destroy(bitmap); }
            }
            finally { pin.Free(); }
        }
        finally { PdfiumNative.FPDF_ClosePage(p); PdfiumNative.FPDF_CloseDocument(d); }
    }, PdfWorkPriority.Interactive, CancellationToken.None);

    [Fact]
    public async Task Unmarked_image_is_delete_only_and_undo_restores_it()
    {
        await using var app = new AppTestHarness();
        await using (var d = await app.Engine.OpenBytesAsync(Blank(), app.PathFor("blank.pdf")))
        {
            await d.AddImageAsync(0, Red(), new(.1, .1, .25, .2)); await d.SaveAsync(app.PathFor("foreign.pdf"));
        }
        Assert.True(await app.OpenAsync(app.PathFor("foreign.pdf")));
        await Select(app); var frame = Frame(app); var bounds = frame.Bounds;
        Assert.False(frame.SavedItem!.HasYrkMark); Assert.False(frame.CanTransform);
        Assert.False(frame.CanAdjustOpacity); Assert.False(frame.ShowResizeHandle); Assert.False(frame.ShowRotationHandle);
        app.Model.BeginItemDrag(frame); app.Model.MoveItem(frame, 30, 40); app.Model.EndItemDrag(frame);
        app.Model.RotateItem90(frame); app.Model.ChangeItemOpacity(frame, .3); await EditFinished(app);
        Assert.Equal(bounds, frame.Bounds); Assert.False(app.Model.IsDirty);
        var before = (await Render(app)).Pixels;
        app.Model.DeleteSelectedCommand.Execute(null); await EditFinished(app);
        Assert.Empty(await Items(app)); Assert.Empty(app.Model.Pages[0].Items);
        app.Model.UndoCommand.Execute(null); await Idle(app); Assert.Equal(before, (await Render(app)).Pixels);
        app.Model.RedoCommand.Execute(null); await Idle(app); Assert.Empty(await Items(app));
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unsafe_layout_is_explained_and_cannot_be_selected_or_deleted(bool clipped)
    {
        await using var app = new AppTestHarness();
        var cos = CosDocument.Load(await LegacyImageAsync()); var update = new CosIncrementalUpdate(cos);
        var edit = new CosPageEdit(cos, cos.GetPages()[0]); var old = Encoding.Latin1.GetString(edit.ReadContent());
        edit.ReplaceContent(Encoding.Latin1.GetBytes(clipped ? "q 40 280 50 80 re W n\n" + old + "\nQ"
            : old + "\nq 0 0 1 rg 200 200 10 10 re f Q\n" + old)); edit.Commit(update);
        var path = app.PathFor("unsafe.pdf"); var bytes = update.Build().Bytes; File.WriteAllBytes(path, bytes);
        Assert.True(await app.OpenAsync(path)); var before = (await Render(app)).Pixels;
        await Select(app); Assert.Empty(app.Model.Pages[0].Items);
        app.Model.DeleteSelectedCommand.Execute(null); await EditFinished(app);
        Assert.Single(app.Dialogs.Named("ShowInfo")); Assert.False(app.Model.IsDirty);
        Assert.Equal(before, (await Render(app)).Pixels); Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Signature_warning_precedes_first_edit_and_declining_changes_nothing(bool accept)
    {
        await using var app = new AppTestHarness(); var path = await PlacedSource(app);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=App saved item test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var signer = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var signed = await PdfSigner.SignAsync(File.ReadAllBytes(path), new(signer));
        var source = app.PathFor("signed.pdf"); File.WriteAllBytes(source, signed);
        Assert.True(await app.OpenAsync(source)); // Deliberately do not wait for background signature validation.
        await Select(app); var before = (await Render(app)).Pixels;
        app.Dialogs.Answer("Confirm", accept);
        app.Model.RotateItem90(Frame(app)); await EditFinished(app);
        Assert.Single(app.Dialogs.Named("Confirm")); Assert.Equal(signed, File.ReadAllBytes(source));
        if (!accept)
        {
            Assert.False(app.Model.IsDirty); Assert.Equal(before, (await Render(app)).Pixels);
            Assert.False(app.Model.UndoCommand.CanExecute(null));
        }
        else
        {
            app.Model.ChangeItemOpacity(Frame(app), .5); await EditFinished(app);
            Assert.Single(app.Dialogs.Named("Confirm"));
            var target = app.PathFor("signed-edited.pdf"); Assert.True(await app.SaveToAsync(target));
            var signature = Assert.Single(PdfSignatureVerifier.Verify(File.ReadAllBytes(target), new(CheckRevocation: false)));
            Assert.True(signature.IsIntact); Assert.True(signature.ModifiedAfterSigning);
        }
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Fact]
    public async Task Saved_selection_does_not_take_active_tools_or_new_placements_and_clears_on_tool_change()
    {
        await using var app = new AppTestHarness(); await PlacedSource(app);
        app.Model.CurrentTool = AnnotationTool.Rectangle;
        Assert.False(await app.Model.TrySelectSavedItemAsync(app.Model.Pages[0], .2, .2));
        app.Model.CurrentTool = AnnotationTool.Select;
        app.Model.AddTextCommand.Execute(null);
        Assert.False(await app.Model.TrySelectSavedItemAsync(app.Model.Pages[0], .2, .2));
        app.Model.OnPageClicked(app.Model.Pages[0], .55, .55);
        var newItem = Assert.Single(app.Model.Pages[0].Items); Assert.False(newItem.IsSaved);
        app.Model.CommitText(newItem, "New text");
        await Select(app); Assert.Contains(newItem, app.Model.Pages[0].Items);
        app.Model.CurrentTool = AnnotationTool.EditText; Assert.DoesNotContain(app.Model.Pages[0].Items, i => i.IsSaved);
        Assert.Contains(newItem, app.Model.Pages[0].Items);
        await Select(app); app.Model.ClearSavedItemSelection();
        Assert.DoesNotContain(app.Model.Pages[0].Items, i => i.IsSaved);
        Assert.Contains(newItem, app.Model.Pages[0].Items);
    }

    [Theory]
    [InlineData("RC4")] [InlineData("AES-128")] [InlineData("AES-256")]
    public async Task Protected_groups_move_rotate_change_opacity_delete_and_keep_both_passwords(string encryption)
    {
        await using var app = new AppTestHarness(); var path = await PlacedSource(app);
        var original = File.ReadAllBytes(path);
        var bytes = encryption == "AES-256" ? PdfSecurity.Protect(original, new("user", "owner", PdfPermissions.All))
            : ProtectedPdfFactory.Legacy(original, encryption == "RC4" ? 3 : 4, encryption == "RC4" ? "V2" : "AESV2", "user", "owner");
        var source = app.PathFor("protected.pdf"); File.WriteAllBytes(source, bytes);
        app.Dialogs.Answer("PromptPassword", "user"); Assert.True(await app.OpenAsync(source));
        app.Dialogs.Answer("ConfirmPassword", "user", "user");
        await Select(app); var frame = Frame(app);
        app.Model.BeginItemDrag(frame); app.Model.MoveItem(frame, 60, 40); app.Model.EndItemDrag(frame); await EditFinished(app);
        app.Model.RotateItem90(Frame(app)); await EditFinished(app);
        app.Model.ChangeItemOpacity(Frame(app), .5); await EditFinished(app);
        var moved = (await Items(app))[0]; Assert.InRange(moved.Bounds.X, .299, .301);
        Assert.InRange(moved.Opacity, .499, .503);
        app.Model.DeleteSelectedCommand.Execute(null); await EditFinished(app); Assert.Single(await Items(app));
        app.Model.UndoCommand.Execute(null); await Idle(app); Assert.Equal(2, (await Items(app)).Count);
        app.Model.RedoCommand.Execute(null); await Idle(app); Assert.Single(await Items(app));
        var target = app.PathFor("protected-edited.pdf"); Assert.True(await app.SaveToAsync(target));
        Assert.Equal(2, app.Dialogs.Named("ConfirmPassword").Length);
        Assert.Equal(bytes, File.ReadAllBytes(source));
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => app.Engine.OpenAsync(target));
        await using var user = await app.Engine.OpenAsync(target, "user");
        await using var owner = await app.Engine.OpenAsync(target, "owner");
        Assert.Single(await user.GetPageItemsAsync(0)); Assert.Single(await owner.GetPageItemsAsync(0));
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Fact]
    public async Task Stale_unmarked_selection_cannot_delete_another_image_at_its_old_index()
    {
        await using var app = new AppTestHarness(); var path = app.PathFor("unmarked.pdf");
        await using (var d = await app.Engine.OpenBytesAsync(Blank(), app.PathFor("blank.pdf")))
        {
            await d.AddImageAsync(0, Red(), new(.1, .1, .2, .2));
            await d.AddImageAsync(0, Red(), new(.7, .7, .2, .2)); await d.SaveAsync(path);
        }
        Assert.True(await app.OpenAsync(path)); await Select(app);
        var old = Frame(app).SavedItem!;
        await Document(app).RemovePageItemAsync(old.Reference); // Simulates an intervening document revision.
        var before = (await Render(app)).Pixels;
        app.Model.DeleteSelectedCommand.Execute(null); await EditFinished(app);
        Assert.Single(await Items(app)); Assert.Equal(before, (await Render(app)).Pixels);
        Assert.DoesNotContain(app.Model.Pages[0].Items, i => i.IsSaved);
        Assert.Single(app.Dialogs.Named("ShowInfo"));
    }

    [Fact]
    public async Task Cancelling_first_edit_password_keeps_the_document_and_frame_unchanged()
    {
        await using var app = new AppTestHarness();
        var bytes = PdfSecurity.Protect(await LegacyImageAsync(), new("user", "owner", PdfPermissions.All));
        var path = app.PathFor("cancel-password.pdf"); File.WriteAllBytes(path, bytes);
        app.Dialogs.Answer("PromptPassword", "user"); Assert.True(await app.OpenAsync(path));
        await Select(app); var before = (await Render(app)).Pixels;
        app.Dialogs.Answer("ConfirmPassword", (object?)null);
        app.Model.RotateItem90(Frame(app)); await EditFinished(app);
        Assert.False(app.Model.IsDirty); Assert.Equal(0, Frame(app).Rotation);
        Assert.Equal(before, (await Render(app)).Pixels); Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_rejected_native_edit_discards_its_partial_changes_and_preserves_pending_placements()
    {
        var armed = false;
        await using var app = new AppTestHarness(wrapOpenedDocument: d =>
        {
            if (!armed) return d;
            var proxy = DispatchProxy.Create<IPdfDocument, SignatureSecurityTests.TestProxy>();
            ((SignatureSecurityTests.TestProxy)(object)proxy).Handle = (method, args) =>
                method.Name == nameof(IPdfDocument.RotatePageItemAsync)
                    ? FailAfterRotation(d, args!) : method.Invoke(d, args);
            return proxy;
        });
        var path = await PlacedSource(app); var bytes = File.ReadAllBytes(path);
        var newItem = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Picture, 400, 400, Image());
        newItem.Bounds = new(.5, .5, .1, .1); Attach(app, newItem);
        var before = (await Render(app)).Pixels;
        await Select(app); var frame = Frame(app);
        armed = true;
        app.Model.BeginItemDrag(frame); app.Model.MoveItem(frame, 20, 30); frame.Rotation = 30; app.Model.EndItemDrag(frame);
        await EditFinished(app);
        Assert.Single(app.Dialogs.Named("ShowError"));
        Assert.Equal(before, (await Render(app)).Pixels); Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Contains(newItem, app.Model.Pages[0].Items);
        Assert.Equal(2, (await Items(app)).Count);
        Assert.False(app.Model.UndoCommand.CanExecute(null));
        Assert.Equal(0, Frame(app).Rotation);

        static async Task FailAfterRotation(IPdfDocument d, object?[] args)
        {
            await d.RotatePageItemAsync((PdfPageItemRef)args[0]!, (double)args[1]!, (CancellationToken)args[2]!, (PdfItemEditOptions?)args[3]);
            throw new PdfException("Synthetic refusal after native mutation");
        }
    }
}
