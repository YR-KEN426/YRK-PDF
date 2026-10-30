using System.Globalization;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Text;

/// <summary>
/// Writes shaped text as real PDF text: an embedded TrueType subset (Type0, Identity-H) with a ToUnicode
/// map, so the text can be selected, searched and copied. Shaping (Thai vowel and tone mark placement,
/// kerning) is done by the caller; glyph positions are written exactly as given.
/// </summary>
/// <remarks>
/// Character codes are not glyph ids: each distinct (glyph, text) pair gets its own code, mapped to the
/// glyph through /CIDToGIDMap and to its text through /ToUnicode. A glyph that stands for different text
/// in different places (common with Thai marks) therefore extracts correctly everywhere.
/// </remarks>
public static class PdfTextWriter
{
    /// <summary>Vertical glyph offsets up to this share of the font size are dropped to keep real text.</summary>
    private const double MaxRealTextRise = 0.06;

    /// <param name="password">The password of a protected file; the text is encrypted like the rest of it.</param>
    public static byte[] AddText(byte[] pdf, IReadOnlyList<PdfTextBlock> blocks, string? password = null)
    {
        var document = CosDocument.Load(pdf, password);
        if (document.IsLocked)
        {
            throw new PdfException("Password-protected PDFs cannot be edited yet. Save an unprotected copy first.");
        }

        var update = new CosIncrementalUpdate(document);
        AddText(document, update, blocks);
        return update.Build().Bytes;
    }

    internal static void AddText(CosDocument document, CosIncrementalUpdate update, IReadOnlyList<PdfTextBlock> blocks)
    {
        var pages = document.GetPages();
        var fonts = new Dictionary<PdfEmbeddedFont, FontObject>(ReferenceEqualityComparer.Instance);
        foreach (var font in blocks.SelectMany(b => b.Runs).Select(r => r.Font).Distinct(ReferenceEqualityComparer.Instance).Cast<PdfEmbeddedFont>())
        {
            var glyphs = blocks.SelectMany(b => b.Runs).Where(r => ReferenceEquals(r.Font, font)).SelectMany(r => r.Glyphs).Where(g => g.Text.Length > 0);
            fonts[font] = AddFont(update, font, glyphs, fonts.Count);
        }

        foreach (var pageBlocks in blocks.GroupBy(b => b.PageIndex))
        {
            if (pageBlocks.Key < 0 || pageBlocks.Key >= pages.Count)
            {
                continue;
            }

            var page = pages[pageBlocks.Key];
            var edit = new CosPageEdit(document, page);
            var names = new Dictionary<PdfEmbeddedFont, string>(ReferenceEqualityComparer.Instance);
            var layers = new Dictionary<string, string>(StringComparer.Ordinal);
            var states = new Dictionary<double, string>();
            var onTop = new StringBuilder();
            var underneath = new StringBuilder();
            foreach (var block in pageBlocks)
            {
                var content = block.Behind ? underneath : onTop;

                // See-through text gets a graphics state with its opacity (one per opacity on the page).
                var opacity = Math.Clamp(block.Opacity, 0, 1);
                string? state = null;
                if (opacity < 1 && !states.TryGetValue(opacity, out state))
                {
                    state = edit.AddResource("ExtGState", "YrkGS", new CosDictionary()
                        .Set("Type", new CosName("ExtGState"))
                        .Set("ca", new CosReal(opacity))
                        .Set("CA", new CosReal(opacity)));
                    states[opacity] = state;
                }

                if (state is not null)
                {
                    content.Append(CultureInfo.InvariantCulture, $"q /{state} gs\n");
                }

                // Text on a layer is marked as optional content, so it is shown and hidden with the layer.
                var layer = block.LayerId is { } id && LayerReference(id) is { } reference
                    ? layers.TryGetValue(id, out var known) ? known : layers[id] = edit.AddResource("Properties", "YrkL", reference)
                    : null;
                if (layer is not null)
                {
                    content.Append(CultureInfo.InvariantCulture, $"/OC /{layer} BDC\n");
                }

                WriteBlock(content, page, block, font =>
                {
                    if (!names.TryGetValue(font, out var name))
                    {
                        name = edit.AddResource("Font", "YrkT", fonts[font].Reference);
                        names[font] = name;
                    }

                    return (name, fonts[font].Codes);
                });

                if (layer is not null)
                {
                    content.Append("EMC\n");
                }

                if (state is not null)
                {
                    content.Append("Q\n");
                }
            }

            if (underneath.Length > 0)
            {
                edit.Prepend(underneath.ToString());
            }

            if (onTop.Length > 0)
            {
                edit.Append(onTop.ToString());
            }

            edit.Commit(update);
        }
    }

    private static void WriteBlock(StringBuilder content, CosPage page, PdfTextBlock block, Func<PdfEmbeddedFont, (string Name, Dictionary<(ushort, string), int> Codes)> fontOf)
    {
        var (x0, y0, x1, y1) = page.CropBox;
        var (width, height) = page.Rotate is 90 or 270 ? (y1 - y0, x1 - x0) : (x1 - x0, y1 - y0);

        // A point given in points from the block's top-left (y down), as user-space coordinates, with the
        // block's width factor and rotation (counter-clockwise as displayed) applied around its top-left.
        var angle = block.Rotation * Math.PI / 180;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        (double X, double Y) At(double x, double y)
        {
            x *= block.WidthFactor;
            var dx = (x * cos) + (y * sin);
            var dy = (-x * sin) + (y * cos);
            return page.ToUserSpace(block.TopLeft.X + (dx / width), block.TopLeft.Y + (dy / height));
        }

        WriteRuns(content, block.Runs, block.Color, At, fontOf);
    }

    /// <summary>
    /// Writes glyph runs whose positions are in points from a top-left corner (y down); <paramref name="at"/>
    /// turns such a point into user space (it also gives the direction of the text and of "up").
    /// </summary>
    internal static void WriteRuns(
        StringBuilder content, IReadOnlyList<PdfGlyphRun> runs, PdfColor c, Func<double, double, (double X, double Y)> at,
        Func<PdfEmbeddedFont, (string Name, Dictionary<(ushort, string), int> Codes)> fontOf)
    {
        var origin = at(0, 0);
        var right = at(1, 0);
        var below = at(0, 1);
        double ux = right.X - origin.X, uy = right.Y - origin.Y;   // one point to the right, as displayed
        double vx = origin.X - below.X, vy = origin.Y - below.Y;   // one point up, as displayed

        string Matrix(double x, double y)
        {
            var p = at(x, y);
            return $"{F(ux)} {F(uy)} {F(vx)} {F(vy)} {F(p.X)} {F(p.Y)}";
        }

        var color = $"{F(c.R / 255.0)} {F(c.G / 255.0)} {F(c.B / 255.0)}";
        var outlines = new StringBuilder();
        content.Append(CultureInfo.InvariantCulture, $"q BT {color} rg\n");
        foreach (var run in runs)
        {
            var (fontName, codes) = fontOf(run.Font);
            var size = run.FontSize;

            // Real text cannot raise single glyphs without breaking the reading order in viewers, and some
            // glyphs stand for no character. Such runs are drawn as outlines over invisible text.
            var asOutlines = run.Glyphs.All(g => g.Outline is not null)
                && run.Glyphs.Any(g => g.Text.Length == 0 || Math.Abs(g.OffsetY) > MaxRealTextRise * size);

            // One text matrix and one TJ array per run, with numbers moving the pen to the shaped positions:
            // the characters stay in reading order, as word processors write them.
            content.Append(CultureInfo.InvariantCulture, $"/{fontName} {F(size)} Tf {(asOutlines ? 3 : 0)} Tr\n");
            content.Append(Matrix(run.X, run.Baseline)).Append(" Tm\n[");
            var pen = run.X;

            void Move(double distance)
            {
                if (Math.Abs(distance) > 0.0001)
                {
                    content.Append(' ').Append(F(-distance * 1000 / size)).Append(' ');
                }
            }

            foreach (var glyph in run.Glyphs)
            {
                if (asOutlines)
                {
                    outlines.Append(CultureInfo.InvariantCulture, $"q {Matrix(pen + glyph.OffsetX, run.Baseline - glyph.OffsetY)} cm\n")
                        .Append(glyph.Outline).Append("\nQ\n");
                }

                if (glyph.Text.Length == 0)
                {
                    Move(glyph.Advance); // Outline only: nothing in the text.
                }
                else
                {
                    var fontAdvance = (run.Font.Widths.TryGetValue(glyph.Id, out var w) ? w : 0) / 1000 * size;
                    Move(glyph.OffsetX);
                    content.Append(CultureInfo.InvariantCulture, $"<{codes[(glyph.Id, glyph.Text)]:X4}>");
                    Move(glyph.Advance - glyph.OffsetX - fontAdvance);
                }

                pen += glyph.Advance;
            }

            content.Append("] TJ\n");
        }

        content.Append("ET\n");
        if (outlines.Length > 0)
        {
            content.Append(CultureInfo.InvariantCulture, $"{color} rg\n").Append(outlines);
        }

        content.Append("Q\n");
    }

    internal sealed record FontObject(CosReference Reference, Dictionary<(ushort, string), int> Codes);

    internal static FontObject AddFont(CosIncrementalUpdate update, PdfEmbeddedFont font, IEnumerable<PdfGlyph> glyphs, int index)
    {
        // Code 0 is left unused; codes 1… are the distinct (glyph, text) pairs.
        var codes = new Dictionary<(ushort, string), int>();
        foreach (var glyph in glyphs)
        {
            codes.TryAdd((glyph.Id, glyph.Text), codes.Count + 1);
        }

        if (codes.Count > 65534)
        {
            throw new PdfException("Too much distinct text for one font.");
        }

        var tag = new string(Enumerable.Range(0, 6).Select(i => (char)('A' + (((index * 7) + (i * 3) + font.PostScriptName.Length) % 26))).ToArray());
        var baseFont = tag + "+" + string.Concat(font.PostScriptName.Where(ch => ch is > ' ' and < (char)127 and not ('(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%')));

        var fontFile = update.Add(CosPageEdit.Stream(font.Data, new CosDictionary().Set("Length1", new CosInteger(font.Data.Length))));
        var descriptor = update.Add(new CosDictionary()
            .Set("Type", new CosName("FontDescriptor"))
            .Set("FontName", new CosName(baseFont))
            .Set("Flags", new CosInteger(4))
            .Set("FontBBox", CosArray.FromNumbers(-1000, font.Descent, 2000, font.Ascent))
            .Set("ItalicAngle", new CosInteger(0))
            .Set("Ascent", new CosReal(font.Ascent))
            .Set("Descent", new CosReal(font.Descent))
            .Set("CapHeight", new CosReal(font.Ascent))
            .Set("StemV", new CosInteger(80))
            .Set("FontFile2", fontFile));

        var cidToGid = new byte[(codes.Count + 1) * 2];
        var widths = new CosArray();
        var widthList = new CosArray();
        foreach (var ((id, _), code) in codes.OrderBy(c => c.Value))
        {
            cidToGid[code * 2] = (byte)(id >> 8);
            cidToGid[(code * 2) + 1] = (byte)id;
            widthList.Items.Add(new CosReal(font.Widths.TryGetValue(id, out var w) ? w : 0));
        }

        if (codes.Count > 0)
        {
            widths.Items.Add(new CosInteger(1));
            widths.Items.Add(widthList);
        }

        var cidFont = update.Add(new CosDictionary()
            .Set("Type", new CosName("Font"))
            .Set("Subtype", new CosName("CIDFontType2"))
            .Set("BaseFont", new CosName(baseFont))
            .Set("CIDSystemInfo", new CosDictionary()
                .Set("Registry", new CosString("Adobe"u8.ToArray()))
                .Set("Ordering", new CosString("Identity"u8.ToArray()))
                .Set("Supplement", new CosInteger(0)))
            .Set("FontDescriptor", descriptor)
            .Set("W", widths)
            .Set("CIDToGIDMap", update.Add(CosPageEdit.Stream(cidToGid))));

        var reference = update.Add(new CosDictionary()
            .Set("Type", new CosName("Font"))
            .Set("Subtype", new CosName("Type0"))
            .Set("BaseFont", new CosName(baseFont))
            .Set("Encoding", new CosName("Identity-H"))
            .Set("DescendantFonts", new CosArray([cidFont]))
            .Set("ToUnicode", update.Add(CosPageEdit.Stream(ToUnicode(codes)))));
        return new FontObject(reference, codes);
    }

    private static byte[] ToUnicode(Dictionary<(ushort Id, string Text), int> codes)
    {
        var text = new StringBuilder();
        text.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        text.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        text.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        text.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (var chunk in codes.Where(c => c.Key.Text.Length > 0).OrderBy(c => c.Value).Chunk(100))
        {
            text.Append(chunk.Length).Append(" beginbfchar\n");
            foreach (var (key, code) in chunk)
            {
                text.Append(CultureInfo.InvariantCulture, $"<{code:X4}> <{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(key.Text))}>\n");
            }

            text.Append("endbfchar\n");
        }

        text.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    /// <summary>"12 0" → a reference to object 12.</summary>
    private static CosReference? LayerReference(string id)
    {
        var parts = id.Split(' ');
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            ? new CosReference(number, generation)
            : null;
    }

    private static string F(double value) => CosWriter.FormatReal(value);
}
