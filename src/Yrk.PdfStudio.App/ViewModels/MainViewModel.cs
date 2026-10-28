using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

public enum ZoomMode
{
    Custom,
    FitWidth,
    FitPage,
}

public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>Gap above, between and around pages, in DIPs. Must match the page list in MainWindow.xaml.</summary>
    public const double PageGap = 12;

    private const string AppName = "YRK PDF";
    private static string FitWidthText => Loc.T("Fit width");
    private static string FitPageText => Loc.T("Fit page");
    private const double PointsToDips = 96.0 / 72.0;
    private const double MaxRenderPixels = 24_000_000;
    private const int RenderAheadPages = 1;
    private const int KeepRenderedPages = 3;

    private const double DetailMargin = 0.25; // share of the visible size rendered around it

    // Up to 6400 %: drawings need it, and the visible part is rendered sharply at any zoom (see UpdateDetail).
    private static readonly double[] ZoomSteps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0, 6.4, 8.0, 10.0, 12.5, 16.0, 20.0, 25.0, 32.0, 40.0, 50.0, 64.0];

    private readonly IPdfEngine _engine;
    private readonly IDialogService _dialogs;

    private IPdfDocument? _document;
    /// <summary>The working copy the open document is read from, when it has changes the file does not have yet.</summary>
    private string? _workingCopy;

    private ObservableCollection<PageViewModel> _pages = [];
    private IReadOnlyList<BookmarkViewModel> _bookmarks = [];
    private double[] _pageTops = [];
    private double _totalHeight;
    private double _maxPageWidthPoints;
    private double _scrollOffset;
    private double _viewportWidth;
    private double _viewportHeight;
    private double? _requestedScrollOffset;
    private int? _navigationTarget;
    private double _zoom = 1.0;
    private ZoomMode _zoomMode = ZoomMode.FitWidth;
    private int _currentPageIndex;
    private bool _isSidebarOpen = true;
    private double _dpiScale = 1.0;

    private string _searchText = string.Empty;
    private bool _matchCase;
    private string _searchStatus = string.Empty;
    private CancellationTokenSource? _searchCancellation;
    private string? _activeQuery;
    private PdfSearchOptions _activeOptions;
    private bool _isSearchRunning;
    private readonly List<SearchHit> _hits = [];
    private SearchHit? _currentHit;

    public MainViewModel(IPdfEngine engine, IDialogService dialogs, ISignatureStore signatureStore)
    {
        _engine = engine;
        _dialogs = dialogs;
        SubscribeToLanguage();
        _signatureStore = signatureStore;

        OpenCommand = new AsyncRelayCommand(OpenWithDialogAsync);
        CloseCommand = new AsyncRelayCommand(CloseWithPromptAsync);
        ZoomInCommand = new RelayCommand(() => ZoomBy(1));
        ZoomOutCommand = new RelayCommand(() => ZoomBy(-1));
        ActualSizeCommand = new RelayCommand(() => SetZoom(1.0));
        FitWidthCommand = new RelayCommand(() => ZoomMode = ZoomMode.FitWidth);
        FitPageCommand = new RelayCommand(() => ZoomMode = ZoomMode.FitPage);
        NextPageCommand = new RelayCommand(() => GoToPage(CurrentPageIndex + 1));
        PreviousPageCommand = new RelayCommand(() => GoToPage(CurrentPageIndex - 1));
        FirstPageCommand = new RelayCommand(() => GoToPage(0));
        LastPageCommand = new RelayCommand(() => GoToPage(Pages.Count - 1));
        FindNextCommand = new RelayCommand(() => Find(forward: true));
        FindPreviousCommand = new RelayCommand(() => Find(forward: false));
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarOpen = !IsSidebarOpen);
        InitializeEditing();
    }

    /// <summary>Asks the view to scroll the page list to a vertical offset (DIPs).</summary>
    public event EventHandler<double>? ScrollRequested;

    public IAsyncRelayCommand OpenCommand { get; }

    public IAsyncRelayCommand CloseCommand { get; }

    public IRelayCommand ZoomInCommand { get; }

    public IRelayCommand ZoomOutCommand { get; }

    public IRelayCommand ActualSizeCommand { get; }

    public IRelayCommand FitWidthCommand { get; }

    public IRelayCommand FitPageCommand { get; }

    public IRelayCommand NextPageCommand { get; }

    public IRelayCommand PreviousPageCommand { get; }

    public IRelayCommand FirstPageCommand { get; }

    public IRelayCommand LastPageCommand { get; }

    public IRelayCommand FindNextCommand { get; }

    public IRelayCommand FindPreviousCommand { get; }

    public IRelayCommand ToggleSidebarCommand { get; }

    public bool HasDocument => _document is not null;

    public string WindowTitle => _document is null
        ? AppName
        : $"{(IsDirty ? "• " : string.Empty)}{(_convertedFrom is { } drawing ? Path.GetFileName(drawing) : Path.GetFileName(_document.FilePath))} - {AppName}";

    public ObservableCollection<PageViewModel> Pages
    {
        get => _pages;
        private set
        {
            if (SetProperty(ref _pages, value))
            {
                ClearRedactionMarks(); // Marks and text edits belong to the old pages.
                ClearNewFields();
                ClearTextEdits();
                ClearMeasurementsOnPagesChange();
            }
        }
    }

    public IReadOnlyList<BookmarkViewModel> Bookmarks
    {
        get => _bookmarks;
        private set
        {
            if (SetProperty(ref _bookmarks, value))
            {
                OnPropertyChanged(nameof(HasBookmarks));
            }
        }
    }

    public bool HasBookmarks => _bookmarks.Count > 0;

    public bool IsSidebarOpen
    {
        get => _isSidebarOpen;
        set => SetProperty(ref _isSidebarOpen, value);
    }

    public int CurrentPageIndex => _currentPageIndex;

    /// <summary>The 1-based page number shown in (and typed into) the toolbar.</summary>
    public string PageInput
    {
        get => HasDocument ? (_currentPageIndex + 1).ToString(CultureInfo.CurrentCulture) : string.Empty;
        set
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var number))
            {
                GoToPage(number - 1);
            }

            OnPropertyChanged();
        }
    }

    public string PageCountText => HasDocument ? $"/ {Pages.Count}" : string.Empty;

    public double Zoom => _zoom;

    public ZoomMode ZoomMode
    {
        get => _zoomMode;
        set
        {
            _zoomMode = value;
            OnPropertyChanged();
            if (value != ZoomMode.Custom)
            {
                ApplyZoom(ComputeFitZoom(value), keepAnchor: true);
            }

            OnPropertyChanged(nameof(ZoomText));
        }
    }

    public IReadOnlyList<string> ZoomPresets =>
        [FitWidthText, FitPageText, "50%", "75%", "100%", "125%", "150%", "200%", "300%", "400%"];

    public string ZoomText
    {
        get => _zoomMode switch
        {
            ZoomMode.FitWidth => FitWidthText,
            ZoomMode.FitPage => FitPageText,
            _ => $"{Math.Round(_zoom * 100)}%",
        };
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Equals(FitWidthText, StringComparison.OrdinalIgnoreCase))
            {
                ZoomMode = ZoomMode.FitWidth;
            }
            else if (text.Equals(FitPageText, StringComparison.OrdinalIgnoreCase))
            {
                ZoomMode = ZoomMode.FitPage;
            }
            else if (double.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.CurrentCulture, out var percent) && percent > 0)
            {
                SetZoom(percent / 100);
            }

            OnPropertyChanged();
        }
    }

    /// <summary>Physical pixels per DIP of the monitor showing the window.</summary>
    public double DpiScale
    {
        get => _dpiScale;
        set
        {
            if (SetProperty(ref _dpiScale, Math.Max(0.5, value)))
            {
                UpdateVisiblePages();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty) && _searchText.Trim().Length == 0)
            {
                ClearSearch();
            }
        }
    }

    public bool MatchCase
    {
        get => _matchCase;
        set
        {
            if (SetProperty(ref _matchCase, value))
            {
                ClearSearch();
            }
        }
    }

    public string SearchStatus
    {
        get => _searchStatus;
        private set => SetProperty(ref _searchStatus, Loc.T(value));
    }

    public async Task OpenFileAsync(string path)
    {
        if (await ConfirmDiscardOrSaveAsync())
        {
            var opened = IsDrawing(path)
                ? await OpenDrawingAsync(path)
                : await OpenDocumentAsync(path, password: null, restore: null, keepEdits: false);
            if (opened)
            {
                RecentFiles.Add(path);
            }
        }
    }

    private IAsyncRelayCommand<string>? _openRecentCommand;

    /// <summary>Opens a file from the recent list (in a new tab when this one shows a document).</summary>
    public IAsyncRelayCommand<string> OpenRecentCommand => _openRecentCommand ??= new AsyncRelayCommand<string>(async path =>
    {
        if (string.IsNullOrEmpty(path) || IsBusy)
        {
            return;
        }

        if (!File.Exists(path))
        {
            RecentFiles.Remove(path);
            _dialogs.ShowError(Loc.F("\"{0}\" is no longer there (moved, renamed or deleted). It was taken off the recent list.", Path.GetFileName(path)));
            return;
        }

        if (!RequestOpenInNewTab(path))
        {
            await OpenFileAsync(path);
        }
    });

    private IRelayCommand? _clearRecentCommand;

    public IRelayCommand ClearRecentCommand => _clearRecentCommand ??= new RelayCommand(RecentFiles.Clear);

    /// <summary>
    /// Opens <paramref name="path"/> and replaces the current document. With <paramref name="keepEdits"/>
    /// the unsaved overlays and field values are re-attached (used when reloading the same file).
    /// </summary>
    /// <param name="copyPath">A working copy of <paramref name="path"/> to read instead (see <see cref="ApplyNewFieldsAsync"/>).</param>
    private async Task<bool> OpenDocumentAsync(string path, string? password, ViewState? restore, bool keepEdits, string? copyPath = null)
    {
        while (true)
        {
            try
            {
                var document = copyPath is null ? await _engine.OpenAsync(path, password) : await _engine.OpenCopyAsync(path, copyPath, password);
                if (copyPath == _workingCopy)
                {
                    _workingCopy = null; // Read again by the new document: releasing the old one must not delete it.
                }

                await ReleaseDocumentAsync();
                _workingCopy = copyPath;
                if (!keepEdits)
                {
                    ClearEdits();
                }

                _password = password;
                LoadDocument(document, restore);
                return true;
            }
            catch (PdfPasswordRequiredException ex)
            {
                password = _dialogs.PromptPassword(Path.GetFileName(path), ex.PasswordWasWrong);
                if (password is null)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
            {
                var hint = Path.GetExtension(path).Equals(".ai", StringComparison.OrdinalIgnoreCase)
                    ? Loc.T("\n\nIllustrator files open when they were saved with \"Create PDF Compatible File\" turned on.")
                    : string.Empty;
                _dialogs.ShowError(Loc.F("Couldn't open \"{0}\".\n\n{1}", Path.GetFileName(path), ex.Message) + hint);
                return false;
            }
        }
    }

    /// <summary>Called by the view whenever the page list scrolls or resizes.</summary>
    public void UpdateViewport(double offset, double width, double height)
    {
        var moved = Math.Abs(offset - _scrollOffset) > 0.5;
        var resized = Math.Abs(width - _viewportWidth) > 0.5 || Math.Abs(height - _viewportHeight) > 0.5;
        _scrollOffset = offset;
        _viewportWidth = width;
        _viewportHeight = height;

        if (resized && _document is not null && _zoomMode != ZoomMode.Custom)
        {
            var fit = ComputeFitZoom(_zoomMode);
            if (Math.Abs(fit - _zoom) > 0.0005)
            {
                ApplyZoom(fit, keepAnchor: true);
                return;
            }
        }

        UpdateVisiblePages(moved);
    }

    public void GoToPage(int index)
    {
        if (_document is null || Pages.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, Pages.Count - 1);
        SetCurrentPage(index);
        RequestScroll(_pageTops[index] - PageGap, navigationTarget: index);
    }

    public void NavigateTo(BookmarkViewModel? bookmark)
    {
        if (bookmark?.PageIndex is int pageIndex)
        {
            GoToPage(pageIndex);
        }
    }

    public void ZoomBy(int direction)
    {
        if (_document is null)
        {
            return;
        }

        var next = direction > 0
            ? ZoomSteps.FirstOrDefault(step => step > _zoom + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(step => step < _zoom - 0.001, ZoomSteps[0]);
        SetZoom(next);
    }

    /// <summary>Called by the view when a thumbnail scrolls into view.</summary>
    public void RequestThumbnail(PageViewModel page)
    {
        if (_document is null || page.Thumbnail is not null || page.PendingThumbnail is not null)
        {
            return;
        }

        _ = LoadThumbnailAsync(_document, page);
    }

    /// <summary>Called by the view when a thumbnail scrolls out of view.</summary>
    public void ReleaseThumbnail(PageViewModel page)
    {
        page.PendingThumbnail?.Cancel();
        page.PendingThumbnail = null;
        page.Thumbnail = null;
    }

    private async Task OpenWithDialogAsync()
    {
        var path = _dialogs.PickPdfFile();
        if (path is not null && !RequestOpenInNewTab(path))
        {
            await OpenFileAsync(path);
        }
    }

    private void LoadDocument(IPdfDocument document, ViewState? restore)
    {
        _document = document;
        var pages = document.PageSizes.Select((size, index) => new PageViewModel(index, size)).ToList();
        _maxPageWidthPoints = pages.Count == 0 ? 0 : pages.Max(page => page.WidthPoints);
        foreach (var item in _placedItems.Where(item => item.PageIndex < pages.Count))
        {
            pages[item.PageIndex].Items.Add(item);
        }

        Pages = new ObservableCollection<PageViewModel>(pages);
        _currentPageIndex = restore is null ? 0 : Math.Clamp(restore.PageIndex, 0, Math.Max(0, pages.Count - 1));
        _zoomMode = restore?.ZoomMode ?? ZoomMode.FitWidth;
        ApplyZoom(_zoomMode == ZoomMode.Custom ? restore!.Zoom : ComputeFitZoom(_zoomMode), keepAnchor: false);
        if (restore is null)
        {
            RequestScroll(0, navigationTarget: 0);
        }
        else
        {
            RequestScroll(restore.ScrollOffset);
        }

        RaiseDocumentPropertiesChanged();
        _ = LoadBookmarksAsync(document);
        _ = LoadCommentsAsync(document);
        _ = LoadSignaturesAsync(document);
        _ = LoadLayersAsync(document);
    }

    private async Task CloseWithPromptAsync()
    {
        if (RequestCloseTab())
        {
            return; // The window closes the whole tab (and asks to save).
        }

        if (await ConfirmDiscardOrSaveAsync())
        {
            await ReleaseDocumentAsync();
            ClearEdits();
        }
    }

    /// <summary>Closes the native document and clears the view; unsaved edits are kept.</summary>
    private async Task ReleaseDocumentAsync()
    {
        if (_document is not { } document)
        {
            return;
        }

        ClearSearch();
        CancelPageGesture();
        CancelDigitalPlacement();
        ClearSignatures();
        ClearLayers();
        ClearTextSelection();
        SelectedComment = null;
        _commentsGeneration++;
        Comments.Clear();
        OnPropertyChanged(nameof(HasComments));
        foreach (var page in Pages)
        {
            page.CancelPendingRender();
            page.PendingThumbnail?.Cancel();
        }

        _document = null;
        _convertedFrom = null;
        Pages = [];
        Bookmarks = [];
        _pageTops = [];
        _totalHeight = 0;
        _currentPageIndex = 0;
        _navigationTarget = null;
        _requestedScrollOffset = null;
        RaiseDocumentPropertiesChanged();

        await document.DisposeAsync();
        if (_workingCopy is { } copy)
        {
            _workingCopy = null;
            TryDelete(copy); // Only read while its document was open.
        }
    }

    private void RaiseDocumentPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(PageCountText));
        OnPropertyChanged(nameof(CurrentPageIndex));
        OnPropertyChanged(nameof(PageInput));
        OnPropertyChanged(nameof(ZoomMode));
        OnPropertyChanged(nameof(ZoomText));
    }

    private async Task LoadBookmarksAsync(IPdfDocument document)
    {
        try
        {
            var bookmarks = await document.GetBookmarksAsync();
            if (document == _document)
            {
                Bookmarks = bookmarks.Select(BookmarkViewModel.From).ToList();
            }
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
        {
            // A broken outline should never block reading the document.
        }
    }

    // --- Zoom and layout -------------------------------------------------------------------

    private void SetZoom(double zoom)
    {
        _zoomMode = ZoomMode.Custom;
        OnPropertyChanged(nameof(ZoomMode));
        ApplyZoom(zoom, keepAnchor: true);
    }

    private void ApplyZoom(double zoom, bool keepAnchor)
    {
        zoom = Math.Clamp(zoom, ZoomSteps[0], ZoomSteps[^1]);

        // Keep the point at the center of the viewport in place while the pages resize.
        var anchorPage = -1;
        var anchorFraction = 0.0;
        if (keepAnchor && _pageTops.Length > 0)
        {
            var center = _scrollOffset + (_viewportHeight / 2);
            anchorPage = PageIndexAt(center);
            anchorFraction = (center - _pageTops[anchorPage]) / Math.Max(1, Pages[anchorPage].DisplayHeight);
        }

        _zoom = zoom;
        foreach (var page in Pages)
        {
            page.SetDisplaySize(page.WidthPoints * PointsToDips * zoom, page.HeightPoints * PointsToDips * zoom);
        }

        RecomputeLayout();
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(OverlayUnit));
        OnPropertyChanged(nameof(MeasureStroke));
        OnPropertyChanged(nameof(ZoomText));

        if (anchorPage >= 0)
        {
            var anchor = Pages[anchorPage];
            RequestScroll(_pageTops[anchorPage] + (anchorFraction * anchor.DisplayHeight) - (_viewportHeight / 2));
        }
    }

    private double ComputeFitZoom(ZoomMode mode)
    {
        if (Pages.Count == 0 || _viewportWidth <= 0 || _viewportHeight <= 0)
        {
            return _zoom;
        }

        var availableWidth = Math.Max(50, _viewportWidth - (2 * PageGap));
        var availableHeight = Math.Max(50, _viewportHeight - (2 * PageGap));
        if (mode == ZoomMode.FitPage)
        {
            var page = Pages[Math.Clamp(_currentPageIndex, 0, Pages.Count - 1)];
            return Math.Min(
                availableWidth / (page.WidthPoints * PointsToDips),
                availableHeight / (page.HeightPoints * PointsToDips));
        }

        return availableWidth / (_maxPageWidthPoints * PointsToDips);
    }

    private void RecomputeLayout()
    {
        _pageTops = new double[Pages.Count];
        var top = PageGap;
        for (var i = 0; i < Pages.Count; i++)
        {
            _pageTops[i] = top;
            top += Pages[i].DisplayHeight + PageGap;
        }

        _totalHeight = top;
    }

    /// <summary>The page whose slot (page plus the gap below it) contains the given offset.</summary>
    private int PageIndexAt(double offset)
    {
        var index = Array.BinarySearch(_pageTops, offset);
        if (index < 0)
        {
            index = ~index - 1;
        }

        return Math.Clamp(index, 0, _pageTops.Length - 1);
    }

    private void RequestScroll(double offset, int? navigationTarget = null)
    {
        offset = Math.Max(0, offset);
        _requestedScrollOffset = offset;
        _navigationTarget = navigationTarget;
        ScrollRequested?.Invoke(this, offset);
    }

    private void SetCurrentPage(int index)
    {
        if (_currentPageIndex == index)
        {
            return;
        }

        _currentPageIndex = index;
        OnPropertyChanged(nameof(CurrentPageIndex));
        OnPropertyChanged(nameof(PageInput));
    }

    private void UpdateVisiblePages(bool moved = false)
    {
        if (_document is null || _pageTops.Length == 0)
        {
            return;
        }

        UpdateCurrentPage(moved);

        var first = PageIndexAt(_scrollOffset);
        var last = PageIndexAt(_scrollOffset + _viewportHeight);
        for (var i = 0; i < Pages.Count; i++)
        {
            if (i >= first - RenderAheadPages && i <= last + RenderAheadPages)
            {
                RequestRender(Pages[i]);
                RequestFormFields(Pages[i]);
            }
            else if (i < first - KeepRenderedPages || i > last + KeepRenderedPages)
            {
                Pages[i].ReleaseImage();
            }
        }
    }

    private void UpdateCurrentPage(bool moved)
    {
        // After an explicit jump (page box, bookmark, search) show the requested page, even when
        // a short last page cannot scroll to the top, until the user scrolls somewhere else.
        if (_navigationTarget is int target)
        {
            var arrived = _requestedScrollOffset is double requested && Math.Abs(requested - _scrollOffset) < 1.5;
            if (arrived || !moved)
            {
                SetCurrentPage(target);
                return;
            }

            _navigationTarget = null;
        }

        var atEnd = _scrollOffset > 0 && _scrollOffset + _viewportHeight >= _totalHeight - 1;
        SetCurrentPage(atEnd ? Pages.Count - 1 : PageIndexAt(_scrollOffset + (PageGap * 2)));
    }

    // --- Rendering -------------------------------------------------------------------------

    private void RequestRender(PageViewModel page)
    {
        var (pixelWidth, pixelHeight) = GetRenderSize(page.DisplayWidth, page.DisplayHeight);
        if ((page.Image is not null && page.RenderedPixelWidth == pixelWidth) || page.PendingPixelWidth == pixelWidth)
        {
            return;
        }

        page.CancelPendingRender();
        _ = RenderPageAsync(_document!, page, pixelWidth, pixelHeight);
    }

    private (int Width, int Height) GetRenderSize(double displayWidth, double displayHeight)
    {
        var width = Math.Ceiling(displayWidth * _dpiScale);
        var height = Math.Ceiling(displayHeight * _dpiScale);

        // The whole page is capped at very high zoom; UpdateDetail renders the visible part sharply.
        var pixels = width * height;
        if (pixels > MaxRenderPixels)
        {
            var factor = Math.Sqrt(MaxRenderPixels / pixels);
            width = Math.Floor(width * factor);
            height = Math.Floor(height * factor);
        }

        return ((int)Math.Max(1, width), (int)Math.Max(1, height));
    }

    /// <summary>
    /// Called by the view (after scrolling or zooming settles) with the part of a page that is on screen,
    /// in page DIPs. When the page is too large to render whole at full resolution, that part is rendered
    /// sharply on its own and shown over the (capped) page image.
    /// </summary>
    public void UpdateDetail(PageViewModel page, Rect visible)
    {
        if (_document is not { } document || visible.IsEmpty || visible.Width < 1 || visible.Height < 1)
        {
            return;
        }

        var fullWidth = Math.Ceiling(page.DisplayWidth * _dpiScale);
        var fullHeight = Math.Ceiling(page.DisplayHeight * _dpiScale);
        if (fullWidth * fullHeight <= MaxRenderPixels || fullWidth > int.MaxValue / 2 || fullHeight > int.MaxValue / 2)
        {
            page.ClearDetail();
            return;
        }

        if (page.DetailCovers(visible))
        {
            return;
        }

        // A margin around the visible part, so small scrolls need no new render.
        var margin = Math.Max(visible.Width, visible.Height) * DetailMargin;
        var area = Rect.Intersect(new Rect(visible.X - margin, visible.Y - margin, visible.Width + (2 * margin), visible.Height + (2 * margin)), new Rect(0, 0, page.DisplayWidth, page.DisplayHeight));
        var region = new PdfPixelRegion(
            (int)Math.Floor(area.X * _dpiScale),
            (int)Math.Floor(area.Y * _dpiScale),
            Math.Max(1, (int)Math.Ceiling(area.Width * _dpiScale)),
            Math.Max(1, (int)Math.Ceiling(area.Height * _dpiScale)));
        _ = RenderDetailAsync(document, page, (int)fullWidth, (int)fullHeight, region);
    }

    private async Task RenderDetailAsync(IPdfDocument document, PageViewModel page, int fullWidth, int fullHeight, PdfPixelRegion region)
    {
        var cancellation = page.BeginDetail();
        try
        {
            var bitmap = await document.RenderPageRegionAsync(page.Index, fullWidth, fullHeight, region, PdfWorkPriority.Interactive, cancellation.Token);
            if (!cancellation.IsCancellationRequested && document == _document && Math.Abs(Math.Ceiling(page.DisplayWidth * _dpiScale) - fullWidth) < 1)
            {
                page.SetDetail(
                    BitmapFactory.Create(bitmap),
                    new Rect(region.X / _dpiScale, region.Y / _dpiScale, region.Width / _dpiScale, region.Height / _dpiScale));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or PdfException)
        {
            // Scrolled or zoomed away, or the document closed; the capped page image stays.
        }
    }

    private async Task RenderPageAsync(IPdfDocument document, PageViewModel page, int pixelWidth, int pixelHeight)
    {
        using var cancellation = new CancellationTokenSource();
        page.BeginRender(cancellation, pixelWidth);
        try
        {
            var bitmap = await document.RenderPageAsync(page.Index, pixelWidth, pixelHeight, PdfWorkPriority.Interactive, cancellation.Token);
            if (!cancellation.IsCancellationRequested && document == _document)
            {
                page.SetImage(BitmapFactory.Create(bitmap), pixelWidth);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or PdfException)
        {
            // Cancelled because the page scrolled away, the zoom changed or the document closed;
            // a page that fails to render stays blank instead of interrupting reading.
        }
        finally
        {
            page.EndRender(cancellation);
        }
    }

    private async Task LoadThumbnailAsync(IPdfDocument document, PageViewModel page)
    {
        var cancellation = new CancellationTokenSource();
        page.PendingThumbnail = cancellation;
        try
        {
            var (pixelWidth, pixelHeight) = GetRenderSize(page.ThumbnailWidth, page.ThumbnailHeight);
            var bitmap = await document.RenderPageAsync(page.Index, pixelWidth, pixelHeight, PdfWorkPriority.Background, cancellation.Token);
            if (!cancellation.IsCancellationRequested && document == _document)
            {
                page.Thumbnail = BitmapFactory.Create(bitmap);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or PdfException)
        {
        }
        finally
        {
            if (page.PendingThumbnail == cancellation)
            {
                page.PendingThumbnail = null;
            }

            cancellation.Dispose();
        }
    }

    // --- Search ----------------------------------------------------------------------------

    private void Find(bool forward)
    {
        var query = _searchText.Trim();
        if (_document is null || query.Length == 0)
        {
            return;
        }

        var options = _matchCase ? PdfSearchOptions.MatchCase : PdfSearchOptions.None;
        if (query != _activeQuery || options != _activeOptions)
        {
            _ = RunSearchAsync(_document, query, options);
            return;
        }

        if (_hits.Count == 0)
        {
            return;
        }

        var index = _currentHit is null ? -1 : _hits.IndexOf(_currentHit);
        var next = forward
            ? (index + 1) % _hits.Count
            : (index <= 0 ? _hits.Count - 1 : index - 1);
        MoveToHit(_hits[next]);
    }

    private async Task RunSearchAsync(IPdfDocument document, string query, PdfSearchOptions options)
    {
        ClearSearch();
        _activeQuery = query;
        _activeOptions = options;
        _isSearchRunning = true;
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;

        // Search from the current page onwards (wrapping around) so the first hit is the nearest one.
        var start = _currentPageIndex;
        var pageCount = document.PageCount;
        try
        {
            for (var n = 0; n < pageCount; n++)
            {
                var pageIndex = (start + n) % pageCount;
                var matches = await document.FindTextAsync(pageIndex, query, options, PdfWorkPriority.Background, cancellation.Token);
                if (cancellation.IsCancellationRequested || document != _document)
                {
                    return;
                }

                foreach (var match in matches)
                {
                    var highlights = match.Rects.Select(rect => new HighlightViewModel(rect)).ToList();
                    Pages[pageIndex].AddHighlights(highlights);
                    _hits.Add(new SearchHit(match, highlights));
                }

                if (_currentHit is null && _hits.Count > 0)
                {
                    MoveToHit(_hits[0]);
                }
                else
                {
                    UpdateSearchStatus(pagesSearched: n + 1, pageCount);
                }
            }

            _hits.Sort(static (a, b) => a.Match.PageIndex != b.Match.PageIndex
                ? a.Match.PageIndex.CompareTo(b.Match.PageIndex)
                : a.Match.CharIndex.CompareTo(b.Match.CharIndex));
            _isSearchRunning = false;
            UpdateSearchStatus();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (PdfException)
        {
            _isSearchRunning = false;
            SearchStatus = "Search failed";
        }
        finally
        {
            if (_searchCancellation == cancellation)
            {
                _searchCancellation = null;
                _isSearchRunning = false;
            }

            cancellation.Dispose();
        }
    }

    private void MoveToHit(SearchHit hit)
    {
        if (_currentHit is not null)
        {
            foreach (var highlight in _currentHit.Highlights)
            {
                highlight.IsCurrent = false;
            }
        }

        _currentHit = hit;
        foreach (var highlight in hit.Highlights)
        {
            highlight.IsCurrent = true;
        }

        // Scroll only when the hit is off screen, placing it a third of the way down the viewport.
        var page = Pages[hit.Match.PageIndex];
        var y = _pageTops[page.Index] + ((hit.Match.Rects.Count > 0 ? hit.Match.Rects[0].Y : 0) * page.DisplayHeight);
        if (y < _scrollOffset + PageGap || y > _scrollOffset + _viewportHeight - 40)
        {
            RequestScroll(y - (_viewportHeight / 3), navigationTarget: page.Index);
        }

        UpdateSearchStatus();
    }

    private void UpdateSearchStatus(int pagesSearched = 0, int pageCount = 0)
    {
        if (_isSearchRunning)
        {
            var progress = pageCount > 0 ? $" {pagesSearched * 100 / pageCount}%" : string.Empty;
            SearchStatus = _hits.Count == 0
                ? Loc.T("Searching…") + progress
                : Loc.F("{0} of {1}+ · searching…", _hits.IndexOf(_currentHit!) + 1, _hits.Count) + progress;
            return;
        }

        SearchStatus = _hits.Count == 0
            ? Loc.T("No results")
            : Loc.F("{0} of {1}", _hits.IndexOf(_currentHit!) + 1, _hits.Count);
    }

    private void ClearSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation = null;
        _isSearchRunning = false;
        foreach (var pageIndex in _hits.Select(hit => hit.Match.PageIndex).Distinct())
        {
            if (pageIndex < Pages.Count)
            {
                Pages[pageIndex].ClearHighlights();
            }
        }

        _hits.Clear();
        _currentHit = null;
        _activeQuery = null;
        SearchStatus = string.Empty;
    }

    private sealed record SearchHit(PdfTextMatch Match, IReadOnlyList<HighlightViewModel> Highlights);
}
