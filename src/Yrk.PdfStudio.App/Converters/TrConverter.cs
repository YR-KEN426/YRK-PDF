using System.Globalization;
using System.Windows.Data;
using Yrk.PdfStudio.App.Localization;

namespace Yrk.PdfStudio.App.Converters;

/// <summary>Shows a bound English value (an enum or a fixed name) in the UI language.</summary>
public sealed class TrConverter : IValueConverter
{
    public static TrConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? string.Empty : Loc.T(value.ToString() ?? string.Empty);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
