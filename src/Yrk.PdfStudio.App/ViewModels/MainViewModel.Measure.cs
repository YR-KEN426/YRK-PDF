using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Measure;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Measuring distances, perimeters and areas at the drawing's scale. The scale comes from the PDF when it
/// has one (CAD exports and converted drawings), from a preset ratio, or from calibrating on a known length.
/// </summary>
public sealed partial class MainViewModel
{
    private const double PaperMillimetresPerPoint = 25.4 / 72;
    private static readonly int[] PresetRatios = [1, 2, 5, 10, 20, 25, 50, 75, 100, 200, 250, 500, 1000];
    private static readonly PdfColor MeasureColor = new(232, 89, 12);
    private const string CalibratedName = "Calibrated";

    private readonly List<MeasurementViewModel> _measurements = [];
    private PageViewModel? _draftPage;
    private readonly List<NormalizedPoint> _draftPoints = [];
    private MeasureScaleOption _measureScale = new("From the drawing", null);
    private string _measureUnit = Measurement.Millimetres(SharedSettings.MeasureUnit) is not null ? SharedSettings.MeasureUnit : "mm";
    private string _measureResult = string.Empty;
    private IRelayCommand? _finishMeasurementCommand;
    private IRelayCommand? _clearMeasurementsCommand;
    private IRelayCommand? _copyMeasurementCommand;
    private IAsyncRelayCommand? _addMeasurementsCommand;

    public ObservableCollection<MeasureScaleOption> MeasureScales { get; } = CreateScales();

    public IReadOnlyList<string> MeasureUnits { get; } = Measurement.Units.Select(u => u.Unit).ToList();

    public MeasureScaleOption MeasureScale
    {
        get => _measureScale;
        set => SetProperty(ref _measureScale, value);
    }

    /// <summary>The unit for measuring and the rulers; remembered for next time.</summary>
    public string MeasureUnit
    {
        get => _measureUnit;
        set
        {
            if (SetProperty(ref _measureUnit, value) && Measurement.Millimetres(value) is not null)
            {
                SharedSettings.MeasureUnit = value;
                SharedSettings.Save();
            }
        }
    }

    /// <summary>The latest result, or a hint for the current tool.</summary>
    public string MeasureResult
    {
        get => _measureResult;
        private set => SetProperty(ref _measureResult, Loc.T(value));
    }

    public bool HasMeasurements => _measurements.Count > 0;

    /// <summary>PDF points per screen DIP: keeps overlay lines and labels the same size at any zoom.</summary>
    public double OverlayUnit => 1 / (PointsToDips * _zoom);

    /// <summary>Measurement line thickness in PDF points (1.5 screen DIPs).</summary>
    public double MeasureStroke => 1.5 * OverlayUnit;

    public bool IsMeasuring => IsMeasureTool(CurrentTool);

    public IRelayCommand FinishMeasurementCommand => _finishMeasurementCommand ??= new RelayCommand(FinishMeasurement);

    public IRelayCommand ClearMeasurementsCommand => _clearMeasurementsCommand ??= new RelayCommand(ClearMeasurements);

    public IRelayCommand CopyMeasurementCommand => _copyMeasurementCommand ??= new RelayCommand(() =>
    {
        if (_measurements.Count > 0)
        {
            Clipboard.SetText(_measurements[^1].Result);
            StatusMessage = "Copied " + _measurements[^1].Result;
        }
    });

    public IAsyncRelayCommand AddMeasurementsCommand => _addMeasurementsCommand ??= new AsyncRelayCommand(AddMeasurementsToDocumentAsync);

    private static bool IsMeasureTool(AnnotationTool tool) =>
        tool is AnnotationTool.MeasureDistance or AnnotationTool.MeasurePerimeter or AnnotationTool.MeasureArea or AnnotationTool.Calibrate or AnnotationTool.Count;

    // --- Gestures ---------------------------------------------------------------------------

    /// <summary>Drag preview for distance and calibration.</summary>
    private async void UpdateMeasureDrag(Gesture gesture, NormalizedPoint point)
    {
        point = Pick(gesture.Page, gesture.Start, point);
        gesture.Page.SetPreview(Polyline(gesture.Page, [gesture.Start, point]), null, MeasureBrush, 1.5 * OverlayUnit);
        if (CurrentTool == AnnotationTool.MeasureDistance)
        {
            MeasureResult = await DescribeAsync(gesture.Page, MeasureKind.Distance, [gesture.Start, point]);
        }
    }

    private async Task EndMeasureGestureAsync(PageViewModel page, NormalizedPoint start, NormalizedPoint end, bool isClick)
    {
        switch (CurrentTool)
        {
            case AnnotationTool.MeasureDistance when !isClick:
                end = Pick(page, start, end);
                ClearSnapMarker();
                await AddMeasurementAsync(page, MeasureKind.Distance, [start, end]);
                break;

            case AnnotationTool.Calibrate when !isClick:
                end = Pick(page, start, end);
                ClearSnapMarker();
                Calibrate(page, start, end);
                break;

            case AnnotationTool.MeasurePerimeter or AnnotationTool.MeasureArea when isClick:
                if (_draftPage != page)
                {
                    _draftPage?.ClearPreview();
                    _draftPoints.Clear();
                    _draftPage = page;
                }

                end = _draftPoints.Count > 0 ? Pick(page, _draftPoints[^1], end) : Snap(page, end);
                _draftPoints.Add(end);
                await UpdateDraftAsync(end);
                break;
        }
    }

    /// <summary>Called by the view as the mouse moves over a page without a button pressed.</summary>
    public async void HoverPage(PageViewModel page, NormalizedPoint point)
    {
        if (!IsMeasuring || IsCounting)
        {
            return;
        }

        var drafting = _draftPage == page && _draftPoints.Count > 0 && CurrentTool is AnnotationTool.MeasurePerimeter or AnnotationTool.MeasureArea;
        point = drafting ? Pick(page, _draftPoints[^1], point) : Snap(page, point); // Shows the snap marker too.
        if (drafting)
        {
            await UpdateDraftAsync(point);
        }
    }

    // --- Snapping ---------------------------------------------------------------------------

    private const double SnapRadiusDips = 10;
    private readonly Dictionary<PageViewModel, Task<PdfSnapIndex?>> _snapIndexes = [];
    private PageViewModel? _snapMarkerPage;
    private bool _snapEnabled = true;

    /// <summary>Measure points jump to line ends and middles of the drawing near the cursor.</summary>
    public bool SnapEnabled
    {
        get => _snapEnabled;
        set
        {
            if (SetProperty(ref _snapEnabled, value) && !value)
            {
                ClearSnapMarker();
            }
        }
    }

    /// <summary>The next point of a line from <paramref name="from"/>: Shift keeps 45° steps, otherwise it snaps.</summary>
    private NormalizedPoint Pick(PageViewModel page, NormalizedPoint from, NormalizedPoint point)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            ClearSnapMarker();
            return Constrain(page, from, point);
        }

        return Snap(page, point);
    }

    /// <summary>The nearest snap point within a few screen pixels, or the point itself (the index loads in the background).</summary>
    internal NormalizedPoint Snap(PageViewModel page, NormalizedPoint point)
    {
        if (!_snapEnabled || _document is not { } document)
        {
            ClearSnapMarker();
            return point;
        }

        if (!_snapIndexes.TryGetValue(page, out var loading))
        {
            _snapIndexes[page] = loading = LoadSnapIndexAsync(document, page);
        }

        var radius = SnapRadiusDips / (PointsToDips * _zoom);
        if (loading is not { IsCompletedSuccessfully: true, Result: { } index } || index.Nearest(point.X, point.Y, radius) is not { } hit)
        {
            ClearSnapMarker();
            return point;
        }

        ShowSnapMarker(page, hit);
        return new NormalizedPoint(hit.X, hit.Y);
    }

    private static async Task<PdfSnapIndex?> LoadSnapIndexAsync(IPdfDocument document, PageViewModel page)
    {
        try
        {
            var points = await document.GetSnapPointsAsync(page.Index);
            return await Task.Run(() => new PdfSnapIndex(points, page.WidthPoints, page.HeightPoints));
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
        {
            return null;
        }
    }

    private void ShowSnapMarker(PageViewModel page, PdfSnapPoint hit)
    {
        if (_snapMarkerPage is { } previous && previous != page)
        {
            previous.SetSnapMarker(null, 0);
        }

        // About 9 screen pixels across at any zoom: a square on line ends, a triangle on middles.
        var size = 4.5 * OverlayUnit;
        var center = new Point(hit.X * page.WidthPoints, hit.Y * page.HeightPoints);
        Geometry marker = hit.Kind == PdfSnapKind.End
            ? new RectangleGeometry(new Rect(center.X - size, center.Y - size, 2 * size, 2 * size))
            : new PathGeometry([new PathFigure(new Point(center.X, center.Y - size), [new PolyLineSegment([new Point(center.X + size, center.Y + size), new Point(center.X - size, center.Y + size)], isStroked: true)], closed: true)]);
        page.SetSnapMarker(marker, 1.5 * OverlayUnit);
        _snapMarkerPage = page;
    }

    private void ClearSnapMarker()
    {
        _snapMarkerPage?.SetSnapMarker(null, 0);
        _snapMarkerPage = null;
    }

    /// <summary>Snap points belong to one version of the pages: forget them when the document changes.</summary>
    private void ClearSnapIndexes()
    {
        ClearSnapMarker();
        _snapIndexes.Clear();
    }

    private async Task UpdateDraftAsync(NormalizedPoint cursor)
    {
        if (_draftPage is not { } page)
        {
            return;
        }

        var points = _draftPoints.Append(cursor).ToList();
        var area = CurrentTool == AnnotationTool.MeasureArea;
        page.SetPreview(Polyline(page, area ? [.. points, points[0]] : points), area ? MeasureFill : null, MeasureBrush, 1.5 * OverlayUnit);
        MeasureResult = await DescribeAsync(page, area ? MeasureKind.Area : MeasureKind.Perimeter, points) + Loc.T("  ·  double-click or Enter to finish");
    }

    private async void FinishMeasurement()
    {
        if (_draftPage is not { } page || _draftPoints.Count < 2)
        {
            return;
        }

        var kind = CurrentTool == AnnotationTool.MeasureArea ? MeasureKind.Area : MeasureKind.Perimeter;
        var points = _draftPoints.ToList();
        CancelMeasurementDraft();
        if (kind == MeasureKind.Area && points.Count < 3)
        {
            return;
        }

        await AddMeasurementAsync(page, kind, points);
    }

    private void CancelMeasurementDraft()
    {
        ClearSnapMarker();
        CancelDraftOnly();
    }

    private void CancelDraftOnly()
    {
        _draftPage?.ClearPreview();
        _draftPage = null;
        _draftPoints.Clear();
    }

    /// <summary>With Shift held, the line from the previous point snaps to multiples of 45°.</summary>
    private static NormalizedPoint Constrain(PageViewModel page, NormalizedPoint from, NormalizedPoint to)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            return to;
        }

        var dx = (to.X - from.X) * page.WidthPoints;
        var dy = (to.Y - from.Y) * page.HeightPoints;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        return new NormalizedPoint(from.X + (Math.Cos(angle) * length / page.WidthPoints), from.Y + (Math.Sin(angle) * length / page.HeightPoints));
    }

    // --- Results ----------------------------------------------------------------------------

    private async Task AddMeasurementAsync(PageViewModel page, MeasureKind kind, IReadOnlyList<NormalizedPoint> points)
    {
        page.ClearPreview();
        var result = await DescribeAsync(page, kind, points);
        var measurement = new MeasurementViewModel(page, kind, points, result);
        _measurements.Add(measurement);
        page.Measurements.Add(measurement);
        MeasureResult = result;
        OnPropertyChanged(nameof(HasMeasurements));
    }

    /// <summary>"Distance 1,234.5 mm (1:25)", using the chosen scale and unit.</summary>
    private async Task<string> DescribeAsync(PageViewModel page, MeasureKind kind, IReadOnlyList<NormalizedPoint> points)
    {
        var inPoints = points.Select(p => (p.X * page.WidthPoints, p.Y * page.HeightPoints)).ToList();
        var (millimetresPerPoint, unit, ratio) = await ScaleAtAsync(page, points[0]);
        var factor = unit is null ? millimetresPerPoint / Measurement.Millimetres(_measureUnit)!.Value : millimetresPerPoint;
        var unitName = unit ?? _measureUnit;
        var label = kind switch
        {
            MeasureKind.Distance => Loc.F("Distance {0}", Measurement.Format(Measurement.Length(inPoints) * factor, unitName)),
            MeasureKind.Perimeter => Loc.F("Length {0}", Measurement.Format(Measurement.Length(inPoints) * factor, unitName)),
            _ => Loc.F(
                "Area {0} · perimeter {1}",
                Measurement.Format(Measurement.Area(inPoints) * factor * factor, unitName, squared: true),
                Measurement.Format(Measurement.Length(inPoints, closed: true) * factor, unitName)),
        };

        return $"{label} ({Loc.T(ratio)})";
    }

    /// <summary>
    /// The scale at a point: real millimetres per point, or units per point in a unit we cannot convert
    /// (returned in <c>Unit</c>), and its name.
    /// </summary>
    private async Task<(double PerPoint, string? Unit, string Name)> ScaleAtAsync(PageViewModel page, NormalizedPoint point)
    {
        if (_measureScale.MillimetresPerPoint is { } fixedScale)
        {
            return (fixedScale, null, _measureScale.ToString());
        }

        IReadOnlyList<PdfPageScale> scales = [];
        if (_document is { } document)
        {
            try
            {
                scales = await document.GetPageScalesAsync(page.Index);
            }
            catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
            {
            }
        }

        if (PdfPageScale.At(scales, point) is { } scale)
        {
            var name = scale.Ratio.Length > 0 ? scale.Ratio : "drawing scale";
            if (scale.Unit.Length == 0)
            {
                // No unit in the file: the numbers are drawing units, which we read in the chosen unit.
                return (scale.UnitsPerPoint * Measurement.Millimetres(_measureUnit)!.Value, null, name);
            }

            return Measurement.Millimetres(scale.Unit) is { } mm
                ? (scale.UnitsPerPoint * mm, null, name)
                : (scale.UnitsPerPoint, scale.Unit, name);
        }

        return (PaperMillimetresPerPoint, null, "1:1 on paper");
    }

    private void Calibrate(PageViewModel page, NormalizedPoint start, NormalizedPoint end)
    {
        page.ClearPreview();
        var points = Measurement.Length([(start.X * page.WidthPoints, start.Y * page.HeightPoints), (end.X * page.WidthPoints, end.Y * page.HeightPoints)]);
        if (points < 1 || _dialogs.AskLength(_measureUnit) is not { } real)
        {
            return;
        }

        var millimetres = real.Value * Measurement.Millimetres(real.Unit)!.Value;
        var option = new MeasureScaleOption(CalibratedName, millimetres / points, Measurement.Format(real.Value, real.Unit));
        foreach (var old in MeasureScales.Where(s => s.Name == CalibratedName).ToList())
        {
            MeasureScales.Remove(old);
        }

        MeasureScales.Insert(1, option);
        MeasureScale = option;
        MeasureUnit = real.Unit;
        CurrentTool = AnnotationTool.MeasureDistance;
        MeasureResult = Loc.F("Scale set: 1 pt on the page = {0} mm", (millimetres / points).ToString("0.###", CultureInfo.CurrentCulture));
    }

    private void ClearMeasurements()
    {
        CancelMeasurementDraft();
        foreach (var measurement in _measurements)
        {
            measurement.Page.Measurements.Remove(measurement);
        }

        _measurements.Clear();
        MeasureResult = string.Empty;
        OnPropertyChanged(nameof(HasMeasurements));
    }

    /// <summary>Keeps the measurements in the file as comments (ink lines with the result as their text).</summary>
    private async Task AddMeasurementsToDocumentAsync()
    {
        if (_document is null || _measurements.Count == 0)
        {
            return;
        }

        foreach (var measurement in _measurements.ToList())
        {
            var stroke = measurement.Kind == MeasureKind.Distance ? measurement.Points.ToList() : [.. measurement.Points, measurement.Points[0]];
            if (measurement.Kind == MeasureKind.Perimeter)
            {
                stroke = measurement.Points.ToList();
            }

            await AddAnnotationAsync(measurement.Page, new PdfInkSpec([stroke], 1, MeasureColor, measurement.Result, Author));
        }

        ClearMeasurements();
        StatusMessage = "Measurements added as comments";
    }

    private void ClearMeasurementsOnPagesChange()
    {
        ClearSnapIndexes();
        ClearCountsOnPagesChange();
        ClearMeasurementsOnly();
    }

    private void ClearMeasurementsOnly()
    {
        CancelMeasurementDraft();
        _measurements.Clear();
        OnPropertyChanged(nameof(HasMeasurements));
    }

    private static ObservableCollection<MeasureScaleOption> CreateScales()
    {
        var scales = new ObservableCollection<MeasureScaleOption> { new("From the drawing", null) };
        foreach (var ratio in PresetRatios)
        {
            scales.Add(new MeasureScaleOption($"1:{ratio}", PaperMillimetresPerPoint * ratio));
        }

        return scales;
    }

    private static readonly Brush MeasureBrush = Frozen(new SolidColorBrush(Color.FromRgb(MeasureColor.R, MeasureColor.G, MeasureColor.B)));
    private static readonly Brush MeasureFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, MeasureColor.R, MeasureColor.G, MeasureColor.B)));
    private static readonly Brush FieldPreviewFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x1C, 0x64, 0xA8)));
    private static readonly Brush FieldPreviewStroke = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x64, 0xA8)));
}
