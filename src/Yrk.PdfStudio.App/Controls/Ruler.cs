using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Yrk.PdfStudio.App.Controls;

/// <summary>
/// A ruler along the page view, in real units of the page under the cursor: millimetres on paper, or
/// the drawing's units at its scale. <see cref="Origin"/> is where 0 is (the page's top or left edge).
/// </summary>
public sealed class Ruler : FrameworkElement
{
    public static readonly DependencyProperty OrientationProperty = Register(nameof(Orientation), Orientation.Horizontal);
    public static readonly DependencyProperty OriginProperty = Register(nameof(Origin), 0.0);
    public static readonly DependencyProperty DipsPerUnitProperty = Register(nameof(DipsPerUnit), 0.0);
    public static readonly DependencyProperty CursorPositionProperty = Register(nameof(CursorPosition), double.NaN);
    public static readonly DependencyProperty LengthProperty = Register(nameof(Length), double.NaN);

    private static readonly double[] Steps = [1, 2, 5];
    private static readonly Typeface Font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>Position of 0, in DIPs from this ruler's start.</summary>
    public double Origin
    {
        get => (double)GetValue(OriginProperty);
        set => SetValue(OriginProperty, value);
    }

    /// <summary>Screen DIPs per real unit; 0 hides the scale.</summary>
    public double DipsPerUnit
    {
        get => (double)GetValue(DipsPerUnitProperty);
        set => SetValue(DipsPerUnitProperty, value);
    }

    /// <summary>The mouse position in DIPs from this ruler's start (NaN when the mouse is elsewhere).</summary>
    public double CursorPosition
    {
        get => (double)GetValue(CursorPositionProperty);
        set => SetValue(CursorPositionProperty, value);
    }

    /// <summary>The page's size along the ruler, in DIPs (the part outside the page is shaded).</summary>
    public double Length
    {
        get => (double)GetValue(LengthProperty);
        set => SetValue(LengthProperty, value);
    }

    public Brush Background { get; set; } = Brushes.WhiteSmoke;

    public Brush Foreground { get; set; } = Brushes.DimGray;

    public Brush PageBrush { get; set; } = Brushes.White;

    public Brush CursorBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xE8, 0x59, 0x0C));

    protected override void OnRender(DrawingContext context)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        double along = horizontal ? ActualWidth : ActualHeight, across = horizontal ? ActualHeight : ActualWidth;
        context.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        Rect Span(double from, double length, double thickness, double offset = 0) => horizontal
            ? new Rect(from, offset, length, thickness)
            : new Rect(offset, from, thickness, length);

        if (!double.IsNaN(Length) && Length > 0)
        {
            var start = Math.Max(0, Origin);
            var end = Math.Min(along, Origin + Length);
            if (end > start)
            {
                context.DrawRectangle(PageBrush, null, Span(start, end - start, across));
            }
        }

        var pen = new Pen(Foreground, 1);
        pen.Freeze();
        context.DrawLine(pen, horizontal ? new Point(0, across - 0.5) : new Point(across - 0.5, 0), horizontal ? new Point(along, across - 0.5) : new Point(across - 0.5, along));

        var perUnit = DipsPerUnit;
        if (perUnit > 0 && double.IsFinite(perUnit))
        {
            // Labels at least 60 DIPs apart, on 1, 2 or 5 × a power of ten; ten minor ticks between (or fewer when crowded).
            var major = NiceStep(60 / perUnit);
            var majorDips = major * perUnit;
            var minorCount = majorDips / 10 >= 5 ? 10 : majorDips / 5 >= 5 ? 5 : 2;
            var minorDips = majorDips / minorCount;
            var first = (int)Math.Floor(-Origin / minorDips) - 1;
            var last = (int)Math.Ceiling((along - Origin) / minorDips) + 1;
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (var i = first; i <= last; i++)
            {
                var position = Math.Round(Origin + (i * minorDips)) + 0.5;
                if (position < 0 || position > along)
                {
                    continue;
                }

                var isMajor = i % minorCount == 0;
                var isHalf = !isMajor && minorCount == 10 && i % 5 == 0;
                var tick = isMajor ? across * 0.55 : isHalf ? across * 0.35 : across * 0.2;
                context.DrawLine(pen,
                    horizontal ? new Point(position, across - tick) : new Point(across - tick, position),
                    horizontal ? new Point(position, across) : new Point(across, position));

                if (isMajor)
                {
                    var value = i / minorCount * major;
                    var label = new FormattedText(
                        Format(value), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 9, Foreground, dpi);
                    if (horizontal)
                    {
                        context.DrawText(label, new Point(position + 2, 1));
                    }
                    else
                    {
                        // Turned to read from the bottom, like most drawing programs.
                        context.PushTransform(new RotateTransform(-90, 1, position - 2));
                        context.DrawText(label, new Point(1, position - 2));
                        context.Pop();
                    }
                }
            }
        }

        if (!double.IsNaN(CursorPosition) && CursorPosition >= 0 && CursorPosition <= along)
        {
            var cursor = new Pen(CursorBrush, 1);
            context.DrawLine(cursor,
                horizontal ? new Point(CursorPosition, 0) : new Point(0, CursorPosition),
                horizontal ? new Point(CursorPosition, across) : new Point(across, CursorPosition));
        }
    }

    /// <summary>The smallest 1, 2 or 5 × 10ⁿ that is at least <paramref name="minimum"/>.</summary>
    internal static double NiceStep(double minimum)
    {
        if (!(minimum > 0) || !double.IsFinite(minimum))
        {
            return 1;
        }

        var power = Math.Pow(10, Math.Floor(Math.Log10(minimum)));
        foreach (var step in Steps)
        {
            if (step * power >= minimum)
            {
                return step * power;
            }
        }

        return 10 * power;
    }

    private static string Format(double value) =>
        Math.Abs(value) >= 1e6 ? value.ToString("0.###e0", CultureInfo.CurrentCulture) : value.ToString("0.###", CultureInfo.CurrentCulture);

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(Ruler), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));
}
