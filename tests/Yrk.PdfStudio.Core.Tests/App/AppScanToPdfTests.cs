using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppScanToPdfTests
{
    private static readonly ScanSettings Settings = new(Dpi: 150, AutoStraighten: false, AutoCrop: false);

    private static Func<object?[], object?> ScanAnswer(Action<ScanSession>? observe = null) => args =>
    {
        var session = (ScanSession)args[0]!;
        var scanner = new FakeScannerService { PageCount = 2 };
        scanner.ScanAsync(FakeScannerService.Device, Settings,
            new ScanSessionTests.Sink<ScannedPage>(page => session.Add(page))).GetAwaiter().GetResult();
        observe?.Invoke(session);
        return new ScanRequest(session, Settings, true, ["tha", "eng"]);
    };

    [Fact]
    public async Task Creates_opens_pdf_with_scanned_page_count_and_cleans_session()
    {
        await using var app = new AppTestHarness();
        app.Model.ScannerFactory = () => new FakeScannerService();
        string? folder = null;
        app.Dialogs.Answer("AskScan", ScanAnswer(session => folder = session.DirectoryPath));
        var output = app.PathFor("scan.pdf");
        app.Dialogs.Answer("PickSavePdfFile", output);

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        Assert.True(File.Exists(output));
        Assert.True(app.Model.HasDocument);
        Assert.Equal(2, app.Model.Pages.Count);
        await using var pdf = await app.Engine.OpenAsync(output);
        Assert.Equal(2, pdf.PageCount);
        Assert.False(Directory.Exists(folder));
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Fact]
    public async Task Declined_replacement_asks_for_another_path_and_preserves_original_bytes()
    {
        await using var app = new AppTestHarness();
        app.Model.ScannerFactory = () => new FakeScannerService();
        var original = app.CreatePdf();
        var bytes = File.ReadAllBytes(original);
        var output = app.PathFor("new.pdf");
        app.Dialogs.Answer("AskScan", ScanAnswer());
        app.Dialogs.Answer("PickSavePdfFile", original, output);
        app.Dialogs.Answer("Confirm", false);

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        Assert.Equal(2, app.Dialogs.Named("PickSavePdfFile").Length);
        Assert.Single(app.Dialogs.Named("Confirm"));
        Assert.Equal(bytes, File.ReadAllBytes(original));
        Assert.True(File.Exists(output));
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Fact]
    public async Task Closing_scan_dialog_disposes_session_without_creating_output()
    {
        await using var app = new AppTestHarness();
        app.Model.ScannerFactory = () => new FakeScannerService();
        app.Dialogs.Answer("AskScan", (object?)null);

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        var session = (ScanSession)app.Dialogs.Named("AskScan").Single().Arguments[0]!;
        Assert.False(Directory.Exists(session.DirectoryPath));
        Assert.Empty(Directory.GetFiles(app.Folder));
        Assert.Empty(app.Dialogs.Named("PickSavePdfFile"));
    }

    [Fact]
    public async Task Cancelling_save_reopens_same_session_with_pages_intact()
    {
        await using var app = new AppTestHarness();
        app.Model.ScannerFactory = () => new FakeScannerService();
        ScanSession? first = null;
        ScanSessionPage? page = null;
        app.Dialogs.Answer("AskScan", ScanAnswer(session => { first = session; page = session.Pages[0]; }));
        app.Dialogs.Answer("AskScan", new Func<object?[], object?>(args =>
        {
            Assert.Same(first, args[0]);
            Assert.Equal(2, first!.Pages.Count);
            Assert.Same(page, first.Pages[0]);
            Assert.True(File.Exists(page!.FilePath));
            return new ScanRequest(first, Settings, false, ["tha", "eng"]);
        }));
        var output = app.PathFor("scan.pdf");
        app.Dialogs.Answer("PickSavePdfFile", null, output);

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        Assert.Equal(2, app.Dialogs.Named("AskScan").Length);
        Assert.True(File.Exists(output));
        Assert.False(Directory.Exists(first!.DirectoryPath));
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Fact]
    public async Task Build_failure_preserves_existing_destination_and_exits_disposed_session()
    {
        await using var app = new AppTestHarness();
        var original = app.CreatePdf();
        var bytes = File.ReadAllBytes(original);
        ScanSession? scanned = null;
        app.Dialogs.Answer("AskScan", new Func<object?[], object?>(args =>
        {
            scanned = (ScanSession)args[0]!;
            scanned.Add(ScanSessionTests.Page(20, 30));
            return new ScanRequest(scanned, new(JpegQuality: 0), false, []);
        }));
        app.Dialogs.Answer("PickSavePdfFile", original);
        app.Dialogs.Answer("Confirm", true);
        await app.Model.ScanToPdfCommand.ExecuteAsync(null);
        Assert.Equal(bytes, File.ReadAllBytes(original));
        Assert.False(Directory.Exists(scanned!.DirectoryPath));
        Assert.Single(app.Dialogs.Named("AskScan"));
        Assert.Single(app.Dialogs.Named("ShowError"));
        Assert.Empty(Directory.GetFiles(app.Folder, "*.yrk-writing"));
    }
}
