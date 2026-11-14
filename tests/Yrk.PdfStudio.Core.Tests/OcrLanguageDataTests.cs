using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Yrk.PdfStudio.Core.Pdf.Ocr;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.Core.Pdf.Scan;
using System.Windows.Threading;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class OcrLanguageDataTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-ocr-download-");

    public void Dispose() => _folder.Delete(recursive: true);

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Orientation_consent_is_asked_once_and_failed_or_declined_download_stays_off(bool accept, bool valid)
    {
        byte[] payload = [1, 2, 3, 4];
        using var handler = new FakeHandler(payload);
        using var client = new HttpClient(handler);
        var table = new Dictionary<string, (string Sha256, long Size)>
        { ["osd"] = (valid ? Convert.ToHexStringLower(SHA256.HashData(payload)) : new string('0', 64), payload.Length) };
        var data = new OcrLanguageData(_folder.FullName, client, table);
        using var session = new ScanSession();
        var model = new ScanViewModel(session, new FakeScannerService(), Dispatcher.CurrentDispatcher);
        Assert.False(model.AutoOrientationEnabled);
        var prompts = 0;
        Task<bool> Consent() { prompts++; return Task.FromResult(accept); }
        if (accept && !valid)
            await Assert.ThrowsAsync<InvalidDataException>(() => model.SetAutoOrientationAsync(true, data, Consent));
        else await model.SetAutoOrientationAsync(true, data, Consent);
        Assert.Equal(accept && valid, model.AutoOrientationEnabled);
        Assert.Equal(accept ? 1 : 0, handler.RequestCount);
        if (accept) Assert.EndsWith("/87416418657359cb625c412a48b6e1d6d41c29bd/osd.traineddata", handler.RequestUri!.AbsoluteUri);
        await model.SetAutoOrientationAsync(false, data, Consent);
        // A consented download that failed is retried without a second prompt.
        if (accept && !valid) await Assert.ThrowsAnyAsync<Exception>(() => model.SetAutoOrientationAsync(true, data, Consent));
        else await model.SetAutoOrientationAsync(true, data, Consent);
        Assert.Equal(1, prompts);
        Assert.Equal(accept ? (valid ? 1 : 2) : 0, handler.RequestCount);
        Assert.Equal(accept && valid, model.AutoOrientationEnabled);
        if (!accept || !valid) Assert.Empty(_folder.GetFiles());
    }

    [Fact]
    public void Verification_table_covers_every_language()
    {
        Assert.Equal(OcrLanguageData.Languages.Count, OcrLanguageData.VerificationData.Count);
        foreach (var (code, _) in OcrLanguageData.Languages)
        {
            Assert.True(OcrLanguageData.VerificationData.TryGetValue(code, out var entry));
            Assert.Matches("^[0-9a-f]{64}$", entry.Sha256);
            Assert.True(entry.Size > 0);
        }
    }

    [Fact]
    public async Task Matching_hash_installs_file_from_pinned_commit()
    {
        byte[] payload = [1, 2, 3, 4];
        using var handler = new FakeHandler(payload);
        using var client = new HttpClient(handler);
        var data = new OcrLanguageData(_folder.FullName, client, Table(payload));

        await data.DownloadAsync("eng");

        Assert.Equal(payload, await File.ReadAllBytesAsync(Target));
        Assert.False(File.Exists(Target + ".download"));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("https://github.com/tesseract-ocr/tessdata_fast/raw/87416418657359cb625c412a48b6e1d6d41c29bd/eng.traineddata",
            handler.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task Wrong_hash_removes_download_without_installing()
    {
        using var handler = new FakeHandler([1, 2, 3, 5]);
        using var client = new HttpClient(handler);
        var data = new OcrLanguageData(_folder.FullName, client, Table([1, 2, 3, 4]));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => data.DownloadAsync("eng"));

        Assert.Contains("SHA-256 mismatch", error.Message);
        AssertNoFiles();
    }

    [Theory]
    [InlineData("eng")]
    [InlineData("unknown")]
    public async Task Language_without_verification_data_sends_no_request(string language)
    {
        using var handler = new FakeHandler([]);
        using var client = new HttpClient(handler);
        var data = new OcrLanguageData(_folder.FullName, client,
            new Dictionary<string, (string Sha256, long Size)>());

        await Assert.ThrowsAsync<ArgumentException>(() => data.DownloadAsync(language));

        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(_folder.GetFiles());
    }

    [Fact]
    public async Task Short_response_removes_download_without_installing()
    {
        using var handler = new FakeHandler([1, 2, 3]);
        using var client = new HttpClient(handler);
        var data = new OcrLanguageData(_folder.FullName, client, Table([1, 2, 3, 4]));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => data.DownloadAsync("eng"));

        Assert.Contains("size mismatch", error.Message);
        AssertNoFiles();
    }

    [Fact]
    public async Task Oversized_response_stops_reading_and_removes_download()
    {
        using var handler = new FakeHandler(new byte[100_000]);
        using var client = new HttpClient(handler);
        var data = new OcrLanguageData(_folder.FullName, client, Table([1, 2, 3, 4]));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => data.DownloadAsync("eng"));

        Assert.Contains("exceeds expected size", error.Message);
        Assert.Equal(5, handler.Body.BytesRead);
        AssertNoFiles();
    }

    [Fact]
    public async Task Failed_verification_preserves_previously_installed_file()
    {
        byte[] installed = [9, 8, 7];
        await File.WriteAllBytesAsync(Target, installed);
        using var handler = new FakeHandler([1, 2, 3, 5]);
        using var client = new HttpClient(handler);
        var data = new OcrLanguageData(_folder.FullName, client, Table([1, 2, 3, 4]));

        await Assert.ThrowsAsync<InvalidDataException>(() => data.DownloadAsync("eng"));

        Assert.Equal(installed, await File.ReadAllBytesAsync(Target));
        Assert.False(File.Exists(Target + ".download"));
    }

    private string Target => Path.Combine(_folder.FullName, "eng.traineddata");

    private void AssertNoFiles()
    {
        Assert.False(File.Exists(Target));
        Assert.False(File.Exists(Target + ".download"));
    }

    private static Dictionary<string, (string Sha256, long Size)> Table(byte[] payload) => new()
    {
        ["eng"] = (Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length),
    };

    private sealed class FakeHandler(byte[] payload) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public CountingStream Body { get; } = new(payload);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestUri = request.RequestUri;
            var content = new StreamContent(Body);
            // Deliberately misleading: verification must use bytes read, not this header.
            content.Headers.ContentLength = 4;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class CountingStream(byte[] payload) : MemoryStream(payload)
    {
        public int BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
    }
}
