using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("Reader bookmarks")]
public sealed class ClearRecentDataTests : IDisposable
{
    private readonly string _oldMarks = ReaderBookmarks.FilePath;
    private readonly string _oldPositions = ReadingPositions.FilePath;
    private readonly string _folder = Directory.CreateDirectory(PrivateTempDirectory.Path + "-clear-" + Guid.NewGuid().ToString("N")).FullName;
    private readonly IDisposable _recents;

    public ClearRecentDataTests()
    {
        ReaderBookmarks.FilePath = Path.Combine(_folder, "reader-bookmarks.json");
        ReadingPositions.FilePath = Path.Combine(_folder, "reading-positions.json");
        _recents = RecentFiles.UseStorage(Path.Combine(_folder, "recent.json"));
    }

    [Theory]
    [InlineData("recent.json")]
    [InlineData("recent-times.json")]
    [InlineData("reading-positions.json")]
    [InlineData("reader-bookmarks.json")]
    public void Locked_store_reports_failure_and_still_clears_every_other_store(string lockedName)
    {
        var path = Path.Combine(_folder, "book.pdf");
        RecentFiles.Add(path);
        ReadingPositions.Save(path, 2);
        ReaderBookmarks.Add(path, 1, "Remember", "blue");
        var lockedPath = Path.Combine(_folder, lockedName);
        var before = File.ReadAllBytes(lockedPath);
        using (var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(RecentFiles.Clear());
            if (lockedName != "reading-positions.json") Assert.Equal(0, ReadingPositions.GetPageIndex(path));
            if (lockedName != "reader-bookmarks.json") Assert.Empty(ReaderBookmarks.Get(path));
            if (lockedName != "recent.json") Assert.Equal("[]", File.ReadAllText(Path.Combine(_folder, "recent.json")));
            if (lockedName != "recent-times.json") Assert.False(File.Exists(Path.Combine(_folder, "recent-times.json")));
        }
        Assert.Equal(before, File.ReadAllBytes(lockedPath));
        Assert.True(RecentFiles.Clear());
        Assert.Empty(ReaderBookmarks.Get(path));
        Assert.Equal(0, ReadingPositions.GetPageIndex(path));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reading_stores_return_their_actual_clear_result(bool marks)
    {
        var path = Path.Combine(_folder, "book.pdf");
        ReadingPositions.Save(path, 2);
        ReaderBookmarks.Add(path, 1, "Remember", "blue");
        using (var held = new FileStream(marks ? ReaderBookmarks.FilePath : ReadingPositions.FilePath,
                   FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(marks ? ReaderBookmarks.Clear() : ReadingPositions.Clear());
        Assert.True(marks ? ReaderBookmarks.Clear() : ReadingPositions.Clear());
    }

    private async Task<string> Book(AppTestHarness app, string name)
    {
        var path = Path.Combine(_folder, name);
        await using var source = await app.Engine.OpenAsync(app.CreatePdf());
        await app.Engine.CombineAsync([source, source, source, source, source], path);
        return path;
    }

    [AppLogTests.FileSymbolicLinkFact]
    public void Reading_stores_reject_file_and_dangling_links_on_write_and_clear()
    {
        var document = Path.Combine(_folder, "book.pdf");
        foreach (var marks in new[] { false, true })
        foreach (var dangling in new[] { false, true })
        {
            var store = marks ? ReaderBookmarks.FilePath : ReadingPositions.FilePath;
            var target = Path.Combine(_folder, "private.json");
            if (!dangling) File.WriteAllText(target, "private content");
            File.CreateSymbolicLink(store, target);
            try
            {
                if (marks) ReaderBookmarks.Add(document, 1, "Remember", "blue");
                else ReadingPositions.Save(document, 2);
                Assert.False(marks ? ReaderBookmarks.Clear() : ReadingPositions.Clear());
                Assert.True((File.GetAttributes(store) & FileAttributes.ReparsePoint) != 0);
                if (dangling) Assert.False(File.Exists(target));
                else Assert.Equal("private content", File.ReadAllText(target));
                Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
            }
            finally { File.Delete(store); File.Delete(target); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reading_stores_reject_linked_parent_or_ancestor_without_creating_or_changing_files(bool ancestor)
    {
        var originalMarks = ReaderBookmarks.FilePath;
        var originalPositions = ReadingPositions.FilePath;
        var target = Directory.CreateDirectory(Path.Combine(_folder, "outside")).FullName;
        var link = Path.Combine(_folder, "linked");
        ScanSessionTests.MakeJunction(link, target);
        try
        {
            foreach (var marks in new[] { false, true })
            {
                var name = marks ? "reader-bookmarks.json" : "reading-positions.json";
                var stored = Path.Combine(target, name);
                File.WriteAllText(stored, "private content");
                var store = ancestor ? Path.Combine(link, "missing", name) : Path.Combine(link, name);
                if (marks) ReaderBookmarks.FilePath = store;
                else ReadingPositions.FilePath = store;
                if (marks) ReaderBookmarks.Add(Path.Combine(_folder, "book.pdf"), 1, "Remember", "blue");
                else ReadingPositions.Save(Path.Combine(_folder, "book.pdf"), 2);
                Assert.False(marks ? ReaderBookmarks.Clear() : ReadingPositions.Clear());
                Assert.Equal("private content", File.ReadAllText(stored));
                Assert.False(Directory.Exists(Path.Combine(target, "missing")));
                Assert.Empty(Directory.GetFiles(target, "*.tmp"));
            }
        }
        finally
        {
            ReaderBookmarks.FilePath = originalMarks;
            ReadingPositions.FilePath = originalPositions;
            Directory.Delete(link); // Remove the junction itself, never its target.
        }
    }

    private static async Task Undo(AppTestHarness app, bool redo = false)
    {
        (redo ? app.Model.RedoCommand : app.Model.UndoCommand).Execute(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (app.Model.IsBusy) await Task.Delay(5, timeout.Token);
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clearing_multiple_open_tabs_survives_page_and_version_undo_save_close_and_reopen(bool version)
    {
        await using var first = new AppTestHarness();
        await using var second = new AppTestHarness();
        var paths = new[] { await Book(first, "first.pdf"), await Book(second, "second.pdf") };
        var apps = new[] { first, second };
        for (var i = 0; i < apps.Length; i++)
        {
            ReaderBookmarks.Add(paths[i], 1, "Deleted page", "red");
            ReaderBookmarks.Add(paths[i], 4, "Survivor", "blue");
            ReadingPositions.Save(paths[i], 4);
            Assert.True(await apps[i].OpenAsync(paths[i]));
            if (version) await ReviewP2aVersionReadingMarksTests.EditForm(apps[i], "Before deletion");
            apps[i].Dialogs.Answer("Confirm", true);
            await apps[i].Model.DeletePagesAsync([1]);
            if (version) await ReviewP2aVersionReadingMarksTests.EditForm(apps[i], "After deletion");
        }
        first.Dialogs.Answer("ConfirmClearRecent", true);
        first.Model.ClearRecentCommand.Execute(null);
        foreach (var app in apps)
        {
            Assert.Empty(app.Model.ReadingMarks);
            app.Model.SaveReadingPosition();
            await Undo(app);
            Assert.Equal(5, app.Model.Pages.Count);
            Assert.Empty(app.Model.ReadingMarks);
            await Undo(app, redo: true);
            Assert.Equal(4, app.Model.Pages.Count);
            Assert.Empty(app.Model.ReadingMarks);
            await app.Model.SaveCommand.ExecuteAsync(null);
            Assert.False(app.Model.IsDirty);
            Assert.Empty(app.Dialogs.Named("ShowError"));
            app.Model.SaveReadingPosition();
            await app.Model.CloseTabAsync(keepAlive: true);
        }
        foreach (var path in paths)
        {
            Assert.Empty(ReaderBookmarks.Get(path));
            Assert.Equal(0, ReadingPositions.GetPageIndex(path));
            await using var reopened = new AppTestHarness();
            Assert.True(await reopened.OpenAsync(path));
            Assert.Equal(4, reopened.Model.Pages.Count);
            Assert.Equal(0, reopened.Model.CurrentPageIndex);
            Assert.Empty(reopened.Model.ReadingMarks);
        }
    }

    [Theory]
    [InlineData("navigate")]
    [InlineData("scroll")]
    [InlineData("open")]
    public async Task Only_user_navigation_or_explicit_open_resumes_position_saving(string action)
    {
        await using var app = new AppTestHarness();
        var path = await Book(app, "book.pdf");
        Assert.True(await app.OpenAsync(path));
        app.Model.UpdateViewport(0, 900, 700);
        app.Model.GoToPage(3);
        Assert.True(RecentFiles.Clear());
        app.Model.SaveReadingPosition();
        Assert.Equal("{}", File.ReadAllText(ReadingPositions.FilePath));
        // Layout and zoom changes do not count as reading.
        app.Model.UpdateViewport(0, 950, 700);
        app.Model.SaveReadingPosition();
        Assert.Equal("{}", File.ReadAllText(ReadingPositions.FilePath));
        if (action == "navigate") app.Model.GoToPage(2);
        else if (action == "scroll")
        {
            app.Model.OnUserNavigation();
            app.Model.UpdateViewport(1600, 950, 700);
        }
        else await app.Model.OpenFileAsync(path);
        app.Model.SaveReadingPosition();
        Assert.NotEqual("{}", File.ReadAllText(ReadingPositions.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clearing_preserves_cut_membership_and_PDF_edits_in_version_snapshots(bool redo)
    {
        await using var source = new AppTestHarness();
        await using var target = new AppTestHarness();
        using var clipboard = new PageClipboard(new AppPageClipboardTests.MemoryClipboard(), source.PathFor("clipboard"));
        source.Model.PageClipboard = target.Model.PageClipboard = clipboard;
        var sourcePath = await Book(source, "source.pdf");
        var targetPath = await Book(target, "target.pdf");
        Assert.True(await source.OpenAsync(sourcePath));
        Assert.True(await target.OpenAsync(targetPath));
        ReaderBookmarks.Add(targetPath, 2, "Remember", "blue");
        await ReviewP2aVersionReadingMarksTests.EditForm(target, "First");
        Assert.True(await source.Model.CopyPagesAsync([1], cut: true));
        await target.Model.PastePagesAsync(1);
        await ReviewP2aVersionReadingMarksTests.EditForm(target, "Second");
        Assert.True(RecentFiles.Clear());
        await Undo(target);
        Assert.Equal(5, target.Model.Pages.Count);
        if (redo) await Undo(target, redo: true);
        Assert.Equal(redo ? 6 : 5, target.Model.Pages.Count);
        Assert.Empty(target.Model.ReadingMarks);
        Assert.True(await target.SaveToAsync(targetPath));
        Assert.Equal(!redo, clipboard.HasUnpastedCut);
        Assert.Empty(ReaderBookmarks.Get(targetPath));
        await using var saved = await target.Engine.OpenAsync(targetPath);
        var fields = await saved.GetFormFieldsAsync(0);
        Assert.Contains(fields, field => field.Name == "First");
        Assert.Equal(redo, fields.Any(field => field.Name == "Second"));
    }

    [Fact]
    public async Task Cancel_leaves_stores_intact_and_partial_clear_is_reported()
    {
        await using var app = new AppTestHarness();
        var path = await Book(app, "book.pdf");
        Assert.True(await app.OpenAsync(path));
        ReaderBookmarks.Add(path, 1, "Remember", "blue");
        ReadingPositions.Save(path, 2);
        app.Dialogs.Answer("ConfirmClearRecent", false);
        app.Model.ClearRecentCommand.Execute(null);
        Assert.Single(ReaderBookmarks.Get(path));
        Assert.Equal(2, ReadingPositions.GetPageIndex(path));
        using (var held = new FileStream(ReadingPositions.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            app.Dialogs.Answer("ConfirmClearRecent", true);
            app.Model.ClearRecentCommand.Execute(null);
        }
        Assert.Single(app.Dialogs.Named("ShowInfo"));
        Assert.Empty(ReaderBookmarks.Get(path));
    }

    [Fact]
    public async Task New_marks_after_clear_are_visible_and_survive_save()
    {
        await using var app = new AppTestHarness();
        var path = await Book(app, "book.pdf");
        Assert.True(await app.OpenAsync(path));
        ReaderBookmarks.Add(path, 1, "Old", "red");
        Assert.True(RecentFiles.Clear());
        app.Dialogs.Answer("AskReaderBookmark", new ReaderBookmarkAnswer("New", "blue"));
        app.Model.EditReadingMark(2);
        Assert.Equal("New", Assert.Single(app.Model.ReadingMarks).Name);
        await app.Model.RotatePagesAsync([0], 1);
        await app.Model.SaveCommand.ExecuteAsync(null);
        Assert.Equal("New", Assert.Single(ReaderBookmarks.Get(path)).Name);
    }

    public void Dispose()
    {
        _recents.Dispose();
        ReaderBookmarks.FilePath = _oldMarks;
        ReadingPositions.FilePath = _oldPositions;
        Directory.Delete(_folder, true);
    }
}
