using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    private IReadOnlyList<ReaderBookmark>? _workingReadingMarks;
    private List<int>? _readingMarkPageOrder;
    private int _savedReadingMarkPageCount;
    private int _nextReadingMarkPageId = -1;

    // Track page identity independently of IsDirty and the undo stack: rotations/overlays do not
    // renumber marks, and committing overlays can clear undo without saving the page structure.
    private bool HasUnsavedReadingMarkPageChanges => _readingMarkPageOrder is { } order &&
        !order.SequenceEqual(Enumerable.Range(0, _savedReadingMarkPageCount));

    private List<int> ReadingMarkPageOrder(IPdfDocument document)
    {
        if (_readingMarkPageOrder is null)
        {
            _savedReadingMarkPageCount = document.PageCount;
            _readingMarkPageOrder = Enumerable.Range(0, document.PageCount).ToList();
        }
        return _readingMarkPageOrder;
    }

    private IReadOnlyList<ReaderBookmark> WorkingReadingMarks(IPdfDocument document) =>
        _workingReadingMarks ??= ReaderBookmarks.Get(document.FilePath);

    private void ClearWorkingReadingMarks()
    {
        _workingReadingMarks = null;
        _readingMarkPageOrder = null;
        _nextReadingMarkPageId = -1;
    }

    // Called only after the complete PDF reaches its final path, before ClearEdits/reopening.
    private void SaveReadingMarks(string oldPath, string savedPath)
    {
        if (!HasUnsavedReadingMarkPageChanges) ReaderBookmarks.Move(oldPath, savedPath);
        if (_workingReadingMarks is { } marks) ReaderBookmarks.Save(savedPath, marks);
    }

    private IReadOnlyList<ReadingMarkViewModel> _readingMarks = [];
    public IReadOnlyList<ReadingMarkViewModel> ReadingMarks
    {
        get => _readingMarks;
        private set
        {
            SetProperty(ref _readingMarks, value);
            OnPropertyChanged(nameof(HasReadingMarks));
        }
    }

    public bool HasReadingMarks => ReadingMarks.Count > 0;
    private IRelayCommand? _readingMarkCommand;
    public IRelayCommand ReadingMarkCommand => _readingMarkCommand ??= new RelayCommand(
        () => EditReadingMark(CurrentPageIndex), () => HasDocument && !IsBusy);

    public void EditReadingMark(int pageIndex, bool focusColour = false)
    {
        if (IsBusy || _document is not { } document || pageIndex < 0 || pageIndex >= Pages.Count) return;
        var marks = _workingReadingMarks ?? ReaderBookmarks.Get(document.FilePath);
        var existing = marks.FirstOrDefault(b => b.PageIndex == pageIndex);
        if (_dialogs.AskReaderBookmark(existing?.Name ?? Loc.F("Page {0}", pageIndex + 1),
                existing?.Color ?? "blue", existing is not null, focusColour) is not { } answer) return;
        if (HasUnsavedReadingMarkPageChanges)
        {
            if (answer.Remove) _workingReadingMarks = marks.Where(b => b.PageIndex != pageIndex).ToArray();
            else if (existing is null && marks.Count >= ReaderBookmarks.MaxBookmarksPerFile)
                _dialogs.ShowInfo(Loc.F("You can keep up to {0} reading marks per file.", ReaderBookmarks.MaxBookmarksPerFile));
            else _workingReadingMarks = ReaderBookmarks.Clean(marks.Where(b => b.PageIndex != pageIndex)
                .Append(new ReaderBookmark(pageIndex, answer.Name, answer.Color, existing?.CreatedUtc ?? DateTimeOffset.UtcNow)));
        }
        else if (answer.Remove) ReaderBookmarks.Remove(document.FilePath, pageIndex);
        else if (!ReaderBookmarks.Add(document.FilePath, pageIndex, answer.Name, answer.Color))
            _dialogs.ShowInfo(Loc.F("You can keep up to {0} reading marks per file.", ReaderBookmarks.MaxBookmarksPerFile));
        RefreshReadingMarks();
    }

    public void RemoveReadingMark(int pageIndex)
    {
        if (IsBusy || _document is not { } document) return;
        if (HasUnsavedReadingMarkPageChanges)
            _workingReadingMarks = WorkingReadingMarks(document).Where(b => b.PageIndex != pageIndex).ToArray();
        else ReaderBookmarks.Remove(document.FilePath, pageIndex);
        RefreshReadingMarks();
    }

    private void RefreshReadingMarks()
    {
        if (_document is not { } document)
        {
            ReadingMarks = [];
            return;
        }
        ReadingMarks = (_workingReadingMarks ?? ReaderBookmarks.Get(document.FilePath))
            .Where(b => b.PageIndex < document.PageCount)
            .Select(b => new ReadingMarkViewModel(b.PageIndex, b.Name, b.Color, b.CreatedUtc)).ToArray();
    }

    private void CompleteReadingMarkPageChange(IPdfDocument document)
    {
        // Only at the end of the whole page step: restoring deleted pages puts their identities
        // back before RestoreReadingMarks has reattached their marks. A UI refresh must not flush midway.
        if (_workingReadingMarks is { } restored && !HasUnsavedReadingMarkPageChanges)
        {
            ReaderBookmarks.Save(document.FilePath, restored);
            _workingReadingMarks = null;
        }
    }

    // Only successful PDF mutations update the working list. Removed records travel with the page undo step,
    // so undo restores their original creation times without overwriting later edits on surviving pages.
    private async Task<IReadOnlyList<ReaderBookmark>> DeletePagesWithReadingMarksAsync(IPdfDocument document, IReadOnlyList<int> pages)
    {
        var marks = WorkingReadingMarks(document);
        var order = ReadingMarkPageOrder(document);
        await document.DeletePagesAsync(pages);
        var deleted = pages.ToHashSet();
        _readingMarkPageOrder = order.Where((_, index) => !deleted.Contains(index)).ToList();
        var change = ReaderBookmarks.DeletePages(marks, pages);
        _workingReadingMarks = change.Bookmarks;
        return change.Removed;
    }

    private async Task InsertPagesWithReadingMarksAsync(IPdfDocument document, IPdfDocument source, int index,
        IReadOnlyList<int>? pages = null, IReadOnlyList<int>? pageIds = null)
    {
        var count = pages?.Count ?? source.PageCount;
        var marks = WorkingReadingMarks(document);
        var order = ReadingMarkPageOrder(document);
        await document.InsertPagesAsync(source, index, pages);
        order.InsertRange(index, pageIds ?? Enumerable.Range(0, count).Select(_ => _nextReadingMarkPageId--).ToArray());
        _workingReadingMarks = ReaderBookmarks.InsertPages(marks, index, count).Bookmarks;
    }

    private async Task MovePagesWithReadingMarksAsync(IPdfDocument document, IReadOnlyList<int> pages, int destination)
    {
        var order = SimulateMove(Enumerable.Range(0, document.PageCount).ToArray(), pages, destination);
        var marks = WorkingReadingMarks(document);
        var identities = ReadingMarkPageOrder(document);
        await document.MovePagesAsync(pages, destination);
        _readingMarkPageOrder = order.Select(index => identities[index]).ToList();
        _workingReadingMarks = ReaderBookmarks.ReorderPages(marks, order).Bookmarks;
    }

    private void RestoreReadingMarks(IPdfDocument document, IReadOnlyList<ReaderBookmark> removed) =>
        _workingReadingMarks = ReaderBookmarks.Clean(WorkingReadingMarks(document).Concat(removed));
}

public sealed record ReadingMarkViewModel(int PageIndex, string Name, string Color, DateTimeOffset CreatedUtc)
{
    public string PageLabel => Loc.F("Page {0}", PageIndex + 1);
    public Brush Brush => Color switch
    {
        "red" => Brushes.Crimson,
        "orange" => Brushes.DarkOrange,
        "yellow" => Brushes.Gold,
        "green" => Brushes.SeaGreen,
        "purple" => Brushes.MediumPurple,
        _ => Brushes.DodgerBlue,
    };
}

/// <summary>Looks up a local mark for a page without adding reader state to the PDF page model.</summary>
public sealed class ReadingMarkConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [IReadOnlyList<ReadingMarkViewModel> marks, int index]
            ? marks.FirstOrDefault(b => b.PageIndex == index) : null;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
