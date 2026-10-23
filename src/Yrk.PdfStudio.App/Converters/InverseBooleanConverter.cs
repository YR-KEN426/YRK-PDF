using System.Globalization;
using System.Windows.Data;

namespace Yrk.PdfStudio.App.Converters;

public sealed class InverseBooleanConverter : IValueConverter
{
    public static InverseBooleanConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
