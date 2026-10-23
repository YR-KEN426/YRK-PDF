using System.Text;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>Writes small, valid PDFs for tests (objects are laid out by hand; the xref is computed).</summary>
internal static class TestPdfFactory
{
    /// <summary>
    /// One A4 page with an AcroForm: text field "name" (value "John"), check box "agree" (off),
    /// combo box "province" (Bangkok / Chiang Mai / Phuket, "Bangkok" selected) and read-only text "id".
    /// </summary>
    public static string CreateFormPdf(string path)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R 6 0 R 11 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 7 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /Helv 7 0 R >> >> /Contents 8 0 R /Annots [4 0 R 5 0 R 6 0 R 11 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (John) /DA (/Helv 12 Tf 0 g) /Rect [72 700 300 724] /F 4 /P 3 0 R /MK << /BG [0.9 0.95 1] >> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /V /Off /AS /Off /Rect [72 650 90 668] /F 4 /P 3 0 R /MK << /CA (4) >> /DA (/ZaDb 0 Tf 0 g) /AP << /N << /Yes 9 0 R /Off 10 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T (province) /Opt [(Bangkok) (Chiang Mai) (Phuket)] /V (Bangkok) /DA (/Helv 12 Tf 0 g) /Rect [72 600 300 620] /F 4 /P 3 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            Stream("BT /Helv 14 Tf 72 760 Td (Application form) Tj ET"),
            Stream("0 0 1 rg 3 3 12 12 re f", "/Type /XObject /Subtype /Form /BBox [0 0 18 18]"),
            Stream("0 0 0 RG 0.5 0.5 17 17 re S", "/Type /XObject /Subtype /Form /BBox [0 0 18 18]"),
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (id) /V (A-001) /Ff 1 /DA (/Helv 12 Tf 0 g) /Rect [72 550 300 574] /F 4 /P 3 0 R >>",
        };

        Write(path, objects);
        return path;
    }

    /// <summary>
    /// A report page: a bold 18 pt heading, a paragraph of two 11 pt lines, and a table with an "Item" column
    /// and a right-aligned "Amount" column (1,250.00 / 980.50 / Total 2,230.50).
    /// </summary>
    public static string CreateReportPdf(string path)
    {
        static string Right(string text, double right, double y) =>
            FormattableString.Invariant($"BT /F1 11 Tf {right - (text.Length * 5.6):0.##} {y} Td ({text}) Tj ET");

        var content = string.Join('\n',
            "BT /F2 18 Tf 72 760 Td (Monthly report) Tj ET",
            "BT /F1 11 Tf 72 730 Td (The figures below show the costs for the month and are) Tj ET",
            "BT /F1 11 Tf 72 716 Td (checked by the accounts team.) Tj ET",
            "BT /F2 11 Tf 72 680 Td (Item) Tj ET",
            Right("Amount", 400, 680),
            "BT /F1 11 Tf 72 664 Td (Paper) Tj ET",
            Right("1,250.00", 400, 664),
            "BT /F1 11 Tf 72 648 Td (Ink) Tj ET",
            Right("980.50", 400, 648),
            "BT /F2 11 Tf 72 632 Td (Total) Tj ET",
            Right("2,230.50", 400, 632));
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> /Contents 4 0 R >>",
            Stream(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
        };

        Write(path, objects);
        return path;
    }

    /// <summary>
    /// A ruled table (yellow heading row across both columns, two rows below) and, under it, a framed chart: a
    /// blue bar and the label "Sales" inside a box.
    /// </summary>
    public static string CreateTablePdf(string path)
    {
        var content = string.Join('\n',
            "1 1 0 rg 100 680 300 20 re f",
            "0 0 0 RG 0.5 w",
            "100 700 m 400 700 l S 100 680 m 400 680 l S 100 660 m 400 660 l S 100 640 m 400 640 l S",
            "100 640 m 100 700 l S 400 640 m 400 700 l S 250 640 m 250 680 l S",
            "0 g BT /F1 11 Tf 215 686 Td (Price list) Tj ET",
            "BT /F1 11 Tf 105 666 Td (Paper) Tj ET BT /F1 11 Tf 330 666 Td (1,250.00) Tj ET",
            "BT /F1 11 Tf 105 646 Td (Ink) Tj ET BT /F1 11 Tf 340 646 Td (15.5%) Tj ET",
            "100 400 200 150 re S 0 0 1 rg 150 420 30 80 re f 0 g BT /F1 11 Tf 110 535 Td (Sales) Tj ET");
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            Stream(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };

        Write(path, objects);
        return path;
    }

    /// <summary>
    /// A drawing with layers, as CAD programs export it: a red square on layer "WALL", a blue square on
    /// layer "DIM" (off by default), and the heading "Model" above them in the layer list.
    /// </summary>
    public static string CreateLayeredPdf(string path)
    {
        var content = "/OC /L1 BDC 1 0 0 rg 100 600 100 100 re f EMC\n/OC /L2 BDC 0 0 1 rg 300 600 100 100 re f EMC";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D << /Order [(Model) 5 0 R 6 0 R] /OFF [6 0 R] /Locked [5 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Properties << /L1 5 0 R /L2 6 0 R >> >> /Contents 4 0 R >>",
            Stream(content),
            "<< /Type /OCG /Name (WALL) >>",
            "<< /Type /OCG /Name (DIM) >>",
        };

        Write(path, objects);
        return path;
    }

    /// <summary>
    /// One page whose catalog, page tree and page live in a compressed object stream, indexed by a
    /// cross-reference stream (the PDF 1.5 layout most modern producers use).
    /// </summary>
    public static string CreateXrefStreamPdf(string path)
    {
        using var output = new MemoryStream();
        void Append(string text) => output.Write(Encoding.Latin1.GetBytes(text));

        Append("%PDF-1.7\n%âãÏÓ\n");
        var content = "BT /F1 24 Tf 72 700 Td (Object streams) Tj ET";
        var contentOffset = output.Position;
        Append($"4 0 obj\n{Stream(content)}\nendobj\n");

        string[] packed =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>",
        ];
        var body = new StringBuilder();
        var header = new StringBuilder();
        for (var i = 0; i < packed.Length; i++)
        {
            header.Append($"{i + 1} {body.Length} ");
            body.Append(packed[i]).Append('\n');
        }

        var objectStream = header.ToString() + body;
        var objectStreamOffset = output.Position;
        Append($"5 0 obj\n<< /Type /ObjStm /N {packed.Length} /First {header.Length} /Length {objectStream.Length} >>\nstream\n{objectStream}\nendstream\nendobj\n");

        var xrefOffset = output.Position;
        var entries = new List<byte>();
        void Entry(byte type, long field2, int field3) =>
            entries.AddRange([type, (byte)(field2 >> 24), (byte)(field2 >> 16), (byte)(field2 >> 8), (byte)field2, (byte)(field3 >> 8), (byte)field3]);
        Entry(0, 0, 65535);
        Entry(2, 5, 0);
        Entry(2, 5, 1);
        Entry(2, 5, 2);
        Entry(1, contentOffset, 0);
        Entry(1, objectStreamOffset, 0);
        Entry(1, xrefOffset, 0);
        Append($"6 0 obj\n<< /Type /XRef /Size 7 /W [1 4 2] /Root 1 0 R /Length {entries.Count} >>\nstream\n");
        output.Write(entries.ToArray());
        Append($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        File.WriteAllBytes(path, output.ToArray());
        return path;
    }

    private static string Stream(string content, string extraDictionary = "") =>
        $"<< {extraDictionary} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static void Write(string path, IReadOnlyList<string> objects)
    {
        using var output = new MemoryStream();
        void Append(string text) => output.Write(Encoding.Latin1.GetBytes(text));

        Append("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new long[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = output.Position;
            Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = output.Position;
        Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Append($"{offset:D10} 00000 n \n");
        }

        Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, output.ToArray());
    }
}
