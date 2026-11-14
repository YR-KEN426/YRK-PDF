using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Ocr;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class AppConvertScanTests
{
    [Fact]
    public void Dialogs_construct_without_showing_and_copy_button_passes_all_file_sections_to_clipboard()
    {
        Sta.Run(() =>
        {
            void Reset() => typeof(MainWindowLayoutTests).GetMethod("ForgetApplicationFromEarlierTest",
                BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            Reset();
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, typeof(MainWindow).Assembly);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative) });
            var folder = Directory.CreateTempSubdirectory("convert-dialog-test-");
            ConvertScanDialog? input = null;
            ConvertScanResultDialog? output = null;
            try
            {
                var file = Path.Combine(folder.FullName, "scan.pdf");
                File.WriteAllBytes(file, ScanPdfFixture.Build(""));
                input = new ConvertScanDialog([file]);
                input.Measure(new Size(680, 780));
                input.Arrange(new Rect(0, 0, 680, 780));
                Assert.True(((CheckBox)input.FindName("PdfBox")).IsChecked);
                Assert.True(((CheckBox)input.FindName("ReviewBox")).IsChecked);
                Assert.False(((CheckBox)input.FindName("WordBox")).IsChecked);
                Assert.True(((RadioButton)input.FindName("ScannedOnly")).IsChecked);
                Assert.Equal(string.Empty, ((TextBox)input.FindName("FolderBox")).Text);
                var choices = ((ItemsControl)input.FindName("LanguageList")).Items.Cast<OcrDialog.LanguageChoice>();
                Assert.Equal(new[] { "tha", "eng" }, choices.Where(c => c.IsChecked).Select(c => c.Code));
                Assert.True(((Button)input.FindName("OkButton")).IsEnabled);
                Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName((Button)input.FindName("OkButton"))));

                var combined = "scan.pdf\nFirst OCR text\n\nphoto.png\nSecond OCR text";
                string? copied = null;
                output = new ConvertScanResultDialog(new ConvertScanResult("Converted 2 of 2 files", [], combined))
                    { CopyText = text => copied = text };
                ((Button)output.FindName("CopyTextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(combined, copied);
                Assert.False(input.IsVisible);
                Assert.False(output.IsVisible);
            }
            finally
            {
                input?.Close();
                output?.Close();
                folder.Delete(true);
                application.Shutdown();
                Reset();
            }
            return true;
        });
    }

    [Fact]
    public async Task Pdf_and_image_export_all_formats_without_overwrite_or_source_changes_and_collect_clipboard_text()
    {
        await using var app = new AppTestHarness();
        var pdf = WritePdf(app);
        var image = app.PathFor("photo.png");
        WriteImage(image);
        var before = new[] { pdf, image }.Select(p => SHA256.HashData(File.ReadAllBytes(p))).ToArray();
        var existing = app.PathFor("scan (OCR).pdf");
        File.WriteAllText(existing, "Existing user's file");
        Configure(app, [pdf, image]);

        await app.Model.ConvertScanCommand.ExecuteAsync(null);

        var result = Result(app);
        Assert.Equal(6, result.OutputFiles.Count);
        Assert.Contains(app.PathFor("scan (OCR) (2).pdf"), result.OutputFiles);
        Assert.Contains(app.PathFor("photo.pdf"), result.OutputFiles);
        foreach (var path in result.OutputFiles)
        {
            Assert.True(File.Exists(path), path);
            if (path.EndsWith(".docx", StringComparison.Ordinal)) AssertOcrDocx(path);
            if (path.EndsWith(".txt", StringComparison.Ordinal)) Assert.Contains("Fresh OCR text", File.ReadAllText(path));
            if (path.EndsWith(".pdf", StringComparison.Ordinal))
            {
                await using var doc = await app.Engine.OpenAsync(path);
                Assert.Contains("Fresh OCR text", await doc.GetPageTextAsync(0));
            }
        }
        Assert.Equal("Existing user's file", File.ReadAllText(existing));
        Assert.Equal(before[0], SHA256.HashData(File.ReadAllBytes(pdf)));
        Assert.Equal(before[1], SHA256.HashData(File.ReadAllBytes(image)));
        Assert.Contains("scan.pdf" + Environment.NewLine + "Fresh OCR text", result.AllText);
        Assert.Contains("photo.png" + Environment.NewLine + "Fresh OCR text", result.AllText);
        Assert.True(result.AllText.IndexOf("scan.pdf", StringComparison.Ordinal) < result.AllText.IndexOf("photo.png", StringComparison.Ordinal));
        Assert.Empty(app.Dialogs.Named("ShowError"));
        AssertClean(app);
    }

    [Theory]
    [InlineData("ocr")]
    [InlineData("export")]
    [InlineData("before-publish")]
    public async Task Cancellation_removes_all_outputs_staging_and_work_files(string phase)
    {
        Action? cancel = null;
        await using var app = new AppTestHarness(wrapOpenedDocument: document =>
        {
            if (!document.FilePath.EndsWith("rotated.pdf", StringComparison.Ordinal)) return document;
            var proxy = DispatchProxy.Create<IPdfDocument, SignatureSecurityTests.TestProxy>();
            ((SignatureSecurityTests.TestProxy)(object)proxy).Handle = (method, args) =>
            {
                if (phase == "export" && method.Name == nameof(IPdfDocument.GetPageTextSpansAsync)) cancel!();
                if (phase == "before-publish" && method.Name == nameof(IAsyncDisposable.DisposeAsync)) return DisposeAsync();
                return method.Invoke(document, args);
            };
            async ValueTask DisposeAsync() { await document.DisposeAsync(); cancel!(); }
            return proxy;
        });
        var pdf = WritePdf(app);
        Configure(app, [pdf]);
        cancel = () => app.Model.CancelOperationCommand.Execute(null);
        if (phase == "ocr")
            app.Model.ConvertScanOcrRunner = async (_, _, _, _, _, _, ct) =>
            {
                cancel();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("Unreachable");
            };

        await app.Model.ConvertScanCommand.ExecuteAsync(null);

        Assert.Empty(app.Dialogs.Named("ShowConvertScanResult"));
        Assert.Single(app.Dialogs.Named("ShowError"));
        Assert.Equal(new[] { pdf }, Directory.GetFiles(app.Folder, "*", SearchOption.TopDirectoryOnly));
        AssertClean(app);
    }

    [Fact]
    public async Task Broken_and_encrypted_files_are_reported_while_other_files_continue()
    {
        await using var app = new AppTestHarness();
        var broken = app.PathFor("broken.pdf");
        File.WriteAllText(broken, "not a PDF");
        var locked = app.PathFor("locked.pdf");
        File.WriteAllBytes(locked, ProtectedPdfFactory.Legacy(ScanPdfFixture.Build(""), 4, "AESV2", "secret", "owner"));
        var good = WritePdf(app);
        Configure(app, [broken, locked, good]);

        await app.Model.ConvertScanCommand.ExecuteAsync(null);

        var result = Result(app);
        Assert.Equal(3, result.OutputFiles.Count);
        Assert.Contains("broken.pdf", result.Summary);
        Assert.Contains("locked.pdf", result.Summary);
        Assert.Contains("Password-protected PDFs cannot be made searchable yet. Save an unprotected copy first.", result.Summary);
        Assert.Empty(app.Dialogs.Named("PromptPassword"));
        AssertClean(app);
    }

    [Fact]
    public async Task Invisible_ocr_is_recognized_again_for_word_and_text_but_pdf_keeps_original_layer()
    {
        await using var app = new AppTestHarness();
        var old = new OcrPageResult(0, [new OcrWord("Old invisible OCR layer words", new(.1, .1, .7, .05), 50, true)]);
        var bytes = PdfOcr.AddTextLayer(ScanPdfFixture.Build(""), [old]);
        var source = WritePdf(app, bytes);
        Configure(app, [source]);

        await app.Model.ConvertScanCommand.ExecuteAsync(null);

        Assert.Equal(bytes, File.ReadAllBytes(app.PathFor("scan (OCR).pdf")));
        await using var searchable = await app.Engine.OpenAsync(app.PathFor("scan (OCR).pdf"));
        var text = await searchable.GetPageTextAsync(0);
        Assert.Contains("Old invisible", text);
        Assert.DoesNotContain("Fresh OCR", text);
        AssertOcrDocx(app.PathFor("scan.docx"));
        Assert.DoesNotContain("Old invisible", File.ReadAllText(app.PathFor("scan.txt")));
        Assert.Contains("Fresh OCR text", File.ReadAllText(app.PathFor("scan.txt")));
        Assert.Contains("Fresh OCR text", Result(app).AllText);
        AssertClean(app);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_text_is_skipped_by_default_and_all_pages_is_explicit(bool all)
    {
        await using var app = new AppTestHarness();
        var source = WritePdf(app, ScanPdfFixture.Build("BT /F1 12 Tf 10 300 Td (Genuine visible text with many characters) Tj ET"));
        var request = Configure(app, [source], all: all);
        var calls = 0;
        app.Model.ConvertScanOcrRunner = (doc, bytes, options, _, _, _, _) =>
        {
            calls++;
            Assert.Equal(new[] { 0 }, options.OnlyPages);
            return Task.FromResult(FakeResult(bytes, options));
        };

        await app.Model.ConvertScanCommand.ExecuteAsync(null);

        Assert.Equal(all ? 1 : 0, calls);
        if (all) Assert.Equal(3, Result(app).OutputFiles.Count);
        else Assert.Contains("Every page already contains text.", (string)Assert.Single(app.Dialogs.Named("ShowError")).Arguments[0]!);
        Assert.True(request.ReviewUncertainWords);
        AssertClean(app);
    }

    [Fact]
    public async Task Mixed_document_only_recognizes_scan_pages_and_preserves_real_text_in_exports()
    {
        await using var app = new AppTestHarness();
        var bytes = ScanPdfFixture.Build("", pages: 2);
        var cos = CosDocument.Load(bytes);
        var edit = new CosPageEdit(cos, cos.GetPages()[1]);
        edit.Append("BT /F1 12 Tf 10 300 Td (Genuine visible text with many characters) Tj ET");
        var update = new CosIncrementalUpdate(cos);
        edit.Commit(update);
        var file = WritePdf(app, update.Build().Bytes);
        Configure(app, [file]);
        app.Model.ConvertScanOcrRunner = (_, pdf, options, _, _, _, _) =>
        {
            Assert.Equal(OcrPageSelection.AllPages, options.Pages);
            Assert.Equal(new[] { 0 }, options.OnlyPages);
            return Task.FromResult(FakeResult(pdf, options));
        };

        await app.Model.ConvertScanCommand.ExecuteAsync(null);

        Assert.Contains("Genuine visible text", File.ReadAllText(app.PathFor("scan.txt")));
        Assert.Contains("Fresh OCR text", File.ReadAllText(app.PathFor("scan.txt")));
        AssertClean(app);
    }

    [Fact]
    public async Task Text_is_collected_without_txt_output_and_osd_decline_is_asked_once_per_run()
    {
        await using var app = new AppTestHarness();
        var a = WritePdf(app);
        var b = WritePdf(app, name: "other.pdf");
        Configure(app, [a, b], text: false, osd: false);
        app.Dialogs.Answer("Confirm", false);
        var calls = 0;
        app.Model.ConvertScanOcrRunner = (_, pdf, options, _, orient, _, _) =>
        {
            Assert.False(orient);
            calls++;
            return Task.FromResult(FakeResult(pdf, options));
        };

        await app.Model.ConvertScanCommand.ExecuteAsync(null);

        Assert.Equal(2, calls);
        Assert.Single(app.Dialogs.Named("Confirm"));
        Assert.Empty(Directory.GetFiles(app.Folder, "*.txt"));
        Assert.Contains("Fresh OCR text", Result(app).AllText);
        AssertClean(app);
    }

    [Fact]
    public async Task A_failed_move_rolls_back_previously_moved_outputs_and_preserves_competing_entry()
    {
        string? collision = null;
        await using var app = new AppTestHarness(wrapOpenedDocument: document =>
        {
            if (!document.FilePath.EndsWith("rotated.pdf", StringComparison.Ordinal)) return document;
            var proxy = DispatchProxy.Create<IPdfDocument, SignatureSecurityTests.TestProxy>();
            ((SignatureSecurityTests.TestProxy)(object)proxy).Handle = (method, args) =>
                method.Name == nameof(IAsyncDisposable.DisposeAsync) ? DisposeAsync() : method.Invoke(document, args);
            async ValueTask DisposeAsync()
            {
                await document.DisposeAsync();
                File.WriteAllText(collision!, "Competing writer's document");
            }
            return proxy;
        });
        var file = WritePdf(app);
        Configure(app, [file]);
        collision = app.PathFor("scan.docx"); // Created after UniquePath, before the second publication move.

        await app.Model.ConvertScanCommand.ExecuteAsync(null);

        Assert.Single(app.Dialogs.Named("ShowError"));
        Assert.False(File.Exists(app.PathFor("scan (OCR).pdf")));
        Assert.Equal("Competing writer's document", File.ReadAllText(collision));
        Assert.False(File.Exists(app.PathFor("scan.txt")));
        AssertClean(app);
    }

    [Fact]
    public async Task Cancellation_after_a_completed_file_keeps_only_that_files_outputs_and_summary_text()
    {
        await using var app = new AppTestHarness();
        var first = WritePdf(app);
        var second = WritePdf(app, name: "second.pdf");
        Configure(app, [first, second]);
        var calls = 0;
        app.Model.ConvertScanOcrRunner = (_, pdf, options, _, _, _, ct) =>
        {
            if (++calls == 2) app.Model.CancelOperationCommand.Execute(null);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(FakeResult(pdf, options));
        };

        await app.Model.ConvertScanCommand.ExecuteAsync(null);

        var result = Result(app);
        Assert.Equal(3, result.OutputFiles.Count);
        Assert.Contains("Stopped before the end", result.Summary);
        Assert.Contains("scan.pdf", result.AllText);
        Assert.DoesNotContain("second.pdf", result.AllText);
        Assert.False(File.Exists(app.PathFor("second (OCR).pdf")));
        AssertClean(app);
    }

    [Fact]
    public async Task Creates_selected_output_folder_and_passes_open_original_as_initial_file()
    {
        await using var app = new AppTestHarness();
        var source = WritePdf(app);
        Assert.True(await app.OpenAsync(source));
        var request = Configure(app, [source]);
        var folder = app.PathFor("new-output-folder");
        // Replace the queued request with the same concrete options and a custom output folder.
        app.Dialogs.Answer("AskConvertScan", request with { OutputFolder = folder });
        // First run uses the default source folder, second run exercises creation of a new folder.
        await app.Model.ConvertScanCommand.ExecuteAsync(null);
        await app.Model.ConvertScanCommand.ExecuteAsync(null);

        Assert.All(app.Dialogs.Named("AskConvertScan"), call => Assert.Equal(new[] { source }, Assert.IsAssignableFrom<IEnumerable<string>>(call.Arguments[0])));
        var result = Assert.IsType<ConvertScanResult>(app.Dialogs.Named("ShowConvertScanResult")[1].Arguments[0]);
        Assert.Equal(3, result.OutputFiles.Count);
        Assert.All(result.OutputFiles, path => Assert.Equal(folder, Path.GetDirectoryName(path)));
        AssertClean(app);
    }

    [Fact]
    public async Task Rotated_pdf_is_reopened_and_its_dimensions_are_used_for_export()
    {
        await using var app = new AppTestHarness();
        var file = WritePdf(app);
        Configure(app, [file]);
        app.Model.ConvertScanOcrRunner = (_, bytes, options, _, _, _, _) =>
        {
            var cos = CosDocument.Load(bytes);
            var page = cos.GetPages()[0];
            var dictionary = page.Dictionary.Clone();
            dictionary["Rotate"] = new CosInteger(90);
            var update = new CosIncrementalUpdate(cos);
            update.Replace(page.Id, dictionary);
            return Task.FromResult(FakeResult(update.Build().Bytes, options));
        };

        await app.Model.ConvertScanCommand.ExecuteAsync(null);

        using var zip = ZipFile.OpenRead(app.PathFor("scan.docx"));
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = reader.ReadToEnd();
        Assert.Contains("w:w=\"8000\"", xml); // 400 pt after /Rotate, in twentieths of a point.
        Assert.Contains("w:h=\"6000\"", xml);
        AssertClean(app);
    }

    private static ConvertScanRequest Configure(AppTestHarness app, string[] files, bool all = false, bool text = true, bool osd = true)
    {
        var folder = Directory.CreateDirectory(app.PathFor("tessdata")).FullName;
        foreach (var language in osd ? new[] { "tha", "eng", "osd" } : ["tha", "eng"])
            File.WriteAllBytes(Path.Combine(folder, language + ".traineddata"), [1]);
        app.Model.OcrDataFactory = () => new OcrLanguageData(folder);
        app.Model.ConvertScanOcrRunner = (_, pdf, options, _, orient, _, ct) =>
        {
            Assert.True(orient);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(FakeResult(pdf, options));
        };
        var request = new ConvertScanRequest(files, ["tha", "eng"], all ? OcrPageSelection.AllPages : OcrPageSelection.PagesWithoutText,
            Word: true, Text: text);
        app.Dialogs.Answer("AskConvertScan", request);
        return request;
    }

    private static OcrDocumentResult FakeResult(byte[] pdf, OcrOptions options) => new(pdf,
        options.OnlyPages!.Select(i => new OcrPageResult(i,
            [new OcrWord("Fresh OCR text", new(.1, .1, .7, .05), 95, true)])).ToArray());

    private static ConvertScanResult Result(AppTestHarness app) =>
        Assert.IsType<ConvertScanResult>(Assert.Single(app.Dialogs.Named("ShowConvertScanResult")).Arguments[0]);

    private static string WritePdf(AppTestHarness app, byte[]? bytes = null, string name = "scan.pdf")
    {
        var path = app.PathFor(name);
        File.WriteAllBytes(path, bytes ?? ScanPdfFixture.Build(""));
        return path;
    }

    private static void WriteImage(string path) => Sta.Run(() =>
    {
        var pixels = Enumerable.Repeat((byte)255, 100 * 120 * 4).ToArray();
        var image = BitmapSource.Create(100, 120, 300, 300, PixelFormats.Bgra32, null, pixels, 400);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path);
        encoder.Save(output);
        return true;
    });

    private static void AssertOcrDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = reader.ReadToEnd();
        Assert.Contains("Fresh OCR text", xml);
        Assert.DoesNotContain("<w:drawing", xml);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal));
    }

    private static void AssertClean(AppTestHarness app)
    {
        Assert.False(app.Model.IsBusy);
        Assert.False(app.Model.IsOperationRunning);
        Assert.Empty(Directory.GetFiles(app.Folder, "*.yrk-tmp", SearchOption.AllDirectories));
        var work = PdfTemporaryFiles.Shared.GetFolder("convertscan");
        Assert.Empty(Directory.GetDirectories(work));
        Assert.Empty(Directory.GetFiles(work).Where(p => Path.GetFileName(p) != ".session-lock"));
    }
}
