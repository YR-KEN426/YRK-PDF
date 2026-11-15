using Yrk.PdfStudio.App.Services;
using static Yrk.PdfStudio.Core.Tests.PageReviewTestSupport;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ReviewP1bCutAcknowledgementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_after_undo_does_not_acknowledge_but_save_after_redo_does(bool redo)
    {
        await using var a = new AppTestHarness();
        await using var b = new AppTestHarness();
        var os = new AppPageClipboardTests.MemoryClipboard();
        using var origin = new PageClipboard(os, a.PathFor("clipboard"));
        using var target = new PageClipboard(os, origin.Folder);
        a.Model.PageClipboard = origin;
        b.Model.PageClipboard = target;
        Assert.True(await a.OpenAsync(Sample(a)));
        Assert.True(await b.OpenAsync(Sample(b)));
        Assert.True(await a.Model.CopyPagesAsync([1], cut: true));
        await b.Model.PastePagesAsync(1);
        Assert.True(origin.HasUnpastedCut);
        await UndoRedo(b);
        if (redo) await UndoRedo(b, redo: true);
        Assert.True(origin.HasUnpastedCut);
        var saved = b.PathFor("saved.pdf");
        Assert.True(await b.SaveToAsync(saved));
        Assert.Equal(!redo, origin.HasUnpastedCut);
        await using var pdf = await b.Engine.OpenAsync(saved);
        Assert.Equal(redo ? 4 : 3, pdf.PageCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discard_or_failed_save_never_acknowledges_and_origin_still_warns(bool failSave)
    {
        await using var a = new AppTestHarness();
        await using var b = new AppTestHarness(failWriteMidway: failSave);
        using var clipboard = new PageClipboard(new AppPageClipboardTests.MemoryClipboard(), a.PathFor("clipboard"));
        a.Model.PageClipboard = b.Model.PageClipboard = clipboard;
        Assert.True(await a.OpenAsync(Sample(a)));
        Assert.True(await b.OpenAsync(Sample(b)));
        Assert.True(await a.Model.CopyPagesAsync([1], cut: true));
        await b.Model.PastePagesAsync(1);
        if (failSave) Assert.False(await b.SaveToAsync(b.PathFor("failed.pdf")));
        b.Dialogs.Answer("AskSaveChanges", SaveChangesChoice.Discard);
        Assert.True(await b.Model.ConfirmDiscardOrSaveAsync());
        await b.Model.CloseTabAsync(keepAlive: true);
        Assert.True(clipboard.HasUnpastedCut);
        Assert.False(clipboard.ConfirmClose(_ => false));
        Assert.True(File.Exists(clipboard.ReadSource()!.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saving_requires_all_pasted_cut_pages_and_page_delete_undo_restores_pending_cut(bool undoDelete)
    {
        await using var a = new AppTestHarness();
        await using var b = new AppTestHarness();
        using var clipboard = new PageClipboard(new AppPageClipboardTests.MemoryClipboard(), a.PathFor("clipboard"));
        a.Model.PageClipboard = b.Model.PageClipboard = clipboard;
        Assert.True(await a.OpenAsync(Sample(a)));
        Assert.True(await b.OpenAsync(Sample(b)));
        Assert.True(await a.Model.CopyPagesAsync([1, 2], cut: true));
        await b.Model.PastePagesAsync(1);
        await b.Model.MovePagesAsync([1, 2], 3);
        b.Dialogs.Answer("Confirm", true);
        await b.Model.DeletePagesAsync([3]);
        if (undoDelete) await UndoRedo(b);
        Assert.True(await b.SaveToAsync(b.PathFor("saved.pdf")));
        Assert.Equal(!undoDelete, clipboard.HasUnpastedCut);
    }
}
