using Yrk.PdfStudio.App;
using System.Runtime.InteropServices;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppLogTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("yrk-app-logs-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void Cleanup_deletes_only_strictly_named_logs_older_than_fourteen_days()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0);
        var old = Path.Combine(_root.FullName, "errors-2026-10-21.log");
        var recent = Path.Combine(_root.FullName, "errors-2026-10-22.log");
        var invalid = Path.Combine(_root.FullName, "errors-2026-10-20-extra.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        File.WriteAllText(invalid, "invalid");

        global::Yrk.PdfStudio.App.App.CleanupOldLogs(_root.FullName, now);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(invalid));
    }

    [Fact]
    public void LogMessage_does_not_append_when_daily_file_is_at_the_limit()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0);
        var path = Path.Combine(_root.FullName, "errors-2026-11-05.log");
        var original = new string('x', 1_048_576);
        File.WriteAllText(path, original);

        global::Yrk.PdfStudio.App.App.LogMessage("must be skipped", _root.FullName, now);

        Assert.Equal(1_048_576, new FileInfo(path).Length);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LogMessage_appends_to_its_own_single_link_file(bool existing)
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0);
        var path = Path.Combine(_root.FullName, "errors-2026-11-05.log");
        if (existing) File.WriteAllText(path, "existing\n");

        global::Yrk.PdfStudio.App.App.LogMessage("ข้อความ log", _root.FullName, now);

        Assert.Equal((existing ? "existing\n" : "")
            + "[2026-11-05T12:00:00.0000000] ข้อความ log" + Environment.NewLine + Environment.NewLine,
            File.ReadAllText(path));
    }

    [Fact]
    public void LogMessage_preserves_the_size_limit_in_UTF8_bytes()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0);
        var path = Path.Combine(_root.FullName, "errors-2026-11-05.log");
        const string message = "ก";
        var content = "[2026-11-05T12:00:00.0000000] " + message + Environment.NewLine + Environment.NewLine;
        var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
        var original = new string('x', 1_048_576 - bytes + 1);
        File.WriteAllText(path, original);

        global::Yrk.PdfStudio.App.App.LogMessage(message, _root.FullName, now);
        Assert.Equal(original, File.ReadAllText(path));

        File.WriteAllText(path, original[1..]);
        global::Yrk.PdfStudio.App.App.LogMessage(message, _root.FullName, now);
        Assert.Equal(1_048_576, new FileInfo(path).Length);
        Assert.Equal(original[1..] + content, File.ReadAllText(path));
    }

    [FileSymbolicLinkFact]
    public void LogMessage_does_not_follow_a_daily_file_reparse_point()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0);
        var path = Path.Combine(_root.FullName, "errors-2026-11-05.log");
        var target = Path.Combine(_root.FullName, "private.txt");
        File.WriteAllText(target, "private original");
        File.CreateSymbolicLink(path, target);
        Assert.True((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);

        global::Yrk.PdfStudio.App.App.LogMessage("must not write through link", _root.FullName, now);

        Assert.Equal("private original", File.ReadAllText(target));
        File.Delete(path);
        File.Delete(target);
        File.CreateSymbolicLink(path, target);
        global::Yrk.PdfStudio.App.App.LogMessage("must not create link target", _root.FullName, now);
        Assert.False(File.Exists(target));
        File.Delete(path);
    }

    [Fact]
    public void LogMessage_rejects_daily_file_hard_links_and_accepts_it_after_alias_is_removed()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0);
        var path = Path.Combine(_root.FullName, "errors-2026-11-05.log");
        var target = Path.Combine(_root.FullName, "private.txt");
        File.WriteAllText(target, "private original");
        Assert.True(CreateHardLink(path, target, IntPtr.Zero), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");

        global::Yrk.PdfStudio.App.App.LogMessage("must not write through hard link", _root.FullName, now);

        Assert.Equal("private original", File.ReadAllText(target));
        Assert.Equal("private original", File.ReadAllText(path));
        File.Delete(target);
        global::Yrk.PdfStudio.App.App.LogMessage("single link again", _root.FullName, now);
        Assert.Contains("single link again", File.ReadAllText(path));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    public sealed class FileSymbolicLinkFactAttribute : FactAttribute
    {
        public FileSymbolicLinkFactAttribute()
        {
            var probe = Directory.CreateTempSubdirectory("yrk-log-symlink-probe-");
            try
            {
                File.CreateSymbolicLink(Path.Combine(probe.FullName, "link"), Path.Combine(probe.FullName, "target"));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xffff) == 1314)
            {
                Skip = "Creating file symlinks requires privileges unavailable to this test process.";
            }
            finally { probe.Delete(recursive: true); }
        }
    }
}
