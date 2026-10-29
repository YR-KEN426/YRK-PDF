using System.Windows;
using System.Windows.Media;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>The look of text people type: added text, form fields and edited lines all use it.</summary>
public sealed record TextStyle(string FontFamilyName, double FontSize, bool IsBold, bool IsItalic, Color Color)
{
    public static TextStyle Default { get; } = new(TextStyles.DefaultFamily, 12, false, false, Colors.Black);

    public FontFamily FontFamily => new(Imaging.BundledFonts.Locate(FontFamilyName + ", " + TextStyles.DefaultFamily));

    public FontWeight Weight => IsBold ? FontWeights.Bold : FontWeights.Normal;

    public FontStyle Style => IsItalic ? FontStyles.Italic : FontStyles.Normal;

    public Typeface Typeface => new(FontFamily, Style, Weight, FontStretches.Normal);
}

/// <summary>What the one font bar on the toolbar offers, the same for every kind of text.</summary>
public static class TextStyles
{
    /// <summary>The font text is typed in unless the user picks another (it has Thai and Latin letters).</summary>
    public const string DefaultFamily = "Leelawadee UI";

    private static readonly string[] Recommended =
        [DefaultFamily, Imaging.BundledFonts.Sarabun, "TH Sarabun New", "TH SarabunPSK", "Tahoma", "Angsana New", "Cordia New", "Browallia New", "Segoe UI", "Arial", "Times New Roman"];

    /// <summary>Installed families, and Sarabun, which ships with the program.</summary>
    private static readonly Lazy<Dictionary<string, string>> Installed = new(() =>
    {
        var families = System.Windows.Media.Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .GroupBy(Normalize, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (Imaging.BundledFonts.HasSarabun)
        {
            families.TryAdd(Normalize(Imaging.BundledFonts.Sarabun), Imaging.BundledFonts.Sarabun);
        }

        return families;
    });

    /// <summary>Fonts with Thai letters first (those installed), then every other installed font.</summary>
    public static IReadOnlyList<string> Fonts { get; } = BuildFontList();

    public static IReadOnlyList<double> Sizes { get; } = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 72];

    public static IReadOnlyList<Color> Colors { get; } =
    [
        System.Windows.Media.Colors.Black,
        Color.FromRgb(0x1A, 0x4F, 0xA0), // blue
        Color.FromRgb(0xC6, 0x28, 0x28), // red
        Color.FromRgb(0x2E, 0x7D, 0x32), // green
        Color.FromRgb(0x61, 0x61, 0x61), // grey
    ];

    /// <summary>The installed family for a typed or picked name ("th sarabun new" → "TH Sarabun New"), or null.</summary>
    public static string? Resolve(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Installed.Value.TryGetValue(Normalize(name), out var family) ? family : null;

    internal static string Normalize(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static List<string> BuildFontList()
    {
        var first = Recommended.Select(Resolve).OfType<string>().Distinct().ToList();
        if (!first.Contains(DefaultFamily))
        {
            first.Insert(0, DefaultFamily);
        }

        var rest = Installed.Value.Values.Except(first).Order(StringComparer.CurrentCultureIgnoreCase);
        return [.. first, .. rest];
    }
}
