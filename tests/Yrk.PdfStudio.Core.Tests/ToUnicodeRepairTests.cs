using System.Buffers.Binary;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ToUnicodeRepairTests
{
    // Glyph 1: mai ek (่). Glyph 2: the same shape moved left (Word leaves it as <0000>). Glyph 3: mai tho drawn
    // smaller (same points, other size). Glyph 4: a different shape that must stay unknown.
    private static readonly TrueTypeOutlines Font = TrueTypeOutlines.Load(BuildFont(
        Square(100, 900, 60, 200),
        Square(-80, 900, 60, 200),
        Triangle(0, 1200, 50),
        Triangle(0, 900, 80),
        Pentagon()))!;

    [Fact]
    public void Names_moved_and_resized_marks_from_their_outlines()
    {
        const string cmap = """
            2 beginbfrange
            <0001><0001><0e48>
            <0004><0004><0e49>
            endbfrange
            3 beginbfchar
            <0002> <0000>
            <0003> <0000>
            <0005> <0000>
            endbfchar
            """;

        var repaired = ToUnicodeRepair.Repair(cmap, Font);

        Assert.NotNull(repaired);
        Assert.Contains("<0002> <0E48>", repaired, StringComparison.Ordinal); // Moved mai ek.
        Assert.Contains("<0003> <0E49>", repaired, StringComparison.Ordinal); // Smaller mai tho.
        Assert.Contains("<0005> <0000>", repaired, StringComparison.Ordinal); // Unknown shape: left alone.
        Assert.Contains("<0001><0001><0e48>", repaired, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_maps_without_empty_entries_alone() =>
        Assert.Null(ToUnicodeRepair.Repair("1 beginbfchar\n<0001> <0e48>\nendbfchar", Font));

    private static (int X, int Y)[] Square(int x, int y, int w, int h) => [(x, y), (x + w, y), (x + w, y + h), (x, y + h)];

    private static (int X, int Y)[] Triangle(int x, int y, int size) => [(x, y), (x + size, y), (x + (size / 2), y + size)];

    private static (int X, int Y)[] Pentagon() => [(0, 0), (40, 0), (50, 30), (20, 50), (-10, 30)];

    /// <summary>A minimal TrueType font: glyph 0 empty, then one closed on-curve contour per outline.</summary>
    private static byte[] BuildFont(params (int X, int Y)[][] outlines)
    {
        var glyphs = new List<byte[]> { Array.Empty<byte>() };
        foreach (var points in outlines)
        {
            var glyph = new List<byte>();
            void Short(int value) { var b = new byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, (short)value); glyph.AddRange(b); }
            Short(1);                                   // One contour.
            Short(0); Short(0); Short(0); Short(0);     // Bounding box (not read).
            Short(points.Length - 1);                   // End point.
            Short(0);                                   // No instructions.
            glyph.AddRange(Enumerable.Repeat((byte)1, points.Length)); // On-curve, 16-bit deltas.
            int last = 0;
            foreach (var (x, _) in points) { Short(x - last); last = x; }
            last = 0;
            foreach (var (_, y) in points) { Short(y - last); last = y; }
            if (glyph.Count % 2 == 1) glyph.Add(0);
            glyphs.Add([.. glyph]);
        }

        var glyf = glyphs.SelectMany(g => g).ToArray();
        var loca = new List<byte>();
        var offset = 0;
        foreach (var glyph in glyphs.Append(Array.Empty<byte>()))
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)offset);
            loca.AddRange(b);
            offset += glyph.Length;
        }

        var head = new byte[54];
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1); // Long loca offsets.
        var maxp = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)glyphs.Count);

        var tables = new (string Tag, byte[] Data)[] { ("glyf", glyf), ("head", head), ("loca", [.. loca]), ("maxp", maxp) };
        var font = new List<byte>(new byte[12 + (16 * tables.Length)]);
        BinaryPrimitives.WriteUInt16BigEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(font)[4..], (ushort)tables.Length);
        for (var i = 0; i < tables.Length; i++)
        {
            var record = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(font).Slice(12 + (16 * i), 16);
            Encoding.ASCII.GetBytes(tables[i].Tag).CopyTo(record);
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)font.Count);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)tables[i].Data.Length);
            font.AddRange(tables[i].Data);
        }

        return [.. font];
    }
}
