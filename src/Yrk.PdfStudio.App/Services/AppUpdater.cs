using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Velopack;
using Velopack.Sources;
using Yrk.PdfStudio.Core.Security;

namespace Yrk.PdfStudio.App.Services;

/// <summary>
/// Updates through Velopack. The feed is a web folder (or GitHub releases, or a local folder for testing)
/// holding the files that build\release.ps1 produces. Updates only work in installed or portable copies,
/// not when running from Visual Studio, and only when the build has a feed URL.
/// </summary>
public sealed class AppUpdater
{
    /// <summary>Overrides the built-in feed, for testing an update from a local folder.</summary>
    public const string FeedVariable = "YRK_PDFSTUDIO_UPDATE_URL";

    private readonly YrkUpdateManager? _manager;

    public AppUpdater()
    {
        FeedUrl = Environment.GetEnvironmentVariable(FeedVariable) is { Length: > 0 } overridden
            ? overridden
            : typeof(AppUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "YrkUpdateUrl")?.Value;
        if (string.IsNullOrWhiteSpace(FeedUrl))
        {
            return;
        }

        try
        {
            _manager = FeedUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
                ? new YrkUpdateManager(new GithubSource(FeedUrl, null, false))
                : new YrkUpdateManager(FeedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? new SimpleWebSource(FeedUrl, null, 30)
                    : new SimpleFileSource(new DirectoryInfo(FeedUrl)));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UriFormatException)
        {
            _manager = null;
        }
    }

    public string? FeedUrl { get; }

    /// <summary>True for installed and portable copies built with a feed.</summary>
    public bool IsAvailable => _manager is { IsInstalled: true };

    public bool IsPortable => _manager is { IsPortable: true };

    public string CurrentVersion =>
        _manager is { IsInstalled: true } manager && manager.CurrentVersion is { } version
            ? version.ToString()
            : typeof(AppUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>A downloaded update waiting for a restart.</summary>
    public UpdateInfo? Ready { get; private set; }

    /// <summary>
    /// Checks the feed and downloads a newer version. Returns it, or null when up to date. The download is
    /// kept only when its signature matches YRK's public key built into this app.
    /// </summary>
    /// <exception cref="UpdateVerificationException">The update is not signed by YRK; it was deleted.</exception>
    public async Task<UpdateInfo?> CheckAndDownloadAsync(Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_manager is not { IsInstalled: true } manager)
        {
            return null;
        }

        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
        {
            return null;
        }

        await manager.DownloadUpdatesAsync(update, progress, cancellationToken).ConfigureAwait(false);
        await VerifyAsync(manager, update.TargetFullRelease, cancellationToken).ConfigureAwait(false);
        Ready = update;
        return update;
    }

    /// <summary>Closes the app, installs the downloaded update and starts the new version.</summary>
    public void ApplyAndRestart()
    {
        if (_manager is not null && Ready is not null)
        {
            _manager.ApplyUpdatesAndRestart(Ready.TargetFullRelease);
        }
    }

    // --- Signature check ---------------------------------------------------------------------

    private static readonly HttpClient SignatureClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>YRK's update key (public half), built in by build\release.ps1; null in development builds.</summary>
    internal static string? PublicKey { get; } = ReadPublicKey();

    private static string? ReadPublicKey()
    {
        using var stream = typeof(AppUpdater).Assembly.GetManifestResourceStream("Yrk.PdfStudio.App.update-public-key.pem");
        return stream is null ? null : new StreamReader(stream).ReadToEnd();
    }

    private async Task VerifyAsync(YrkUpdateManager manager, VelopackAsset asset, CancellationToken cancellationToken)
    {
        var package = Path.Combine(manager.PackagesDir, asset.FileName);
        var version = asset.Version.ToString();
        string? problem = null;
        if (PublicKey is null)
        {
            problem = "this copy has no update key";
        }
        else
        {
            // The content signature also matches a package rebuilt from a delta update; releases before 1.0.5
            // have only the file signature, which matches when the full package itself was downloaded.
            var contentSignature = await TryReadSignatureAsync(UpdateSignature.ContentFileName(asset.PackageId, version), version, cancellationToken).ConfigureAwait(false);
            var fileSignature = contentSignature is null
                ? await TryReadSignatureAsync(UpdateSignature.FileName(asset.PackageId, version), version, cancellationToken).ConfigureAwait(false)
                : null;
            if (contentSignature is null && fileSignature is null)
            {
                problem = "no signature was found";
            }
            else
            {
                var valid = await Task.Run(() =>
                {
                    try
                    {
                        return contentSignature is not null
                            ? UpdateSignature.VerifyContent(PublicKey, asset.PackageId, version, UpdateSignature.ContentDigest(package), contentSignature)
                            : UpdateSignature.Verify(PublicKey, asset.PackageId, version, asset.FileName, UpdateSignature.Sha256Hex(package), fileSignature!);
                    }
                    catch (InvalidDataException)
                    {
                        return false; // Not a readable package.
                    }
                }, cancellationToken).ConfigureAwait(false);
                if (!valid)
                {
                    problem = "the signature does not match";
                }
            }
        }

        if (problem is not null)
        {
            // Never leave an unverified package where the updater could pick it up.
            try
            {
                File.Delete(package);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.LogMessage("Could not delete an unverified update: " + ex.Message);
            }

            throw new UpdateVerificationException($"Update {version} was not installed: {problem}.");
        }
    }

    /// <summary>A signature published with the release, or null when there is none (or it can't be read).</summary>
    private async Task<string?> TryReadSignatureAsync(string name, string version, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadSignatureAsync(name, version, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            App.LogMessage($"Update signature {name} not read: {ex.Message}");
            return null;
        }
    }

    /// <summary>The signature published next to the release: GitHub release asset, web folder, or local folder.</summary>
    private async Task<string> ReadSignatureAsync(string name, string version, CancellationToken cancellationToken)
    {
        var feed = FeedUrl!.TrimEnd('/', '\\');
        if (feed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return await SignatureClient.GetStringAsync($"{feed}/releases/download/v{version}/{name}", cancellationToken).ConfigureAwait(false);
        }

        if (feed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || feed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return await SignatureClient.GetStringAsync($"{feed}/{name}", cancellationToken).ConfigureAwait(false);
        }

        return await File.ReadAllTextAsync(Path.Combine(feed, name), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A downloaded update was not signed with YRK's key, so it was thrown away.</summary>
public sealed class UpdateVerificationException(string message) : Exception(message);

/// <summary>Small per-user preferences, kept in %LOCALAPPDATA%\YRK\EasyPdf\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "settings.json");

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

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a convenience.
        }
    }
}

/// <summary>Velopack's update manager, with the folder where it keeps downloaded packages.</summary>
internal sealed class YrkUpdateManager(IUpdateSource source) : UpdateManager(source)
{
    public string PackagesDir => Locator.PackagesDir!;
}
