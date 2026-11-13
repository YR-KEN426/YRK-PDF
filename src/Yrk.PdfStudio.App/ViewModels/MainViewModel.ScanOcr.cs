using System.IO;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Ocr;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    private async Task<string?> OcrScannedFileAsync(string output, IReadOnlyList<string> languages)
    {
        string? temp = null;
        string? error = null;
        CancellationTokenSource? operation = null;
        var reporting = false;
        var keepTemp = false;
        try
        {
            var outputStamp = ScanOutputStamp.Read(output) ?? throw new FileNotFoundException(null, output);
            var data = OcrDataFactory();
            if (!await EnsureLanguageDataAsync(data, languages))
            {
                StatusMessage = Loc.T("The PDF was saved without searchable text.");
                return StatusMessage;
            }

            IsBusy = true;
            operation = new CancellationTokenSource();
            _operation = operation;
            OnPropertyChanged(nameof(IsOperationRunning));
            ReportOperation("Recognizing text…");
            var token = operation.Token;
            reporting = true;
            var progress = new Progress<OcrProgress>(p =>
            {
                if (!reporting || token.IsCancellationRequested) return;
                if (p.Done < p.Total)
                {
                    ReportOperation(Loc.F("Recognizing text: page {0} ({1} of {2})…", p.PageIndex + 1, p.Done + 1, p.Total));
                }
                else
                {
                    ReportOperation("Adding the text layer…");
                }
            });

            OcrSummary summary;
            await using (var doc = await _engine.OpenAsync(output))
            {
                var bytes = await File.ReadAllBytesAsync(output, token);
                summary = await OcrRunner(doc, bytes, new OcrOptions(languages, OcrPageSelection.AllPages), data, progress, token);
            }

            reporting = false;
            token.ThrowIfCancellationRequested();
            if (summary.PagesRecognised > 0)
            {
                // PDFium must release the original before it can be replaced.
                temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $"~{Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.yrk-writing");
                await PdfTemporaryFiles.WriteDurableAsync(temp, summary.Output, token);
                token.ThrowIfCancellationRequested();
                keepTemp = true;
                PublishScanOutput(temp, output, outputStamp);
                keepTemp = false;
            }

            StatusMessage = summary.PagesRecognised == 1
                ? Loc.F("Recognized {0:N0} words on 1 page", summary.Words)
                : Loc.F("Recognized {0:N0} words on {1} pages", summary.Words, summary.PagesRecognised);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.T("Text recognition was cancelled. The PDF was saved without searchable text.");
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            App.LogMessage("Document transform failed: " + ex);
            error = Loc.F("Text recognition failed. The PDF was saved without searchable text.\n\n{0}", ex.Message);
            StatusMessage = Loc.T("The PDF was saved without searchable text.");
        }
        finally
        {
            reporting = false;
            if (temp is not null && !keepTemp) TryDelete(temp);
            if (operation is not null)
            {
                operation.Dispose();
                _operation = null;
                OnPropertyChanged(nameof(IsOperationRunning));
                OperationText = string.Empty;
                IsBusy = false;
            }

            // Release the progress UI before showing the modal error dialog.
            if (error is not null) _dialogs.ShowError(error);
        }

        return StatusMessage;
    }
}
