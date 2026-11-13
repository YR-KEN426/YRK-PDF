using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Ocr;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppOcrTests
{
    [Fact]
    public async Task Scan_ocr_reads_whitened_sharpened_output_instead_of_raw_gray_scan()
    {
        await using var app = new AppTestHarness();
        app.Model.OcrDataFactory = () => CreateLanguageData(app, install: true);
        app.Dialogs.Answer("AskScan", new Func<object?[], object?>(args =>
        {
            var session = (ScanSession)args[0]!;
            var scanner = new FakeScannerService { PageCount = 1, PageStyle = FakeScanPageStyle.GrayFaintText };
            scanner.ScanAsync(FakeScannerService.Device, ScanSettings,
                new ScanSessionTests.Sink<ScannedPage>(page => session.Add(page))).GetAwaiter().GetResult();
            var entry = Assert.Single(session.Pages);
            Assert.Equal(211, session.Read(entry).Bitmap.Pixels[0]);
            entry.WhiteBackground = true;
            entry.Sharpness = 35;
            return new ScanRequest(session, ScanSettings, true, ["tha", "eng"]);
        }));
        var called = false;
        app.Model.OcrRunner = async (document, bytes, _, _, _, token) =>
        {
            called = true;
            var image = await document.RenderPageAsync(0, 827, 1169, cancellationToken: token);
            Assert.InRange(image.Pixels[0], 245, 255);
            Assert.Contains(image.Pixels.Where((_, i) => i % 4 == 0), value => value < 100);
            return new OcrSummary(bytes, 0, 0, 0);
        };
        app.Dialogs.Answer("PickSavePdfFile", app.PathFor("adjusted-scan.pdf"));
        await app.Model.ScanToPdfCommand.ExecuteAsync(null);
        Assert.True(called);
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    private static readonly ScanSettings ScanSettings = new(Dpi: 100, AutoStraighten: false, AutoCrop: false);
    private static readonly OcrOptions OcrOptions = new(["tha", "eng"], OcrPageSelection.AllPages);

    [Fact]
    public async Task Menu_ocr_with_installed_languages_adds_searchable_text_without_confirmation()
    {
        await using var app = new AppTestHarness();
        var source = app.CreatePdf();
        Assert.True(await app.OpenAsync(source));
        var data = CreateLanguageData(app, install: true);
        app.Model.OcrDataFactory = () => data;
        var runner = new TextLayerRunner();
        app.Model.OcrRunner = runner.RunAsync;
        var output = app.PathFor("searchable.pdf");
        app.Dialogs.Answer("AskOcr", OcrOptions);
        app.Dialogs.Answer("PickSavePdfFile", output);

        await app.Model.RecognizeTextCommand.ExecuteAsync(null);

        Assert.Empty(app.Dialogs.Named("Confirm"));
        Assert.Equal(1, runner.Calls);
        Assert.True(File.Exists(output));
        await using var pdf = await app.Engine.OpenAsync(output);
        Assert.Contains("สัญญาเช่า", await pdf.GetPageTextAsync(0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Menu_ocr_downloads_missing_languages_and_runs_recognition()
    {
        await using var app = new AppTestHarness();
        Assert.True(await app.OpenAsync(app.CreatePdf()));
        using var handler = new LanguageHandler();
        using var client = new HttpClient(handler);
        var data = CreateLanguageData(app, install: false, client, handler.VerificationData);
        app.Model.OcrDataFactory = () => data;
        var runner = new TextLayerRunner();
        app.Model.OcrRunner = runner.RunAsync;
        var output = app.PathFor("downloaded-searchable.pdf");
        app.Dialogs.Answer("AskOcr", OcrOptions);
        app.Dialogs.Answer("Confirm", true);
        app.Dialogs.Answer("PickSavePdfFile", output);

        await app.Model.RecognizeTextCommand.ExecuteAsync(null);

        Assert.Single(app.Dialogs.Named("Confirm"));
        Assert.Equal(2, handler.RequestCount);
        Assert.All(new[] { "tha", "eng" }, language => Assert.True(data.IsInstalled(language)));
        Assert.Equal(1, runner.Calls);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task Menu_ocr_declined_download_does_not_run_or_pick_output()
    {
        await using var app = new AppTestHarness();
        Assert.True(await app.OpenAsync(app.CreatePdf()));
        using var handler = new LanguageHandler();
        using var client = new HttpClient(handler);
        var data = CreateLanguageData(app, install: false, client, handler.VerificationData);
        app.Model.OcrDataFactory = () => data;
        var runner = new TextLayerRunner();
        app.Model.OcrRunner = runner.RunAsync;
        app.Dialogs.Answer("AskOcr", OcrOptions);
        app.Dialogs.Answer("Confirm", false);

        await app.Model.RecognizeTextCommand.ExecuteAsync(null);

        Assert.Single(app.Dialogs.Named("Confirm"));
        Assert.Equal(0, runner.Calls);
        Assert.Empty(app.Dialogs.Named("PickSavePdfFile"));
        Assert.Equal(0, handler.RequestCount);
        Assert.Single(Directory.GetFiles(app.Folder, "*.pdf"));
    }

    [Fact]
    public async Task Menu_ocr_cancellation_keeps_original_open_and_cleans_operation()
    {
        await using var app = new AppTestHarness();
        var source = app.CreatePdf();
        var originalBytes = File.ReadAllBytes(source);
        Assert.True(await app.OpenAsync(source));
        app.Model.OcrDataFactory = () => CreateLanguageData(app, install: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Model.OcrRunner = async (_, _, _, _, _, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        };
        var output = app.PathFor("cancelled.pdf");
        app.Dialogs.Answer("AskOcr", OcrOptions);
        app.Dialogs.Answer("PickSavePdfFile", output);

        var operation = app.Model.RecognizeTextCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        app.Model.CancelOperationCommand.Execute(null);
        await operation;

        Assert.False(File.Exists(output));
        Assert.False(app.Model.IsBusy);
        Assert.False(app.Model.IsOperationRunning);
        Assert.Empty(app.Dialogs.Named("ShowError"));
        Assert.True(app.Model.HasDocument);
        Assert.Equal(originalBytes, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task Scanning_with_ocr_makes_every_page_searchable_and_opens_output()
    {
        await using var app = new AppTestHarness();
        app.Model.ScannerFactory = () => new FakeScannerService();
        app.Model.OcrDataFactory = () => CreateLanguageData(app, install: true);
        var runner = new TextLayerRunner();
        app.Model.OcrRunner = runner.RunAsync;
        string? sessionFolder = null;
        app.Dialogs.Answer("AskScan", ScanAnswer(ocr: true, observe: session => sessionFolder = session.DirectoryPath));
        var output = app.PathFor("scan.pdf");
        app.Dialogs.Answer("PickSavePdfFile", output);

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        await using var pdf = await app.Engine.OpenAsync(output);
        Assert.Equal(2, pdf.PageCount);
        for (var i = 0; i < pdf.PageCount; i++)
            Assert.Contains("สัญญาเช่า", await pdf.GetPageTextAsync(i), StringComparison.Ordinal);
        Assert.NotEmpty(await pdf.FindTextAsync(0, "สัญญาเช่า", PdfSearchOptions.None));
        Assert.True(app.Model.HasDocument);
        Assert.False(Directory.Exists(sessionFolder));
        Assert.Empty(Directory.GetFiles(app.Folder, "~*.yrk-writing"));
        Assert.False(string.IsNullOrWhiteSpace(app.Model.StatusMessage));
        Assert.Contains("words", app.Model.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scanning_ocr_rejects_output_changed_while_downloading_languages()
    {
        await using var app = new AppTestHarness();
        var output = app.PathFor("scan.pdf");
        app.Dialogs.Answer("AskScan", ScanAnswer(ocr: true));
        app.Dialogs.Answer("PickSavePdfFile", output);
        using var handler = new LanguageHandler();
        using var client = new HttpClient(handler);
        app.Model.OcrDataFactory = () => CreateLanguageData(app, install: false, client, handler.VerificationData);
        byte[]? scanned = null;
        app.Dialogs.Answer("Confirm", new Func<object?[], object?>(_ =>
        {
            scanned = File.ReadAllBytes(output);
            File.SetLastWriteTimeUtc(output, File.GetLastWriteTimeUtc(output).AddMinutes(1));
            return true;
        }));
        app.Model.OcrRunner = new TextLayerRunner().RunAsync;

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        Assert.Equal(scanned, File.ReadAllBytes(output));
        Assert.Single(app.Dialogs.Named("ShowError"));
        var recovery = Assert.Single(Directory.GetFiles(app.Folder, "*.yrk-writing"));
        await using var pdf = await app.Engine.OpenAsync(recovery);
        Assert.Contains("Scan", await pdf.GetPageTextAsync(0), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("timestamp")]
    public async Task Scanning_ocr_preserves_changed_non_ocr_pdf_and_retains_searchable_result(string change)
    {
        Action? changeOutput = null;
        await using var app = new AppTestHarness(wrapOpenedDocument: document => AfterDispose(document, () =>
        {
            var update = changeOutput;
            changeOutput = null;
            update?.Invoke();
        }));
        var output = app.PathFor("scan-conflict.pdf");
        app.Model.OcrDataFactory = () => CreateLanguageData(app, install: true);
        app.Dialogs.Answer("AskScan", ScanAnswer(ocr: true));
        app.Dialogs.Answer("PickSavePdfFile", output);
        var runner = new TextLayerRunner();
        byte[]? competing = null;
        app.Model.OcrRunner = async (document, bytes, options, data, progress, token) =>
        {
            var result = await runner.RunAsync(document, bytes, options, data, progress, token);
            var written = File.GetLastWriteTimeUtc(output);
            competing = change == "length" ? bytes.Concat(new byte[] { 10 }).ToArray() : bytes;
            // Simulate another writer as soon as OCR releases its read handle, before publish.
            changeOutput = () =>
            {
                File.WriteAllBytes(output, competing);
                File.SetLastWriteTimeUtc(output, change == "length" ? written : written.AddMinutes(1));
            };
            return result;
        };

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        var error = (string)Assert.Single(app.Dialogs.Named("ShowError")).Arguments[0]!;
        Assert.True(error.Contains("destination changed", StringComparison.OrdinalIgnoreCase), error);
        Assert.Equal(competing, File.ReadAllBytes(output));
        Assert.True(app.Model.HasDocument);
        var recovery = Assert.Single(Directory.GetFiles(app.Folder, "*.yrk-writing"));
        Assert.Contains(recovery, error, StringComparison.Ordinal);
        await using var nonOcr = await app.Engine.OpenAsync(output);
        Assert.Equal(string.Empty, (await nonOcr.GetPageTextAsync(0)).Trim());
        await using var ocr = await app.Engine.OpenAsync(recovery);
        Assert.Contains("Scan", await ocr.GetPageTextAsync(0), StringComparison.Ordinal);
        Assert.False(app.Model.IsBusy);
        Assert.False(app.Model.IsOperationRunning);
    }

    [Fact]
    public async Task Scanning_ocr_cancellation_keeps_scanned_pdf_without_text()
    {
        await using var app = new AppTestHarness();
        app.Model.ScannerFactory = () => new FakeScannerService();
        app.Model.OcrDataFactory = () => CreateLanguageData(app, install: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Model.OcrRunner = async (_, _, _, _, _, token) =>
        {
            started.SetResult();
            app.Model.CancelOperationCommand.Execute(null);
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        };
        app.Dialogs.Answer("AskScan", ScanAnswer(ocr: true));
        var output = app.PathFor("scan-cancelled.pdf");
        app.Dialogs.Answer("PickSavePdfFile", output);

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        Assert.True(started.Task.IsCompleted);
        Assert.True(File.Exists(output));
        await using var pdf = await app.Engine.OpenAsync(output);
        Assert.Equal(2, pdf.PageCount);
        Assert.Equal(string.Empty, (await pdf.GetPageTextAsync(0)).Trim());
        Assert.Empty(app.Dialogs.Named("ShowError"));
        Assert.Empty(Directory.GetFiles(app.Folder, "*.yrk-writing"));
    }

    [Fact]
    public async Task Scanning_ocr_declining_language_download_saves_without_text()
    {
        await using var app = new AppTestHarness();
        app.Model.ScannerFactory = () => new FakeScannerService();
        using var handler = new LanguageHandler();
        using var client = new HttpClient(handler);
        var data = CreateLanguageData(app, install: false, client, handler.VerificationData);
        app.Model.OcrDataFactory = () => data;
        var runner = new TextLayerRunner();
        app.Model.OcrRunner = runner.RunAsync;
        app.Dialogs.Answer("AskScan", ScanAnswer(ocr: true));
        app.Dialogs.Answer("PickSavePdfFile", app.PathFor("scan-no-languages.pdf"));
        app.Dialogs.Answer("Confirm", false);

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        Assert.Equal(0, runner.Calls);
        Assert.Equal(0, handler.RequestCount);
        await using var pdf = await app.Engine.OpenAsync(app.PathFor("scan-no-languages.pdf"));
        Assert.Equal(string.Empty, (await pdf.GetPageTextAsync(0)).Trim());
    }

    [Fact]
    public async Task Scanning_ocr_pdf_failure_reports_one_error_and_keeps_original_pdf()
    {
        await using var app = new AppTestHarness();
        app.Model.ScannerFactory = () => new FakeScannerService();
        app.Model.OcrDataFactory = () => CreateLanguageData(app, install: true);
        app.Model.OcrRunner = (_, _, _, _, _, _) => throw new PdfException("OCR failed in test.");
        app.Dialogs.Answer("AskScan", ScanAnswer(ocr: true));
        app.Dialogs.Answer("PickSavePdfFile", app.PathFor("scan-error.pdf"));

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        Assert.Single(app.Dialogs.Named("ShowError"));
        await using var pdf = await app.Engine.OpenAsync(app.PathFor("scan-error.pdf"));
        Assert.Equal(2, pdf.PageCount);
        Assert.Equal(string.Empty, (await pdf.GetPageTextAsync(0)).Trim());
    }

    [Fact]
    public async Task Cancelling_scan_after_first_page_cleans_session_and_never_picks_output()
    {
        await using var app = new AppTestHarness();
        app.Model.ScannerFactory = () => new FakeScannerService { PageCount = 4 };
        string? sessionFolder = null;
        app.Dialogs.Answer("AskScan", (Func<object?[], object?>)(args =>
        {
            var session = (ScanSession)args[0]!;
            sessionFolder = session.DirectoryPath;
            var scanner = (IScannerService)args[1]!;
            using var cancellation = new CancellationTokenSource();
            var sink = new ScanSessionTests.Sink<ScannedPage>(page =>
            {
                session.Add(page, CancellationToken.None);
                if (session.Pages.Count == 1) cancellation.Cancel();
            });
            try
            {
                scanner.ScanAsync(FakeScannerService.Device, ScanSettings, sink, cancellation.Token).GetAwaiter().GetResult();
            }
            catch (ScannerException ex) when (ex.Kind == ScannerErrorKind.Cancelled) { }
            catch (OperationCanceledException) { }
            return null;
        }));

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        Assert.NotNull(sessionFolder);
        Assert.False(Directory.Exists(sessionFolder));
        Assert.Empty(app.Dialogs.Named("PickSavePdfFile"));
        Assert.Empty(Directory.GetFiles(app.Folder, "*.pdf"));
    }

    [Fact]
    public async Task Scanning_with_real_tesseract_recognizes_english_when_language_data_exists()
    {
        await using var app = new AppTestHarness();
        var data = new OcrLanguageData();
        if (!data.IsInstalled("tha") || !data.IsInstalled("eng")) return;
        app.Model.ScannerFactory = () => new FakeScannerService { PageCount = 1 };
        app.Dialogs.Answer("AskScan", ScanAnswer(ocr: true));
        app.Dialogs.Answer("PickSavePdfFile", app.PathFor("scan-real-ocr.pdf"));

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        await using var pdf = await app.Engine.OpenAsync(app.PathFor("scan-real-ocr.pdf"));
        Assert.Contains("Scan", await pdf.GetPageTextAsync(0), StringComparison.OrdinalIgnoreCase);
    }

    private static Func<object?[], object?> ScanAnswer(bool ocr, Action<ScanSession>? observe = null) => args =>
    {
        var session = (ScanSession)args[0]!;
        var scanner = new FakeScannerService { PageCount = 2 };
        scanner.ScanAsync(FakeScannerService.Device, ScanSettings,
            new ScanSessionTests.Sink<ScannedPage>(page => session.Add(page))).GetAwaiter().GetResult();
        observe?.Invoke(session);
        return new ScanRequest(session, ScanSettings, ocr, ["tha", "eng"]);
    };

    private static IPdfDocument AfterDispose(IPdfDocument document, Action afterDispose)
    {
        var proxy = DispatchProxy.Create<IPdfDocument, SignatureSecurityTests.TestProxy>();
        ((SignatureSecurityTests.TestProxy)(object)proxy).Handle = (method, args) =>
            method.Name == nameof(IAsyncDisposable.DisposeAsync) ? DisposeAsync() : method.Invoke(document, args);
        return proxy;

        async ValueTask DisposeAsync()
        {
            await document.DisposeAsync();
            afterDispose();
        }
    }

    private static OcrLanguageData CreateLanguageData(AppTestHarness app, bool install, HttpClient? client = null,
        IReadOnlyDictionary<string, (string Sha256, long Size)>? verificationData = null)
    {
        var folder = Path.Combine(app.Folder, "tessdata");
        if (install)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "tha.traineddata"), [1]);
            File.WriteAllBytes(Path.Combine(folder, "eng.traineddata"), [1]);
        }

        return new OcrLanguageData(folder, client ?? new HttpClient(new LanguageHandler()), verificationData ?? OcrLanguageData.VerificationData);
    }

    private sealed class TextLayerRunner
    {
        public int Calls { get; private set; }

        public Task<OcrSummary> RunAsync(IPdfDocument document, byte[] bytes, OcrOptions options,
            OcrLanguageData data, IProgress<OcrProgress>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var pages = Enumerable.Range(0, document.PageCount)
                .Select(i => new OcrPageResult(i,
                [
                    new OcrWord("สัญญาเช่า", new NormalizedRect(0.1, 0.1, 0.3, 0.05), 90f, true),
                    new OcrWord("Scan", new NormalizedRect(0.1, 0.2, 0.2, 0.05), 90f, true),
                ])).ToArray();
            var output = PdfOcr.AddTextLayer(bytes, pages);
            return Task.FromResult(new OcrSummary(output, pages.Length, 0, pages.Sum(p => p.Words.Count)));
        }
    }

    private sealed class LanguageHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        private static readonly byte[] Payload = [1, 2, 3, 4];
        public IReadOnlyDictionary<string, (string Sha256, long Size)> VerificationData { get; } =
            new Dictionary<string, (string Sha256, long Size)>
            {
                ["tha"] = (Convert.ToHexStringLower(SHA256.HashData(Payload)), Payload.Length),
                ["eng"] = (Convert.ToHexStringLower(SHA256.HashData(Payload)), Payload.Length),
            };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            var language = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
            // The actual verification table is pinned to large binaries. Return a correctly-sized payload
            // only for a test-local table so tests exercise the download flow without relying on the network.
            var content = new ByteArrayContent(Payload);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
