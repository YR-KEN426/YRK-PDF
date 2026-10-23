using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Cos;

/// <summary>An affine matrix [a b c d e f] as PDF uses it: x' = a·x + c·y + e, y' = b·x + d·y + f.</summary>
internal readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static PdfMatrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

    /// <summary>This transformation followed by <paramref name="next"/> (PDF's "this × next").</summary>
    public PdfMatrix Then(PdfMatrix next) => new(
        (A * next.A) + (B * next.C),
        (A * next.B) + (B * next.D),
        (C * next.A) + (D * next.C),
        (C * next.B) + (D * next.D),
        (E * next.A) + (F * next.C) + next.E,
        (E * next.B) + (F * next.D) + next.F);

    public (double X, double Y) Transform(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    public static PdfMatrix FromOperands(IReadOnlyList<CosObject> operands, int start = 0) => new(
        Number(operands[start]), Number(operands[start + 1]), Number(operands[start + 2]),
        Number(operands[start + 3]), Number(operands[start + 4]), Number(operands[start + 5]));

    public static PdfMatrix FromArray(CosArray? array) =>
        array is { Count: 6 } ? FromOperands(array.Items) : Identity;

    public static double Number(CosObject value) => value switch
    {
        CosInteger i => i.Value,
        CosReal r => r.Value,
        _ => 0,
    };
}

/// <summary>An axis-aligned rectangle in user space.</summary>
internal readonly record struct PdfRect(double X0, double Y0, double X1, double Y1)
{
    public double Width => X1 - X0;

    public double Height => Y1 - Y0;

    public double Area => Math.Max(0, Width) * Math.Max(0, Height);

    public static PdfRect Bounds(IEnumerable<(double X, double Y)> points)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var (x, y) in points)
        {
            x0 = Math.Min(x0, x);
            y0 = Math.Min(y0, y);
            x1 = Math.Max(x1, x);
            y1 = Math.Max(y1, y);
        }

        return new PdfRect(x0, y0, x1, y1);
    }

    /// <summary>The unit square under a matrix (where an image or form lands).</summary>
    public static PdfRect Of(PdfMatrix matrix, PdfRect box) => Bounds(
    [
        matrix.Transform(box.X0, box.Y0), matrix.Transform(box.X1, box.Y0),
        matrix.Transform(box.X0, box.Y1), matrix.Transform(box.X1, box.Y1),
    ]);

    public double OverlapArea(PdfRect other) =>
        Math.Max(0, Math.Min(X1, other.X1) - Math.Max(X0, other.X0)) * Math.Max(0, Math.Min(Y1, other.Y1) - Math.Max(Y0, other.Y0));

    public bool Intersects(PdfRect other) =>
        Math.Min(X1, other.X1) > Math.Max(X0, other.X0) && Math.Min(Y1, other.Y1) > Math.Max(Y0, other.Y0);

    public bool Contains(PdfRect other) =>
        other.X0 >= X0 - 0.01 && other.X1 <= X1 + 0.01 && other.Y0 >= Y0 - 0.01 && other.Y1 <= Y1 + 0.01;
}

/// <summary>One content-stream operation, with the exact bytes it came from.</summary>
internal sealed record ContentOperation(string Operator, IReadOnlyList<CosObject> Operands, int Start, int End);

/// <summary>Splits a content stream into operations (§7.8.2), including inline images.</summary>
internal static class ContentTokenizer
{
    public static List<ContentOperation> Parse(byte[] content)
    {
        var operations = new List<ContentOperation>();
        var parser = new CosParser(content);
        var operands = new List<CosObject>();
        var start = -1;
        while (true)
        {
            parser.SkipWhitespace();
            if (parser.Position >= content.Length)
            {
                break;
            }

            if (start < 0)
            {
                start = parser.Position;
            }

            var b = content[parser.Position];
            if (IsOperatorStart(b))
            {
                var keywordStart = parser.Position;
                while (parser.Position < content.Length && !CosParser.IsWhitespace(content[parser.Position]) && !CosParser.IsDelimiter(content[parser.Position]))
                {
                    parser.Position++;
                }

                var keyword = Encoding.ASCII.GetString(content, keywordStart, parser.Position - keywordStart);
                switch (keyword)
                {
                    case "true":
                        operands.Add(new CosBoolean(true));
                        continue;
                    case "false":
                        operands.Add(new CosBoolean(false));
                        continue;
                    case "null":
                        operands.Add(CosNull.Instance);
                        continue;
                    case "BI":
                        operations.Add(ReadInlineImage(content, parser, start));
                        break;
                    default:
                        operations.Add(new ContentOperation(keyword, operands.ToArray(), start, parser.Position));
                        break;
                }

                operands.Clear();
                start = -1;
                continue;
            }

            try
            {
                operands.Add(parser.ParseObject());
            }
            catch (PdfException)
            {
                // Skip a byte of junk, as viewers do.
                parser.Position++;
            }
        }

        return operations;
    }

    private static bool IsOperatorStart(byte b) =>
        !CosParser.IsWhitespace(b) && !CosParser.IsDelimiter(b) && b is not ((>= (byte)'0' and <= (byte)'9') or (byte)'+' or (byte)'-' or (byte)'.');

    /// <summary>BI key value … ID binary-data EI. The operands hold the image dictionary.</summary>
    private static ContentOperation ReadInlineImage(byte[] content, CosParser parser, int start)
    {
        var dictionary = new CosDictionary();
        while (true)
        {
            parser.SkipWhitespace();
            if (parser.Position >= content.Length)
            {
                return new ContentOperation("BI", [dictionary], start, content.Length);
            }

            if (parser.PeekKeyword("ID"))
            {
                parser.Position += 2;
                break;
            }

            if (parser.ParseObject() is CosName key)
            {
                dictionary[key.Value] = parser.ParseObject();
            }
        }

        parser.Position++; // The single whitespace after ID.
        var span = content.AsSpan();
        for (var i = parser.Position; i + 1 < content.Length; i++)
        {
            if (span[i] == 'E' && span[i + 1] == 'I' && (i == 0 || CosParser.IsWhitespace(span[i - 1]))
                && (i + 2 >= content.Length || CosParser.IsWhitespace(span[i + 2]) || CosParser.IsDelimiter(span[i + 2])))
            {
                parser.Position = i + 2;
                return new ContentOperation("BI", [dictionary], start, i + 2);
            }
        }

        parser.Position = content.Length;
        return new ContentOperation("BI", [dictionary], start, content.Length);
    }
}
