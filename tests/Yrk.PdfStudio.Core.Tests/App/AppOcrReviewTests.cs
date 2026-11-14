using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Ocr;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class AppOcrReviewTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Unsafe_or_failed_removal_keeps_old_layer_and_adds_corrections(bool extFont, bool failDuringRemoval)
    {
        await using var app = new AppTestHarness();
        var file = Source(app);
        var content = "BT /F1 12 Tf 3 Tr (Old hidden words) Tj ET";
        if (!extFont && !failDuringRemoval) content += " BT /F1 12 Tf 0.0 Tr (Visible) Tj ET";
        var original = ScanPdfFixture.Build(content + (extFont ? " /GS1 gs" : ""), extGStateFont: extFont);
        File.WriteAllBytes(file, original);
        Configure(app, [file], allPages: true);
        if (failDuringRemoval)
        {
            var runner = app.Model.ConvertScanOcrRunner;
            app.Model.ConvertScanOcrRunner = async (doc, bytes, options, data, orient, progress, ct) =>
            {
                var result = await runner(doc, bytes, options, data, orient, progress, ct);
                // Classification succeeded on the source; rewriting the oriented result now fails.
                return result with { Pdf = ScanPdfFixture.Build(content + " Unknown") };
            };
        }
        Review(app, model =>
        {
            model.NextWord(); model.EditText = "Corrected"; model.ConfirmEdit();
            return OcrReviewChoice.Save;
        });
        await app.Model.ConvertScanCommand.ExecuteAsync(null);
        Assert.Equal(3, Result(app).OutputFiles.Count);
        var logs = string.Join("\n", Directory.GetFiles(Yrk.PdfStudio.App.App.LogFolder, "*.log").Select(File.ReadAllText));
        Assert.Contains(failDuringRemoval ? "kept the existing layer after removal failed"
            : "kept the existing layer because removal is unsafe", logs);
        await using var output = await app.Engine.OpenAsync(app.PathFor("scan (OCR).pdf"));
        var text = await output.GetPageTextAsync(0);
        Assert.Contains("Corrected", text);
        Assert.Contains("Old hidden words", text);
        if (!extFont && !failDuringRemoval) Assert.Contains("Visible", text);
        Assert.Equal(original, File.ReadAllBytes(file));
        AssertClean(app);
    }

    [Fact]
    public void Review_dialog_constructs_without_showing_and_positions_accessible_editor_over_the_word()
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
            var document = DispatchProxy.Create<IPdfDocument, SignatureSecurityTests.TestProxy>();
            ((SignatureSecurityTests.TestProxy)(object)document).Handle = (method, _) => method.Name switch
            {
                "get_PageCount" => 1,
                "get_PageSizes" => new PdfPageSize[] { new(300, 400) },
                nameof(IPdfDocument.RenderPageAsync) => Task.FromResult(new PdfBitmap(1, 1, 4, [255, 255, 255, 255])),
                _ => throw new InvalidOperationException(method.Name),
            };
            var model = new OcrReviewViewModel([Page(0)], document, false);
            OcrReviewDialog? window = null;
            try
            {
                window = new OcrReviewDialog(model);
                var warning = Assert.IsType<TextBlock>(window.FindName("ReviewWarningText"));
                Assert.Equal("Words in orange boxes are ones the program is not sure about. Check and fix them before saving. Words without a box can be wrong too, so read the text through again.", warning.Text);
                Assert.Equal(FontWeights.SemiBold, warning.FontWeight);
                Assert.Equal(TextWrapping.Wrap, warning.TextWrapping);
                var border = AppConvertScanTests.AssertWarningBox(window, warning);
                var topPanel = Assert.IsType<StackPanel>(border.Parent);
                Assert.Equal(Dock.Top, DockPanel.GetDock(topPanel));
                Assert.Same(border, topPanel.Children[0]);
                model.NextWord();
                window.Measure(new Size(1100, 800)); window.Arrange(new Rect(0, 0, 1100, 800));
                Assert.False(window.IsVisible);
                Assert.True(((Button)window.FindName("SaveButton")).IsEnabled);
                Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("SkipButton")).Visibility);
                var canvas = (Canvas)window.FindName("WordCanvas");
                var editor = Assert.Single(canvas.Children.OfType<TextBox>());
                Assert.Equal(30, Canvas.GetLeft(editor));
                Assert.Equal(60, Canvas.GetTop(editor));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(editor)));
                Assert.All(Descendants(window).OfType<Button>(), button => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));
                editor.Text = "Corrected";
                model.ConfirmEdit();
                Assert.Equal("Corrected", model.EditsForPage(0)[1]);
                Assert.Equal(1, model.Remaining);
                model.CancelEdit();
                Assert.Empty(canvas.Children.OfType<TextBox>());
                window.Close();
                Assert.Equal(OcrReviewChoice.CancelFile, window.Choice);
            }
            finally
            {
                window?.Close();
                model.DisposeAsync().AsTask().GetAwaiter().GetResult();
                application.Shutdown(); Reset();
            }
            return true;
        });

        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
    }

    [Fact]
    public async Task Corrected_and_deleted_words_are_used_in_docx_text_and_pdf_by_original_index()
    {
        await using var app = new AppTestHarness();
        Configure(app, [Source(app)]);
        Review(app, model =>
        {
            Assert.NotNull(model.Image);
            Assert.Equal(2, model.Remaining);
            Assert.Equal(1, model.Words[0].WordIndex); // The raw garbage word was filtered out.
            model.NextWord();
            model.EditText = "Corrected";
            model.ConfirmEdit();
            Assert.Equal(2, model.SelectedWord!.WordIndex);
            model.EditText = string.Empty;
            model.ConfirmEdit();
            Assert.Equal(0, model.Remaining);
            return OcrReviewChoice.Save;
        });

        await app.Model.ConvertScanCommand.ExecuteAsync(null);

        var result = Result(app);
        Assert.Equal(3, result.OutputFiles.Count);
        await AssertExports(app, "scan", "Corrected", "mistaken", "speck");
        Assert.Contains("Corrected", result.AllText);
        Assert.DoesNotContain("speck", result.AllText);
        AssertClean(app);
    }

    [Fact]
    public async Task Edited_words_replace_an_existing_invisible_layer_without_changing_source_or_picture()
    {
        await using var app = new AppTestHarness();
        var source = Source(app);
        var original = PdfOcr.AddTextLayer(File.ReadAllBytes(source),
            [new OcrPageResult(0, [new OcrWord("Old invisible OCR words", new(.1, .1, .6, .05), 50, true)])]);
        File.WriteAllBytes(source, original);
        Configure(app, [source]);
        Review(app, model =>
        {
            model.NextWord(); model.EditText = "Corrected"; model.ConfirmEdit(); model.DeleteWord();
            return OcrReviewChoice.Save;
        });
        await app.Model.ConvertScanCommand.ExecuteAsync(null);
        await AssertExports(app, "scan", "Corrected", "mistaken", "speck", "Old invisible");
        await using var before = await app.Engine.OpenAsync(source);
        await using var after = await app.Engine.OpenAsync(app.PathFor("scan (OCR).pdf"));
        Assert.Equal((await before.RenderPageAsync(0, 300, 400)).Pixels, (await after.RenderPageAsync(0, 300, 400)).Pixels);
        Assert.Equal(original, File.ReadAllBytes(source));
        AssertClean(app);
    }

    [Fact]
    public async Task Saving_without_edits_matches_disabled_review_for_pdf_bytes_text_and_every_docx_part()
    {
        await using var app = new AppTestHarness();
        var file = Source(app);
        var baseline = app.PathFor("baseline");
        var reviewed = app.PathFor("reviewed");
        Configure(app, [file], review: false, folder: baseline);
        Configure(app, [file], folder: reviewed);
        Review(app, _ => OcrReviewChoice.Save);

        await app.Model.ConvertScanCommand.ExecuteAsync(null);
        await app.Model.ConvertScanCommand.ExecuteAsync(null);

        Assert.Single(app.Dialogs.Named("AskOcrReview"));
        foreach (var name in new[] { "scan (OCR).pdf", "scan.txt" })
            Assert.Equal(File.ReadAllBytes(Path.Combine(baseline, name)), File.ReadAllBytes(Path.Combine(reviewed, name)));
        using var a = ZipFile.OpenRead(Path.Combine(baseline, "scan.docx"));
        using var b = ZipFile.OpenRead(Path.Combine(reviewed, "scan.docx"));
        Assert.Equal(a.Entries.Select(e => e.FullName), b.Entries.Select(e => e.FullName));
        foreach (var entry in a.Entries)
        {
            using var first = entry.Open();
            using var second = b.GetEntry(entry.FullName)!.Open();
            using var x = new MemoryStream();
            using var y = new MemoryStream();
            await first.CopyToAsync(x);
            await second.CopyToAsync(y);
            Assert.Equal(x.ToArray(), y.ToArray());
        }
        var results = app.Dialogs.Named("ShowConvertScanResult");
        Assert.Equal(((ConvertScanResult)results[0].Arguments[0]!).AllText, ((ConvertScanResult)results[1].Arguments[0]!).AllText);
        AssertClean(app);
    }

    [Fact]
    public async Task Cancel_file_discards_all_edits_without_outputs_and_continues_with_the_next_file()
    {
        await using var app = new AppTestHarness();
        var first = Source(app);
        var second = Source(app, "second.pdf");
        Configure(app, [first, second]);
        Review(app, model =>
        {
            model.NextWord(); model.EditText = "Discarded"; model.ConfirmEdit();
            return OcrReviewChoice.CancelFile;
        });
        Review(app, _ => OcrReviewChoice.Save);

        await app.Model.ConvertScanCommand.ExecuteAsync(null);

        var result = Result(app);
        Assert.Equal(3, result.OutputFiles.Count);
        Assert.All(result.OutputFiles, file => Assert.StartsWith("second", Path.GetFileName(file)));
        Assert.Contains("scan.pdf: This file was cancelled by the user.", result.Summary);
        Assert.DoesNotContain("Not done", result.Summary);
        Assert.DoesNotContain("scan.pdf", result.AllText);
        Assert.Empty(app.Dialogs.Named("ShowError"));
        Assert.False(File.Exists(app.PathFor("scan (OCR).pdf")));
        Assert.False(File.Exists(app.PathFor("scan.docx")));
        Assert.False(File.Exists(app.PathFor("scan.txt")));
        Assert.Equal(2, app.Dialogs.Named("AskOcrReview").Length);
        AssertClean(app);
    }

    [Fact]
    public async Task Cancelling_the_only_file_is_reported_as_information_and_does_not_create_output_folder()
    {
        await using var app = new AppTestHarness();
        var folder = app.PathFor("unused");
        Configure(app, [Source(app)], folder: folder);
        Review(app, _ => OcrReviewChoice.CancelFile);
        await app.Model.ConvertScanCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(folder));
        Assert.Single(app.Dialogs.Named("ShowInfo"));
        Assert.Empty(app.Dialogs.Named("ShowError"));
        AssertClean(app);
    }

    [Fact]
    public async Task Skip_remaining_saves_current_edits_and_only_suppresses_reviews_for_this_run()
    {
        await using var app = new AppTestHarness();
        var first = Source(app);
        var second = Source(app, "second.pdf");
        Configure(app, [first, second]);
        Review(app, model =>
        {
            Assert.True(model.HasMoreFiles);
            model.NextWord(); model.EditText = "Corrected"; model.ConfirmEdit();
            return OcrReviewChoice.SkipRemaining;
        });
        await app.Model.ConvertScanCommand.ExecuteAsync(null);
        Assert.Single(app.Dialogs.Named("AskOcrReview"));
        await AssertExports(app, "scan", "Corrected", "mistaken");
        await AssertExports(app, "second", "mistaken", "Corrected");
        Configure(app, [first]);
        Review(app, model => { Assert.False(model.HasMoreFiles); return OcrReviewChoice.Save; });
        await app.Model.ConvertScanCommand.ExecuteAsync(null);
        Assert.Equal(2, app.Dialogs.Named("AskOcrReview").Length);
        AssertClean(app);
    }

    [Fact]
    public async Task Skip_remaining_also_applies_when_the_current_file_later_fails_to_export()
    {
        var rotatedOpened = 0;
        await using var app = new AppTestHarness(wrapOpenedDocument: document =>
        {
            if (!document.FilePath.EndsWith("rotated.pdf", StringComparison.Ordinal) || ++rotatedOpened != 1) return document;
            var proxy = DispatchProxy.Create<IPdfDocument, SignatureSecurityTests.TestProxy>();
            ((SignatureSecurityTests.TestProxy)(object)proxy).Handle = (method, args) =>
                method.Name == nameof(IPdfDocument.GetPageTextSpansAsync) ? throw new IOException("Simulated export failure")
                    : method.Invoke(document, args);
            return proxy;
        });
        Configure(app, [Source(app), Source(app, "second.pdf")]);
        Review(app, _ => OcrReviewChoice.SkipRemaining);
        await app.Model.ConvertScanCommand.ExecuteAsync(null);
        Assert.Single(app.Dialogs.Named("AskOcrReview"));
        Assert.Equal(3, Result(app).OutputFiles.Count);
        Assert.All(Result(app).OutputFiles, file => Assert.StartsWith("second", Path.GetFileName(file)));
        AssertClean(app);
    }

    [Fact]
    public async Task Confident_words_and_low_confidence_garbage_do_not_open_review()
    {
        await using var app = new AppTestHarness();
        Configure(app, [Source(app)], confident: true);
        await app.Model.ConvertScanCommand.ExecuteAsync(null);
        Assert.Empty(app.Dialogs.Named("AskOcrReview"));
        Assert.Equal(3, Result(app).OutputFiles.Count);
        AssertClean(app);
    }

    [Fact]
    public async Task View_model_next_previous_escape_enter_delete_and_remaining_use_its_own_api()
    {
        await using var app = new AppTestHarness();
        var file = Source(app, pages: 3);
        await using var document = await app.Engine.OpenAsync(file);
        await using var model = new OcrReviewViewModel([Page(2), Page(0)], document, false);
        Assert.Equal([0, 2], model.PageIndices);
        Assert.Equal(4, model.Remaining);
        model.NextWord();
        Assert.Equal(0, model.PageIndex);
        model.EditText = "Discarded";
        model.CancelEdit(); // Esc
        Assert.False(model.IsEditing);
        Assert.Equal(4, model.Remaining);
        Assert.Empty(model.EditsForPage(0));
        model.SelectWord(model.Words[0]);
        Assert.Equal("mistaken", model.EditText);
        model.ConfirmEdit(); // Enter confirms the original text too.
        Assert.Equal(3, model.Remaining);
        Assert.True(model.IsConfirmed(model.Words[0]));
        Assert.Equal(2, model.SelectedWord!.WordIndex);
        model.NextWord(-1); // Shift+Tab can revisit a confirmed word.
        Assert.Equal(1, model.SelectedWord!.WordIndex);
        model.EditText = "Corrected";
        model.ConfirmEdit();
        Assert.Equal(3, model.Remaining);
        model.DeleteWord();
        Assert.Equal(2, model.Remaining);
        Assert.Equal(2, model.PageIndex);
        Assert.True(model.EditsForPage(0).ContainsKey(2));
        Assert.Null(model.EditsForPage(0)[2]);
        model.ChangeZoom(100);
        Assert.Equal(4, model.Zoom);
        await model.RenderAsync();
        Assert.NotNull(model.Image);
        Assert.Equal((int)Math.Ceiling(model.PageWidth * 1.5), model.Image.PixelWidth);
        model.MovePage(-1);
        model.MovePage(1);
        model.NextWord();
        Assert.Equal(2, model.SelectedWord!.PageIndex);
        Assert.Equal(1, model.SelectedWord.WordIndex);
        model.MovePage(-1);
        model.NextWord(-1);
        Assert.Equal(0, model.SelectedWord!.PageIndex);
        Assert.Equal(2, model.SelectedWord.WordIndex);
        AssertClean(app);
    }

    [Fact]
    public async Task Review_renders_the_rotated_result_and_keeps_it_open_until_all_renders_finish()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = false;
        var renderCalls = 0;
        await using var app = new AppTestHarness(wrapOpenedDocument: document =>
        {
            if (!document.FilePath.EndsWith("rotated.pdf", StringComparison.Ordinal)) return document;
            var proxy = DispatchProxy.Create<IPdfDocument, SignatureSecurityTests.TestProxy>();
            ((SignatureSecurityTests.TestProxy)(object)proxy).Handle = (method, args) =>
            {
                if (method.Name == nameof(IPdfDocument.RenderPageAsync)) return Render();
                if (method.Name == nameof(IAsyncDisposable.DisposeAsync)) { disposed = true; return document.DisposeAsync(); }
                return method.Invoke(document, args);
                async Task<PdfBitmap> Render()
                {
                    Assert.Equal(400, document.PageSizes[0].Width);
                    if (++renderCalls > 1) { started.TrySetResult(); await release.Task; Assert.False(disposed); }
                    return await document.RenderPageAsync((int)args![0]!, (int)args[1]!, (int)args[2]!);
                }
            };
            return proxy;
        });
        Configure(app, [Source(app)], rotate: true);
        Review(app, model => { _ = model.RenderAsync(); return OcrReviewChoice.CancelFile; });
        var operation = app.Model.ConvertScanCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try { Assert.False(disposed); Assert.False(operation.IsCompleted); }
        finally { release.TrySetResult(); }
        await operation;
        Assert.True(disposed);
        AssertClean(app);
    }

    private static void Configure(AppTestHarness app, string[] files, bool review = true, string? folder = null,
        bool confident = false, bool rotate = false, bool allPages = false)
    {
        var data = Directory.CreateDirectory(app.PathFor("tessdata")).FullName;
        foreach (var language in new[] { "tha", "eng", "osd" }) File.WriteAllBytes(Path.Combine(data, language + ".traineddata"), [1]);
        app.Model.OcrDataFactory = () => new OcrLanguageData(data);
        app.Model.ConvertScanOcrRunner = (_, bytes, options, _, _, _, _) =>
        {
            if (rotate)
            {
                var source = CosDocument.Load(bytes);
                var page = source.GetPages()[0];
                var dictionary = page.Dictionary.Clone(); dictionary["Rotate"] = new CosInteger(90);
                var update = new CosIncrementalUpdate(source); update.Replace(page.Id, dictionary);
                bytes = update.Build().Bytes;
            }
            return Task.FromResult(new OcrDocumentResult(bytes, options.OnlyPages!.Select(i => Page(i, confident)).ToArray()));
        };
        app.Dialogs.Answer("AskConvertScan", new ConvertScanRequest(files, ["tha", "eng"],
            Pages: allPages ? OcrPageSelection.AllPages : OcrPageSelection.PagesWithoutText, Word: true,
            Text: true, ReviewUncertainWords: review, OutputFolder: folder));
    }

    private static OcrPageResult Page(int index, bool confident = false) => new(index,
    [
        new OcrWord("x", new(.1, .05, .05, .02), 10, true, 0, .02, 30),
        new OcrWord("mistaken", new(.1, .15, .2, .04), confident ? OcrTuning.ReviewConfidence : 30, true, 1, .04, 90),
        new OcrWord("speck", new(.4, .15, .15, .04), confident ? 95 : 40, true, 1, .04, 90),
        new OcrWord("retained", new(.65, .15, .2, .04), 95, false, 1, .04, 90),
    ]);
    private static string Source(AppTestHarness app, string name = "scan.pdf", int pages = 1)
    {
        var file = app.PathFor(name);
        File.WriteAllBytes(file, ScanPdfFixture.Build("", pages: pages));
        return file;
    }
    private static void Review(AppTestHarness app, Func<OcrReviewViewModel, OcrReviewChoice> callback) =>
        app.Dialogs.Answer("AskOcrReview", new Func<object?[], object?>(args => callback((OcrReviewViewModel)args[0]!)));
    private static ConvertScanResult Result(AppTestHarness app) =>
        (ConvertScanResult)Assert.Single(app.Dialogs.Named("ShowConvertScanResult")).Arguments[0]!;
    private static async Task AssertExports(AppTestHarness app, string name, string expected, params string[] absent)
    {
        using var zip = ZipFile.OpenRead(app.PathFor(name + ".docx"));
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = await reader.ReadToEndAsync();
        var text = File.ReadAllText(app.PathFor(name + ".txt"));
        await using var pdf = await app.Engine.OpenAsync(app.PathFor(name + " (OCR).pdf"));
        var pdfText = await pdf.GetPageTextAsync(0);
        foreach (var output in new[] { xml, text, pdfText })
        {
            Assert.Contains(expected, output);
            foreach (var word in absent) Assert.DoesNotContain(word, output);
        }
    }
    private static void AssertClean(AppTestHarness app)
    {
        Assert.Empty(Directory.GetFiles(app.Folder, "*.yrk-tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(PdfTemporaryFiles.Shared.GetFolder("convertscan")));
        Assert.False(app.Model.IsBusy);
    }
}
