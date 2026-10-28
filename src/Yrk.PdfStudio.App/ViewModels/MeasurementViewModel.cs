using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

public enum MeasureKind
{
    Distance,
    Perimeter,
    Area,
}

/// <summary>
/// A measurement drawn on a page: its outline (in PDF points) and a label with the result. When selected,
/// its corners show handles that can be dragged to correct them.
/// </summary>
public sealed class MeasurementViewModel : ObservableObject
{
    private IReadOnlyList<NormalizedPoint> _points = [];
    private string _result;
    private bool _isSelected;

    public MeasurementViewModel(PageViewModel page, MeasureKind kind, IReadOnlyList<NormalizedPoint> points, string result)
    {
        Page = page;
        Kind = kind;
        _result = result;
        Points = points;
    }

    public PageViewModel Page { get; }

    public MeasureKind Kind { get; }

    public IReadOnlyList<NormalizedPoint> Points
    {
        get => _points;
        set
        {
            _points = value;
            var inPoints = value.Select(p => new Point(p.X * Page.WidthPoints, p.Y * Page.HeightPoints)).ToList();
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(inPoints[0], isFilled: IsArea, isClosed: IsArea);
                context.PolyLineTo(inPoints.Skip(1).ToList(), isStroked: true, isSmoothJoin: false);
            }

            geometry.Freeze();
            Geometry = geometry;
            Handles = inPoints.Select(p => new MeasureHandle(p.X, p.Y)).ToList();

            // The label sits at the middle of a distance, or the centre of the shape.
            var label = Kind == MeasureKind.Distance && inPoints.Count == 2
                ? new Point((inPoints[0].X + inPoints[1].X) / 2, (inPoints[0].Y + inPoints[1].Y) / 2)
                : new Point(inPoints.Average(p => p.X), inPoints.Average(p => p.Y));
            LabelLeft = label.X;
            LabelTop = label.Y;
            OnPropertyChanged(nameof(Points));
            OnPropertyChanged(nameof(Geometry));
            OnPropertyChanged(nameof(Handles));
            OnPropertyChanged(nameof(LabelLeft));
            OnPropertyChanged(nameof(LabelTop));
        }
    }

    public string Result
    {
        get => _result;
        set => SetProperty(ref _result, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public Geometry Geometry { get; private set; } = Geometry.Empty;

    /// <summary>The corners in PDF points, for the drag handles.</summary>
    public IReadOnlyList<MeasureHandle> Handles { get; private set; } = [];

    public bool IsArea => Kind == MeasureKind.Area;

    public double LabelLeft { get; private set; }

    public double LabelTop { get; private set; }
}

/// <summary>A corner of a measurement, in PDF points.</summary>
public sealed record MeasureHandle(double Left, double Top);

/// <summary>A choice in the scale list: the drawing's own scale, a preset ratio, or a calibrated one.</summary>
/// <param name="Name">English name (the key for its translation).</param>
/// <param name="MillimetresPerPoint">Real millimetres per PDF point; null for "from the drawing".</param>
/// <param name="CalibratedLength">For a calibrated scale: the length of the line it was calibrated on.</param>
public sealed record MeasureScaleOption(string Name, double? MillimetresPerPoint, string? CalibratedLength = null)
{
    public override string ToString() => CalibratedLength is null ? Loc.T(Name) : Loc.F("Calibrated ({0} line)", CalibratedLength);
}
