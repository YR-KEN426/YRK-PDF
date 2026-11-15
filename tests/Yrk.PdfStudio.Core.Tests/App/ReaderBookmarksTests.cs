using System.Security.Cryptography;
using System.Text.Json;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.Services.Backup;

namespace Yrk.PdfStudio.Core.Tests;

[CollectionDefinition("Reader bookmarks", DisableParallelization = true)]
public sealed class ReaderBookmarksCollection;

[Collection("Reader bookmarks")]
public sealed class ReaderBookmarksTests : IDisposable
{
    private readonly string _previousStore = ReaderBookmarks.FilePath;
    private readonly string _settings = Directory.CreateDirectory(Path.Combine(PrivateTempDirectory.Path, "bookmarks-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _documents = Directory.CreateDirectory(PrivateTempDirectory.Path + "-documents-" + Guid.NewGuid().ToString("N")).FullName;

    public ReaderBookmarksTests() => ReaderBookmarks.FilePath = Path.Combine(_settings, "reader-bookmarks.json");
    private string DocumentPath(string name = "book.pdf") => Path.Combine(_documents, name);
    private static ReaderBookmark Bookmark(int page) => new(page, $"Bookmark {page}", "blue", DateTimeOffset.UnixEpoch.AddSeconds(page));

    [Fact]
    public void Round_trip_normalizes_paths_sorts_pages_and_preserves_creation_time()
    {
        var before = DateTimeOffset.UtcNow;
        Assert.True(ReaderBookmarks.Add(Path.Combine(_documents, ".", "book.pdf"), 4, "Read", "red"));
        Assert.True(ReaderBookmarks.Add(DocumentPath().ToUpperInvariant(), 0, "", "purple"));
        var saved = ReaderBookmarks.Get(DocumentPath());
        Assert.Equal(new[] { 0, 4 }, saved.Select(b => b.PageIndex));
        Assert.Equal("", saved[0].Name);
        Assert.Equal("purple", saved[0].Color);
        Assert.InRange(saved[1].CreatedUtc, before, DateTimeOffset.UtcNow);
        Assert.Equal(saved, ReaderBookmarks.Get(DocumentPath().ToUpperInvariant()));
        using var json = JsonDocument.Parse(File.ReadAllText(ReaderBookmarks.FilePath));
        var entry = Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal(Path.GetFullPath(DocumentPath()), entry.Name, ignoreCase: true);
        Assert.True(entry.Value.GetProperty("LastUsed").GetDateTimeOffset() >= before);
        Assert.Empty(Directory.GetFiles(_settings, "*.tmp"));
    }

    [Fact]
    public void Oldest_file_is_evicted_at_200_and_reading_refreshes_last_used()
    {
        var old = DocumentPath("old.pdf");
        var refreshed = DocumentPath("refreshed.pdf");
        File.WriteAllText(ReaderBookmarks.FilePath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [old] = new { Bookmarks = new[] { Bookmark(0) }, LastUsed = DateTimeOffset.UtcNow.AddDays(-2) },
            [refreshed] = new { Bookmarks = new[] { Bookmark(1) }, LastUsed = DateTimeOffset.UtcNow.AddDays(-1) }
        }));
        for (var i = 0; i < 198; i++) ReaderBookmarks.Add(DocumentPath($"{i}.pdf"), 0, "", "red");
        Assert.Single(ReaderBookmarks.Get(refreshed));
        ReaderBookmarks.Add(DocumentPath("new.pdf"), 0, "", "red");
        Assert.Empty(ReaderBookmarks.Get(old));
        Assert.Single(ReaderBookmarks.Get(refreshed));
        using var json = JsonDocument.Parse(File.ReadAllText(ReaderBookmarks.FilePath));
        Assert.Equal(200, json.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void Limit_is_100_but_duplicate_page_edits_preserve_creation_time()
    {
        for (var i = 0; i < 100; i++) Assert.True(ReaderBookmarks.Add(DocumentPath(), i, "", "green"));
        var original = ReaderBookmarks.Get(DocumentPath())[20];
        Assert.False(ReaderBookmarks.Add(DocumentPath(), 100, "extra", "red"));
        Assert.True(ReaderBookmarks.Add(DocumentPath(), 20, "edited", "orange"));
        var saved = ReaderBookmarks.Get(DocumentPath());
        Assert.Equal(100, saved.Count);
        Assert.Equal(original with { Name = "edited", Color = "orange" }, saved[20]);
        ReaderBookmarks.Remove(DocumentPath(), 20);
        Assert.Equal(99, ReaderBookmarks.Get(DocumentPath()).Count);
        Assert.True(ReaderBookmarks.Add(DocumentPath(), 100, "new", "yellow"));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"file\":42}")]
    public void Corrupt_json_is_empty_and_can_be_replaced(string json)
    {
        File.WriteAllText(ReaderBookmarks.FilePath, json);
        Assert.Empty(ReaderBookmarks.Get(DocumentPath()));
        Assert.True(ReaderBookmarks.Add(DocumentPath(), 0, "", "red"));
        Assert.Single(ReaderBookmarks.Get(DocumentPath()));
    }

    [Fact]
    public void Temporary_and_invalid_paths_are_skipped_and_io_failures_do_not_throw()
    {
        var temp = Path.Combine(PrivateTempDirectory.Path, "converted.pdf");
        foreach (var path in new[] { temp, "", "\0" })
        {
            Assert.False(ReaderBookmarks.Add(path, 0, "", "red"));
            ReaderBookmarks.Save(path, [Bookmark(0)]);
            ReaderBookmarks.Remove(path, 0);
            Assert.Empty(ReaderBookmarks.Get(path));
        }
        Assert.False(File.Exists(ReaderBookmarks.FilePath));
        ReaderBookmarks.FilePath = _settings;
        ReaderBookmarks.Add(DocumentPath(), 0, "", "red");
        ReaderBookmarks.Save(DocumentPath(), [Bookmark(0)]);
        ReaderBookmarks.Clear();
        Assert.Empty(ReaderBookmarks.Get(DocumentPath()));
        Assert.Empty(Directory.GetFiles(_settings, "*.tmp"));
    }

    [Fact]
    public void Names_are_truncated_blank_names_stay_empty_and_colors_are_validated()
    {
        Assert.True(ReaderBookmarks.Add(DocumentPath(), 0, new string('ก', 45), "yellow"));
        Assert.Equal(new string('ก', 40), Assert.Single(ReaderBookmarks.Get(DocumentPath())).Name);
        Assert.True(ReaderBookmarks.Add(DocumentPath(), 0, "   ", "yellow"));
        Assert.Equal("", Assert.Single(ReaderBookmarks.Get(DocumentPath())).Name);
        Assert.False(ReaderBookmarks.Add(DocumentPath(), 0, "bad", "pink"));
        Assert.False(ReaderBookmarks.Add(DocumentPath(), -1, "bad", "red"));
        foreach (var color in new[] { "red", "orange", "yellow", "green", "blue", "purple" })
            Assert.True(ReaderBookmarks.Add(DocumentPath(), 0, "", color));
        ReaderBookmarks.Save(DocumentPath(), Enumerable.Range(0, 120).Select(Bookmark)
            .Concat([Bookmark(0) with { Name = new string('x', 50) }, Bookmark(-1), Bookmark(121) with { Color = "pink" }]));
        var saved = ReaderBookmarks.Get(DocumentPath());
        Assert.Equal(100, saved.Count);
        Assert.Equal(new string('x', 40), saved[0].Name);
    }

    [Fact]
    public void Loaded_lists_are_normalized_limited_and_invalid_entries_are_skipped()
    {
        var raw = new Dictionary<string, object?>
        {
            [DocumentPath()] = new { Bookmarks = Enumerable.Range(0, 120).Select(Bookmark).Cast<ReaderBookmark?>()
                .Concat([null, Bookmark(-1), Bookmark(125) with { Color = "pink" }]), LastUsed = DateTimeOffset.UtcNow },
            [Path.Combine(PrivateTempDirectory.Path, "temp.pdf")] = new { Bookmarks = new[] { Bookmark(0) }, LastUsed = DateTimeOffset.UtcNow },
            [DocumentPath("null.pdf")] = null
        };
        File.WriteAllText(ReaderBookmarks.FilePath, JsonSerializer.Serialize(raw));
        Assert.Equal(100, ReaderBookmarks.Get(DocumentPath()).Count);
        Assert.Empty(ReaderBookmarks.Get(DocumentPath("null.pdf")));
        Assert.Empty(ReaderBookmarks.Get(Path.Combine(PrivateTempDirectory.Path, "temp.pdf")));
    }

    [Fact]
    public void Delete_pages_shifts_survivors_and_returns_original_removed_bookmarks_for_undo()
    {
        var source = Enumerable.Range(0, 6).Select(Bookmark).ToArray();
        var change = ReaderBookmarks.DeletePages(source, [0, 2, 2, 4]);
        Assert.Equal(new[] { Bookmark(1) with { PageIndex = 0 }, Bookmark(3) with { PageIndex = 1 }, Bookmark(5) with { PageIndex = 2 } }, change.Bookmarks);
        Assert.Equal(new[] { source[0], source[2], source[4] }, change.Removed);
        Assert.Equal(Enumerable.Range(0, 6), source.Select(b => b.PageIndex));
        Assert.Empty(ReaderBookmarks.DeletePages(source, Enumerable.Range(0, 6)).Bookmarks);
        Assert.Equal(source, ReaderBookmarks.DeletePages(source, []).Bookmarks);
        ReaderBookmarks.Save(DocumentPath(), change.Bookmarks);
        ReaderBookmarks.Save(DocumentPath(), source); // Undo restores the full snapshot and original timestamps.
        Assert.Equal(source, ReaderBookmarks.Get(DocumentPath()));
    }

    [Theory]
    [InlineData(0, 3, 3, 5, 7)]
    [InlineData(2, 2, 0, 4, 6)]
    [InlineData(5, 2, 0, 2, 4)]
    [InlineData(2, 0, 0, 2, 4)]
    public void Insert_pages_handles_start_middle_end_and_zero(int at, int count, int a, int b, int c)
    {
        var source = new[] { Bookmark(0), Bookmark(2), Bookmark(4) };
        var change = ReaderBookmarks.InsertPages(source, at, count);
        Assert.Equal(new[] { a, b, c }, change.Bookmarks.Select(x => x.PageIndex));
        Assert.Equal(source.Select(x => x.CreatedUtc), change.Bookmarks.Select(x => x.CreatedUtc));
        Assert.Empty(change.Removed);
        Assert.Equal(new[] { 0, 2, 4 }, source.Select(x => x.PageIndex));
    }

    [Fact]
    public void Reorder_moves_pages_both_directions_and_mapping_handles_replacement()
    {
        var source = Enumerable.Range(0, 5).Select(Bookmark).ToArray();
        foreach (var order in new[] { new[] { 4, 0, 1, 2, 3 }, new[] { 1, 2, 3, 4, 0 }, new[] { 4, 3, 2, 1, 0 }, new[] { 0, 1, 2, 3, 4 } })
        {
            var change = ReaderBookmarks.ReorderPages(source, order);
            Assert.Equal(order.Select((old, index) => source[old] with { PageIndex = index }), change.Bookmarks);
            Assert.Empty(change.Removed);
        }
        var replaced = ReaderBookmarks.RemapPages(source, new Dictionary<int, int> { [0] = 4, [2] = 0, [4] = 2 });
        Assert.Equal(new[] { source[2] with { PageIndex = 0 }, source[4] with { PageIndex = 2 }, source[0] with { PageIndex = 4 } }, replaced.Bookmarks);
        Assert.Equal(new[] { source[1], source[3] }, replaced.Removed);
        Assert.Equal(source, ReaderBookmarks.ReorderPages(source, [0, 1, 2, 3, 4]).Bookmarks); // Rotation has no index change.
        Assert.Equal(source, ReaderBookmarks.RemapPages(source, new Dictionary<int, int>()).Removed);
        Assert.Equal(new[] { source[1], source[3] }, ReaderBookmarks.ReorderPages(source, [4, 2, 0]).Removed);
        Assert.False(File.Exists(ReaderBookmarks.FilePath)); // All transforms are pure.
    }

    [Fact]
    public void Invalid_page_operations_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReaderBookmarks.InsertPages([], -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReaderBookmarks.InsertPages([], 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReaderBookmarks.DeletePages([], [-1]));
        Assert.Throws<ArgumentException>(() => ReaderBookmarks.ReorderPages([], [0, 0]));
        Assert.Throws<ArgumentException>(() => ReaderBookmarks.RemapPages([], new Dictionary<int, int> { [0] = 0, [1] = 0 }));
        Assert.Throws<ArgumentException>(() => ReaderBookmarks.RemapPages([], new Dictionary<int, int> { [0] = -1 }));
    }

    [Fact]
    public void Move_transfers_and_replaces_destination_and_clear_removes_everything()
    {
        var original = new[] { Bookmark(2), Bookmark(4) };
        ReaderBookmarks.Save(DocumentPath(), original);
        ReaderBookmarks.Add(DocumentPath("new.pdf"), 0, "old destination", "red");
        ReaderBookmarks.Move(DocumentPath(), Path.Combine(PrivateTempDirectory.Path, "skip.pdf"));
        Assert.Equal(original, ReaderBookmarks.Get(DocumentPath()));
        ReaderBookmarks.Move(DocumentPath(), DocumentPath("new.pdf"));
        Assert.Empty(ReaderBookmarks.Get(DocumentPath()));
        Assert.Equal(original, ReaderBookmarks.Get(DocumentPath("new.pdf")));
        ReaderBookmarks.Move(DocumentPath("new.pdf"), DocumentPath("NEW.PDF"));
        Assert.Equal(original, ReaderBookmarks.Get(DocumentPath("new.pdf")));
        ReaderBookmarks.Move(DocumentPath("missing.pdf"), DocumentPath("new.pdf"));
        Assert.Empty(ReaderBookmarks.Get(DocumentPath("new.pdf")));
        ReaderBookmarks.Clear();
        Assert.Empty(ReaderBookmarks.Get(DocumentPath("new.pdf")));
        Assert.Equal("{}", File.ReadAllText(ReaderBookmarks.FilePath));
    }

    [Fact]
    public void Backup_import_preserves_bookmarks_and_obeys_settings_selection_and_replace_flag()
    {
        ReaderBookmarks.Save(DocumentPath(), [Bookmark(4)]);
        var expected = File.ReadAllBytes(ReaderBookmarks.FilePath);
        var archive = Path.Combine(_settings, "bookmarks.yrkbackup");
        var target = Path.Combine(_settings, "import");
        var staging = Path.Combine(_settings, "staging");
        var manifest = new BackupWriter().Write(archive, _settings, "test-password", "test", BackupSelection.Settings);
        Assert.Equal("reader-bookmarks.json", Assert.Single(manifest.Files).Path);
        Assert.Equal(BackupSelection.Settings, BackupWriter.Category("reader-bookmarks.json"));
        var imported = new BackupReader().Import(archive, target, staging, "test-password");
        Assert.Equal("reader-bookmarks.json", Assert.Single(imported.ImportedPaths).Key);
        ReaderBookmarks.FilePath = Path.Combine(target, "reader-bookmarks.json");
        Assert.Equal(expected, File.ReadAllBytes(ReaderBookmarks.FilePath));
        Assert.Equal(new[] { Bookmark(4) }, ReaderBookmarks.Get(DocumentPath()));
        ReaderBookmarks.Save(DocumentPath(), [Bookmark(2)]);
        Assert.Empty(new BackupReader().Import(archive, target, staging, "test-password").ImportedPaths);
        Assert.Equal(new[] { Bookmark(2) }, ReaderBookmarks.Get(DocumentPath()));
        new BackupReader().Import(archive, target, staging, "test-password", replaceSettings: true);
        Assert.Equal(expected, File.ReadAllBytes(ReaderBookmarks.FilePath));
        var excluded = new BackupWriter().Write(Path.Combine(_settings, "excluded.yrkbackup"), target,
            "test-password", "test", BackupSelection.Stamps);
        Assert.Empty(excluded.Files);
    }

    [Fact]
    public void Adding_editing_and_removing_bookmarks_never_changes_pdf_bytes()
    {
        var path = DocumentPath();
        TestPdfFactory.CreateReportPdf(path);
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        ReaderBookmarks.Add(path, 0, "read", "blue");
        ReaderBookmarks.Add(path, 0, "edited", "red");
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
        ReaderBookmarks.Remove(path, 0);
        Assert.Empty(ReaderBookmarks.Get(path));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    public void Dispose()
    {
        ReaderBookmarks.FilePath = _previousStore;
        Directory.Delete(_settings, recursive: true);
        Directory.Delete(_documents, recursive: true);
    }
}
