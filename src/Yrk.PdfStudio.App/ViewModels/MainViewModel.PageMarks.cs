using System.IO;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf.Security;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>Watermark, header, footer and page numbers, written as real text into a new copy.</summary>
public sealed partial class MainViewModel
{
    private IAsyncRelayCommand? _pageMarksCommand;

    public IAsyncRelayCommand PageMarksCommand => _pageMarksCommand ??= new AsyncRelayCommand(AddPageMarksAsync);

    private async Task AddPageMarksAsync()
    {
        if (_document is null || IsBusy || !await EnsureAllowedAsync(PdfPermissions.Modify, "change it") || _document is not { } document)
        {
            return;
        }

        if (document.IsEncrypted)
        {
            _dialogs.ShowInfo("Remove the password protection first (Tools › Remove password protection), then add the marks. You can protect the result again.");
            return;
        }

        var name = Path.GetFileName(_convertedFrom ?? document.FilePath);
        if (_dialogs.AskPageMarks(new PageMarksViewModel(document, name, _currentPageIndex)) is not { } settings)
        {
            return;
        }

        var targetPath = _dialogs.PickSavePdfFile(
            Path.GetFileNameWithoutExtension(document.FilePath) + " (marked).pdf", Path.GetDirectoryName(document.FilePath), "Save the marked document");
        if (targetPath is null)
        {
            return;
        }

        var saved = await TransformToFileAsync(targetPath, "Adding watermark, header and footer…", async (doc, input, cancellationToken) =>
        {
            var pages = Enumerable.Range(0, doc.PageCount).ToList();
            var blocks = PageMarks.ToBlocks(settings, doc.PageSizes, pages, name, DateTime.Today); // WPF shaping: on this thread.
            return await Task.Run(() => PdfTextWriter.AddText(input, blocks), cancellationToken);
        });
        if (saved is not null)
        {
            StatusMessage = Loc.F("Saved {0}", Path.GetFileName(saved));
        }
    }
}
