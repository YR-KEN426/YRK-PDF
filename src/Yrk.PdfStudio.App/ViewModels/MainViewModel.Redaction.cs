using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Redaction;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>Redaction: mark areas (by dragging or from search results), then apply to a new file.</summary>
public sealed partial class MainViewModel
{
    private const double MarkPadding = 1.5; // points around marked search results
    private const double MinMarkSize = 4;   // points

    private static readonly Brush RedactPreviewFill = FrozenBrush(Color.FromArgb(0x40, 0xC4, 0x2B, 0x1C));
    private static readonly Brush RedactPreviewStroke = FrozenBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));

    private readonly List<RedactionMarkViewModel> _redactionMarks = [];
    private IAsyncRelayCommand? _markSearchResultsCommand;
    private IRelayCommand? _clearRedactionMarksCommand;
    private IAsyncRelayCommand? _applyRedactionsCommand;
    private IRelayCommand<RedactionMarkViewModel>? _removeRedactionMarkCommand;

    public IAsyncRelayCommand MarkSearchResultsCommand => _markSearchResultsCommand ??= new AsyncRelayCommand(MarkSearchResultsAsync);

    public IRelayCommand ClearRedactionMarksCommand => _clearRedactionMarksCommand ??= new RelayCommand(() =>
    {
        var marks = _redactionMarks.ToList();
        if (marks.Count > 0)
        {
            marks.ForEach(DetachMark);
            _undo.Push(() => marks.ForEach(AttachMark), () => marks.ForEach(DetachMark));
        }
    });

    public IAsyncRelayCommand ApplyRedactionsCommand => _applyRedactionsCommand ??= new AsyncRelayCommand(ApplyRedactionsAsync);

    public IRelayCommand<RedactionMarkViewModel> RemoveRedactionMarkCommand => _removeRedactionMarkCommand ??= new RelayCommand<RedactionMarkViewModel>(mark =>
    {
        if (mark is not null && _redactionMarks.Contains(mark))
        {
            DetachMark(mark);
            _undo.Push(() => AttachMark(mark), () => DetachMark(mark));
        }
    });

    public bool HasRedactionMarks => _redactionMarks.Count > 0;

    public string RedactionMarkText => _redactionMarks.Count switch
    {
        0 => Loc.T("Drag over anything you want to remove"),
        1 => Loc.T("1 area marked"),
        var n => Loc.F("{0} areas marked", n),
    };

    private void UpdateRedactionPreview(Gesture gesture, NormalizedPoint point) =>
        gesture.Page.SetPreview(new RectangleGeometry(ToPoints(gesture.Page, gesture.Start, point)), RedactPreviewFill, RedactPreviewStroke, 1);

    private void EndRedactionGesture(PageViewModel page, NormalizedPoint start, NormalizedPoint end)
    {
        var bounds = Normalize(start, end);
        if (bounds.Width * page.WidthPoints < MinMarkSize || bounds.Height * page.HeightPoints < MinMarkSize)
        {
            return;
        }

        var mark = new RedactionMarkViewModel(page, bounds);
        AttachMark(mark);
        _undo.Push(() => DetachMark(mark), () => AttachMark(mark));
    }

    /// <summary>Marks every occurrence of the text in the search box, on all pages.</summary>
    private async Task MarkSearchResultsAsync()
    {
        if (_document is not { } document || IsBusy)
        {
            return;
        }

        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            _dialogs.ShowInfo("Type the text to remove in the search box first (for example a name or an ID number), then choose Mark search results.");
            return;
        }

        var added = new List<RedactionMarkViewModel>();
        IsBusy = true;
        StatusMessage = "Finding text to mark…";
        try
        {
            var pages = Pages;
            for (var i = 0; i < document.PageCount && i < pages.Count; i++)
            {
                var page = pages[i];
                var options = MatchCase ? PdfSearchOptions.MatchCase : PdfSearchOptions.None;
                foreach (var match in await document.FindTextAsync(i, query, options, PdfWorkPriority.Interactive))
                {
                    foreach (var rect in MergeByLine(match.Rects))
                    {
                        var padX = MarkPadding / page.WidthPoints;
                        var padY = MarkPadding / page.HeightPoints;
                        var mark = new RedactionMarkViewModel(page, new NormalizedRect(rect.X - padX, rect.Y - padY, rect.Width + (2 * padX), rect.Height + (2 * padY)));
                        AttachMark(mark);
                        added.Add(mark);
                    }
                }
            }
        }
        catch (PdfException ex)
        {
            _dialogs.ShowError(Loc.F("Searching failed.\n\n{0}", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }

        if (added.Count > 0)
        {
            _undo.Push(() => added.ForEach(DetachMark), () => added.ForEach(AttachMark));
        }

        StatusMessage = added.Count switch
        {
            0 => Loc.F("\"{0}\" was not found", query),
            1 => Loc.F("Marked 1 place with \"{0}\"", query),
            var n => Loc.F("Marked {0} places with \"{1}\"", n, query),
        };
    }

    private async Task ApplyRedactionsAsync()
    {
        if (_document is null || IsBusy || _redactionMarks.Count == 0
            || !await EnsureAllowedAsync(Core.Pdf.Security.PdfPermissions.Modify, "change it")
            || _document is not { } document)
        {
            return;
        }

        var warning = Loc.T(HasSignatures
            ? "Redaction permanently removes the text, images and drawings in the marked areas and puts black boxes in their place.\n\nThe result is saved as a new file (the original stays as it is). Hidden earlier versions of the document are removed too, and so are its digital signatures."
            : "Redaction permanently removes the text, images and drawings in the marked areas and puts black boxes in their place.\n\nThe result is saved as a new file (the original stays as it is). Hidden earlier versions of the document are removed too.");
        if (!_dialogs.Confirm(warning))
        {
            return;
        }

        var (confirmed, password) = ConfirmPassword(Core.Pdf.Security.PdfPermissions.Modify, "change it");
        if (!confirmed)
        {
            return;
        }

        var targetPath = _dialogs.PickSavePdfFile(
            Path.GetFileNameWithoutExtension(document.FilePath) + " (redacted).pdf", Path.GetDirectoryName(document.FilePath), "Save the redacted document");
        if (targetPath is null)
        {
            return;
        }

        var areas = _redactionMarks.Select(m => new PdfRedactionArea(m.Page.Index, m.Bounds)).ToList();
        PdfRedactionResult? result = null;
        var saved = await TransformToFileAsync(targetPath, "Redacting…", async (_, input, cancellationToken) =>
        {
            result = await PdfRedactor.RedactAsync(input, areas, _engine, new WpfImageCodec(), cancellationToken, password);
            return result.Output;
        });

        if (saved is null || result is null)
        {
            return;
        }

        StatusMessage = areas.Count == 1
            ? Loc.F("Redacted 1 area and saved {0}", Path.GetFileName(saved))
            : Loc.F("Redacted {0} areas and saved {1}", areas.Count, Path.GetFileName(saved));
        if (result.ImagePages.Count > 0)
        {
            var pages = string.Join(", ", result.ImagePages.Select(p => (p + 1).ToString(System.Globalization.CultureInfo.CurrentCulture)));
            _dialogs.ShowInfo(Loc.F(
                "Page(s) {0} had images or text that could not be removed piece by piece under the marked areas, so those pages were turned into images with the areas blacked out.\n\nUse Tools › Recognize text (OCR) to make them searchable again.",
                pages));
        }
    }

    private void AttachMark(RedactionMarkViewModel mark)
    {
        if (!Pages.Contains(mark.Page))
        {
            return; // The page is gone (the document was reloaded).
        }

        _redactionMarks.Add(mark);
        mark.Page.RedactionMarks.Add(mark);
        RaiseMarksChanged();
    }

    private void DetachMark(RedactionMarkViewModel mark)
    {
        _redactionMarks.Remove(mark);
        mark.Page.RedactionMarks.Remove(mark);
        RaiseMarksChanged();
    }

    private void ClearRedactionMarks()
    {
        foreach (var mark in _redactionMarks)
        {
            mark.Page.RedactionMarks.Remove(mark);
        }

        _redactionMarks.Clear();
        RaiseMarksChanged();
    }

    private void RaiseMarksChanged()
    {
        OnPropertyChanged(nameof(HasRedactionMarks));
        OnPropertyChanged(nameof(RedactionMarkText));
        ApplyRedactionsCommand.NotifyCanExecuteChanged();
    }
}
