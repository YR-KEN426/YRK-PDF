using Yrk.PdfStudio.App.Services;
using static Yrk.PdfStudio.Core.Tests.PageReviewTestSupport;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("Reader bookmarks")]
public sealed class ReviewP2bSaveAsReadingMarksTests : IDisposable
{
    private readonly string _oldStore = ReaderBookmarks.FilePath;
    private readonly RecentFileScope _recents = new();
    private readonly string _folder = Directory.CreateDirectory(PrivateTempDirectory.Path + "-save-as-marks-" + Guid.NewGuid().ToString("N")).FullName;
    public ReviewP2bSaveAsReadingMarksTests() => ReaderBookmarks.FilePath = Path.Combine(_folder, "marks.json");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Move_from_missing_or_temporary_source_record_removes_destination_record_and_is_persisted(bool temporarySource)
    {
        var destination = Path.Combine(_folder, "destination.pdf");
        ReaderBookmarks.Add(destination, 1, "Old destination", "red");
        ReaderBookmarks.Move(Path.Combine(temporarySource ? PrivateTempDirectory.Path : _folder, "no-record.pdf"), destination);
        Assert.Empty(ReaderBookmarks.Get(destination));
        Assert.DoesNotContain("destination.pdf", File.ReadAllText(ReaderBookmarks.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_as_over_existing_marked_PDF_replaces_marks_with_empty_source_or_working_list(bool workingList)
    {
        await using var app = new AppTestHarness();
        var path = Path.Combine(_folder, "source.pdf");
        var destination = Path.Combine(_folder, "destination.pdf");
        File.Copy(Sample(app), path);
        File.Copy(path, destination);
        ReaderBookmarks.Add(destination, 0, "Old destination", "red");
        Assert.Empty(ReaderBookmarks.Get(path));
        Assert.True(await app.OpenAsync(path));
        if (workingList)
        {
            app.Dialogs.Answer("Confirm", true);
            await app.Model.DeletePagesAsync([1]);
        }
        app.Dialogs.Answer("PickSavePdfFile", destination);
        await app.Model.SaveAsCommand.ExecuteAsync(null);
        Assert.Empty(app.Dialogs.Named("ShowError"));
        Assert.Empty(ReaderBookmarks.Get(destination));
        Assert.Empty(app.Model.ReadingMarks);
        Assert.Equal(destination, app.Model.DocumentPath);
        Assert.Equal(workingList ? 2 : 3, app.Model.Pages.Count);
        await app.Model.CloseTabAsync(keepAlive: true);
        Assert.True(await app.OpenAsync(destination));
        Assert.Empty(app.Model.ReadingMarks);
    }

    public void Dispose()
    {
        ReaderBookmarks.FilePath = _oldStore;
        _recents.Dispose();
        Directory.Delete(_folder, recursive: true);
    }
}
