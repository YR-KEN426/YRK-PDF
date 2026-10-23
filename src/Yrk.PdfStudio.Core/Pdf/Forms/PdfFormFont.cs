using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Forms;

/// <summary>
/// A whole TrueType font for a form's default resources (/DR), so that viewers which redraw a field while
/// someone types in it have Thai letters: the field fonts PDFs usually name (Helvetica) have none.
/// </summary>
/// <param name="Name">The font's PostScript name, e.g. "Sarabun-Regular".</param>
/// <param name="Data">The complete font file (not a subset: people may type any letter).</param>
/// <param name="Widths">Advance width of each character the font has, in 1/1000 em.</param>
/// <param name="Ascent">In 1/1000 em, positive.</param>
/// <param name="Descent">In 1/1000 em, negative.</param>
public sealed record PdfFormFont(string Name, byte[] Data, IReadOnlyDictionary<char, double> Widths, double Ascent, double Descent)
{
    /// <summary>The name fields use for it in their /DA.</summary>
    public const string ResourceName = "YrkTh";

    private const int FirstCode = 32;
    private const int LastCode = 0xFB;

    /// <summary>
    /// The one-byte code of a character: ASCII as is, Thai at its TIS-620 place (U+0E01 → 0xA1), as Thai
    /// fonts for Windows always have. Null for other characters.
    /// </summary>
    public static int? Code(char c) => c switch
    {
        >= ' ' and <= '~' => c,
        >= 'ก' and <= '๛' => c - 0x0E00 + 0xA0,
        _ => null,
    };

    internal CosReference AddTo(CosIncrementalUpdate update)
    {
        var widths = new CosArray();
        var differences = new CosArray();
        differences.Items.Add(new CosInteger(0xA1));
        for (var code = FirstCode; code <= LastCode; code++)
        {
            var c = code < 0x7F ? (char)code : code >= 0xA1 ? (char)(code - 0xA0 + 0x0E00) : '\0';
            widths.Items.Add(new CosReal(c != '\0' && Widths.TryGetValue(c, out var width) ? width : 0));
            if (code >= 0xA1)
            {
                differences.Items.Add(new CosName($"uni{(int)c:X4}"));
            }
        }

        var fontFile = update.Add(CosPageEdit.Stream(Data, new CosDictionary().Set("Length1", new CosInteger(Data.Length))));
        var baseFont = string.Concat(Name.Where(ch => ch is > ' ' and < (char)127 and not ('(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%')));
        var descriptor = update.Add(new CosDictionary()
            .Set("Type", new CosName("FontDescriptor"))
            .Set("FontName", new CosName(baseFont))
            .Set("Flags", new CosInteger(32)) // Nonsymbolic: the encoding below applies.
            .Set("FontBBox", CosArray.FromNumbers(-500, Descent, 1500, Ascent))
            .Set("ItalicAngle", new CosInteger(0))
            .Set("Ascent", new CosReal(Ascent))
            .Set("Descent", new CosReal(Descent))
            .Set("CapHeight", new CosReal(Ascent))
            .Set("StemV", new CosInteger(80))
            .Set("FontFile2", fontFile));
        return update.Add(new CosDictionary()
            .Set("Type", new CosName("Font"))
            .Set("Subtype", new CosName("TrueType"))
            .Set("BaseFont", new CosName(baseFont))
            .Set("FirstChar", new CosInteger(FirstCode))
            .Set("LastChar", new CosInteger(LastCode))
            .Set("Widths", widths)
            .Set("Encoding", new CosDictionary()
                .Set("Type", new CosName("Encoding"))
                .Set("BaseEncoding", new CosName("WinAnsiEncoding"))
                .Set("Differences", differences))
            .Set("FontDescriptor", descriptor)
            .Set("ToUnicode", update.Add(CosPageEdit.Stream(Encoding.ASCII.GetBytes(ToUnicode())))));
    }

    /// <summary>
    /// Code → character for every code: viewers read it backwards to find the code of a letter someone types
    /// (PDFium does only this), and forwards for copy and search.
    /// </summary>
    private static string ToUnicode() =>
        "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
        "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n" +
        "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
        "2 beginbfrange\n<20> <7E> <0020>\n<A1> <FB> <0E01>\nendbfrange\n" +
        "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";
}
