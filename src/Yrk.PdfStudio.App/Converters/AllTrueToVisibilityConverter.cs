using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Yrk.PdfStudio.App.Converters;

/// <summary>Visible only when every bound value is <c>true</c>.</summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public static AllTrueToVisibilityConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(value => value is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
