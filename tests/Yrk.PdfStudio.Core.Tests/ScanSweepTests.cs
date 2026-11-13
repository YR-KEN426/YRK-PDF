using System.Diagnostics;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanSweepTests : IDisposable
{
    private readonly string _temp = Directory.CreateDirectory(Path.Combine(PrivateTempDirectory.Path, "sweep-" + Guid.NewGuid().ToString("N"))).FullName;
    private string Root => Path.Combine(_temp, "YRK PDF", "scan");
    private readonly DateTime _now = DateTime.UtcNow;

    public void Dispose() => Directory.Delete(_temp, true);

    private void Age(string path)
    {
        Directory.SetCreationTimeUtc(path, _now.AddDays(-2));
        Directory.SetLastWriteTimeUtc(path, _now.AddDays(-2));
    }

    [Fact]
    public void Removes_only_old_first_level_entries_and_preserves_new_and_siblings()
    {
        var old = Directory.CreateDirectory(Path.Combine(Root, "old")).FullName;
        File.WriteAllText(Path.Combine(old, "page"), "old");
        Age(old);
        var fresh = Directory.CreateDirectory(Path.Combine(Root, "new")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(_temp, "YRK PDF", "scan-other")).FullName;
        File.WriteAllText(Path.Combine(sibling, "keep"), "keep");
        Age(sibling);
        Assert.Equal(1, ScanSession.SweepStale(_now, TimeSpan.FromDays(1), Root, _temp));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(sibling, "keep")));
    }

    [Fact]
    public void Missing_scan_directory_does_not_throw()
        => Assert.Equal(0, ScanSession.SweepStale(_now, TimeSpan.FromDays(1), Root, _temp));

    [Fact]
    public void Junction_is_removed_without_deleting_target_or_its_files()
    {
        Directory.CreateDirectory(Root);
        var target = Directory.CreateDirectory(Path.Combine(_temp, "target")).FullName;
        var file = Path.Combine(target, "keep.txt");
        File.WriteAllText(file, "keep");
        var junction = Path.Combine(Root, "junction");
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        // Advance the sweep clock so the link itself is old without touching its target's timestamps.
        Assert.Equal(1, ScanSession.SweepStale(_now.AddDays(3), TimeSpan.FromDays(1), Root, _temp));
        Assert.False(Directory.Exists(junction));
        Assert.True(Directory.Exists(target));
        Assert.Equal("keep", File.ReadAllText(file));
    }

    [Fact]
    public void Sibling_root_is_never_swept()
    {
        var sibling = Directory.CreateDirectory(Path.Combine(_temp, "YRK PDF", "scan-other")).FullName;
        var old = Directory.CreateDirectory(Path.Combine(sibling, "old")).FullName;
        Age(old);
        Assert.Equal(0, ScanSession.SweepStale(_now, TimeSpan.FromDays(1), sibling, _temp));
        Assert.True(Directory.Exists(old));
    }

    [Fact]
    public void Linked_scan_root_is_not_traversed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
        var target = Directory.CreateDirectory(Path.Combine(_temp, "root-target")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(target, "old")).FullName;
        File.WriteAllText(Path.Combine(child, "keep"), "keep");
        Age(child);
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Root}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(0, ScanSession.SweepStale(_now, TimeSpan.FromDays(1), Root, _temp));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(child, "keep")));
        Directory.Delete(Root); // Remove only the junction we just created.
    }

    [Fact]
    public void Either_recent_timestamp_preserves_an_entry_and_locked_entries_do_not_stop_sweep()
    {
        var createdRecently = Directory.CreateDirectory(Path.Combine(Root, "created-recently")).FullName;
        Directory.SetLastWriteTimeUtc(createdRecently, _now.AddDays(-2));
        var writtenRecently = Directory.CreateDirectory(Path.Combine(Root, "written-recently")).FullName;
        Directory.SetCreationTimeUtc(writtenRecently, _now.AddDays(-2));
        var locked = Directory.CreateDirectory(Path.Combine(Root, "locked")).FullName;
        var path = Path.Combine(locked, "locked.txt");
        using var held = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Age(locked);
        var removable = Directory.CreateDirectory(Path.Combine(Root, "removable")).FullName;
        Age(removable);
        Assert.Equal(1, ScanSession.SweepStale(_now, TimeSpan.FromDays(1), Root, _temp));
        Assert.True(Directory.Exists(createdRecently));
        Assert.True(Directory.Exists(writtenRecently));
        Assert.True(Directory.Exists(locked));
        Assert.False(Directory.Exists(removable));
    }
}
