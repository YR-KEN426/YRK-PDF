using System.Globalization;
using System.Windows.Data;

namespace Yrk.PdfStudio.App.Converters;

/// <summary>Shows a page-number pattern as it will look, for example "{page} / {pages}" as "1 / 10".</summary>
public sealed class PagePresetConverter : IValueConverter
{
    public static PagePresetConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string ?? string.Empty).Replace("{page}", "1", StringComparison.Ordinal).Replace("{pages}", "10", StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
