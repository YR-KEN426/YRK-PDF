using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>
/// What the start page shows as recent files. A file that cannot be found is hidden only; the history keeps it. Every path
/// and name is made up, and files that must exist are made in a folder of this test.
/// </summary>
[Collection("Reader bookmarks")] // RecentFiles.Clear also clears the reading bookmarks file.
public sealed class HomeRecentListTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(PrivateTempDirectory.Path, "home-recent-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _documents = Directory.CreateDirectory(PrivateTempDirectory.Path + "-documents-" + Guid.NewGuid().ToString("N")).FullName;
    private readonly string _previousBookmarks = ReaderBookmarks.FilePath;
    private readonly string _previousPositions = ReadingPositions.FilePath;
    private readonly IDisposable _storage;

    public HomeRecentListTests()
    {
        ReaderBookmarks.FilePath = Path.Combine(_folder, "reader-bookmarks.json");
        ReadingPositions.FilePath = Path.Combine(_folder, "reading-positions.json");
        _storage = RecentFiles.UseStorage(Path.Combine(_folder, "recent.json"));
    }

    public void Dispose()
    {
        _storage.Dispose();
        ReaderBookmarks.FilePath = _previousBookmarks;
        ReadingPositions.FilePath = _previousPositions;
    }

    private string Make(string name)
    {
        var path = Path.Combine(_documents, name);
        File.WriteAllText(path, "x");
        RecentFiles.Add(path);
        return path;
    }

    private static string[] Names(HomeRecentList list) => list.Rows.Select(r => r.Name).ToArray();

    [Fact]
    public async Task A_missing_file_is_hidden_but_stays_in_the_history()
    {
        var kept = Make("kept.pdf");
        var gone = Make("gone.pdf");
        File.Delete(gone);
        using var list = new HomeRecentList();
        await list.RefreshAsync();
        Assert.Equal(new[] { "kept.pdf" }, Names(list));
        Assert.Contains(RecentFiles.Items, i => i.Path == gone); // Not removed.
        Assert.Contains(RecentFiles.Items, i => i.Path == kept);

        File.WriteAllText(gone, "back again"); // A drive plugged in again.
        await list.RefreshAsync();
        Assert.Equal(new[] { "gone.pdf", "kept.pdf" }, Names(list));
    }

    [Fact]
    public async Task With_every_file_missing_there_is_nothing_to_show()
    {
        File.Delete(Make("a.pdf"));
        File.Delete(Make("b.pdf"));
        using var list = new HomeRecentList();
        await list.RefreshAsync();
        Assert.Empty(list.Rows);
        Assert.False(list.HasRows);
        Assert.Equal(2, RecentFiles.Items.Count);
    }

    [Fact]
    public async Task The_files_are_looked_for_off_the_calling_thread()
    {
        Make("a.pdf");
        var callers = new List<int>();
        using var list = new HomeRecentList(path =>
        {
            lock (callers) callers.Add(Environment.CurrentManagedThreadId);
            return true;
        });
        await list.RefreshAsync();
        Assert.NotEmpty(callers);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, callers);
    }

    [Fact]
    public async Task Six_rows_show_first_and_view_all_shows_twelve()
    {
        for (var i = 0; i < 12; i++) Make($"file-{i:00}.pdf");
        using var list = new HomeRecentList();
        await list.RefreshAsync();
        Assert.Equal(6, list.Rows.Count);
        Assert.Equal("file-11.pdf", list.Rows[0].Name); // Newest first.
        Assert.True(list.CanExpand);
        Assert.Equal("View all", list.ToggleText);
        list.ToggleExpanded();
        Assert.Equal(12, list.Rows.Count);
        Assert.Equal("Show fewer", list.ToggleText);
        list.ToggleExpanded();
        Assert.Equal(6, list.Rows.Count);
    }

    [Fact]
    public async Task Six_files_or_fewer_offer_no_view_all()
    {
        for (var i = 0; i < 6; i++) Make($"file-{i}.pdf");
        using var list = new HomeRecentList();
        await list.RefreshAsync();
        Assert.Equal(6, list.Rows.Count);
        Assert.False(list.CanExpand);
    }

    [Fact]
    public async Task Turning_the_option_off_hides_the_panel_and_keeps_the_history()
    {
        Make("a.pdf");
        using var list = new HomeRecentList();
        await list.RefreshAsync();
        Assert.True(list.HasRows);
        list.Enabled = false;
        await list.RefreshAsync();
        Assert.False(list.HasRows);
        Assert.Empty(list.Rows);
        Assert.Single(RecentFiles.Items);
        list.Enabled = true;
        await list.RefreshAsync();
        Assert.True(list.HasRows);
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("remove")]
    [InlineData("off")]
    public async Task A_late_answer_never_brings_back_a_row_that_was_cleared_removed_or_switched_off(string change)
    {
        var path = Make("a.pdf");
        Make("b.pdf");
        using var gate = new ManualResetEventSlim();
        using var asked = new ManualResetEventSlim();
        var slow = false;
        using var list = new HomeRecentList(_ =>
        {
            if (!slow) return true;
            asked.Set();
            gate.Wait(TimeSpan.FromSeconds(20)); // The answer is held back.
            return true;
        });
        await list.RefreshAsync();
        Assert.Equal(2, list.Rows.Count);

        slow = true;
        var pending = list.RefreshAsync();
        Assert.True(asked.Wait(TimeSpan.FromSeconds(10)));
        switch (change)
        {
            case "clear": RecentFiles.Clear(); break;
            case "remove": RecentFiles.Remove(path); break;
            default: list.Enabled = false; break;
        }

        slow = false;
        gate.Set(); // The late answer arrives now.
        await pending;
        await list.RefreshAsync();
        switch (change)
        {
            case "clear":
                Assert.Empty(list.Rows);
                Assert.False(list.HasRows);
                break;
            case "remove":
                Assert.Equal(new[] { "b.pdf" }, Names(list));
                break;
            default:
                Assert.Empty(list.Rows);
                Assert.False(list.HasRows);
                Assert.Equal(2, RecentFiles.Items.Count);
                break;
        }
    }

    [Fact]
    public async Task A_removed_file_leaves_the_panel_at_once_without_waiting_for_a_new_look()
    {
        var path = Make("a.pdf");
        Make("b.pdf");
        using var list = new HomeRecentList();
        await list.RefreshAsync();
        RecentFiles.Remove(path);
        Assert.Equal(new[] { "b.pdf" }, Names(list)); // Checked before any await.
    }

    [Fact]
    public async Task Rows_carry_the_name_badge_kind_and_time_of_each_file()
    {
        File.WriteAllText(Path.Combine(_documents, "plan.dwg"), "x");
        RecentFiles.Add(Path.Combine(_documents, "plan.dwg"));
        Make("invoice.pdf");
        File.WriteAllText(Path.Combine(_documents, "logo.ai"), "x");
        RecentFiles.Add(Path.Combine(_documents, "logo.ai"));
        using var list = new HomeRecentList();
        await list.RefreshAsync();
        var rows = list.Rows.ToDictionary(r => r.Name);
        Assert.Equal(("DWG", RecentKind.Cad), (rows["plan.dwg"].Badge, rows["plan.dwg"].Kind));
        Assert.Equal(("PDF", RecentKind.Pdf), (rows["invoice.pdf"].Badge, rows["invoice.pdf"].Kind));
        Assert.Equal(("AI", RecentKind.Other), (rows["logo.ai"].Badge, rows["logo.ai"].Kind));
        Assert.All(list.Rows, r => Assert.Equal(Path.Combine(_documents, r.Name), r.Path));
        Assert.All(list.Rows, r => Assert.True(r.HasTime));
    }

    [Theory]
    [InlineData(@"\\server\share\folder\a.pdf", true)]
    [InlineData(@"\\?\UNC\server\share\a.pdf", true)]
    [InlineData(@"\\?\C:\Example\a.pdf", false)]
    [InlineData(@"C:\Example\a.pdf", false)]
    [InlineData(@"relative\a.pdf", false)]
    [InlineData("", false)]
    public void Network_paths_are_recognised_without_touching_the_network(string path, bool network) =>
        Assert.Equal(network, HomeRecentList.IsNetworkPath(path));

    [Fact]
    public void A_network_path_is_shown_without_being_looked_for_and_a_local_one_is_looked_for()
    {
        // This share does not exist; looking for it would try the network. It is simply taken as there.
        Assert.True(HomeRecentList.FileIsThere(@"\\yrk-example-server.invalid\share\missing.pdf"));
        Assert.False(HomeRecentList.FileIsThere(Path.Combine(_documents, "not-there.pdf")));
        Assert.True(HomeRecentList.FileIsThere(Make("here.pdf")));
    }

}
