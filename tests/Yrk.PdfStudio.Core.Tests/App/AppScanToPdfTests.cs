using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppScanToPdfTests
{
    [Theory]
    [InlineData(ScanOutputFormat.Jpeg, false)]
    [InlineData(ScanOutputFormat.Png, false)]
    [InlineData(ScanOutputFormat.Pdf, false)]
    [InlineData(ScanOutputFormat.Jpeg, true)]
    [InlineData(ScanOutputFormat.Png, true)]
    [InlineData(ScanOutputFormat.Pdf, true)]
    public async Task Save_failure_reopens_intact_session_and_retry_succeeds(ScanOutputFormat format, bool beforeWrite)
    {
        await using var app = new AppTestHarness();
        ScanSession? first = null;
        ScanSessionPage? page = null;
        app.Dialogs.Answer("AskScan", new Func<object?[], object?>(args =>
        {
            first = (ScanSession)args[0]!;
            page = first.Add(ScanSessionTests.Page(40, 60));
            page.Rotation = 90; page.Crop = new(.1, .2, .5, .6);
            return new ScanRequest(first, Settings, false, [], format);
        }), new Func<object?[], object?>(args =>
        {
            Assert.Same(first, args[0]);
            Assert.Same(page, Assert.Single(first!.Pages));
            Assert.Equal(90, page!.Rotation);
            Assert.Equal(new(.1, .2, .5, .6), page.Crop);
            Assert.Equal(40, first.Read(page).Bitmap.Width);
            Assert.Single(app.Dialogs.Named("ShowError"));
            return new ScanRequest(first, Settings, false, [], format);
        }));
        var chosen = app.PathFor("retry.pdf");
        app.Dialogs.Answer(format == ScanOutputFormat.Pdf ? "PickSavePdfFile" : "PickSaveFile",
            beforeWrite ? "invalid\0path" : app.PathFor("missing/scan.pdf"), chosen);

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        Assert.Equal(2, app.Dialogs.Named("AskScan").Length);
        Assert.True(File.Exists(format == ScanOutputFormat.Pdf ? chosen
            : Yrk.PdfStudio.App.ViewModels.MainViewModel.ScanImagePath(chosen, 1, 1, format)));
        Assert.False(Directory.Exists(first!.DirectoryPath));
        Assert.Single(app.Dialogs.Named("ShowError"));
    }

    private static readonly ScanSettings Settings = new(Dpi: 150, AutoStraighten: false, AutoCrop: false);

    private static Func<object?[], object?> ScanAnswer(Action<ScanSession>? observe = null) => args =>
    {
        var session = (ScanSession)args[0]!;
        var scanner = new FakeScannerService { PageCount = 2 };
        scanner.ScanAsync(FakeScannerService.Device, Settings,
            new ScanSessionTests.Sink<ScannedPage>(page => session.Add(page))).GetAwaiter().GetResult();
        observe?.Invoke(session);
        return new ScanRequest(session, Settings, false, ["tha", "eng"]);
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
    public async Task Destination_created_after_save_dialog_is_not_overwritten_and_result_is_kept()
    {
        await using var app = new AppTestHarness();
        var output = app.PathFor("scan.pdf");
        byte[] competing = [1, 2, 3, 4];
        app.Dialogs.Answer("AskScan", ScanAnswer());
        app.Dialogs.Answer("AskScan", (object?)null);
        app.Dialogs.Answer("PickSavePdfFile", output);
        app.Model.CommitEditsRequested += (_, _) => File.WriteAllBytes(output, competing);

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        Assert.Equal(competing, File.ReadAllBytes(output));
        Assert.Empty(app.Dialogs.Named("Confirm"));
        Assert.False(app.Model.HasDocument);
        var recovery = Assert.Single(Directory.GetFiles(app.Folder, "*.yrk-writing"));
        Assert.Contains(recovery, (string)Assert.Single(app.Dialogs.Named("ShowError")).Arguments[0]!, StringComparison.Ordinal);
        await using var pdf = await app.Engine.OpenAsync(recovery);
        Assert.Equal(2, pdf.PageCount);
    }

    [Fact]
    public async Task Confirmed_unchanged_destination_is_replaced()
    {
        await using var app = new AppTestHarness();
        var output = app.CreatePdf();
        var original = File.ReadAllBytes(output);
        app.Dialogs.Answer("AskScan", ScanAnswer());
        app.Dialogs.Answer("PickSavePdfFile", output);
        app.Dialogs.Answer("Confirm", true);

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        Assert.NotEqual(original, File.ReadAllBytes(output));
        Assert.True(app.Model.HasDocument);
        Assert.Empty(app.Dialogs.Named("ShowError"));
        Assert.Empty(Directory.GetFiles(app.Folder, "*.yrk-writing"));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("timestamp")]
    [InlineData("deleted")]
    public async Task Confirmed_destination_changed_before_publish_is_not_replaced(string change)
    {
        await using var app = new AppTestHarness();
        var output = app.CreatePdf();
        var original = File.ReadAllBytes(output);
        var written = File.GetLastWriteTimeUtc(output);
        app.Dialogs.Answer("AskScan", ScanAnswer());
        app.Dialogs.Answer("AskScan", (object?)null);
        app.Dialogs.Answer("PickSavePdfFile", output);
        app.Dialogs.Answer("Confirm", true);
        app.Model.CommitEditsRequested += (_, _) =>
        {
            if (change == "length")
            {
                File.WriteAllBytes(output, [1, 2]);
                File.SetLastWriteTimeUtc(output, written); // Prove length alone detects a change.
            }
            else if (change == "timestamp") File.SetLastWriteTimeUtc(output, written.AddMinutes(1));
            else File.Delete(output);
        };

        await app.Model.ScanToPdfCommand.ExecuteAsync(null);

        if (change == "deleted") Assert.False(File.Exists(output));
        else Assert.Equal(change == "length" ? new byte[] { 1, 2 } : original, File.ReadAllBytes(output));
        var recovery = Assert.Single(Directory.GetFiles(app.Folder, "*.yrk-writing"));
        var error = (string)Assert.Single(app.Dialogs.Named("ShowError")).Arguments[0]!;
        Assert.Contains("destination changed", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(recovery, error, StringComparison.Ordinal);
        Assert.False(app.Model.HasDocument);
        await using var pdf = await app.Engine.OpenAsync(recovery);
        Assert.Equal(2, pdf.PageCount);
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
    public async Task Build_failure_preserves_existing_destination_and_disposes_session_after_user_closes()
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
        app.Dialogs.Answer("AskScan", (object?)null);
        app.Dialogs.Answer("Confirm", true);
        await app.Model.ScanToPdfCommand.ExecuteAsync(null);
        Assert.Equal(bytes, File.ReadAllBytes(original));
        Assert.False(Directory.Exists(scanned!.DirectoryPath));
        Assert.Equal(2, app.Dialogs.Named("AskScan").Length);
        Assert.Single(app.Dialogs.Named("ShowError"));
        Assert.Empty(Directory.GetFiles(app.Folder, "*.yrk-writing"));
    }
}
