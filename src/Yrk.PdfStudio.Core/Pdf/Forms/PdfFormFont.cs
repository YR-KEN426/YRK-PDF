using System.Globalization;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Forms;

/// <summary>
/// A whole TrueType font for a form's default resources (/DR), so that viewers which redraw a field while
/// someone types in it have Thai letters: the field fonts PDFs usually name (Helvetica) have none.
/// </summary>
/// <remarks>
/// It is written as a two-byte font (Type0, Identity-H: each code is a glyph of the font), the kind Acrobat
/// itself writes typed Thai with. Acrobat cannot type with a one-byte font whose letters come from a
/// /Differences list: it drew spaces as "•", took Thai from a font of its own, and would not type English.
/// Viewers find the code of a typed letter through /ToUnicode.
/// </remarks>
/// <param name="Name">The font's PostScript name, e.g. "Sarabun-Regular".</param>
/// <param name="Data">The complete font file (not a subset: people may type any letter).</param>
/// <param name="Widths">Advance width of each character the font has, in 1/1000 em.</param>
/// <param name="Glyphs">The glyph of each character in <paramref name="Widths"/>.</param>
/// <param name="Ascent">In 1/1000 em, positive.</param>
/// <param name="Descent">In 1/1000 em, negative.</param>
public sealed record PdfFormFont(
    string Name, byte[] Data, IReadOnlyDictionary<char, double> Widths, IReadOnlyDictionary<char, ushort> Glyphs, double Ascent, double Descent)
{
    /// <summary>The name fields use for it in their /DA.</summary>
    public const string ResourceName = "YrkTh";

    /// <summary>
    /// The characters a form font carries: printable ASCII, the no-break space (Acrobat types it after a
    /// Thai word) and Thai.
    /// </summary>
    public static bool Covers(char c) => c is >= ' ' and <= '~' or ' ' or >= 'ก' and <= '๛';

    internal CosReference AddTo(CosIncrementalUpdate update)
    {
        // One glyph per code; a glyph two characters share keeps the first (space before no-break space).
        var byGlyph = new SortedDictionary<ushort, char>();
        foreach (var (c, glyph) in Glyphs.OrderBy(g => g.Key))
        {
            byGlyph.TryAdd(glyph, c);
        }

        var widths = new CosArray();
        foreach (var (glyph, c) in byGlyph)
        {
            widths.Items.Add(new CosInteger(glyph));
            widths.Items.Add(new CosArray([new CosReal(Widths.GetValueOrDefault(c))]));
        }

        var fontFile = update.Add(CosPageEdit.Stream(Data, new CosDictionary().Set("Length1", new CosInteger(Data.Length))));
        var baseFont = string.Concat(Name.Where(ch => ch is > ' ' and < (char)127 and not ('(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%')));
        var descriptor = update.Add(new CosDictionary()
            .Set("Type", new CosName("FontDescriptor"))
            .Set("FontName", new CosName(baseFont))
            .Set("Flags", new CosInteger(32))
            .Set("FontBBox", CosArray.FromNumbers(-500, Descent, 1500, Ascent))
            .Set("ItalicAngle", new CosInteger(0))
            .Set("Ascent", new CosReal(Ascent))
            .Set("Descent", new CosReal(Descent))
            .Set("CapHeight", new CosReal(Ascent))
            .Set("StemV", new CosInteger(80))
            .Set("FontFile2", fontFile));
        var cidFont = update.Add(new CosDictionary()
            .Set("Type", new CosName("Font"))
            .Set("Subtype", new CosName("CIDFontType2"))
            .Set("BaseFont", new CosName(baseFont))
            .Set("CIDSystemInfo", new CosDictionary()
                .Set("Registry", CosString.FromText("Adobe"))
                .Set("Ordering", CosString.FromText("Identity"))
                .Set("Supplement", new CosInteger(0)))
            .Set("FontDescriptor", descriptor)
            .Set("DW", new CosInteger(1000))
            .Set("W", widths)
            .Set("CIDToGIDMap", new CosName("Identity")));
        return update.Add(new CosDictionary()
            .Set("Type", new CosName("Font"))
            .Set("Subtype", new CosName("Type0"))
            .Set("BaseFont", new CosName(baseFont))
            .Set("Encoding", new CosName("Identity-H"))
            .Set("DescendantFonts", new CosArray([cidFont]))
            .Set("ToUnicode", update.Add(CosPageEdit.Stream(Encoding.ASCII.GetBytes(ToUnicode(byGlyph))))));
    }

    /// <summary>
    /// Code (glyph) → character: viewers read it backwards to find the code of a letter someone types
    /// (PDFium does only this), and forwards for copy and search.
    /// </summary>
    private static string ToUnicode(SortedDictionary<ushort, char> byGlyph)
    {
        var text = new StringBuilder(
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (var chunk in byGlyph.Chunk(100)) // At most 100 entries per block.
        {
            text.Append(CultureInfo.InvariantCulture, $"{chunk.Length} beginbfchar\n");
            foreach (var (glyph, c) in chunk)
            {
                text.Append(CultureInfo.InvariantCulture, $"<{glyph:X4}> <{(int)c:X4}>\n");
            }

            text.Append("endbfchar\n");
        }

        return text.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n").ToString();
    }
}
