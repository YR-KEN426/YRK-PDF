using System.Globalization;
using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Cos;

/// <summary>Reads PDF objects from a byte buffer (ISO 32000-1 §7.2–7.3).</summary>
internal sealed class CosParser(ReadOnlyMemory<byte> data, Func<CosObjectId, CosObject?>? resolve = null)
{
    private const int MaxNesting = 256;

    private readonly ReadOnlyMemory<byte> _data = data;

    public int Position { get; set; }

    private ReadOnlySpan<byte> Span => _data.Span;

    public static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static bool IsRegular(byte b) => !IsWhitespace(b) && !IsDelimiter(b);

    public CosObject ParseObject() => ParseObject(0);

    /// <summary>Parses "n g obj … endobj" at the current position. Returns the object number and its value.</summary>
    public (CosObjectId Id, CosObject Value) ParseIndirectObject()
    {
        SkipWhitespace();
        var number = ReadInteger();
        SkipWhitespace();
        var generation = ReadInteger();
        SkipWhitespace();
        ExpectKeyword("obj");
        var value = ParseObject();
        SkipWhitespace();
        if (value is CosDictionary dictionary && PeekKeyword("stream"))
        {
            Position += "stream".Length;
            value = ReadStreamData(dictionary);
        }

        return (new CosObjectId((int)number, (int)generation), value);
    }

    public void SkipWhitespace()
    {
        var span = Span;
        while (Position < span.Length)
        {
            var b = span[Position];
            if (IsWhitespace(b))
            {
                Position++;
            }
            else if (b == '%')
            {
                while (Position < span.Length && span[Position] is not (10 or 13))
                {
                    Position++;
                }
            }
            else
            {
                break;
            }
        }
    }

    public bool PeekKeyword(string keyword)
    {
        var span = Span;
        if (Position + keyword.Length > span.Length)
        {
            return false;
        }

        for (var i = 0; i < keyword.Length; i++)
        {
            if (span[Position + i] != keyword[i])
            {
                return false;
            }
        }

        var end = Position + keyword.Length;
        return end == span.Length || !IsRegular(span[end]);
    }

    public void ExpectKeyword(string keyword)
    {
        SkipWhitespace();
        if (!PeekKeyword(keyword))
        {
            throw Error($"Expected '{keyword}'");
        }

        Position += keyword.Length;
    }

    public long ReadInteger()
    {
        var span = Span;
        var start = Position;
        if (Position < span.Length && span[Position] is (byte)'+' or (byte)'-')
        {
            Position++;
        }

        while (Position < span.Length && span[Position] is >= (byte)'0' and <= (byte)'9')
        {
            Position++;
        }

        if (!long.TryParse(span[start..Position], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            Position = start;
            throw Error("Expected an integer");
        }

        return value;
    }

    private CosObject ParseObject(int depth)
    {
        if (depth > MaxNesting)
        {
            throw Error("Objects are nested too deeply");
        }

        SkipWhitespace();
        var span = Span;
        if (Position >= span.Length)
        {
            throw Error("Unexpected end of data");
        }

        var b = span[Position];
        switch (b)
        {
            case (byte)'/':
                Position++;
                return new CosName(ReadNameBody());
            case (byte)'(':
                Position++;
                return new CosString(ReadLiteralString());
            case (byte)'<' when Position + 1 < span.Length && span[Position + 1] == '<':
                Position += 2;
                return ReadDictionary(depth);
            case (byte)'<':
                Position++;
                return new CosString(ReadHexString(), isHex: true);
            case (byte)'[':
                Position++;
                return ReadArray(depth);
            case (byte)'+' or (byte)'-' or (byte)'.' or (>= (byte)'0' and <= (byte)'9'):
                return ReadNumberOrReference();
        }

        var start = Position;
        while (Position < span.Length && IsRegular(span[Position]))
        {
            Position++;
        }

        var keyword = Encoding.ASCII.GetString(span[start..Position]);
        return keyword switch
        {
            "true" => new CosBoolean(true),
            "false" => new CosBoolean(false),
            "null" => CosNull.Instance,
            _ => throw Error($"Unexpected '{(keyword.Length > 0 ? keyword : ((char)b).ToString())}'"),
        };
    }

    private CosDictionary ReadDictionary(int depth)
    {
        var dictionary = new CosDictionary();
        while (true)
        {
            SkipWhitespace();
            var span = Span;
            if (Position + 1 < span.Length && span[Position] == '>' && span[Position + 1] == '>')
            {
                Position += 2;
                return dictionary;
            }

            if (Position >= span.Length || span[Position] != '/')
            {
                throw Error("Expected a name as dictionary key");
            }

            Position++;
            var key = ReadNameBody();
            var value = ParseObject(depth + 1);
            if (value is not CosNull)
            {
                dictionary.Entries[key] = value; // A null value is the same as a missing entry (§7.3.7).
            }
        }
    }

    private CosArray ReadArray(int depth)
    {
        var array = new CosArray();
        while (true)
        {
            SkipWhitespace();
            if (Position < Span.Length && Span[Position] == ']')
            {
                Position++;
                return array;
            }

            array.Items.Add(ParseObject(depth + 1));
        }
    }

    private CosObject ReadNumberOrReference()
    {
        var span = Span;
        var start = Position;
        var isReal = false;
        Position++;
        while (Position < span.Length && (span[Position] is >= (byte)'0' and <= (byte)'9' || span[Position] == '.'))
        {
            isReal |= span[Position] == '.';
            Position++;
        }

        isReal |= span[start] == '.';
        var text = span[start..Position];
        if (isReal)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? new CosReal(real) : new CosReal(0);
        }

        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return new CosReal(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var big) ? big : 0);
        }

        // "n g R" is a reference; anything else leaves the position after the first integer.
        if (integer >= 0 && span[start] is >= (byte)'0' and <= (byte)'9')
        {
            var afterFirst = Position;
            SkipWhitespace();
            var genStart = Position;
            while (Position < span.Length && span[Position] is >= (byte)'0' and <= (byte)'9')
            {
                Position++;
            }

            if (genStart > afterFirst && Position > genStart &&Position < span.Length && !IsRegular(span[Position]))
            {
                var generation = int.Parse(span[genStart..Position], CultureInfo.InvariantCulture);
                SkipWhitespace();
                if (PeekKeyword("R"))
                {
                    Position++;
                    return new CosReference((int)integer, generation);
                }
            }

            Position = afterFirst;
        }

        return new CosInteger(integer);
    }

    private string ReadNameBody()
    {
        var span = Span;
        var bytes = new List<byte>();
        while (Position < span.Length && IsRegular(span[Position]))
        {
            var b = span[Position++];
            if (b == '#' && Position + 1 < span.Length && IsHexDigit(span[Position]) && IsHexDigit(span[Position + 1]))
            {
                b = (byte)((HexValue(span[Position]) << 4) | HexValue(span[Position + 1]));
                Position += 2;
            }

            bytes.Add(b);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private byte[] ReadLiteralString()
    {
        var span = Span;
        var bytes = new List<byte>();
        var depth = 1;
        while (Position < span.Length)
        {
            var b = span[Position++];
            switch (b)
            {
                case (byte)'(':
                    depth++;
                    bytes.Add(b);
                    break;
                case (byte)')':
                    if (--depth == 0)
                    {
                        return bytes.ToArray();
                    }

                    bytes.Add(b);
                    break;
                case 13:
                    // An unescaped end-of-line is read as a single line feed (§7.3.4.2).
                    if (Position < span.Length && span[Position] == 10)
                    {
                        Position++;
                    }

                    bytes.Add(10);
                    break;
                case (byte)'\\' when Position < span.Length:
                    var escaped = span[Position++];
                    switch (escaped)
                    {
                        case (byte)'n': bytes.Add(10); break;
                        case (byte)'r': bytes.Add(13); break;
                        case (byte)'t': bytes.Add(9); break;
                        case (byte)'b': bytes.Add(8); break;
                        case (byte)'f': bytes.Add(12); break;
                        case 13:
                            if (Position < span.Length && span[Position] == 10)
                            {
                                Position++;
                            }

                            break;
                        case 10:
                            break;
                        case >= (byte)'0' and <= (byte)'7':
                            var value = escaped - '0';
                            for (var i = 0; i < 2 && Position < span.Length && span[Position] is >= (byte)'0' and <= (byte)'7'; i++)
                            {
                                value = (value * 8) + (span[Position++] - '0');
                            }

                            bytes.Add((byte)value);
                            break;
                        default:
                            bytes.Add(escaped);
                            break;
                    }

                    break;
                default:
                    bytes.Add(b);
                    break;
            }
        }

        throw Error("Unterminated string");
    }

    private byte[] ReadHexString()
    {
        var span = Span;
        var bytes = new List<byte>();
        int? high = null;
        while (Position < span.Length)
        {
            var b = span[Position++];
            if (b == '>')
            {
                if (high is not null)
                {
                    bytes.Add((byte)(high.Value << 4)); // An odd final digit is followed by an implied 0.
                }

                return bytes.ToArray();
            }

            if (!IsHexDigit(b))
            {
                continue; // Whitespace (and, leniently, junk) is ignored.
            }

            if (high is null)
            {
                high = HexValue(b);
            }
            else
            {
                bytes.Add((byte)((high.Value << 4) | HexValue(b)));
                high = null;
            }
        }

        throw Error("Unterminated hex string");
    }

    private CosStream ReadStreamData(CosDictionary dictionary)
    {
        var span = Span;

        // The keyword is followed by CRLF or LF (a lone CR is tolerated).
        if (Position < span.Length && span[Position] == 13)
        {
            Position++;
        }

        if (Position < span.Length && span[Position] == 10)
        {
            Position++;
        }

        var start = Position;
        var length = dictionary["Length"] switch
        {
            CosInteger i => i.Value,
            CosReference r when resolve?.Invoke(r.Id) is CosInteger i => i.Value,
            _ => -1,
        };

        if (length >= 0 && start + length <= span.Length)
        {
            var check = new CosParser(_data) { Position = (int)(start + length) };
            check.SkipWhitespace();
            if (check.PeekKeyword("endstream"))
            {
                Position = check.Position + "endstream".Length;
                return new CosStream(dictionary, _data.Slice(start, (int)length));
            }
        }

        // /Length is missing or wrong: find the keyword instead.
        var end = span[start..].IndexOf("endstream"u8);
        if (end < 0)
        {
            throw Error("Stream without 'endstream'");
        }

        var dataEnd = start + end;
        if (dataEnd > start && span[dataEnd - 1] == 10)
        {
            dataEnd--;
        }

        if (dataEnd > start && span[dataEnd - 1] == 13)
        {
            dataEnd--;
        }

        Position = start + end + "endstream".Length;
        return new CosStream(dictionary, _data[start..dataEnd]);
    }

    private static bool IsHexDigit(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static int HexValue(byte b) => b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;

    private PdfException Error(string message) => new($"{message} at byte {Position}.");
}
