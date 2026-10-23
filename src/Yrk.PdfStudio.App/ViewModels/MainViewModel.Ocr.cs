using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.Core.Pdf.Ocr;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>Text recognition (OCR) for scanned documents.</summary>
public sealed partial class MainViewModel
{
    private IAsyncRelayCommand? _recognizeTextCommand;

    public IAsyncRelayCommand RecognizeTextCommand => _recognizeTextCommand ??= new AsyncRelayCommand(RecognizeTextAsync);

    private async Task RecognizeTextAsync()
    {
        if (_document is null || IsBusy || !await EnsureAllowedAsync(Core.Pdf.Security.PdfPermissions.Modify, "change it"))
        {
            return;
        }

        if (_document is not { } document || _dialogs.AskOcr() is not { } options)
        {
            return;
        }

        var data = new OcrLanguageData();
        if (!await EnsureLanguageDataAsync(data, options.Languages))
        {
            return;
        }

        var targetPath = _dialogs.PickSavePdfFile(
            Path.GetFileNameWithoutExtension(document.FilePath) + " (searchable).pdf", Path.GetDirectoryName(document.FilePath), "Save the searchable document");
        if (targetPath is null)
        {
            return;
        }

        OcrSummary? summary = null;
        var saved = await TransformToFileAsync(targetPath, "Recognizing text…", async (doc, input, cancellationToken) =>
        {
            var progress = new Progress<OcrProgress>(p =>
            {
                if (p.Done < p.Total)
                {
                    ReportOperation(Loc.F("Recognizing text: page {0} ({1} of {2})…", p.PageIndex + 1, p.Done + 1, p.Total));
                }
                else
                {
                    ReportOperation("Adding the text layer…");
                }
            });
            summary = await PdfOcr.RecognizeAsync(doc, input, options, data, progress, cancellationToken);
            if (summary.PagesRecognised == 0)
            {
                _dialogs.ShowInfo("Every page already contains text, so nothing was changed.\n\nChoose \"All pages\" to recognize them anyway.");
                return null;
            }

            return summary.Output;
        });

        if (saved is not null && summary is not null)
        {
            StatusMessage = summary.PagesRecognised == 1
                ? Loc.F("Recognized {0:N0} words on 1 page", summary.Words)
                : Loc.F("Recognized {0:N0} words on {1} pages", summary.Words, summary.PagesRecognised);
        }
    }

    /// <summary>Downloads missing language files after asking. Returns false if they are still missing.</summary>
    private async Task<bool> EnsureLanguageDataAsync(OcrLanguageData data, IReadOnlyList<string> languages)
    {
        var missing = languages.Where(l => !data.IsInstalled(l)).ToList();
        if (missing.Count == 0)
        {
            return true;
        }

        var names = string.Join(", ", missing.Select(m => OcrLanguageData.Languages.First(l => l.Code == m).Name));
        if (!_dialogs.Confirm(Loc.F("Text recognition needs language data for: {0}.\n\nDownload it now from github.com/tesseract-ocr (1–5 MB per language)? It is downloaded only once.", names)))
        {
            return false;
        }

        IsBusy = true;
        try
        {
            foreach (var language in missing)
            {
                var name = OcrLanguageData.Languages.First(l => l.Code == language).Name;
                var progress = new Progress<double>(p => StatusMessage = Loc.F("Downloading {0} language data… {1:P0}", name, p));
                await data.DownloadAsync(language, progress);
            }

            StatusMessage = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            StatusMessage = string.Empty;
            _dialogs.ShowError(Loc.F("The language data could not be downloaded. Check the internet connection and try again.\n\n{0}", ex.Message));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
