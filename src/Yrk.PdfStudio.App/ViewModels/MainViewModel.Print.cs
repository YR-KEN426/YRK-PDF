using System.IO;
using System.Printing;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>Printing (Ctrl+P): what is shown is printed, including hidden layers and comments as displayed.</summary>
public sealed partial class MainViewModel
{
    private IAsyncRelayCommand? _printCommand;

    public IAsyncRelayCommand PrintCommand => _printCommand ??= new AsyncRelayCommand(PrintAsync);

    /// <summary>Placed signatures and text, form entries and text edits only become page content when saved.</summary>
    private bool HasUnsavedOverlays => _placedItems.Count > 0 || _fieldEdits.Count > 0 || HasTextEdits;

    private async Task PrintAsync()
    {
        if (_document is null || IsBusy || !await EnsureAllowedAsync(Core.Pdf.Security.PdfPermissions.Print, "print it"))
        {
            return;
        }

        CommitEditsRequested?.Invoke(this, EventArgs.Empty);
        if (HasUnsavedOverlays
            && _dialogs.AskYesNo("Signatures, added text, form entries and text edits are printed once they are saved.\n\nSave the document before printing?"))
        {
            await SaveCommand.ExecuteAsync(null);
        }

        if (_document is not { } document || _dialogs.AskPrint(new PrintViewModel(document, _currentPageIndex)) is not { } settings)
        {
            return;
        }

        if (!document.Permissions.HasFlag(Core.Pdf.Security.PdfPermissions.PrintHighQuality))
        {
            settings = settings with { Quality = PrintQuality.Draft }; // The author allows low-quality printing only.
        }

        IsBusy = true;
        _operation = new CancellationTokenSource();
        OnPropertyChanged(nameof(IsOperationRunning));
        var total = settings.Pages.Count;
        ReportOperation(Loc.F("Printing page {0} of {1}…", 1, total));
        var progress = new Progress<int>(page => ReportOperation(Loc.F("Printing page {0} of {1}…", page + 1, total)));
        try
        {
            var name = Path.GetFileName(_convertedFrom ?? document.FilePath);
            await PdfPrinting.PrintAsync(document, settings, name, progress, _operation.Token);
            StatusMessage = total == 1 ? Loc.F("Sent 1 page to {0}", settings.Printer) : Loc.F("Sent {0} pages to {1}", total, settings.Printer);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Printing cancelled";
        }
        catch (Exception ex) when (ex is PrintSystemException or PdfException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = string.Empty;
            _dialogs.ShowError(Loc.F("Couldn't print.\n\n{0}", ex.Message));
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
}
