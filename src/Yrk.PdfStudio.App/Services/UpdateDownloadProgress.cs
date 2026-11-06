namespace Yrk.PdfStudio.App.Services;

/// <summary>Tracks percentage changes using caller-supplied time, without a timer.</summary>
public sealed class UpdateDownloadProgress(long totalBytes = 0)
{
    private DateTimeOffset? _lastChanged;

    public int Percent { get; private set; }
    public long DownloadedBytes => (long)((decimal)Percent * totalBytes / 100);

    public void Report(int percent, DateTimeOffset now)
    {
        if (_lastChanged is null || percent != Percent)
        {
            Percent = percent;
            _lastChanged = now;
        }
    }

    public bool IsStalled(DateTimeOffset now) =>
        _lastChanged is { } changed && now - changed >= TimeSpan.FromSeconds(30);
}
