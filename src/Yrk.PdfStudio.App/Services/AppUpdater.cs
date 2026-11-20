using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            : AssemblyVersion;

    public static string AssemblyVersion =>
        typeof(AppUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

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
public sealed class UpdateVerificationException(string message, Exception? innerException = null, bool deleteFailed = false)
    : Exception(message, innerException)
{
    public bool DeleteFailed { get; } = deleteFailed;
}

/// <summary>Small per-user preferences, kept in %LOCALAPPDATA%\YRK\EasyPdf\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly object SaveLock = new();
    private static volatile bool s_saveSuspended;
    internal static void SuspendSavingUntilRestart()
    {
        lock (SaveLock) s_saveSuspended = true;
    }

    // Tests must finish all pending saves before resetting process lifetime state.
    internal static void ResetSavingForTests()
    {
        lock (SaveLock) s_saveSuspended = false;
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "settings.json");
    private static readonly JsonObject Defaults = Snapshot(new AppSettings());
    private JsonObject? _baseline;

    public string ScanSaveFolder { get; set; } = string.Empty;
    public Dictionary<string, ScanPaperPreference> ScanPaperByPreset { get; set; } = [];
    public int ScanDpi { get; set; } = 300;
    public string ScanColorMode { get; set; } = "Color";
    public string ScanSource { get; set; } = "Flatbed";
    public int ScanJpegQuality { get; set; } = 80;
    public string ScanPreset { get; set; } = "ColorDocument";
    public bool ScanOcr { get; set; }
    public string ScanDeviceName { get; set; } = string.Empty;

    public bool CheckForUpdates { get; set; } = true;

    /// <summary>The user has answered the first-start question about automatic updates.</summary>
    public bool UpdateChoiceMade { get; set; }

    /// <summary>"auto" (follow Windows), "en" or "th".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>The unit for measuring and the rulers (mm, cm, m, in, ft, pt).</summary>
    public string MeasureUnit { get; set; } = "mm";

    /// <summary>Show the recent files panel on the start page. Off only hides it: the history is still kept (clear it in Privacy and safety).</summary>
    public bool ShowRecentOnHome { get; set; } = true;

    public static AppSettings Load(string? path = null)
    {
        lock (SaveLock)
        {
            Read(path ?? FilePath, out _, out var settings);
            settings._baseline = Snapshot(settings);
            return settings;
        }
    }

    /// <summary>Changes the latest settings and commits only changed fields under the process-wide lock.</summary>
    public static void Update(Action<AppSettings> change, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (SaveLock)
        {
            if (s_saveSuspended) return;
            path ??= FilePath;
            var state = Read(path, out var latest, out var settings);
            if (state == ReadState.Unreadable) return;
            var baseline = Snapshot(settings);
            change(settings);
            Commit(path, state, latest, baseline, Snapshot(settings));
        }
    }

    /// <summary>Merges changes since Load or the last successful Save into the latest file.</summary>
    public void Save(string? path = null)
    {
        lock (SaveLock)
        {
            if (s_saveSuspended) return;
            path ??= FilePath;
            var state = Read(path, out var latest, out _);
            if (state == ReadState.Unreadable) return;
            var current = Snapshot(this);
            if (Commit(path, state, latest, _baseline ?? Defaults, current))
                _baseline = current;
        }
    }

    private enum ReadState { Valid, Missing, Corrupt, Unreadable }

    private static ReadState Read(string path, out JsonObject? latest, out AppSettings settings)
    {
        latest = null;
        settings = new();
        try
        {
            // Do not use File.Exists: it also returns false for some access failures.
            latest = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (latest is null) return ReadState.Corrupt;
            settings = latest.Deserialize<AppSettings>()!;
            return ReadState.Valid;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ReadState.Missing;
        }
        catch (JsonException)
        {
            latest = null;
            return ReadState.Corrupt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ReadState.Unreadable;
        }
    }

    private static JsonObject Snapshot(AppSettings settings) => JsonSerializer.SerializeToNode(settings)!.AsObject();

    private static bool Commit(string path, ReadState state, JsonObject? latest, JsonObject baseline, JsonObject current)
    {
        if (s_saveSuspended) return false; // An Update callback can suspend saving while holding this lock.
        // Loading a damaged file or saving an unchanged copy must not replace it with defaults.
        if (state != ReadState.Missing && JsonNode.DeepEquals(baseline, current)) return true;
        latest ??= (JsonObject)Defaults.DeepClone();
        MergeChanges(baseline, current, latest);
        string? temporary = null;
        try
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (state == ReadState.Corrupt)
            {
                // Copy without overwrite. An existing backup (or any backup failure) blocks the save.
                File.Copy(path, path + ".bad", overwrite: false);
            }
            temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, latest);
                stream.Flush(flushToDisk: true);
            }
            if (state == ReadState.Missing) File.Move(temporary, path);
            else File.Replace(temporary, path, destinationBackupFileName: null);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // Only a convenience. Keep the baseline so a later Save can retry.
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static void MergeChanges(JsonObject baseline, JsonObject current, JsonObject latest)
    {
        foreach (var (key, value) in current)
        {
            var previous = baseline[key];
            if (JsonNode.DeepEquals(previous, value)) continue;
            if (previous is JsonObject previousObject && value is JsonObject currentObject)
            {
                // Merge dictionary entries too, preserving other presets and newer nested fields.
                var target = latest[key] as JsonObject;
                if (target is null) latest[key] = target = new JsonObject();
                MergeChanges(previousObject, currentObject, target);
            }
            else latest[key] = value?.DeepClone();
        }
        foreach (var (key, _) in baseline)
            if (!current.ContainsKey(key)) latest.Remove(key);
    }
}

/// <summary>Velopack's update manager, with the folder where it keeps downloaded packages.</summary>
internal sealed class YrkUpdateManager(IUpdateSource source, IVelopackLocator? locator = null)
    : UpdateManager(source, new UpdateOptions { MaximumDeltasBeforeFallback = 0 }, locator)
{
    public string PackagesDir => Locator.PackagesDir!;
}
