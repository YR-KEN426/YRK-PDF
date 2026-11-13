using System.Diagnostics;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanSessionTests
{
    [Fact]
    public void Stores_pixels_and_edits_reorders_deletes_then_disposes_all_files()
    {
        var session = new ScanSession();
        var folder = session.DirectoryPath;
        Assert.StartsWith(Path.GetFullPath(PrivateTempDirectory.Path) + Path.DirectorySeparatorChar, folder, StringComparison.OrdinalIgnoreCase);
        var first = session.Add(Page(4, 6));
        var second = session.Add(Page(8, 10));
        first.Rotation = 90;
        first.Crop = new(0, 0, 0.5, 1);
        first.IsBlank = true;
        session.Move(0, 1);
        Assert.Same(first, session.Pages[1]);
        Assert.Equal(90, session.Pages[1].Rotation);
        Assert.Equal(new NormalizedRect(0, 0, 0.5, 1), session.Pages[1].Crop);
        Assert.True(session.Pages[1].IsBlank);
        Assert.Equal(4, session.Read(first).Bitmap.Width);
        Assert.Equal(Page(4, 6).Bitmap.Pixels, session.Read(first).Bitmap.Pixels);
        session.RemoveAt(0);
        Assert.False(File.Exists(second.FilePath));
        session.Dispose();
        session.Dispose();
        Assert.False(Directory.Exists(folder));
        Assert.Throws<ObjectDisposedException>(() => session.Add(Page(1, 1)));
    }

    [Theory]
    [InlineData(ScannerErrorKind.NoDevice)]
    [InlineData(ScannerErrorKind.DeviceBusy)]
    [InlineData(ScannerErrorKind.PaperJam)]
    [InlineData(ScannerErrorKind.FeederEmpty)]
    [InlineData(ScannerErrorKind.Cancelled)]
    [InlineData(ScannerErrorKind.Unavailable)]
    [InlineData(ScannerErrorKind.Other)]
    public async Task Every_scanner_error_cleans_up_previously_scanned_pages(ScannerErrorKind kind)
    {
        using var session = new ScanSession();
        var scanner = new FakeScannerService { SimulatedError = kind, ErrorAfterPages = 1 };
        var error = await Assert.ThrowsAsync<ScannerException>(() => session.ScanAsync(scanner, FakeScannerService.Device, new(Dpi: 100)));
        Assert.Equal(kind, error.Kind);
        Assert.False(Directory.Exists(session.DirectoryPath));
    }

    [Fact]
    public async Task Cancellation_after_one_page_removes_the_whole_session()
    {
        using var session = new ScanSession();
        using var cancellation = new CancellationTokenSource();
        var observer = new Sink<ScanSessionPage>(_ => cancellation.Cancel());
        var error = await Assert.ThrowsAsync<ScannerException>(() => session.ScanAsync(new FakeScannerService(), FakeScannerService.Device,
            new(Dpi: 100), observer, cancellation.Token));
        Assert.Equal(ScannerErrorKind.Cancelled, error.Kind);
        Assert.False(Directory.Exists(session.DirectoryPath));
    }

    [Fact]
    public async Task Scanning_without_a_progress_observer_still_stores_each_page()
    {
        using var session = new ScanSession();
        await session.ScanAsync(new FakeScannerService(), FakeScannerService.Device, new(Dpi: 100));
        Assert.Equal(3, session.Pages.Count);
        Assert.All(session.Pages, page => Assert.True(File.Exists(page.FilePath)));
    }

    [Fact]
    public void Exception_while_adding_a_page_preserves_existing_pages_and_allows_retry()
    {
        using var session = new ScanSession();
        var first = session.Add(Page(4, 6));
        Assert.Throws<ArgumentException>(() => session.Add(new(new(2, 2, 8, []), 300, 300)));
        Assert.Same(first, Assert.Single(session.Pages));
        Assert.Equal(Page(4, 6).Bitmap.Pixels, session.Read(first).Bitmap.Pixels);
        session.Add(Page(8, 10));
        Assert.Equal(2, session.Pages.Count);
    }

    [Fact]
    public void Failure_after_writing_a_page_rolls_back_only_that_file()
    {
        using var session = new ScanSession();
        var first = session.Add(Page(4, 6));
        var files = Directory.GetFiles(session.DirectoryPath).Order().ToArray();
        var page = Page(8, 10) with { Notices = new FailingNotices() };
        Assert.Throws<IOException>(() => session.Add(page));
        Assert.Equal(files, Directory.GetFiles(session.DirectoryPath).Order().ToArray());
        Assert.Same(first, Assert.Single(session.Pages));
        Assert.Equal(4, session.Read(first).Bitmap.Width);
        session.Add(Page(8, 10));
        Assert.Equal(2, session.Pages.Count);
    }

    [Fact]
    public void Cancelled_add_keeps_existing_pages()
    {
        using var session = new ScanSession();
        var first = session.Add(Page(4, 6));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.Add(Page(8, 10), cancellation.Token));
        Assert.Same(first, Assert.Single(session.Pages));
        Assert.Equal(4, session.Read(first).Bitmap.Width);
    }

    [Fact]
    public void Deletion_guard_rejects_outside_traversal_and_sibling_prefix()
    {
        using var session = new ScanSession();
        var outside = Directory.CreateTempSubdirectory("scan-outside-");
        try
        {
            var sentinel = Path.Combine(outside.FullName, "keep.txt");
            File.WriteAllText(sentinel, "keep");
            Assert.Throws<ArgumentException>(() => session.DeleteOwnedPath(sentinel));
            Assert.Throws<ArgumentException>(() => session.DeleteOwnedPath(Path.Combine(session.DirectoryPath, "..")));
            Assert.Throws<ArgumentException>(() => session.DeleteOwnedPath(session.DirectoryPath + "-sibling"));
            session.Dispose();
            Assert.Equal("keep", File.ReadAllText(sentinel));
        }
        finally { outside.Delete(true); }
    }

    [Fact]
    public void Dispose_removes_a_junction_without_following_it_and_rejects_paths_through_it()
    {
        using var session = new ScanSession();
        var outside = Directory.CreateTempSubdirectory("scan-outside-");
        try
        {
            var sentinel = Path.Combine(outside.FullName, "keep.txt");
            File.WriteAllText(sentinel, "keep");
            var junction = Path.Combine(session.DirectoryPath, "junction");
            MakeJunction(junction, outside.FullName);
            Assert.Throws<IOException>(() => session.DeleteOwnedPath(Path.Combine(junction, "keep.txt")));
            session.Dispose();
            Assert.False(Directory.Exists(session.DirectoryPath));
            Assert.Equal("keep", File.ReadAllText(sentinel));
        }
        finally { outside.Delete(true); }
    }

    [Fact]
    public void Replacing_session_root_with_a_junction_never_deletes_its_target()
    {
        using var session = new ScanSession();
        var outside = Directory.CreateTempSubdirectory("scan-outside-");
        try
        {
            File.WriteAllText(Path.Combine(outside.FullName, "keep.txt"), "keep");
            // Simulate replacement after an external actor has bypassed the live-session lock.
            ((IDisposable)typeof(ScanSession).GetField("_sessionLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(session)!).Dispose();
            File.Delete(Path.Combine(session.DirectoryPath, ".lock"));
            Directory.Delete(session.DirectoryPath); // This exact empty, session-created folder is owned here.
            MakeJunction(session.DirectoryPath, outside.FullName);
            session.Dispose();
            Assert.True(File.Exists(Path.Combine(outside.FullName, "keep.txt")));
            Assert.False(Directory.Exists(session.DirectoryPath));
        }
        finally { outside.Delete(true); }
    }

    internal static ScannedPage Page(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        return new(new(width, height, width * 4, pixels), 300, 300);
    }

    private static void MakeJunction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
    }

    internal sealed class Sink<T>(Action<T> receive) : IProgress<T>
    {
        public void Report(T value) => receive(value);
    }

    private sealed class FailingNotices : IReadOnlyList<string>
    {
        public int Count => 1;
        public string this[int index] => throw new IOException("Simulated failure after page bytes were written.");
        public IEnumerator<string> GetEnumerator() { yield return this[0]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
