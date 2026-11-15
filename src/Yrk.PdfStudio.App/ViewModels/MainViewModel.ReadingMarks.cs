using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

public sealed partial class MainViewModel
{
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
        var existing = ReaderBookmarks.Get(document.FilePath).FirstOrDefault(b => b.PageIndex == pageIndex);
        if (_dialogs.AskReaderBookmark(existing?.Name ?? Loc.F("Page {0}", pageIndex + 1),
                existing?.Color ?? "blue", existing is not null, focusColour) is not { } answer) return;
        if (answer.Remove) ReaderBookmarks.Remove(document.FilePath, pageIndex);
        else if (!ReaderBookmarks.Add(document.FilePath, pageIndex, answer.Name, answer.Color))
            _dialogs.ShowInfo(Loc.F("You can keep up to {0} reading marks per file.", ReaderBookmarks.MaxBookmarksPerFile));
        RefreshReadingMarks();
    }

    public void RemoveReadingMark(int pageIndex)
    {
        if (IsBusy || _document is not { } document) return;
        ReaderBookmarks.Remove(document.FilePath, pageIndex);
        RefreshReadingMarks();
    }

    private void RefreshReadingMarks() => ReadingMarks = _document is { } document
        ? ReaderBookmarks.Get(document.FilePath).Where(b => b.PageIndex < document.PageCount)
            .Select(b => new ReadingMarkViewModel(b.PageIndex, b.Name, b.Color, b.CreatedUtc)).ToArray()
        : [];

    // Only successful PDF mutations update the local store. Removed records travel with the page undo step,
    // so undo restores their original creation times without overwriting later edits on surviving pages.
    private async Task<IReadOnlyList<ReaderBookmark>> DeletePagesWithReadingMarksAsync(IPdfDocument document, IReadOnlyList<int> pages)
    {
        await document.DeletePagesAsync(pages);
        var change = ReaderBookmarks.DeletePages(ReaderBookmarks.Get(document.FilePath), pages);
        ReaderBookmarks.Save(document.FilePath, change.Bookmarks);
        return change.Removed;
    }

    private async Task InsertPagesWithReadingMarksAsync(IPdfDocument document, IPdfDocument source, int index,
        IReadOnlyList<int>? pages = null)
    {
        var count = pages?.Count ?? source.PageCount;
        await document.InsertPagesAsync(source, index, pages);
        ReaderBookmarks.Save(document.FilePath,
            ReaderBookmarks.InsertPages(ReaderBookmarks.Get(document.FilePath), index, count).Bookmarks);
    }

    private async Task MovePagesWithReadingMarksAsync(IPdfDocument document, IReadOnlyList<int> pages, int destination)
    {
        var order = SimulateMove(Enumerable.Range(0, document.PageCount).ToArray(), pages, destination);
        await document.MovePagesAsync(pages, destination);
        ReaderBookmarks.Save(document.FilePath,
            ReaderBookmarks.ReorderPages(ReaderBookmarks.Get(document.FilePath), order).Bookmarks);
    }

    private static void RestoreReadingMarks(IPdfDocument document, IReadOnlyList<ReaderBookmark> removed) =>
        ReaderBookmarks.Save(document.FilePath, ReaderBookmarks.Get(document.FilePath).Concat(removed));
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
