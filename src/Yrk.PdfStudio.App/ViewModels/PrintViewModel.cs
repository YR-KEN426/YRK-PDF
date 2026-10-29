using System.Printing;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Printing;

namespace Yrk.PdfStudio.App.ViewModels;

public enum PrintPageChoice
{
    All,
    Current,
    Range,
}

/// <summary>The print dialog: printer, paper, pages, sizing and a preview of how a page lands on the paper.</summary>
public sealed partial class PrintViewModel : ObservableObject
{
    private const double PreviewPixels = 700;

    private readonly IPdfDocument _document;
    private readonly int _currentPage;
    private IReadOnlyList<PaperOption> _papers = [];
    private string? _printer;
    private PaperOption? _paper;
    private PaperMargins _margins;
    private PrintOrientation _orientation = PrintOrientation.Auto;
    private PrintPageChoice _pageChoice = PrintPageChoice.All;
    private string _rangeText = string.Empty;
    private int _copies = 1;
    private bool _collate = true;
    private PrintSizing _sizing = PrintSizing.Fit;
    private double _customPercent = 100;
    private bool _autoRotate = true;
    private bool _grayscale;
    private bool _comments = true;
    private PrintQuality _quality = PrintQuality.Standard;
    private IReadOnlyList<int> _pages = [];
    private string _error = string.Empty;
    private string _printerError = string.Empty;
    private int _previewPosition;
    private BitmapSource? _previewImage;
    private CancellationTokenSource? _previewWork;

    public PrintViewModel(IPdfDocument document, int currentPage)
    {
        _document = document;
        _currentPage = currentPage;
        PreviousPreviewCommand = new RelayCommand(() => PreviewPosition--, () => _previewPosition > 0);
        NextPreviewCommand = new RelayCommand(() => PreviewPosition++, () => _previewPosition < PreviewCount - 1);
        try
        {
            Printers = PdfPrinting.PrinterNames(out var defaultPrinter);
            _printer = defaultPrinter is not null && Printers.Contains(defaultPrinter) ? defaultPrinter : Printers.FirstOrDefault();
        }
        catch (PrintSystemException ex)
        {
            Printers = [];
            _printerError = Loc.T("Couldn't list the printers: ") + ex.Message;
        }

        LoadPapers();
        UpdatePages();
    }

    public IReadOnlyList<string> Printers { get; }

    public IReadOnlyList<PrintOrientation> Orientations { get; } = Enum.GetValues<PrintOrientation>();

    public IReadOnlyList<PrintQuality> Qualities { get; } = [PrintQuality.Draft, PrintQuality.Standard, PrintQuality.High];

    public string? Printer
    {
        get => _printer;
        set
        {
            if (SetProperty(ref _printer, value))
            {
                LoadPapers();
            }
        }
    }

    public IReadOnlyList<PaperOption> Papers
    {
        get => _papers;
        private set => SetProperty(ref _papers, value);
    }

    public PaperOption? Paper
    {
        get => _paper;
        set
        {
            if (SetProperty(ref _paper, value))
            {
                LoadMargins();
                RefreshPreview();
            }
        }
    }

    public PrintOrientation Orientation
    {
        get => _orientation;
        set => Set(ref _orientation, value);
    }

    public PrintPageChoice PageChoice
    {
        get => _pageChoice;
        set
        {
            if (SetProperty(ref _pageChoice, value))
            {
                UpdatePages();
            }
        }
    }

    public string RangeText
    {
        get => _rangeText;
        set
        {
            if (SetProperty(ref _rangeText, value))
            {
                if (_pageChoice != PrintPageChoice.Range)
                {
                    PageChoice = PrintPageChoice.Range;
                }
                else
                {
                    UpdatePages();
                }
            }
        }
    }

    public string RangeHint => Loc.F("For example 1-3, 5 (of {0})", _document.PageCount);

    public int Copies
    {
        get => _copies;
        set => SetProperty(ref _copies, Math.Clamp(value, 1, 999));
    }

    public bool Collate
    {
        get => _collate;
        set => SetProperty(ref _collate, value);
    }

    public PrintSizing Sizing
    {
        get => _sizing;
        set => Set(ref _sizing, value);
    }

    public double CustomPercent
    {
        get => _customPercent;
        set
        {
            if (Set(ref _customPercent, Math.Clamp(value, 1, 1000)) && _sizing != PrintSizing.Custom)
            {
                Sizing = PrintSizing.Custom;
            }
        }
    }

    public bool AutoRotate
    {
        get => _autoRotate;
        set => Set(ref _autoRotate, value);
    }

    public bool Grayscale
    {
        get => _grayscale;
        set
        {
            if (SetProperty(ref _grayscale, value))
            {
                RenderPreview();
            }
        }
    }

    public bool Comments
    {
        get => _comments;
        set
        {
            if (SetProperty(ref _comments, value))
            {
                RenderPreview();
            }
        }
    }

    public PrintQuality Quality
    {
        get => _quality;
        set => SetProperty(ref _quality, value);
    }

    private static readonly PagesPerSheet[] LayoutChoices =
        [PagesPerSheet.One, PagesPerSheet.Two, PagesPerSheet.Four, PagesPerSheet.Six, PagesPerSheet.Nine, PagesPerSheet.Booklet];

    private PagesPerSheet _layout = PagesPerSheet.One;

    /// <summary>"1", "2", … pages a sheet, and "Booklet" (in the order of <see cref="LayoutChoices"/>).</summary>
    public IReadOnlyList<string> LayoutNames { get; } =
        ["1", "2", "4", "6", "9", Loc.T("Booklet (print both sides, fold in half)")];

    public int LayoutIndex
    {
        get => Array.IndexOf(LayoutChoices, _layout);
        set
        {
            var layout = LayoutChoices[Math.Clamp(value, 0, LayoutChoices.Length - 1)];
            if (layout == _layout)
            {
                return;
            }

            _layout = layout;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOneToASheet));
            OnPropertyChanged(nameof(Summary));
            _previewPosition = -1;
            PreviewPosition = 0;
        }
    }

    /// <summary>Sizing and turning apply to one page a sheet; several are always fitted.</summary>
    public bool IsOneToASheet => _layout == PagesPerSheet.One;

    /// <summary>How many sides of paper the preview steps through.</summary>
    private int PreviewCount => _layout switch
    {
        PagesPerSheet.One => _pages.Count,
        PagesPerSheet.Booklet => (_pages.Count + 3) / 4 * 2,
        _ => (_pages.Count + (int)_layout - 1) / (int)_layout,
    };

    /// <summary>Why printing is not possible right now (bad page range, no printer).</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(CanPrint));
            }
        }
    }

    public bool CanPrint => _error.Length == 0 && _printerError.Length == 0 && _printer is not null && _paper is not null && _pages.Count > 0;

    public string PrinterError => _printerError;

    public string Summary => _layout != PagesPerSheet.One && _pages.Count > 0 ? Loc.F("{0} pages on {1} side(s) of paper", _pages.Count, PreviewCount)
        : _pages.Count == 1 ? Loc.T("1 page") : Loc.F("{0} pages", _pages.Count);

    // --- Preview (all in paper points) ------------------------------------------------------

    public double PaperWidth { get; private set; } = 595;

    public double PaperHeight { get; private set; } = 842;

    public double PrintableLeft { get; private set; }

    public double PrintableTop { get; private set; }

    public double PrintableWidth { get; private set; }

    public double PrintableHeight { get; private set; }

    /// <summary>Where the page image goes before it is turned (see <see cref="PreviewAngle"/>).</summary>
    public double ImageLeft { get; private set; }

    public double ImageTop { get; private set; }

    public double ImageWidth { get; private set; }

    public double ImageHeight { get; private set; }

    public double PreviewAngle { get; private set; }

    public string ScaleText { get; private set; } = string.Empty;

    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set => SetProperty(ref _previewImage, value);
    }

    public int PreviewPosition
    {
        get => _previewPosition;
        set
        {
            value = Math.Clamp(value, 0, Math.Max(0, PreviewCount - 1));
            if (SetProperty(ref _previewPosition, value))
            {
                PreviousPreviewCommand.NotifyCanExecuteChanged();
                NextPreviewCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(PreviewText));
                RefreshPreview();
            }
        }
    }

    public string PreviewText => _pages.Count == 0 ? Loc.T("No pages")
        : _layout != PagesPerSheet.One ? Loc.F("Side {0} of {1}", _previewPosition + 1, PreviewCount)
        : Loc.F("Page {0} ({1} of {2})", _pages[_previewPosition] + 1, _previewPosition + 1, _pages.Count);

    public IRelayCommand PreviousPreviewCommand { get; }

    public IRelayCommand NextPreviewCommand { get; }

    public PrintSettings? CreateSettings() => CanPrint
        ? new PrintSettings(_printer!, _paper!, _margins, _orientation, _pages, _copies, _collate, _sizing, _customPercent / 100, _autoRotate, _grayscale, _comments, _quality, _layout)
        : null;

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name))
        {
            return false;
        }

        RefreshPreview();
        return true;
    }

    private void LoadPapers()
    {
        if (_printer is null)
        {
            Papers = [];
            Paper = null;
            return;
        }

        try
        {
            var (papers, paper) = PdfPrinting.Papers(_printer);
            Papers = papers;
            Paper = paper;
            SetPrinterError(string.Empty);
        }
        catch (PrintSystemException ex)
        {
            Papers = [];
            Paper = null;
            SetPrinterError(Loc.F("The printer is not available: {0}", ex.Message));
        }
    }

    private void LoadMargins()
    {
        try
        {
            _margins = _printer is not null && _paper is not null ? PdfPrinting.Margins(_printer, _paper) : default;
        }
        catch (PrintSystemException)
        {
            _margins = default;
        }
    }

    private void SetPrinterError(string text)
    {
        _printerError = text;
        OnPropertyChanged(nameof(PrinterError));
        OnPropertyChanged(nameof(CanPrint));
    }

    private void UpdatePages()
    {
        try
        {
            _pages = _pageChoice switch
            {
                PrintPageChoice.Current => [_currentPage],
                PrintPageChoice.Range => PrintLayout.Pages(_rangeText, _document.PageCount),
                _ => PrintLayout.Pages(null, _document.PageCount),
            };
            Error = string.Empty;
        }
        catch (FormatException ex)
        {
            _pages = [];
            Error = Loc.T(ex.Message);
        }

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanPrint));
        _previewPosition = -1;
        PreviewPosition = 0;
        if (_pages.Count == 0)
        {
            OnPropertyChanged(nameof(PreviewText));
            PreviewImage = null;
        }
    }

    /// <summary>Recomputes where the preview page sits on the paper, then renders it.</summary>
    private void RefreshPreview()
    {
        if (CreatePreviewSettings() is not { } settings)
        {
            return;
        }

        if (_layout == PagesPerSheet.One)
        {
            var landscape = PdfPrinting.IsLandscape(settings with { Pages = [_pages[_previewPosition]] }, _document);
            var (width, height, printable) = PdfPrinting.Sheet(settings, landscape);
            var size = _document.PageSizes[_pages[_previewPosition]];
            var placement = PrintLayout.Place(size.Width, size.Height, width, height, printable, _sizing, _customPercent / 100, _autoRotate);
            var target = placement.Target;

            PaperWidth = width;
            PaperHeight = height;
            (PrintableLeft, PrintableTop, PrintableWidth, PrintableHeight) = (printable.X, printable.Y, printable.Width, printable.Height);
            ImageLeft = placement.Rotated ? target.X + target.Width : target.X;
            ImageTop = target.Y;
            ImageWidth = placement.Rotated ? target.Height : target.Width;
            ImageHeight = placement.Rotated ? target.Width : target.Height;
            PreviewAngle = placement.Rotated ? 90 : 0;
            ScaleText = $"{placement.Scale * 100:0.#} %" + (placement.Rotated ? Loc.T(" · turned") : string.Empty);
        }
        else
        {
            // Several pages: the preview is a picture of the whole side of paper.
            var (width, height, printable) = PdfPrinting.Sheet(settings, PdfPrinting.IsLandscape(settings, _document));
            PaperWidth = width;
            PaperHeight = height;
            (PrintableLeft, PrintableTop, PrintableWidth, PrintableHeight) = (printable.X, printable.Y, printable.Width, printable.Height);
            (ImageLeft, ImageTop, ImageWidth, ImageHeight, PreviewAngle) = (0, 0, width, height, 0);
            ScaleText = _layout == PagesPerSheet.Booklet ? Loc.T("Both sides, flip on the short edge") : string.Empty;
        }
        foreach (var name in new[]
                 {
                     nameof(PaperWidth), nameof(PaperHeight), nameof(PrintableLeft), nameof(PrintableTop), nameof(PrintableWidth),
                     nameof(PrintableHeight), nameof(ImageLeft), nameof(ImageTop), nameof(ImageWidth), nameof(ImageHeight),
                     nameof(PreviewAngle), nameof(ScaleText),
                 })
        {
            OnPropertyChanged(name);
        }

        RenderPreview();
    }

    private PrintSettings? CreatePreviewSettings() => _paper is null || _pages.Count == 0 || _previewPosition < 0
        ? null
        : new PrintSettings(_printer ?? string.Empty, _paper, _margins, _orientation, _pages, 1, true, _sizing, _customPercent / 100, _autoRotate, _grayscale, _comments, _quality, _layout);

    private async void RenderPreview()
    {
        if (_pages.Count == 0 || _previewPosition < 0)
        {
            return;
        }

        _previewWork?.Cancel();
        var work = _previewWork = new CancellationTokenSource();
        if (_layout != PagesPerSheet.One)
        {
            await RenderSheetPreviewAsync(work);
            return;
        }

        var index = _pages[_previewPosition];
        var size = _document.PageSizes[index];
        var scale = PreviewPixels / Math.Max(size.Width, size.Height);
        try
        {
            var bitmap = await _document.RenderPageForPrintAsync(
                index,
                Math.Max(1, (int)(size.Width * scale)),
                Math.Max(1, (int)(size.Height * scale)),
                _grayscale,
                _comments,
                work.Token);
            if (!work.IsCancellationRequested)
            {
                PreviewImage = BitmapFactory.Create(bitmap);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or PdfException or ObjectDisposedException)
        {
        }
    }

    /// <summary>A side of paper with its pages, drawn as the printer will get it.</summary>
    private async Task RenderSheetPreviewAsync(CancellationTokenSource work)
    {
        if (CreatePreviewSettings() is not { } settings)
        {
            return;
        }

        var (width, height, printable) = PdfPrinting.Sheet(settings, PdfPrinting.IsLandscape(settings, _document));
        var sides = PdfPrinting.Sides(settings, printable);
        if (_previewPosition >= sides.Count)
        {
            return;
        }

        var unit = PreviewPixels / Math.Max(width, height); // pixels per paper point
        try
        {
            var pages = new List<(BitmapSource Image, PrintPlacement Placement)>();
            foreach (var cell in sides[_previewPosition])
            {
                if (cell.PageIndex is not { } index)
                {
                    continue;
                }

                var size = _document.PageSizes[index];
                var placement = PdfPrinting.Place(settings, size, width, height, cell.Area);
                var bitmap = await _document.RenderPageForPrintAsync(
                    index,
                    Math.Max(1, (int)(size.Width * placement.Scale * unit)),
                    Math.Max(1, (int)(size.Height * placement.Scale * unit)),
                    _grayscale,
                    _comments,
                    work.Token);
                pages.Add((BitmapFactory.Create(bitmap), placement));
            }

            if (work.IsCancellationRequested)
            {
                return;
            }

            var visual = new System.Windows.Media.DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(0, 0, width * unit, height * unit));
                foreach (var (image, placement) in pages)
                {
                    PdfPrinting.DrawPage(context, image, placement, unit);
                }

                if (_layout == PagesPerSheet.Booklet)
                {
                    // Where the sheet is folded.
                    var fold = new System.Windows.Media.Pen(System.Windows.Media.Brushes.Silver, 1) { DashStyle = System.Windows.Media.DashStyles.Dash };
                    context.DrawLine(fold, new System.Windows.Point(width * unit / 2, 0), new System.Windows.Point(width * unit / 2, height * unit));
                }
            }

            PreviewImage = ImageConversion.Render(visual, Math.Max(1, (int)(width * unit)), Math.Max(1, (int)(height * unit)));
        }
        catch (Exception ex) when (ex is OperationCanceledException or PdfException or ObjectDisposedException)
        {
        }
    }
}
