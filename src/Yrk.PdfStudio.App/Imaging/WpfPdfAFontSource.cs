using System.IO;
using System.Windows;
using System.Windows.Media;
using Yrk.PdfStudio.Core.Pdf.Archive;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>
/// Finds installed TrueType fonts for PDF/A conversion through WPF: the font file, its character widths and
/// metrics. Only real matches count (WPF would otherwise fall back to another family), and only single .ttf
/// files, which PDF can embed as they are.
/// </summary>
internal sealed class WpfPdfAFontSource : IPdfAFontSource
{
    public PdfAFont? Find(string family, bool bold, bool italic)
    {
        var typeface = new Typeface(
            new FontFamily(family),
            italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out var glyphs)
            || !glyphs.Win32FamilyNames.Values.Concat(glyphs.FamilyNames.Values).Any(n => string.Equals(n, family, StringComparison.OrdinalIgnoreCase))
            || glyphs.FontUri is not { IsFile: true } uri
            || !uri.LocalPath.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(uri.LocalPath))
        {
            return null;
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(uri.LocalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var map = glyphs.CharacterToGlyphMap;
        var widths = glyphs.AdvanceWidths;
        double? Width(char c) => map.TryGetValue(c, out var glyph) && widths.TryGetValue(glyph, out var width) ? Math.Round(width * 1000, 2) : null;

        var style = (bold, italic) switch
        {
            (true, true) => ",BoldItalic",
            (true, false) => ",Bold",
            (false, true) => ",Italic",
            _ => string.Empty,
        };
        var ascent = glyphs.Baseline * 1000;
        var descent = -(glyphs.Height - glyphs.Baseline) * 1000;
        return new PdfAFont(
            family.Replace(" ", string.Empty, StringComparison.Ordinal) + style,
            data,
            Width,
            Math.Round(ascent),
            Math.Round(descent),
            Math.Round(glyphs.CapsHeight * 1000),
            italic ? -12 : 0,
            (-600, Math.Round(descent), 2000, Math.Round(ascent)),
            bold);
    }
}
