using System.Buffers.Binary;
using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Archive;

/// <summary>
/// An sRGB (IEC 61966-2.1) ICC version 2 display profile, built from the published colorimetry, for the
/// PDF/A output intent. Written by the app, so no third-party profile file (and its licence) is needed.
/// </summary>
internal static class SrgbProfile
{
    public const string Description = "sRGB IEC61966-2.1";

    private static readonly Lazy<byte[]> Cached = new(Build);

    public static byte[] Bytes => Cached.Value;

    private static byte[] Build()
    {
        // Tag data, each padded to 4 bytes. The three tone curves share one copy.
        var desc = TextDescription(Description);
        var cprt = Text("No copyright, use freely");
        var wtpt = Xyz(0.9505, 1.0, 1.0891); // D65 media white (version 2 convention).
        var rXyz = Xyz(0.4361, 0.2225, 0.0139); // Primaries adapted to the D50 connection space (Bradford).
        var gXyz = Xyz(0.3851, 0.7169, 0.0971);
        var bXyz = Xyz(0.1431, 0.0606, 0.7141);
        var trc = Curve();

        var tags = new List<(string Signature, byte[] Data)>
        {
            ("desc", desc), ("cprt", cprt), ("wtpt", wtpt), ("rXYZ", rXyz), ("gXYZ", gXyz), ("bXYZ", bXyz),
            ("rTRC", trc), ("gTRC", trc), ("bTRC", trc),
        };

        const int headerSize = 128;
        var tableSize = 4 + (tags.Count * 12);
        var offsets = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        var position = headerSize + tableSize;
        foreach (var (_, data) in tags)
        {
            if (!offsets.ContainsKey(data))
            {
                offsets[data] = position;
                position += Pad(data.Length);
            }
        }

        var profile = new byte[position];
        var span = profile.AsSpan();

        // Header (ICC.1:2001-04, version 2.1).
        BinaryPrimitives.WriteUInt32BigEndian(span[0..], (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 0x02100000);
        Ascii(span[12..], "mntr");
        Ascii(span[16..], "RGB ");
        Ascii(span[20..], "XYZ ");
        BinaryPrimitives.WriteUInt16BigEndian(span[24..], 2026); // Creation date: 2026-01-01 00:00:00.
        BinaryPrimitives.WriteUInt16BigEndian(span[26..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[28..], 1);
        Ascii(span[36..], "acsp");
        Ascii(span[40..], "MSFT");
        BinaryPrimitives.WriteUInt32BigEndian(span[64..], 0); // Perceptual intent.
        S15Fixed16(span[68..], 0.9642);                       // D50 illuminant of the connection space.
        S15Fixed16(span[72..], 1.0);
        S15Fixed16(span[76..], 0.8249);

        // Tag table, then the tag data.
        BinaryPrimitives.WriteUInt32BigEndian(span[headerSize..], (uint)tags.Count);
        for (var i = 0; i < tags.Count; i++)
        {
            var entry = span[(headerSize + 4 + (i * 12))..];
            Ascii(entry, tags[i].Signature);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)offsets[tags[i].Data]);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)tags[i].Data.Length);
        }

        foreach (var (data, offset) in offsets)
        {
            data.CopyTo(span[offset..]);
        }

        return profile;
    }

    /// <summary>The sRGB transfer curve, 1024 samples.</summary>
    private static byte[] Curve()
    {
        const int count = 1024;
        var data = new byte[12 + (count * 2)];
        Ascii(data, "curv");
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), count);
        for (var i = 0; i < count; i++)
        {
            var v = i / (double)(count - 1);
            var linear = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12 + (i * 2)), (ushort)Math.Round(linear * 65535));
        }

        return data;
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        var data = new byte[20];
        Ascii(data, "XYZ ");
        S15Fixed16(data.AsSpan(8), x);
        S15Fixed16(data.AsSpan(12), y);
        S15Fixed16(data.AsSpan(16), z);
        return data;
    }

    private static byte[] Text(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text);
        var data = new byte[8 + ascii.Length + 1];
        Ascii(data, "text");
        ascii.CopyTo(data, 8);
        return data;
    }

    /// <summary>textDescriptionType: ASCII, then empty Unicode and ScriptCode parts.</summary>
    private static byte[] TextDescription(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text);
        var data = new byte[12 + ascii.Length + 1 + 8 + 3 + 67];
        Ascii(data, "desc");
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)(ascii.Length + 1));
        ascii.CopyTo(data, 12);
        return data; // Unicode language/count and ScriptCode code/count/text stay zero.
    }

    private static void S15Fixed16(Span<byte> target, double value) =>
        BinaryPrimitives.WriteInt32BigEndian(target, (int)Math.Round(value * 65536));

    private static void Ascii(Span<byte> target, string text) => Encoding.ASCII.GetBytes(text, target);

    private static int Pad(int length) => (length + 3) & ~3;
}
