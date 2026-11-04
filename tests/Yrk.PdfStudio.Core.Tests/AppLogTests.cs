using Yrk.PdfStudio.App;

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
}
