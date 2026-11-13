using System.IO;
using System.Reflection;
using System.Text.Json;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Yrk.PdfStudio.App.Services;

/// <summary>
/// Updates through Velopack. The feed is a web folder (or GitHub releases, or a local folder for testing)
/// holding the files that build\release.ps1 produces. Updates only work in installed or portable copies,
/// not when running from Visual Studio, and only when the build has a feed URL. Only full packages are
/// downloaded, and their signatures are verified before promotion or extraction.
/// </summary>
public sealed class AppUpdater
{
    /// <summary>Overrides the built-in feed, for testing an update from a local folder.</summary>
    public const string FeedVariable = "YRK_PDFSTUDIO_UPDATE_URL";
    private const string PackageId = "YRK.EasyPdf";
    private readonly YrkUpdateManager? _manager;
    private readonly VerifiedUpdateSource? _source;
    private readonly TimeSpan _checkTimeout;
    private readonly TimeSpan _downloadIdleTimeout;
    private readonly bool _updatesDisabled;
    private sealed record DownloadPackage(long Size, string Version);
    private DownloadPackage? _downloadingPackage;

    public AppUpdater(TimeSpan? checkTimeout = null, TimeSpan? downloadIdleTimeout = null)
        : this(PackageIdentity.Current, checkTimeout, downloadIdleTimeout)
    {
    }

    internal AppUpdater(PackageIdentity identity, TimeSpan? checkTimeout = null, TimeSpan? downloadIdleTimeout = null)
    {
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(60);
        _downloadIdleTimeout = downloadIdleTimeout ?? TimeSpan.FromMinutes(5);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_checkTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_downloadIdleTimeout, TimeSpan.Zero);
        _updatesDisabled = !identity.CanUseVelopack;
        if (_updatesDisabled) return; // Even a feed override must never enable Velopack in a package.
        FeedUrl = Environment.GetEnvironmentVariable(FeedVariable) is { Length: > 0 } overridden
            ? overridden
            : typeof(AppUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "YrkUpdateUrl")?.Value;
        if (string.IsNullOrWhiteSpace(FeedUrl)) return;

        try
        {
            var web = VerifiedUpdateSource.IsWebFeed(FeedUrl);
            IUpdateSource inner = FeedUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
                ? new GithubSource(FeedUrl, null, false)
                : web
                    ? new SimpleWebSource(FeedUrl, null, 30)
                    : new SimpleFileSource(new DirectoryInfo(FeedUrl));
            _source = new VerifiedUpdateSource(inner, PackageId, PublicKey, FeedUrl);
            _manager = new YrkUpdateManager(_source);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UriFormatException)
        {
            _manager = null;
        }
    }

    internal AppUpdater(YrkUpdateManager manager, VerifiedUpdateSource source,
        TimeSpan? checkTimeout = null, TimeSpan? downloadIdleTimeout = null, PackageIdentity? identity = null)
    {
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(60);
        _downloadIdleTimeout = downloadIdleTimeout ?? TimeSpan.FromMinutes(5);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_checkTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_downloadIdleTimeout, TimeSpan.Zero);
        _updatesDisabled = !(identity ?? PackageIdentity.Current).CanUseVelopack;
        if (_updatesDisabled) return;
        _manager = manager;
        _source = source;
    }

    public string? FeedUrl { get; }
    /// <summary>True for installed and portable copies built with a feed.</summary>
    public bool IsAvailable => !_updatesDisabled && _manager is { IsInstalled: true };
    public bool IsPortable => !_updatesDisabled && _manager is { IsPortable: true };
    public string CurrentVersion =>
        _manager is { IsInstalled: true } manager && manager.CurrentVersion is { } version
            ? version.ToString()
            : typeof(AppUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>A downloaded update waiting for a restart.</summary>
    public UpdateInfo? Ready { get; private set; }

    /// <summary>Size of the active full package in bytes; zero when no download is active.</summary>
    public long DownloadingPackageSize => Volatile.Read(ref _downloadingPackage)?.Size ?? 0;
    /// <summary>Version of the active full package; null when no download is active.</summary>
    public string? DownloadingVersion => Volatile.Read(ref _downloadingPackage)?.Version;

    /// <summary>Checks for and downloads an update, keeping only a package signed by YRK.</summary>
    /// <exception cref="UpdateVerificationException">The update could not be authenticated.</exception>
    /// <param name="downloadStarting">Called synchronously before downloading, with full-package bytes and version.
    /// Like progress, this callback may run on a worker thread.</param>
    public async Task<UpdateInfo?> CheckAndDownloadAsync(Action<int>? progress = null, CancellationToken cancellationToken = default,
        Action<long, string>? downloadStarting = null)
    {
        if (_updatesDisabled || _manager is not { IsInstalled: true } manager) return null;
        Ready = null;
        var update = await manager.CheckForUpdatesAsync().WaitAsync(_checkTimeout, cancellationToken).ConfigureAwait(false);
        if (update is null) return null;

        using var download = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        download.CancelAfter(_downloadIdleTimeout);
        var progressLock = new object();
        var lastPercent = -1;
        var finished = false;
        var packageInfo = new DownloadPackage(update.TargetFullRelease.Size, update.TargetFullRelease.Version.ToString());
        void ReportProgress(int percent)
        {
            lock (progressLock)
            {
                if (finished || download.IsCancellationRequested) return;
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    download.CancelAfter(_downloadIdleTimeout);
                }
                progress?.Invoke(percent);
            }
        }
        try
        {
            Volatile.Write(ref _downloadingPackage, packageInfo);
            downloadStarting?.Invoke(packageInfo.Size, packageInfo.Version);
            await RemoveInvalidCachedPackageAsync(manager, update.TargetFullRelease, download.Token).ConfigureAwait(false);
            // Await cancellation fully: abandoning the task could let it write files after cleanup or retry.
            await manager.DownloadUpdatesAsync(update, ReportProgress, download.Token).ConfigureAwait(false);
            await VerifyAsync(manager, update.TargetFullRelease, download.Token).ConfigureAwait(false);
            download.Token.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (download.IsCancellationRequested)
        {
            var package = VerifiedUpdateSource.GetPackagePath(manager.PackagesDir, update.TargetFullRelease);
            // The source normally deletes rejected files; also cover a cached package and Velopack's partial.
            File.Delete(package + ".partial");
            File.Delete(package);
            if (cancellationToken.IsCancellationRequested) throw; // Keep the original exception; the caller cancelled.
            throw new TimeoutException("The download stopped. Check the internet connection and try again.", ex);
        }
        finally
        {
            lock (progressLock) finished = true;
            Interlocked.CompareExchange(ref _downloadingPackage, null, packageInfo);
        }
        Ready = update;
        return update;
    }

    /// <summary>Closes the app, installs the downloaded update and starts the new version.</summary>
    public async Task ApplyAndRestartAsync()
    {
        if (_updatesDisabled) return;
        if (_manager is not null && Ready is not null)
        {
            var update = Ready;
            Ready = null;
            // Rechecking immediately before apply reduces, but does not eliminate, the TOCTOU window.
            var package = VerifiedUpdateSource.GetPackagePath(_manager.PackagesDir, update.TargetFullRelease);
            await _source!.VerifyCachedAsync(update.TargetFullRelease, package, CancellationToken.None).ConfigureAwait(false);
            _manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
    }

    private static readonly Lazy<string?> UpdatePublicKey = new(ReadPublicKey);

    /// <summary>YRK's public update key, built in by build\release.ps1; null in development builds.</summary>
    internal static string? PublicKey => UpdatePublicKey.Value;

    private static string? ReadPublicKey()
    {
        using var stream = typeof(AppUpdater).Assembly.GetManifestResourceStream("Yrk.PdfStudio.App.update-public-key.pem");
        return stream is null ? null : new StreamReader(stream).ReadToEnd();
    }

    private Task VerifyAsync(YrkUpdateManager manager, VelopackAsset asset, CancellationToken cancellationToken)
    {
        var package = VerifiedUpdateSource.GetPackagePath(manager.PackagesDir, asset);
        return _source!.VerifyFileAsync(asset, package, cancellationToken);
    }

    private async Task RemoveInvalidCachedPackageAsync(YrkUpdateManager manager, VelopackAsset asset,
        CancellationToken cancellationToken)
    {
        var package = VerifiedUpdateSource.GetPackagePath(manager.PackagesDir, asset);
        if (!File.Exists(package)) return;
        try
        {
            await VerifyAsync(manager, asset, cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateVerificationException) when (!cancellationToken.IsCancellationRequested && !File.Exists(package))
        {
            // Verification deleted the stale complete package. Only a fresh verified download can replace it.
        }
    }
}

/// <summary>A downloaded update could not be authenticated and must not be installed.</summary>
public sealed class UpdateVerificationException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Small per-user preferences, kept in %LOCALAPPDATA%\YRK\EasyPdf\settings.json.</summary>
public sealed class AppSettings
{
    private static volatile bool s_saveSuspended;
    internal static void SuspendSavingUntilRestart() => s_saveSuspended = true;
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "settings.json");

    public int ScanDpi { get; set; } = 300;
    public string ScanColorMode { get; set; } = "Color";
    public string ScanSource { get; set; } = "Flatbed";
    public int ScanJpegQuality { get; set; } = 80;
    public bool ScanOcr { get; set; }
    public string ScanDeviceName { get; set; } = string.Empty;

    public bool CheckForUpdates { get; set; } = true;

    /// <summary>The user has answered the first-start question about automatic updates.</summary>
    public bool UpdateChoiceMade { get; set; }

    /// <summary>"auto" (follow Windows), "en" or "th".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>The unit for measuring and the rulers (mm, cm, m, in, ft, pt).</summary>
    public string MeasureUnit { get; set; } = "mm";

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(string? path = null)
    {
        if (s_saveSuspended) return;
        path ??= FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a convenience.
        }
    }
}

/// <summary>Velopack's update manager, with the folder where it keeps downloaded packages.</summary>
internal sealed class YrkUpdateManager(IUpdateSource source, IVelopackLocator? locator = null)
    : UpdateManager(source, new UpdateOptions { MaximumDeltasBeforeFallback = 0 }, locator)
{
    public string PackagesDir => Locator.PackagesDir!;
}
