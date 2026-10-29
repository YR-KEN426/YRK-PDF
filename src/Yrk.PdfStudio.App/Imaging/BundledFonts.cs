using System.IO;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>
/// Sarabun (SIL Open Font License) ships with the program (Assets\Fonts). Many Thai documents use it (Word files
/// made in Thai offices, for one), so text edited in them keeps its font even where Sarabun is not installed.
/// </summary>
public static class BundledFonts
{
    public const string Sarabun = "Sarabun";

    private static readonly Lazy<string?> SarabunLocation = new(() =>
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        return File.Exists(Path.Combine(folder, "Sarabun-Regular.ttf")) && !IsInstalled(Sarabun)
            ? new Uri(folder + Path.DirectorySeparatorChar).AbsoluteUri + "#" + Sarabun
            : null;
    });

    /// <summary>Whether Sarabun is available here, installed or shipped with the program.</summary>
    public static bool HasSarabun => SarabunLocation.Value is not null || IsInstalled(Sarabun);

    /// <summary>
    /// A WPF family name (a comma-separated list) with the shipped Sarabun located where it is not installed:
    /// "Sarabun, Leelawadee UI" → "file:///…/Assets/Fonts/#Sarabun, Leelawadee UI".
    /// </summary>
    public static string Locate(string families) =>
        SarabunLocation.Value is not { } location
            ? families
            : string.Join(", ", families.Split(',').Select(f => f.Trim()).Select(f => string.Equals(f, Sarabun, StringComparison.OrdinalIgnoreCase) ? location : f));

    private static bool IsInstalled(string family) =>
        System.Windows.Media.Fonts.SystemFontFamilies.Any(f => string.Equals(f.Source, family, StringComparison.OrdinalIgnoreCase));
}
