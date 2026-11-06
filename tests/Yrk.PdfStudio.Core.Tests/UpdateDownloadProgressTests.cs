using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class UpdateDownloadProgressTests
{
    [Fact]
    public void Stalled_after_thirty_seconds_but_not_twenty_nine()
    {
        var now = DateTimeOffset.UnixEpoch;
        var progress = new UpdateDownloadProgress(1_000);
        progress.Report(25, now);

        Assert.Equal(250L, progress.DownloadedBytes);
        Assert.False(progress.IsStalled(now.AddSeconds(29)));
        Assert.True(progress.IsStalled(now.AddSeconds(30)));
    }

    [Fact]
    public void Repeated_percentage_keeps_deadline_and_changed_percentage_resets_it()
    {
        var now = DateTimeOffset.UnixEpoch;
        var progress = new UpdateDownloadProgress(long.MaxValue);
        progress.Report(0, now);
        progress.Report(0, now.AddSeconds(29));
        Assert.True(progress.IsStalled(now.AddSeconds(30)));

        progress.Report(100, now.AddSeconds(30));
        Assert.Equal(long.MaxValue, progress.DownloadedBytes);
        Assert.False(progress.IsStalled(now.AddSeconds(59)));
        Assert.True(progress.IsStalled(now.AddSeconds(60)));
    }
}
