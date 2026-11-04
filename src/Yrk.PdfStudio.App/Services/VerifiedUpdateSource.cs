using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;
using Yrk.PdfStudio.Core.Security;

namespace Yrk.PdfStudio.App.Services;

/// <summary>Authenticates the entire download before Velopack can promote or unpack it.</summary>
internal sealed class VerifiedUpdateSource : IUpdateSource
{
    internal const int MaximumSignatureBytes = 4096;
    // Internal diagnostics are logged; the update UI displays its existing translated error.
    private const string NoUpdateKey = "This copy has no update key.";
    private const string OversizedSignature = "Update signature exceeds 4 KB.";
    private const string SignatureMismatch = "The full-file signature does not match.";
    private const string InvalidPackageName = "Invalid update file name.";
    private const string OutsidePackages = "Update package is outside PackagesDir.";
    private const string InvalidPackagePath = "Invalid update package path.";
    private const string InvalidAsset = "Invalid update package identity or file name.";
    private const string DeleteFailed = "Update rejected; could not delete the unverified file.";
    private const string VerificationFailed = "Update rejected by full-file signature verification.";
    private const string InvalidSignatureName = "Invalid signature file name.";
    private const string InsecureRedirect = "Signature redirect must use HTTPS.";
    private const string TooManyRedirects = "Too many signature redirects.";

    private static readonly HttpClient SignatureClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly IUpdateSource _inner;
    private readonly string _packageId;
    private readonly string? _publicKey;
    private readonly Func<string, string, CancellationToken, Task<string>> _readSignature;
    private readonly ConcurrentDictionary<(string PackageId, string Version, string FileName), string> _verifiedSignatures = new();

    internal VerifiedUpdateSource(IUpdateSource inner, string packageId, string? publicKey, string feedBase)
        : this(inner, packageId, publicKey, CreateSignatureReader(feedBase)) { }

    internal VerifiedUpdateSource(IUpdateSource inner, string packageId, string? publicKey,
        Func<string, string, CancellationToken, Task<string>> readSignature)
    {
        if (!IsPlainFileName(packageId))
            throw new ArgumentException(message: "Invalid update package id.", nameof(packageId));
        _inner = inner;
        _packageId = packageId;
        _publicKey = publicKey;
        _readSignature = readSignature;
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var feed = await _inner.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease).ConfigureAwait(false);
        return feed with { Assets = feed.Assets.Where(IsAllowedAsset).ToArray() };
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken)
    {
        try
        {
            RequireAllowedAsset(releaseEntry);
            await _inner.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken).ConfigureAwait(false);
            await VerifyFileAsync(releaseEntry, localFile, cancelToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw DeleteRejectedFile(localFile, ex);
        }
    }

    internal async Task VerifyFileAsync(VelopackAsset asset, string localFile, CancellationToken cancellationToken)
    {
        try
        {
            RequireAllowedAsset(asset);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(_publicKey))
                throw new InvalidDataException(message: NoUpdateKey);
            var version = asset.Version.ToString();
            var name = UpdateSignature.FileName(asset.PackageId, version);
            var signature = await _readSignature(name, version, cancellationToken).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(signature) > MaximumSignatureBytes)
                throw new InvalidDataException(message: OversizedSignature);
            await VerifySignatureAsync(asset, localFile, signature, cancellationToken).ConfigureAwait(false);
            _verifiedSignatures[(asset.PackageId, version, asset.FileName)] = signature;
        }
        catch (Exception ex)
        {
            throw DeleteRejectedFile(localFile, ex);
        }
    }

    /// <summary>Rechecks local bytes using only a signature already authenticated by this source.</summary>
    internal async Task VerifyCachedAsync(VelopackAsset asset, string localFile, CancellationToken cancellationToken)
    {
        try
        {
            RequireAllowedAsset(asset);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(_publicKey))
                throw new InvalidDataException(message: NoUpdateKey);
            if (!_verifiedSignatures.TryGetValue((asset.PackageId, asset.Version.ToString(), asset.FileName), out var signature))
                throw new InvalidDataException(message: VerificationFailed);
            await VerifySignatureAsync(asset, localFile, signature, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw DeleteRejectedFile(localFile, ex);
        }
    }

    private async Task VerifySignatureAsync(VelopackAsset asset, string localFile, string signature,
        CancellationToken cancellationToken)
    {
        var valid = await Task.Run(() => UpdateSignature.Verify(_publicKey!, asset.PackageId, asset.Version.ToString(),
            asset.FileName, UpdateSignature.Sha256Hex(localFile), signature), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!valid)
            throw new InvalidDataException(message: SignatureMismatch);
    }

    internal static bool IsPlainFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Path.GetFileName(name) == name &&
        !Path.IsPathRooted(name) && !name.Contains("..", StringComparison.Ordinal) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');

    internal static string GetPackagePath(string packagesDir, VelopackAsset asset)
    {
        try
        {
            if (!IsPlainFileName(asset.FileName))
                throw new InvalidDataException(message: InvalidPackageName);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packagesDir)) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, asset.FileName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(message: OutsidePackages);
            return path;
        }
        catch (Exception ex)
        {
            // There is no safe file to delete when the path itself is invalid.
            throw new UpdateVerificationException(message: InvalidPackagePath, ex);
        }
    }

    private bool IsAllowedAsset(VelopackAsset asset) =>
        asset.PackageId == _packageId && IsPlainFileName(asset.FileName) && asset.Version is not null &&
        IsPlainFileName(UpdateSignature.FileName(asset.PackageId, asset.Version.ToString()));

    private void RequireAllowedAsset(VelopackAsset asset)
    {
        if (!IsAllowedAsset(asset))
            throw new InvalidDataException(message: InvalidAsset);
    }

    private static UpdateVerificationException DeleteRejectedFile(string path, Exception problem)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception deletionError)
        {
            return new UpdateVerificationException(message: DeleteFailed,
                new AggregateException(problem, deletionError));
        }
        return new UpdateVerificationException(message: VerificationFailed, problem);
    }

    internal static bool IsWebFeed(string feedBase)
    {
        if (!Uri.TryCreate(feedBase, UriKind.Absolute, out var uri) || uri.IsFile)
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException(message: "Web update feeds must use HTTPS.", nameof(feedBase));
        return true;
    }

    private static Func<string, string, CancellationToken, Task<string>> CreateSignatureReader(string feedBase)
    {
        var web = IsWebFeed(feedBase);
        var feed = feedBase.TrimEnd('/', '\\');
        return async (name, version, cancellationToken) =>
        {
            if (!IsPlainFileName(name))
                throw new InvalidDataException(message: InvalidSignatureName);
            if (!web)
            {
                using var file = File.OpenRead(Path.Combine(feed, name));
                return await ReadBoundedSignatureAsync(file, cancellationToken).ConfigureAwait(false);
            }

            var url = new Uri(feed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
                ? $"{feed}/releases/download/v{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(name)}"
                : $"{feed}/{Uri.EscapeDataString(name)}");
            // GitHub assets redirect to a CDN. Follow only HTTPS redirects, including every hop.
            for (var redirects = 0; redirects <= 5; redirects++)
            {
                using var response = await SignatureClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    url = new Uri(url, location);
                    if (url.Scheme != Uri.UriSchemeHttps)
                        throw new InvalidDataException(message: InsecureRedirect);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaximumSignatureBytes)
                    throw new InvalidDataException(message: OversizedSignature);
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await ReadBoundedSignatureAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            throw new HttpRequestException(message: TooManyRedirects);
        };
    }

    private static async Task<string> ReadBoundedSignatureAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek && stream.Length > MaximumSignatureBytes)
            throw new InvalidDataException(message: OversizedSignature);
        var buffer = new byte[MaximumSignatureBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return Encoding.UTF8.GetString(buffer, 0, count).TrimStart('\uFEFF');
            count += read;
        }
        throw new InvalidDataException(message: OversizedSignature);
    }
}
