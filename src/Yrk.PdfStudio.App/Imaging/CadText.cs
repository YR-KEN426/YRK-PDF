using System.IO;
using System.Windows.Media;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cad;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>
/// Turns the texts of a converted drawing into real PDF text: picks an installed font for the drawing's
/// font file, sizes it so capital letters have AutoCAD's text height, and applies alignment and rotation.
/// </summary>
internal static class CadText
{
    private const string LatinFallback = "Arial";

    /// <summary>AutoCAD's single-stroke SHX fonts have no Windows equivalent; Arial is the usual stand-in.</summary>
    private static readonly HashSet<string> ShxLatin = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "romans", "romand", "romanc", "romant", "simplex", "complex", "italic", "isocp", "isocp2", "isocp3",
        "isoct", "iso", "monotxt", "gothice", "scripts", "greeks", "syastro", "symath",
    };

    private static readonly Lazy<Dictionary<string, string>> FamiliesByFile = new(IndexInstalledFonts);
    private static readonly Dictionary<string, double> CapsRatios = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="pages">The size of each page of the converted drawing (texts name their page).</param>
    public static IReadOnlyList<PdfTextBlock> Shape(IReadOnlyList<CadTextItem> items, IReadOnlyList<CadPageInfo> pages)
    {
        var lines = new List<(int PageIndex, NormalizedPoint TopLeft, PdfColor Color, TextShaper.ShapedLine Line)>();
        var kept = new List<CadTextItem>();
        foreach (var item in items)
        {
            var family = ResolveFamily(item.FontFile, item.Text);
            var size = item.Height / CapsRatio(family);
            if (size <= 0 || !double.IsFinite(size))
            {
                continue;
            }

            var shaped = TextShaper.Shape(item.Text, family, false, false, size);
            if (shaped.Runs.Count == 0)
            {
                continue;
            }

            // Where the baseline starts, from the anchor: along the text by the alignment, up or down by the
            // vertical anchor (text-relative axes, y up), then rotated.
            var width = shaped.Width * item.WidthFactor;
            var along = -item.HorizontalAlign * width;
            var up = item.Vertical switch
            {
                CadTextVertical.Bottom => shaped.Height - shaped.Baseline,
                CadTextVertical.Middle => -item.Height / 2,
                CadTextVertical.Top => -item.Height,
                _ => 0,
            };
            var angle = item.Rotation * Math.PI / 180;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            var baseX = item.X + (along * cos) - (up * sin);
            var baseY = item.Y + (along * sin) + (up * cos);

            // The block's top-left is one baseline-distance "up" from the baseline start.
            var topX = baseX - (sin * shaped.Baseline);
            var topY = baseY + (cos * shaped.Baseline);
            var page = pages[item.PageIndex];
            lines.Add((item.PageIndex, new NormalizedPoint(topX / page.Width, 1 - (topY / page.Height)), item.Color, shaped));
            kept.Add(item);
        }

        IReadOnlyList<PdfTextBlock> blocks;
        try
        {
            blocks = TextShaper.ToBlocks(lines);
        }
        catch (InvalidOperationException)
        {
            // A font that cannot be embedded: use fonts that can.
            lines = lines.Select((l, i) => (l.PageIndex, l.TopLeft, l.Color, TextShaper.Shape(kept[i].Text, TextShaper.ThaiFallbackFamily, false, false, kept[i].Height / CapsRatio(TextShaper.ThaiFallbackFamily)))).ToList();
            blocks = TextShaper.ToBlocks(lines);
        }

        return blocks.Select((b, i) => b with { Rotation = kept[i].Rotation, WidthFactor = kept[i].WidthFactor, LayerId = kept[i].LayerId }).ToList();
    }

    /// <summary>An installed family for a drawing font file ("romans.shx", "AngsanaUPC.ttf", "arial").</summary>
    internal static string ResolveFamily(string fontFile, string text)
    {
        var name = Path.GetFileNameWithoutExtension(fontFile.Trim());
        var thai = text.Any(c => c is >= '฀' and <= '๿');
        if (FamiliesByFile.Value.TryGetValue(name, out var byFile))
        {
            return byFile;
        }

        if (ShxLatin.Contains(name))
        {
            return thai ? TextShaper.ThaiFallbackFamily : LatinFallback;
        }

        // Thai SHX fonts and Thai TrueType fonts that are not installed.
        foreach (var candidate in new[] { "Angsana New", "AngsanaUPC", "Cordia New", "CordiaUPC", "Browallia New", "TH Sarabun New", "TH SarabunPSK" })
        {
            if (name.StartsWith(candidate.Split(' ')[0], StringComparison.OrdinalIgnoreCase) && FamiliesByFile.Value.Values.Contains(candidate))
            {
                return candidate;
            }
        }

        return thai ? TextShaper.ThaiFallbackFamily : LatinFallback;
    }

    /// <summary>Capital height as a share of the em, so text height = capital height, as in AutoCAD.</summary>
    private static double CapsRatio(string family)
    {
        if (!CapsRatios.TryGetValue(family, out var ratio))
        {
            ratio = new Typeface(family).TryGetGlyphTypeface(out var glyphs) && glyphs.CapsHeight > 0.3 ? glyphs.CapsHeight : 0.7;
            CapsRatios[family] = ratio;
        }

        return ratio;
    }

    /// <summary>Font file name (without extension) → family, for the fonts installed in Windows.</summary>
    private static Dictionary<string, string> IndexInstalledFonts()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Fonts.SystemFontFamilies)
        {
            index.TryAdd(family.Source, family.Source);
            foreach (var typeface in family.GetTypefaces())
            {
                try
                {
                    if (typeface.TryGetGlyphTypeface(out var glyphs) && glyphs.FontUri is { IsFile: true } uri)
                    {
                        index.TryAdd(Path.GetFileNameWithoutExtension(uri.LocalPath), family.Source);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A broken font file: skip it.
                }
            }
        }

        return index;
    }
}
