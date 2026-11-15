using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using static Yrk.PdfStudio.Core.Tests.PageReviewTestSupport;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ReviewP1aClipboardReplacementTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Close_and_update_without_window_warn_before_save(bool update, bool proceed)
    {
        await using var app = new AppTestHarness();
        using var clipboard = new PageClipboard(new AppPageClipboardTests.MemoryClipboard(), app.PathFor("clipboard"));
        app.Model.PageClipboard = clipboard;
        Assert.True(await app.OpenAsync(Sample(app)));
        Assert.True(await app.Model.CopyPagesAsync([1], cut: true));
        app.Dialogs.Calls.Clear();
        app.Dialogs.Answer("Confirm", proceed);
        app.Dialogs.Answer("AskSaveChanges", SaveChangesChoice.Discard);

        if (update)
            Assert.Equal(proceed, await (Task<bool>)typeof(Yrk.PdfStudio.App.ViewModels.MainViewModel)
                .GetMethod("ConfirmCloseForUpdateAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(app.Model, null)!);
        else
            await app.Model.CloseCommand.ExecuteAsync(null);

        Assert.Equal(proceed ? new[] { "Confirm", "AskSaveChanges" } : new[] { "Confirm" },
            app.Dialogs.Calls.Select(call => call.Name));
        Assert.Equal(update || !proceed, app.Model.HasDocument);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_of_saved_unpasted_cut_requires_replace_dialog_and_cancel_is_noop(bool nextCut)
    {
        await using var app = new AppTestHarness();
        var os = new AppPageClipboardTests.MemoryClipboard();
        using var clipboard = new PageClipboard(os, app.PathFor("clipboard"));
        app.Model.PageClipboard = clipboard;
        var source = Sample(app);
        Assert.True(await app.OpenAsync(source));
        Assert.True(await app.Model.CopyPagesAsync([1], cut: true));
        Assert.True(await app.SaveToAsync(source)); // The only remaining copy of page 2 is the clipboard.
        var oldPath = clipboard.ReadSource()!.Path;
        var oldPayload = os.Data;
        var disk = File.ReadAllBytes(source);
        app.Dialogs.Answer("Confirm", false, true);

        Assert.False(await app.Model.CopyPagesAsync([0], cut: nextCut));

        Assert.Equal(oldPayload, os.Data);
        Assert.Equal([oldPath], Directory.GetFiles(clipboard.Folder));
        Assert.Equal(2, app.Model.Pages.Count);
        Assert.False(app.Model.IsDirty);
        Assert.Equal(disk, File.ReadAllBytes(source));
        Assert.True(clipboard.HasUnpastedCut);
        Assert.Equal(Loc.T("Cut pages have not been pasted yet. Copying again will lose them.\n\nContinue?"),
            Assert.Single(app.Dialogs.Named("Confirm")).Arguments[0]);

        Assert.True(await app.Model.CopyPagesAsync([0], cut: nextCut));
        Assert.False(File.Exists(oldPath));
        Assert.Equal(nextCut, clipboard.HasUnpastedCut);
        Assert.Equal(2, app.Dialogs.Named("Confirm").Length);
    }

    [Fact]
    public async Task Other_instance_publish_deletes_only_its_own_copy_and_original_cut_still_prompts_on_exit()
    {
        await using var app = new AppTestHarness();
        var os = new AppPageClipboardTests.MemoryClipboard();
        using var origin = new PageClipboard(os, app.PathFor("clipboard"));
        using var other = new PageClipboard(os, origin.Folder);
        app.Model.PageClipboard = origin;
        Assert.True(await app.OpenAsync(Sample(app)));
        Assert.True(await app.Model.CopyPagesAsync([1], cut: true));
        var cutPath = origin.ReadSource()!.Path;
        app.Model.PageClipboard = other;
        Assert.True(await app.Model.CopyPagesAsync([0]));
        var otherOld = other.ReadSource()!.Path;
        Assert.True(await app.Model.CopyPagesAsync([1]));
        Assert.False(File.Exists(otherOld));
        Assert.True(File.Exists(cutPath));
        Assert.DoesNotContain(cutPath, other.ReadSource()!.Path);
        Assert.True(origin.HasUnpastedCut);
        Assert.Empty(app.Dialogs.Named("Confirm"));
        Assert.False(origin.ConfirmClose(_ => false));
        other.Dispose();
        Assert.True(File.Exists(cutPath));
        Assert.False(origin.ConfirmClose(_ => false));
    }
}
