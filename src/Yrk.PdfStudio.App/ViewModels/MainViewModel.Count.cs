using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Export;
using Yrk.PdfStudio.Core.Pdf.Measure;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// The count tool (Measure tab): click items on a drawing to count them, by type, each with its own colour
/// and running number. Clicking a mark again removes it. The list can be copied, saved for Excel, or kept
/// in the PDF as comments.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Distinct colours that stay readable on white drawings.</summary>
    private static readonly Color[] CountColors =
    [
        Color.FromRgb(214, 40, 40), Color.FromRgb(0, 114, 206), Color.FromRgb(46, 139, 87), Color.FromRgb(230, 126, 0),
        Color.FromRgb(142, 68, 173), Color.FromRgb(0, 150, 160), Color.FromRgb(200, 30, 140), Color.FromRgb(110, 90, 40),
    ];

    /// <summary>How near (screen DIPs) a click must be to a mark to remove it.</summary>
    private const double CountHitRadiusDips = 9;

    private readonly List<CountMarkViewModel> _countMarks = [];
    private CountTypeViewModel? _currentCountType;
    private IRelayCommand? _addCountTypeCommand;
    private IRelayCommand? _copyCountsCommand;
    private IAsyncRelayCommand? _saveCountsCommand;
    private IAsyncRelayCommand? _addCountsCommand;
    private IRelayCommand? _clearCountsCommand;

    public ObservableCollection<CountTypeViewModel> CountTypes { get; } = [];

    public CountTypeViewModel? CurrentCountType
    {
        get => _currentCountType;
        set => SetProperty(ref _currentCountType, value);
    }

    public bool IsCounting => CurrentTool == AnnotationTool.Count;

    public bool HasCounts => _countMarks.Count > 0;

    public IRelayCommand AddCountTypeCommand => _addCountTypeCommand ??= new RelayCommand(() => CurrentCountType = NewCountType());

    public IRelayCommand CopyCountsCommand => _copyCountsCommand ??= new RelayCommand(() =>
    {
        if (_countMarks.Count > 0)
        {
            Clipboard.SetText(CountTally.ToTabSeparated(CountTable()));
            StatusMessage = "Count list copied: paste it into Excel or a document";
        }
    });

    public IAsyncRelayCommand SaveCountsCommand => _saveCountsCommand ??= new AsyncRelayCommand(SaveCountsAsync);

    public IAsyncRelayCommand AddCountsCommand => _addCountsCommand ??= new AsyncRelayCommand(AddCountsToDocumentAsync);

    public IRelayCommand ClearCountsCommand => _clearCountsCommand ??= new RelayCommand(() =>
    {
        if (_countMarks.Count > 0 && _dialogs.Confirm(Loc.F("Remove all {0} count marks?", _countMarks.Count)))
        {
            ClearCounts();
        }
    });

    /// <summary>Adds a mark of the current type, or removes the mark under the click.</summary>
    private void CountAt(PageViewModel page, NormalizedPoint point)
    {
        if (CountMarkAt(page, point) is { } hit)
        {
            RemoveCountMark(hit);
            return;
        }

        var type = CurrentCountType ??= CountTypes.FirstOrDefault() ?? NewCountType();
        var mark = new CountMarkViewModel(page, type, point);
        var index = _countMarks.Count;
        AttachMark(mark, index);
        _undo.Push(() => DetachMark(mark), () => AttachMark(mark, index));
    }

    private CountMarkViewModel? CountMarkAt(PageViewModel page, NormalizedPoint point)
    {
        var radius = CountHitRadiusDips * OverlayUnit;
        return page.CountMarks
            .Select(m => (Mark: m, Distance: Math.Sqrt(Math.Pow(m.Left - (point.X * page.WidthPoints), 2) + Math.Pow(m.Top - (point.Y * page.HeightPoints), 2))))
            .Where(h => h.Distance <= radius)
            .OrderBy(h => h.Distance)
            .Select(h => h.Mark)
            .FirstOrDefault();
    }

    private void RemoveCountMark(CountMarkViewModel mark)
    {
        var index = DetachMark(mark);
        if (index >= 0)
        {
            _undo.Push(() => AttachMark(mark, index), () => DetachMark(mark));
        }
    }

    /// <summary>Puts a mark back at its place in the click order, so it gets its old number again.</summary>
    private void AttachMark(CountMarkViewModel mark, int index)
    {
        // A step from before the pages changed refers to pages that are no longer shown.
        if (_countMarks.Contains(mark) || !Pages.Contains(mark.Page))
        {
            return;
        }

        _countMarks.Insert(Math.Min(index, _countMarks.Count), mark);
        mark.Page.CountMarks.Add(mark);
        Renumber(mark.Type);
        CountsChanged();
    }

    /// <summary>Takes a mark off its page; returns where it was in the click order, or -1.</summary>
    private int DetachMark(CountMarkViewModel mark)
    {
        var index = _countMarks.IndexOf(mark);
        if (index < 0)
        {
            return -1;
        }

        _countMarks.RemoveAt(index);
        mark.Page.CountMarks.Remove(mark);
        Renumber(mark.Type);
        CountsChanged();
        return index;
    }

    private void CountsChanged()
    {
        if (IsCounting)
        {
            UpdateCountSummary();
        }
        else
        {
            OnPropertyChanged(nameof(HasCounts));
        }
    }

    private CountTypeViewModel NewCountType()
    {
        var number = CountTypes.Count + 1;
        var type = new CountTypeViewModel(Loc.F("Item {0}", number), CountColors[(number - 1) % CountColors.Length]);
        type.NameChanged += (_, _) => UpdateCountSummary();
        CountTypes.Add(type);
        return type;
    }

    /// <summary>Numbers follow the order the items were clicked.</summary>
    private void Renumber(CountTypeViewModel type)
    {
        var number = 0;
        foreach (var mark in _countMarks.Where(m => m.Type == type))
        {
            mark.Number = ++number;
        }

        type.Total = number;
    }

    private void UpdateCountSummary()
    {
        MeasureResult = _countMarks.Count == 0
            ? "Click each item to count it. Click a mark again to remove it."
            : CountTally.Summary(CountedItems(), CountTypes.Select(t => t.Name).ToList(), Loc.T("Total"));
        OnPropertyChanged(nameof(HasCounts));
    }

    private IEnumerable<CountedItem> CountedItems() => _countMarks.Select(m => new CountedItem(m.Type.Name, m.Page.Index));

    private IReadOnlyList<IReadOnlyList<string?>> CountTable() =>
        CountTally.Table(CountTally.Rows(CountedItems(), CountTypes.Select(t => t.Name).ToList()), Loc.T("Item type"), Loc.T("Page"), Loc.T("Count"), Loc.T("Total"));

    private async Task SaveCountsAsync()
    {
        if (_document is not { } document || _countMarks.Count == 0)
        {
            return;
        }

        var source = _convertedFrom ?? document.FilePath;
        var name = Path.GetFileNameWithoutExtension(source) + " - " + Loc.T("count") + ".xlsx";
        if (_dialogs.PickSaveFile(name, Path.GetDirectoryName(source), "Save count list", "Excel workbook (*.xlsx)|*.xlsx", ".xlsx") is not { } path)
        {
            return;
        }

        // A summary, and every item with its number and position (millimetres from the page's top-left corner).
        var items = new List<IReadOnlyList<string?>> { new[] { Loc.T("Item type"), Loc.T("No."), Loc.T("Page"), Loc.T("X (mm)"), Loc.T("Y (mm)") } };
        items.AddRange(_countMarks
            .OrderBy(m => CountTypes.IndexOf(m.Type)).ThenBy(m => m.Number)
            .Select(m => (IReadOnlyList<string?>)
            [
                m.Type.Name,
                m.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (m.Page.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                (m.Left * PaperMillimetresPerPoint).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                (m.Top * PaperMillimetresPerPoint).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            ]));
        var sheets = new[]
        {
            CountTally.ToSheet(Loc.T("Count"), CountTable()),
            new XlsxSheet(Loc.T("Items"), [140, 40, 40, 60, 60], items),
        };

        try
        {
            await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                XlsxWriter.Write(stream, sheets);
            });
            StatusMessage = Loc.F("Saved the count list to {0}", Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(Loc.F("Couldn't save the count list.\n\n{0}", ex.Message));
        }
    }

    /// <summary>Keeps the marks in the file: a small circle per item in its colour, named "Door 3" in its comment.</summary>
    private async Task AddCountsToDocumentAsync()
    {
        if (_document is null || _countMarks.Count == 0)
        {
            return;
        }

        // Removed first, so undoing goes back through the comments to the marks on screen.
        var marks = _countMarks.Select(m => (Mark: m, m.Number)).ToList();
        ClearCounts();
        foreach (var (mark, number) in marks)
        {
            var page = mark.Page;
            var radius = Math.Max(5, Math.Max(page.WidthPoints, page.HeightPoints) / 120);
            var bounds = new NormalizedRect(
                mark.Position.X - (radius / page.WidthPoints),
                mark.Position.Y - (radius / page.HeightPoints),
                2 * radius / page.WidthPoints,
                2 * radius / page.HeightPoints);
            await AddAnnotationAsync(page, new PdfShapeSpec(PdfAnnotationKind.Circle, bounds, 2, mark.Type.PdfColor, $"{mark.Type.Name} {number}", Author));
        }

        StatusMessage = "Count marks added as comments";
    }

    /// <summary>Takes every mark off the pages, as one step that Undo brings back.</summary>
    private void ClearCounts()
    {
        var removed = _countMarks.ToList();
        if (removed.Count == 0)
        {
            return;
        }

        void Remove() => removed.ForEach(m => DetachMark(m));
        void Restore()
        {
            for (var i = 0; i < removed.Count; i++)
            {
                AttachMark(removed[i], i);
            }
        }

        Remove();
        _undo.Push(Restore, Remove);
    }

    /// <summary>The marks belong to the old pages; the types (and their names) stay for the next count.</summary>
    private void ClearCountsOnPagesChange()
    {
        _countMarks.Clear();
        foreach (var type in CountTypes)
        {
            type.Total = 0;
        }

        OnPropertyChanged(nameof(HasCounts));
    }
}
