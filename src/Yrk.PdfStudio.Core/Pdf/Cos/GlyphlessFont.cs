using System.Buffers.Binary;
using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Cos;

/// <summary>
/// A tiny TrueType font for invisible OCR text (the approach Tesseract's PDF
/// output uses). The text is never drawn (render mode 3), but embedding a real font keeps viewers from
/// complaining about a missing font. Every character maps to glyph 1 through /CIDToGIDMap.
/// </summary>
internal static class GlyphlessFont
{
    public const int UnitsPerEm = 1000;
    public const int Advance = 500;

    private static readonly Lazy<byte[]> Data = new(Build);

    public static byte[] Bytes => Data.Value;

    /// <summary>The Type0 font (Identity-H, 2-byte codes = UTF-16 code units) and its parts, added to the update.</summary>
    public static CosReference AddTo(CosIncrementalUpdate update)
    {
        var fontFile = update.Add(CosPageEdit.Stream(Bytes, new CosDictionary().Set("Length1", new CosInteger(Bytes.Length))));
        var descriptor = update.Add(new CosDictionary()
            .Set("Type", new CosName("FontDescriptor"))
            .Set("FontName", new CosName("GlyphLessFont"))
            .Set("Flags", new CosInteger(5))
            .Set("FontBBox", CosArray.FromNumbers(0, 0, Advance, UnitsPerEm))
            .Set("ItalicAngle", new CosInteger(0))
            .Set("Ascent", new CosInteger(UnitsPerEm))
            .Set("Descent", new CosInteger(0))
            .Set("CapHeight", new CosInteger(UnitsPerEm))
            .Set("StemV", new CosInteger(80))
            .Set("FontFile2", fontFile));

        // Every CID (0..65535) → glyph 1.
        var map = new byte[65536 * 2];
        for (var i = 1; i < map.Length; i += 2)
        {
            map[i] = 1;
        }

        var cidFont = new CosDictionary()
            .Set("Type", new CosName("Font"))
            .Set("Subtype", new CosName("CIDFontType2"))
            .Set("BaseFont", new CosName("GlyphLessFont"))
            .Set("CIDSystemInfo", new CosDictionary()
                .Set("Registry", new CosString("Adobe"u8.ToArray()))
                .Set("Ordering", new CosString("Identity"u8.ToArray()))
                .Set("Supplement", new CosInteger(0)))
            .Set("FontDescriptor", descriptor)
            .Set("DW", new CosInteger(Advance))
            .Set("CIDToGIDMap", update.Add(CosPageEdit.Stream(map)));

        return update.Add(new CosDictionary()
            .Set("Type", new CosName("Font"))
            .Set("Subtype", new CosName("Type0"))
            .Set("BaseFont", new CosName("GlyphLessFont"))
            .Set("Encoding", new CosName("Identity-H"))
            .Set("DescendantFonts", new CosArray([update.Add(cidFont)]))
            .Set("ToUnicode", update.Add(CosPageEdit.Stream(IdentityToUnicode()))));
    }

    /// <summary>A ToUnicode CMap mapping each 2-byte code to the same UTF-16 code unit.</summary>
    public static byte[] IdentityToUnicode()
    {
        var text = new StringBuilder();
        text.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        text.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        text.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        text.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        // A bfrange may only vary in its last byte, so one range per high byte, at most 100 per block.
        for (var block = 0; block < 256; block += 100)
        {
            var count = Math.Min(100, 256 - block);
            text.Append(count).Append(" beginbfrange\n");
            for (var high = block; high < block + count; high++)
            {
                text.Append($"<{high:X2}00> <{high:X2}FF> <{high:X2}00>\n");
            }

            text.Append("endbfrange\n");
        }

        text.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static byte[] Build()
    {
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = Cmap(),
            ["glyf"] = Glyf(),
            ["head"] = Head(),
            ["hhea"] = Hhea(),
            ["hmtx"] = [0x01, 0xF4, 0, 0, 0x01, 0xF4, 0, 0], // Two glyphs, advance 500.
            ["loca"] = [.. U16(0), .. U16(0), .. U16(Glyf().Length / 2)], // Short offsets: glyph 0 empty.
            ["maxp"] = Maxp(),
            ["name"] = Name(),
            ["post"] = Post(),
        };

        var numTables = tables.Count;
        var entrySelector = (int)Math.Floor(Math.Log2(numTables));
        var searchRange = (1 << entrySelector) * 16;
        var headerLength = 12 + (16 * numTables);
        var output = new List<byte>();
        output.AddRange(U32(0x00010000));
        output.AddRange(U16(numTables));
        output.AddRange(U16(searchRange));
        output.AddRange(U16(entrySelector));
        output.AddRange(U16((numTables * 16) - searchRange));

        var offset = headerLength;
        var body = new List<byte>();
        foreach (var (tag, data) in tables)
        {
            output.AddRange(Encoding.ASCII.GetBytes(tag));
            output.AddRange(U32(Checksum(data)));
            output.AddRange(U32((uint)offset));
            output.AddRange(U32((uint)data.Length));
            body.AddRange(data);
            while (body.Count % 4 != 0)
            {
                body.Add(0);
            }

            offset = headerLength + body.Count;
        }

        output.AddRange(body);
        var font = output.ToArray();

        // head.checkSumAdjustment makes the whole font sum to 0xB1B0AFBA.
        var headOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(12 + (16 * tables.Keys.ToList().IndexOf("head")) + 8));
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headOffset + 8), unchecked(0xB1B0AFBA - Checksum(font)));
        return font;
    }

    private static byte[] Head() =>
    [
        .. U32(0x00010000), .. U32(0x00010000), .. U32(0), .. U32(0x5F0F3CF5),
        .. U16(0x000B), .. U16(UnitsPerEm),
        .. new byte[16],                                // created, modified
        .. U16(0), .. U16(0), .. U16(Advance), .. U16(UnitsPerEm),
        .. U16(0), .. U16(3), .. U16(2), .. U16(0), .. U16(0),
    ];

    private static byte[] Hhea() =>
    [
        .. U32(0x00010000), .. U16(UnitsPerEm), .. U16(0), .. U16(0), .. U16(Advance),
        .. U16(0), .. U16(0), .. U16(0), .. U16(1), .. U16(0), .. U16(0),
        .. new byte[8], .. U16(0), .. U16(2),
    ];

    private static byte[] Maxp() =>
    [
        .. U32(0x00010000), .. U16(2),
        .. U16(4), .. U16(1), .. U16(0), .. U16(0), .. U16(2), .. U16(0), .. U16(0),
        .. U16(0), .. U16(0), .. U16(0), .. U16(0), .. U16(0), .. U16(0),
    ];

    /// <summary>
    /// Glyph 1 is a rectangle the size of the advance. It is never painted (the text uses render mode 3),
    /// but PDFium ignores rotated text whose glyphs have no outline.
    /// </summary>
    private static byte[] Glyf() =>
    [
        .. U16(1), .. U16(0), .. U16(0), .. U16(Advance), .. U16(UnitsPerEm), // 1 contour, bounding box
        .. U16(3), .. U16(0),                                              // last point index, no instructions
        1, 1, 1, 1,                                                        // four on-curve points
        .. U16(0), .. U16(Advance), .. U16(0), .. U16(-Advance),           // x deltas
        .. U16(0), .. U16(0), .. U16(UnitsPerEm), .. U16(0),               // y deltas
        0, 0,                                                              // pad to 4 bytes
    ];

    private static byte[] Post() => [.. U32(0x00030000), .. new byte[28]];

    /// <summary>Format 4 with only the required final segment: every character maps to glyph 0.</summary>
    private static byte[] Cmap() =>
    [
        .. U16(0), .. U16(1), .. U16(3), .. U16(1), .. U32(12),
        .. U16(4), .. U16(24), .. U16(0), .. U16(2), .. U16(2), .. U16(0), .. U16(0),
        .. U16(0xFFFF), .. U16(0), .. U16(0xFFFF), .. U16(1), .. U16(0),
    ];

    private static byte[] Name()
    {
        var value = Encoding.BigEndianUnicode.GetBytes("GlyphLessFont");
        ushort[] ids = [1, 4, 6];
        var result = new List<byte>();
        result.AddRange(U16(0));
        result.AddRange(U16(ids.Length));
        result.AddRange(U16(6 + (12 * ids.Length)));
        foreach (var id in ids)
        {
            result.AddRange([.. U16(3), .. U16(1), .. U16(0x409), .. U16(id), .. U16(value.Length), .. U16(0)]);
        }

        result.AddRange(value);
        return result.ToArray();
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var j = 0; j < 4; j++)
            {
                word = (word << 8) | (i + j < data.Length ? data[i + j] : 0u);
            }

            sum = unchecked(sum + word);
        }

        return sum;
    }

    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    private static byte[] U32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
