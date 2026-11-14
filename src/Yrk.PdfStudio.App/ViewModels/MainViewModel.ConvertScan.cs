using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Ocr;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    internal Func<IPdfDocument, byte[], OcrOptions, OcrLanguageData, bool, IProgress<OcrProgress>?, CancellationToken,
        Task<OcrDocumentResult>> ConvertScanOcrRunner = async (document, pdf, options, data, orient, progress, ct) =>
            orient ? await PdfOcr.RecognizeOrientedPagesAsync(document, pdf, options, data, progress, ct)
                : new OcrDocumentResult(pdf, await PdfOcr.RecognizePagesAsync(document, options, data, progress, ct));

    private IAsyncRelayCommand? _convertScanCommand;
    public IAsyncRelayCommand ConvertScanCommand => _convertScanCommand ??= new AsyncRelayCommand(ConvertScanAsync);

    private async Task ConvertScanAsync()
    {
        if (IsBusy) return;
        var initial = _document is { } open && _convertedFrom is null ? new[] { open.FilePath } : [];
        if (_dialogs.AskConvertScan(initial) is not { } request) return;
        if (request.Files.Count == 0 || request.Languages.Count == 0 || !(request.SearchablePdf || request.Word || request.Text))
        {
            _dialogs.ShowError(Loc.T("Choose at least one file, language and output format."));
            return;
        }
        var data = OcrDataFactory();
        if (!await EnsureLanguageDataAsync(data, request.Languages)) return;

        IsBusy = true;
        var operation = new CancellationTokenSource();
        _operation = operation;
        OnPropertyChanged(nameof(IsOperationRunning));
        var outputs = new List<string>();
        var texts = new List<string>();
        var failed = new List<(string File, string Reason)>();
        var succeeded = 0;
        var cancelled = false;
        try
        {
            // Called once per run: declining OSD still permits recognition without rotation.
            var orient = await data.EnsureOrientationDataAsync(() => Task.FromResult(_dialogs.Confirm(Loc.T(
                "Auto rotation needs orientation data.\n\nDownload it now from github.com/tesseract-ocr (about 10 MB)? It is downloaded only once."))), operation.Token);
            for (var i = 0; i < request.Files.Count; i++)
            {
                operation.Token.ThrowIfCancellationRequested();
                var file = request.Files[i];
                var number = i + 1;
                ReportOperation(Loc.F("File {0} of {1} · Page {2} of {3}", number, request.Files.Count, 0, 0));
                var progress = new Progress<OcrProgress>(p =>
                {
                    if (_operation == operation)
                        ReportOperation(Loc.F("File {0} of {1} · Page {2} of {3}", number, request.Files.Count,
                            Math.Min(p.Done + 1, p.Total), p.Total));
                });
                try
                {
                    var converted = await ConvertScanFileAsync(file, request, data, orient, progress, operation.Token);
                    outputs.AddRange(converted.Files);
                    texts.Add(Path.GetFileName(file) + Environment.NewLine + converted.Text);
                    succeeded++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException
                    or InvalidOperationException or NotSupportedException or ArgumentException)
                {
                    failed.Add((file, Loc.T(ex.Message)));
                }
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            failed.Add((string.Empty, Loc.F("The language data could not be downloaded. Check the internet connection and try again.\n\n{0}", ex.Message)));
        }
        finally
        {
            operation.Dispose();
            _operation = null;
            OnPropertyChanged(nameof(IsOperationRunning));
            OperationText = string.Empty;
            IsBusy = false;
        }

        StatusMessage = Loc.F("Converted {0} of {1} files", succeeded, request.Files.Count);
        var summary = StatusMessage;
        if (cancelled) summary += "\n" + Loc.T("Stopped before the end, as you asked.");
        if (failed.Count > 0)
        {
            summary += "\n\n" + Loc.F("Not done ({0}):", failed.Count) + "\n"
                + string.Join("\n", failed.Take(15).Select(f => $"• {Path.GetFileName(f.File)}: {f.Reason}"));
            if (failed.Count > 15) summary += "\n" + Loc.F("…and {0} more", failed.Count - 15);
        }
        if (succeeded == 0) _dialogs.ShowError(summary);
        else _dialogs.ShowConvertScanResult(new ConvertScanResult(summary, outputs, string.Join("\n\n", texts)));
    }

    private async Task<(IReadOnlyList<string> Files, string Text)> ConvertScanFileAsync(string file, ConvertScanRequest request,
        OcrLanguageData data, bool orient, IProgress<OcrProgress> progress, CancellationToken ct)
    {
        var work = Path.Combine(PdfTemporaryFiles.Shared.GetFolder("convertscan"), Guid.NewGuid().ToString("N"));
        var staging = new List<string>();
        var publish = new List<(string Stage, string Target)>();
        var moved = new List<string>();
        Directory.CreateDirectory(work);
        try
        {
            var input = file;
            if (!Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var image = await Task.Run(() => ImageInputLoader.Load(file), ct);
                input = Path.Combine(work, "image.pdf");
                await _engine.CreateFromImagesAsync([image], new ImagesToPdfOptions(PdfPageSizeMode.MatchImage, 0, 300), input, ct);
            }
            var bytes = await File.ReadAllBytesAsync(input, ct);
            if (PdfFiles.IsEncrypted(bytes))
                throw new PdfException(Loc.T("Password-protected PDFs cannot be made searchable yet. Save an unprotected copy first."));

            OcrDocumentResult result;
            IReadOnlyList<OcrScanPage> scanPages;
            await using (var document = await _engine.OpenAsync(input, cancellationToken: ct))
            {
                scanPages = await OcrScanPages.FindAsync(document, bytes, ct);
                var only = scanPages.Where(p => request.Pages == OcrPageSelection.AllPages || p.IsScan).Select(p => p.PageIndex).ToArray();
                if (only.Length == 0) throw new ConvertScanSkipException(Loc.T("Every page already contains text."));
                var options = new OcrOptions(request.Languages, OcrPageSelection.AllPages, OnlyPages: only);
                result = await ConvertScanOcrRunner(document, bytes, options, data, orient, progress, ct);
            }

            // Step 4 insertion point: review result.Pages here when request.ReviewUncertainWords is true,
            // before any format is generated. Step 3 only retains the preference in the request.
            var rotatedPath = Path.Combine(work, "rotated.pdf");
            await File.WriteAllBytesAsync(rotatedPath, result.Pdf, ct);
            string allText;
            await using (var rotated = await _engine.OpenAsync(rotatedPath, cancellationToken: ct))
            {
                var ocrPages = result.Pages.ToDictionary(p => p.PageIndex, p =>
                    OcrPageText.ToPageText(p, rotated.PageSizes[p.PageIndex].Width, rotated.PageSizes[p.PageIndex].Height));
                var allPages = Enumerable.Range(0, rotated.PageCount).ToArray();
                var folder = string.IsNullOrWhiteSpace(request.OutputFolder) ? Path.GetDirectoryName(Path.GetFullPath(file))! : request.OutputFolder;
                Directory.CreateDirectory(folder);
                var name = Path.GetFileNameWithoutExtension(file);
                string Stage(string? extension)
                {
                    var path = Path.Combine(folder, "~" + Guid.NewGuid().ToString("N") + ".yrk-tmp");
                    staging.Add(path);
                    if (extension is not null)
                    {
                        const string ocrSuffix = " (OCR)";
                        var suffix = extension == ".pdf" && Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? ocrSuffix : string.Empty;
                        publish.Add((path, UniquePath(Path.Combine(folder, name + suffix + extension), file)));
                    }
                    return path;
                }
                if (request.SearchablePdf)
                {
                    var existing = scanPages.Where(p => p.HasOcrLayer).Select(p => p.PageIndex).ToHashSet();
                    var searchable = await Task.Run(() => PdfOcr.AddTextLayer(result.Pdf,
                        result.Pages.Where(p => !existing.Contains(p.PageIndex)).ToArray()), ct);
                    await File.WriteAllBytesAsync(Stage(".pdf"), searchable, ct);
                }
                var exportProgress = new ConvertScanExportProgress();
                if (request.Word)
                    await PdfExporter.ExportAsync(rotated, new ExportSettings(ExportFormat.Word, allPages, OcrPages: ocrPages),
                        Stage(".docx"), exportProgress, ct);
                // Always produce text using the same export path, even if only the clipboard needs it.
                var textStage = Stage(request.Text ? ".txt" : null);
                await PdfExporter.ExportAsync(rotated, new ExportSettings(ExportFormat.Text, allPages, OcrPages: ocrPages),
                    textStage, exportProgress, ct);
                allText = await File.ReadAllTextAsync(textStage, ct);
            }

            ct.ThrowIfCancellationRequested();
            // Publication is deliberately non-cancellable. File.Move never overwrites, even after a race.
            try
            {
                foreach (var output in publish)
                {
                    File.Move(output.Stage, output.Target, overwrite: false);
                    moved.Add(output.Target);
                }
            }
            catch
            {
                DeleteConvertScanFiles(moved);
                throw;
            }
            return (moved, allText);
        }
        finally
        {
            try
            {
                DeleteConvertScanFiles(staging);
            }
            finally { Directory.Delete(work, recursive: true); }
        }
    }

    private sealed class ConvertScanSkipException(string message) : InvalidOperationException(message);

    private static void DeleteConvertScanFiles(IEnumerable<string> paths)
    {
        Exception? failure = null;
        foreach (var path in paths)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure ??= ex; }
        }
        if (failure is not null) throw new IOException(failure.Message, failure);
    }

    private sealed class ConvertScanExportProgress : IProgress<int>
    {
        public void Report(int value) { }
    }
}
