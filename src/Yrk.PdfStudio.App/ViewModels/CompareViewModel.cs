using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Compare;

namespace Yrk.PdfStudio.App.ViewModels;

public enum CompareView
{
    Differences,
    First,
    Second,
}

/// <summary>One changed area, in pixels of the comparison image.</summary>
public sealed record CompareRegionViewModel(int Number, PdfPixelRegion Region, string Description)
{
    // Outlines sit a few pixels outside the change so they do not cover it.
    public double Left => Region.X - 4;

    public double Top => Region.Y - 4;

    public double Width => Region.Width + 8;

    public double Height => Region.Height + 8;
}

/// <summary>
/// Compares two versions of a document page by page: both pages are rendered at the same size and every
/// ink pixel that is not in the other version is coloured (red: only in the first, blue: only in the second).
/// Owns both documents and closes them when the window closes.
/// </summary>
public sealed partial class CompareViewModel : ObservableObject, IAsyncDisposable
{
    private const double MaxPixelsPerPoint = 3;
    private const double MaxPixels = 20_000_000;

    private IPdfDocument _first;
    private IPdfDocument _second;
    private string _firstName;
    private string _secondName;
    private int _pageIndex;
    private CompareView _view = CompareView.Differences;
    private BitmapSource? _overlay;
    private BitmapSource? _firstImage;
    private BitmapSource? _secondImage;
    private string _summary = string.Empty;
    private bool _isBusy;
    private CancellationTokenSource? _work;

    public CompareViewModel(IPdfDocument first, string firstName, IPdfDocument second, string secondName)
    {
        _first = first;
        _second = second;
        _firstName = firstName;
        _secondName = secondName;
        PreviousPageCommand = new RelayCommand(() => PageIndex--, () => _pageIndex > 0);
        NextPageCommand = new RelayCommand(() => PageIndex++, () => _pageIndex < PageCount - 1);
        SwapCommand = new AsyncRelayCommand(SwapAsync);
    }

    public string FirstName => _firstName;

    public string SecondName => _secondName;

    public string Title => Loc.F("Compare: {0} ↔ {1}", _firstName, _secondName);

    public int PageCount => Math.Min(_first.PageCount, _second.PageCount);

    public string PageText => Loc.F("Page {0} of {1}", _pageIndex + 1, PageCount)
        + (_first.PageCount != _second.PageCount ? Loc.F(" (the versions have {0} and {1} pages)", _first.PageCount, _second.PageCount) : string.Empty);

    public int PageIndex
    {
        get => _pageIndex;
        set
        {
            value = Math.Clamp(value, 0, Math.Max(0, PageCount - 1));
            if (SetProperty(ref _pageIndex, value))
            {
                OnPropertyChanged(nameof(PageText));
                PreviousPageCommand.NotifyCanExecuteChanged();
                NextPageCommand.NotifyCanExecuteChanged();
                _ = CompareAsync();
            }
        }
    }

    public CompareView View
    {
        get => _view;
        set
        {
            if (SetProperty(ref _view, value))
            {
                OnPropertyChanged(nameof(Image));
            }
        }
    }

    /// <summary>The picture for the current view.</summary>
    public BitmapSource? Image => _view switch
    {
        CompareView.First => _firstImage,
        CompareView.Second => _secondImage,
        _ => _overlay,
    };

    public ObservableCollection<CompareRegionViewModel> Regions { get; } = [];

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, Loc.T(value));
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public IRelayCommand PreviousPageCommand { get; }

    public IRelayCommand NextPageCommand { get; }

    public IAsyncRelayCommand SwapCommand { get; }

    /// <summary>Raised after a page has been compared (the view fits it to the window).</summary>
    public event EventHandler? Compared;

    public async Task CompareAsync()
    {
        _work?.Cancel();
        var work = _work = new CancellationTokenSource();
        var token = work.Token;
        IsBusy = true;
        Summary = "Comparing…";
        try
        {
            // Both pages at the size of the first, so a revision on the same sheet lines up pixel for pixel.
            var size = _first.PageSizes[_pageIndex];
            var perPoint = Math.Min(MaxPixelsPerPoint, Math.Sqrt(MaxPixels / (size.Width * size.Height)));
            int width = Math.Max(1, (int)Math.Round(size.Width * perPoint)), height = Math.Max(1, (int)Math.Round(size.Height * perPoint));
            var first = await _first.RenderPageAsync(_pageIndex, width, height, PdfWorkPriority.Interactive, token);
            var second = await _second.RenderPageAsync(_pageIndex, width, height, PdfWorkPriority.Interactive, token);
            var diff = await Task.Run(() => PdfCompare.Diff(first, second), token);
            token.ThrowIfCancellationRequested();

            _firstImage = BitmapFactory.Create(first);
            _secondImage = BitmapFactory.Create(second);
            _overlay = BitmapFactory.Create(diff.Overlay);
            OnPropertyChanged(nameof(Image));

            Regions.Clear();
            var number = 1;
            foreach (var region in diff.Regions)
            {
                var w = region.Width / perPoint * 25.4 / 72;
                var h = region.Height / perPoint * 25.4 / 72;
                Regions.Add(new CompareRegionViewModel(number++, region, Loc.F("{0:0} × {1:0} mm on paper", w, h)));
            }

            var otherSize = _second.PageSizes[_pageIndex];
            var sizeNote = Math.Abs(otherSize.Width - size.Width) > 1 || Math.Abs(otherSize.Height - size.Height) > 1
                ? Loc.T(" · the pages have different sizes, so the second is stretched to the first")
                : string.Empty;
            Summary = (diff.Regions.Count switch
            {
                0 => Loc.T("No differences on this page"),
                1 => Loc.T("1 changed area on this page"),
                var n => Loc.F("{0} changed areas on this page", n),
            }) + sizeNote;
            Compared?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (PdfException ex)
        {
            Summary = Loc.T("Couldn't compare this page: ") + ex.Message;
        }
        finally
        {
            if (_work == work)
            {
                IsBusy = false;
            }
        }
    }

    private async Task SwapAsync()
    {
        (_first, _second) = (_second, _first);
        (_firstName, _secondName) = (_secondName, _firstName);
        OnPropertyChanged(nameof(FirstName));
        OnPropertyChanged(nameof(SecondName));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PageText));
        await CompareAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _work?.Cancel();
        await _first.DisposeAsync();
        await _second.DisposeAsync();
    }

    public static string DisplayName(string path) => Path.GetFileName(path);
}
