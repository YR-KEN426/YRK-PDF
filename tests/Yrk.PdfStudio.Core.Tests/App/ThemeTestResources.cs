using System.Windows;

namespace Yrk.PdfStudio.Core.Tests;

internal static class ThemeTestResources
{
    public static readonly string[] PaletteNames = ["Fixed", "Light", "Dark", "HighContrast"];

    // Publish this before Styles is parsed, just as App.xaml does.
    public static ResourceDictionary Create(string palette = "Light") => new()
    {
        MergedDictionaries = { Load("Fixed"), Load(palette) }
    };

    public static ResourceDictionary Load(string palette)
    {
        if (!PaletteNames.Contains(palette, StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(palette));
        return new ResourceDictionary
        {
            Source = new Uri($"/YrkEasyPdf;component/Themes/Colors.{palette}.xaml", UriKind.Relative)
        };
    }

    public static string AppSource
    {
        get
        {
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                var source = Path.Combine(folder.FullName, "src", "Yrk.PdfStudio.App");
                if (Directory.Exists(source)) return source;
            }
            throw new DirectoryNotFoundException("Cannot locate src/Yrk.PdfStudio.App for palette tests.");
        }
    }
}
