using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Text;

/// <summary>
/// Puts back the Thai vowels and tone marks that a PDF's /ToUnicode leaves out. Microsoft Word, for one, maps the
/// shifted forms of ่ ้ ์ ็ (moved left beside ป ฝ ฟ, or up over ิ ี) to U+0000, so PDFium reads them as nothing:
/// copying, searching and editing lose the mark, and a line breaks in two at it. A shifted form has the same outline
/// as its mark, only moved, so comparing outlines in the embedded TrueType font names it. The fixed /ToUnicode goes
/// into an incremental update that PDFium reads after the file (the file itself is not changed).
/// </summary>
internal static partial class ToUnicodeRepair
{
    /// <summary>Larger files are left as they are: the check reads the whole file.</summary>
    private const long MaxFileLength = 40L * 1024 * 1024;

    /// <summary>The bytes to read after <paramref name="file"/> (an incremental update), or null when nothing needs repair.</summary>
    public static byte[]? Build(Stream file)
    {
        if (file.Length is 0 or > MaxFileLength)
        {
            return null;
        }

        var bytes = new byte[file.Length];
        file.Position = 0;
        file.ReadExactly(bytes);
        try
        {
            return Build(bytes);
        }
        catch (Exception ex) when (ex is PdfException or FormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return null; // A damaged font or map: show the file as PDFium reads it.
        }
    }

    internal static byte[]? Build(byte[] bytes)
    {
        var document = CosDocument.Load(bytes);
        if (document.IsEncrypted || IsSigned(document))
        {
            return null; // An added revision would count as a change to a signed file.
        }

        var update = new CosIncrementalUpdate(document);
        var repaired = false;
        for (var number = 1; number < document.Size; number++)
        {
            if (document.GetObject(new CosObjectId(number, 0)) is not CosDictionary font
                || (font["Subtype"] as CosName)?.Value != "Type0"
                || font["ToUnicode"] is not CosReference toUnicodeRef
                || document.Resolve(toUnicodeRef) is not CosStream toUnicode
                || FontProgram(document, font) is not { } program)
            {
                continue;
            }

            var cmap = Encoding.Latin1.GetString(CosFilters.Decode(toUnicode, document.Resolve));
            if (Repair(cmap, program) is { } fixedCmap)
            {
                update.Replace(toUnicodeRef.Id, new CosStream(
                    new CosDictionary().Set("Filter", new CosName("FlateDecode")),
                    CosFilters.Deflate(Encoding.Latin1.GetBytes(fixedCmap))));
                repaired = true;
            }
        }

        return repaired ? update.Build().Bytes.AsSpan(bytes.Length).ToArray() : null;
    }

    /// <summary>The CMap with its empty Thai mark entries filled in; null when it has none that can be named.</summary>
    internal static string? Repair(string cmap, TrueTypeOutlines program)
    {
        var known = new Dictionary<int, char>();
        var missing = new List<int>();
        foreach (Match entry in SingleEntry().Matches(cmap))
        {
            var code = int.Parse(entry.Groups["code"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var target = entry.Groups["to"].Value;
            if (target.Length == 0 || int.Parse(target, NumberStyles.HexNumber, CultureInfo.InvariantCulture) == 0)
            {
                missing.Add(code);
            }
            else if (target.Length == 4 && (char)int.Parse(target, NumberStyles.HexNumber, CultureInfo.InvariantCulture) is var c && IsThaiMark(c))
            {
                known[code] = c;
            }
        }

        if (missing.Count == 0)
        {
            return null;
        }

        var shapes = known.Select(k => (Shape: program.Outline(k.Key), Mark: k.Value.ToString())).Where(k => k.Shape is not null).ToList();
        var found = new Dictionary<int, string>();
        List<(GlyphShape Shape, string Mark)>? reference = null;
        foreach (var code in missing)
        {
            if (program.Outline(code) is not { } shape)
            {
                continue;
            }

            // A moved copy first; then a resized one (marks over ิ ี are drawn a little smaller): the same points and
            // contours, when exactly one mark has them. Last, the same glyph in a full font we carry: a mark used only
            // in its shifted form has no copy in the file to compare with.
            if (shapes.FirstOrDefault(k => k.Shape!.SameShape(shape)) is { Shape: not null } match)
            {
                found[code] = match.Mark;
            }
            else if (shapes.Where(k => k.Shape!.SameStructure(shape)).Select(k => k.Mark).Distinct().ToList() is [var only])
            {
                found[code] = only;
            }
            else if ((reference ??= ReferenceMarks()).Where(r => r.Shape.SameShape(shape)).Select(r => r.Mark).Distinct().ToList() is [var named])
            {
                found[code] = named;
            }
        }

        if (found.Count == 0)
        {
            return null;
        }

        return SingleEntry().Replace(cmap, m =>
        {
            var code = int.Parse(m.Groups["code"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (!found.TryGetValue(code, out var mark))
            {
                return m.Value;
            }

            var to = m.Groups["to"];
            var at = to.Index - m.Index;
            var hex = string.Concat(mark.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)));
            return m.Value[..at] + hex + m.Value[(at + to.Length)..];
        });
    }

    /// <summary>Full fonts to name marks by (the Sarabun weights the program carries, which Word documents often use).</summary>
    internal static string ReferenceFontFolder { get; set; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");

    private static (string Folder, List<(GlyphShape Shape, string Mark)> Marks)? _referenceMarks;

    /// <summary>
    /// The mark glyphs of the reference fonts with what they read, from their glyph names: "uni0E4C.small" is ์,
    /// "uni0E34_uni0E4C" is ิ์.
    /// </summary>
    private static List<(GlyphShape Shape, string Mark)> ReferenceMarks()
    {
        var folder = ReferenceFontFolder;
        if (_referenceMarks is { } cached && cached.Folder == folder)
        {
            return cached.Marks;
        }

        var marks = new List<(GlyphShape, string)>();
        try
        {
            foreach (var file in Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.ttf") : [])
            {
                if (TrueTypeOutlines.Load(File.ReadAllBytes(file)) is not { } font)
                {
                    continue;
                }

                var names = font.GlyphNames();
                for (var glyph = 0; glyph < names.Count; glyph++)
                {
                    if (MarksNamed(names[glyph]) is { } text && font.Outline(glyph) is { } shape)
                    {
                        marks.Add((shape, text));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without them, marks used only shifted stay unnamed.
        }

        _referenceMarks = (folder, marks);
        return marks;
    }

    /// <summary>What a glyph name reads when it names Thai marks only: "uni0E4C.small" → ์; anything else → null.</summary>
    internal static string? MarksNamed(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var part in name.Split('.')[0].Split('_'))
        {
            if (!part.StartsWith("uni", StringComparison.Ordinal) || part.Length < 7 || (part.Length - 3) % 4 != 0)
            {
                return null;
            }

            for (var i = 3; i < part.Length; i += 4)
            {
                if (!int.TryParse(part.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) || !IsThaiMark((char)code))
                {
                    return null;
                }

                text.Append((char)code);
            }
        }

        return text.Length > 0 ? text.ToString() : null;
    }

    /// <summary>Thai vowels above and below and tone marks: the characters fonts draw in shifted forms.</summary>
    private static bool IsThaiMark(char c) => c is 'ั' or (>= 'ิ' and <= 'ฺ') or (>= '็' and <= '๎');

    /// <summary>
    /// One code to one character: "&lt;02de&gt;&lt;02de&gt;&lt;0e48&gt;" in a bfrange or "&lt;02de&gt; &lt;0e48&gt;" in a bfchar.
    /// </summary>
    [GeneratedRegex(@"<(?<code>[0-9A-Fa-f]{2,4})>\s*(?:<\k<code>>\s*)?<(?<to>[0-9A-Fa-f]{0,4})>")]
    private static partial Regex SingleEntry();

    /// <summary>Whether a signature field holds a signature (the /SigFlags of empty signature fields say so too).</summary>
    private static bool IsSigned(CosDocument document)
    {
        if (document.Catalog["Perms"] is not null)
        {
            return true;
        }

        if (document.Resolve(document.Catalog["AcroForm"]) is not CosDictionary form || document.Resolve(form["Fields"]) is not CosArray fields)
        {
            return false;
        }

        var pending = new Stack<CosObject>(fields.Items);
        var seen = new HashSet<CosObject>();
        while (pending.Count > 0)
        {
            if (document.Resolve(pending.Pop()) is not CosDictionary field || !seen.Add(field))
            {
                continue;
            }

            if (document.Resolve(field["V"]) is CosDictionary value && (document.Resolve(value["Type"]) as CosName)?.Value is "Sig" or null
                && value["ByteRange"] is not null)
            {
                return true;
            }

            if (document.Resolve(field["Kids"]) is CosArray kids)
            {
                kids.Items.ForEach(pending.Push);
            }
        }

        return false;
    }

    /// <summary>The embedded TrueType program of a Type0 font whose codes are glyph ids (Identity-H, identity CIDToGIDMap).</summary>
    private static TrueTypeOutlines? FontProgram(CosDocument document, CosDictionary font)
    {
        if ((document.Resolve(font["Encoding"]) as CosName)?.Value != "Identity-H"
            || document.Resolve(font["DescendantFonts"]) is not CosArray { Count: 1 } descendants
            || document.Resolve(descendants[0]) is not CosDictionary cidFont
            || (document.Resolve(cidFont["Subtype"]) as CosName)?.Value != "CIDFontType2"
            || (cidFont["CIDToGIDMap"] is { } map && (document.Resolve(map) as CosName)?.Value != "Identity")
            || document.Resolve(cidFont["FontDescriptor"]) is not CosDictionary descriptor
            || document.Resolve(descriptor["FontFile2"]) is not CosStream file)
        {
            return null;
        }

        return TrueTypeOutlines.Load(CosFilters.Decode(file, document.Resolve));
    }
}

/// <summary>Reads glyph outlines from a TrueType font program, enough to compare shapes.</summary>
internal sealed class TrueTypeOutlines
{
    private const int MaxDepth = 8;

    private readonly byte[] _font;
    private readonly int _glyf;
    private readonly int _loca;
    private readonly bool _longOffsets;
    private readonly int _glyphCount;

    private TrueTypeOutlines(byte[] font, int glyf, int loca, bool longOffsets, int glyphCount)
    {
        _font = font;
        _glyf = glyf;
        _loca = loca;
        _longOffsets = longOffsets;
        _glyphCount = glyphCount;
    }

    public static TrueTypeOutlines? Load(byte[] font)
    {
        if (font.Length < 12)
        {
            return null;
        }

        int glyf = -1, loca = -1, head = -1, maxp = -1, post = -1, postLength = 0;
        int tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (var i = 0; i < tables && 12 + (16 * i) + 16 <= font.Length; i++)
        {
            var record = font.AsSpan(12 + (16 * i), 16);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            switch (Encoding.ASCII.GetString(record[..4]))
            {
                case "glyf": glyf = offset; break;
                case "loca": loca = offset; break;
                case "head": head = offset; break;
                case "maxp": maxp = offset; break;
                case "post": (post, postLength) = (offset, (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..])); break;
            }
        }

        if (glyf < 0 || loca < 0 || head < 0 || maxp < 0 || head + 54 > font.Length || maxp + 6 > font.Length)
        {
            return null;
        }

        var longOffsets = BinaryPrimitives.ReadInt16BigEndian(font.AsSpan(head + 50)) == 1;
        var glyphCount = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(maxp + 4));
        return new TrueTypeOutlines(font, glyf, loca, longOffsets, glyphCount) { _post = (post, postLength) };
    }

    private (int Offset, int Length) _post = (-1, 0);

    public int GlyphCount => _glyphCount;

    /// <summary>The glyph names of a 'post' table of version 2 ("uni0E4C.small"); empty when the font has none.</summary>
    public IReadOnlyList<string?> GlyphNames()
    {
        var (post, length) = _post;
        if (post < 0 || post + 34 > _font.Length || BinaryPrimitives.ReadUInt32BigEndian(_font.AsSpan(post)) != 0x00020000)
        {
            return [];
        }

        var end = Math.Min(_font.Length, post + length);
        int count = BinaryPrimitives.ReadUInt16BigEndian(_font.AsSpan(post + 32));
        if (post + 34 + (2 * count) > end)
        {
            return [];
        }

        var names = new List<string>();
        for (var p = post + 34 + (2 * count); p < end && p + 1 + _font[p] <= end; p += 1 + _font[p])
        {
            names.Add(Encoding.ASCII.GetString(_font, p + 1, _font[p]));
        }

        var result = new string?[count];
        for (var glyph = 0; glyph < count; glyph++)
        {
            var index = BinaryPrimitives.ReadUInt16BigEndian(_font.AsSpan(post + 34 + (2 * glyph)));
            result[glyph] = index >= 258 && index - 258 < names.Count ? names[index - 258] : null; // The first 258 are Latin names.
        }

        return result;
    }

    /// <summary>The outline of glyph <paramref name="glyph"/>, or null when it is empty or cannot be read.</summary>
    public GlyphShape? Outline(int glyph)
    {
        var points = new List<(int X, int Y, bool OnCurve)>();
        var ends = new List<int>();
        return Collect(glyph, 0, 0, points, ends, 0) && points.Count > 0 ? new GlyphShape(points, ends) : null;
    }

    private bool Collect(int glyph, int dx, int dy, List<(int X, int Y, bool OnCurve)> points, List<int> ends, int depth)
    {
        if (glyph < 0 || glyph >= _glyphCount || depth > MaxDepth)
        {
            return false;
        }

        var (start, end) = _longOffsets
            ? ((int)BinaryPrimitives.ReadUInt32BigEndian(_font.AsSpan(_loca + (4 * glyph))), (int)BinaryPrimitives.ReadUInt32BigEndian(_font.AsSpan(_loca + (4 * glyph) + 4)))
            : (BinaryPrimitives.ReadUInt16BigEndian(_font.AsSpan(_loca + (2 * glyph))) * 2, BinaryPrimitives.ReadUInt16BigEndian(_font.AsSpan(_loca + (2 * glyph) + 2)) * 2);
        if (end <= start)
        {
            return true; // No outline (a space).
        }

        var data = _font.AsSpan(_glyf + start, end - start);
        int contours = BinaryPrimitives.ReadInt16BigEndian(data);
        return contours >= 0 ? ReadSimple(data, contours, dx, dy, points, ends) : ReadComposite(data, dx, dy, points, ends, depth);
    }

    private static bool ReadSimple(ReadOnlySpan<byte> data, int contours, int dx, int dy, List<(int X, int Y, bool OnCurve)> points, List<int> ends)
    {
        var p = 10;
        var first = points.Count;
        var count = 0;
        for (var c = 0; c < contours; c++)
        {
            count = BinaryPrimitives.ReadUInt16BigEndian(data[p..]) + 1;
            ends.Add(first + count - 1);
            p += 2;
        }

        p += 2 + BinaryPrimitives.ReadUInt16BigEndian(data[p..]); // Instructions.
        var flags = new byte[count];
        for (var i = 0; i < count;)
        {
            var flag = data[p++];
            flags[i++] = flag;
            if ((flag & 8) != 0)
            {
                for (var repeat = data[p++]; repeat > 0 && i < count; repeat--)
                {
                    flags[i++] = flag;
                }
            }
        }

        var xs = new int[count];
        var x = 0;
        for (var i = 0; i < count; i++)
        {
            x += (flags[i] & 2) != 0 ? ((flags[i] & 16) != 0 ? data[p++] : -data[p++])
                : (flags[i] & 16) != 0 ? 0 : ReadShort(data, ref p);
            xs[i] = x;
        }

        var y = 0;
        for (var i = 0; i < count; i++)
        {
            y += (flags[i] & 4) != 0 ? ((flags[i] & 32) != 0 ? data[p++] : -data[p++])
                : (flags[i] & 32) != 0 ? 0 : ReadShort(data, ref p);
            points.Add((xs[i] + dx, y + dy, (flags[i] & 1) != 0));
        }

        return true;
    }

    private bool ReadComposite(ReadOnlySpan<byte> data, int dx, int dy, List<(int X, int Y, bool OnCurve)> points, List<int> ends, int depth)
    {
        var p = 10;
        int flags;
        do
        {
            flags = BinaryPrimitives.ReadUInt16BigEndian(data[p..]);
            int component = BinaryPrimitives.ReadUInt16BigEndian(data[(p + 2)..]);
            p += 4;
            int argument1, argument2;
            if ((flags & 1) != 0)
            {
                argument1 = ReadShort(data, ref p);
                argument2 = ReadShort(data, ref p);
            }
            else
            {
                argument1 = (sbyte)data[p++];
                argument2 = (sbyte)data[p++];
            }

            if ((flags & (8 | 64 | 128)) != 0 || (flags & 2) == 0)
            {
                return false; // Scaled or point-matched parts: not a plain shifted copy.
            }

            if (!Collect(component, dx + argument1, dy + argument2, points, ends, depth + 1))
            {
                return false;
            }
        }
        while ((flags & 32) != 0);

        return true;
    }

    private static short ReadShort(ReadOnlySpan<byte> data, ref int p)
    {
        var value = BinaryPrimitives.ReadInt16BigEndian(data[p..]);
        p += 2;
        return value;
    }
}

/// <summary>A glyph's points, compared regardless of where the glyph sits.</summary>
internal sealed class GlyphShape(List<(int X, int Y, bool OnCurve)> points, List<int> ends)
{
    public bool SameShape(GlyphShape other)
    {
        if (points.Count != other.Points.Count || !ends.SequenceEqual(other.Ends))
        {
            return false;
        }

        var (x0, y0, _) = points[0];
        var (ox0, oy0, _) = other.Points[0];
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = other.Points[i];
            if (a.OnCurve != b.OnCurve || Math.Abs((a.X - x0) - (b.X - ox0)) > 1 || Math.Abs((a.Y - y0) - (b.Y - oy0)) > 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The same contours with the same on- and off-curve points, at any size.</summary>
    public bool SameStructure(GlyphShape other) =>
        points.Count == other.Points.Count && ends.SequenceEqual(other.Ends) && points.Select(p => p.OnCurve).SequenceEqual(other.Points.Select(p => p.OnCurve));

    public List<(int X, int Y, bool OnCurve)> Points => points;

    public List<int> Ends => ends;
}
