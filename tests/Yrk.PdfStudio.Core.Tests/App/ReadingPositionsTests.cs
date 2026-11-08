using System.Text.Json;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

[CollectionDefinition("Reading positions", DisableParallelization = true)]
public sealed class ReadingPositionsCollection;

[Collection("Reading positions")]
public sealed class ReadingPositionsTests : IDisposable
{
    private readonly string _previousStore = ReadingPositions.FilePath;
    private readonly string _settings = Directory.CreateDirectory(Path.Combine(PrivateTempDirectory.Path, "positions-" + Guid.NewGuid().ToString("N"))).FullName;
    // A sibling of the redirected test temp is a regular document path to the app. Nothing in real user settings is used.
    private readonly string _documents = Directory.CreateDirectory(PrivateTempDirectory.Path + "-documents-" + Guid.NewGuid().ToString("N")).FullName;

    public ReadingPositionsTests() => ReadingPositions.FilePath = Path.Combine(_settings, "reading-positions.json");

    private string DocumentPath(string name = "book.pdf") => Path.Combine(_documents, name);

    [Fact]
    public void Store_round_trips_normalized_case_insensitive_paths_and_page_zero()
    {
        var path = DocumentPath();
        ReadingPositions.Save(Path.Combine(_documents, ".", "book.pdf"), 4);
        Assert.Equal(4, ReadingPositions.GetPageIndex(path.ToUpperInvariant()));
        ReadingPositions.Save(path.ToUpperInvariant(), 0);
        Assert.Equal(0, ReadingPositions.GetPageIndex(path));
        using var json = JsonDocument.Parse(File.ReadAllText(ReadingPositions.FilePath));
        var entry = Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal(2, entry.Value.EnumerateObject().Count());
        Assert.Equal(0, entry.Value.GetProperty("PageIndex").GetInt32());
        Assert.True(entry.Value.GetProperty("LastUsed").GetDateTimeOffset() > DateTimeOffset.MinValue);
        Assert.Empty(Directory.GetFiles(_settings, "*.tmp"));
        ReadingPositions.Clear();
        Assert.Equal(0, ReadingPositions.GetPageIndex(path));
    }

    [Fact]
    public void Store_drops_oldest_at_200_and_refreshes_last_used()
    {
        var old = DocumentPath("old.pdf");
        var refreshed = DocumentPath("refreshed.pdf");
        File.WriteAllText(ReadingPositions.FilePath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [old] = new { PageIndex = 1, LastUsed = DateTimeOffset.UtcNow.AddDays(-2) },
            [refreshed] = new { PageIndex = 2, LastUsed = DateTimeOffset.UtcNow.AddDays(-1) }
        }));
        for (var i = 0; i < 198; i++) ReadingPositions.Save(DocumentPath($"{i}.pdf"), 3);
        ReadingPositions.Save(refreshed, 4);
        ReadingPositions.Save(DocumentPath("new.pdf"), 5);
        Assert.Equal(0, ReadingPositions.GetPageIndex(old));
        Assert.Equal(4, ReadingPositions.GetPageIndex(refreshed));
        Assert.Equal(5, ReadingPositions.GetPageIndex(DocumentPath("new.pdf")));
        using var json = JsonDocument.Parse(File.ReadAllText(ReadingPositions.FilePath));
        Assert.Equal(200, json.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    public void Corrupt_json_is_empty_and_can_be_replaced(string json)
    {
        File.WriteAllText(ReadingPositions.FilePath, json);
        Assert.Equal(0, ReadingPositions.GetPageIndex(DocumentPath()));
        ReadingPositions.Save(DocumentPath(), 4);
        Assert.Equal(4, ReadingPositions.GetPageIndex(DocumentPath()));
    }

    [Fact]
    public void Temporary_documents_are_skipped_and_unwritable_store_does_not_throw()
    {
        var temporary = Path.Combine(PrivateTempDirectory.Path, "converted.pdf");
        ReadingPositions.Save(temporary, 4);
        Assert.False(File.Exists(ReadingPositions.FilePath));
        Assert.Equal(0, ReadingPositions.GetPageIndex(temporary));
        ReadingPositions.FilePath = _settings; // A directory cannot be read or replaced as a JSON file.
        Assert.Equal(0, ReadingPositions.GetPageIndex(DocumentPath()));
        ReadingPositions.Save(DocumentPath(), 4);
        ReadingPositions.Clear();
    }

    [Fact]
    public async Task Closing_and_fresh_reopening_restores_page_five_but_ViewState_wins()
    {
        await using var app = new AppTestHarness();
        var path = await CreateBookAsync(app);
        Assert.True(await app.OpenAsync(path));
        app.Model.GoToPage(4);
        await app.Model.CloseTabAsync(keepAlive: true);
        Assert.Equal(4, ReadingPositions.GetPageIndex(path));
        Assert.True(await app.OpenAsync(path));
        Assert.Equal(4, app.Model.CurrentPageIndex);

        app.Model.GoToPage(1);
        var state = app.CaptureViewState();
        await app.Model.CloseTabAsync(keepAlive: true);
        ReadingPositions.Save(path, 4);
        Assert.True(await app.OpenWithViewStateAsync(path, state));
        Assert.Equal(1, app.Model.CurrentPageIndex);
    }

    [Theory]
    [InlineData(5)] // Equal to the page count is already out of range.
    [InlineData(500)]
    [InlineData(0)]
    public async Task Out_of_range_or_first_page_stays_at_page_one_without_extra_navigation(int page)
    {
        await using var app = new AppTestHarness();
        var path = await CreateBookAsync(app);
        ReadingPositions.Save(path, page);
        var scrolls = new List<double>();
        app.Model.ScrollRequested += (_, offset) => scrolls.Add(offset);
        Assert.True(await app.OpenAsync(path));
        Assert.Equal(0, app.Model.CurrentPageIndex);
        Assert.Single(scrolls);
        Assert.Equal(0, scrolls[0]);
    }

    private async Task<string> CreateBookAsync(AppTestHarness app)
    {
        await using var source = await app.Engine.OpenAsync(app.CreatePdf());
        var path = DocumentPath();
        await app.Engine.CombineAsync([source, source, source, source, source], path);
        return path;
    }

    public void Dispose()
    {
        ReadingPositions.FilePath = _previousStore;
        Directory.Delete(_settings, recursive: true);
        Directory.Delete(_documents, recursive: true);
    }
}
