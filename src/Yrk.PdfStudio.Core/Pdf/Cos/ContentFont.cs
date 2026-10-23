using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Cos;

/// <summary>
/// What redaction needs to know about a font: how to split a string into character codes and how far
/// each code advances. Fonts it cannot measure exactly (Type 3, vertical writing, simple fonts without
/// /Widths, unusual CMaps) are marked unsupported, and pages that use them where text must be removed are
/// converted to images instead.
/// </summary>
internal sealed class ContentFont
{
    private const double DefaultAscent = 0.85;
    private const double DefaultDescent = -0.25;

    private readonly Dictionary<int, double> _widths = [];
    private double _defaultWidth;

    private ContentFont()
    {
    }

    public bool IsSupported { get; private init; }

    /// <summary>1 for simple fonts, 2 for Identity-H and similar CID fonts.</summary>
    public int BytesPerCode { get; private init; } = 1;

    /// <summary>Ascent and descent in text space units (1 = font size).</summary>
    public double Ascent { get; private set; } = DefaultAscent;

    public double Descent { get; private set; } = DefaultDescent;

    public static ContentFont Unsupported { get; } = new() { IsSupported = false };

    /// <summary>Glyph advance in text space units (glyph-space width / 1000).</summary>
    public double Width(int code) => (_widths.TryGetValue(code, out var width) ? width : _defaultWidth) / 1000.0;

    public IEnumerable<int> Codes(byte[] bytes)
    {
        for (var i = 0; i + BytesPerCode <= bytes.Length; i += BytesPerCode)
        {
            yield return BytesPerCode == 2 ? (bytes[i] << 8) | bytes[i + 1] : bytes[i];
        }
    }

    public static ContentFont Load(CosDocument document, CosDictionary? font)
    {
        if (font is null)
        {
            return Unsupported;
        }

        var subtype = (document.Resolve(font["Subtype"]) as CosName)?.Value;
        ContentFont result;
        CosDictionary? descriptor;
        if (subtype == "Type0")
        {
            var bytes = CodeLength(document, document.Resolve(font["Encoding"]));
            var descendant = (document.Resolve(font["DescendantFonts"]) as CosArray)?.Items.Select(document.Resolve).OfType<CosDictionary>().FirstOrDefault();
            if (bytes is null || descendant is null)
            {
                return Unsupported;
            }

            result = new ContentFont { IsSupported = true, BytesPerCode = bytes.Value };
            result._defaultWidth = document.Resolve(descendant["DW"]) is CosInteger or CosReal ? PdfMatrix.Number(document.Resolve(descendant["DW"])!) : 1000;

            ReadCidWidths(document, document.Resolve(descendant["W"]) as CosArray, result._widths);
            descriptor = document.Resolve(descendant["FontDescriptor"]) as CosDictionary;
        }
        else if (subtype is "Type1" or "TrueType" or "MMType1")
        {
            descriptor = document.Resolve(font["FontDescriptor"]) as CosDictionary;
            if (document.Resolve(font["Widths"]) is not CosArray widths)
            {
                return Unsupported; // Standard 14 fonts without /Widths: metrics would be guesses.
            }

            result = new ContentFont { IsSupported = true, BytesPerCode = 1 };
            var first = (int)PdfMatrix.Number(document.Resolve(font["FirstChar"]) ?? new CosInteger(0));
            for (var i = 0; i < widths.Count; i++)
            {
                result._widths[first + i] = PdfMatrix.Number(document.Resolve(widths[i]) ?? new CosInteger(0));
            }

            result._defaultWidth = descriptor?["MissingWidth"] is { } missing ? PdfMatrix.Number(document.Resolve(missing) ?? new CosInteger(0)) : 0;
        }
        else
        {
            return Unsupported; // Type 3 (glyphs are drawn with content streams) and unknown types.
        }

        if (descriptor is not null)
        {
            var ascent = PdfMatrix.Number(document.Resolve(descriptor["Ascent"]) ?? new CosInteger(0)) / 1000;
            var descent = PdfMatrix.Number(document.Resolve(descriptor["Descent"]) ?? new CosInteger(0)) / 1000;
            if (ascent > 0.2)
            {
                result.Ascent = Math.Max(ascent, DefaultAscent * 0.8);
            }

            if (descent < 0)
            {
                result.Descent = Math.Min(descent, DefaultDescent * 0.5);
            }
        }

        return result;
    }

    /// <summary>Bytes per code for a Type0 /Encoding: Identity-H and UCS-2/UTF-16 CMaps, or an embedded CMap.</summary>
    private static int? CodeLength(CosDocument document, CosObject? encoding)
    {
        switch (encoding)
        {
            case CosName { Value: var name } when name.EndsWith("-V", StringComparison.Ordinal):
                return null; // Vertical writing.
            case CosName { Value: "Identity-H" }:
            case CosName { Value: var ucs } when ucs.Contains("UCS2", StringComparison.Ordinal) || ucs.Contains("UTF16", StringComparison.Ordinal):
                return 2;
            case CosStream stream:
                if (document.Resolve(stream.Dictionary["WMode"]) is CosInteger { Value: 1 })
                {
                    return null;
                }

                var text = Encoding.Latin1.GetString(CosFilters.Decode(stream, document.Resolve));
                var lengths = new HashSet<int>();
                var at = 0;
                while ((at = text.IndexOf("begincodespacerange", at, StringComparison.Ordinal)) >= 0)
                {
                    var end = text.IndexOf("endcodespacerange", at, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        break;
                    }

                    foreach (var token in text[at..end].Split('<', '>').Where((_, i) => i % 2 == 1))
                    {
                        lengths.Add(token.Trim().Length / 2);
                    }

                    at = end;
                }

                return lengths.Count == 1 && lengths.First() is 1 or 2 ? lengths.First() : null;
            default:
                return null;
        }
    }

    /// <summary>/W: "c [w1 w2 …]" or "cFirst cLast w" (§9.7.4.3).</summary>
    private static void ReadCidWidths(CosDocument document, CosArray? array, Dictionary<int, double> widths)
    {
        if (array is null)
        {
            return;
        }

        var items = array.Items.Select(document.Resolve).ToList();
        for (var i = 0; i + 1 < items.Count;)
        {
            var first = (int)PdfMatrix.Number(items[i]!);
            if (items[i + 1] is CosArray list)
            {
                for (var j = 0; j < list.Count; j++)
                {
                    widths[first + j] = PdfMatrix.Number(document.Resolve(list[j]) ?? new CosInteger(0));
                }

                i += 2;
            }
            else if (i + 2 < items.Count)
            {
                var last = (int)PdfMatrix.Number(items[i + 1]!);
                var width = PdfMatrix.Number(items[i + 2]!);
                for (var c = first; c <= last && c - first < 65536; c++)
                {
                    widths[c] = width;
                }

                i += 3;
            }
            else
            {
                break;
            }
        }
    }
}
