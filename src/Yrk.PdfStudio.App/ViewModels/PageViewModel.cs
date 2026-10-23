using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

public sealed class PageViewModel : ObservableObject
{
    private const double ThumbnailMaxWidth = 120;
    private const double ThumbnailMaxHeight = 160;

    private double _displayWidth;
    private double _displayHeight;
    private ImageSource? _image;
    private ImageSource? _thumbnail;
    private CancellationTokenSource? _pendingRender;

    public PageViewModel(int index, PdfPageSize size)
    {
        Index = index;
        WidthPoints = size.Width;
        HeightPoints = size.Height;

        var aspect = HeightPoints / WidthPoints;
        var width = ThumbnailMaxWidth;
        var height = width * aspect;
        if (height > ThumbnailMaxHeight)
        {
            height = ThumbnailMaxHeight;
            width = height / aspect;
        }

        ThumbnailWidth = Math.Round(width);
        ThumbnailHeight = Math.Round(height);
    }

    public int Index { get; }

    public int Number => Index + 1;

    public double WidthPoints { get; }

    public double HeightPoints { get; }

    /// <summary>Size on screen in DIPs at the current zoom.</summary>
    public double DisplayWidth
    {
        get => _displayWidth;
        private set => SetProperty(ref _displayWidth, value);
    }

    public double DisplayHeight
    {
        get => _displayHeight;
        private set => SetProperty(ref _displayHeight, value);
    }

    public ImageSource? Image
    {
        get => _image;
        private set => SetProperty(ref _image, value);
    }

    public double ThumbnailWidth { get; }

    public double ThumbnailHeight { get; }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    /// <summary>In-flight thumbnail render, if any. Owned by <see cref="MainViewModel"/>.</summary>
    internal CancellationTokenSource? PendingThumbnail { get; set; }

    /// <summary>Search highlights in a 1000×1000 page-relative space (the view stretches it over the page).</summary>
    public ObservableCollection<HighlightViewModel> Highlights { get; } = [];

    public bool HasHighlights => Highlights.Count > 0;

    /// <summary>Editable form widgets on this page (loaded when the page first becomes visible).</summary>
    public ObservableCollection<FormFieldViewModel> Fields { get; } = [];

    /// <summary>Unsaved signatures, stamps and text placed on this page.</summary>
    public ObservableCollection<PlacedItemViewModel> Items { get; } = [];

    /// <summary>Areas marked for redaction (not applied yet).</summary>
    public ObservableCollection<RedactionMarkViewModel> RedactionMarks { get; } = [];

    /// <summary>New form fields on the page, written into the document when it is saved.</summary>
    public ObservableCollection<FormFieldDraftViewModel> NewFields { get; } = [];

    /// <summary>Changes waiting for Save on fields already in the document.</summary>
    public ObservableCollection<FieldChangeViewModel> ChangedFields { get; } = [];

    /// <summary>Lines of existing text being edited (applied when the document is saved).</summary>
    public ObservableCollection<TextEditViewModel> TextEdits { get; } = [];

    /// <summary>Measurements shown on the page (not part of the document unless added as comments).</summary>
    public ObservableCollection<MeasurementViewModel> Measurements { get; } = [];

    /// <summary>Count tool marks on the page (not part of the document unless added as comments).</summary>
    public ObservableCollection<CountMarkViewModel> CountMarks { get; } = [];

    internal bool FieldsRequested { get; set; }

    private Geometry? _preview;
    private Brush? _previewFill;
    private Brush? _previewStroke;
    private double _previewThickness;
    private Rect? _annotationSelection;

    /// <summary>Live feedback while dragging (text selection, pen stroke, shape), in page points.</summary>
    public Geometry? Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public Brush? PreviewFill
    {
        get => _previewFill;
        private set => SetProperty(ref _previewFill, value);
    }

    public Brush? PreviewStroke
    {
        get => _previewStroke;
        private set => SetProperty(ref _previewStroke, value);
    }

    public double PreviewThickness
    {
        get => _previewThickness;
        private set => SetProperty(ref _previewThickness, value);
    }

    /// <summary>Box around the annotation selected in the comments panel, in page points.</summary>
    public Rect? AnnotationSelection
    {
        get => _annotationSelection;
        set
        {
            if (SetProperty(ref _annotationSelection, value))
            {
                OnPropertyChanged(nameof(HasAnnotationSelection));
            }
        }
    }

    public bool HasAnnotationSelection => _annotationSelection is not null;

    internal void SetPreview(Geometry? geometry, Brush? fill, Brush? stroke, double thickness)
    {
        geometry?.Freeze();
        Preview = geometry;
        PreviewFill = fill;
        PreviewStroke = stroke;
        PreviewThickness = thickness;
    }

    internal void ClearPreview() => SetPreview(null, null, null, 0);

    private Geometry? _snapMarker;
    private double _snapMarkerThickness;

    /// <summary>The drawing point the measure tool snaps to (a square for ends, a triangle for middles), in page points.</summary>
    public Geometry? SnapMarker
    {
        get => _snapMarker;
        private set => SetProperty(ref _snapMarker, value);
    }

    public double SnapMarkerThickness
    {
        get => _snapMarkerThickness;
        private set => SetProperty(ref _snapMarkerThickness, value);
    }

    internal void SetSnapMarker(Geometry? marker, double thickness)
    {
        marker?.Freeze();
        SnapMarker = marker;
        SnapMarkerThickness = thickness;
    }

    /// <summary>Pixel width of <see cref="Image"/>; 0 when nothing is rendered.</summary>
    internal int RenderedPixelWidth { get; private set; }

    /// <summary>Pixel width of the in-flight render; 0 when idle.</summary>
    internal int PendingPixelWidth { get; private set; }

    internal void SetDisplaySize(double width, double height)
    {
        if (Math.Abs(width - DisplayWidth) > 0.01)
        {
            ClearDetail(); // Placed for the old size.
        }

        DisplayWidth = width;
        DisplayHeight = height;
    }

    // --- Detail: a sharp render of the visible part, over a page whose full render had to be capped. ---

    private ImageSource? _detailImage;
    private Rect _detailRect;
    private CancellationTokenSource? _pendingDetail;

    public ImageSource? DetailImage
    {
        get => _detailImage;
        private set => SetProperty(ref _detailImage, value);
    }

    /// <summary>Where <see cref="DetailImage"/> sits on the page, in DIPs.</summary>
    public Rect DetailRect
    {
        get => _detailRect;
        private set => SetProperty(ref _detailRect, value);
    }

    /// <summary>True when the current detail already covers <paramref name="rect"/> at this size.</summary>
    internal bool DetailCovers(Rect rect) =>
        _detailImage is not null && _detailRect.Contains(rect);

    internal CancellationTokenSource BeginDetail()
    {
        _pendingDetail?.Cancel();
        _pendingDetail = new CancellationTokenSource();
        return _pendingDetail;
    }

    internal void SetDetail(ImageSource image, Rect rect)
    {
        DetailImage = image;
        DetailRect = rect;
    }

    internal void ClearDetail()
    {
        _pendingDetail?.Cancel();
        _pendingDetail = null;
        DetailImage = null;
    }

    internal void BeginRender(CancellationTokenSource cancellation, int pixelWidth)
    {
        _pendingRender = cancellation;
        PendingPixelWidth = pixelWidth;
    }

    internal void EndRender(CancellationTokenSource cancellation)
    {
        if (_pendingRender != cancellation)
        {
            return;
        }

        _pendingRender = null;
        PendingPixelWidth = 0;
    }

    internal void CancelPendingRender()
    {
        _pendingRender?.Cancel();
        _pendingRender = null;
        PendingPixelWidth = 0;
    }

    internal void SetImage(ImageSource image, int pixelWidth)
    {
        Image = image;
        RenderedPixelWidth = pixelWidth;
    }

    /// <summary>Frees the rendered bitmap (the page keeps its size, so layout does not change).</summary>
    internal void ReleaseImage()
    {
        CancelPendingRender();
        ClearDetail();
        Image = null;
        RenderedPixelWidth = 0;
    }

    internal void AddHighlights(IEnumerable<HighlightViewModel> highlights)
    {
        foreach (var highlight in highlights)
        {
            Highlights.Add(highlight);
        }

        OnPropertyChanged(nameof(HasHighlights));
    }

    internal void ClearHighlights()
    {
        if (Highlights.Count == 0)
        {
            return;
        }

        Highlights.Clear();
        OnPropertyChanged(nameof(HasHighlights));
    }
}
