using System.IO;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>Compare with another version: opens the other file (a PDF, or a drawing converted on the fly) next to this one.</summary>
public sealed partial class MainViewModel
{
    private IAsyncRelayCommand? _compareCommand;

    public IAsyncRelayCommand CompareCommand => _compareCommand ??= new AsyncRelayCommand(CompareAsync);

    private async Task CompareAsync()
    {
        if (_document is not null && _dialogs.PickPdfFile("Compare with another version") is { } path)
        {
            await CompareWithAsync(path);
        }
    }

    /// <summary>Compares the open document with <paramref name="path"/> (also used by "--compare" on the command line).</summary>
    public async Task CompareWithAsync(string path)
    {
        if (_document is not { } current)
        {
            return;
        }

        // The current document as saved: a separate copy, so the comparison can render while the viewer is used.
        var currentName = Path.GetFileName(_convertedFrom ?? current.FilePath);
        IPdfDocument? first = null;
        IsBusy = true;
        StatusMessage = IsDrawing(path) ? "Converting the drawing…" : "Opening…";
        try
        {
            first = await _engine.OpenAsync(current.FilePath, _password);
            var otherPdf = IsDrawing(path) ? (await ConvertDrawingAsync(path)).PdfPath : path;
            var second = await OpenForCompareAsync(otherPdf, Path.GetFileName(path));
            if (second is null)
            {
                await first.DisposeAsync();
                return;
            }

            StatusMessage = IsDirty ? "Compared with the saved file; unsaved changes are not included" : string.Empty;
            _dialogs.ShowCompare(new CompareViewModel(first, currentName, second, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            if (first is not null)
            {
                await first.DisposeAsync();
            }

            StatusMessage = string.Empty;
            _dialogs.ShowError(Loc.F("Couldn't open \"{0}\".\n\n{1}", Path.GetFileName(path), ex.Message));
        }
        finally
        {
            IsBusy = false;
            if (StatusMessage == Loc.T("Converting the drawing…") || StatusMessage == Loc.T("Opening…"))
            {
                StatusMessage = string.Empty;
            }
        }
    }

    /// <summary>Opens a file for comparing, asking for its password if it has one; null when the user gives up.</summary>
    private async Task<IPdfDocument?> OpenForCompareAsync(string path, string displayName)
    {
        string? password = null;
        var failed = false;
        while (true)
        {
            try
            {
                return await _engine.OpenAsync(path, password);
            }
            catch (PdfPasswordRequiredException ex)
            {
                password = _dialogs.PromptPassword(displayName, failed || ex.PasswordWasWrong);
                failed = true;
                if (password is null)
                {
                    return null;
                }
            }
        }
    }
}
