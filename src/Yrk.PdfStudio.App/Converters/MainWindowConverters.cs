using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Yrk.PdfStudio.App.Converters;

// Bind on the owning element, not inside a Freezable: detached/replaced templates
// cannot supply a governing element or ancestor for bindings on brushes/transforms.
public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color color) return DependencyProperty.UnsetValue;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class OverlayScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double unit) return DependencyProperty.UnsetValue;
        var offset = parameter is null ? 0 : double.Parse((string)parameter, CultureInfo.InvariantCulture);
        // Preserve the original order: translate the marker to its centre, then scale.
        var transform = new MatrixTransform(unit, 0, 0, unit, -offset * unit, -offset * unit);
        transform.Freeze();
        return transform;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
