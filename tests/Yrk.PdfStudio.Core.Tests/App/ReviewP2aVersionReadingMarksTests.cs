using System.Reflection;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf.Forms;
using static Yrk.PdfStudio.Core.Tests.PageReviewTestSupport;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("Reader bookmarks")]
public sealed class ReviewP2aVersionReadingMarksTests : IDisposable
{
    private readonly string _oldStore = ReaderBookmarks.FilePath;
    private readonly RecentFileScope _recents = new();
    private readonly string _folder = Directory.CreateDirectory(PrivateTempDirectory.Path + "-version-marks-" + Guid.NewGuid().ToString("N")).FullName;
    public ReviewP2aVersionReadingMarksTests() => ReaderBookmarks.FilePath = Path.Combine(_folder, "marks.json");

    internal static async Task EditForm(AppTestHarness app, string name)
    {
        var draft = new FormFieldDraftViewModel(app.Model.Pages[0],
            new PdfNewField(0, new(.1, .1, .3, .05), PdfNewFieldKind.Text, name));
        typeof(MainViewModel).GetMethod("AddNewField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app.Model, [draft]);
        await (Task)typeof(MainViewModel).GetMethod("ApplyNewFieldsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app.Model, [])!;
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Fact]
    public async Task Form_edit_delete_page_form_edit_undo_restores_version_page_structure_and_its_working_marks()
    {
        await using var app = new AppTestHarness();
        var path = Path.Combine(_folder, "book.pdf");
        File.Copy(Sample(app), path);
        ReaderBookmarks.Add(path, 1, "Deleted page", "red");
        ReaderBookmarks.Add(path, 2, "Survivor", "purple");
        var saved = ReaderBookmarks.Get(path).ToArray();
        Assert.True(await app.OpenAsync(path));
        var original = app.Model.ReadingMarks.ToArray();

        await EditForm(app, "First form edit");
        app.Dialogs.Answer("Confirm", true);
        await app.Model.DeletePagesAsync([1]);
        Assert.Equal(1, Assert.Single(app.Model.ReadingMarks).PageIndex);
        await EditForm(app, "Second form edit");
        await UndoRedo(app);

        Assert.Equal(3, app.Model.Pages.Count);
        Assert.Equal(original, app.Model.ReadingMarks);
        Assert.Contains("Chapter 2", await Document(app).GetPageTextAsync(1));
        Assert.Equal(saved, ReaderBookmarks.Get(path));
        await UndoRedo(app, redo: true);
        Assert.Equal(2, app.Model.Pages.Count);
        var shifted = Assert.Single(app.Model.ReadingMarks);
        Assert.Equal(original[1] with { PageIndex = 1 }, shifted);
        Assert.Equal(saved, ReaderBookmarks.Get(path));
        Assert.True(await app.SaveToAsync(Path.Combine(_folder, "saved.pdf")));
        Assert.Equal(saved[1] with { PageIndex = 1 }, Assert.Single(ReaderBookmarks.Get(app.Model.DocumentPath!)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Version_restore_also_restores_pasted_cut_membership_before_saving(bool redo)
    {
        await using var source = new AppTestHarness();
        await using var target = new AppTestHarness();
        using var clipboard = new PageClipboard(new AppPageClipboardTests.MemoryClipboard(), source.PathFor("clipboard"));
        source.Model.PageClipboard = target.Model.PageClipboard = clipboard;
        Assert.True(await source.OpenAsync(Sample(source)));
        Assert.True(await target.OpenAsync(Sample(target)));
        await EditForm(target, "First");
        Assert.True(await source.Model.CopyPagesAsync([1], cut: true));
        await target.Model.PastePagesAsync(1);
        await EditForm(target, "Second");
        await UndoRedo(target);
        Assert.Equal(3, target.Model.Pages.Count);
        if (redo) await UndoRedo(target, redo: true);
        Assert.True(await target.SaveToAsync(target.PathFor("saved.pdf")));
        Assert.Equal(!redo, clipboard.HasUnpastedCut);
    }

    public void Dispose()
    {
        ReaderBookmarks.FilePath = _oldStore;
        _recents.Dispose();
        Directory.Delete(_folder, recursive: true);
    }
}
