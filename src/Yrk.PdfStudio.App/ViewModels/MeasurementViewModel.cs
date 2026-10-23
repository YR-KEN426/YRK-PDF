using System.Windows;
using System.Windows.Media;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

public enum MeasureKind
{
    Distance,
    Perimeter,
    Area,
}

/// <summary>A measurement drawn on a page: its outline (in PDF points) and a label with the result.</summary>
public sealed class MeasurementViewModel
{
    public MeasurementViewModel(PageViewModel page, MeasureKind kind, IReadOnlyList<NormalizedPoint> points, string result)
    {
        Page = page;
        Kind = kind;
        Points = points;
        Result = result;

        var inPoints = points.Select(p => new Point(p.X * page.WidthPoints, p.Y * page.HeightPoints)).ToList();
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(inPoints[0], isFilled: kind == MeasureKind.Area, isClosed: kind == MeasureKind.Area);
            context.PolyLineTo(inPoints.Skip(1).ToList(), isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        Geometry = geometry;

        // The label sits at the middle of a distance, or the centre of the shape.
        var label = kind == MeasureKind.Distance && inPoints.Count == 2
            ? new Point((inPoints[0].X + inPoints[1].X) / 2, (inPoints[0].Y + inPoints[1].Y) / 2)
            : new Point(inPoints.Average(p => p.X), inPoints.Average(p => p.Y));
        LabelLeft = label.X;
        LabelTop = label.Y;
    }

    public PageViewModel Page { get; }

    public MeasureKind Kind { get; }

    public IReadOnlyList<NormalizedPoint> Points { get; }

    public string Result { get; }

    public Geometry Geometry { get; }

    public bool IsArea => Kind == MeasureKind.Area;

    public double LabelLeft { get; }

    public double LabelTop { get; }
}

/// <summary>A choice in the scale list: the drawing's own scale, a preset ratio, or a calibrated one.</summary>
/// <param name="Name">English name (the key for its translation).</param>
/// <param name="MillimetresPerPoint">Real millimetres per PDF point; null for "from the drawing".</param>
/// <param name="CalibratedLength">For a calibrated scale: the length of the line it was calibrated on.</param>
public sealed record MeasureScaleOption(string Name, double? MillimetresPerPoint, string? CalibratedLength = null)
{
    public override string ToString() => CalibratedLength is null ? Loc.T(Name) : Loc.F("Calibrated ({0} line)", CalibratedLength);
}
