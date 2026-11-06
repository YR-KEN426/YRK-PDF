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

public sealed class AppUpdaterTests
{
    private const string Id = "YRK.EasyPdf";
    private const string Version = "1.1.0";
    private const string Name = Id + "-" + Version + "-full.nupkg";
    private static readonly IVelopackLogger Log = new NullVelopackLogger();

    [Fact]
    public async Task Caller_cancellation_deletes_partial_and_package_clears_metadata_and_can_retry()
    {
        using var fixture = new UpdateFixture();
        using var cancellation = new CancellationTokenSource();
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Inner.InterruptDownload = async (path, progress, token) =>
        {
            await File.WriteAllTextAsync(path, "incomplete", token);
            await File.WriteAllTextAsync(fixture.Complete, "incomplete", token);
            await File.WriteAllTextAsync(fixture.Complete + ".partial", "incomplete", token);
            progress(25);
            written.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var updater = fixture.CreateUpdater();
        var starts = 0;
        void DownloadStarting(long size, string version)
        {
            Assert.Equal(fixture.Asset.Size, size);
            Assert.Equal(Version, version);
            Assert.Equal(size, updater.DownloadingPackageSize);
            Assert.Equal(version, updater.DownloadingVersion);
            Assert.Equal(starts, fixture.Inner.Downloads);
            starts++;
        }
        var downloading = updater.CheckAndDownloadAsync(cancellationToken: cancellation.Token, downloadStarting: DownloadStarting);
        try
        {
            await written.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Prove the cleanup assertions below exercise files that actually existed before cancellation.
            Assert.True(File.Exists(fixture.Complete + ".partial"));
            Assert.True(File.Exists(fixture.Complete));
            Assert.Equal(fixture.Asset.Size, updater.DownloadingPackageSize);
            Assert.Equal(Version, updater.DownloadingVersion);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<Exception>(() => downloading).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Null(updater.Ready);
        Assert.Equal(0L, updater.DownloadingPackageSize);
        Assert.Null(updater.DownloadingVersion);
        Assert.False(File.Exists(fixture.Complete + ".partial"));
        Assert.False(File.Exists(fixture.Complete));
        Assert.Equal("original updater", File.ReadAllText(fixture.UpdateExe));

        fixture.Inner.InterruptDownload = null;
        var update = await updater.CheckAndDownloadAsync(downloadStarting: DownloadStarting);
        Assert.NotNull(update);
        Assert.Same(update, updater.Ready);
        Assert.Equal(2, starts);
        Assert.Equal(0L, updater.DownloadingPackageSize);
        Assert.Null(updater.DownloadingVersion);
        Assert.Equal("verified updater", File.ReadAllText(fixture.UpdateExe));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stalled_download_times_out_deletes_files_and_can_retry(bool repeatProgress)
    {
        using var fixture = new UpdateFixture();
        fixture.Inner.InterruptDownload = async (path, progress, token) =>
        {
            await File.WriteAllTextAsync(path, "incomplete", token);
            progress(25);
            while (true)
            {
                await Task.Delay(25, token);
                if (repeatProgress) progress(25); // The same percentage must not postpone timeout.
            }
        };
        var updater = fixture.CreateUpdater(idleTimeout: TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<TimeoutException>(() => updater.CheckAndDownloadAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(updater.Ready);
        Assert.False(File.Exists(fixture.Complete));
        Assert.False(File.Exists(fixture.Complete + ".partial"));
        Assert.Equal("original updater", File.ReadAllText(fixture.UpdateExe));

        fixture.Inner.InterruptDownload = null;
        var update = await updater.CheckAndDownloadAsync();
        Assert.NotNull(update);
        Assert.Same(update, updater.Ready);
        Assert.Equal("verified updater", File.ReadAllText(fixture.UpdateExe));
    }

    [Fact]
    public async Task Changing_progress_resets_the_idle_deadline()
    {
        using var fixture = new UpdateFixture();
        fixture.Inner.InterruptDownload = async (path, progress, token) =>
        {
            for (var percent = 0; percent < 5; percent++)
            {
                progress(percent);
                await Task.Delay(100, token);
            }
            File.Copy(fixture.Package, path);
        };
        var updater = fixture.CreateUpdater(idleTimeout: TimeSpan.FromMilliseconds(350));
        var update = await updater.CheckAndDownloadAsync();
        Assert.NotNull(update);
        Assert.Same(update, updater.Ready);
        Assert.Equal("verified updater", File.ReadAllText(fixture.UpdateExe));
    }

    [Fact]
    public async Task Feed_timeout_is_bounded_and_allows_another_check()
    {
        using var fixture = new UpdateFixture();
        var pending = new TaskCompletionSource<VelopackAssetFeed>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Inner.PendingFeed = pending.Task;
        var updater = fixture.CreateUpdater(checkTimeout: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(() => updater.CheckAndDownloadAsync()).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(updater.Ready);
        Assert.Equal(0, fixture.Inner.Downloads);
        fixture.Inner.PendingFeed = null;
        pending.SetResult(new VelopackAssetFeed { Assets = [] });
        Assert.NotNull(await updater.CheckAndDownloadAsync());
    }

    [Fact]
    public async Task Stalled_signature_verification_deletes_download_before_any_promotion()
    {
        using var fixture = new UpdateFixture();
        var source = new VerifiedUpdateSource(fixture.Inner, Id, fixture.PublicKey, async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        });
        var updater = fixture.CreateUpdater(source, idleTimeout: TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<TimeoutException>(() => updater.CheckAndDownloadAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(updater.Ready);
        Assert.False(File.Exists(fixture.Complete));
        Assert.False(File.Exists(fixture.Complete + ".partial"));
        Assert.Equal("original updater", File.ReadAllText(fixture.UpdateExe));
    }

    [Fact]
    public async Task Successful_download_still_requires_a_valid_full_file_signature()
    {
        using var fixture = new UpdateFixture();
        fixture.ChangeEntry("tools/Squirrel.exe", "tampered updater");
        fixture.WriteFeed();
        var updater = fixture.CreateUpdater();
        await Assert.ThrowsAsync<UpdateVerificationException>(() => updater.CheckAndDownloadAsync());
        Assert.Null(updater.Ready);
        Assert.False(File.Exists(fixture.Complete));
        Assert.False(File.Exists(fixture.Complete + ".partial"));
        Assert.Equal("original updater", File.ReadAllText(fixture.UpdateExe));
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

        internal AppUpdater CreateUpdater(VerifiedUpdateSource? source = null, TimeSpan? checkTimeout = null, TimeSpan? idleTimeout = null)
        {
            source ??= CreateSource(PublicKey);
            return new AppUpdater(new YrkUpdateManager(source, CreateLocator()), source, checkTimeout, idleTimeout);
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
        internal Func<string, Action<int>, CancellationToken, Task>? InterruptDownload { get; set; }
        internal VelopackAssetFeed? Feed { get; set; }
        internal Task<VelopackAssetFeed>? PendingFeed { get; set; }
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null) =>
            PendingFeed ?? (Feed is not null ? Task.FromResult(Feed) : inner.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease));

        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
            Action<int> progress, CancellationToken cancelToken)
        {
            Downloads++;
            LastDownloaded = releaseEntry;
            BeforeDownload?.Invoke();
            if (InterruptDownload is not null)
            {
                await InterruptDownload(localFile, progress, cancelToken);
                return;
            }
            await inner.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
            AfterDownload?.Invoke();
        }
    }
}
