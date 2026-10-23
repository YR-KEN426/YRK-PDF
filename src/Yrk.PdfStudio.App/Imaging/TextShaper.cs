using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Forms;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>
/// Turns text into positioned glyphs with WPF (DirectWrite), which places Thai vowels and tone marks and
/// picks fallback fonts. The glyphs are then written as real PDF text with an embedded font subset.
/// </summary>
internal static class TextShaper
{
    public const string ThaiFallbackFamily = "Leelawadee UI";

    /// <summary>A shaped line: glyph runs, each in one typeface, with positions in points (y down from the top).</summary>
    public sealed record ShapedLine(IReadOnlyList<ShapedRun> Runs, double Width, double Height, double Baseline);

    public sealed record ShapedRun(GlyphTypeface Typeface, double FontSize, double X, double Baseline, IReadOnlyList<PdfGlyph> Glyphs);

    /// <param name="wrapWidth">Breaks lines to this width in points (multi-line form fields); 0 keeps one line.</param>
    public static ShapedLine Shape(string text, string familyName, bool bold, bool italic, double fontSize, double wrapWidth = 0)
    {
        var typeface = new Typeface(
            new FontFamily(familyName + ", " + ThaiFallbackFamily),
            italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, 1.0);
        if (wrapWidth > 0)
        {
            formatted.MaxTextWidth = wrapWidth;
        }

        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawText(formatted, new Point(0, 0));
        }

        var runs = new List<ShapedRun>();
        Collect(drawing, Matrix.Identity, runs);
        return new ShapedLine(runs, formatted.WidthIncludingTrailingWhitespace, formatted.Height, formatted.Baseline);
    }

    /// <summary>
    /// Builds the PDF text blocks for shaped lines, embedding one font subset per typeface (holding the
    /// glyphs of all the lines).
    /// </summary>
    /// <exception cref="InvalidOperationException">A font does not allow embedding or has no TrueType outlines.</exception>
    public static IReadOnlyList<PdfTextBlock> ToBlocks(IReadOnlyList<(int PageIndex, NormalizedPoint TopLeft, PdfColor Color, ShapedLine Line)> lines)
    {
        var fonts = new Dictionary<GlyphTypeface, PdfEmbeddedFont>();
        foreach (var group in lines.SelectMany(l => l.Line.Runs).GroupBy(r => r.Typeface))
        {
            fonts[group.Key] = Embed(group.Key, group.SelectMany(r => r.Glyphs).Select(g => g.Id).Distinct().ToList());
        }

        return lines.Select(l => new PdfTextBlock(
            l.PageIndex,
            l.TopLeft,
            l.Color,
            l.Line.Runs.Select(r => new PdfGlyphRun(fonts[r.Typeface], r.FontSize, r.X, r.Baseline, r.Glyphs)).ToList())).ToList();
    }

    /// <summary>Shaped text for form fields, embedding one font subset per typeface for all of them.</summary>
    /// <exception cref="InvalidOperationException">A font does not allow embedding or has no TrueType outlines.</exception>
    public static IReadOnlyList<PdfShapedText> ToShapedTexts(IReadOnlyList<ShapedLine> lines)
    {
        var fonts = new Dictionary<GlyphTypeface, PdfEmbeddedFont>();
        foreach (var group in lines.SelectMany(l => l.Runs).GroupBy(r => r.Typeface))
        {
            fonts[group.Key] = Embed(group.Key, group.SelectMany(r => r.Glyphs).Select(g => g.Id).Distinct().ToList());
        }

        return lines.Select(l => new PdfShapedText(
            l.Runs.Select(r => new PdfGlyphRun(fonts[r.Typeface], r.FontSize, r.X, r.Baseline, r.Glyphs)).ToList(),
            l.Width,
            l.Height)).ToList();
    }

    private static void Collect(Drawing drawing, Matrix transform, List<ShapedRun> runs)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                var inner = group.Transform is { } t ? t.Value * transform : transform;
                foreach (var child in group.Children)
                {
                    Collect(child, inner, runs);
                }

                break;
            case GlyphRunDrawing { GlyphRun: { } run }:
                var origin = transform.Transform(run.BaselineOrigin);
                runs.Add(new ShapedRun(run.GlyphTypeface, run.FontRenderingEmSize, origin.X, origin.Y, Glyphs(run)));
                break;
        }
    }

    /// <summary>
    /// Glyphs with their advances and offsets, and the characters each one stands for. Within a cluster,
    /// a glyph that the font maps from one of the cluster's characters gets that character; the other
    /// characters go to the remaining glyphs, aligned to the end (Thai SARA AM: the trailing glyph stands
    /// for it). Glyphs left without a character get no text (the writer draws them as outlines).
    /// </summary>
    private static List<PdfGlyph> Glyphs(GlyphRun run)
    {
        var count = run.GlyphIndices.Count;
        var texts = new string?[count];
        if (run.Characters is { } characters && run.ClusterMap is { } clusters && clusters.Count == characters.Count)
        {
            var cmap = run.GlyphTypeface.CharacterToGlyphMap;
            var starts = clusters.Distinct().Order().ToList();
            for (var k = 0; k < starts.Count; k++)
            {
                var firstGlyph = starts[k];
                var endGlyph = k + 1 < starts.Count ? starts[k + 1] : count;
                var chars = Enumerable.Range(0, characters.Count).Where(i => clusters[i] == firstGlyph).Select(i => characters[i]).ToList();
                var used = new bool[chars.Count];
                for (var g = firstGlyph; g < endGlyph; g++)
                {
                    var match = chars.FindIndex(c => !used[chars.IndexOf(c)] && cmap.TryGetValue(c, out var id) && id == run.GlyphIndices[g]);
                    if (match >= 0)
                    {
                        used[match] = true;
                        texts[g] = chars[match].ToString();
                    }
                }

                var remaining = chars.Where((_, i) => !used[i]).ToList();
                var free = Enumerable.Range(firstGlyph, endGlyph - firstGlyph).Where(g => texts[g] is null).ToList();
                if (remaining.Count == 0)
                {
                    continue;
                }

                if (free.Count == 0)
                {
                    var last = Enumerable.Range(firstGlyph, endGlyph - firstGlyph).Last();
                    texts[last] += new string(remaining.ToArray());
                }
                else if (free.Count >= remaining.Count)
                {
                    for (var i = 0; i < remaining.Count; i++)
                    {
                        texts[free[free.Count - remaining.Count + i]] = remaining[i].ToString();
                    }
                }
                else
                {
                    var extra = remaining.Count - free.Count;
                    texts[free[0]] = new string(remaining.Take(extra + 1).ToArray());
                    for (var i = 1; i < free.Count; i++)
                    {
                        texts[free[i]] = remaining[extra + i].ToString();
                    }
                }
            }
        }

        var glyphs = new List<PdfGlyph>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = run.GlyphOffsets is { Count: > 0 } offsets ? offsets[i] : default;
            var id = run.GlyphIndices[i];
            // Every glyph carries its outline; the writer uses outlines for runs that real text cannot express.
            glyphs.Add(new PdfGlyph(id, run.AdvanceWidths[i], offset.X, offset.Y, texts[i] ?? string.Empty, Outline(run.GlyphTypeface, id, run.FontRenderingEmSize)));
        }

        return glyphs;
    }

    /// <summary>A glyph's outline as PDF path operators (points, y up, origin on the baseline).</summary>
    private static string Outline(GlyphTypeface typeface, ushort glyph, double size)
    {
        var path = PathGeometry.CreateFromGeometry(typeface.GetGlyphOutline(glyph, size, size));
        var text = new System.Text.StringBuilder();
        static string F(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
        void Point(Point p) => text.Append(F(p.X)).Append(' ').Append(F(-p.Y)).Append(' ');

        foreach (var figure in path.Figures)
        {
            var current = figure.StartPoint;
            Point(current);
            text.Append("m\n");
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        Point(line.Point);
                        text.Append("l\n");
                        current = line.Point;
                        break;
                    case PolyLineSegment poly:
                        foreach (var p in poly.Points)
                        {
                            Point(p);
                            text.Append("l\n");
                            current = p;
                        }

                        break;
                    case BezierSegment bezier:
                        Point(bezier.Point1);
                        Point(bezier.Point2);
                        Point(bezier.Point3);
                        text.Append("c\n");
                        current = bezier.Point3;
                        break;
                    case PolyBezierSegment polyBezier:
                        for (var i = 0; i + 2 < polyBezier.Points.Count; i += 3)
                        {
                            Point(polyBezier.Points[i]);
                            Point(polyBezier.Points[i + 1]);
                            Point(polyBezier.Points[i + 2]);
                            text.Append("c\n");
                            current = polyBezier.Points[i + 2];
                        }

                        break;
                    case QuadraticBezierSegment quadratic:
                        current = Quadratic(current, quadratic.Point1, quadratic.Point2);
                        break;
                    case PolyQuadraticBezierSegment polyQuadratic:
                        for (var i = 0; i + 1 < polyQuadratic.Points.Count; i += 2)
                        {
                            current = Quadratic(current, polyQuadratic.Points[i], polyQuadratic.Points[i + 1]);
                        }

                        break;
                }
            }

            if (figure.IsClosed)
            {
                text.Append("h\n");
            }
        }

        return text.Append(path.FillRule == FillRule.EvenOdd ? "f*" : "f").ToString();

        // A quadratic curve written as the equivalent cubic one.
        Point Quadratic(Point from, Point control, Point to)
        {
            Point(new Point(from.X + (2.0 / 3 * (control.X - from.X)), from.Y + (2.0 / 3 * (control.Y - from.Y))));
            Point(new Point(to.X + (2.0 / 3 * (control.X - to.X)), to.Y + (2.0 / 3 * (control.Y - to.Y))));
            Point(to);
            text.Append("c\n");
            return to;
        }
    }

    private static PdfEmbeddedFont Embed(GlyphTypeface typeface, IReadOnlyCollection<ushort> glyphs)
    {
        if (typeface.EmbeddingRights is FontEmbeddingRight.RestrictedLicense)
        {
            throw new InvalidOperationException(Loc.F("The font {0} does not allow embedding.", Name(typeface)));
        }

        using (var stream = typeface.GetFontStream())
        {
            var tag = new byte[4];
            stream.ReadExactly(tag);
            if (tag is [(byte)'O', (byte)'T', (byte)'T', (byte)'O'])
            {
                throw new InvalidOperationException(Loc.F("The font {0} has PostScript outlines, which cannot be embedded yet.", Name(typeface)));
            }
        }

        // Subsets keep the original glyph ids, so glyph id = CID in the PDF.
        var noSubsetting = typeface.EmbeddingRights.ToString().Contains("NoSubsetting", StringComparison.Ordinal);
        byte[] data;
        if (noSubsetting)
        {
            using var stream = typeface.GetFontStream();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            data = copy.ToArray();
        }
        else
        {
            data = typeface.ComputeSubset(glyphs.ToList());
        }

        var widths = glyphs.ToDictionary(g => g, g => typeface.AdvanceWidths.TryGetValue(g, out var w) ? w * 1000 : 0);
        return new PdfEmbeddedFont(Name(typeface), data, widths, typeface.Baseline * 1000, -(typeface.Height - typeface.Baseline) * 1000);
    }

    private static string Name(GlyphTypeface typeface)
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        var family = typeface.Win32FamilyNames.TryGetValue(english, out var f) ? f : typeface.FamilyNames.Values.FirstOrDefault() ?? "Font";
        var face = typeface.Win32FaceNames.TryGetValue(english, out var s) ? s : string.Empty;
        return (family + (face.Length > 0 && face != "Regular" ? "-" + face : string.Empty)).Replace(" ", string.Empty, StringComparison.Ordinal);
    }
}
