using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Security;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>Export to Word, Excel, PNG/JPG images or plain text (Tools ▸ Export…).</summary>
public sealed partial class MainViewModel
{
    private IAsyncRelayCommand? _exportCommand;

    public IAsyncRelayCommand ExportCommand => _exportCommand ??= new AsyncRelayCommand(() => ExportAsync());

    /// <param name="format">Chosen in the dialog to begin with (from the start page's "PDF to Word" and the like).</param>
    public async Task ExportAsync(ExportFormat? format = null)
    {
        if (_document is null || IsBusy)
        {
            return;
        }

        CommitEditsRequested?.Invoke(this, EventArgs.Empty);
        if (_dialogs.AskExport(_document.PageCount, _currentPageIndex, format: format) is not { } settings)
        {
            return;
        }

        // Taking the text out is "copying" in PDF terms; images are what printing would give.
        var needed = settings.IsPicture ? PdfPermissions.Print : PdfPermissions.Copy;
        if (!await EnsureAllowedAsync(needed, settings.IsPicture ? "export it as images" : "export its text"))
        {
            return;
        }

        if (HasUnsavedOverlays
            && _dialogs.AskYesNo("Signatures, added text, form entries and text edits are exported once they are saved.\n\nSave the document before exporting?"))
        {
            await SaveCommand.ExecuteAsync(null);
        }

        if (_document is not { } document)
        {
            return;
        }

        var source = _convertedFrom ?? document.FilePath;
        var name = Path.GetFileNameWithoutExtension(source) + settings.Extension;
        if (_dialogs.PickSaveFile(name, Path.GetDirectoryName(source), "Export", settings.Filter, settings.Extension) is not { } path)
        {
            return;
        }

        if (settings.IsImage && settings.Pages.Count > 1)
        {
            var existing = PdfExporter.ImagePaths(path, settings.Pages, document.PageCount).Count(File.Exists);
            if (existing > 0 && !_dialogs.Confirm(Loc.F("{0} of the image files already exist in this folder and will be replaced.", existing)))
            {
                return;
            }
        }

        IsBusy = true;
        _operation = new CancellationTokenSource();
        OnPropertyChanged(nameof(IsOperationRunning));
        var total = settings.Pages.Count;
        var progress = new Progress<int>(done => ReportOperation(Loc.F("Exporting page {0} of {1}…", done + 1, total)));
        ReportOperation(Loc.F("Exporting page {0} of {1}…", 1, total));
        try
        {
            var result = await PdfExporter.ExportAsync(document, settings, path, progress, _operation.Token);
            StatusMessage = result.Files.Count == 1
                ? Loc.F("Exported to {0}", Path.GetFileName(result.Files[0]))
                : Loc.F("Exported {0} images to {1}", result.Files.Count, Path.GetDirectoryName(path) ?? string.Empty);
            if (result.PicturePages > 0)
            {
                _dialogs.ShowInfo(Loc.F(
                    "{0} of the pages have no text (scans), so they were added to the Word file as pictures.\n\nTo get editable text, use Recognize text (OCR) first, then export again.",
                    result.PicturePages));
            }

            ShowExported(result.Files, settings.IsImage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Export cancelled";
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            StatusMessage = string.Empty;
            _dialogs.ShowError(Loc.F("Couldn't export.\n\n{0}", ex.Message));
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            OnPropertyChanged(nameof(IsOperationRunning));
            OperationText = string.Empty;
            IsBusy = false;
        }
    }

    /// <summary>Shows the result in Explorer (images) or opens it in its app (Word, Excel, Notepad), if the user wants.</summary>
    private void ShowExported(IReadOnlyList<string> files, bool images)
    {
        if (files.Count == 0 || !_dialogs.AskYesNo(images ? "Export finished. Show the images in their folder?" : "Export finished. Open the file now?"))
        {
            return;
        }

        try
        {
            if (images)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{files[0]}\"") { UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo(files[0]) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _dialogs.ShowError(Loc.F("Couldn't open the file.\n\n{0}", ex.Message));
        }
    }
}
