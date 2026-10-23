using System.Globalization;
using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Cos;

/// <summary>Serializes PDF objects. Placeholder positions are recorded so they can be patched later.</summary>
internal sealed class CosWriter(Stream output)
{
    private readonly Stream _output = output;

    public Dictionary<CosPlaceholder, long> PlaceholderOffsets { get; } = [];

    public long Position => _output.Position;

    public void WriteRaw(string text) => _output.Write(Encoding.ASCII.GetBytes(text));

    public void WriteRaw(ReadOnlySpan<byte> bytes) => _output.Write(bytes);

    public void WriteIndirectObject(CosObjectId id, CosObject value)
    {
        WriteRaw($"{id.Number} {id.Generation} obj\n");
        Write(value);
        WriteRaw("\nendobj\n");
    }

    public void Write(CosObject value)
    {
        switch (value)
        {
            case CosNull:
                WriteRaw("null");
                break;
            case CosBoolean b:
                WriteRaw(b.Value ? "true" : "false");
                break;
            case CosInteger i:
                WriteRaw(i.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case CosReal r:
                WriteRaw(FormatReal(r.Value));
                break;
            case CosName n:
                WriteName(n.Value);
                break;
            case CosString s:
                WriteString(s);
                break;
            case CosReference reference:
                WriteRaw(reference.Id.ToString());
                break;
            case CosPlaceholder placeholder:
                PlaceholderOffsets[placeholder] = Position;
                WriteRaw(placeholder.Text);
                break;
            case CosArray array:
                WriteRaw("[");
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        WriteRaw(" ");
                    }

                    Write(array[i]);
                }

                WriteRaw("]");
                break;
            case CosStream stream:
                var dictionary = stream.Dictionary.Clone();
                dictionary["Length"] = new CosInteger(stream.Data.Length);
                Write(dictionary);
                WriteRaw("\nstream\n");
                WriteRaw(stream.Data.Span);
                WriteRaw("\nendstream");
                break;
            case CosDictionary d:
                WriteRaw("<<");
                foreach (var (key, entry) in d.Entries)
                {
                    WriteName(key);
                    WriteRaw(" ");
                    Write(entry);
                }

                WriteRaw(">>");
                break;
            default:
                throw new ArgumentException($"Cannot write {value.GetType().Name}.", nameof(value));
        }
    }

    public static string FormatReal(double value)
    {
        if (!double.IsFinite(value))
        {
            return "0";
        }

        var text = Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    private void WriteName(string name)
    {
        var builder = new StringBuilder("/");
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            if (b is < 0x21 or > 0x7E or (byte)'#' || CosParser.IsDelimiter(b))
            {
                builder.Append('#').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append((char)b);
            }
        }

        WriteRaw(builder.ToString());
    }

    private void WriteString(CosString value)
    {
        if (value.IsHex || value.Bytes.Any(b => b is < 0x20 or > 0x7E))
        {
            WriteRaw("<" + Convert.ToHexString(value.Bytes) + ">");
            return;
        }

        var builder = new StringBuilder("(");
        foreach (var b in value.Bytes)
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
            {
                builder.Append('\\');
            }

            builder.Append((char)b);
        }

        WriteRaw(builder.Append(')').ToString());
    }
}
