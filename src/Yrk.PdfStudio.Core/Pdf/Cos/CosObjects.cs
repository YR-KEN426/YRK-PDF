using System.Globalization;
using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Cos;

// A small model of PDF objects (ISO 32000-1 §7.3). It exists for the few jobs PDFium cannot do,
// such as appending a digital signature as an incremental update, so it is deliberately minimal.

internal abstract class CosObject
{
}

internal sealed class CosNull : CosObject
{
    public static CosNull Instance { get; } = new();

    private CosNull()
    {
    }
}

internal sealed class CosBoolean(bool value) : CosObject
{
    public bool Value { get; } = value;
}

internal sealed class CosInteger(long value) : CosObject
{
    public long Value { get; } = value;
}

internal sealed class CosReal(double value) : CosObject
{
    public double Value { get; } = value;
}

internal sealed class CosName(string value) : CosObject
{
    public string Value { get; } = value;

    public override string ToString() => "/" + Value;
}

internal sealed class CosString(byte[] bytes, bool isHex = false) : CosObject
{
    public byte[] Bytes { get; } = bytes;

    public bool IsHex { get; } = isHex;

    /// <summary>A text string: ASCII as is, anything else as UTF-16BE with a byte order mark (§7.9.2.2).</summary>
    public static CosString FromText(string text)
    {
        if (text.All(c => c is >= ' ' and < (char)0x7F))
        {
            return new CosString(Encoding.ASCII.GetBytes(text));
        }

        var bytes = new byte[2 + (text.Length * 2)];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        Encoding.BigEndianUnicode.GetBytes(text, 0, text.Length, bytes, 2);
        return new CosString(bytes, isHex: true);
    }

    /// <summary>Decodes a text string (UTF-16 with BOM, UTF-8 with BOM, or PDFDocEncoding treated as Latin-1).</summary>
    public string ToText()
    {
        if (Bytes is [0xFE, 0xFF, ..])
        {
            return Encoding.BigEndianUnicode.GetString(Bytes, 2, Bytes.Length - 2);
        }

        if (Bytes is [0xFF, 0xFE, ..])
        {
            return Encoding.Unicode.GetString(Bytes, 2, Bytes.Length - 2);
        }

        if (Bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return Encoding.UTF8.GetString(Bytes, 3, Bytes.Length - 3);
        }

        return Encoding.Latin1.GetString(Bytes);
    }

    /// <summary>A PDF date (§7.9.4), for example D:20261017142201+07'00'.</summary>
    public static CosString FromDate(DateTimeOffset date)
    {
        var offset = date.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"D:{date:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'");
        return new CosString(Encoding.ASCII.GetBytes(text));
    }

    public DateTimeOffset? ToDate()
    {
        var text = Encoding.ASCII.GetString(Bytes).Trim();
        if (text.StartsWith("D:", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        if (text.Length < 4 || !int.TryParse(text[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return null;
        }

        int Part(int start, int fallback) =>
            text.Length >= start + 2 && int.TryParse(text.AsSpan(start, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : fallback;

        var offset = TimeSpan.Zero;
        if (text.Length > 14 && text[14] is '+' or '-')
        {
            var hours = Part(15, 0);
            var minutes = text.Length > 18 ? Part(18, 0) : 0;
            offset = new TimeSpan(hours, minutes, 0);
            if (text[14] == '-')
            {
                offset = -offset;
            }
        }

        try
        {
            return new DateTimeOffset(year, Part(4, 1), Part(6, 1), Part(8, 0), Part(10, 0), Part(12, 0), offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

internal sealed class CosArray : CosObject
{
    public CosArray()
    {
    }

    public CosArray(IEnumerable<CosObject> items) => Items.AddRange(items);

    public List<CosObject> Items { get; } = [];

    public int Count => Items.Count;

    public CosObject this[int index] => Items[index];

    public static CosArray FromNumbers(params double[] values) => new(values.Select(v => (CosObject)new CosReal(v)));

    public CosArray Clone() => new(Items);
}

internal class CosDictionary : CosObject
{
    public OrderedDictionary<string, CosObject> Entries { get; } = new(StringComparer.Ordinal);

    public CosObject? this[string key]
    {
        get => Entries.TryGetValue(key, out var value) ? value : null;
        set
        {
            if (value is null)
            {
                Entries.Remove(key);
            }
            else
            {
                Entries[key] = value;
            }
        }
    }

    public CosDictionary Set(string key, CosObject value)
    {
        Entries[key] = value;
        return this;
    }

    public CosDictionary Clone()
    {
        var copy = new CosDictionary();
        foreach (var (key, value) in Entries)
        {
            copy.Entries[key] = value;
        }

        return copy;
    }
}

/// <summary>
/// A stream. Parsed streams keep a slice of the file (still encoded); streams built for writing
/// carry their final bytes, and their /Length is set when written.
/// </summary>
internal sealed class CosStream(CosDictionary dictionary, ReadOnlyMemory<byte> data) : CosObject
{
    public CosDictionary Dictionary { get; } = dictionary;

    public ReadOnlyMemory<byte> Data { get; } = data;
}

internal readonly record struct CosObjectId(int Number, int Generation)
{
    public override string ToString() => $"{Number} {Generation} R";
}

internal sealed class CosReference(CosObjectId id) : CosObject
{
    public CosReference(int number, int generation)
        : this(new CosObjectId(number, generation))
    {
    }

    public CosObjectId Id { get; } = id;
}

/// <summary>
/// Written as a fixed-width run of bytes whose position is reported back, so a value that depends
/// on the final file layout (a signature's /ByteRange and /Contents) can be patched in afterwards.
/// </summary>
internal sealed class CosPlaceholder(string text) : CosObject
{
    public string Text { get; } = text;
}
