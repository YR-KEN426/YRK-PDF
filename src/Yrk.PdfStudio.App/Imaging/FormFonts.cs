using System.IO;
using System.Windows.Media;
using Yrk.PdfStudio.Core.Pdf.Forms;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>
/// The Thai font a form gets for its fields: Sarabun (SIL Open Font License), shipped with the program, so a
/// viewer that redraws a Thai field while someone edits it there has Thai letters.
/// </summary>
internal static class FormFonts
{
    private static readonly Lazy<PdfFormFont?> Sarabun = new(() => Load(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Sarabun-Regular.ttf")));

    /// <summary>Null when the font file is missing from the installation (fields then keep their own /DA font).</summary>
    public static PdfFormFont? Thai => Sarabun.Value;

    internal static PdfFormFont? Load(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            var typeface = new GlyphTypeface(new Uri(path));
            var widths = new Dictionary<char, double>();
            for (var c = ' '; c <= '๛'; c++)
            {
                if (PdfFormFont.Code(c) is not null && typeface.CharacterToGlyphMap.TryGetValue(c, out var glyph))
                {
                    widths[c] = typeface.AdvanceWidths[glyph] * 1000;
                }
            }

            var name = typeface.Win32FamilyNames.Values.FirstOrDefault() ?? "Sarabun";
            return new PdfFormFont(name.Replace(" ", string.Empty, StringComparison.Ordinal) + "-Regular", data, widths,
                typeface.Baseline * 1000, -(typeface.Height - typeface.Baseline) * 1000);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileFormatException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
