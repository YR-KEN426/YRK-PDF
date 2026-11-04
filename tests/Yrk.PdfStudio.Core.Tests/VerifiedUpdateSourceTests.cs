using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Security;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class VerifiedUpdateSourceTests
{
    private const string Id = "YRK.EasyPdf";
    private const string Version = "1.1.0";
    private const string Name = Id + "-" + Version + "-full.nupkg";
    private static readonly IVelopackLogger Log = new NullVelopackLogger();

    [Fact]
    public async Task Real_manager_promotes_and_extracts_only_a_verified_full_package()
    {
        using var fixture = new UpdateFixture();
        var updater = fixture.CreateUpdater();
        var update = await updater.CheckAndDownloadAsync();

        Assert.NotNull(update);
        Assert.Same(update, updater.Ready);
        Assert.Empty(update.DeltasToTarget);
        Assert.True(File.Exists(fixture.Complete));
        Assert.False(File.Exists(fixture.Complete + ".partial"));
        Assert.Equal("verified updater", File.ReadAllText(fixture.UpdateExe));
        Assert.Equal(1, fixture.Inner.Downloads);
        Assert.True(UpdateSignature.Verify(fixture.PublicKey, Id, Version, Name,
            UpdateSignature.Sha256Hex(fixture.Complete), File.ReadAllText(fixture.Signature)));
    }

    [Theory]
    [InlineData("outside-lib")]
    [InlineData("squirrel")]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("oversize")]
    [InlineData("no-key")]
    [InlineData("io")]
    [InlineData("cancel")]
    [InlineData("cancel-during")]
    public async Task Real_manager_never_promotes_or_extracts_a_rejected_download(string failure)
    {
        using var fixture = new UpdateFixture();
        var originalContentDigest = UpdateSignature.ContentDigest(fixture.Package);
        if (failure == "outside-lib") fixture.ChangeEntry("outside-lib.txt", "untrusted bytes");
        if (failure == "squirrel") fixture.ChangeEntry("tools/Squirrel.exe", "untrusted updater");
        if (failure == "missing") File.Delete(fixture.Signature);
        if (failure == "version") fixture.Sign("1.2.0");
        if (failure == "oversize") File.AppendAllText(fixture.Signature, new string(' ', 4096));
        if (failure is "outside-lib" or "squirrel")
            Assert.Equal(originalContentDigest, UpdateSignature.ContentDigest(fixture.Package));
        if (failure == "missing")
            fixture.WriteContentSignature(); // A valid legacy content signature must not be a fallback.
        // The feed checksum matches even the tampered package, as it would on a compromised host.
        fixture.WriteFeed();
        using var cancellation = new CancellationTokenSource();
        if (failure == "cancel") fixture.Inner.AfterDownload = () =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };
        if (failure == "cancel-during") fixture.Inner.InterruptDownload = async localFile =>
        {
            await File.WriteAllBytesAsync(localFile, [1, 2, 3]);
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };
        var source = failure == "io"
            ? new VerifiedUpdateSource(fixture.Inner, Id, fixture.PublicKey, (_, _, _) => throw new IOException("test IO failure"))
            : fixture.CreateSource(failure == "no-key" ? null : fixture.PublicKey);
        var updater = fixture.CreateUpdater(source);

        await Assert.ThrowsAsync<UpdateVerificationException>(() => updater.CheckAndDownloadAsync(cancellationToken: cancellation.Token));

        Assert.Null(updater.Ready);
        Assert.False(File.Exists(fixture.Complete));
        Assert.False(File.Exists(fixture.Complete + ".partial"));
        Assert.Equal("original updater", File.ReadAllText(fixture.UpdateExe));
        Assert.Equal(1, fixture.Inner.Downloads);
    }

    [Fact]
    public async Task Unverified_cached_complete_is_deleted_before_the_real_manager_downloads()
    {
        using var fixture = new UpdateFixture();
        File.WriteAllText(fixture.Complete, "unverified cached package");
        fixture.Inner.BeforeDownload = () =>
        {
            Assert.False(File.Exists(fixture.Complete));
            Assert.Equal("original updater", File.ReadAllText(fixture.UpdateExe));
        };

        await fixture.CreateUpdater().CheckAndDownloadAsync();

        Assert.Equal(1, fixture.Inner.Downloads);
        Assert.Equal(UpdateSignature.Sha256Hex(fixture.Package), UpdateSignature.Sha256Hex(fixture.Complete));
        Assert.Equal("verified updater", File.ReadAllText(fixture.UpdateExe));
    }

    [Fact]
    public async Task Rejected_cache_and_rejected_feed_leave_no_complete_and_do_not_extract()
    {
        using var fixture = new UpdateFixture();
        fixture.ChangeEntry("tools/Squirrel.exe", "tampered updater");
        fixture.WriteFeed();
        File.Copy(fixture.Package, fixture.Complete);

        await Assert.ThrowsAsync<UpdateVerificationException>(() => fixture.CreateUpdater().CheckAndDownloadAsync());

        Assert.False(File.Exists(fixture.Complete));
        Assert.Equal("original updater", File.ReadAllText(fixture.UpdateExe));
    }

    [Fact]
    public async Task Undeletable_invalid_cache_aborts_before_the_manager_can_use_it()
    {
        using var fixture = new UpdateFixture();
        File.WriteAllText(fixture.Complete, "invalid locked cache");
        using var locked = new FileStream(fixture.Complete, FileMode.Open, FileAccess.Read, FileShare.Read);

        await Assert.ThrowsAsync<UpdateVerificationException>(() => fixture.CreateUpdater().CheckAndDownloadAsync());

        Assert.Equal(0, fixture.Inner.Downloads);
        Assert.Equal("original updater", File.ReadAllText(fixture.UpdateExe));
    }

    [Fact]
    public async Task Verified_cached_package_can_be_reused_without_downloading()
    {
        using var fixture = new UpdateFixture();
        File.Copy(fixture.Package, fixture.Complete);
        var updater = fixture.CreateUpdater();

        await updater.CheckAndDownloadAsync();

        Assert.NotNull(updater.Ready);
        Assert.Equal(0, fixture.Inner.Downloads);
        // Velopack returns before extraction when the complete package already exists.
        Assert.Equal("original updater", File.ReadAllText(fixture.UpdateExe));
    }

    [Fact]
    public async Task Apply_rechecks_the_whole_package_and_clears_ready_on_failure()
    {
        using var fixture = new UpdateFixture();
        var reads = 0;
        var source = new VerifiedUpdateSource(fixture.Inner, Id, fixture.PublicKey, (_, _, _) =>
        {
            reads++;
            return Task.FromResult(File.ReadAllText(fixture.Signature));
        });
        var updater = fixture.CreateUpdater(source);
        await updater.CheckAndDownloadAsync();
        Assert.NotNull(updater.Ready);
        var readsBeforeApply = reads;
        Assert.True(readsBeforeApply > 0);
        File.Delete(fixture.Signature); // Applying must also work offline without rereading a signature.
        File.AppendAllText(fixture.Complete, "changed after Ready");

        await Assert.ThrowsAsync<UpdateVerificationException>(() => updater.ApplyAndRestartAsync());

        Assert.Null(updater.Ready);
        Assert.False(File.Exists(fixture.Complete));
        Assert.Equal(readsBeforeApply, reads);
    }

    [Fact]
    public async Task Cached_verification_without_an_authenticated_signature_fails_closed()
    {
        using var fixture = new UpdateFixture();
        var reads = 0;
        var source = new VerifiedUpdateSource(fixture.Inner, Id, fixture.PublicKey, (_, _, _) =>
        {
            reads++;
            return Task.FromResult(File.ReadAllText(fixture.Signature));
        });
        File.Copy(fixture.Package, fixture.Complete);

        await Assert.ThrowsAsync<UpdateVerificationException>(() => source.VerifyCachedAsync(fixture.Asset, fixture.Complete, default));

        Assert.False(File.Exists(fixture.Complete));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task Cached_verification_accepts_unchanged_bytes_without_rereading_the_signature()
    {
        using var fixture = new UpdateFixture();
        var reads = 0;
        var source = new VerifiedUpdateSource(fixture.Inner, Id, fixture.PublicKey, (_, _, _) =>
        {
            reads++;
            return Task.FromResult(File.ReadAllText(fixture.Signature));
        });
        var updater = fixture.CreateUpdater(source);
        await updater.CheckAndDownloadAsync();
        var readsBeforeRecheck = reads;
        File.Delete(fixture.Signature);

        await source.VerifyCachedAsync(fixture.Asset, fixture.Complete, default);

        Assert.True(File.Exists(fixture.Complete));
        Assert.NotNull(updater.Ready);
        Assert.Equal(readsBeforeRecheck, reads);
    }

    [Theory]
    [InlineData("read-io")]
    [InlineData("read-cancel")]
    public async Task Final_verification_failure_deletes_the_complete_and_never_sets_ready(string failure)
    {
        using var fixture = new UpdateFixture();
        var reads = 0;
        var source = new VerifiedUpdateSource(fixture.Inner, Id, fixture.PublicKey, (_, _, _) =>
        {
            if (++reads > 1)
            {
                if (failure == "read-cancel") throw new OperationCanceledException();
                throw new IOException("final verification failed");
            }
            return Task.FromResult(File.ReadAllText(fixture.Signature));
        });
        var updater = fixture.CreateUpdater(source);

        await Assert.ThrowsAsync<UpdateVerificationException>(() => updater.CheckAndDownloadAsync());

        Assert.Null(updater.Ready);
        Assert.False(File.Exists(fixture.Complete));
    }

    [Theory]
    [InlineData("../outside.nupkg")]
    [InlineData("..\\outside.nupkg")]
    [InlineData("C:\\outside.nupkg")]
    [InlineData("/outside.nupkg")]
    [InlineData("package..nupkg")]
    [InlineData("package.nupkg ")]
    public async Task Unsafe_names_are_filtered_and_cannot_be_downloaded_or_resolved(string name)
    {
        using var fixture = new UpdateFixture();
        var unsafeAsset = fixture.Asset with { FileName = name };
        fixture.Inner.Feed = new VelopackAssetFeed { Assets = [unsafeAsset, fixture.Asset with { PackageId = "Other.App" }, fixture.Asset] };
        var source = fixture.CreateSource(fixture.PublicKey);
        var feed = await source.GetReleaseFeed(Log, Id, "win");
        Assert.Equal([fixture.Asset], feed.Assets);
        var partial = Path.Combine(fixture.Packages, "rejected.partial");
        File.WriteAllText(partial, "partial");

        await Assert.ThrowsAsync<UpdateVerificationException>(() => source.DownloadReleaseEntry(Log, unsafeAsset, partial, _ => { }, default));

        Assert.False(File.Exists(partial));
        Assert.Equal(0, fixture.Inner.Downloads);
        Assert.Throws<UpdateVerificationException>(() => VerifiedUpdateSource.GetPackagePath(fixture.Packages, unsafeAsset));
        Assert.Equal("original updater", File.ReadAllText(fixture.UpdateExe));
    }

    [Fact]
    public void Unsafe_signature_identity_is_rejected()
    {
        using var fixture = new UpdateFixture();
        Assert.Throws<ArgumentException>(() => new VerifiedUpdateSource(fixture.Inner, "../Other.App", fixture.PublicKey, fixture.Feed));
    }

    [Fact]
    public void An_absolute_asset_path_never_resolves_to_an_existing_file_outside_packages()
    {
        using var fixture = new UpdateFixture();
        var asset = fixture.Asset with { FileName = fixture.UpdateExe };

        Assert.Throws<UpdateVerificationException>(() => VerifiedUpdateSource.GetPackagePath(fixture.Packages, asset));

        Assert.Equal("original updater", File.ReadAllText(fixture.UpdateExe));
    }

    [Theory]
    [InlineData("http://example.test/updates")]
    [InlineData("ftp://example.test/updates")]
    public void Web_feeds_require_https(string feed)
    {
        using var fixture = new UpdateFixture();
        Assert.Throws<ArgumentException>(() => new VerifiedUpdateSource(fixture.Inner, Id, fixture.PublicKey, feed));
        Assert.True(VerifiedUpdateSource.IsWebFeed("https://example.test/updates"));
        Assert.False(VerifiedUpdateSource.IsWebFeed(fixture.Feed));
    }

    [Fact]
    public async Task A_signature_at_the_4KB_limit_is_accepted()
    {
        using var fixture = new UpdateFixture();
        var signature = File.ReadAllText(fixture.Signature);
        File.WriteAllText(fixture.Signature, signature.PadRight(4096), new UTF8Encoding(false));
        Assert.Equal(4096, new FileInfo(fixture.Signature).Length);

        Assert.NotNull(await fixture.CreateUpdater().CheckAndDownloadAsync());
    }

    [Fact]
    public async Task Available_deltas_are_disabled_with_a_real_manager_and_local_base_package()
    {
        using var fixture = new UpdateFixture();
        var local = fixture.Asset with { Version = SemanticVersion.Parse("1.0.0"), FileName = Id + "-1.0.0-full.nupkg" };
        File.Copy(fixture.Package, Path.Combine(fixture.Packages, local.FileName));
        var delta = fixture.Asset with { Type = VelopackAssetType.Delta, FileName = Id + "-" + Version + "-delta.nupkg", Size = 1 };
        fixture.Inner.Feed = new VelopackAssetFeed { Assets = [local, delta, fixture.Asset] };
        var source = fixture.CreateSource(fixture.PublicKey);
        var locator = fixture.CreateLocator(local);
        var manager = new YrkUpdateManager(source, locator);

        var update = await manager.CheckForUpdatesAsync();
        Assert.NotNull(update);
        // Velopack still reports the available strategy here; the option is enforced when downloading.
        Assert.Single(update.DeltasToTarget);
        await manager.DownloadUpdatesAsync(update);

        Assert.Equal(1, fixture.Inner.Downloads);
        Assert.Equal(VelopackAssetType.Full, fixture.Inner.LastDownloaded!.Type);
        Assert.Equal("verified updater", File.ReadAllText(fixture.UpdateExe));
    }

    private sealed class UpdateFixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("yrk-verified-update-");
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal string Feed { get; }
        internal string Packages { get; }
        internal string Package => Path.Combine(Feed, Name);
        internal string Complete => Path.Combine(Packages, Name);
        internal string Signature => Path.Combine(Feed, UpdateSignature.FileName(Id, Version));
        internal string UpdateExe => Path.Combine(_root.FullName, "Update.exe");
        internal string PublicKey => _key.ExportSubjectPublicKeyInfoPem();
        internal TrackingSource Inner { get; }
        internal VelopackAsset Asset { get; private set; } = null!;

        internal UpdateFixture()
        {
            Feed = Directory.CreateDirectory(Path.Combine(_root.FullName, "feed")).FullName;
            Packages = Directory.CreateDirectory(Path.Combine(_root.FullName, "packages")).FullName;
            File.WriteAllText(UpdateExe, "original updater");
            using (var zip = ZipFile.Open(Package, ZipArchiveMode.Create))
            {
                WriteEntry(zip, Id + ".nuspec", $"<package><metadata><id>{Id}</id><version>{Version}</version><authors>Test</authors><description>Test only</description></metadata></package>");
                WriteEntry(zip, "lib/net10.0/app.dll", "unchanged app bytes");
                WriteEntry(zip, "tools/Squirrel.exe", "verified updater");
            }
            Sign(Version);
            Inner = new TrackingSource(new SimpleFileSource(new DirectoryInfo(Feed)));
            WriteFeed();
        }

        internal void Sign(string version) => File.WriteAllText(Signature,
            UpdateSignature.Sign(_key, Id, version, Name, UpdateSignature.Sha256Hex(Package)), new UTF8Encoding(false));

        internal void WriteContentSignature() => File.WriteAllText(Path.Combine(Feed, UpdateSignature.ContentFileName(Id, Version)),
            UpdateSignature.SignContent(_key, Id, Version, UpdateSignature.ContentDigest(Package)));

        internal void ChangeEntry(string name, string content)
        {
            using var zip = ZipFile.Open(Package, ZipArchiveMode.Update);
            zip.GetEntry(name)?.Delete();
            WriteEntry(zip, name, content);
        }

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        internal void WriteFeed()
        {
            var bytes = File.ReadAllBytes(Package);
            var sha1 = Convert.ToHexString(SHA1.HashData(bytes));
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            File.WriteAllText(Path.Combine(Feed, "releases.win.json"), JsonSerializer.Serialize(new
            {
                Assets = new[] { new { PackageId = Id, Version, Type = "Full", FileName = Name, SHA1 = sha1, SHA256 = sha256, Size = bytes.Length } }
            }));
            Asset = new VelopackAsset { PackageId = Id, Version = SemanticVersion.Parse(Version), Type = VelopackAssetType.Full,
                FileName = Name, SHA1 = sha1, SHA256 = sha256, Size = bytes.Length };
        }

        internal VerifiedUpdateSource CreateSource(string? key) => new(Inner, Id, key, Feed);

        internal AppUpdater CreateUpdater(VerifiedUpdateSource? source = null)
        {
            source ??= CreateSource(PublicKey);
            return new AppUpdater(new YrkUpdateManager(source, CreateLocator()), source);
        }

        internal TestVelopackLocator CreateLocator(VelopackAsset? localPackage = null) =>
            new(Id, "1.0.0", Packages, _root.FullName, _root.FullName,
                UpdateExe, "win", Log, localPackage!, Path.Combine(_root.FullName, "app.exe"));

        public void Dispose()
        {
            _key.Dispose();
            _root.Delete(recursive: true);
        }
    }

    private sealed class TrackingSource(IUpdateSource inner) : IUpdateSource
    {
        internal int Downloads { get; private set; }
        internal VelopackAsset? LastDownloaded { get; private set; }
        internal Action? BeforeDownload { get; set; }
        internal Action? AfterDownload { get; set; }
        internal Func<string, Task>? InterruptDownload { get; set; }
        internal VelopackAssetFeed? Feed { get; set; }
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null) =>
            Feed is not null ? Task.FromResult(Feed) : inner.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);

        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
            Action<int> progress, CancellationToken cancelToken)
        {
            Downloads++;
            LastDownloaded = releaseEntry;
            BeforeDownload?.Invoke();
            if (InterruptDownload is not null)
            {
                await InterruptDownload(localFile);
                return;
            }
            await inner.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
            AfterDownload?.Invoke();
        }
    }
}
