using System.Reflection;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Forms;
using Yrk.PdfStudio.Core.Pdf.Security;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("Reader bookmarks")]
public sealed class AppReadingMarksTests : IDisposable
{
    private readonly string _oldStore = ReaderBookmarks.FilePath;
    private readonly object? _oldRecents = RecentItems.GetValue(null);
    private static FieldInfo RecentItems => typeof(RecentFiles).GetField("s_items", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly string _folder = Directory.CreateDirectory(PrivateTempDirectory.Path + "-reading-ui-" + Guid.NewGuid().ToString("N")).FullName;
    public AppReadingMarksTests()
    {
        ReaderBookmarks.FilePath = Path.Combine(_folder, "marks.json");
        RecentItems.SetValue(null, new ObservableCollection<RecentFile>());
    }
    private string Sample(string name = "book.pdf")
    {
        var path = Path.Combine(_folder, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf"), path);
        return path;
    }

    private static void Mark(AppTestHarness app, int page, string name, string color = "blue")
    {
        app.Dialogs.Answer("AskReaderBookmark", new ReaderBookmarkAnswer(name, color));
        app.Model.GoToPage(page);
        app.Model.ReadingMarkCommand.Execute(null);
    }

    private static async Task UndoRedo(AppTestHarness app, bool redo = false)
    {
        (redo ? app.Model.RedoCommand : app.Model.UndoCommand).Execute(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (app.Model.IsBusy) await Task.Delay(5, timeout.Token);
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    private async Task<string> SixPageBook(AppTestHarness app)
    {
        await using var source = await app.Engine.OpenAsync(Sample());
        var path = Path.Combine(_folder, "six.pdf");
        await app.Engine.CombineAsync([source, source], path);
        return path;
    }

    private static async Task DeleteSecondPage(AppTestHarness app)
    {
        app.Dialogs.Answer("Confirm", true);
        await app.Model.DeletePagesAsync([1]);
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Fact]
    public async Task Unsaved_delete_keeps_crash_store_and_discard_reopens_original_page_five()
    {
        await using var app = new AppTestHarness();
        var path = await SixPageBook(app);
        Assert.True(await app.OpenAsync(path));
        Mark(app, 4, "Page five", "purple");
        var saved = ReaderBookmarks.Get(path).ToArray();
        var pdf = File.ReadAllBytes(path);
        await DeleteSecondPage(app);
        Assert.Equal(3, Assert.Single(app.Model.ReadingMarks).PageIndex);
        // Read the actual persisted store, as another process would after a crash.
        Assert.Equal(saved, ReaderBookmarks.Get(path));
        Assert.Equal(pdf, File.ReadAllBytes(path));
        app.Dialogs.Answer("AskSaveChanges", SaveChangesChoice.Discard);
        Assert.True(await app.Model.ConfirmDiscardOrSaveAsync());
        await app.Model.CloseTabAsync(keepAlive: true);
        Assert.Empty(app.Model.ReadingMarks);
        Assert.Equal(saved, ReaderBookmarks.Get(path));
        await using var reopened = new AppTestHarness();
        Assert.True(await reopened.OpenAsync(path));
        Assert.Equal(6, reopened.Model.Pages.Count);
        Assert.Equal(4, Assert.Single(reopened.Model.ReadingMarks).PageIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saving_delete_persists_page_four_and_save_as_keeps_original_page_five(bool saveAs)
    {
        await using var app = new AppTestHarness();
        var path = await SixPageBook(app);
        var target = saveAs ? Path.Combine(_folder, "saved.pdf") : path;
        Assert.True(await app.OpenAsync(path));
        Mark(app, 4, "Page five", "purple");
        var saved = Assert.Single(ReaderBookmarks.Get(path));
        await DeleteSecondPage(app);
        // Existing destination marks must be replaced.
        if (saveAs) ReaderBookmarks.Add(target, 0, "Old destination", "red");
        if (saveAs)
        {
            app.Dialogs.Answer("PickSavePdfFile", target);
            await app.Model.SaveAsCommand.ExecuteAsync(null);
        }
        else await app.Model.SaveCommand.ExecuteAsync(null);
        Assert.Empty(app.Dialogs.Named("ShowError"));
        Assert.False(app.Model.IsDirty);
        Assert.Equal(target, app.Model.DocumentPath);
        Assert.Equal(saved with { PageIndex = 3 }, Assert.Single(ReaderBookmarks.Get(target)));
        Assert.Equal(3, Assert.Single(app.Model.ReadingMarks).PageIndex);
        if (saveAs) Assert.Equal(saved, Assert.Single(ReaderBookmarks.Get(path)));
        await app.Model.CloseTabAsync(keepAlive: true);
        await using var reopened = new AppTestHarness();
        Assert.True(await reopened.OpenAsync(target));
        Assert.Equal(5, reopened.Model.Pages.Count);
        Assert.Equal(3, Assert.Single(reopened.Model.ReadingMarks).PageIndex);
        if (saveAs)
        {
            Assert.True(await reopened.OpenAsync(path));
            Assert.Equal(6, reopened.Model.Pages.Count);
            Assert.Equal(4, Assert.Single(reopened.Model.ReadingMarks).PageIndex);
        }
    }

    [Fact]
    public async Task Pending_structure_defers_user_add_edit_and_both_remove_paths_until_save()
    {
        await using var app = new AppTestHarness();
        var path = await SixPageBook(app);
        Assert.True(await app.OpenAsync(path));
        Mark(app, 0, "Remove with command");
        Mark(app, 2, "Remove with dialog");
        Mark(app, 4, "Original");
        var saved = ReaderBookmarks.Get(path).ToArray();
        await DeleteSecondPage(app);
        Mark(app, 3, new string('a', 45), "red");
        Assert.Equal("Original", app.Dialogs.Named("AskReaderBookmark").Last().Arguments[0]);
        Assert.Equal(saved[2].CreatedUtc, app.Model.ReadingMarks.Single(b => b.PageIndex == 3).CreatedUtc);
        Mark(app, 4, "New", "green");
        app.Model.RemoveReadingMark(0);
        app.Dialogs.Answer("AskReaderBookmark", new ReaderBookmarkAnswer("", "blue", Remove: true));
        app.Model.EditReadingMark(1);
        Assert.Equal(new[] { 3, 4 }, app.Model.ReadingMarks.Select(b => b.PageIndex));
        Assert.Equal(new string('a', 40), app.Model.ReadingMarks[0].Name);
        Assert.Equal(saved, ReaderBookmarks.Get(path));
        Assert.True(await app.SaveToAsync(path));
        Assert.Equal(app.Model.ReadingMarks.Select(b => (b.PageIndex, b.Name, b.Color, b.CreatedUtc)),
            ReaderBookmarks.Get(path).Select(b => (b.PageIndex, b.Name, b.Color, b.CreatedUtc)));
    }

    [Fact]
    public async Task Undo_delete_restores_working_marks_and_immediate_writes_then_redo_defers_again()
    {
        await using var app = new AppTestHarness();
        var path = await SixPageBook(app);
        Assert.True(await app.OpenAsync(path));
        Mark(app, 4, "Original");
        await DeleteSecondPage(app);
        Mark(app, 3, "Edited survivor", "red");
        Assert.Equal("Original", Assert.Single(ReaderBookmarks.Get(path)).Name);
        await UndoRedo(app);
        Assert.Equal(4, Assert.Single(app.Model.ReadingMarks).PageIndex);
        Mark(app, 4, "Immediate", "green");
        Assert.Equal("Immediate", Assert.Single(ReaderBookmarks.Get(path)).Name);
        await UndoRedo(app, redo: true);
        Assert.Equal(3, Assert.Single(app.Model.ReadingMarks).PageIndex);
        Mark(app, 3, "Deferred");
        Assert.Equal("Immediate", Assert.Single(ReaderBookmarks.Get(path)).Name);
        Assert.Equal(4, Assert.Single(ReaderBookmarks.Get(path)).PageIndex);
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("rotate")]
    [InlineData("undo-delete")]
    [InlineData("undo-insert")]
    [InlineData("undo-move")]
    public async Task Save_as_without_pending_structure_keeps_move_behavior(string operation)
    {
        await using var app = new AppTestHarness();
        var path = Sample();
        var target = Path.Combine(_folder, "moved.pdf");
        Assert.True(await app.OpenAsync(path));
        Mark(app, 2, "Keep");
        switch (operation)
        {
            case "rotate": await app.Model.RotatePagesAsync([0], 1); break;
            case "undo-delete": await DeleteSecondPage(app); await UndoRedo(app); break;
            case "undo-insert":
                app.Dialogs.Answer("PickPdfFile", path);
                await app.Model.InsertPagesFromFileAsync(1);
                await UndoRedo(app);
                break;
            case "undo-move": await app.Model.MovePagesAsync([0], 2); await UndoRedo(app); break;
        }
        Mark(app, 2, "Immediate", "red");
        Assert.Equal("Immediate", Assert.Single(ReaderBookmarks.Get(path)).Name);
        Assert.True(await app.SaveToAsync(target));
        Assert.Empty(ReaderBookmarks.Get(path));
        Assert.Equal("Immediate", Assert.Single(ReaderBookmarks.Get(target)).Name);
    }

    [Fact]
    public async Task Replacing_document_discards_working_marks_and_tabs_do_not_share_them()
    {
        await using var first = new AppTestHarness();
        await using var second = new AppTestHarness();
        var path = await SixPageBook(first);
        var other = Sample("other.pdf");
        Assert.True(await first.OpenAsync(path));
        Mark(first, 4, "Saved");
        await DeleteSecondPage(first);
        Mark(first, 3, "Unsaved");
        Assert.True(await second.OpenAsync(path));
        first.Model.IsActiveTab = false;
        second.Model.IsActiveTab = true;
        Assert.Equal(4, Assert.Single(second.Model.ReadingMarks).PageIndex);
        Assert.Equal("Saved", Assert.Single(second.Model.ReadingMarks).Name);
        first.Model.IsActiveTab = true;
        Assert.Equal(3, Assert.Single(first.Model.ReadingMarks).PageIndex);
        first.Dialogs.Answer("AskSaveChanges", SaveChangesChoice.Discard);
        await first.Model.OpenFileAsync(other);
        Assert.Empty(first.Model.ReadingMarks);
        Assert.True(await first.OpenAsync(path));
        Assert.Equal(4, Assert.Single(first.Model.ReadingMarks).PageIndex);
        Assert.Equal("Saved", Assert.Single(first.Model.ReadingMarks).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_save_with_pending_structure_keeps_both_stores_and_working_list(bool saveAs)
    {
        await using var app = new AppTestHarness(failWriteMidway: true);
        var path = await SixPageBook(app);
        var target = saveAs ? Path.Combine(_folder, "failed.pdf") : path;
        Assert.True(await app.OpenAsync(path));
        Mark(app, 4, "Saved");
        await DeleteSecondPage(app);
        Mark(app, 3, "Unsaved");
        Assert.False(await app.SaveToAsync(target));
        Assert.Equal(4, Assert.Single(ReaderBookmarks.Get(path)).PageIndex);
        Assert.Equal("Saved", Assert.Single(ReaderBookmarks.Get(path)).Name);
        if (saveAs) Assert.Empty(ReaderBookmarks.Get(target));
        Assert.Equal(3, Assert.Single(app.Model.ReadingMarks).PageIndex);
        Assert.Equal("Unsaved", Assert.Single(app.Model.ReadingMarks).Name);
        Assert.True(app.Model.IsDirty);
    }

    [Fact]
    public async Task Save_as_with_empty_working_list_clears_destination_and_preserves_original()
    {
        await using var app = new AppTestHarness();
        var path = Sample();
        var target = Path.Combine(_folder, "empty-marks.pdf");
        Assert.True(await app.OpenAsync(path));
        Mark(app, 1, "Deleted page");
        ReaderBookmarks.Add(target, 0, "Previous destination", "red");
        await DeleteSecondPage(app);
        Assert.Empty(app.Model.ReadingMarks);
        Assert.True(await app.SaveToAsync(target));
        Assert.Single(ReaderBookmarks.Get(path));
        Assert.Empty(ReaderBookmarks.Get(target));
        Assert.Empty(app.Model.ReadingMarks);
    }

    [Fact]
    public async Task Form_working_copy_and_version_undo_redo_preserve_pending_marks_until_save()
    {
        await using var app = new AppTestHarness();
        var path = await SixPageBook(app);
        Assert.True(await app.OpenAsync(path));
        Mark(app, 4, "Saved");
        await DeleteSecondPage(app);
        Mark(app, 3, "Working", "red");
        var marks = app.Model.ReadingMarks.ToArray();
        var draft = new FormFieldDraftViewModel(app.Model.Pages[0],
            new PdfNewField(0, new(.1, .1, .3, .05), PdfNewFieldKind.Text, "Test field"));
        typeof(MainViewModel).GetMethod("AddNewField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app.Model, [draft]);
        await (Task)typeof(MainViewModel).GetMethod("ApplyNewFieldsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app.Model, [])!;
        Assert.Empty(app.Dialogs.Named("ShowError"));
        Assert.Equal(marks, app.Model.ReadingMarks);
        await UndoRedo(app);
        Assert.Equal(marks, app.Model.ReadingMarks);
        Assert.Equal(5, app.Model.Pages.Count);
        await UndoRedo(app, redo: true);
        Assert.Equal(marks, app.Model.ReadingMarks);
        Assert.Equal(4, Assert.Single(ReaderBookmarks.Get(path)).PageIndex);
        Assert.Equal("Saved", Assert.Single(ReaderBookmarks.Get(path)).Name);
        var target = Path.Combine(_folder, "form.pdf");
        Assert.True(await app.SaveToAsync(target));
        Assert.Equal(3, Assert.Single(ReaderBookmarks.Get(target)).PageIndex);
        Assert.Equal("Working", Assert.Single(ReaderBookmarks.Get(target)).Name);
        Assert.Equal(4, Assert.Single(ReaderBookmarks.Get(path)).PageIndex);
    }

    [Fact]
    public async Task Whole_file_save_publishes_working_marks_before_reopening_and_keeps_source()
    {
        await using var app = new AppTestHarness();
        var path = await SixPageBook(app);
        Assert.True(await app.OpenAsync(path));
        Mark(app, 4, "Keep");
        await DeleteSecondPage(app);
        var target = Path.Combine(_folder, "transformed.pdf");
        Func<IPdfDocument, byte[], CancellationToken, Task<byte[]?>> transform = (_, bytes, _) => Task.FromResult<byte[]?>(bytes);
        Assert.Equal(target, await (Task<string?>)typeof(MainViewModel)
            .GetMethod("TransformToFileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app.Model, [target, "Testing", transform])!);
        Assert.Equal(3, Assert.Single(app.Model.ReadingMarks).PageIndex);
        Assert.Equal(3, Assert.Single(ReaderBookmarks.Get(target)).PageIndex);
        Assert.Equal(4, Assert.Single(ReaderBookmarks.Get(path)).PageIndex);
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Fact]
    public async Task Add_edit_remove_and_cancel_preserve_hash_creation_time_and_clean_document()
    {
        await using var app = new AppTestHarness();
        var path = Sample();
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        Assert.False(app.Model.ReadingMarkCommand.CanExecute(null));
        Assert.True(await app.OpenAsync(path));
        Assert.True(app.Model.ReadingMarkCommand.CanExecute(null));
        var document = (IPdfDocument)typeof(MainViewModel).GetField("_document", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app.Model)!;
        var renderedBefore = await document.RenderPageAsync(1, 120, 160);
        Mark(app, 1, "Review", "red");
        Assert.Equal(renderedBefore.Pixels, (await document.RenderPageAsync(1, 120, 160)).Pixels);
        Assert.Equal(Loc.F("Page {0}", 2), app.Dialogs.Named("AskReaderBookmark")[0].Arguments[0]);
        var original = Assert.Single(app.Model.ReadingMarks);
        Assert.Equal(1, original.PageIndex);
        Assert.Equal("red", original.Color);
        Mark(app, 1, new string('a', 45), "green");
        var edited = Assert.Single(app.Model.ReadingMarks);
        Assert.Equal(new string('a', 40), edited.Name);
        Assert.Equal("green", edited.Color);
        Assert.Equal(original.CreatedUtc, edited.CreatedUtc);
        Assert.Equal(true, app.Dialogs.Named("AskReaderBookmark")[1].Arguments[2]);
        app.Dialogs.Answer("AskReaderBookmark", (object?)null);
        app.Model.EditReadingMark(1, focusColour: true);
        Assert.Equal(edited, Assert.Single(app.Model.ReadingMarks));
        Assert.Equal(true, app.Dialogs.Named("AskReaderBookmark")[2].Arguments[3]);
        app.Dialogs.Answer("AskReaderBookmark", new ReaderBookmarkAnswer("", "blue", Remove: true));
        app.Model.ReadingMarkCommand.Execute(null);
        Assert.False(app.Model.HasReadingMarks);
        Assert.Empty(ReaderBookmarks.Get(path));
        Assert.False(app.Model.IsDirty);
        Assert.True(await app.Model.ConfirmDiscardOrSaveAsync());
        Assert.Empty(app.Dialogs.Named("AskSaveChanges"));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task Fresh_model_reopens_marks_switching_files_does_not_leak_and_invalid_pages_are_hidden()
    {
        var path = Sample();
        var other = Sample("other.pdf");
        await using (var first = new AppTestHarness())
        {
            Assert.True(await first.OpenAsync(path));
            Mark(first, 2, "Return", "purple");
            Assert.True(await first.OpenAsync(other));
            Assert.Empty(first.Model.ReadingMarks);
            Assert.True(await first.OpenAsync(path));
            Assert.Equal("Return", Assert.Single(first.Model.ReadingMarks).Name);
            await first.Model.CloseTabAsync(keepAlive: true);
            Assert.Empty(first.Model.ReadingMarks);
        }
        ReaderBookmarks.Add(path, 99, "Old page", "orange");
        await using var reopened = new AppTestHarness();
        Assert.True(await reopened.OpenAsync(path));
        Assert.Equal(2, Assert.Single(reopened.Model.ReadingMarks).PageIndex);
        Assert.Empty(reopened.Dialogs.Named("ShowError"));
    }

    [Theory]
    [InlineData("delete", 1, -1)]
    [InlineData("cut", 1, -1)]
    [InlineData("move", 0, 2)]
    [InlineData("insert", 4, 5)]
    [InlineData("paste", 4, 5)]
    [InlineData("rotate", 1, 2)]
    public async Task Page_operations_and_undo_redo_follow_the_same_pages(string operation, int firstPage, int lastPage)
    {
        await using var app = new AppTestHarness();
        var path = Sample();
        var source = Sample("insert.pdf");
        using var clipboard = new PageClipboard(new AppPageClipboardTests.MemoryClipboard
            { Data = new(null, [source]) }, app.PathFor("clipboard"));
        app.Model.PageClipboard = clipboard;
        Assert.True(await app.OpenAsync(path));
        Mark(app, 1, "Middle", "yellow");
        Mark(app, 2, "Last", "purple");
        var original = app.Model.ReadingMarks.ToArray();
        switch (operation)
        {
            case "delete": app.Dialogs.Answer("Confirm", true); await app.Model.DeletePagesAsync([1]); break;
            case "cut": Assert.True(await app.Model.CopyPagesAsync([1], cut: true)); break;
            case "move": await app.Model.MovePagesAsync([1], 0); break;
            case "insert": app.Dialogs.Answer("PickPdfFile", source); await app.Model.InsertPagesFromFileAsync(1); break;
            case "paste": await app.Model.PastePagesAsync(1); break;
            case "rotate": await app.Model.RotatePagesAsync([1, 2], 1); break;
        }
        var after = app.Model.ReadingMarks.ToArray();
        Assert.Equal(original.Select(b => (b.PageIndex, b.Name, b.Color, b.CreatedUtc)),
            ReaderBookmarks.Get(path).Select(b => (b.PageIndex, b.Name, b.Color, b.CreatedUtc)));
        Assert.Equal(lastPage < 0 ? new[] { firstPage } : new[] { firstPage, lastPage }, after.Select(b => b.PageIndex));
        if (lastPage < 0) Assert.Equal("Last", Assert.Single(after).Name);
        await UndoRedo(app);
        Assert.Equal(original, app.Model.ReadingMarks);
        Assert.Equal(original.Select(b => b.CreatedUtc), ReaderBookmarks.Get(path).Select(b => b.CreatedUtc));
        await UndoRedo(app, redo: true);
        Assert.Equal(after, app.Model.ReadingMarks);
        Assert.Equal(original.Select(b => b.PageIndex), ReaderBookmarks.Get(path).Select(b => b.PageIndex));
        await UndoRedo(app);
        Assert.Equal(original, app.Model.ReadingMarks);
    }

    [Fact]
    public async Task Undo_preserves_edits_to_surviving_marks_and_redo_restores_marks_added_to_pasted_pages()
    {
        await using var app = new AppTestHarness();
        var path = Sample();
        using var clipboard = new PageClipboard(new AppPageClipboardTests.MemoryClipboard
            { Data = new(null, [path]) }, app.PathFor("clipboard"));
        app.Model.PageClipboard = clipboard;
        Assert.True(await app.OpenAsync(path));
        Mark(app, 2, "Last");
        await app.Model.PastePagesAsync(1);
        Mark(app, 2, "On pasted page", "orange");
        var pasted = app.Model.ReadingMarks.Single(b => b.PageIndex == 2);
        Mark(app, 5, "Edited survivor", "red");
        await UndoRedo(app);
        Assert.Equal("Edited survivor", Assert.Single(app.Model.ReadingMarks).Name);
        Assert.Equal(2, Assert.Single(app.Model.ReadingMarks).PageIndex);
        await UndoRedo(app, redo: true);
        Assert.Equal(pasted, app.Model.ReadingMarks.Single(b => b.PageIndex == 2));
        Assert.Equal("Edited survivor", app.Model.ReadingMarks.Single(b => b.PageIndex == 5).Name);
    }

    [Fact]
    public async Task Scan_cleanup_deletes_blank_pages_and_restores_marks_on_undo_and_redo()
    {
        await using var app = new AppTestHarness();
        var blank = app.PathFor("blank.pdf");
        await app.Engine.CreateFromImagesAsync([new PdfBitmapInput(new PdfImage(10, 10, Enumerable.Repeat((byte)255, 400).ToArray()))], new(), blank);
        await using (var content = await app.Engine.OpenAsync(app.CreatePdf()))
        await using (var empty = await app.Engine.OpenAsync(blank))
            await app.Engine.CombineAsync([content, empty, content], Path.Combine(_folder, "scan.pdf"));
        Assert.True(await app.OpenAsync(Path.Combine(_folder, "scan.pdf")));
        Mark(app, 1, "Blank", "red");
        Mark(app, 2, "Last", "green");
        var before = app.Model.ReadingMarks.ToArray();
        app.Dialogs.Answer("AskScanCleanup", new ScanCleanupChoice(false, false, true));
        await app.Model.CleanUpScanCommand.ExecuteAsync(null);
        Assert.Equal(2, app.Model.Pages.Count);
        Assert.Equal(1, Assert.Single(app.Model.ReadingMarks).PageIndex);
        await UndoRedo(app);
        Assert.Equal(before, app.Model.ReadingMarks);
        await UndoRedo(app, redo: true);
        Assert.Equal(1, Assert.Single(app.Model.ReadingMarks).PageIndex);
    }

    [Fact]
    public async Task Save_as_moves_marks_only_after_success_and_fresh_reopening_uses_new_path()
    {
        var path = Sample();
        var target = Path.Combine(_folder, "renamed.pdf");
        ReadingMarkViewModel[] marks;
        await using (var app = new AppTestHarness())
        {
            Assert.True(await app.OpenAsync(path));
            Mark(app, 2, "Keep", "purple");
            marks = app.Model.ReadingMarks.ToArray();
            app.Dialogs.Answer("PickSavePdfFile", target);
            await app.Model.SaveAsCommand.ExecuteAsync(null);
            Assert.Equal(target, app.Model.DocumentPath);
            Assert.Equal(marks, app.Model.ReadingMarks);
            Assert.Empty(ReaderBookmarks.Get(path));
            Assert.Equal(2, Assert.Single(ReaderBookmarks.Get(target)).PageIndex);
        }
        await using var reopened = new AppTestHarness();
        Assert.True(await reopened.OpenAsync(target));
        Assert.Equal(marks, reopened.Model.ReadingMarks);
    }

    [Fact]
    public async Task Failed_save_as_keeps_marks_on_the_original_path()
    {
        await using var app = new AppTestHarness(failWriteMidway: true);
        var path = Sample();
        var target = Path.Combine(_folder, "failed.pdf");
        Assert.True(await app.OpenAsync(path));
        Mark(app, 0, "Keep");
        Assert.False(await app.SaveToAsync(target));
        Assert.Single(ReaderBookmarks.Get(path));
        Assert.Empty(ReaderBookmarks.Get(target));
    }

    [Theory]
    [InlineData("readonly")]
    [InlineData("restricted")]
    [InlineData("signed")]
    public async Task Marks_work_without_permission_checks_pdf_writes_or_save_prompts(string kind)
    {
        await using var app = new AppTestHarness();
        var path = Sample();
        if (kind == "readonly") File.SetAttributes(path, FileAttributes.ReadOnly);
        if (kind == "restricted")
            File.WriteAllBytes(path, PdfSecurity.Protect(File.ReadAllBytes(path), new(null, "owner", PdfPermissions.None)));
        if (kind == "signed")
        {
            using var rsa = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=Reading marks", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(path, await PdfSigner.SignAsync(File.ReadAllBytes(path), new(certificate)));
        }
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        try
        {
            Assert.True(await app.OpenAsync(path));
            Mark(app, 1, "Review");
            Mark(app, 1, "Changed", "red");
            Assert.Single(app.Model.ReadingMarks);
            app.Model.RemoveReadingMark(1);
            Assert.Empty(app.Model.ReadingMarks);
            Assert.False(app.Model.IsDirty);
            Assert.True(await app.Model.ConfirmDiscardOrSaveAsync());
            Assert.Empty(app.Dialogs.Named("Confirm"));
            Assert.Empty(app.Dialogs.Named("AskOwnerPassword"));
            Assert.Empty(app.Dialogs.Named("AskSaveChanges"));
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    public void Dispose()
    {
        ReaderBookmarks.FilePath = _oldStore;
        RecentItems.SetValue(null, _oldRecents);
        Directory.Delete(_folder, recursive: true);
    }
}
