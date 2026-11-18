using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>
/// recent.json stays the main file (a plain list of paths); the time each file was opened lives in recent-times.json beside it.
/// Every path here is made up and lies outside the temp folder (the app never lists temp files).
/// </summary>
[Collection("Reader bookmarks")] // RecentFiles.Clear also clears the reading bookmarks file.
public sealed class RecentFilesTimesTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 10, 42, 0, TimeSpan.Zero);

    private readonly string _folder = Directory.CreateDirectory(Path.Combine(PrivateTempDirectory.Path, "recent-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _previousBookmarks = ReaderBookmarks.FilePath;
    private readonly string _previousPositions = ReadingPositions.FilePath;
    private DateTimeOffset _now = Start;

    public RecentFilesTimesTests()
    {
        ReaderBookmarks.FilePath = Path.Combine(_folder, "reader-bookmarks.json");
        ReadingPositions.FilePath = Path.Combine(_folder, "reading-positions.json");
    }

    public void Dispose()
    {
        ReaderBookmarks.FilePath = _previousBookmarks;
        ReadingPositions.FilePath = _previousPositions;
    }

    private string Main => Path.Combine(_folder, "recent.json");
    private string Times => Path.Combine(_folder, "recent-times.json");
    private IDisposable Use() => RecentFiles.UseStorage(Main, () => _now);
    private static string Doc(string name) => @"C:\Example\Documents\" + name;
    private string[] Paths() => JsonSerializer.Deserialize<string[]>(File.ReadAllText(Main))!;
    private Dictionary<string, string> TimeFile() => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Times))!;

    [Fact]
    public void Recent_json_keeps_its_original_format_and_times_go_to_the_second_file()
    {
        using var storage = Use();
        RecentFiles.Add(Doc("a.pdf"));
        _now = _now.AddMinutes(5);
        RecentFiles.Add(Doc("b.pdf"));
        Assert.Equal(new[] { Doc("b.pdf"), Doc("a.pdf") }, Paths()); // Only strings, newest first, exactly as before.
        using var json = JsonDocument.Parse(File.ReadAllText(Main));
        Assert.All(json.RootElement.EnumerateArray(), e => Assert.Equal(JsonValueKind.String, e.ValueKind));
        var times = TimeFile();
        Assert.Equal(2, times.Count);
        Assert.Equal(Start, DateTimeOffset.Parse(times[Doc("a.pdf")], CultureInfo.InvariantCulture));
        Assert.Equal(Start.AddMinutes(5), DateTimeOffset.Parse(times[Doc("b.pdf")], CultureInfo.InvariantCulture));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void Times_are_read_back_and_missing_ones_stay_empty()
    {
        using (Use())
        {
            RecentFiles.Add(Doc("a.pdf"));
        }

        File.WriteAllText(Main, JsonSerializer.Serialize(new[] { Doc("a.pdf"), Doc("old-version.pdf") })); // The second came from an older version.
        using var reload = Use();
        Assert.Equal(Start, RecentFiles.Items[0].LastOpened);
        Assert.Null(RecentFiles.Items[1].LastOpened);
        Assert.Equal("", RecentTimeText.Format(RecentFiles.Items[1].LastOpened, _now));
    }

    [Fact]
    public void No_times_file_loads_the_history_without_times()
    {
        File.WriteAllText(Main, JsonSerializer.Serialize(new[] { Doc("a.pdf"), Doc("b.pdf") }));
        using var storage = Use();
        Assert.Equal(new[] { Doc("a.pdf"), Doc("b.pdf") }, RecentFiles.Items.Select(i => i.Path));
        Assert.All(RecentFiles.Items, i => Assert.Null(i.LastOpened));
        Assert.False(File.Exists(Times));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"C:\\\\Example\\\\Documents\\\\a.pdf\": 5}")]
    [InlineData("")]
    public void Damaged_times_file_never_stops_the_history_from_loading(string content)
    {
        File.WriteAllText(Main, JsonSerializer.Serialize(new[] { Doc("a.pdf") }));
        File.WriteAllText(Times, content);
        using var storage = Use();
        Assert.Equal(Doc("a.pdf"), Assert.Single(RecentFiles.Items).Path);
        Assert.Null(RecentFiles.Items[0].LastOpened);
        // The next write puts a good file in its place.
        RecentFiles.Add(Doc("b.pdf"));
        Assert.Equal(new[] { Start.ToString("O", CultureInfo.InvariantCulture) }, TimeFile().Values);
    }

    [Fact]
    public void A_time_with_a_bad_value_is_skipped_alone()
    {
        File.WriteAllText(Main, JsonSerializer.Serialize(new[] { Doc("a.pdf"), Doc("b.pdf") }));
        File.WriteAllText(Times, JsonSerializer.Serialize(new Dictionary<string, string>
            { [Doc("a.pdf")] = "yesterday maybe", [Doc("b.pdf")] = Start.ToString("O", CultureInfo.InvariantCulture) }));
        using var storage = Use();
        Assert.Null(RecentFiles.Items[0].LastOpened);
        Assert.Equal(Start, RecentFiles.Items[1].LastOpened);
    }

    [Fact]
    public void The_same_path_in_another_case_is_one_entry_with_the_new_time()
    {
        using var storage = Use();
        RecentFiles.Add(Doc("Report.pdf"));
        _now = _now.AddHours(3);
        RecentFiles.Add(Doc("REPORT.PDF"));
        var item = Assert.Single(RecentFiles.Items);
        Assert.Equal(Start.AddHours(3), item.LastOpened);
        Assert.Single(Paths());
        Assert.Single(TimeFile());
    }

    [Fact]
    public void More_than_twelve_files_keep_twelve_and_drop_the_times_of_the_rest()
    {
        using var storage = Use();
        for (var i = 0; i < 15; i++)
        {
            _now = _now.AddMinutes(1);
            RecentFiles.Add(Doc($"f{i}.pdf"));
        }

        Assert.Equal(RecentFiles.MaxCount, Paths().Length);
        var times = TimeFile();
        Assert.Equal(RecentFiles.MaxCount, times.Count);
        Assert.DoesNotContain(Doc("f0.pdf"), times.Keys);
        Assert.DoesNotContain(Doc("f2.pdf"), times.Keys);
        Assert.Contains(Doc("f14.pdf"), times.Keys);
    }

    [Fact]
    public void Remove_takes_the_time_out_too_and_leaves_the_others()
    {
        using var storage = Use();
        RecentFiles.Add(Doc("a.pdf"));
        RecentFiles.Add(Doc("b.pdf"));
        RecentFiles.Remove(Doc("A.PDF"));
        Assert.Equal(new[] { Doc("b.pdf") }, Paths());
        Assert.Equal(new[] { Doc("b.pdf") }, TimeFile().Keys);
    }

    [Fact]
    public void Clear_removes_both_files_content_and_the_reading_history()
    {
        using var storage = Use();
        RecentFiles.Add(Doc("a.pdf"));
        ReadingPositions.Save(Doc("a.pdf"), 3);
        RecentFiles.Clear();
        Assert.Empty(RecentFiles.Items);
        Assert.Empty(Paths());
        Assert.False(File.Exists(Times), "No path may stay behind in the times file.");
        Assert.Equal(0, ReadingPositions.GetPageIndex(Doc("a.pdf")));
        using var reload = Use();
        Assert.Empty(RecentFiles.Items);
    }

    [Fact]
    public void A_stop_between_the_two_writes_never_brings_back_a_cleared_path()
    {
        // The state after recent.json was cleared and the times file was not yet written.
        File.WriteAllText(Main, "[]");
        File.WriteAllText(Times, JsonSerializer.Serialize(new Dictionary<string, string>
            { [Doc("gone.pdf")] = Start.ToString("O", CultureInfo.InvariantCulture) }));
        using var storage = Use();
        Assert.Empty(RecentFiles.Items);
        // And a path that is added later does not pick up the old time.
        _now = _now.AddDays(1);
        RecentFiles.Add(Doc("gone.pdf"));
        Assert.Equal(Start.AddDays(1), Assert.Single(RecentFiles.Items).LastOpened);
        Assert.Equal(new[] { Doc("gone.pdf") }, TimeFile().Keys);
    }

    [Fact]
    public void An_older_version_that_edits_only_recent_json_costs_nothing_when_we_come_back()
    {
        using (Use())
        {
            RecentFiles.Add(Doc("a.pdf"));
            _now = _now.AddMinutes(1);
            RecentFiles.Add(Doc("b.pdf"));
        }

        // The old version drops "a", adds "c" and does not know the times file.
        File.WriteAllText(Main, JsonSerializer.Serialize(new[] { Doc("c.pdf"), Doc("b.pdf") }));
        using var storage = Use();
        Assert.Null(RecentFiles.Items[0].LastOpened);
        Assert.Equal(Start.AddMinutes(1), RecentFiles.Items[1].LastOpened);
        Assert.Contains(Doc("a.pdf"), TimeFile().Keys); // Still on disk until the next write...
        _now = _now.AddMinutes(1);
        RecentFiles.Add(Doc("d.pdf"));
        Assert.DoesNotContain(Doc("a.pdf"), TimeFile().Keys); // ...and gone after it.
        Assert.Equal(2, TimeFile().Count); // b and d: c never had a time.
    }

    [Fact]
    public void A_times_file_that_cannot_be_written_leaves_recent_json_and_the_list_correct()
    {
        Directory.CreateDirectory(Times); // A folder where the file should go: the replace fails.
        using var storage = Use();
        RecentFiles.Add(Doc("a.pdf"));
        Assert.Equal(new[] { Doc("a.pdf") }, Paths());
        Assert.Equal(Doc("a.pdf"), Assert.Single(RecentFiles.Items).Path);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void A_recent_json_that_cannot_be_written_skips_the_times_file_and_keeps_the_list_in_memory()
    {
        Directory.CreateDirectory(Main);
        using var storage = Use();
        RecentFiles.Add(Doc("a.pdf"));
        Assert.Equal(Doc("a.pdf"), Assert.Single(RecentFiles.Items).Path);
        Assert.False(File.Exists(Times));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void Clear_and_remove_survive_a_failed_write_without_touching_user_files()
    {
        var document = Path.Combine(_folder, "kept.pdf");
        File.WriteAllText(document, "x");
        using var storage = Use();
        RecentFiles.Add(Doc("a.pdf"));
        RecentFiles.Add(Doc("b.pdf"));
        File.Delete(Main);
        Directory.CreateDirectory(Main); // From now on the main file cannot be replaced.
        RecentFiles.Remove(Doc("a.pdf"));
        Assert.Equal(new[] { Doc("b.pdf") }, RecentFiles.Items.Select(i => i.Path));
        RecentFiles.Clear();
        Assert.Empty(RecentFiles.Items);
        Assert.True(File.Exists(document));
    }

    [Fact]
    public void Another_file_name_gets_its_own_times_file_name()
    {
        using var storage = RecentFiles.UseStorage(Path.Combine(_folder, "other.json"), () => _now);
        RecentFiles.Add(Doc("a.pdf"));
        Assert.True(File.Exists(Path.Combine(_folder, "other-times.json")));
        Assert.False(File.Exists(Times));
    }

    [Fact]
    public void Items_keep_working_for_code_that_still_builds_recent_files_from_a_path_alone()
    {
        var list = new ObservableCollection<RecentFile> { new(Doc("a.pdf")) };
        Assert.Null(list[0].LastOpened);
        Assert.Equal("a.pdf", list[0].Name);
    }

}
