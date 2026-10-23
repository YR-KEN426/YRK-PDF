using System.IO;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Archive;
using Yrk.PdfStudio.Core.Pdf.Security;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>Save a copy as PDF/A-2b, the ISO standard for keeping documents for the long term.</summary>
public sealed partial class MainViewModel
{
    private IAsyncRelayCommand? _saveAsPdfACommand;

    public IAsyncRelayCommand SaveAsPdfACommand => _saveAsPdfACommand ??= new AsyncRelayCommand(SaveAsPdfAAsync);

    /// <summary>The translated list of what a conversion changed or what stops it.</summary>
    internal static string Describe(IEnumerable<PdfANote> notes) =>
        string.Join("\n", notes.Select(n => "• " + (n.Argument is null ? Loc.T(n.Format) : Loc.F(n.Format, n.Argument))));

    private async Task SaveAsPdfAAsync()
    {
        if (_document is null || IsBusy || !await EnsureAllowedAsync(PdfPermissions.All, "save it as PDF/A"))
        {
            return;
        }

        CommitEditsRequested?.Invoke(this, EventArgs.Empty);
        if (HasUnsavedOverlays
            && _dialogs.AskYesNo("Signatures, added text, form entries and text edits go into the PDF/A copy once they are saved.\n\nSave the document first?"))
        {
            await SaveCommand.ExecuteAsync(null);
        }

        if (_document is not { } document)
        {
            return;
        }

        var source = _convertedFrom ?? document.FilePath;
        var name = Path.GetFileNameWithoutExtension(source) + " (PDF-A).pdf";
        if (_dialogs.PickSavePdfFile(name, Path.GetDirectoryName(source), "Save as PDF/A") is not { } path)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Converting to PDF/A…";
        try
        {
            var bytes = await document.SaveDecryptedAsync();
            var title = Path.GetFileNameWithoutExtension(source);
            var result = await Task.Run(() => PdfA.Convert(bytes, new WpfPdfAFontSource(), title));
            await File.WriteAllBytesAsync(path, result.Pdf);
            StatusMessage = Loc.F("Saved as PDF/A: {0}", Path.GetFileName(path));
            _dialogs.ShowInfo(Loc.F("Saved as PDF/A-2b: {0}", Path.GetFileName(path))
                + (result.Changes.Count > 0 ? "\n\n" + Loc.T("Adjusted for the standard:") + "\n" + Describe(result.Changes) : string.Empty));
        }
        catch (PdfAException ex)
        {
            StatusMessage = string.Empty;
            _dialogs.ShowError(Loc.T("This document cannot be saved as PDF/A without changing how it looks. Nothing was saved.") + "\n\n" + Describe(ex.Problems));
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = string.Empty;
            _dialogs.ShowError(Loc.F("Couldn't save as PDF/A.\n\n{0}", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }
}
