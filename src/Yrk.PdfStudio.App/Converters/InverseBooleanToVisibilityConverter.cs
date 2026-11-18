using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Yrk.PdfStudio.App.Converters;

/// <summary>True gives Collapsed, false (or anything else) gives Visible: for what shows only while there is no document.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
