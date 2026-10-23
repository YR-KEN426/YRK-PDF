using System.Globalization;
using System.Windows.Data;

namespace Yrk.PdfStudio.App.Converters;

/// <summary>Binds a radio button's IsChecked to "value equals ConverterParameter".</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static EnumEqualsConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) => Equals(value, parameter);

    public object? ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}
